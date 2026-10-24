using FallbackPlan.Domain;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// Direct mode against a repository a newer build upgraded past the newest
/// format this build writes (NFR-COMP-003; specification 11 §5.3; ADR-0066
/// Amendment 1). A command that would write is refused as an operator's
/// error, naming both versions, rather than escaping as a bug; a command that
/// reads, and one that only rebuilds this client's own catalogue, go on.
/// </summary>
[TestClass]
public sealed class NewerFormatCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    [TestMethod]
    public async Task Backup_OfARepositoryANewerBuildUpgraded_IsRefusedNamingBothVersions()
    {
        var root = await BackedUpAndUpgradedPastThisBuildAsync();
        _cli.WriteFile("tree/later.txt", "a change the refused backup must not carry");

        var backup = await _cli.RunAsync("backup", root);

        Assert.AreEqual(1, backup.ExitCode, backup.All);
        Assert.Contains($"format {NewerBuildUpgrade.UnknownVersion}", backup.Error, StringComparison.Ordinal);
        Assert.Contains($"format {FormatLimits.LatestFormatVersion}", backup.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(" at FallbackPlan.", backup.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ReadsAndTheCatalogueRebuild_OfARepositoryANewerBuildUpgraded_StillWork()
    {
        await BackedUpAndUpgradedPastThisBuildAsync();

        var rebuild = await _cli.RunAsync("rebuild-index");
        Assert.AreEqual(0, rebuild.ExitCode, rebuild.All);

        var snapshots = await _cli.RunAsync("snapshots");
        Assert.AreEqual(0, snapshots.ExitCode, snapshots.All);
        Assert.IsFalse(string.IsNullOrWhiteSpace(snapshots.Output), "snapshots listed nothing from the newer repository");

        var verify = await _cli.RunAsync("verify", "--level", "records");
        Assert.AreEqual(0, verify.ExitCode, verify.All);
    }

    /// <summary>One backup by this build, then the record a newer build leaves when it upgrades.</summary>
    private async Task<string> BackedUpAndUpgradedPastThisBuildAsync()
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/one.txt", "first");
        var root = Path.Combine(_cli.WorkPath, "tree");
        var backup = await _cli.RunAsync("backup", root);
        Assert.AreEqual(0, backup.ExitCode, backup.All);

        var store = new LocalFileSystemObjectStore(_cli.RepositoryPath);
        using var passphrase = Passphrase.Create("correct horse battery staple");
        var (repository, authority) = await RepositoryLifecycle.OpenForReadAsync(store, passphrase, CancellationToken.None);
        using (repository)
        using (authority)
        {
            await NewerBuildUpgrade.PlantAsync(
                store, repository.Credential, repository.EffectiveFormatVersion, CancellationToken.None);
        }

        return root;
    }
}
