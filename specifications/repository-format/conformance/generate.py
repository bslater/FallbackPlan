#!/usr/bin/env python3
"""
Generate FallbackPlan repository-format conformance vectors.

Deliberately depends on nothing but the Python standard library. The point of
these vectors is that an implementer can reproduce them without trusting the
reference implementation, so anything computed here uses only SHA-256 and
HMAC-SHA256 -- primitives available in every language.

The HKDF implementation below is checked against RFC 5869 test case 1 on every
run. If that check ever fails, every derived vector in this file is wrong, and
the script exits non-zero rather than emitting them.

Vectors that CANNOT be produced here -- AEAD ciphertexts -- are not faked. See
README.md for what is independently derived and what is not.

Usage:  python3 generate.py [--check]
        --check verifies the committed vectors match freshly computed ones
        without writing anything (used in CI).
"""

from __future__ import annotations

import argparse
import hashlib
import hmac
import json
import pathlib
import sys

VECTORS = pathlib.Path(__file__).parent / "vectors"

# --------------------------------------------------------------------------
# Primitives
# --------------------------------------------------------------------------


def hkdf_expand(prk: bytes, info: bytes, length: int) -> bytes:
    """RFC 5869 section 2.3. HMAC-SHA256 only."""
    if length > 255 * 32:
        raise ValueError("length too large for HKDF-Expand with SHA-256")
    out, block, counter = b"", b"", 1
    while len(out) < length:
        block = hmac.new(prk, block + info + bytes([counter]), hashlib.sha256).digest()
        out += block
        counter += 1
    return out[:length]


def self_test() -> None:
    """RFC 5869 test case 1, expand step. Guards every derived vector below."""
    prk = bytes.fromhex(
        "077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5"
    )
    info = bytes.fromhex("f0f1f2f3f4f5f6f7f8f9")
    expected = (
        "3cb25f25faacd57a90434f64d0362f2a"
        "2d2d0a90cf1a5a4c5db02d56ecc4c5bf"
        "34007208d5b887185865"
    )
    actual = hkdf_expand(prk, info, 42).hex()
    if actual != expected:
        sys.exit(f"FATAL: HKDF self-test failed.\n  expected {expected}\n  actual   {actual}")


def b32(data: bytes) -> str:
    """Lowercase unpadded base32, per specification 00 section 6."""
    import base64

    return base64.b32encode(data).decode("ascii").rstrip("=").lower()


# --------------------------------------------------------------------------
# Fixed inputs
#
# Every value here is a constant so the vectors are reproducible. Nothing is
# random, nothing depends on the clock, and running this twice must produce
# byte-identical output.
# --------------------------------------------------------------------------

MASTER_KEY = bytes(range(32))                      # 00 01 02 ... 1f
REPOSITORY_ID = bytes.fromhex("0102030405060708090a0b0c0d0e0f10")
WRITER_ID = bytes.fromhex("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf")
BLOB_SALT = bytes([0x5A]) * 32
BLOB_COUNTER = 42
FORMAT_VERSION = 1

INFO_CONTENT_ID = b"fbp/content-id/v1"
INFO_KEY_ID = b"fbp/key-id/v1"
INFO_DATA = b"fbp/data/v1"
INFO_METADATA = b"fbp/metadata/v1"
INFO_SIGNING = b"fbp/signing/v1"
INFO_BLOB = b"fbp/blob/v1"

OBJECT_TYPE_SEGMENT = 0x01
OBJECT_TYPE_FILE_VERSION = 0x02
OBJECT_TYPE_TREE = 0x03
OBJECT_TYPE_BLOB_KEY = 0x07


def u32(n: int) -> bytes:
    return n.to_bytes(4, "big")


def u64(n: int) -> bytes:
    return n.to_bytes(8, "big")


def u16(n: int) -> bytes:
    return n.to_bytes(2, "big")


# --------------------------------------------------------------------------
# Vector groups
# --------------------------------------------------------------------------


