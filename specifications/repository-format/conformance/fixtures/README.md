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
`tests/FallbackPlan.Repository.ConformanceTests/FixtureRepositoryV2.cs`.
There is **no byte-identical regeneration**: sealing takes a fresh random
content key and a fresh ephemeral X25519 share per blob, by design, so no
two generations share bytes. What this committed copy freezes is the **read
contract**, enforced by `FixtureRepositoryV2Tests` on every run: the
descriptor verifies by derive-and-compare (and refuses the wrong
passphrase), the derived write bundle alone opens the structure plane —
record tables, manifests, catalogue rebuild, journal — and answers
`ContentSealed` for content, and the passphrase-derived authority restores
`fixture.bin` byte-identically.

A format-1 fixture (`fixture-repository-v1`, with a key object and a
byte-identical regeneration) and its committed recovery kit lived here while
format v1 did. Both were withdrawn with the format, before any freeze; the
git history holds them.

These files are marked `binary` in `.gitattributes` — no text normalisation
may ever touch them — and they are deliberately not gitignored: CI's
source-archive build must see them.
