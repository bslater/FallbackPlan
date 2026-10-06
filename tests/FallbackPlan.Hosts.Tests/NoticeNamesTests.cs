using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A notice names a backup's files only to a caller who proved the set's
/// passphrase (FR-WOR-007, ADR-0089 Amendment 1). A drill that could not bring
/// back a sampled file says so in counts — in its notice, on the pair's row,
/// on the status matrix and in a person's drill answer — and the file it was
/// about is kept beside the notice, answered by <c>notice_names</c> through a
/// source the set's passphrase unlocked and refused without one. A notice that
/// names no files answers none, and asks for nothing.
/// </summary>
/// <remarks>
/// A write-only set, the only shape setup produces, with one file, so the
/// drill's sample is that file.
/// </remarks>
[TestClass]
public sealed class NoticeNamesTests : IDisposable
{
    private const string FileName = "content.txt";

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(5));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task ADrillThatCouldNotRestoreAFile_SaysSoInCounts_AndNamesTheFileOnlyBehindThePassphrase()
    {
        await using var runtime = await StartDrilledAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        TamperEveryDataBlob(Assert.ContainsSingle(Directory.GetDirectories(Vault)));

        Assert.IsInstanceOfType<DrillResult>(
            await handler.ExecuteAsync(new RunDrillCommand("docs", "vault"), Timeout), out var drilled);
        Assert.AreEqual(1, drilled.Failed, string.Join(" | ", drilled.Lines));

        // Nowhere a signed-in account reads without the passphrase: the
        // answer, the pair's row, the status matrix and the notice.
        Assert.DoesNotContain(FileName, Assert.ContainsSingle(drilled.Lines), StringComparison.Ordinal);
        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "vault")!;
        Assert.IsNotNull(record.DrillFailure);
        Assert.DoesNotContain(FileName, record.DrillFailure, StringComparison.Ordinal);
        Assert.IsInstanceOfType<StatusResult>(await handler.ExecuteAsync(new GetStatusCommand(), Timeout), out var status);
        var row = Assert.ContainsSingle(Assert.ContainsSingle(status.Sets).Destinations);
        Assert.DoesNotContain(FileName, row.DrillFailure!, StringComparison.Ordinal);

        Assert.IsInstanceOfType<NoticesResult>(await handler.ExecuteAsync(new ListNoticesCommand(), Timeout), out var listed);
        var notice = Assert.ContainsSingle(listed.Notices.Where(n => n.Key.StartsWith("drill-failed:", StringComparison.Ordinal)));
        Assert.DoesNotContain(FileName, notice.Message, StringComparison.Ordinal);
        Assert.AreEqual(_harness.DocsSetId, notice.SetId, "the set whose passphrase names the file");
        Assert.AreEqual(1, notice.NamesWithheld);

        // Without a source the set's passphrase unlocked, refused by name.
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new NoticeNamesCommand(notice.Id), Timeout), out var refused);
        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason, refused.Message);
        Assert.Contains("passphrase", refused.Message, StringComparison.Ordinal);

        // With one, the file the drill could not bring back.
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, Timeout);
        Assert.IsInstanceOfType<NoticeNamesResult>(
            await handler.ExecuteAsync(new NoticeNamesCommand(notice.Id, source.SourceId), Timeout), out var named);
        Assert.EndsWith(FileName, Assert.ContainsSingle(named.Names), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ANoticeThatNamesNoFiles_AnswersNone_WithoutAskingForThePassphrase()
    {
        await using var runtime = await StartDrilledAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var raised = runtime.Notices.Raise(
            "quota-low:friend", "the peer has 3 GiB left", (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.IsInstanceOfType<NoticesResult>(await handler.ExecuteAsync(new ListNoticesCommand(), Timeout), out var listed);
        var notice = Assert.ContainsSingle(listed.Notices.Where(n => n.Id == raised.Id));
        Assert.AreEqual(0, notice.NamesWithheld);
        Assert.IsNull(notice.SetId);

        Assert.IsInstanceOfType<NoticeNamesResult>(
            await handler.ExecuteAsync(new NoticeNamesCommand(raised.Id), Timeout), out var named);
        Assert.IsEmpty(named.Names);
    }

    [TestMethod]
    public async Task AnUnknownNotice_IsNotFound()
    {
        await using var runtime = await StartDrilledAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new NoticeNamesCommand("nonesuch"), Timeout), out var missing);
        Assert.AreEqual(ServiceErrorReason.NotFound, missing.Reason);
    }

    private static void TamperEveryDataBlob(string replicaRoot)
    {
        var files = Directory.GetFiles(
            Path.Combine(replicaRoot, "blobs", "data"), "*", SearchOption.AllDirectories);
        Assert.IsNotEmpty(files, "the destination must hold data blobs for this to test anything");

        foreach (var path in files)
        {
            var bytes = File.ReadAllBytes(path);
            for (var i = 200; i < bytes.Length; i++)
            {
                bytes[i] ^= 0xFF;
            }

            File.WriteAllBytes(path, bytes);
        }
    }

    /// <summary>A set-up installation whose one pair has converged and passed a clean scheduled drill.</summary>
    private async Task<ServiceRuntime> StartDrilledAsync()
    {
        Directory.CreateDirectory(Vault);
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('d', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
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
                    DirectShip = true,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));
        _harness.WriteSourceFile($"docs/{FileName}", new string('c', 90_000) + "the bytes a restore needs");

        await _harness.SetupAsync();
        var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            Timeout);
        try
        {
            var first = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
            await first.Transfers.WaitAsync(Timeout);
            await first.Drills.WaitAsync(Timeout);
            var drilled = runtime.DestinationSync.Find(_harness.DocsSetId, "vault");
            Assert.IsNull(drilled?.DrillFailure, $"the clean drill must pass first: {drilled?.DrillFailure}");
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync();
            throw;
        }
    }
}
