#!/usr/bin/env python3
"""Keep docs/proof-obligations.md from claiming proofs that do not exist.

The traceability matrix taught this lesson once already: a column written as
intentions reads as coverage, and by the time anyone resolved it 73 of its 86
test citations named classes nobody had written. The proof-obligation register
is the same shape of claim — "this invariant has a falsifier, and here is what
catches it" — so it is checked the same way, from the first commit rather than
after the drift.

This refuses a build where:

  1. a row's state is not one of the four the legend defines;
  2. a Proof cell cites a test class that no test project declares, a test
     project that does not exist, or a drill file that is not on disk;
  3. a row states no proof but is not marked Unproved or Open by decision --
     an em dash with a confident state is the exact flattery this register
     exists to refuse; or
  4. an Unproved row cites no requirement id, which is how a gap stops being
     a shrug and becomes something a phase can pick up.

Check 2 is the one that earns its keep. A row may still be generous about what
"proved" means -- whether a test would actually go red without the invariant is
a reading, and no script settles it -- but it can no longer name a witness that
was never written.
"""

from __future__ import annotations

import collections
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOC = ROOT / "docs" / "proof-obligations.md"
TEST_ROOT = ROOT / "tests"

# Rows the register is allowed to be in. Held here rather than read from the
# legend so that deleting the legend cannot quietly widen the vocabulary.
STATES = {"Proved", "Partly proved", "Unproved", "Open by decision"}

# A state that admits to having no proof. Only these may leave the Proof cell
# empty, and only these are exempt from naming a witness.
WITHOUT_PROOF = {"Unproved", "Open by decision"}

# The register is worth nothing if it silently empties. This is the floor the
# document had when the checker was written; a drop below it means rows were
# lost or the table shape changed under the parser, and either way the pass
# that reports "all checks passed" over three rows is worse than no pass.
MINIMUM_ROWS = 15

CODE = re.compile(r"`([^`]+)`")
CLASS = re.compile(r"\b(?:public|internal)\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+)*class\s+(\w+)")
REQUIREMENT = re.compile(r"\b(?:FR|NFR)-[A-Z]+-\d{3}\b")
NO_PROOF = re.compile(r"^[\s—-]*$")


def test_classes() -> dict[str, set[str]]:
    """Class names declared in each test project, keyed by project short name."""
    found: dict[str, set[str]] = collections.defaultdict(set)
    for path in TEST_ROOT.glob("*/**/*.cs"):
        relative = path.relative_to(ROOT).as_posix()
        if "/bin/" in relative or "/obj/" in relative:
            continue

        project = relative.split("/")[1].removeprefix("FallbackPlan.")
        found[project] |= set(CLASS.findall(path.read_text(encoding="utf-8")))
    return found


def rows(text: str) -> list[tuple[str, str, str]]:
    """Returns (invariant, proof, state) for each four-column table row.

    The legend is a two-column table and is skipped by the same rule, so the
    parser needs no positional knowledge of where the obligations begin.
    """
    found = []
    for line in text.splitlines():
        stripped = line.strip()
        if not stripped.startswith("|") or not stripped.endswith("|"):
            continue

        cells = [cell.strip() for cell in stripped.strip("|").split("|")]
        if len(cells) != 4:
            continue

        # The header and its separator, by shape rather than by content.
        if cells[0] in {"Invariant", ""} or set(cells[0]) <= set("-: "):
            continue

        found.append((cells[0], cells[2], cells[3]))
    return found


def state_of(cell: str) -> str | None:
    """The legend state a State cell declares, or None when it names none.

    A State cell is a state in bold followed, often, by the reason: the
    qualification is the point of a Partly proved row, so it is read as prose
    and only the leading claim is matched.
    """
    match = re.match(r"\*\*([^*]+)\*\*", cell.strip())
    return match.group(1).strip() if match else None


def resolves(token: str, projects: dict[str, set[str]]) -> bool:
    """Whether a Proof citation names something on disk.

    Accepted shapes:

        InterruptionTests/SequenceRollbackTests   a class in a test project
        Hosts.Tests/*                             several classes there
        eng/recovery-drill.sh                     a drill script
        Repository.Packing/BlobWriter             a source type, for context
    """
    if "/" not in token:
        return False

    project, _, name = token.rpartition("/")

    if project == "eng" or token.startswith("eng/"):
        return (ROOT / token).is_file()

    if project in projects:
        return name == "*" or name in projects[project]

    # A citation may name production code -- a row explaining WHERE the
    # invariant is broken reads better naming the file than describing it.
    source = ROOT / "src" / f"FallbackPlan.{project.split('/')[0]}"
    if source.is_dir():
        tail = "/".join(token.split("/")[1:])
        pattern = tail if tail.endswith((".cs", "*")) else f"{tail}.cs"
        return any(source.glob(pattern)) or any(source.glob(f"**/{pattern}"))

    return False


def main() -> int:
    failures: list[str] = []

    if not DOC.is_file():
        print(f"missing {DOC.relative_to(ROOT)}", file=sys.stderr)
        return 1

    text = DOC.read_text(encoding="utf-8")
    obligations = rows(text)
    projects = test_classes()

    if len(obligations) < MINIMUM_ROWS:
        failures.append(
            f"only {len(obligations)} obligation row(s) parsed, below the floor of {MINIMUM_ROWS} -- "
            "rows were lost, or the table shape changed under the parser")

    for invariant, proof, state_cell in obligations:
        short = invariant if len(invariant) <= 60 else invariant[:57] + "..."
        state = state_of(state_cell)

        if state not in STATES:
            failures.append(f"{short!r}: state {state!r} is not in the legend")
            continue

        citations = CODE.findall(proof)
        cited_anything = bool(citations) or not NO_PROOF.match(proof)

        if not cited_anything and state not in WITHOUT_PROOF:
            failures.append(f"{short!r}: claims {state} and names no proof")

        if cited_anything and not citations and state not in WITHOUT_PROOF:
            failures.append(f"{short!r}: claims {state} with prose where a citation belongs")

        for citation in citations:
            if not resolves(citation, projects):
                failures.append(f"{short!r}: cites `{citation}`, which is not on disk")

        if state == "Unproved" and not REQUIREMENT.search(state_cell + invariant):
            failures.append(
                f"{short!r}: is Unproved and cites no requirement id, so the gap is not countable")

    print(f"obligation rows    : {len(obligations)}")
    print(f"proof citations    : {sum(len(CODE.findall(p)) for _, p, _ in obligations)}")
    for state in sorted(STATES):
        count = sum(1 for _, _, cell in obligations if state_of(cell) == state)
        print(f"  {state:<18}: {count}")

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
