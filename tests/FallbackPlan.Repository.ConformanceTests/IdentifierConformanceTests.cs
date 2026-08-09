using System.Text.Json;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Drives every group of <c>identifiers.json</c> through the real engine
/// types — <see cref="ContentHasher"/>, <see cref="ObjectIdDeriver"/>,
/// <see cref="StoreBlobKeyDeriver"/>, and <see cref="BlobId"/> formation —
/// which is Wave A3's acceptance criterion: matches the vectors exactly
/// (specification 02; FR-ARCH-003, NFR-SEC-004).
/// </summary>
/// <remarks>
/// The derivation keys come from the pinned <c>derived</c> values in
/// <c>keys.json</c>; the key-hierarchy conformance suite separately proves
/// those derive from the master key, closing the chain.
/// </remarks>
[TestClass]
public sealed class IdentifierConformanceTests
{
    private static JsonDocument LoadVectors(string fileName) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", fileName)));

    private static byte[] ContentIdKey()
    {
        using var keys = LoadVectors("keys.json");

        return Convert.FromHexString(keys.RootElement.GetProperty("derived").GetProperty("content_id_key").GetString()!);
    }

    private static byte[] CasePlaintext(string name) => name switch
    {
        "empty" => [],
        "single_byte" => [0x00],
        "ascii" => "hello world"u8.ToArray(),
        "one_mib_cycle" => [.. Enumerable.Repeat(Enumerable.Range(0, 256).Select(value => (byte)value), 4096).SelectMany(cycle => cycle)],
        "one_mib_zeros" => new byte[1_048_576],
        _ => throw new InvalidOperationException($"Unknown vector case '{name}' — the fixture and this test have diverged."),
    };

    [TestMethod]
    public void Content_and_object_identifiers_match_every_committed_case()
    {
        using var vectors = LoadVectors("identifiers.json");
        using var deriver = new ObjectIdDeriver(ContentIdKey());

        foreach (var vectorCase in vectors.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = vectorCase.GetProperty("name").GetString()!;
            var plaintext = CasePlaintext(name);

            Assert.AreEqual(vectorCase.GetProperty("plaintext_length").GetInt64(), plaintext.LongLength);

            // The one-MiB cases exercise the incremental path in bounded
            // slices; the small cases exercise the one-shot path. Both agree.
            using var hasher = new ContentHasher();
            foreach (var chunk in plaintext.Chunk(4096))
            {
                hasher.Append(chunk);
            }

            var contentId = hasher.FinishAndReset();

            Assert.AreEqual(ContentHasher.Hash(plaintext), contentId);
            SequenceAssert.AreEqual(
                vectorCase.GetProperty("content_id").GetString(),
                Convert.ToHexStringLower(contentId.ToArray()));

            var objectId = deriver.Derive(ObjectType.SegmentRecord, contentId);

            SequenceAssert.AreEqual(
                vectorCase.GetProperty("object_id_segment").GetString(),
                Convert.ToHexStringLower(objectId.ToArray()));
            Assert.AreEqual(vectorCase.GetProperty("object_id_base32").GetString(), objectId.ToBase32());
        }
    }

    [TestMethod]
    public void Object_identifiers_separate_by_object_type()
    {
        using var vectors = LoadVectors("identifiers.json");
        using var deriver = new ObjectIdDeriver(ContentIdKey());

        var separation = vectors.RootElement.GetProperty("object_type_separation");
        var plaintext = separation.GetProperty("plaintext").GetString()!;
        var contentId = ContentHasher.Hash(System.Text.Encoding.ASCII.GetBytes(plaintext));
        var expected = separation.GetProperty("object_ids");

        SequenceAssert.AreEqual(
            expected.GetProperty("segment").GetString(),
            Convert.ToHexStringLower(deriver.Derive(ObjectType.SegmentRecord, contentId).ToArray()));
        SequenceAssert.AreEqual(
            expected.GetProperty("file_version").GetString(),
            Convert.ToHexStringLower(deriver.Derive(ObjectType.FileVersionManifest, contentId).ToArray()));
        SequenceAssert.AreEqual(
            expected.GetProperty("tree").GetString(),
            Convert.ToHexStringLower(deriver.Derive(ObjectType.TreeManifest, contentId).ToArray()));
    }

    [TestMethod]
    public void Blob_identifier_and_store_key_match_the_committed_vector()
    {
        using var vectors = LoadVectors("identifiers.json");
        using var keys = LoadVectors("keys.json");

        var group = vectors.RootElement.GetProperty("blob_identifier");
        var writer = WriterId.FromBytes(Convert.FromHexString(group.GetProperty("writer_id").GetString()!));
        var counter = group.GetProperty("blob_counter").GetUInt64();

        var blobId = BlobId.FromWriterCounter(writer, counter);

        SequenceAssert.AreEqual(group.GetProperty("blob_id").GetString(), Convert.ToHexStringLower(blobId.ToArray()));

        var keyIdKey = Convert.FromHexString(
            keys.RootElement.GetProperty("derived").GetProperty("key_id_key").GetString()!);
        using var deriver = new StoreBlobKeyDeriver(keyIdKey);

        var storeKey = deriver.Derive(blobId);

        SequenceAssert.AreEqual(group.GetProperty("store_blob_key").GetString(), Convert.ToHexStringLower(storeKey.ToArray()));
        Assert.AreEqual(group.GetProperty("store_blob_key_base32").GetString(), storeKey.ToBase32());
    }
}
