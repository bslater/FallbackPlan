using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A restore says which captured metadata it will not write back, before it
/// starts and after it ends (FR-RST-003, FR-RST-004; ADR-0083; architecture
/// 06 §3's "no silent drop"). The plan declares each attribute the tree
/// carries and the target will not get back, with how many files carry it.
/// For ownership it names the privilege applying it needs. The receipt names
/// what was not applied to each item it landed.
/// </summary>
/// <remarks>
/// <para>
/// The plan predicts by the target's rule; the receipt records what each
/// attribute's write actually did (ADR-0084), so a write the platform
/// refuses is listed even where the rule expected it to land. A file gets
/// its modification and access times everywhere, its creation time where
/// the target can set one, and its permissions where the target applies
/// POSIX metadata. Ownership, security descriptors, extended attributes and
/// file attributes are captured and recorded, not yet written back, and a
/// symlink is created with none of its own.
/// </para>
/// <para>
/// An item's outcome does not change for metadata alone. A file whose
/// content landed and verified is restored. What it lacks is said beside it,
/// as the plan said it would be. Alternate streams stay the exception they
/// were: a file missing one of its streams is not the captured file, so it
/// is degraded.
/// </para>
/// </remarks>
[TestClass]
public sealed class RestoreMetadataHonestyTests : ArchiveTestHarness
{
    private const ulong Modified = 1_722_000_000_000;

    private static readonly RestoreTargetProfile Posix = new()
    {
        CaseSensitive = true,
        SupportsPosixMetadata = true,
        SupportsSymlinks = true,
    };

