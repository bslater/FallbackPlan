#!/usr/bin/env python3
"""
Generate FallbackPlan repository-format conformance vectors.

Deliberately depends on nothing but the Python standard library. The point of
these vectors is that an implementer can reproduce them without trusting the
reference implementation, so anything computed here uses only SHA-256,
HMAC-SHA256, SHA-512, and integer arithmetic -- primitives available in every
language. That includes Ed25519, implemented below directly from RFC 8032 and
self-checked against the RFC's published test vectors on every run.

The HKDF implementation below is checked against RFC 5869 test case 1 on every
run. If that check ever fails, every derived vector in this file is wrong, and
the script exits non-zero rather than emitting them.

Vectors that CANNOT be produced here -- AES-GCM and Argon2id outputs -- are
pinned constants with their real provenance declared and
independently_derived set to False. See README.md for what each flag means.

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
import re
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
# cdc-v1 Rabin fingerprint (specification 09 section 3, ADR-0023)
#
# Everything below is computed from the pinned polynomial with GF(2) integer
# arithmetic -- no reference implementation involved, which is what lets
# segmentation.json keep independently_derived = true.
# --------------------------------------------------------------------------

CDC_POLYNOMIAL_LOW = 0x1B                      # x^4 + x^3 + x + 1; the x^64 term is implicit
CDC_MODULUS = (1 << 64) | CDC_POLYNOMIAL_LOW   # P(x) = x^64 + x^4 + x^3 + x + 1
CDC_WINDOW = 64
_M64 = (1 << 64) - 1


def gf2_mod(value: int) -> int:
    """Reduce a GF(2) polynomial (bits = coefficients) modulo P(x)."""
    while value.bit_length() > 64:
        value ^= CDC_MODULUS << (value.bit_length() - 65)
    return value


def _gf2_polymod(a: int, b: int) -> int:
    """a mod b over GF(2), for arbitrary modulus b (used by the gcd below)."""
    db = b.bit_length()
    while a.bit_length() >= db:
        a ^= b << (a.bit_length() - db)
    return a


def _gf2_mulmod(a: int, b: int) -> int:
    """(a * b) mod P over GF(2)."""
    r = 0
    while b:
        if b & 1:
            r ^= a
        b >>= 1
        a <<= 1
    return gf2_mod(r)


def _cdc_polynomial_self_test() -> None:
    """
    Rabin's irreducibility test for P over GF(2), degree n = 64.

    P is irreducible iff x^(2^64) == x (mod P) and, for each prime q dividing
    64 (only q = 2), gcd(x^(2^32) - x mod P, P) = 1. If this ever fails, the
    pinned constant does not define the field ADR-0023 claims it does, and
    every cdc vector would be built on a degenerate ring.
    """

    def pow_x(squarings: int) -> int:
        r = 2  # the polynomial x
        for _ in range(squarings):
            r = _gf2_mulmod(r, r)
        return r

    if pow_x(64) != 2:
        sys.exit("FATAL: cdc-v1 polynomial fails x^(2^64) == x; not irreducible.")

    t = pow_x(32) ^ 2
    a, b = CDC_MODULUS, t
    while b:
        a, b = b, _gf2_polymod(a, b)
    if a != 1:
        sys.exit("FATAL: cdc-v1 polynomial shares a factor with x^(2^32) - x; not irreducible.")


def _cdc_tables() -> tuple[list[int], list[int]]:
    """push_table[b] = (b * x^64) mod P; pop_table[b] = (b * x^(8*64)) mod P."""
    push = [gf2_mod(b << 64) for b in range(256)]
    pop = [gf2_mod(b << (8 * CDC_WINDOW)) for b in range(256)]
    return push, pop


def cdc_segments(data: bytes, target: int, min_size: int, max_size: int) -> list[dict]:
    """
    The cdc-v1 reference segmenter (specification 09 section 3.1, ADR-0023).

    The window rolls continuously across segment boundaries -- never reset --
    so a boundary is a pure function of the local 64 bytes, which is the whole
    resynchronisation property.
    """
    push, pop = _CDC_PUSH, _CDC_POP
    mask = target - 1
    h = 0
    window = bytearray(CDC_WINDOW)
    segments: list[dict] = []
    start = 0
    for p, byte in enumerate(data):
        i = p & (CDC_WINDOW - 1)
        outgoing = window[i]
        window[i] = byte
        h = ((h << 8) & _M64) ^ push[h >> 56] ^ byte ^ pop[outgoing]
        length = p - start + 1
        if length == max_size or (length >= min_size and (h & mask) == 0):
            segments.append({"offset": start, "length": length})
            start = p + 1
    if start < len(data):
        segments.append({"offset": start, "length": len(data) - start})
    return segments


_CDC_PUSH, _CDC_POP = _cdc_tables()


# --------------------------------------------------------------------------
# Ed25519 (RFC 8032), pure Python
#
# Implemented from the RFC alone so signature vectors can be computed here
# with no reference implementation involved (ADR-0022 Decision 8). Slow and
# simple on purpose; it signs a handful of test messages, nothing more.
# --------------------------------------------------------------------------

_ED_P = 2**255 - 19
_ED_L = 2**252 + 27742317777372353535851937790883648493
_ED_D = (-121665 * pow(121666, _ED_P - 2, _ED_P)) % _ED_P


def _ed_recover_x(y: int, sign: int) -> int:
    x2 = (y * y - 1) * pow(_ED_D * y * y + 1, _ED_P - 2, _ED_P) % _ED_P
    x = pow(x2, (_ED_P + 3) // 8, _ED_P)
    if (x * x - x2) % _ED_P != 0:
        x = x * pow(2, (_ED_P - 1) // 4, _ED_P) % _ED_P
    if (x * x - x2) % _ED_P != 0:
        raise ValueError("not a square; invalid point")
    if x % 2 != sign:
        x = _ED_P - x
    return x


_ED_GY = 4 * pow(5, _ED_P - 2, _ED_P) % _ED_P
_ED_GX = _ed_recover_x(_ED_GY, 0)
_ED_G = (_ED_GX, _ED_GY, 1, _ED_GX * _ED_GY % _ED_P)  # extended coordinates


def _ed_add(p: tuple, q: tuple) -> tuple:
    a = (p[1] - p[0]) * (q[1] - q[0]) % _ED_P
    b = (p[1] + p[0]) * (q[1] + q[0]) % _ED_P
    c = 2 * p[3] * q[3] * _ED_D % _ED_P
    d = 2 * p[2] * q[2] % _ED_P
    e, f, g, h = b - a, d - c, d + c, b + a
    return (e * f % _ED_P, g * h % _ED_P, f * g % _ED_P, e * h % _ED_P)


def _ed_mul(s: int, p: tuple) -> tuple:
    q = (0, 1, 1, 0)  # the neutral element
    while s > 0:
        if s & 1:
            q = _ed_add(q, p)
        p = _ed_add(p, p)
        s >>= 1
    return q


def _ed_compress(p: tuple) -> bytes:
    zinv = pow(p[2], _ED_P - 2, _ED_P)
    x = p[0] * zinv % _ED_P
    y = p[1] * zinv % _ED_P
    return int.to_bytes(y | ((x & 1) << 255), 32, "little")


def _ed_secret_expand(seed: bytes) -> tuple[int, bytes]:
    """RFC 8032 section 5.1.5: the 32-byte seed is hashed, clamped, split."""
    h = hashlib.sha512(seed).digest()
    a = int.from_bytes(h[:32], "little")
    a &= (1 << 254) - 8
    a |= 1 << 254
    return a, h[32:]


def ed25519_public_key(seed: bytes) -> bytes:
    a, _ = _ed_secret_expand(seed)
    return _ed_compress(_ed_mul(a, _ED_G))


def ed25519_sign(seed: bytes, message: bytes) -> bytes:
    a, prefix = _ed_secret_expand(seed)
    public = _ed_compress(_ed_mul(a, _ED_G))
    r = int.from_bytes(hashlib.sha512(prefix + message).digest(), "little") % _ED_L
    big_r = _ed_compress(_ed_mul(r, _ED_G))
    k = int.from_bytes(hashlib.sha512(big_r + public + message).digest(), "little") % _ED_L
    s = (r + k * a) % _ED_L
    return big_r + int.to_bytes(s, 32, "little")


# RFC 8032 section 7.1 test vectors 1-3: seed, public key, message, signature.
_ED_RFC8032_CASES = [
    (
        "rfc8032_test_1_empty_message",
        "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
        "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
        "",
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b",
    ),
    (
        "rfc8032_test_2_one_byte",
        "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
        "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
        "72",
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00",
    ),
    (
        "rfc8032_test_3_two_bytes",
        "c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7",
        "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025",
        "af82",
        "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a",
    ),
]


def _ed25519_self_test() -> None:
    """The RFC's own vectors gate every Ed25519 value this generator emits."""
    for name, seed_hex, public_hex, message_hex, signature_hex in _ED_RFC8032_CASES:
        seed = bytes.fromhex(seed_hex)
        if ed25519_public_key(seed).hex() != public_hex:
            sys.exit(f"FATAL: Ed25519 self-test {name}: public key mismatch.")
        if ed25519_sign(seed, bytes.fromhex(message_hex)).hex() != signature_hex:
            sys.exit(f"FATAL: Ed25519 self-test {name}: signature mismatch.")


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

    # Footer AAD as bytes, not just a shape string. The record AAD above is
    # pinned exactly; the footer's deserves the same treatment -- a shape an
    # implementer has to assemble themselves is a shape two implementers can
    # assemble differently.
    footer_blob_id = WRITER_ID[:8] + u64(BLOB_COUNTER)
    footer_record_count = 3
    footer_aad = (
        REPOSITORY_ID + u16(FORMAT_VERSION) + footer_blob_id + u32(footer_record_count)
    )
    assert len(footer_aad) == 38, f"footer AAD must be 38 bytes, got {len(footer_aad)}"

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
            "blob_id": footer_blob_id.hex(),
            "record_count": footer_record_count,
            "aad": footer_aad.hex(),
            "aad_length": len(footer_aad),
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
        "description": "fixed-v1 and cdc-v1 segment boundaries (specification 09).",
        "independently_derived": True,
        "profile": "fixed-v1",
        "cases": cases,
        "cdc_v1": cdc_v1_vectors(),
    }


