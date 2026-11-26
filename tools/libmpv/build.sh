#!/usr/bin/env bash
# Builds the libmpv Lightcone ships: one shared library with FFmpeg, libplacebo, libass, FreeType,
# HarfBuzz, FriBidi and Mbed TLS linked in statically, so it needs only the OS's own libraries.
#
#   tools/libmpv/build.sh <out dir> [target]
#
#   linux          (default on Linux) <out>/libmpv.so.2. Run it in the Steam Runtime (sniper) SDK so
#                  the result runs on SteamOS, Bazzite and desktop distros. Needs from the OS at run
#                  time: glibc, libEGL/libGL, libva + libva-drm, libdrm, PulseAudio or ALSA, fontconfig.
#   windows        cross-built with mingw-w64 on Linux: <out>/libmpv-2.dll (system DLLs only).
#   macos-arm64,   on a Mac (x86_64 cross-built on Apple silicon): <out>/libmpv.2.dylib, install
#   macos-x86_64   name @rpath/libmpv.2.dylib, system frameworks only. Combine both with lipo.
#
# Needs: C/C++ compiler (mingw-w64 posix for windows), meson >= 1.3, ninja, cmake, nasm,
# pkg-config, python3 with jinja2, curl, git. Sources are pinned in versions.env.
set -euo pipefail
OUT=$(mkdir -p "$1" && cd "$1" && pwd)
HERE=$(cd "$(dirname "$0")" && pwd)
# shellcheck source=versions.env
. "$HERE/versions.env"
HOST_OS=$(uname -s)
if [ -n "${2:-}" ]; then TARGET=$2
elif [ "$HOST_OS" = Darwin ]; then TARGET=macos-$(uname -m)
else TARGET=linux; fi
case "$TARGET" in
  linux) OS=linux; ARCH=$(uname -m) ;;
  windows) OS=windows; ARCH=x86_64 ;;
  macos-arm64) OS=macos; ARCH=arm64 ;;
  macos-x86_64) OS=macos; ARCH=x86_64 ;;
  *) echo "unknown target $TARGET" >&2; exit 2 ;;
esac
WORK=${LIBMPV_WORK:-$(mktemp -d)}
SRC="$WORK/src"; PREFIX="$WORK/prefix-$TARGET"; BUILD="$WORK/build-$TARGET"
mkdir -p "$SRC" "$PREFIX" "$BUILD"
JOBS=$(getconf _NPROCESSORS_ONLN 2>/dev/null || sysctl -n hw.ncpu)
log() { printf '\n=== %s\n' "$*"; }
# pkg-config --static everywhere: libmpv links our static prefix libraries in full, including what
# they need privately (libplacebo's libstdc++, Mbed TLS's ws2_32/bcrypt...).
printf '#!/bin/sh\nexec pkg-config --static "$@"\n' > "$WORK/pkg-config-static" && chmod +x "$WORK/pkg-config-static"
printf "[binaries]\npkg-config = '%s'\n" "$WORK/pkg-config-static" > "$WORK/native-static.txt"
# Each step leaves a stamp in the build dir, so a re-run (or a CI cache restore) skips finished steps.
step() { if [ -f "$BUILD/.done-$1" ]; then log "$1 (already built)"; return 1; fi; log "$1"; }
fin() { touch "$BUILD/.done-$1"; }
wipe() { [ -f "$1/meson-private/coredata.dat" ] && echo --wipe || true; }  # fresh configure, no stale cache

