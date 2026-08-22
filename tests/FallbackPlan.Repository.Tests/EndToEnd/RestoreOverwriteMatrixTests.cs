using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using FallbackPlan.Filesystem;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The rest of the existing-destination matrix (FR-RST-004, FR-RST-006;
/// ADR-0041). The four policies were proven for regular files only: the
/// executor's symlink branch — with its own four-way policy switch — had
/// never executed in any test, a directory sitting at a file's landing path
/// aborted the whole run instead of failing the item, and cancellation was
/// only ever tested pre-armed, never mid-item. Note one deliberate
/// asymmetry these tests do not "fix": <c>ExistingDestinationPolicy.Fail</c>
/// is an engine-level policy the service contract does not map — the wire
/// offers preserve (default), rename, and overwrite only.
/// </summary>
[TestClass]
public sealed class RestoreOverwriteMatrixTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private const string LinkTargetText = "sibling.bin";

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "creating symlinks needs no privilege on POSIX; on Windows it does")]
    public async Task RestoreExecution_ASymlink_IsCreatedWithItsCapturedTarget()
    {
        var (plan, target, store, keys) = await PublishLinkTreeAsync("fresh", 0xC1);
        using var _ = keys;

        var output = Path.Combine(SpoolDirectory, "fresh-out");
        var receipt = await ExecuteAsync(store, keys, target, plan, output, ExistingDestinationPolicy.Preserve, "fresh-run");

        // The link is a link, pointing at the captured target text — the
        // first time this branch has executed rather than been skipped.
        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        var link = Path.Combine(output, "data", "link");
        Assert.AreEqual(LinkTargetText, new FileInfo(link).LinkTarget);
        Assert.AreEqual("restored", Assert.ContainsSingle(receipt.Items.Where(item => item.Path == "data/link")).Outcome);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "creating symlinks needs no privilege on POSIX; on Windows it does")]
    public async Task RestoreExecution_AFileAtASymlinksDestinationUnderPreserve_DisplacesItAndCreatesTheLink()
    {
        var (plan, target, store, keys) = await PublishLinkTreeAsync("lnkpres", 0xC2);
        using var _ = keys;

        var output = Path.Combine(SpoolDirectory, "lnkpres-out");
        var occupied = Path.Combine(output, "data", "link");
        Directory.CreateDirectory(Path.GetDirectoryName(occupied)!);
        File.WriteAllText(occupied, "a plain file where the link goes");

        var receipt = await ExecuteAsync(store, keys, target, plan, output, ExistingDestinationPolicy.Preserve, "lnkpres-run");

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.AreEqual(LinkTargetText, new FileInfo(occupied).LinkTarget);
        Assert.Contains("data/link", receipt.Displaced);
        Assert.AreEqual(
            "a plain file where the link goes",
            File.ReadAllText(Path.Combine(output, ".fbp-displaced", "lnkpres-run", "data", "link")));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "creating symlinks needs no privilege on POSIX; on Windows it does")]
    public async Task RestoreExecution_AFileAtASymlinksDestinationUnderFail_FailsTheLinkAndRestoresTheRest()
    {
        var (plan, target, store, keys) = await PublishLinkTreeAsync("lnkfail", 0xC3);
        using var _ = keys;

        var output = Path.Combine(SpoolDirectory, "lnkfail-out");
        var occupied = Path.Combine(output, "data", "link");
        Directory.CreateDirectory(Path.GetDirectoryName(occupied)!);
        File.WriteAllText(occupied, "kept");

        var receipt = await ExecuteAsync(store, keys, target, plan, output, ExistingDestinationPolicy.Fail, "lnkfail-run");

        Assert.AreEqual(RestoreOutcome.Failed, receipt.Outcome);
        Assert.AreEqual("kept", File.ReadAllText(occupied));
        var failed = Assert.ContainsSingle(receipt.Items.Where(item => item.Outcome == "failed"));
        Assert.AreEqual("data/link", failed.Path);
        Assert.Contains("policy is to fail", failed.Detail!, StringComparison.Ordinal);
        Assert.IsTrue(File.Exists(Path.Combine(output, "data", LinkTargetText)), "the sibling file still restores");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "creating symlinks needs no privilege on POSIX; on Windows it does")]
    public async Task RestoreExecution_AFileAtASymlinksDestinationUnderReplace_ReplacesItWithTheLink()
    {
        var (plan, target, store, keys) = await PublishLinkTreeAsync("lnkrepl", 0xC4);
        using var _ = keys;

        var output = Path.Combine(SpoolDirectory, "lnkrepl-out");
        var occupied = Path.Combine(output, "data", "link");
        Directory.CreateDirectory(Path.GetDirectoryName(occupied)!);
        File.WriteAllText(occupied, "forfeited");

        var receipt = await ExecuteAsync(store, keys, target, plan, output, ExistingDestinationPolicy.Replace, "lnkrepl-run");

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.AreEqual(LinkTargetText, new FileInfo(occupied).LinkTarget);
        Assert.IsEmpty(receipt.Displaced);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "creating symlinks needs no privilege on POSIX; on Windows it does")]
    public async Task RestoreExecution_AFileAtASymlinksDestinationUnderWriteBeside_KeepsTheFileAndWritesTheLinkBeside()
    {
        var (plan, target, store, keys) = await PublishLinkTreeAsync("lnkbes", 0xC5);
        using var _ = keys;

        var output = Path.Combine(SpoolDirectory, "lnkbes-out");
        var occupied = Path.Combine(output, "data", "link");
        Directory.CreateDirectory(Path.GetDirectoryName(occupied)!);
        File.WriteAllText(occupied, "the live file, kept");

        var receipt = await ExecuteAsync(store, keys, target, plan, output, ExistingDestinationPolicy.WriteBeside, "lnkbes-run");

        // The live file untouched, the link beside it under the dated name,
        // and the receipt says where — the link half of ADR-0041's rename.
        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.AreEqual("the live file, kept", File.ReadAllText(occupied));
        var beside = Path.Combine(output, "data", "link (restored 2024-08-03)");
        Assert.AreEqual(LinkTargetText, new FileInfo(beside).LinkTarget);
        Assert.AreEqual(
            "data/link (restored 2024-08-03)",
            Assert.ContainsSingle(receipt.Items.Where(item => item.Path == "data/link")).WrittenAs);
    }

    [TestMethod]
    [DataRow(ExistingDestinationPolicy.Preserve)]
    [DataRow(ExistingDestinationPolicy.Fail)]
    [DataRow(ExistingDestinationPolicy.Replace)]
    [DataRow(ExistingDestinationPolicy.WriteBeside)]
    public async Task RestoreExecution_ADirectoryOccupiesAFilesDestination_FailsThatItemUnderEveryPolicy(
        ExistingDestinationPolicy policy)
    {
        // A directory is no policy's to resolve: Preserve would displace a
        // subtree it never planned, Replace would delete one. Before the
        // Directory.Exists check this skipped the policy block entirely and
        // the final move threw — aborting the run with no receipt at all.
        var content = Deterministic(9_000, 5);
        var sibling = Deterministic(9_000, 11);
        var source = new FakeFileSystemSource();
        source.AddFile("data/blocked.bin", content, fileId: 9_001);
        source.AddFile("data/free.bin", sibling, fileId: 9_002);
        var seed = (byte)(0xC6 + (int)policy);
        var (plan, target, store, keys) = await PublishAsync($"dir-{policy}", source, seed);
        using var _ = keys;

        var output = Path.Combine(SpoolDirectory, $"dir-{policy}-out");
        var occupied = Path.Combine(output, "data", "blocked.bin");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "inhabitant.txt"), "lives here");

        var receipt = await ExecuteAsync(store, keys, target, plan, output, policy, $"dir-{policy}-run");

        Assert.AreEqual(RestoreOutcome.Failed, receipt.Outcome);
        var failed = Assert.ContainsSingle(receipt.Items.Where(item => item.Outcome == "failed"));
        Assert.AreEqual("data/blocked.bin", failed.Path);
        Assert.Contains("directory occupies", failed.Detail!, StringComparison.Ordinal);
        Assert.AreEqual("lives here", File.ReadAllText(Path.Combine(occupied, "inhabitant.txt")));
        SequenceAssert.AreEqual(sibling, File.ReadAllBytes(Path.Combine(output, "data", "free.bin")));
        Assert.IsEmpty(receipt.Displaced);
    }

    [TestMethod]
    public async Task RestoreExecution_AHundredBesideCopiesAlready_RefusesTheItemRatherThanGuessing()
    {
        var content = Deterministic(9_000, 5);
        var source = new FakeFileSystemSource();
        source.AddFile("data/file.bin", content, fileId: 9_001);
        var (plan, target, store, keys) = await PublishAsync("cap", source, 0xCB);
        using var _ = keys;

        var output = Path.Combine(SpoolDirectory, "cap-out");
        var destination = Path.Combine(output, "data", "file.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "the live file");
        for (var attempt = 1; attempt <= 100; attempt++)
        {
            var suffix = attempt == 1 ? " (restored 2024-08-03)" : $" (restored 2024-08-03-{attempt})";
            File.WriteAllText(Path.Combine(output, "data", $"file{suffix}.bin"), "an earlier beside copy");
        }

        var receipt = await ExecuteAsync(store, keys, target, plan, output, ExistingDestinationPolicy.WriteBeside, "cap-run");

        // The hundredth name being taken is a refusal, not a hundred-and-
        // first guess — and none of the earlier copies is disturbed.
        Assert.AreEqual(RestoreOutcome.Failed, receipt.Outcome);
        var failed = Assert.ContainsSingle(receipt.Items.Where(item => item.Outcome == "failed"));
        Assert.Contains("hundred beside-named copies", failed.Detail!, StringComparison.Ordinal);
        Assert.AreEqual("the live file", File.ReadAllText(destination));
    }

    [TestMethod]
    public async Task RestoreExecution_CancelledMidItem_StillProducesAnHonestReceiptForWhatLanded()
    {
        // The pre-cancelled case was tested; a token firing INSIDE an item's
        // read was not — and it threw out of ExecuteAsync, losing the
        // receipt the cancellation design exists to keep.
        var alpha = Deterministic(9_000, 3);
        var beta = Deterministic(9_000, 7);
        var source = new FakeFileSystemSource();
        source.AddFile("data/alpha.bin", alpha, fileId: 9_001);
        source.AddFile("data/beta.bin", beta, fileId: 9_002);
        var (plan, target, store, keys) = await PublishAsync("midcancel", source, 0xCC);
        using var _ = keys;

        using var cancellation = new CancellationTokenSource();

        // Reads during execution: alpha's manifest, alpha's segment, beta's
        // manifest. The third read cancels and throws with the token — the
        // fault arrives mid-item, after alpha already landed.
        var cancelling = new CancellingObjectStore(store, cancellation, cancelOnRead: 3);
        using var reader = new RepositoryReader(Repo, keys, cancelling);
        await reader.LoadBlobsAsync(CancellationToken.None);
        cancelling.Arm();

        var output = Path.Combine(SpoolDirectory, "midcancel-out");
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "midcancel-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            cancellation.Token);

        Assert.AreEqual(RestoreOutcome.Cancelled, receipt.Outcome);
        Assert.IsTrue(receipt.Items.Count < plan.Items.Count, "a cancelled run reports fewer items than planned");
        SequenceAssert.AreEqual(alpha, File.ReadAllBytes(Path.Combine(output, "data", "alpha.bin")));
        Assert.IsFalse(File.Exists(Path.Combine(output, "data", "beta.bin")), "the interrupted item must not land");
        Assert.IsFalse(File.Exists(Path.Combine(output, "data", "beta.bin.fbp-restore-tmp")), "no spool debris either");
    }

    [TestMethod]
    public async Task RestorePlan_APathBeyondTheTargetsByteLimit_IsAConflictNamingTheLimit()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("data/a-name-well-beyond-a-tiny-path-limit.bin", Deterministic(100, 3), fileId: 9_001);
        source.AddFile("data/ok.bin", Deterministic(100, 5), fileId: 9_002);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue("pathlimit");
        await CreateOrchestrator(store, keys, hierarchy, catalogue, "pathlimit")
            .PublishAsync(Job(source, 0xCD), CancellationToken.None);

        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat((byte)0xCD, 16).ToArray(), string.Empty,
            RestoreTargetProfile.ForLocalPlatform() with { MaxPathBytes = 24 });

        // The overrun is a plan-time conflict naming the limit (architecture
        // 08 §2) — the operator decides, nothing surprises mid-transfer.
        var conflict = Assert.ContainsSingle(plan.Conflicts);
        Assert.AreEqual("data/a-name-well-beyond-a-tiny-path-limit.bin", conflict.Path);
        Assert.Contains("24-byte limit", conflict.Reason, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RestoreExecution_ASpecialFile_IsDeclaredInThePlanAndSkippedInTheReceipt()
    {
        var source = new FakeFileSystemSource();
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = "data/pipe",
            Kind = FallbackPlan.Filesystem.ScanEntryKind.Special,
            Diagnostics = ["special-kind: fifo"],
            Metadata = new FallbackPlan.Repository.Format.Manifests.EntryMetadata
            {
                ModifiedAt = 1_722_000_000_000, PosixMode = 0x1A4,
            },
        });
        source.AddFile("data/plain.bin", Deterministic(4_096, 3), fileId: 9_001);
        var (plan, target, store, keys) = await PublishAsync("special", source, 0xCE);
        using var _ = keys;

        // The plan declares the degradation up front…
        var degradation = Assert.ContainsSingle(
            plan.Degradations.Where(candidate => candidate.Capability == "special-files"));
        Assert.Contains("not materialised", degradation.Detail, StringComparison.Ordinal);

        // …and the run skips the item honestly: nothing lands at its path,
        // the receipt says why, and the run is Partial, never Complete.
        var output = Path.Combine(SpoolDirectory, "special-out");
        var receipt = await ExecuteAsync(store, keys, target, plan, output, ExistingDestinationPolicy.Preserve, "special-run");

        Assert.AreEqual(RestoreOutcome.Partial, receipt.Outcome);
        var skipped = Assert.ContainsSingle(receipt.Items.Where(item => item.Outcome == "skipped"));
        Assert.AreEqual("data/pipe", skipped.Path);
        Assert.Contains("not materialised", skipped.Detail!, StringComparison.Ordinal);
        Assert.IsFalse(File.Exists(Path.Combine(output, "data", "pipe")));
        Assert.IsTrue(File.Exists(Path.Combine(output, "data", "plain.bin")));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "the characterization needs a genuinely case-sensitive disk under the case-folding plan")]
    public async Task RestoreExecution_APlanCarryingCaseCollisions_StillExecutesBecauseConflictsAreTheOperatorsCall()
    {
        // Conflicts are advisory (architecture 08 §2): the plan states them
        // and the caller decides whether to run anyway. This characterises
        // that decision's mechanics — the executor does not re-refuse a plan
        // the operator accepted. On this case-sensitive disk both spellings
        // land distinctly; on a genuinely case-folding target the
        // existing-destination policy governs whichever lands second.
        var one = Deterministic(1_000, 3);
        var two = Deterministic(1_000, 9);
        var source = new FakeFileSystemSource();
        source.AddFile("docs/Readme.txt", one, fileId: 9_001);
        source.AddFile("docs/readme.TXT", two, fileId: 9_002);
        var (_, _, store, keys) = await PublishAsync("casefold", source, 0xCF);
        using var _ = keys;

        using var catalogue = OpenCatalogue("casefold");
        var foldingTarget = RestoreTargetProfile.ForLocalPlatform() with { CaseSensitive = false };
        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat((byte)0xCF, 16).ToArray(), string.Empty, foldingTarget);
        Assert.AreEqual(2, plan.Conflicts.Count(conflict => conflict.Reason.Contains("Collides", StringComparison.Ordinal)));

        var output = Path.Combine(SpoolDirectory, "casefold-out");
        var receipt = await ExecuteAsync(store, keys, foldingTarget, plan, output, ExistingDestinationPolicy.Preserve, "casefold-run");

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        SequenceAssert.AreEqual(one, File.ReadAllBytes(Path.Combine(output, "docs", "Readme.txt")));
        SequenceAssert.AreEqual(two, File.ReadAllBytes(Path.Combine(output, "docs", "readme.TXT")));
    }

    /// <summary>
    /// Delegates every read; once armed, the Nth read cancels the source and
    /// throws with its token — a fault arriving mid-item, not between items.
    /// </summary>
    private sealed class CancellingObjectStore(
        IObjectStore inner, CancellationTokenSource cancellation, int cancelOnRead) : IObjectStore
    {
        private bool _armed;
        private int _reads;

        public void Arm() => _armed = true;

        public StoreCapabilities Capabilities => inner.Capabilities;

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public ValueTask<OpenReadResult> OpenReadAsync(
            ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
        {
            if (_armed && ++_reads >= cancelOnRead)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }

            return inner.OpenReadAsync(key, range, cancellationToken);
        }

        public ValueTask<PutResult> PutAsync(
            ObjectKey key,
            Func<CancellationToken, ValueTask<Stream>> openContent,
            PutConditions conditions,
            CancellationToken cancellationToken) =>
            inner.PutAsync(key, openContent, conditions, cancellationToken);

        public IAsyncEnumerable<ObjectEntry> ListAsync(
            ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, options, cancellationToken);

        public ValueTask<DeleteResult> DeleteAsync(
            ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, conditions, cancellationToken);
    }

    private async Task<(RestorePlan Plan, RestoreTargetProfile Target, Storage.Local.LocalFileSystemObjectStore Store, RepositoryKeySet Keys)>
        PublishLinkTreeAsync(string name, byte seed)
    {
        var source = new FakeFileSystemSource();
        source.AddFile("data/" + LinkTargetText, Deterministic(4_096, 3), fileId: 9_001);
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = "data/link",
            Kind = FallbackPlan.Filesystem.ScanEntryKind.Symlink,
            LinkTarget = System.Text.Encoding.UTF8.GetBytes(LinkTargetText),
            Metadata = new FallbackPlan.Repository.Format.Manifests.EntryMetadata
            {
                ModifiedAt = 1_722_000_000_000,
            },
        });

        return await PublishAsync(name, source, seed);
    }

    private async Task<(RestorePlan Plan, RestoreTargetProfile Target, Storage.Local.LocalFileSystemObjectStore Store, RepositoryKeySet Keys)>
        PublishAsync(string name, FakeFileSystemSource source, byte seed)
    {
        var store = CreateStore();
        var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue(name);
        await CreateOrchestrator(store, keys, hierarchy, catalogue, name)
            .PublishAsync(Job(source, seed), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform() with { SupportsSymlinks = !OperatingSystem.IsWindows() };
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, target);
        return (plan, target, store, keys);
    }

    private static async Task<RestoreReceipt> ExecuteAsync(
        IObjectStore store,
        RepositoryKeySet keys,
        RestoreTargetProfile target,
        RestorePlan plan,
        string output,
        ExistingDestinationPolicy policy,
        string runId)
    {
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        return await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                ExistingDestination = policy,
                RunId = runId,
                NowUnixMilliseconds = 1_722_700_000_000, // 2024-08-03 UTC
            },
            CancellationToken.None);
    }

    private CatalogueDb OpenCatalogue(string name) =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, $"catalogue-{name}.db"), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, KeyHierarchy hierarchy, CatalogueDb catalogue, string spoolName)
    {
        var spool = Path.Combine(SpoolDirectory, spoolName);
        Directory.CreateDirectory(spool);

        return new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
            spool, observer: null, catalogue);
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
        ClientVersion = "restore-overwrite-matrix-tests/1.0",
    };

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }
}
