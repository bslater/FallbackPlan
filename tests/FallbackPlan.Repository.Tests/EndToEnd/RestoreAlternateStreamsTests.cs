using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A restore writes back the alternate data streams captured with a file it
/// lands, where the target keeps them (FR-RST-004; ADR-0174; the write-back
/// half of the restore review's RR-6). Each is read and proved like any
/// segment, and written onto the file before its times, since writing a
/// stream moves a file's modification time. A stream that does not land, for
/// a name no stream can have, a read that fails or a volume that refuses it,
/// leaves the file degraded, saying which and why (FR-RST-005). A stream
/// that already stands as captured, as one a run cut short wrote, is left as
/// it stands and read from nowhere. Where the target keeps streams and
/// writes no extended attributes of its own, as Windows does, a file's
/// attributes are kept as streams of the same names (ADR-0177), and so are a
/// folder's (ADR-0177 Amendment 2).
/// </summary>
/// <remarks>
/// Only Windows keeps alternate streams. Most cases run everywhere, against a
/// target that says it writes them and calls that hold them in memory, so
/// what the executor asks for and when is judged on every platform; one runs
/// the platform's own calls end to end on Windows, and one holds that they
/// write nothing anywhere else.
/// </remarks>
[TestClass]
public sealed class RestoreAlternateStreamsTests : ArchiveTestHarness
{
    private const string CarrierPath = "data/streams.bin";
    private const ulong CapturedModifiedAt = 1_722_000_000_000;

    private static readonly byte[] Zone = "[ZoneTransfer]\nZoneId=3"u8.ToArray();
    private static readonly byte[] Notes = "kept beside the file"u8.ToArray();

    private readonly string _sourceRoot =
        Path.Combine(Path.GetTempPath(), "fbp-alternate-streams-tests", Guid.NewGuid().ToString("n"));

    /// <summary>This machine's target, saying it writes alternate streams back.</summary>
    private static RestoreTargetProfile WritesStreams => RestoreTargetProfile.ForLocalPlatform() with { SupportsAlternateStreams = true };

    [TestMethod]
    public async Task AFileCarryingStreams_HasEachWrittenBackOntoIt_AndIsRestored()
    {
        var content = Deterministic(50_000, 9);
        using var published = await PublishAsync(Carrier(content, ("Zone.Identifier", Zone), ("notes", Notes)), 0xC1);
        var held = new HeldStreams();

        var plan = published.Plan(WritesStreams);
        Assert.DoesNotContain(degradation => degradation.Capability == "alternate-streams", plan.Degradations);

        var (receipt, output) = await RestoreAsync(published, plan, WritesStreams, held, "written");

        var landed = Path.Combine(output, "data", "streams.bin");
        SequenceAssert.AreEqual(content, File.ReadAllBytes(landed));
        SequenceAssert.AreEqual(Zone, held.Streams[(landed, "Zone.Identifier")]);
        SequenceAssert.AreEqual(Notes, held.Streams[(landed, "notes")]);
        Assert.HasCount(2, held.Streams);

        var item = Item(receipt);
        Assert.AreEqual("restored", item.Outcome);
        Assert.IsFalse(item.NotApplied?.Contains("alternate_streams") ?? false, "the streams it carried were applied");
        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
    }

    [TestMethod]
    public async Task AFilesExtendedAttributes_AreWrittenAsItsStreams_WhereTheTargetKeepsStreamsAndNoAttributes()
    {
        var content = Deterministic(4_096, 5);
        var source = new FakeFileSystemSource();
        source.AddFile(CarrierPath, content, fileId: 9_101).Metadata = new EntryMetadata
        {
            ModifiedAt = CapturedModifiedAt,
            ExtendedAttributes = [new ExtendedAttributeEntry("user.comment"u8.ToArray(), Notes)],
        };
        using var published = await PublishAsync(source, 0xCA);
        var keepsAsStreams = WritesStreams with { SupportsExtendedAttributes = false };
        var held = new HeldStreams();

        var plan = published.Plan(keepsAsStreams);
        Assert.DoesNotContain(degradation => degradation.Capability == "extended-attributes", plan.Degradations);
        var (receipt, output) = await RestoreAsync(published, plan, keepsAsStreams, held, "attributes");

        var landed = Path.Combine(output, "data", "streams.bin");
        SequenceAssert.AreEqual(Notes, held.Streams[(landed, "user.comment")]);
        Assert.IsFalse(Item(receipt).NotApplied?.Contains("extended_attributes") ?? false, "the attribute it carried was kept");
        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
    }

