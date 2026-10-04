# ADR-0082 — The recovery tool's diagnostic bundle, through the rule the service's log uses

**Status:** Accepted
**Date:** 2026-10
**Requirements:** NFR-PRIV-003, NFR-SEC-006
**Related:** [ADR-0081](0081-diagnostic-bundle.md) (the service's bundle and the fail-closed rendering; amended by this record), [ADR-0043](0043-structured-logging-and-diagnostics.md) (§4, redaction by declared type; §6, the recovery tool writes no file it was not asked to write), [ADR-0060](0060-the-passphrase-is-the-recovery-credential.md) (the passphrase is the recovery credential), [architecture 08 §5](../architecture/08-restore-and-recovery.md#5-emergency-recovery), [architecture 10 §4](../architecture/10-observability.md#4-diagnostics), [architecture 11 §2](../architecture/11-solution-structure.md#2-dependency-rules) (the recovery tool's closure)

**Built:**
- The rule: `Domain/Diagnostics/RedactedRendering`, moved unchanged from `Diagnostics/LogRecordRenderer`, which now applies it to records.
- The bundle: `Recovery/RecoveryBundle`, built from what `Recovery/RecoveryRun` records of a run. `Recovery/RecoverySession` records what the descriptor said and whether the passphrase reproduced the keys. `Recovery/RecoveryNote` makes a note fields rather than a sentence. `Recovery/RecoveryHost` takes `--diagnostic-bundle` and `--include-paths`.
- The tests:
  - `Hosts.Tests/RecoveryBundleTests` — the bundle inspection test, against a real archive
  - `Domain.Tests/RedactedRenderingRuleTests` — the rule at its new home
  - `ArchitectureTests/DependencyRuleTests` — the tool renders through the shared rule, and its closure is unchanged
  - `Repository.Tests/RecoveryContainmentTests` — a refused entry's note, by kind and path

---

## Context

