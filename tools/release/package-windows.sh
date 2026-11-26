#!/usr/bin/env bash
# Packages an exported Windows build into the release zip the in-app updater expects:
#   Lightcone-windows-x86_64.zip (one folder, Lightcone/), and adds it to SHA256SUMS in the out dir.
#   tools/release/package-windows.sh <export dir with Lightcone.exe and lightcone-cli.exe> <version> <out dir>
set -euo pipefail
BUILD=$(realpath "$1"); VERSION="$2"; OUT=$(realpath -m "$3")
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT
APP="$WORK/Lightcone"
mkdir -p "$APP" "$OUT"
cp -a "$BUILD"/. "$APP/"
cp -a "$ROOT/data/steam-art" "$APP/steam-art"
cp "$ROOT/tools/release/windows/"* "$APP/"
cp "$ROOT/LICENSE" "$APP/LICENSE.txt" 2>/dev/null || true
printf 'Lightcone %s (Windows x86_64)\r\n\r\nDouble-click "Install Lightcone.cmd" to install for this user, or start Lightcone.exe from here.\r\nhttps://github.com/TheRoben-Dev/Lightcone\r\n' "$VERSION" > "$APP/README.txt"
test -f "$APP/Lightcone.exe" && test -f "$APP/lightcone-cli.exe"
rm -f "$OUT/Lightcone-windows-x86_64.zip"
(cd "$WORK" && zip -qr -9 "$OUT/Lightcone-windows-x86_64.zip" Lightcone)
(cd "$OUT" && sha256sum Lightcone-windows-x86_64.zip >> SHA256SUMS)
cat "$OUT/SHA256SUMS"
