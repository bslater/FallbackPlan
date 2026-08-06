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

The third check is the one that earns its keep: a requirement reference that
looks authoritative but names an ID nobody defined is worse than no reference,
because it implies a guarantee that is not tracked anywhere.

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

    print(f"requirements defined : {len(defined)}")
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
