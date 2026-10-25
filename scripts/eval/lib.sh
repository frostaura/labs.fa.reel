#!/usr/bin/env bash
# Shared guards for every eval script. Sourced, never executed.
#
# The eval stack lives on the ISOLATED eval Postgres (127.0.0.1:5434). The founder's live data lives on
# :5433 in the database `reel`. Nothing under scripts/eval may ever connect to either of those, so every
# script calls eval_guard_target before it touches a database. The guard is deliberately dumb: it refuses
# by port and by name, because a "smart" guard is one a typo walks straight past.

EVAL_PGHOST="${EVAL_PGHOST:-127.0.0.1}"
EVAL_PGPORT="${EVAL_PGPORT:-5434}"
EVAL_PGUSER="${EVAL_PGUSER:-reel}"
# The eval Postgres password is a local, non-secret fixture value (the container is bound to loopback).
export PGPASSWORD="${EVAL_PGPASSWORD:-reel_eval}"

eval_die() { echo "eval: $*" >&2; exit 2; }

# eval_guard_target <database> [port]
eval_guard_target() {
  local db="$1" port="${2:-$EVAL_PGPORT}"
  [ -n "$db" ] || eval_die "no database named"
  [ "$port" = "5433" ] && eval_die "refusing port 5433 — that is the founder's LIVE Postgres"
  [ "$db" = "reel" ] && eval_die "refusing database 'reel' — that is the founder's LIVE database"
  case "$db" in
    reel_eval|reel_eval_*|reel_ws*|reel_test_*|reel_baseline*|reel_int_*) ;;
    *) eval_die "refusing database '$db' — eval scripts only touch reel_eval*, reel_ws*, reel_test_*, reel_baseline* or reel_int_* (integrator gate copies)" ;;
  esac
}

eval_psql() { # eval_psql <db> [psql args...]
  local db="$1"; shift
  eval_guard_target "$db"
  psql -X -v ON_ERROR_STOP=1 -h "$EVAL_PGHOST" -p "$EVAL_PGPORT" -U "$EVAL_PGUSER" -d "$db" "$@"
}
