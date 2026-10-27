# ADR-0111 — A cloud destination says what its store was asked

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-DEST-019 (new: a cloud destination's status says what its store was sent over the thirty days ending today, by operation, with the bytes both ways and when counting began)
**Related:** [ADR-0091](0091-an-s3-compatible-destination.md) (an S3-compatible destination), [ADR-0093](0093-an-azure-blob-destination.md) (an Azure Blob destination), [ADR-0027](0027-services-scheduling-status-telemetry.md) §3 (nothing listens to the in-box instruments), [ADR-0090](0090-a-backups-hints-are-one-pack.md) and [ADR-0092](0092-a-backup-names-its-blobs-a-batch-at-a-time.md) (the request budget, measured in tests), [architecture 05 §5](../architecture/05-storage-providers.md#5-request-economics), [architecture 10 §1](../architecture/10-observability.md#1-user-level-status)

**Built:**
- The seam: `Storage.Abstractions/IStoreRequestObserver`, told of every request as it is sent, once an attempt, with its body's length and the length its answer said its body was. `Storage.S3/S3StoreOptions` and `Storage.AzureBlob/AzureBlobStoreOptions` each take one, and `Storage.S3/S3ObjectStore` and `Storage.AzureBlob/AzureBlobObjectStore` tell it at the one place each sends.
- The record: `Application/StoreRequestLedger`, in `store-requests.json`, by destination and by UTC day, keeping the days the window reads. `Agent/StoreComposition` gives every store it opens an observer for its destination.
- The wire: contract 1.69's `DestinationStatusDescriptor.Requests`, a `StoreRequestsDescriptor` that `Agent/ServiceCommandHandler` fills from the ledger, and null for a destination no store bills by the request.
- The surfaces: `Cli/CliApplication` prints a line under a cloud destination's row of `status`, and the console's destination card has a Requests row.
- The tests:
  - `Storage.ContractTests/StoreRequestObserverTests` — per store API: every operation is told with its bytes; a retried put once an attempt; a listing once a page; a store nobody counts for sends as before
  - `Application.Tests/StoreRequestLedgerTests` — the window's thirty UTC days; each destination apart; an operation no store bills by counts nothing; the count outlives the ledger and no older day is kept; the file is written at most once an interval and closing writes the rest; when counting began; a damaged or newer file is set aside
  - `Hosts.Tests/ObjectStoreDestinationTests`, run by `Hosts.Tests/S3DestinationTests` and `Hosts.Tests/AzureBlobDestinationTests` — the row's counts are every request the store received, and its puts the store's; a local path's row has none; a restart neither loses the count nor counts it twice
  - `Api.Tests/ContractAdditiveFieldsTests`, `Api.Tests/ConfigurationContractTests` — the wire names and the pre-1.69 default
  - `Cli.Tests/StatusRequestsLineTests` — the line, when counting began, and no line where nothing is counted
  - `Web.DomTests/DestinationRequestsDomTests`, `Web.Tests/ConsoleDestinationCardTests`, `Web.Tests/StatusRelayNamesTests` — the card's row, and none for a local path, and the names the console reads

---

## Context

A bucket or a container bills its owner by the request: each put, get, head, listing page and delete. [Architecture 05 §5](../architecture/05-storage-providers.md#5-request-economics) makes the request count a design concern for that reason. Tests hold a backup's requests per GiB to a budget ([ADR-0090](0090-a-backups-hints-are-one-pack.md), [ADR-0092](0092-a-backup-names-its-blobs-a-batch-at-a-time.md)), and a restore's to the blobs it needs.

Those tests measure a fixture. Nothing measured what the service sent a person's store. A person whose bill was larger than they expected could not ask the service what it had sent, or which destination it sent it to.

The process already counted. Each provider adds to an in-box meter, `fallbackplan.store.requests`, at the point it sends. But nothing may listen to that meter ([ADR-0027](0027-services-scheduling-status-telemetry.md) §3, [architecture 10 §5](../architecture/10-observability.md#5-telemetry)): a listener is the line that would turn instruments nobody reads into telemetry. And a meter counts for the process, not for a destination.

## Decision

### 1. A network store tells an observer of every request it sends

A network store's options take an `IStoreRequestObserver`. The provider tells it at the one place it sends a request:
- **Once an attempt.** A store bills each attempt. A put retried twice is three puts, and a listing is one request a page.
- **Answered or not.** An attempt whose connection failed before an answer came back is told too, since the store may have taken it. One its caller cancelled is not: a stopped job has at most one in flight per store.
- **With its bytes:** the request body's length, and the length the answer said its body was. A head carries none. Its length describes the object, and its answer has no body.

An observer is a reader of the requests, never a party to them. A store with none sends exactly as it did. One that cannot record what it is told must not fail the request it was told of.

The words for the operations are the meter's: `put`, `get`, `head`, `list`, `delete`. The S3 provider's delete asks with a head first, because the API answers a delete of nothing as done. That head is billed, so it is told.

### 2. The service keeps the count by destination and by day

The service keeps a ledger in `store-requests.json`, beside the destination ledger. For each destination it holds a UTC day's requests by operation, and the bytes both ways.
- **By destination, not by set.** A store bills for every request, whichever set sent it. Two sets copying to one bucket are one bill.
- **Every store opened for the destination counts.** A sync, a sweep, a drill, a restore, an adoption and a probe all open their store in one place, and that place gives each store its destination's observer.
- **Kept for the window.** A day the window can no longer read is dropped when the file is next written.
- **Written at most once an interval:** thirty seconds. A store is sent thousands of requests an hour, and a write per request would cost more than the request. Stopping the service writes what the interval held back. A service that dies unstopped loses at most that interval's count.
- **Sacrificial, like the ledgers beside it.** A file that cannot be read, or one a newer build wrote, is set aside and the count begins again.

### 3. A cloud destination's row says what its store was asked

`get_status` gains `requests` on each destination row (contract 1.69). It covers the thirty UTC days ending today: the puts, gets, heads, listing pages and deletes, the bytes sent, and the bytes the store's answers said they carried.

**When counting began.** A count that began inside the window says when. Requests sent before that were never counted. A week counted after an upgrade is not a month in which the store was asked that little.

**Null where nothing is counted:** a local path, a peer, a destination no longer declared, and a service before 1.69. A client draws nothing for it, never a row of zeros, which would say a store was asked nothing.

`status` prints the count on a line under the destination's row. The console's destination card gives it a row of its own.

## Consequences

- A person reads, per destination, what the service sent the store their bill comes from, in the store's own units. A destination that is costing more than it should be is visible without reading a log.
- A regression the request budget's tests would not catch shows up on a real installation: a listing repeated each pass, say, or a retry storm against a throttling store.
- A state file the service writes about once every thirty seconds while a store is busy.

## What this does not do

- **Price the requests.** A price belongs to the provider, the region, the storage class and the plan, and changes without notice. The count is what a bill is checked against.
- **Count what a store charges for other than requests.** Storage held is the destination's held bytes. Egress is close to the bytes received, but a store may bill a range differently, and a get's body is counted as the store said it, whether or not all of it was read.
- **Count a request the service did not send.** A lifecycle rule, another client, or the store's own replication bills the same account and is not seen here.
- **Send anything anywhere.** The count stays in the state directory and on the service's own answers. Nothing listens to the in-box meter, and [architecture 10 §5](../architecture/10-observability.md#5-telemetry)'s tests still hold.

## Alternatives considered

- **Listen to the in-box meter.** Rejected. A listener is the line [architecture 10 §5](../architecture/10-observability.md#5-telemetry) forbids, and the meter counts for the process, not for a destination.
- **Count where a caller asks the store.** Rejected. A listing is one call and as many requests as it has pages, and a retried put is one call and as many requests as it took. Counted at the call, the figure would be lower than the bill whenever the store was busy.
- **Count by set.** Rejected. A store bills its owner, not a set, and two sets sharing a bucket would each show part of one bill.
- **Write the file on every request.** Rejected. It would double the work of every request to record that it was made.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. Each network store tells an observer of every request it sends, once an attempt, with its bytes. The service keeps the count by destination and by UTC day, and a cloud destination's status row says what its store was asked over the thirty days ending today, and when counting began where that falls inside them (contract 1.69). The command line and the console say it on the destination's row. |
