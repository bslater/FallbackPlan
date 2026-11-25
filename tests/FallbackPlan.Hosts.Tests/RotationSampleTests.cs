using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// How many keys a pass's rotating sample asks about at a destination is that
/// destination's setting where it states one, and the service's default where
/// it does not (FR-VER-002, ADR-0035 Amendment 4). Each place a pass draws the
/// rotation reads it: a local path's sync, which a store's shares, a staging
/// set's push to a peer, and the read-back of a set whose content is only at
/// the peer. Watched through what the pass proved: a sample of one asks about
/// one key beside the new backup's one blob a pass, and a larger sample about
/// more.
/// </summary>
/// <remarks>
/// The new backup's proof is held to one blob a pass by the destination's own
/// share, so the count the pass proved is the rotation's plus one. The
/// default sample is narrowed to one where the destination's must stand over
/// it, and left as it is where the destination's is one.
/// </remarks>
[TestClass]
public sealed class RotationSampleTests : IAsyncDisposable
{
    private readonly HostHarness _harness = new();
    private readonly string _peersRoot = Path.Combine(Path.GetTempPath(), "fbp-rotation", Guid.NewGuid().ToString("n"));
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly DateTimeOffset _start = DateTimeOffset.Now;
    private LoopbackPeer? _friend;

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private LoopbackPeer Friend => _friend ?? throw new InvalidOperationException("no peer started");

    [TestMethod]
    public async Task ALocalPathsSampleOfOne_AsksAboutOneKeyBesideTheProof()
    {
        await using var runtime = await StartAtAPathAsync(sample: 1);

        await PassAsync(runtime);

        Assert.AreEqual(2, Row(runtime, "vault").VerifiedObjects, "the pass asked about more than the vault's sample and its proof");
    }

    [TestMethod]
    public async Task ALocalPathsLargerSample_AsksAboutMore_WhateverTheDefault()
    {
        FanOut.ReadBackBudget = 1;
        await using var runtime = await StartAtAPathAsync(sample: 16);

        await PassAsync(runtime);

        Assert.IsGreaterThan(2, Row(runtime, "vault").VerifiedObjects, "the pass asked only as far as the default");
    }

    [TestMethod]
    public async Task ALocalPathSayingNothing_AsksAsFarAsTheDefault()
    {
        FanOut.ReadBackBudget = 1;
        await using var runtime = await StartAtAPathAsync(sample: null);

        await PassAsync(runtime);

        Assert.AreEqual(2, Row(runtime, "vault").VerifiedObjects);
    }

    [TestMethod]
    public async Task AStagingSetsPeer_AsksAboutNoMoreThanItsSample()
    {
        await using var runtime = await StartAtAPeerAsync(directShip: false, sample: 1);

        await PassAsync(runtime);

        var row = Row(runtime, "friend");
        Assert.AreEqual(DestinationSyncState.InSync, row.State, row.LastError);
        Assert.IsLessThanOrEqualTo(2, row.VerifiedObjects, "the push asked about more than the peer's sample and its proof");
    }

    [TestMethod]
    public async Task AStagingSetsPeer_AsksAboutMore_WhereItsSampleIsLarger()
    {
        FanOut.ReadBackBudget = 1;
        await using var runtime = await StartAtAPeerAsync(directShip: false, sample: 16);

        await PassAsync(runtime);

        var row = Row(runtime, "friend");
        Assert.AreEqual(DestinationSyncState.InSync, row.State, row.LastError);
        Assert.IsGreaterThan(2, row.VerifiedObjects, "the push asked only as far as the default");
    }

    [TestMethod]
    public async Task APeerOnlySetsPeer_AsksAboutNoMoreThanItsSample()
    {
        await using var runtime = await StartAtAPeerAsync(directShip: true, sample: 1);

        await PassAsync(runtime);

        var row = Row(runtime, "friend");
        Assert.AreEqual(DestinationSyncState.InSync, row.State, row.LastError);
        Assert.IsLessThanOrEqualTo(2, row.VerifiedObjects, "the read-back asked about more than the peer's sample and its proof");
    }

    [TestMethod]
    public async Task APeerOnlySetsPeer_AsksAboutMore_WhereItsSampleIsLarger()
    {
        FanOut.ReadBackBudget = 1;
        await using var runtime = await StartAtAPeerAsync(directShip: true, sample: 16);

        await PassAsync(runtime);

        var row = Row(runtime, "friend");
        Assert.AreEqual(DestinationSyncState.InSync, row.State, row.LastError);
        Assert.IsGreaterThan(2, row.VerifiedObjects, "the read-back asked only as far as the default");
    }

    private async Task<ServiceRuntime> StartAtAPathAsync(int? sample)
    {
        Directory.CreateDirectory(Vault);
        await _harness.SetupAsync();
        Save(new DestinationConfiguration
        {
            Id = new string('2', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
            NewBackupProofShare = 1, RotationSample = sample,
        }, directShip: false);
        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            Timeout);
    }

    private async Task<ServiceRuntime> StartAtAPeerAsync(bool directShip, int sample)
    {
        await _harness.SetupAsync();
        _friend = LoopbackPeer.Start("friend", Path.Combine(_peersRoot, "friend"));
        PeerGrantStore.Open(_harness.StateDirectory).Pin(
            new PeerGrant(Friend.Identity, "friend", PeerRole.StoresForUs, PeerTerms.None, 1_722_600_000_000));
        Friend.Admit(_harness.StateDirectory);
        Save(new DestinationConfiguration
        {
            Id = new string('1', 32),
            Name = "friend",
            Kind = DestinationKind.Peer,
            Fingerprint = Friend.Fingerprint,
            Endpoint = $"{Friend.Endpoint.Address}:{Friend.Endpoint.Port}",
            NewBackupProofShare = 1,
            RotationSample = sample,
        }, directShip);
        return await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            Timeout);
    }

    private void Save(DestinationConfiguration destination, bool directShip)
    {
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [destination],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations = [new SetDestinationReference { Ref = destination.Name }],
                    DirectShip = directShip,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("docs/b.txt", new string('b', 40_000));
    }

    private async Task PassAsync(ServiceRuntime runtime)
    {
        var pass = await Scheduler.RunPassAsync(runtime, _start, Timeout);
        Assert.AreEqual(1, pass.Ran, "the pass ran no backup");
        await pass.Transfers.WaitAsync(Timeout);
    }

    private DestinationSyncRecord Row(ServiceRuntime runtime, string destination) =>
        runtime.DestinationSync.Find(_harness.DocsSetId, destination)
            ?? throw new AssertFailedException($"no ledger row for {destination}");

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_friend is not null)
        {
            await _friend.DisposeAsync();
        }

        _timeout.Dispose();
        _harness.Dispose();
        try
        {
            if (Directory.Exists(_peersRoot))
            {
                Directory.Delete(_peersRoot, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }
}
