#!/usr/bin/env bash
# run-api.sh — start a Reel API on an eval database with every isolation switch the eval needs.
#
#   scripts/eval/run-api.sh --dll <FrostAura.Reel.Api.dll> --db <eval db> --port <port> \
#       --env-file <keys-only env file> [--jobs] [--log <file>] [--pidfile <file>]
#
# What it guarantees (program §4.12 / §7 rule 1), whatever is in the env file:
#   - the connection string points at the eval Postgres (:5434) and a guarded eval database;
#   - every background service is OFF (the job runner too, unless --jobs is passed for a Fresh build);
#   - no Trakt network call can leave the process: all outbound HTTPS goes through a dead loopback
#     proxy EXCEPT the model and TMDB hosts, so a Trakt call fails locally and never reaches Trakt
#     (the ledger's Trakt row is the proof — see docs/ask-reel-eval.md);
#   - the dev-link QA session resolves the existing account by slug, so it never touches Trakt tokens;
#   - the eval account's Ask/rerank allowances are raised on THIS process only;
#   - it never pays with the founder's own OpenRouter key (the env file's key is compared by SHA-256 with
#     the live clone's, never printed) unless EVAL_FOUNDER_KEY_SIGNOFF="<who, when>" records his sign-off;
#   - it never loads the founder's credentials: the API's DotEnv walks UP from the dll's folder to the
#     first .env it finds and fills every variable still unset, so a dll with a .env in any parent
#     folder (e.g. one built inside ~/src/fa.reel) is refused, and every credential the env file does not
#     set is exported empty (DotEnv never overrides a variable that is set, even to "");
#   - the API starts from an EMPTY environment (env -i) holding only the env file's variables, the
#     switches below and HOME/PATH/TMPDIR — never a key inherited from the caller's shell profile (a
#     2026-10-24 smoke run with a key-less env file still paid 19 rerank-page calls on an OPENROUTER_API_KEY
#     the shell had exported). --dry-run prints the variable NAMES the API would get (values never) and exits.
#   - the API boots the way the merged code expects an eval copy to boot: from the dll's own folder (its
#     content root, so appsettings.json loads), with Database__AutoMigrateOnStartup=true (a pre-merge copy
#     is migrated, never run against an older schema) and no pre-migration dump handshake (a disposable copy
#     needs none), with boot-time data repairs and the trust purge OFF (both mutate the snapshot, and the
#     purge forces a paid Fresh build), and with Trakt OFFLINE in-process (TRAKT_CLIENT_ID=eval-no-trakt
#     selects the OfflineTraktClient, so no Trakt call ever opens a socket);
#   - an env file without a JWT_SECRET is refused here, by name, instead of the API dying later with an
#     options-validation error.
# It never starts on :5054 (the live dev port), never on the `reel` database and never on a *_template
# database (the shared base every slot is cloned from: migrating it would change every later clone).
set -euo pipefail
. "$(dirname "$0")/lib.sh"

dll=""; db=""; port=""; envfile=""; jobs=false; log=""; pidfile=""; dryrun=false
while [ $# -gt 0 ]; do
  case "$1" in
    --dll) dll="$2"; shift 2 ;;
    --db) db="$2"; shift 2 ;;
    --port) port="$2"; shift 2 ;;
    --env-file) envfile="$2"; shift 2 ;;
    --jobs) jobs=true; shift ;;
    --log) log="$2"; shift 2 ;;
    --pidfile) pidfile="$2"; shift 2 ;;
    --dry-run) dryrun=true; shift ;;
    *) eval_die "unknown argument $1" ;;
  esac
done
[ -f "$dll" ] || eval_die "--dll must name a built FrostAura.Reel.Api.dll"
[ -n "$port" ] || eval_die "--port is required"
[ "$port" = "5054" ] && eval_die "refusing port 5054 — that is the founder's live dev API"
eval_guard_target "$db"
case "$db" in *_template) eval_die "refusing $db — a template is cloned, never run (clone it first: scripts/eval/snapshot.sh clone $db <slot db>)" ;; esac
[ -f "$envfile" ] || eval_die "--env-file is required (model/TMDB keys only)"

# No .env anywhere above the dll: the API would read the first one it finds.
d="$(cd "$(dirname "$dll")" && pwd)"
while [ "$d" != "/" ]; do
  [ -f "$d/.env" ] && eval_die "refusing $dll: $d/.env is above it, and the API's DotEnv would load it (build with scripts/eval/build-at.sh outside any checkout)"
  d="$(dirname "$d")"
