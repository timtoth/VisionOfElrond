# Vision of Elrond

An in-game tracking overlay for the Steam version of **Journeys in Middle-earth**. It is a
[BepInEx](https://github.com/BepInEx/BepInEx) plugin that draws a small panel over the game showing
live scenario state, on both the tile map and battle maps.

It only reads game state. It doesn't change game files, assets, or your saves.

## What it shows

- Adventure name, and whether you're on a battle map
- Round and phase (hero / shadow)
- Threat (current / max), darkness, and the thresholds of upcoming threat events
- Heroes: corruption, defeated status, last stand difficulty, and a last stand alert
- Enemies in play: units remaining, HP, armor, sorcery, elite and exhausted status

## Install

1. Download **BepInEx 5.4.x for Windows x86** (`BepInEx_win_x86_5.4.*.zip`) from the
   [BepInEx releases](https://github.com/BepInEx/BepInEx/releases). The game is 32-bit, so the x86
   build is required.
2. Extract it into the game folder, next to `JiME.exe`. By default that is
   `C:\Program Files (x86)\Steam\steamapps\common\Journeys in Middle-earth`.
3. Launch the game once so BepInEx creates its folders, then quit.
4. Copy `JiMEOverlay.dll` into `BepInEx\plugins\JiMEOverlay\`.

## Use

- **F8** shows or hides the panel.
- Drag the panel by its title bar; the position is remembered.
- Settings (toggle key, visible on launch, position) are in
  `BepInEx\config\com.timto.jimeoverlay.cfg`.
- If something isn't showing, check `BepInEx\LogOutput.log`.

## Build from source

Requires the .NET SDK (any recent version) and the game installed, since the project references the
game's assemblies.

```
dotnet build JiMEOverlay.csproj -c Release
```

The build copies the plugin straight into the game's `BepInEx\plugins\JiMEOverlay` folder. If your
game is installed somewhere else, change `GameDir` in `JiMEOverlay.csproj`. The game only picks up a
new build on restart.

## Uninstall

Delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt` and the `BepInEx`
folder from the game folder, or run **Verify integrity of game files** in Steam.

## Disclaimer

Fan-made and unofficial. Not affiliated with or endorsed by Fantasy Flight Games or Asmodee. No game
code or assets are included in this repository.
