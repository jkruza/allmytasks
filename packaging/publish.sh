#!/usr/bin/env bash
# Publishes the self-contained single-file build for one runtime id. Shared by CI and the release workflow.
# Usage: packaging/publish.sh <rid> <version> <output-dir>
set -euo pipefail
rid="$1"; version="$2"; out="$3"
cd "$(dirname "$0")/.."

# Single-file publish adds an implicit Microsoft.NET.ILLink.Tasks reference whose version follows the runner's SDK,
# so it can't be in packages.lock.json. App packages still resolve to the versions a prior locked restore used.
dotnet publish src/AllMyTasks/AllMyTasks.csproj -c Release -r "$rid" \
  --self-contained true -p:PublishSingleFile=true -p:RestoreLockedMode=false \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:Version="$version" -o "$out"
