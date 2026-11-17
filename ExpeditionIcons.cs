using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ExileCore.Shared.Nodes;
using ExpeditionIcons.PathPlannerData;
using GameOffsets.Native;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace ExpeditionIcons;

public class ExpeditionIcons : BaseSettingsPlugin<ExpeditionIconsSettings>
{
    private const string MarkerPath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionMarker";
    private const string ExplosivePath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionExplosive";
    private const string RelicPath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionRelic";

    private const string TextureName = "Icons.png";
    private const double CameraAngle = 38.7 * Math.PI / 180;
    private static readonly float CameraAngleCos = (float)Math.Cos(CameraAngle);
    private static readonly float CameraAngleSin = (float)Math.Sin(CameraAngle);

    private const float GridToWorldMultiplier = 250 / 23f;

    //TODO
    private const int ExplosiveBaseRange = 87;
    private const int ExplosiveBaseRadius = 30;

    private readonly ConcurrentDictionary<string, List<ExpeditionMarkerIconDescription>> _relicModIconMapping = new();
    private readonly ConcurrentDictionary<string, ExpeditionMarkerIconDescription> _metadataIconMapping = new();
    private readonly Dictionary<uint, EntityCacheItem> _cachedEntities = new Dictionary<uint, EntityCacheItem>();
    private readonly ConcurrentDictionary<string, ExpeditionEntityType> _entityTypeCache = new();
    private double _mapScale;
    private Vector2 _mapCenter;
    private bool _largeMapOpen;
    private Vector2 _playerGridPos;
    private float _playerZ;
    private List<Vector2> _explosives2DPositions;
    private float _explosiveRadius;
    private float _explosiveRange;
    private PathPlannerRunner _plannerRunner;
    private (Vector2, float)? _detonatorPos;
    private bool _zoneCleared;
    private int[][] _pathfindingData;
    private Vector2i _areaDimensions;
    private List<float> _scoreHistory = [];
    private List<Vector2> _editedPath;
    private int? _editedIndex = null;
    private PathPlanner.DetailedLootScore _editedPathEval;
    private PathPlanner.DetailedLootScore EditedOrNativeScore => _editedPathEval ?? _plannerRunner?.CurrentBestPath;
    private bool _showExplosiveCountPrompt;
    private int _explosiveCount = 5;

    private Camera Camera => GameController.Game.IngameState.Camera;

    private (Vector2 Pos, float Rotation)? DetonatorPos => _detonatorPos ??= RealDetonatorPos;

    private (Vector2, float)? RealDetonatorPos => DetonatorEntity is { } e
        ? (e.GridPosNum, e.GetComponent<Positioned>().Rotation)
        : null;

    private Entity DetonatorEntity =>
        GameController.EntityListWrapper.ValidEntitiesByType[EntityType.IngameIcon]
            .FirstOrDefault(x => x.Path == "Metadata/MiscellaneousObjects/Expedition/ExpeditionDetonator");

    private int PlacedExplosiveCount => ExpeditionInfo.PlacedExplosiveCount;
    private Vector2i[] PlacedExplosives => ExpeditionInfo.PlacedExplosiveGridPositions;

    private Vector2i? PlacementIndicatorPos =>
        ExpeditionInfo.IsExplosivePlacementActive
            ? GameController.EntityListWrapper.ValidEntitiesByType[EntityType.MiscellaneousObjects]
                  .FirstOrDefault(x => x.Path == "Metadata/MiscellaneousObjects/Expedition/ExpeditionPlacementIndicator")?.GridPosNum.RoundToVector2I() ??
              ExpeditionInfo.PlacementIndicatorGridPosition
            : null;

    private ExpeditionDetonatorInfo ExpeditionInfo => GameController.IngameState.IngameUi.ExpeditionDetonatorElement.Info;

    private RectangleF LocalWindowRect => GameController.Window.GetWindowRectangleTimeCache with { Location = SharpDX.Vector2.Zero };

    public override bool Initialise()
    {
        Graphics.InitImage(TextureName);
        Settings._iconsImageId = Graphics.GetTextureId(TextureName);
        Settings.PlannerSettings.StartSearch.OnPressed += StartSearch;
        Settings.PlannerSettings.StopSearch.OnPressed += StopSearch;
        Settings.PlannerSettings.ClearSearch.OnPressed += ClearSearch;
        RegisterHotkey(Settings.PlannerSettings.StartSearchHotkey);
        RegisterHotkey(Settings.PlannerSettings.StopSearchHotkey);
        RegisterHotkey(Settings.PlannerSettings.ClearSearchHotkey);
        return base.Initialise();
    }

    private static void RegisterHotkey(HotkeyNode hotkey)
    {
        Input.RegisterKey(hotkey);
        hotkey.OnValueChanged += () => { Input.RegisterKey(hotkey); };
    }

    private void StopSearch()
    {
        _showExplosiveCountPrompt = false;
        if (_plannerRunner is { } run)
        {
            run.Stop();
            Settings.PlannerSettings.SearchState = SearchState.Stopped;
        }
        else
        {
            Settings.PlannerSettings.SearchState = SearchState.Empty;
        }
    }

