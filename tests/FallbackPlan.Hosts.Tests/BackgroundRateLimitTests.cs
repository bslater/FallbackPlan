using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The disk and network limits of NFR-PERF-013 in a running service: a
/// destination's <c>transfer_limit</c> paces every background byte to or from
/// it — the fan-out's copy, a direct-ship capture's writes, the deep sweep's
/// reads and a drill's — and <c>background_read_limit</c> paces what a
/// background capture reads from the source. A person is never paced.
/// </summary>
/// <remarks>
/// <para>
/// "Background" is the predicate the window already governs by: exactly what
/// the scheduler starts with <c>userInitiated: false</c>. So each seam has its
/// person case beside it, and each person case is also the control that
/// proves its fixture had bytes to pace.
/// </para>
/// <para>
/// The runtime paces on a virtual clock (<see cref="ServiceOptions.PacingClock"/>)
/// whose waits complete at once and are summed, so a rate is proved by the
/// waits it asked for rather than by minutes of wall time. The clock is the
/// runtime's own, set before it starts, so nothing here shares process state
/// and the class runs concurrently with the rest of the suite.
/// </para>
/// <para>
/// Does not establish FR-DRL-002 or FR-VER-004 — the drill and the sweep are
/// observed here as background readers of a destination and nothing about
/// what they prove is tested.
/// </para>
/// </remarks>
[TestClass]
public sealed class BackgroundRateLimitTests : IDisposable
{
    private const string Limit = "64 KiB/s";
    private const long LimitBytesPerSecond = 64 * 1024;

    private readonly HostHarness _harness = new();
    private readonly VirtualPacing _pacing = new();

