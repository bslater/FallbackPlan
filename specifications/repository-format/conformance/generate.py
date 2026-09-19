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
# X25519 (RFC 7748), pure Python
#
# The write-only vectors (specification 03 section 9) need Curve25519 scalar
# multiplication. It is modular arithmetic, so like Ed25519 above it is
# implemented here from the RFC directly -- no reference implementation
# involved -- and gated by the RFC's own test vectors.
# --------------------------------------------------------------------------

_X_P = 2**255 - 19
_X_A24 = 121665


def _x25519_clamp(k: bytes) -> int:
    a = bytearray(k)
    a[0] &= 248
    a[31] &= 127
    a[31] |= 64
    return int.from_bytes(a, "little")


def x25519(scalar: bytes, u: bytes) -> bytes:
    """RFC 7748 section 5: the Montgomery ladder, constant-time not required here."""
    x1 = int.from_bytes(bytearray(u[:31]) + bytes([u[31] & 127]), "little")
    k = _x25519_clamp(scalar)
    x2, z2, x3, z3, swap = 1, 0, x1, 1, 0

    for t in range(254, -1, -1):
        bit = (k >> t) & 1
        swap ^= bit
        if swap:
            x2, x3, z2, z3 = x3, x2, z3, z2
        swap = bit

        a = (x2 + z2) % _X_P
        aa = a * a % _X_P
        b = (x2 - z2) % _X_P
        bb = b * b % _X_P
        e = (aa - bb) % _X_P
        c = (x3 + z3) % _X_P
        d = (x3 - z3) % _X_P
        da = d * a % _X_P
        cb = c * b % _X_P
        x3 = (da + cb) % _X_P
        x3 = x3 * x3 % _X_P
        z3 = (da - cb) % _X_P
        z3 = z3 * z3 % _X_P
        z3 = z3 * x1 % _X_P
        x2 = aa * bb % _X_P
        z2 = e * (aa + _X_A24 * e) % _X_P

    if swap:
        x2, x3, z2, z3 = x3, x2, z3, z2

    return (x2 * pow(z2, _X_P - 2, _X_P) % _X_P).to_bytes(32, "little")


def x25519_public(scalar: bytes) -> bytes:
    return x25519(scalar, (9).to_bytes(32, "little"))


def hkdf_extract(salt: bytes, ikm: bytes) -> bytes:
    """RFC 5869 section 2.2."""
    return hmac.new(salt, ikm, hashlib.sha256).digest()


def _x25519_self_test() -> None:
    """RFC 7748 sections 5.2 and 6.1 gate every X25519 value emitted below."""
    scalar = bytes.fromhex("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4")
    u = bytes.fromhex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c")
    if x25519(scalar, u).hex() != "c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552":
        sys.exit("FATAL: X25519 self-test failed (RFC 7748 section 5.2 vector 1).")

    alice = bytes.fromhex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a")
    bob_public = bytes.fromhex("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f")
    if x25519_public(alice).hex() != "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a":
        sys.exit("FATAL: X25519 self-test failed (RFC 7748 section 6.1 public key).")
    if x25519(alice, bob_public).hex() != "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742":
        sys.exit("FATAL: X25519 self-test failed (RFC 7748 section 6.1 shared secret).")


# --------------------------------------------------------------------------
# Fixed inputs
#
# Every value here is a constant so the vectors are reproducible. Nothing is
# random, nothing depends on the clock, and running this twice must produce
# byte-identical output.
# --------------------------------------------------------------------------

# The root is Argon2id output and is PINNED here, exactly as argon2id.json
# pins the KDF (no independent implementation exists in this generator).
# Everything below it -- every group in this suite that needs a key -- derives
# from this one root, so the files cannot drift from each other.
ROOT = bytes(range(0xC0, 0xE0))                    # c0 c1 c2 ... df
REPOSITORY_ID = bytes.fromhex("0102030405060708090a0b0c0d0e0f10")
WRITER_ID = bytes.fromhex("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf")
BLOB_SALT = bytes([0x5A]) * 32
BLOB_COUNTER = 42
FORMAT_VERSION = 2

