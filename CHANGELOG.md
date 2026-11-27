# Changelog

User-facing changes per release. The section for a version opens its GitHub release notes and
the in-app "What's new" page.

## 0.10.0

- New Lightcone theme, now the default: a projector beam cuts through a dark auditorium, with dust drifting in the light, the beam slowly changing colour like the film it's showing, and velvet-red curtains at the edges. It stops during playback. Your current theme stays as it is; pick Lightcone in Settings → Appearance.
- Projector intro: while a movie or episode loads, the room goes dark, the projector lamp strikes with a flicker and a soft cone of light opens across the screen, with haze and dust drifting through it and the light shimmering like running film, plus mechanical sounds and matching rumble. The beam heads for a cinema screen just out of frame, and when the film is ready the camera turns to find it and pushes in on a smooth curve, with a slight handheld drift and a soft settle, until the film fills the frame. A tip for a better movie night (snacks, drinks, lighting, sound) or a bit of cinema history shows underneath. It never holds playback back. Settings → Sound & feel → Projector intro.
- Did you know?: movie and show pages show a behind-the-scenes fact from the Wikipedia article's production sections, when there is one. Only the title and year are looked up, and each title once. Facts for what you're watching, the newest titles and whatever tile you're on are fetched in the background, so they're there as soon as the page opens. Settings → Sound & feel can turn it off.
- Smoother menus during playback: the controls and menus now run at the screen's full rate instead of the film's 24 fps. On a VRR screen, the picture follows the film's own rate while you just watch, and any button brings full speed back at once.
- Downloads: a progress indicator in the header (in the sidebar on the desktop), a message when a download starts, finishes or fails, and a live percentage on the Download button. When the server doesn't allow downloads, it now says so ("Downloads are turned off on this server") instead of just failing.
- Fits the screen better: grids fill the full width, the last row clears the hint bar, a page's buttons wrap onto a second line instead of running off the screen, and descriptions wrap properly.
- Smooth scrolling: rows and lists glide along with the selector, keeping the next item in view, and the first and last cards in a row are never cut off at the edge.
- Rows fade out softly at the screen edge instead of being cut off, and pages fade at the top and bottom (against the header and hint bar) while there's more to scroll to.
- The selector glides between cards on Home and in the grids, the same way it does in Settings, as an outline around the poster.
- Mark single episodes: Y on an episode in a show's list marks just that episode watched or unwatched, and it syncs to your server. The season button still marks the whole season.
- No more screen dimming, screensaver or sleep while a video plays (SteamOS Game Mode included); paused or stopped, the usual timers apply.
- External controllers: fixed every button counting twice (two steps on the D-pad, two tabs on LB / RB, A pausing and unpausing) when Steam shows one controller to the app as two.
- Did you know? only shows a fact that makes sense on its own, and hides the box otherwise.
- On the desktop, the button hints at the bottom can be clicked.
- Mouse tooltips no longer pop up over cards in the controller layout.
- Settings → Layout shows the resolution Lightcone is drawing at (and, on a Steam Deck given a 16:9 resolution by Steam, how to set it to Native).

## 0.9.0

