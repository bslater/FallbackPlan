# ADR-0019 — Third-party dependency policy, and the Bodu adoption

**Status:** Accepted
**Date:** 2026-08
**Requirements:** NFR-SEC-002, NFR-SUP-001..002, NFR-PORT-001
**Related:** [ADR-0001](0001-licence-and-contribution-model.md), [ADR-0004](0004-segment-hash-function.md), [ADR-0005](0005-aead-suite-and-nonce-construction.md), [`../../specifications/repository-format/03-keys.md` §6.1](../../specifications/repository-format/03-keys.md#61-where-each-primitive-comes-from)

---

## Context

Two questions arrived together and only one of them was about a library.

The narrow question: the project maintainer's own package set, [Bodu](https://github.com/bslater/bodu), should be used "where applicable" across general utilities, storage/IO abstractions, and hashing/cryptography.

The broader question the evaluation exposed: **this project had no stated rule for when a third-party dependency may enter the format-critical path.** Specification 03 §1 says "use audited platform primitives or established libraries — write no primitive ourselves", which reads as a complete policy until you discover it is not satisfiable. The evaluation established, by probing the runtime rather than by recollection, that .NET 10 provides SHA-256, HMAC-SHA256, HKDF, AES-256-GCM and ChaCha20-Poly1305 — and provides **neither Argon2id nor XChaCha20-Poly1305**, both of which the format requires.

So the format already depended on two unaudited third-party primitives. That was true before this ADR and was written down nowhere. Adopting Bodu did not create the exposure; it made it visible.

## Decision

### 1. Dependencies are classified by blast radius, not by popularity

| Tier | Definition | Bar to enter |
|------|-----------|--------------|
| **Format-critical** | Anything whose output is committed to durable storage or determines whether stored bytes decrypt: `Repository.Crypto`, `Repository.Format`, `Repository.Packing`, `Repository.Index`, and everything the standalone recovery tool links | All five gates in §2 |
| **Operational** | Agent, CLI, API, Web, Desktop, providers, catalogue | Licence compatible, no unpatched advisory, `net10.0`, warnings clean |
| **Test-only** | Never shipped | Licence compatible only |

The distinction is that a defect in a format-critical dependency is **unrecoverable**: it is already in the user's stored bytes, possibly for years, possibly on media the user no longer controls. A defect anywhere else is a bug fix.

### 2. Gates for a format-critical dependency

A dependency may enter the format-critical path only if **all five** hold, each with recorded evidence rather than an assertion:

1. **It does not reimplement a primitive the platform already provides.** Not a judgement about code quality — the platform's primitives carry an audit history no small library can match, and using two implementations of one primitive doubles the surface for no gain.
2. **It introduces no native dependency into `Repository.Format` or the recovery tool.** This is the same constraint that chose SHA-256 over the faster BLAKE3 ([ADR-0004](0004-segment-hash-function.md)); it exists so the recovery tool runs on a clean machine on any platform (NFR-PORT-001).
3. **It reproduces the committed conformance vectors bit-for-bit.** Adoption is testable, not a matter of opinion.
4. **It does not change which algorithms are permitted.** No dependency may introduce an algorithm choice the security requirements do not already admit (NFR-SEC-002).
5. **Its licence is compatible with every option still open in [ADR-0001](0001-licence-and-contribution-model.md).** Taking a dependency that narrows an undecided licence decides it by the back door.

### 3. Where the platform provides nothing, the gap is named

Gate 1 is unsatisfiable for Argon2id and XChaCha20-Poly1305. Where that happens the rule is not to relax the gate quietly but to **record the exception, contain it, and compensate for it**:

- the primitive is named in [specification 03 §6.1](../../specifications/repository-format/03-keys.md#61-where-each-primitive-comes-from), with its source and status, so an implementer reads the exposure in the normative text rather than inferring it from a package manifest;
- it is confined to `Repository.Crypto` and may not be referenced from any other project, enforced by `ArchitectureTests` rather than by convention;
- where a second independent implementation exists, CI cross-verifies against it on every run;
- where none exists, that is written down as a gap rather than left unstated;
- the external cryptographic review required before the first beta MUST cover these primitives specifically.

### 4. The Bodu adoption, gate by gate

| Area | Bodu package | Outcome |
|------|--------------|---------|
| General utilities | `Bodu.Core` | **Adopted** — referenced from `Repository.Packing`. Managed, no native dependency, no primitive reimplementation. |
| Cryptography | `Bodu.Security.Cryptography` | **Adopted, narrowly** — for Argon2id only, in `Repository.Crypto` only. Everything the platform supplies continues to come from the platform. |
| Text encodings | `Bodu.Text.Encoding` | **Adopted, narrowly** — for the base32 rendering of identifiers (spec 02 §5), in `FallbackPlan.Domain`, behind a strict adapter that pins the format's lowercase-unpadded-bijective contract (case and length rules ours; canonical trailing-bit enforcement Bodu's). The platform provides no base32, so gate 1 holds; the committed `identifiers.json` base32 renderings are the conformance evidence for gate 3. Hex stays on platform `Convert` — gate 1 forbids duplicating it. |
| Storage / IO abstractions | `Bodu.IO.Compound`, `Bodu.IO.Hashing` | **Not adopted.** `Bodu.IO.Compound` implements the OLE Compound File format, which is unrelated to `IObjectStore`. `Bodu.IO.Hashing` is non-cryptographic hashing, which the format-critical path must not use. Neither is a fit; forcing one would have been adoption for its own sake. |

**A reference is not a licence to use everything behind it.** `Bodu.Security.Cryptography` is a broad package: it also carries its own HKDF, BLAKE2/BLAKE3, Whirlpool, Tiger, Twofish, Skipjack, Ed25519, ML-DSA and more. Several of those duplicate primitives .NET already provides, and gate 1 forbids using them here — a duplicate implementation of an in-box primitive is exactly what the gate exists to keep out of stored bytes.

Referencing the assembly makes all of it *reachable*, which is why the containment is enforced by a test rather than by intent. `Only_Repository_Crypto_may_reference_third_party_cryptography` keeps the reachable surface inside one project; **which** of it may be called is this ADR's rule, and code review is what enforces that. Anything beyond Argon2id requires amending this record.

**Evidence for the gates:**

1. *No reimplementation of a platform primitive.* Satisfied by scope rather than by the package's contents: only Argon2id is called, and the platform has no Argon2id. Every primitive .NET does provide continues to come from .NET, including where the package offers its own.
2. *No native dependency.* `Bodu.Security.Cryptography` is managed. `Repository.Format` does not reference it at all — the recovery tool's closure is unchanged.
3. *Vectors reproduce.* `generate.py --check` passes unchanged; `CryptographicPrimitiveTests` recomputes every vector against in-box primitives.
4. *No algorithm change.* The approved suite table in specification 03 §6 is unchanged.
5. *Licence.* Bodu is **MIT**, which is compatible with every option in ADR-0001 — permissive, weak copyleft, and strong copyleft alike. The dependency therefore does not constrain the deferred licence decision, and Q1 stays open on its own merits.

### 5. Cross-verification, and why Konscious stayed

Konscious.Security.Cryptography was removed as a production dependency and **retained as a test-only one**, as an independent oracle for Argon2id ([`Argon2idCrossVerificationTests`](../../tests/FallbackPlan.Repository.ConformanceTests/Argon2idCrossVerificationTests.cs)). The two agree bit-for-bit across the parameter range including the mandated minimums.

They disagree at exactly one input boundary: Konscious refuses an empty password, Bodu accepts one. RFC 9106 permits zero-length, so Bodu is not wrong — but the disagreement showed that **the specification was relying on an accident of which library was linked** to reject an empty passphrase. That is now stated as a writer obligation in [specification 03 §2.1](../../specifications/repository-format/03-keys.md#21-the-passphrase-is-constrained-too-and-the-primitive-will-not-do-it-for-you).

Finding that is the whole argument for keeping the second implementation.

### 6. Vendoring mechanics

> **Superseded by [ADR-0021](0021-consume-bodu-via-committed-package-feed.md).** Bodu is now consumed as prebuilt packages from the committed `external/packages` feed; the submodule described below no longer exists. This section and Amendment 1's gitlink discussion are retained as the record of the earlier mechanics. The dependency policy in §§1–5 is unaffected.

Bodu enters as a **git submodule** at `external/bodu` with project references, not as NuGet packages, because it is not published to nuget.org.

- The submodule is pinned to a commit; a submodule bump is a reviewable change like any other.
- CI checks out with `submodules: recursive`.
- The warnings-as-errors gate is **scoped to exclude `/external/`**. Vendored code is held to its own repository's standards, not ours; a submodule bump must not be able to fail our build on a style rule. Our own code remains at zero warnings, and the gate would still fail on a warning from `src/` or `tests/`.
- `eng/check-links.py` excludes `/external/` for the same reason: the submodule carries its own documentation conventions.

## Consequences

**Positive**

- The format's actual cryptographic exposure is written in the normative specification instead of being discoverable only from a package manifest.
- Argon2id is now cross-verified on every CI run against an independent implementation; it previously had one implementation and no check.
- The rule for the next dependency exists before it is needed, decided on principle rather than in the presence of a specific library someone wants to use.
- Bodu's MIT licence leaves ADR-0001 genuinely open.

**Negative**

- Two format-critical primitives are outside the platform's audit posture. This is a real and permanent risk; it is bounded by containment and cross-verification, not removed by them.
- A submodule is more friction than a package: contributors must clone recursively, and the pinned commit needs periodic attention.
- Excluding `/external/` from the warning gate means a genuine defect in vendored code will not fail our build. Accepted: the alternative is our build failing on someone else's style rule, which trains people to ignore the gate.
- XChaCha20-Poly1305 remains cross-verified by nothing. Recorded in [`conformance/README.md`](../../specifications/repository-format/conformance/README.md) as an open gap rather than glossed.

**Neutral**

- Two of the three areas named in the original request did not produce an adoption. "Where applicable" was read as a genuine conditional, and reporting the non-fit is more useful than a reference that exists to satisfy the request.

## Alternatives considered

**Adopt Bodu across all three named areas.** Rejected. `Bodu.IO.Compound` is a document-container format, not a storage abstraction, and `Bodu.IO.Hashing` is non-cryptographic. Routing `IObjectStore` through either would trade a deliberately shaped contract ([ADR-0012](0012-storage-provider-contract.md)) for a coincidence of naming.

**Keep Konscious in production and skip Bodu for crypto entirely.** Considered seriously — Konscious is widely used and the incumbent. Rejected because it leaves exactly one implementation with no oracle. Swapping which library ships and keeping the other as the check gives a real cross-verification for the cost of one test-only dependency.

**Write Argon2id and XChaCha20-Poly1305 ourselves.** Rejected without hesitation. Specification 03 §1 forbids it, and a self-written KDF standing between a stolen repository and its plaintext is the defect class this project cannot recover from.

**Drop XChaCha20-Poly1305 from the format.** Genuinely open. `aes-256-gcm-v1` alone is sufficient, and specification 03 §6.1 already permits an implementer to omit the profile. Not decided here because the profile costs nothing while unused, and the format freeze gate is the right point to decide whether an unverifiable profile should ship at all.

**Vendor Bodu by copying sources.** Rejected. It severs upstream history, makes provenance unauditable, and turns every upstream fix into a manual merge.

## Amendment 1 — the pin is the gitlink, and restore is part of the build

Two clarifications from a supply-chain review of the scaffold, recorded here because this ADR is where the policy lives:

**What pins the submodule.** `.gitmodules` names a branch; that is *advisory* — it tells `git submodule update --remote` where to look. The actual pin is the **gitlink SHA committed in this repository's tree**, which no upstream push can move. A submodule bump is therefore always a reviewable commit here, never something that happens to the build. No CI check re-verifies this because git itself enforces it; a check would restate the guarantee without strengthening it.

**Restore inputs are controlled, not inherited.** The review found the gates this ADR describes resting on defaults: `NuGetAudit` was an unpinned SDK behaviour, one committed "transitive pin" named a package the resolved graph no longer contains, and restore results depended on feed state and machine configuration. Now: audit mode is set explicitly (`NuGetAudit`/`NuGetAuditMode=all`/`NuGetAuditLevel=low`), every project commits a `packages.lock.json` and CI restores in locked mode, `nuget.config` clears inherited sources and maps which source may supply which packages, transitive pins mirror the graph that actually resolves, and CI runs an explicit vulnerable-package gate rather than relying on restore warnings alone (NFR-SUP-002, groundwork for NFR-SUP-003). The submodule's own `Directory.Build.props` shields Bodu projects from the lockfile property, which is what makes it safe to set repo-wide.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Adopted with `Bodu.Core` in packing and `Bodu.Security.Cryptography` confined to crypto |
| 2026-08 | Accepted (amended) | Amendment 1: gitlink-is-the-pin clarified; restore inputs pinned (explicit NuGetAudit, lockfiles + locked-mode CI, source mapping, graph-accurate transitive pins, explicit vulnerable-package gate) |
| 2026-08 | Accepted (amended) | Amended by [ADR-0021](0021-consume-bodu-via-committed-package-feed.md): submodule replaced by the committed `external/packages` feed; dependency-tier policy, five gates, and containment unchanged |
