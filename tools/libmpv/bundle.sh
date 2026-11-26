#!/usr/bin/env bash
# Copies a libmpv built by build.sh into an app folder, with its licence notice and source versions.
#   tools/libmpv/bundle.sh <build.sh out dir> <dest>
# Linux: <export>/lib   Windows: next to Lightcone.exe   macOS: Lightcone.app/Contents/Frameworks
set -euo pipefail
SRC=$1; DEST=$2; HERE=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$DEST"
n=0
for f in libmpv.so.2 libmpv-2.dll libmpv.2.dylib; do
  if [ -f "$SRC/$f" ]; then cp "$SRC/$f" "$DEST/$f"; n=$((n + 1)); fi
done
[ "$n" -gt 0 ] || { echo "no libmpv in $SRC" >&2; exit 1; }
cp "$HERE/NOTICE.txt" "$DEST/libmpv-NOTICE.txt"
cp "$SRC/libmpv-versions.env" "$DEST/libmpv-versions.txt"
ls -la "$DEST" | grep -i mpv
