#!/usr/bin/env bash
set -Eeuo pipefail
project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
export npm_config_cache=/workspace/.cache/npm
export ELECTRON_BUILDER_CACHE=/workspace/.cache/electron-builder
mkdir -p "$npm_config_cache" "$ELECTRON_BUILDER_CACHE"
cd "$project_dir/client"
# The custom NSIS script creates its uninstaller on Windows, so Wine isn't needed.
# Skip editing PE metadata on Linux; the installer, shortcuts and UI use the proper product name.
npm run dist:win -- --config.win.signAndEditExecutable=false --config.electronDownload.cache=/workspace/.cache/electron
