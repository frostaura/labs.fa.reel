#!/usr/bin/env bash
# build-at.sh <commit> <out-dir> — build the API exactly as it was at <commit>, outside any worktree's
# own tree, so a baseline (e.g. 99c0761) can be re-run against the eval stack at any time.
#   scripts/eval/build-at.sh 99c0761 ~/src/labs.fa.reel-ws/ws7/.tmp/base
# Prints the path of the built FrostAura.Reel.Api.dll and writes <out-dir>/BUILD_INFO.json (the full
# commit, the dll's SHA-256, when it was built). Pass that file to `askreel.mjs live --build-info` and
# `surfaces.mjs --build-info`, so a recording names the build it measured instead of a free-text flag.
# The build runs inside the lanes' 3-slot machine semaphore (~/src/labs.fa.reel-ws/bin/build-slot) whenever it
# exists (§5.0 rule 1); BUILD_SLOT overrides it. Release build, no tests. The archived commit is stamped
# explicitly (-p:SourceRevisionId): extracted under a worktree's .tmp the build would otherwise stamp the
# enclosing checkout's HEAD, and outside any checkout "unknown", so /health named the wrong build.
set -euo pipefail
commit="${1:?commit}"; out="${2:?out dir}"
repo="$(cd "$(dirname "$0")/../.." && pwd)"
slot="${BUILD_SLOT-}"
[ -z "$slot" ] && [ -x "$HOME/src/labs.fa.reel-ws/bin/build-slot" ] && slot="$HOME/src/labs.fa.reel-ws/bin/build-slot"
full="$(git -C "$repo" rev-parse --verify "$commit^{commit}")"
rm -rf "$out"; mkdir -p "$out"
# Commits before the 2026-11-20 rename carry the solution as fa.reel.slnx; archive whichever one exists.
sln="labs.fa.reel.slnx"; git -C "$repo" cat-file -e "$full:$sln" 2>/dev/null || sln="fa.reel.slnx"
git -C "$repo" archive "$full" src/backend "$sln" | tar -x -C "$out"
(cd "$out" && MSBuildEnableWorkloadResolver=false $slot "${DOTNET:-$HOME/.dotnet/dotnet}" build \
  src/backend/FrostAura.Reel.Api/FrostAura.Reel.Api.csproj -c Release -nologo -v q \
  -p:SourceRevisionId="$full" >&2)
dll="$out/src/backend/FrostAura.Reel.Api/bin/Release/net10.0/FrostAura.Reel.Api.dll"
sha="$(shasum -a 256 "$dll" | cut -d' ' -f1)"
printf '{"commit":"%s","dllSha256":"%s","builtAt":"%s","slot":"%s"}\n' "$full" "$sha" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "${slot:-none}" > "$out/BUILD_INFO.json"
echo "$dll"
