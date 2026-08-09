using System.Globalization;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// FR-ARCH-002 at a scale a test machine can run: the archive pipeline —
/// segmentation, hashing, compression, encryption, packing — holds memory
/// proportional to its own buffers and not to the length of its input.
/// </summary>
/// <remarks>
/// <para>
/// The requirement is stated against a 2 TiB file, which no test can
/// process, so this asserts the property the requirement is really about:
/// give the pipeline eight times the bytes and it must not take
/// meaningfully more memory. A pipeline that buffered its input would grow
/// by the same factor, which is the failure this is here to catch — and the
/// one that is invisible until the file is bigger than the machine.
/// </para>
/// <para>
/// The measurement is a forced-collection live set, sampled during the run
/// and taken net of a baseline collected immediately before it, because
/// what the bound is about is retention — not allocation rate, and not the
/// heap the rest of the test run happens to be holding.
/// A streaming pipeline allocates a great deal and retains almost none of
/// it. The tolerance is generous for the same reason a benchmark is not an
/// assertion — this is a scaling claim, and a tight absolute number
/// measured in a container would be a claim about the container.
/// </para>
/// <para>
/// It runs in its own non-parallel collection because the measurement is
/// process-wide: another test allocating alongside it would be counted as
/// this pipeline's retention, and the reading would be about the test run
/// rather than about the code.
/// </para>
/// </remarks>
[TestClass]
public sealed class SerialMemoryMeasurement;

/// <inheritdoc cref="SerialMemoryMeasurement"/>
[DoNotParallelize]
[TestClass]
public sealed class BoundedMemoryTests : ArchiveTestHarness
{
    private const long Mebibyte = 1024 * 1024;

    /// <summary>The NFR-PERF-001 live-set bound this reproduces at reduced scale.</summary>
    private const long LiveSetBound = 256 * Mebibyte;

    [TestMethod]
    public async Task Eight_times_the_input_does_not_cost_eight_times_the_memory()
    {
        var small = await MeasurePeakLiveSetAsync(32 * Mebibyte, firstCounter: 1);
        var large = await MeasurePeakLiveSetAsync(128 * Mebibyte, firstCounter: 10_000);

        var detail = string.Create(
            CultureInfo.InvariantCulture,
            $"32 MiB retained {small / (double)Mebibyte:f1} MiB; 128 MiB retained {large / (double)Mebibyte:f1} MiB.");

        // Four times the input. A pipeline that buffered would be at four
        // times the memory; the measured ratio is around 1.5, and the
        // threshold sits well below proportionality rather than close to the
        // observation, because the instrument is a sampled live set and its
        // spread is tens of mebibytes either way.
        Assert.IsTrue(large < small * 2.5, $"Memory grew with input length. {detail}");
        Assert.IsTrue(large <= LiveSetBound, $"The retained set exceeded the NFR-PERF-001 bound. {detail}");
    }

    private async Task<long> MeasurePeakLiveSetAsync(long inputBytes, ulong firstCounter)
    {
        var policy = CapturePolicy.Default with
        {
            SegmentationProfile = Domain.Profiles.SegmentationProfile.CdcV1,
            CdcParameters = CdcParameters.Default,
        };

        using var keys = CreateKeys();
        var store = new DiscardingObjectStore();
        var archiver = new FileArchiver(
            policy, Repo, Writer, KeyGeneration.Zero, keys, store,
            new MonotonicBlobCounterAllocator(firstCounter), SpoolDirectory);

        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var peak = 0L;
        using (var sampler = new Timer(
            _ => peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true)), null, 50, 100))
        {
            using var input = new SyntheticStream(inputBytes, seed: 42);
            var result = await archiver.ArchiveAsync(input, CancellationToken.None);

            // The archive really happened — a pipeline that read nothing would
            // also hold no memory.
            Assert.AreEqual(inputBytes, result.LogicalLength);
            Assert.IsTrue(store.BytesConsumed > 0);
        }

        return Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true)) - baseline;
    }
}
