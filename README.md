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

## Prompt rules

The plugin can add text before or after the game's message popups. For example, with Aragorn in the
party, a rally phase popup that says "reset your deck and scout 2" gets this extra line: "Heroes
adjacent to Aragorn: **Scout +1** (scout 3)." Only the text on screen changes; game rules and saves
are not affected.

Two files in `BepInEx\config\JiMEOverlay\` drive this:

- **`prompts.json`**: every text the game has loaded, by localization key, in your game's language.
  The plugin writes it while the game runs and adds new keys as the game loads more text. Use it to
  find the key or exact wording of a prompt. Don't edit it; it gets overwritten.
- **`prompt-rules.json`**: your rules. Edit it any time, even while the game is running; changes
  apply to the next popup. If the file has a mistake, the previous rules stay in effect and
  `BepInEx\LogOutput.log` says what's wrong.

Each rule looks like this:

```json
{
  "id": "aragorn-rally-scout",
  "description": "Aragorn: in the rally phase, heroes adjacent to him scout 1 more than the app says.",
  "enabled": true,
  "when": {
    "heroesInParty": ["Aragorn"],
    "textMatches": "\\breset\\w* (?:your|their|his|her) decks?\\b[\\s\\S]{0,80}?\\bscouts? (\\d+)"
  },
  "append": "Heroes adjacent to Aragorn: [b]Scout +1[/b] (scout {1+1})."
}
```

A rule applies when every condition under `when` holds; leave a condition out to skip it:

- **`heroesInParty`**: any of these heroes is in the party (name contains, ignoring case).
- **`keys`**: the popup uses any of these localization keys from `prompts.json`; `*` is a wildcard,
  e.g. `"UI_RALLY_*"`. This is the most reliable way to target one specific prompt.
- **`textMatches`**: a .NET regex matched against the popup text with formatting tags removed,
  ignoring case. Backslashes are doubled in JSON.
- **`battleMap`**: `true` for battle maps only, `false` for tile maps only.

`append` adds text after the popup text, `prepend` before it, separated by a blank line. `[b]bold[/b]`
and `[i]italic[/i]` work. Placeholders: `{1}` inserts what `textMatches`' first `( )` group matched,
`{1+1}` adds 1 to it as a number (`{1-1}` subtracts), and `{hero}` is the matched hero's name. Set
`"enabled": false` at the top of the file to turn all rules off.

Rules from the 0.5.x `[Reminders]` config section are moved into `prompt-rules.json` automatically
the first time 0.6 runs.

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
code, text or assets are included in this repository; `prompts.json` is generated on your own machine
from your copy of the game and should not be shared.