    private void StartSearch()
    {
        if (_zoneCleared)
        {
            DebugWindow.LogError("ExpeditionIcons: Cannot start path search because the expedition has already been detonated.");
            return;
        }

        if (DetonatorPos == null)
        {
            DebugWindow.LogError("ExpeditionIcons: Cannot start path search because the expedition detonator was not found.");
            return;
        }

        if (TryGetUnplacedExplosiveCount(out var count))
        {
            _explosiveCount = count;
            DebugWindow.LogMsg($"ExpeditionIcons: Read {count} explosives from the expedition counter.");
            StartSearchWithExplosiveCount();
            return;
        }

        DebugWindow.LogMsg("ExpeditionIcons: Could not reliably determine the total bomb count; enter it in the prompt.");
        _showExplosiveCountPrompt = true;
    }

    private bool TryGetUnplacedExplosiveCount(out int count)
    {
        count = 0;
        if (GameController.EntityListWrapper.ValidEntitiesByType[EntityType.IngameIcon]
            .Any(entity => entity.Path == ExplosivePath))
        {
            return false;
        }

        Element element = GameController.IngameState.IngameUi.ExpeditionDetonatorElement;
        if (element == null || !element.IsVisible)
        {
            return false;
        }

        for (var depth = 0; depth < 3; depth++)
        {
            var children = element.Children;
            if (children == null || children.Count == 0 || children[0] == null)
            {
                return false;
            }

            element = children[0];
        }

        return int.TryParse(element.Text, out count) && count is > 0 and <= 100;
    }

    private void StartSearchWithExplosiveCount()
    {
        if (_zoneCleared || DetonatorPos == null)
        {
            _showExplosiveCountPrompt = false;
            DebugWindow.LogError("ExpeditionIcons: Cannot start path search because the expedition is no longer available.");
            return;
        }

        var environment = BuildEnvironment(_explosiveCount);

        if (environment.Loot.Count == 0)
        {
            DebugWindow.LogError("ExpeditionIcons: No scorable expedition loot was detected; the suggested path may not be useful. Move closer to the expedition and try again.");
        }

        _scoreHistory = [];
        _editedPath = null;
        _editedIndex = null;
        _editedPathEval = null;
        _plannerRunner?.Stop();
        var plannerRunner = new PathPlannerRunner();
        plannerRunner.Start(Settings.PlannerSettings, environment, GameController.SoundController);
        _plannerRunner = plannerRunner;
        Settings.PlannerSettings.SearchState = SearchState.Searching;
        _showExplosiveCountPrompt = false;
    }

