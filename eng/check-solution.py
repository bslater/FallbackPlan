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

Listing a project is not enough: a solution entry may also carry a per-
configuration <Build ... Project="false" /> override, and one of those is just
as effective at not building something as leaving it out. Worse, it fails
quietly and at a distance. A project excluded from a configuration is skipped
in a solution build of that configuration, so every ProjectReference to it
stops resolving -- and, less obviously, stops contributing the transitive
package assemblies that reached the output only through it. That is how
`Microsoft.Extensions.Logging` went missing from the agent's output while the
agent still compiled: it is a CentralTransitive dependency arriving solely via
FallbackPlan.Diagnostics, which had been unchecked for Debug. The symptom was a
FileNotFoundException at run time, days from the commit that caused it.

These overrides are written by the IDE's configuration manager rather than
chosen by anyone, and they had accumulated across six separate commits before
anything noticed -- because README and CONTRIBUTING both build Release, and so
does CI. So this refuses all of them, in every configuration. If a project ever
genuinely should not build somewhere, that is a decision worth an ADR and an
explicit exemption here, not a checkbox.
"""

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
SOLUTION = ROOT / "FallbackPlan.slnx"

solution_text = SOLUTION.read_text(encoding="utf-8")
listed = set(re.findall(r'Project Path="([^"]+\.csproj)"', solution_text))

# <Project Path="..."> ... <Build Solution="Debug|*" Project="false" /> ... </Project>
# The lookahead skips self-closing entries: without it, `<Project Path="x" />`
# reads as an opening tag and swallows the rest of the file, so the failure
# names the first project in the solution rather than the guilty one.
excluded = re.findall(
    r'<Project Path="([^"]+\.csproj)"(?![^>]*/>)[^>]*>(.*?)</Project>', solution_text, re.DOTALL)
not_built = [
    (path, configuration)
    for path, body in excluded
    for configuration in re.findall(r'<Build Solution="([^"]*)" Project="false"\s*/>', body)
]

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

for path, configuration in sorted(not_built):
    print(
        f"FAIL excluded from the '{configuration}' build in FallbackPlan.slnx: {path}"
        " -- every project builds in every configuration; remove the <Build> override",
        file=sys.stderr)

print(
    f"projects on disk: {len(on_disk)}  listed in solution: {len(listed)}"
    f"  build exclusions: {len(not_built)}")
if missing_from_solution or missing_from_disk or not_built:
    sys.exit(1)
print("all checks passed")
