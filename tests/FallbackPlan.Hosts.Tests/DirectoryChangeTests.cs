using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The snapshot browser's change markers (ADR-0039): list_directory compares
/// each entry with the set's previous snapshot by recorded object identity —
/// new, changed, same — and names what the previous snapshot held that this
/// one does not, because deletion is absence between snapshots (FR-SNP-002)
/// and this is where the absence becomes visible to a person.
/// </summary>
[TestClass]
public sealed class DirectoryChangeTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task ListDirectory_AcrossThreeSnapshots_TellsWhatChangedWhere()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "the original notes");
        _harness.WriteSourceFile("c.txt", "doomed");
        _harness.WriteSourceFile("sub/b.txt", "nested content");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        // ---- Snapshot 1: a first snapshot has nothing to compare against,
        // and says so with nulls rather than pretending everything is new.
        await RunBackupAndWaitAsync(runtime, handler);
        var first = await LatestSnapshotIdAsync(handler);

        Assert.IsInstanceOfType<DirectoryResult>(
            await handler.ExecuteAsync(new ListDirectoryCommand(first, null), _timeout.Token), out var opening);
        Assert.IsNull(opening.PreviousSnapshotId);
        Assert.IsNull(opening.Deleted);
        Assert.IsTrue(opening.Entries.All(entry => entry.Change is null));
        Assert.IsNotNull(
            opening.Entries.Single(entry => entry.Name == "notes.txt").ModifiedAt,
            "file entries carry their recorded modification time");

        // ---- Snapshot 2: an edit, an addition, a deletion — and an
        // untouched subtree that must read as exactly that.
        _harness.WriteSourceFile("notes.txt", "the notes, edited");
        _harness.WriteSourceFile("d.txt", "added later");
        File.Delete(Path.Combine(_harness.SourceRoot, "c.txt"));
        await RunBackupAndWaitAsync(runtime, handler);
        var second = await LatestSnapshotIdAsync(handler);
        Assert.AreNotEqual(first, second);

        Assert.IsInstanceOfType<DirectoryResult>(
            await handler.ExecuteAsync(new ListDirectoryCommand(second, null), _timeout.Token), out var root);
        Assert.AreEqual(first, root.PreviousSnapshotId);
        Assert.AreEqual("changed", root.Entries.Single(entry => entry.Name == "notes.txt").Change);
        Assert.AreEqual("new", root.Entries.Single(entry => entry.Name == "d.txt").Change);
        Assert.IsNull(root.Entries.Single(entry => entry.Name == "sub").Change,
            "a folder makes no claim — its head mixes in access times the scan itself moves");
        Assert.AreEqual("c.txt", Assert.ContainsSingle(root.Deleted!));

        // ---- Snapshot 3: the deep edit is answered where the file lives —
        // the folder above stays silent, its own listing tells the story.
        _harness.WriteSourceFile("sub/b.txt", "nested content, edited");
        await RunBackupAndWaitAsync(runtime, handler);
        var third = await LatestSnapshotIdAsync(handler);

        Assert.IsInstanceOfType<DirectoryResult>(
            await handler.ExecuteAsync(new ListDirectoryCommand(third, null), _timeout.Token), out var atRoot);
        Assert.AreEqual(second, atRoot.PreviousSnapshotId);
        Assert.IsNull(atRoot.Entries.Single(entry => entry.Name == "sub").Change);
        Assert.AreEqual("same", atRoot.Entries.Single(entry => entry.Name == "notes.txt").Change);
        Assert.IsEmpty(atRoot.Deleted!);

        Assert.IsInstanceOfType<DirectoryResult>(
            await handler.ExecuteAsync(new ListDirectoryCommand(third, "sub"), _timeout.Token), out var inside);
        Assert.AreEqual("changed", Assert.ContainsSingle(inside.Entries).Change);
    }

    /// <summary>The newest snapshot's hex id, from the same surface a console reads.</summary>
    private async Task<string> LatestSnapshotIdAsync(ServiceCommandHandler handler)
    {
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);
        return snapshots.Snapshots[0].SnapshotId;
    }

    /// <summary>Commands a backup of "docs" and waits for the job to complete.</summary>
    private async Task RunBackupAndWaitAsync(ServiceRuntime runtime, ServiceCommandHandler handler)
    {
        var progress = runtime.Progress.WatchAsync(_timeout.Token);
        var watching = Task.Run(
            async () =>
            {
                await foreach (var observation in progress)
                {
                    if (observation.Progress.State is JobState.Complete or JobState.CompletedWithFailures)
                    {
                        return;
                    }
                }
            },
            _timeout.Token);

        Assert.IsInstanceOfType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand("docs", Full: false), _timeout.Token));
        await watching;
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            passphrase,
            _timeout.Token);
    }
}