INFO_BLOB = b"fbp/blob/v1"      # the per-blob construction did not change between formats (03 section 5)

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


def write_only_tree() -> dict:
    """The derivation tree off the root (specification 03 section 9.1): the
    sealing keypair, the repository-scoped keys, and the generational keys
    under their sub-roots. One derivation, every file -- no drift."""
    sealing_scalar = hkdf_expand(ROOT, b"fbp/seal/v2", 32)
    structure_root = hkdf_expand(ROOT, b"fbp/metadata/v2", 32)
    content_id_key = hkdf_expand(ROOT, b"fbp/content-id/v2", 32)
    key_id_key = hkdf_expand(ROOT, b"fbp/key-id/v2", 32)
    signing_root = hkdf_expand(ROOT, b"fbp/signing/v2", 32)
    return {
        "sealing_scalar": sealing_scalar,
        "sealing_public_key": x25519_public(sealing_scalar),
        "structure_root": structure_root,
        "content_id_key": content_id_key,
        "key_id_key": key_id_key,
        "signing_root": signing_root,
        "metadata_key_generation_0": hkdf_expand(structure_root, b"fbp/metadata-generation/v2" + u32(0), 32),
        "metadata_key_generation_1": hkdf_expand(structure_root, b"fbp/metadata-generation/v2" + u32(1), 32),
        "signing_seed_generation_0": hkdf_expand(signing_root, b"fbp/signing-generation/v2" + u32(0), 32),
    }


def signing_seed(generation: int) -> bytes:
    """The Ed25519 seed for a key generation (03 section 9.1, ADR-0020)."""
    return hkdf_expand(write_only_tree()["signing_root"], b"fbp/signing-generation/v2" + u32(generation), 32)


# --------------------------------------------------------------------------
# Vector groups
# --------------------------------------------------------------------------


