#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
if [[ $# != 2 || $2 != --confirm-overwrite ]]; then
    echo 'Использование: scripts/restore.sh /полный/путь/к/бэкапу --confirm-overwrite' >&2
    echo 'ВНИМАНИЕ: текущие данные будут заменены. Сначала создайте резервную копию.' >&2
    exit 2
fi
snapshot_dir=$(realpath -- "$1")
project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$snapshot_dir"
sha256sum -c SHA256SUMS
cd "$project_dir"
docker compose stop proxy server
# Leave services stopped on failure; don't expose a partially restored archive.
docker compose exec -T db sh -c 'exec pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists --no-owner --no-acl --exit-on-error' <"$snapshot_dir/database.dump"
# Start an isolated helper, mounting the existing volume with the same unprivileged UID.
docker compose run --rm --no-deps -T server sh -c 'find /data/files -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +; tar -xzf - -C /data' <"$snapshot_dir/files.tar.gz"
docker compose up -d server proxy
echo 'Восстановлено. Проверьте health, вход и открытие документа через HTTPS.'
