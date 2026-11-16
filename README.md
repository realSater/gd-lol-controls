# GD LoL Controls

MOBA-style controls for **Grim Dawn**: move with the right mouse button, skills on Q W E R D F, cast while walking, and an auto-attack key. One small tray app for Windows. Free and open source.

> **Fair play.** This is a control tool, not a cheat. It only does what you could do with your own mouse and keyboard. It does not change stats, items, experience, drops, damage or save files, and it is not made for botting or unattended farming. Use it in single player, or in multiplayer only with the other players' agreement. See [Disclaimer](#disclaimer).

## Features

- **Right-click to move and attack** (works like the game's left button). Right-click stays normal in every other program.
- **Skills on Q W E R D F, 1-4, potions on 5 / 6.** Written into the game's own key bindings, with a backup and a one-click restore.
- **Cast, then keep walking.** Press a skill while walking: the skill fires at your cursor, then your character continues to where you were going. Repeat as often as you like (kiting).
- **Auto attack (A).** Hold A: the nearest enemy (or the weakest, or the one nearest your mouse) is attacked until it dies, then the next. Your cursor stays free for aiming and walking.
- **Optional:** kite steps between attacks, attack-move, a range ring drawn over the game, auto potion (health and energy), smart cast (skills hit the auto-attack target).
- **Everything can be switched off or tuned** in Settings.

## Requirements

- Windows 10 or 11, 64-bit, with .NET Framework 4.8 (part of Windows).
- Grim Dawn, 64-bit (Steam or GOG). Borderless or windowed mode recommended.

## Start

1. Download the release zip and unpack it anywhere (for example `C:\Games\GDLoLControls`).
2. **Close Grim Dawn the first time** (the key bindings can only be written while it is closed).
3. Run `GDLoLControls.exe`. A small round **GD** icon appears in the tray. A short notice is shown once.
4. Start Grim Dawn. The icon turns **green** while the game is in front.

Nothing is installed, no administrator rights are needed, and nothing connects to the internet. Settings and logs are kept next to the exe (or in `%APPDATA%\GDLoLControls` if that folder is read-only).

| Icon | Meaning |
|---|---|
| green | Grim Dawn is in front, the app is working |
| amber | waiting for Grim Dawn (or it is in the background) |
| grey, red slash | switched off (left-click the icon, or `Ctrl+Alt+F10`) |

Tray menu: Enabled · Settings · Calibrate · Apply key bindings now · Restore original key bindings · Start with Windows · Open folder · View log · Exit.

## Key bindings

The layout is written into Grim Dawn's own `keybindings.txt` (`Documents\My Games\Grim Dawn\Settings`), so the in-game bar shows the same keys.

- Written only while the game is closed. If the game is running, the app translates keys live until the next restart of the game.
- The original file is saved once (`backups\keybindings.original.txt`), every change keeps a dated copy, and the file is checked before anything is written.
- Keys that lose their usual meaning move: Switch Weapons → B, Codex → N, Drop Item → 9, Force Move → 8. Change everything in Settings → Controls.
- **Undo:** tray → *Restore original key bindings* (with the game closed).

## Auto attack

Hold **A** (or press it once in toggle mode): an enemy in range is attacked and kept until it dies, then the next one. Your own clicks always come first. Settings → Auto attack has the range, which enemy first, hold or toggle, and the optional extras. The game no longer receives A (in the stock game A is "move to cursor").

## Mouse buttons and interactions

GD LoL Controls adds MOBA-style right-click movement without removing the game's original left-click functionality.

- **Right-click:** Mirrors Grim Dawn's normal left-click behavior for moving, attacking, and other actions that the game handles through left-click.
- **Left-click:** Still works normally. You can continue using it to pick up loot, interact with NPCs and objects, attack, and move as Grim Dawn allows.
- **Genuine right-click:** If you need the game's original right-click action, configure a passthrough key under **Settings → Advanced → Hold this key for a genuine right-click**. Hold that key while right-clicking.
- **Outside Grim Dawn:** Right-click behaves normally in Windows and other applications.
- **Customization:** Disable right-click mirroring entirely under **Settings → Advanced → Right mouse button in Grim Dawn → off**.

**Important:** Right-click is not a dedicated movement-only command. It mirrors left-click, so clicking directly on an enemy or interactive object can still trigger the game's normal action.

## Game data and screen-reading modes

GD LoL Controls supports two ways of detecting what happens in Grim Dawn.

### Game data mode (GDLoLHook.dll)

For precise targeting and movement tracking, the application can load `GDLoLHook.dll` into the running game.

The helper can read character positions, health, nearby entities, and other gameplay information. Enabled features can also use the game's own action functions for movement, attacks, skills, and potions.

This avoids some of the limitations of visual detection, including estimating enemy positions from on-screen health bars.

The helper does not intentionally modify character stats, inventory, experience, damage, drops, or save files. However, it does interact with the game's running process, and some security products may flag this behavior.

### Screen-reading mode

If you prefer not to load a helper DLL into Grim Dawn, you can use the application's screen-reading and keyboard/mouse input methods.

To disable all game-data-dependent features:

1. Open **Settings → Auto attack**.
2. Change **Enemy detection** to **screen**.
3. Disable **Kite steps and attack-move by the game's own move command**.
4. Disable **Smart cast**.
5. Disable **Drink through the game's own potion slot**.
6. Open **Settings → Movement resume** and disable **Use game data: the exact destination from the game and its own move command**.
7. Save your settings.

When no feature requests game data, the application stops requesting the helper and unloads it from the game.

**Screen mode limitations:**

- Enemy detection relies on visual information, such as health bars, rather than exact internal game positions.
- Some actions may be less precise than their game-data equivalents.
- Borderless or windowed mode is recommended because screen capture is required.
- Calibration may be necessary for reliable movement tracking and cursor recognition.
- Certain game-data-only functions, including smart cast targeting, will no longer be available.

Screen mode does not disable the application's ordinary mouse and keyboard input handling. It is an alternative to the in-game helper, not a guarantee of compatibility with every antivirus or anti-cheat system.

### Antivirus and Windows SmartScreen

GD LoL Controls is an independent, open-source project, and its executables are not currently code-signed.

Windows SmartScreen may display an unknown-publisher warning, and some antivirus products may flag the helper DLL because it is designed to load into another process.

A warning does not automatically mean a file is malicious, but it should not be ignored without verification.

You can inspect the source in `src/` and `hook/`, build the project yourself using `build.ps1`, or choose screen-reading mode.

The application does not require an internet connection and is designed to keep its settings and logs locally.


## Settings

Tray → Settings, or edit `config.ini` next to the exe (it reloads when saved). Tabs: Controls · Auto attack · Movement resume · Calibration · Diagnostics (live view of what the app sees) · Advanced · Live log.

Calibration is optional with game data. Without it (screen mode), tray → Calibrate teaches the app where your character stands and what the ground cursor looks like.

## Troubleshooting

- **Nothing happens in game.** Is the icon green while the game is in front? If Grim Dawn runs as administrator, start it normally (or run this app as administrator too).
- **Q W E R D F do nothing.** Is the app running? Tray → *Apply key bindings now* shows the reason if the file was not written.
- **I use the WASD scheme.** Grim Dawn then uses `alternate_keybindings.txt`, which this app does not edit.
- **Exclusive fullscreen.** Use borderless or windowed; the range ring and screen mode need it.
- **Something feels off.** Tray → View log (`logs\gdlol.log`) and Settings → Diagnostics show what the app sees and decides. Please attach the relevant log lines to bug reports.

## Build from source

```powershell
.\build.ps1              # dist\GDLoLControls.exe, and dist\GDLoLHook.dll if the Visual Studio C++ Build Tools are installed
.\tests\run-tests.ps1    # builds and runs the unit tests
```

The app is C# 5 compiled with the compiler that ships with Windows (no SDK needed). The helper is plain C++ built with MSVC (`/MT`, no dependencies). Source: `src\` (app), `hook\` (helper), `tests\` (unit tests).

## Disclaimer

- GD LoL Controls is an independent fan project. It is **not affiliated with, endorsed or sponsored by Crate Entertainment or Riot Games**. *Grim Dawn* is a trademark of Crate Entertainment; *League of Legends* is a trademark of Riot Games. Names are used only to describe what the tool works with and what it is inspired by.
- It is meant for convenience and accessibility in active play, not for cheating, botting, unattended farming or gaining an unfair advantage over other players. Do not use it where third-party tools are not allowed.
- Provided **"as is", without warranty of any kind** (see [LICENSE](LICENSE)). You use it at your own risk; the authors are not responsible for any damage, lost data, lost characters (including hardcore characters) or other consequences. Keep backups of your saves.
- It collects no data and sends nothing anywhere.
- **Rights holders:** if you would like anything changed or removed, please open an issue. Requests will be handled promptly.

## License

[MIT](LICENSE).
