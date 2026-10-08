using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>
/// Where the tombstone for an object lives (specification 11 §3):
/// <c>tombstones/&lt;type&gt;/&lt;base32 identifier&gt;</c>, the type as two
/// lowercase hex digits.
/// </summary>
/// <remarks>
/// Two sides read the same key and must spell it the same way. A collector
/// writes and sweeps tombstones, and a backup asks whether a blob it would
/// build on carries one (ADR-0009 Amendment 8).
/// </remarks>
public static class TombstoneKeys
{
    /// <summary>The tombstone key for an object of <paramref name="objectTypeCode"/>.</summary>
    /// <param name="objectTypeCode">The object's type, or <see cref="Tombstone.BlobTypeCode"/> for a blob.</param>
    /// <param name="objectId">The object's identifier.</param>
    public static ObjectKey For(byte objectTypeCode, ReadOnlySpan<byte> objectId) =>
        ObjectKey.Parse($"tombstones/{objectTypeCode:x2}/{Base32.Encode(objectId.ToArray())}");

    /// <summary>The tombstone key for a blob.</summary>
    /// <param name="blobId">The blob's identifier.</param>
    public static ObjectKey ForBlob(BlobId blobId) => For(Tombstone.BlobTypeCode, blobId.ToArray());
}
