using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// Collection in a repository a newer build upgraded past the newest format
/// this build writes (NFR-COMP-003; specification 11 §5.3; ADR-0066
/// Amendment 1). Which objects are live is read off an index whose newest
/// format this build has never seen, so nothing is condemned, tombstoned or
/// converged away on that reading: an apply and a person's deletion are
/// refused naming both versions, a destination's keep-set is refused for a
/// whole copy, and a dry run, which writes nothing, still reports.
/// </summary>
[TestClass]
public sealed class NewerFormatRetentionTests : IDisposable
{
    private const string PassphraseText = "collected only by a build that knows";

    private static readonly WriterId Writer = WriterId.FromBytes([.. Enumerable.Repeat((byte)0x5a, 16)]);

    private static readonly RetentionConfiguration Narrow = new() { KeepDaily = 1, MinGenerations = 1 };

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-newer-format-retention", Guid.NewGuid().ToString("n"));

    private static string Newer => $"format {NewerBuildUpgrade.UnknownVersion}";

    [TestMethod]
    public async Task Apply_InARepositoryANewerBuildUpgraded_IsRefusedNamingBothVersions()
    {
        var (store, repository, authority) = await UpgradedPastThisBuildAsync("apply");
        using var _ = repository;
        using var __ = authority;

        var refused = await Assert.ThrowsExactlyAsync<NewerFormatException>(
            async () => await RunAsync(store, repository, apply: true));
        Assert.Contains(Newer, refused.Message, StringComparison.Ordinal);
        Assert.Contains($"format {FormatLimits.LatestFormatVersion}", refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task DryRun_InARepositoryANewerBuildUpgraded_StillReports()
    {
        var (store, repository, authority) = await UpgradedPastThisBuildAsync("dry-run");
        using var _ = repository;
        using var __ = authority;

        Assert.IsNotNull(await RunAsync(store, repository, apply: false));
    }

    [TestMethod]
    public async Task DeletionRequest_InARepositoryANewerBuildUpgraded_IsRefusedNamingBothVersions()
    {
        var (store, repository, authority) = await UpgradedPastThisBuildAsync("deletion");
        using var _ = repository;
        using var __ = authority;

        var refused = await Assert.ThrowsExactlyAsync<NewerFormatException>(
            async () => await SnapshotDeletion.RequestAsync(
                store,
                repository,
                Writer,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(_root, "deletion-sequence.txt"))),
                [],
                "ben",
                1_800_000_100_000,
                CancellationToken.None));
        Assert.Contains(Newer, refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Convergence_OfARepositoryANewerBuildUpgraded_IsRefusedForAWholeCopy()
    {
        var (store, repository, authority) = await UpgradedPastThisBuildAsync("convergence");
        using var _ = repository;
        using var __ = authority;

        var plan = await DestinationConvergence.ComputeKeepsAsync(
            store, repository, Narrow, DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_100_000), CancellationToken.None);

        Assert.IsNull(plan.Keeps, "a keep-set judged under a format this build has never seen would be executed as deletions");
        Assert.AreEqual(ConvergenceRefusal.NewerFormat, plan.Refusal);
    }

    private static ValueTask<RetentionReport> RunAsync(
        LocalFileSystemObjectStore store, OpenedRepository repository, bool apply) =>
        RetentionRunner.RunAsync(
            store,
            repository,
            Narrow,
            [],
            _ => null,
            _ => TrimVerification.None,
            Writer,
            apply,
            1_800_000_100_000,
            CancellationToken.None);

    private async Task<(LocalFileSystemObjectStore Store, OpenedRepository Repository, RepositoryReadAuthority Authority)>
        UpgradedPastThisBuildAsync(string name)
    {
        var store = new LocalFileSystemObjectStore(Path.Combine(_root, name));
        using var passphrase = Passphrase.Create(PassphraseText);
        var (created, createdAuthority) = await RepositoryLifecycle.CreateFromPassphraseAsync(
            store,
            passphrase,
            RepositoryCreationSettings.Default with { CreatedBy = "newer-format-retention-tests/1.0" },
            1_800_000_000_000,
            CancellationToken.None);
        using (created)
        using (createdAuthority)
        {
            await NewerBuildUpgrade.PlantAsync(
                store, created.Credential, created.EffectiveFormatVersion, CancellationToken.None);
        }

        var (repository, authority) = await RepositoryLifecycle.OpenForReadAsync(store, passphrase, CancellationToken.None);
        Assert.AreEqual(NewerBuildUpgrade.UnknownVersion, repository.EffectiveFormatVersion);
        return (store, repository, authority);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
