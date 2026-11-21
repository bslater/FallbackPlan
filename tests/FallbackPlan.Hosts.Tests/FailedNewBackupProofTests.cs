using System.Globalization;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A failed proof of a new backup names the backup (FR-VER-005, ADR-0199
/// Amendment 4): a blob of the backup under proof that does not prove fails
/// the pass as before, and the failure, its notice and the destination's
/// status say which backup that blob belongs to, that it is not proven there,
/// and which backup is the newest proven there, so a person knows which
/// recovery point to distrust and which still stands.
/// </summary>
[TestClass]
public sealed class FailedNewBackupProofTests : IAsyncDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly DateTimeOffset _start = DateTimeOffset.Now;

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    [TestMethod]
    public async Task ADamagedBlobOfTheBackupUnderProof_NamesThatBackup_AndTheNewestProvenBeforeIt()
    {
        // The rotation asks only about the record, so only the second
        // backup's own proof reaches its blobs.
        FanOut.ReadBackBudget = 1;
        await using var runtime = await StartAsync();
        await PassAsync(runtime);
        var first = Assert.ContainsSingle(await SnapshotsAsync(runtime));
        Assert.AreEqual(first.Key, Row(runtime).ProvenSnapshot, "the premise: the vault is proven through the first backup");

        var damaged = await SecondBackupWithADamagedBlobAsync(runtime);
        var second = Assert.ContainsSingle((await SnapshotsAsync(runtime)).Where(snapshot => snapshot.Key != first.Key));
        await SyncAsync(runtime);

        var row = Row(runtime);
        Assert.AreEqual(DestinationSyncState.Failed, row.State, row.LastError);
        var words = $"'{damaged}' belongs to {Name(second)}, which is not proven here; "
            + $"the newest backup proven here is {Name(first)}";
        Assert.Contains(words, row.LastError!, StringComparison.Ordinal);
        Assert.AreEqual(first.Key, row.ProvenSnapshot);
        Assert.AreEqual(second.Key, row.ProvingSnapshot, "the ledger does not say which backup's proof failed");
        Assert.AreEqual(damaged, row.ProvingFailedKey);

        // The staging sweep may meet the damaged blob too, and say so in a
        // notice of its own.
        var notice = Assert.ContainsSingle(runtime.Notices.Unacknowledged
            .Where(raised => raised.Key.StartsWith("verification-failed:", StringComparison.Ordinal)));
        Assert.Contains(words, notice.Message, StringComparison.Ordinal);

        var proof = (await StatusRowAsync(runtime)).NewBackupProof!;
        Assert.AreEqual(second.Descriptor.SnapshotId, proof.ProvingSnapshotId);
        Assert.AreEqual(first.Descriptor.SnapshotId, proof.ProvenSnapshotId);
        Assert.AreEqual(damaged, proof.FailedBlob);
    }

    [TestMethod]
    public async Task ADamagedBlobOfAFirstBackup_SaysNoBackupIsProvenHereYet()
    {
        FanOut.ReadBackBudget = 1;
        await using var runtime = await StartAsync();
        var before = StagingBlobs(runtime);
        await BackUpAloneAsync(runtime, _start);
        var damaged = FirstNewDataBlob(runtime, before);
        Damage(Path.Combine(Staging(runtime), damaged));

        await SyncAsync(runtime);

        var row = Row(runtime);
        Assert.AreEqual(DestinationSyncState.Failed, row.State, row.LastError);
        var only = Assert.ContainsSingle(await SnapshotsAsync(runtime));
        Assert.Contains(
            $"'{damaged}' belongs to {Name(only)}, which is not proven here; no backup is proven here yet",
            row.LastError!, StringComparison.Ordinal);
        Assert.IsNull(row.ProvenSnapshot);
        Assert.AreEqual(only.Key, row.ProvingSnapshot);
        Assert.AreEqual(damaged, row.ProvingFailedKey);
    }

    [TestMethod]
    public async Task ABlobRepairedAtTheDestination_IsProvenByTheNextPass_AndTheFailureIsForgotten()
    {
        FanOut.ReadBackBudget = 1;
        await using var runtime = await StartAsync();
        await PassAsync(runtime);
        var damaged = await SecondBackupWithADamagedBlobAsync(runtime);
        await SyncAsync(runtime);
        Assert.AreEqual(damaged, Row(runtime).ProvingFailedKey, "the premise: the second backup's proof failed");

        // The same flip again restores the bytes, here and at the vault.
        Damage(Path.Combine(Staging(runtime), damaged));
        Damage(Path.Combine(VaultReplica(), damaged));
        await SyncAsync(runtime);

        var row = Row(runtime);
        Assert.AreEqual(DestinationSyncState.InSync, row.State, row.LastError);
        Assert.IsNull(row.ProvingSnapshot);
        Assert.IsNull(row.ProvingFailedKey, "a proof that went on to its end still names a blob that failed it");
        Assert.IsNull((await StatusRowAsync(runtime)).NewBackupProof!.FailedBlob);
    }

    [TestMethod]
    public async Task ADamagedBlobOfABackupAlreadyProven_FailsAsBefore_NamingNoBackup()
    {
        // Nothing is being proven: the rotation finds the blob, and it may
        // belong to any backup still kept, so the failure names none.
        await using var runtime = await StartAsync();
        await PassAsync(runtime);
        Assert.IsNull(Row(runtime).ProvingSnapshot, "the premise: the vault is proven through its newest backup");
        var blob = StagingBlobs(runtime).First(key => key.StartsWith("blobs/data/", StringComparison.Ordinal));
        Damage(Path.Combine(VaultReplica(), blob));

        await SyncAsync(runtime);

        var row = Row(runtime);
        Assert.AreEqual(DestinationSyncState.Failed, row.State, row.LastError);
        Assert.Contains("verification failed", row.LastError!, StringComparison.Ordinal);
        Assert.DoesNotContain("belongs to", row.LastError!, StringComparison.Ordinal);
        Assert.IsNull(row.ProvingFailedKey);
    }

    [TestMethod]
    public async Task ADamagedBlobOfAnOlderBackup_MetWhileANewerIsBeingProven_NamesNoBackup()
    {
        // The second backup's own blobs prove; the rotation meets a blob only
        // the first needed, damaged at the vault. It is no blob of the
        // backup under proof, so the failure names none.
        await using var runtime = await StartAsync();
        await PassAsync(runtime);
        var before = StagingBlobs(runtime);
        var older = before.First(key => key.StartsWith("blobs/data/", StringComparison.Ordinal));
        Damage(Path.Combine(VaultReplica(), older));
        _harness.WriteSourceFile("docs/c.txt", new string('c', 30_000));
        await BackUpAloneAsync(runtime, _start.AddHours(1));
        Assert.IsNotEmpty(StagingBlobs(runtime).Except(before, StringComparer.Ordinal), "the premise: the second backup wrote blobs");

        await SyncAsync(runtime);

        var row = Row(runtime);
        Assert.AreEqual(DestinationSyncState.Failed, row.State, row.LastError);
        Assert.Contains(older, row.LastError!, StringComparison.Ordinal);
        Assert.DoesNotContain("belongs to", row.LastError!, StringComparison.Ordinal);
        Assert.IsNull(row.ProvingFailedKey);
    }

    /// <summary>Another backup, of one more file, its first new data blob damaged in staging before any sync.</summary>
    private async Task<string> SecondBackupWithADamagedBlobAsync(ServiceRuntime runtime)
    {
        var before = StagingBlobs(runtime);
        _harness.WriteSourceFile("docs/c.txt", new string('c', 30_000));
        await BackUpAloneAsync(runtime, _start.AddHours(1));
        var damaged = FirstNewDataBlob(runtime, before);
        Damage(Path.Combine(Staging(runtime), damaged));
        return damaged;
    }

    private string FirstNewDataBlob(ServiceRuntime runtime, List<string> before) =>
        StagingBlobs(runtime).Except(before, StringComparer.Ordinal)
            .Where(key => key.StartsWith("blobs/data/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault()
        ?? throw new AssertFailedException("the premise: the backup wrote a data blob");

    /// <summary>A backup as the failure names it: when it was taken, and the start of its identity.</summary>
    private static string Name(Backup backup) => string.Create(
        CultureInfo.InvariantCulture,
        $"the backup of {DateTimeOffset.FromUnixTimeMilliseconds((long)backup.Descriptor.CapturedAt):yyyy-MM-dd HH:mm} UTC ({backup.Descriptor.SnapshotId[..8]})");

    private async Task<ServiceRuntime> StartAsync()
    {
        Directory.CreateDirectory(Vault);
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
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("docs/b.txt", new string('b', 40_000));

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

    private async Task PassAsync(ServiceRuntime runtime)
    {
        var pass = await Scheduler.RunPassAsync(runtime, _start, Timeout);
        Assert.AreEqual(1, pass.Ran, "the pass ran no backup");
        await pass.Transfers.WaitAsync(Timeout);
    }

    private async Task BackUpAloneAsync(ServiceRuntime runtime, DateTimeOffset at)
    {
        var ran = await Scheduler.Enqueue(runtime, runtime.Configuration.BackupSets.Single(), at, userInitiated: true)
            .WaitAsync(Timeout);
        Assert.AreEqual("ran", ran.Outcome, ran.Detail);
    }

    private async Task SyncAsync(ServiceRuntime runtime)
    {
        var answer = await Handler(runtime).ExecuteAsync(new SyncCommand("docs", "vault"), Timeout);
        Assert.IsInstanceOfType<SyncResult>(answer, (answer as ServiceError)?.Message);
    }

    private async Task<DestinationStatusDescriptor> StatusRowAsync(ServiceRuntime runtime)
    {
        var answer = await Handler(runtime).ExecuteAsync(new GetStatusCommand(), Timeout);
        Assert.IsInstanceOfType<StatusResult>(answer, out var status, (answer as ServiceError)?.Message);
        return Assert.ContainsSingle(Assert.ContainsSingle(status.Sets).Destinations);
    }

    /// <summary>The set's backups, each with the record key the ledger names it by.</summary>
    private async Task<List<Backup>> SnapshotsAsync(ServiceRuntime runtime)
    {
        var answer = await Handler(runtime).ExecuteAsync(new ListSnapshotsCommand(), Timeout);
        Assert.IsInstanceOfType<SnapshotsResult>(answer, out var listing, (answer as ServiceError)?.Message);
        var records = KeysUnder(Staging(runtime), "snapshots");
        return [.. listing.Snapshots.Select(snapshot => new Backup(
            Assert.ContainsSingle(records.Where(key => key.EndsWith(
                "/" + FallbackPlan.Domain.Base32.Encode(Convert.FromHexString(snapshot.SnapshotId)), StringComparison.Ordinal))),
            snapshot))];
    }

    private static ServiceCommandHandler Handler(ServiceRuntime runtime) => new(runtime, RemoteBindingState.Off);

    private DestinationSyncRecord Row(ServiceRuntime runtime) =>
        runtime.DestinationSync.Find(_harness.DocsSetId, "vault") ?? throw new AssertFailedException("no ledger row for the vault");

    private string Staging(ServiceRuntime runtime) => runtime.ArchivePath(_harness.DocsSetId);

    private string VaultReplica() => Assert.ContainsSingle(Directory.GetDirectories(Vault));

    private List<string> StagingBlobs(ServiceRuntime runtime) => [.. KeysUnder(Staging(runtime), "blobs")];

    private static IEnumerable<string> KeysUnder(string root, string plane)
    {
        var folder = Path.Combine(root, plane);
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            : [];
    }

    /// <summary>Flips a blob's tail, where its footer is, so it no longer opens; the same flip again restores it.</summary>
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

    /// <summary>A backup: its record key, as the ledger names it, and the listing's descriptor of it.</summary>
    private sealed record Backup(string Key, SnapshotDescriptor Descriptor);
}
