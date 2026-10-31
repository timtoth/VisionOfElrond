# Vision of Elrond

An in-game tracking overlay for the Steam version of **Journeys in Middle-earth**. It is a
[BepInEx](https://github.com/BepInEx/BepInEx) plugin that draws a small panel over the game showing
live scenario state, on both the tile map and battle maps.

It only reads game state. It doesn't change game files, assets, or your saves.

## What it shows

- Adventure name, and whether you're on a battle map
- **Round 0 setup checklist** at the start of each adventure: role cards, deck building, re-adding
  each hero's titles (listed by name), item cards, shuffling, and placing the starting board. Tick
  items off or press "Setup done"; the checklist resets for each new adventure
- Round and phase (hero / shadow)
- Threat (current / max), darkness, and the thresholds of upcoming threat events
- Heroes with their earned titles and role, e.g. "Aragorn, the Unfallen (Captain)", plus corruption,
  defeated status, last stand difficulty, and a last stand alert
- Enemies in play: units remaining, HP, armor, sorcery, elite and exhausted status

Clicks on the panel don't pass through to the game underneath.

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
- Settings (toggle key, visible on launch, position, extra setup checklist items) are in
  `BepInEx\config\com.timto.jimeoverlay.cfg`. Add your own checklist steps under `[Setup]`
  `ExtraItems`, separated by `|`.
- If something isn't showing, check `BepInEx\LogOutput.log`.

## Hero reminders

The plugin can add a reminder line to the game's message popups when a certain hero is in the
party. For example, with Aragorn in the party, a rally phase popup that says "reset your deck and
scout 2" gets this extra line: "Heroes adjacent to Aragorn: **Scout +1** (scout 3)." The number is
worked out from the prompt, so "scout 4" shows "(scout 5)". Other Scout prompts, like battle setup,
are left alone. Only the text on screen changes; game rules and saves are not affected.

Rules are under `[Reminders]` in the config file. `Enabled` turns the feature on or off, and
`Rule1` to `Rule8` each hold one rule in the form `HeroName|regex|text`:

- **HeroName**: the rule applies when any party hero's name contains this text.
- **regex**: a .NET regular expression matched against the popup text, with formatting tags
  removed. Everything between the first and last `|` counts as the regex, so `|` alternation works.
- **text**: the line to add. NGUI tags like `[b]bold[/b]` and `[i]italic[/i]` work, and `\n` starts
  a new line. `{1}` inserts what the regex's first `( )` group matched, and `{1+1}` adds 1 to it
  as a number (`{1-1}` subtracts).

Matching ignores case. BepInEx writes backslashes doubled in the file, so the default rule looks
like this (writing single backslashes works too):

```
Rule1 = Aragorn|\\breset\\w* (?:your|their|his|her) decks?\\b[\\s\\S]{0,80}?\\bscouts? (\\d+)|Heroes adjacent to Aragorn: [b]Scout +1[/b] (scout {1+1}).
```

If your config still has the 0.5.0 default (which matched every Scout prompt), it is updated to
this one automatically. A rule you've edited is never changed.

The game only applies config changes made while it is running if you use a config manager plugin;
otherwise edit the file and restart the game.

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
