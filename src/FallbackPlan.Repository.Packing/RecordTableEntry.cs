using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// One entry of the recovery footer's record table (specification 05 §3.1,
/// CBOR keys 1–8): everything needed to locate and read one record from the
/// blob and keys alone (FR-MAN-007).
/// </summary>
public readonly record struct RecordTableEntry(
    ObjectId ObjectId,
    uint Ordinal,
    ulong PhysicalOffset,
    uint StoredLength,
    ulong LogicalLength,
    ushort CompressionProfileValue,
    ushort EncryptionProfileValue,
    ObjectType ObjectType);
