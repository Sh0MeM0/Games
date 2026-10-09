#!/usr/bin/env bash
# Local HTTPS development services only; this is not a public deployment.
set -Eeuo pipefail
umask 077
project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$project_dir"
state_dir=/workspace/.archive-onboarding
mkdir -p "$state_dir"
export BUILDX_CONFIG=/workspace/.cache/buildx
export ARCHIVE_BUILD_CA=${SSL_CERT_FILE:-/etc/ssl/certs/ca-certificates.crt}
python3 - "$state_dir" <<'PY'
import secrets, sys
from pathlib import Path
root = Path(sys.argv[1])
env_file = root / 'compose.env'
if not env_file.exists():
    env_file.write_text('DOMAIN=localhost\nACME_EMAIL=admin@example.org\nPOSTGRES_PASSWORD=' + secrets.token_hex(32)
                        + '\nJWT_SECRET=' + secrets.token_hex(48) + '\n')
    env_file.chmod(0o600)
PY
compose=(docker compose --env-file "$state_dir/compose.env" -p archive-onboarding -f docker-compose.yml -f docker-compose.test.yml)
"${compose[@]}" up -d --build --wait
proxy_id=$("${compose[@]}" ps -q proxy)
docker cp "$proxy_id:/data/caddy/pki/authorities/local/root.crt" "$state_dir/ca.crt"
curl -4 --noproxy '*' --fail --silent --show-error --cacert "$state_dir/ca.crt" https://localhost:18443/health
echo
