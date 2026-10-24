using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Lifecycle;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A repository a newer build upgraded past the newest format this build
/// writes (NFR-COMP-003; specification 11 §5.3; ADR-0066 Amendment 1). Its
/// effective version is one this build has never seen, so what an object
/// written at it must carry is the newer build's to say. This build still
/// opens it and reads it, and writes nothing into it: a publication and a
/// compaction are refused naming both versions before either touches the
/// store, and so is an upgrade record naming a version this build does not
/// implement.
/// </summary>
[TestClass]
public sealed class NewerFormatTests : IDisposable
{
    private const string PassphraseText = "a newer build was here before this one";

    private static readonly Domain.Identifiers.WriterId Writer =
        Domain.Identifiers.WriterId.FromBytes(Convert.FromHexString("d0d1d2d3d4d5d6d7d8d9dadbdcdddedf"));

    private static readonly byte[] SnapshotId = [.. Enumerable.Repeat((byte)0x71, 16)];

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-newer-format", Guid.NewGuid().ToString("n"));

    public NewerFormatTests() => Directory.CreateDirectory(_root);

    private static string Newer => $"format {NewerBuildUpgrade.UnknownVersion}";

    private static string Latest => $"format {FormatLimits.LatestFormatVersion}";

    /// <summary>The write-only default: the repository domain's verify-on-reuse reads content (ADR-0042 §7).</summary>
    private static CapturePolicy Policy => CapturePolicy.Default with
    {
        SegmentSize = SegmentSize.Create(64 * 1024),
        DedupTrustDomain = DedupTrustDomain.Device,
    };

    [TestMethod]
    public async Task Open_ARepositoryANewerBuildUpgraded_OpensToReadAndToWrite_AtTheNewerVersion()
    {
        var store = Store("open");
        using var catalogue = await CreateBackedUpAndUpgradedPastThisBuildAsync(store);

        using var passphrase = Passphrase.Create(PassphraseText);
        var (read, authority) = await RepositoryLifecycle.OpenForReadAsync(store, passphrase, CancellationToken.None);
        using (read)
        using (authority)
        {
            Assert.AreEqual(FormatLimits.LatestFormatVersion, read.Descriptor.FormatVersion);
            Assert.AreEqual(NewerBuildUpgrade.UnknownVersion, read.EffectiveFormatVersion);
            Assert.IsFalse(read.WritableByThisBuild);

            // The open a service makes for its staging archive: everything
            // that reads the set goes through this handle, so a refusal here
            // would take restore, verify and copy down with the writes.
            using var written = await RepositoryLifecycle.OpenAsync(store, read.Credential, CancellationToken.None);
            Assert.AreEqual(NewerBuildUpgrade.UnknownVersion, written.EffectiveFormatVersion);
            Assert.IsFalse(written.WritableByThisBuild);
        }
    }

    [TestMethod]
    public async Task Publication_ToARepositoryANewerBuildUpgraded_IsRefusedNamingBothVersions_AndWritesNothing()
    {
        var store = Store("publish");
        using var catalogue = await CreateBackedUpAndUpgradedPastThisBuildAsync(store);
        var before = await KeysAsync(store);

        using var passphrase = Passphrase.Create(PassphraseText);
        var (reopened, authority) = await RepositoryLifecycle.OpenForReadAsync(store, passphrase, CancellationToken.None);
        using var _ = reopened;
        using var __ = authority;

        var refused = Assert.ThrowsExactly<NewerFormatException>(() => Orchestrator(reopened, store, catalogue, "refused"));
        Assert.Contains(Newer, refused.Message, StringComparison.Ordinal);
        Assert.Contains(Latest, refused.Message, StringComparison.Ordinal);

        CollectionAssert.AreEquivalent(before, await KeysAsync(store));
    }

