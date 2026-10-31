#!/usr/bin/env bash
# Builds the AppImage, .deb and .rpm for one Linux runtime id from an existing publish directory.
# Usage: packaging/linux/build.sh <rid> <version> <publish-dir> <dist-dir>
#   rid: linux-x64 | linux-arm64     version: e.g. 1.2.0 or 1.2.0-rc.1 (no leading "v")
# Needs curl, tar, sha256sum. Downloads pinned nfpm and appimagetool (checked against the hashes below) into $TOOLS_DIR.
set -euo pipefail

rid="$1"; version="$2"; publish_dir="$(realpath "$3")"; dist="$4"
root="$(realpath "$(dirname "$0")/../..")"
tools="${TOOLS_DIR:-$root/out/tools}"
mkdir -p "$dist" "$tools"
dist="$(realpath "$dist")"

NFPM_VERSION=2.9.2
NFPM_SHA256=f6c19da083dbfd07332374e904780ff6a829c0d2503a74f93a1cf465a33ad57f
APPIMAGETOOL_VERSION=1.9.1
APPIMAGETOOL_SHA256=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
# The AppImage runtime for arm64. appimagetool runs on the x64 runner and is told to embed this one.
RUNTIME_AARCH64_URL=https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-aarch64
RUNTIME_AARCH64_SHA256=9a72a744d221576c9347ec074243acc5c3c7568896b12714e0e82e9f0bf47900

case "$rid" in
  linux-x64)   deb_arch=amd64; appimage_arch=x86_64 ;;
  linux-arm64) deb_arch=arm64; appimage_arch=aarch64 ;;
  *) echo "unsupported rid: $rid" >&2; exit 2 ;;
esac

fetch() { # url sha256 file
  if [[ ! -f "$3" ]] || ! echo "$2  $3" | sha256sum -c --quiet - 2>/dev/null; then
    curl -fsSL -o "$3" "$1"
    echo "$2  $3" | sha256sum -c --quiet -
  fi
}

[[ -x "$publish_dir/AllMyTasks" ]] || { echo "no AllMyTasks binary in $publish_dir" >&2; exit 1; }

# --- deb and rpm ---
fetch "https://github.com/goreleaser/nfpm/releases/download/v$NFPM_VERSION/nfpm_${NFPM_VERSION}_Linux_x86_64.tar.gz" \
  "$NFPM_SHA256" "$tools/nfpm.tar.gz"
tar -xzf "$tools/nfpm.tar.gz" -C "$tools" nfpm
mkdir -p "$root/out/pkg-stage"
install -m 0755 "$publish_dir/AllMyTasks" "$root/out/pkg-stage/AllMyTasks"
for fmt in deb rpm; do
  (cd "$root" && VERSION="$version" ARCH="$deb_arch" \
    "$tools/nfpm" package --config packaging/linux/nfpm.yaml --packager "$fmt" \
    --target "$dist/AllMyTasks-$version-$rid.$fmt")
done

# --- AppImage ---
fetch "https://github.com/AppImage/appimagetool/releases/download/$APPIMAGETOOL_VERSION/appimagetool-x86_64.AppImage" \
  "$APPIMAGETOOL_SHA256" "$tools/appimagetool"
chmod +x "$tools/appimagetool"

appdir="$(mktemp -d)/AllMyTasks.AppDir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications"
install -m 0755 "$publish_dir/AllMyTasks" "$appdir/usr/bin/AllMyTasks"
install -m 0644 "$root/packaging/linux/com.jkruza.allmytasks.desktop" "$appdir/com.jkruza.allmytasks.desktop"
sed -i 's|^Exec=.*|Exec=AllMyTasks|' "$appdir/com.jkruza.allmytasks.desktop"
cp "$appdir/com.jkruza.allmytasks.desktop" "$appdir/usr/share/applications/"
install -m 0644 "$root/packaging/linux/icons/256.png" "$appdir/com.jkruza.allmytasks.png"
for size in 16 32 48 64 128 256; do
  mkdir -p "$appdir/usr/share/icons/hicolor/${size}x${size}/apps"
  install -m 0644 "$root/packaging/linux/icons/$size.png" "$appdir/usr/share/icons/hicolor/${size}x${size}/apps/com.jkruza.allmytasks.png"
done
cat > "$appdir/AppRun" <<'APPRUN'
#!/bin/sh
here="$(dirname "$(readlink -f "$0")")"
exec "$here/usr/bin/AllMyTasks" "$@"
APPRUN
chmod +x "$appdir/AppRun"

runtime_args=()
if [[ "$appimage_arch" == aarch64 ]]; then
  fetch "$RUNTIME_AARCH64_URL" "$RUNTIME_AARCH64_SHA256" "$tools/runtime-aarch64"
  runtime_args=(--runtime-file "$tools/runtime-aarch64")
fi
# appimagetool is itself an AppImage and FUSE is often missing on CI runners and containers.
ARCH="$appimage_arch" "$tools/appimagetool" --appimage-extract-and-run "${runtime_args[@]}" \
  "$appdir" "$dist/AllMyTasks-$version-$rid.AppImage"

ls -l "$dist"
