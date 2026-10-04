# ADR-0081 — A diagnostic bundle, and a redacted rendering that withholds what no type declares

**Status:** Accepted
**Date:** 2026-10
**Requirements:** NFR-PRIV-003, NFR-SEC-006
**Related:** [ADR-0043](0043-structured-logging-and-diagnostics.md) (redaction by declared type, §4, and the local/remote split, §6; amended by this record), [ADR-0028 §6](0028-service-boundary-and-deployment-topologies.md) (what a paired console may decide), [architecture 10 §4](../architecture/10-observability.md#4-diagnostics), [threat model T-16](../threat-model.md) (no filesystem access for clients), [specification 03 §8](../../specifications/repository-format/03-keys.md) (no secret in any log)

**Built:**
- The rendering: `Diagnostics/LogRecordRenderer`, with a third mode, `RenderMode.RedactedWithPaths`, and the withheld marker. The declared types it reads: `Domain/Diagnostics/LogLabel` (new), `Domain/Diagnostics/LogId` (now also wrapping an identifier held as text) and `Storage.Abstractions/ObjectKey` (now redactable).
- Every product `Log.cs` hole classified, and the call sites with them; a paired caller's `read_log` withholds an exception's message (`Agent/ServiceCommandHandler.Diagnostics`).
- The bundle: `Agent/DiagnosticBundle`, built by `export_diagnostics` in `Agent/ServiceCommandHandler.Diagnostics`, contract 1.52 (`Api/Commands`, `Api/Results`, `Api/ContractVersion`).
- The surfaces: `diagnostics-export` in `Cli/CliApplication`, and the Diagnostic bundle card on the console's Diagnostics view in `wwwroot/app.js`, through the existing relay.
- The tests:
  - `Hosts.Tests/DiagnosticBundleTests` — the bundle inspection test, against a running service
  - `Diagnostics.Tests/RedactedRenderingTests`, `Domain.Tests/LogRedactionTests` and `Storage.ContractTests/ObjectKeyTests`
  - `ArchitectureTests/LoggingShapeTests`, `Hosts.Tests/DiagnosticsCommandTests` and `Repository.Tests/LogPrivacyTests`
  - `Api.Tests/ConfigurationContractTests`, `Cli.Tests/DiagnosticsExportVerbTests`, `Web.Tests/DiagnosticsRelayTests` and `Web.DomTests/ConsoleViewsDomTests`

---

## Context

Architecture 10 §4 has promised a diagnostic bundle since its first draft: one file a person can send to whoever is helping them, which by default leaves out credentials, keys and recovery material, plaintext paths, and the repository identifiers that could correlate a user across stores, and which carries paths only by an opt-in made for that bundle, with the consequence stated. NFR-PRIV-003 asks for the same, proved by inspecting a bundle. Nothing built one. The nearest thing was `fallbackplan logs`, a page at a time from the ring.

A bundle is mostly the log, and the log was meant to be safe already. ADR-0043 §4 renders a record twice: in full for the service's own file, and redacted for anything that crosses the boundary, where a value whose declared type implements `IRedactedValue` renders through it. Reading that rendering closely, before building on it, showed it was **fail-open**. A value no type classified went out as written, and three kinds of them were routinely carrying exactly what the rule exists to withhold:

- **An exception's message.** The renderer appended it in every mode, and `read_log` handed it to a paired caller as it stood. On a real filesystem it is the platform's own sentence: "Access to the path '/home/…/a-telling-folder/' is denied." The path a `LogPath` hole had just hashed came back whole beside it. `LogPrivacyTests` could not see this, because its fake source throws "denied".
- **Reasons and details passed as strings.** The filesystem's `ListingFailed` and the engine's `FileFailed` log that same message as their `{Reason}`. So do a dozen service records, as a detail, a finding or a notice's message.
- **Identifiers and paths passed as strings.** Of about a hundred and fifty `string` holes across the product, the service's held peer fingerprints, set, repository and writer identities, and its own state and archives directories at startup. All were rendered as written to a paired console.

A bundle built on that rendering would have failed its own requirement on the first unreadable folder. The redaction had to be made true before the bundle could rely on it.

## Decision

### 1. A redacted rendering lets through only what a declared type clears

> **Amended (2026-10) by [ADR-0082](0082-the-recovery-tools-diagnostic-bundle.md):**
> the rule below moved, unchanged, from `Diagnostics/LogRecordRenderer` to
> `Domain/Diagnostics/RedactedRendering`, so the standalone recovery tool can
> render its own bundle through it. See [the amendment](#amendment-2026-10-the-recovery-tools-bundle-and-the-rules-new-home).

The rule is now default-deny. A value crosses the boundary only when its declared type says how:

| Declared type | Full: own file, local caller | Redacted: paired caller, default bundle | Redacted with paths: bundle, opted in |
|---|---|---|---|
| A secret (`Passphrase`, `Kek`, …) | its own `ToString` redacts | the same | the same |
| `LogPath` | as written | `path#` and eight hex, extension kept | as written |
| An identifier (`LogId`, `RepositoryId`, `ObjectId`, `BlobId`, `WriterId`, `StoreBlobKey`, `ObjectKey`, …) | in full | shortened | shortened |
| `LogLabel` | as written | as written | as written |
| A number, a bool, an enum, a time or a duration | as written | as written | as written |
| A `string`, any other type, an exception's message | as written | `(withheld)` | as written |

The exception's type still crosses. It is the code's own name, and "an `UnauthorizedAccessException` here" is half of what a reader needs.

This is ADR-0043 §4's own principle, followed to the end it was stated for. A type that knows how to cross is how a value earns the right to. Rendering everything else unless somebody remembered to wrap it is a filter list kept in reverse, and it fails the way NFR-SEC-006 says filter lists fail: silently, and when it matters.

### 2. `LogLabel` is how a call site vouches for words

Most strings in a log are safe. A command's name, an outcome, a role, a schedule as written, the name a person gave a set or a destination. Withheld wholesale, they would leave a paired console reading "Command (withheld) answered (withheld)". `LogLabel` is the declaration that a string is safe in every rendering: a word from the code's own vocabulary, or a name somebody chose to see on a screen.

It has no implicit conversion from `string`. Declaring words safe to send off the machine is a decision, and an implicit conversion would make it one nobody took. It is not an `IRedactedValue` either, because it has no redacted form to offer and a reader of that interface should never be told a label hides anything.

Names are labels deliberately. Set, destination, paired-device and account names are what a status, a notice and a log line are about, and a paired console already sees them through every other verb. A bundle says so in its README.

Some strings stay strings: text a client typed (a sign-in's user name, which is sometimes a password typed into the wrong box, and a request's Host header), a peer's network address, and every reason or detail that can carry an exception's message.

### 3. Identifiers held as text are identifiers, and a store key is one

`LogId` wraps an identifier already held as text: a set's hex id from the configuration, or a peer's base32 fingerprint. It equals and redacts exactly as the same identifier wrapped from its bytes. The factories name the kinds the service logs: `Fingerprint` (`peer#`), `BackupSet`, `Repository`, `Writer`, `Snapshot` and `Destination`. A job's identity goes through `FromText("job", …)`, because a transfer job's id embeds its set's.

`ObjectKey` now implements `IRedactedValue`. Its rendering is its kind and the first eight characters of its leaf, `blobs#n7do2wyk`. The leaf is a blob's or a delta's identity, and the components between are layout.

A call at a level that may be disabled takes these as locals rather than as factory calls inside the arguments. That is CA1873's rule, and the idiom the publication path already used.

### 4. Every hole is classified, and a lint keeps the sensitive ones typed

Every `string` hole in every product `Log.cs` was looked at, and each became a `LogId`, a `LogPath` or a `LogLabel`, or stayed a string because its content is not the call site's to vouch for. Tests that asserted the raw value of a reclassified hole now expect the label. The values are the same words, and only their declared type changed.

`LoggingShapeTests` refuses a hole named for an identifier or a path (`…Id`, `…Fingerprint`, `…Path`, `…Directory`, `…Root`) declared as a bare string. That is a lint over names, not the redaction. A hole it misses is withheld across the boundary, not exposed. What the lint prevents is the opposite failure: an identifier withheld from a paired console that should have been shortened and correlated, or released in full in a bundle whose person opted in to paths and nothing more.

### 5. The bundle

`export_diagnostics` (contract 1.52) builds one zip and answers `diagnostic_bundle` with its bytes, base64, because no contract member carries raw bytes. With them come a suggested file name, whether paths are in it, the entries, and how many log records it carries and left out. The service writes no file. The client saves the bytes where its own person chose, which is the reading of T-16 that lets a paired console ask for a bundle at all.

The zip holds:

- `README.txt`, what the bundle carries and leaves out, in words;
- `manifest.json`, the same for a tool;
- `environment.json`, the product, contract, runtime and operating-system versions, setup state, active jobs and how the service is logging;
- `configuration.json`, a projection of `config.json`;
- `status.json`, each set's derived status and each destination's state;
- `notices.json`, the notices still waiting for acknowledgement;
- `jobs.json`, the last fifty jobs;
- `log.txt`, the ring, in the line format of the service's own file, sequence first, so the two line up record for record.

Every field is classified where it is declared in the builder, by the same types the log uses, and rendered by the same rule. A destination's path is a `LogPath`, its fingerprint a `LogId`, its endpoint a string; a set's include and exclude rules are paths, because they name folders; a notice is its kind as a label, its subject (the key after the colon) as an identifier, and its message as a string. A field added to the configuration later is absent from the bundle until someone classifies it in the builder. That is the direction a privacy rule should fail.

**Never in it**, whatever the opt-in. The bundle reads no credential store, session, pairing secret or environment variable. The installation's salt, its sealing and grant keys and its device identity are left out too, although `describe_service` hands them to any client, because each is a durable handle tying the file to every archive the installation wrote. The machine's name is left out because it often names its owner.

**Paths are a per-bundle opt-in**, `include_paths`, rendered `RedactedWithPaths`. It brings the folders backed up, where the destinations are, the file names in the log, and the text no type classifies: chiefly errors and the messages of notices and jobs, which name files more often than not, and which can quote an identifier in full. Every surface states that before anything is built. A paired console may not ask for it: it reads this service's log redacted, and a bundle is not a way round that.

**A bundle is built even when part of it cannot be.** A configuration that fails to load, or a status that fails to derive, is said in its own entry, and the log, usually what explains the failure, still travels. A service composed without logging gets a bundle that says it holds no log.

**It fits a frame.** The bundle carries at most the newest 10 000 records. If the zip is still over 5 MiB, it halves the log, oldest records first, until it fits, and says how many it left out. 5 MiB of base64 inside the result's envelope clears the 8 MiB frame with room to spare.

### 6. The surfaces

`fallbackplan diagnostics-export <file> [--include-paths]` asks the service named by `--state` or `--connect`. It refuses a file already at that path before it asks anything, and writes with create-new, so a file that appears in the meantime is not written over either. With `--include-paths` it states the consequence on standard error before the bundle is built.

The console's Diagnostics view has a Diagnostic bundle card with an unticked "Include file and folder paths" box. Ticking the box shows the consequence at once, before anything is sent, and the button saves the file through the browser under the name the service suggested.

## Consequences

**Positive**

- NFR-PRIV-003 is proved by inspection. A real service backs up a folder with a telling name, with a passphrase and a password in its environment and an identity and a fingerprint in typed records. Its default bundle carries none of them; its opt-in bundle carries the paths and still none of the rest.
- The redaction ADR-0043 promised is now true of everything the service logs, by construction, and a hole added tomorrow is withheld until somebody vouches for it.
- A paired console's log no longer carries an unreadable folder's path, a peer's full fingerprint or the service's state directory. Each was a live leak.

**Negative**

- A paired console sees less. An exception's message and every reason that can carry one read `(withheld)` where they used to read in full. That is the point, and it is still a loss to someone diagnosing at a distance. The local feed and the service's file are unchanged.
- The opt-in releases text no type classifies, which can quote an identifier in full. The consent says so. Separating "paths" from "everything an error might say" would need errors that do not embed what they describe, and that is not this codebase.
- Wrapping is ceremony at every call site, and a local per identifier where CA1873 asks for one.

**Neutral**

- `read_log` keeps its shape. A paired caller reads `(withheld)` in `exception_message` where it read the text, which the 1.52 changelog records.
- Third-party code sharing the factory gets the same rule. Its strings are withheld, which is right, because nothing here can vouch for them.

## What this does not do

- It does not redact the service's own log file, which stays inside the trust boundary in full, and the bundle does not include it. The rolled files were rendered in full when written, and redacting them after the fact would be string matching.
- It does not scrub strings by pattern anywhere. A path inside a string is withheld because the string is, not because it looks like a path.
- It does not include acknowledged notices or the whole job journal: fifty jobs and the open notices answer "what is wrong now".
- It does not give the standalone recovery tool a bundle. [Architecture 08 §5](../architecture/08-restore-and-recovery.md#5-emergency-recovery) asks the tool for one too, and a tool with no service and no ring needs its own design. That remains owed.

  > **Built (2026-10) by [ADR-0082](0082-the-recovery-tools-diagnostic-bundle.md):**
  > the recovery tool's bundle is a report of one run, asked for on that run,
  > and rendered through this record's rule.

## Alternatives considered

**Scrubbing paths out of strings by pattern.** Rejected. NFR-SEC-006 forbids it, and the string it misses is the one that matters.

**Keeping the rendering fail-open and wrapping only the holes known to be risky.** Rejected. It fixes today's list and leaves tomorrow's hole to leak silently. Reviewing every hole was the cost of classifying them in either design.

**Including the on-disk log files.** Rejected. They are written in full, so they could cross the boundary only by being re-redacted as text.

**The service writing the bundle to a path the client names.** Rejected. A service exposes no filesystem access to clients (T-16), and a paired console's path means nothing on this machine.

**The bundle as hex, the way identifiers cross.** Rejected for its size: hex doubles the bytes, and a frame carries 8 MiB. Base64 is still text, named for what it is.

**A data-classification package's redaction attributes.** Rejected. It would be a new dependency judged at ADR-0019's bar, for what two small types and the existing renderer already do.

## Amendment (2026-10): the recovery tool's bundle, and the rule's new home

"What this does not do" left the standalone recovery tool's bundle owed, because a
tool with no service and no ring needed its own design. [ADR-0082](0082-the-recovery-tools-diagnostic-bundle.md)
is that design, and it changes one thing about this record.

The rule in §1 lived in `Diagnostics/LogRecordRenderer`, and the recovery tool may not
reference that project: it carries the concrete logging package and the ring's
collection library, and the tool's project references are an exact whitelist. Copying
the rule into the tool would have made two rules. So the rule moved, unchanged, to
`Domain/Diagnostics/RedactedRendering`, beside the types it reads. The renderer applies
it to records, the service's bundle renders its fields through it as before, and the
recovery tool's bundle renders through it too. An architecture canary fails if the
tool ever grows a copy. The Built line above names where the rule was first built.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first: the fail-closed rendering and its third mode, `LogLabel`, identifiers held as text, `ObjectKey` redaction, every product log hole classified, a paired caller's `read_log` withholding an exception's message, `export_diagnostics` (contract 1.52), the CLI verb and the console's card. |
| 2026-10 | Amended | [ADR-0082](0082-the-recovery-tools-diagnostic-bundle.md): the owed recovery tool's bundle is built, and the rule of §1 moved, unchanged, to Domain so the tool can render through it |
