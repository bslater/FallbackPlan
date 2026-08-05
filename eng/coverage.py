#!/usr/bin/env python3
"""
Measure line coverage of the production assemblies.

Runs the test suite with coverlet, then aggregates every per-project
Cobertura report into one per-module summary. Optionally fails when a module
falls below a floor, so coverage can be a gate rather than a number nobody
reads.

Two things this handles that a naive aggregation gets wrong:

  1. **Paths are normalised before lines are keyed.** The same source file is
     reported as `src/FallbackPlan.Application/Schedule.cs` by one project's
     report, `FallbackPlan.Application/Schedule.cs` by another, and an
     absolute `/home/…/src/FallbackPlan.Application/Schedule.cs` by a third.
     Keyed verbatim, one file becomes three, a line covered under one
     spelling stays "uncovered" under the others, and modules appear to LOSE
     coverage when tests are added. Both times this bit, the drop looked like
     a real regression; coverage cannot fall when only tests are added, which
     is the check worth applying to any such number.

  2. **A line is covered if any report covers it.** Each test project emits a
     report for every assembly it loaded, so a module appears many times with
     different hits. The union is the answer; a per-file average is not.

Coverage of a single OS is a partial answer by construction: the platform
interop of the scanner (Linux, Darwin, Windows) can only be exercised on its
own platform, so the honest total comes from merging the CI matrix's reports,
not from any one run. `--report-only` consumes reports collected elsewhere so
that merge is possible.

Usage:
    eng/coverage.py                     # run the suite, then summarise
    eng/coverage.py --report-only DIR   # summarise reports already collected
    eng/coverage.py --floor 60          # exit non-zero if a module is below 60%
"""

from __future__ import annotations

import argparse
import collections
import glob
import pathlib
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parent.parent

# Test projects and test-only helpers are measured by what they prove, not by
# their own coverage; a test asserting nothing would score 100%.
EXCLUDED_SUFFIXES = ("Tests", "TestSupport")

# A file is identified by its path from the repository root onwards. Reports
# spell the same file at least three ways — "src/X/Y.cs", "X/Y.cs", and an
# absolute "/home/…/src/X/Y.cs" — depending on which project emitted them, so
# the anchor is the LAST "src/" or "tests/" segment, wherever it appears.
SOURCE_ROOT = re.compile(r"^.*?(?:^|/)(?:src|tests)/")


def normalise(filename: str) -> str:
    return SOURCE_ROOT.sub("", filename.replace("\\", "/"))


def collect(results_directory: pathlib.Path) -> dict[str, tuple[set, set]]:
    modules: dict[str, tuple[set, set]] = collections.defaultdict(lambda: (set(), set()))
    reports = glob.glob(str(results_directory / "**" / "coverage.cobertura.xml"), recursive=True)
    if not reports:
        raise SystemExit(f"no Cobertura reports under {results_directory}")

    for report in reports:
        for package in ET.parse(report).getroot().iter("package"):
            name = package.get("name") or ""
            if name.endswith(EXCLUDED_SUFFIXES):
                continue
            covered, uncovered = modules[name]
            for klass in package.iter("class"):
                filename = normalise(klass.get("filename") or "")
                for line in klass.iter("line"):
                    key = (int(line.get("number")), filename)
                    (covered if int(line.get("hits")) > 0 else uncovered).add(key)

    return modules


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--report-only", metavar="DIR", help="summarise existing reports instead of running tests")
    parser.add_argument("--floor", type=float, default=None, help="fail if any module is below this percentage")
    arguments = parser.parse_args()

    with tempfile.TemporaryDirectory() as scratch:
        if arguments.report_only:
            results = pathlib.Path(arguments.report_only)
        else:
            results = pathlib.Path(scratch)
            completed = subprocess.run(
                [
                    "dotnet", "test", "FallbackPlan.slnx", "-c", "Release",
                    "--collect:XPlat Code Coverage", "--results-directory", str(results),
                ],
                cwd=ROOT, check=False,
            )
            if completed.returncode != 0:
                print("tests failed; coverage of a red suite is not worth reporting", file=sys.stderr)
                return completed.returncode

        modules = collect(results)

        rows = []
        for name, (covered, uncovered) in modules.items():
            missing = uncovered - covered
            total = len(covered) + len(missing)
            rows.append((name, 100 * len(covered) / total if total else 100.0, len(covered), len(missing)))
        rows.sort(key=lambda row: row[1])

        print(f"{'module':44} {'line%':>8} {'covered':>8} {'uncovered':>10}")
        for name, percentage, covered_count, missing_count in rows:
            print(f"{name:44} {percentage:7.2f}% {covered_count:8} {missing_count:10}")

        total_covered = sum(row[2] for row in rows)
        total_missing = sum(row[3] for row in rows)
        overall = 100 * total_covered / (total_covered + total_missing)
        print(f"\n{'TOTAL (production assemblies)':44} {overall:7.2f}% {total_covered:8} {total_missing:10}")

        if arguments.floor is not None:
            below = [(name, percentage) for name, percentage, _, _ in rows if percentage < arguments.floor]
            if below:
                print(f"\nBelow the {arguments.floor:.0f}% floor:", file=sys.stderr)
                for name, percentage in below:
                    print(f"  {name}: {percentage:.2f}%", file=sys.stderr)
                return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
