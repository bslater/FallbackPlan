using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Multi-root backup sets over the service (ADR-0040): an upsert that grows a
/// set past one root materialises labels and re-anchors the saved rules to
/// the new coordinates, the captured snapshot lists the labels at its top,
/// a vanished root refuses the run naming itself, and the preview verb
/// answers a draft that matches no saved set against an empty baseline.
/// </summary>
[TestClass]
public sealed class MultiRootSetTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    /// <summary>A second folder to capture, sibling to the harness source.</summary>
    private string MediaRoot => Path.Combine(_harness.WorkPath, "media");

    [TestMethod]
    public async Task UpsertBackupSet_GrowingToTwoRoots_MaterialisesLabelsAndReanchorsSavedRules()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "kept");
        _harness.WriteSourceFile("photos/beach.jpg", new string('p', 4_000));
        _harness.WriteSourceFile("photos/raw/big.raw", new string('r', 4_000));
        Directory.CreateDirectory(MediaRoot);
        File.WriteAllText(Path.Combine(MediaRoot, "clip.mp4"), new string('m', 4_000));
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        // The set first earns an anchored rule under single-root coordinates,
        // and a baseline to compare later listings against.
        Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 1h", [], ["photos/raw"], ["vault"])),
            _timeout.Token));
        await RunBackupAndWaitAsync(handler, runtime);

        // Growing to two roots. The client sends the rules as it knows them —
        // old coordinates — and the service re-anchors the saved ones.
        Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 1h", [], ["photos/raw"], ["vault"],
                Roots:
                [
                    new BackupRootDescriptor(_harness.SourceRoot),
                    new BackupRootDescriptor(MediaRoot),
                ])),
            _timeout.Token), out var answer);
        Assert.Contains(
            line => line.Contains($"root added: '{MediaRoot}' as 'media'", StringComparison.Ordinal),
            answer.Lines);
        Assert.Contains(line => line.Contains("re-anchored", StringComparison.Ordinal), answer.Lines);

        // Labels were materialised from the leaf names and persisted; the
        // saved exclude speaks the new label-prefixed coordinates.
        var saved = runtime.Configuration.FindSet("docs")!;
        Assert.AreEqual("source", saved.Roots[0].Label);
        Assert.AreEqual("media", saved.Roots[1].Label);
        Assert.AreEqual("source/photos/raw", Assert.ContainsSingle(saved.ExcludeRules));

        // The listing back-fills Root with the first root for pre-1.10
        // clients and carries the whole truth in Roots.
        Assert.IsInstanceOfType<BackupSetsResult>(
            await handler.ExecuteAsync(new ListBackupSetsCommand(), _timeout.Token), out var sets);
        var descriptor = Assert.ContainsSingle(sets.Sets);
        Assert.AreEqual(_harness.SourceRoot, descriptor.Root);
        Assert.AreEqual("media", descriptor.Roots?[1].Label);

        // The next backup captures both roots into one snapshot whose top
        // level is the labels — and the re-anchored exclude still prunes.
        await RunBackupAndWaitAsync(handler, runtime);
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);

        // Two snapshots stand: the single-root baseline (2 files) and the
        // multi-root capture (those plus the clip). Picked by shape — the
        // listing's order is the catalogue's, not capture order.
        Assert.AreEqual(2, snapshots.Snapshots.Count);
        var latest = snapshots.Snapshots.Single(snapshot => snapshot.Files == 3);

        Assert.IsInstanceOfType<DirectoryResult>(
            await handler.ExecuteAsync(new ListDirectoryCommand(latest.SnapshotId, null), _timeout.Token),
            out var top);
        Assert.AreEqual(
            "media, source",
            string.Join(", ", top.Entries.Select(entry => entry.Name)),
            "the labels are the snapshot's top level, in raw-byte order");
        Assert.IsTrue(top.Entries.All(entry => entry.Kind == "directory"));

        Assert.IsInstanceOfType<DirectoryResult>(
            await handler.ExecuteAsync(
                new ListDirectoryCommand(latest.SnapshotId, "source/photos"), _timeout.Token),
            out var photos);
        Assert.AreEqual("beach.jpg", Assert.ContainsSingle(photos.Entries).Name,
            "the re-anchored exclude kept pruning what it always pruned");
    }

    [TestMethod]
    public async Task RunBackup_AVanishedRoot_RefusesRecoverablyNamingIt()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "kept");
        var gone = Path.Combine(_harness.WorkPath, "unplugged");
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('d', 32),
                    Name = "vault",
                    Kind = DestinationKind.LocalPath,
                    Path = Path.Combine(_harness.StateDirectory, "vault"),
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots =
                    [
                        new BackupRootConfiguration { Path = _harness.SourceRoot, Label = "source" },
                        new BackupRootConfiguration { Path = gone, Label = "unplugged" },
                    ],
                    Schedule = "every 1h",
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        // Capturing without the unplugged root would make everything under
        // its label read "deleted" — the run refuses instead, recoverably:
        // the next pass retries after the drive comes back.
        var final = await RunBackupAndWaitAsync(
            handler, runtime, until: state => state == JobState.FailedRecoverable);
        Assert.AreEqual(JobState.FailedRecoverable, final.State);

        Assert.IsInstanceOfType<JobsResult>(
            await handler.ExecuteAsync(new ListJobsCommand(ActiveOnly: false), _timeout.Token), out var jobs);
        var detail = jobs.Jobs[^1].Detail;
        Assert.IsNotNull(detail);
        Assert.Contains(gone, detail, StringComparison.Ordinal);
        Assert.Contains("does not exist", detail, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task PreviewSetChanges_ADraftMatchingNoSavedSet_AnswersAgainstAnEmptyBaseline()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "kept");
        Directory.CreateDirectory(MediaRoot);
        File.WriteAllText(Path.Combine(MediaRoot, "clip.mp4"), new string('m', 4_000));
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        // The editor building a brand-new set has nothing saved to resolve;
        // draft roots make the question answerable anyway, against an empty
        // baseline, with label-prefixed coordinates.
        Assert.IsInstanceOfType<SetChangePreviewResult>(
            await handler.ExecuteAsync(
                new PreviewSetChangesCommand(
                    "brand-new",
                    Roots:
                    [
                        new BackupRootDescriptor(_harness.SourceRoot),
                        new BackupRootDescriptor(MediaRoot),
                    ]),
                _timeout.Token),
            out var draft);
        Assert.AreEqual("brand-new", draft.SetName);
        Assert.IsNull(draft.BaselineSnapshotId);
        Assert.AreEqual(2, draft.New.Count);
        Assert.Contains(path => path == "media/clip.mp4", draft.New.Sample);
        Assert.Contains(path => path == "source/notes.txt", draft.New.Sample);

        // Without draft roots the unknown set stays a stated miss.
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new PreviewSetChangesCommand("brand-new"), _timeout.Token),
            out var error);
        Assert.AreEqual(ServiceErrorReason.NotFound, error.Reason);

        // A draft root that is not there is a stated error, not a walk of
        // nothing that reads as "everything deleted".
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new PreviewSetChangesCommand(
                    "brand-new",
                    Roots: [new BackupRootDescriptor(Path.Combine(_harness.WorkPath, "absent"))]),
                _timeout.Token),
            out var missing);
        Assert.Contains("does not exist", missing.Message, StringComparison.Ordinal);
    }

    /// <summary>Commands a backup and waits for the state <paramref name="until"/> accepts (completion by default).</summary>
    private async Task<JobProgress> RunBackupAndWaitAsync(
        ServiceCommandHandler handler, ServiceRuntime runtime, Func<JobState, bool>? until = null)
    {
        until ??= state => state is JobState.Complete or JobState.CompletedWithFailures;
        JobProgress? final = null;

        var progress = runtime.Progress.WatchAsync(_timeout.Token);
        var watching = Task.Run(
            async () =>
            {
                await foreach (var observation in progress)
                {
                    if (until(observation.Progress.State))
                    {
                        final = observation.Progress;
                        return;
                    }
                }
            },
            _timeout.Token);

        Assert.IsInstanceOfType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand("docs", Full: false), _timeout.Token));

        await watching;
        Assert.IsNotNull(final);
        return final;
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