# ---- toolchain ----
CROSS=()          # meson
FF_CROSS=()       # FFmpeg configure
AT_HOST=()        # autotools
CM_CROSS=()       # cmake
STRIP=strip
case "$OS" in
  linux)
    # The Steam Runtime SDK's default gcc is old; use its newest one.
    if [ -z "${CC:-}" ]; then
      for v in 14 13 12; do command -v gcc-$v >/dev/null && { export CC=gcc-$v CXX=g++-$v; break; }; done
    fi
    export CFLAGS="-O2 -fPIC" CXXFLAGS="-O2 -fPIC" LDFLAGS=""
    export PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig"
    ;;
  windows)
    T=x86_64-w64-mingw32
    # The posix thread model (libplacebo's C++ needs std::thread); Debian/Ubuntu default to win32.
    P=""; command -v $T-gcc-posix >/dev/null && P=-posix
    export CC=$T-gcc$P CXX=$T-g++$P CFLAGS="-O2" CXXFLAGS="-O2" LDFLAGS=""
    # Only our own .pc files: never let the host's Linux libraries leak in.
    export PKG_CONFIG_LIBDIR="$PREFIX/lib/pkgconfig" PKG_CONFIG_PATH=""
    cat > "$WORK/cross-$TARGET.txt" <<EOF
[binaries]
c = '$CC'
cpp = '$CXX'
ar = '$T-ar'
strip = '$T-strip'
windres = '$T-windres'
dlltool = '$T-dlltool'
pkg-config = '$WORK/pkg-config-static'
[properties]
pkg_config_libdir = '$PREFIX/lib/pkgconfig'
[host_machine]
system = 'windows'
cpu_family = 'x86_64'
cpu = 'x86_64'
endian = 'little'
EOF
    CROSS=(--cross-file "$WORK/cross-$TARGET.txt")
    FF_CROSS=(--enable-cross-compile --target-os=mingw32 --arch=x86_64 --cross-prefix=$T- --cc=$CC --cxx=$CXX --pkg-config=pkg-config)
    AT_HOST=(--host=$T)
    CM_CROSS=(-DCMAKE_SYSTEM_NAME=Windows -DCMAKE_C_COMPILER=$CC -DCMAKE_CXX_COMPILER=$CXX -DCMAKE_RC_COMPILER=$T-windres)
    STRIP=$T-strip
    ;;
  macos)
    export MACOSX_DEPLOYMENT_TARGET=${MACOSX_DEPLOYMENT_TARGET:-11.0}
    flags="-arch $ARCH -mmacosx-version-min=$MACOSX_DEPLOYMENT_TARGET"
    export CFLAGS="-O2 $flags" CXXFLAGS="-O2 $flags" OBJCFLAGS="-O2 $flags" LDFLAGS="$flags"
    export PKG_CONFIG_LIBDIR="$PREFIX/lib/pkgconfig" PKG_CONFIG_PATH=""
    CM_CROSS=(-DCMAKE_OSX_ARCHITECTURES=$ARCH -DCMAKE_OSX_DEPLOYMENT_TARGET=$MACOSX_DEPLOYMENT_TARGET)
    if [ "$ARCH" != "$(uname -m)" ]; then
      cpu=$([ "$ARCH" = arm64 ] && echo aarch64 || echo x86_64)
      args="['-arch', '$ARCH', '-mmacosx-version-min=$MACOSX_DEPLOYMENT_TARGET']"
      cat > "$WORK/cross-$TARGET.txt" <<EOF
[binaries]
c = 'clang'
cpp = 'clang++'
objc = 'clang'
ar = 'ar'
strip = 'strip'
pkg-config = '$WORK/pkg-config-static'
[properties]
pkg_config_libdir = '$PREFIX/lib/pkgconfig'
[built-in options]
c_args = $args
cpp_args = $args
objc_args = $args
c_link_args = $args
cpp_link_args = $args
objc_link_args = $args
[host_machine]
system = 'darwin'
cpu_family = '$cpu'
cpu = '$cpu'
endian = 'little'
EOF
      CROSS=(--cross-file "$WORK/cross-$TARGET.txt")
      FF_CROSS=(--enable-cross-compile --target-os=darwin --arch=$ARCH --cc="clang -arch $ARCH" --cxx="clang++ -arch $ARCH")
      AT_HOST=(--host=$cpu-apple-darwin)
    fi
    ;;
