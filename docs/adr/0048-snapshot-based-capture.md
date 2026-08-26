# ADR-0048 — Snapshot-based capture: a privileged helper, and what each platform is actually promised

**Status:** Proposed
**Date:** 2026-08
**Requirements:** NFR-OPS-002, NFR-REL-001
**Related:** [ADR-0026](0026-phase-1-capture-shapes.md), [ADR-0028](0028-service-boundary-and-deployment-topologies.md), [ADR-0033](0033-hosting-under-an-os-service-manager.md), [architecture 06 §5](../architecture/06-filesystem-capture.md#5-consistency), [format 06 §6](../../specifications/repository-format/06-manifests.md)

---

## Context

Capture today is live, best-effort, and honest about it: the scanner stats a file
before and after reading it, re-reads when the two disagree, and records
`captured-inconsistent: <attempts>` when they never settle ([ADR-0026
§Decision 2](0026-phase-1-capture-shapes.md)). That machinery **detects** a torn
read. It cannot **prevent** one.

Three things follow, and the third is the one that forced this decision.

**A file that keeps changing is captured torn and reported clean.** The read
budget is two attempts. A file being written continuously — a database, a VM
disk, a busy log — exhausts it, and the last read is published with a diagnostic
string on the file version and `capture_status = 1` on the snapshot. That is
correct per ADR-0026 and it is also the sharpest limit of live capture.

**On Windows a locked file is not captured at all.** The POSIX walk opens content
with `openat` during traversal and hands the read a duplicate of that descriptor,
so a lock taken through the *name* never reaches it. Windows takes no content
handle — every read goes through the path — so a file held with
`FILE_SHARE_NONE` fails the open, lands in the error manifest, and makes the
backup partial. `LocalTreeAdverseCaptureTests` asserts exactly this asymmetry on
both platforms. Outlook PSTs, live database files and anything with an exclusive
lock are, today, simply absent from Windows backups.

**The format has been waiting.** `consistency_method` (key 10) has carried
`1 live, 2 VSS, 3 filesystem snapshot, 4 application-quiesced` since the format
was written, is validated on read and write, and rides inside the snapshot
signature. Nothing needs to move in the format to record a snapshot-based
capture; only a producer has to exist. As of contract 1.18 the value is also
carried by the catalogue, the contract and both clients, so whatever a producer
records is visible the day it lands.

A snapshot is the only mechanism that turns all three from *detected* into
*impossible*. The obstacle is not the API. It is that every snapshot mechanism
requires privilege the agent deliberately does not have.

**The agent runs as a named ordinary account, and that is load-bearing.** The
generated service artifacts name an account ([ADR-0033](0033-hosting-under-an-os-service-manager.md));
the keystore is scoped to that account, so the operator who seeds the passphrase
and the service that reads it must be the same identity, or the boot-started
process exits 1 with no passphrase ([ADR-0028 §9](0028-service-boundary-and-deployment-topologies.md)).
Running the agent as `LocalSystem` or `root` to obtain snapshot privilege breaks
unattended unlock, because the passphrase lives in the operator's keystore and
not in `SYSTEM`'s.

And ADR-0033 already rejected this shape of thing once, in its own words:

> The alternative, shelling out to `systemctl`/`launchctl`/`sc.exe` to register
> the service directly, was rejected: it would run privileged, mutate the system
> in ways an operator cannot inspect first, and — because it is an OS mutation —
> could not be tested on CI at all.

`tmutil localsnapshot`, `lvcreate`, `btrfs subvolume snapshot` and
`IVssBackupComponents` are all privileged, all mutate the system, and none is
testable on this project's CI. That objection has to be answered, not stepped
around.

---

## Decision

**Take the snapshot in a separate privileged helper; keep the agent
unprivileged.** The helper's entire surface is: given a volume or mount point,
create a snapshot and report a path; and given that path, release it. It opens
no repository, reads no keystore, holds no key material, and never touches the
store. The agent runs as it does today, calls the helper across a local
boundary, scans the returned path, and calls back to release.

This is the only option that keeps the keystore rule intact. The passphrase
stays in the service account's keystore because the service account is
unchanged; the privilege lives in a process that has no passphrase to want.

**Answer to ADR-0033 §Decision 3: the objection holds for durable mutations and
does not reach ephemeral ones.** Registering a service changes the machine until
somebody changes it back, which is why an operator must see the artifact first.
A shadow copy or an LVM snapshot is created and released inside one backup run
and reverts itself if the process dies — Windows releases a non-persistent
shadow copy when its `IVssBackupComponents` goes away, and a helper that crashes
leaves a snapshot the next run reaps by name. The inspect-before-apply argument
does not apply to a mutation with no lasting effect to inspect.

The CI half of the objection stands unchanged and is accepted rather than
argued with. See *Consequences*.

**Every platform is offered; none is promised.** The provider probes, and a
volume that cannot snapshot falls back to live capture and records `1`. This
follows the doctrine `SparseProbe` already states for a comparable optional
capability — *"degrades to 'no holes' on any failure: sparse detection is an
optimisation, never a correctness input"*. A snapshot is the same shape of thing:
when it is available the capture is stronger, and when it is not the capture is
exactly what it is today.

| Platform | Mechanism | When it works |
|---|---|---|
| Windows | VSS, non-persistent, no writers by default | Any NTFS volume with the VSS service running and the helper elevated |
| macOS | APFS local snapshot | APFS with root and Full Disk Access |
| Linux | LVM thin, Btrfs subvolume, ZFS dataset | Only where the root sits on one of those; **ext4 on a plain partition cannot, and that is the commonest layout** |

**The Linux caveat is stated in the product, not just here.** A user who enables
snapshot capture on an ordinary ext4 partition gets live capture, and the failure
mode this whole area already suffers from is silence. The probe's finding is
reported at configuration time — "this root is on ext4 with no volume manager;
captures will be live" — rather than discovered later by reading
`consistency_method` on a snapshot they have already taken.

**Application-quiesced capture (method 4) is out of scope here.** It needs a hook
contract, a trust decision about running user-supplied commands as part of a
backup, and a failure policy when a hook hangs. That is a separate ADR.

---

## What is not yet decided

**The VSS interop path.** There is no COM anywhere in this solution, all 47
P/Invokes are source-generated `[LibraryImport]`, and no project targets
`net10.0-windows`. `IVssBackupComponents` is not obtained through
`CoCreateInstance`; it comes from `CreateVssBackupComponents` in `vssapi.dll`
and is, on x64, a C++ vtable rather than a marshallable COM interface — which is
why AlphaVSS exists as a C++/CLI shim at all. AlphaVSS last shipped in July 2023
targeting `net45` and `netcoreapp3.1`; that restores into `net10.0`, but on an
end-of-life target framework, unmaintained, and it must clear a dependency audit
set to severity `low` with warnings as errors, plus a licence check against the
AGPL/commercial dual arrangement ([ADR-0019](0019-third-party-dependency-policy.md)).

**This is a spike, and it gates the Windows half of this ADR.** The candidates
are CsWin32 source generation, hand-rolled `ComWrappers` interop, and AlphaVSS.
If none survives, the honest outcome is that macOS and Linux ship and Windows
does not — which would be an unwelcome inversion, since Windows is the platform
with the most to gain.

---

## Structural work this implies

Three seams do not exist yet, and naming them is half the point of writing this
down now.

**`IFileSystemSource` has no lifecycle.** No `IDisposable`, no begin/end-capture
pair. A snapshot must be created before the walk and released after it, and there
is currently nowhere to hang that.

**`ScanRoot` is one plain path**, with no distinction between the path a user
declared and the path capture actually reads. Under a snapshot those differ —
`\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\...` against `C:\Users\...` —
and on Windows every read goes through `ScanEntry.FullPath` because the walk
takes no content handle there. The multi-root label machinery
(`MultiRootScan.Prefix`/`Relabel`) is precedent for rewriting an emitted path,
not a reusable mechanism for it.

**`consistency_method` is singular and signed**, so a multi-root job where only
some roots snapshotted needs a conservative floor: the method recorded is the
weakest any root got. `SourceFilesystemIntersection.Intersect` already does
exactly this for the filesystem capability record and is the precedent to copy.

Two pieces of groundwork already exist and should be reused rather than rebuilt:
`LocalFileSystemSource.Probe` resolves the Windows volume root that
`AddToSnapshotSet` needs, and its Linux path already parses `/proc/mounts` for
the longest mount-point prefix — then discards the mount point and returns only
the filesystem type.

---

## Consequences

**Positive** — a locked Windows file becomes backupable at all, which is a
category of data currently missing rather than merely degraded. A torn read
becomes impossible instead of detected, closing the one gap live capture cannot.
Application-consistent capture becomes reachable where VSS writers cooperate.
The agent stays unprivileged and the keystore rule is untouched. The format,
catalogue, contract and both clients already carry the answer, so a provider
lands visible.

**Negative** — a second process to install, supervise and version, and a local
boundary between it and the agent that has to be authenticated or it becomes a
way to ask a privileged process to snapshot arbitrary volumes. Installation gains
a privileged component, which ADR-0033 worked to avoid.

**None of it is testable on this project's CI, and that is stated rather than
hidden.** No CI job runs elevated; making one would silently disable the ten-odd
tests that depend on an unprivileged process to observe permission denial at all.
GitHub's Windows runners lack `SeCreateSymbolicLinkPrivilege` — `LocalScanTests`
already records that — and `SeBackupPrivilege` is the same family. macOS runners
cannot grant Full Disk Access; Linux runners are ext4 on a single non-LVM volume.
Following ADR-0033's own precedent: the pure parts — probing, choosing, the
conservative floor, path translation, the fallback to live — are unit-tested on
every platform, and the privileged call itself is a thin shim verified manually.
**A green CI run will not claim to have taken a shadow copy.**

**A new assembly needs a coverage floor** before it can land, and largely
unreachable platform code drags one down. Splitting the untestable shim thinly
from the testable decision-making is what keeps that honest rather than merely
passing.

---

## Alternatives considered

**Run the agent elevated.** Simplest by far, and it breaks unattended unlock: the
keystore is per-account, so a service running as `LocalSystem` cannot read a
passphrase seeded by the operator. Solving that means either a second credential
store for the elevated identity — more key material in more places, against
NFR-SEC-009's direction — or prompting at boot, which defeats the point of a
service.

**Do nothing and rely on retry.** Defensible for most files and already
implemented. It leaves the continuously-written file torn-but-reported-clean, and
leaves Windows unable to back up locked files at all. The second of those is not
a degradation; it is absence.

**Increase `ReadAttempts`.** Cheap, and it helps a file that settles. It does
nothing for a file that never settles, and nothing at all for a locked one. It
also cannot be raised far: a failed attempt has already claimed segment object
identifiers, so retries are bounded by more than patience.

---

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Written after adverse-I/O coverage showed live capture's two hard limits — a continuously-written file captured torn and reported clean, and a Windows locked file not captured at all. Decision fixed on a privileged helper rather than an elevated agent, because the per-account keystore makes elevation cost unattended unlock. All three platforms offered, none promised, degrading to live capture exactly as sparse detection degrades. The VSS interop path is explicitly unresolved and gates the Windows half |