    private void ShowExplosiveCountPrompt()
    {
        if (!_showExplosiveCountPrompt)
        {
            return;
        }

        if (ImGui.Begin("Expedition bomb count", ref _showExplosiveCountPrompt, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Enter the total number of explosives shown in game.");
            var submitted = ImGui.InputInt("Explosives", ref _explosiveCount, 1, 5, ImGuiInputTextFlags.EnterReturnsTrue);
            if (_explosiveCount < 1 || _explosiveCount > 100)
            {
                ImGui.Text("Enter a number between 1 and 100.");
            }
            else if (ImGui.Button("Start search") || submitted)
            {
                StartSearchWithExplosiveCount();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                _showExplosiveCountPrompt = false;
            }
        }

        ImGui.End();
    }

    private void ClearSearch()
    {
        _showExplosiveCountPrompt = false;
        if (_plannerRunner is { } run)
        {
            run.Stop();
            _plannerRunner = null;
            _scoreHistory = [];
            _editedPath = null;
            _editedIndex = null;
            _editedPathEval = null;
        }
    }

    public override void AreaChange(AreaInstance area)
    {
        _showExplosiveCountPrompt = false;
        _plannerRunner?.Stop();
        _plannerRunner = null;
        _scoreHistory = [];
        _editedPath = null;
        _editedIndex = null;
        _editedPathEval = null;
        _detonatorPos = null;
        _cachedEntities.Clear();
        _zoneCleared = false;
        _pathfindingData = GameController.IngameState.Data.RawPathfindingData;
        _areaDimensions = GameController.IngameState.Data.AreaDimensions;
    }

    private ExpeditionEntityType GetEntityType(string path)
    {
        return _entityTypeCache.GetOrAdd(path, p => p switch
        {
            RelicPath => ExpeditionEntityType.Relic,
            MarkerPath => ExpeditionEntityType.Marker,
            _ when p.StartsWith("Metadata/Terrain/Leagues/Expedition/Tiles/ExpeditionChamber") => ExpeditionEntityType.Cave,
            _ when p.StartsWith("Metadata/Terrain/Leagues/Expedition/Tiles/ExpeditionBossDispenser") => ExpeditionEntityType.Boss,
            _ => ExpeditionEntityType.None,
        });
    }

    private Vector3 ExpandWithTerrainHeight(Vector2 gridPosition)
    {
        return new Vector3(gridPosition.GridToWorld(), GameController.IngameState.Data.GetTerrainHeightAt(gridPosition));
    }

    private void DrawCirclesInWorld(List<Vector3> positions, float radius, Color color, float thickness = 1)
    {
        const int segments = 90;
        const int segmentSpan = 360 / segments;
        var playerPos = GameController.Player?.GetComponent<Positioned>()?.WorldPosNum;
        if (playerPos == null)
        {
            return;
        }

        foreach (var position in positions
                     .Where(x => playerPos.Value.Distance(new Vector2(x.X, x.Y)) < 80 * GridToWorldMultiplier + radius))
        {
            foreach (var segmentId in Enumerable.Range(0, segments))
            {
                (Vector2, Vector2) GetVector(int i)
                {
                    var (sin, cos) = MathF.SinCos(MathF.PI / 180 * i);
                    var offset = new Vector2(cos, sin) * radius;
                    var xy = position.Xy() + offset;
                    var screen = Camera.WorldToScreen(ExpandWithTerrainHeight(xy.WorldToGrid()));
                    return (xy, screen);
                }

                var segmentOrigin = segmentId * segmentSpan;
                var (w1, c1) = GetVector(segmentOrigin);
                var (w2, c2) = GetVector(segmentOrigin + segmentSpan);
                if (Settings.ExplosivesSettings.EnableExplosiveRadiusMerging)
                {
                    if (positions
                        .Where(x => x != position)
                        .Select(x => new Vector2(x.X, x.Y))
                        .Any(x => Vector2.Distance(w1, x) < radius &&
                                  Vector2.Distance(w2, x) < radius))
                    {
                        continue;
                    }
                }

                Graphics.DrawLine(c1, c2, thickness, color);
            }
        }
    }

    public override Job Tick()
    {
        Settings._iconsImageId = Graphics.GetTextureId(TextureName);
        Settings.PlannerSettings.SearchState = _plannerRunner switch
        {
            { IsRunning: true } => SearchState.Searching,
            { IsRunning: false, CurrentBestPath.PerPointScore.Count: > 0 } => SearchState.Stopped,
            _ => SearchState.Empty
        };

        var detonatorPos = DetonatorPos;
        var playerGridPos = GameController.Player?.GetComponent<Positioned>()?.WorldPosNum.WorldToGrid();
        if (playerGridPos == null)
        {
            return null;
        }

        _playerGridPos = playerGridPos.Value;
        if (detonatorPos is { Pos: var dp } && _playerGridPos.Distance(dp) < 90)
        {
            if (DetonatorEntity is { } detonator)
            {
                _zoneCleared = !detonator.IsTargetable;
            }
            if (_zoneCleared)
            {
                ClearSearch();
                return null;
            }
        }

        var ingameUi = GameController.Game.IngameState.IngameUi;
        var map = ingameUi.Map;
        var largeMap = map.LargeMap.AsObject<SubMap>();
        _largeMapOpen = largeMap.IsVisible;
        _mapScale = GameController.IngameState.Camera.Height / 677f * largeMap.Zoom;
        _mapCenter = largeMap.GetClientRect().TopLeft.ToVector2Num() + largeMap.ShiftNum + largeMap.DefaultShiftNum;
        _playerZ = GameController.Player.GetComponent<Render>().Z;

        _explosiveRadius = Settings.ExplosivesSettings.CalculateRadiusAutomatically
            //ReSharper disable once PossibleLossOfFraction
            //rounding here is extremely important to get right, this is taken from the game's code
            ? ExplosiveBaseRadius * (100 + GameController.IngameState.Data.MapStats?.GetValueOrDefault(GameStat.MapExpeditionExplosionRadiusPct) ?? 0) / 100 * GridToWorldMultiplier
            : Settings.ExplosivesSettings.ExplosiveRadius.Value;
        //ReSharper disable once PossibleLossOfFraction
        //rounding here is extremely important to get right, this is taken from the game's code
        _explosiveRange = ExplosiveBaseRange * (100 + GameController.IngameState.Data.MapStats?.GetValueOrDefault(GameStat.MapExpeditionMaximumPlacementDistancePct) ?? 0) / 100 *
                          GridToWorldMultiplier;

        foreach (var entity in new[] { EntityType.IngameIcon, EntityType.Terrain }
                     .SelectMany(x => GameController.EntityListWrapper.ValidEntitiesByType[x]))
        {
            if (GetEntityType(entity.Path) != ExpeditionEntityType.None)
            {
                var newValue = BuildCacheItem(entity);
                _cachedEntities[entity.Id] = _cachedEntities.TryGetValue(entity.Id, out var oldValue)
                    ? oldValue.Merge(newValue)
                    : newValue;
            }
        }

        return null;
    }

    private (Vector2 Min, Vector2 Max)? GetExclusionRect()
    {
        if (DetonatorPos is not { } detonatorPos)
        {
            return null;
        }

        var negVec = new Vector2(-11.5f, -8.5f);
        var posVec = new Vector2(10.5f, 23.5f);
        var rotations = (int)Math.Round(detonatorPos.Rotation / (MathF.PI / 2));
        for (int i = 0; i < rotations; i++)
        {
            (negVec.X, negVec.Y, posVec.X, posVec.Y) = (-posVec.Y, negVec.X, -negVec.Y, posVec.X);
        }

        return (detonatorPos.Pos + negVec, detonatorPos.Pos + posVec);
    }

    private ExpeditionEnvironment BuildEnvironment(int explosiveCount)
    {
        if (DetonatorPos is not { Pos: var detonatorPos })
        {
            throw new Exception("Unable to plan a path: detonator position is unknown");
        }

        var loot = new List<(Vector2, IExpeditionLoot)>();
        var relics = new List<(Vector2, IExpeditionRelic)>();
        foreach (var e in _cachedEntities.Values)
        {
            switch (GetEntityType(e.Path))
            {
                case ExpeditionEntityType.Marker:
                {
                    var animatedMetaData = e.BaseAnimatedEntityMetadata;
                    if (animatedMetaData != null)
                    {
                        if (animatedMetaData.Contains("elitemarker"))
                        {
                            loot.Add((e.GridPos, new RunicMonster()));
                        }
                        else
                        {
                            var iconDescription = _metadataIconMapping.GetOrAdd(animatedMetaData,
                                a => Icons.LogbookChestIcons.FirstOrDefault(icon =>
                                    icon.BaseEntityMetadataSubstrings.Any(a.Contains)));
                            if (iconDescription != null)
                            {
                                loot.Add((e.GridPos, new PathPlannerData.Chest(iconDescription.IconPickerIndex)));
                            }
                        }
                    }

                    continue;
                }
                case ExpeditionEntityType.Relic:
                {
                    var mods = e.Mods;
                    if (mods == null)
                    {
                        continue;
                    }

                    if (e.MinimapIconHide != false) continue;
                    if (!mods.Any(x => x.Contains("ExpeditionRelicModifier"))) continue;

                    if (ContainsWarnMods(mods))
                    {
                        relics.Add((e.GridPos, new WarningRelic()));
                        continue;
                    }

                    var iconDescriptions = mods.SelectMany(mod =>
                        _relicModIconMapping.GetOrAdd(mod, s =>
                            Icons.ExpeditionRelicIcons.Where(icon =>
                                icon.BaseEntityMetadataSubstrings.Any(s.Contains)).ToList())).Distinct();
                    var allSubStrings = iconDescriptions.SelectMany(d => d.BaseEntityMetadataSubstrings).ToList();
                    var fittingMods = mods
                        .SelectMany(mod => allSubStrings.Where(mod.Contains))
                        .Distinct()
                        .Select(x => Icons.GetRelicType(x, Settings.PlannerSettings));
                    relics.AddRange(fittingMods.Select(expeditionRelic => (e.GridPos, expeditionRelic)));

                    break;
                }
                case ExpeditionEntityType.Cave:
                {
                    //Shits given about performance: some? a few?
                    for (int i = 0; i < Settings.PlannerSettings.LogbookCaveRunicMonsterMultiplier; i++)
                    {
                        loot.Add((e.GridPos, new RunicMonster()));
                    }

                    for (int i = 0; i < Settings.PlannerSettings.LogbookCaveArtifactChestMultiplier; i++)
                    {
                        loot.Add((e.GridPos, new PathPlannerData.Chest(IconPickerIndex.LeagueChest)));
                    }

                    break;
                }
                case ExpeditionEntityType.Boss:
                {
                    //Shits given about performance: some? a few?
                    for (int i = 0; i < Settings.PlannerSettings.LogbookBossRunicMonsterMultiplier; i++)
                    {
                        loot.Add((e.GridPos, new RunicMonster()));
                    }

                    break;
                }
            }
        }

        return new ExpeditionEnvironment(
            relics.FindAll(x => x.Item2 != null),
            loot.FindAll(x => x.Item2 != null),
            _explosiveRange / GridToWorldMultiplier,
            _explosiveRadius / GridToWorldMultiplier,
            explosiveCount,
            detonatorPos,
            IsValidPlacement,
            GetExclusionRect() ?? default,
            (GameController.IngameState.Data.MapStats?.GetValueOrDefault(GameStat.MapMinimapMainAreaRevealed) ?? 0) != 0);
    }

    private bool IsValidPlacement(Vector2 x)
    {
        return x.X >= 0 && x.Y >= 0 &&
               x.X < _areaDimensions.X &&
               x.Y < _areaDimensions.Y &&
               _pathfindingData[(int)x.Y][(int)x.X] > 3;
    }

    public override void Render()
    {
        if (Settings.PlannerSettings.ClearSearchHotkey.PressedOnce())
        {
            ClearSearch();
        }

        if (Settings.PlannerSettings.StopSearchHotkey.PressedOnce())
        {
            StopSearch();
        }

        if (Settings.PlannerSettings.StartSearchHotkey.PressedOnce())
        {
            StartSearch();
        }

        ShowExplosiveCountPrompt();

        if (_zoneCleared)
        {
            return;
        }

        var explosives3D = GameController.EntityListWrapper.ValidEntitiesByType[EntityType.IngameIcon]
            .Where(x => x.Path == ExplosivePath)
            .Select(x => x.PosNum)
            .ToList();
        _explosives2DPositions = explosives3D.Select(x => new Vector2(x.X, x.Y)).ToList();
        if (Settings.ExplosivesSettings.ShowExplosives)
        {
            DrawCirclesInWorld(
                positions: explosives3D,
                radius: _explosiveRadius,
                color: Settings.ExplosivesSettings.ExplosiveColor.Value);
        }

        foreach (var e in _cachedEntities.Values)
        {
            switch (GetEntityType(e.Path))
            {
                case ExpeditionEntityType.Marker:
                {
                    var animatedMetaData = e.BaseAnimatedEntityMetadata;
                    if (animatedMetaData != null)
                    {
                        if (animatedMetaData.Contains("elitemarker"))
                        {
                            var mapSettings = Settings.IconMapping.GetValueOrDefault(IconPickerIndex.EliteMonstersIndicator, new IconDisplaySettings());
                            if (mapSettings.ShowOnMap)
                            {
                                DrawIconOnMap(e, mapSettings.Icon ?? ExpeditionIconsSettings.DefaultEliteMonsterIcon, mapSettings.Tint, Vector2.Zero);
                            }

                            if (mapSettings.ShowInWorld)
                            {
                                DrawIconInWorld(e, mapSettings.Icon ?? ExpeditionIconsSettings.DefaultEliteMonsterIcon, mapSettings.Tint, Vector2.Zero);
                            }
                        }
                        else
                        {
                            var iconDescription = _metadataIconMapping.GetOrAdd(animatedMetaData,
                                a => Icons.LogbookChestIcons.FirstOrDefault(icon =>
                                    icon.BaseEntityMetadataSubstrings.Any(a.Contains)));
                            if (iconDescription != null)
                            {
                                var settings = Settings.IconMapping.GetValueOrDefault(iconDescription.IconPickerIndex, new IconDisplaySettings());
                                var icon = settings.Icon ?? iconDescription.DefaultIcon;
                                if (settings.ShowOnMap)
                                {
                                    DrawIconOnMap(e, icon, settings.Tint, Vector2.Zero);
                                }

                                if (settings.ShowInWorld)
                                {
                                    DrawIconInWorld(e, icon, settings.Tint, Vector2.Zero);
                                }
                            }
                        }
                    }

                    continue;
                }
                case ExpeditionEntityType.Relic:
                {
                    var mods = e.Mods;
                    if (e.Mods == null) continue;
                    if (e.MinimapIconHide != false) continue;
                    if (!mods.Any(x => x.Contains("ExpeditionRelicModifier"))) continue;

                    if (ContainsWarnMods(mods))
                    {
                        var mapSettings = Settings.IconMapping.GetValueOrDefault(IconPickerIndex.BadModsIndicator, new IconDisplaySettings());
                        if (mapSettings.ShowOnMap)
                        {
                            DrawIconOnMap(e, mapSettings.Icon ?? ExpeditionIconsSettings.DefaultBadModsIcon, mapSettings.Tint, Vector2.Zero);
                        }

                        if (mapSettings.ShowInWorld)
                        {
                            DrawIconInWorld(e, mapSettings.Icon ?? ExpeditionIconsSettings.DefaultBadModsIcon, mapSettings.Tint, -Vector2.UnitY);
                        }

                        continue;
                    }

                    if (Settings.DrawGoodModsInWorld || Settings.DrawGoodModsOnMap)
                    {
                        var worldIcons = new HashSet<(MapIconsIndex, Color?)>();
                        var mapIcons = new HashSet<(MapIconsIndex, Color?)>();
                        var iconDescriptions = mods.SelectMany(mod =>
                            _relicModIconMapping.GetOrAdd(mod, s =>
                                Icons.ExpeditionRelicIcons.Where(icon =>
                                    icon.BaseEntityMetadataSubstrings.Any(s.Contains)).ToList())).Distinct();
                        foreach (var iconDescription in iconDescriptions)
                        {
                            var settings = Settings.IconMapping.GetValueOrDefault(iconDescription.IconPickerIndex, new IconDisplaySettings());
                            var icon = settings.Icon ?? iconDescription.DefaultIcon;
                            if (settings.ShowOnMap)
                            {
                                mapIcons.Add((icon, settings.Tint));
                            }

                            if (settings.ShowInWorld)
                            {
                                worldIcons.Add((icon, settings.Tint));
                            }
                        }

                        var offset = new Vector2(-worldIcons.Count * 0.5f + 0.5f, 0);
                        foreach (var (icon, tint) in worldIcons)
                        {
                            if (Settings.DrawGoodModsInWorld)
                            {
                                DrawIconInWorld(e, icon, tint, offset);
                            }

                            offset += Vector2.UnitX;
                        }

                        offset = new Vector2(-mapIcons.Count * 0.5f + 0.5f, 0);
                        foreach (var (icon, tint) in mapIcons)
                        {
                            if (Settings.DrawGoodModsOnMap)
                            {
                                DrawIconOnMap(e, icon, tint, offset);
                            }

                            offset += Vector2.UnitX;
                        }
                    }

                    break;
                }
            }
        }

        if (EditedOrNativeScore is { PerPointScore.Count: > 0 } score)
        {
            var path = score.PerPointScore;
            var firstPoint = DetonatorPos?.Pos ?? _playerGridPos;
            var prevPoint = firstPoint;
            for (var i = 0; i < path.Count; i++)
            {
                var point = path[i].Point;
                if (_largeMapOpen)
                {
                    Graphics.DrawLine(GetMapScreenPosition(prevPoint), GetMapScreenPosition(point), Settings.PlannerSettings.PathLineThickness, Settings.PlannerSettings.MapLineColor);
                }

                var worldPos = GetWorldScreenPosition(point);
                Graphics.DrawLine(GetWorldScreenPosition(prevPoint), worldPos, Settings.PlannerSettings.PathLineThickness, Settings.PlannerSettings.WorldLineColor);
                var text = $"#{i}";
                using (Graphics.SetTextScale(Settings.PlannerSettings.TextMarkerScale * 4))
                {
                    Graphics.DrawBox(worldPos, worldPos + Graphics.MeasureText(text) + new Vector2(2, 1), Color.Black);
                    Graphics.DrawText(text, worldPos, Color.White);
                    Graphics.DrawText(text, worldPos + Vector2.UnitX, Color.White);
                    Graphics.DrawText(text, worldPos + new Vector2(2, 0), Color.White);
                    prevPoint = point;
                }
            }

            if (Settings.PlannerSettings.IsSearchRunning)
            {
                _scoreHistory.Add((float)score.TotalScore);
            }

            ShowSearchWindow(score);

            DrawCirclesInWorld(
                positions: path.Select(x => ExpandWithTerrainHeight(x.Point)).ToList(),
                radius: _explosiveRadius,
                color: Settings.PlannerSettings.ExplosiveColor.Value,
                thickness: Settings.PlannerSettings.PathLineThickness);
        }
    }

    private void ShowSearchWindow(PathPlanner.DetailedLootScore score)
    {
        if (Settings.PlannerSettings.ShowScoreHistory &&
            (Settings.PlannerSettings.IsSearchRunning || Settings.PlannerSettings.ShowScoreHistoryAfterSearchEnds) &&
            ImGui.Begin("Expedition planning result"))
        {
            if (ImGui.TreeNode("Detailed view"))
            {
                PathPlanner.DetailedLootScore scoreDiff = null;
                if (_editedPath != null && _editedIndex is { } editedIndex)
                {
                    var pos = GameController.IngameState.ServerData.WorldMousePositionNum.WorldToGrid();
                    var pp = new PathPlanner(Settings.PlannerSettings);
                    pp.Init(score.Environment);
                    var path = _editedPath.ToList();
                    path[editedIndex] = pos;
                    scoreDiff = pp.GetDetailedScore(path, score.Environment);
                    DrawCirclesInWorld([ExpandWithTerrainHeight(pos)], _explosiveRadius, Color.LightBlue);
                    Graphics.DrawLine(GetWorldScreenPosition(_editedPath[editedIndex]), GetWorldScreenPosition(pos), Settings.PlannerSettings.PathLineThickness, Settings.PlannerSettings.WorldLineColor);

                    if (Input.IsKeyDown(Settings.PlannerSettings.ConfirmEditorPlacementHotkey))
                    {
                        _editedPath[editedIndex] = pos;
                        _editedPathEval = pp.GetDetailedScore(_editedPath, score.Environment);
                        _editedIndex = null;
                    }

                    if (Input.IsKeyDown(Keys.Escape))
                    {
                        _editedIndex = null;
                    }
                }

                if (ImGui.BeginTable("Change per explosive", 6, ImGuiTableFlags.Hideable | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("Id");
                    ImGui.TableSetupColumn("Running score");
                    ImGui.TableSetupColumn("Score diff");
                    ImGui.TableSetupColumn("New relic mods");
                    ImGui.TableSetupColumn("New loot");
                    ImGui.TableSetupColumn("Edit");
                    ImGui.TableHeadersRow();

                    var runningScore = 0.0;
                    var runningScoreAfterDiff = 0.0;
                    for (var i = 0; i < score.PerPointScore.Count; i++)
                    {
                        var perPointLootScore = score.PerPointScore[i];
                        var diffOrOld = scoreDiff?.PerPointScore[i] ?? perPointLootScore;
                        ImGui.TableNextRow();
                        ImGui.PushID(i);
                        ImGui.TableNextColumn();
                        ImGui.Text($"{i,2}");
                        ImGui.TableNextColumn();
                        runningScore += perPointLootScore.ScoreDiff;
                        if (scoreDiff != null)
                        {
                            runningScoreAfterDiff += scoreDiff.PerPointScore[i].ScoreDiff;
                            ImGui.Text($"{runningScoreAfterDiff,7:F2}");
                            var valueDiff = runningScoreAfterDiff - runningScore;
                            if (valueDiff != 0)
                            {
                                ImGui.SameLine();
                                ImGui.TextColored(GetCompareColor(runningScoreAfterDiff, runningScore), $"{valueDiff:(+0.00);(-0.00);''}");
                            }
                        }
                        else
                        {
                            ImGui.Text($"{runningScore,7:F2}");
                        }

                        ImGui.TableNextColumn();
                        ImGui.Text($"{diffOrOld.ScoreDiff,7:F2}");
                        if (scoreDiff != null)
                        {
                            var valueDiff = scoreDiff.PerPointScore[i].ScoreDiff - perPointLootScore.ScoreDiff;
                            if (valueDiff != 0)
                            {
                                ImGui.SameLine();
                                ImGui.TextColored(
                                    GetCompareColor(scoreDiff.PerPointScore[i].ScoreDiff, perPointLootScore.ScoreDiff),
                                    $"{valueDiff:(+0.00);(-0.00);''}");
                            }
                        }

                        ImGui.TableNextColumn();
                        ImGui.Text($"{diffOrOld.NewRelics}");
                        if (scoreDiff != null)
                        {
                            var valueDiff = scoreDiff.PerPointScore[i].NewRelics - perPointLootScore.NewRelics;
                            if (valueDiff != 0)
                            {
                                ImGui.SameLine();
                                ImGui.TextColored(
                                    GetCompareColor(scoreDiff.PerPointScore[i].NewRelics, perPointLootScore.NewRelics),
                                    $"{valueDiff:(+0);(-0);''}");
                            }
                        }

                        ImGui.TableNextColumn();
                        ImGui.Text($"{diffOrOld.Loot}");
                        if (scoreDiff != null)
                        {
                            var valueDiff = scoreDiff.PerPointScore[i].Loot - perPointLootScore.Loot;
                            if (valueDiff != 0)
                            {
                                ImGui.SameLine();
                                ImGui.TextColored(
                                    GetCompareColor(scoreDiff.PerPointScore[i].Loot, perPointLootScore.Loot),
                                    $"{valueDiff:(+0);(-0);''}");
                            }
                        }

                        ImGui.TableNextColumn();
                        if (i == _editedIndex)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Button, Color.Green.ToImguiVec4());
                            if (ImGui.Button("Cancel"))
                            {
                                _editedIndex = null;
                            }

                            ImGui.PopStyleColor();
                        }
                        else if (ImGui.Button(" Edit "))
                        {
                            _editedPath ??= score.PerPointScore.Select(x => x.Point).ToList();
                            var pp = new PathPlanner(Settings.PlannerSettings);
                            pp.Init(score.Environment);
                            _editedPathEval = pp.GetDetailedScore(_editedPath, score.Environment);
                            _editedIndex = i;
                        }

                        ImGui.PopID();
                    }

                    ImGui.EndTable();
                }

                if (_editedPath != null && ImGui.Button("Reset edited path"))
                {
                    _editedIndex = null;
                    _editedPath = null;
                    _editedPathEval = null;
                }
            }

            ImGui.PlotLines("Score over time", ref CollectionsMarshal.AsSpan(_scoreHistory)[0],
                _scoreHistory.Count, 0, "", 0, _scoreHistory.Max(),
                new Vector2(0, ImGui.GetContentRegionAvail().Y));
            ImGui.End();
        }
    }

