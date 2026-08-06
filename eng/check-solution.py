"""Every csproj under src/ and tests/ must appear in FallbackPlan.slnx.

A project on disk but absent from the solution is never built, never tested,
and never architecture-checked -- and nothing else notices, because every gate
in CI operates on the solution. This is the check that turns that silence into
a failure.

The reverse direction is checked too: a solution entry whose file is gone is a
broken reference someone will trip over later.

Vendored code under external/ is exempt -- those projects are built as
dependencies via ProjectReference and belong to their own repository's
solution, not this one.
"""

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
SOLUTION = ROOT / "FallbackPlan.slnx"

listed = set(re.findall(r'Project Path="([^"]+\.csproj)"', SOLUTION.read_text(encoding="utf-8")))

on_disk = set()
for tree in ("src", "tests"):
    for csproj in (ROOT / tree).rglob("*.csproj"):
        rel = csproj.relative_to(ROOT).as_posix()
        if "/bin/" in rel or "/obj/" in rel:
            continue
        on_disk.add(rel)

missing_from_solution = sorted(on_disk - listed)
missing_from_disk = sorted(listed - on_disk)

for path in missing_from_solution:
    print(f"FAIL not in FallbackPlan.slnx: {path}", file=sys.stderr)
for path in missing_from_disk:
    print(f"FAIL listed in FallbackPlan.slnx but not on disk: {path}", file=sys.stderr)

print(f"projects on disk: {len(on_disk)}  listed in solution: {len(listed)}")
if missing_from_solution or missing_from_disk:
    sys.exit(1)
print("all checks passed")
