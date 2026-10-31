using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Cli;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// A restore in direct mode runs outside any service, so it says it is under
/// way where a service looks (FR-GC-003; ADR-0172 Amendment 6). While it
/// runs it holds a marker for its repository in the state directory it works
/// in and, where it named none, in the one the installation's service keeps,
/// where there is one. It lets them go however it ends. A restore that cannot
/// hold one restores anyway, and says so.
/// </summary>
[TestClass]
public sealed class DirectRestoreUnderWayTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    private string Destination => Path.Combine(_cli.WorkPath, "restored");

    private string Markers => Path.Combine(_cli.StatePath, "restores-under-way");

    [TestMethod]
    public async Task ARestore_HoldsItsRepositoryUnderWayInItsStateDirectory_UntilItEnds()
    {
        var snapshot = await BackUpAsync();
        RepositoryId? restoring = null;
        var heldWhileRunning = false;
        DirectGateway.RestoreUnderWayInFlow = repository =>
        {
            restoring = repository;
            heldWhileRunning = RestoresUnderWay.AnyFor(_cli.StatePath, repository);
        };

        var run = await _cli.RunAsync("restore", snapshot, "--output", Destination);

        Assert.AreEqual(0, run.ExitCode, run.All);
        Assert.IsNotNull(restoring, "the run reached its restore");
        Assert.IsTrue(heldWhileRunning, "a service working in the state directory sees the restore under way");
        Assert.IsFalse(RestoresUnderWay.AnyFor(_cli.StatePath, restoring.Value), "and no longer once it has ended");
    }

    [TestMethod]
    public async Task ARestoreCutShort_LetsItsMarkerGo()
    {
        var snapshot = await BackUpAsync();
        RepositoryId? restoring = null;
        DirectGateway.RestoreUnderWayInFlow = repository =>
        {
            restoring = repository;
            throw new IOException("cut short");
        };

        var run = await _cli.RunAsync("restore", snapshot, "--output", Destination);

        Assert.AreNotEqual(0, run.ExitCode, run.All);
        Assert.IsNotNull(restoring, "the run reached its restore");
        Assert.IsEmpty(Directory.EnumerateFiles(Markers, "*", SearchOption.AllDirectories), "the marker went with the run");
        Assert.IsFalse(RestoresUnderWay.AnyFor(_cli.StatePath, restoring.Value));
    }

    [TestMethod]
    public async Task ARestoreThatCannotHoldAMarker_RestoresAnyway_AndSaysSo()
    {
        var snapshot = await BackUpAsync();

        // A file where the markers' folder belongs, so none can be held there.
        await File.WriteAllTextAsync(Markers, string.Empty);

        var run = await _cli.RunAsync("restore", snapshot, "--output", Destination);

        Assert.AreEqual(0, run.ExitCode, run.All);
        Assert.AreEqual("first", await File.ReadAllTextAsync(Path.Combine(Destination, "one.txt")));
        Assert.Contains(
            $"could not say it is under way in '{_cli.StatePath}'", run.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    // Alone: it sets the installation-wide state variable every path-less invocation reads.
    [DoNotParallelize]
    public void ARestoreNamingNoStateDirectory_IsAlsoHeldWhereTheInstallationsServiceLooks_WhereThereIsOne()
    {
        var installation = Path.Combine(_cli.WorkPath, "installation-state");
        var before = Environment.GetEnvironmentVariable(InstallationDefaults.StateVariable);
        try
        {
            Environment.SetEnvironmentVariable(InstallationDefaults.StateVariable, installation);

            SequenceAssert.AreEqual([_cli.StatePath], DirectGateway.RestoreSeenFrom(_cli.StatePath, stateNamed: false));
            Assert.IsFalse(Directory.Exists(installation), "a restore makes no installation where there is none");

            Directory.CreateDirectory(installation);

            SequenceAssert.AreEqual(
                [_cli.StatePath, installation], DirectGateway.RestoreSeenFrom(_cli.StatePath, stateNamed: false));
            SequenceAssert.AreEqual([installation], DirectGateway.RestoreSeenFrom(installation, stateNamed: false));

            // A state directory named is the installation the command works in.
            SequenceAssert.AreEqual([_cli.StatePath], DirectGateway.RestoreSeenFrom(_cli.StatePath, stateNamed: true));
        }
        finally
        {
            Environment.SetEnvironmentVariable(InstallationDefaults.StateVariable, before);
        }
    }

    private async Task<string> BackUpAsync()
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/one.txt", "first");
        _cli.WriteFile("tree/nested/two.txt", "second");

        var backup = await _cli.RunAsync("backup", Path.Combine(_cli.WorkPath, "tree"));
        Assert.AreEqual(0, backup.ExitCode, backup.All);

        var snapshots = await _cli.RunAsync("snapshots");
        Assert.AreEqual(0, snapshots.ExitCode, snapshots.All);

        // The identifier is the leading hex token of the listing line.
        var first = snapshots.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        Assert.IsNotNull(first);
        return first.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }
}
