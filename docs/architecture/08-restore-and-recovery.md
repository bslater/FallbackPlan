# 08 — Restore and recovery

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §12 · **Resolves:** [H4](../review/2026-08-architecture-review.md#h4--the-recovery-kit-is-load-bearing-but-never-specified), [H3](../review/2026-08-architecture-review.md#h3--disposable-conflates-three-stores-with-incompatible-durability-requirements)

**Built:** §§1–6 yes, except three items of §2: the plan estimates no physical transfer size, only the logical size and what the files will write; a plan is not yet exportable or resumable; and an object held only in an archival tier is not reported as such. This line once claimed §2 whole, while its free-space assessment and its account of the metadata a target cannot preserve did not exist; both do now ([ADR-0083](../adr/0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md)). Built too are §3.2's reading around damage ([ADR-0075](../adr/0075-a-restore-reads-around-damage.md)), §5's diagnostic bundle from the recovery tool ([ADR-0082](../adr/0082-the-recovery-tools-diagnostic-bundle.md)), and the disaster-recovery ceremony in §6: the peer claim and its hold, and adoption shown and confirmed before it takes effect. There is no §7; the disaster-recovery claim this line once gave a §7 is §6's, and built. See [implementation status](../implementation-status.md).

---

## 1. Restore paths

Restore the latest version · state at a chosen date and time · a named or tagged snapshot · deleted files · an individual file version · a directory tree · an entire device · to the original location or an alternate destination · with overwrite, rename, or skip policies.

"Deleted files" is a first-class path rather than a special case, because it is one of the two things users actually come to a backup product for. The other is "the version from before I broke it", which is the individual-file-version path.

Every path starts at the passphrase. A person sees a backup's file names — listing a snapshot, planning or running a restore, a run's changes and failures — only through a restore source opened under a grant (§4.3) derived from the set's passphrase, which serves the session that opened it and is asked for again at the next look ([ADR-0089](../adr/0089-a-backups-file-names-need-the-passphrase.md), FR-WOR-007). The service's own drill reads without one.

## 2. Restore planning

A plan is constructed **before** any transfer, and it is the mechanism by which the user finds out about problems while they are still cheap. It contains:

- the selected source snapshot and the resolved file-version set;
- the required object set and which replicas can serve it — for a direct-ship set ([ADR-0046](../adr/0046-direct-to-destination-publication.md)) that is always a destination read: no local content exists, the sink answers each blob from whichever destination holds it (proven byte-identical end to end), and a restore with no reachable destination is a plan that says so rather than a transfer that fails. A restore of a set's own archive reads around what its own store does not hold (§3.2), so an object counts as missing only when no copy of the set holds it;
- estimated logical and physical transfer size — these differ, sometimes greatly, when the store lacks range reads ([`05-storage-providers.md` §3](05-storage-providers.md#3-capabilities));
- target conflicts: existing files, and the resolution policy that will apply to each;
- **path and case collisions** ([`06-filesystem-capture.md` §2](06-filesystem-capture.md#2-path-handling));
- metadata that cannot be preserved on this target, per the matrix in [`06-filesystem-capture.md` §3](06-filesystem-capture.md#3-metadata-matrix);
- free-space assessment against the physical requirement;
- privileges required — restoring ACLs or ownership may need elevation the current user lacks;
- objects that are damaged, missing, or held only in an archival tier requiring rehydration.

Plans are exportable and resumable. A plan that reveals unacceptable degradation can be abandoned before a single byte is written, which is the entire point of producing one.

**Room, and what will not come back.** The free-space assessment counts what the run writes, on each volume it writes to. That is each file's segments in whole 4 KiB clusters, its holes skipped (§3), and a cluster for each directory it creates. It adds room for the largest file where the engine holds it while its hash verifies, which is the volume's own figure when the two share it. In place, the run is credited only what its existing-file policy frees. It is measured against what the platform says is free. A run that will not fit is refused before anything is written, unless the person chooses to restore anyway. They may, because a volume that compresses what it stores can hold more than the estimate. The plan names each captured attribute the target will not get back, with how many files carry it, and for ownership the privilege applying it needs. The receipt then names, item by item, what was not applied ([ADR-0083](../adr/0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md)).

Producing the plan and running it are separately priced, and deliberately so. The plan's reachability pass reads every manifest it names and probes every blob they reference, because "this path cannot be restored" is worth knowing before anything moves (FR-RST-003). The **run** then reads nothing it does not need: it opens no blob, taking each record's position from the catalogue and falling back to the blob's own footer only when that turns out to be wrong, and it fetches neighbouring records together ([ADR-0068](../adr/0068-the-catalogue-directed-restore-read.md)). The budget that holds it is NFR-PERF-009's, and it is about requests rather than bytes — on an object store a request is billed and a round trip is waited on, whoever is waiting.

## 3. Restore verification

Every restore:

- authenticates and decrypts every object;
- verifies each segment's plaintext content identifier after decryption;
- verifies the reconstructed file's length and whole-file verification hash;
- restores metadata **after** content, so a failure mid-content never leaves a file with correct permissions and wrong bytes; each attribute is written on its own, and one the platform refuses is reported as not applied rather than failing a file whose content landed ([ADR-0084](../adr/0084-a-restore-writes-back-the-times-it-can-set.md)); ownership goes before permissions, which keep a set-id bit only where its owner or group landed ([ADR-0085](../adr/0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md)); extended attributes go between them, each alone and never through a link, an ACL that names accounts by number only where this installation captured the snapshot, and the group given no more than an ACL that did not come back gave it ([ADR-0087](../adr/0087-a-restore-writes-back-the-extended-attributes-it-may.md)); a folder's own metadata goes on last, deepest folder first, once nothing more will land in it, never to a folder that was already there and never through a link ([ADR-0086](../adr/0086-a-restore-gives-a-folder-its-own-metadata-back-last.md));
- reports every skipped or degraded attribute;
- produces a machine-readable **restore receipt**;
- **never reports success when any required file failed.**

The last rule is absolute. A restore that recovered 9 999 of 10 000 files is a failed restore that recovered 9 999 files, and it is reported that way.

**The receipt therefore reports an outcome, not a success flag** — `complete`, `partial`, `failed` or `cancelled`. A boolean cannot express "nothing went wrong and the tree is not the tree that was captured", which is exactly what happens when the target cannot materialise a symlink or a device node. A skipped required item makes the restore **partial**: the plan declaring the shortfall in advance is a reason it was expected, never a reason to report that nothing is missing.

**A hole is restored as a hole.** A sparse extent is hashed as the zeroes it reads as, so the whole-file hash is the same wherever the file lands. It is then skipped rather than written, both in the spool the engine verifies in and in the file it emits, so it stays a hole wherever the target filesystem can hold one ([specification 09 §4](../../specifications/repository-format/09-segmentation.md#4-sparse-extents)). APFS holds one only from 16 MiB: it fills a shorter hole with zeroes when it writes the file back, however the file was written. On Windows a file that will have a hole is marked sparse before anything is written, because NTFS otherwise allocates what a write skips. Two destinations cannot take a hole and are given the zeroes: one that cannot seek, and one that already holds bytes where the file goes, since a skipped range keeps what it held ([ADR-0079](../adr/0079-sparse-restore.md)).

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

**Quarantine decides where content lands, not what comes back.** A quarantine restore is as faithful as any other. Times, owner, group, permissions and extended attributes come back as they would anywhere, set-id bits included, and from a restore as root file capabilities too ([ADR-0085](../adr/0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md), [ADR-0087](../adr/0087-a-restore-writes-back-the-extended-attributes-it-may.md)). So a restore running as root recreates a root-owned set-user-id program in the quarantine folder as it was captured, including an old one from a historical snapshot. The folder is `.fbp-quarantine/<run>`, under the folder the restore was pointed at, and it is created with the restoring process's defaults. It is made for the run, not restored from the snapshot, so it is given no captured metadata, while the folders restored inside it get theirs back ([ADR-0086](../adr/0086-a-restore-gives-a-folder-its-own-metadata-back-last.md)). Whether other accounts can reach what lands there depends on the folder the restore was pointed at. A historical system tree restored as root belongs in a folder only the restoring account can reach, or one restricted before anyone else uses the machine ([ADR-0085 Amendment 1](../adr/0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md#amendment-1-2026-10--a-quarantine-restore-keeps-set-id-bits)).

### 3.2 Reading around damage

Every copy of a set's blobs is the same bytes: blobs are immutable, and a replica holds them key for key. So a record sits at the same offset of the same blob wherever the blob is held, and a restore that meets a record its own store will not serve can read that record from another copy ([ADR-0075](../adr/0075-a-restore-reads-around-damage.md)). → FR-RST-007

- **Which restores.** A restore of the set's own archive reads around: the staging archive, or a direct-ship set's store, which reads through its destinations. A destination named as the source is read alone, as a stranger would read it, because the recovery drill restores through it to prove that copy restores.
- **Which copies, in what order.** The repair's order ([09 §5.5](09-replication-and-peers.md#55-re-reading-what-is-held-and-repairing-it)): the set's local-path destinations by priority, then its peers over the retrieval session. Each copy is opened only once every earlier one has failed to serve, and a peer is dialled only then.
- **What sends a record there.** A record that fails authentication, framing or its content identifier; a blob that will not read; or a blob the store does not hold, which is what a staging trim leaves. Content sealed to a key the restore does not hold is the same at every copy and is not read around.
- **Nothing is trusted more for being second.** The other copy's record goes through the whole of §3, and the whole-file hash covers the reassembly whichever copies served which segments. A failed read at a copy is not damage until that copy's own footer agrees: a footer that does not list the record means the location was wrong, not the copy.
- **What is said, and what is kept.** The receipt names, per item, the copies it was read from and what was wrong with those it was read around. The answer counts the files read around damage or an unreadable copy, but not those read from a destination only because staging no longer holds them. Damage found at a destination goes on its ledger row, as the deep sweep's does, so the next sync repairs a local path's and a peer's is held for its owner to remove. Damage found in the staging archive is raised as a notice.

With every copy failed, the file still fails, and the failure names each copy tried.

## 4. Recovery credential

Clean-machine recovery is a release gate ([`../requirements/functional.md`](../requirements/functional.md#recovery-drills)), and what it needs is deliberately short: **the passphrase, and reach to an archive** ([ADR-0060](../adr/0060-the-passphrase-is-the-recovery-credential.md)). There is no recovery kit. The original proposal defined one in a single sentence and left open whether it carried key material; the answer, once format 1 went ([ADR-0014 Amendment 1](../adr/0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)), was that a repository stores no key material at all — and with nothing to carry, the kit's remaining payload was a strict subset of every archive's own descriptor.

### 4.1 What a recovery needs

| Held by | Fact | Sensitive |
|---------|------|-----------|
| The person | The passphrase | **Yes** — the one secret, never stored anywhere |
| The person | Where the backups are: a destination folder, a drive, or a paired peer | Low — and no longer written down by the product; see §6 |
| The archive's descriptor | Repository ID | Low |
| The archive's descriptor | Repository format version | No |
| The archive's descriptor | KDF parameters (Argon2id salt, memory, iterations, parallelism) | No |
| The archive's descriptor | Sealing public key — the wrong-passphrase verifier: derive from the passphrase, compare | No |

The descriptor is unencrypted by design ([repository format 01 §3.3](../../specifications/repository-format/01-object-layout.md)). `RecoverySession` reads it, derives under its salt and parameters, and proves the derivation against its sealing public key by equality — nothing is decrypted to find out. One passphrase opens every archive an installation wrote, because one salt stamps them all ([ADR-0044](../adr/0044-first-run-setup.md)).

For a **peer replica** the descriptor sits behind the peer's attribution gate, where a rebuilt machine cannot read it. The peer therefore serves the KDF salts and costs behind its claimable replicas to a paired claimant, the claim proves the passphrase against each, and the attribution follows the new machine (§6; [ADR-0053 Amendment 2](../adr/0053-peer-claim-and-configuration-recovery.md#amendment-2-2026-09--the-claim-takes-the-passphrase-and-nothing-else)).

### 4.2 What is deliberately excluded

- **Any exported artefact.** Nothing is produced at setup for a person to print, save, confirm or lose. An artefact whose every field is public and already recorded in every archive is not a second factor; calling it one would have been a fiction.
- **Store credentials.** The product records *where* the repository is only in the configuration that dies with the machine, and never how to authenticate to it.
- **The device private key.** Recovery does not need it; a new device establishes a new identity and is re-authorised. For a peer destination, "re-authorised" is the claim ceremony rather than only re-pairing (§6).

### 4.3 Restore grants (format v2)

On a write-only set the service cannot read file contents, so a guided restore ([ADR-0041](../adr/0041-guided-restore-and-peer-retrieval.md)) carries a **grant**: the admin client re-derives the sealing scalar from the passphrase where the person typed it, seals it end-to-end to the service's published recipient key (opaque to the browser and to every relay), and sends it on `open_restore_source`. The unsealed scalar lives only inside the source handle — zeroed on explicit close, the 30-minute idle sweep, or shutdown — and structure-plane verbs (browse, list, plan) never needed it at all. A restore attempted without a grant degrades honestly: each sealed read is reported as sealed in the receipt, never as damage. The installation's public derivation parameters ride `describe_service` (contract 1.28) so a client holding the passphrase can build the grant without holding an archive.

Since [ADR-0089](../adr/0089-a-backups-file-names-need-the-passphrase.md) the grant is also the proof a person needs to see the structure plane at all. A person's open without one is refused. The structure-plane verbs need a source opened under one, held by the caller's session, so "never needed it" now holds only for the service's own drill. A grant-less run is what that drill does.

### 4.4 Lifecycle

- Nothing is generated at first-run setup: the ceremony is the passphrase and the first account ([ADR-0044](../adr/0044-first-run-setup.md) as amended).
- A **recovery drill** — actually restoring a file using only the passphrase and reach to an archive — is a supported and prompted workflow (FR-DRL-001). A recovery that has never been tested is a recovery whose failure is discovered at the worst possible moment.
- The drill also happens **on a cadence**, unattended, from each destination's own replica (FR-DRL-002; [ADR-0054](../adr/0054-scheduled-restore-drills.md)) — §6.

## 5. Emergency recovery

A standalone recovery executable, independent of the Agent and UI, that can:

- open a repository using the passphrase alone;
- list snapshots;
- validate format compatibility and refuse clearly when it cannot read a repository;
- restore without the service, the catalogue, or any local state;
- rebuild a local index — including forensic rebuild ([`02-repository-format.md` §8.2](02-repository-format.md#82-forensic-rebuild));
- operate from offline media;
- produce a diagnostic bundle containing no secrets: a report of one run, asked for on that run, rendered through the rule the service's log uses ([ADR-0082](../adr/0082-the-recovery-tools-diagnostic-bundle.md)).

Source and reproducible release artifacts are published for every major format version, and remain downloadable and buildable for as long as that format version is supported. A recovery tool that cannot be obtained when needed is not a recovery tool.

## 6. What must survive a clean machine

The release gate is recovery using **only** repository access and the passphrase. That constrains what may live in local state, and it is why local state is separated into three stores rather than one ([H3](../review/2026-08-architecture-review.md#h3--disposable-conflates-three-stores-with-incompatible-durability-requirements)):

| Store | Rebuildable from repository? | Needed for clean-machine recovery? |
|-------|------------------------------|-----------------------------------|
| Catalogue | Yes — [`02-repository-format.md` §8](02-repository-format.md#8-catalogue-rebuild) | No |
| Durable local state (device keypair, pairing grants, job history) | **No** | No — a recovering device establishes a new identity, and for a peer destination claims its replica under that new identity (below) |
| Configuration (backup sets, schedules, policies) | Mostly — the policy manifest records each set's name, roots, schedule and rules since [ADR-0061](../adr/0061-adopt-a-destinations-archives.md), and its own retention since that record's Amendment 1; a destination's retention override, priority and destinations are re-declared by hand | Not for restore; needed to *resume backing up* — and a destination's archive re-declares its set through adoption |

Recovery of **data** needs only the repository and the passphrase. Recovery of **operation** — resuming scheduled backups to the same destinations — additionally needs configuration and re-pairing. The distinction is stated plainly in the UI, because a user who has restored their files and believes they are protected again is in a worse position than one who knows they still have to set up their destinations.

**A peer destination needed one more thing than "a new identity", and it now has it** ([ADR-0053](../adr/0053-peer-claim-and-configuration-recovery.md)). A peer attributes each replica to a *pinned device identity*, and re-pairing deliberately does not transfer an attribution — that rule is what stops a stranger who pairs with your friend's machine asking for your repository by name. A rebuilt machine therefore arrives with a new identity that owns nothing, and the row above was, for peers, an aspiration.

The exit is a **claim**: the peer first serves the KDF salts and costs behind every replica it holds that is claimable, the machine derives a claim key per pair from the passphrase and signs the live session under each, and the peer re-points every attribution whose recorded key matches at the new identity. The authority is the installation's passphrase — the one thing that already opens every byte of the replica — so the claim grants no new power over the data, only over whom the destination will hand it to. `claim` is the verb; it takes the passphrase and the peer's address, and nothing else ([ADR-0053](../adr/0053-peer-claim-and-configuration-recovery.md) Amendment 2).

A claim hands over **reading, and not yet deleting**. The recovery above runs unattended, because a disaster is when the far household is least reachable; but the re-pointed attribution is *held*, the peer puts the claim in front of its operator as a notice, and the claimant's retention instructions are refused, deleting nothing, until that operator acknowledges it — on their console, or with `fallbackplan-agent acknowledge-claim` at their shell ([ADR-0053](../adr/0053-peer-claim-and-configuration-recovery.md) Amendment 4, FR-DR-005). A passphrase can be stolen, and the replica may be the one copy that outlived the machine it was stolen from. Until then the rebuilt machine's backups to that peer land, and its retention there is refused by name.

One limit belongs on the recovery screen rather than in a footnote. A replica attributed before the claim key existed has no key to check against: it becomes claimable the moment an updated source makes one more offer, and if the machine died first it needs the destination's operator to re-point it by hand — which is a stated verb there ([ADR-0053](../adr/0053-peer-claim-and-configuration-recovery.md) Amendment 3: `reattribute_replica` on that machine's contract, `fallbackplan-agent reattribute` at its shell, a Re-point control on its console), Owner-only and refused wherever the passphrase could claim instead. The **set's shape** — name, roots, schedule and rules — travels in the archive itself since [ADR-0061](../adr/0061-adopt-a-destinations-archives.md), so a claimed replica is adopted back under its original ids and says what it was for; its own retention travels with it since that record's Amendment 1, and a destination's override and the destinations themselves are still re-declared.

Full model in [`11-solution-structure.md` §3](11-solution-structure.md#3-local-state-separation).

**Resuming operation is now a ceremony rather than a re-declaration** ([ADR-0061](../adr/0061-adopt-a-destinations-archives.md)). A rebuilt machine, set up afresh under the same passphrase, is pointed at the destination it still has; the service lists the archives there by descriptor alone, shows what one of them recorded, and adopts it only once the person confirms that ([ADR-0061](../adr/0061-adopt-a-destinations-archives.md) Amendment 2, FR-DR-009). The preview shows each root's recorded path, flagged where it does not resolve on this machine and re-pointable at confirmation, and the retention the set would delete by. The adoption is under the archive's original repository id and set id, with the passphrase. The set's name, roots, schedule and rules come from the archive's newest policy manifest, the archive's writer identity is resumed when nothing here has published yet, and the destination's ledger is seeded at the replica's own head, so the next backup ships only what changed into the same archive. At a peer the same steps run over the retrieval session, after the claim above. What the person must still know is where the backups are.

**A direct-ship set sharpens this** ([ADR-0046](../adr/0046-direct-to-destination-publication.md)): its content never lands locally at all, so the state directory holds metadata and nothing else, and losing that directory is the whole loss rather than an inconvenience. Its destination is the only complete copy in existence, and recovery reads it directly — pointing the standalone tool at `<destination>/<repository id>` with the passphrase. The drill that holds this to the Release binaries is [eng/recovery-drill.sh](../../eng/recovery-drill.sh): it builds an installation, captures a corpus across the segment and blob boundaries, deletes the state directory, the archives root and the sources, and then compares every recovered byte against a manifest taken beforehand. Its in-process half runs in CI as `Hosts.Tests/RecoveryHostTests`.

**And the drill happens without being asked** ([ADR-0054](../adr/0054-scheduled-restore-drills.md)). Every scheduler pass, after the transfers, each `(set, local-path destination)` pair due a drill has a bounded sample of files restored out of that destination's own replica — opened as a stranger would open it, through the same guided-restore verbs a person uses ([ADR-0041](../adr/0041-guided-restore-and-peer-retrieval.md)): that replica's store, its own repository open, and a catalogue rebuilt from its own index plane, with no staging archive and no live catalogue in the path. Thirty days by default, against the deep sweep's seven and the possession challenge's six hours, because a drill rebuilds a catalogue and writes real bytes while watching for something that changes far more slowly than rot does.

The answer is durable per pair and reaches the status matrix as **three** states, never two: never drilled, drilled and passed, drilled and failed. The first and the last both mean this destination has not been shown to work, and only the last means something is wrong — a surface that folded "never" into either would turn an unexercised destination into a reassuring one. A failed drill raises a notice and deliberately does not touch the sync state: a destination can hold every byte it was sent, prove possession of them, and still fail to restore, and blaming the copy that worked would back off the transfers that are fine. A set with no snapshot yet is not drilled at all, nor copied, since there is nothing there to restore; and a failed drill is checked again an hour after a sync has succeeded since it with no damage standing there, backing off while it keeps failing, so a notice about a fault since put right does not stand for the interval ([ADR-0054 Amendment 5](../adr/0054-scheduled-restore-drills.md#amendment-5--a-set-with-no-snapshot-is-not-drilled-and-a-failed-drill-is-checked-again-once-its-replica-has-synced-2026-10)).

What the scheduled drill does **not** prove is as load-bearing as what it does. On a write-only set — the only shape setup produces — the service holds no content key, so the drill proves the road back as far as the sealed content (the replica opens, its index and catalogue rebuild, every sampled file's manifest and segment records are found) and records that as a pass with a stated limit, `drill_limit`, rather than as a failure or a plain pass ([ADR-0054 Amendment 2](../adr/0054-scheduled-restore-drills.md#amendment-2--a-drill-on-a-write-only-set-proves-the-road-as-far-as-the-sealed-content-2026-09)); the content drill there is the manual one, with the passphrase. It runs inside the service, so it never exercises the standalone tool's dependency closure, and cannot delete the state directory it is running out of. Those three remain [eng/recovery-drill.sh](../../eng/recovery-drill.sh)'s alone, which is why that script is not superseded: it asks "does recovery work at all", thoroughly and on demand, while the scheduled drill asks "does *this* destination still restore", repeatedly and unprompted.

---

**Previous:** [07 — Retention and garbage collection](07-retention-and-gc.md) · **Next:** [09 — Replication and peers](09-replication-and-peers.md)
