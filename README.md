# SimpleWSO

Gunners and Weapon System Officers for Nuclear Option. Simply select a friendly aircraft in spectate, Press H, become a WSO.

## Features ##
- One button enter/exit
- Gunner turret reticule (when turret selected) shown on pilot screen for coordination
- Seperate target lists for pilot and Gunner, can be pushed bi-directionally from Gunner <-> Pilot
- While turrets (Chicane gun, Ibis Turrets) are selected by gunner, Pilot cannot take control. Stops pilot ripping control from gunner while cycling weapons.
- When Pilot and Gunner aren't controlling a turret, AI takes over like vanilla.
- Works with AI or Player controlled aircraft
- Works with all modded aircraft, KAR and BOTE
- Auto creates camera entry positions for modded aircraft to allow custom placement of external cameras. Configurable in F1 or config file (see below)

## How to use ##
1. Install BepinEx https://github.com/BepInEx/BepInEx
2. Download and place the SimpleWSO folder (containing SimpleWSO.dll) in your Nuclear Option BepInEx plugin folder. (steamapps\common\Nuclear Option\BepInEx\plugins)
3. Start the game either through Nuclear Option Mod Manager (recommended) or via steam.
4. Start or join a multiplayer or singleplayer lobby.
5. Join a faction
6. Click on a player or AI friendly aircraft.
7. Press H by default to enter gunner view.
8. Press U by default to share targets from Pilot -> Gunner or Gunner -> Pilot
9. Press K to cycle between gunner camera positions."
10. Press H again to leave the gunner seat and be put back into spectate.

## Limitations and things to note ##
Every attempt has been made to make this mod as seamless as possible, but there are some unfortunate caveats:
- MFDs do not work for the gunner, they still work for the pilot but its an area I did not want to touch for 1.0, potentially in the future I will attempt to get the vanilla ones working.
-  WSO does not have flight control, a given but it means you can't take over sticks if they need to leave or AFK
-  WSO does not have counter measures, no design reason why they do not, I just did not see it as a priority because the pilot needs SOMETHING to do.
-  I do not know if more than one person can be in an aircraft as a gunner at once. theoretically yes but if not then uhhh don't do that.

## Configuration ## 
I've set up camera positions (Gunner camera offset cycleable with K) for most of the vanilla two+ seater aircraft. Often placed where the "targeting pod" is on most aircraft e.g. the Cricket, Compass, Chichane. For the Ibis and Tarantula the
additional camera views are on the "gun" positions, so door guns for Ibis and side gun for the Tarantula

You can change these camera offsets along with keybinds and an option so that when a target list is shared between Pilot and Gunner and vice versa it can either fully replace or append the target lists, based on how much you trust your other 
crewman.

BepInEx Configuration Manager (https://github.com/BepInEx/BepInEx.ConfigurationManager or downloadable via NOMM) is the easiest way to change these. You just press F1 in game. Otherwise you can change the settings in: 
`\Nuclear Option\BepInEx\config\com.bongus.simplewso.cfg`

Camera offsets are under **F1 → SimpleWSO → CameraOffsets**. Each aircraft appears as its own collapsible group (e.g. **UH-90 Ibis**) with **Position 1–4** inside. New airframes get the same treatment after you gunner them once (reopen F1 if the group is not visible yet). Values are aircraft-local meters (`X` right, `Y` up, `Z` forward). Empty positions are ignored in-game.

### Default Controls ###
| Key | Config entry | Action |
| --- | --- | --- |
| H | `ToggleGunnerKey` | Possess gunner seat on followed aircraft / leave |
| U | `ShareTargetsKey` | Share targets with the other seat |
| K | `CycleCameraPositionKey` | Cycle configured gunner camera positions |

Whole things CC0 do what you want with it.
