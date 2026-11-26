#!/usr/bin/env bash
# Runs the playback spike under Xvfb and checks every screenshot shows the test pattern's red bar.
# Usage: tools/spike/run-spike.sh GODOT_BIN CLIP OUT_DIR [extra godot args...]
# Env: SPIKE_OPTS (mpv options k=v,k=v), SPIKE_SHARED=0 (render in Godot's own context instead), SPIKE_HWDEC.
set -euo pipefail
godot="$1"; clip="$2"; out="$3"; shift 3
here="$(cd "$(dirname "$0")/../.." && pwd)"
mkdir -p "$out"
SPIKE_VIDEO="$clip" SPIKE_OUT="$out" timeout 180 xvfb-run -a -s "-screen 0 1280x800x24" \
  "$godot" --path "$here/spike/video-playback" --rendering-driver opengl3_es "$@" 2>&1 | grep -v -E '^ALSA|DRI3' | tee "$out/log.txt"
python3 - "$out" <<'PY'
import sys, glob
from PIL import Image
bad = 0
shots = sorted(glob.glob(sys.argv[1] + "/0*.png"))
for f in shots:
    r, g, b = Image.open(f).convert("RGB").getpixel((100, 300))
    ok = r > 200 and g < 80
    bad += not ok
    print(("OK  " if ok else "BAD ") + f.split("/")[-1], (r, g, b))
sys.exit(1 if bad or len(shots) != 5 else 0)
PY