    // A hang guard, not a performance claim: the virtual clock makes every
    // paced wait instant, and what remains is real captures of small trees.
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(5));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task AScheduledSync_ToALimitedDestination_IsPaced_AndStillCompletes()
    {
        await using var runtime = await StartAsync(transferLimit: Limit);

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);

        Assert.IsNotNull(
            runtime.DestinationSync.Find(_harness.DocsSetId, "vault")?.LastSuccessAt, "a paced sync still finishes");

        var limiter = runtime.Pacing.ForDestination(VaultIn(runtime));
        Assert.IsNotNull(limiter);
        var blobs = BlobBytes();
        Assert.IsGreaterThanOrEqualTo(blobs, limiter.BytesPaced, "every blob byte the replica holds passed the limit");
        AssertPacedFor(blobs);
    }

    [TestMethod]
    public async Task APersonsSync_ToTheSameDestination_IsNotPaced()
    {
        await using var runtime = await StartAsync(transferLimit: Limit);

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout, userInitiated: true);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);

        // The control first: the person's pass did move the bytes the
        // scheduled case paces, so "nothing was paced" is about the person
        // and not about an empty fixture.
        Assert.IsNotNull(runtime.DestinationSync.Find(_harness.DocsSetId, "vault")?.LastSuccessAt);
        Assert.IsGreaterThan(0L, BlobBytes());

        Assert.AreEqual(0L, runtime.Pacing.ForDestination(VaultIn(runtime))!.BytesPaced);
        Assert.AreEqual(TimeSpan.Zero, _pacing.Waited);
    }

    [TestMethod]
    public async Task AScheduledCapture_UnderTheReadLimit_IsPaced_AndAManualRunIsNot()
    {
        await using var runtime = await StartAsync(readLimit: Limit);

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
        Assert.AreEqual(1, pass.Ran);

        var reads = runtime.Pacing.ForSourceReads(runtime.Configuration);
        Assert.IsNotNull(reads);
        Assert.IsGreaterThanOrEqualTo(SourceBytes(), reads.BytesPaced, "every source byte was read through the limit");
        AssertPacedFor(SourceBytes());

        // A person's run of the same set, after a change, reads through no
        // limit at all.
        var paced = reads.BytesPaced;
        var waited = _pacing.Waited;
        WriteRandomSourceFile("docs/later.bin", 96 * 1024);
        var set = runtime.Configuration.BackupSets.Single();
        var manual = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", manual.Outcome, manual.Detail);

        Assert.AreEqual(paced, reads.BytesPaced);
        Assert.AreEqual(waited, _pacing.Waited);
    }

    [TestMethod]
    public async Task ADirectShipCapture_ToALimitedDestination_IsPaced()
    {
        // With no staging archive the capture's own writes are the transfer:
        // the ship sink writes each blob to the destination as it seals, so
        // that is where the limit has to bite.
        await using var runtime = await StartAsync(transferLimit: Limit, directShip: true);

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
        Assert.AreEqual(1, pass.Ran);

        var limiter = runtime.Pacing.ForDestination(VaultIn(runtime));
        Assert.IsNotNull(limiter);
        var blobs = BlobBytes();
        Assert.IsGreaterThan(0L, blobs, "the direct-ship run must have written blobs to the destination");
        Assert.IsGreaterThanOrEqualTo(blobs, limiter.BytesPaced);
        AssertPacedFor(blobs);
    }

    [TestMethod]
    public async Task ASweepOfALimitedDestination_IsPaced_AndAPersonsIsNot()
    {
        await using var runtime = await StartAsync(transferLimit: Limit);
        await ReachTheVaultAsAPersonAsync(runtime);
        var limiter = runtime.Pacing.ForDestination(VaultIn(runtime))!;
        var set = runtime.Configuration.BackupSets.Single();
        var nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var person = await ReplicaSweepJob.RunAsync(runtime, set, "vault", nowMs, userInitiated: true, Timeout);
        Assert.IsGreaterThan(0, person.Examined, "the control: the sweep had objects to read");
        Assert.AreEqual(0L, limiter.BytesPaced, "a person's deep verification is not paced");

        var scheduled = await ReplicaSweepJob.RunAsync(runtime, set, "vault", nowMs, userInitiated: false, Timeout);
        Assert.IsGreaterThan(0, scheduled.Examined);
        Assert.IsGreaterThan(0L, limiter.BytesPaced, "a scheduled sweep reads the replica through the limit");
    }

    [TestMethod]
    public async Task ADrillOfALimitedDestination_IsPaced_AndAPersonsIsNot()
    {
        await using var runtime = await StartAsync(transferLimit: Limit);
        await ReachTheVaultAsAPersonAsync(runtime);
        var limiter = runtime.Pacing.ForDestination(VaultIn(runtime))!;
        var set = runtime.Configuration.BackupSets.Single();
        var nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var before = limiter.BytesPaced;

        var person = await RecoveryDrillJob.RunAsync(runtime, set, "vault", nowMs, userInitiated: true, Timeout);
        Assert.IsNull(person.Failure, person.Failure);
        Assert.IsGreaterThan(0, person.Files, "the control: the drill had files to restore");
        Assert.AreEqual(before, limiter.BytesPaced, "a person's drill is not paced");

        var scheduled = await RecoveryDrillJob.RunAsync(runtime, set, "vault", nowMs, userInitiated: false, Timeout);
        Assert.IsNull(scheduled.Failure, scheduled.Failure);
        Assert.IsGreaterThan(before, limiter.BytesPaced, "a scheduled drill reads the replica through the limit");
    }

    [TestMethod]
    public async Task EveryJobUsingADestination_SharesItsOneLimiter_UntilTheLimitChanges()
    {
        // Two sets syncing to one peer get that peer's rate between them only
        // if they draw from one limiter; one built per job would give each the
        // full rate, and the link the operator capped would carry twice what
        // they wrote (the arithmetic is Application.Tests/ByteRateLimiterTests').
        await using var runtime = await StartAsync(transferLimit: Limit);

        var first = runtime.Pacing.ForDestination(VaultIn(runtime));
        Assert.IsNotNull(first);
        Assert.AreSame(first, runtime.Pacing.ForDestination(VaultIn(runtime)));

        // The limit is read afresh, as the window is: an operator who edits it
        // is not asked to restart the service for it to take.
        WriteConfiguration(transferLimit: "128 KiB/s");
        var changed = runtime.Pacing.ForDestination(VaultIn(runtime));
        Assert.IsNotNull(changed);
        Assert.AreNotSame(first, changed);
        Assert.AreEqual(128 * 1024L, changed.Rate.BytesPerSecond);

        WriteConfiguration(transferLimit: null);
        Assert.IsNull(runtime.Pacing.ForDestination(VaultIn(runtime)));
    }

    [TestMethod]
    public async Task AnInstallationWithNoLimits_PacesNothing()
    {
        // Every file written before schema 7 says "unlimited" by not
        // mentioning a limit. A regression here slows every installation at
        // once, which is why it is a case of its own.
        await using var runtime = await StartAsync();

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);

        Assert.AreEqual(1, pass.Ran);
        Assert.IsNotNull(runtime.DestinationSync.Find(_harness.DocsSetId, "vault")?.LastSuccessAt);
        Assert.IsNull(runtime.Pacing.ForDestination(VaultIn(runtime)));
        Assert.IsNull(runtime.Pacing.ForSourceReads(runtime.Configuration));
        Assert.AreEqual(TimeSpan.Zero, _pacing.Waited);
    }

    [TestMethod]
    public async Task TheStatus_CarriesTheLimitsInForce_AndNothingWhenThereAreNone()
    {
        await using var runtime = await StartAsync(transferLimit: "2 MiB/s", readLimit: "40 MiB/s");
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var limited = await handler.ExecuteAsync(new GetStatusCommand(), Timeout);
        Assert.IsInstanceOfType<StatusResult>(limited, out var status);
        var limits = status.BackgroundLimits;
        Assert.IsNotNull(limits);
        Assert.AreEqual("40 MiB/s", limits.ReadLimit?.Text);
        Assert.AreEqual(41_943_040L, limits.ReadLimit?.BytesPerSecond);
        var vault = Assert.ContainsSingle(limits.TransferLimits);
        Assert.AreEqual("vault", vault.DestinationName);
        Assert.AreEqual("2 MiB/s", vault.Text);
        Assert.AreEqual(2_097_152L, vault.BytesPerSecond);

        // Null, not an empty descriptor: the same thing a pre-1.43 service
        // says, and the instruction a client needs — draw no line.
        WriteConfiguration();
        var unlimited = await handler.ExecuteAsync(new GetStatusCommand(), Timeout);
        Assert.IsInstanceOfType<StatusResult>(unlimited, out var none);
        Assert.IsNull(none.BackgroundLimits);
    }

    private async Task ReachTheVaultAsAPersonAsync(ServiceRuntime runtime)
    {
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout, userInitiated: true);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
        Assert.IsNotNull(runtime.DestinationSync.Find(_harness.DocsSetId, "vault")?.LastSuccessAt);
        Assert.AreEqual(0L, runtime.Pacing.ForDestination(VaultIn(runtime))!.BytesPaced);
    }

    /// <summary>
    /// The virtual time the limiter asked to wait covers the bytes at the
    /// rate, less the one second's burst an idle limiter lets through.
    /// </summary>
    private void AssertPacedFor(long bytes)
    {
        var owed = TimeSpan.FromSeconds(((double)bytes / LimitBytesPerSecond) - 1.0);
        Assert.IsGreaterThan(TimeSpan.Zero, owed, "the fixture must move more than one second's worth");
        Assert.IsGreaterThanOrEqualTo(
            owed - TimeSpan.FromMilliseconds(100), _pacing.Waited, $"{bytes} bytes at {Limit} were not paced");
    }

    private static DestinationConfiguration VaultIn(ServiceRuntime runtime) =>
        runtime.Configuration.FindDestination("vault")
        ?? throw new AssertFailedException("the fixture declares a destination named vault");

    private long BlobBytes() =>
        Directory.Exists(Vault)
            ? Directory.GetDirectories(Vault)
                .Select(replica => Path.Combine(replica, "blobs"))
                .Where(Directory.Exists)
                .SelectMany(blobs => Directory.GetFiles(blobs, "*", SearchOption.AllDirectories))
                .Sum(path => new FileInfo(path).Length)
            : 0;

    private long SourceBytes() =>
        Directory.GetFiles(_harness.SourceRoot, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);

    private void WriteRandomSourceFile(string relativePath, int length)
    {
        // Random, so compression cannot shrink the replica below the bytes
        // the arithmetic is about.
        var path = _harness.WriteSourceFile(relativePath, string.Empty);
        var content = new byte[length];
        Random.Shared.NextBytes(content);
        File.WriteAllBytes(path, content);
    }

    private void WriteConfiguration(string? transferLimit = null, string? readLimit = null, bool directShip = false) =>
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            BackgroundReadLimit = readLimit,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('d', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
                    TransferLimit = transferLimit,
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

    private async Task<ServiceRuntime> StartAsync(
        string? transferLimit = null, string? readLimit = null, bool directShip = false)
    {
        Directory.CreateDirectory(Vault);
        WriteRandomSourceFile("docs/content.bin", 256 * 1024);
        _harness.WriteSourceFile("docs/notes.txt", "a file small enough to ride in the burst");
        WriteConfiguration(transferLimit, readLimit, directShip);

        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                PacingClock = _pacing.Clock,
                // The placement condition (ADR-0051) judges by volume, and the
                // fixture's every path shares one real volume — the vault is
                // told apart by name, the compliant install's shape.
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            Timeout);
    }

    /// <summary>
    /// A clock whose waits complete at once and advance virtual time by what
    /// was asked, summed, so a rate is read off the requested waits.
    /// </summary>
    private sealed class VirtualPacing
    {
        private readonly Lock _gate = new();
        private long _now;
        private long _waited;

        public VirtualPacing() =>
            Clock = new PacingClock(
                () =>
                {
                    lock (_gate)
                    {
                        return _now;
                    }
                },
                (wait, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        _now += wait.Ticks;
                        _waited += wait.Ticks;
                    }

                    return Task.CompletedTask;
                });

        public PacingClock Clock { get; }

        public TimeSpan Waited
        {
            get
            {
                lock (_gate)
                {
                    return TimeSpan.FromTicks(_waited);
                }
            }
        }
    }
}
