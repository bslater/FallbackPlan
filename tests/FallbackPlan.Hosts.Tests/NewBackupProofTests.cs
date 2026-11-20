using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A backup is proven at each destination it lands at (FR-VER-002, ADR-0199):
/// the pass that finds a destination holding the newest backup whole proves
/// there every blob that backup needs which no backup proven there before
/// needed, rather than leaving them to the sample's rotation to reach. A
/// backup larger than one pass's share is proven over the passes after it,
/// and the pair stays due until it is. These run against a local path; the
/// same pass serves an object store.
/// </summary>
[TestClass]
public sealed class NewBackupProofTests : IAsyncDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly DateTimeOffset _start = DateTimeOffset.Now;

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    [TestMethod]
    public async Task AFirstBackup_LeavesItsPathProvenThroughIt()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false);
        WriteFiles();
        await using var runtime = await StartAsync();

        await PassAsync(runtime, _start);

        var row = Row(runtime);
        Assert.AreEqual(DestinationSyncState.InSync, row.State, row.LastError);
        Assert.AreEqual(Assert.ContainsSingle(StagingRecords(runtime)), row.ProvenSnapshot);
        Assert.IsNull(row.ProvingSnapshot);
        Assert.IsNull(row.ProvingCursor);
    }

    [TestMethod]
    public async Task ADirectShipSetsPath_IsProvenThroughTheBackupItsRunSent()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);
        WriteFiles();
        await using var runtime = await StartAsync();

        await PassAsync(runtime, _start);

        var row = Row(runtime);
        Assert.AreEqual(DestinationSyncState.InSync, row.State, row.LastError);
        Assert.AreEqual(Assert.ContainsSingle(VaultRecords()), row.ProvenSnapshot);
    }

    [TestMethod]
    public async Task ABlobTheNewestBackupSentDamaged_IsFound_ThoughTheSampleAsksOnlyAboutItsRecord()
    {
        // The rotation's share is the record alone, so only the new backup's
        // own proof can reach the blob.
        FanOut.ReadBackBudget = 1;
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false);
        WriteFiles();
        await using var runtime = await StartAsync();
        await PassAsync(runtime, _start);
        await SyncAsync(runtime);
        Assert.AreEqual(1, Row(runtime).VerifiedObjects, "the premise: with nothing new to prove, the sample asks only about the record");
        var proven = Row(runtime).ProvenSnapshot;
        var before = StagingBlobs(runtime);

        await BackUpAloneAsync(runtime, _start.AddHours(1));
        var damaged = StagingBlobs(runtime).Except(before, StringComparer.Ordinal)
            .Where(key => key.StartsWith("blobs/data/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new AssertFailedException("the premise: the second backup wrote a data blob");
        Damage(Path.Combine(Staging(runtime), damaged));
        await SyncAsync(runtime);

        var row = Row(runtime);
        Assert.AreEqual(DestinationSyncState.Failed, row.State, row.LastError);
        Assert.Contains(damaged, row.LastError!, StringComparison.Ordinal);
        Assert.AreEqual(proven, row.ProvenSnapshot, "a backup with a blob that would not prove was called proven");
    }

    [TestMethod]
    public async Task ABackupLargerThanAPassesShare_IsProvenOverTheNextPasses_AndThePairIsDueUntilItIs()
    {
        FanOut.NewBackupProofBudget = 1;
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false);
        WriteFiles();
        await using var runtime = await StartAsync();

        await PassAsync(runtime, _start);
        var newest = Assert.ContainsSingle(StagingRecords(runtime));
        var first = Row(runtime);
        Assert.IsNull(first.ProvenSnapshot, "the premise: the first backup needs more than one blob");
        Assert.AreEqual(newest, first.ProvingSnapshot);
        Assert.IsNotNull(first.ProvingCursor);

        // Not a backup's hour and long before a challenge is due: only the
        // proof under way makes the pair due.
        var due = await Scheduler.RunPassAsync(runtime, _start.AddMinutes(1), Timeout);
        await due.Transfers.WaitAsync(Timeout);
        var second = Row(runtime);
        Assert.IsGreaterThan(first.LastAttemptAt, second.LastAttemptAt, "a pair with a proof under way was not due");
        Assert.IsGreaterThan(0, string.CompareOrdinal(second.ProvingCursor ?? "~", first.ProvingCursor), "the proof did not move on");

        var passes = 0;
        while (Row(runtime).ProvenSnapshot is null)
        {
            Assert.IsLessThan(20, ++passes, "the proof never finished");
            await SyncAsync(runtime);
        }

        var last = Row(runtime);
        Assert.AreEqual(newest, last.ProvenSnapshot);
        Assert.IsNull(last.ProvingSnapshot);
        Assert.IsNull(last.ProvingCursor);
    }

    [TestMethod]
    public async Task ASecondBackup_ProvesOnlyTheBlobsTheFirstDidNotNeed()
    {
        // One blob a pass, so the passes count the blobs proven.
        FanOut.NewBackupProofBudget = 1;
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false);
        WriteFiles();
        await using var runtime = await StartAsync();
        await PassAsync(runtime, _start);
        while (Row(runtime).ProvenSnapshot is null)
        {
            await SyncAsync(runtime);
        }

        var blobsBefore = StagingBlobs(runtime);
        var recordsBefore = StagingRecords(runtime);
        await BackUpAloneAsync(runtime, _start.AddHours(1));
        var added = StagingBlobs(runtime).Except(blobsBefore, StringComparer.Ordinal).ToList();
        var newest = Assert.ContainsSingle(StagingRecords(runtime).Except(recordsBefore, StringComparer.Ordinal));

        var passes = 0;
        do
        {
            Assert.IsLessThan(20, ++passes, "the proof never finished");
            await SyncAsync(runtime);
        }
        while (Row(runtime).ProvenSnapshot != newest);

        Assert.IsNotEmpty(added, "the premise: the second backup wrote blobs");
        Assert.AreEqual(added.Count, passes, "the proof asked about blobs the first backup had needed too");
    }

    private void WriteFiles()
    {
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("docs/b.txt", new string('b', 40_000));
    }

    private void WriteConfiguration(bool directShip) =>
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('2', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                    DirectShip = directShip,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.SetupAsync();
        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            Timeout);
    }

    private async Task PassAsync(ServiceRuntime runtime, DateTimeOffset at)
    {
        var pass = await Scheduler.RunPassAsync(runtime, at, Timeout);
        Assert.AreEqual(1, pass.Ran, "the pass ran no backup");
        await pass.Transfers.WaitAsync(Timeout);
    }

    /// <summary>Another backup, of one more file, with no sync after it.</summary>
    private async Task BackUpAloneAsync(ServiceRuntime runtime, DateTimeOffset at)
    {
        _harness.WriteSourceFile("docs/c.txt", new string('c', 30_000));
        var ran = await Scheduler.Enqueue(runtime, runtime.Configuration.BackupSets.Single(), at, userInitiated: true)
            .WaitAsync(Timeout);
        Assert.AreEqual("ran", ran.Outcome, ran.Detail);
    }

    private async Task SyncAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var answer = await handler.ExecuteAsync(new SyncCommand("docs", "vault"), Timeout);
        Assert.IsInstanceOfType<SyncResult>(answer, (answer as ServiceError)?.Message);
    }

    private DestinationSyncRecord Row(ServiceRuntime runtime) =>
        runtime.DestinationSync.Find(_harness.DocsSetId, "vault") ?? throw new AssertFailedException("no ledger row for the vault");

    private string Staging(ServiceRuntime runtime) => runtime.ArchivePath(_harness.DocsSetId);

    private List<string> StagingBlobs(ServiceRuntime runtime) => [.. KeysUnder(Staging(runtime), "blobs")];

    private List<string> StagingRecords(ServiceRuntime runtime) => [.. KeysUnder(Staging(runtime), "snapshots")];

    private List<string> VaultRecords() => [.. KeysUnder(Assert.ContainsSingle(Directory.GetDirectories(Vault)), "snapshots")];

    private static IEnumerable<string> KeysUnder(string root, string plane)
    {
        var folder = Path.Combine(root, plane);
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            : [];
    }

    /// <summary>Overwrites a blob's tail, where its footer is, so it no longer opens.</summary>
    private static void Damage(string path)
    {
        var bytes = File.ReadAllBytes(path.Replace('/', Path.DirectorySeparatorChar));
        for (var index = Math.Max(0, bytes.Length - 64); index < bytes.Length; index++)
        {
            bytes[index] ^= 0x5A;
        }

        File.WriteAllBytes(path.Replace('/', Path.DirectorySeparatorChar), bytes);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _timeout.Dispose();
        _harness.Dispose();
        return ValueTask.CompletedTask;
    }
}
