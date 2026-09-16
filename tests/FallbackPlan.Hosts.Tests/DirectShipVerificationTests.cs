using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// What <c>verified</c> means for a direct-ship set, on both counts it failed
/// (FR-VER-001, FR-VER-002): that it happens at all, and that what happens
/// proves something.
/// </summary>
/// <remarks>
/// <para>
/// <b>That it happens.</b> Verification lived inside the copy path, and
/// <c>ShouldSync</c> queues a pair only when there is something to copy. A
/// direct-ship set writes straight to its destinations during capture, so
/// once converged there is never anything to copy — and the pair was
/// therefore never challenged again. FR-VER-002 says verification samples
/// "per interval"; the interval was "whenever a copy happened to be due".
/// </para>
/// <para>
/// <b>That it proves something.</b> The verifier compared the replica against
/// <c>archive.Store</c>, which for a direct-ship set is the ship sink — whose
/// blob reads are answered by the destinations themselves. With one
/// destination that is the replica against itself. The proof is now the AEAD
/// tag: a record read back from the destination opens under a key the
/// destination has never held, so rot cannot survive it and no second copy is
/// needed.
/// </para>
/// <para>
/// This does not establish FR-KIT-006 — nothing here restores anything.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class DirectShipVerificationTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task Sync_AConvergedDirectShipSet_IsStillChallenged()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/content.txt", new string('c', 80_000) + "the bytes a restore needs");

        await using var runtime = await StartAsync();

        var first = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        Assert.AreEqual(1, first.Ran);
        await first.Transfers.WaitAsync(Timeout);

        // Nothing is left to copy — the capture shipped straight there — so
        // under the old rule this pass queued nothing and the pair was never
        // challenged. A destination that has never been challenged is due one.
        var later = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddMinutes(30), Timeout);
        await later.Transfers.WaitAsync(Timeout);

        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "vault");
        Assert.IsNotNull(record);
        Assert.IsNotNull(
            record.VerifiedAt,
            $"a converged direct-ship destination must still earn a challenge: state={record.State} "
            + $"objects={record.VerifiedObjects} population={record.VerifiedPopulation} error={record.LastError}");
        Assert.IsGreaterThan(0, record.VerifiedObjects, "a stamp with nothing proved is worse than no stamp");
    }

    [TestMethod]
    public async Task Sync_ATamperedBlobAtTheOnlyDestination_FailsVerification()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/content.txt", new string('c', 80_000) + "the bytes a restore needs");

        await using var runtime = await StartAsync();

        var first = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        Assert.AreEqual(1, first.Ran);
        await first.Transfers.WaitAsync(Timeout);

        var clean = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddMinutes(30), Timeout);
        await clean.Transfers.WaitAsync(Timeout);

        var replicaRoot = Assert.ContainsSingle(Directory.GetDirectories(Vault));
        var verifiedBefore = runtime.DestinationSync.Find(_harness.DocsSetId, "vault")?.VerifiedAt;
        Assert.IsNotNull(verifiedBefore, "the clean pass must earn a stamp, or this proves nothing");

        // Length-preserving rot in the content plane, on the only copy there
        // is. A copy diff cannot see it — the key still lists at its full
        // length — and neither can a comparison whose other side is this same
        // replica. The AEAD tag can, because the destination never held the
        // key that computed it.
        TamperEveryDataBlob(replicaRoot);

        // Past the challenge interval, so this pass is due one.
        var after = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddHours(8), Timeout);
        await after.Transfers.WaitAsync(Timeout);

        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "vault");
        Assert.IsNotNull(record);
        Assert.AreEqual(
            DestinationSyncState.Failed, record.State,
            $"corrupted content must fail verification: verifiedAt={record.VerifiedAt} error={record.LastError}");
        Assert.Contains("verification failed", record.LastError!, StringComparison.Ordinal);
    }

    private static void TamperEveryDataBlob(string replicaRoot)
    {
        var files = Directory.GetFiles(
            Path.Combine(replicaRoot, "blobs", "data"), "*", SearchOption.AllDirectories);
        Assert.IsNotEmpty(files, "the destination must hold data blobs for this to test anything");

        foreach (var path in files)
        {
            var bytes = File.ReadAllBytes(path);

            // Past the envelope, so the blob still reads as a blob and the
            // damage is in the records a restore would need.
            for (var i = 200; i < bytes.Length; i++)
            {
                bytes[i] ^= 0xFF;
            }

            File.WriteAllBytes(path, bytes);
        }
    }

    private void WriteConfiguration() => new ClientConfiguration
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

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            passphrase: null,
            Timeout);
    }
}
