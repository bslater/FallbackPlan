using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Repository.Packing.Resources;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// Builds the store path for a sealed blob (specification 01 §2; FR-SNP-006):
/// <c>blobs/&lt;data|meta&gt;/&lt;shard&gt;/&lt;store-blob-key&gt;</c>, where the shard is
/// the first four characters of the base32-rendered store blob key. The raw
/// blob identifier never reaches a store key — only the keyed rendering does
/// (specification 02 §4.3).
/// </summary>
public static class BlobStoreKeys
{
    /// <summary>Returns the store key for a blob of <paramref name="blobClass"/>.</summary>
    public static ObjectKey ForBlob(BlobClass blobClass, StoreBlobKey storeBlobKey)
    {
        var rendered = storeBlobKey.ToBase32();
        var classSegment = blobClass switch
        {
            BlobClass.Data => "data",
            BlobClass.Metadata => "meta",
            _ => throw new ArgumentException(Strings.FormatBlobEnvelope_BlobClassXNotDefined((ushort)blobClass), nameof(blobClass)),
        };

        return ObjectKey.Parse($"blobs/{classSegment}/{rendered[..4]}/{rendered}");
    }
}
