#!/usr/bin/env python3
"""Test vectors for docs/bodu-merkle-requirements.md.

Computes every value in that document's appendices from the definitions in its
section 3 — RFC 6962's Merkle Tree Hash, inclusion proofs, consistency proofs,
the block mode and the length-bound root — using nothing but the Python
standard library, so any implementer can reproduce them without installing
anything and without trusting either implementation.

    python3 eng/bodu-merkle-vectors.py            # print the vectors
    python3 eng/bodu-merkle-vectors.py --check    # verify the document carries them

The --check mode asserts that every computed value appears in the document. It
does not parse the tables, so it catches a value that drifted, not a row that
was deleted.
"""

import argparse
import hashlib
import pathlib
import struct
import sys

DOC = pathlib.Path(__file__).resolve().parent.parent / "docs" / "bodu-merkle-requirements.md"

# --- section 3.1: hashing and domain separation ------------------------------

EMPTY = hashlib.sha256(b"").digest()


def leaf_hash(entry: bytes) -> bytes:
    return hashlib.sha256(b"\x00" + entry).digest()


def node_hash(left: bytes, right: bytes) -> bytes:
    return hashlib.sha256(b"\x01" + left + right).digest()


def split_point(count: int) -> int:
    """The largest power of two strictly below count (section 3.2)."""
    k = 1
    while k * 2 < count:
        k *= 2
    return k


# --- section 3.2: the Merkle Tree Hash ---------------------------------------


def mth(entries: list) -> bytes:
    if not entries:
        return EMPTY
    if len(entries) == 1:
        return leaf_hash(entries[0])
    k = split_point(len(entries))
    return node_hash(mth(entries[:k]), mth(entries[k:]))


# --- section 3.3 and 3.4: inclusion proofs -----------------------------------


def inclusion_path(index: int, entries: list) -> list:
    if len(entries) <= 1:
        return []
    k = split_point(len(entries))
    if index < k:
        return inclusion_path(index, entries[:k]) + [mth(entries[k:])]
    return inclusion_path(index - k, entries[k:]) + [mth(entries[:k])]


def verify_inclusion(root: bytes, size: int, index: int, entry: bytes, path: list) -> bool:
    """RFC 6962 section 2.1.1, verbatim — including its inner-shift wording."""
    if size <= 0 or index < 0 or index >= size:
        return False

    fn, sn = index, size - 1
    running = leaf_hash(entry)
    for step in path:
        if sn == 0 or len(step) != 32:
            return False
        if (fn & 1) or fn == sn:
            running = node_hash(step, running)
            if not (fn & 1):
                while not (fn & 1) and sn != 0:
                    fn >>= 1
                    sn >>= 1
        else:
            running = node_hash(running, step)
        fn >>= 1
        sn >>= 1

    return sn == 0 and running == root


# --- section 3.5: consistency proofs -----------------------------------------


def _subproof(first: int, entries: list, on_boundary: bool) -> list:
    if first == len(entries):
        return [] if on_boundary else [mth(entries)]
    k = split_point(len(entries))
    if first <= k:
        return _subproof(first, entries[:k], on_boundary) + [mth(entries[k:])]
    return _subproof(first - k, entries[k:], False) + [mth(entries[:k])]


def consistency_proof(first: int, entries: list) -> list:
    if first == len(entries):
        return []
    return _subproof(first, entries, True)


# --- section 3.6 and 3.7: block mode and the bound root ----------------------


def blocks(length: int, block_size: int) -> list:
    stream = (bytes(range(256)) * ((length // 256) + 1))[:length]
    return [stream[at:at + block_size] for at in range(0, length, block_size)]


def bound_root(root: bytes, value: int) -> bytes:
    return hashlib.sha256(b"\x02" + struct.pack(">Q", value) + root).digest()


# --- the vectors -------------------------------------------------------------

ENTRIES = [
    bytes.fromhex(h) for h in [
        "", "00", "10", "2021", "3031", "40414243",
        "5051525354555657", "606162636465666768696a6b6c6d6e6f",
    ]
]

BLOCK_SIZE = 4
LENGTHS = [0, 1, 4, 5, 8, 9, 12, 13, 16, 17, 20, 28, 32, 33]


def vectors() -> list:
    """Every (label, hex) pair the document carries."""
    out = []

    for n in range(len(ENTRIES) + 1):
        out.append((f"A entries={n}", mth(ENTRIES[:n]).hex()))

    for size in (7, 8):
        subject = ENTRIES[:size]
        root = mth(subject)
        for index in range(size):
            path = inclusion_path(index, subject)
            assert verify_inclusion(root, size, index, subject[index], path), (size, index)
            for step, value in enumerate(path):
                out.append((f"A{size - 5} size={size} leaf={index} step={step}", value.hex()))

    for first, second in [(1, 1), (1, 8), (2, 5), (3, 7), (4, 8), (6, 8), (7, 8)]:
        for step, value in enumerate(consistency_proof(first, ENTRIES[:second])):
            out.append((f"A4 {first}->{second} step={step}", value.hex()))

    for length in LENGTHS:
        root = mth(blocks(length, BLOCK_SIZE))
        out.append((f"B length={length}", root.hex()))
        if length:
            out.append((f"C length={length}", bound_root(root, length).hex()))

    return out


def ambiguity() -> list:
    """Section 3.7's worked case, which a conforming verifier must reproduce."""
    four = blocks(16, BLOCK_SIZE)
    root = mth(four)
    path = inclusion_path(0, four)
    return [
        ("D root", root.hex()),
        *[(f"D path step={i}", p.hex()) for i, p in enumerate(path)],
        ("D accepts size=4", verify_inclusion(root, 4, 0, four[0], path)),
        ("D accepts size=3", verify_inclusion(root, 3, 0, four[0], path)),
        ("D bound(16) == bound(12)", bound_root(root, 16) == bound_root(mth(blocks(12, BLOCK_SIZE)), 12)),
    ]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true",
                        help="verify the document carries every computed value")
    args = parser.parse_args()

    computed = vectors()

    # Self-tests before anything is printed or checked: a generator whose own
    # arithmetic is wrong would otherwise "verify" a document that agreed with
    # it.
    assert mth([]) == EMPTY
    assert mth([b"x"]) == leaf_hash(b"x") != hashlib.sha256(b"x").digest()
    three = [b"a", b"b", b"c"]
    assert mth(three) == node_hash(node_hash(leaf_hash(b"a"), leaf_hash(b"b")), leaf_hash(b"c"))
    assert mth(three) != node_hash(leaf_hash(b"a"), node_hash(leaf_hash(b"b"), leaf_hash(b"c")))
    for size in range(1, 65):
        subject = [bytes([i % 251]) for i in range(size)]
        root = mth(subject)
        for index in range(size):
            path = inclusion_path(index, subject)
            assert verify_inclusion(root, size, index, subject[index], path)
            assert not verify_inclusion(root, size, index, subject[index], path[:-1] if path else [b"\x00" * 32])

    if not args.check:
        for label, value in computed:
            print(f"{label:34} {value}")
        print()
        for label, value in ambiguity():
            print(f"{label:34} {value}")
        print(f"\nself-tests passed; {len(computed)} values computed")
        return 0

    text = DOC.read_text(encoding="utf-8")
    missing = [f"{label}: {value}" for label, value in computed if value not in text]
    if missing:
        for entry in missing:
            print(f"FAIL missing from {DOC.name}: {entry}", file=sys.stderr)
        return 1

    print(f"{DOC.name}: all {len(computed)} vectors present; self-tests passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
