#!/usr/bin/env bash
# Installs the packages for linux-x64 in clean containers and runs the version flag; also runs the AppImage.
# Usage: packaging/linux/smoke.sh <version> <dist-dir>      (needs docker and the x64 packages from build.sh)
set -euo pipefail
version="$1"; dist="$(realpath "$2")"

expect() { grep -q "^AllMyTasks ${version}\$" || { echo "unexpected version output" >&2; exit 1; }; }

# The AppImage, extracted in place because FUSE 2 is usually missing.
chmod +x "$dist/AllMyTasks-$version-linux-x64.AppImage"
"$dist/AllMyTasks-$version-linux-x64.AppImage" --appimage-extract-and-run --version | tee /dev/stderr | expect

for image in debian:stable ubuntu:24.04; do
  echo "== deb on $image"
  docker run --rm -v "$dist:/dist:ro" "$image" bash -ec "
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq
    apt-get install -y -qq desktop-file-utils >/dev/null
    apt-get install -y -qq /dist/AllMyTasks-$version-linux-x64.deb >/dev/null
    allmytasks --version
    desktop-file-validate /usr/share/applications/com.jkruza.allmytasks.desktop
    test -f /usr/share/icons/hicolor/256x256/apps/com.jkruza.allmytasks.png
    mkdir -p ~/.local/share/AllMyTasks && touch ~/.local/share/AllMyTasks/keep
    apt-get remove -y -qq allmytasks >/dev/null
    test ! -e /usr/bin/allmytasks && test ! -e /opt/allmytasks/AllMyTasks
    test -f ~/.local/share/AllMyTasks/keep
  " | tee /dev/stderr | expect
done

echo "== rpm on fedora"
docker run --rm -v "$dist:/dist:ro" fedora:latest bash -ec "
  dnf install -y -q desktop-file-utils /dist/AllMyTasks-$version-linux-x64.rpm >/dev/null
  allmytasks --version
  desktop-file-validate /usr/share/applications/com.jkruza.allmytasks.desktop
  dnf remove -y -q allmytasks >/dev/null
  test ! -e /usr/bin/allmytasks
" | tee /dev/stderr | expect
