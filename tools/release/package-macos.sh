#!/usr/bin/env bash
# Packages the macOS export into the release zip the in-app updater expects:
#   Lightcone-macos-universal.zip (Lightcone.app at the root), and adds it to SHA256SUMS in the out dir.
# Runs on macOS (ditto, codesign, lipo-free: the CLI ships per architecture behind a small launcher).
#   tools/release/package-macos.sh <Godot's Lightcone.zip> <dir with lightcone-arm64, lightcone-x64> <version> <out dir> \
#     <libmpv dir from tools/libmpv/build.sh, universal libmpv.2.dylib>
set -euo pipefail
EXPORT=$(cd "$(dirname "$1")" && pwd)/$(basename "$1"); CLI=$(cd "$2" && pwd); VERSION="$3"; OUT="$4"; LIBMPV=$(cd "$5" && pwd)
mkdir -p "$OUT"; OUT=$(cd "$OUT" && pwd)
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT
ditto -x -k "$EXPORT" "$WORK"
APP="$WORK/Lightcone.app"
test -x "$APP/Contents/MacOS/Lightcone"
mkdir -p "$APP/Contents/Resources"
cp -R "$ROOT/data/steam-art" "$APP/Contents/Resources/steam-art"
cp "$ROOT/LICENSE" "$APP/Contents/Resources/LICENSE.txt" 2>/dev/null || true
printf 'Lightcone %s (macOS universal)\n\nMove Lightcone.app to Applications.\nhttps://github.com/TheRoben-Dev/Lightcone\n' "$VERSION" > "$APP/Contents/Resources/README.txt"
# Headless CLI, one self-contained binary per architecture behind a launcher.
install -m 755 "$CLI/lightcone-arm64" "$CLI/lightcone-x64" "$APP/Contents/MacOS/"
# Named lightcone-cli: "lightcone" would overwrite the app binary on a case-insensitive file system.
cat > "$APP/Contents/MacOS/lightcone-cli" <<'SH'
#!/bin/sh
here=$(cd "$(dirname "$0")" && pwd)
case "$(uname -m)" in arm64) exec "$here/lightcone-arm64" "$@" ;; *) exec "$here/lightcone-x64" "$@" ;; esac
SH
chmod 755 "$APP/Contents/MacOS/lightcone-cli"
test "$(head -c 2 "$APP/Contents/MacOS/Lightcone")" != "#!"   # the app binary is still the app
# The playback engine, found by the app and the CLI in Contents/Frameworks.
"$ROOT/tools/libmpv/bundle.sh" "$LIBMPV" "$APP/Contents/Frameworks"
mv "$APP/Contents/Frameworks/libmpv-NOTICE.txt" "$APP/Contents/Frameworks/libmpv-versions.txt" "$APP/Contents/Resources/"
lipo -archs "$APP/Contents/Frameworks/libmpv.2.dylib" | grep -q arm64
# Ad-hoc signature (no Apple Developer ID yet): Gatekeeper asks once, see README.
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"
rm -f "$OUT/Lightcone-macos-universal.zip"
ditto -c -k --keepParent "$APP" "$OUT/Lightcone-macos-universal.zip"
(cd "$OUT" && shasum -a 256 Lightcone-macos-universal.zip >> SHA256SUMS)
cat "$OUT/SHA256SUMS"
