#!/bin/sh
# require-tests.sh <command...>
# Runs a `dotnet test` command and fails unless it exits 0 AND reports at least one test executed.
# A filtered run that matches nothing exits 0 ("No test matches the given testcase filter"), which
# would let a suite that does not exist yet — or one renamed out from under its filter — report
# green forever.
set -u
log=$(mktemp)
rc=$(mktemp)
# POSIX sh has no pipefail, and `$?` after a pipe is tee's. The command writes its own exit status
# to a file instead, so a test host that crashes or aborts after printing "Passed: N" still fails.
{ "$@"; echo "$?" > "$rc"; } 2>&1 | tee "$log"
status=$(cat "$rc")
case "$status" in
  '' | *[!0-9]*) echo "require-tests: the test command left no exit status" >&2; status=1 ;;
esac
if grep -q 'Failed!' "$log"; then status=1; fi
if ! grep -Eq 'Passed:[[:space:]]+[1-9][0-9]*' "$log"; then
  echo "require-tests: no test ran for: $*" >&2
  status=1
fi
rm -f "$log" "$rc"
exit "$status"
