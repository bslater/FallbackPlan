# ADR-0051 — A local destination lives on its own drive

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-DEST-017, FR-SNP-007, NFR-OPS-002
**Related:** [ADR-0018](0018-replica-failure-domains.md), [ADR-0035](0035-destination-fitness.md), [architecture 10 §1.1](../architecture/10-observability.md#11-states-must-be-distinguishable), [PT-8](../review/2026-08-fix-pressure-test.md#pt-8--protected-does-not-require-a-replica-outside-the-sources-failure-domain)

---

## Context

A locally stored backup is not wrong — so long as it does not live on the
drive whose files it protects. That separation is the primary check for a
local destination, and until this record nothing enforced it: a destination
on the source's own volume saved cleanly and earned a permanent warning,
while a destination on a second drive was lumped with it — capped at
`captured` under ADR-0018's machine-boundary reading of PT-8, reading
**Needs attention** at the glance forever, however correctly it was placed.

The owner's direction, recorded here, resolves both halves: make drive
separation the **condition of choosing** a local destination, and let a
destination that satisfies it **earn protection**.

## Decision

1. **The condition.** Binding a local-path destination to a backup set
   requires it to sit on a **different volume** than every one of the set's
   roots — and, where the platform can name physical drives, on a
   **different drive** (two partitions of one disk fail together). The
   command boundary refuses the choosing with both paths named
   (`LocalDestinationPlacement.Judge`; `Agent/ServiceCommandHandler`
   enforces it on new bindings, on root changes, and on a referenced
   destination's path edit).

   > **2026-10 ([ADR-0037](0037-configuration-over-the-command-contract.md)
   > Amendment 2).** An editor is told before it saves. A draft that names
   > its set (contract 1.49) is answered with the refusal its save would
   > give, word for word, judged as the save judges it. So the console's
   > destinations step says it while the destination is being chosen. A
   > draft that names no set is not judged, because a standing binding could
   > not be told from a new one.

   > **2026-10 ([Amendment 2](#amendment-2-2026-10--a-debug-build-lets-the-binding-stand-and-says-so)).**
   > A Debug build lets such a binding stand instead, and says what a
   > Release build refuses and why. Every build CI makes, and every build
   > that ships, is a Release build and refuses.

2. **"Where possible", honestly.** Volume separation is the hard core of
   the condition — judged by volume identity via the nearest existing
   ancestor. The physical-drive refinement applies only where the platform
   can answer (`Filesystem.Local/PhysicalDisk`: Linux sysfs today — a
   partition resolves to its parent disk; anonymous and multi-device
   volumes answer null). An unknowable answer never refuses on a guess:
   the status derivation stays conservative for it instead.

3. **Only the choosing is gated.** A configuration written before this
   record keeps loading and keeps its standing bindings (ADR-0035's
   posture: report, never refuse to load); its same-volume placement keeps
   its status warning. An edit that touches neither the set's roots nor the
   binding is not re-judged.

4. **The protection boundary moves from machine to volume.** `protected`
   now asks: *if the drive the files live on is destroyed, does a copy
   survive?* A second drive answers yes and earns `protected` — and, when
   verified, `verified` — reading **Healthy** at the console's glance. Only
   `same-volume` still caps at `captured`. The residual risk of the best
   protecting copy is always named beside the badge ("survives drive
   failure, not fire, theft, or losing the machine"; same-site keeps its
   site-loss note), so the distinction PT-8 exists for — a copy that dies
   with its source reading healthy — remains impossible: the copy that
   earns Healthy provably does not die with the source's drive.

## Consequences

**Positive** — the false-confidence case (same disk) can no longer be
chosen at all, which is stronger than any badge; a correctly placed local
backup stops nagging; status and choosing judge drive-sharing through one
probe, so they cannot disagree.

**Negative** — a single-drive machine cannot choose a local destination
(an external drive, a peer, or a cloud kind is the answer — which is the
truth of that machine's options, stated at save time rather than
discovered at restore time); the machine-loss residue of a second internal
drive is a warning rather than a badge tier, resting on the named warning
staying in front of the owner.

**Neutral** — the failure-domain ladder (ADR-0018) is unchanged as data;
what changed is which rung earns protection. Declared domains still win
over derivation.

## Alternatives considered

- **Warn-and-confirm instead of refuse**: rejected — the whole class of
  placement exists to be caught before the first byte lands; a confirmed
  same-drive backup is still a backup that dies with its files.
- **A distinct badge tier for same-machine** (between captured and
  protected): rejected as vocabulary growth the glance layer would fold
  away again; the warning carries the residue.
- **Refusing unknowable topologies**: rejected — a network mount or
  layered volume would be refused on a guess; conservatism belongs to the
  status derivation, not the gate.

## Amendment 1 (2026-10) — the Windows probe called every path volume 0

The decision is unchanged. What was wrong was the probe it judges by, on
Windows.

There the volume a path sits on is the serial number
`GetFileInformationByHandle` reports, read through a managed copy of
`BY_HANDLE_FILE_INFORMATION`. That copy declared the struct's three
`FILETIME`s as longs. C# aligns a long to eight bytes and Windows aligns a
`FILETIME` to four, so every field after the first was read four bytes late.
The serial number read as the high half of the file's size, which is zero for
every directory. **Every path on Windows was volume 0**, so every local
destination shared a volume with every root, and choosing one was refused,
including a destination on an external drive. The refusal said "shares a
volume", which was false, and named both paths, which were right. The status
derivation reads the same probe, so it treated every standing local binding
on Windows as sharing its source's volume.

Nothing caught it. Every service suite that binds a local destination
overrides the probe so that its fixture paths can pretend to sit on two
drives, and the judgement's own suite is pure. No test had let the real probe
answer on Windows.

The fix gives the struct its native layout: `FILETIME` as two four-byte
halves, 52 bytes in all.

- `Filesystem.Tests/WindowsFileIdentityTests` pins that layout on every
  platform. On Windows it checks the reported serial against
  `GetVolumeInformationW`, and the link count against a file before and
  after `CreateHardLinkW`.
- `Hosts.Tests/LocalPlacementRealVolumeTests` runs the case that was
  reported, through the service with no override. A destination on another
  volume is accepted, and one beside its root is refused.

The same misread gave capture two wrong fields, and the fix corrects both:

- **The link count read part of the file index**, so every Windows file
  looked hard-linked and carried a hard-link group of its own.
- **The file identity was shifted.**

Both now read true. The first backup of an existing Windows set after this
change therefore finds every file's identity changed. It reads each file
again rather than reusing the prior version, and writes new manifests without
the stray groups. The content deduplicates against what is stored, so it
costs a read and not the space.

## Amendment 2 (2026-10) — a Debug build lets the binding stand, and says so

The condition is unchanged for every build that ships. A Release build
refuses exactly as §1 says, and every build CI makes, and every build a
person installs, is a Release build.

A Debug build allows the binding. A developer working on this product
usually has one disk, and §1 refused every backup set they tried to create
on it. Nothing past the destinations step could be run end to end without a
second drive, or a test harness's override of the volume probe. The
allowance is the build's: `Agent/BuildConfiguration` answers from the
compilation symbol. A test harness can set the answer either way through
`ServiceOptions.SameDrivePlacementOverride`, so a suite says which build it
means rather than inheriting the one it runs in.

What it relaxes, and what it does not:

- **The choosing, and nothing else.** Wherever §1 refuses, a Debug build
  lets the binding stand: a set's save, its draft (ADR-0037 Amendment 2), a
  referenced destination's path edit, and an adoption. The volume probe
  still answers truly. So status still derives `captured`, never
  `protected`, for such a binding (§4), and the draft's durability warning
  still says so. The allowance makes the binding possible. It does not make
  it safe, and nothing says it does.
- **Never silently.** Each answer says so. It names the conflict the refusal
  would name, says that only a Debug build allows it, and gives the reason a
  Release build refuses. That goes in the save's answer, the draft's
  warnings, a path edit's answer and an adoption's lines. The console shows
  all four:
  - the set editor's report;
  - the destinations step's advisories;
  - the adoption's report;
  - the destination editor's report. Until now that editor dropped whatever a
    save answered with, which already lost the line saying a relative path
    was resolved.
- **Both halves of §1.** Two partitions of one disk are as common on a
  developer's machine as one volume, so the physical-drive refusal is
  relaxed and said the same way.

This is not the warn-and-confirm alternative rejected below. That
alternative was rejected because a person protecting their files must not
be able to confirm their way into a backup that dies with them. A Release
build keeps the rejection whole. A Debug build protects nobody's files: it
is what a developer runs to see the product work, on the one disk they
have.

CI builds Release only, so it never compiles the Debug branch of
`Agent/BuildConfiguration`. That branch is one property, and building the
Agent and its tests in Debug was part of this change's validation. The pin
runs on every build. `Hosts.Tests/LocalPlacementTests` saves a same-volume
binding with no override and expects the answer the Agent assembly's own
configuration attribute calls for. On CI, that pins the property that
matters: a Release build refuses.

The tests:

- `Hosts.Tests/LocalPlacementTests`:
  - the build's own answer, with no override;
  - the allowance, on one volume and on one drive;
  - the draft's warning, in the save's words, beside a durability warning
    that still reads the real volumes;
  - a path edit.
- `Hosts.Tests/DestinationAdoptionTests`: adoption's refusal, which had no
  test before, and its allowance.
- `Web.DomTests/ConfigEditingDomTests`: the destination editor shows what the
  service said.

The refusal tests now set the override to false. Without it, a Debug run of
the suite (an IDE's test explorer, or a bare `dotnet test`) would allow what
they expect refused. Their assertions are unchanged.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | The owner's direction: drive separation as the condition of choosing a local destination, and the protection boundary moved from machine to volume — `Application/LocalDestinationPlacement`, `Filesystem.Local/PhysicalDisk`, the upsert guards, and the deriver's gates, pinned by `Application.Tests/LocalDestinationPlacementTests`, `Hosts.Tests/LocalPlacementTests` and the flipped deriver suite |
| 2026-10 | Amended | Amendment 1: the Windows volume probe read the wrong field of `BY_HANDLE_FILE_INFORMATION` and called every path volume 0, so every local destination was refused. The struct has its native layout again, held by `Filesystem.Tests/WindowsFileIdentityTests` and `Hosts.Tests/LocalPlacementRealVolumeTests` |
| 2026-10 | Amended | §1's refusal is said in the draft too, under ADR-0037 Amendment 2: a draft that names its set is answered with the refusal its save would give, from the same judgement in `Agent/ServiceCommandHandler`, held by `Hosts.Tests/LocalPlacementTests` |
| 2026-10 | Amended | Amendment 2: a Debug build lets a binding that fails §1 stand and says what a Release build refuses and why, at every door §1 guards. A Release build refuses as before. The volume probe and status are untouched. `Agent/BuildConfiguration` answers per build and `ServiceOptions.SameDrivePlacementOverride` lets a suite say which it means. Held by `Hosts.Tests/LocalPlacementTests`, `Hosts.Tests/DestinationAdoptionTests` and `Web.DomTests/ConfigEditingDomTests` |
