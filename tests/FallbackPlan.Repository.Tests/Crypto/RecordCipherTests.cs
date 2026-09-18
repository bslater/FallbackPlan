using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Crypto;

/// <summary>
/// The B5 acceptance criteria, verbatim: a record moved between ordinals or
/// repositories fails authentication (specification 04 §4; NFR-SEC-003). The
/// AAD binds repository, format version, object type, object identifier, and
/// ordinal — each variation must break the tag.
/// </summary>
[TestClass]
public sealed class RecordCipherTests
{
    private static readonly byte[] BlobKey = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    private static readonly RepositoryId RepoA = RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));
    private static readonly RepositoryId RepoB = RepositoryId.FromBytes(Convert.FromHexString("ffffffffffffffffffffffffffffffff"));
    private static readonly ObjectId SomeId = ObjectId.FromBytes(new byte[32]);

    private static (byte[] Ciphertext, byte[] Tag) SealAt(uint ordinal, RepositoryId repository, byte[] plaintext)
    {
        var nonce = new byte[RecordNonce.AesGcmLength];
        RecordNonce.Write(ordinal, nonce);
        var aad = new byte[RecordAad.Length];
        RecordAad.Write(repository, FormatLimits.FormatVersion, ObjectType.SegmentRecord, SomeId, ordinal, aad);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[RecordCipher.TagLength];
        RecordCipher.Seal(BlobKey, nonce, aad, plaintext, ciphertext, tag);

        return (ciphertext, tag);
    }

    private static bool OpenAt(uint ordinal, RepositoryId repository, byte[] ciphertext, byte[] tag, byte[] destination)
    {
        var nonce = new byte[RecordNonce.AesGcmLength];
        RecordNonce.Write(ordinal, nonce);
        var aad = new byte[RecordAad.Length];
        RecordAad.Write(repository, FormatLimits.FormatVersion, ObjectType.SegmentRecord, SomeId, ordinal, aad);

        return RecordCipher.TryOpen(BlobKey, nonce, aad, ciphertext, tag, destination);
    }

    [TestMethod]
    public void RecordCipher_AtItsOwnOrdinalAndRepository_Opens()
    {
        var plaintext = "segment plaintext"u8.ToArray();
        var (ciphertext, tag) = SealAt(47, RepoA, plaintext);
        var restored = new byte[plaintext.Length];

        Assert.IsTrue(OpenAt(47, RepoA, ciphertext, tag, restored));
        SequenceAssert.AreEqual(plaintext, restored);
    }

    [TestMethod]
    public void RecordCipher_MovedToADifferentOrdinal_FailsAuthentication()
    {
        var (ciphertext, tag) = SealAt(47, RepoA, "segment plaintext"u8.ToArray());

        Assert.IsFalse(OpenAt(48, RepoA, ciphertext, tag, new byte[ciphertext.Length]));
    }

    [TestMethod]
    public void RecordCipher_MovedToADifferentRepository_FailsAuthentication()
    {
        var (ciphertext, tag) = SealAt(47, RepoA, "segment plaintext"u8.ToArray());

        Assert.IsFalse(OpenAt(47, RepoB, ciphertext, tag, new byte[ciphertext.Length]));
    }

    [TestMethod]
    public void RecordCipher_MovedToADifferentBlob_FailsBecauseTheKeyDoesNotTravel()
    {
        // The third of architecture 03 §3.5's negative test — "a record moved
        // between blobs, ordinals, or repositories fails authentication" — and
        // the one the other two methods here never covered, because they hold
        // the blob key fixed and vary only what the AAD binds.
        //
        // It fails for a different reason from its two siblings, and the
        // difference is the point [ADR-0025](../../../docs/adr/0025-compaction-reseals-records.md)
        // §3 rests on: the AAD does not name the blob, so nothing here breaks
        // a tag over a mismatched field. The record simply cannot be opened,
        // because the key is derived from the blob's own salt, writer and
        // counter and none of those travel with the bytes. That is what makes
        // compaction a decrypt-and-reseal operation rather than a copy — and
        // it is precisely the property format v3 changes
        // ([ADR-0052](../../../docs/adr/0052-relocatable-records-format-v3.md)),
        // which is why it is worth having executable before it moves.
        var classKey = Enumerable.Range(0, 32).Select(value => (byte)(value ^ 0x5a)).ToArray();
        var writer = WriterId.FromBytes(Enumerable.Repeat((byte)0x11, WriterId.Size).ToArray());

        var source = new byte[BlobKeyDeriver.BlobKeyLength];
        BlobKeyDeriver.Derive(
            classKey, Enumerable.Repeat((byte)0xa1, BlobKeyDeriver.BlobSaltLength).ToArray(), writer, 7, source);

        var elsewhere = new byte[BlobKeyDeriver.BlobKeyLength];
        BlobKeyDeriver.Derive(
            classKey, Enumerable.Repeat((byte)0xb2, BlobKeyDeriver.BlobSaltLength).ToArray(), writer, 8, elsewhere);

        Assert.IsFalse(source.SequenceEqual(elsewhere), "two blobs must never derive one key");

        var plaintext = "segment plaintext"u8.ToArray();
        var nonce = new byte[RecordNonce.AesGcmLength];
        RecordNonce.Write(3, nonce);
        var aad = new byte[RecordAad.Length];
        RecordAad.Write(RepoA, FormatLimits.FormatVersion, ObjectType.SegmentRecord, SomeId, 3, aad);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[RecordCipher.TagLength];
        RecordCipher.Seal(source, nonce, aad, plaintext, ciphertext, tag);

        // Byte-identical relocation: same ordinal, same AAD, same repository —
        // only the container changed, and the record is already unreadable.
        Assert.IsFalse(
            RecordCipher.TryOpen(elsewhere, nonce, aad, ciphertext, tag, new byte[ciphertext.Length]),
            "a record must not open under another blob's key context");

        var restored = new byte[plaintext.Length];
        Assert.IsTrue(RecordCipher.TryOpen(source, nonce, aad, ciphertext, tag, restored));
        SequenceAssert.AreEqual(plaintext, restored);
    }

    [TestMethod]
    public void RecordCipher_CiphertextIsTampered_FailsAndEmitsNoPlaintext()
    {
        var plaintext = "segment plaintext"u8.ToArray();
        var (ciphertext, tag) = SealAt(0, RepoA, plaintext);
        ciphertext[0] ^= 0x01;

        var destination = new byte[plaintext.Length];
        destination.AsSpan().Fill(0xEE);

        Assert.IsFalse(OpenAt(0, RepoA, ciphertext, tag, destination));
        foreach (var value in destination)
        {
            Assert.AreEqual(0, value);
        }
    }

    [TestMethod]
    public void RecordCipher_V3_MovedToADifferentBlob_OpensBecauseTheKeyIsTheObjects()
    {
        // The positive twin of the format-2 negative above, for format 3
        // ([ADR-0052](../../../docs/adr/0052-relocatable-records-format-v3.md)
        // Amendment 1): the key derives from the class key and the record's
        // own type and identifier, the nonce travels in the record's prefix,
        // and the AAD names no position — so a reader in any blob, holding
        // the class key and the record's header, opens the same bytes. Two
        // envelopes are modelled below and neither enters the derivation.
        var classKey = Enumerable.Range(0, 32).Select(value => (byte)(value ^ 0x5a)).ToArray();
        var writer = WriterId.FromBytes(Enumerable.Repeat((byte)0x11, WriterId.Size).ToArray());

        // The two containers' own keys still differ, as in format 2 — they
        // seal the footers, and nothing else now.
        var sourceBlob = new byte[BlobKeyDeriver.BlobKeyLength];
        BlobKeyDeriver.Derive(classKey, Enumerable.Repeat((byte)0xa1, BlobKeyDeriver.BlobSaltLength).ToArray(), writer, 7, sourceBlob);
        var elsewhereBlob = new byte[BlobKeyDeriver.BlobKeyLength];
        BlobKeyDeriver.Derive(classKey, Enumerable.Repeat((byte)0xb2, BlobKeyDeriver.BlobSaltLength).ToArray(), writer, 8, elsewhereBlob);
        Assert.IsFalse(sourceBlob.SequenceEqual(elsewhereBlob));

        var writerKey = new byte[RecordKeyDeriver.RecordKeyLength];
        RecordKeyDeriver.Derive(classKey, ObjectType.SegmentRecord, SomeId, writerKey);
        var nonce = new byte[RecordNonce.AesGcmLength];
        RecordNonce.DrawRandom(nonce);
        var aad = new byte[RecordAad.RelocatableLength];
        RecordAad.WriteRelocatable(RepoA, FormatVersions.RelocatableRecords, ObjectType.SegmentRecord, SomeId, aad);

        var plaintext = "segment plaintext"u8.ToArray();
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[RecordCipher.TagLength];
        RecordCipher.Seal(writerKey, nonce, aad, plaintext, ciphertext, tag);

        // A reader in the other blob, at another ordinal, re-derives from the
        // header it has and the class key it holds — nothing else.
        var readerKey = new byte[RecordKeyDeriver.RecordKeyLength];
        RecordKeyDeriver.Derive(classKey, ObjectType.SegmentRecord, SomeId, readerKey);
        var readerAad = new byte[RecordAad.RelocatableLength];
        RecordAad.WriteRelocatable(RepoA, FormatVersions.RelocatableRecords, ObjectType.SegmentRecord, SomeId, readerAad);
        var restored = new byte[plaintext.Length];
        Assert.IsTrue(
            RecordCipher.TryOpen(readerKey, nonce, readerAad, ciphertext, tag, restored),
            "a format-3 record must open wherever its bytes are copied");
        SequenceAssert.AreEqual(plaintext, restored);

        // And what the move must still refuse: another repository's AAD, and
        // another object's key — the two things the record does bind.
        var elsewhereAad = new byte[RecordAad.RelocatableLength];
        RecordAad.WriteRelocatable(RepoB, FormatVersions.RelocatableRecords, ObjectType.SegmentRecord, SomeId, elsewhereAad);
        Assert.IsFalse(RecordCipher.TryOpen(readerKey, nonce, elsewhereAad, ciphertext, tag, new byte[plaintext.Length]));
        var otherKey = new byte[RecordKeyDeriver.RecordKeyLength];
        RecordKeyDeriver.Derive(classKey, ObjectType.SegmentRecord, ObjectId.FromBytes(Enumerable.Repeat((byte)1, 32).ToArray()), otherKey);
        Assert.IsFalse(RecordCipher.TryOpen(otherKey, nonce, readerAad, ciphertext, tag, new byte[plaintext.Length]));
    }
}
