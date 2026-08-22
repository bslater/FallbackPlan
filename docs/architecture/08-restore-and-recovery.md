# 08 — Restore and recovery

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §12 · **Resolves:** [H4](../review/2026-08-architecture-review.md#h4--the-recovery-kit-is-load-bearing-but-never-specified), [H3](../review/2026-08-architecture-review.md#h3--disposable-conflates-three-stores-with-incompatible-durability-requirements)

**Built:** §§1–6 yes; §7's disaster-recovery claim is specified only — see [implementation status](../implementation-status.md).

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

### 4.5 The write-only kit (format v2)

A write-only repository's kit ([ADR-0042](../adr/0042-write-only-repositories.md)) carries **no key material at all** — not even wrapped: no key object exists to carry. It holds the repository id, format 2, the sealing public key, the KDF salt and parameters, and the destinations — purely "where the repository is and how to re-derive". The passphrase is the one factor; `RecoverySession` derives the whole authority from it against the kit's recorded parameters and proves it by public-key equality. A stolen v2 kit yields strictly less than a stolen v1 kit (which at least carried a wrapped key to attack offline): it yields an address and a public key.

### 4.6 Restore grants (format v2)

On a write-only set the service cannot read file contents, so a guided restore ([ADR-0041](../adr/0041-guided-restore-and-peer-retrieval.md)) carries a **grant**: the admin client re-derives the sealing scalar from the passphrase where the person typed it, seals it end-to-end to the service's published recipient key (opaque to the browser and to every relay), and sends it on `open_restore_source`. The unsealed scalar lives only inside the source handle — zeroed on explicit close, the 30-minute idle sweep, or shutdown — and structure-plane verbs (browse, list, plan) never needed it at all. A restore attempted without a grant degrades honestly: each sealed read is reported as sealed in the receipt, never as damage.

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

One qualification the table above does not make, and §7 exists to make: *"needs only the repository and the kit"* assumes the repository can be **reached**. When the surviving copy is a replica held by a peer, reaching it is itself gated on a device identity that a clean machine no longer has.

## 7. Disaster recovery: when the machine itself is gone

§§1–6 answer degrees of local loss: a lost file, a lost catalogue, a lost staging archive, a clean machine with the store still in hand. This section answers total loss — ransomware that reached everything writable, theft, fire, or a rebuild from bare metal after malware — where the machine is not being repaired but replaced, and the only surviving copy of the data is at a destination.

### 7.1 What the recovering machine actually has

| Survives | Does not survive |
|----------|------------------|
| The recovery kit, wherever the user kept it | The archives |
| The passphrase, in the user's head or password manager | The catalogue |
| The replica, at the peer | The device keypair, and with it every pairing |
| | The configuration: backup sets, their ids, destination addresses |

The kit carries destination descriptors, so the machine knows *where* its friend is. What it cannot do is prove it is the same machine, because [ADR-0010](../adr/0010-local-store-separation.md) deliberately keeps the device private key out of the kit — a kit is a printable page, and a page that could impersonate a device to its destinations would be a worse credential than the one it replaces.

So the recovering machine is, correctly, a stranger. It generates a fresh identity and re-pairs. And then it hits the wall that makes this section necessary: the peer's attribution ledger records the replica against the *old* fingerprint, the owner inventory answers for the dialling identity and so answers with nothing, and the restore path — which keeps a candidate replica only if it holds the named set's `backup_set_id` — has no set id left to match against.

### 7.2 The claim

The passphrase is what closes the gap, and it is the right credential rather than a convenient one: whoever holds it can already decrypt every byte of that replica, so gating *recovery* on a key the recovery model deliberately destroys protects nothing and costs the user their data.

The mechanism is recorded in [ADR-0046](../adr/0046-replica-claim-after-total-loss.md) and specified in [peer-protocol 07](../../specifications/peer-protocol/07-retrieval.md). In outline:

1. When a destination first accepts a repository, it mints a **token unique to itself** for that replica and stores it beside the attribution. The source derives a keypair from its passphrase and that token, and registers the **public half**.
2. A recovering machine re-pairs, then dials and asks to claim. The destination returns the token and a fresh nonce.
3. The machine re-derives the keypair — the passphrase and the descriptor's public salt are all it needs — and signs the nonce bound to the session transcript and its own fingerprint.
4. The destination verifies against the public key it stored. On success the attribution moves to the new identity, and the answer carries back the **set ids** the replica's snapshots hold, which is the one piece of lost configuration nothing else can supply.

From there the machine is an ordinary hub with an unusual history: it recreates its set under the recovered id, and §§1–3's restore path runs unchanged over the peer retrieval session.

The token is per destination on purpose. Two friends holding replicas of the same repository hold different tokens and validate against different public keys, so a proof produced at one is inert at the other.

### 7.3 Reading is unattended; deleting is not

A claim is deliberately asymmetric.

**Reading needs no human at the far end.** A disaster is exactly when the other household is least likely to be reachable, and a recovery that stalls until a friend wakes up is a recovery that fails when it is needed. A claimed replica is readable the moment the signature verifies.

**Deleting waits.** The destination raises a durable notice, and refuses retention instructions from the claiming identity until its own operator acknowledges it ([peer-protocol 06 §3](../../specifications/peer-protocol/06-retention.md)).

The asymmetry follows from what an attacker gains. Someone who has stolen the passphrase can decrypt the data wherever they find it, so gating reads on a human buys nothing and costs real recoveries. Destroying a household's last surviving copy is a different act with a different blast radius, and it waits for the person who owns the disk. This is the case the malware scenario turns on: a compromised machine that claims can read what it could already decrypt, and cannot quietly delete the copy that outlived it.

### 7.4 Recovering operation

A claim recovers **data**. Recovering **operation** — resuming the schedule, the rules and the retention policy that were protecting that data — is the other half, and §6's distinction is at its sharpest here: a user looking at restored files has no way to tell whether anything is still protecting them.

Most of the answer is already in the repository. Capture rules, segmentation and the dedup trust domain are in each snapshot's policy manifest; the root **labels** are the snapshot tree's own top-level names, persisted rather than derived ([ADR-0040](../adr/0040-multi-root-backup-sets.md)); the set ids come back with the claim itself. What was missing is what lived only in `config.json`: the set's name, the **paths** its labels pointed at, the schedule, and the retention policy.

Those are carried by the **set-configuration object** ([format 11 §5](../../specifications/repository-format/11-lifecycle-objects.md#5-set-configuration-object), [ADR-0047](../adr/0047-recovering-operation-after-total-loss.md)), written for a set on every publication and whenever its configuration changes.

**It is sealed to a public key, and only the passphrase opens it.** The outer record is an ordinary standalone record, so the writer can locate and replace these objects while running; the configuration inside is sealed again to an X25519 recipient — the descriptor's own `fbp/seal/v2` key for a write-only repository, and a key derived from the master key for a v1 one. This matters because a v2 service is granted the entire structure plane by design ([ADR-0042](../adr/0042-write-only-repositories.md)): an unsealed record would hand a compromised hub the user's folder layout, schedule and rules. Sealed, the hub writes an envelope it can never open.

**Destinations are deliberately not in it.** The repository is held *by* the destinations, and [ADR-0034 §5](../adr/0034-hub-and-spoke-destinations.md) keeps that list local as a privacy statement. Destinations come from the recovery kit, which the user holds and no peer does — which is another reason FR-KIT-004 will not let setup complete until the kit is saved.

### 7.5 Resuming, step by step

1. **Claim**, per §7.2. The answer names the set ids.
2. **Read the configuration** — fetch the newest object under each set's prefix and open the envelope with the passphrase-derived scalar.
3. **Take destinations from the kit.**
4. **Confirm the paths.** Each root's path on the lost machine is shown as a *hint*, flagged where it does not exist here. This step stays a human decision: the new machine's layout may legitimately differ, and silently capturing the wrong tree under a name that says otherwise is worse than asking. The recovered retention policy is confirmed for the same reason — it governs deletion, and the signature that protects it defends against a destination, not against a compromised member.
5. **Re-adopt staging.** The hub pulls the descriptor and `/keys/` back from the replica, writes them to a fresh staging path, and opens it. It does *not* create a new repository: history stays continuous, the peer keeps one repository per set, and fan-out sends only what the destination's inventory lacks, so nothing already safe re-crosses the wire. An empty staging archive is already a supported state ([ADR-0034 §6](../adr/0034-hub-and-spoke-destinations.md)).
6. **The scheduler resumes.**

The recovered machine is a **new writer** — `LocalState` mints a fresh `writer_id` when its state file is absent — which is exactly what [T-18](../threat-model.md#t-18-writer-identity-cloning)'s gapless-monotonic rule wants, and the opposite of re-using an identity whose sequence file was lost.

**One cost is stated rather than discovered.** A fresh writer id means the previously written segments belong to another writer. Under the default `repository` dedup trust domain they are still reused. Under `device` — which [ADR-0042](../adr/0042-write-only-repositories.md) forces for write-only repositories, since they cannot read another writer's segments to verify reuse — the first backup after recovery re-uploads the entire source. On a domestic uplink that is days, and it belongs in the recovery summary the user reads.

### 7.6 What still needs a person

Bounded, and stated rather than left to be discovered:

- **The paths**, confirmed against the hint (§7.5 step 4).
- **The destinations**, from the kit — and a user who lost both machine and kit recovers their data from any peer that will re-pair, but cannot enumerate where their other copies live.
- **Store credentials** for cloud destinations, which are in no kit and no repository by design ([§4.2](#42-what-is-deliberately-excluded)).

A repository written before this revision carries no configuration object at all. Its data recovers exactly as §7.2 describes; its operation is rebuilt by hand.

---

**Previous:** [07 — Retention and garbage collection](07-retention-and-gc.md) · **Next:** [09 — Replication and peers](09-replication-and-peers.md)