    private static Vector4 GetCompareColor(double @new, double old)
    {
        return @new.CompareTo(old) switch
        {
            > 0 => Color.Green.ToImguiVec4(), 0 => Color.White.ToImguiVec4(), < 0 => Color.Red.ToImguiVec4()
        };
    }

    private bool ContainsWarnMods(List<string> mods)
    {
        return Settings.WarnPhysImmune && mods.Any(x => x.Contains("ExpeditionRelicModifierImmunePhysicalDamage")) ||
               Settings.WarnFireImmune && mods.Any(x => x.Contains("ExpeditionRelicModifierImmuneFireDamage")) ||
               Settings.WarnColdImmune && mods.Any(x => x.Contains("ExpeditionRelicModifierImmuneColdDamage")) ||
               Settings.WarnLightningImmune && mods.Any(x => x.Contains("ExpeditionRelicModifierImmuneLightningDamage")) ||
               Settings.WarnChaosImmune && mods.Any(x => x.Contains("ExpeditionRelicModifierImmuneChaosDamage")) ||
               Settings.WarnCritImmune && mods.Any(x => x.Contains("ExpeditionRelicModifierCannotBeCrit")) ||
               Settings.WarnIgniteImmune && mods.Any(x => x.Contains("ExpeditionRelicModifierImmuneStatusAilments")) ||
               Settings.WarnArmorPen && mods.Any(x => x.Contains("ExpeditionRelicModifierIgnoreArmour")) ||
               Settings.WarnNoEvade && mods.Any(x => x.Contains("ExpeditionRelicModifierHitsCannotBeEvaded")) ||
               Settings.WarnNoLeech && mods.Any(x => x.Contains("ExpeditionRelicModifierCannotBeLeechedFrom")) ||
               Settings.WarnNoFlask && mods.Any(x => x.Contains("ExpeditionRelicModifierGrantNoFlaskCharges")) ||
               Settings.WarnPetrify && mods.Any(x => x.Contains("ExpeditionRelicModifierElitesPetrifyOnHit")) ||
               Settings.WarnCurseImmune && mods.Any(x => x.Contains("ExpeditionRelicModifierImmuneToCurses")) ||
               Settings.WarnCull && mods.Any(x => x.Contains("ExpeditionRelicModifierCullingStrikeTwentyPercent")) ||
               Settings.WarnMonsterBlock && mods.Any(x => x.Contains("ExpeditionRelicModifierAttackBlockSpellBlockMaxBlockChance")) ||
               Settings.WarnMonsterResist && mods.Any(x => x.Contains("ExpeditionRelicModifierResistancesAndMaxResistances")) ||
               Settings.WarnMonsterRegen && mods.Any(x => x.Contains("ExpeditionRelicModifierElitesRegenerateLifeEveryFourSeconds")) ||
               Settings.WarnAlwaysCrit && mods.Any(x => x.Contains("ExpeditionRelicModifierAlwaysCrit")) ||
               Settings.WarnReducedDamageTaken && mods.Any(x => x.Contains("ExpeditionRelicModifierReducedDamageTaken")) ||
               Settings.WarnBleed && mods.Any(x => x.Contains("ExpeditionRelicModifierBleedOnHitBleedDuration")) ||
               Settings.WarnCorrupted && mods.Any(x => x.Contains("ExpeditionRelicModifierExpeditionCorruptedItemsElite")) ||
               Settings.WarnPoison && mods.Any(x => x.Contains("ExpeditionRelicModifierAllDamagePoisonsPoisonDuration")) ||
               Settings.WarnPhysicalAsExtraChaos && mods.Any(x => x.Contains("ExpeditionRelicModifierDamageAddedAsChaos")) ||
               false;
    }