done

key_class=0
node "$(dirname "$0")/guards.mjs" key "$envfile" >/dev/null || key_class=$?
case "$key_class" in
  0|4) ;;
  3) [ -n "${EVAL_FOUNDER_KEY_SIGNOFF:-}" ] || eval_die "the env file's OPENROUTER_API_KEY is the founder's own key (§4.12, §7 rule 1); use the eval key, or set EVAL_FOUNDER_KEY_SIGNOFF=\"<who, when>\" with his explicit sign-off"
     echo "run-api: paying with the FOUNDER'S key, signed off: $EVAL_FOUNDER_KEY_SIGNOFF" >&2 ;;
  *) [ -n "${EVAL_FOUNDER_KEY_SIGNOFF:-}" ] || eval_die "cannot establish whether the env file's key is the founder's (set EVAL_FOUNDER_KEY_SHA256)" ;;
esac

# The env file is read in an EMPTY subshell, so only ITS variables (never the caller's) are carried over.
names=()
while IFS= read -r -d '' kv; do
  n="${kv%%=*}"
  case "$n" in PWD|SHLVL|_|OLDPWD) continue ;; esac
  export "$n=${kv#*=}"; names+=("$n")
done < <(env -i bash -c 'set -a; . "$1"; set +a; env -0' _ "$envfile")
# The API refuses to start without a session signing secret; say so by name rather than let it die later.
jwt_in_file=false
for n in ${names[@]+"${names[@]}"}; do [ "$n" = "JWT_SECRET" ] && jwt_in_file=true; done
if ! $jwt_in_file || [ -z "${JWT_SECRET//[[:space:]]/}" ]; then
  eval_die "$envfile sets no JWT_SECRET, and the API will not start without one. Add a throwaway local value (any 32+ characters, never the live secret): JWT_SECRET=<value>"
fi
# Every credential the env file did not set is exported EMPTY, so nothing can fill it from elsewhere.
credentials=(OPENROUTER_API_KEY EMBEDDINGS_API_KEY OPENAI_API_KEY STRIPE_SECRET_KEY STRIPE_WEBHOOK_SECRET STRIPE_PRICE_ID TRAKT_CLIENT_SECRET TRAKT_ACCESS_TOKEN TRAKT_REFRESH_TOKEN)
for v in "${credentials[@]}"; do
  in_file=false
  for n in ${names[@]+"${names[@]}"}; do [ "$n" = "$v" ] && in_file=true; done
  $in_file || export "$v="
done

export ConnectionStrings__Postgres="Host=$EVAL_PGHOST;Port=$EVAL_PGPORT;Username=$EVAL_PGUSER;Password=$PGPASSWORD;Database=$db"
export ASPNETCORE_ENVIRONMENT=Development QA_HELPERS_ENABLED=true
export TRAKT_DEV_PROFILE="${EVAL_TRAKT_DEV_PROFILE:-deanmar09}"
# Trakt isolation in-process: this client id selects OfflineTraktClient, which fails every call locally,
# and no Trakt token reaches the API even when the env file carries one (the compose eval stack's rule).
export TRAKT_CLIENT_ID=eval-no-trakt TRAKT_CLIENT_SECRET=eval-no-trakt TRAKT_ACCESS_TOKEN="" TRAKT_REFRESH_TOKEN=""
# Boot like a disposable eval copy: migrate it, with no dump handshake, no repairs and no trust purge.
export Database__AutoMigrateOnStartup=true BACKUP_REQUEST_DIR="" Background__DataRepairs=false Background__TrustPurge=false
for s in OutboxDispatcher TokenRefresh DeltaPoll Digest CatalogGrowth NightlyReconcile CatalogFollowThrough LanguageBackfill; do
  export "Background__$s=false"
done
export Background__JobRunner="$jobs"
if [ "$jobs" = true ]; then
  # A job runner is on only for the surface harness's one Fresh build. Everything a build can chain into
  # that is not the surface under test is switched off by its own ceiling (zero is the off switch):
  # cross-reference verdicts, catalogue extraction, embedding and hydration.
  export LLM_XREF_DAILY_CALLS=0 LLM_XREF_DAILY_TOKENS=0 EXTRACT_DAILY_TOKENS=0 EMBED_DAILY_TOKENS=0 HYDRATE_MAX_TITLES_PER_DAY=0
