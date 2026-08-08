namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Exercises object-type validation (specification 02 §3.1), including the
/// reserved store-key domain separator <c>0x07</c> that must never be accepted
/// as a record's object type.
/// </summary>
public sealed class ObjectTypeTests
{
    [Theory]
    [InlineData(0x01, ObjectType.SegmentRecord)]
    [InlineData(0x02, ObjectType.FileVersionManifest)]
    [InlineData(0x03, ObjectType.TreeManifest)]
    [InlineData(0x04, ObjectType.SnapshotManifest)]
    [InlineData(0x05, ObjectType.PolicyManifest)]
    [InlineData(0x06, ObjectType.ErrorManifest)]
    [InlineData(0x08, ObjectType.IndexDelta)]
    [InlineData(0x09, ObjectType.IndexCheckpoint)]
    [InlineData(0x0A, ObjectType.JournalRecord)]
    [InlineData(0x0B, ObjectType.PlacementHint)]
    [InlineData(0x0C, ObjectType.SourceIdentityHint)]
    public void Assigned_types_are_valid(byte value, ObjectType expected)
    {
        Assert.True(ObjectTypes.IsValid(value));
        Assert.True(ObjectTypes.TryFromValue(value, out var objectType));
        Assert.Equal(expected, objectType);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x07)] // reserved: store-blob-key domain separator, never a record type
    [InlineData(0x0D)]
    [InlineData(0xFF)]
    public void Unassigned_and_reserved_values_are_refused(byte value)
    {
        Assert.False(ObjectTypes.IsValid(value));
        Assert.False(ObjectTypes.TryFromValue(value, out _));
    }
}
