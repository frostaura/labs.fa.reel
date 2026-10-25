#!/usr/bin/env python3
"""Fail when line coverage drops below a ratchet.

  coverage-gate.py cobertura <results-dir> <threshold-percent>
  coverage-gate.py istanbul  <coverage-summary.json> <threshold-percent>

A regression guard, not a target: the thresholds live in the Makefile next to the step that
produces each report. Portable on purpose (the old inline gate used `grep -P`, which macOS lacks),
so `make ci` runs the same check locally that CI runs.
"""
import json
import pathlib
import re
import sys


def cobertura(results_dir: str) -> float:
    reports = sorted(pathlib.Path(results_dir).rglob("coverage.cobertura.xml"))
    if not reports:
        sys.exit(f"coverage-gate: no coverage.cobertura.xml under {results_dir}")
    match = re.search(r'line-rate="([0-9.]+)"', reports[-1].read_text(encoding="utf-8"))
    if not match:
        sys.exit(f"coverage-gate: no line-rate in {reports[-1]}")
    return float(match.group(1)) * 100


def istanbul(summary: str) -> float:
    path = pathlib.Path(summary)
    if not path.is_file():
        sys.exit(f"coverage-gate: missing {summary}")
    return float(json.loads(path.read_text(encoding="utf-8"))["total"]["lines"]["pct"])


def main() -> None:
    if len(sys.argv) != 4 or sys.argv[1] not in ("cobertura", "istanbul"):
        sys.exit(__doc__)
    kind, source, threshold = sys.argv[1], sys.argv[2], float(sys.argv[3])
    pct = cobertura(source) if kind == "cobertura" else istanbul(source)
    print(f"Line coverage: {pct:.1f}%  (threshold {threshold:.0f}%)")
    if pct < threshold:
        sys.exit(f"coverage-gate: {pct:.1f}% is below the {threshold:.0f}% threshold")


main()
