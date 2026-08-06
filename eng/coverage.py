#!/usr/bin/env python3
"""
Measure line coverage of the production assemblies.

Runs the test suite with coverlet, then aggregates every per-project
Cobertura report into one per-module summary. Optionally fails when a module
falls below a floor, so coverage can be a gate rather than a number nobody
reads.

Two things this handles that a naive aggregation gets wrong:

  1. **Filenames are resolved against the report's own `<source>` root.**
     Coverlet chooses a different root per project — "/", the repository's
     `src/`, or the project directory itself — so one file arrives as
     `home/…/src/X/Y.cs`, `X/Y.cs` and `Y.cs` in three reports. Keyed
     verbatim, one file becomes three, a line covered under one spelling
     stays "uncovered" under the others, and every module is understated.
     Stripping prefixes off the filename cannot fix this: the information
     needed lives in the `<source>` element, and ignoring it is what made
     the first two attempts here wrong.

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

# Cobertura filenames are relative to the report's own <source> root, and
# coverlet picks a different root per project — "/", the repository's src/,
# or the individual project directory. The same file therefore arrives as
# "home/…/src/X/Y.cs", "X/Y.cs" and "Y.cs" in three reports. Resolving the
# filename against its declared source root first is what makes them one
# key; stripping prefixes off the filename alone cannot, because the
# information needed is in the element that was ignored.
SOURCE_ROOT = re.compile(r"^.*?(?:^|/)(?:src|tests)/")


def normalise(source: str, filename: str) -> str:
    combined = filename.replace("\\", "/")
    if source:
        combined = source.replace("\\", "/").rstrip("/") + "/" + combined.lstrip("/")

    return SOURCE_ROOT.sub("", combined)


def collect(results_directory: pathlib.Path) -> dict[str, tuple[set, set]]:
    modules: dict[str, tuple[set, set]] = collections.defaultdict(lambda: (set(), set()))
    reports = glob.glob(str(results_directory / "**" / "coverage.cobertura.xml"), recursive=True)
    if not reports:
        raise SystemExit(f"no Cobertura reports under {results_directory}")

    for report in reports:
        root = ET.parse(report).getroot()
        sources = [source.text or "" for source in root.iter("source")]
        source = sources[0] if len(sources) == 1 else ""
        for package in root.iter("package"):
            name = package.get("name") or ""
            if name.endswith(EXCLUDED_SUFFIXES):
                continue
            covered, uncovered = modules[name]
            for klass in package.iter("class"):
                filename = normalise(source, klass.get("filename") or "")
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
