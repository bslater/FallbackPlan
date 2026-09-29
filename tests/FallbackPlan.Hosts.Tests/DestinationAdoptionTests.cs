using System.CommandLine;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;
using RestoreResult = FallbackPlan.Api.RestoreResult;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Adopting a destination's archives after a rebuild (ADR-0061; FR-WOR-006,
/// FR-DR-006, FR-DR-009). A fresh installation pointed at an existing
/// destination lists the archives it holds by descriptor alone and adopts one
/// under its original repository and set ids with the passphrase, re-declaring
/// the set from the shape the archive records, its own retention included —
/// and the next backup is incremental against the replica rather than a
/// re-seed. Nothing takes effect until that shape has been shown and
/// confirmed: a preview writes nothing, an adoption without its confirmation
/// is refused, and one confirmed against an archive that has since changed is
/// refused as changed. The drill is the one <c>eng/recovery-drill.sh</c>
/// step 8 runs on the Release binaries.
/// </summary>
[TestClass]
public sealed class DestinationAdoptionTests : IDisposable
{
    private const string Vault = "vault";
    private const string ExcludeRule = "**/*.tmp";

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(4));

    private CancellationToken Timeout => _timeout.Token;

    private string VaultPath => Path.Combine(_harness.WorkPath, Vault);

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task Adopt_AfterTheMachineIsGone_ResumesTheSetUnderItsOriginalIdAndShape()
    {
        _harness.WriteSourceFile("docs/notes.txt", "the first words");
        WriteIncompressible("docs/big.bin");
        _harness.WriteSourceFile("docs/scratch.tmp", "never captured");
        var replica = await BackUpThenLoseTheMachineAsync();
        var blobBytesBefore = BlobBytes(replica);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        // Discovery reads descriptors and cleartext counters only: one
        // archive, nobody's yet, written under another installation's salt.
        Assert.IsInstanceOfType<ArchivesDiscoveredResult>(
            await handler.ExecuteAsync(new DiscoverArchivesCommand(Vault), Timeout), out var discovered);
        var row = Assert.ContainsSingle(discovered.Archives);
        Assert.AreEqual(Path.GetFileName(replica), row.RepositoryId);
        Assert.IsNull(row.OwnedBySet);
        Assert.IsFalse(row.SameInstallation, "the rebuilt installation minted its own salt");
        Assert.AreEqual(1, row.SnapshotObjects);
        Assert.IsTrue(row.HighestPublicationSequence > 0);

        Assert.IsInstanceOfType<ArchiveAdoptedResult>(
            await AdoptConfirmedAsync(
                handler, new AdoptArchiveCommand(Vault, row.RepositoryId, await EnvelopeForAsync(handler, row, PassphraseText))),
            out var adopted, "adoption refused");

        // The set comes back as it was declared: id, name, root, schedule,
        // rules — from the archive, not from anything typed here.
        Assert.AreEqual(_harness.DocsSetId, adopted.SetId);
        Assert.AreEqual("docs", adopted.SetName);
        Assert.AreEqual(row.RepositoryId, adopted.RepositoryId);
        Assert.AreEqual(_harness.SourceRoot, Assert.ContainsSingle(adopted.Roots).Path);
        Assert.IsEmpty(adopted.MissingRoots);
        Assert.AreEqual("every 1h", adopted.Schedule);
        Assert.AreEqual(ExcludeRule, Assert.ContainsSingle(adopted.ExcludeRules));
        Assert.AreEqual(1, adopted.SnapshotCount);
        Assert.IsTrue(adopted.WriterIdentityResumed, "a never-published installation resumes the archive's writer");
        Assert.IsFalse(adopted.AlreadyAdopted);

        // What the service now holds: the per-set credential, a metadata
        // store with no content, the catalogue at the runtime's real path,
        // the set appended as direct-ship, and a ledger row that admits the
        // destination without owing it a full copy.
        using (var credential = runtime.WriteCredentials.TryLoad(_harness.DocsSetId))
        {
            Assert.IsNotNull(credential, "no per-set credential stored");
        }

        var metadata = runtime.SetMetadataPath(_harness.DocsSetId);
        Assert.IsTrue(File.Exists(Path.Combine(metadata, "repository-format")));
        Assert.IsFalse(Directory.Exists(Path.Combine(metadata, "blobs")), "content must never land locally");
        Assert.IsTrue(File.Exists(Path.Combine(_harness.StateDirectory, $"catalogue-{row.RepositoryId}.db")));

        var set = Assert.ContainsSingle(runtime.Configuration.BackupSets);
        Assert.AreEqual(_harness.DocsSetId, set.Id);
        Assert.IsTrue(set.DirectShip);
        Assert.AreEqual(Vault, Assert.ContainsSingle(set.Destinations).Ref);

        var ledger = runtime.DestinationSync.Find(_harness.DocsSetId, Vault);
        Assert.IsNotNull(ledger);
        Assert.IsNotNull(ledger.BaselineCompletedAt);
        Assert.IsFalse(ledger.NeedsFull);
        Assert.IsTrue(ledger.SyncedSequence > 0);

        // The proof: change one file and touch the big one — same bytes, new
        // modification time, so it is re-read and re-segmented rather than
        // carried forward by identity — and only the change ships. Under a
        // fresh writer identity the device dedup domain would never reuse
        // the old writer's segments (ADR-0006), and the big file would ship
        // whole again.
        _harness.WriteSourceFile("docs/notes.txt", "the second words");
        File.SetLastWriteTimeUtc(WriteIncompressible("docs/big.bin"), DateTime.UtcNow.AddMinutes(1));
        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);

        Assert.AreEqual(replica, Assert.ContainsSingle(Directory.GetDirectories(VaultPath)), "a second archive was born");
        Assert.AreEqual(2, Directory.GetFiles(Path.Combine(replica, "snapshots"), "*", SearchOption.AllDirectories).Length);
        var grown = BlobBytes(replica) - blobBytesBefore;
        Assert.IsTrue(grown < 64 * 1024, $"the incremental run shipped {grown} bytes — the writer identity was not resumed");

        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        Assert.HasCount(2, listed.Snapshots);
        Assert.IsTrue(listed.Snapshots.All(snapshot => snapshot.BackupSetId == _harness.DocsSetId));
        var newest = listed.Snapshots.MaxBy(snapshot => snapshot.CapturedAt)!.SnapshotId;

        var output = Path.Combine(_harness.WorkPath, "restored");
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    newest, null, output,
                    Source: (await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, Timeout)).SourceId),
                Timeout),
            out var restored);
        Assert.AreEqual("complete", restored.Outcome);
        var recovered = Assert.ContainsSingle(Directory.GetFiles(output, "notes.txt", SearchOption.AllDirectories));
        Assert.AreEqual("the second words", await File.ReadAllTextAsync(recovered, Timeout));
    }

    [TestMethod]
    public async Task Adopt_AfterTheMachineIsGone_BringsBackTheSetsOwnRetentionAndNotADestinationsOverride()
    {
        // FR-DR-006: what the set was allowed to delete comes back with it.
        // The destination's override does not: it names a destination, the
        // repository never carries destination identities (FR-DEST-006), and
        // it is re-declared with the destination, by hand.
        _harness.WriteSourceFile("docs/notes.txt", "the first words");
        await BackUpThenLoseTheMachineAsync(
            retention: new RetentionConfiguration { KeepDaily = 7, KeepMonthly = 12, MinGenerations = 3 },
            destinationRetention: new RetentionConfiguration { KeepDaily = 2 });

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var row = await DiscoverSingleAsync(handler);

        Assert.IsInstanceOfType<ArchiveAdoptedResult>(
            await AdoptConfirmedAsync(
                handler, new AdoptArchiveCommand(Vault, row.RepositoryId, await EnvelopeForAsync(handler, row, PassphraseText))),
            out var adopted, "adoption refused");

        Assert.AreEqual(
            new RetentionPolicyDescriptor(KeepDaily: 7, KeepMonthly: 12, MinGenerations: 3), adopted.Retention);

        var set = Assert.ContainsSingle(runtime.Configuration.BackupSets);
        Assert.AreEqual(
            new RetentionConfiguration { KeepDaily = 7, KeepMonthly = 12, MinGenerations = 3 }, set.Retention);
        Assert.IsNull(
            Assert.ContainsSingle(set.Destinations).Retention,
            "a destination's retention override came back from the repository");
    }

    [TestMethod]
    public async Task Preview_AfterTheMachineIsGone_ShowsTheRecordedShapeWithEachRootFlagged_AndWritesNothing()
    {
        // FR-DR-009: what an adoption would declare is shown before it takes
        // effect — each root's recorded path as a hint and whether it
        // resolves here, the schedule, the rules and what the set would
        // delete by — and showing it changes nothing on this machine.
        _harness.WriteSourceFile("docs/notes.txt", "the first words");
        await BackUpThenLoseTheMachineAsync(
            retention: new RetentionConfiguration { KeepDaily = 7, KeepMonthly = 12, MinGenerations = 3 });

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var row = await DiscoverSingleAsync(handler);
        var envelope = await EnvelopeForAsync(handler, row, PassphraseText);
        var writerBefore = runtime.State.WriterId.ToArray();
        var before = StateDirectoryFingerprint();

        Assert.IsInstanceOfType<AdoptionPreviewResult>(
            await handler.ExecuteAsync(new PreviewAdoptionCommand(Vault, row.RepositoryId, envelope), Timeout),
            out var preview, "the preview was refused");

        Assert.AreEqual(Vault, preview.DestinationName);
        Assert.AreEqual(row.RepositoryId, preview.RepositoryId);
        Assert.AreEqual(_harness.DocsSetId, preview.SetId);
        Assert.AreEqual("docs", preview.SetName);
        var root = Assert.ContainsSingle(preview.Roots);
        Assert.AreEqual(_harness.SourceRoot, root.RecordedPath);
        Assert.IsTrue(root.Resolves, "a folder this machine has was shown as missing");
        Assert.AreEqual("every 1h", preview.Schedule);
        Assert.AreEqual(ExcludeRule, Assert.ContainsSingle(preview.ExcludeRules));
        Assert.AreEqual(
            new RetentionPolicyDescriptor(KeepDaily: 7, KeepMonthly: 12, MinGenerations: 3), preview.Retention);
        Assert.AreEqual(1, preview.SnapshotCount);
        Assert.IsNotNull(preview.NewestSnapshotId);
        Assert.IsFalse(preview.AlreadyAdopted);
        Assert.IsFalse(string.IsNullOrWhiteSpace(preview.Confirmation), "the preview carries no confirmation");
        Assert.IsTrue(
            preview.Lines.Any(line => line.Contains("7 daily", StringComparison.Ordinal)),
            "the retention the set would delete by is not said in words: " + string.Join(" | ", preview.Lines));

        // Nothing took effect: no set, credential, metadata, catalogue or
        // ledger row, the writer identity untouched, and the state directory
        // as it was, file for file.
        CollectionAssert.AreEqual(before, StateDirectoryFingerprint(), "a preview wrote into the state directory");
        Assert.IsEmpty(runtime.Configuration.BackupSets);
        Assert.IsNull(runtime.WriteCredentials.TryLoad(_harness.DocsSetId));
        Assert.IsNull(runtime.DestinationSync.Find(_harness.DocsSetId, Vault));
        Assert.IsTrue(writerBefore.AsSpan().SequenceEqual(runtime.State.WriterId), "a preview adopted the writer identity");

        // A preview of an unchanged archive is repeatable: the same shape,
        // the same confirmation.
        Assert.IsInstanceOfType<AdoptionPreviewResult>(
            await handler.ExecuteAsync(new PreviewAdoptionCommand(Vault, row.RepositoryId, envelope), Timeout),
            out var again);
        Assert.AreEqual(preview.Confirmation, again.Confirmation);
    }

    [TestMethod]
    public async Task Preview_ARecordedRootThisMachineDoesNotHave_IsFlagged_AndTheConfirmedSetCapturesNothingFromIt()
    {
        // After a total loss the recorded folders are often not there until
        // the person restores into them. The preview flags the path; the set
        // is adopted as confirmed, schedule included, and a run refuses the
        // missing root by name rather than capturing it as empty — which
        // would read every file under it as deleted (ADR-0040).
        _harness.WriteSourceFile("docs/notes.txt", "words");
        var replica = await BackUpThenLoseTheMachineAsync();
        Directory.Delete(_harness.SourceRoot, recursive: true);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var row = await DiscoverSingleAsync(handler);
        var (preview, result) = await HostHarness.PreviewThenAdoptAsync(
            handler.ExecuteAsync,
            new AdoptArchiveCommand(Vault, row.RepositoryId, await EnvelopeForAsync(handler, row, PassphraseText)),
            Timeout);

        var root = Assert.ContainsSingle(preview.Roots);
        Assert.AreEqual(_harness.SourceRoot, root.RecordedPath);
        Assert.IsFalse(root.Resolves, "a folder this machine does not have was shown as present");
        Assert.IsTrue(
            preview.Lines.Any(line => line.Contains(_harness.SourceRoot, StringComparison.Ordinal)),
            "the missing folder is not named: " + string.Join(" | ", preview.Lines));

        Assert.IsInstanceOfType<ArchiveAdoptedResult>(result, out var adopted, (result as ServiceError)?.Message);
        Assert.AreEqual(_harness.SourceRoot, Assert.ContainsSingle(adopted.MissingRoots));
        var set = Assert.ContainsSingle(runtime.Configuration.BackupSets);
        Assert.AreEqual("every 1h", set.Schedule, "the confirmed schedule was not kept");

        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("failed", outcome.Outcome);
        Assert.Contains(_harness.SourceRoot, outcome.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.AreEqual(
            1, Directory.GetFiles(Path.Combine(replica, "snapshots"), "*", SearchOption.AllDirectories).Length,
            "a snapshot was captured from a folder that is not there");
    }

    [TestMethod]
    public async Task Adopt_WithoutAConfirmation_IsRefusedNamingThePreview_AndWritesNothing()
    {
        // "Reconstruction cannot complete without confirming the roots" holds
        // on the wire, not only in a front end: a client that skips the
        // preview is refused before anything is opened or written.
        _harness.WriteSourceFile("docs/notes.txt", "words");
        var replica = await BackUpThenLoseTheMachineAsync();

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var row = await DiscoverSingleAsync(handler);
        var before = StateDirectoryFingerprint();

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new AdoptArchiveCommand(Vault, row.RepositoryId, await EnvelopeForAsync(handler, row, PassphraseText)),
                Timeout),
            out var refused);
        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains("preview_adoption", refused.Message, StringComparison.Ordinal);

        CollectionAssert.AreEqual(before, StateDirectoryFingerprint(), "a refused adoption wrote into the state directory");
        Assert.IsEmpty(runtime.Configuration.BackupSets);
        Assert.IsNull(runtime.WriteCredentials.TryLoad(_harness.DocsSetId));
        Assert.IsFalse(Directory.Exists(runtime.SetMetadataPath(_harness.DocsSetId)));
        Assert.IsFalse(File.Exists(Path.Combine(_harness.StateDirectory, $"catalogue-{Path.GetFileName(replica)}.db")));
    }

    [TestMethod]
    public async Task Adopt_ConfirmedAgainstAShapeTheArchiveHasSinceMovedOn_IsRefusedAsChanged_AndWritesNothing()
    {
        // The destination confirmed against is not the one previewed: in
        // between, the old machine — still alive somewhere — backed up once
        // more under a new schedule. What would take effect is no longer what
        // was shown, so nothing does; previewing again shows what is there
        // now, and that confirms.
        _harness.WriteSourceFile("docs/notes.txt", "the first words");
        Directory.CreateDirectory(VaultPath);
        WriteConfiguration(_harness, withDocsSet: true);
        await _harness.SetupAsync();
        var asPreviewed = Path.Combine(_harness.WorkPath, "vault-as-previewed");
        var asConfirmed = Path.Combine(_harness.WorkPath, "vault-as-confirmed");
        string replica;
        await using (var original = await ServiceRuntime.StartAsync(OptionsFor(_harness), Timeout))
        {
            var set = original.Configuration.BackupSets.Single();
            var first = await Scheduler.Enqueue(original, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
            Assert.AreEqual("ran", first.Outcome, first.Detail);
            replica = Assert.ContainsSingle(Directory.GetDirectories(VaultPath));
            CopyTree(replica, asPreviewed);

            (original.Configuration with { BackupSets = [set with { Schedule = "every 6h" }] })
                .Save(original.ConfigurationPath);
            _harness.WriteSourceFile("docs/notes.txt", "the second words");
            var edited = original.Configuration.BackupSets.Single();
            var second = await Scheduler.Enqueue(original, edited, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
            Assert.AreEqual("ran", second.Outcome, second.Detail);
            CopyTree(replica, asConfirmed);
        }

        LoseTheMachine();
        ReplaceTree(asPreviewed, replica);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var row = await DiscoverSingleAsync(handler);
        var envelope = await EnvelopeForAsync(handler, row, PassphraseText);
        Assert.IsInstanceOfType<AdoptionPreviewResult>(
            await handler.ExecuteAsync(new PreviewAdoptionCommand(Vault, row.RepositoryId, envelope), Timeout),
            out var preview, "the preview was refused");
        Assert.AreEqual("every 1h", preview.Schedule);
        Assert.AreEqual(1, preview.SnapshotCount);

        ReplaceTree(asConfirmed, replica);
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new AdoptArchiveCommand(Vault, row.RepositoryId, envelope, Confirmation: preview.Confirmation), Timeout),
            out var refused);
        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains("changed since", refused.Message, StringComparison.Ordinal);

        Assert.IsEmpty(runtime.Configuration.BackupSets);
        Assert.IsNull(runtime.WriteCredentials.TryLoad(_harness.DocsSetId));
        Assert.IsFalse(Directory.Exists(runtime.SetMetadataPath(_harness.DocsSetId)));
        Assert.IsFalse(File.Exists(Path.Combine(_harness.StateDirectory, $"catalogue-{row.RepositoryId}.db")));
        Assert.IsNull(runtime.DestinationSync.Find(_harness.DocsSetId, Vault));

        var (current, result) = await HostHarness.PreviewThenAdoptAsync(
            handler.ExecuteAsync, new AdoptArchiveCommand(Vault, row.RepositoryId, envelope), Timeout);
        Assert.AreEqual("every 6h", current.Schedule);
        Assert.AreEqual(2, current.SnapshotCount);
        Assert.AreNotEqual(preview.Confirmation, current.Confirmation);
        Assert.IsInstanceOfType<ArchiveAdoptedResult>(result, out var adopted, (result as ServiceError)?.Message);
        Assert.AreEqual("every 6h", adopted.Schedule);
    }

    [TestMethod]
    public async Task Adopt_WithARootRepointedAtConfirmation_TakesEffectWithThePathConfirmed()
    {
        // A rebuilt machine is often laid out differently: the folder the
        // archive recorded now lives somewhere else. The person re-points it
        // when confirming, the label the snapshots already carry is kept, and
        // the next backup captures from the confirmed path.
        _harness.WriteSourceFile("docs/notes.txt", "the first words");
        var replica = await BackUpThenLoseTheMachineAsync();
        var moved = Path.Combine(_harness.WorkPath, "documents-on-the-new-machine");
        Directory.Move(_harness.SourceRoot, moved);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var row = await DiscoverSingleAsync(handler);
        var envelope = await EnvelopeForAsync(handler, row, PassphraseText);
        Assert.IsInstanceOfType<AdoptionPreviewResult>(
            await handler.ExecuteAsync(new PreviewAdoptionCommand(Vault, row.RepositoryId, envelope), Timeout),
            out var preview, "the preview was refused");
        var recorded = Assert.ContainsSingle(preview.Roots);
        Assert.IsFalse(recorded.Resolves);

        Assert.IsInstanceOfType<ArchiveAdoptedResult>(
            await handler.ExecuteAsync(
                new AdoptArchiveCommand(
                    Vault, row.RepositoryId, envelope,
                    Roots: [new BackupRootDescriptor(moved, recorded.Label)],
                    Confirmation: preview.Confirmation),
                Timeout),
            out var adopted);
        Assert.AreEqual(moved, Assert.ContainsSingle(adopted.Roots).Path);
        Assert.IsEmpty(adopted.MissingRoots);

        var set = Assert.ContainsSingle(runtime.Configuration.BackupSets);
        Assert.AreEqual(moved, Assert.ContainsSingle(set.Roots).Path);
        await File.WriteAllTextAsync(Path.Combine(moved, "docs", "notes.txt"), "the second words", Timeout);
        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
        Assert.AreEqual(2, Directory.GetFiles(Path.Combine(replica, "snapshots"), "*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task Adopt_WithTheWrongPassphrase_IsRefusedBeforeAnythingIsStored()
    {
        _harness.WriteSourceFile("docs/notes.txt", "words");
        var replica = await BackUpThenLoseTheMachineAsync();

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var row = await DiscoverSingleAsync(handler);

        var wrongEnvelope = await EnvelopeForAsync(handler, row, "not the passphrase this archive was born from");

        // Refused where it is first sent — the preview proves the key exactly
        // as adoption does — and again by adoption itself, which proves the
        // key before it looks at the confirmation at all.
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new PreviewAdoptionCommand(Vault, row.RepositoryId, wrongEnvelope), Timeout),
            out var refusedPreview);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, refusedPreview.Reason);
        Assert.Contains("does not match", refusedPreview.Message, StringComparison.Ordinal);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new AdoptArchiveCommand(Vault, row.RepositoryId, wrongEnvelope, Confirmation: new string('0', 64)),
                Timeout),
            out var refused);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, refused.Reason);
        Assert.Contains("does not match", refused.Message, StringComparison.Ordinal);

        Assert.IsFalse(Directory.Exists(runtime.SetMetadataPath(_harness.DocsSetId)));
        Assert.IsNull(runtime.WriteCredentials.TryLoad(_harness.DocsSetId));
        Assert.IsEmpty(runtime.Configuration.BackupSets);
        Assert.IsNull(runtime.DestinationSync.Find(_harness.DocsSetId, Vault));
        Assert.IsFalse(File.Exists(Path.Combine(_harness.StateDirectory, $"catalogue-{Path.GetFileName(replica)}.db")));
    }

    [TestMethod]
    public async Task Adopt_Twice_IsAcknowledgedNotRepeated()
    {
        _harness.WriteSourceFile("docs/notes.txt", "words");
        await BackUpThenLoseTheMachineAsync();

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var row = await DiscoverSingleAsync(handler);
        var envelope = await EnvelopeForAsync(handler, row, PassphraseText);

        Assert.IsInstanceOfType<ArchiveAdoptedResult>(
            await AdoptConfirmedAsync(handler, new AdoptArchiveCommand(Vault, row.RepositoryId, envelope)),
            out var first);
        Assert.IsFalse(first.AlreadyAdopted);

        var (preview, secondResult) = await HostHarness.PreviewThenAdoptAsync(
            handler.ExecuteAsync, new AdoptArchiveCommand(Vault, row.RepositoryId, envelope), Timeout);
        Assert.IsTrue(preview.AlreadyAdopted, "the second preview does not say the archive is already adopted");
        Assert.IsInstanceOfType<ArchiveAdoptedResult>(secondResult, out var second);
        Assert.IsTrue(second.AlreadyAdopted);
        Assert.AreEqual(first.SetId, second.SetId);
        Assert.ContainsSingle(runtime.Configuration.BackupSets);

        // And discovery now names the owner.
        Assert.AreEqual("docs", (await DiscoverSingleAsync(handler)).OwnedBySet);
    }

    [TestMethod]
    public async Task Adopt_AForeignInstallationsArchive_IsDiscoveredAndRefusedByName()
    {
        _harness.WriteSourceFile("docs/notes.txt", "ours");
        var ours = await BackUpThenLoseTheMachineAsync();

        // Another installation, another passphrase, the same drive.
        using var foreign = new HostHarness();
        Environment.SetEnvironmentVariable(foreign.PassphraseVariable, "A different Passphrase 43 of the neighbour!");
        foreign.WriteSourceFile("docs/theirs.txt", "theirs");
        WriteConfiguration(foreign, withDocsSet: true);
        await foreign.SetupAsync();
        await using (var theirs = await ServiceRuntime.StartAsync(OptionsFor(foreign), Timeout))
        {
            var set = theirs.Configuration.BackupSets.Single();
            Assert.AreEqual(
                "ran", (await Scheduler.Enqueue(theirs, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout)).Outcome);
        }

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ArchivesDiscoveredResult>(
            await handler.ExecuteAsync(new DiscoverArchivesCommand(Vault), Timeout), out var discovered);
        Assert.HasCount(2, discovered.Archives);
        var foreignRow = discovered.Archives.Single(row => row.RepositoryId != Path.GetFileName(ours));

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new PreviewAdoptionCommand(
                    Vault, foreignRow.RepositoryId, await EnvelopeForAsync(handler, foreignRow, PassphraseText)),
                Timeout),
            out var refused);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, refused.Reason);
        Assert.Contains("does not match", refused.Message, StringComparison.Ordinal);
        Assert.IsEmpty(runtime.Configuration.BackupSets);
    }

    [TestMethod]
    public async Task Discover_AnEmptyOrUnknownDestination_AnswersHonestly()
    {
        Directory.CreateDirectory(VaultPath);
        WriteConfiguration(_harness, withDocsSet: false);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ArchivesDiscoveredResult>(
            await handler.ExecuteAsync(new DiscoverArchivesCommand(Vault), Timeout), out var empty);
        Assert.IsEmpty(empty.Archives);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new DiscoverArchivesCommand("nowhere"), Timeout), out var unknown);
        Assert.AreEqual(ServiceErrorReason.NotFound, unknown.Reason);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new AdoptArchiveCommand(Vault, new string('f', 32), "00"), Timeout),
            out var absent);
        Assert.AreEqual(ServiceErrorReason.NotFound, absent.Reason);
        Assert.Contains("holds no archive", absent.Message, StringComparison.Ordinal);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new PreviewAdoptionCommand(Vault, new string('f', 32), "00"), Timeout),
            out var absentPreview);
        Assert.AreEqual(ServiceErrorReason.NotFound, absentPreview.Reason);
        Assert.Contains("holds no archive", absentPreview.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Discover_AnUnreachablePeer_IsUnavailableNotACrash()
    {
        Directory.CreateDirectory(VaultPath);
        var path = Path.Combine(_harness.StateDirectory, "config.json");
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('d', 32), Name = "friend", Kind = DestinationKind.Peer,
                    Fingerprint = new string('f', 64), Endpoint = "friend.example:7777",
                },
            ],
        }.Save(path);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        // Declared but never paired, and at an address nothing answers: the
        // peer half of ADR-0061 dials, and what it learns is said as
        // unavailability rather than surfacing as an exception.
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new DiscoverArchivesCommand("friend"), Timeout), out var refused);
        Assert.AreEqual(ServiceErrorReason.Unavailable, refused.Reason);
        Assert.Contains("friend", refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Adopt_AnArchiveThatRecordsNoName_NeedsOneAndTakesTheRestFromTheArchive()
    {
        // The CLI's direct mode writes an archive for no configured set: it
        // records the roots it walked and nothing else, so adoption must be
        // told the name — and only the name.
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("docs/notes.txt", "from the command line");
        await _harness.BackUpAsync();
        var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(
            new LocalFileSystemObjectStore(_harness.RepositoryPath), Timeout);
        var replica = Path.Combine(VaultPath, descriptor.RepositoryId.ToString());
        CopyTree(_harness.RepositoryPath, replica);
        LoseTheMachine();

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var row = await DiscoverSingleAsync(handler);
        var envelope = await EnvelopeForAsync(handler, row, PassphraseText);

        // The preview says what is missing before anything is asked of it.
        var (preview, unnamedResult) = await HostHarness.PreviewThenAdoptAsync(
            handler.ExecuteAsync, new AdoptArchiveCommand(Vault, row.RepositoryId, envelope), Timeout);
        Assert.IsNull(preview.SetName);
        Assert.AreEqual(_harness.SourceRoot, Assert.ContainsSingle(preview.Roots).RecordedPath);
        Assert.IsTrue(
            preview.Lines.Any(line => line.Contains("name", StringComparison.Ordinal)),
            string.Join(" | ", preview.Lines));

        Assert.IsInstanceOfType<ServiceError>(unnamedResult, out var unnamed);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, unnamed.Reason);
        Assert.Contains("name", unnamed.Message, StringComparison.Ordinal);
        Assert.IsEmpty(runtime.Configuration.BackupSets);

        Assert.IsInstanceOfType<ArchiveAdoptedResult>(
            await AdoptConfirmedAsync(
                handler, new AdoptArchiveCommand(Vault, row.RepositoryId, envelope, SetName: "from-the-cli")),
            out var adopted);
        Assert.AreEqual("from-the-cli", adopted.SetName);
        Assert.AreEqual(_harness.SourceRoot, Assert.ContainsSingle(adopted.Roots).Path);
        Assert.IsNull(adopted.Schedule);
        Assert.AreEqual("from-the-cli", Assert.ContainsSingle(runtime.Configuration.BackupSets).Name);
    }

    [TestMethod]
    public async Task Adopt_WhenThisInstallationAlreadyPublishes_KeepsItsWriterIdentityAndSaysSo()
    {
        _harness.WriteSourceFile("docs/notes.txt", "words");
        var docsReplica = await BackUpThenLoseTheMachineAsync();

        // The rebuilt machine has already backed another set up before it
        // adopts: its writer identity is in use, so the archive's is not
        // resumed — the next run re-sends once, and the answer says so.
        var otherSource = Path.Combine(_harness.WorkPath, "other-source");
        Directory.CreateDirectory(otherSource);
        await File.WriteAllTextAsync(Path.Combine(otherSource, "other.txt"), "another set's bytes", Timeout);
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var configuration = runtime.Configuration;
        (configuration with
        {
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = new string('b', 32), Name = "other",
                    Roots = [new BackupRootConfiguration { Path = otherSource }],
                    Destinations = [new SetDestinationReference { Ref = Vault }], DirectShip = true,
                },
            ],
        }).Save(runtime.ConfigurationPath);
        var other = runtime.Configuration.BackupSets.Single();
        Assert.AreEqual(
            "ran", (await Scheduler.Enqueue(runtime, other, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout)).Outcome);

        Assert.IsInstanceOfType<ArchivesDiscoveredResult>(
            await handler.ExecuteAsync(new DiscoverArchivesCommand(Vault), Timeout), out var discovered);
        Assert.HasCount(2, discovered.Archives);
        Assert.AreEqual("other", discovered.Archives.Single(row => row.RepositoryId != Path.GetFileName(docsReplica)).OwnedBySet);
        var docsRow = discovered.Archives.Single(row => row.RepositoryId == Path.GetFileName(docsReplica));
        Assert.IsNull(docsRow.OwnedBySet);

        Assert.IsInstanceOfType<ArchiveAdoptedResult>(
            await AdoptConfirmedAsync(
                handler,
                new AdoptArchiveCommand(Vault, docsRow.RepositoryId, await EnvelopeForAsync(handler, docsRow, PassphraseText))),
            out var adopted);
        Assert.IsFalse(adopted.WriterIdentityResumed);
        Assert.IsTrue(
            adopted.Lines.Any(line => line.Contains("writer identity", StringComparison.Ordinal)),
            string.Join(" | ", adopted.Lines));
        Assert.HasCount(2, runtime.Configuration.BackupSets);

        // The adopted set still runs, under the new writer: two writers in
        // one repository is a supported shape, slow rather than wrong.
        var docs = runtime.Configuration.BackupSets.Single(set => set.Id == _harness.DocsSetId);
        _harness.WriteSourceFile("docs/notes.txt", "changed words");
        Assert.AreEqual(
            "ran", (await Scheduler.Enqueue(runtime, docs, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout)).Outcome);
        Assert.AreEqual(2, Directory.GetFiles(Path.Combine(docsReplica, "snapshots"), "*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task Cli_DiscoversAdoptsBacksUpAndRestores_ThroughTheLocalService()
    {
        // The headless road back (ADR-0061 §6): the same drill the recovery
        // script runs on the Release binaries, here against an in-process
        // service. The CLI derives against the discovered archive's salt and
        // sends only the sealed envelope; its routed restore then derives the
        // grant PER SET (contract 1.30), because the adopted set's salt is
        // not the rebuilt installation's.
        _harness.WriteSourceFile("docs/notes.txt", "the first words");
        WriteIncompressible("docs/big.bin");
        var replica = await BackUpThenLoseTheMachineAsync();
        var blobBytesBefore = BlobBytes(replica);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var discovered = await RunCliAsync("discover", "--destination", Vault, "--state", _harness.StateDirectory);
        Assert.AreEqual(0, discovered.ExitCode, discovered.All);
        Assert.Contains(Path.GetFileName(replica), discovered.Output, StringComparison.Ordinal);
        Assert.Contains("nobody yet", discovered.Output, StringComparison.Ordinal);

        var wrong = "FBP_WRONG_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(wrong, "not the passphrase this archive was born from");
        try
        {
            var refused = await RunCliAsync(
                "adopt", "--destination", Vault, "--repository", Path.GetFileName(replica),
                "--state", _harness.StateDirectory, "--passphrase-env", wrong);
            Assert.AreEqual(1, refused.ExitCode, refused.All);
            Assert.Contains("Nothing was sent", refused.Error, StringComparison.Ordinal);
            Assert.IsEmpty(runtime.Configuration.BackupSets, "a refused derivation must reach the service with nothing");
        }
        finally
        {
            Environment.SetEnvironmentVariable(wrong, null);
        }

        // Without --confirm the verb shows what the archive recorded and
        // adopts nothing (FR-DR-009): the exit code says the operator has
        // something to look at, and the service holds no set.
        var previewed = await RunCliAsync(
            "adopt", "--destination", Vault, "--repository", Path.GetFileName(replica),
            "--state", _harness.StateDirectory, "--passphrase-env", _harness.PassphraseVariable);
        Assert.AreEqual(2, previewed.ExitCode, previewed.All);
        Assert.Contains(_harness.SourceRoot, previewed.Output, StringComparison.Ordinal);
        Assert.Contains("every 1h", previewed.Output, StringComparison.Ordinal);
        Assert.Contains("--confirm", previewed.Output, StringComparison.Ordinal);
        Assert.Contains("Nothing was adopted", previewed.Output, StringComparison.Ordinal);
        Assert.IsEmpty(runtime.Configuration.BackupSets, "a preview must not adopt");

        var adopted = await RunCliAsync(
            "adopt", "--destination", Vault, "--repository", Path.GetFileName(replica),
            "--state", _harness.StateDirectory, "--passphrase-env", _harness.PassphraseVariable, "--confirm");
        Assert.AreEqual(0, adopted.ExitCode, adopted.All);
        Assert.Contains($"set 'docs' ({_harness.DocsSetId}) adopted", adopted.Output, StringComparison.Ordinal);
        Assert.Contains("writer identity resumed: yes", adopted.Output, StringComparison.Ordinal);
        Assert.AreEqual(_harness.DocsSetId, Assert.ContainsSingle(runtime.Configuration.BackupSets).Id);

        // The set descriptor now carries the archive's own derivation facts,
        // which differ from the rebuilt installation's.
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), Timeout), out var description);
        Assert.IsInstanceOfType<BackupSetsResult>(
            await handler.ExecuteAsync(new ListBackupSetsCommand(), Timeout), out var sets);
        var docs = Assert.ContainsSingle(sets.Sets);
        Assert.IsNotNull(docs.KdfSalt);
        Assert.AreNotEqual(description.KdfSalt, docs.KdfSalt, "an adopted set keeps the salt its archive was born under");

        _harness.WriteSourceFile("docs/notes.txt", "the second words");
        File.SetLastWriteTimeUtc(WriteIncompressible("docs/big.bin"), DateTime.UtcNow.AddMinutes(1));
        var backedUp = await RunCliAsync("backup", "--set", "docs", "--state", _harness.StateDirectory);
        Assert.AreEqual(0, backedUp.ExitCode, backedUp.All);
        Assert.AreEqual(replica, Assert.ContainsSingle(Directory.GetDirectories(VaultPath)));
        var grown = BlobBytes(replica) - blobBytesBefore;
        Assert.IsTrue(grown < 64 * 1024, $"the incremental run shipped {grown} bytes");

        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        var newest = listed.Snapshots.MaxBy(snapshot => snapshot.CapturedAt)!.SnapshotId;
        var output = Path.Combine(_harness.WorkPath, "restored-cli");
        var restored = await RunCliAsync(
            "restore", newest, "--output", output, "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable);
        Assert.AreEqual(0, restored.ExitCode, restored.All);
        var recovered = Assert.ContainsSingle(Directory.GetFiles(output, "notes.txt", SearchOption.AllDirectories));
        Assert.AreEqual("the second words", await File.ReadAllTextAsync(recovered, Timeout));
    }

    private static Task<HostHarness.Invocation> RunCliAsync(params string[] args) =>
        HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            args);

    private static string PassphraseText => "The hosts-tests Passphrase 42 of this installation!";

    /// <summary>
    /// The owner's sequence: set up, back the docs set up into the vault,
    /// then lose the state directory — configuration, credentials, catalogue,
    /// writer identity, everything — and set the rebuilt machine up afresh
    /// with the same passphrase and a configuration naming only the vault.
    /// Returns the replica directory the vault holds.
    /// </summary>
    private async Task<string> BackUpThenLoseTheMachineAsync(
        RetentionConfiguration? retention = null, RetentionConfiguration? destinationRetention = null)
    {
        Directory.CreateDirectory(VaultPath);
        WriteConfiguration(_harness, withDocsSet: true, retention, destinationRetention);
        await _harness.SetupAsync();
        await using (var runtime = await ServiceRuntime.StartAsync(OptionsFor(_harness), Timeout))
        {
            var set = runtime.Configuration.BackupSets.Single();
            var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
            Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
        }

        var replica = Assert.ContainsSingle(Directory.GetDirectories(VaultPath));
        LoseTheMachine();
        return replica;
    }

    private void LoseTheMachine()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_harness.StateDirectory, recursive: true);
        Directory.CreateDirectory(_harness.StateDirectory);
        if (Directory.Exists(_harness.ArchivesRoot))
        {
            Directory.Delete(_harness.ArchivesRoot, recursive: true);
        }

        WriteConfiguration(_harness, withDocsSet: false);
    }

    /// <summary>
    /// Starts the service on the rebuilt machine: setup runs again through
    /// the agent's own verb — a new salt under the same passphrase — because
    /// the harness's once-only guard remembers the installation that died.
    /// </summary>
    private async Task<ServiceRuntime> StartAsync()
    {
        var setup = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "setup", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable, "--acknowledge-loss",
            "--user", HostHarness.OwnerUser, "--password-env", _harness.PasswordVariable);
        Assert.IsTrue(setup.ExitCode == 0 || setup.All.Contains("already", StringComparison.OrdinalIgnoreCase), setup.All);
        return await ServiceRuntime.StartAsync(OptionsFor(_harness), Timeout);
    }

    private static ServiceOptions OptionsFor(HostHarness harness) => new()
    {
        ArchivesRoot = harness.ArchivesRoot,
        StateDirectory = harness.StateDirectory,
        // The placement condition (ADR-0051) judges by volume, and the
        // fixture's every path shares one real volume — the vault is told
        // apart by name, the compliant install's shape.
        VolumeIdentityOverride = path => path.Contains(Vault, StringComparison.Ordinal) ? 2UL : 1UL,
    };

    private void WriteConfiguration(
        HostHarness harness,
        bool withDocsSet,
        RetentionConfiguration? retention = null,
        RetentionConfiguration? destinationRetention = null) => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('d', 32), Name = Vault, Kind = DestinationKind.LocalPath, Path = VaultPath,
            },
        ],
        BackupSets = withDocsSet
            ?
            [
                new BackupSetConfiguration
                {
                    Id = harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = harness.SourceRoot }],
                    Schedule = "every 1h",
                    ExcludeRules = [ExcludeRule],
                    Retention = retention,
                    Destinations = [new SetDestinationReference { Ref = Vault, Retention = destinationRetention }],
                    DirectShip = true,
                },
            ]
            : [],
    }.Save(Path.Combine(harness.StateDirectory, "config.json"));

    /// <summary>An adoption confirmed as the preview showed it, the only kind the contract takes (FR-DR-009).</summary>
    private async Task<ServiceResult> AdoptConfirmedAsync(ServiceCommandHandler handler, AdoptArchiveCommand adopt) =>
        (await HostHarness.PreviewThenAdoptAsync(handler.ExecuteAsync, adopt, Timeout)).Adopted;

    /// <summary>
    /// Every file under the state directory as path, length and content
    /// digest, in path order — except the service's own log, which records
    /// the command that was just refused or previewed and is meant to, and
    /// the writer-role lock, which the running service holds shut.
    /// </summary>
    private List<string> StateDirectoryFingerprint()
    {
        var logs = Path.Combine(_harness.StateDirectory, "logs") + Path.DirectorySeparatorChar;
        return
        [
            .. Directory.GetFiles(_harness.StateDirectory, "*", SearchOption.AllDirectories)
                .Where(file => !file.StartsWith(logs, StringComparison.Ordinal))
                .Where(file => !file.EndsWith(".lock", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .Select(file =>
                {
                    using var stream = new FileStream(
                        file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    return $"{Path.GetRelativePath(_harness.StateDirectory, file)} {stream.Length} "
                        + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
                }),
        ];
    }

    /// <summary>Makes <paramref name="to"/> hold exactly what <paramref name="from"/> holds.</summary>
    private static void ReplaceTree(string from, string to)
    {
        Directory.Delete(to, recursive: true);
        CopyTree(from, to);
    }

    private async Task<DiscoveredArchiveDescriptor> DiscoverSingleAsync(ServiceCommandHandler handler)
    {
        Assert.IsInstanceOfType<ArchivesDiscoveredResult>(
            await handler.ExecuteAsync(new DiscoverArchivesCommand(Vault), Timeout), out var discovered);
        return Assert.ContainsSingle(discovered.Archives);
    }

    /// <summary>
    /// The client half of the ceremony: derive against the DISCOVERED salt
    /// and parameters, seal the write credential to the service's recipient
    /// key. The passphrase never reaches the service.
    /// </summary>
    private async Task<string> EnvelopeForAsync(
        ServiceCommandHandler handler, DiscoveredArchiveDescriptor row, string passphraseText)
    {
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), Timeout), out var description);
        var parameters = new Argon2Parameters
        {
            MemoryKiB = row.KdfMemoryKib, Iterations = row.KdfIterations, Parallelism = row.KdfParallelism,
        };
        using var passphrase = Passphrase.Create(passphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, parameters, Convert.FromHexString(row.KdfSalt), KdfValidationMode.OpenRepository);
        return Convert.ToHexStringLower(
            WriteOnlyProvisioning.SealProvision(
                Convert.FromHexString(description.RestoreGrantRecipient!), authority,
                Convert.FromHexString(row.KdfSalt), parameters));
    }

    /// <summary>
    /// 300 KB that does not compress, so a re-shipped copy costs what it
    /// looks like it costs — the same bytes every call.
    /// </summary>
    private string WriteIncompressible(string relativePath)
    {
        var bytes = new byte[300_000];
        new Random(7).NextBytes(bytes);
        var full = Path.Combine(_harness.SourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    private static long BlobBytes(string replica)
    {
        var blobs = Path.Combine(replica, "blobs");
        return Directory.Exists(blobs)
            ? Directory.GetFiles(blobs, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
            : 0;
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
