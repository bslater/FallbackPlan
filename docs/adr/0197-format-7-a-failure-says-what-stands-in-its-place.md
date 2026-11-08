# ADR-0197 — Format 7: a failure says what stands in its place

**Status:** Accepted
**Date:** 2026-11
**Requirements:** NFR-COMP-004 (amended: `fixture-repository-v7`). Does not establish FR-RST-003 or FR-RST-004: no restore reads the key yet, and nothing writes it
**Related:** [ADR-0194](0194-a-restores-plan-names-the-files-an-earlier-version-stands-in-for.md) (a restore's plan names the files an earlier version stands in for, and its Amendment 3: by whether an earlier snapshot holds the version), [ADR-0185](0185-a-run-stops-fetching-online-only-files-after-the-first-that-does-not-come.md) (a file not fetched keeps its version before, Amendment 3, and a folder not listed its tree, Amendment 6), [ADR-0014](0014-format-versioning-and-stability.md) (refuse by name), [ADR-0066](0066-the-format-upgrade-record.md) (the upgrade record), [ADR-0184](0184-format-6-a-manifest-keeps-a-files-flags-and-a-links-form.md) (format 6, whose manifests this keeps)

**Built:**
- The constants: `Domain/FormatVersions` (`StandInFailures` = 7) and `Domain/FormatLimits`, which create and upgrade at 7.
- The wire: `Repository.Format/Manifests/PolicyManifest` carries the failure's new field, and its error manifest codec reads and writes failure key 4, refusing any value but 1. `Repository.Format/Descriptor/RepositoryDescriptorCodec` adds feature `0x0007`, and `Repository/RepositoryLifecycle` lists it at format 7.
- The tests:
  - `Repository.Tests/Format/ManifestCodecTests`: key 4 round-trips; a failure that says nothing is byte for byte format 6's; a key 4 of 0 or 2 is refused; key 5 is unknown
  - `Repository.Tests/Format/RepositoryDescriptorCodecTests`, `Repository.Tests/EndToEnd/RepositoryLifecycleTests`, `Domain.Tests/FormatVersionsTests`: a format-7 descriptor lists all five features, either mismatch is a violation, format 8 is refused naming 2 to 7, a repository is born at 7, and format 6 is still creatable without `0x0007`
  - `Hosts.Tests/CreatedFormatTests`: a set created today is format 7 and lists its feature
  - `Repository.FuzzTests/FuzzCorpus`: the decoder is fuzzed from an error manifest whose failure carries key 4
  - `Repository.ConformanceTests/FixtureRepositoryV7Tests`: the committed `fixture-repository-v7` declares format 7 and its five features, keeps format 3's blobs, format 5's index and format 6's manifests, rebuilds a catalogue with no finding, and holds a partial snapshot whose one failure names the file its tree holds, saying in key 4 what stands there; the file restores byte for byte

---

## Context

A snapshot's failures name each path the backup could not capture (specification 06 §8). Two records put something at such a path all the same:

- **A file the backup could not fetch keeps its version before** ([ADR-0185 Amendment 3](0185-a-run-stops-fetching-online-only-files-after-the-first-that-does-not-come.md#amendment-3-2026-11--a-file-not-fetched-keeps-its-version-before)), and **a folder it could not list keeps its tree before** ([Amendment 6](0185-a-run-stops-fetching-online-only-files-after-the-first-that-does-not-come.md#amendment-6-2026-11--a-folder-its-provider-holds-keeps-its-tree-when-its-listing-does-not-come)). The failure's detail says so in words.
- **A file whose alternate stream alone failed** is captured afresh, and its failure names the file too.

A restore's plan names each file it restores as the version backed up before ([ADR-0194](0194-a-restores-plan-names-the-files-an-earlier-version-stands-in-for.md)). Nothing in the snapshot says which failures put an earlier version in place, so [Amendment 3](0194-a-restores-plan-names-the-files-an-earlier-version-stands-in-for.md#amendment-3-2026-11--an-earlier-version-is-one-an-earlier-snapshot-holds) decides by asking whether an earlier snapshot of the set holds the very version. That holds while one does. Once retention has forgotten every snapshot that held it, nothing says the version is an earlier one, and the plan names nothing.

The backup knows when it does it. A failure's keys are closed, and a reader refuses one it does not know rather than skip it, so saying it is a key, and a key is a format change.

## Decision

### 1 Failure key 4 says what stands in the failure's place

Key 4 of a failure entry, `stands_in`, is a u8 whose one value, 1, says that what the snapshot's tree holds at the failure's path is what the snapshot before held there: a file's version, or a folder's tree. It is absent where it would say nothing, so a failure that says nothing is byte for byte the one format 6 writes. A reader refuses a key 4 of any other value, at read, as a second encoding of nothing.

The key says what the backup did, not why. The detail still says why, in words.

### 2 Format 7 names itself, and the product creates at it

A format-7 descriptor lists `0x0007` (`stand-in-failures`) beside `0x0003`, `0x0004`, `0x0005` and `0x0006`. An earlier descriptor never lists it, and either mismatch is a format violation ([01 §3.2](../../specifications/repository-format/01-object-layout.md#32-body)). So a reader that predates format 7 refuses a repository created at it by name, at open.

Format 7 changes what a failure may say and nothing else. Its manifests are format 6's, its index format 5's, and its blobs and records format 3's, stamped 3 ([00 §5](../../specifications/repository-format/00-conventions.md#5-versioning-and-feature-negotiation)). `FormatLimits.FormatVersion` is 7, so a repository is born at 7, and formats 2 to 6 remain creatable on request and readable for ever. A set below format 7 is upgraded on request by [ADR-0066](0066-the-format-upgrade-record.md)'s record, to the latest.

### 3 A writer writes the key only from format 7

A reader that predates format 7 refuses an error manifest carrying key 4 as an unknown key, and reports it as damage. So a repository such a reader is entitled to read must never hold one. `FormatVersions.HasStandInFailures` is the gate the writer asks.

Nothing writes the key yet. The writer and the reader come in the next record: publication sets it where it keeps a file's version or a folder's tree, from format 7, and a restore's plan names a file from it where the snapshot carries it, and by ADR-0194 Amendment 3's rule where it does not.

## Consequences

**Positive**
- **The backup can say what it did.** A restore no longer needs an earlier snapshot to know a version is an earlier one, once a writer sets the key.
- **Nothing a format-6 failure says changes.** A failure that says nothing is byte for byte format 6's, so a repository created at 7 holds format 6's error manifests until a writer has something to say.

**Negative**
- **A build that predates format 7 cannot open a repository created at it.** It refuses it by name at open, as ADR-0014 asks.
- **An older reader of an upgraded repository will report an error manifest carrying the key as damage.** The descriptor of a repository upgraded to 7 still says what it was created at, so an older build opens it, refuses each such error manifest's key, and reports an object it cannot read. It cannot then say what its snapshot failed. That is ADR-0066's stated price, as at formats 4 to 6, and it is paid only once a writer sets the key. A repository created at 7 is refused at the door.

## Alternatives considered

- **Keep deciding by the snapshots before (ADR-0194 Amendment 3) alone.** It needs no format change, and it cannot name a kept version once retention forgets every snapshot that held it.
- **Read it from the detail's words.** The words are the platform's and the product's, may be reworded, and are not a contract a reader can hold a writer to.
- **Name a stream's failure at a path the tree does not hold**, such as `file:stream`. Every reader that reads a failure's path as a file's would change, and the next backup would no longer retry the file a failure names.
- **A key on the tree entry.** A tree entry says what is there, not that it was carried from before, and has no key to spare without a format change of its own.

## What this record does not do

- **Write or read the key.** Publication sets nothing yet, and no restore reads it. Both are the next record's, held to format 7.
- **Say why in a key.** The detail says why, as before.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-11 | Accepted | Built in one slice, tests first, as format 6 was: failure key 4 and feature `0x0007`; a repository is born at 7, formats 2 to 6 stay creatable, and format 8 is refused naming 2 to 7; the committed `fixture-repository-v7` holds a partial snapshot whose failure carries the key. Nothing writes or reads it yet. `Domain/FormatVersions`; `Domain/FormatLimits`; `Repository.Format/Manifests/PolicyManifest`; `Repository.Format/Descriptor/RepositoryDescriptorCodec`; `Repository/RepositoryLifecycle`; `Repository.Tests/Format/ManifestCodecTests`; `Repository.ConformanceTests/FixtureRepositoryV7Tests` |
