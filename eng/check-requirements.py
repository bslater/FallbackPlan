#!/usr/bin/env python3
"""
Verify requirement-ID integrity across the documentation set.

Checks:
  1. Every FR-*/NFR-* defined in requirements/ is defined exactly once.
  2. Every defined requirement is reachable from the traceability matrix.
  3. Every requirement ID referenced anywhere in docs/, specifications/, or a
     C# source file under src/ or tests/ actually exists. Code comments cite
     requirement IDs as authority for design rules, and a citation of an ID
     nobody defined is authority borrowed from nothing.
  4. Every Test cell in the traceability matrix resolves: a class that exists
     in the project it names, a 'Project/*' wildcard whose project exists, or
     an explicit untested marker.

The third check is the one that earns its keep: a requirement reference that
looks authoritative but names an ID nobody defined is worse than no reference,
because it implies a guarantee that is not tracked anywhere.

The fourth exists because the same thing happened in the other direction. The
Test column was written as *planned* class names, and by the time anyone
resolved them 73 of 86 named nothing at all — the matrix read as coverage and
was mostly fiction. Naming a test that does not exist is a claim, so the column
is now checked like one; a requirement genuinely without a test says so.

Exits non-zero on any failure. Run from anywhere.
"""

from __future__ import annotations

import collections
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
REQUIREMENTS = ROOT / "docs" / "requirements"
SEARCH_ROOTS = [ROOT / "docs", ROOT / "specifications"]

# The archived original proposal quotes the superseded requirement set verbatim.
# Its IDs are historical and deliberately not part of the live set.
EXCLUDED = {"2026-08-original-proposal.md"}

ID_PATTERN = re.compile(r"\b((?:FR|NFR)-[A-Z]+-\d{3})\b")
DEFINITION_PATTERN = re.compile(
    r"^\|\s*((?:FR|NFR)-[A-Z]+-\d{3})\s*(?:\*\*\[(?:changed|new|amended)\]\*\*)?\s*\|"
)


def definitions(text: str) -> list[str]:
    return [
        match.group(1)
        for line in text.splitlines()
        if (match := DEFINITION_PATTERN.match(line))
    ]


# Citations are written three ways, and all three mean the same thing:
# 'FR-X-001..003' (a run), 'FR-X-001/002' (a handful), 'FR-X-001, 002' (a list).
# The slash form is the one that bit: a scanner matching only whole IDs sees
# 'FR-MAN-009/010/011/014' as a citation of FR-MAN-009 alone, and the other
# three read as untested while a test was proving them all along.
CITATION_PATTERN = re.compile(
    r"\b((?:FR|NFR)-[A-Z]+)-(\d{3})((?:(?:\.\.|/|,[ \t]*)\d{3})*)"
)


def traced_ids(text: str) -> set[str]:
    """Every requirement id in text, expanding all three citation shorthands."""
    found: set[str] = set()
    for prefix, first, tail in CITATION_PATTERN.findall(text):
        found.add(f"{prefix}-{first}")
        if tail.startswith(".."):
            end = re.findall(r"\d{3}", tail)[0]
            found |= {f"{prefix}-{n:03d}" for n in range(int(first), int(end) + 1)}
        else:
            found |= {f"{prefix}-{n}" for n in re.findall(r"\d{3}", tail)}
    return found


TEST_ROOT = ROOT / "tests"
ROW_PATTERN = re.compile(
    r"^\|\s*((?:FR|NFR)-[A-Z]+-\d{3}(?:\.\.\d{3}|(?:,\s*\d{3})+)?)\s*\|(.*)$"
)
CLASS_PATTERN = re.compile(
    r"\b(?:public|internal)\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+)*class\s+(\w+)"
)
UNTESTED_PATTERN = re.compile(r"\((?:untested|not a test|unmet);")

# A test file may need to NAME a requirement in order to say it does not
# establish it - which is the most useful thing a doc comment can say when a
# cell used to claim otherwise. Without this, the explanation reinstates the
# claim it exists to withdraw, and --drift reports the file as proving the
# requirement it just disclaimed. The phrase is fixed so it is greppable.
DISCLAIMER_PATTERN = re.compile(r"does not establish[^.]*", re.IGNORECASE)


def test_classes() -> dict[str, set[str]]:
    """Class names declared in each test project, keyed by project short name."""
    found: dict[str, set[str]] = collections.defaultdict(set)
    for path in TEST_ROOT.glob("*/**/*.cs"):
        relative = path.relative_to(ROOT).as_posix()
        if "/bin/" in relative or "/obj/" in relative:
            continue
        project = relative.split("/")[1].removeprefix("FallbackPlan.")
        found[project] |= set(CLASS_PATTERN.findall(path.read_text(encoding="utf-8")))
    return found


def unresolved_tests(traceability: str) -> list[str]:
    """Test cells naming a class or project that does not exist."""
    projects = test_classes()
    problems: list[str] = []

    for line in traceability.splitlines():
        match = ROW_PATTERN.match(line)
        if not match:
            continue

        cells = [cell.strip() for cell in match.group(2).split("|")]
        cell = cells[2] if len(cells) > 2 else ""
        citations = re.findall(r"`([^`]+)`", cell)

        if not citations:
            # No class named at all: only an explicit marker is acceptable,
            # because a blank cell is indistinguishable from an oversight.
            if not UNTESTED_PATTERN.search(cell):
                problems.append(f"{match.group(1)}: Test cell is neither a class nor an untested marker")
            continue

        for citation in citations:
            project, _, name = citation.rpartition("/")
            if project not in projects:
                problems.append(f"{match.group(1)}: no test project '{project}' (cited as '{citation}')")
            elif name != "*" and name not in projects[project]:
                problems.append(f"{match.group(1)}: '{project}' declares no class '{name}'")

    return problems


