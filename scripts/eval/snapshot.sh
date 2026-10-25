#!/usr/bin/env bash
# snapshot.sh — make an eval database from a snapshot of the founder account, and sanitise it.
#
#   scripts/eval/snapshot.sh restore <dump-file> <target-db> [snapshot-id]
#       pg_restore a custom-format dump (taken BY THE FOUNDER/INTEGRATOR from live; this script never
#       reads :5433) into a fresh <target-db> on the eval Postgres, then sanitise it.
#   scripts/eval/snapshot.sh clone <source-db> <target-db>
#       CREATE DATABASE <target-db> TEMPLATE <source-db> on the eval Postgres (e.g. reel_eval_template →
#       reel_eval or a lane slot), then re-run the sanitiser (idempotent) and carry the snapshot id over.
#   scripts/eval/snapshot.sh sanitise <db>
#       Re-apply the sanitiser to an existing eval database.
#   scripts/eval/snapshot.sh id <db>
#       Print the snapshot id stamped on <db>.
#
# Sanitising (program §4.12, WS-7 task 1):
#   - every TraktConnections token is replaced by a placeholder that cannot decrypt, so no refresh can
#     ever rotate the founder's single-use refresh token (the columns are NOT NULL, so "null" is a
#     constant placeholder rather than SQL NULL);
#   - the Trakt outbox is truncated, so no write can reach his real profile;
#   - pending and running jobs are deleted, so a slot started with the job runner on cannot replay
#     the live queue against real providers;
#   - push subscriptions are deleted, so no eval build can notify his devices;
#   - the live secrets a copy would otherwise carry are deleted: AppSecrets (the VAPID private key — the
#     API generates a fresh pair on demand), DataProtectionKeys (the live key ring) and RefreshSessions
#     (live refresh-token hashes), so nothing on an eval copy can act as, or decrypt for, the live stack.
# Before any DROP of an existing eval database, its ExternalApiUsages rows are archived
# (scripts/eval/ledger.mjs archive), because re-cloning a lane database used to destroy the only record
# of what it spent (§7 rule 7).
# Background services are switched off by the RUNNER (run-api.sh / docker-compose.eval.yml), not here.
#
# The snapshot id is stamped with COMMENT ON DATABASE, so every eval report can name the data it ran on.
set -euo pipefail
. "$(dirname "$0")/lib.sh"

sanitise() {
  local db="$1"
  eval_psql "$db" -q <<'SQL'
BEGIN;
UPDATE "TraktConnections"
   SET "AccessTokenEncrypted" = 'eval-sanitised', "RefreshTokenEncrypted" = 'eval-sanitised'
 WHERE "AccessTokenEncrypted" <> 'eval-sanitised' OR "RefreshTokenEncrypted" <> 'eval-sanitised';
TRUNCATE "TraktOutbox";
DELETE FROM "SyncJobs" WHERE "Status" IN ('Pending', 'Running');
DELETE FROM "PushSubscriptions";
DELETE FROM "AppSecrets";
DELETE FROM "DataProtectionKeys";
DELETE FROM "RefreshSessions";
COMMIT;
SQL
  verify "$db"
}

verify() {
  local db="$1" bad
  bad=$(eval_psql "$db" -Atc "
    SELECT (SELECT count(*) FROM \"TraktConnections\" WHERE \"AccessTokenEncrypted\" <> 'eval-sanitised' OR \"RefreshTokenEncrypted\" <> 'eval-sanitised')
         + (SELECT count(*) FROM \"TraktOutbox\")
         + (SELECT count(*) FROM \"SyncJobs\" WHERE \"Status\" IN ('Pending','Running'))
         + (SELECT count(*) FROM \"PushSubscriptions\")
         + (SELECT count(*) FROM \"AppSecrets\")
         + (SELECT count(*) FROM \"DataProtectionKeys\")
         + (SELECT count(*) FROM \"RefreshSessions\")")
  [ "$bad" = "0" ] || eval_die "$db is NOT sanitised ($bad offending rows)"
  echo "sanitised: $db (tokens placeholdered, outbox empty, no pending jobs, no push subscriptions, no live secrets or sessions)"
}

stamp() { # stamp <db> <snapshot-id>
  psql -X -v ON_ERROR_STOP=1 -h "$EVAL_PGHOST" -p "$EVAL_PGPORT" -U "$EVAL_PGUSER" -d postgres \
    -c "COMMENT ON DATABASE \"$1\" IS 'snapshot:$2'" >/dev/null
}

snapshot_id() {
  psql -X -At -h "$EVAL_PGHOST" -p "$EVAL_PGPORT" -U "$EVAL_PGUSER" -d postgres \
    -c "SELECT coalesce(shobj_description(oid, 'pg_database'), '') FROM pg_database WHERE datname = '$1'" \
    | sed -n 's/^snapshot://p'
}

admin() { psql -X -v ON_ERROR_STOP=1 -h "$EVAL_PGHOST" -p "$EVAL_PGPORT" -U "$EVAL_PGUSER" -d postgres "$@"; }

# archive_ledger <db>: keep the spend record of a database about to be dropped (no-op if it does not exist).
archive_ledger() {
  local exists
  exists=$(admin -Atc "SELECT 1 FROM pg_database WHERE datname = '$1'")
  [ "$exists" = "1" ] || return 0
  node "$(dirname "$0")/ledger.mjs" archive --db "$1" >&2
}

cmd="${1:-}"; shift || true
case "$cmd" in
  restore)
    dump="${1:?dump file}"; target="${2:?target db}"; id="${3:-$(basename "$dump" .dump)}"
    eval_guard_target "$target"
    [ -f "$dump" ] || eval_die "no such dump: $dump"
    archive_ledger "$target"
    admin -c "DROP DATABASE IF EXISTS \"$target\" WITH (FORCE)" -c "CREATE DATABASE \"$target\""
    # The container's own pg_restore, because a dump from the live pg16 server is a newer archive
    # format than an older host client can read ("unsupported version in file header").
    docker exec -i "${EVAL_PG_CONTAINER:-fareel-eval-postgres}" \
      pg_restore --exit-on-error --no-owner --no-privileges -U "$EVAL_PGUSER" -d "$target" < "$dump"
    sanitise "$target"; stamp "$target" "$id"; echo "snapshot id: $id"
    ;;
  clone)
    source="${1:?source db}"; target="${2:?target db}"
    eval_guard_target "$source"; eval_guard_target "$target"
    [ "$source" != "$target" ] || eval_die "source and target are the same database"
    id="$(snapshot_id "$source")"; [ -n "$id" ] || id="$source"
    archive_ledger "$target"
    admin -c "DROP DATABASE IF EXISTS \"$target\" WITH (FORCE)" -c "CREATE DATABASE \"$target\" TEMPLATE \"$source\""
    sanitise "$target"; stamp "$target" "$id"; echo "snapshot id: $id"
    ;;
  sanitise) db="${1:?db}"; eval_guard_target "$db"; sanitise "$db" ;;
  id) db="${1:?db}"; eval_guard_target "$db"; snapshot_id "$db" ;;
  *) sed -n '2,/^set -euo/p' "$0" | sed '$d'; exit 64 ;;
esac
