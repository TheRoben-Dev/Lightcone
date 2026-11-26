#!/usr/bin/env bash
# Builds the spike's test clips: 1080p24, 2 audio tracks (eng/swe), ASS + SRT subs, Intro/Episode chapters.
# Usage: tools/spike/make-test-clip.sh OUT_DIR   -> OUT_DIR/test-hevc10.mkv, OUT_DIR/test-h264.mkv
set -euo pipefail
out="${1:?out dir}"; mkdir -p "$out"; tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
cat > "$tmp/subs.ass" <<'ASS'
[Script Info]
ScriptType: v4.00+
PlayResX: 1280
PlayResY: 720
[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,DejaVu Sans,56,&H0000FFFF,&H000000FF,&H00000000,&H80000000,1,0,0,0,100,100,0,0,1,3,1,2,20,20,40,1
[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.00,0:00:20.00,Default,,0,0,0,,{\c&H00FF88&}ASS subtitle via libass{\r} - styled
ASS
printf '1\n00:00:00,000 --> 00:00:20,000\nSRT subtitle track\n' > "$tmp/subs.srt"
printf ';FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=4000\ntitle=Intro\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=4000\nEND=20000\ntitle=Episode\n' > "$tmp/chap.txt"
ffmpeg -hide_banner -loglevel error -y -f lavfi -i testsrc2=s=1920x1080:r=24:d=20 -f lavfi -i sine=f=440:d=20 -f lavfi -i sine=f=880:d=20 \
  -i "$tmp/subs.ass" -i "$tmp/subs.srt" -i "$tmp/chap.txt" -map 0 -map 1 -map 2 -map 3 -map 4 -map_metadata 5 -map_chapters 5 \
  -c:v libx265 -pix_fmt yuv420p10le -preset ultrafast -x265-params log-level=error -c:a aac -c:s:0 ass -c:s:1 srt \
  -metadata:s:a:0 language=eng -metadata:s:a:0 title="English 440Hz" -metadata:s:a:1 language=swe -metadata:s:a:1 title="Svenska 880Hz" \
  -metadata:s:s:0 language=eng -metadata:s:s:1 language=swe "$out/test-hevc10.mkv"
ffmpeg -hide_banner -loglevel error -y -i "$out/test-hevc10.mkv" -map 0 -c copy -c:v libx264 -pix_fmt yuv420p -preset ultrafast "$out/test-h264.mkv"
