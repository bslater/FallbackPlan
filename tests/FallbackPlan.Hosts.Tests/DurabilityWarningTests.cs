using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Configuration;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The failure-domain warning on a set draft (FR-SNP-007, ADR-0018): a set
/// whose every destination sits inside its source's own failure domain is
/// told so at the moment it is chosen, not after the first backup reports
/// <c>captured</c> and somebody wonders why.
/// </summary>
[TestClass]
public sealed class DurabilityWarningTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    /// <summary>Writes a configuration whose single destination is the one described.</summary>
    private void WriteDestination(DestinationKind kind, string? path, FailureDomain? domain = null) =>
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('d', 32),
                    Name = "vault",
                    Kind = kind,
                    Path = path,
                    Fingerprint = kind == DestinationKind.Peer ? new string('f', 64) : null,
                    Endpoint = kind == DestinationKind.Peer ? "192.0.2.10:9" : null,
                    FailureDomain = domain,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    private async Task<SetDraftValidationResult> ValidateAsync(
        IReadOnlyList<string>? roots, IReadOnlyList<string>? destinations)
    {
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            passphrase: null, _timeout.Token);

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<SetDraftValidationResult>(
            await handler.ExecuteAsync(
                new ValidateSetDraftCommand("every 1h", [], [], roots, destinations), _timeout.Token),
            out var result);
        return result;
    }

    [TestMethod]
    public async Task Draft_ADestinationOnTheSourceVolume_WarnsThatNothingSurvivesTheDisk()
    {
        // The ADR-0018 failure in miniature: a local folder beside the
        // source reports `protected` right up until the disk dies.
        WriteDestination(DestinationKind.LocalPath, Path.Combine(_harness.StateDirectory, "vault"));

        var result = await ValidateAsync([_harness.SourceRoot], ["vault"]);

        Assert.IsEmpty(result.Defects, "a single-disk setup is unwise, not invalid");
        Assert.IsNotNull(result.Warnings);
        Assert.IsTrue(
            result.Warnings!.Any(warning => warning.Contains("captured", StringComparison.Ordinal)),
            "the warning says what the status page will go on to say, in the same word");
    }

    [TestMethod]
    public async Task Draft_ADestinationDeclaredIndependent_IsNotWarnedAbout()
    {
        // The declaration wins — only the user knows where the NAS sits.
        WriteDestination(
            DestinationKind.LocalPath, Path.Combine(_harness.StateDirectory, "vault"), FailureDomain.Independent);

        var result = await ValidateAsync([_harness.SourceRoot], ["vault"]);

        Assert.IsTrue(result.Warnings is null or { Count: 0 }, "a destination declared off-site is not a warning");
    }

    [TestMethod]
    public async Task Draft_APeerDestination_IsNotWarnedAbout()
    {
        // A LAN friend does not survive the house fire, but it does survive
        // this machine — which is the line FR-SNP-007 draws.
        WriteDestination(DestinationKind.Peer, path: null);

        var result = await ValidateAsync([_harness.SourceRoot], ["vault"]);

        Assert.IsTrue(result.Warnings is null or { Count: 0 });
    }

    [TestMethod]
    public async Task Draft_MultipleRootsAndASecondDisk_StillWarnsBecauseTheMachineIsTheDomain()
    {
        // Another disk survives losing a disk and nothing more. The wording
        // has to differ from the same-volume case, because the remedy does:
        // one needs a second disk, the other needs somewhere else entirely.
        WriteDestination(
            DestinationKind.LocalPath, Path.Combine(_harness.StateDirectory, "vault"), FailureDomain.SameMachine);

        var result = await ValidateAsync(
            [_harness.SourceRoot, Path.Combine(_harness.StateDirectory, "second-root")], ["vault"]);

        Assert.IsNotNull(result.Warnings);
        Assert.IsTrue(
            result.Warnings!.Any(warning => warning.Contains("on this machine", StringComparison.Ordinal)),
            "the same-machine warning names a different remedy from the same-volume one");
        Assert.IsFalse(
            result.Warnings!.Any(warning => warning.Contains("same volume", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Draft_ADestinationThatIsNotDeclared_IsNamed()
    {
        WriteDestination(DestinationKind.LocalPath, Path.Combine(_harness.StateDirectory, "vault"));

        var result = await ValidateAsync([_harness.SourceRoot], ["nowhere"]);

        Assert.IsNotNull(result.Warnings);
        Assert.IsTrue(result.Warnings!.Any(warning => warning.Contains("'nowhere'", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Draft_WithoutRootsOrDestinations_IsNotToldAnythingAboutDurability()
    {
        // An editor part-way through choosing folders has not asked the
        // question yet, and answering it anyway would say "this survives
        // nothing" about a set that is not finished.
        WriteDestination(DestinationKind.LocalPath, Path.Combine(_harness.StateDirectory, "vault"));

        Assert.IsTrue((await ValidateAsync(null, null)).Warnings is null or { Count: 0 });
        Assert.IsTrue((await ValidateAsync([_harness.SourceRoot], [])).Warnings is null or { Count: 0 });
        Assert.IsTrue((await ValidateAsync([], ["vault"])).Warnings is null or { Count: 0 });
    }

    [TestMethod]
    public async Task Draft_AConfigurationThatWillNotLoad_StillValidatesTheRestOfTheDraft()
    {
        // A draft check is advice. It must not be the thing that turns a
        // typo in config.json into a failed request, when the editor asking
        // is very likely how that typo gets fixed.
        await File.WriteAllTextAsync(
            Path.Combine(_harness.StateDirectory, "config.json"), "{ not json at all", _timeout.Token);

        var result = await ValidateAsync([_harness.SourceRoot], ["vault"]);

        Assert.IsTrue(result.Warnings is null or { Count: 0 });
        Assert.IsNotEmpty(result.NextRuns, "the schedule half of the answer still works");
    }
}
