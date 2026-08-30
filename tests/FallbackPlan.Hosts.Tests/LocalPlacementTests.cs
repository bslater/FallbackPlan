using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The condition of choosing a local destination (ADR-0051, FR-DEST-017),
/// enforced at the command boundary: binding a set to a local-path
/// destination on any root's volume — or, where the platform can say, on
/// the same physical drive — is refused with both paths named. Existing
/// configuration files keep loading and keep their warnings (ADR-0035);
/// only the choosing is gated.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LocalPlacementTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    private CancellationToken Timeout => _timeout.Token;

    private string VaultPath => Path.Combine(_harness.WorkPath, "vault");

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task Upsert_ALocalDestinationOnTheRootsVolume_IsRefusedNamingBoth()
    {
        // The harness's source and vault share one real volume — exactly the
        // placement the condition exists to refuse.
        Directory.CreateDirectory(VaultPath);
        _harness.WriteConfiguration("every 4h");
        AddVaultDeclaration();
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var result = await handler.ExecuteAsync(new UpsertBackupSetCommand(new BackupSetDescriptor(
            new string('b', 32), "second", _harness.SourceRoot, null, [], [], ["vault-b"])), Timeout);

        Assert.IsInstanceOfType<ServiceError>(result, out var error);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, error.Reason);
        Assert.Contains("vault-b", error.Message, StringComparison.Ordinal);
        Assert.Contains(_harness.SourceRoot, error.Message, StringComparison.Ordinal);
        Assert.Contains("volume", error.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Upsert_ALocalDestinationOnItsOwnDrive_IsAccepted()
    {
        Directory.CreateDirectory(VaultPath);
        _harness.WriteConfiguration("every 4h");
        AddVaultDeclaration();
        await using var runtime = await StartAsync(options => options with
        {
            // The fixture's one real volume, told apart by path: the vault
            // reads as its own drive, which is what a compliant install has.
            VolumeIdentityOverride = path =>
                path.StartsWith(_harness.WorkPath, StringComparison.Ordinal) ? 2UL : 1UL,
        });
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var result = await handler.ExecuteAsync(new UpsertBackupSetCommand(new BackupSetDescriptor(
            new string('b', 32), "second", _harness.SourceRoot, null, [], [], ["vault-b"])), Timeout);

        Assert.IsInstanceOfType<ConfigurationChangeResult>(result);
    }

    [TestMethod]
    public async Task Upsert_DistinctVolumesOnOneDisk_IsRefusedWhereThePlatformCanSay()
    {
        Directory.CreateDirectory(VaultPath);
        _harness.WriteConfiguration("every 4h");
        AddVaultDeclaration();
        await using var runtime = await StartAsync(options => options with
        {
            VolumeIdentityOverride = path =>
                path.StartsWith(_harness.WorkPath, StringComparison.Ordinal) ? 2UL : 1UL,
            PhysicalDiskOverride = _ => "disk-a",
        });
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var result = await handler.ExecuteAsync(new UpsertBackupSetCommand(new BackupSetDescriptor(
            new string('b', 32), "second", _harness.SourceRoot, null, [], [], ["vault-b"])), Timeout);

        Assert.IsInstanceOfType<ServiceError>(result, out var error);
        Assert.Contains("physical drive", error.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Upsert_AnExistingBindingLeftAlone_IsNotReJudged()
    {
        // A config written before the condition keeps working (ADR-0035): an
        // edit that touches neither roots nor destinations — a schedule
        // change here — saves cleanly even though the standing binding would
        // be refused if chosen today. The status warnings stay its signal.
        _harness.WriteConfiguration("every 4h");
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var existing = runtime.Configuration.BackupSets.Single();

        var result = await handler.ExecuteAsync(new UpsertBackupSetCommand(new BackupSetDescriptor(
            existing.Id, existing.Name, "", "every 8h", [.. existing.IncludeRules], [.. existing.ExcludeRules],
            [.. existing.Destinations.Select(reference => reference.Ref)],
            Roots: [.. existing.Roots.Select(root => new BackupRootDescriptor(root.Path, root.Label))])), Timeout);

        Assert.IsNotInstanceOfType<ServiceError>(
            result, "an untouched binding must not be re-judged by an unrelated edit");
    }

    [TestMethod]
    public async Task UpsertDestination_APathEditOntoARootsVolume_IsRefused()
    {
        // The other way to create the violation: moving an already-referenced
        // destination's path onto a source volume.
        _harness.WriteConfiguration("every 4h");
        await using var runtime = await StartAsync(options => options with
        {
            VolumeIdentityOverride = path =>
                path.StartsWith(Path.Combine(_harness.StateDirectory, "vault"), StringComparison.Ordinal) ? 2UL : 1UL,
        });
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var declared = runtime.Configuration.Destinations.Single();

        var result = await handler.ExecuteAsync(new UpsertDestinationCommand(new DestinationDescriptor(
            declared.Id, declared.Name, "local-path", Path.Combine(_harness.SourceRoot, "vault-inside"),
            null, null)), Timeout);

        Assert.IsInstanceOfType<ServiceError>(result, out var error);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, error.Reason);
        Assert.Contains("docs", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Declares a second local-path destination beside the harness's default one.</summary>
    private void AddVaultDeclaration()
    {
        var path = Path.Combine(_harness.StateDirectory, "config.json");
        var configuration = ClientConfiguration.Load(path);
        (configuration with
        {
            Destinations =
            [
                .. configuration.Destinations,
                new DestinationConfiguration
                {
                    Id = new string('e', 32),
                    Name = "vault-b",
                    Kind = DestinationKind.LocalPath,
                    Path = VaultPath,
                },
            ],
        }).Save(path);
    }

    private async Task<ServiceRuntime> StartAsync(Func<ServiceOptions, ServiceOptions>? adjust = null)
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);

        var options = new ServiceOptions
        {
            ArchivesRoot = _harness.ArchivesRoot,
            StateDirectory = _harness.StateDirectory,
        };

        return await ServiceRuntime.StartAsync(adjust?.Invoke(options) ?? options, passphrase, Timeout);
    }
}
