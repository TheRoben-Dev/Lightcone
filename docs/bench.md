# Playback bench: Steam Deck and Mac

CI proves the bundled libmpv loads, decodes every promised format and plays through Godot's GL
context. What CI can't see is real hardware: whether VAAPI / VideoToolbox actually decode on the
GPU, and whether a 4K HEVC 10-bit file plays without dropping frames. `--bench` measures that.

## Run it

Plays a file for 30 seconds, prints a report, writes it to `bench-<time>.txt` in the logs folder,
then quits. Without a file it plays the first demo movie (generated, so it only checks the path).

| | Command | Logs folder |
|---|---|---|
| Steam Deck, Desktop Mode | `~/Applications/Lightcone/Lightcone.x86_64 --bench=/path/to/file.mkv` | `~/.local/share/lightcone/logs` |
| Steam Deck, Game Mode | Steam → Lightcone → Properties → Launch options: `--bench=/path/to/file.mkv` | same |
| Mac | `/Applications/Lightcone.app/Contents/MacOS/Lightcone --bench=/path/to/file.mkv` | `~/Library/Application Support/Lightcone/logs` |
| Windows | `"%LOCALAPPDATA%\Programs\Lightcone\Lightcone.exe" --bench=D:\file.mkv` | `%LOCALAPPDATA%\Lightcone\logs` |

Remove the launch option after a Game Mode run.

## Files worth testing

1. 4K HEVC Main 10 (the usual remux), the case that needs hardware decoding.
2. 1080p H.264, the common case.
3. AV1 (the Deck's GPU decodes it in hardware; Apple silicon only from M3).
4. A Blu-ray rip with PGS subtitles (`hdmv_pgs_subtitle`) and TrueHD or DTS-HD audio.

## Reading the report

```
render       OpenGL (EGL)             the GPU texture path; "software" is the slow fallback
video        hevc 3840x2160 @ 23.976 fps
hwdec        vaapi (asked: vaapi,auto-copy-safe)   "no" or "-" means CPU decoding
dropped      vo 0, decoder 0, mistimed 2           a handful is fine; steady growth is not
audio        truehd 8 ch
subtitles    hdmv_pgs_subtitle eng, subrip eng forced
godot fps    avg 60.0, min 58
```

Good on a Deck: `hwdec vaapi`, render `OpenGL (EGL)`, drops near zero, Godot near 60 fps (90 on an
OLED in Game Mode with a 90 Hz limit). Good on a Mac: `hwdec videotoolbox`. If hwdec says `no`,
send the report and `app.log` from the same folder.
