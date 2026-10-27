# ADR-0106 — A closed circuit says what it did not read

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-VER-003 (amended: a destination's sweep says how many blobs the circuit that last closed passed over held only in an archival tier, and only a circuit that passed over none says every stored object was read back)
**Related:** [ADR-0104](0104-a-replicas-readers-take-an-archived-object-as-archived.md) (the sweep passing over an archived blob, and the record that left the status row as it was), [ADR-0035](0035-destination-fitness.md) Amendment 3 (the sweep on the status row, and the claim a closed circuit supports), [ADR-0095](0095-the-staging-archive-heals.md) (the staging archive's sweep, in the same words), [architecture 09 §5.5](../architecture/09-replication-and-peers.md#55-re-reading-what-is-held-and-repairing-it)

**Built:**
- The record: `Application/DestinationSyncStore` (schema 12) keeps how many blobs the circuit that last closed passed over, the circuit's running count taken as it closes.
- The wire: contract 1.66's `DeepSweepDescriptor` carries it as `circuit_archived`, which `Agent/ServiceCommandHandler` fills from the ledger.
- The surfaces: `Cli/CliApplication` prints `sweep:partial@…` for a circuit that closed past archived blobs, and the console's destination card says how many it did not read, in place of "every stored object read back".
- The tests:
  - `Application.Tests/DestinationSyncStoreTests` — schema 12: a schema-11 row migrates with nothing counted; a circuit's count is every segment's, kept until the next circuit closes, and none after one that met none
  - `Api.Tests/ContractAdditiveFieldsTests`, `Api.Tests/ConfigurationContractTests` — contract 1.66's wire name and its pre-1.66 default
  - `Cli.Tests/StatusSweepTokenTests` — a circuit that closed past archived blobs prints `partial`, not `ok`
  - `Web.Tests/ConsoleDestinationCardTests`, `Web.DomTests/ConsoleViewsDomTests` — the card gives the count, and only a circuit that passed over none says every stored object was read back
  - `Hosts.Tests/ObjectStoreDestinationTests`, run by `Hosts.Tests/S3DestinationTests` and `Hosts.Tests/AzureBlobDestinationTests` — the status row says how many blobs the closed circuit did not read, and none after a circuit that met none

---

## Context

A deep sweep re-reads every blob at a destination over a circuit. Since [ADR-0104](0104-a-replicas-readers-take-an-archived-object-as-archived.md) it passes over a blob the store holds only in an archival tier, and the circuit closes past it. That record left the status row as it was. The notice said what the store held archived, and the row's sweep still said when a circuit last closed.

But every surface read the close as a claim. [ADR-0035](0035-destination-fitness.md) Amendment 3 made `circuit_closed_at` "the one fact that supports every stored object was read back and matched its seal". The console's destination card said "every stored object read back", and the CLI's row printed `sweep:ok@` the day it closed. A replica whose data blobs were all archived was shown as read back whole, the day after a circuit had read none of them.

The notice could not correct it. A person acknowledges a notice once per run of meetings, and the row is read on every status.

## Decision

### 1. The ledger keeps what the closed circuit passed over

The ledger already counts what the circuit under way has passed over (ADR-0104). When a circuit closes, that count, with what its closing segment passed over, becomes the closed circuit's. It stands until the next circuit closes, as `circuit_closed_at` does. A circuit that met none keeps none.

The ledger moves to schema 12. An older row counted nothing apart, so it reads as none until its next circuit closes.

### 2. It crosses as `circuit_archived` (contract 1.66)

`DeepSweepDescriptor` gains `circuit_archived`, beside `circuit_closed_at`. It is zero when the circuit read every blob, and a service before 1.66 sends none. A staging archive's sweep meets no archival tier and sends none.

### 3. Only a circuit that passed over none is every object read

- **The CLI** prints `sweep:partial@` the day such a circuit closed, never `sweep:ok@`. A script that reads `ok` as every stored object read back stays right.
- **The console** says "every stored object but N held only in an archival tier read back", on the stalled line as on the closed one.

Nothing else about the sweep changes. A stall is still said first, and a circuit under way is still progress.

## Consequences

- A destination whose store archives a backup's older blobs shows its sweep as partial, with how many blobs it could not read, on every status.
- A circuit that closes after the blobs are rehydrated reads every one, and the row goes back to every stored object read back.
- The ledger moves to schema 12. A build before it sets a newer ledger aside, as for every schema step.

## What this does not do

- **Name the blobs, or the files that need them.** A restore's plan names the files ([ADR-0103](0103-a-restore-plan-names-what-is-held-only-in-an-archival-tier.md)).
- **Count what a read-back or a drill met.** They sample a few objects, and a circuit reads every blob. Their meetings are the notice's (ADR-0104, [ADR-0105](0105-a-drill-takes-archived-content-as-a-stated-limit.md)).
- **Count a circuit closed before schema 12.** It reads as passing over none until the next one closes.

## Alternatives considered

- **Leave it to the notice.** Rejected. A notice is acknowledged once, and the row's claim is read on every status.
- **Withhold `circuit_closed_at` from a circuit that passed over anything.** Rejected. The circuit did close, and re-read everything that could be read. Hiding the close would make a destination read as never swept.
- **Keep printing `ok`, with the count beside it.** Rejected. A script that reads `ok` as every stored object read back would be wrong without changing a line.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The ledger (schema 12) keeps how many blobs the circuit that last closed passed over held only in an archival tier, contract 1.66 carries it as `circuit_archived`, the CLI prints `sweep:partial@` for such a circuit, and the console says how many it did not read rather than every stored object. |
