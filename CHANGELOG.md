# Changelog

User-facing changes per release. The section for a version opens its GitHub release notes and
the in-app "What's new" page.

## 0.2.0

- Playback needs nothing installed anymore. Every release carries its own playback engine (libmpv with FFmpeg), built for Steam Deck, Linux, Windows and macOS.
- The engine checks itself: `lightcone probe` (and Settings → Playback) lists the video and audio formats it decodes, H.264, HEVC, AV1, VP9, TrueHD, DTS and more.
- `--bench` lists the file's subtitle tracks, so you can confirm Blu-ray (PGS) subtitles show up on your device.

## 0.1.0

- First preview: Lightcone's shell on Steam Deck, Linux, Windows and macOS. A 10-foot Controller mode (A play, B back, bumpers switch sections, triggers seek) and a separate Desktop mode with a sidebar, grid/list views and a mini player.
- Settings for theme, UI mode, sounds, hardware decoding, preferred audio and subtitle languages.
- Video playback engine (libmpv) with hardware decoding, ASS/SRT/PGS subtitles and audio track switching. A test player opens files from Settings → Playback → Play a file.
- A built-in sample library (movies, shows and albums made of test patterns and tones) to try everything before adding your own media. Turn it off in Sources.
- `--bench` measures playback on your hardware (decoder, dropped frames, fps).
- Add Lightcone to Steam with its own library art, and updates from inside the app with one-step rollback.