    [TestMethod]
    public async Task AFoldersExtendedAttributes_AreWrittenAsItsStreams_WhereTheTargetKeepsStreamsAndNoAttributes()
    {
        var source = new FakeFileSystemSource();
        source.AddFile(CarrierPath, Deterministic(4_096, 6), fileId: 9_102).Metadata = new EntryMetadata { ModifiedAt = CapturedModifiedAt };
        source.Folders["data"] = new EntryMetadata
        {
            ModifiedAt = CapturedModifiedAt,
            ExtendedAttributes = [new ExtendedAttributeEntry("user.comment"u8.ToArray(), Notes)],
        };
        using var published = await PublishAsync(source, 0xCB);
        var keepsAsStreams = WritesStreams with { SupportsExtendedAttributes = false };
        var held = new HeldStreams();

        var plan = published.Plan(keepsAsStreams);
        Assert.DoesNotContain(degradation => degradation.Capability == "extended-attributes", plan.Degradations);
        var (receipt, output) = await RestoreAsync(published, plan, keepsAsStreams, held, "folder-attributes");

        SequenceAssert.AreEqual(Notes, held.Streams[(Path.Combine(output, "data"), "user.comment")]);
        var folder = Assert.ContainsSingle(receipt.Items.Where(item => item.Path == "data"));
        Assert.IsFalse(folder.NotApplied?.Contains("extended_attributes") ?? false, "the attribute it carried was kept");
        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
    }

    [TestMethod]
    public void Profile_PassesWhetherItKeepsStreams_ToTheWriter_AndTheRuleCountsAFilesAttributesKeptAsThem()
    {
        var keepsAsStreams = RestoreTargetProfile.ForLocalPlatform() with { SupportsAlternateStreams = true, SupportsExtendedAttributes = false };

        Assert.IsTrue(keepsAsStreams.Metadata.SupportsAlternateStreams);
        Assert.IsFalse((keepsAsStreams with { SupportsAlternateStreams = false }).Metadata.SupportsAlternateStreams);
        Assert.IsTrue(RestoreMetadata.Applied(EntryKind.File, keepsAsStreams).HasFlag(CapturedMetadata.ExtendedAttributes));
        Assert.IsTrue(RestoreMetadata.Applied(EntryKind.DirectoryPlaceholder, keepsAsStreams).HasFlag(CapturedMetadata.ExtendedAttributes));
        Assert.IsFalse(RestoreMetadata.Applied(EntryKind.Symlink, keepsAsStreams).HasFlag(CapturedMetadata.ExtendedAttributes));
        Assert.IsFalse(RestoreMetadata.Applied(EntryKind.File, keepsAsStreams with { SupportsAlternateStreams = false })
            .HasFlag(CapturedMetadata.ExtendedAttributes));
    }