    private void DrawIconOnMap(EntityCacheItem entity, MapIconsIndex icon, Color? color, Vector2 offset)
    {
        if (_largeMapOpen)
        {
            var halfsize = Settings.MapIconSize / 2.0f;
            var point = GetEntityPosOnMapScreen(entity) + offset * halfsize * 2;
            var entityPos = entity.Pos;
            var entityPos2 = new Vector2(entityPos.X, entityPos.Y);

            DrawIcon(icon, color, point, entityPos2,
                Settings.ExplosivesSettings.HideCapturedEntitiesOnMap,
                Settings.ExplosivesSettings.MarkCapturedEntitiesOnMap,
                Settings.ExplosivesSettings.CapturedEntityMapFrameColor,
                Settings.PlannerSettings.CapturedEntityMapFrameColor,
                Settings.ExplosivesSettings.CapturedEntityMapFrameThickness,
                Settings.MapIconSize);
        }
    }

    private void DrawIconInWorld(EntityCacheItem entity, MapIconsIndex icon, Color? color, Vector2 offset)
    {
        var halfsize = Settings.WorldIconSize / 2.0f;
        var entityPos = entity.Pos;
        var entityPos2 = new Vector2(entityPos.X, entityPos.Y);
        var point = Camera.WorldToScreen(entityPos) + offset * halfsize * 2;
        DrawIcon(icon, color, point, entityPos2,
            Settings.ExplosivesSettings.HideCapturedEntitiesInWorld,
            Settings.ExplosivesSettings.MarkCapturedEntitiesInWorld,
            Settings.ExplosivesSettings.CapturedEntityWorldFrameColor,
            Settings.PlannerSettings.CapturedEntityWorldFrameColor,
            Settings.ExplosivesSettings.CapturedEntityWorldFrameThickness,
            Settings.WorldIconSize);
    }

