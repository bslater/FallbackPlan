using System.Text.Json;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Checks that the committed conformance vectors are present, parse, and carry
/// the fields an implementation will consume.
///
/// These run before any engine code exists, and are useful precisely then: a
/// vector file that is malformed, truncated, or missing a field is a defect that
/// would otherwise surface as a confusing test failure much later, when it would
/// be blamed on the implementation rather than on the fixture.
///
/// As the engine is built, assertions comparing computed values against these
/// vectors join this project. See specifications/repository-format/conformance/.
/// </summary>
[TestClass]
public sealed class VectorFileTests
{
    /// <summary>
    /// Every committed vector file, with the provenance its content actually
    /// has. True: computed by the stdlib-only generator from published
    /// algorithms — reproducible by anyone in any language. False: pinned
    /// constants the generator cannot compute (AES-GCM, Argon2id), whose
    /// per-file provenance fields say where the values really came from.
    ///
    /// An earlier revision asserted true for every file, including one whose
    /// values the generator could not possibly have derived. A provenance
    /// check that enforces an overstatement is worse than no check, so the
    /// expectation is now per-file.
    /// </summary>
    private static readonly Dictionary<string, bool> ExpectedFiles = new()
    {
        ["keys.json"] = true,
        ["identifiers.json"] = true,
        ["records.json"] = true,
        ["segmentation.json"] = true,
        ["compression.json"] = true,
        ["aes-gcm.json"] = false,
        ["argon2id.json"] = false,
        ["ed25519.json"] = true,
        ["path-rules.json"] = true,
        ["recovery-kit.json"] = true,
    };

    private static string VectorDirectory =>
        Path.Combine(AppContext.BaseDirectory, "vectors");

    private static JsonDocument Load(string name)
    {
        var path = Path.Combine(VectorDirectory, name);
        Assert.IsTrue(File.Exists(path), $"Vector file not found: {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [TestMethod]
    public void VectorFiles_EveryExpectedFile_IsPresentAndParses()
    {
        foreach (var name in ExpectedFiles.Keys)
        {
            using var document = Load(name);
            Assert.AreEqual(JsonValueKind.Object, document.RootElement.ValueKind);
            Assert.IsTrue(
                document.RootElement.TryGetProperty("description", out _),
                $"{name} has no description");
        }
    }

    /// <summary>
    /// Every group must declare whether it was derived independently of the
    /// reference implementation — and declare it truthfully. A suite that
    /// quietly mixes self-certifying vectors with independent ones overstates
    /// its own authority, which is worse than having fewer vectors.
    ///
    /// The expected value is per-file: a group of pinned constants claiming
    /// independent derivation is the overstatement, so flipping a file's flag
    /// requires flipping the expectation here too — deliberately, in the same
    /// change, with the reason in view.
    /// </summary>
    [TestMethod]
    public void VectorGroups_EveryGroup_DeclaresItsProvenanceTruthfully()
    {
        foreach (var (name, expected) in ExpectedFiles)
        {
            using var document = Load(name);
            Assert.IsTrue(
                document.RootElement.TryGetProperty("independently_derived", out var derived),
                $"{name} does not declare independently_derived");
            Assert.IsTrue(
                derived.ValueKind is JsonValueKind.True or JsonValueKind.False,
                $"{name}: independently_derived must be a boolean");
            Assert.AreEqual(expected, derived.GetBoolean());
        }
    }

    [TestMethod]
    public void KeyDerivationVectors_DifferentWriters_ProveSeparation()
    {
        using var document = Load("keys.json");
        var derived = document.RootElement.GetProperty("derived");
        var checks = document.RootElement.GetProperty("separation_checks");

        var blobKey = derived.GetProperty("blob_key").GetString();
        var otherWriter = checks.GetProperty("blob_key_other_writer").GetString();
        var otherCounter = checks.GetProperty("blob_key_other_counter").GetString();

        Assert.AreEqual(64, blobKey!.Length);

        // The same blob_salt with a different writer or counter must produce a
        // different key. This is what makes key separation survive a cloned VM
        // replaying CSPRNG state, and a vector file that failed to demonstrate
        // it would be silently useless.
        Assert.AreNotEqual(blobKey, otherWriter);
        Assert.AreNotEqual(blobKey, otherCounter);
        Assert.AreNotEqual(otherWriter, otherCounter);
    }

    [TestMethod]
    public void AssociatedData_EveryCommittedCase_IsFiftyFiveBytes()
    {
        using var document = Load("records.json");
        foreach (var testCase in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            Assert.AreEqual(55, testCase.GetProperty("aad_length").GetInt32());
            Assert.AreEqual(110, testCase.GetProperty("aad").GetString()!.Length);
        }
    }

    [TestMethod]
    public void ObjectIdentifierVectors_TheSameContentUnderDifferentTypes_Differ()
    {
        using var document = Load("identifiers.json");
        var ids = document.RootElement
            .GetProperty("object_type_separation")
            .GetProperty("object_ids");

        var values = ids.EnumerateObject().Select(p => p.Value.GetString()).ToList();
        Assert.AreEqual(values.Count, values.Distinct().Count());
    }

    [TestMethod]
    public void FixedSegmentationVectors_EveryCase_AreContiguousAndComplete()
    {
        using var document = Load("segmentation.json");
        foreach (var testCase in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var fileLength = testCase.GetProperty("file_length").GetInt64();
            var segmentSize = testCase.GetProperty("segment_size").GetInt64();
            var segments = testCase.GetProperty("segments").EnumerateArray().ToList();

            // segment_count is what an implementation will read; the segments
            // array is what it will verify against. A generator bug that
            // desynchronised them would otherwise pass every check here.
            Assert.AreEqual(testCase.GetProperty("segment_count").GetInt32(), segments.Count);

            long covered = 0;
            long expectedOffset = 0;
            for (var i = 0; i < segments.Count; i++)
            {
                var offset = segments[i].GetProperty("offset").GetInt64();
                var length = segments[i].GetProperty("length").GetInt64();

                Assert.AreEqual(expectedOffset, offset);
                if (i < segments.Count - 1)
                {
                    Assert.AreEqual(segmentSize, length);   // only the last may be short
                }

                covered += length;
                expectedOffset += length;
            }

            Assert.AreEqual(fileLength, covered);
        }
    }

    [TestMethod]
    public void CompressionThresholdVectors_EveryCase_AreSelfConsistent()
    {
        using var document = Load("compression.json");
        var threshold = document.RootElement.GetProperty("threshold_permille").GetInt32();

        foreach (var testCase in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var logical = testCase.GetProperty("logical_length").GetInt64();
            var compressed = testCase.GetProperty("compressed_length").GetInt64();
            var expected = testCase.GetProperty("expected_profile").GetString();

            var shouldCompress = compressed * 1000 <= logical * (1000 - threshold);
            Assert.AreEqual(shouldCompress ? "zstd-v1" : "none", expected);
        }
    }
}