def keys_vectors() -> dict:
    """Specification 03 -- key derivation."""
    content_id_key = hkdf_expand(MASTER_KEY, INFO_CONTENT_ID, 32)
    key_id_key = hkdf_expand(MASTER_KEY, INFO_KEY_ID, 32)
    data_key_0 = hkdf_expand(MASTER_KEY, INFO_DATA + u32(0), 32)
    data_key_1 = hkdf_expand(MASTER_KEY, INFO_DATA + u32(1), 32)
    metadata_key_0 = hkdf_expand(MASTER_KEY, INFO_METADATA + u32(0), 32)
    signing_key_0 = hkdf_expand(MASTER_KEY, INFO_SIGNING + u32(0), 32)

    blob_info = INFO_BLOB + BLOB_SALT + WRITER_ID + u64(BLOB_COUNTER)
    blob_key = hkdf_expand(data_key_0, blob_info, 32)

    # Same salt, different writer -- must differ. This is the property that
    # makes key separation independent of CSPRNG quality (PT-13).
    other_writer = bytes([0xB0]) * 16
    blob_key_other_writer = hkdf_expand(
        data_key_0, INFO_BLOB + BLOB_SALT + other_writer + u64(BLOB_COUNTER), 32
    )
    # Same salt and writer, different counter -- must also differ.
    blob_key_other_counter = hkdf_expand(
        data_key_0, INFO_BLOB + BLOB_SALT + WRITER_ID + u64(BLOB_COUNTER + 1), 32
    )

    assert blob_key != blob_key_other_writer
    assert blob_key != blob_key_other_counter

    return {
        "description": "HKDF-Expand key derivation (specification 03).",
        "independently_derived": True,
        "inputs": {
            "master_key": MASTER_KEY.hex(),
            "writer_id": WRITER_ID.hex(),
            "blob_salt": BLOB_SALT.hex(),
            "blob_counter": BLOB_COUNTER,
        },
        "derived": {
            "content_id_key": content_id_key.hex(),
            "key_id_key": key_id_key.hex(),
            "data_key_generation_0": data_key_0.hex(),
            "data_key_generation_1": data_key_1.hex(),
            "metadata_key_generation_0": metadata_key_0.hex(),
            "signing_key_generation_0": signing_key_0.hex(),
            "blob_key": blob_key.hex(),
        },
        "separation_checks": {
            "comment": (
                "Same blob_salt with a different writer_id or blob_counter must "
                "produce a different blob key. This is what makes key separation "
                "survive a cloned VM replaying CSPRNG state."
            ),
            "blob_key_other_writer": blob_key_other_writer.hex(),
            "blob_key_other_counter": blob_key_other_counter.hex(),
        },
    }


def identifier_vectors() -> dict:
    """Specification 02 -- content and object identifiers."""
    content_id_key = hkdf_expand(MASTER_KEY, INFO_CONTENT_ID, 32)
    key_id_key = hkdf_expand(MASTER_KEY, INFO_KEY_ID, 32)

    cases = []
    for name, plaintext in [
        ("empty", b""),
        ("single_byte", b"\x00"),
        ("ascii", b"hello world"),
        ("one_mib_cycle", bytes(range(256)) * 4096),      # exactly 1 MiB
        ("one_mib_zeros", b"\x00" * (1024 * 1024)),
    ]:
        content_id = hashlib.sha256(plaintext).digest()
        object_id = hmac.new(
            content_id_key, bytes([OBJECT_TYPE_SEGMENT]) + content_id, hashlib.sha256
        ).digest()
        cases.append(
            {
                "name": name,
                "plaintext_length": len(plaintext),
                "content_id": content_id.hex(),
                "object_id_segment": object_id.hex(),
                "object_id_base32": b32(object_id),
            }
        )

    # The same content under a different object type must yield a different
    # object identifier -- a record can never be reinterpreted as a manifest.
    probe = b"hello world"
    cid = hashlib.sha256(probe).digest()
    per_type = {
        "segment": hmac.new(content_id_key, bytes([OBJECT_TYPE_SEGMENT]) + cid, hashlib.sha256).hexdigest(),
        "file_version": hmac.new(content_id_key, bytes([OBJECT_TYPE_FILE_VERSION]) + cid, hashlib.sha256).hexdigest(),
        "tree": hmac.new(content_id_key, bytes([OBJECT_TYPE_TREE]) + cid, hashlib.sha256).hexdigest(),
    }
    assert len(set(per_type.values())) == 3

    blob_id = WRITER_ID[:8] + u64(BLOB_COUNTER)
    store_blob_key = hmac.new(
        key_id_key, bytes([OBJECT_TYPE_BLOB_KEY]) + blob_id, hashlib.sha256
    ).digest()[:16]

    return {
        "description": "Content identifiers, object identifiers and store keys (specification 02).",
        "independently_derived": True,
        "content_hash_profile": "sha-256-v1",
        "cases": cases,
        "object_type_separation": {
            "comment": "Identical plaintext under different object types must not collide.",
            "plaintext": probe.decode("ascii"),
            "object_ids": per_type,
        },
        "blob_identifier": {
            "writer_id": WRITER_ID.hex(),
            "blob_counter": BLOB_COUNTER,
            "blob_id": blob_id.hex(),
            "store_blob_key": store_blob_key.hex(),
            "store_blob_key_base32": b32(store_blob_key),
        },
    }