    private void DrawIcon(
        MapIconsIndex icon,
        Color? color,
        Vector2 displayPosition,
        Vector2 worldPosition,
        bool hideCaptured,
        bool markCaptured,
        Color capturedFrameColor,
        Color plannerCapturedFrameColor,
        int frameThickness,
        float iconSize)
    {
        var halfsize = iconSize / 2.0f;
        var rect = new RectangleF(displayPosition.X, displayPosition.Y, 0, 0);
        rect.Inflate(halfsize, halfsize);
        var calculateExplosiveFrameDisplay = hideCaptured || markCaptured;
        var isInExplosiveRadius = calculateExplosiveFrameDisplay &&
                                  _explosives2DPositions.Any(x => Vector2.Distance(x, worldPosition) < _explosiveRadius);
        var gridPosition = worldPosition.WorldToGrid();
        var isInPlannedExplosiveRadius = calculateExplosiveFrameDisplay &&
                                         EditedOrNativeScore is { PerPointScore.Count: > 0 } path &&
                                         path.PerPointScore.Any(x => Vector2.Distance(x.Point, gridPosition) < _explosiveRadius / GridToWorldMultiplier);

        if (markCaptured)
        {
            var plannedRect = rect;
            if (isInExplosiveRadius)
            {
                Graphics.DrawFrame(rect, capturedFrameColor, frameThickness);
                plannedRect.Inflate(frameThickness, frameThickness);
            }

            if (isInPlannedExplosiveRadius)
            {
                Graphics.DrawFrame(plannedRect, plannerCapturedFrameColor, frameThickness);
            }
        }

        if (!isInExplosiveRadius || !hideCaptured)
        {
            Graphics.DrawImage(TextureName, rect, SpriteHelper.GetUV(icon), color ?? Color.White);
        }
    }