fi
# Trakt isolation by construction: a dead proxy for everything but the hosts the eval may pay for.
export HTTPS_PROXY="http://127.0.0.1:9" HTTP_PROXY="http://127.0.0.1:9"
export NO_PROXY="openrouter.ai,api.themoviedb.org,image.tmdb.org,localhost,127.0.0.1"
# Eval-only allowances (never the live stack). The rerank family ceiling is the one an eval run exhausts.
export LLM_RERANK_DAILY_CALLS="${EVAL_RERANK_DAILY_CALLS:-100000}"
export LLM_RERANK_DAILY_TOKENS="${EVAL_RERANK_DAILY_TOKENS:-200000000}"

# Exactly these variables reach the API, from an empty environment.
pass=(HOME PATH TMPDIR USER LANG ${names[@]+"${names[@]}"} "${credentials[@]}" ConnectionStrings__Postgres ASPNETCORE_ENVIRONMENT QA_HELPERS_ENABLED
  TRAKT_DEV_PROFILE TRAKT_CLIENT_ID Database__AutoMigrateOnStartup BACKUP_REQUEST_DIR Background__DataRepairs Background__TrustPurge
  Background__JobRunner HTTPS_PROXY HTTP_PROXY NO_PROXY LLM_RERANK_DAILY_CALLS LLM_RERANK_DAILY_TOKENS)
for s in OutboxDispatcher TokenRefresh DeltaPoll Digest CatalogGrowth NightlyReconcile CatalogFollowThrough LanguageBackfill; do pass+=("Background__$s"); done
[ "$jobs" = true ] && pass+=(LLM_XREF_DAILY_CALLS LLM_XREF_DAILY_TOKENS EXTRACT_DAILY_TOKENS EMBED_DAILY_TOKENS HYDRATE_MAX_TITLES_PER_DAY)
envv=()
for n in $(printf '%s\n' "${pass[@]}" | sort -u); do
  [ -n "${!n+x}" ] && envv+=("$n=${!n}")
done
# The content root is the dll's own folder (appsettings*.json sit beside the dll); the log and pid file
# paths are made absolute first, because the API starts from that folder.
dlldir="$(cd "$(dirname "$dll")" && pwd)"
abspath() { case "$1" in /*) echo "$1" ;; *) echo "$PWD/$1" ;; esac; }
[ -n "$log" ] && log="$(abspath "$log")"
[ -n "$pidfile" ] && pidfile="$(abspath "$pidfile")"
[ -f "$dlldir/appsettings.json" ] || echo "run-api: warning: no appsettings.json beside $dll, so the API runs on code defaults" >&2
if [ "$dryrun" = true ]; then
  echo "run-api: content root $dlldir" >&2
  # Names only, never values — except the isolation switches this script itself sets, which hold no
  # secret and are what a reader needs to check (e.g. Background__DataRepairs=false).
  for kv in "${envv[@]}"; do
    n="${kv%%=*}"
    case "$n" in
      Background__*|Database__*|BACKUP_REQUEST_DIR|TRAKT_CLIENT_ID|TRAKT_DEV_PROFILE|ASPNETCORE_ENVIRONMENT|HTTP_PROXY|HTTPS_PROXY|NO_PROXY|LLM_RERANK_DAILY_*) echo "$kv" ;;
      *) if [ -n "${kv#*=}" ]; then echo "$n"; else echo "$n (empty)"; fi ;;
    esac
  done
  exit 0
fi

log="${log:-/dev/null}"
dotnet="${DOTNET:-$HOME/.dotnet/dotnet}"
cd "$dlldir"
nohup env -i "${envv[@]}" "$dotnet" "$dlldir/$(basename "$dll")" --urls "http://127.0.0.1:$port" < /dev/null > "$log" 2>&1 &
pid=$!
[ -n "$pidfile" ] && echo "$pid" > "$pidfile"
for _ in $(seq 1 120); do
  if curl -fsS "http://127.0.0.1:$port/health" >/dev/null 2>&1; then echo "api up: pid $pid on :$port db $db jobs=$jobs"; exit 0; fi
  kill -0 "$pid" 2>/dev/null || eval_die "api exited during startup — see $log"
  sleep 1
done
eval_die "api did not become healthy in 120 s — see $log"
