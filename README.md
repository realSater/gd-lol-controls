![GD LoL Controls — MOBA-style movement for Grim Dawn](assets/gd-lol-controls-banner.png)

# GD LoL Controls

**Bring MOBA-style muscle memory to Grim Dawn.** Right-click to move and attack, cast with `Q W E R D F`, keep walking after a skill, and optionally use an assisted auto-attack key—all from a small Windows tray application.

**Free and open source · MIT licensed · Windows 10/11 · Steam & GOG**

**[Download the latest release](https://github.com/realSater/gd-lol-controls/releases/latest)** · **[See how it works](#features)** · **[Report an issue](https://github.com/realSater/gd-lol-controls/issues)**

> **Fair-play intent:** GD LoL Controls is made for convenience and accessibility during *active* play, not unattended farming or character/stat modification. It offers optional automation, including target selection and potions, which different players and multiplayer groups may judge differently. Use it in single player, or with the agreement of other players, and follow any applicable rules. See [Fair play, safety & disclaimer](#fair-play-safety--disclaimer).

## Features

| Feature | What it does |
| --- | --- |
| **Right-click movement & attack** | Right-click mirrors Grim Dawn's normal left-click action *in the game only*. It stays a normal right-click in other applications. |
| **MOBA-inspired hotkeys** | Skills on `Q W E R D F` and `1–4`, potions on `5` / `6`. Updates the game's own keybindings so the hotbar shows the assigned keys. |
| **Cast, then keep walking** | Cast toward your cursor while walking; the character then resumes moving toward the previous destination. Useful for active kiting. |
| **Assisted auto-attack (`A`)** | Hold `A` (or use toggle mode) to attack targets selected by distance, health, or proximity to the cursor. Your manual clicks take priority. |
| **Optional combat helpers** | Kite steps, attack-move, an on-screen range ring, automatic health/energy potions, and smart cast. |
| **Configurable & reversible** | Tune or disable behavior in Settings, back up your original bindings, and restore them from the tray menu. |

**Release status:** v0.1.0 is the first public release. **Smart cast and energy-potion behavior have not yet been verified in gameplay** in its release notes; treat those features as experimental and report problems.

## Quick start

### Requirements

- **Windows 10 or 11 (64-bit)** with **.NET Framework 4.8**.
- **Grim Dawn (64-bit)** from Steam or GOG.
- **Borderless or windowed mode recommended**, especially for screen reading and the range indicator.

### Install and run

1. **[Download the latest release ZIP](https://github.com/realSater/gd-lol-controls/releases/latest)** and extract it somewhere convenient, such as `C:\Games\GDLoLControls`.
2. **Close Grim Dawn before the first run.** The game's keybindings file can only be safely updated while the game is closed.
3. Run **`GDLoLControls.exe`**. A small round **GD** icon appears in the Windows system tray; a short notice is shown once.
4. Launch **Grim Dawn**. When the game is in the foreground, the tray icon turns green.

There is **no installer** and **no internet connection required**. Normally, no administrator privileges are needed. Settings and logs are stored next to the executable, or under `%APPDATA%\GDLoLControls` if that directory is read-only.

### Tray icon

| Appearance | Meaning |
| --- | --- |
| **Green** | Grim Dawn is the active window and the tool is enabled. |
| **Amber** | Waiting for Grim Dawn, or Grim Dawn is in the background. |
| **Grey, with red slash** | Tool disabled; left-click the icon or press `Ctrl+Alt+F10` to toggle. |

Tray menu: **Enabled · Settings · Calibrate · Apply key bindings now · Restore original key bindings · Start with Windows · Open folder · View log · Exit**.

## Controls at a glance

| Input | Default behavior |
| --- | --- |
| **Right mouse button** | Mirrors the game's ordinary **left-click**, including movement and attack. |
| **Left mouse button** | Still works normally for movement, attacks, looting, NPCs, and other in-game interactions. |
| **`Q W E R D F`** | Main skill bindings. |
| **`1 2 3 4`** | Additional skills—useful for multi-skill or “piano” builds. |
| **`5` / `6`** | Potions. |
| **`A`** | Assisted auto-attack (hold by default; toggle mode is available). |

### Mouse interactions and genuine right-click

- **Right-click isn't movement-only.** It mirrors the game's ordinary left-click, so clicking an enemy or interactive object may trigger Grim Dawn's normal attack or interaction behavior.
- **Left-click isn't removed.** Continue using it for loot, NPC conversations, objects, and any other action the game normally permits with left-click.
- **Need the game's original right-click?** Configure a passthrough modifier under **Settings → Advanced → Hold this key for a genuine right-click**, then hold that key while right-clicking.
- **Want the original behavior?** Set **Settings → Advanced → Right mouse button in Grim Dawn** to **off**.
- **Outside Grim Dawn**, mouse behavior is unchanged.

### How keybinding changes work

The app writes its layout to Grim Dawn's existing `keybindings.txt` file in:

```text
Documents\My Games\Grim Dawn\Settings\keybindings.txt
```

- The file is written **only while Grim Dawn is closed**. If the game is already open, live key translation is used until its next restart.
- The original is backed up as `backups\keybindings.original.txt`. Changes also create dated copies, and the file is checked before being written.
- Because some keys are repurposed, the default assignments for **Switch Weapons**, **Codex**, **Drop Item**, and **Force Move** move to `B`, `N`, `9`, and `8`, respectively.
- You can customize these in **Settings → Controls**.
- To undo the layout, close Grim Dawn and select **Tray → Restore original key bindings**.

> **WASD-control users:** Grim Dawn uses `alternate_keybindings.txt` in that scheme. GD LoL Controls does **not** edit that alternate file.

## Assisted auto-attack and optional helpers

Press and hold **`A`** to select and attack an eligible enemy in range. You can also choose toggle mode. Target preferences include **nearest enemy**, **weakest enemy**, and **enemy nearest your cursor**. After a target dies, another may be selected. Manual mouse clicks always take priority.

Change the range, target preference, hold/toggle behavior, and related options under **Settings → Auto attack**. The original stock-game meaning of `A` (“move to cursor”) is replaced while this feature is active.

Optional helpers include attack-move, short kite steps between attacks, a range-ring overlay, automatic potion use, and smart cast (skills directed at the auto-attack target). They can be configured or disabled individually. **The experimental caveat for smart cast and energy potions in v0.1.0 still applies.**

## How it works: game data or screen reading

Some features need more information than a basic key remapper can provide. GD LoL Controls supports a **game-data-assisted path** and a **screen-reading/input fallback**. These approaches have different tradeoffs.

### Game-data-assisted mode (`GDLoLHook.dll`)

The application may load a small native helper, `GDLoLHook.dll`, **into the running Grim Dawn process** when a feature requests game data.

- **Reads:** player and nearby character positions, health, and friend/foe information.
- **Acts:** uses game functions to request movements, attacks, hotbar skills, and potions, so compatible features can work without moving your actual cursor.
- **Implementation:** calls the game's named functions and hooks a main-loop call through the import table. It does **not** claim to be a simple external remapper or a no-hook solution.
- **Intended limits:** the project is not designed to modify character stats, items, XP, drops, damage, or save files.
- **Fallback:** if loading or using the helper fails (for example, permissions, antivirus interference, or a game update), supported features can fall back to screen detection and mouse/keyboard input. The log explains what happened; after repeated action failures, the tool can switch to the input method.

Because this helper executes inside another process, it can trigger security warnings or become incompatible after a Grim Dawn update.

### Screen-reading mode (without the game-data helper)

If you prefer screen-based detection and ordinary simulated input, **disable every feature that requests game data**. Simply changing enemy detection to screen mode is **not enough** when other game-data options remain enabled.

1. In **Settings → Auto attack**, set **Enemy detection** to **screen**.
2. In the same area, disable the option for **kite steps / attack-move using the game's own movement command**.
3. Disable **Smart cast** and **potion use through the game's own potion slot**.
4. In **Settings → Movement resume**, disable **Use game data** (the exact destination and game's own move command).
5. Save Settings, then check **Diagnostics** and **View log** to confirm the intended mode.

With no feature requesting game data, the app is designed to unload or avoid requesting the helper. Screen mode may be less precise, depends on what is visible, and can require calibration. **Screen-only is not a guarantee of antivirus or anti-cheat compatibility.**

### Calibration

In screen mode, use **Tray → Calibrate** to teach the app the character's screen position and the ground cursor appearance. Calibration is optional when usable game data is available. Prefer **borderless/windowed** mode when screen capture or the overlay is required.

## Settings and diagnostics

Open **Tray → Settings**, or edit `config.ini` next to the executable. The app reloads the configuration when the file is saved.

Settings tabs: **Controls · Auto attack · Movement resume · Calibration · Diagnostics · Advanced · Live log**.

- **Diagnostics** shows what the application currently sees and decides.
- **View log** opens `logs\gdlol.log`; include relevant lines when reporting a reproducible problem.
- **Restore original key bindings** is available in the tray menu and should be used with Grim Dawn closed.

## Troubleshooting

<details>
<summary><strong>Nothing happens in Grim Dawn</strong></summary>

Check that the tray icon is **green** while Grim Dawn is the foreground window and the application is enabled. If Grim Dawn is running as administrator while the tool is not, start the game normally without elevation, or run the tool with matching permissions if necessary.

</details>

<details>
<summary><strong>Q W E R D F aren't working</strong></summary>

Check that the tool is active. Close the game, then use **Tray → Apply key bindings now** to inspect any failure message. Reopen Grim Dawn after the bindings have been applied.

</details>

<details>
<summary><strong>I use Grim Dawn's WASD control scheme</strong></summary>

WASD mode uses `alternate_keybindings.txt`, which this app does **not** edit. Use the supported keybinding scheme or configure controls appropriately.

</details>

<details>
<summary><strong>The overlay or screen mode isn't working</strong></summary>

Switch Grim Dawn to **borderless or windowed** mode, run **Tray → Calibrate**, and review **Settings → Diagnostics** and the live log.

</details>

<details>
<summary><strong>Antivirus or Windows SmartScreen shows a warning</strong></summary>

The binaries are **unsigned**, and the optional helper loads into another process. Inspect the source, compare release SHA-256 checksums, and only run files you trust. You can build from source or configure screen-only operation. **Do not assume every warning is a false positive.**

</details>

<details>
<summary><strong>Something feels wrong after a game update</strong></summary>

Game-data features can break when internal game functions change. Disable game-data-dependent features or use screen-based operation, and include relevant lines from `logs\gdlol.log` in a [GitHub issue](https://github.com/realSater/gd-lol-controls/issues).

</details>

## Security and privacy

- **Open source:** inspect the [C# app](src/), [C++ helper](hook/), and [tests](tests/) before running anything.
- **Local operation:** the project says it makes no network connections and does not collect or upload telemetry. Settings and logs remain local.
- **Unsigned binaries:** Windows SmartScreen or antivirus products may warn, particularly about the in-process helper. Independent inspection or building from source is an option—not a promise of safety.
- **Release verification:** the [v0.1.0 release](https://github.com/realSater/gd-lol-controls/releases/tag/v0.1.0) includes SHA-256 checksums and a `SHA256SUMS.txt` file. Compare the downloaded archive against the values supplied in the release before running it.

To calculate a ZIP checksum with PowerShell:

```powershell
Get-FileHash .\GDLoLControls-0.1.0.zip -Algorithm SHA256
```

> This checksum example is for v0.1.0; use the actual ZIP name and matching checksums for future releases. **No third-party application's permission or anti-cheat compatibility is guaranteed.**

## Build from source

Clone or download the repository, then run the provided PowerShell build and test scripts on Windows:

```powershell
.\build.ps1
.\tests\run-tests.ps1
```

- **Application:** C# 5, built with the compiler supplied with Windows (no separate .NET SDK is required for the app build as documented).
- **Native helper:** C++ compiled with MSVC (`/MT`); **Visual Studio C++ Build Tools** are required if you also want to build `GDLoLHook.dll`.
- **Outputs:** `dist\GDLoLControls.exe` and, when native build prerequisites are installed, `dist\GDLoLHook.dll`.
- **Source folders:** [`src/`](src/) · [`hook/`](hook/) · [`tests/`](tests/).

## Feedback and contributions

Bug reports, edge cases with different character builds, and suggestions are welcome in [Issues](https://github.com/realSater/gd-lol-controls/issues). If possible, include:

- Your Grim Dawn version, Windows version, and whether you're using Steam or GOG.
- Whether game-data or screen-reading mode was selected.
- The behavior you expected versus what actually happened.
- Relevant lines from `logs\gdlol.log` (remove any personal information before sharing).

## Fair play, safety & disclaimer

- **Independent fan project.** GD LoL Controls is **not affiliated with, endorsed by, or sponsored by Crate Entertainment or Riot Games**. *Grim Dawn* and *League of Legends* are trademarks of their respective owners; the names identify compatibility and inspiration only.
- **Active play, not botting.** The project is intended for convenience and accessibility, not unattended farming, unfair multiplayer advantages, or modification of stats, experience, items, drops, damage, or saved characters. Optional automation may not be acceptable to every player or multiplayer group; obtain agreement and respect applicable rules.
- **Use at your own risk.** Software is provided **“as is,” without warranty**, as described in [LICENSE](LICENSE). Back up your character saves—especially if you play Hardcore—and your settings before experimenting with third-party tools.
- **No affiliation or guaranteed compatibility.** Antivirus, system security, and game updates can affect operation. No guarantee is made that other games or their anti-cheat systems will accept input-remapping or in-process helper tools running in the background.
- **Privacy:** the project states that it does not collect data or send information over the internet.
- **Rights holders:** please [open an issue](https://github.com/realSater/gd-lol-controls/issues) if you have concerns or requests about names, assets, or descriptions.

## License

Released under the **[MIT License](LICENSE)**.
