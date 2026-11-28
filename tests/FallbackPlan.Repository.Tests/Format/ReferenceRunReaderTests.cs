using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Manifests;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// Following a file version's reference runs (ADR-0202 decision 4; FR-ARCH-010):
/// a reader reads every run the manifest lists, in its order, before it treats
/// the version as read, and each run is held to the manifest as it arrives. A
/// run that does not read, or that does not start where the one before ended,
/// is damage to the version, never a shorter file. A manifest listing runs
/// names them among the objects its version needs.
/// </summary>
[TestClass]
public sealed class ReferenceRunReaderTests
{
    private const int Full = ReferenceRunCodec.MaxPieces;

    private static ObjectId Id(int seed)
    {
        var bytes = new byte[32];
        BitConverter.TryWriteBytes(bytes, seed);
        bytes[31] = 0xCD;
        return ObjectId.FromBytes(bytes);
    }

    /// <summary>A run of <paramref name="pieces"/> one-byte segments from <paramref name="start"/>, numbered from <paramref name="first"/>.</summary>
    private static ReferenceRun Run(ulong start, int pieces, int first = 0) => new()
    {
        Start = start,
        Length = (ulong)pieces,
        SegmentReferences = [.. Enumerable.Range(first, pieces).Select(index => new SegmentReference((long)start + index - first, 1, Id(index)))],
    };

    private static FileVersionManifest Head(ulong length, params ObjectId[] runs) => new()
    {
        EntryKind = EntryKind.File,
        Name = "huge.bin"u8.ToArray(),
        NameNormalisation = NameNormalisation.Nfc,
        LogicalLength = length,
        SegmentReferences = [],
        ReferenceRuns = runs,
        WholeFileHash = SHA256.HashData("huge"u8),
        SegmentationProfile = 0x0001,
        Metadata = new EntryMetadata { ModifiedAt = 1_722_600_000_000 },
    };

    /// <summary>A store of encoded runs by object identifier, and the identifiers asked for, in order.</summary>
    private sealed class Runs
    {
        private readonly Dictionary<ObjectId, byte[]> _held = [];

        public List<ObjectId> Asked { get; } = [];

        public ObjectId Hold(int seed, ReferenceRun run)
        {
            var id = Id(1_000_000 + seed);
            _held[id] = ReferenceRunCodec.Encode(run);
            return id;
        }

