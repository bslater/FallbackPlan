# 10 — Compression

**Normative.** Derived from [`02-repository-format.md` §4](../../docs/architecture/02-repository-format.md#4-compression).

---

## 1 Profiles

| Profile | Value | Codec |
|---------|-------|-------|
| `none` | `0x0000` | Stored uncompressed |
| `zstd-v1` | `0x0001` | Zstandard, [RFC 8878](https://www.rfc-editor.org/rfc/rfc8878) frame format |

The profile is recorded **per record** ([04 §2](04-record.md#2-framing)), not per repository or per file. A single blob may hold records under both, and a reader never has to guess.

## 2 Ordering

**Compression happens before encryption.** Always, and there is no configuration that reverses it.

Encrypted output is indistinguishable from random, so compressing after encrypting saves nothing while costing CPU. The ordering is therefore forced, not chosen.

It has a consequence worth stating plainly rather than burying: because stored lengths reflect compressed sizes, they leak information about content. Compressed size fingerprints file types and, for some inputs, individual files. A store operator sees this legitimately — it is not a leak through a mistake, it is the price of compressing at all. → [T-11](../../docs/threat-model.md#t-11-metadata-side-channels)

An optional record-padding policy for high-sensitivity backup sets is under consideration ([Q10](../../docs/open-questions.md#q10--padding-policy)); it is not part of format v1. Where padding matters more than storage, the honest answer today is to disable compression for that backup set, which removes the channel entirely at a cost in space.

## 3 The storage threshold

A segment is stored **uncompressed** when compression saves less than the configured fraction of its plaintext length.

```text
store compressed   iff   compressed_length <= logical_length × (1000 − threshold_permille) / 1000
```

| Parameter | Default |
|-----------|---------|
| `compression_threshold_permille` | 50 (5%) |

The threshold is recorded in the policy manifest ([06 §7](06-manifests.md#7-policy-manifest)) so a snapshot can always report the setting it was captured under.

The reason for a threshold rather than "compress if smaller": a segment that compresses by 0.3% costs decompression CPU on every read, forever, to save almost nothing. Incompressible data — already-compressed media, encrypted files, random blobs — is a large fraction of most real backup sets, and paying for it twice is a real cost.

Recording the choice per record means a benchmark can measure how often the threshold fires, rather than the setting being an untested guess.

## 4 Zstandard parameters

| Parameter | Default | Range |
|-----------|---------|-------|
| Compression level | 3 | 1 – 19 |
| Window log | Bounded to the segment size | — |
| Dictionary | None in v1 | — |

Level 3 is the default because this runs continuously in the background on consumer hardware, where a level that halves throughput to gain a few percent is the wrong trade. Levels above 19 (`--ultra`) are not permitted: their memory requirements are unbounded relative to segment size, which conflicts with the bounded-memory guarantee.

Dictionaries are excluded from v1. They would improve the ratio on many small similar files, and they introduce a shared object that every record referencing it depends on — a new failure mode and a new lifecycle. If added, it will be as a new profile.

### 4.1 Frame requirements

A writer MUST emit a single standard Zstandard frame per record. It MUST NOT use skippable frames, multi-frame concatenation, or the raw block format.

A reader MUST enforce a decompressed-size limit equal to the record's `logical_length` and MUST refuse a frame that attempts to exceed it. A decompressor that trusts the frame's declared content size is a decompression-bomb vulnerability, and repository objects come from parties this format does not trust. → [00 §8](00-conventions.md#8-lengths-and-limits), [T-7](../../docs/threat-model.md#t-7-malicious-or-malformed-protocol-input)

## 5 Codec version pinning

**A blob's spool checkpoint MUST record the exact Zstandard library version used to produce its records** ([05 §6.2](05-blob.md#62-everything-that-could-vary-is-pinned)).

Zstandard is deterministic for a given library version and parameter set. It offers **no guarantee across versions** — compressors change output when internal heuristics are tuned, and that is normal, permitted behaviour on their part.

The consequence for this format is severe and non-obvious. If a resumed blob recompressed its records, an agent that crashed, was upgraded, and resumed would produce different compressed bytes and encrypt them under an already-used `(blob_key, ordinal)` pair. That is nonce reuse: XOR leakage plus recovery of the GHASH authentication subkey, after which arbitrary records in that blob can be forged.

Two rules prevent it, and an implementation needs both:

1. The spool checkpoint stores **sealed record bytes**, so resume re-emits rather than recomputes.
2. A codec version mismatch on resume **forces a restart**, which draws a fresh salt and is therefore safe.

→ [PT-1](../../docs/review/2026-08-fix-pressure-test.md#pt-1--c2s-resume-guarantee-silently-assumes-bit-reproducible-compression), [ADR-0005](../../docs/adr/0005-aead-suite-and-nonce-construction.md)

This is the only place in the format where a third-party library's version affects correctness rather than merely performance, which is why it is called out twice.

## 6 Metadata records

Metadata records — manifests, index deltas, checkpoints, journal records — follow the same rules and the same threshold.

CBOR metadata compresses well, so the threshold rarely fires for it. The behaviour is deliberately identical rather than special-cased: one compression path is one path to test, and metadata is not important enough to justify a second.

## 7 Test vectors

Threshold decisions for compressible, marginally compressible, and incompressible inputs are in [`conformance/vectors/compression.json`](conformance/vectors/compression.json), expressed as plaintext lengths, compressed lengths, and the expected profile.

The vectors deliberately test the **decision**, not the compressed bytes. Compressed output is not reproducible across Zstandard versions — that is the whole point of §5 — so a vector asserting exact compressed bytes would fail on a different library version and would be asserting the wrong thing.

---

**Previous:** [09 — Segmentation](09-segmentation.md) · **Next:** [11 — Lifecycle objects](11-lifecycle-objects.md)