esac
MESON_STATIC=(--prefix "$PREFIX" --libdir lib --buildtype release --default-library static -Db_staticpic=true --wrap-mode nofallback)

# ---- sources ----
fetch_git() {  # name "repo tag"
  local name=$1 repo tag; read -r repo tag <<< "$2"
  [ -d "$SRC/$name" ] || git clone -q --depth 1 --branch "$tag" --recursive --shallow-submodules "$repo" "$SRC/$name"
}
fetch_tar() {  # name "url sha256"
  local name=$1 url sum; read -r url sum <<< "$2"
  [ -d "$SRC/$name" ] && return
  curl -fsSL --retry 3 -o "$SRC/$name.tar" "$url"
  echo "$sum  $SRC/$name.tar" | (sha256sum -c - 2>/dev/null || shasum -a 256 -c -) >/dev/null
  mkdir -p "$SRC/$name" && tar -xf "$SRC/$name.tar" -C "$SRC/$name" --strip-components 1
}
log "sources"
fetch_git ffmpeg "$FFMPEG_GIT"
fetch_git mpv "$MPV_GIT"
fetch_git libplacebo "$LIBPLACEBO_GIT"
fetch_git freetype "$FREETYPE_GIT"
fetch_tar fribidi "$FRIBIDI_TAR"
fetch_tar harfbuzz "$HARFBUZZ_TAR"
fetch_tar libass "$LIBASS_TAR"
fetch_tar mbedtls "$MBEDTLS_TAR"
fetch_tar zlib "$ZLIB_TAR"
[ "$OS" = linux ] && fetch_tar libdrm "$LIBDRM_TAR"

meson_static() {  # name [options...]
  local name=$1; shift
  step "$name" || return 0
  meson setup $(wipe "$BUILD/$name") "$BUILD/$name" "$SRC/$name" "${MESON_STATIC[@]}" ${CROSS[@]+"${CROSS[@]}"} "$@"
  ninja -C "$BUILD/$name" -j "$JOBS" install
  fin "$name"
}

# ---- zlib: Linux and macOS have it; mingw doesn't ----
if [ "$OS" = windows ] && step zlib; then
  make -C "$SRC/zlib" -f win32/Makefile.gcc PREFIX=$T- CC="$CC" CFLAGS="$CFLAGS" libz.a
  mkdir -p "$PREFIX/lib/pkgconfig" "$PREFIX/include"
  install -m 644 "$SRC/zlib/libz.a" "$PREFIX/lib/libz.a"
  install -m 644 "$SRC/zlib/zlib.h" "$SRC/zlib/zconf.h" "$PREFIX/include/"
  printf 'prefix=%s\nName: zlib\nDescription: zlib\nVersion: 1.3.1\nLibs: -L${prefix}/lib -lz\nCflags: -I${prefix}/include\n' "$PREFIX" \
    > "$PREFIX/lib/pkgconfig/zlib.pc"
  fin zlib
fi

# ---- libdrm (Linux): mpv needs >= 2.4.105, the Steam Runtime has 2.4.104. Only the core library,
# linked in privately; it is a thin ioctl wrapper, and libva-drm keeps using the OS's copy. ----
if [ "$OS" = linux ]; then
  meson_static libdrm -Dintel=disabled -Dradeon=disabled -Damdgpu=disabled -Dnouveau=disabled -Dvmwgfx=disabled \
    -Domap=disabled -Dexynos=disabled -Dfreedreno=disabled -Dtegra=disabled -Dvc4=disabled -Detnaviv=disabled \
    -Dcairo-tests=disabled -Dman-pages=disabled -Dvalgrind=disabled -Dinstall-test-programs=false -Dudev=false -Dtests=false
fi

