# Lightcone: video playback tech spike

**Recommendation: libmpv, embedded through mpv's render API, with our own thin C# P/Invoke binding (no GDExtension, no third-party wrapper).** Godot stays on the `gl_compatibility` renderer (same as Luma and Scanline). The same libmpv engine plays music too.

## What was built and tested

A throwaway Godot 4.7.2 .NET project (not kept in the repo; its binding became `src/Lightcone.Playback`) that loads libmpv 0.37 at runtime and draws video into a normal Godot `ImageTexture`, with Godot UI (a label) drawn on top. Run under Xvfb + Mesa llvmpipe, 1080p H.264 MKV with 2 audio tracks, an ASS track and an SRT track.

| Check | SW render path | GL render path (FBO into Godot texture) |
|---|---|---|
| Video in a Godot texture, Godot UI on top | ✅ | ✅ (both `opengl3`/GLX and `opengl3_es`/EGL drivers) |
| ASS styled subs (colour, italics, outline) | ✅ | ✅ |
| SRT subs, switching `sid` at runtime | ✅ | ✅ |
| Audio track switch (`aid` 1 → 2, jpn) | ✅ | ✅ |
| Seek (absolute), position readback | ✅ | ✅ |
| Godot GL state intact after mpv renders | n/a | ✅ (only FBO binding restored) |
| Orientation | correct, no flip | correct, no flip |

Screenshot grid: [images/spike-screens.png](images/spike-screens.png). Timings in the container are meaningless (CPU-emulated GL), so performance is still unmeasured.

Gotchas found:
- mpv writes alpha 0 for opaque video, so the display node needs a one-line canvas shader forcing alpha to 1 (or Godot shows the clear colour through).
- Render must run on Godot's render thread (`RenderingServer.CallOnRenderThread`); mpv's update callback only sets a flag.
- GL proc lookup must match Godot's context: `glXGetProcAddressARB` on X11 + `opengl3`, `eglGetProcAddress` on `opengl3_es` or Wayland, `wglGetProcAddress` + opengl32 fallback on Windows.

## Hardware decoding (needs a Deck to confirm)

The container has no GPU, so VAAPI could only be confirmed to be probed, not used.

- **Zero-copy VAAPI on the Deck** needs mpv's EGL/dmabuf interop, which needs an EGL context. Godot's `opengl3` driver on X11 uses GLX; `opengl3_es` on X11 and anything on Wayland uses EGL. Game Mode (gamescope) runs apps through XWayland, so the plan is to **start with `--rendering-driver opengl3_es` on Linux** and set `hwdec=vaapi`.
- **Fallback:** `hwdec=vaapi-copy` (GPU decode, copy back). Works with any context. Costs a memcpy per frame; fine for 1080p, to be measured for 4K HEVC/AV1.
- **Windows:** `hwdec=auto-safe` (d3d11va-copy in practice with a GL target); D3D11 zero-copy would need WGL_NV_DX_interop and isn't worth it for v1.
- Settings will expose hwdec mode (auto / vaapi / copy / off) and the log will record `hwdec-current`.

**Needed from you:** run the spike build on the Deck (Game Mode and Desktop Mode) with a 4K HEVC 10-bit file and a PGS-subbed Blu-ray rip. I'll ship it as a CI artifact with a `--bench` flag that logs `hwdec-current`, dropped frames and CPU time.

## Alternatives rejected

| Option | Why not |
|---|---|
| Godot `VideoStreamPlayer` | Theora only. |
| FFmpeg GDExtensions (EIRTeam.FFmpeg, GoZen, elly_videoplayer) | No libass/PGS, no track switching, mostly software decode, we'd rebuild a player. |
| mpv as a separate process / `--wid` child window | Godot can't draw its 10-foot OSD over it, `--wid` doesn't exist on Wayland, focus/controller handover under gamescope gets messy. Kept as emergency fallback only. |
| LibVLCSharp with video callbacks | Software copy path only (same cost as mpv SW, no zero-copy route), heavier to bundle on Linux, weaker ASS rendering. |
| Forward+/Vulkan renderer | libmpv render API is OpenGL or SW only; we'd lose the GL interop. UI doesn't need Vulkan. |

## Packaging

- **Linux:** can't rely on a system libmpv on SteamOS. Ubuntu's distro libmpv pulls 227 shared libs (~240 MB), so CI builds a **slim libmpv** (mpv + static FFmpeg with only the decoders we need + libass + libplacebo, linking host libva/libdrm/libGL/libEGL dynamically) inside the Steam Runtime (sniper) container for glibc compatibility. Target ~40 MB in the tarball under `lib/`. Cached by version so it only rebuilds on bumps.
- **Windows:** use a prebuilt `libmpv-2.dll` from the shinchiro/zhongfly mpv-winbuild releases, pinned by version + SHA256 in CI.
- Binding: ~250 lines of `DllImport`s with a `NativeLibrary` resolver that looks in the app's `lib/` first.

## Feature mapping

- Subs: SRT/ASS/PGS/VobSub all handled by mpv (libass + libavcodec), external subs via `sub-add`, styling override in Settings.
- Audio/sub tracks: `track-list` property → our own controller picker.
- Resume: `start=` on load, position saved on pause/stop/every 10 s; synced to Jellyfin (`/Sessions/Playing/Progress`) and Plex (`/:/timeline`).
- Skip intro/credits: Jellyfin MediaSegments API (10.10+) and Plex markers (`includeMarkers=1`) → a "Skip intro" button in our OSD that seeks.
- Music: a second, audio-only mpv instance (`vid=no`, `gapless-audio=yes`, `prefetch-playlist=yes`) owned by an autoload so it keeps playing while browsing.

## Risks

1. VAAPI zero-copy under gamescope + `opengl3_es` is unverified until a Deck run. Fallback (`vaapi-copy`) is known-good in mpv.
2. Slim Linux libmpv build is the most CI work in the project; budget a dedicated PR for it.
3. HDR passthrough isn't possible through the GL texture path (tonemapped to SDR). Acceptable for Deck/most TVs for v1.