    [TestMethod]
    public async Task AStreamWrittenBack_LeavesTheFileTheModifiedTimeItWasCapturedWith()
    {
        // Writing a stream moves its file's modification time on NTFS, as
        // writing the file does. These calls do the same, so a stream
        // written after the times would leave the file with the time of the
        // restore.
        using var published = await PublishAsync(Carrier(Deterministic(4_096, 3), ("Zone.Identifier", Zone)), 0xC2);
        var held = new HeldStreams { OnWrite = file => File.SetLastWriteTimeUtc(file, DateTime.UtcNow) };

        var (receipt, output) = await RestoreAsync(published, published.Plan(WritesStreams), WritesStreams, held, "timed");

        Assert.AreEqual("restored", Item(receipt).Outcome);
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeMilliseconds((long)CapturedModifiedAt).UtcDateTime,
            File.GetLastWriteTimeUtc(Path.Combine(output, "data", "streams.bin")));
    }

    [TestMethod]
    [DataRow(@"..\..\escape")]
    [DataRow("..")]
    [DataRow("other:$DATA")]
    public async Task AStreamWhoseNameNoStreamCanHave_IsNotWritten_AndTheFileIsDegraded_NamingIt(string name)
    {
        var content = Deterministic(4_096, 4);
        using var published = await PublishAsync(Carrier(content, (name, Zone), ("notes", Notes)), 0xC3);
        var held = new HeldStreams();

        var (receipt, output) = await RestoreAsync(published, published.Plan(WritesStreams), WritesStreams, held, "named");

        // The stream with a name of its own still lands; the other reaches
        // no call at all.
        var landed = Path.Combine(output, "data", "streams.bin");
        SequenceAssert.AreEqual(content, File.ReadAllBytes(landed));
        Assert.AreEqual((landed, "notes"), Assert.ContainsSingle(held.Streams.Keys));
        Assert.AreEqual(1, held.Writes);

        var item = Item(receipt);
        Assert.AreEqual("degraded", item.Outcome);
        Assert.IsNotNull(item.Detail);
        Assert.Contains(name, item.Detail, StringComparison.Ordinal);
        Assert.Contains("alternate_streams", item.NotApplied!);
        Assert.AreEqual(RestoreOutcome.Partial, receipt.Outcome);
    }

    [TestMethod]
    public async Task AStreamTheVolumeRefuses_LeavesTheFileDegraded_SayingWhy()
    {
        var content = Deterministic(4_096, 5);
        using var published = await PublishAsync(Carrier(content, ("Zone.Identifier", Zone)), 0xC4);
        var held = new HeldStreams { Refusal = "the volume keeps no streams" };

        var (receipt, output) = await RestoreAsync(published, published.Plan(WritesStreams), WritesStreams, held, "refused");

        SequenceAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(output, "data", "streams.bin")));
        var item = Item(receipt);
        Assert.AreEqual("degraded", item.Outcome);
        Assert.Contains("Zone.Identifier", item.Detail!, StringComparison.Ordinal);
        Assert.Contains("the volume keeps no streams", item.Detail!, StringComparison.Ordinal);
        Assert.Contains("alternate_streams", item.NotApplied!);
    }

    [TestMethod]
    public async Task AStreamWhoseRecordWillNotRead_IsNotWritten_AndTheFileIsDegraded_WithItsContentWhole()
    {
        var content = Deterministic(4_096, 6);
        using var published = await PublishAsync(Carrier(content, ("Zone.Identifier", Zone), ("notes", Notes)), 0xC5);
        await DamageAsync(published, "Zone.Identifier");
        var held = new HeldStreams();

        var (receipt, output) = await RestoreAsync(published, published.Plan(WritesStreams), WritesStreams, held, "damaged");

        var landed = Path.Combine(output, "data", "streams.bin");
        SequenceAssert.AreEqual(content, File.ReadAllBytes(landed));
        Assert.AreEqual((landed, "notes"), Assert.ContainsSingle(held.Streams.Keys));

        var item = Item(receipt);
        Assert.AreEqual("degraded", item.Outcome);
        Assert.Contains("Zone.Identifier", item.Detail!, StringComparison.Ordinal);
        Assert.Contains(nameof(RecordReadOutcome.AuthenticationFailed), item.Detail!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AStreamItsOwnStoreHoldsDamaged_IsReadFromAnotherCopy_AndTheFileSaysWhere()
    {
        using var published = await PublishAsync(Carrier(Deterministic(4_096, 12), ("Zone.Identifier", Zone)), 0xCB);
        var spareRoot = Path.Combine(StoreRoot, "..", "spare");
        foreach (var file in Directory.EnumerateFiles(StoreRoot, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(spareRoot, Path.GetRelativePath(StoreRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }

        await DamageAsync(published, "Zone.Identifier");
        using var reader = new RepositoryReader(Repo, published.Keys, published.Store, Authority);
        reader.UseLocationSource(published.Catalogue.ResolveLocation);
        var spare = new LocalFileSystemObjectStore(spareRoot);
        reader.UseOtherCopies("the staging archive", [new CopySource("destination 'spare'", _ => ValueTask.FromResult<Storage.Abstractions.IObjectStore?>(spare))]);
        var held = new HeldStreams();

        var receipt = await new RestoreExecutor(reader, WritesStreams, streams: held).ExecuteAsync(
            published.Plan(WritesStreams), Path.Combine(SpoolDirectory, "around"), Options("around"), CancellationToken.None);

        var item = Item(receipt);
        Assert.AreEqual("restored", item.Outcome);
        SequenceAssert.AreEqual(Zone, Assert.ContainsSingle(held.Streams.Values));
        Assert.AreEqual("destination 'spare'", Assert.ContainsSingle(item.ReadFrom!));
        Assert.Contains("the staging archive", Assert.ContainsSingle(item.ReadAround!), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AResumedRun_LeavesAStreamStandingAsCaptured_AndReadsItFromNowhere()
    {
        using var published = await PublishAsync(Carrier(Deterministic(4_096, 7), ("Zone.Identifier", Zone)), 0xC6);
        var plan = published.Plan(WritesStreams);
        var held = new HeldStreams();
        var (_, output) = await RestoreAsync(published, plan, WritesStreams, held, "standing");
        var unwritten = await ResumeAsync(published, plan, new HeldStreams(), output);

        var (resumed, recordsRead) = await ResumeAsync(published, plan, held, output);

        var item = Item(resumed);
        Assert.AreEqual("restored", item.Outcome);
        Assert.IsTrue(item.FoundInPlace);
        Assert.AreEqual(1, held.Writes, "written by the first run alone");
        Assert.AreEqual(unwritten.RecordsRead - 1, recordsRead, "the stream that stands is the one record not read");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AResumedRun_WritesAStreamOfAFileFoundInPlace_ThatDoesNotStandAsCaptured(bool altered)
    {
        // A run cut short between a file's landing and its streams leaves it
        // whole without them, or with one the volume kept only part of.
        using var published = await PublishAsync(Carrier(Deterministic(4_096, 8), ("Zone.Identifier", Zone)), 0xC7);
        var plan = published.Plan(WritesStreams);
        var (_, output) = await RestoreAsync(published, plan, WritesStreams, new HeldStreams(), altered ? "altered" : "unwritten");
        var landed = Path.Combine(output, "data", "streams.bin");
        var held = new HeldStreams();
        if (altered)
        {
            var other = Zone.ToArray();
            other[^1] ^= 0x01;
            held.Streams[(landed, "Zone.Identifier")] = other;
        }

        var (resumed, _) = await ResumeAsync(published, plan, held, output);

        Assert.AreEqual("restored", Item(resumed).Outcome);
        Assert.IsTrue(Item(resumed).FoundInPlace);
        SequenceAssert.AreEqual(Zone, held.Streams[(landed, "Zone.Identifier")]);
        Assert.AreEqual(1, held.Writes);
    }

    [TestMethod]
    public async Task StreamsWrittenBack_AreReadAheadWithTheSegments_NotAFetchAStream()
    {
        // A file downloaded on Windows carries a stream naming where it came
        // from, so a tree of them is the common case, not the odd one. The
        // reader bridges no gap, so a stream not read ahead with its file's
        // segments is a fetch of its own.
        var source = new FakeFileSystemSource();
        for (var index = 0; index < 24; index++)
        {
            var file = source.AddFile($"data/file-{index:d2}.bin", Deterministic(4_096, (byte)index), fileId: (ulong)(9_200 + index));
            file.AlternateStreams["Zone.Identifier"] = Zone;
        }

        using var published = await PublishAsync(source, 0xC8);

        var without = await CountedRestoreAsync(published, WritesStreams with { SupportsAlternateStreams = false }, "reads-without");
        var with = await CountedRestoreAsync(published, WritesStreams, "reads-with");

        Assert.AreEqual(24, with.Held.Writes);
        Assert.IsLessThanOrEqualTo(without.Reads + 2, with.Reads, $"{without.Reads} store reads without the streams, {with.Reads} with them");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "POSIX keeps no alternate data streams, and a colon is a name's own character there")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task OnPosix_ATargetSaidToWriteStreams_HasThePlatformsCallsWriteNoFileBesideTheOneRestored()
    {
        using var published = await PublishAsync(Carrier(Deterministic(4_096, 10), ("Zone.Identifier", Zone)), 0xC9);

        var (receipt, output) = await RestoreAsync(published, published.Plan(WritesStreams), WritesStreams, streams: null, "posix");

        Assert.AreEqual("streams.bin", Path.GetFileName(Assert.ContainsSingle(Directory.GetFiles(Path.Combine(output, "data")))));
        var item = Item(receipt);
        Assert.AreEqual("degraded", item.Outcome);
        Assert.Contains("Windows", item.Detail!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Profile_ForThisPlatform_WritesStreamsOnlyOnWindows()
    {
        Assert.AreEqual(OperatingSystem.IsWindows(), RestoreTargetProfile.ForLocalPlatform().SupportsAlternateStreams);
        Assert.IsTrue(RestoreMetadata.Applied(EntryKind.File, WritesStreams).HasFlag(CapturedMetadata.AlternateStreams));
        Assert.IsFalse(RestoreMetadata.Applied(
            EntryKind.File, WritesStreams with { SupportsAlternateStreams = false }).HasFlag(CapturedMetadata.AlternateStreams));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps alternate data streams")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task OnWindows_AFileCarryingStreams_RoundTripsWithThem_ThroughCaptureAndRestore()
    {
        var content = Deterministic(70_000, 11);
        var file = Path.Combine(_sourceRoot, "data", "streams.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, content);
        File.WriteAllBytes(file + ":Zone.Identifier", Zone);
        File.WriteAllBytes(file + ":notes", Notes);
        var modified = new DateTime(2024, 7, 26, 9, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, modified);

        using var published = await PublishAsync(new LocalFileSystemSource(), 0xCA, _sourceRoot);
        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = published.Plan(target);
        Assert.DoesNotContain(degradation => degradation.Capability == "alternate-streams", plan.Degradations);

        var (receipt, output) = await RestoreAsync(published, plan, target, streams: null, "windows");

        var landed = Path.Combine(output, "data", "streams.bin");
        Assert.AreEqual("restored", Item(receipt).Outcome);
        SequenceAssert.AreEqual(content, File.ReadAllBytes(landed));
        SequenceAssert.AreEqual(Zone, File.ReadAllBytes(landed + ":Zone.Identifier"));
        SequenceAssert.AreEqual(Notes, File.ReadAllBytes(landed + ":notes"));
        Assert.AreEqual(modified, File.GetLastWriteTimeUtc(landed));
    }

    private static FakeFileSystemSource Carrier(byte[] content, params (string Name, byte[] Content)[] streams)
    {
        var source = new FakeFileSystemSource();
        var carrier = source.AddFile(CarrierPath, content, fileId: 9_101);
        carrier.Metadata = new EntryMetadata { ModifiedAt = CapturedModifiedAt };
        foreach (var (name, stream) in streams)
        {
            carrier.AlternateStreams[name] = stream;
        }

        return source;
    }

    private static ReceiptItem Item(RestoreReceipt receipt) =>
        Assert.ContainsSingle(receipt.Items.Where(item => item.Path == CarrierPath));

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }

    private async Task<Published> PublishAsync(IFileSystemSource source, byte seed, string root = "/")
    {
        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"streams-{seed:x2}.db"), Repo);
        var spool = Path.Combine(SpoolDirectory, $"spool-{seed:x2}");
        Directory.CreateDirectory(spool);

        var result = await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
                spool, FormatVersions.SealedDataPlane, observer: null, catalogue)
            .PublishAsync(
                new SnapshotJob
                {
                    Source = source,
                    Roots = [new ScanRoot(root)],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = Enumerable.Repeat(seed, 16).ToArray(),
                    NowUnixMilliseconds = 1_722_600_000_000,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "restore-alternate-streams-tests/1.0",
                },
                CancellationToken.None);
        Assert.IsEmpty(result.Failures);

        return new Published(store, keys, catalogue, Enumerable.Repeat(seed, 16).ToArray());
    }

    /// <summary>Flips one byte of the record that holds the carrier's stream <paramref name="name"/>.</summary>
    private async Task DamageAsync(Published published, string name)
    {
        using var probe = new RepositoryReader(Repo, published.Keys, published.Store, Authority);
        await probe.LoadBlobsAsync(CancellationToken.None);
        var item = published.Plan(WritesStreams).Items.Single(candidate => candidate.Path == CarrierPath);
        var manifest = FileVersionManifestCodec.Decode((await probe.ReadSegmentAsync(item.ObjectId, CancellationToken.None)).Plaintext!);
        var stream = manifest.Metadata.AlternateStreams.Single(
            candidate => System.Text.Encoding.UTF8.GetString(candidate.Name.Span) == name);
        Assert.IsTrue(probe.TryLocateRecord(stream.ObjectId, out var blobKey, out var entry));

        var blob = Path.Combine(StoreRoot, blobKey.Value.Replace('/', Path.DirectorySeparatorChar));
        var stored = await File.ReadAllBytesAsync(blob, CancellationToken.None);
        stored[(long)entry.PhysicalOffset + 54 + 3] ^= 0x01;
        await File.WriteAllBytesAsync(blob, stored, CancellationToken.None);
    }

    private async Task<(RestoreReceipt Receipt, string Output)> RestoreAsync(
        Published published, RestorePlan plan, RestoreTargetProfile target, IAlternateStreams? streams, string output)
    {
        using var reader = new RepositoryReader(Repo, published.Keys, published.Store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var root = Path.Combine(SpoolDirectory, output);
        var receipt = await new RestoreExecutor(reader, target, streams: streams).ExecuteAsync(
            plan, root, Options(output), CancellationToken.None);
        return (receipt, root);
    }

    private static async Task<(RestoreReceipt Receipt, long RecordsRead)> ResumeAsync(
        Published published, RestorePlan plan, HeldStreams streams, string root)
    {
        using var reader = new RepositoryReader(Repo, published.Keys, published.Store, Authority);
        reader.UseLocationSource(published.Catalogue.ResolveLocation);

        var receipt = await new RestoreExecutor(reader, WritesStreams, streams: streams).ExecuteAsync(
            plan, root, Options(Path.GetFileName(root)) with { Resume = true }, CancellationToken.None);
        return (receipt, reader.RecordsRead);
    }

    private async Task<(long Reads, HeldStreams Held)> CountedRestoreAsync(
        Published published, RestoreTargetProfile target, string output)
    {
        var counting = new CountingObjectStore(published.Store);
        using var reader = new RepositoryReader(Repo, published.Keys, counting, Authority);
        reader.UseLocationSource(published.Catalogue.ResolveLocation);
        reader.UsePrefetchPolicy(PrefetchPolicy.Default with { MaximumBridgeBytes = 0 });
        var held = new HeldStreams();

        var receipt = await new RestoreExecutor(reader, target, streams: held).ExecuteAsync(
            published.Plan(target), Path.Combine(SpoolDirectory, output), Options(output), CancellationToken.None);
        Assert.AreNotEqual(RestoreOutcome.Failed, receipt.Outcome);
        return (counting.Reads, held);
    }

    private static RestoreExecutionOptions Options(string run) => new()
    {
        DestinationMode = RestoreDestinationMode.InPlace,
        RunId = run,
        NowUnixMilliseconds = 1_722_700_000_000,
    };

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(_sourceRoot))
        {
            Directory.Delete(_sourceRoot, recursive: true);
        }

        base.Dispose(disposing);
    }

    private sealed record Published(LocalFileSystemObjectStore Store, RepositoryKeySet Keys, CatalogueDb Catalogue, byte[] Snapshot)
        : IDisposable
    {
        public RestorePlan Plan(RestoreTargetProfile target) => RestorePlanner.Plan(Catalogue, Snapshot, string.Empty, target);

        public void Dispose()
        {
            Keys.Dispose();
            Catalogue.Dispose();
        }
    }
}