# ---- text rendering (subtitles) ----
meson_static freetype -Dzlib=disabled -Dbzip2=disabled -Dpng=disabled -Dharfbuzz=disabled -Dbrotli=disabled
meson_static fribidi -Ddocs=false -Dbin=false -Dtests=false
meson_static harfbuzz -Dfreetype=enabled -Dglib=disabled -Dgobject=disabled -Dcairo=disabled -Dicu=disabled \
  -Dtests=disabled -Ddocs=disabled -Dutilities=disabled -Dintrospection=disabled
if step libass; then
# Fonts: fontconfig on Linux, DirectWrite on Windows, CoreText on macOS.
ASS=(--prefix="$PREFIX" --libdir="$PREFIX/lib" --disable-shared --enable-static --with-pic --disable-require-system-font-provider
     --disable-libunibreak)
[ "$OS" = linux ] && ASS+=(--enable-fontconfig) || ASS+=(--disable-fontconfig)
mkdir -p "$BUILD/libass" && (cd "$BUILD/libass" && "$SRC/libass/configure" "${ASS[@]}" ${AT_HOST[@]+"${AT_HOST[@]}"} && make -j "$JOBS" install)
fin libass; fi

# ---- TLS for https streams (Jellyfin / Plex over https) ----
# (macOS uses SecureTransport instead.) Mbed TLS builds with -Werror by default; a newer compiler
# warning must not break a pinned release.
if [ "$OS" != macos ] && step mbedtls; then
cmake -S "$SRC/mbedtls" -B "$BUILD/mbedtls" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$PREFIX" \
  -DCMAKE_INSTALL_LIBDIR=lib -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DENABLE_PROGRAMS=OFF -DENABLE_TESTING=OFF \
  -DMBEDTLS_FATAL_WARNINGS=OFF -DUSE_SHARED_MBEDTLS_LIBRARY=OFF -DUSE_STATIC_MBEDTLS_LIBRARY=ON ${CM_CROSS[@]+"${CM_CROSS[@]}"}
ninja -C "$BUILD/mbedtls" -j "$JOBS" install
fin mbedtls; fi

# ---- FFmpeg: every decoder, demuxer and parser; no encoders or muxers beyond mpv's screenshots ----
if step ffmpeg; then
FF=(--prefix="$PREFIX" --libdir="$PREFIX/lib" --enable-static --disable-shared --enable-pic
    --disable-programs --disable-doc --disable-debug --enable-version3 --enable-mbedtls
    --disable-encoders --enable-encoder=png,mjpeg --disable-muxers
    --disable-devices --enable-indev=lavfi
    --disable-autodetect --enable-zlib)
case "$OS" in
  linux) FF+=(--enable-vaapi --enable-libdrm --cc="${CC:-cc}" --cxx="${CXX:-c++}") ;;
  windows) FF+=(--enable-d3d11va --enable-dxva2 --enable-w32threads --extra-libs="-lws2_32 -lbcrypt") ;;  # Mbed TLS needs both
  macos) FF+=(--enable-videotoolbox --enable-audiotoolbox --enable-securetransport --disable-mbedtls) ;;
esac
mkdir -p "$BUILD/ffmpeg" && (cd "$BUILD/ffmpeg" && "$SRC/ffmpeg/configure" "${FF[@]}" ${FF_CROSS[@]+"${FF_CROSS[@]}"} \
  --extra-cflags="$CFLAGS -I$PREFIX/include" --extra-ldflags="$LDFLAGS -L$PREFIX/lib" && make -j "$JOBS" install)
fin ffmpeg; fi

# ---- libplacebo (mpv's renderer), OpenGL only ----
meson_static libplacebo -Dvulkan=disabled -Dopengl=enabled -Dglslang=disabled -Dshaderc=disabled -Dlcms=disabled \
  -Ddovi=enabled -Dlibdovi=disabled -Dd3d11=disabled -Ddemos=false -Dtests=false -Dbench=false -Dfuzz=false \
  -Dunwind=disabled -Dxxhash=disabled

