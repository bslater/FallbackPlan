# Conformance fixtures

Committed, frozen repositories for cross-implementation and cross-version
verification (wave F7; NFR-COMP-004). Everything here is **synthetic**: fixed
test keys, fixed salts, generated content. Nothing originates from a real
machine, and nothing here is secret.

## fixture-repository-v2

A complete, tiny **write-only** repository (ADR-0042; format 2,
specification 03 §9): a deterministic 200 000-byte `fixture.bin`
(concatenated `SHA-256(BE64(i))`) as four `fixed-v1` 64 KiB segments,
compression `none`, and the six-step sequence layout a real publication
produces (specification 08 §2: 1 intent · 2 data blob · 3 metadata blob ·
4 standalone snapshot · 5 delta · 6 retirement) — with **no key object
anywhere**: the descriptor records the sealing public key, the KDF salt and
the parameters, and every key derives from the fixture passphrase
`fallbackplan-fixture-v2-passphrase` (deliberately small Argon2id parameters
— 8 MiB, 1 iteration, 1 lane — below the creation minimums, accepted on open
with a warning per specification 03 §2, so the suite stays fast; a
fixture-speed decision, not an endorsement). The data blob's records are
sealed under a per-blob content key wrapped to the repository public key;
its footer — the structure plane — opens under the metadata class key.

| Object | What it is |
|---|---|
| `repository-format` | Descriptor: repository id, format 2, the sealing public key, the KDF salt and parameters, `unstable_format = true` |
| `blobs/data/…` | One sealed data blob: `fixture.bin` as four segments |
| `blobs/meta/…` | One metadata blob: file-version, tree, policy, and signed snapshot manifests |
| `snapshots/…` | The standalone `FBPKSREC` copy of the signed snapshot |
| `index/delta/…` | One signed index delta covering both blobs |
| `journal/…` | The write intent (sequence 1) and its retirement (sequence 6) |

**Provenance and the read contract.** The generator is
`tests/FallbackPlan.Repository.ConformanceTests/FixtureRepositoryBuilder.cs`,
which every fixture drives with its own identity. There is **no
byte-identical regeneration**: sealing takes a fresh random content key and a
fresh ephemeral X25519 share per blob, by design, so no two generations share
bytes. What this committed copy freezes is the **read contract**, enforced by
`FixtureRepositoryV2Tests` on every run: the descriptor verifies by
derive-and-compare (and refuses the wrong passphrase), the derived write
bundle alone opens the structure plane — record tables, manifests, catalogue
rebuild, journal — and answers `ContentSealed` for content, and the
passphrase-derived authority restores `fixture.bin` byte-identically.

## fixture-repository-v3

The same six objects and the same deterministic `fixture.bin`, written at
**format 3** ([ADR-0052](../../../../docs/adr/0052-relocatable-records-format-v3.md)
Amendment 1) under the passphrase `fallbackplan-fixture-v3-passphrase`. What
differs on disk is the whole point of the revision: the descriptor declares
format 3 and `relocatable-records` as a **required** feature, so a reader
that does not implement it refuses the repository by name instead of
misreading it; the data blob's envelope carries **no** sealed share, because
each of its records carries one in its own prefix; and every record — in both
the data and metadata blobs — carries a random 12-byte nonce and is keyed to
its object rather than to its container. Metadata blobs are stamped 3 here,
where a format-2 repository stamps its metadata blobs 1. The standalone
snapshot and the journal records stay format-1 containers, as they do in
every repository: they are never inside a blob and never relocated.

Byte-identical regeneration is further out of reach than for format 2, not
closer — a format-3 record draws a fresh nonce and a fresh share each.
`FixtureRepositoryV3Tests` holds the same read contract on these bytes, and
one more that only frozen bytes can establish: a data record lifted verbatim
out of this blob, and appended into a blob written later by a different
writer under a different salt at a different ordinal, still opens.

## fixture-repository-v4

The same six objects and the same deterministic `fixture.bin`, written at
**format 4** ([ADR-0096](../../../../docs/adr/0096-format-4-an-index-generation-per-delta.md))
under the passphrase `fallbackplan-fixture-v4-passphrase`. Format 4 changes
the index delta and nothing else, so everything sealed here is format 3's and
stamped 3. What differs on disk is the descriptor, which declares format 4
and lists `index-generations` beside `relocatable-records` as **required**
features, so a reader that does not rank by index generation refuses the
repository by name; and the one delta, which carries key 12,
`index_generation`, at 1, the first any repository holds.

`FixtureRepositoryV4Tests` holds the read contract on these bytes: the
descriptor names format 4 and both features, every blob is stamped 3, the
index loads with no finding and every entry ranks at index generation 1, a
catalogue rebuilt from it holds that as its highest, and the passphrase
restores `fixture.bin` byte for byte. Regeneration is no more byte-identical
than format 3's.

## fixture-repository-v5

The same six objects and the same deterministic `fixture.bin`, written at
**format 5** ([ADR-0097](../../../../docs/adr/0097-format-5-a-checkpoint-carries-its-deltas.md))
under the passphrase `fallbackplan-fixture-v5-passphrase`, plus a seventh: a
checkpoint at sequence 7 (`index/checkpoint/…`) carrying the one delta's
stored bytes at key 10, beside its identifier. Format 5 changes the
checkpoint and nothing else, so the delta is format 4's, carrying index
generation 1, and everything sealed is format 3's and stamped 3. The
descriptor declares format 5 and lists `delta-checkpoints` beside
`index-generations` and `relocatable-records` as **required** features.

`FixtureRepositoryV5Tests` holds the read contract on these bytes: the
descriptor names format 5 and its three features, every blob is stamped 3,
the index loads with no finding from the checkpoint without fetching the
delta at its key, the checkpoint carries nothing of its own, the same index
loads from a copy with the delta gone from its key, and the passphrase
restores `fixture.bin` byte for byte. Regeneration is no more
byte-identical than format 3's.

## fixture-repository-v6

The same seven objects, written at **format 6**
([ADR-0184](../../../../docs/adr/0184-format-6-a-manifest-keeps-a-files-flags-and-a-links-form.md))
under the passphrase `fallbackplan-fixture-v6-passphrase`. Format 6 changes
what a manifest may say and nothing else, so the checkpoint carries the one
delta as at format 5, and everything sealed is format 3's and stamped 3. The
descriptor declares format 6 and lists `file-flags-and-link-forms` beside
`delta-checkpoints`, `index-generations` and `relocatable-records` as
**required** features.

The snapshot's tree holds two entries. `fixture.bin` is the same file, whose
manifest keeps the no-dump flag in metadata key 11. `fixture-junction` is a
link to `C:\FixtureFolder` whose manifest carries key 14 at 1, naming it a
mount point, as a Windows junction is.

`FixtureRepositoryV6Tests` holds the read contract on these bytes: the
descriptor names format 6 and its four features, every blob is stamped 3,
the index loads with no finding and its delta is carried in the checkpoint,
a catalogue rebuilds with no finding, the file's manifest keeps the no-dump
flag and the passphrase restores `fixture.bin` byte for byte, and the
junction's manifest names it a mount point with its target. Regeneration is
no more byte-identical than format 3's.

A format-1 fixture (`fixture-repository-v1`, with a key object and a
byte-identical regeneration) and its committed recovery kit lived here while
format v1 did. Both were withdrawn with the format, before any freeze; the
git history holds them.

These files are marked `binary` in `.gitattributes` — no text normalisation
may ever touch them — and they are deliberately not gitignored: CI's
source-archive build must see them.
