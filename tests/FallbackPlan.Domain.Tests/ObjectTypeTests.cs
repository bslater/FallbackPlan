namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Exercises object-type validation (specification 02 §3.1), including the
/// reserved store-key domain separator <c>0x07</c> that must never be accepted
/// as a record's object type.
/// </summary>
[TestClass]
public sealed class ObjectTypeTests
{
    [TestMethod]
    [DataRow((byte)0x01, ObjectType.SegmentRecord)]
    [DataRow((byte)0x02, ObjectType.FileVersionManifest)]
    [DataRow((byte)0x03, ObjectType.TreeManifest)]
    [DataRow((byte)0x04, ObjectType.SnapshotManifest)]
    [DataRow((byte)0x05, ObjectType.PolicyManifest)]
    [DataRow((byte)0x06, ObjectType.ErrorManifest)]
    [DataRow((byte)0x08, ObjectType.IndexDelta)]
    [DataRow((byte)0x09, ObjectType.IndexCheckpoint)]
    [DataRow((byte)0x0A, ObjectType.JournalRecord)]
    [DataRow((byte)0x0B, ObjectType.PlacementHint)]
    [DataRow((byte)0x0C, ObjectType.SourceIdentityHint)]
    [DataRow((byte)0x0D, ObjectType.Lease)]
    [DataRow((byte)0x0E, ObjectType.Tombstone)]
    [DataRow((byte)0x0F, ObjectType.AuditPeriodRecord)]
    public void TryFromValue_WhenTheValueIsAssigned_ShouldResolveToItsType(byte value, ObjectType expected)
    {
        Assert.IsTrue(ObjectTypes.IsValid(value));
        Assert.IsTrue(ObjectTypes.TryFromValue(value, out var objectType));
        Assert.AreEqual(expected, objectType);
    }

    [TestMethod]
    [DataRow((byte)0x00)]
    [DataRow((byte)0x07)] // reserved: store-blob-key domain separator, never a record type
    [DataRow((byte)0x10)]
    [DataRow((byte)0xFF)]
    public void TryFromValue_WhenTheValueIsUnassignedOrReserved_ShouldReturnFalse(byte value)
    {
        Assert.IsFalse(ObjectTypes.IsValid(value));
        Assert.IsFalse(ObjectTypes.TryFromValue(value, out _));
    }
}