    [TestMethod]
    public async Task Receipt_AFileCarryingMetadataTheTargetDoesNotApply_NamesEachAttributeNotApplied()
    {
        var source = new FakeFileSystemSource();
        var file = source.AddFile("data/owned.bin", Deterministic(3_000, 1));
        file.Metadata = new EntryMetadata
        {
            ModifiedAt = Modified,
            CreatedAt = 1_721_000_000_000,
            AccessedAt = 1_722_500_000_000,
            OwnerName = "ana",
            GroupName = "staff",
            ExtendedAttributes = [new ExtendedAttributeEntry("user.tag"u8.ToArray(), "kept"u8.ToArray())],
            FileAttributes = 0x20,
        };

        // A target that sets no creation times, so the list is one list on
        // every platform; the creation-time tests below hold the others.
        var (receipt, output) = await PublishAndRestoreAsync(
            source, 0xC1, RestoreTargetProfile.ForLocalPlatform() with { SupportsCreationTimes = false });

        var item = receipt.Items.Single(candidate => candidate.Path == "data/owned.bin");
        Assert.AreEqual("restored", item.Outcome, "metadata alone does not change what the content achieved");
        Assert.IsNotNull(item.NotApplied);
        CollectionAssert.AreEqual(
            new[] { "created_at", "owner", "group", "extended_attributes", "file_attributes" },
            item.NotApplied.ToArray());

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.AreEqual(6, receipt.SchemaVersion);
        Assert.AreEqual(6, RestoreReceipt.CurrentSchemaVersion);
        Assert.Contains("\"not_applied\"", receipt.ToJson(), StringComparison.Ordinal);

        // What it did apply, it applied. Nothing has read the file since, so
        // its access time is still the one the restore wrote.
        var landed = Path.Combine(output, "data", "owned.bin");
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds((long)Modified).UtcDateTime, File.GetLastWriteTimeUtc(landed));
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1_722_500_000_000).UtcDateTime, File.GetLastAccessTimeUtc(landed));

        // A directory's own captured metadata is not read back yet (ADR-0084
        // says so), so its item says nothing.
        Assert.IsNull(receipt.Items.Single(candidate => candidate.Path == "data").NotApplied);
    }

    [TestMethod]
    public async Task Receipt_AFileCarryingOnlyWhatEveryTargetApplies_SaysNothingMore()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("plain.bin", Deterministic(1_000, 2)).Metadata = new EntryMetadata { ModifiedAt = Modified };

        var (receipt, _) = await PublishAndRestoreAsync(source, 0xC2, RestoreTargetProfile.ForLocalPlatform());

        Assert.IsNull(Assert.ContainsSingle(receipt.Items).NotApplied);
        Assert.DoesNotContain("not_applied", receipt.ToJson(), StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows | TestPlatforms.MacOs, "only Windows and macOS can set a file's creation time")]
    [PlatformTrait(TestPlatforms.Windows | TestPlatforms.MacOs)]
    public async Task Receipt_ACreationTimeTheTargetCanSet_IsWrittenBack_AndNotListed()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("created.bin", Deterministic(1_000, 10)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, CreatedAt = 1_721_000_000_000 };

        var (receipt, output) = await PublishAndRestoreAsync(source, 0xC7, RestoreTargetProfile.ForLocalPlatform());

        Assert.IsNull(Assert.ContainsSingle(receipt.Items).NotApplied);
        var landed = Path.Combine(output, "created.bin");
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1_721_000_000_000).UtcDateTime, File.GetCreationTimeUtc(landed));
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds((long)Modified).UtcDateTime, File.GetLastWriteTimeUtc(landed));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux has no call that sets a file's creation time")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task Receipt_ACreationTimeThePlatformCannotSet_IsListed_WhateverTheProfileClaims()
    {
        // The receipt records what the write did, not what the rule expected:
        // a profile claiming creation times on Linux still gets none.
        var source = new FakeFileSystemSource();
        source.AddFile("created.bin", Deterministic(1_000, 11)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, CreatedAt = 1_721_000_000_000 };

        var (receipt, output) = await PublishAndRestoreAsync(
            source, 0xC8, RestoreTargetProfile.ForLocalPlatform() with { SupportsCreationTimes = true });

        CollectionAssert.AreEqual(new[] { "created_at" }, Assert.ContainsSingle(receipt.Items).NotApplied!.ToArray());
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeMilliseconds((long)Modified).UtcDateTime,
            File.GetLastWriteTimeUtc(Path.Combine(output, "created.bin")),
            "an attempt at the creation time must not land on the modification time");
    }

    [TestMethod]
    public async Task Receipt_ATimeBeyondWhatAFileCanCarry_IsNotApplied_AndTheRunCompletes()
    {
        // A manifest is untrusted input. A modification time in the year 10000
        // used to end the whole run with no receipt at all.
        var source = new FakeFileSystemSource();
        source.AddFile("far.bin", Deterministic(1_000, 12)).Metadata =
            new EntryMetadata { ModifiedAt = 253_402_300_800_000, AccessedAt = 1_722_500_000_000 };

        var (receipt, _) = await PublishAndRestoreAsync(source, 0xC9, RestoreTargetProfile.ForLocalPlatform());

        var item = Assert.ContainsSingle(receipt.Items);
        Assert.AreEqual("restored", item.Outcome);
        CollectionAssert.AreEqual(new[] { "modified_at" }, item.NotApplied!.ToArray());
        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
    }

    [TestMethod]
    public async Task Receipt_PermissionsOnATargetThatDoesNotApplyThem_AreNotApplied()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("mode.bin", Deterministic(1_000, 3)).Metadata =
            new EntryMetadata { ModifiedAt = Modified, PosixMode = 0x1A4 };

        var (receipt, _) = await PublishAndRestoreAsync(
            source, 0xC3, RestoreTargetProfile.ForLocalPlatform() with { SupportsPosixMetadata = false });

        CollectionAssert.AreEqual(new[] { "posix_mode" }, Assert.ContainsSingle(receipt.Items).NotApplied!.ToArray());
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a symlink needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Receipt_ASymlink_IsCreatedWithoutItsCapturedMetadata_AndSaysSo()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("data/target.bin", Deterministic(500, 4)).Metadata = new EntryMetadata { ModifiedAt = Modified };
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = "link",
            Kind = ScanEntryKind.Symlink,
            LinkTarget = "data/target.bin"u8.ToArray(),
            Metadata = new EntryMetadata { ModifiedAt = Modified, OwnerName = "ana" },
        });

        var (receipt, _) = await PublishAndRestoreAsync(source, 0xC4, Posix);

        var link = receipt.Items.Single(candidate => candidate.Path == "link");
        Assert.AreEqual("restored", link.Outcome);
        CollectionAssert.AreEqual(new[] { "modified_at", "owner" }, link.NotApplied!.ToArray());
    }

    [TestMethod]
    public async Task Plan_CapturedMetadataTheTargetWillNotApply_IsDeclaredWithCounts_AndOwnershipNamesItsPrivilege()
    {
        var source = new FakeFileSystemSource();
        foreach (var name in new[] { "a.txt", "b.txt" })
        {
            source.AddFile($"docs/{name}", Deterministic(700, 5)).Metadata = new EntryMetadata
            {
                ModifiedAt = Modified, PosixMode = 0x1A4, OwnerName = "ana", GroupName = "staff",
            };
        }

        source.AddFile("docs/tagged.txt", Deterministic(700, 6)).Metadata = new EntryMetadata
        {
            ModifiedAt = Modified,
            PosixMode = 0x1A4,
            OwnerName = "ana",
            ExtendedAttributes = [new ExtendedAttributeEntry("user.tag"u8.ToArray(), "kept"u8.ToArray())],
        };
        source.AddFile("docs/plain.txt", Deterministic(700, 7));

        var (plan, facts) = await PublishAndProbeAsync(source, 0xC5);

        var declared = RestoreMetadata.Declare(plan, facts, Posix);

        var ownership = Assert.ContainsSingle(declared.Where(degradation => degradation.Capability == "ownership"));
        Assert.Contains("3 file(s)", ownership.Detail, StringComparison.Ordinal);
        Assert.Contains("CAP_CHOWN", ownership.Detail, StringComparison.Ordinal);

        var attributes = Assert.ContainsSingle(declared.Where(degradation => degradation.Capability == "extended-attributes"));
        Assert.Contains("1 file(s)", attributes.Detail, StringComparison.Ordinal);

        // Declared only when the tree carries it, like the symlink and stream
        // declarations before it.
        Assert.DoesNotContain(degradation => degradation.Capability == "security-descriptors", declared);
        Assert.DoesNotContain(degradation => degradation.Capability == "creation-times", declared);

        // Where a target applies no POSIX metadata, the planner's own line
        // already says ownership will not be applied. It is not said twice.
        Assert.DoesNotContain(
            degradation => degradation.Capability == "ownership",
            RestoreMetadata.Declare(plan, facts, Posix with { SupportsPosixMetadata = false }));
    }

    [TestMethod]
    public async Task Plan_TimesAreDeclaredOnlyWhereTheTargetCannotSetThem()
    {
        var source = new FakeFileSystemSource();
        foreach (var name in new[] { "a.txt", "b.txt" })
        {
            source.AddFile($"docs/{name}", Deterministic(700, 13)).Metadata = new EntryMetadata
            {
                ModifiedAt = Modified, CreatedAt = 1_721_000_000_000, AccessedAt = 1_722_500_000_000,
            };
        }

        var (plan, facts) = await PublishAndProbeAsync(source, 0xCA);

        // Access times are written back on every target, so they are never
        // declared. Creation times are, where the target cannot set them.
        var withoutCreation = RestoreMetadata.Declare(plan, facts, Posix);
        Assert.DoesNotContain(degradation => degradation.Capability == "access-times", withoutCreation);
        var created = Assert.ContainsSingle(withoutCreation.Where(degradation => degradation.Capability == "creation-times"));
        Assert.Contains("2 file(s)", created.Detail, StringComparison.Ordinal);

        Assert.IsEmpty(RestoreMetadata.Declare(plan, facts, Posix with { SupportsCreationTimes = true }));
    }

    [TestMethod]
    public async Task Plan_ATreeCarryingOnlyWhatTheTargetApplies_DeclaresNothingMore()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.txt", Deterministic(700, 8));
        source.AddFile("docs/b.txt", Deterministic(700, 9));

        var (plan, facts) = await PublishAndProbeAsync(source, 0xC6);

        Assert.IsEmpty(RestoreMetadata.Declare(plan, facts, Posix));
    }

    [TestMethod]
    public async Task Probe_EachFile_SaysWhatItWritesAndWhatWasCaptured()
    {
        // The plan already decodes every manifest to find what the store is
        // missing, so what each file writes and carries costs it nothing more.
        const int MiB = 1024 * 1024;
        var content = new byte[4 * MiB];
        new Random(9).NextBytes(content.AsSpan(MiB, MiB));
        var source = new FakeFileSystemSource();
        var disk = source.AddFile("disk.img", content);
        source.AddNode(disk with { SparseExtents = [new SparseExtent(0, MiB), new SparseExtent(2 * MiB, 2 * MiB)] });

        var (plan, facts) = await PublishAndProbeAsync(source, 0xC7);

        var item = Assert.ContainsSingle(plan.Items);
        var known = facts[item.ObjectId];
        Assert.AreEqual(EntryKind.File, known.Kind);
        Assert.AreEqual((ulong)MiB, known.WrittenBytes);
        Assert.AreEqual(CapturedMetadata.ModifiedAt | CapturedMetadata.PosixMode, known.Captured);
    }

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }

    private static SnapshotJob Job(FakeFileSystemSource source, byte seed) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(seed, 16).ToArray(),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "restore-metadata-tests/1.0",
    };

    private async Task<(LocalFileSystemObjectStore Store, RepositoryKeySet Keys, CatalogueDb Catalogue)> PublishAsync(
        FakeFileSystemSource source, byte seed)
    {
        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"metadata-{seed:x2}.db"), Repo);

        await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, $"sequence-{seed:x2}.txt"))),
                SpoolDirectory, FormatVersions.SealedDataPlane, observer: null, catalogue)
            .PublishAsync(Job(source, seed), CancellationToken.None);

        return (store, keys, catalogue);
    }

    private async Task<(RestoreReceipt Receipt, string Output)> PublishAndRestoreAsync(
        FakeFileSystemSource source, byte seed, RestoreTargetProfile target)
    {
        var (store, keys, catalogue) = await PublishAsync(source, seed);
        using (keys)
        using (catalogue)
        {
            var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, target);
            using var reader = new RepositoryReader(Repo, keys, store, Authority);
            await reader.LoadBlobsAsync(CancellationToken.None);

            var output = Path.Combine(SpoolDirectory, $"restored-{seed:x2}");
            var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
                plan, output,
                new RestoreExecutionOptions
                {
                    DestinationMode = RestoreDestinationMode.InPlace,
                    RunId = "metadata",
                    NowUnixMilliseconds = 1_722_700_000_000,
                },
                CancellationToken.None);

            return (receipt, output);
        }
    }

    private async Task<(RestorePlan Plan, IReadOnlyDictionary<ObjectId, RestoreItemFacts> Facts)> PublishAndProbeAsync(
        FakeFileSystemSource source, byte seed)
    {
        var (store, keys, catalogue) = await PublishAsync(source, seed);
        using (keys)
        using (catalogue)
        {
            var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, Posix);
            var probed = await RestoreBlobSet.ResolveAsync(catalogue, plan, store, Repo, keys, CancellationToken.None);
            Assert.IsEmpty(probed.Missing);
            return (plan, probed.Facts);
        }
    }
}
