using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The configuration surface (ADR-0037): set and destination CRUD, the folder
/// browser, and draft validation — every refusal asserted on the reason and
/// the file left byte-identical, because the page branches on reasons and the
/// scheduler reads the file.
/// </summary>
[TestClass]
public sealed class ConfigurationCommandTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    private string ConfigurationPath => Path.Combine(_harness.StateDirectory, "config.json");

    [TestMethod]
    public async Task UpsertBackupSet_CarryingRetentionAndOverrides_WritesBoth()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var result = await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 4h", [], [], ["vault"],
                new RetentionPolicyDescriptor(KeepDaily: 14, MinGenerations: 3),
                new Dictionary<string, RetentionPolicyDescriptor>
                {
                    ["vault"] = new(KeepDaily: 7),
                })),
            _timeout.Token);

        Assert.IsInstanceOfType<AcknowledgedResult>(result);

        var reloaded = ClientConfiguration.Load(ConfigurationPath).FindSet("docs")!;
        Assert.AreEqual(14, reloaded.Retention?.KeepDaily);
        Assert.AreEqual(3, reloaded.Retention?.MinGenerations);
        Assert.AreEqual(7, reloaded.Destinations.Single().Retention?.KeepDaily);

        // And the round trip carries both back out over the contract.
        Assert.IsInstanceOfType<BackupSetsResult>(
            await handler.ExecuteAsync(new ListBackupSetsCommand(), _timeout.Token), out var listed);
        var descriptor = Assert.ContainsSingle(listed.Sets);
        Assert.AreEqual(14, descriptor.Retention?.KeepDaily);
        Assert.AreEqual(7, descriptor.DestinationRetention?["vault"].KeepDaily);
    }

    [TestMethod]
    public async Task UpsertBackupSet_SpeakingAnEmptyPolicy_ClearsWhereSilencePreserves()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        // Seed a policy through the command surface itself.
        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 4h", [], [], ["vault"],
                new RetentionPolicyDescriptor(KeepDaily: 14))),
            _timeout.Token));

        // Silence preserves: a 1.6-shaped upsert names no retention at all.
        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 2h", [], [], ["vault"])),
            _timeout.Token));
        Assert.AreEqual(14, ClientConfiguration.Load(ConfigurationPath).FindSet("docs")!.Retention?.KeepDaily);

        // Speaking an empty policy clears it.
        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 2h", [], [], ["vault"],
                new RetentionPolicyDescriptor())),
            _timeout.Token));
        Assert.IsNull(ClientConfiguration.Load(ConfigurationPath).FindSet("docs")!.Retention);
    }

    [TestMethod]
    public async Task UpsertBackupSet_WithAnUnparseableSchedule_IsRefusedNamingTheDefect()
    {
        // Unvalidated, this saves cleanly and fails permanently at the next
        // pass — at two in the morning, once per pass, forever (ADR-0037 §2).
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        var before = await File.ReadAllTextAsync(ConfigurationPath, _timeout.Token);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var refused = await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "hourly", [], [], ["vault"])),
            _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(refused, out var error);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, error.Reason);
        Assert.Contains("hourly", error.Message, StringComparison.Ordinal);
        Assert.AreEqual(before, await File.ReadAllTextAsync(ConfigurationPath, _timeout.Token));
    }

    [TestMethod]
    public async Task UpsertBackupSet_EditingTheFirstOfTwoSets_KeepsItsPosition()
    {
        // The first set is what RunBackupCommand(null) runs and status renders
        // declaration order — an edit must not reshuffle either (ADR-0037 §5).
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        // A new set answers with its queued first backup (ADR-0047); the
        // existing set's schedule edit stays a plain acknowledgement.
        Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                new string('b', 32), "second", _harness.SourceRoot, null, [], [], ["vault"])),
            _timeout.Token));

        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 8h", [], [], ["vault"])),
            _timeout.Token));

        var names = ClientConfiguration.Load(ConfigurationPath).BackupSets.Select(set => set.Name).ToList();
        SequenceAssert.AreEqual(["docs", "second"], names);
    }

    [TestMethod]
    public async Task DeleteBackupSet_RemovesTheEntry_AndNamesWhatRemains()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var result = await handler.ExecuteAsync(new DeleteBackupSetCommand("docs"), _timeout.Token);

        Assert.IsInstanceOfType<ConfigurationChangeResult>(result, out var change);
        Assert.IsNull(ClientConfiguration.Load(ConfigurationPath).FindSet("docs"));

        // ADR-0037 §4: a config edit, never an erasure — and it says so.
        Assert.IsTrue(change.Lines.Any(line => line.Contains("no data was deleted", StringComparison.Ordinal)));
        Assert.IsTrue(change.Lines.Any(line => line.Contains(_harness.RepositoryPath, StringComparison.Ordinal)));
        Assert.IsTrue(change.Lines.Any(line => line.Contains("'vault'", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DeleteBackupSet_NamingNoConfiguredSet_IsNotFound()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var refused = await handler.ExecuteAsync(new DeleteBackupSetCommand("nothere"), _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(refused, out var error);
        Assert.AreEqual(ServiceErrorReason.NotFound, error.Reason);
        Assert.Contains("nothere", error.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Destinations_UpsertListAndDelete_RoundTrip()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var second = Path.Combine(_harness.WorkPath, "second-vault");
        Directory.CreateDirectory(second);

        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertDestinationCommand(new DestinationDescriptor(
                null, "second-vault", "local-path", second, null, null, "same-machine")),
            _timeout.Token));

        Assert.IsInstanceOfType<DestinationsResult>(
            await handler.ExecuteAsync(new ListDestinationsCommand(), _timeout.Token), out var listed);
        Assert.HasCount(2, listed.Destinations);
        var added = listed.Destinations.Single(destination => destination.Name == "second-vault");
        Assert.AreEqual("local-path", added.Kind);
        Assert.AreEqual("same-machine", added.FailureDomain);
        Assert.IsNotNull(added.Id);
        Assert.IsNull(added.AddressDefect);

        // Unreferenced, so deletable — and the result reports what remains.
        var deleted = await handler.ExecuteAsync(new DeleteDestinationCommand("second-vault"), _timeout.Token);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(deleted, out var change);
        Assert.IsTrue(change.Lines.Any(line => line.Contains(second, StringComparison.Ordinal)));
        Assert.ContainsSingle(ClientConfiguration.Load(ConfigurationPath).Destinations);
    }

    [TestMethod]
    public async Task DeleteDestination_StillReferenced_IsRefusedNamingTheSets()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        var before = await File.ReadAllTextAsync(ConfigurationPath, _timeout.Token);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var refused = await handler.ExecuteAsync(new DeleteDestinationCommand("vault"), _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(refused, out var error);
        Assert.AreEqual(ServiceErrorReason.Refused, error.Reason);
        Assert.Contains("'docs'", error.Message, StringComparison.Ordinal);
        Assert.AreEqual(before, await File.ReadAllTextAsync(ConfigurationPath, _timeout.Token));
    }

    [TestMethod]
    public async Task UpsertDestination_RenamingIt_FollowsThroughToReferencingSets()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var existing = ClientConfiguration.Load(ConfigurationPath).FindDestination("vault")!;

        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertDestinationCommand(new DestinationDescriptor(
                existing.Id, "renamed-vault", "local-path", existing.Path, null, null)),
            _timeout.Token));

        var reloaded = ClientConfiguration.Load(ConfigurationPath);
        Assert.IsNotNull(reloaded.FindDestination("renamed-vault"));
        Assert.AreEqual("renamed-vault", reloaded.FindSet("docs")!.Destinations.Single().Ref);
    }

    [TestMethod]
    public async Task UpsertDestination_WithAnUnknownKind_IsRefusedNamingTheVocabulary()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var refused = await handler.ExecuteAsync(
            new UpsertDestinationCommand(new DestinationDescriptor(
                null, "tape", "tape-robot", "/mnt/tape", null, null)),
            _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(refused, out var error);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, error.Reason);
        Assert.Contains("local-path", error.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task BrowseFolders_WalksRootsAndChildren_DirectoriesOnly()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        _harness.WriteSourceFile("photos/summer/beach.jpg", "pixels");
        _harness.WriteSourceFile("docs/notes.txt", "words");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<FolderListingResult>(
            await handler.ExecuteAsync(new BrowseFoldersCommand(null), _timeout.Token), out var roots);
        Assert.IsNull(roots.Path);
        Assert.IsTrue(roots.Folders.Count > 0);

        Assert.IsInstanceOfType<FolderListingResult>(
            await handler.ExecuteAsync(new BrowseFoldersCommand(_harness.SourceRoot), _timeout.Token),
            out var listing);
        SequenceAssert.AreEqual(
            ["docs", "photos"],
            [.. listing.Folders.Select(folder => folder.Name)]);
        Assert.IsFalse(listing.Folders.Any(folder => folder.Name.Contains("notes", StringComparison.Ordinal)),
            "files must never appear — this is a folder picker, not a content browser");

        var refused = await handler.ExecuteAsync(
            new BrowseFoldersCommand(Path.Combine(_harness.SourceRoot, "absent")), _timeout.Token);
        Assert.IsInstanceOfType<ServiceError>(refused, out var error);
        Assert.AreEqual(ServiceErrorReason.NotFound, error.Reason);
    }

    [TestMethod]
    public async Task ValidateSetDraft_NamesRuleAndScheduleDefects_AndPreviewsAGoodSchedule()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<SetDraftValidationResult>(
            await handler.ExecuteAsync(
                new ValidateSetDraftCommand("hourly", ["a**b"], ["/absolute"]), _timeout.Token),
            out var bad);
        Assert.HasCount(3, bad.Defects);
        Assert.IsTrue(bad.Defects.Any(defect => defect.Contains("a**b", StringComparison.Ordinal)));
        Assert.IsTrue(bad.Defects.Any(defect => defect.Contains("/absolute", StringComparison.Ordinal)));
        Assert.IsTrue(bad.Defects.Any(defect => defect.Contains("hourly", StringComparison.Ordinal)));
        Assert.IsEmpty(bad.NextRuns);

        Assert.IsInstanceOfType<SetDraftValidationResult>(
            await handler.ExecuteAsync(
                new ValidateSetDraftCommand("every 4h", ["photos/**"], ["*.iso"]), _timeout.Token),
            out var good);
        Assert.IsEmpty(good.Defects);
        Assert.HasCount(3, good.NextRuns);

        // The overflow Schedule.TryParse lets escape must come back as a
        // defect, not a fault (ADR-0037).
        Assert.IsInstanceOfType<SetDraftValidationResult>(
            await handler.ExecuteAsync(
                new ValidateSetDraftCommand("every 4294967295d", [], []), _timeout.Token),
            out var overflow);
        Assert.ContainsSingle(overflow.Defects);
    }

    [TestMethod]
    public async Task UpsertDestination_ARelativeLocalPath_IsStoredAbsolute()
    {
        // Stored verbatim, a relative path resolves against whatever working
        // directory the service happens to have — which is how a replica tree
        // appeared beside the logs in the 2026-08 report. The path is pinned
        // to an absolute one at the boundary, and the reply says where it
        // pinned, because the resolution is the part the operator did not type.
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var result = await handler.ExecuteAsync(
            new UpsertDestinationCommand(new DestinationDescriptor(
                null, "portable", "local-path", "portable-vault", null, null)),
            _timeout.Token);

        var stored = ClientConfiguration.Load(ConfigurationPath).FindDestination("portable")!;
        Assert.IsTrue(Path.IsPathRooted(stored.Path), $"'{stored.Path}' should have been made absolute");
        Assert.AreEqual(Path.GetFullPath("portable-vault"), stored.Path);

        Assert.IsInstanceOfType<ConfigurationChangeResult>(result, out var change);
        Assert.IsTrue(change.Lines.Any(line => line.Contains(stored.Path!, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task UpsertBackupSet_ARootContainingADeclaredDestination_IsRefusedNamingBoth()
    {
        // The circular capture (FR-DEST-011): a root over a destination backs
        // the backup up into itself, growing without bound. Refused at the
        // boundary, naming both paths, with the config file untouched.
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var inside = Path.Combine(_harness.WorkPath, "vault2");
        Directory.CreateDirectory(inside);
        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertDestinationCommand(new DestinationDescriptor(
                null, "vault2", "local-path", inside, null, null)),
            _timeout.Token));

        var before = await File.ReadAllTextAsync(ConfigurationPath, _timeout.Token);

        var refused = await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                new string('b', 32), "work", _harness.WorkPath, "every 4h", [], [], ["vault"])),
            _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(refused, out var error);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, error.Reason);
        Assert.Contains("vault2", error.Message, StringComparison.Ordinal);
        Assert.Contains(_harness.WorkPath, error.Message, StringComparison.Ordinal);
        Assert.AreEqual(before, await File.ReadAllTextAsync(ConfigurationPath, _timeout.Token));
    }

    [TestMethod]
    public async Task UpsertBackupSet_ARootContainingAnExcludedDestination_IsAccepted()
    {
        // The carve-out the owner chose: a destination the set's own exclude
        // rules provably fence off is not captured, so the layout is allowed.
        // The judgement is the scanner's own rule evaluation, not a second
        // opinion.
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var inside = Path.Combine(_harness.WorkPath, "vault2");
        Directory.CreateDirectory(inside);
        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertDestinationCommand(new DestinationDescriptor(
                null, "vault2", "local-path", inside, null, null)),
            _timeout.Token));

        // A new set answers with its queued first backup (ADR-0047,
        // FR-SVC-015), so the acceptance here is the configuration-change
        // report.
        Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                new string('b', 32), "work", _harness.WorkPath, "every 4h", [], ["vault2"], ["vault"])),
            _timeout.Token));

        Assert.IsNotNull(ClientConfiguration.Load(ConfigurationPath).FindSet("work"));
    }

    [TestMethod]
    public async Task UpsertBackupSet_ARuleEditThatReincludesTheDestination_IsRefused()
    {
        // The carve-out's drift risk, closed at the moment it would open:
        // rules change independently of destinations, so the edit that stops
        // excluding the destination is the edit that gets refused.
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var inside = Path.Combine(_harness.WorkPath, "vault2");
        Directory.CreateDirectory(inside);
        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertDestinationCommand(new DestinationDescriptor(
                null, "vault2", "local-path", inside, null, null)),
            _timeout.Token));
        Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                new string('b', 32), "work", _harness.WorkPath, "every 4h", [], ["vault2"], ["vault"])),
            _timeout.Token));

        var refused = await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                new string('b', 32), "work", _harness.WorkPath, "every 4h", [], [], ["vault"])),
            _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(refused, out var error);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, error.Reason);
        Assert.Contains("vault2", error.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task UpsertDestination_APathInsideASetRoot_IsRefusedNamingTheSet()
    {
        // The same circle entered from the other door: declaring a
        // destination inside a set's captured sources.
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        var before = await File.ReadAllTextAsync(ConfigurationPath, _timeout.Token);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var refused = await handler.ExecuteAsync(
            new UpsertDestinationCommand(new DestinationDescriptor(
                null, "external", "local-path", Path.Combine(_harness.SourceRoot, "external-drive"), null, null)),
            _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(refused, out var error);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, error.Reason);
        Assert.Contains("docs", error.Message, StringComparison.Ordinal);
        Assert.AreEqual(before, await File.ReadAllTextAsync(ConfigurationPath, _timeout.Token));
    }

    [TestMethod]
    public async Task UpsertDestination_AnExcludedPathInsideASetRoot_IsAccepted()
    {
        // The carve-out holds from this door too: the set already excludes
        // the folder, so the declaration is sound.
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        // A rules change on the existing set is material, so the answer is
        // the configuration-change report rather than a bare acknowledgement.
        Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 4h", [], ["external-drive"], ["vault"])),
            _timeout.Token));

        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertDestinationCommand(new DestinationDescriptor(
                null, "external", "local-path", Path.Combine(_harness.SourceRoot, "external-drive"), null, null)),
            _timeout.Token));
    }

    [TestMethod]
    public async Task UpsertBackupSet_ARootContainingTheStateDirectory_IsRefused()
    {
        // The service's own state directory inside a root is the same circle
        // wearing a different coat — only the agent knows where that is, so
        // only this boundary can refuse it.
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        var before = await File.ReadAllTextAsync(ConfigurationPath, _timeout.Token);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var refused = await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                new string('b', 32), "everything", _harness.StateDirectory, "every 4h", [], [], ["vault"])),
            _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(refused, out var error);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, error.Reason);
        Assert.Contains("state directory", error.Message, StringComparison.Ordinal);
        Assert.AreEqual(before, await File.ReadAllTextAsync(ConfigurationPath, _timeout.Token));
    }

    [TestMethod]
    public async Task UpsertBackupSet_ARootContainingTheArchivesRoot_IsRefused()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var refused = await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                new string('b', 32), "archives", _harness.ArchivesRoot, "every 4h", [], [], ["vault"])),
            _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(refused, out var error);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, error.Reason);
        Assert.Contains("archives", error.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ValidateSetDraft_ARootContainingADeclaredDestination_NamesTheDefect()
    {
        // The editor's live surface: a defect, not a warning, because the
        // save it previews would be refused (defects refuse, warnings advise).
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var inside = Path.Combine(_harness.WorkPath, "vault2");
        Directory.CreateDirectory(inside);
        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertDestinationCommand(new DestinationDescriptor(
                null, "vault2", "local-path", inside, null, null)),
            _timeout.Token));

        Assert.IsInstanceOfType<SetDraftValidationResult>(
            await handler.ExecuteAsync(
                new ValidateSetDraftCommand("every 4h", [], [], [_harness.WorkPath]), _timeout.Token),
            out var flagged);
        Assert.IsTrue(flagged.Defects.Any(defect => defect.Contains("vault2", StringComparison.Ordinal)));

        // And the same draft with the exclude in place is clean.
        Assert.IsInstanceOfType<SetDraftValidationResult>(
            await handler.ExecuteAsync(
                new ValidateSetDraftCommand("every 4h", [], ["vault2"], [_harness.WorkPath]), _timeout.Token),
            out var clean);
        Assert.IsEmpty(clean.Defects);
    }

    [TestMethod]
    public async Task UpsertConfiguration_CarryingPriorities_RoundTripsAndPreservesOnSilence()
    {
        // Contract 1.17 (ADR-0047): priorities ride the descriptors; a
        // pre-1.17 upsert says nothing and must change nothing.
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var vault = ClientConfiguration.Load(ConfigurationPath).FindDestination("vault")!;
        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertDestinationCommand(new DestinationDescriptor(
                vault.Id, "vault", "local-path", vault.Path, null, null, Priority: 7)),
            _timeout.Token));
        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 1h", [], [], ["vault"], Priority: 5)),
            _timeout.Token));

        Assert.IsInstanceOfType<DestinationsResult>(
            await handler.ExecuteAsync(new ListDestinationsCommand(), _timeout.Token), out var destinations);
        Assert.AreEqual(7, Assert.ContainsSingle(destinations.Destinations).Priority);

        Assert.IsInstanceOfType<BackupSetsResult>(
            await handler.ExecuteAsync(new ListBackupSetsCommand(), _timeout.Token), out var sets);
        Assert.AreEqual(5, Assert.ContainsSingle(sets.Sets).Priority);

        // Silence preserves: an upsert that says nothing about priority.
        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 2h", [], [], ["vault"])),
            _timeout.Token));
        Assert.AreEqual(5, ClientConfiguration.Load(ConfigurationPath).FindSet("docs")!.Priority);
    }

    [TestMethod]
    public async Task UpsertBackupSet_ANewSet_QueuesItsFirstCaptureAndFanOut()
    {
        // The owner's rule (ADR-0047): saving a new set IS asking for its
        // first backup — the destination populates without waiting for a
        // schedule or a person remembering to run one.
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));
        var photos = Path.Combine(_harness.WorkPath, "photos-src");
        Directory.CreateDirectory(photos);
        await File.WriteAllTextAsync(Path.Combine(photos, "one.jpg"), "pixels", _timeout.Token);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var photosSetId = new string('c', 32);

        var result = await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                photosSetId, "photos", photos, "every 4h", [], [], ["vault"])),
            _timeout.Token);

        Assert.IsInstanceOfType<ConfigurationChangeResult>(result, out var change);
        Assert.IsTrue(
            change.Lines.Any(line => line.Contains("First backup", StringComparison.Ordinal)),
            "the reply must say the first backup was queued");

        while (!runtime.Jobs.Jobs.Any(job =>
            job.BackupSetId == photosSetId && job.State == FallbackPlan.Domain.Jobs.JobState.Complete))
        {
            await Task.Delay(50, _timeout.Token);
        }

        while (runtime.DestinationSync.Find(photosSetId, "vault")?.LastSuccessAt is null)
        {
            await Task.Delay(50, _timeout.Token);
        }
    }

    [TestMethod]
    public async Task UpsertBackupSet_AnExistingSetGainingADestination_QueuesItsSync()
    {
        // Same rule from the other side: referencing a destination IS asking
        // it to hold the set — the seed starts now, flagged needs-full until
        // the copy lands.
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));
        var second = Path.Combine(_harness.WorkPath, "second-vault");
        Directory.CreateDirectory(second);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertDestinationCommand(new DestinationDescriptor(
                null, "second-vault", "local-path", second, null, null)),
            _timeout.Token));

        var result = await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 1h", [], [], ["vault", "second-vault"])),
            _timeout.Token);

        Assert.IsInstanceOfType<ConfigurationChangeResult>(result, out var change);
        Assert.IsTrue(
            change.Lines.Any(line => line.Contains("second-vault", StringComparison.Ordinal)),
            "the reply must name the destination whose seed was queued");

        while (runtime.DestinationSync.Find(_harness.DocsSetId, "second-vault")?.LastSuccessAt is null)
        {
            await Task.Delay(50, _timeout.Token);
        }
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            passphrase,
            _timeout.Token);
    }
}