    private Vector2 GetMapScreenPosition(Vector2 gridPos)
    {
        return _mapCenter + TranslateGridDeltaToMapDelta(gridPos - _playerGridPos, GameController.IngameState.Data.GetTerrainHeightAt(gridPos) - _playerZ);
    }

    private Vector2 GetWorldScreenPosition(Vector2 gridPos)
    {
        return Camera.WorldToScreen(ExpandWithTerrainHeight(gridPos));
    }

    private Vector2 GetEntityPosOnMapScreen(EntityCacheItem entity)
    {
        var point = _mapCenter + TranslateGridDeltaToMapDelta(entity.GridPos - _playerGridPos, (entity.RenderZ ?? 0) - _playerZ);
        return point;
    }

    private Vector2 TranslateGridDeltaToMapDelta(Vector2 delta, float deltaZ)
    {
        deltaZ /= GridToWorldMultiplier; //z is normally "world" units, translate to grid
        return (float)_mapScale * new Vector2((delta.X - delta.Y) * CameraAngleCos, (deltaZ - (delta.X + delta.Y)) * CameraAngleSin);
    }

    private enum ExpeditionEntityType
    {
        None,
        Relic,
        Marker,
        Cave,
        Boss,
    }

    private record EntityCacheItem(
        string Path,
        Lazy<string> BaseAnimatedEntityMetadataCache,
        List<string> Mods,
        Vector3 Pos,
        Vector2 GridPos,
        float? RenderZ,
        float? RenderSize,
        bool? MinimapIconHide)
    {
        public string BaseAnimatedEntityMetadata => BaseAnimatedEntityMetadataCache.Value;

        public EntityCacheItem Merge(EntityCacheItem other)
        {
            return new EntityCacheItem(
                Path ?? other.Path,
                BaseAnimatedEntityMetadata == null ? other.BaseAnimatedEntityMetadataCache : BaseAnimatedEntityMetadataCache,
                Mods ?? other.Mods,
                Pos,
                GridPos,
                RenderZ ?? other.RenderZ,
                RenderSize ?? other.RenderSize,
                MinimapIconHide ?? MinimapIconHide);
        }
    }


    public override void EntityAdded(Entity entity)
    {
        if (entity.Type is EntityType.IngameIcon or EntityType.Terrain && GetEntityType(entity.Path) != ExpeditionEntityType.None)
        {
            _cachedEntities[entity.Id] = BuildCacheItem(entity);
        }
    }

    private static EntityCacheItem BuildCacheItem(Entity entity)
    {
        return new EntityCacheItem(
            entity.Path,
            new Lazy<string>(() => entity.GetComponent<Animated>()?.BaseAnimatedObjectEntity?.Metadata, LazyThreadSafetyMode.None),
            entity.GetComponent<ObjectMagicProperties>()?.Mods,
            entity.PosNum,
            entity.PosNum.WorldToGrid(),
            entity.GetComponent<Render>()?.Z,
            entity.GetComponent<Render>()?.BoundsNum is { } b ? Math.Min(b.X, b.Y) : null,
            entity.GetComponent<MinimapIcon>()?.IsHide);
    }
}