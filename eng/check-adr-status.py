#!/usr/bin/env python3
"""Keep docs/implementation-status.md honest about what is built.

The traceability matrix taught this lesson expensively: a status page written
as intentions drifts one way only, and the drift is invisible until someone
acts on it. Seventy-three of that matrix's eighty-six test citations named
classes nobody had written.

So this refuses a build where:

  1. an ADR exists and no row of the table covers it, or a row covers an ADR
     that does not exist;
  2. a row's state is not one of the four the legend defines;
  3. a code citation in the "Where it is" column names a project, directory or
     type that is not on disk; or
  4. an architecture document does not say whether the code does what it
     describes.

Check 3 is the one that matters. A row may still be generous about what "built"
means — that is a reading, and no script settles it — but it can no longer cite
a file that was never written.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ADR_DIR = ROOT / "docs" / "adr"
STATUS_DOC = ROOT / "docs" / "implementation-status.md"
ABANDONED_DOC = ROOT / "docs" / "decisions-abandoned.md"

STATES = {"Built", "Partly built", "Specified only", "Applied"}

ROW = re.compile(r"^\|\s*\[(\d{4})\]\([^)]+\)\s*\|(.+)$")
CODE = re.compile(r"`([^`]+)`")
BUILT = re.compile(r"^\*\*Built:\*\* .+$", re.M)

# Paths that are prose or documentation rather than a code citation. Documented
# links are already resolved by check-links.py, so they are skipped here.
NOT_CODE = re.compile(r"^(LICENSE|LICENSING\.md|CONTRIBUTING\.md|nuget\.config|external/|specifications/)")


def adrs_on_disk() -> set[str]:
    return {
        path.name[:4]
        for path in sorted(ADR_DIR.glob("0*.md"))
        if path.name[:4] != "0000"
    }


def table_rows(text: str) -> list[tuple[str, str, str]]:
    """Returns (adr, state, citations) for each row of the by-decision table."""
    rows = []
    for line in text.splitlines():
        match = ROW.match(line)
        if not match:
            continue

        cells = [cell.strip() for cell in match.group(2).split("|")]
        if len(cells) < 3:
            continue

        rows.append((match.group(1), cells[1].strip("* "), cells[2]))

    return rows


def resolves(token: str) -> bool:
    """Whether a citation names something on disk.

    Accepted shapes, in the order a reader would expect them to mean something:

        FallbackPlan.Protocol              a project directory
        Application/JobStateStore          a type in FallbackPlan.Application
        Repository.Index/Journal/Lifecycle  a type in a subdirectory
        Repository.Format/Cbor/Canonical*  a glob over one
    """
    if NOT_CODE.match(token):
        return True

    head, _, tail = token.partition("/")
    project = head if head.startswith("FallbackPlan.") else f"FallbackPlan.{head}"

    roots = [ROOT / "src" / project, ROOT / "tests" / project]
    roots = [root for root in roots if root.is_dir()]
    if not roots:
        return False

    if not tail:
        return True

    pattern = tail if tail.endswith("*") else f"{tail}.cs"
    if not pattern.endswith(".cs") and "*" not in pattern:
        pattern += ".cs"

    for root in roots:
        if any(root.glob(pattern)) or any(root.glob(f"**/{pattern}")):
            return True
        # A bare glob such as Hosts.Tests/* means "several files here".
        if pattern == "*" and any(root.glob("*.cs")):
            return True

    return False


def main() -> int:
    failures: list[str] = []

    if not STATUS_DOC.is_file():
        print(f"missing {STATUS_DOC.relative_to(ROOT)}", file=sys.stderr)
        return 1

    text = STATUS_DOC.read_text(encoding="utf-8")
    rows = table_rows(text)

    on_disk = adrs_on_disk()
    covered = {adr for adr, _, _ in rows}

    for adr in sorted(on_disk - covered):
        failures.append(f"ADR-{adr} exists and no row of implementation-status.md covers it")

    for adr in sorted(covered - on_disk):
        failures.append(f"implementation-status.md names ADR-{adr}, which does not exist")

    seen: set[str] = set()
    for adr, state, citations in rows:
        if adr in seen:
            failures.append(f"ADR-{adr} appears more than once in implementation-status.md")
        seen.add(adr)

        if state not in STATES:
            failures.append(f"ADR-{adr} has state {state!r}, which is not in the legend")

        for token in CODE.findall(citations):
            if not resolves(token):
                failures.append(f"ADR-{adr} cites `{token}`, which is not on disk")

    if not ABANDONED_DOC.is_file():
        failures.append("docs/decisions-abandoned.md is missing")

    # An architecture document describes a design. Whether the code does it is
    # a separate question, and one a reader will otherwise answer by assuming.
    architecture = sorted((ROOT / "docs" / "architecture").glob("*.md"))
    for path in architecture:
        if not BUILT.search(path.read_text(encoding="utf-8")):
            failures.append(f"{path.name} has no **Built:** line saying whether the code does this")

    print(f"ADRs on disk        : {len(on_disk)}")
    print(f"rows in the table   : {len(rows)}")
    print(f"citations resolved  : {sum(len(CODE.findall(c)) for _, _, c in rows)}")
    print(f"architecture docs   : {len(architecture)}, each saying whether it is built")

    if failures:
        print()
        for failure in failures:
            print(f"  {failure}", file=sys.stderr)
        print(f"\n{len(failures)} problem(s)", file=sys.stderr)
        return 1

    print("all checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
