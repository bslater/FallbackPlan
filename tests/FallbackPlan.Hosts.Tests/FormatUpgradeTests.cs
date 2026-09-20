using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Lifecycle;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The format-upgrade record reaches the copies
/// (specification 11 §4; FR-REP-002, NFR-COMP-004). The descriptor cannot
/// carry an upgrade: <c>DestinationShipSink</c> seeds it only if absent, a
/// peer commits an object it lacks and keeps the one it has, and
/// <c>repository-format</c> may not be deleted by instruction — so a
/// rewritten descriptor would move the source alone and leave every copy
/// claiming the older format over newer blobs. An append-only record needs
/// none of that: it is an ordinary immutable object, which is the one thing
/// every copy path already moves.
/// </summary>
/// <remarks>Does not establish FR-DRL-001: nothing here recovers content to a person.</remarks>
[TestClass]
[DoNotParallelize]
public sealed class FormatUpgradeTests : IDisposable
{
    private static readonly byte[] WriterId = [.. Enumerable.Repeat((byte)23, 16)];

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private string MetadataRoot => Path.Combine(_harness.StateDirectory, "sets", _harness.DocsSetId);

    public void Dispose()
    {
        ServiceRuntime.ArchiveFormatVersion = FormatLimits.FormatVersion;
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task AnUpgradeRecord_WrittenAtTheSource_ReachesALocalPathDestinationOnTheNextOrdinaryPass()
    {
        // Created at format 2, which is where every repository written before
        // the creation default moved sits, and the only shape an upgrade has
        // anything to say about.
        ServiceRuntime.ArchiveFormatVersion = FormatVersions.SealedDataPlane;
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/report.txt", "the words worth keeping");

        await using (var runtime = await StartAsync())
        {
            var set = runtime.Configuration.BackupSets.Single();
            var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)
                .WaitAsync(Timeout);
            Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
        }

        var replica = Assert.ContainsSingle(Directory.GetDirectories(Vault));
        var upgradeKey = FormatUpgradeRecordCodec.KeyFor(FormatVersions.RelocatableRecords);
        Assert.IsFalse(
            File.Exists(Path.Combine(replica, upgradeKey.Replace('/', Path.DirectorySeparatorChar))),
            "a repository nobody upgraded carries no upgrade record");

        await WriteUpgradeAsync(FormatVersions.RelocatableRecords);

        // No protocol change, no new accept list, no seeding step: the next
        // ordinary reconciliation carries it because the copier's catch-all
        // phase claims every key no named phase does.
        await using (var runtime = await StartAsync())
        {
            var queued = FanOut.EnqueueAll(
                runtime, runtime.Configuration.BackupSets.Single(), DateTimeOffset.Now, userInitiated: true);
            await Task.WhenAll(queued).WaitAsync(Timeout);
        }

        var store = new LocalFileSystemObjectStore(replica);
        using var result = await store.OpenReadAsync(ObjectKey.Parse(upgradeKey), range: null, Timeout);
        Assert.AreEqual(OpenReadOutcome.Found, result.Outcome, "the upgrade record never reached the destination");

        using var memory = new MemoryStream();
        await result.Content!.CopyToAsync(memory, Timeout);
        var decoded = FormatUpgradeRecordCodec.Decode(memory.ToArray());
        Assert.AreEqual(FormatVersions.RelocatableRecords, decoded.Value.ToVersion);

        // And the copy is what a reader opening the destination alone would
        // act on: the replica's own effective version, read as any recovery
        // would read it, is the upgraded one.
        using var passphrase = Passphrase.Create(Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);
        var (repository, authority) = await RepositoryLifecycle.OpenForReadAsync(store, passphrase, Timeout);
        using (repository)
        using (authority)
        {
            Assert.AreEqual(
                FormatVersions.RelocatableRecords,
                await RepositoryLifecycle.ReadEffectiveFormatAsync(
                    store, repository.Descriptor, repository.Credential, Timeout));
        }
    }

    private async Task WriteUpgradeAsync(ushort toVersion)
    {
        var store = new LocalFileSystemObjectStore(MetadataRoot);
        using var passphrase = Passphrase.Create(Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);
        var (repository, authority) = await RepositoryLifecycle.OpenForReadAsync(store, passphrase, Timeout);
        using (repository)
        using (authority)
        {
            await RepositoryLifecycle.WriteFormatUpgradeAsync(
                store, repository.Descriptor, repository.Credential, toVersion, WriterId,
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Timeout);
        }
    }

    private void WriteConfiguration() => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
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
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            Timeout);
    }
}
