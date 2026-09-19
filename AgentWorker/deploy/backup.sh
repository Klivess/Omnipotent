#!/bin/bash
set -euo pipefail
umask 077
: "${RESTIC_REPOSITORY:?Use a separate backup disk}"
: "${RESTIC_PASSWORD_FILE:?Supply a protected restic password file}"
[[ "$(findmnt -n -o SOURCE --target "$RESTIC_REPOSITORY")" != "$(findmnt -n -o SOURCE --target /srv/ka)" ]] || {
  echo 'Backup must be on a separate filesystem/device'; exit 1;
}
exec 9>/run/ka-backup.lock
flock -n 9 || exit 0
stamp=$(date -u +%Y%m%dT%H%M%SZ)
staging="/srv/ka/backup-$stamp"
mkdir -m 0700 "$staging"
incus list --format json | python3 -c 'import json,sys; print("\n".join(x["name"] for x in json.load(sys.stdin) if x.get("config",{}).get("user.ka.project")))' > "$staging/computers.txt"
while IFS= read -r name; do
  [[ "$name" =~ ^ka-[a-f0-9]{24}$ ]] || continue
  incus snapshot create "$name" "backup-$stamp"
  clone="backup-$name-$stamp"
  incus copy "$name/backup-$stamp" "$clone" --config boot.autostart=false
  incus export "$clone" "$staging/$name.tar" --instance-only --compression=none
  incus delete "$clone"
done < "$staging/computers.txt"
mkdir "$staging/projects"
for project in /srv/ka/projects/*; do
  [[ -d "$project" && ! -L "$project" ]] || continue
  btrfs subvolume snapshot -r "$project" "$staging/projects/$(basename "$project")"
done
python3 - "$staging/state.sqlite3" <<'PY'
import sqlite3,sys
with sqlite3.connect('/srv/ka/state/state.sqlite3') as source, sqlite3.connect(sys.argv[1]) as destination:
    source.backup(destination)
PY
restic backup "$staging" /etc/ka --tag ka-computers --host ka-worker
restic check
restic forget --tag ka-computers --host ka-worker --group-by host,tags --keep-daily 7 --keep-weekly 4 --prune
# Successful exports are removed explicitly; failure leaves staging for diagnosis/retry.
for project in "$staging/projects"/*; do
  [[ -d "$project" ]] || continue
  btrfs subvolume delete "$project"
done
rmdir "$staging/projects"
while IFS= read -r name; do
  [[ "$name" =~ ^ka-[a-f0-9]{24}$ ]] || continue
  incus snapshot delete "$name" "backup-$stamp"
done < "$staging/computers.txt"
find "$staging" -maxdepth 1 -type f -delete
rmdir "$staging"
