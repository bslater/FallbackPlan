using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The most of a new backup's blobs one pass proves at a destination is that
/// destination's setting where it states one, and the service's default where
/// it does not (FR-VER-002, ADR-0199 Amendment 13). A share smaller than the
/// backup leaves its proof under way after the pass that sent it, a share of
/// one having proven one blob; a share larger than the backup proves it in
/// that pass, whatever the default. Each place a pass proves a new backup
/// reads it: a local path's sync, which a store's shares, a staging set's
/// push to a peer, and the read-back of a set whose content is only at the
/// peer.
/// </summary>
/// <remarks>
/// The default is narrowed to one blob in the cases where the destination's
/// share must stand over it, so a pass that ignored the setting would leave
/// the backup unproven, and left as it is where the share is one, so a pass
/// that ignored it would prove the backup whole.
/// </remarks>
[TestClass]
public sealed class NewBackupProofShareTests : IAsyncDisposable
{
    private readonly HostHarness _harness = new();
    private readonly string _peersRoot = Path.Combine(Path.GetTempPath(), "fbp-proof-share", Guid.NewGuid().ToString("n"));
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly DateTimeOffset _start = DateTimeOffset.Now;
    private LoopbackPeer? _friend;

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private LoopbackPeer Friend => _friend ?? throw new InvalidOperationException("no peer started");

    [TestMethod]
    public async Task ALocalPathsShareOfOne_ProvesOneBlobAPass()
    {
        await using var runtime = await StartAtAPathAsync(share: 1);

        await PassAsync(runtime);

        var row = Row(runtime, "vault");
        Assert.IsNull(row.ProvenSnapshot, "the pass proved more of the backup than the vault's share");
        Assert.IsNotNull(row.ProvingSnapshot, "the pass that sent the backup began no proof of it");
        Assert.AreEqual(1, row.ProvingBlobsProven);
        Assert.IsGreaterThan(0, row.ProvingBlobsLeft ?? 0, "the premise: the backup needs more than one blob");
    }

    [TestMethod]
    public async Task ALocalPathsShareLargerThanTheBackup_ProvesItInOnePass_WhateverTheDefault()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAtAPathAsync(share: 256);

        await PassAsync(runtime);

        var row = Row(runtime, "vault");
        Assert.IsNotNull(row.ProvenSnapshot, "the pass proved the backup only as far as the default");
        Assert.IsNull(row.ProvingSnapshot);
    }

    [TestMethod]
    public async Task ALocalPathSayingNothing_ProvesAsFarAsTheDefault()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAtAPathAsync(share: null);

        await PassAsync(runtime);

        var row = Row(runtime, "vault");
        Assert.IsNull(row.ProvenSnapshot);
        Assert.AreEqual(1, row.ProvingBlobsProven);
    }

    [TestMethod]
    public async Task AStagingSetsPeer_ProvesNoMoreThanItsShareAPass()
    {
        await using var runtime = await StartAtAPeerAsync(directShip: false, share: 1);

        await PassAsync(runtime);

        var row = Row(runtime, "friend");
        Assert.AreEqual(DestinationSyncState.InSync, row.State, row.LastError);
        Assert.IsNull(row.ProvenSnapshot, "the push proved more of the backup than the peer's share");
        Assert.IsNotNull(row.ProvingSnapshot, "the push began no proof of the backup");
    }

    [TestMethod]
    public async Task AStagingSetsPeer_ProvesTheBackupInOnePass_WhereItsShareHoldsIt()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAtAPeerAsync(directShip: false, share: 256);

        await PassAsync(runtime);

        var row = Row(runtime, "friend");
        Assert.AreEqual(DestinationSyncState.InSync, row.State, row.LastError);
        Assert.IsNotNull(row.ProvenSnapshot, "the push proved the backup only as far as the default");
    }

    [TestMethod]
    public async Task APeerOnlySetsPeer_ProvesNoMoreThanItsShareAPass()
    {
        await using var runtime = await StartAtAPeerAsync(directShip: true, share: 1);

        await PassAsync(runtime);

        var row = Row(runtime, "friend");
        Assert.AreEqual(DestinationSyncState.InSync, row.State, row.LastError);
        Assert.IsNull(row.ProvenSnapshot, "the read-back proved more of the backup than the peer's share");
        Assert.IsNotNull(row.ProvingSnapshot, "the read-back began no proof of the backup");
    }

    [TestMethod]
    public async Task APeerOnlySetsPeer_ProvesTheBackupInOnePass_WhereItsShareHoldsIt()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAtAPeerAsync(directShip: true, share: 256);

        await PassAsync(runtime);

        var row = Row(runtime, "friend");
        Assert.AreEqual(DestinationSyncState.InSync, row.State, row.LastError);
        Assert.IsNotNull(row.ProvenSnapshot, "the read-back proved the backup only as far as the default");
    }

    private async Task<ServiceRuntime> StartAtAPathAsync(int? share)
    {
        Directory.CreateDirectory(Vault);
        await _harness.SetupAsync();
        Save(new DestinationConfiguration
        {
            Id = new string('2', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
            NewBackupProofShare = share,
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

    private async Task<ServiceRuntime> StartAtAPeerAsync(bool directShip, int share)
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
            NewBackupProofShare = share,
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
