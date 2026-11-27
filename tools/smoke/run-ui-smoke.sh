#!/usr/bin/env bash
# UI smoke test: a fake $HOME, then drives the Godot app with scripted controller, keyboard, mouse
# and touch input (--smoke) under Xvfb and saves a screenshot per step. The sample library (--demo)
# plays generated test patterns and tones through libmpv, so playback is exercised end to end.
#
#   tools/smoke/run-ui-smoke.sh <godot-mono-editor | exported Lightcone.x86_64> <out-dir>
#
# SMOKE_DRIVER=opengl3 to test the GLX path instead of EGL (opengl3_es, the default on Linux).
# SMOKE_LIBMPV=lib/libmpv.so.2 to require that libmpv was loaded from a path ending that way.
# SMOKE_THEME=<id> to start in another theme.
set -euo pipefail
GODOT=$(realpath "$1")
OUT=$(realpath -m "$2")
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

# Every XDG dir inside the fake home: CI runners set XDG_CONFIG_HOME.
export HOME="$WORK/home" XDG_RUNTIME_DIR="$WORK/run"
export XDG_CONFIG_HOME="$HOME/.config" XDG_DATA_HOME="$HOME/.local/share" XDG_CACHE_HOME="$HOME/.cache"
mkdir -p "$HOME" "$XDG_RUNTIME_DIR" "$OUT"
chmod 700 "$XDG_RUNTIME_DIR"

if [ -n "${SMOKE_THEME:-}" ]; then
  mkdir -p "$XDG_CONFIG_HOME/lightcone"
  printf '{"Theme":"%s"}\n' "$SMOKE_THEME" > "$XDG_CONFIG_HOME/lightcone/settings.json"
fi

# A short video with chapters, for the chapter steps (skipped without ffmpeg).
if command -v ffmpeg >/dev/null 2>&1; then
  printf ';FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=20000\ntitle=Opening\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=20000\nEND=40000\ntitle=The heist\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=40000\nEND=60000\ntitle=Credits\n' > "$WORK/chapters.txt"
  if ffmpeg -v error -f lavfi -i testsrc2=size=640x360:rate=30:duration=60 -f lavfi -i sine=frequency=330:duration=60 -i "$WORK/chapters.txt" \
      -map 0 -map 1 -map_metadata 2 -map_chapters 2 -c:v mpeg4 -q:v 8 -c:a aac -shortest "$WORK/chapters.mkv"; then
    export SMOKE_CHAPTERS="$WORK/chapters.mkv"
  fi
fi

cd "$ROOT/src/Lightcone.App"
PROJECT=(--path .)
[ "$(basename "$GODOT")" = "Lightcone.x86_64" ] && PROJECT=()  # exported build: project is embedded
set +e
timeout 300 xvfb-run -a -s "-screen 0 1280x800x24" "$GODOT" "${PROJECT[@]}" --rendering-driver "${SMOKE_DRIVER:-opengl3_es}" \
  -- --windowed --demo --smoke "$OUT" 2>&1 | tee "$OUT/godot.log"
code=${PIPESTATUS[0]}
set -e
cp "$XDG_DATA_HOME"/lightcone/logs/app.log "$OUT/app.log" 2>/dev/null || true
if grep -qE "Unhandled exception|System\.[A-Za-z.]*Exception|smoke: step .* failed" "$OUT/godot.log"; then
  echo "::error::exceptions or failed steps during the UI smoke test (see godot.log)"
  exit 1
fi
if ! grep -q "loaded: " "$OUT/app.log" 2>/dev/null; then
  echo "::error::nothing played during the UI smoke test (see app.log)"
  exit 1
fi
# SMOKE_LIBMPV=<path suffix>: fail unless playback used that libmpv (the bundled one, say).
if [ -n "${SMOKE_LIBMPV:-}" ] && ! grep -q "from [^ ]*${SMOKE_LIBMPV}, render" "$OUT/app.log"; then
  echo "::error::playback did not use ${SMOKE_LIBMPV}: $(grep -o 'mpv .* from [^,]*' "$OUT/app.log" | head -1)"
  exit 1
fi
shots=$(ls "$OUT"/*.png 2>/dev/null | wc -l)
echo "exit=$code screenshots=$shots"
[ "$code" -eq 0 ] && [ "$shots" -ge 20 ]
