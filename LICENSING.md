# Licensing

FallbackPlan is **dual-licensed** ([ADR-0001](docs/adr/0001-licence-and-contribution-model.md)).

## The map

| What | Licence | Why |
|------|---------|-----|
| All source code, tests, tooling, and documentation in this repository (default) | **AGPL-3.0-only** ([`LICENSE`](LICENSE)) | Every derivative — distributed *or offered as a network service* — must remain open. For a backup product, trust is the currency; the copyleft guarantee is deliberate. |
| Everything under [`specifications/`](specifications/) — the repository-format specification, the conformance generator, vectors, and fixture repositories | **Apache-2.0** ([`specifications/LICENSE`](specifications/LICENSE)) | Independent readers are the format's "your data is not locked in" promise (NFR-COMP-004). Anyone may implement a reader from the specification and conformance suite under **any licence they choose**, owing this project nothing. The permissive carve-out exists precisely so the copyleft on the code never deters an independent implementation. |
| A future legacy-archive importer (`FallbackPlan.Import.<Format>`, none yet written) | Its own statement, decided at [ADR-0015](docs/adr/0015-legacy-importer-isolation.md)'s gate | Isolated so its legal posture never couples to the core. |

## What "dual" means

The AGPL-3.0 grant is available to everyone, unconditionally. Separately,
**commercial licences are available from the maintainer** for uses the AGPL
does not fit — embedding in a proprietary product, or offering a hosted
service without the network-source obligation. Contact the maintainer to
discuss terms.

Dual licensing is possible because the project currently has a **single
copyright holder**. External code contributions therefore require a
contributor licence agreement (CLA) that preserves the dual-licensing
right; until a CLA is published, external contributions cannot be merged —
see [`CONTRIBUTING.md`](CONTRIBUTING.md).

## Charging and the AGPL

Selling binaries, packaged builds, or support for the AGPL edition is
permitted — the AGPL obliges giving *recipients* the corresponding source,
not giving everything away. The FallbackPlan name is a project identifier;
licence rights to the code are not rights to the name.

## Third-party components

Bodu packages (`external/packages/`, ADR-0021) are MIT-licensed upstream.
All other dependencies are consumed from nuget.org under their own
licences; the lockfiles enumerate them exactly.
