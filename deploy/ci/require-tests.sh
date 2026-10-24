#!/bin/sh
# require-tests.sh <command...>
# Runs a `dotnet test` command and fails unless it reports at least one test executed. A filtered
# run that matches nothing exits 0 ("No test matches the given testcase filter"), which would let a
# suite that does not exist yet — or one renamed out from under its filter — report green forever.
set -u
log=$(mktemp)
"$@" 2>&1 | tee "$log"
status=$?
# POSIX sh has no pipefail: recover the test command's own status from its output.
if grep -q 'Failed!' "$log"; then status=1; fi
if ! grep -Eq 'Passed:[[:space:]]+[1-9][0-9]*' "$log"; then
  echo "require-tests: no test ran for: $*" >&2
  status=1
fi
rm -f "$log"
exit "$status"
