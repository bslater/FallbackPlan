using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Records;

namespace FallbackPlan.Repository.Tests.Crypto;

/// <summary>
/// The B5 acceptance criteria, verbatim: a record moved between ordinals or
/// repositories fails authentication (specification 04 §4; NFR-SEC-003). The
/// AAD binds repository, format version, object type, object identifier, and
/// ordinal — each variation must break the tag.
/// </summary>
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

    [Fact]
    public void A_record_opens_at_its_own_ordinal_and_repository()
    {
        var plaintext = "segment plaintext"u8.ToArray();
        var (ciphertext, tag) = SealAt(47, RepoA, plaintext);
        var restored = new byte[plaintext.Length];

        Assert.True(OpenAt(47, RepoA, ciphertext, tag, restored));
        Assert.Equal(plaintext, restored);
    }

    [Fact]
    public void A_record_moved_to_a_different_ordinal_fails_authentication()
    {
        var (ciphertext, tag) = SealAt(47, RepoA, "segment plaintext"u8.ToArray());

        Assert.False(OpenAt(48, RepoA, ciphertext, tag, new byte[ciphertext.Length]));
    }

    [Fact]
    public void A_record_moved_to_a_different_repository_fails_authentication()
    {
        var (ciphertext, tag) = SealAt(47, RepoA, "segment plaintext"u8.ToArray());

        Assert.False(OpenAt(47, RepoB, ciphertext, tag, new byte[ciphertext.Length]));
    }

    [Fact]
    public void Tampered_ciphertext_fails_and_no_plaintext_is_emitted()
    {
        var plaintext = "segment plaintext"u8.ToArray();
        var (ciphertext, tag) = SealAt(0, RepoA, plaintext);
        ciphertext[0] ^= 0x01;

        var destination = new byte[plaintext.Length];
        destination.AsSpan().Fill(0xEE);

        Assert.False(OpenAt(0, RepoA, ciphertext, tag, destination));
        Assert.All(destination, value => Assert.Equal(0, value));
    }
}