def cdc_v1_vectors() -> dict:
    """
    cdc-v1 boundaries under the ADR-0023 pin.

    Reduced parameters (target 64 KiB, min 8 KiB, max 512 KiB) sit exactly on
    the specification's ratio bounds (min = target/8, max = target*8) and keep
    the inputs small enough to segment in pure Python. Every boundary below is
    COMPUTED from the pinned polynomial -- the tables are committed alongside
    so an implementation may either regenerate them from the rule or embed
    them; this generator proves the two routes agree by construction.
    """
    _cdc_polynomial_self_test()

    target, min_size, max_size = 64 * 1024, 8 * 1024, 512 * 1024

    def sha_stream(blocks: int) -> bytes:
        return b"".join(
            hashlib.sha256(u64(i)).digest() for i in range(blocks)
        )

    stream = sha_stream(16384)  # 512 KiB, statistically random, stdlib-reproducible

    cases = []
    for name, data, input_desc in [
        ("empty", b"", {"kind": "empty"}),
        (
            "all_zeros_256_kib",
            bytes(256 * 1024),
            {"kind": "zeros", "length": 256 * 1024},
        ),
        (
            "sha256_stream_512_kib",
            stream,
            {
                "kind": "sha256_stream",
                "blocks": 16384,
                "rule": "concatenation of SHA-256(u64_be(i)) for i in 0..blocks-1",
            },
        ),
        (
            "sha256_stream_one_byte_prepended",
            b"\xa5" + stream,
            {
                "kind": "prefixed_sha256_stream",
                "prefix": "a5",
                "blocks": 16384,
                "rule": "one byte 0xA5 followed by the sha256_stream_512_kib input",
            },
        ),
        (
            "max_size_forcing_repeat",
            bytes([0x01, 0x02, 0x03, 0x04]) * (1_310_720 // 4),
            {
                "kind": "repeat",
                "pattern": "01020304",
                "repetitions": 1_310_720 // 4,
            },
        ),
    ]:
        segments = cdc_segments(data, target, min_size, max_size)

        # Invariants the specification requires (09 section 3.1).
        assert sum(s["length"] for s in segments) == len(data)
        for i in range(len(segments) - 1):
            assert segments[i]["offset"] + segments[i]["length"] == segments[i + 1]["offset"]
            assert min_size <= segments[i]["length"] <= max_size
        if segments:
            assert segments[-1]["length"] <= max_size

        cases.append(
            {
                "name": name,
                "input": input_desc,
                "target_size": target,
                "min_size": min_size,
                "max_size": max_size,
                "mask": f"0x{target - 1:016x}",
                "segment_count": len(segments),
                "segments": segments,
            }
        )

    by_name = {c["name"]: c for c in cases}

    # The zero window fingerprints to zero, so every boundary test passes and
    # every segment is exactly min_size -- the sharp edge of the min rule.
    zeros = by_name["all_zeros_256_kib"]["segments"]
    assert all(s["length"] == min_size for s in zeros)

    # Resynchronisation (09 section 3.2): boundaries are a pure function of
    # the local window, so prepending one byte shifts every cut by exactly
    # one. This is the property cdc-v1 exists for, asserted rather than hoped.
    base_cuts = [s["offset"] + s["length"] for s in by_name["sha256_stream_512_kib"]["segments"][:-1]]
    shifted_cuts = [
        s["offset"] + s["length"] - 1
        for s in by_name["sha256_stream_one_byte_prepended"]["segments"][:-1]
    ]
    assert base_cuts == shifted_cuts, "insertion failed to resynchronise"

    # The repeating pattern never satisfies the mask, so every non-final
    # segment is a forced max_size cut.
    forced = by_name["max_size_forcing_repeat"]["segments"]
    assert all(s["length"] == max_size for s in forced[:-1]) and len(forced) > 1

    return {
        "profile": "cdc-v1",
        "polynomial": "0x000000000000001b",
        "polynomial_comment": (
            "Low 64 coefficient bits of P(x) = x^64 + x^4 + x^3 + x + 1; the "
            "x^64 term is implicit. Irreducible over GF(2), asserted by this "
            "generator on every run (ADR-0023)."
        ),
        "window_size": CDC_WINDOW,
        "hash_rule": (
            "H(p) = the 64 bytes ending at and including position p, "
            "interpreted most-significant-byte-first as a GF(2) polynomial, "
            "reduced mod P. The window rolls continuously across segment "
            "boundaries and is never reset. A segment ends at p (inclusive) "
            "when (H(p) & mask) == 0 and its length is >= min_size, or "
            "unconditionally at max_size; the final short segment is emitted "
            "as-is."
        ),
        "table_rule": (
            "push_table[b] = (b * x^64) mod P; pop_table[b] = (b * x^512) mod P. "
            "Rolling step: H' = ((H << 8) & (2^64-1)) XOR push_table[H >> 56] "
            "XOR incoming XOR pop_table[outgoing]."
        ),
        "push_table": [f"{v:016x}" for v in _CDC_PUSH],
        "pop_table": [f"{v:016x}" for v in _CDC_POP],
        "cases": cases,
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
    # Case names state which side of the threshold the SAVING falls on --
    # earlier names ("marginal_just_over/under") were ambiguous about whether
    # "over" meant the saving or the compressed size, which is exactly the
    # confusion a boundary vector exists to remove.
    #
    # There is deliberately no (0, 0) case: a zero-length file produces no
    # segments and no records at all (specification 09 section 2, 04 section
    # 2.1), so no compression decision for it can ever be taken.
    for name, logical, compressed in [
        ("highly_compressible", 1_048_576, 611_204),
        ("saving_just_above_threshold", 1_000_000, 949_000),
        ("saving_just_below_threshold", 1_000_000, 951_000),
        ("exactly_at_threshold", 1_000_000, 950_000),
        ("incompressible", 1_048_576, 1_048_600),
        ("small_incompressible", 100, 99),
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


def aes_gcm_vectors() -> dict:
    """
    AES-256-GCM known-answer tests.

    This group is NOT independently derived, and says so. The generator cannot
    compute AES-GCM from the standard library, so nothing in this file was
    computed here -- each case's `provenance` field states where its values
    actually came from, and the file-level flag is False. An earlier revision
    claimed independent derivation for values the generator could not have
    derived; that overstatement is exactly the failure mode the provenance
    flag exists to prevent, so it is now enforced honestly.

    History worth keeping: an even earlier revision carried a case whose
    ciphertext was written from memory rather than obtained, and it was
    wrong -- CryptographicPrimitiveTests caught it on first run. It was
    removed rather than replaced with another remembered value.

    Case 1 is believed to be a NIST CAVP vector but the archive could not be
    reached to confirm (csrc.nist.gov is unreachable from this environment);
    `provenance_reverified: false` records that. It exercises only the
    empty-plaintext, empty-AAD path.

    Case 2 exists because case 1 proves nothing about AAD absorption -- the
    one property the record format leans on (specification 04 section 4). It
    uses the format's REAL construction: the blob key pinned in keys.json,
    ordinal 47's nonce, and ordinal 47's 55-byte AAD from records.json. It was
    computed ONCE with the platform implementation
    (System.Security.Cryptography.AesGcm) and pinned. It is a regression
    vector, not conformance evidence: it proves a future implementation
    matches the platform's AES-GCM over the format's exact inputs, not that
    either matches the specification.

    To expand this set properly: fetch gcmEncryptExtIV256 from the NIST CAVP
    archive, and add cases from it. Do not add remembered values.
    """
    return {
        "description": "AES-256-GCM known-answer tests.",
        "independently_derived": False,
        "comment": (
            "Nothing here was computed by this generator -- it cannot compute "
            "AES-GCM from the standard library. Correctness of every case is "
            "verified by CryptographicPrimitiveTests against the platform "
            "implementation on every CI run; per-case provenance states where "
            "each value came from."
        ),
        "cases": [
            {
                "name": "empty_plaintext_empty_aad",
                "provenance": "believed NIST CAVP gcmEncryptExtIV256, 96-bit IV, 128-bit tag",
                "provenance_reverified": False,
                "key": "b52c505a37d78eda5dd34f20c22540ea1b58963cf8e5bf8ffa85f9f2492505b4",
                "iv": "516c33929df5a3284ff463d7",
                "plaintext": "",
                "aad": "",
                "ciphertext": "",
                "tag": "bdc1ac884d332457a1d2664f168c76f0",
            },
            {
                "name": "record_ordinal_47_real_construction",
                "provenance": (
                    "platform-derived: computed once with "
                    "System.Security.Cryptography.AesGcm and pinned. Regression "
                    "vector, not conformance evidence. Key is keys.json blob_key; "
                    "nonce and AAD are records.json ordinal 47."
                ),
                "provenance_reverified": False,
                "key": "d35875180e8f91a5044f4786c560624cd62ab51f82897b771b5bfbccd9ee313d",
                "iv": "00000000000000000000002f",
                "plaintext": (
                    "46616c6c6261636b506c616e20636f6e666f726d616e63652073756974653a"
                    "207265636f7264206f7264696e616c203437"
                ),
                "aad": (
                    "0102030405060708090a0b0c0d0e0f1000010166817ba59f4e1868f6c52dfe"
                    "ca501904d9a70aa87b2dd5857e15be14738fdde00000002f"
                ),
                "ciphertext": (
                    "bb9690d382d7f70b6f00d12e22c54208a8c069455a621f254665e8c1f92ebd"
                    "e56c53f9ea31fca86794953ff5f01cdf3fa6"
                ),
                "tag": "d5649651bd6452be41bd0b23f5fff22f",
            },
        ],
    }


def argon2id_vectors() -> dict:
    """
    Argon2id known-answer test at the specification's mandated minimum
    parameters (03 section 2: 64 MiB, 3 iterations, parallelism 4).

    NOT independently derived -- the generator cannot compute Argon2id from
    the standard library. The pinned value was computed by TWO independent
    implementations (Bodu.Security.Cryptography and
    Konscious.Security.Cryptography), which agree bit-for-bit; that agreement
    is re-verified against this committed value by
    Argon2idCrossVerificationTests on every CI run. Two implementations
    agreeing is weaker than an independent derivation and much stronger than
    one implementation asserting itself correct; the flag records which of
    those this is.
    """
    return {
        "description": "Argon2id KEK derivation at the mandated minimum parameters (specification 03 section 2).",
        "independently_derived": False,
        "provenance": (
            "computed by two independent implementations (Bodu.Security.Cryptography "
            "and Konscious.Security.Cryptography 1.3.1), which agree bit-for-bit; "
            "re-verified against both on every CI run by Argon2idCrossVerificationTests"
        ),
        "cases": [
            {
                "name": "mandated_minimum_parameters",
                "password_utf8": "fallbackplan conformance passphrase",
                "salt": "000102030405060708090a0b0c0d0e0f",
                "memory_kib": 65536,
                "iterations": 3,
                "parallelism": 4,
                "tag_length": 32,
                "tag": "4f10a625c20dd8499acfd84ec9618d2822928bdc8c66db770e9d27305e2f1aa2",
            },
        ],
    }


def ed25519_vectors() -> dict:
    """
    Ed25519 signatures (specification 06 section 6.1; ADR-0020, ADR-0022).

    Everything here is computed by the pure-Python RFC 8032 implementation
    above, which is itself gated by the RFC's published test vectors 1-3 on
    every run -- the same pattern as the HKDF RFC 5869 self-test. The
    format-real cases sign with seeds derived exactly as specification 03
    section 4 derives them, proving the seed interpretation end to end: the
    32 HKDF bytes are an RFC 8032 section 5.1.5 seed, never a pre-clamped
    scalar, and the public key is computed from it rather than distributed.
    """
    _ed25519_self_test()

    rfc_cases = [
        {
            "name": name,
            "provenance": "RFC 8032 section 7.1; recomputed by this generator on every run",
            "seed": seed,
            "public_key": public,
            "message": message,
            "signature": signature,
        }
        for name, seed, public, message, signature in _ED_RFC8032_CASES
    ]

    format_cases = []
    for generation, message, message_comment in [
        (
            0,
            b"FallbackPlan conformance suite: repository-scoped signature domain",
            "plain ASCII bytes",
        ),
        (
            1,
            # A hand-assembled deterministic CBOR map (00 section 4.1):
            # {1: repository_id, 2: 1, 3: "fbp"} -- the shape of a signed
            # structure's canonical prefix.
            bytes.fromhex("a3") + bytes.fromhex("01") + bytes.fromhex("50") + REPOSITORY_ID
            + bytes.fromhex("0201")
            + bytes.fromhex("0363666270"),
            "deterministic CBOR map {1: repository_id (bytes 16), 2: 1, 3: \"fbp\"}",
        ),
    ]:
        seed = hkdf_expand(MASTER_KEY, INFO_SIGNING + u32(generation), 32)
        public = ed25519_public_key(seed)
        signature = ed25519_sign(seed, message)

        format_cases.append(
            {
                "name": f"format_signing_seed_generation_{generation}",
                "generation": generation,
                "seed_derivation": "HKDF-Expand(master_key, 'fbp/signing/v1' || u32(generation), 32)",
                "seed": seed.hex(),
                "public_key": public.hex(),
                "message": message.hex(),
                "message_comment": message_comment,
                "signature": signature.hex(),
            }
        )

    # Generation 0's seed must equal keys.json's signing_key_generation_0 --
    # one derivation, two files, no drift.
    assert format_cases[0]["seed"] == hkdf_expand(MASTER_KEY, INFO_SIGNING + u32(0), 32).hex()

    return {
        "description": "Ed25519 signatures over RFC 8032 vectors and the format's real signing seeds.",
        "independently_derived": True,
        "comment": (
            "Computed by a pure-Python RFC 8032 implementation living in this "
            "generator, gated by the RFC's published test vectors on every "
            "run. The seed is the RFC 8032 section 5.1.5 private-key seed "
            "(ADR-0020) -- an implementation that treats it as a pre-clamped "
            "scalar will fail every case here."
        ),
        "rfc8032_cases": rfc_cases,
        "format_cases": format_cases,
    }


# --------------------------------------------------------------------------
# Path rules (specification 06 section 7.1, rules-v1)
# --------------------------------------------------------------------------

def _rules_glob_to_regex(rule: str) -> str:
    """Translates a rules-v1 glob rule to a regex, or raises ValueError.

    The translation is the dialect: `*` -> `[^/]*` within a component, `?`
    -> `[^/]`, a whole-component `**` -> zero or more components, and a
    rule without `/` is shorthand for `**/<rule>` (specification 06
    section 7.1).
    """
    if rule == "":
        raise ValueError("empty rule")

    components = rule.split("/") if "/" in rule else ["**", rule]
    if any(component == "" for component in components):
        raise ValueError("empty component")

    parts: list[str] = []
    for index, component in enumerate(components):
        last = index == len(components) - 1
        if component == "**":
            # A trailing ** matches one or more further components (the
            # prefix itself is not matched); elsewhere it matches zero or
            # more whole components including the joining slash.
            parts.append(".+" if last else "(?:[^/]+/)*")
            continue

        if "**" in component:
            raise ValueError("** must stand alone as a component")

        for character in component:
            if character == "*":
                parts.append("[^/]*")
            elif character == "?":
                parts.append("[^/]")
            else:
                parts.append(re.escape(character))

        if not last:
            parts.append("/")

    return "".join(parts)


def _rules_validate_regex(pattern: str) -> None:
    """Validates the rules-v1 regex subset, raising ValueError on any rule
    outside it (specification 06 section 7.1): no anchors, no backslash-
    alphanumeric escapes (shorthand classes, backreferences), no (?...)
    constructs, and unescaped { only as a counted quantifier.
    """
    if pattern == "":
        raise ValueError("empty rule")
    if pattern.split("/") != [c for c in pattern.split("/") if c != ""]:
        raise ValueError("empty component")

    index = 0
    in_class = False
    while index < len(pattern):
        character = pattern[index]
        if character == "\\":
            if index + 1 >= len(pattern):
                raise ValueError("trailing backslash")
            if pattern[index + 1].isalnum():
                raise ValueError("backslash-alphanumeric escapes are outside the subset")
            index += 2
            continue

        if in_class:
            if character == "]":
                in_class = False
            index += 1
            continue

        if character == "[":
            in_class = True
            index += 1
            continue

        if character in "^$":
            raise ValueError("anchors are outside the subset (rules are implicitly anchored)")

        if character == "(" and pattern[index + 1 : index + 2] == "?":
            raise ValueError("(?...) constructs are outside the subset")

        if character == "{":
            quantifier = re.match(r"\{\d+(,\d*)?\}", pattern[index:])
            if quantifier is None:
                raise ValueError("unescaped { must open a counted quantifier")
            index += quantifier.end()
            continue

        if character == "}":
            raise ValueError("unescaped } outside a quantifier")

        index += 1

    if in_class:
        raise ValueError("unterminated character class")


def _rules_compile(rule: str, case_sensitive: bool):
    """Compiles one rules-v1 rule of either form, raising ValueError."""
    if rule.startswith("re:"):
        pattern = rule[3:]
        _rules_validate_regex(pattern)
    else:
        pattern = _rules_glob_to_regex(rule)

    try:
        return re.compile(pattern, 0 if case_sensitive else re.IGNORECASE)
    except re.error as error:  # a subset-passing pattern the engine refuses
        raise ValueError(str(error))


def _rules_evaluate(includes, excludes, case_sensitive: bool, path: str):
    """The section 7.1 evaluation: exclude wins and prunes subtrees; a path
    is captured when not excluded and reached by an (or no) include.
    """
    compiled_includes = [_rules_compile(rule, case_sensitive) for rule in includes]
    compiled_excludes = [_rules_compile(rule, case_sensitive) for rule in excludes]

    components = path.split("/")
    prefixes = ["/".join(components[: n + 1]) for n in range(len(components))]

    excluded = any(
        matcher.fullmatch(prefix)
        for matcher in compiled_excludes
        for prefix in prefixes
    )
    captured = not excluded and (
        not compiled_includes
        or any(
            matcher.fullmatch(prefix)
            for matcher in compiled_includes
            for prefix in prefixes
        )
    )

    return excluded, captured


def path_rules_vectors() -> dict:
    """Specification 06 section 7.1 -- the rules-v1 include/exclude dialect."""
    match_rows = [
        # (rule, path, case_sensitive, matches) -- single-rule fullmatch
        # including the no-slash shorthand.
        ("*.log", "system.log", True, True),
        ("*.log", "var/log/system.log", True, True),
        ("*.log", "system.log.1", True, False),
        ("*.log", "nested.log/file", True, False),
        ("?.txt", "a.txt", True, True),
        ("?.txt", "ab.txt", True, False),
        ("?.txt", "docs/a.txt", True, True),
        ("build", "build", True, True),
        ("build", "src/build", True, True),
        ("build", "buildings", True, False),
        ("src/*.cs", "src/main.cs", True, True),
        ("src/*.cs", "src/sub/main.cs", True, False),
        ("src/*.cs", "other/src/main.cs", True, False),
        ("src/**", "src", True, False),
        ("src/**", "src/main.cs", True, True),
        ("src/**", "src/a/b/c.cs", True, True),
        ("**/obj/**", "obj/x", True, True),
        ("**/obj/**", "a/obj/x/y", True, True),
        ("**/obj/**", "a/obj", True, False),
        ("**/obj/**", "a/objx/y", True, False),
        ("a/**/b", "a/b", True, True),
        ("a/**/b", "a/x/b", True, True),
        ("a/**/b", "a/x/y/b", True, True),
        ("a/**/b", "a/x/bc", True, False),
        ("**", "anything/at/all", True, True),
        ("*.LOG", "system.log", False, True),
        ("*.LOG", "system.log", True, False),
        ("Caf?", "cafe", False, True),
        (r"re:.*\.(jpg|png)", "photos/a.jpg", True, True),
        (r"re:.*\.(jpg|png)", "photos/a.gif", True, False),
        (r"re:.*\.(jpg|png)", "a.jpgx", True, False),
        (r"re:snap-[0-9]{4}", "snap-2026", True, True),
        (r"re:snap-[0-9]{4}", "snap-26", True, False),
        (r"re:snap-[0-9]{4}", "x/snap-2026", True, False),
        (r"re:re\:literal", "re:literal", True, True),
        (r"re:a[^/]*", "abc", True, True),
        (r"re:a[^/]*", "abc/d", True, False),
        (r"re:docs/.*", "docs/deep/tree/file", True, True),
    ]

    match_cases = []
    for rule, candidate, case_sensitive, expected in match_rows:
        matcher = _rules_compile(rule, case_sensitive)
        actual = matcher.fullmatch(candidate) is not None
        assert actual == expected, (rule, candidate, actual)
        match_cases.append({
            "rule": rule,
            "path": candidate,
            "case_sensitive": case_sensitive,
            "matches": expected,
        })

    invalid_rows = [
        ("", "empty rule"),
        ("/absolute", "empty component (leading slash)"),
        ("trailing/", "empty component (trailing slash)"),
        ("a//b", "empty component"),
        ("a**b", "** must stand alone as a component"),
        ("**.log", "** must stand alone as a component"),
        ("re:", "empty rule"),
        ("re:^anchored", "anchors are outside the subset"),
        ("re:anchored$", "anchors are outside the subset"),
        (r"re:\d+", "backslash-alphanumeric escape (shorthand class)"),
        (r"re:(a)\1", "backslash-alphanumeric escape (backreference)"),
        ("re:(?:group)", "(?...) construct"),
        ("re:(?=look)", "(?...) construct"),
        ("re:brace{", "unescaped { must open a counted quantifier"),
        ("re:brace}", "unescaped } outside a quantifier"),
        ("re:class[unterminated", "unterminated character class"),
        ("re:trailing\\", "trailing backslash"),
    ]

    invalid_cases = []
    for rule, reason in invalid_rows:
        try:
            _rules_compile(rule, case_sensitive=True)
        except ValueError:
            invalid_cases.append({"rule": rule, "reason": reason})
        else:
            raise AssertionError(f"rule {rule!r} unexpectedly valid")

    evaluation_scenarios = [
        {
            "name": "empty_includes_capture_everything_not_excluded",
            "includes": [],
            "excludes": ["*.tmp", "**/.cache/**", ".cache"],
            "case_sensitive": True,
            "paths": [
                "docs/report.txt",
                "scratch.tmp",
                "deep/scratch.tmp",
                ".cache",
                ".cache/entry",
                "home/.cache/a/b",
                "home/.cachet/file",
            ],
        },
        {
            "name": "includes_select_subtrees_excludes_prune_within",
            "includes": ["photos/**", "docs/**"],
            "excludes": ["**/*.tmp", "docs/drafts"],
            "case_sensitive": True,
            "paths": [
                "photos/2026/a.jpg",
                "photos",
                "music/song.mp3",
                "docs/final.txt",
                "docs/drafts",
                "docs/drafts/wip.txt",
                "docs/edit.tmp",
            ],
        },
        {
            "name": "ancestor_exclusion_beats_descendant_include",
            "includes": ["build/keep/**"],
            "excludes": ["build"],
            "case_sensitive": True,
            "paths": ["build", "build/keep/artefact", "build/other"],
        },
        {
            "name": "case_insensitive_filesystem",
            "includes": [],
            "excludes": ["*.BAK"],
            "case_sensitive": False,
            "paths": ["notes.bak", "notes.Bak", "notes.bakx"],
        },
    ]

    for scenario in evaluation_scenarios:
        expectations = []
        for candidate in scenario["paths"]:
            excluded, captured = _rules_evaluate(
                scenario["includes"], scenario["excludes"],
                scenario["case_sensitive"], candidate)
            expectations.append({
                "path": candidate,
                "excluded": excluded,
                "captured": captured,
            })
        scenario["paths"] = expectations

    # Spot-check the evaluation semantics inline, the same way the cdc and
    # segmentation builders assert their own invariants.
    assert _rules_evaluate([], ["build"], True, "build/keep/artefact") == (True, False)
    assert _rules_evaluate(["photos/**"], [], True, "photos/a.jpg") == (False, True)
    assert _rules_evaluate(["photos/**"], [], True, "music/a.mp3") == (False, False)
    assert _rules_evaluate([], [], True, "anything") == (False, True)

    return {
        "description": "rules-v1 include/exclude dialect (specification 06 section 7.1).",
        "independently_derived": True,
        "comment": (
            "Single-rule cases exercise fullmatch of each rule against one "
            "path, including the no-slash shorthand (a rule without / is "
            "**/<rule>). Invalid cases MUST be refused by a writer before "
            "publication. Evaluation scenarios apply section 7.1's rules: "
            "exclude wins and prunes subtrees via ancestors; a path is "
            "captured when not excluded and the include list is empty or "
            "reaches it. Case-insensitive cases fold pattern and path."
        ),
        "match_cases": match_cases,
        "invalid_cases": invalid_cases,
        "evaluation_scenarios": evaluation_scenarios,
    }



# --------------------------------------------------------------------------
# Recovery kit (specifications/recovery-kit, sections 2-4)
# --------------------------------------------------------------------------


def _cbor_uint(value: int) -> bytes:
    """Minimal-length unsigned integer, major type 0 (deterministic CBOR)."""
    if value < 24:
        return bytes([value])
    if value < 0x100:
        return bytes([0x18, value])
    if value < 0x10000:
        return b"\x19" + value.to_bytes(2, "big")
    if value < 0x100000000:
        return b"\x1a" + value.to_bytes(4, "big")
    return b"\x1b" + value.to_bytes(8, "big")


def _cbor_head(major: int, argument: int) -> bytes:
    head = _cbor_uint(argument)
    return bytes([head[0] | (major << 5)]) + head[1:]


def _cbor_bytes(value: bytes) -> bytes:
    return _cbor_head(2, len(value)) + value


def _cbor_text(value: str) -> bytes:
    raw = value.encode("utf-8")
    return _cbor_head(3, len(raw)) + raw


def _cbor_map(entries: list) -> bytes:
    """Definite-length map of (uint key, encoded value) pairs, key-sorted."""
    body = b"".join(_cbor_uint(k) + v for k, v in sorted(entries))
    return _cbor_head(5, len(entries)) + body


def _cbor_array(items: list) -> bytes:
    return _cbor_head(4, len(items)) + b"".join(items)


def _kit_body(overrides: dict | None = None) -> bytes:
    """The synthetic v1 kit body: all ten keys, a placeholder key object
    (framing-level parsers treat key 5 as opaque past its magic).
    """
    fields = {
        1: _cbor_uint(1),
        2: _cbor_text("0.1.0"),
        3: _cbor_bytes(bytes.fromhex("0102030405060708090a0b0c0d0e0f10")),
        4: _cbor_uint(1),
        5: _cbor_bytes(b"FBPKKEYS" + bytes(88)),
        6: _cbor_map([
            (1, _cbor_uint(8192)),
            (2, _cbor_uint(1)),
            (3, _cbor_uint(1)),
            (4, _cbor_bytes(bytes(range(0x10, 0x20)))),
        ]),
        7: _cbor_array([
            _cbor_map([
                (1, _cbor_text("local-path")),
                (2, _cbor_text("file:///backups/fallbackplan")),
                (3, _cbor_text("")),
                (4, _cbor_text("")),
            ]),
        ]),
        8: _cbor_bytes(bytes([0x22]) * 16),
        9: _cbor_uint(1_722_600_000_000),
        10: _cbor_text("Install the recovery tool, then: recover --kit this-file."),
    }
    if overrides:
        fields.update(overrides)
    return _cbor_map(sorted(fields.items()))


def _kit_frame(body: bytes, version: int = 1, declared_length: int | None = None,
               corrupt_checksum: bool = False) -> bytes:
    header = b"FBPKRKIT" + u16(version) + u16(0) + u32(
        len(body) if declared_length is None else declared_length)
    checksum = hashlib.sha256(header + body).digest()
    if corrupt_checksum:
        checksum = bytes([checksum[0] ^ 0x01]) + checksum[1:]
    return header + body + checksum


def _kit_line_check(number: str, payload: str) -> str:
    digest = hashlib.sha256((number + ":" + payload.lower()).encode("utf-8")).digest()
    return b32(digest)[:4]


def _kit_text(framed: bytes) -> str:
    """The canonical section 4 layout: 12 groups of 4, per-line checks."""
    encoded = b32(framed)
    per_line = 48
    lines = [encoded[i : i + per_line] for i in range(0, len(encoded), per_line)]
    width = 3 if len(lines) >= 100 else 2
    rendered = ["FALLBACKPLAN RECOVERY KIT v1", ""]
    for index, payload in enumerate(lines):
        number = str(index + 1).zfill(width)
        groups = " ".join(payload[i : i + 4] for i in range(0, len(payload), 4))
        rendered.append(f"{number}: {groups} {_kit_line_check(number, payload)}")
    rendered.append("END FALLBACKPLAN RECOVERY KIT")
    return "\n".join(rendered) + "\n"


def recovery_kit_vectors() -> dict:
    """Specifications/recovery-kit sections 2-4 -- framing and text form."""
    body = _kit_body()
    framed = _kit_frame(body)

    refusals = [
        {
            "name": "checksum_flip",
            "framed_hex": _kit_frame(body, corrupt_checksum=True).hex(),
            "reason": "checksum does not verify (transcription or storage damage)",
        },
        {
            "name": "unknown_body_key",
            "framed_hex": _kit_frame(_cbor_map(
                sorted(({**{k: v for k, v in [
                    (1, _cbor_uint(1))]}, 11: _cbor_uint(0)}).items()))).hex(),
            "reason": "a v1 kit body assigns keys 1-10 only",
        },
        {
            "name": "version_mismatch",
            "framed_hex": _kit_frame(_kit_body({1: _cbor_uint(2)})).hex(),
            "reason": "framed version and body key 1 disagree",
        },
        {
            "name": "oversize_declared_body",
            "framed_hex": _kit_frame(body, declared_length=65 * 1024 + 1).hex(),
            "reason": "declared body exceeds the 64 KiB bound",
        },
        {
            "name": "truncated",
            "framed_hex": framed[:-8].hex(),
            "reason": "length does not match the framing declaration",
        },
        {
            "name": "wrong_magic",
            "framed_hex": (b"NOTAKIT!" + framed[8:]).hex(),
            "reason": "not a recovery kit",
        },
    ]

    text = _kit_text(framed)

    # A single-character transcription error must be caught by that LINE's
    # check, before the whole-kit checksum is even reachable.
    damaged_lines = text.split("\n")
    target = next(i for i, line in enumerate(damaged_lines) if line.startswith("02: "))
    damaged_lines[target] = damaged_lines[target].replace(" ", "  ", 1)
    line = damaged_lines[target]
    payload_start = line.index(":") + 1
    body_chars = line[payload_start:].replace(" ", "")
    flip = "a" if body_chars[0] != "a" else "b"
    damaged_lines[target] = line.replace(body_chars[0], flip, 1)
    damaged_text = "\n".join(damaged_lines)

    # Inline self-checks, the same discipline as every other builder.
    assert len(framed) == 16 + len(body) + 32
    assert hashlib.sha256(framed[:-32]).digest() == framed[-32:]
    assert _kit_line_check("01", b32(framed)[:48]) in text

    return {
        "description": "Recovery-kit framing and text form (specifications/recovery-kit sections 2-4).",
        "independently_derived": True,
        "comment": (
            "The kit body is synthetic: key 5 carries a placeholder FBPKKEYS "
            "prefix because framing-level conformance treats it as opaque "
            "bytes; unwrap-level conformance is exercised by the committed "
            "fixture kit, whose key object is real. Text-form cases pin the "
            "canonical layout exactly: base32 of the framed binary, 12 "
            "groups of 4 per line, per-line check = first 4 base32 chars of "
            "SHA-256(line_number ':' payload)."
        ),
        "kit": {
            "body_hex": body.hex(),
            "framed_hex": framed.hex(),
            "checksum_hex": framed[-32:].hex(),
            "text_form": text,
            "damaged_text_form": damaged_text,
            "fields": {
                "kit_format_version": 1,
                "minimum_tool_version": "0.1.0",
                "repository_id": "0102030405060708090a0b0c0d0e0f10",
                "repository_format_version": 1,
                "kdf": {"memory_kib": 8192, "iterations": 1, "parallelism": 1,
                        "salt": bytes(range(0x10, 0x20)).hex()},
                "issued_at": 1_722_600_000_000,
                "destination_count": 1,
            },
        },
        "refusal_cases": refusals,
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
    "aes-gcm.json": aes_gcm_vectors,
    "argon2id.json": argon2id_vectors,
    "ed25519.json": ed25519_vectors,
    "path-rules.json": path_rules_vectors,
    "recovery-kit.json": recovery_kit_vectors,
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

    if not args.check:
        # --check must not touch the filesystem at all, so the mkdir happens
        # only on the write path.
        VECTORS.mkdir(parents=True, exist_ok=True)
    failures = []

    for filename, builder in GROUPS.items():
        rendered = render(builder)
        path = VECTORS / filename
        if args.check:
            if not path.exists():
                failures.append(f"{filename}: missing")
            elif path.read_text(encoding="utf-8") != rendered:
                failures.append(f"{filename}: differs from freshly computed output")
        else:
            # Encoding and newlines pinned so the comparison above is not
            # locale- or platform-dependent.
            path.write_text(rendered, encoding="utf-8", newline="\n")
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