def write_only_vectors() -> dict:
    """Specification 03 -- the derivation tree, the per-blob key, and the
    sealed-content-key key agreement."""
    _x25519_self_test()

    root = ROOT
    tree = write_only_tree()
    sealing_scalar = tree["sealing_scalar"]
    structure_root = tree["structure_root"]
    content_id_key = tree["content_id_key"]
    key_id_key = tree["key_id_key"]
    signing_root = tree["signing_root"]

    metadata_key_0 = tree["metadata_key_generation_0"]
    metadata_key_1 = tree["metadata_key_generation_1"]
    signing_seed_0 = tree["signing_seed_generation_0"]

    sealing_public = tree["sealing_public_key"]

    # The per-blob key (03 section 5): the class key of the blob's
    # generation expanded over the salt, writer and counter the envelope
    # carries. For a metadata blob, and for a sealed data blob's footer,
    # the class key is the metadata key; a sealed data blob's records derive
    # the same way from the content key its sealed share opens to.
    blob_info = INFO_BLOB + BLOB_SALT + WRITER_ID + u64(BLOB_COUNTER)
    blob_key = hkdf_expand(metadata_key_0, blob_info, 32)

    # Same salt, different writer -- must differ. This is the property that
    # makes key separation independent of CSPRNG quality (PT-13).
    other_writer = bytes([0xB0]) * 16
    blob_key_other_writer = hkdf_expand(
        metadata_key_0, INFO_BLOB + BLOB_SALT + other_writer + u64(BLOB_COUNTER), 32
    )
    # Same salt and writer, different counter -- must also differ.
    blob_key_other_counter = hkdf_expand(
        metadata_key_0, INFO_BLOB + BLOB_SALT + WRITER_ID + u64(BLOB_COUNTER + 1), 32
    )
    assert blob_key != blob_key_other_writer
    assert blob_key != blob_key_other_counter

    # The content-key sealing's key agreement (05 section 2.1): a pinned
    # ephemeral scalar stands in for the CSPRNG draw; the AEAD key is
    # HKDF extract-then-expand over the shared secret, salted by both
    # public shares. The final AES-256-GCM step is not vectored here --
    # the generator cannot compute it (the aes-gcm.json posture); it is
    # covered by the implementation's primitive tests and round-trips.
    ephemeral_scalar = bytes(range(0x40, 0x60))
    ephemeral_public = x25519_public(ephemeral_scalar)
    shared_secret = x25519(ephemeral_scalar, sealing_public)
    aead_key = hkdf_expand(
        hkdf_extract(ephemeral_public + sealing_public, shared_secret),
        b"fbp/seal-content/v2",
        32,
    )

    # Opening from the other side must agree -- the recipient computes the
    # same shared secret from its scalar and the ephemeral share.
    assert x25519(sealing_scalar, ephemeral_public) == shared_secret

    members = [
        sealing_scalar, sealing_public, structure_root, content_id_key,
        key_id_key, signing_root, metadata_key_0, metadata_key_1, signing_seed_0,
    ]
    assert len({m.hex() for m in members}) == len(members), "domains must be pairwise distinct"

    return {
        "description": (
            "Repository key derivation (specification 03, ADR-0042): the root's "
            "one-way expansion into the sealing keypair and the write bundle, "
            "the per-blob key, and the sealed-content-key key agreement."
        ),
        "independently_derived": True,
        "inputs": {
            "root": root.hex(),
            "root_note": (
                "root = Argon2id(passphrase, kdf_salt, kdf_parameters); pinned "
                "here because Argon2id has no independent implementation in "
                "this generator (see argon2id.json)."
            ),
        },
        "derived": {
            "sealing_scalar": sealing_scalar.hex(),
            "sealing_public_key": sealing_public.hex(),
            "structure_root": structure_root.hex(),
            "content_id_key": content_id_key.hex(),
            "key_id_key": key_id_key.hex(),
            "signing_root": signing_root.hex(),
            "metadata_key_generation_0": metadata_key_0.hex(),
            "metadata_key_generation_1": metadata_key_1.hex(),
            "signing_seed_generation_0": signing_seed_0.hex(),
        },
        "blob_key": {
            "comment": (
                "blob_key = HKDF-Expand(class_key, 'fbp/blob/v1' || blob_salt || "
                "writer_id || u64(blob_counter), 32) (specification 03 section 5). "
                "The class key here is metadata_key_generation_0 -- what a "
                "metadata blob, and a sealed data blob's footer, derive under; "
                "a sealed data blob's records derive the same way from the "
                "content key its sealed share opens to. The label keeps its v1 "
                "spelling: the construction did not change between formats."
            ),
            "inputs": {
                "class_key": metadata_key_0.hex(),
                "class_key_is": "metadata_key_generation_0",
                "writer_id": WRITER_ID.hex(),
                "blob_salt": BLOB_SALT.hex(),
                "blob_counter": BLOB_COUNTER,
            },
            "blob_key": blob_key.hex(),
            "separation_checks": {
                "comment": (
                    "Same blob_salt with a different writer_id or blob_counter must "
                    "produce a different blob key. This is what makes key separation "
                    "survive a cloned VM replaying CSPRNG state."
                ),
                "blob_key_other_writer": blob_key_other_writer.hex(),
                "blob_key_other_counter": blob_key_other_counter.hex(),
            },
        },
        "content_key_sealing": {
            "ephemeral_scalar": ephemeral_scalar.hex(),
            "ephemeral_public_key": ephemeral_public.hex(),
            "shared_secret": shared_secret.hex(),
            "hkdf_salt": (ephemeral_public + sealing_public).hex(),
            "hkdf_info": "fbp/seal-content/v2",
            "aead_key": aead_key.hex(),
            "comment": (
                "AEAD key = HKDF-SHA256(extract(salt=ephemeral_public || "
                "sealing_public_key, ikm=shared_secret), info) -- the final "
                "AES-256-GCM seal (zero nonce, AAD repository_id || blob_id) "
                "is deliberately not vectored; see aes-gcm.json."
            ),
        },
    }