def aad_vectors() -> dict:
    """Specification 04 section 4 -- associated data construction."""
    content_id_key = hkdf_expand(MASTER_KEY, INFO_CONTENT_ID, 32)
    content_id = hashlib.sha256(b"hello world").digest()
    object_id = hmac.new(
        content_id_key, bytes([OBJECT_TYPE_SEGMENT]) + content_id, hashlib.sha256
    ).digest()

    cases = []
    for ordinal in (0, 1, 47, 65535):
        aad = (
            REPOSITORY_ID
            + u16(FORMAT_VERSION)
            + bytes([OBJECT_TYPE_SEGMENT])
            + object_id
            + u32(ordinal)
        )
        assert len(aad) == 55, f"AAD must be 55 bytes, got {len(aad)}"
        cases.append(
            {
                "ordinal": ordinal,
                "nonce_aes_gcm": ordinal.to_bytes(12, "big").hex(),
                "nonce_xchacha": (b"\x00" * 12 + ordinal.to_bytes(12, "big")).hex(),
                "aad": aad.hex(),
                "aad_length": len(aad),
            }
        )

    return {
        "description": "Record nonce and associated-data construction (specification 04).",
        "independently_derived": True,
        "inputs": {
            "repository_id": REPOSITORY_ID.hex(),
            "format_version": FORMAT_VERSION,
            "object_type": OBJECT_TYPE_SEGMENT,
            "object_id": object_id.hex(),
        },
        "cases": cases,
        "footer": {
            "comment": "The footer uses a reserved all-ones nonce no record can reach.",
            "nonce": "ffffffffffffffffffffffff",
            "aad_shape": "repository_id || u16(format_version) || blob_id || u32(record_count)",
        },
    }


def segmentation_vectors() -> dict:
    """Specification 09 -- fixed-v1 boundaries."""
    mib = 1024 * 1024
    cases = []
    for name, length, seg_size in [
        ("empty", 0, mib),
        ("smaller_than_one_segment", 1000, mib),
        ("exactly_one_segment", mib, mib),
        ("worked_example_3_5_mib", 3_670_016, mib),
        ("exactly_two_segments", 2 * mib, mib),
        ("one_byte_over", mib + 1, mib),
        ("small_segment_size", 100_000, 64 * 1024),
    ]:
        segments = []
        offset = 0
        while offset < length:
            seg_len = min(seg_size, length - offset)
            segments.append({"offset": offset, "length": seg_len})
            offset += seg_len
        # Invariants the specification requires.
        assert sum(s["length"] for s in segments) == length
        for i in range(len(segments) - 1):
            assert segments[i]["offset"] + segments[i]["length"] == segments[i + 1]["offset"]
            assert segments[i]["length"] == seg_size, "only the final segment may be short"
        cases.append(
            {
                "name": name,
                "file_length": length,
                "segment_size": seg_size,
                "segment_count": len(segments),
                "segments": segments,
            }
        )

    return {
        "description": "fixed-v1 segment boundaries (specification 09 section 2).",
        "independently_derived": True,
        "profile": "fixed-v1",
        "cases": cases,
        "cdc_v1": {
            "status": "parameters not yet pinned",
            "comment": (
                "cdc-v1 requires a fixed Rabin polynomial and per-byte table before "
                "vectors can be generated. Until those are pinned, two implementations "
                "would produce different boundaries and deduplicate against nothing. "
                "Pinning them is a Phase 0 work item; see docs/phase-0-execution-plan.md."
            ),
        },
    }


