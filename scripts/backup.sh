#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$project_dir"
backup_dir=${BACKUP_DIR:-"$project_dir/backups"}
mkdir -p "$backup_dir"
exec 9>"$backup_dir/.backup.lock"
flock -n 9 || { echo 'Другой бэкап уже выполняется' >&2; exit 1; }
stamp=$(date -u +%Y%m%dT%H%M%SZ)
pending="$backup_dir/.pending-$stamp"
mkdir "$pending"
trap 'rm -rf -- "$pending"' EXIT
# Immutable blobs are never physically deleted by the application.
# Dump the DB first, then all blobs: every referenced blob already exists and is fsynced.
docker compose exec -T db sh -c 'exec pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc --no-owner --no-acl' >"$pending/database.dump"
docker compose exec -T server tar --exclude=files/staging -C /data -czf - files >"$pending/files.tar.gz"
test -s "$pending/database.dump"
test -s "$pending/files.tar.gz"
tar -tzf "$pending/files.tar.gz" >/dev/null
docker compose exec -T db pg_restore --list <"$pending/database.dump" >/dev/null
(cd "$pending" && sha256sum database.dump files.tar.gz >SHA256SUMS)
mv "$pending" "$backup_dir/$stamp"
trap - EXIT
# Retain successful snapshots for 30 days. Copy them to a separate protected storage.
find "$backup_dir" -mindepth 1 -maxdepth 1 -type d -name '20*T*Z' -mtime +30 -exec rm -rf -- {} +
echo "Бэкап завершён: $backup_dir/$stamp"
