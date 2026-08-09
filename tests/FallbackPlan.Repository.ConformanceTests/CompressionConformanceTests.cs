using System.Text.Json;
using FallbackPlan.Repository.Format.Compression;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Drives every <c>compression.json</c> case through the real threshold
/// decision (specification 10 §3; FR-ARCH-005). The vectors deliberately pin
/// the decision, not compressed bytes — zstd output is not reproducible
/// across library versions.
/// </summary>
[TestClass]
public sealed class CompressionConformanceTests
{
    [TestMethod]
    public void CompressionStorageDecision_EveryCommittedCase_Matches()
    {
        using var vectors = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "compression.json")));

        var thresholdPermille = vectors.RootElement.GetProperty("threshold_permille").GetUInt16();

        foreach (var vectorCase in vectors.RootElement.GetProperty("cases").EnumerateArray())
        {
            var storeCompressed = CompressionThreshold.StoreCompressed(
                vectorCase.GetProperty("logical_length").GetUInt64(),
                vectorCase.GetProperty("compressed_length").GetUInt64(),
                thresholdPermille);

            var expectedProfile = vectorCase.GetProperty("expected_profile").GetString();

            Assert.AreEqual(
                expectedProfile,
                storeCompressed ? "zstd-v1" : "none");
        }
    }
}