# ---- mpv as a shared library ----
log mpv
# Every optional feature off (nothing picked up from the build machine by accident), then only what we use.
MPV=(--auto-features=disabled -Dgpl=false -Dlibmpv=true -Dcplayer=false -Dlua=disabled -Dtests=false -Dfuzzers=false
     -Dlibavdevice=enabled -Dzlib=enabled -Dgl=enabled -Dplain-gl=enabled)
LINK=""
STATIC=(-Dprefer_static=true --native-file "$WORK/native-static.txt")
case "$OS" in
  linux)
    # The OS's own libraries (fontconfig, libva, libdrm, EGL, pulse, alsa) stay shared: never their .a files.
    STATIC=(-Dprefer_static=false --native-file "$WORK/native-static.txt")
    # Render API on Godot's EGL (or GLX via plain-gl) context; VAAPI zero-copy through EGL + DRM.
    MPV+=(-Degl=enabled -Ddrm=enabled -Dvaapi=enabled -Dvaapi-drm=enabled -Dpulse=enabled -Dalsa=enabled -Diconv=enabled)
    # libplacebo has a little C++ and the link runs through the C driver: add libstdc++ statically
    # (not the host's), keep static symbols private, and refuse to link with anything unresolved.
    LINK="-static-libgcc -Wl,-Bstatic -lstdc++ -Wl,-Bdynamic -Wl,--exclude-libs,ALL -Wl,-z,defs"
    ;;
  windows)
    MPV+=(-Dgl-win32=enabled -Dgl-dxinterop=enabled -Dd3d-hwaccel=enabled -Dwasapi=enabled)
    LINK="-static-libgcc -Wl,-Bstatic -lstdc++ -lwinpthread -Wl,-Bdynamic -lws2_32 -lbcrypt"
    ;;
  macos)
    # No cocoa / gl-cocoa: in mpv 0.39 they need the Swift build, and the render API draws into
    # Godot's context without them. VideoToolbox still decodes on the GPU, copied back
    # (videotoolbox-copy, which "auto-copy-safe" picks).
    MPV+=(-Dcoreaudio=enabled -Diconv=enabled)
    # libc++ for libplacebo's C++; the arch flags again, since these replace the cross file's link args.
    LINK="-lc++ $flags"
    # Only .a files in our prefix anyway; prefer_static would hide the system's libiconv dylib.
    STATIC=(-Dprefer_static=false --native-file "$WORK/native-static.txt")
    ;;
esac
meson setup $(wipe "$BUILD/mpv") "$BUILD/mpv" "$SRC/mpv" --prefix "$PREFIX" --libdir lib --buildtype release --default-library shared \
  "${STATIC[@]}" ${CROSS[@]+"${CROSS[@]}"} ${LINK:+"-Dc_link_args=$LINK" "-Dcpp_link_args=$LINK"} "${MPV[@]}"
ninja -C "$BUILD/mpv" -j "$JOBS" install

# ---- result ----
log result
case "$OS" in
  linux)
    cp "$(readlink -f "$PREFIX/lib/libmpv.so.2")" "$OUT/libmpv.so.2"
    $STRIP --strip-unneeded "$OUT/libmpv.so.2"
    ldd "$OUT/libmpv.so.2" || true
    ;;
  windows)
    dll=$(find "$PREFIX" -name 'libmpv-2.dll' | head -1)
    cp "$dll" "$OUT/libmpv-2.dll"
    $STRIP --strip-unneeded "$OUT/libmpv-2.dll"
    x86_64-w64-mingw32-objdump -p "$OUT/libmpv-2.dll" | grep 'DLL Name' || true
    ;;
  macos)
    cp "$PREFIX/lib/libmpv.2.dylib" "$OUT/libmpv.2.dylib"
    install_name_tool -id @rpath/libmpv.2.dylib "$OUT/libmpv.2.dylib"
    strip -x "$OUT/libmpv.2.dylib"
    otool -L "$OUT/libmpv.2.dylib"
    ;;
esac
cp "$HERE/versions.env" "$OUT/libmpv-versions.env"
ls -la "$OUT"
