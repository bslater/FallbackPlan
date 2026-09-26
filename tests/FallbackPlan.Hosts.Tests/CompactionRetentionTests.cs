using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Compaction as a phase of <c>retention --apply</c>
/// ([ADR-0067](../../docs/adr/0067-the-keyless-compactor.md); ADR-0025's
/// twelve criteria, run against the product rather than a fixture): the pass
/// rewrites what the collector's plan chose, the space comes back once the
/// next pass condemns the drained blobs and their grace runs, and the set is
/// a whole restorable repository throughout. Establishes FR-GC-011 and
/// FR-MAN-019.
/// </summary>
/// <remarks>
/// The direct-ship case is the one worth having. Its candidates are read
/// back through the ship sink from the destinations that hold them and the
/// blobs it produces are written out through the same sink, so a source
/// machine keeping no archive of its own still compacts — which is the shape
/// every new local-path set has.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class CompactionRetentionTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(5));

    private CancellationToken Timeout => _timeout.Token;

    private string VaultA => Path.Combine(_harness.WorkPath, "vault-a");

    /// <summary>
    /// Six mebibytes that do not compress, so a blob holding one dead copy
    /// of it is past the policy's four-mebibyte floor. Repeated bytes would
    /// leave a backlog of about a kilobyte and prove nothing.
    /// </summary>
    private string WriteIncompressible(string relativePath, int seed)
    {
        var bytes = new byte[6 * 1024 * 1024];
        new Random(seed).NextBytes(bytes);
        var full = Path.Combine(
            _harness.SourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task ADirectShipSet_CompactsThroughTheSink_AndTheSpaceComesBackAfterTheGrace()
    {
        Directory.CreateDirectory(VaultA);
        WriteConfiguration(directShip: true);

        // One file that never changes and one that changes every run: the
        // steady file's records keep an early blob alive while the churning
        // file's die in it, which is the whole shape of a compaction
        // candidate.
        WriteIncompressible("docs/steady.bin", seed: 1);
        WriteIncompressible("docs/churn.bin", seed: 2);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var grant = await _harness.ReclaimGrantAsync(handler, Timeout);

        await BackUpAsync(runtime);
        WriteIncompressible("docs/churn.bin", seed: 3);
        await BackUpAsync(runtime);
        WriteIncompressible("docs/churn.bin", seed: 4);
        await BackUpAsync(runtime);

        var replica = Assert.ContainsSingle(Directory.GetDirectories(VaultA));

        // Enough passes for the whole rhythm to play out: the ordinary
        // collection first, then the pass that compacts, then the pass that
        // condemns what it drained, then the grace and the sweep. The
        // replica's blob files are watched across the compacting pass
        // itself, because what this case is named for is the rewrite going
        // out through the sink rather than into a local archive the set does
        // not have.
        var lines = new List<string>();
        List<string>? beforeCompaction = null;
        List<string>? afterCompaction = null;

        for (var pass = 0; pass < 6; pass++)
        {
            var before = BlobObjects(replica);
            var passLines = await RetainAsync(handler, grant);
            lines.AddRange(passLines);

            if (beforeCompaction is null
                && passLines.Any(line => line.Contains("compacted:", StringComparison.Ordinal)))
            {
                beforeCompaction = before;
                afterCompaction = BlobObjects(replica);
            }

            WriteIncompressible("docs/churn.bin", seed: 10 + pass);
            await BackUpAsync(runtime);
        }

        var report = string.Join(Environment.NewLine, lines);
        Assert.IsNotNull(afterCompaction, $"no pass compacted anything:{Environment.NewLine}{report}");

        // The rewrite landed at the destination, put there by the same sink
        // its candidates were read back through — no archive was involved on
        // this machine at any point.
        Assert.IsNotEmpty(
            afterCompaction.Except(beforeCompaction!, StringComparer.Ordinal).ToList(),
            "the compacted blob never reached the destination");

        // And it was compacted ONCE. A blob whose records the index has moved
        // is condemned by the next pass and swept after its grace; a pass
        // that could not see the move would find the same live minority in
        // the same blob for ever and rewrite it again on every run, which is
        // the shape this assertion is really watching for.
        Assert.HasCount(
            1,
            lines.Where(line => line.Contains("compacted:", StringComparison.Ordinal)).ToList(),
            $"the same backlog was rewritten more than once:{Environment.NewLine}{report}");

        Assert.EndsWith(
            "0 blob(s), reclaiming 0 byte(s)",
            lines.Last(line => line.Contains("compaction would rewrite", StringComparison.Ordinal)),
            StringComparison.Ordinal);

        await AssertRestoresAsync(handler, "steady.bin", seed: 1);
    }

    [TestMethod]
    public async Task AStagingSet_Compacts_AndTheArchiveStaysRestorable()
    {
        Directory.CreateDirectory(VaultA);
        WriteConfiguration(directShip: false);
        WriteIncompressible("docs/steady.bin", seed: 1);
        WriteIncompressible("docs/churn.bin", seed: 2);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var grant = await _harness.ReclaimGrantAsync(handler, Timeout);

        await BackUpAsync(runtime);
        WriteIncompressible("docs/churn.bin", seed: 3);
        await BackUpAsync(runtime);
        WriteIncompressible("docs/churn.bin", seed: 4);
        await BackUpAsync(runtime);

        var lines = new List<string>();
        for (var pass = 0; pass < 5; pass++)
        {
            lines.AddRange(await RetainAsync(handler, grant));
            WriteIncompressible("docs/churn.bin", seed: 10 + pass);
            await BackUpAsync(runtime);
        }

        lines.AddRange(await RetainAsync(handler, grant));

        Assert.HasCount(
            1,
            lines.Where(line => line.Contains("compacted:", StringComparison.Ordinal)).ToList(),
            $"the backlog was rewritten none or more than once:{Environment.NewLine}"
            + string.Join(Environment.NewLine, lines));

        await AssertRestoresAsync(handler, "steady.bin", seed: 1);
    }

    /// <summary>
    /// The mandatory dry run (FR-GC-005) says what it would rewrite and
    /// rewrites nothing — through the service, where the <c>apply</c> guard
    /// on the phase actually lives.
    /// </summary>
    [TestMethod]
    public async Task ADryRunThroughTheService_SaysWhatItWouldRewrite_AndRewritesNothing()
    {
        Directory.CreateDirectory(VaultA);
        WriteConfiguration(directShip: true);
        WriteIncompressible("docs/steady.bin", seed: 1);
        WriteIncompressible("docs/churn.bin", seed: 2);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        await BackUpAsync(runtime);
        WriteIncompressible("docs/churn.bin", seed: 3);
        await BackUpAsync(runtime);
        WriteIncompressible("docs/churn.bin", seed: 4);
        await BackUpAsync(runtime);

        var replica = Assert.ContainsSingle(Directory.GetDirectories(VaultA));
        var before = BlobObjects(replica);

        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: false), Timeout), out var result);

        Assert.IsNotEmpty(
            result.Lines.Where(line =>
                line.Contains("compaction would rewrite: 1 blob(s)", StringComparison.Ordinal)).ToList(),
            $"the dry run did not say what it would rewrite:{Environment.NewLine}"
            + string.Join(Environment.NewLine, result.Lines));

        Assert.IsEmpty(result.Lines.Where(line => line.Contains("compacted:", StringComparison.Ordinal)).ToList());
        CollectionAssert.AreEquivalent(before, BlobObjects(replica), "a dry run wrote to the destination");
    }

    /// <summary>
    /// A direct-ship set whose destination is away compacts nothing and says
    /// nothing — the sink lists no blobs, so the plan has no backlog. It must
    /// not be an error: retention runs on a schedule and an unplugged drive
    /// is an ordinary Tuesday.
    /// </summary>
    [TestMethod]
    public async Task WithTheDestinationAway_ThePassPlansNothingAndDoesNotFail()
    {
        Directory.CreateDirectory(VaultA);
        WriteConfiguration(directShip: true);
        WriteIncompressible("docs/steady.bin", seed: 1);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var grant = await _harness.ReclaimGrantAsync(handler, Timeout);

        await BackUpAsync(runtime);
        Directory.Move(VaultA, VaultA + "-away");

        var lines = await RetainAsync(handler, grant);

        Assert.IsEmpty(lines.Where(line => line.Contains("compacted:", StringComparison.Ordinal)).ToList());
        Assert.IsEmpty(lines.Where(line => line.Contains("compaction did not run", StringComparison.Ordinal)).ToList());
    }

    private async Task<IReadOnlyList<string>> RetainAsync(ServiceCommandHandler handler, string grant)
    {
        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout),
            out var result);
        return result.Lines;
    }

    private async Task AssertRestoresAsync(ServiceCommandHandler handler, string file, int seed)
    {
        var expected = new byte[6 * 1024 * 1024];
        new Random(seed).NextBytes(expected);

        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        var newest = listed.Snapshots.OrderByDescending(snapshot => snapshot.CapturedAt).First();
        var output = Path.Combine(_harness.WorkPath, $"restored-{Guid.NewGuid():n}");

        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    newest.SnapshotId,
                    null,
                    output,
                    Source: (await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, Timeout)).SourceId),
                Timeout),
            out var restored);

        Assert.AreEqual("complete", restored.Outcome);
        var recovered = Assert.ContainsSingle(Directory.GetFiles(output, file, SearchOption.AllDirectories));
        var actual = await File.ReadAllBytesAsync(recovered, Timeout);
        Assert.IsTrue(
            expected.AsSpan().SequenceEqual(actual),
            $"{file} did not come back byte for byte after compaction");
    }

    private int _runs;

    private async Task BackUpAsync(ServiceRuntime runtime)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var when = DateTimeOffset.Now.AddMinutes(5 * ++_runs);
        var outcome = await Scheduler.Enqueue(runtime, set, when, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private static List<string> BlobObjects(string replica) =>
        Directory.Exists(Path.Combine(replica, "blobs"))
            ? [.. Directory.GetFiles(Path.Combine(replica, "blobs"), "*", SearchOption.AllDirectories)]
            : [];

    private void WriteConfiguration(bool directShip) => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32),
                Name = "vault-a",
                Kind = DestinationKind.LocalPath,
                Path = VaultA,
            },
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = _harness.DocsSetId,
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                Schedule = "every 4h",
                Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
                Destinations = [new SetDestinationReference { Ref = "vault-a" }],
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
            },
            Timeout);
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(25, cancellationToken);
        }
    }
}
