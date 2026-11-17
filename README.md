# ExpeditionIcons

## Bomb chain planner

Set a Start Search Hotkey in the planner settings, then approach an expedition
detonator and press the key to generate a suggested chain. The default F13
binding is intended to be changed if your keyboard does not have that key.
You can also start the search using the settings button. The planner tries to
read the on-screen bomb count automatically before any bombs have been placed.
If it cannot read the count, or bombs are already placed, enter the total bomb
count in the prompt and press Enter or select Start search. The prompt remembers
the last count until the plugin is reloaded.
If you cannot interact with the prompt, open the ExileApi menu with F12.

If no path appears, check the ExileApi debug log for an ExpeditionIcons message.
The planner needs a detected detonator and a valid bomb count. You do not need
to enter explosive placement mode before planning.
The log also warns when no scorable loot has been detected; move closer and
let the expedition markers load before starting another search.

Path lines and suggested explosion circles default to 4 pixels and can be
adjusted with Path Line Thickness. Markers are drawn at four times the Text
Marker Scale setting and use a heavier outline for visibility.

If you like it, you can donate via:

BTC: bc1qke67907s6d5k3cm7lx7m020chyjp9e8ysfwtuz

ETH: 0x3A37B3f57453555C2ceabb1a2A4f55E0eB969105 
