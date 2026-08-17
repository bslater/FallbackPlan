# FallbackPlan

> FallbackPlan gives every computer a safe fallback: encrypted, versioned copies held on computers and storage that the user chooses.

An open-source backup and archival platform for Windows, macOS, and Linux. Its purpose is to restore a capability the consumer market has largely withdrawn — backing up one computer to another computer you control or trust, across a LAN or the internet, with no proprietary cloud service in between.

**Status: Phase 0 ready to start.** The architecture is reviewed, the repository format is specified with conformance vectors, and the solution scaffold builds. Engine implementation has not begun.

---

## Start here

📖 **[Documentation index](docs/README.md)**

| | |
|---|---|
| What this is | [Overview](docs/architecture/00-overview.md) |
| How the repository format works | [Repository format](docs/architecture/02-repository-format.md) |
| What changed in review, and why | [Architecture review](docs/review/2026-08-architecture-review.md) |
| What is still undecided | [Open questions](docs/open-questions.md) |
| What gets built, in what order | [Roadmap](docs/roadmap.md) |
| The on-disk format, normatively | [Repository format specification](specifications/repository-format/README.md) |
| What to build first | [Phase 0 execution plan](docs/phase-0-execution-plan.md) |

## Principles

1. **Recovery is the product.** Backup completion is not enough; recoverability must be verified.
2. **You own the repository.** No FallbackPlan-operated cloud account is required, ever.
3. **Open format, open protocol, open implementation.** Published and versioned, so someone else can write a reader.
4. **No silent destructive synchronisation.** Deleting a file writes history; it does not erase it.
5. **Encryption before transport.** Storage nodes and cloud providers never see plaintext content or filenames.
6. **Metadata scales incrementally.** No repository-wide monolithic manifest.
7. **Every durable object is independently verifiable.** Corruption stays localised and detectable.
8. **Caches are disposable.** The repository is the source of truth and can rebuild the rest.
9. **Interruption is normal.** Every operation survives termination, connectivity loss, and eventually consistent storage.
10. **Compatibility is isolated.** Legacy importers never dictate the native design.

## Where it is going

| Phase | Goal |
|-------|------|
| 0 | Archive engine vertical slice — segment, encrypt, pack, index, rebuild, restore |
| 1 | Snapshot and local repository MVP |
| 2 | Peer-to-peer backup — computer to computer |
| 3 | Cloud object stores — Azure Blob, S3 |
| 4 | Retention, pruning, and healing |
| — | **Format v1 freeze gate** |
| 5 | Legacy archive import preview |
| 6 | Consumer-ready release |

Details in the [roadmap](docs/roadmap.md).

## Building

```bash
dotnet build FallbackPlan.slnx -c Release
dotnet test  FallbackPlan.slnx -c Release
```

Requires the .NET SDK pinned in [`global.json`](global.json). Warnings are errors. A plain `git clone` and a GitHub "Download ZIP" both build — all dependencies restore from this tree and nuget.org, and `FallbackPlan.slnx` opens directly in Visual Studio 2022 17.14+ on Windows. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Licence

**Dual-licensed** ([ADR-0001](docs/adr/0001-licence-and-contribution-model.md)): the code is **AGPL-3.0-only** ([LICENSE](LICENSE)) — every derivative, distributed or hosted, stays open — with commercial licences available from the maintainer for uses the AGPL does not fit. The repository-format **specification and conformance suite are Apache-2.0** ([specifications/LICENSE](specifications/LICENSE)), so independent readers can be implemented under any licence, owing this project nothing. The full map is in [LICENSING.md](LICENSING.md). External code contributions remain unmergeable until a CLA is published — see [CONTRIBUTING.md](CONTRIBUTING.md).

## A note on legacy archive import

FallbackPlan is not affiliated with, endorsed by, or derived from any other backup product or its vendor. Importing an archive written by another product is a separate, optional, read-only compatibility component, gated on legal review ([ADR-0015](docs/adr/0015-legacy-importer-isolation.md)), and treated as experimental until validated against real archives. It never modifies a source archive.