def identifier_vectors() -> dict:
    """Specification 02 -- content and object identifiers."""
    tree = write_only_tree()
    content_id_key = tree["content_id_key"]
    key_id_key = tree["key_id_key"]

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
    content_id_key = write_only_tree()["content_id_key"]
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


def records_v3_vectors() -> dict:
    """Format-3 records (specification 04 sections 2-4, 03 section 5.4;
    ADR-0052 Amendment 1): the key is the record's, derived from the class
    key and the record's own type and identifier; the nonce is carried; the
    AAD omits the ordinal. Everything here is HKDF and concatenation, so it
    is derived, not pinned -- the same root and object as records.json, so
    the two files describe one record under two formats."""
    tree = write_only_tree()
    content_id = hashlib.sha256(b"hello world").digest()
    object_id = hmac.new(
        tree["content_id_key"], bytes([OBJECT_TYPE_SEGMENT]) + content_id, hashlib.sha256
    ).digest()
    class_key = tree["metadata_key_generation_0"]
    format_version = 3

    def record_key(object_type: int, oid: bytes) -> bytes:
        return hkdf_expand(class_key, b"fbp/record/v3" + bytes([object_type]) + oid, 32)

    aad = REPOSITORY_ID + u16(format_version) + bytes([OBJECT_TYPE_SEGMENT]) + object_id
    assert len(aad) == 51, f"format-3 AAD must be 51 bytes, got {len(aad)}"

    other_object_id = hmac.new(
        tree["content_id_key"], bytes([OBJECT_TYPE_SEGMENT]) + hashlib.sha256(b"goodbye world").digest(),
        hashlib.sha256,
    ).digest()

    # The writer's per-blob seed and the content key it derives for a data
    # record before sealing it (03 section 5.4). Any 32 bytes stand in for a
    # CSPRNG draw; what the vector pins is the derivation.
    seed = bytes([0x33] * 32)
    seed_key = hkdf_expand(seed, b"fbp/record-seed/v3" + object_id, 32)

    return {
        "description": (
            "Format-3 record key derivation, carried nonce and associated data "
            "(specification 04 sections 2-4, 03 section 5.4)."
        ),
        "independently_derived": True,
        "inputs": {
            "repository_id": REPOSITORY_ID.hex(),
            "format_version": format_version,
            "object_type": OBJECT_TYPE_SEGMENT,
            "object_id": object_id.hex(),
            "class_key": class_key.hex(),
            "class_key_provenance": "write-only.json derived.metadata_key_generation_0",
        },
        "record_key": {
            "info": "fbp/record/v3 || u8(object_type) || object_id",
            "record_key": record_key(OBJECT_TYPE_SEGMENT, object_id).hex(),
            "separation_checks": {
                "record_key_other_object": record_key(OBJECT_TYPE_SEGMENT, other_object_id).hex(),
                "record_key_other_type": record_key(0x02, object_id).hex(),
            },
        },
        "prefix": {
            "nonce": RECORD_V3_VECTOR_NONCE.hex(),
            "nonce_comment": "Carried in the record's prefix; a real record draws twelve random bytes.",
            "metadata_prefix_length": 12,
            "sealed_data_prefix_length": 12 + 80,
        },
        "aad": aad.hex(),
        "aad_length": len(aad),
        "seed_derivation": {
            "info": "fbp/record-seed/v3 || object_id",
            "seed": seed.hex(),
            "record_content_key": seed_key.hex(),
        },
    }


