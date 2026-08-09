using System.Text.Json;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Drives <c>keys.json</c> through the real <see cref="KeyHierarchy"/> and
/// <see cref="BlobKeyDeriver"/>: every derived key, both generations, and the
/// separation checks (specification 03 §4–§5; FR-ARCH-008, NFR-SEC-008). This
/// closes the chain the identifier conformance suite opens — the content-ID
/// and key-ID keys it consumes are proven here to derive from the master key.
/// </summary>
[TestClass]
public sealed class KeyHierarchyConformanceTests
{
    private static JsonDocument Vectors { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "keys.json")));

    private static KeyHierarchy CreateHierarchy() =>
        new(Convert.FromHexString(Vectors.RootElement.GetProperty("inputs").GetProperty("master_key").GetString()!));

    private static string Derived(string name) =>
        Vectors.RootElement.GetProperty("derived").GetProperty(name).GetString()!;

    [TestMethod]
    public void Repository_scoped_keys_match_the_committed_vectors()
    {
        using var hierarchy = CreateHierarchy();

        Assert.AreEqual(Derived("content_id_key"), Convert.ToHexStringLower(hierarchy.DeriveContentIdKey()));
        Assert.AreEqual(Derived("key_id_key"), Convert.ToHexStringLower(hierarchy.DeriveKeyIdKey()));
    }

    [TestMethod]
    public void Generational_keys_match_the_committed_vectors()
    {
        using var hierarchy = CreateHierarchy();

        Assert.AreEqual(Derived("data_key_generation_0"), Convert.ToHexStringLower(hierarchy.DeriveDataKey(KeyGeneration.Zero)));
        Assert.AreEqual(Derived("data_key_generation_1"), Convert.ToHexStringLower(hierarchy.DeriveDataKey(new KeyGeneration(1))));
        Assert.AreEqual(Derived("metadata_key_generation_0"), Convert.ToHexStringLower(hierarchy.DeriveMetadataKey(KeyGeneration.Zero)));
        Assert.AreEqual(Derived("signing_key_generation_0"), Convert.ToHexStringLower(hierarchy.DeriveSigningKeySeed(KeyGeneration.Zero)));
    }

    [TestMethod]
    public void Blob_key_and_separation_checks_match_the_committed_vectors()
    {
        var inputs = Vectors.RootElement.GetProperty("inputs");
        var writer = WriterId.FromBytes(Convert.FromHexString(inputs.GetProperty("writer_id").GetString()!));
        var blobSalt = Convert.FromHexString(inputs.GetProperty("blob_salt").GetString()!);
        var counter = inputs.GetProperty("blob_counter").GetUInt64();

        using var hierarchy = CreateHierarchy();
        var dataKey = hierarchy.DeriveDataKey(KeyGeneration.Zero);

        var blobKey = new byte[BlobKeyDeriver.BlobKeyLength];
        BlobKeyDeriver.Derive(dataKey, blobSalt, writer, counter, blobKey);
        Assert.AreEqual(Derived("blob_key"), Convert.ToHexStringLower(blobKey));

        var separation = Vectors.RootElement.GetProperty("separation_checks");

        var otherWriter = WriterId.FromBytes(Convert.FromHexString("b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0"));
        var otherWriterKey = new byte[BlobKeyDeriver.BlobKeyLength];
        BlobKeyDeriver.Derive(dataKey, blobSalt, otherWriter, counter, otherWriterKey);
        Assert.AreEqual(
            separation.GetProperty("blob_key_other_writer").GetString(),
            Convert.ToHexStringLower(otherWriterKey));

        var otherCounterKey = new byte[BlobKeyDeriver.BlobKeyLength];
        BlobKeyDeriver.Derive(dataKey, blobSalt, writer, counter + 1, otherCounterKey);
        Assert.AreEqual(
            separation.GetProperty("blob_key_other_counter").GetString(),
            Convert.ToHexStringLower(otherCounterKey));

        Assert.AreNotEqual(Convert.ToHexStringLower(blobKey), Convert.ToHexStringLower(otherWriterKey));
        Assert.AreNotEqual(Convert.ToHexStringLower(blobKey), Convert.ToHexStringLower(otherCounterKey));
    }
}
