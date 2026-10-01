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

    [TestMethod]
    public async Task Draft_ForANewSet_ADestinationOnTheRootsVolume_IsTheDefectTheSaveWouldRefuseWith()
    {
        // The editor learns of the refusal while the destination is being
        // chosen, in the words the save would use, rather than after every
        // later step of a new set has been filled in.
        Directory.CreateDirectory(VaultPath);
        _harness.WriteConfiguration("every 4h");
        AddVaultDeclaration();
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var id = new string('b', 32);

        var draft = await ValidateAsync(handler, id, [_harness.SourceRoot], ["vault-b"]);
        var refusal = await handler.ExecuteAsync(new UpsertBackupSetCommand(new BackupSetDescriptor(
            id, "second", _harness.SourceRoot, null, [], [], ["vault-b"])), Timeout);

        Assert.IsInstanceOfType<ServiceError>(refusal, out var error);
        Assert.AreEqual(error.Message, Assert.ContainsSingle(draft.Defects));
    }

    [TestMethod]
    public async Task Draft_ForANewSet_ADestinationOnItsOwnDrive_HasNoDefect()
    {
        Directory.CreateDirectory(VaultPath);
        _harness.WriteConfiguration("every 4h");
        AddVaultDeclaration();
        await using var runtime = await StartAsync(options => options with
        {
            VolumeIdentityOverride = path =>
                path.StartsWith(_harness.WorkPath, StringComparison.Ordinal) ? 2UL : 1UL,
        });
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var draft = await ValidateAsync(handler, new string('b', 32), [_harness.SourceRoot], ["vault-b"]);

        Assert.IsEmpty(draft.Defects);
    }

    [TestMethod]
    public async Task Draft_ForAnExistingSet_AStandingBindingItLeavesAlone_IsNotADefect()
    {
        // The save does not re-judge a binding an edit leaves alone
        // (ADR-0035), so neither does the draft: an editor told its standing
        // destination is refused would be told something the save never
        // says.
        _harness.WriteConfiguration("every 4h");
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var existing = runtime.Configuration.BackupSets.Single();

        var draft = await ValidateAsync(
            handler, existing.Id, [.. existing.Roots.Select(root => root.Path)],
            [.. existing.Destinations.Select(reference => reference.Ref)]);

        Assert.IsEmpty(draft.Defects);
    }

    [TestMethod]
    public async Task Draft_ForAnExistingSet_ChangingItsRoots_JudgesItsStandingBindingAgain()
    {
        // New roots re-open the choice, in the save and in the draft alike.
        _harness.WriteConfiguration("every 4h");
        var otherRoot = Directory.CreateDirectory(Path.Combine(_harness.WorkPath, "other-root")).FullName;
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var existing = runtime.Configuration.BackupSets.Single();

        var draft = await ValidateAsync(handler, existing.Id, [otherRoot], ["vault"]);

        var defect = Assert.ContainsSingle(draft.Defects);
        Assert.Contains("'vault' shares a volume", defect, StringComparison.Ordinal);
        Assert.Contains(otherRoot, defect, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Draft_NamingNoSet_IsNotJudgedForPlacement()
    {
        // A pre-1.49 client names no set, so the service cannot tell a new
        // binding from a standing one. It does not guess, and says nothing.
        Directory.CreateDirectory(VaultPath);
        _harness.WriteConfiguration("every 4h");
        AddVaultDeclaration();
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var draft = await ValidateAsync(handler, setId: null, [_harness.SourceRoot], ["vault-b"]);

        Assert.IsEmpty(draft.Defects);
    }

    private async Task<SetDraftValidationResult> ValidateAsync(
        ServiceCommandHandler handler, string? setId, IReadOnlyList<string> roots, IReadOnlyList<string> destinations)
    {
        Assert.IsInstanceOfType<SetDraftValidationResult>(
            await handler.ExecuteAsync(
                new ValidateSetDraftCommand(null, [], [], roots, destinations, setId), Timeout),
            out var result);
        return result;
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
        await _harness.SetupAsync();

        var options = new ServiceOptions
        {
            ArchivesRoot = _harness.ArchivesRoot,
            StateDirectory = _harness.StateDirectory,
        };

        return await ServiceRuntime.StartAsync(adjust?.Invoke(options) ?? options, Timeout);
    }
}
