# ADR-0024 — Include/exclude rule dialect (rules-v1)

**Status:** Accepted
**Date:** 2026-08
**Requirements:** NFR-COMP-004, NFR-PORT-003
**Related:** [ADR-0021](0021-consume-bodu-via-committed-package-feed.md), [specification 06 §7.1](../../specifications/repository-format/06-manifests.md#71-rule-dialect-rules-v1), [external/packages/README.md](../../external/packages/README.md)

---

## Context

The policy manifest carries `include_rules` and `exclude_rules` (06 §7
keys 8 and 9) as arrays of text strings, and until now nothing anywhere
defined what those strings *mean*. That was survivable in phase 0 — no
component evaluates a rule — but Phase 1's streaming scanner both evaluates
them and persists them into signed, immutable policy manifests. The moment
the first rule string is published, its dialect is de-facto format surface:
two implementations reading the same snapshot must agree on what
`**/.cache/**` matched at capture time, or "what did this backup exclude?"
has no portable answer (NFR-COMP-004). A dialect adopted silently from
whatever library happens to be linked is exactly the kind of decision this
project refuses to make silently.

The dialect must therefore be pinned *before* Phase 1, in the
specification, with executable conformance vectors — not inherited from an
implementation.

## Decision

Format v1 include/exclude rules are **rules-v1**, defined normatively in
[specification 06 §7.1](../../specifications/repository-format/06-manifests.md#71-rule-dialect-rules-v1)
and frozen by the `path-rules.json` conformance vector group. In summary:

### 1 Two rule forms

A rule beginning `re:` is a **regex rule** under a pinned portable subset;
any other rule is a **glob rule**. There is no glob escape mechanism — a
pattern that needs to match literal `*` or `?` (or a literal path beginning
`re:`) is written as a regex rule.

### 2 The glob form

Matched against the entry's `/`-separated, NFC-normalised relative path
within the backup-set root. `*` matches zero or more characters within one
component; `?` matches exactly one non-`/` character; `**` matches zero or
more whole components and is valid only as a complete component (`a**b` is
an invalid rule). No character classes, no braces, no backslash escapes. A
rule containing `/` is anchored at the backup-set root; a rule without `/`
is implicitly `**/<rule>`.

### 3 The regex subset

Implicitly anchored at both ends (the whole relative path must match;
`^` and `$` are forbidden). Permitted: literals, `.`, `[...]`/`[^...]` with
ranges, `|`, `(...)`, `*`, `+`, `?`, `{m}`, `{m,n}`, and `\` escaping of
metacharacters. Forbidden: backreferences, lookaround, shorthand classes
(`\d`, `\w`, `\s`), inline flags, named groups. The subset is chosen to
mean the same thing in .NET, Python, RE2, and every mainstream engine, and
to be implementable in linear time.

### 4 Evaluation

Exclude wins; there is no rule ordering and no negation. A path is
*excluded* iff it or any ancestor matches an exclude rule (subtree
pruning). A path is *captured* iff it is not excluded and either the
include list is empty, or it or an ancestor matches an include rule.
A directory that lies on the path to a potential include match may be
descended even though it is not itself captured. Case sensitivity follows
the source filesystem as the snapshot records it (06 §6 key 12), using
Unicode simple case folding when insensitive.

### 5 Enforcement and versioning

A writer MUST NOT publish a policy manifest containing an invalid rule; a
reader treats stored rules as informational and never fails decoding on
them. In format v1 the dialect is always rules-v1 — no dialect field
exists, and a future dialect requires a new policy-manifest key in a future
format revision, under the same discipline as every profile (00 §3).

### 6 Implementation

The reference matcher lives twice, deliberately: a stdlib-only pure-Python
implementation inside `conformance/generate.py` (which produces
`path-rules.json`, `independently_derived: true`), and a dependency-free C#
implementation in `FallbackPlan.Domain` (`PathRules.cs`, compiled to
non-backtracking `System.Text.RegularExpressions`). The conformance suite
runs every vector case through the C# matcher, so the two implementations
are held in agreement on every committed case. `Bodu.Text.Filter` — the
candidate engine recorded in the package-feed README — was **not** adopted
for the normative path: the dialect's exact `**` and anchoring semantics
are a page of code to implement directly, and adopting an external engine
would invert the authority (the library defining the dialect instead of
the specification). It remains a legitimate Phase-1 choice for the scanner
*if* it is verified against `path-rules.json` first.

## Consequences

**Positive**

- Rule strings in signed manifests have one portable meaning, checkable by
  an independent implementation from the vectors alone — before the first
  scanner exists.
- The glob form covers the overwhelmingly common cases (`*.log`, `.cache`,
  `build/**`) with no surprises; the `re:` hatch absorbs everything else
  without ever extending the glob grammar.
- No format change: keys 8/9 already carry plain text strings, and absent
  rules already mean "capture everything".

**Negative**

- Users familiar with gitignore will miss negation (`!`) and ordering;
  achieving "exclude a directory except one child" requires include rules
  instead. This is a real expressiveness loss, accepted for specifiability.
- The regex subset must be policed by our own validator — passing rules
  straight to a host regex engine would silently widen the dialect.

## Alternatives considered

**gitignore-compatible.** Familiar, but its semantics (negation
re-inclusion, directory short-circuit against re-included children,
anchoring subtleties) are defined by one implementation's behaviour rather
than a specification, and faithfully freezing them as format surface is a
research project. Rejected.

**Glob only, no regex.** Simplest to specify, but leaves literal-`*` paths
and genuinely irregular patterns unexpressible forever, and retrofitting an
escape hatch later would be a dialect revision — the expensive kind.
Rejected in favour of carrying the hatch from day one.

**Full host regex (.NET/PCRE).** Maximally expressive and maximally
unportable: lookbehind, backreferences, and flag semantics differ across
engines and versions, and catastrophic backtracking becomes an input-driven
denial of service inside a backup agent. Rejected.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Dialect pinned with conformance vectors and dual reference implementations, ahead of Phase 1's scanner |
