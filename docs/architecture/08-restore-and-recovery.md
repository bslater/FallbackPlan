# 08 — Restore and recovery

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §12 · **Resolves:** [H4](../review/2026-08-architecture-review.md#h4--the-recovery-kit-is-load-bearing-but-never-specified), [H3](../review/2026-08-architecture-review.md#h3--disposable-conflates-three-stores-with-incompatible-durability-requirements)

**Built:** Yes, except FR-RST-006's quarantine destination default (§3.1) — see [implementation status](../implementation-status.md).

---

## 1. Restore paths

Restore the latest version · state at a chosen date and time · a named or tagged snapshot · deleted files · an individual file version · a directory tree · an entire device · to the original location or an alternate destination · with overwrite, rename, or skip policies.

"Deleted files" is a first-class path rather than a special case, because it is one of the two things users actually come to a backup product for. The other is "the version from before I broke it", which is the individual-file-version path.

## 2. Restore planning

A plan is constructed **before** any transfer, and it is the mechanism by which the user finds out about problems while they are still cheap. It contains:

- the selected source snapshot and the resolved file-version set;
- the required object set and which replicas can serve it;
- estimated logical and physical transfer size — these differ, sometimes greatly, when the store lacks range reads ([`05-storage-providers.md` §3](05-storage-providers.md#3-capabilities));
- target conflicts: existing files, and the resolution policy that will apply to each;
- **path and case collisions** ([`06-filesystem-capture.md` §2](06-filesystem-capture.md#2-path-handling));
- metadata that cannot be preserved on this target, per the matrix in [`06-filesystem-capture.md` §3](06-filesystem-capture.md#3-metadata-matrix);
- free-space assessment against the physical requirement;
- privileges required — restoring ACLs or ownership may need elevation the current user lacks;
- objects that are damaged, missing, or held only in an archival tier requiring rehydration.

Plans are exportable and resumable. A plan that reveals unacceptable degradation can be abandoned before a single byte is written, which is the entire point of producing one.

## 3. Restore verification

Every restore:

- authenticates and decrypts every object;
- verifies each segment's plaintext content identifier after decryption;
- verifies the reconstructed file's length and whole-file verification hash;
- restores metadata **after** content, so a failure mid-content never leaves a file with correct permissions and wrong bytes;
- reports every skipped or degraded attribute;
- produces a machine-readable **restore receipt**;
- **never reports success when any required file failed.**

The last rule is absolute. A restore that recovered 9 999 of 10 000 files is a failed restore that recovered 9 999 files, and it is reported that way.

**The receipt therefore reports an outcome, not a success flag** — `complete`, `partial`, `failed` or `cancelled`. A boolean cannot express "nothing went wrong and the tree is not the tree that was captured", which is exactly what happens when the target cannot materialise a symlink or a device node. A skipped required item makes the restore **partial**: the plan declaring the shortfall in advance is a reason it was expected, never a reason to report that nothing is missing.

**Repository path text is untrusted.** A restore materialises only paths that are a sequence of plain name components resolving under the restore root; anything else — a traversal, an absolute path, a drive marker, an empty or dotted component — is refused and recorded as a failed item. The store is written by other participants and holds historical data, both of which the threat model treats as adversarial ([`../threat-model.md`](../threat-model.md)), so containment is a property of the executor rather than of whoever wrote the manifest.

### 3.1 Quarantine by default

Restores default to a quarantine path rather than the original location when restoring historical content that has not been scanned. Historical snapshots may contain malware that was present at capture time, and re-introducing it directly into a live tree is the wrong default. Restoring in place is a deliberate choice the user makes, not what happens if they press Enter. → FR-RST-006

**This is about where restored content lands, and it is a separate control from what happens to a file already there.** The two were conflated once, in the direction that matters: an option named for this section moved the *existing* file aside and put unscanned historical content at the live path — the inverse of the rule above, implemented under its name.

They are now distinct:

| Control | Question it answers | Default |
|---------|--------------------|---------|
| Destination mode | Where does restored historical content go? | A quarantine path, per this section |
| Existing-destination policy | What happens to a file already at a destination? | Preserve it — moved into this run's own displaced store |

A displaced file goes into a directory namespaced by the restore run. A single shared refuge is worse than none: restoring the same path twice silently destroys the first displaced copy, which is precisely the data the policy exists to keep.

## 4. Recovery kit

The recovery kit is what makes clean-machine recovery possible, and it is a release gate ([`../requirements/functional.md`](../requirements/functional.md#recovery-kit)). The original proposal defined it in a single sentence and left its most important property — whether it contains key material or *wrapped* key material — open. Those have completely different consequences if a kit is stolen.

### 4.1 Contents

| Field | Purpose | Sensitive |
|-------|---------|-----------|
| Kit format version | Lets a future tool parse an old kit | No |
| Minimum recovery-tool version | Refuse rather than misread | No |
| Repository ID | Identifies which repository this opens | Low |
| Repository format profile | Lets the tool check compatibility before starting | No |
| **Wrapped** repository master key | The key material, encrypted under the KEK | Yes — but useless without the passphrase |
| KDF parameters (Argon2id salt, memory, iterations, parallelism) | Reproduces the KEK from the passphrase | No |
| Destination descriptors | Where the repository lives — endpoint, bucket/container, prefix | Low |
| Issuing device identity (public) | Names the device that created the kit | No |
| Issue timestamp | Detects an outdated kit | No |
| Recovery instructions | Step-by-step, embedded in the kit | No |
| Integrity checksum over the whole kit | Detects transcription errors | No |

### 4.2 What is deliberately excluded

- **The passphrase.** The kit is one factor; the passphrase is the other. A stolen kit alone does not open the repository.
- **Store credentials.** The kit says *where* the repository is, never how to authenticate to it. A kit found on a printout must not grant access to the user's cloud account.
- **The device private key.** Recovery does not need it; a new device establishes a new identity and is re-authorised.

### 4.3 Representations

**Printable** — QR code plus checksummed text, transcribable by hand. It must survive a printer, a filing cabinet, a decade, and a person typing it back in. Human-transcribable encoding with a checksum is what makes the last part survivable.

**Machine-readable** — a single file for a password manager or an encrypted USB stick.

Both carry identical content. Both embed their own instructions, on the assumption that when the kit is needed, no other project documentation is reachable — which is precisely the scenario the kit exists for.

### 4.4 Lifecycle

- Generated during first-run setup, with **explicit confirmation** that it has been saved before setup completes.
- Regenerated when destinations change materially, with a clear indication that the old kit's destination list is stale. The old kit still *opens* the repository; it just may not know where all of it is.
- Status surfaced continuously ([`10-observability.md`](10-observability.md#1-user-level-status)): never generated, saved, or stale.
- A **recovery drill** — actually restoring a file using only the kit — is a supported and prompted workflow. A kit that has never been tested is a kit whose failure is discovered at the worst possible moment.

## 5. Emergency recovery

A standalone recovery executable, independent of the Agent and UI, that can:

- open a repository using a recovery kit;
- list snapshots;
- validate format compatibility and refuse clearly when it cannot read a repository;
- restore without the service, the catalogue, or any local state;
- rebuild a local index — including forensic rebuild ([`02-repository-format.md` §8.2](02-repository-format.md#82-forensic-rebuild));
- operate from offline media;
- produce a diagnostic bundle containing no secrets.

Source and reproducible release artifacts are published for every major format version, and remain downloadable and buildable for as long as that format version is supported. A recovery tool that cannot be obtained when needed is not a recovery tool.

## 6. What must survive a clean machine

The release gate is recovery using **only** repository access and a recovery kit. That constrains what may live in local state, and it is why local state is separated into three stores rather than one ([H3](../review/2026-08-architecture-review.md#h3--disposable-conflates-three-stores-with-incompatible-durability-requirements)):

| Store | Rebuildable from repository? | Needed for clean-machine recovery? |
|-------|------------------------------|-----------------------------------|
| Catalogue | Yes — [`02-repository-format.md` §8](02-repository-format.md#8-catalogue-rebuild) | No |
| Durable local state (device keypair, pairing grants, job history) | **No** | No — a recovering device establishes a new identity |
| Configuration (backup sets, schedules, policies) | Partially — policy manifests record what each snapshot used | Not for restore; needed to *resume backing up* |

Recovery of **data** needs only the repository and the kit. Recovery of **operation** — resuming scheduled backups to the same destinations — additionally needs configuration and re-pairing. The distinction is stated plainly in the UI, because a user who has restored their files and believes they are protected again is in a worse position than one who knows they still have to set up their destinations.

Full model in [`11-solution-structure.md` §3](11-solution-structure.md#3-local-state-separation).

---

**Previous:** [07 — Retention and garbage collection](07-retention-and-gc.md) · **Next:** [09 — Replication and peers](09-replication-and-peers.md)
