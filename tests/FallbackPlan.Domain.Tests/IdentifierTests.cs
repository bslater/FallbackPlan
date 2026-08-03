using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Exercises the identifier value types' construction rules, equality, and
/// rendering (specification 02).
/// </summary>
public sealed class IdentifierTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(32)]
    public void Sixteen_byte_identifiers_reject_every_other_length(int length)
    {
        var bytes = new byte[length];

        Assert.Throws<ArgumentException>(() => WriterId.FromBytes(bytes));
        Assert.Throws<ArgumentException>(() => RepositoryId.FromBytes(bytes));
        Assert.Throws<ArgumentException>(() => KeyId.FromBytes(bytes));
        Assert.Throws<ArgumentException>(() => BlobId.FromBytes(bytes));
        Assert.Throws<ArgumentException>(() => StoreBlobKey.FromBytes(bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void Thirty_two_byte_identifiers_reject_every_other_length(int length)
    {
        var bytes = new byte[length];

        Assert.Throws<ArgumentException>(() => ContentId.FromBytes(bytes));
        Assert.Throws<ArgumentException>(() => ObjectId.FromBytes(bytes));
    }

    [Fact]
    public void Round_trip_preserves_the_exact_bytes()
    {
        var sixteen = Enumerable.Range(0xa0, 16).Select(value => (byte)value).ToArray();
        var thirtyTwo = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

        Assert.Equal(sixteen, WriterId.FromBytes(sixteen).ToArray());
        Assert.Equal(sixteen, RepositoryId.FromBytes(sixteen).ToArray());
        Assert.Equal(sixteen, KeyId.FromBytes(sixteen).ToArray());
        Assert.Equal(sixteen, BlobId.FromBytes(sixteen).ToArray());
        Assert.Equal(sixteen, StoreBlobKey.FromBytes(sixteen).ToArray());
        Assert.Equal(thirtyTwo, ContentId.FromBytes(thirtyTwo).ToArray());
        Assert.Equal(thirtyTwo, ObjectId.FromBytes(thirtyTwo).ToArray());
    }

    [Fact]
    public void Equality_is_by_value()
    {
        var bytes = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        var other = (byte[])bytes.Clone();
        other[15] ^= 0x01;

        Assert.Equal(WriterId.FromBytes(bytes), WriterId.FromBytes(bytes));
        Assert.NotEqual(WriterId.FromBytes(bytes), WriterId.FromBytes(other));
        Assert.True(WriterId.FromBytes(bytes) == WriterId.FromBytes(bytes));
        Assert.True(WriterId.FromBytes(bytes) != WriterId.FromBytes(other));
    }

    [Fact]
    public void Content_identifier_rendering_is_redacted()
    {
        var contentId = ContentId.FromBytes(new byte[ContentId.Size]);

        Assert.DoesNotContain("00", contentId.ToString(), StringComparison.Ordinal);
        Assert.Contains("redacted", contentId.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Rendered_identifiers_produce_the_specified_character_counts()
    {
        // Specification 02 §5: 32 bytes → 52 characters, 16 bytes → 26.
        Assert.Equal(52, ObjectId.FromBytes(new byte[32]).ToBase32().Length);
        Assert.Equal(26, StoreBlobKey.FromBytes(new byte[16]).ToBase32().Length);
        Assert.Equal(26, RepositoryId.FromBytes(new byte[16]).ToBase32().Length);
    }
}