def merkle_vectors() -> dict:
    """The Merkle commitment over a sealed blob's bytes (specification 05
    section 5; ADR-0052 open question 4). RFC 6962's tree over one-mebibyte
    leaves of the digest's own preimage, with the preimage's length hashed
    into the published root under a prefix of its own -- without that binding
    a four-leaf tree's first path verifies under a claimed size of three, and
    a destination could understate its length to exempt its last leaf from
    ever being drawn. Everything here is SHA-256 and concatenation, so it is
    derived, not pinned."""
    leaf_size = 1024 * 1024

    def leaf(chunk: bytes) -> bytes:
        return hashlib.sha256(b"\x00" + chunk).digest()

    def node(left: bytes, right: bytes) -> bytes:
        return hashlib.sha256(b"\x01" + left + right).digest()

    def split(count: int) -> int:
        k = 1
        while k * 2 < count:
            k *= 2
        return k

    def mth(leaves: list) -> bytes:
        if not leaves:
            return hashlib.sha256(b"").digest()
        if len(leaves) == 1:
            return leaves[0]
        k = split(len(leaves))
        return node(mth(leaves[:k]), mth(leaves[k:]))

    def bind(length: int, head: bytes) -> bytes:
        return hashlib.sha256(b"\x02" + u64(length) + head).digest()

    def path(leaves: list, index: int) -> list:
        if len(leaves) <= 1:
            return []
        k = split(len(leaves))
        if index < k:
            return path(leaves[:k], index) + [mth(leaves[k:])]
        return path(leaves[k:], index - k) + [mth(leaves[:k])]

    def stream(length: int) -> bytes:
        out = bytearray()
        counter = 0
        while len(out) < length:
            out += hashlib.sha256(u64(counter)).digest()
            counter += 1
        return bytes(out[:length])

    # Synthetic leaf hashes, so the tree arithmetic can be pinned for shapes
    # whose real preimages would be megabytes. They stand in for chunk
    # hashes; what these cases fix is the split and the folding.
    synthetic = [hashlib.sha256(b"\x00" + bytes([i])).digest() for i in range(8)]
    shapes = []
    for count in range(1, 9):
        length = ((count - 1) * leaf_size) + 1
        leaves = synthetic[:count]
        shapes.append(
            {
                "leaf_count": count,
                "preimage_length": length,
                "split_point": split(count) if count > 1 else 0,
                "mth": mth(leaves).hex(),
                "root": bind(length, mth(leaves)).hex(),
            }
        )

    # Paths are pinned over a real preimage, not over the synthetic hashes:
    # a verifier hashes the chunk it is handed and checks its length against
    # the tree the root names, so a path case has to carry chunks that could
    # actually sit at those offsets.
    five_length = (4 * leaf_size) + 4096
    five_preimage = stream(five_length)
    five = [
        leaf(five_preimage[offset:offset + leaf_size])
        for offset in range(0, five_length, leaf_size)
    ]
    paths = [
        {
            "leaf_index": index,
            "chunk_offset": index * leaf_size,
            "chunk_length": min(leaf_size, five_length - (index * leaf_size)),
            "path": [step.hex() for step in path(five, index)],
        }
        for index in range(len(five))
    ]

    # Two whole preimages the reader can rebuild byte for byte: the stream is
    # concatenated SHA-256(BE64(counter)), the same shape the committed
    # fixtures' file content uses.
    wholes = []
    for name, length in [
        ("under_one_leaf", 3), ("exactly_one_leaf", leaf_size),
        ("one_byte_over_one_leaf", leaf_size + 1), ("two_leaves_and_a_tail", (2 * leaf_size) + 12_345),
    ]:
        preimage = stream(length)
        leaves = [
            leaf(preimage[offset:offset + leaf_size]) for offset in range(0, max(length, 1), leaf_size)
        ]
        wholes.append(
            {
                "name": name,
                "preimage_length": length,
                "leaf_count": len(leaves),
                "root": bind(length, mth(leaves)).hex(),
            }
        )

    return {
        "description": (
            "Merkle commitment over a sealed blob's bytes: RFC 6962 leaves and nodes, "
            "one-mebibyte chunks, and a root bound to the preimage's length "
            "(specification 05 section 5)."
        ),
        "independently_derived": True,
        "parameters": {
            "leaf_size": leaf_size,
            "leaf_prefix": "00",
            "node_prefix": "01",
            "root_prefix": "02",
            "root_construction": "SHA-256(0x02 || u64_be(preimage_length) || MTH(leaf_hashes))",
            "preimage": "bytes [0, blob_length - 16) -- the flat digest's preimage, 05 section 5",
        },
        "primitives": {
            "leaf_of_empty_chunk": leaf(b"").hex(),
            "leaf_of_abc": leaf(b"abc").hex(),
            "node_of_two_leaves": node(leaf(b"abc"), leaf(b"def")).hex(),
            "leaf_and_node_differ_comment": (
                "SHA-256('abc') is not leaf('abc'): without the prefix a one-leaf tree's head "
                "would be the chunk's bare digest."
            ),
            "sha256_of_abc": hashlib.sha256(b"abc").hexdigest(),
        },
        "synthetic_leaf_hashes": [value.hex() for value in synthetic],
        "shapes": shapes,
        "authentication_paths": {
            "leaf_count": len(five),
            "preimage_length": five_length,
            "root": bind(five_length, mth(five)).hex(),
            "paths": paths,
        },
        "whole_preimages": {
            "stream": "concatenated SHA-256(BE64(counter)) from counter 0, truncated to preimage_length",
            "cases": wholes,
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


# Computed ONCE with System.Security.Cryptography.AesGcm over the inputs
# case 2 below assembles from the other groups, and pinned. If those inputs
# change, CryptographicPrimitiveTests fails until these are recomputed --
# by running the platform over the new inputs, never from memory.
AES_GCM_CASE_2_CIPHERTEXT = (
    "b41f78c1c843a7769ad72605805380febf0ec1116454f7165986026e9461eacc"
    "ceedb95d926f6d10d9ad38296aa326af54"
)
AES_GCM_CASE_2_TAG = "412ae7ebd757a4d4836bf14210248da1"

# Case 3: the format-3 record construction (04 sections 2-4, 03 section 5.4).
# Computed ONCE with an independent AES-256-GCM (Node's crypto, OpenSSL
# underneath) over the key, nonce and AAD records-v3.json derives, and
# pinned. A regression vector, not conformance evidence, exactly as case 2.
AES_GCM_CASE_3_CIPHERTEXT = (
    "5b864b6faf5d1f5b185a6f32413cc12d0e6679f218d8309516abef99bd097d5a"
    "fc59e75862e94a86108f883dbd0c3e"
)
AES_GCM_CASE_3_TAG = "f52db06ac1176245e4fe7a4c77ffc32c"

# The nonce the format-3 vector case carries. A real record draws twelve
# random bytes; a vector needs a fixed one, and this is any twelve.
RECORD_V3_VECTOR_NONCE = bytes(range(0x30, 0x3C))


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
    uses the format's REAL construction: the blob key pinned in write-only.json,
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
                    "vector, not conformance evidence. Key is write-only.json "
                    "blob_key; nonce and AAD are records.json ordinal 47."
                ),
                "provenance_reverified": False,
                "key": write_only_vectors()["blob_key"]["blob_key"],
                "iv": "00000000000000000000002f",
                "plaintext": (
                    "46616c6c6261636b506c616e20636f6e666f726d616e63652073756974653a"
                    "207265636f7264206f7264696e616c203437"
                ),
                "aad": next(
                    case["aad"] for case in aad_vectors()["cases"] if case["ordinal"] == 47
                ),
                "ciphertext": AES_GCM_CASE_2_CIPHERTEXT,
                "tag": AES_GCM_CASE_2_TAG,
            },
            {
                "name": "record_v3_real_construction",
                "provenance": (
                    "platform-derived: computed once with an independent "
                    "AES-256-GCM (Node crypto over OpenSSL) and pinned. "
                    "Regression vector, not conformance evidence. Key is "
                    "records-v3.json record_key; nonce and 51-byte AAD are "
                    "records-v3.json prefix.nonce and aad (format 3, no ordinal)."
                ),
                "provenance_reverified": False,
                "key": records_v3_vectors()["record_key"]["record_key"],
                "iv": RECORD_V3_VECTOR_NONCE.hex(),
                "plaintext": (
                    "46616c6c6261636b506c616e20636f6e666f726d616e63652073756974653a"
                    "20666f726d61742d33207265636f7264"
                ),
                "aad": records_v3_vectors()["aad"],
                "ciphertext": AES_GCM_CASE_3_CIPHERTEXT,
                "tag": AES_GCM_CASE_3_TAG,
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
    section 9.1 derives them, proving the seed interpretation end to end: the
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
        seed = signing_seed(generation)
        public = ed25519_public_key(seed)
        signature = ed25519_sign(seed, message)

        format_cases.append(
            {
                "name": f"format_signing_seed_generation_{generation}",
                "generation": generation,
                "seed_derivation": (
                    "HKDF-Expand(signing_root, 'fbp/signing-generation/v2' || u32(generation), 32), "
                    "signing_root = HKDF-Expand(root, 'fbp/signing/v2', 32)"
                ),
                "seed": seed.hex(),
                "public_key": public.hex(),
                "message": message.hex(),
                "message_comment": message_comment,
                "signature": signature.hex(),
            }
        )

    # Generation 0's seed must equal write-only.json's
    # signing_seed_generation_0 -- one derivation, two files, no drift.
    assert format_cases[0]["seed"] == write_only_tree()["signing_seed_generation_0"].hex()

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

    # DOTALL because section 7.1 says `.` is "any character" and a newline is
    # a legal character in a POSIX filename. Without it a trailing `**`, which
    # compiles to `.+`, fails to reach `secrets/ssh\nkey` -- an exclude rule
    # that does not reach a file is a file that gets copied.
    #
    # Matching is by `fullmatch` throughout, never `match` with `$`: `$` also
    # matches immediately before a final newline, which would make the two
    # different names `keep.txt` and `keep.txt\n` one name to a rule.
    flags = re.DOTALL | (0 if case_sensitive else re.IGNORECASE)
    try:
        return re.compile(pattern, flags)
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
        # Names hostile to the regex a rule compiles into. A newline is legal
        # in a POSIX filename, and both the "any character" of `.` and the
        # "whole path" of implicit anchoring have to mean it -- an
        # implementation whose `.` stops at a newline lets a file out of an
        # exclude, and one that anchors with `$` merges two different names.
        ("secrets/**", "secrets/ssh\nkey", True, True),
        ("secrets/**", "secrets/two\nline\nname", True, True),
        ("*.key", "host\nname.key", True, True),
        ("keep.txt", "keep.txt\n", True, False),
        ("docs/keep.txt", "docs/keep.txt\n", True, False),
        (r"re:docs/.*", "docs/we\nird", True, True),
        # Regex metacharacters as literal characters in a glob rule.
        ("a$b.txt", "a$b.txt", True, True),
        ("a$b.txt", "ab.txt", True, False),
        ("v1.0", "v1.0", True, True),
        ("v1.0", "v1x0", True, False),
        ("(draft)", "notes/(draft)", True, True),
        ("a+b", "a+b", True, True),
        ("a+b", "aab", True, False),
        ("a|b", "a|b", True, True),
        ("a|b", "a", True, False),
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
        {
            # A filename is attacker-influenced input in any shared directory,
            # and an exclude list is a promise. Nothing about a name may let a
            # path out of a subtree exclusion.
            "name": "hostile_names_do_not_escape_exclusion",
            "includes": [],
            "excludes": ["secrets/**", "*.key", "*$"],
            "case_sensitive": True,
            "paths": [
                "secrets/ssh\nkey",
                "secrets/inner/ssh\nkey",
                "secrets/trailing\n",
                "home/host\nname.key",
                "home/notes$",
                "home/notes",
                "home/keys",
            ],
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
# Driver
# --------------------------------------------------------------------------

GROUPS = {
    "write-only.json": write_only_vectors,
    "identifiers.json": identifier_vectors,
    "records.json": aad_vectors,
    "records-v3.json": records_v3_vectors,
    "merkle.json": merkle_vectors,
    "segmentation.json": segmentation_vectors,
    "compression.json": compression_vectors,
    "aes-gcm.json": aes_gcm_vectors,
    "argon2id.json": argon2id_vectors,
    "ed25519.json": ed25519_vectors,
    "path-rules.json": path_rules_vectors,
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
