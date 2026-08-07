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


def traced_ids(text: str) -> set[str]:
    """IDs reachable from the matrix, expanding 'FR-X-001..004' and 'NFR-X-001, 004'."""
    found = set(ID_PATTERN.findall(text))
    for prefix, start, end in re.findall(r"((?:FR|NFR)-[A-Z]+)-(\d{3})\.\.(\d{3})", text):
        found |= {f"{prefix}-{n:03d}" for n in range(int(start), int(end) + 1)}
    for match in re.finditer(r"((?:FR|NFR)-[A-Z]+)-(\d{3})((?:,\s*\d{3})+)", text):
        found |= {f"{match.group(1)}-{n}" for n in re.findall(r"\d{3}", match.group(3))}
    return found


TEST_ROOT = ROOT / "tests"
ROW_PATTERN = re.compile(
    r"^\|\s*((?:FR|NFR)-[A-Z]+-\d{3}(?:\.\.\d{3}|(?:,\s*\d{3})+)?)\s*\|(.*)$"
)
CLASS_PATTERN = re.compile(
    r"\b(?:public|internal)\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+)*class\s+(\w+)"
)
UNTESTED_PATTERN = re.compile(r"\((?:untested|not a test);")


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

    tested = sum(
        1
        for line in traceability.splitlines()
        if ROW_PATTERN.match(line) and "`" in line.split("|")[4]
    )

    print(f"requirements defined : {len(defined)}")
    print(f"traceability tested  : {tested}/{len(defined_set)}")
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
