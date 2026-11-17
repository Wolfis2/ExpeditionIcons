using System;
using System.Diagnostics;

namespace ExpeditionIcons;

internal sealed class EncounterCompletionTracker
{
    private int _maximumExplosives;
    private bool _detonationObserved;
    private bool _monstersObserved;
    private long? _quietSince;

    public void Pause() => _quietSince = null;

    public bool Observe(bool? detonatorTargetable, int explosives, bool expeditionMonstersAlive, long timestamp)
    {
        _maximumExplosives = Math.Max(_maximumExplosives, explosives);
        if (detonatorTargetable == false && _maximumExplosives > 0 && explosives < _maximumExplosives)
        {
            _detonationObserved = true;
        }

        if (!_detonationObserved)
        {
            return false;
        }

        _monstersObserved |= expeditionMonstersAlive;
        if (!_monstersObserved || explosives != 0 || expeditionMonstersAlive || detonatorTargetable != false)
        {
            _quietSince = null;
            return false;
        }

        _quietSince ??= timestamp;
        return Stopwatch.GetElapsedTime(_quietSince.Value, timestamp) >= TimeSpan.FromSeconds(5);
    }
}
