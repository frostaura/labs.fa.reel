#!/bin/sh
# test-filter.sh unit|integration
# Prints the `dotnet test --filter` expression for one half of the backend suite (CR-11, R1-34).
#   integration  the IntegrationTests namespace plus every class in database-test-classes.txt
#   unit         everything else
# The two are complements by construction, and Tests/Harness/CiTestSelectionTests proves it against
# the compiled test assembly. A trailing `.` on each class name stops `LeakageTests` matching a
# `LeakageTestsExtra`.
set -eu
list="$(dirname "$0")/database-test-classes.txt"
[ -r "$list" ] || { echo "test-filter: cannot read $list" >&2; exit 1; }
case "${1:-}" in
  integration) filter='FullyQualifiedName~IntegrationTests'; join='|'; op='~' ;;
  unit) filter='FullyQualifiedName!~IntegrationTests'; join='&'; op='!~' ;;
  *) echo "usage: test-filter.sh unit|integration" >&2; exit 2 ;;
esac
for class in $(sed -e 's/#.*//' -e 's/[[:space:]]//g' "$list"); do
  case "$class" in
    *[!A-Za-z0-9_.]*) echo "test-filter: not a class name: $class" >&2; exit 1 ;;
  esac
  filter="$filter${join}FullyQualifiedName$op$class."
done
printf '%s\n' "$filter"
