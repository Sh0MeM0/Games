#!/usr/bin/env bash
# Reproducible dependency setup for the existing isolated cloud checkout.
set -Eeuo pipefail
project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
export UV_CACHE_DIR=/workspace/.cache/uv
export npm_config_cache=/workspace/.cache/npm
export ELECTRON_CACHE=/workspace/.cache/electron
export ELECTRON_BUILDER_CACHE=/workspace/.cache/electron-builder
export BUILDX_CONFIG=/workspace/.cache/buildx
mkdir -p "$UV_CACHE_DIR" "$npm_config_cache" "$ELECTRON_CACHE" "$ELECTRON_BUILDER_CACHE" "$BUILDX_CONFIG"
python3 -c 'import sys; assert sys.version_info[:2] == (3, 12), "Требуется Python 3.12"'
node -e 'if (Number(process.versions.node.split(".")[0]) < 24) throw new Error("Требуется Node.js 24+")'
(cd "$project_dir/server" && uv sync --frozen)
(cd "$project_dir/client" && npm ci && npm run build && npm test)
docker compose version
# Runtime services are started separately by cloud-start.sh / saved start instructions.