[Architecture 08 §5](../architecture/08-restore-and-recovery.md#5-emergency-recovery) lists what the standalone recovery tool must do, and the last item is to produce a diagnostic bundle containing no secrets. [ADR-0081](0081-diagnostic-bundle.md) built the service's bundle and left the tool's owed, because the two have almost nothing in common to build on:

- **The tool has no service, no ring, no state directory and no configuration.** It keeps to the console ([ADR-0043](0043-structured-logging-and-diagnostics.md) §1), it has four log events, and it runs on a machine that was rebuilt this morning, usually on the worst day somebody has had in a while. The run that fails is the one a person most needs to describe to somebody else.
- **Its dependency closure is a whitelist.** Format, crypto, packing and storage, and nothing else ([architecture 11 §2](../architecture/11-solution-structure.md#2-dependency-rules), NFR-PORT-001). The fail-closed rule ADR-0081 decided lived in `FallbackPlan.Diagnostics`, which carries the concrete logging package and the ring's collection library. Both are pinned out of the format-critical closure, and the tool's project references are pinned exactly.
- **What it holds is sensitive in its own ways.** The passphrase itself sits in an environment variable the tool reads. The tool prints the repository and snapshot identities in full, because that is what an operator reads back down a phone. The archive's descriptor carries the salt and the sealing public key, which are the same in every archive an installation writes, so they are a handle across stores. It also carries a creator, which is free text that the full client fills with the machine's name. And a restore that fails names the person's own files.

## Decision

### 1. The rule lives beside the types it reads

`RenderMode`, the withheld marker and the value rule move from `Diagnostics/LogRecordRenderer` to `Domain/Diagnostics/RedactedRendering`, beside `IRedactedValue`, `LogPath`, `LogId` and `LogLabel`. Nothing about the rule changes; [ADR-0081 §1](0081-diagnostic-bundle.md#1-a-redacted-rendering-lets-through-only-what-a-declared-type-clears)'s table is still the whole of it. What changes is who can reach it. The service's renderer substitutes it into each record's template, and the recovery tool renders each bundle field through it, without a new project reference.

The alternative to moving it was copying it, and the tool's console sink already set that precedent for forty lines. A privacy rule is a different kind of forty lines: two copies are two rules the moment one of them changes. An architecture canary fails if the tool ever renders through anything else.

### 2. A bundle is a report of one run, asked for on that run

Every verb takes `--diagnostic-bundle <file>`. The tool writes the bundle after the verb, however the run ended: success, a partial restore, a refusal, a wrong passphrase, an archive that is not one. It also writes after an exception nothing anticipated, which then propagates as it always did. The verb's own output does not change. The tool says it wrote the bundle on standard error, where a script reading the snapshot listing does not look.

The tool still writes no file it was not asked to write, and it overwrites none it was. These are refused before the archive is touched:

- a bundle file that already exists, which is also refused again at the write by `CreateNew`, so the refusal cannot race;
- `--diagnostic-bundle` with no file after it;
- `--include-paths` without a bundle to change.

The bundle has no log in it. The tool keeps no log beyond the console, and its four events say less than the report does.

### 3. What a bundle carries

| Entry | What it says |
|---|---|
| `run.json` | The verb, the options it was given, the exit code, and why the run stopped. That is a stage (options, descriptor, passphrase, blobs, snapshots, restore) and a reason the tool records at the point it refuses, not one guessed back from a message, plus the exception's type and its message. |
| `archive.json` | What the descriptor said, or why it said nothing, then the format versions; the required, optional and unsupported features; the derivation's cost parameters; when the archive was created; whether the passphrase reproduced the keys; and the blobs that did not open. |
| `snapshots.json` | The snapshots the archive lists, with their capture times, signature verdicts and observed clock skew, when the run listed them. |
| `restore.json` | What a restore did, and every file it could not restore, when one ran. |
| `environment.json`, `manifest.json`, `README.txt` | The tool's version, the runtime and the operating system; the entries and whether paths are included; and the same in words, with what the bundle leaves out. |

`RecoveryRun`, the record the tool keeps of a run, holds only what the builder classifies. Each field is rendered through the rule by the type it is declared as. A path is a `LogPath`. An identifier is shortened. The tool's vocabulary (a verb, an option, an exception's type) is a `LogLabel`. A reader's or the platform's words are text no type cleared. A field added to the record later is absent from the bundle until the builder classifies it.

### 4. What it never carries

- The passphrase, and the name of the variable holding it. The bundle says only whether one was named and set.
- Any key.
- The archive's salt and sealing public key. The descriptor read takes what describes the archive and leaves what identifies the installation.
- The descriptor's creator, even with paths opted in, because it can hold the machine's name.
- The machine's name, and the environment.

Identifiers shorten in every rendering that leaves, opted in or not: `repo#` and `snap#` with eight characters, a blob as its kind and eight characters of its leaf.

### 5. A note is fields, not a sentence

A recovery note is now a `RecoveryNote`, holding:

- a kind;
- the entry's path inside the snapshot;
- the blob or the record it concerns;
- the read outcome or the entry's kind;
- a refusal's reason, in the code's own words;
- the reader's detail.

Its `ToString` is the sentence the operator's terminal has always printed, so standard error reads exactly as before. The bundle renders each field by its type, so a failed file's path is a digest and the reader's detail is withheld. Handing on the sentence, with a path inside it, would have put the bundle back to matching strings.

A note list longer than ten thousand is cut, and the bundle counts what it left out. A restore of a badly damaged archive can fail on every file it holds, and a bundle too large to send helps nobody.

### 6. The opt-in to paths

`--include-paths` renders paths and the text no type classifies as written, in the third rendering ADR-0081 defined: identifiers still shorten. The tool states the consequence on standard error before the run starts, and the manifest and the README record it. The paths are the archive's location, the output folder and the failed files' paths. The text is chiefly errors, which often repeat a path.

## Consequences

- One rule now serves two executables, and a third would reach it the same way. The canary makes a copy visible in review instead of in a leak.
- The tool's library surface changed. `RecoverySession.LoadBlobsAsync` and `RecoveryRestoreReport` carry `RecoveryNote`s, not strings. The solution's own callers were updated, and the text an operator reads did not change.
- A default bundle withholds the text of every error, including the format codec's own explanations, such as a damaged footer's locator. The stage and the reason say what kind of failure it was, and the opt-in releases the rest. That is the fail-closed rule's usual price, paid here knowingly.
- The bundle's JSON is something a helper reads, so its field names are pinned by the inspection test rather than left to drift.

## What this does not do

- It does not add a log to the tool, or a log to the bundle. The operator's terminal shows full paths as before, because it is the operator's own terminal.
- It does not identify the archive beyond a shortened repository identity and the archive's creation time.
- It does not include the descriptor's creator under any opt-in.
- It does not bound the bundle's size beyond the note lists. The rest is small.
- It does not change the service's bundle, whose rendering is the same rule reached from its new home.

## Alternatives considered

- **Reference `FallbackPlan.Diagnostics` from the tool.** Rejected: it breaks the closure whitelist and brings the concrete logging package and the ring's collection library into the one executable that must stay smallest.
- **Copy the rule into the tool.** Rejected for the reason in §1. The canary would now fail it.
- **A dedicated `bundle` verb that inspects the archive.** Rejected: it would miss the run that failed, and a restore's per-file failures are what a helper most needs. Re-running a long restore to describe it is the wrong cost.
- **Capture the console log into the bundle.** Rejected: four events say less than the report, and the template substitution that would render them lives with the record renderer, outside the closure.
- **Read the notes back out of their sentences.** Rejected: that is string matching, which ADR-0043 §4 rules out because it fails silently and exactly when it matters.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first: the rule's move to Domain, a diagnostic bundle and its opt-in to paths on every verb of the recovery tool, the run record, structured notes, and the bundle inspection test against a real archive with planted secrets and damaged data blobs. |
