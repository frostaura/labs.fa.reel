#!/bin/sh
# Reel database backups: nightly pg_dump -Fc with 14 daily + 8 weekly kept, plus a dump on
# request before the API applies a pending migration. Canonical copy; docker-compose.prod.yml and
# deploy/portainer-stack.yml inline it byte for byte (with $ escaped as $$), pinned by
# BackupDescriptorParityTests. Runbook: docs/deployment.md#backups-and-restore.
#
#   reel-backup.sh          run forever (the sidecar)
#   reel-backup.sh once     take one daily dump now and exit (drills, manual safety copies)
#
# Reads the standard libpq variables (PGHOST, PGPORT, PGUSER, PGPASSWORD, PGDATABASE).
# Every file this script lists is one it named itself (reel-<date>.dump), so ls is safe here.
# shellcheck disable=SC2012
set -u

BACKUP_DIR="${BACKUP_DIR:-/backups}"
REQUEST_DIR="${BACKUP_REQUEST_DIR:-/backup-requests}"
HOUR_UTC="${BACKUP_HOUR_UTC:-2}"
KEEP_DAILY="${BACKUP_KEEP_DAILY:-14}"
KEEP_WEEKLY="${BACKUP_KEEP_WEEKLY:-8}"
KEEP_PRE_MIGRATION="${BACKUP_KEEP_PRE_MIGRATION:-10}"
WEEKLY_DAY="${BACKUP_WEEKLY_DAY:-7}"
DB="${PGDATABASE:-reel}"

log() { echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) reel-backup: $*"; }

# dump <destination file>: pg_dump to a .partial, prove the archive reads back, then rename.
dump() {
  dest="$1"
  partial="$dest.partial"
  started=$(date -u +%s)
  if ! pg_dump -Fc -Z 6 -d "$DB" -f "$partial"; then
    rm -f "$partial"
    log "FAILED pg_dump for $dest"
    echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) pg_dump failed for $dest" > "$BACKUP_DIR/last-failure"
    return 1
  fi
  if ! pg_restore --list "$partial" > /dev/null; then
    rm -f "$partial"
    log "FAILED archive check for $dest"
    echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) archive check failed for $dest" > "$BACKUP_DIR/last-failure"
    return 1
  fi
  mv "$partial" "$dest"
  bytes=$(wc -c < "$dest" | tr -d ' ')
  log "wrote $dest ($bytes bytes, $(( $(date -u +%s) - started ))s)"
  echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) $dest $bytes" > "$BACKUP_DIR/last-success"
  return 0
}

# prune <directory> <keep>: newest <keep> dumps survive; names sort by date.
prune() {
  ls -1 "$1"/reel-*.dump 2> /dev/null | sort -r | tail -n +"$(( $2 + 1 ))" | while read -r old; do
    rm -f "$old"
    log "pruned $old"
  done
}

daily() {
  today=$(date -u +%Y-%m-%d)
  dest="$BACKUP_DIR/daily/reel-$today.dump"
  dump "$dest" || return 1
  newest_weekly=$(ls -1 "$BACKUP_DIR"/weekly/reel-*.dump 2> /dev/null | sort -r | head -n 1)
  week_ago=$(date -u -d "@$(( $(date -u +%s) - 6 * 86400 ))" +%Y%m%d)
  if [ "$(date -u +%u)" = "$WEEKLY_DAY" ] || [ -z "$newest_weekly" ] \
    || [ "$(basename "$newest_weekly" .dump | cut -c6- | tr -d '-')" -lt "$week_ago" ]; then
    # A hard link costs no space and outlives the daily copy's pruning; cp where links are refused.
    ln -f "$dest" "$BACKUP_DIR/weekly/reel-$today.dump" 2> /dev/null || cp "$dest" "$BACKUP_DIR/weekly/reel-$today.dump"
    log "kept weekly copy reel-$today.dump"
  fi
  prune "$BACKUP_DIR/daily" "$KEEP_DAILY"
  prune "$BACKUP_DIR/weekly" "$KEEP_WEEKLY"
}

# Requests from the API: <id>.request in, <id>.done or <id>.failed out.
serve_requests() {
  for request in "$REQUEST_DIR"/*.request; do
    [ -e "$request" ] || return 0
    id=$(basename "$request" .request)
    dest="$BACKUP_DIR/pre-migration/reel-$id.dump"
    log "pre-migration dump requested: $id ($(tr '\n' ' ' < "$request"))"
    if dump "$dest"; then
      echo "$dest" > "$REQUEST_DIR/$id.done.partial" && mv "$REQUEST_DIR/$id.done.partial" "$REQUEST_DIR/$id.done"
    else
      echo "pg_dump failed; see the backup container log" > "$REQUEST_DIR/$id.failed.partial" && mv "$REQUEST_DIR/$id.failed.partial" "$REQUEST_DIR/$id.failed"
    fi
    rm -f "$request"
    prune "$BACKUP_DIR/pre-migration" "$KEEP_PRE_MIGRATION"
  done
}

wait_for_database() {
  until pg_isready -q -d "$DB"; do
    log "waiting for $DB"
    sleep 5
  done
}

mkdir -p "$BACKUP_DIR/daily" "$BACKUP_DIR/weekly" "$BACKUP_DIR/pre-migration" "$REQUEST_DIR"
# The API may run as a non-root user; it must be able to drop a request here.
chmod 1777 "$REQUEST_DIR"

if [ "${1:-run}" = "once" ]; then
  wait_for_database
  daily
  exit $?
fi

log "started: daily at ${HOUR_UTC}:00 UTC, keeping $KEEP_DAILY daily / $KEEP_WEEKLY weekly / $KEEP_PRE_MIGRATION pre-migration in $BACKUP_DIR"
wait_for_database
while true; do
  serve_requests
  today=$(date -u +%Y-%m-%d)
  if [ ! -e "$BACKUP_DIR/daily/reel-$today.dump" ]; then
    # A host that was asleep at the hour still gets its dump the moment it wakes; a stack with no
    # dump at all takes one immediately rather than waiting for tomorrow.
    if [ "$(date -u +%H | sed 's/^0//')" -ge "$HOUR_UTC" ] || [ -z "$(ls -1 "$BACKUP_DIR"/daily/reel-*.dump 2> /dev/null)" ]; then
      wait_for_database
      daily || log "daily dump failed; retrying in 5 minutes"
    fi
  fi
  # Requests are polled every 5 seconds; the daily check rides the same loop.
  i=0
  while [ "$i" -lt 60 ]; do
    serve_requests
    sleep 5
    i=$(( i + 1 ))
  done
done
