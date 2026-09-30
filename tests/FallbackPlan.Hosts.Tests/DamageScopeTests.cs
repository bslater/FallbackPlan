using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Damage has a scope a person can act on (FR-VER-005, specification 04 §7).
/// Objects a destination holds damaged, with no sound copy to replace them,
/// degrade the snapshots that need them there — in the per-snapshot status —
/// and no others; the notice and verify-destination's line name the files and
/// the snapshots they reach. The scope is read when it is asked for, so a
/// snapshot taken after the finding that needs the same objects is degraded
/// too. Damage the service cannot trace to any snapshot degrades every
/// snapshot there, as all damage did before it could be traced.
/// </summary>
/// <remarks>
/// A direct-ship set with one destination, so nothing else holds its blobs
/// and a finding cannot be repaired unless the test gives it a sibling. The
/// content is random so that a changed file shares no segment with the
/// version before it.
/// </remarks>
[TestClass]
public sealed class DamageScopeTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private string Spare => Path.Combine(_harness.WorkPath, "spare");

    [TestMethod]
    public async Task DamageOnlyTheOlderSnapshotNeeds_DegradesThatSnapshotThere_AndNotTheNewer()
    {
        await using var runtime = await StartAsync(withSpare: false);
        WriteRandom("docs/ledger.txt", 1);
        await BackUpAsync(runtime);
        var older = DataKeys(Vault);
        WriteRandom("docs/ledger.txt", 2);
        await BackUpAsync(runtime);

        Tamper(Vault, older);
        var verified = await VerifyAsync(runtime);
        Assert.IsTrue(verified.Damaged > 0, string.Join(" | ", verified.Lines));

        var (first, second) = await BothSnapshotsAsync(runtime);
        Assert.AreEqual("vault: degraded", Assert.ContainsSingle(first.Destinations!));
        Assert.AreNotEqual(
            "vault: degraded", Assert.ContainsSingle(second.Destinations!),
            "the newer snapshot needs none of what was found damaged, and is still restorable from there");

        var line = Assert.ContainsSingle(verified.Lines);
        Assert.Contains("docs/ledger.txt", line, StringComparison.Ordinal);
        Assert.Contains("1 snapshot(s)", line, StringComparison.Ordinal);

        var notice = Notice(runtime).Message;
        Assert.Contains("docs/ledger.txt", notice, StringComparison.Ordinal);
        Assert.Contains("1 snapshot(s)", notice, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ASnapshotTakenAfterTheFinding_ThatNeedsTheSameObjects_IsDegradedThereToo()
    {
        // The scope is read when a person asks, never kept from when the
        // damage was found: a capture that finds the same content already
        // held reuses the damaged objects rather than writing them again.
        await using var runtime = await StartAsync(withSpare: false);
        WriteRandom("docs/ledger.txt", 1);
        await BackUpAsync(runtime);
        var older = DataKeys(Vault);
        WriteRandom("docs/ledger.txt", 2);
        await BackUpAsync(runtime);
        Tamper(Vault, older);
        await VerifyAsync(runtime);

        WriteRandom("docs/ledger.txt", 1);
        await BackUpAsync(runtime);

        var listed = await ListAsync(runtime);
        Assert.HasCount(3, listed);
        CollectionAssert.AreEqual(
            new[] { "vault: degraded", "vault: degraded" },
            new[] { listed[0], listed[2] }.Select(snapshot => snapshot.Destinations!.Single()).ToList(),
            "both snapshots that hold the first content need what is damaged");
        Assert.AreNotEqual("vault: degraded", listed[1].Destinations!.Single());
    }

    [TestMethod]
    public async Task ASyncWhoseCopyAnswersWhileTheDamageStands_NarrowsAFailureOfAnotherKindThatStoodBefore()
    {
        // A failure of another kind makes the whole copy suspect, and a
        // damage finding over it does not narrow it. A sync that copies
        // everything and still finds the damage standing has shown the rest
        // of the copy answers: from then on, only what needs the damaged
        // objects is degraded there.
        await using var runtime = await StartAsync(withSpare: false);
        var set = runtime.Configuration.BackupSets.Single();
        WriteRandom("docs/ledger.txt", 1);
        await BackUpAsync(runtime);
        var older = DataKeys(Vault);
        WriteRandom("docs/ledger.txt", 2);
        await BackUpAsync(runtime);
        Tamper(Vault, older);
        await VerifyAsync(runtime);
        runtime.DestinationSync.RecordFailure(
            set.Id, "vault", DestinationSyncState.Failed, "the copy broke off",
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var (_, whileSuspect) = await BothSnapshotsAsync(runtime);
        Assert.AreEqual("vault: degraded", Assert.ContainsSingle(whileSuspect.Destinations!), "until a sync answers");

        var sync = FanOut.Enqueue(runtime, set, "vault", DateTimeOffset.Now, userInitiated: true);
        Assert.IsNotNull(sync, "nothing else was syncing");
        await sync.WaitAsync(Timeout);

        var row = Row(runtime, "vault");
        Assert.AreEqual(DestinationSyncState.Failed, row.State, "the damage still stands");
        var (_, second) = await BothSnapshotsAsync(runtime);
        Assert.AreNotEqual("vault: degraded", Assert.ContainsSingle(second.Destinations!), row.LastError);
    }

    [TestMethod]
    public async Task DamageTheServiceCannotTraceToASnapshot_DegradesEverySnapshotThere()
    {
        // A blob no snapshot this installation lists is known to need could
        // be garbage, or what another writer's snapshot needs. Not knowing is
        // not evidence that nothing does.
        await using var runtime = await StartAsync(withSpare: false);
        WriteRandom("docs/ledger.txt", 1);
        await BackUpAsync(runtime);
        WriteRandom("docs/ledger.txt", 2);
        await BackUpAsync(runtime);

        var stray = Path.Combine(ReplicaRoot(Vault), "blobs", "data", "zzzz", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        await File.WriteAllBytesAsync(stray, new byte[4096], Timeout);
        var verified = await VerifyAsync(runtime);
        Assert.IsTrue(verified.Damaged > 0, string.Join(" | ", verified.Lines));

        var (first, second) = await BothSnapshotsAsync(runtime);
        Assert.AreEqual("vault: degraded", Assert.ContainsSingle(first.Destinations!));
        Assert.AreEqual("vault: degraded", Assert.ContainsSingle(second.Destinations!));
        Assert.Contains("cannot trace", Notice(runtime).Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AFindingEveryObjectOfWhichWasReplaced_LeavesNoSnapshotDegradedThere()
    {
        // Replaced from a sound copy and re-verified where it landed, the
        // destination is whole again: the finding stands for the device, and
        // the snapshots it holds are restorable.
        await using var runtime = await StartAsync(withSpare: true);
        WriteRandom("docs/ledger.txt", 1);
        await BackUpAsync(runtime);
        WriteRandom("docs/ledger.txt", 2);
        await BackUpAsync(runtime);

        Tamper(Vault, DataKeys(Vault));
        var verified = await VerifyAsync(runtime);
        Assert.IsTrue(verified.Damaged > 0, string.Join(" | ", verified.Lines));
        Assert.IsNull(Row(runtime, "vault").DamagedKeys, "every damaged object was replaced from the sibling");

        var (first, second) = await BothSnapshotsAsync(runtime);
        CollectionAssert.DoesNotContain(first.Destinations!.ToList(), "vault: degraded");
        CollectionAssert.DoesNotContain(second.Destinations!.ToList(), "vault: degraded");
    }

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    private static DestinationSyncRecord Row(ServiceRuntime runtime, string destination) =>
        runtime.DestinationSync.Find(runtime.Configuration.BackupSets.Single().Id, destination)
        ?? throw new AssertFailedException($"'{destination}' has no ledger row");

    private static Notice Notice(ServiceRuntime runtime) =>
        runtime.Notices.Unacknowledged.SingleOrDefault(notice =>
            notice.Key == $"deep-verify-failed:{runtime.Configuration.BackupSets.Single().Id}:vault")
        ?? throw new AssertFailedException(
            "no deep-verification notice stands for 'vault': "
            + string.Join(" | ", runtime.Notices.Unacknowledged.Select(notice => notice.Key)));

    private async Task<VerifyDestinationResult> VerifyAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var result = await handler.ExecuteAsync(new VerifyDestinationCommand("docs", "vault", Full: true), Timeout);
        Assert.IsInstanceOfType<VerifyDestinationResult>(result, out var verified, (result as ServiceError)?.Message);
        return verified;
    }

    /// <summary>The set's snapshots, oldest capture first.</summary>
    private async Task<IReadOnlyList<SnapshotDescriptor>> ListAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var result = await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout);
        Assert.IsInstanceOfType<SnapshotsResult>(result, out var listed, (result as ServiceError)?.Message);
        return [.. listed.Snapshots.OrderBy(snapshot => snapshot.CapturedAt)];
    }

    private async Task<(SnapshotDescriptor First, SnapshotDescriptor Second)> BothSnapshotsAsync(ServiceRuntime runtime)
    {
        var listed = await ListAsync(runtime);
        Assert.HasCount(2, listed);
        return (listed[0], listed[1]);
    }

    private async Task BackUpAsync(ServiceRuntime runtime)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);

        // Captures a millisecond apart would tie on the time the list is
        // ordered by.
        await Task.Delay(5, Timeout);
    }

    /// <summary>The one repository directory a destination path holds.</summary>
    private static string ReplicaRoot(string destinationPath) =>
        Assert.ContainsSingle(Directory.GetDirectories(destinationPath));

    /// <summary>The data blob keys a destination's replica holds now.</summary>
    private static List<string> DataKeys(string destinationPath)
    {
        var root = ReplicaRoot(destinationPath);
        return [.. Directory.GetFiles(Path.Combine(root, "blobs", "data"), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))];
    }

    /// <summary>Flips every byte of each blob past its first two hundred: the envelope survives, its records do not.</summary>
    private static void Tamper(string destinationPath, IEnumerable<string> keys)
    {
        var root = ReplicaRoot(destinationPath);
        foreach (var key in keys)
        {
            var path = Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));
            var bytes = File.ReadAllBytes(path);
            for (var i = 200; i < bytes.Length; i++)
            {
                bytes[i] ^= 0xFF;
            }

            File.WriteAllBytes(path, bytes);
        }
    }

    private void WriteRandom(string relative, int seed)
    {
        var bytes = new byte[60_000];
        new Random(seed).NextBytes(bytes);
        _harness.WriteSourceFile(relative, Convert.ToBase64String(bytes));
    }

    private async Task<ServiceRuntime> StartAsync(bool withSpare)
    {
        Directory.CreateDirectory(Vault);
        var destinations = new List<DestinationConfiguration>
        {
            new() { Id = new string('1', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault, Priority = 10 },
        };
        var references = new List<SetDestinationReference> { new() { Ref = "vault" } };
        if (withSpare)
        {
            Directory.CreateDirectory(Spare);
            destinations.Add(new() { Id = new string('2', 32), Name = "spare", Kind = DestinationKind.LocalPath, Path = Spare, Priority = 1 });
            references.Add(new() { Ref = "spare" });
        }

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = destinations,
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Destinations = references,
                    DirectShip = true,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

        await _harness.SetupAsync();
        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                // The placement condition (ADR-0051) judges by volume, and the
                // fixture's every path shares one real volume.
                VolumeIdentityOverride = path =>
                    path.Contains("vault", StringComparison.Ordinal) ? 2UL
                    : path.Contains("spare", StringComparison.Ordinal) ? 3UL
                    : 1UL,
            },
            Timeout);
    }
}
