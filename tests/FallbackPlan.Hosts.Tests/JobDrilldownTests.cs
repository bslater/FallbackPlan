using System.Runtime.Versioning;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The completed-job drill-down (ADR-0050): a settled run can be asked what
/// it did. The summary rides the job row itself; the details — what changed
/// against the previous snapshot, and which files failed and why — are read
/// from the repository on demand, so they survive the sacrificial journal.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed partial class JobDrilldownTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    /// <summary>
    /// The exact diff of a run against its predecessor: counts are exact,
    /// samples are bounded by the ask, and the baseline is the same
    /// predecessor the snapshot browser's change badges use.
    /// </summary>
    [TestMethod]
    public async Task JobChanges_AgainstThePreviousSnapshot_CountsExactlyAndSamplesBounded()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        _harness.WriteSourceFile("keep.txt", "stays the same");
        var change = _harness.WriteSourceFile("change.txt", "first content");
        var gone = _harness.WriteSourceFile("gone.txt", "will be deleted");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var first = await RunBackupAsync(runtime, handler);

        File.WriteAllText(change, "second content, longer than the first");
        File.Delete(gone);
        _harness.WriteSourceFile("fresh.txt", "newly arrived");
        var second = await RunBackupAsync(runtime, handler);

        Assert.IsInstanceOfType<JobChangesResult>(
            await handler.ExecuteAsync(new JobChangesCommand(second.Id, SampleLimit: 1), _timeout.Token),
            out var changes);

        Assert.AreEqual(first.SnapshotId, changes.BaselineSnapshotId,
            "the baseline is the set's previous snapshot — the browser's own comparison");
        Assert.AreEqual(1L, changes.Unchanged);
        Assert.AreEqual(1L, changes.New.Count);
        Assert.AreEqual(1L, changes.Changed.Count);
        Assert.AreEqual(1L, changes.Removed.Count);
        Assert.Contains("fresh.txt", Assert.ContainsSingle(changes.New.Sample), StringComparison.Ordinal);
        Assert.Contains("gone.txt", Assert.ContainsSingle(changes.Removed.Sample), StringComparison.Ordinal);
        Assert.AreEqual(1, changes.SampleLimit, "the result echoes the applied cap");
    }

    [TestMethod]
    public async Task JobChanges_TheFirstBackup_ReadsEverythingAsNewWithNoBaseline()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        _harness.WriteSourceFile("one.txt", "1");
        _harness.WriteSourceFile("two.txt", "2");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var job = await RunBackupAsync(runtime, handler);

        Assert.IsInstanceOfType<JobChangesResult>(
            await handler.ExecuteAsync(new JobChangesCommand(job.Id), _timeout.Token),
            out var changes);

        Assert.IsNull(changes.BaselineSnapshotId, "a first backup has nothing to be compared against");
        Assert.AreEqual(0L, changes.Unchanged);
        Assert.AreEqual(2L, changes.New.Count);
        Assert.AreEqual(0L, changes.Removed.Count);
    }

    /// <summary>
    /// A failed or cancelled run committed nothing, so there is no snapshot
    /// to diff — a stated refusal, never an exception or an invented answer.
    /// </summary>
    [TestMethod]
    public async Task JobChanges_AJobThatCommittedNothing_IsRefusedByName()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartAsync();

        var set = runtime.Configuration.BackupSets.Single();
        var orphan = runtime.Jobs.Begin(set.Id, 1_000);
        runtime.Jobs.Transition(orphan.Id, JobState.Cancelled, 2_000, "cancelled by request");

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new JobChangesCommand(orphan.Id), _timeout.Token), out var error);
        Assert.Contains("snapshot", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task JobChanges_ASnapshotNoArchiveKnows_IsRefusedNotThrown()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartAsync();

        var set = runtime.Configuration.BackupSets.Single();
        var planted = runtime.Jobs.Begin(set.Id, 1_000);
        runtime.Jobs.Transition(planted.Id, JobState.Complete, 2_000, snapshotId: new string('f', 64));

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new JobChangesCommand(planted.Id), _timeout.Token), out var error);
        Assert.AreEqual(ServiceErrorReason.NotFound, error.Reason);
    }

    /// <summary>
    /// The failure listing, end to end: a run refusing a name the repository
    /// cannot faithfully encode (reason 8) writes a real error manifest, and
    /// job_failures reads it back with the path, the typed reason and the
    /// scanner's own words. This is also the round-trip that used to throw —
    /// the decoder rejected reason 8 as unassigned while the encoder wrote
    /// it, so the first snapshot to carry one was unreadable at exactly the
    /// moment somebody asked what failed.
    /// </summary>
    [TestMethod]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task JobFailures_APartialRun_ListsPathReasonAndDetail()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        _harness.WriteSourceFile("readable.txt", "this one is fine");

        var raw = RawPath(_harness.SourceRoot, [(byte)'b', (byte)'a', (byte)'d', 0xFF, 0xFE]);
        Assert.IsTrue(CreateFileWithRawName(raw), "could not create a file with a non-UTF-8 name");
        try
        {
            await using var runtime = await StartAsync();
            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            var job = await RunBackupAsync(runtime, handler);

            Assert.AreEqual(JobState.CompletedWithFailures, job.State);

            Assert.IsInstanceOfType<JobFailuresResult>(
                await handler.ExecuteAsync(new JobFailuresCommand(job.Id), _timeout.Token),
                out var failures);

            Assert.AreEqual(1L, failures.Failures);
            var failure = Assert.ContainsSingle(failures.Sample);
            Assert.AreEqual("name-not-representable", failure.Reason);
            Assert.Contains("bad", failure.Path, StringComparison.Ordinal);
            Assert.IsFalse(string.IsNullOrWhiteSpace(failure.Detail), "the scanner's own words travel with the reason");
        }
        finally
        {
            NativeUnlink(raw);
        }
    }

    [TestMethod]
    public async Task JobFailures_ACleanRun_AnswersZeroWithoutError()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        _harness.WriteSourceFile("fine.txt", "all good");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var job = await RunBackupAsync(runtime, handler);

        Assert.IsInstanceOfType<JobFailuresResult>(
            await handler.ExecuteAsync(new JobFailuresCommand(job.Id), _timeout.Token),
            out var failures);

        Assert.AreEqual(0L, failures.Failures);
        Assert.IsEmpty(failures.Sample);
    }

    private async Task<JobDescriptor> RunBackupAsync(ServiceRuntime runtime, ServiceCommandHandler handler)
    {
        Assert.IsInstanceOfType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand(null, Full: false), _timeout.Token),
            out var accepted);

        while (!runtime.Jobs.Jobs.Any(job =>
            job.Id == accepted.JobId && JobStateStore.HasSettled(job.State)))
        {
            await Task.Delay(25, _timeout.Token);
        }

        Assert.IsInstanceOfType<JobsResult>(
            await handler.ExecuteAsync(new ListJobsCommand(ActiveOnly: false), _timeout.Token), out var jobs);
        return jobs.Jobs.Single(job => job.Id == accepted.JobId);
    }

    /// <summary>A NUL-terminated path with raw (non-UTF-8) name bytes.</summary>
    private static byte[] RawPath(string directory, byte[] nameBytes)
    {
        var path = new byte[System.Text.Encoding.UTF8.GetByteCount(directory) + 1 + nameBytes.Length + 1];
        var written = System.Text.Encoding.UTF8.GetBytes(directory, path);
        path[written++] = (byte)'/';
        nameBytes.CopyTo(path, written);
        path[written + nameBytes.Length] = 0;
        return path;
    }

    /// <summary>Creates a file whose name the managed API cannot express.</summary>
    private static bool CreateFileWithRawName(byte[] path)
    {
        var handle = NativeOpen(path, 0x40 | 0x1 /* O_CREAT | O_WRONLY */, 0x1A4 /* 0644 */);
        if (handle < 0)
        {
            return false;
        }

        _ = NativeClose(handle);
        return true;
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "unlink", SetLastError = true)]
    private static partial int NativeUnlink(byte[] pathname);

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    private static partial int NativeOpen(byte[] pathname, int flags, uint mode);

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "close")]
    private static partial int NativeClose(int handle);

    /// <summary>
    /// The journal grows for the life of the installation and the frame codec
    /// caps a reply at 8 MiB, so the client may bound its ask. The newest rows
    /// are the ones a history view shows; the output order stays oldest-first,
    /// the documented order of the unbounded form.
    /// </summary>
    [TestMethod]
    public async Task ListJobs_WithLimit_ReturnsTheNewestRowsOldestFirst()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartAsync();

        var set = runtime.Configuration.BackupSets.Single();
        for (var i = 0; i < 5; i++)
        {
            var job = runtime.Jobs.Begin(set.Id, (ulong)(1_000 + i));
            runtime.Jobs.Transition(job.Id, JobState.Complete, (ulong)(2_000 + i));
        }

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<JobsResult>(
            await handler.ExecuteAsync(new ListJobsCommand(ActiveOnly: false, Limit: 2), _timeout.Token),
            out var bounded);

        Assert.HasCount(2, bounded.Jobs);
        Assert.AreEqual(2_003UL, bounded.Jobs[0].UpdatedAt, "the bound keeps the newest rows, not the oldest");
        Assert.IsTrue(
            bounded.Jobs[0].StartedAt < bounded.Jobs[1].StartedAt,
            "a bounded listing keeps the oldest-first order the unbounded one documents");

        Assert.IsInstanceOfType<JobsResult>(
            await handler.ExecuteAsync(new ListJobsCommand(ActiveOnly: false), _timeout.Token),
            out var unbounded);
        Assert.HasCount(5, unbounded.Jobs, "no limit still means everything — the pre-1.22 meaning");
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
