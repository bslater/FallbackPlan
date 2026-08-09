using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Packing;

namespace FallbackPlan.Repository.Tests.Packing;

/// <summary>
/// Exercises the blob store-key path builder (specification 01 §2, 02 §4.3;
/// FR-SNP-006): shard = the first four characters of the base32 rendering,
/// and the raw blob identifier never appears.
/// </summary>
[TestClass]
public sealed class BlobStoreKeysTests
{
    // The committed conformance constant: store_blob_key_base32 for the
    // identifiers.json blob_identifier group.
    private static readonly StoreBlobKey Key =
        StoreBlobKey.FromBytes(Convert.FromHexString("6fc6ed5b0ac59f95a4a936c8fce01488"));

    [TestMethod]
    public void The_data_path_shape_matches_the_specification()
    {
        var objectKey = BlobStoreKeys.ForBlob(BlobClass.Data, Key);

        Assert.AreEqual("blobs/data/n7do/n7do2wykywpzljfjg3epzyaura", objectKey.Value);
    }

    [TestMethod]
    public void The_metadata_class_maps_to_meta()
    {
        Assert.StartsWith("blobs/meta/", BlobStoreKeys.ForBlob(BlobClass.Metadata, Key).Value, StringComparison.Ordinal);
    }

    [TestMethod]
    public void The_shard_is_the_first_four_rendered_characters()
    {
        var objectKey = BlobStoreKeys.ForBlob(BlobClass.Data, Key);
        var components = objectKey.Components;

        Assert.AreEqual(4, components[2].Length);
        Assert.StartsWith(components[2], components[3], StringComparison.Ordinal);
        Assert.AreEqual(26, components[3].Length);
    }
}
