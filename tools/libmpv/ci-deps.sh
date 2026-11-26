#!/usr/bin/env bash
# Installs what tools/libmpv/build.sh needs on a CI machine, then prints the PATH entry to add.
#   tools/libmpv/ci-deps.sh linux     inside the Steam Runtime sniper SDK container
#   tools/libmpv/ci-deps.sh windows   Ubuntu 24.04 (mingw-w64 cross)
#   tools/libmpv/ci-deps.sh macos     GitHub's macOS runner (Homebrew)
# meson and jinja2 come from pip everywhere, so every OS builds with the same meson.
set -euo pipefail
MESON_VERSION=1.6.1
TOOLS=${RUNNER_TEMP:-/tmp}/libmpv-tools
case "$1" in
  linux)
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -q
    # The SDK has the dev headers of the libraries the Steam Runtime ships (libva, libdrm, EGL,
    # PulseAudio, ALSA, fontconfig); add the build tools it may lack.
    apt-get install -y -q --no-install-recommends nasm cmake ninja-build pkg-config python3-pip git curl xz-utils bzip2 \
      libva-dev libdrm-dev libegl-dev libpulse-dev libasound2-dev libfontconfig-dev zlib1g-dev || true
    ;;
  windows)
    sudo apt-get update -q
    sudo apt-get install -y -q --no-install-recommends mingw-w64 nasm cmake ninja-build pkg-config python3-pip
    ;;
  macos)
    brew install nasm ninja pkg-config cmake >/dev/null
    ;;
  *) echo "usage: $0 linux|windows|macos" >&2; exit 2 ;;
esac
python3 -m pip install -q --target "$TOOLS" "meson==$MESON_VERSION" jinja2 markupsafe 2>/dev/null \
  || python3 -m pip install -q --break-system-packages --target "$TOOLS" "meson==$MESON_VERSION" jinja2 markupsafe
# A meson launcher that finds its modules (and jinja2 for libplacebo) in the --target folder.
mkdir -p "$TOOLS/bin"
printf '#!/bin/sh\nPYTHONPATH="%s${PYTHONPATH:+:$PYTHONPATH}" exec python3 -m mesonbuild.mesonmain "$@"\n' "$TOOLS" > "$TOOLS/bin/meson"
chmod +x "$TOOLS/bin/meson"
echo "PYTHONPATH=$TOOLS" >> "${GITHUB_ENV:-/dev/null}"
echo "$TOOLS/bin" >> "${GITHUB_PATH:-/dev/null}"
PATH="$TOOLS/bin:$PATH" PYTHONPATH="$TOOLS" meson --version
