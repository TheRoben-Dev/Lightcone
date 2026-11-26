#!/usr/bin/env bash
# Installs Lightcone for this user: ~/Applications/Lightcone, a menu entry and
# (when Steam is closed, or with --restart-steam) a Steam shortcut with Lightcone's art.
# Run from the unpacked release folder:  ./install.sh   (re-run to repair; updates happen in-app)
set -euo pipefail
SRC=$(cd "$(dirname "$0")" && pwd)
DEST="${LIGHTCONE_INSTALL_DIR:-$HOME/Applications/Lightcone}"
mkdir -p "$(dirname "$DEST")"
if [ "$SRC" != "$DEST" ]; then
  rm -rf "$DEST.installing"
  cp -a "$SRC" "$DEST.installing"
  rm -rf "$DEST"
  mv "$DEST.installing" "$DEST"
fi
chmod +x "$DEST/Lightcone.x86_64" "$DEST/lightcone"
if ! "$DEST/lightcone" probe >/dev/null 2>&1; then
  echo "Note: libmpv (the playback engine) wasn't found. Install mpv / libmpv from your distribution to play media."
fi

APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p "$APPS"
cat > "$APPS/lightcone.desktop" <<DESK
[Desktop Entry]
Type=Application
Name=Lightcone
Comment=Controller-first media hub
Exec="$DEST/Lightcone.x86_64"
Icon=$DEST/steam-art/icon.png
Terminal=false
Categories=AudioVideo;Video;Player;
Keywords=movies;tv;music;jellyfin;plex;
DESK
command -v update-desktop-database >/dev/null && update-desktop-database "$APPS" || true

echo "Installed to $DEST"
if [ "${1:-}" = "--restart-steam" ]; then
  "$DEST/lightcone" steam-add --restart || true
else
  "$DEST/lightcone" steam-add || echo "Add Lightcone to Steam later from Lightcone → Settings → Updates, or run: $DEST/install.sh --restart-steam"
fi