def covering_classes() -> dict[str, set[str]]:
    """Requirement id -> the test classes whose file cites it and holds tests."""
    covers: dict[str, set[str]] = collections.defaultdict(set)
    for path in TEST_ROOT.glob("*/**/*.cs"):
        relative = path.relative_to(ROOT).as_posix()
        if "/bin/" in relative or "/obj/" in relative:
            continue

        text = path.read_text(encoding="utf-8")
        names = [n for n in CLASS_PATTERN.findall(text) if n.endswith(("Tests", "Properties"))]
        if not names or not re.search(r"\[(?:Fact|Theory)\b", text):
            continue

        disclaimed = {
            rid
            for span in DISCLAIMER_PATTERN.findall(text)
            for rid in ID_PATTERN.findall(span)
        }

        project = relative.split("/")[1].removeprefix("FallbackPlan.")
        for rid in traced_ids(text) - disclaimed:
            covers[rid] |= {f"{project}/{name}" for name in names}
    return covers


def drift(traceability: str) -> list[str]:
    """Requirements a test proves that the matrix does not credit it for."""
    covers = covering_classes()
    reports: list[str] = []

    for line in traceability.splitlines():
        match = ROW_PATTERN.match(line)
        if not match:
            continue

        cells = [cell.strip() for cell in match.group(2).split("|")]
        cell = cells[2] if len(cells) > 2 else ""
        named = set(re.findall(r"`([^`]+)`", cell))

        for rid in sorted(traced_ids(match.group(1))):
            proving = covers.get(rid, set())

            # Only a row crediting *none* of the tests that prove it is drift.
            # A row naming two of four is not stale, it is abridged — cells list
            # the clearest witnesses rather than every one, and reporting that
            # as a problem would bury the real signal in noise.
            if proving and not (proving & named):
                reports.append(f"{rid}: proven by {', '.join(sorted(proving))}, credited to none of them")

    return reports


def main() -> int:
    functional = (REQUIREMENTS / "functional.md").read_text()
    non_functional = (REQUIREMENTS / "non-functional.md").read_text()
    traceability = (REQUIREMENTS / "traceability.md").read_text()

    defined = definitions(functional) + definitions(non_functional)
    defined_set = set(defined)
    failures: list[str] = []

    duplicates = [i for i, c in collections.Counter(defined).items() if c > 1]
    if duplicates:
        failures.append(f"duplicate requirement IDs: {sorted(duplicates)}")

    untraced = sorted(defined_set - traced_ids(traceability))
    if untraced:
        failures.append(f"defined but absent from traceability.md: {untraced}")

    orphans = sorted(traced_ids(traceability) - defined_set)
    if orphans:
        failures.append(f"in traceability.md but never defined: {orphans}")

    def reference_files():
        for root in SEARCH_ROOTS:
            for path in root.rglob("*.md"):
                if path.name not in EXCLUDED:
                    yield path
        # C# sources cite requirement IDs in XML doc comments. Vendored code
        # and build output are not ours to validate.
        for tree in (ROOT / "src", ROOT / "tests"):
            for path in tree.rglob("*.cs"):
                rel = path.relative_to(ROOT).as_posix()
                if "/bin/" not in rel and "/obj/" not in rel:
                    yield path

    scanned = 0
    dangling: set[tuple[str, str]] = set()
    for path in reference_files():
        scanned += 1
        for rid in ID_PATTERN.findall(path.read_text(encoding="utf-8")):
            if rid not in defined_set:
                dangling.add((str(path.relative_to(ROOT)), rid))
    if dangling:
        failures.append(f"references to undefined requirement IDs: {sorted(dangling)}")

    if unresolved := unresolved_tests(traceability):
        failures.append("traceability Test column does not resolve:\n       " + "\n       ".join(unresolved))

    # A cell may both name a class and disclaim it — "measured by X, but the
    # figure is stated against a machine none of it ran on". The marker wins:
    # counting those as tested is exactly the flattery this column had before.
    tested = sum(
        1
        for line in traceability.splitlines()
        if ROW_PATTERN.match(line)
        and "`" in line.split("|")[4]
        and not UNTESTED_PATTERN.search(line.split("|")[4])
    )

    print(f"requirements defined : {len(defined)}")
    print(f"traceability tested  : {tested}/{len(defined_set)}")

    if "--drift" in sys.argv:
        # Reporting only, never a failure. Check 4 stops the matrix claiming a
        # test that does not exist; this is the opposite direction — coverage
        # improving while the matrix sleeps — and it cannot be a rule, because a
        # test may cite an id in passing without being that requirement's proof.
        # It is a prompt for a maintainer to look, not a verdict.
        reports = drift(traceability)
        print(f"\ndrift ({len(reports)} requirement(s) proven by a test the matrix does not name):")
        for report in reports:
            print(f"  {report}")
    print(f"traceability coverage: {len(defined_set & traced_ids(traceability))}/{len(defined_set)}")
    print(f"files scanned        : {scanned}")

    if failures:
        print()
        for failure in failures:
            print(f"FAIL {failure}", file=sys.stderr)
        return 1

    print("all checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