- More like this: movie and show pages end with similar titles from your library (your server's picks first, then by genre, era and rating).
- Home learns what you like: "Because you watched …" rows after something you finished, and Top picks for you from the genres you watch most. Only titles you haven't started.
- Arrange Home (Settings → Home): show, hide and reorder every kind of Home row. The top card still shows what you're watching.
- Quality: Settings → Playback has a quality for home (your server on this network) and one for away (over the internet or Plex's relay), from Original to 1 Mbps. Lower settings have the server convert the video. The player's options can change it for what's playing, and it reopens at the same spot.
- Offline downloads: Download on a movie or episode page (a whole season or the next 5 episodes on a show page), as the file is or converted smaller. Downloads keep going in the background, resume after a break, and play without a network. What you watch offline syncs back to the server when it's reachable. Settings → Library → Downloads lists them; on a Steam Deck you can save them to the SD card.
- Find subtitles: in the player's options, search OpenSubtitles through your server (Plex's built-in agent, or Jellyfin's OpenSubtitles plugin) in any language, and pick one to show it right away.
- Subtitle look: colour, outline, drop shadow or a dark box, height and bold, in Settings → Playback. Changes show on what's playing.
- Play on another screen: send a movie or episode to a Plex or Jellyfin app on a TV or phone (Play on… on its page, or from the player's options to hand over from where you are). A small remote then controls it from Lightcone.
- Phone remote (Settings → Phone remote, off by default): scan the QR code and your phone's browser becomes a remote with a D-pad, play / pause, skipping and volume. It only works on your own network and asks for the 4-digit PIN shown in Settings.
- Ambient mode: after 5 idle minutes on the menus (2 to 20, or never, in Settings → Sound & feel), the screen shows a slow slideshow of your library with a clock. It moves around so nothing burns in, and any button brings the menus back.
- Button glyphs follow the controller behind Steam Input, so a DualSense shows ✕ ○ □ △ even in Game Mode, and a Steam Deck shows its own L1 / R1 and ☰. Settings → Layout → Button glyphs can pick a style by hand.
- Svenska: Settings → Layout → Interface language. The menus, the player and Settings are translated; rarer texts are still in English.

## 0.8.0

- Cast & crew on movie, show and episode pages (from Plex or Jellyfin). Pick someone to see everything else in your library they're in.
- Trailers & extras: a Trailer button on movie and show pages, plus a row of the trailers, behind-the-scenes clips, deleted scenes and featurettes your server has.
- Night mode evens out the volume, so quiet dialogue comes up and explosions come down. Clear voices lifts dialogue over music and effects, and with surround tracks it brings the centre channel forward. Both are in the player's options and in Settings → Playback, and they stay on until you turn them off.
- Chapters: ticks on the seek bar, the current chapter by the clock, and a chapter list in the player's options. With the controls showing, D-pad up / down (or LB / RB) jump between chapters. On the desktop, use the ⏮ / ⏭ buttons or Page Up / Page Down.
- Seek previews: while you skip with the triggers or hover over the seek bar, a thumbnail shows where you'll land. This needs Plex preview thumbnails or Jellyfin trickplay images; with neither, you still see the time and chapter.
- Smart resume: resuming backs up 5 seconds so you hear the last line again. After a day away it backs up 10 seconds, after three days 20, and after a week 30. Settings → Playback.
- Spoiler-free mode (Settings → Library, off by default): episodes you haven't started show no thumbnail and no summary.
- On the desktop, F switches full screen in the player as its button says (it used to open the options).

## 0.7.0

- Up next: in an episode's end credits (or 20 seconds before the end), a card shows the next episode and counts down. A plays it now, B lets the credits run. The countdown waits while you're paused, and Settings → Playback sets its length or turns it off.
- Still watching?: after 3 episodes in a row with no button pressed, Lightcone asks before going on, so a show doesn't play all night (2, 3, 5 or never, in Settings → Playback).
- Sleep timer: in the player's options (X, or Options on the desktop), pick 15 minutes to 1 h 30, or the end of this episode. The sound fades out over the last 30 seconds, then playback pauses with your spot saved.
- Speed, subtitle timing and audio timing, in the same options. They're remembered per show (every episode) or per movie, like your audio and subtitle picks.
- "Previously": back to a show after a week away, Lightcone shows what happened in the last episode before the next one plays, and can replay its last two minutes first. Show pages show where you left off too. Settings → Playback → Recap after a break.
- Shuffle on show pages: random episodes one after another, new ones first. Good for sitcoms.
- Surprise me (Y on Home, or its button): a random pick from what you haven't watched, leaning toward your watchlist and shows you've started. Choose movies, shows or both, and how much time you have. Y picks again.
- Sort & filter (Y in Movies and Shows; drop-downs on the desktop): show everything, unwatched, in progress or watched.
- Your stats (X on Home, or Settings → Library): hours watched, movies and episodes, shows finished, your day streak, the last two weeks, top shows and genres, and what you watched lately. Plex and Jellyfin watched history counts from day one.

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
