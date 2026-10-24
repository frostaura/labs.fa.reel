#!/bin/sh
# no-build-warnings.sh <command...>
# Runs a build and fails on any warning it prints, as well as on a non-zero exit. Vite and its
# plugins print warnings ("WARN", "(!)", "warning:") and still exit 0, so the exit code alone lets
# them through; the repository's rule is zero warnings, ever.
set -u
log=$(mktemp)
"$@" > "$log" 2>&1
status=$?
cat "$log"
if grep -En '(^|[[:space:]])(WARN|warn|Warning|warning)([[:space:]]|:|\])|\(!\)' "$log" > /dev/null; then
  echo "no-build-warnings: the build printed warning(s):" >&2
  grep -En '(^|[[:space:]])(WARN|warn|Warning|warning)([[:space:]]|:|\])|\(!\)' "$log" >&2
  status=1
fi
rm -f "$log"
exit "$status"