        public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ObjectId id, CancellationToken cancellationToken)
        {
            Asked.Add(id);
            return ValueTask.FromResult(_held.TryGetValue(id, out var bytes) ? (ReadOnlyMemory<byte>?)bytes : null);
        }
    }

    private static async Task<List<ReferenceRun>> ReadAllAsync(FileVersionManifest manifest, Runs runs)
    {
        var read = new List<ReferenceRun>();
        await foreach (var run in ReferenceRunReader.ReadAsync(manifest, runs.ReadAsync, CancellationToken.None))
        {
            read.Add(run);
        }

        return read;
    }

    [TestMethod]
    public async Task AManifestListingRuns_IsReadRunByRun_InItsOrder()
    {
        var runs = new Runs();
        var first = runs.Hold(0, Run(0, Full));
        var second = runs.Hold(1, Run(Full, Full, first: Full));
        var third = runs.Hold(2, Run(2 * (ulong)Full, 3, first: 2 * Full));

        var read = await ReadAllAsync(Head(2 * (ulong)Full + 3, first, second, third), runs);

        CollectionAssert.AreEqual(new[] { first, second, third }, runs.Asked);
        CollectionAssert.AreEqual(new[] { 0UL, (ulong)Full, 2 * (ulong)Full }, read.Select(run => run.Start).ToArray());
        Assert.AreEqual(Id(2 * Full + 2), read[2].SegmentReferences[^1].ObjectId);
    }

    [TestMethod]
    public async Task AManifestListingNoRuns_ReadsNone_ItsReferencesAreItsOwn()
    {
        var runs = new Runs();
        var manifest = Head(1) with { SegmentReferences = [new SegmentReference(0, 1, Id(7))] };

        Assert.IsEmpty(await ReadAllAsync(manifest, runs));
        Assert.IsEmpty(runs.Asked);
    }

    [TestMethod]
    public async Task ARunThatDoesNotRead_IsAMissingRun_NamedByItsIdentifier_AndNothingAfterItIsRead()
    {
        var runs = new Runs();
        var first = runs.Hold(0, Run(0, Full));
        var lost = Id(424_242);
        var third = runs.Hold(2, Run(2 * (ulong)Full, 3, first: 2 * Full));
        var read = new List<ReferenceRun>();

        var missing = await Assert.ThrowsExactlyAsync<ReferenceRunMissingException>(async () =>
        {
            await foreach (var run in ReferenceRunReader.ReadAsync(Head(2 * (ulong)Full + 3, first, lost, third), runs.ReadAsync, CancellationToken.None))
            {
                read.Add(run);
            }
        });

        Assert.AreEqual(lost, missing.RunId);
        Assert.AreEqual(1, missing.Index);
        Assert.Contains(lost.ToString(), missing.Message, StringComparison.Ordinal);
        Assert.IsInstanceOfType<ManifestValidationException>(missing, "a missing run is damage to its version, as any other");
        Assert.ContainsSingle(read);
        CollectionAssert.AreEqual(new[] { first, lost }, runs.Asked);
    }

    [TestMethod]
    public async Task ARunThatDoesNotStartWhereTheOneBeforeEnded_IsRefused_WhenItArrives()
    {
        var runs = new Runs();
        var first = runs.Hold(0, Run(0, Full));
        var gapped = runs.Hold(1, Run((ulong)Full + 1, 3, first: Full));
        var read = new List<ReferenceRun>();

        var refused = await Assert.ThrowsExactlyAsync<ManifestValidationException>(async () =>
        {
            await foreach (var run in ReferenceRunReader.ReadAsync(Head((ulong)Full + 4, first, gapped), runs.ReadAsync, CancellationToken.None))
            {
                read.Add(run);
            }
        });

        Assert.Contains("run 1", refused.Message, StringComparison.Ordinal);
        Assert.ContainsSingle(read, "the run before the break is the reader's; the one that breaks is not");
    }

    [TestMethod]
    public async Task ARunNotFullBeforeTheLast_IsRefused_WhenItArrives()
    {
        var runs = new Runs();
        var first = runs.Hold(0, Run(0, Full - 1));
        var second = runs.Hold(1, Run((ulong)Full - 1, 2, first: Full));

        var refused = await Assert.ThrowsExactlyAsync<ManifestValidationException>(
            () => ReadAllAsync(Head((ulong)Full + 1, first, second), runs));

        Assert.Contains("run 0", refused.Message, StringComparison.Ordinal);
        CollectionAssert.AreEqual(new[] { first }, runs.Asked, "a run that is not full before the last is refused before the next is read");
    }

    [TestMethod]
    public async Task RunsThatStopShortOfTheFilesLength_AreRefused_OnceTheLastIsRead()
    {
        var runs = new Runs();
        var first = runs.Hold(0, Run(0, Full));
        var second = runs.Hold(1, Run(Full, 3, first: Full));

        var refused = await Assert.ThrowsExactlyAsync<ManifestValidationException>(
            () => ReadAllAsync(Head((ulong)Full + 4, first, second), runs));

        Assert.Contains(((ulong)Full + 3).ToString(System.Globalization.CultureInfo.InvariantCulture), refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ARunThatDoesNotDecode_IsRefused_AsDamageToItsVersion()
    {
        var first = Id(1);
        var second = Id(2);
        async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ObjectId id, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return id == first ? ReferenceRunCodec.Encode(Run(0, Full)) : new byte[] { 0xA0 };
        }

        await Assert.ThrowsExactlyAsync<ManifestValidationException>(async () =>
        {
            await foreach (var run in ReferenceRunReader.ReadAsync(Head((ulong)Full + 1, first, second), ReadAsync, CancellationToken.None))
            {
                _ = run;
            }
        });
    }

    [TestMethod]
    public void AManifestListingRuns_NamesThemAmongTheObjectsItsVersionNeeds_BeforeItsStreams()
    {
        var stream = Id(99);
        var manifest = Head((ulong)Full + 1, Id(1), Id(2)) with
        {
            Metadata = new EntryMetadata
            {
                ModifiedAt = 1_722_600_000_000,
                AlternateStreams = [new AlternateStreamEntry("Zone.Identifier"u8.ToArray(), stream, 12)],
            },
        };

        CollectionAssert.AreEqual(new[] { Id(1), Id(2), stream }, manifest.ContentObjects().ToArray());
    }
}
