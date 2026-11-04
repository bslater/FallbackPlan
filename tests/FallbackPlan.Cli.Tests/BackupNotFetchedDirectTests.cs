using FallbackPlan.Cli;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// In direct mode a backup whose every failure is an online-only file it did
/// not fetch, and whose version backed up before it kept, says it is behind
/// and on how many, and exits zero; one that missed a file new to the set,
/// which is in no backup at all, is partial and exits non-zero (FR-SNP-014,
/// ADR-0193 Amendment 1).
/// </summary>
/// <remarks>
/// No runner has a cloud provider, so a stand-in source marks the files
/// named <c>.cloud</c> online-only and fails their fetch while told to.
/// </remarks>
[TestClass]
public sealed class BackupNotFetchedDirectTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    private string Tree => Path.Combine(_cli.WorkPath, "tree");

    [TestMethod]
    public async Task ABackupThatKeptTheEarlierVersionOfEachFileItMissed_SaysItIsBehind_AndExitsZero()
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/a.cloud", "alpha");
        _cli.WriteFile("tree/b.txt", "bravo");
        var delivers = true;
        DirectGateway.SourceInFlow = source => new CloudStandIn(source, () => delivers);
        var first = await _cli.RunAsync("backup", Tree);
        Assert.AreEqual(0, first.ExitCode, first.All);
        Assert.Contains("status         complete", first.Output, StringComparison.Ordinal);

        _cli.WriteFile("tree/a.cloud", "alpha, changed");
        delivers = false;
        var behind = await _cli.RunAsync("backup", Tree);

        Assert.AreEqual(0, behind.ExitCode, behind.All);
        Assert.Contains(
            "status         behind — 1 online-only file(s) not downloaded keep the version backed up before",
            behind.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PARTIAL", behind.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ABackupThatMissedAFileNewToTheSet_IsPartial_AndExitsNonZero()
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/a.cloud", "alpha");
        var delivers = true;
        DirectGateway.SourceInFlow = source => new CloudStandIn(source, () => delivers);
        var first = await _cli.RunAsync("backup", Tree);
        Assert.AreEqual(0, first.ExitCode, first.All);

        _cli.WriteFile("tree/a.cloud", "alpha, changed");
        _cli.WriteFile("tree/new.cloud", "new, and never fetched");
        delivers = false;
        var partial = await _cli.RunAsync("backup", Tree);

        Assert.AreNotEqual(0, partial.ExitCode, partial.All);
        Assert.Contains("status         PARTIAL", partial.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("behind", partial.Output, StringComparison.Ordinal);
    }
}