    [TestMethod]
    public async Task Restore_FromARepositoryANewerBuildUpgraded_ReturnsWhatItHolds()
    {
        var store = Store("restore");
        using var catalogue = await CreateBackedUpAndUpgradedPastThisBuildAsync(store);

        using var passphrase = Passphrase.Create(PassphraseText);
        var (reopened, authority) = await RepositoryLifecycle.OpenForReadAsync(store, passphrase, CancellationToken.None);
        using var _ = reopened;
        using var __ = authority;

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, SnapshotId, string.Empty, target);
        using var reader = new RepositoryReader(reopened.RepositoryId, reopened.Keys, store, authority);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var output = Path.Combine(_root, "restored");
        var restored = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan,
            output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "newer",
                NowUnixMilliseconds = 1_800_000_100_000,
            },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, restored.Outcome);
        SequenceAssert.AreEqual(Content, await File.ReadAllBytesAsync(Path.Combine(output, "kept.bin"), CancellationToken.None));
    }

    [TestMethod]
    public async Task Upgrade_ToAFormatThisBuildDoesNotKnow_IsRefused_AndWritesNoRecord()
    {
        var store = Store("unknown-upgrade");
        using var passphrase = Passphrase.Create(PassphraseText);
        var (created, authority) = await RepositoryLifecycle.CreateFromPassphraseAsync(
            store, passphrase, Settings(FormatLimits.LatestFormatVersion), 1_800_000_000_000, CancellationToken.None);
        using var _ = created;
        using var __ = authority;

        // The record is the claim that the repository now writes that format,
        // and this build cannot make it true.
        var refused = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            async () => await RepositoryLifecycle.WriteFormatUpgradeAsync(
                store, created.Descriptor, created.Credential, NewerBuildUpgrade.UnknownVersion,
                Writer.ToArray(), 1_800_000_000_001, CancellationToken.None));
        Assert.Contains(Newer, refused.Message, StringComparison.Ordinal);
        Assert.Contains(Latest, refused.Message, StringComparison.Ordinal);

        Assert.IsEmpty(await KeysAsync(store, FormatUpgradeRecordCodec.KeyPrefix));
    }

    [TestMethod]
    public async Task Upgrade_ToTheNewestFormatThisBuildKnows_LeavesTheRepositoryWritable()
    {
        // The boundary: a repository upgraded to the newest format this build
        // writes is this build's to write, as one created there is.
        var store = Store("latest-upgrade");
        using var passphrase = Passphrase.Create(PassphraseText);
        var (created, authority) = await RepositoryLifecycle.CreateFromPassphraseAsync(
            store, passphrase, Settings(FormatLimits.LatestFormatVersion - 1), 1_800_000_000_000,
            CancellationToken.None);
        using (created)
        using (authority)
        {
            await RepositoryLifecycle.WriteFormatUpgradeAsync(
                store, created.Descriptor, created.Credential, FormatLimits.LatestFormatVersion,
                Writer.ToArray(), 1_800_000_000_001, CancellationToken.None);
        }

        var (reopened, reauthority) = await RepositoryLifecycle.OpenForReadAsync(store, passphrase, CancellationToken.None);
        using var _ = reopened;
        using var __ = reauthority;
        using var catalogue = CatalogueDb.Open(Path.Combine(_root, "latest-upgrade.db"), reopened.RepositoryId);

        Assert.AreEqual(FormatLimits.LatestFormatVersion, reopened.EffectiveFormatVersion);
        Assert.IsTrue(reopened.WritableByThisBuild);
        Assert.IsNotNull(Orchestrator(reopened, store, catalogue, "latest"));
    }

    [TestMethod]
    public async Task Compaction_OfARepositoryANewerBuildUpgraded_IsRefusedNamingBothVersions()
    {
        var store = Store("compact");
        using var catalogue = await CreateBackedUpAndUpgradedPastThisBuildAsync(store);
        var before = await KeysAsync(store);

        using var passphrase = Passphrase.Create(PassphraseText);
        var (reopened, authority) = await RepositoryLifecycle.OpenForReadAsync(store, passphrase, CancellationToken.None);
        using var _ = reopened;
        using var __ = authority;

        // Refused before it looks at what it was handed, so the refusal is
        // the repository's and does not wait for a plan with work in it.
        var spool = Path.Combine(_root, "compact-spool");
        var refused = await Assert.ThrowsExactlyAsync<NewerFormatException>(
            async () => await CompactionPass.RunAsync(
                [],
                reopened,
                store,
                store,
                Writer,
                Policy,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
                catalogue,
                spool,
                1_800_000_100_000,
                declaredMaxDurationMs: 3_600_000,
                expiryGeneration: 5,
                CancellationToken.None));
        Assert.Contains(Newer, refused.Message, StringComparison.Ordinal);
        Assert.Contains(Latest, refused.Message, StringComparison.Ordinal);

        CollectionAssert.AreEquivalent(before, await KeysAsync(store));
    }

    private static readonly byte[] Content = [.. Enumerable.Range(0, 150_000).Select(index => (byte)(index * 31))];

    private LocalFileSystemObjectStore Store(string name) => new(Path.Combine(_root, name));

    private static RepositoryCreationSettings Settings(int formatVersion) => RepositoryCreationSettings.Default with
    {
        CreatedBy = "newer-format-tests/1.0",
        FormatVersion = (ushort)formatVersion,
    };

    /// <summary>
    /// A repository this build created and backed up once, then upgraded by
    /// a newer build to a format this build does not know. Returns the
    /// catalogue the backup projected into.
    /// </summary>
    private async Task<CatalogueDb> CreateBackedUpAndUpgradedPastThisBuildAsync(LocalFileSystemObjectStore store)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        var (created, authority) = await RepositoryLifecycle.CreateFromPassphraseAsync(
            store, passphrase, Settings(FormatLimits.LatestFormatVersion), 1_800_000_000_000, CancellationToken.None);
        using var _ = created;
        using var __ = authority;

        var catalogue = CatalogueDb.Open(Path.Combine(_root, Guid.NewGuid().ToString("n") + ".db"), created.RepositoryId);
        var source = new FakeFileSystemSource();
        source.AddFile("kept.bin", Content);

        var published = await Orchestrator(created, store, catalogue, "first").PublishAsync(
            new SnapshotJob
            {
                Source = source,
                Roots = [new ScanRoot("/")],
                DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                SnapshotId = SnapshotId,
                NowUnixMilliseconds = 1_800_000_000_002,
                DeclaredMaxDurationMs = 3_600_000,
                ExpiryGeneration = 5,
                ClientVersion = "newer-format-tests/1.0",
            },
            CancellationToken.None);
        Assert.IsEmpty(published.Failures);

        await NewerBuildUpgrade.PlantAsync(
            store, created.Credential, created.EffectiveFormatVersion, CancellationToken.None);
        return catalogue;
    }

    private PublicationOrchestrator Orchestrator(
        OpenedRepository repository, IObjectStore store, CatalogueDb catalogue, string name)
    {
        var spool = Path.Combine(_root, "spool-" + name);
        Directory.CreateDirectory(spool);
        return new PublicationOrchestrator(
            Policy, repository.RepositoryId, Writer, KeyGeneration.Zero, repository.Keys, repository.Credential, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
            spool,
            repository.EffectiveFormatVersion, observer: null, catalogue);
    }

    private static async Task<List<string>> KeysAsync(LocalFileSystemObjectStore store, string prefix = "")
    {
        var keys = new List<string>();
        await foreach (var entry in store.ListAsync(
            prefix.Length == 0 ? ObjectPrefix.All : ObjectPrefix.Parse(prefix), ListOptions.Default, CancellationToken.None))
        {
            keys.Add(entry.Key.Value);
        }

        return keys;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
