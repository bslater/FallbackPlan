using FallbackPlan.Agent;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A bare <c>fallbackplan-agent</c> starts the service on the installation it
/// finds — or creates — in the platform profile's data directory; the path
/// flags exist for pointing somewhere specific, never as a prerequisite
/// (FR-SVC-016). Environment overrides sit between the two so a harness or a
/// second install can redirect the defaults without composing a command line.
/// </summary>
/// <remarks>
/// Not parallelised: the environment overrides are process-global, and two of
/// these tests set them. Every other suite passes explicit paths, so the
/// variables never leak into their runs.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class AgentDefaultLocationsTests : IDisposable
{
    private readonly HostHarness _harness = new();

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(60));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AgentDefaults.StateVariable, null);
        Environment.SetEnvironmentVariable(AgentDefaults.ArchivesVariable, null);
        _timeout.Dispose();
        _harness.Dispose();
    }

    private void PointDefaultsAtTheHarness()
    {
        Environment.SetEnvironmentVariable(AgentDefaults.StateVariable, _harness.StateDirectory);
        Environment.SetEnvironmentVariable(AgentDefaults.ArchivesVariable, _harness.ArchivesRoot);
    }

    [TestMethod]
    public void Defaults_DeriveFromThePlatformProfile_UnlessTheEnvironmentSaysOtherwise()
    {
        Environment.SetEnvironmentVariable(AgentDefaults.StateVariable, null);
        Environment.SetEnvironmentVariable(AgentDefaults.ArchivesVariable, null);

        // The derived shape, not a literal path: the profile root is the
        // platform's and the test must hold on every platform.
        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "fallbackplan");
        Assert.AreEqual(Path.Combine(expectedRoot, "state"), AgentDefaults.StateDirectory);
        Assert.AreEqual(Path.Combine(expectedRoot, "archives"), AgentDefaults.ArchivesRoot);

        PointDefaultsAtTheHarness();
        Assert.AreEqual(_harness.StateDirectory, AgentDefaults.StateDirectory);
        Assert.AreEqual(_harness.ArchivesRoot, AgentDefaults.ArchivesRoot);
    }

    [TestMethod]
    public async Task AgentHost_NoArgumentsAtAll_StartsTheServiceOnTheDefaultLocations()
    {
        PointDefaultsAtTheHarness();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_timeout.Token);
        var output = new StringWriter();
        var error = new StringWriter();

        var running = AgentHost.RunAsync([], output, error, stop.Token);

        // The listening banner is the service saying it is up; only then is
        // cancelling a shutdown rather than a stillbirth.
        while (!output.ToString().Contains("listening on", StringComparison.Ordinal))
        {
            Assert.IsFalse(
                running.IsCompleted,
                $"the bare invocation exited ({(running.IsCompleted ? await running : 0)}) instead of "
                + $"serving: {error}");
            await Task.Delay(25, _timeout.Token);
        }

        stop.Cancel();
        Assert.AreEqual(0, await running, error.ToString());
    }

    [TestMethod]
    public async Task AgentHost_RunOnce_WithNoPathFlags_PassesOnTheDefaults()
    {
        PointDefaultsAtTheHarness();

        // A fresh default installation holds no configuration yet: the pass
        // finds nothing due and says so by exit code, not by usage error.
        var result = await HostHarness.RunAsync(AgentHost.RunAsync, "run", "--once");

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.DoesNotContain("usage", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task AgentHost_AStateOnlyVerb_ReadsTheDefaultInstallation()
    {
        PointDefaultsAtTheHarness();

        var result = await HostHarness.RunAsync(AgentHost.RunAsync, "notices");

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.DoesNotContain("usage", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
