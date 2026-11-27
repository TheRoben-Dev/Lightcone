# Changelog

User-facing changes per release. The section for a version opens its GitHub release notes and
the in-app "What's new" page.

## 0.6.0

- Show pages list seasons properly: a strip of season posters (from Plex or Jellyfin) with each season's episode count, a ✓ or how many are left, and a watched bar. Only the picked season's episodes show below, with thumbnails and a line of the plot, instead of one long list.
- The page opens on the season you're in. Moving along the strip switches season; LT / RT (J / L on a keyboard) switch from anywhere on the page, and a click picks one in Desktop mode.
- Y (or Mark season watched) marks a whole season watched or unwatched, and it syncs to the server.
- The play button names the episode it starts ("▶ Resume S2 · E4"), and specials wait until the regular seasons are done instead of playing first.

## 0.5.0

- Jellyfin: Sources → Jellyfin finds servers on your network (or type the address with the built-in keyboard), then sign in with Quick Connect: approve a 6-digit code from any device already signed in, no typing. User name and password work too.
- Jellyfin movies, shows and music show up next to everything else with artwork; resume positions and watched state sync both ways, box sets become collections, and intro / credits segments (Jellyfin 10.10+) give Skip intro and Skip credits.
- Several Jellyfin servers can be added; each can be refreshed, signed in again or removed from Sources.
- Up next: Continue watching also lists the next episode of shows you're watching (Home's top card says Up next).
- During an episode's end credits, the skip button becomes Next episode (Y).
- Tiles show a ✓ for watched movies and episodes, how many episodes of a show are left, and a ribbon for the watchlist.
- Shows count as recently added when a new episode arrives (Home and the date-added sort).
- Settings → Playback → Skip intros automatically: marked intros are skipped without a press.
- Audio and subtitle choices are remembered per show: pick Japanese audio with English subtitles once, and every episode starts that way.

## 0.4.0

- Watchlist: X on a movie or show (or its Watchlist button) keeps it for later, and Home shows a Watchlist row. Signed in to Plex, it's your Plex watchlist, so it matches every Plex app.
- Collections: Plex collections show up in Lightcone, you can make your own (Collections… on any movie or show), and Lightcone groups titles by genre and decade. X in Movies or Shows switches to collections; Home has a Collections row.
- Music is optional: Settings → Library → Music off removes the Music tab and keeps music out of Home and search. Movies and TV come first.
- Detail pages show the age rating and score when the server has them.

## 0.3.0

- Plex: sign in with a code at plex.tv/link (no typing with a controller), pick your servers, and their movies, shows and music show up next to everything else, with artwork. Lightcone finds the fastest way to each server (home network, internet, or Plex's relay).
- Continue watching and watched state come from Plex too, and Lightcone reports progress back, so you can stop on the Deck and resume on the TV. Plex's intro and credits markers give Skip intro / Skip credits.
- Plex libraries stay visible offline (what was loaded last), marked offline in Sources.
- Lightcone's own on-screen keyboard (from Luma) for search and every text field, on every OS: X deletes, Y adds a space, Start is Done. Steam's keyboard is no longer used.
- Skip back 10 s and forward 30 s (both adjustable), holding speeds up, and the seek bar glides to the new spot.
- Test builds update themselves (Settings → Updates → Update channel).
- Home and detail pages polished for 1280x800: even card heights, rounded artwork, bumpers switch sections from any page, D-pad back to the top scrolls all the way up.

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
