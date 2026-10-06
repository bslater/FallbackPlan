# ADR-0089 — A backup's file names need the passphrase

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-WOR-007; amends FR-WOR-001, FR-WOR-002, FR-WOR-003, FR-WOR-004
**Related:** [ADR-0041](0041-guided-restore-and-peer-retrieval.md) (the restore wizard's gate, which this replaces), [ADR-0042](0042-write-only-repositories.md) §5 (the restore grant this reuses as the proof), [ADR-0045](0045-client-authentication.md) (sessions, which a proof is bound to), [ADR-0050](0050-completed-run-record-and-drill-down.md) (the run's changes and failures, whose paths this gates), [ADR-0055](0055-reclaim-authority.md) §6 (the per-set grant ceremony this shares), [threat model T-23](../threat-model.md#t-23-a-signed-in-account-reads-a-backups-file-names)

**Built:**
- The service: `Agent/ServiceCommandHandler` refuses `open_restore_source` without a restore grant, and refuses listing a snapshot, planning or running a restore, and a run's changes and failures without a source opened under one, held by the caller's session and, for a run, of the run's set. A change preview without such a source counts the files only the backup names and leaves their names out. `Agent/AuthenticatingService` stamps each command with the session it came from; `Agent/RestoreSourceRegistry` records it on the source. The drill runs in the service's own caller scope (`Agent/RecoveryDrillJob`).
- The contract: `Api/Commands` gains a source on `job_changes`, `job_failures` and `preview_set_changes`, and a session id the wire never carries; `Api/Results` gains `names_withheld` on the preview (contract 1.56, `Api/ContractVersion`).
- The console: `Web/ConsoleRestoreGate` derives a grant per set under the facts the service publishes and proves each against the set's sealing key; `Web/WebConsoleHost` serves it at `/api/restore-gate`, slowed by `Web/RestoreGateThrottle`. The page asks for the passphrase before every browse, run report, comparison and restore, opens a source for that look and closes it after; the restore wizard has no way past a passphrase it could not check.
- The CLI: `Cli/GrantedSources` derives and opens; `ls` over `--connect`, `jobs <id> --changes`/`--failures`, `changes` and `restore` name files only with `--passphrase-env` (`Cli/CliApplication`, `Cli/OperationGateway`).
- The tests:
  - `Hosts.Tests/PassphraseGateTests` — each naming verb refused by name without an unlocked source and answered with one; a source another session unlocked, even the same account's second sign-in, serves nothing; one set's source opens nothing of another's; a change preview without a source counts deleted and no-longer-included files and names neither, and with one names both; a set never backed up has nothing to withhold; the CLI's `changes` names deleted files only with the passphrase
  - `Hosts.Tests/WriteOnlySetTests`, `Hosts.Tests/WriteOnlyPeerRetrievalTests` — a person's open refused without a grant, while the service's own work reads the structure plane on the write bundle alone
  - `Hosts.Tests/JobsVerbServiceTests`, `Hosts.Tests/RemoteConsoleTests` — the CLI refuses a run's or a snapshot's files without `--passphrase-env`, naming it
  - `Web.Tests/RestoreGateTests` — a grant per set under that set's own facts, each opening with the service's recipient scalar to the published key; nothing minted for a wrong passphrase; unavailable when nothing is published; a run of wrong ones slowed per account, whichever session it signed in with, one try at a time, and forgiven by the right one
  - `Web.DomTests/PassphraseGateDomTests`, `Web.DomTests/RestoreWizardDomTests` — asked at every look, a wrong passphrase opening and naming nothing, each look reading through the source it unlocked and closing it, and no way past a passphrase that could not be checked
  - `Web.Tests/ConsolePassphraseGateScriptTests` — every action that names a backup's files unlocks first; no bypass remains; the comparison says when names were withheld
  - `Api.Tests/ContractAdditiveFieldsTests`, `Api.Tests/ConfigurationContractTests` — the new fields' wire names, their pre-1.56 defaults, a session id that is never written and never believed, and the version pinned at 1.56

---

## Context

The owner noticed that anyone signed in to the console could browse a backup, open a run's "what changed" and "failures", and plan a restore, and so read the name of every file the backup holds, without knowing the passphrase. The direction was plain: viewing a backup set or starting a restore must ask for the master passphrase and confirm it before anything is shown or restored.

Three facts shaped the answer.

- **The service reads every name on its own.** A repository is write-only ([ADR-0042](0042-write-only-repositories.md)): file contents are sealed to a key the service never holds, but the structure plane (manifests, paths, sizes) is readable on the write bundle the service keeps so it can back up, apply retention and verify unattended (FR-WOR-003). Its catalogue keeps paths in the clear. So the gate cannot be cryptographic. It is a policy the service enforces: it names a backup's files only to a caller who has proved the passphrase.
- **A proof already exists.** A restore grant ([ADR-0042](0042-write-only-repositories.md) §5) is the set's sealing scalar, derived from the passphrase where the person typed it and sealed to the service's recipient key. The service checks it against the set's sealing public key. It is the one passphrase-shaped thing [NFR-SEC-009](../requirements/non-functional.md) lets cross the contract, and a restore source opened under one already exists.
- **The console's gate could be passed.** The restore wizard checked the passphrase against archive files on the console's own disk ([ADR-0041](0041-guided-restore-and-peer-retrieval.md) §1). That works only when the console runs beside the service. Anywhere else the gate said "unavailable" and offered "Continue without local verification". The service, for its part, answered any signed-in caller.

The owner chose what is gated (browsing, restoring, and a run's file lists), how long a proof lasts (one look: every browse, report and restore asks again), and who may give it (any account that knows the passphrase).

## Decision

1. **The rule.** The service names a file a backup holds only to a caller who has proved the set's passphrase for the action at hand (FR-WOR-007). Names on disk now are not the backup's: the folder picker shows them to anyone signed in, and a running backup's progress names the file it is reading. The rule covers what only the backup can say.

2. **The proof is an unlocked source.** A restore source opened under a verified restore grant is the proof. `open_restore_source` without a grant is refused. The refusal comes after the cheap checks, such as whether the set exists and has backed up, and before a replica is opened or a peer dialled, so a caller without a grant gets nothing of it.

3. **What needs the proof.** `list_directory`, `plan_restore`, `run_restore`, `job_changes` and `job_failures` are refused without such a source. The legacy path that read the staging archives with no source is gone for every caller but the service's own work. `job_changes`, `job_failures` and `preview_set_changes` gain a `source` to name one (contract 1.56).

4. **A source serves the session that unlocked it.** The connection's gate ([ADR-0045](0045-client-authentication.md)) stamps each command with an id for the session it came from: a digest of the session's token, never the token, and never on the wire. A source records the session that opened it. A command from any other session naming it is refused, even from the same account signed in elsewhere. Closing another session's source acknowledges and closes nothing. A source opened without a grant, which only the service's own work can open, is no proof for anyone.

5. **One set's proof is that set's.** A set adopted from a destination keeps the salt it was born under ([ADR-0061](0061-adopt-a-destinations-archives.md)) and may answer to another passphrase. A run's changes and failures, and a comparison, need a source of that set; another set's source is refused, naming both sets.

6. **Every time.** Nothing remembers an unlock. The console opens a source for each look, whether a snapshot's listing, a run's report, a comparison or a restore, and closes it when that look ends; the next look asks again. The CLI does the same per invocation, under `--passphrase-env`.

7. **The change preview withholds; it does not refuse.** The set editor asks for a comparison as rules are ticked, and on the save step. Without a source, the service answers with every count and with the names of new, updated, metadata-only and moved files, all on disk now. It leaves out the names of deleted files and of files the rules stop capturing, which only the backup holds. `names_withheld` says so, and the editor says those files are named only with the passphrase. A set's "What changed?" asks for the passphrase and names everything. A set that has never backed up has nothing to withhold.

8. **The console checks against what the service publishes.** The gate derives a grant for each set under that set's published salt and cost, or the installation's, and proves it against the set's published sealing key ([ADR-0055](0055-reclaim-authority.md) §6's ceremony, sealing the scalar instead of the reclaim root). It reads nothing local, so a console on any machine checks alike. "Unavailable" now means the service published nothing to derive under. Nothing proceeds past it: the bypass is gone, because an unchecked passphrase is no passphrase.

9. **Wrong passphrases are slowed, never locked.** Per signed-in account, whichever session it signed in with, three wrong tries are free, then each further try waits twice as long as the last, up to thirty seconds, and a passphrase that opens something clears the count. Signing in again does not start the count over. An account's tries take turns, so a burst of them is not judged against one free count. This is FR-USR-005's posture for passwords. It deters guessing at the console and nothing more (see Consequences).

10. **The service's own work needs no proof.** A drill restores samples of a replica to prove it restores, with no person present. It runs in the service's own caller scope, which no listener builds, and reads the structure plane on the write bundle as backup and retention do.

## Consequences

**Positive**
- A backup's file names reach a person only through the passphrase, at every surface: the console, the CLI, a paired remote console, and anything else speaking the contract, because the service enforces it.
- The console's check works wherever the console runs, and cannot be stepped past.
- A proof cannot be borrowed by another session, or carried from one set to another.

**Negative**
- Every look costs a derivation in the console process: about a second at the creation minimums, more at higher costs. The owner chose that over remembering an unlock.
- A pre-1.56 client that browsed, planned or read a run's files on its sign-in alone is refused, by name. This is the one non-additive part of 1.56, as 1.42's adoption refusal was. The console and the CLI ship with the service.
- The set editor counts deleted files without naming them.

**Residuals.** These are recorded as [T-23](../threat-model.md#t-23-a-signed-in-account-reads-a-backups-file-names).
- **The gate is policy.** Whoever controls the service account or its state directory reads every name directly from the catalogue, as [T-19](../threat-model.md#t-19-key-material-at-rest-in-the-service-account) already concedes.
- **Guessing offline.** Any signed-in account can read a set's salt, cost and sealing key, so it can test guesses offline at Argon2id's cost per guess. The throttle slows only guesses made through the console. Against offline guessing the defence is the passphrase's own strength, as it is against anyone holding a copy of the backup, whose descriptor carries the same facts.
- **A captured grant is a bearer proof.** A grant envelope taken from the page while a look is open unlocks sources on that service until its recipient key changes. The page holds it only for the open. Anyone able to read it there can read the passphrase as it is typed.
- **Incidental names.** A notice can name a path a drill could not restore, and a damage notice names a sample of the files the damage reaches ([ADR-0076](0076-damage-is-traced-to-what-needs-it.md)), both shown to anyone signed in. The service's log is redacted by type ([ADR-0081](0081-diagnostic-bundle.md)), and a diagnostic bundle carries paths only when a local person opts in.

## Alternatives considered

- **Remember an unlock for the session, or for a few minutes.** One derivation per sign-in would be cheaper. The owner chose every time.
- **Gate in the console only.** The CLI and a paired remote console would still be answered. Only the service can make the gate hold for every client.
- **Grant the right per account.** That is a permission an owner sets, not a proof of the passphrase. The owner chose "any account that knows it".
- **A new `envelope` on each gated verb.** It would put key material on verbs that carry none today, and NFR-SEC-009's confinement counts every one that does. A source opened under one grant already carries the proof, and the restore needs that source anyway.
- **Single-use grants against a service-issued challenge.** These would stop a captured envelope being replayed. They are not done: the page that holds an envelope is the page the passphrase is typed into.
- **Stop publishing the sealing key, so guessing has to go through the throttle.** The key is in the descriptor every copy of the backup carries, and the retention and adoption ceremonies prove against it. Withholding it here would close one route and leave the others open.
- **Refuse the change preview without a source.** The editor asks for it at every tick of a rule. All but two of its buckets name files on disk now, which the folder picker shows anyway.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Written from the owner's direction that viewing or restoring a backup must ask for, and check, the passphrase first; built with it — the service's refusals and session-bound sources (`Agent/ServiceCommandHandler`, `Agent/AuthenticatingService`, `Agent/RestoreSourceRegistry`), contract 1.56 (`Api/ContractVersion`), the console's per-set grants and throttle (`Web/ConsoleRestoreGate`, `Web/RestoreGateThrottle`), and the CLI's (`Cli/GrantedSources`), pinned by `Hosts.Tests/PassphraseGateTests`, `Web.Tests/RestoreGateTests`, `Web.DomTests/PassphraseGateDomTests` and `Api.Tests/ContractAdditiveFieldsTests` |
