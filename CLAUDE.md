# Working in this repository

Short pointers to the conventions this repository enforces. The documents
they point at are the authority; this file just stops you learning them by
being refused.

## Build and test

```bash
dotnet build FallbackPlan.slnx -c Release
dotnet test  FallbackPlan.slnx -c Release
```

- SDK pinned in `global.json`. **Warnings are errors** — analyzer findings
  (CA1822, CA1873, CA2263, …) fail the build, including in tests.
- MSTest ([ADR-0032](docs/adr/0032-mstest-as-the-test-framework.md)). House
  idioms: `Assert.ContainsSingle(collection)`, `Assert.HasCount(n, c)`,
  `Assert.Contains(substring, value, StringComparison.Ordinal)`,
  `Assert.IsInstanceOfType<T>(value, out var typed)`,
  `Assert.ThrowsExactly<T>(...)`.
- **Tests first.** Fixes and features start with named failing tests; a
  compile error against a not-yet-written API counts as the red.

## Generated code and checkers

- After editing any `.resx`: `python3 eng/generate-resources.py` — the
  `Strings.g.cs` accessors are generated and CI checks they match
  ([ADR-0031](docs/adr/0031-exception-messages-are-resources.md)).
- Before committing doc or test-comment changes, run all three:

  ```bash
  python3 eng/check-requirements.py    # requirement IDs, traceability, test citations
  python3 eng/check-adr-status.py      # ADR table, status legend, code citations
  python3 eng/check-links.py           # every relative link and anchor resolves
  ```

  `check-requirements.py --drift` / `--audit` are advisory extras worth
  reading when touching tests or the matrix.

## The documentation culture

- Decisions live in `docs/adr/`. A superseded or amended ADR is never
  edited silently: it gains a dated **amendment section**, a scoped
  blockquote at the affected section, and/or a **status-history row**
  pointing at the newer record (see ADR-0019 §6 or ADR-0034 for the shape).
  Every ADR must appear in `docs/README.md`'s index and in
  `docs/implementation-status.md`'s table, whose legend states are
  **Built / Partly built / Specified only / Applied**.
- Backticked tokens in implementation-status and ADR status rows are parsed
  as code citations by `check-adr-status.py` — they must name real
  projects/files/classes, so don't backtick ordinary words there.
- **Requirements** (`docs/requirements/functional.md`, `non-functional.md`)
  are numbered rows with acceptance criteria; behavior changes update or add
  rows (marked `**[new]**` / `**[amended]**`). The traceability matrix's
  Test column is fed by the requirement IDs **test class doc comments
  cite** — a test that names no FR/NFR id is invisible to the tooling, so
  new suites should cite the requirements they establish (and use the exact
  phrase "does not establish FR-…" to disclaim one).
- Architecture docs (`docs/architecture/00`–`12`) each open with a
  **Built:** line; keep it true when landing structural change. Document 01
  is the normative glossary — new concepts get an entry there and one name
  everywhere.
- The client↔service contract is defined by `FallbackPlan.Api`
  (`ContractVersion.cs` carries the per-version changelog); bump the minor
  for additive wire changes, update the pin test in
  `Api.Tests/ConfigurationContractTests`, and mirror the entry in
  [specifications/command-contract](specifications/command-contract/README.md).

## Style

- Commit messages are long-form narrative: what changed, why, and what the
  reader of `git log` needs that the diff doesn't say. No model identifiers
  in committed artifacts.
- Code comments state constraints the code can't; they never narrate the
  diff. Third-party backup products are never named anywhere in the
  repository ([docs/naming-and-attribution.md](docs/naming-and-attribution.md)).
