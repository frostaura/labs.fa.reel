#!/usr/bin/env python3
"""Fail on any vulnerable NuGet package, top-level or transitive.

  dotnet list labs.fa.reel.slnx package --vulnerable --include-transitive --format json | nuget-vulnerable-gate.py
"""
import json
import sys

report = json.load(sys.stdin)
vulnerable = []
for project in report.get("projects", []):
    for framework in project.get("frameworks", []):
        for pkg in framework.get("topLevelPackages", []) + framework.get("transitivePackages", []):
            if pkg.get("vulnerabilities"):
                vulnerable.append(pkg)

if vulnerable:
    print("Vulnerable NuGet packages detected:", file=sys.stderr)
    for pkg in vulnerable:
        # The JSON report spells it "advisoryurl"; older SDKs wrote "advisoryUrl".
        ids = ", ".join(f"{v.get('severity')}:{v.get('advisoryurl') or v.get('advisoryUrl')}" for v in pkg["vulnerabilities"])
        print(f"- {pkg['id']} {pkg['resolvedVersion']} -> {ids}", file=sys.stderr)
    sys.exit(1)
print("No vulnerable NuGet packages detected")
