#!/usr/bin/env bash
# Offline A/B of WS-5 task 12 (show rating rollups in the taste pools): the real EvalHarness on the
# founder's eval data, rollups on vs off, same code and same data. Clones the source database, runs
# ShowRollupAblationRun against the clone, writes the artifact, and drops the clone.
#
#   PGPASSWORD=<eval password> scripts/ml/show-rollup-ablation.sh [source-db] [out.json]
#
#   source-db  default reel_eval_template (read only: it is the TEMPLATE of the clone, never written)
#   out.json   default eval/model/show-rollup-ablation.json
#   env        REEL_ABLATION_HOST (127.0.0.1), REEL_ABLATION_PORT (5434), REEL_ABLATION_USER (reel),
#              REEL_ABLATION_ACCOUNT (default: the account with the most movie/show ratings),
#              DOTNET (default dotnet). The password is read from PGPASSWORD and never printed.
#
# Paid calls: none (FastTree, local). Re-running on the same source and commit reproduces the file.
set -euo pipefail

repo=$(cd "$(dirname "$0")/../.." && pwd)
source_db=${1:-reel_eval_template}
out=${2:-$repo/eval/model/show-rollup-ablation.json}
host=${REEL_ABLATION_HOST:-127.0.0.1}
port=${REEL_ABLATION_PORT:-5434}
user=${REEL_ABLATION_USER:-reel}
: "${PGPASSWORD:?set PGPASSWORD to the eval Postgres password}"

if [ "$port" = "5433" ]; then
  echo "refusing port 5433: that is the live Postgres" >&2
  exit 2
fi

clone="reel_ablation_$$_$(date +%s)"
psql_admin() { psql -h "$host" -p "$port" -U "$user" -d postgres -v ON_ERROR_STOP=1 -qAtc "$1"; }
psql_admin "CREATE DATABASE \"$clone\" TEMPLATE \"$source_db\""
trap 'psql_admin "DROP DATABASE IF EXISTS \"$clone\" WITH (FORCE)" || true' EXIT

commit=$(git -C "$repo" rev-parse --short HEAD)
if [ -n "$(git -C "$repo" status --porcelain -- src)" ]; then commit="$commit-dirty"; fi

export REEL_ABLATION_CONNECTION_STRING="Host=$host;Port=$port;Username=$user;Password=$PGPASSWORD;Database=$clone"
export REEL_ABLATION_OUT="$out" REEL_ABLATION_SOURCE="$source_db" REEL_ABLATION_COMMIT="$commit"
export MSBuildEnableWorkloadResolver=false
unset OPENROUTER_API_KEY

"${DOTNET:-dotnet}" test "$repo/src/backend/FrostAura.Reel.Tests/FrostAura.Reel.Tests.csproj" \
  --filter "FullyQualifiedName~ShowRollupAblationRun" --logger "console;verbosity=minimal"
echo "wrote $out"
