#!/bin/sh
# Reel database backups: nightly pg_dump -Fc with 14 daily + 8 weekly kept, plus a dump on
# request before the API applies a pending migration. Canonical copy; docker-compose.prod.yml
# inlines it byte for byte (with $ escaped as $$), pinned by BackupDescriptorParityTests.
# Runbook: docs/deployment.md#backups-and-restore.
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
# A scheduled dump never starts in the first GRACE seconds after this service starts: a backend
# started with it (a fresh stack, a G0 rebuild) asks for its pre-migration dump within seconds, and
# a request queued behind a full first-start dump waited for two dumps instead of one.
GRACE="${BACKUP_FIRST_DUMP_GRACE_SECONDS:-120}"
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

# keep_as_daily <verified dump>: today's daily copy (a hard link when the dump is not already it),
# the weekly copy when one is due, then retention.
keep_as_daily() {
  today=$(date -u +%Y-%m-%d)
  dest="$BACKUP_DIR/daily/reel-$today.dump"
  if [ "$1" != "$dest" ] && [ ! -e "$dest" ]; then
    ln -f "$1" "$dest" 2> /dev/null || cp "$1" "$dest"
    log "kept $1 as today's daily dump"
  fi
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

daily() {
  dest="$BACKUP_DIR/daily/reel-$(date -u +%Y-%m-%d).dump"
  dump "$dest" || return 1
  keep_as_daily "$dest"
}

# answer <request> <suffix> <text>: written atomically, and owned by whoever wrote the request, so
# an API running as a non-root user can delete the answer it reads.
answer() {
  file="$REQUEST_DIR/$(basename "$1" .request)$2"
  echo "$3" > "$file.partial"
  chown "$(stat -c %u:%g "$1" 2> /dev/null || echo 0:0)" "$file.partial" 2> /dev/null || true
  mv "$file.partial" "$file"
}

# Requests from the API: <id>.request in, <id>.done or <id>.failed out.
serve_requests() {
  for request in "$REQUEST_DIR"/*.request; do
    [ -e "$request" ] || continue
    id=$(basename "$request" .request)
    dest="$BACKUP_DIR/pre-migration/reel-$id.dump"
    log "pre-migration dump requested: $id ($(tr '\n' ' ' < "$request"))"
    if dump "$dest"; then
      answer "$request" .done "$dest"
      # A full, verified dump: it is also today's daily if today has none, so a fresh stack does
      # not take a second full dump minutes after this one.
      keep_as_daily "$dest"
    else
      answer "$request" .failed "pg_dump failed; see the backup container log"
    fi
    rm -f "$request"
    prune "$BACKUP_DIR/pre-migration" "$KEEP_PRE_MIGRATION"
  done
  # Answers nobody collected (a requester that timed out and withdrew) are dropped after a day.
  find "$REQUEST_DIR" -maxdepth 1 \( -name '*.done' -o -name '*.failed' -o -name '*.partial' \) -mmin +1440 -delete 2> /dev/null || true
}

wait_for_database() {
  until pg_isready -q -d "$DB"; do
    log "waiting for $DB"
    sleep 5
  done
}

mkdir -p "$BACKUP_DIR/daily" "$BACKUP_DIR/weekly" "$BACKUP_DIR/pre-migration" "$REQUEST_DIR"
# The API may run as a non-root user: it writes requests here and deletes the answers it reads. No
# sticky bit: with one, a root-owned answer could not be deleted by the user it answers.
chmod 0777 "$REQUEST_DIR"

if [ "${1:-run}" = "once" ]; then
  wait_for_database
  daily
  exit $?
fi

log "started: daily at ${HOUR_UTC}:00 UTC, keeping $KEEP_DAILY daily / $KEEP_WEEKLY weekly / $KEEP_PRE_MIGRATION pre-migration in $BACKUP_DIR; requests first for ${GRACE}s"
started_at=$(date -u +%s)
wait_for_database
while true; do
  serve_requests
  today=$(date -u +%Y-%m-%d)
  if [ ! -e "$BACKUP_DIR/daily/reel-$today.dump" ] && [ $(( $(date -u +%s) - started_at )) -ge "$GRACE" ]; then
    # A host that was asleep at the hour still gets its dump the moment it wakes; a stack with no
    # dump at all takes one immediately rather than waiting for tomorrow.
    if [ "$(date -u +%H | sed 's/^0//')" -ge "$HOUR_UTC" ] || [ -z "$(ls -1 "$BACKUP_DIR"/daily/reel-*.dump 2> /dev/null)" ]; then
      wait_for_database
      daily || log "daily dump failed; retrying in 5 minutes"
    fi
  fi
  # Requests are polled every 5 seconds; the daily check runs once a minute.
  i=0
  while [ "$i" -lt 12 ]; do
    serve_requests
    sleep 5
    i=$(( i + 1 ))
  done
done
