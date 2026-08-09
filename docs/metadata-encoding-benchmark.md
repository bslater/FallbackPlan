# Metadata encoding size — canonical CBOR against what it replaced

**Status:** published · **Settles:** [Q4](open-questions.md#closed)'s encoding-size half · **Decides nothing new:** [ADR-0003](adr/0003-canonical-metadata-encoding.md) is Accepted; this measures what it costs and corrects one of its stated reasons

---

## Question under test

ADR-0003 chose canonical CBOR and rejected three alternatives. Two of the three rejections rest partly on size, and neither had a number behind it:

- **A bespoke binary format** — "full control over canonicality, but every independent implementer must reimplement it from our prose". The unstated premise is that CBOR's self-description costs enough to make that trade worth considering.
- **JSON / JCS** — "the size cost at scale **L** is unacceptable for metadata".

Q4 asked for an encoding-size benchmark on realistic manifests before the format freezes. This is it.

## Method

`MetadataEncodingBenchmark` (`tests/FallbackPlan.PerformanceTests`, run with `dotnet run -c Release -- metadata-size`) generates a deterministic corpus of 20 000 file-version manifests and 2 000 tree manifests, encodes each through the **production codec**, and measures three sizes per object.

**The corpus shape is taken from the requirements rather than invented.** [Reference scale L](requirements/non-functional.md#reference-scales) is 100 M file versions against 500 M segment references, so a file version averages five references; the size distribution is built to land on that ratio and does — the run below measures 5.10. Names are 8–40 bytes, metadata is a full POSIX capture (times, mode, owner and group names), 15 % of versions carry an extended attribute, and 85 % carry a `parent_version`, because a repository whose manifests mostly had no parent would be one nobody had backed up twice.

**The two comparisons are derived from the encoded CBOR itself**, not from a parallel hand-written model of each manifest, so neither can drift from the codecs as fields are added.

- **Payload floor** — the bytes a format with no self-description could not avoid: byte-string and text-string contents, the minimal big-endian width of each integer, and a minimal length prefix per variable-length item. Map keys cost nothing, on the basis that a bespoke format knows its own field order, and booleans cost nothing, on the basis that flags pack into a header. It is therefore a **lower bound** on any real bespoke encoding — deliberately generous to the alternative, which makes CBOR's overhead look larger than a competitor would actually achieve.
- **Canonical JSON** — the same logical content with integer map keys as quoted strings and byte strings as base64, which is what JSON costs and why.

## Results

Machine: 4-core Intel Xeon @ 2.80 GHz container (sub-reference), .NET 10. Sizes are deterministic — the corpus is seeded, and the numbers reproduce exactly.

Corpus: 20 000 file versions (5.10 segment references each), 2 000 directories (27.9 entries each).

| Object | CBOR mean | Payload floor | CBOR overhead | Canonical JSON | JSON over CBOR |
|--------|----------:|--------------:|--------------:|---------------:|---------------:|
| File-version manifest | 386.0 B | 346.9 B | 11.2 % | 591.2 B | 1.53× |
| …of which single-segment (13 499 of 20 000) | 205.0 B | 178.4 B | 14.9 % | 334.1 B | 1.63× |
| Tree manifest | 1 773.7 B | 1 718.8 B | 3.2 % | 2 540.2 B | 1.43× |
| Tree manifest, per entry | 63.7 B | 61.7 B | 3.2 % | 91.2 B | 1.43× |

Extrapolated to scale L — 100 M file versions and 10 M directories, metadata plane only:

| Encoding | Total | Against CBOR |
|----------|------:|-------------:|
| Payload floor | 48.3 GiB | 0.92× |
| **Canonical CBOR** | **52.5 GiB** | — |
| Canonical JSON | 78.7 GiB | 1.50× (+26.3 GiB) |

## What it says

**The bespoke-format argument is retired.** Canonical CBOR costs **8.6 %** over a floor computed to flatter a bespoke encoding, and the floor ignores the length prefixes, alignment padding and version framing a real one would carry — so the true gap is smaller still. Eight per cent is not a price worth paying to make every independent implementer write a parser from prose, which is the trade NFR-COMP-004 exists to refuse. The rejection stands and now has a number.

**Overhead falls as objects grow, and the worst case is the common one.** A single-segment file version — 67 % of the corpus, and the shape of most files in most backups — pays 14.9 %, because everything but the one segment reference is fixed cost. A tree manifest pays 3.2 %, because an entry is three fields of which two are large. Nothing in the distribution gets worse than the single-segment case, which is the useful bound: **CBOR's overhead on this format is between 3 % and 15 %, and never more.**

**ADR-0003's reasons for rejecting JSON were ranked wrong.** The size cost is real — 1.50×, and 26 GiB at scale L is not nothing — but "unacceptable" overstates it. A 50 % metadata premium against a 50 TB logical repository is 0.05 % of the data it describes, and reasonable people ship formats with worse ratios. **The determinism argument is the one that decides it**: JSON's number handling disagrees across languages, and this format derives object identifiers from encoded bytes, so an encoder that two implementations disagree about is an encoder that silently breaks deduplication and verification. That argument is dispositive on its own, and the size argument was never needed. ADR-0003 now says so.

**The measurement does not change the decision.** It was never going to: CBOR's competitors lose on canonicality and library availability, and this exercise could only have found a size cost large enough to reopen the question. It did not.

## What this does not settle

The other half of Q4 — an **independent reader**, written from the published specification by someone who did not write the format, producing byte-identical output — is [freeze-gate item 2](roadmap.md#format-v1-freeze-gate) and remains open. `conformance/generate.py` builds the same objects with its own CBOR encoder in another language and agrees byte for byte on every run, which is a second implementation rather than an independent one; the same author wrote both, and that is precisely the limitation gate item 2 exists to remove.

---

**See also:** [segmentation benchmark](segmentation-benchmark.md) (freeze-gate item 1) · [phase-0 benchmarks](phase-0-benchmarks.md) · [ADR-0003](adr/0003-canonical-metadata-encoding.md)