def compression_vectors() -> dict:
    """Specification 10 section 3 -- the storage threshold decision."""
    threshold_permille = 50

    def decide(logical: int, compressed: int) -> str:
        return (
            "zstd-v1"
            if compressed * 1000 <= logical * (1000 - threshold_permille)
            else "none"
        )

    cases = []
    for name, logical, compressed in [
        ("highly_compressible", 1_048_576, 611_204),
        ("marginal_just_over", 1_000_000, 949_000),
        ("marginal_just_under", 1_000_000, 951_000),
        ("exactly_at_threshold", 1_000_000, 950_000),
        ("incompressible", 1_048_576, 1_048_600),
        ("empty", 0, 0),
    ]:
        cases.append(
            {
                "name": name,
                "logical_length": logical,
                "compressed_length": compressed,
                "expected_profile": decide(logical, compressed),
            }
        )

    return {
        "description": "Compression storage-threshold decisions (specification 10 section 3).",
        "independently_derived": True,
        "threshold_permille": threshold_permille,
        "comment": (
            "These vectors assert the DECISION, not compressed bytes. Zstandard output "
            "is not reproducible across library versions -- which is precisely why "
            "specification 10 section 5 requires codec version pinning -- so a vector "
            "asserting exact compressed bytes would fail on a different library and "
            "would be asserting the wrong thing."
        ),
        "cases": cases,
    }


def nist_gcm_vectors() -> dict:
    """
    AES-256-GCM known-answer tests from NIST CAVP (gcmEncryptExtIV256).

    These are the only AEAD vectors in this suite that are independent of the
    reference implementation. They prove an implementation uses AES-GCM
    correctly; they say nothing about our record framing, which is checked by
    self-generated vectors and, ultimately, by the freeze-gate independent
    reader.
    """
    return {
        "description": "NIST CAVP AES-256-GCM known-answer tests.",
        "independently_derived": True,
        "source": "NIST CAVP gcmEncryptExtIV256, 96-bit IV, 128-bit tag",
        "cases": [
            {
                "name": "empty_plaintext_empty_aad",
                "key": "b52c505a37d78eda5dd34f20c22540ea1b58963cf8e5bf8ffa85f9f2492505b4",
                "iv": "516c33929df5a3284ff463d7",
                "plaintext": "",
                "aad": "",
                "ciphertext": "",
                "tag": "bdc1ac884d332457a1d2664f168c76f0",
            },
            {
                "name": "single_block",
                "key": "78dc4e0aaf52d935c3c01eea57428f00ca1fd475f5da86a49c8dd73d68c8e223",
                "iv": "d79cf22d504cc793c3fb6c8a",
                "plaintext": "b96baa8c1c75a671bfb2d08d06be5f36",
                "aad": "",
                "ciphertext": "3e5d486aa2e30b22e040b85723a06e76",
                "tag": "d5ca3854ce834f2c73b8bb9b8b5d4d78",
            },
        ],
    }


# --------------------------------------------------------------------------
# Driver
# --------------------------------------------------------------------------

GROUPS = {
    "keys.json": keys_vectors,
    "identifiers.json": identifier_vectors,
    "records.json": aad_vectors,
    "segmentation.json": segmentation_vectors,
    "compression.json": compression_vectors,
    "nist-gcm.json": nist_gcm_vectors,
}


def render(builder) -> str:
    payload = builder()
    payload = {
        "$comment": (
            "Generated by generate.py. Do not edit by hand. "
            "Re-run the generator and commit the result."
        ),
        "specification_version": 1,
        **payload,
    }
    return json.dumps(payload, indent=2, sort_keys=False) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--check",
        action="store_true",
        help="verify committed vectors match freshly computed ones; write nothing",
    )
    args = parser.parse_args()

    self_test()

    VECTORS.mkdir(parents=True, exist_ok=True)
    failures = []

    for filename, builder in GROUPS.items():
        rendered = render(builder)
        path = VECTORS / filename
        if args.check:
            if not path.exists():
                failures.append(f"{filename}: missing")
            elif path.read_text() != rendered:
                failures.append(f"{filename}: differs from freshly computed output")
        else:
            path.write_text(rendered)
            print(f"wrote {path.relative_to(VECTORS.parent.parent.parent)}")

    if failures:
        for f in failures:
            print(f"FAIL {f}", file=sys.stderr)
        return 1

    print("HKDF self-test: passed (RFC 5869 TC1)")
    print("all vector groups " + ("verified" if args.check else "generated"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
