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

## Game data (GDLoLHook.dll)

For exact and instant results the app loads a small helper, `GDLoLHook.dll`, into Grim Dawn while both run.

- **What it reads:** where you and the characters near you are, their health, and whether they are friend or foe.
- **What it does:** the same actions you do with the mouse and keyboard (attack this enemy, walk to this point, use this hotbar slot, drink a potion), so the auto attack and the walk after a cast need no cursor movement.
- **What it never does:** change stats, items, experience, drops, damage or saves. It patches no game code: it calls the game's own named functions and hooks one call of the game's main loop through its import table.
- **Fallback:** if it cannot load (game running as administrator, antivirus, a game update that renamed functions), the app reads the screen instead and says so in the log. Each feature checks that the game really acted (the character moved, the potion went on cooldown) and switches back to the mouse/keyboard way after three misses.
- **Switch it off:** Settings → Auto attack → Enemy detection → *screen*, and the "game data" options in the other tabs.

Some antivirus programs dislike any tool that loads into another program, and the files are not code-signed. Windows SmartScreen may warn on first start ("More info" → "Run anyway"). The full source is here; build it yourself if you prefer.

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
