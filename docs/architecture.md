# Architecture

```
src/
  Lightcone.Core      platform paths, Steam shortcuts/VDF. No Godot, no I/O beyond the file system.
  Lightcone.Playback  libmpv binding (P/Invoke client + render API), track parsing, hwdec and resume policies. No Godot.
  Lightcone.Host      everything the app does that isn't UI: settings, media library + sources, watch progress,
                      self-updater, Steam setup, logging. Shared by the app and the CLI.
  Lightcone.Cli       `lightcone` (Linux) / `lightcone-cli.exe` / `Lightcone.app/Contents/MacOS/lightcone-cli`: version, settings, probe (libmpv), steam-add.
  Lightcone.App       Godot 4.7.2 .NET project. All UI is built in code (Scripts/), gl_compatibility renderer.
tests/Lightcone.Core.Tests   xUnit tests for Core, Host and Playback.
tools/smoke                  UI smoke test (Xvfb, scripted pad/keyboard/mouse/touch, screenshot per step).
tools/release                packaging (Linux tarball, Windows zip, macOS .app zip) and installers.
tools/art                    Steam library art generator (data/steam-art).
```

## App

- `Main` holds the screen stack and the chrome. Controller mode has a header with section tabs, a hint bar and no cursor. Desktop mode has `Desktop/Sidebar`, `Desktop/MiniPlayer` and mouse input. Screens are shared between the modes and only adjust density (`Screen.Desktop`), so every feature exists in both. **Full feature parity between the modes is a project rule.**
- `Playback/PlayerService` is the one player:
  - It owns the `MpvPlayer` and the render context (GL FBO on Godot's render thread, or software).
  - It saves progress and raises `Changed` / `Finished`.
  - `VideoView` shows its texture anywhere: the player screen and the mini player.
- `Host.Library.MediaLibrary` merges `IMediaSource`s into sections, home rows and search. Today the only source is `DemoSource`, the sample library.
- Sources added in later phases, each behind `IMediaSource`:
  - Phase 3: local folders.
  - Phase 4: Jellyfin and Plex, which also provide markers for skip intro/credits.
- `Ui/Pad` turns the controller, keyboard and Steam Input into a set of actions: accept, back, tab, search, menu, seek. It also handles hold-to-repeat and glyphs.

## Paths

| | Linux | Windows | macOS |
|---|---|---|---|
| Settings, sources | `~/.config/lightcone` | `%APPDATA%\Lightcone` | `~/Library/Application Support/Lightcone` |
| Progress, library index, logs | `~/.local/share/lightcone` | `%LOCALAPPDATA%\Lightcone` | `~/Library/Application Support/Lightcone` |
| Artwork cache | `~/.cache/lightcone` | `%LOCALAPPDATA%\Lightcone\cache` | `~/Library/Caches/Lightcone` |

## libmpv lookup

Lookup order:

1. `LIGHTCONE_LIBMPV`.
2. Next to the app: `Contents/Frameworks` on macOS, then `lib/`, then the app folder.
3. The system loader (`libmpv.so.2`, `libmpv-2.dll`, `libmpv.2.dylib`).
4. On macOS only, Homebrew (`/opt/homebrew/lib`, `/usr/local/lib`).

`lightcone probe` reports which libmpv was found.
