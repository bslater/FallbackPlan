using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using FallbackPlan.Cli;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Catalogue.Forensic;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

// The low-level phase-0 CLI (wave F5): init, archive, inspect-blob,
// inspect-manifest, rebuild-index, verify, restore-file. Deliberately thin —
// every command is a straight line into the engine with no logic of its own,
// so what it demonstrates is the engine, not the shell around it.

var repoOption = new Option<string>("--repo")
{
    Description = "Path of the repository store root.",
    Required = true,
};
var passphraseEnvOption = new Option<string>("--passphrase-env")
{
    Description = "Name of the environment variable holding the passphrase (never the passphrase itself).",
    Required = true,
};
var stateOption = new Option<string?>("--state")
{
    Description = "Client-local state directory (writer identity, sequence, catalogue, spool). Defaults per repository under the user profile.",
};

var root = new RootCommand("FallbackPlan low-level repository tooling (phase 0)");

Command WithSession(Command command)
{
    command.Options.Add(repoOption);
    command.Options.Add(passphraseEnvOption);
    command.Options.Add(stateOption);
    root.Subcommands.Add(command);
    return command;
}

ValueTask<CliSession> OpenSessionAsync(ParseResult parse, CancellationToken cancellationToken) => CliSession.OpenAsync(
    parse.GetValue(repoOption)!, parse.GetValue(passphraseEnvOption)!, parse.GetValue(stateOption), cancellationToken);

static async Task<int> GuardAsync(Func<Task<int>> action)
{
    try
    {
        return await action().ConfigureAwait(false);
    }
    catch (CliFailureException exception)
    {
        Console.Error.WriteLine($"error: {exception.Message}");
        return 1;
    }
    catch (FormatException exception)
    {
        Console.Error.WriteLine($"error: {exception.Message}");
        return 1;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine($"error: {exception.Message}");
        return 1;
    }
}

static string Hex(ReadOnlyMemory<byte> bytes) => Convert.ToHexString(bytes.Span).ToLowerInvariant();

static ObjectId ParseObjectId(string hex)
{
    try
    {
        return ObjectId.FromBytes(Convert.FromHexString(hex));
    }
    catch (Exception exception) when (exception is FormatException or ArgumentException)
    {
        throw new CliFailureException($"'{hex}' is not a 64-hex-digit object identifier.");
    }
}

// ---------------------------------------------------------------- init

{
    var createdByOption = new Option<string>("--created-by")
    {
        Description = "Informational creator string recorded in the descriptor.",
        DefaultValueFactory = _ => "fallbackplan-cli/0.1",
    };
    var command = new Command("init", "Create a new repository at --repo (keys first, descriptor last).");
    command.Options.Add(repoOption);
    command.Options.Add(passphraseEnvOption);
    command.Options.Add(createdByOption);
    root.Subcommands.Add(command);

    command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
    {
        var store = new LocalFileSystemObjectStore(parse.GetValue(repoOption)!);
        using var passphrase = CliSession.ReadPassphrase(parse.GetValue(passphraseEnvOption)!);
        var settings = RepositoryCreationSettings.Default with { CreatedBy = parse.GetValue(createdByOption)! };

        using var repository = await RepositoryLifecycle.CreateAsync(
            store, passphrase, settings, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"created repository {Base32.Encode(repository.RepositoryId.ToArray())}");
        Console.WriteLine("note: format is UNSTABLE (phase 0) — the descriptor says so (specification 01 §3.2).");
        return 0;
    }));
}

// ------------------------------------------------------------- archive

{
    var fileArgument = new Argument<string>("file") { Description = "Path of the file to archive." };
    var cdcOption = new Option<bool>("--cdc") { Description = "Segment with cdc-v1 (default parameters) instead of fixed-v1." };
    var command = WithSession(new Command("archive", "Publish one file as a snapshot through the full 08 §10 order."));
    command.Arguments.Add(fileArgument);
    command.Options.Add(cdcOption);

    command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
    {
        var filePath = parse.GetValue(fileArgument)!;
        if (!File.Exists(filePath))
        {
            throw new CliFailureException($"'{filePath}' does not exist.");
        }

        using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);
        var policy = parse.GetValue(cdcOption)
            ? CapturePolicy.Default with
            {
                SegmentationProfile = FallbackPlan.Domain.Profiles.SegmentationProfile.CdcV1,
                CdcParameters = CdcParameters.Default,
            }
            : CapturePolicy.Default;

        var orchestrator = new PublicationOrchestrator(
            policy,
            session.Repository.RepositoryId,
            session.Writer,
            session.CurrentGeneration,
            session.Repository.Keys,
            session.Repository.Hierarchy,
            session.Store,
            session.CreateSequence(),
            session.SpoolDirectory);

        var snapshotId = RandomNumberGenerator.GetBytes(16);
        var fileName = Path.GetFileName(filePath);

        PublishedSnapshot published;
        var source = File.OpenRead(filePath);
        await using (source.ConfigureAwait(false))
        {
            published = await orchestrator.PublishAsync(
                new BackupJob(
                    source,
                    System.Text.Encoding.UTF8.GetBytes(fileName),
                    session.DeviceId,
                    session.BackupSetId,
                    snapshotId,
                    (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    DeclaredMaxDurationMs: 3_600_000,
                    ExpiryGeneration: session.CurrentGeneration.Value + 2,
                    ClientVersion: "fallbackplan-cli/0.1"),
                cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine($"snapshot id       {Convert.ToHexString(snapshotId).ToLowerInvariant()}");
        Console.WriteLine($"snapshot object   {Hex(published.SnapshotObjectId.ToArray())}");
        Console.WriteLine($"file version      {Hex(published.FileVersionObjectId.ToArray())}");
        Console.WriteLine($"root tree         {Hex(published.RootTreeObjectId.ToArray())}");
        Console.WriteLine($"index delta       {published.DeltaId.ToBase32()}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"segments          {published.Archive.SegmentReferences.Count} ({published.Archive.LogicalLength} bytes) in {published.Archive.Blobs.Count} data blob(s)"));
        return 0;
    }));
}

// -------------------------------------------------------- inspect-blob

{
    var keyArgument = new Argument<string>("store-key") { Description = "Store key of the blob, e.g. blobs/data/ab/…" };
    var command = WithSession(new Command("inspect-blob", "Open one blob's envelope and authenticated record table."));
    command.Arguments.Add(keyArgument);

    command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
    {
        using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);
        var key = ObjectKey.Parse(parse.GetValue(keyArgument)!);

        var metadata = await session.Store.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        if (!metadata.Found)
        {
            throw new CliFailureException($"No object exists at '{key.Value}'.");
        }

        using var deriver = new FallbackPlan.Repository.Crypto.ObjectIdDeriver(session.Repository.Hierarchy.DeriveContentIdKey());
        using var reader = await BlobReader.OpenAsync(
            session.Store, key, metadata.Metadata!.Length, session.Repository.RepositoryId,
            session.Repository.Keys.DeriveClassKey, deriver, cancellationToken).ConfigureAwait(false);

        var envelope = reader.Envelope;
        Console.WriteLine($"blob id        {Convert.ToHexString(envelope.BlobId.ToArray()).ToLowerInvariant()}");
        Console.WriteLine($"class          {envelope.BlobClass}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"key generation {envelope.KeyGeneration.Value}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"counter        {envelope.BlobCounter}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"records        {reader.RecordTable.Count} in {metadata.Metadata.Length} bytes"));

        foreach (var entry in reader.RecordTable)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  [{entry.Ordinal}] {entry.ObjectType,-18} offset {entry.PhysicalOffset,10} stored {entry.StoredLength,9} logical {entry.LogicalLength,10}  {Hex(entry.ObjectId.ToArray())}"));
        }

        return 0;
    }));
}

// ---------------------------------------------------- inspect-manifest

{
    var idArgument = new Argument<string>("object-id") { Description = "Hex object identifier of the manifest record." };
    var command = WithSession(new Command("inspect-manifest", "Decode one manifest record and print its logical content."));
    command.Arguments.Add(idArgument);

    command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
    {
        using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);
        var objectId = ParseObjectId(parse.GetValue(idArgument)!);

        using var reader = new RepositoryReader(session.Repository.RepositoryId, session.Repository.Keys, session.Store);
        await reader.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);

        if (!reader.TryLocateRecord(objectId, out _, out var entry))
        {
            throw new CliFailureException("No record with that object identifier exists in any loaded blob.");
        }

        var read = await reader.ReadSegmentAsync(objectId, cancellationToken).ConfigureAwait(false);
        if (read.Outcome != RecordReadOutcome.Ok)
        {
            throw new CliFailureException($"The record failed to read: {read.Outcome}.");
        }

        Console.WriteLine($"object type    {entry.ObjectType}");
        switch (entry.ObjectType)
        {
            case ObjectType.FileVersionManifest:
                var fileVersion = FileVersionManifestCodec.Decode(read.Plaintext!);
                Console.WriteLine($"name           {System.Text.Encoding.UTF8.GetString(fileVersion.Name.Span)}");
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"length         {fileVersion.LogicalLength}"));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"segments       {fileVersion.SegmentReferences.Count} (+{fileVersion.SparseExtents.Count} sparse extents)"));
                Console.WriteLine($"whole-file     sha256:{Hex(fileVersion.WholeFileHash)}");
                foreach (var diagnostic in fileVersion.CaptureDiagnostics)
                {
                    Console.WriteLine($"diagnostic     {diagnostic}");
                }

                break;

            case ObjectType.TreeManifest:
                var tree = TreeManifestCodec.Decode(read.Plaintext!);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"entries        {tree.Entries.Count}"));
                foreach (var treeEntry in tree.Entries)
                {
                    Console.WriteLine(
                        $"  {treeEntry.EntryKind,-10} {System.Text.Encoding.UTF8.GetString(treeEntry.Name.Span)}  {Hex(treeEntry.ObjectId.ToArray())}");
                }

                break;

            case ObjectType.SnapshotManifest:
                var snapshot = SnapshotManifestCodec.Decode(read.Plaintext!);
                Console.WriteLine($"snapshot id    {Hex(snapshot.Manifest.SnapshotId)}");
                Console.WriteLine($"root tree      {Hex(snapshot.Manifest.RootTree.ToArray())}");
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"generation     {snapshot.Manifest.PublicationGeneration}"));
                Console.WriteLine($"client         {snapshot.Manifest.ClientVersion}");
                break;

            case ObjectType.PolicyManifest:
                var policy = PolicyManifestCodec.Decode(read.Plaintext!);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"segmentation   0x{policy.SegmentationProfile:x4} (size/target {policy.SegmentSizeOrTarget})"));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"blob targets   {policy.BlobTargetSize}/{policy.BlobMaxSize} (max {policy.BlobMaxRecordCount} records)"));
                break;

            case ObjectType.ErrorManifest:
                var errors = ErrorManifestCodec.Decode(read.Plaintext!);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"failures       {errors.Failures.Count}"));
                break;

            default:
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"length         {read.Plaintext!.Length} bytes (not a manifest type — no decoder applied)"));
                break;
        }

        return 0;
    }));
}

// ------------------------------------------------------- rebuild-index

{
    var forensicOption = new Option<bool>("--forensic")
    {
        Description = "Rebuild from blob recovery footers instead of the index plane (07 §10).",
    };
    var targetSnapshotOption = new Option<string?>("--target-snapshot")
    {
        Description = "Forensic only: stop as soon as this snapshot id (hex) and its dependencies are located.",
    };
    var catalogueOption = new Option<string?>("--catalogue")
    {
        Description = "Catalogue database path. Defaults to the state directory's catalogue.db.",
    };
    var command = WithSession(new Command("rebuild-index", "Rebuild the local catalogue from the store."));
    command.Options.Add(forensicOption);
    command.Options.Add(targetSnapshotOption);
    command.Options.Add(catalogueOption);

    command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
    {
        using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);
        var cataloguePath = parse.GetValue(catalogueOption) ?? session.CataloguePath;
        using var catalogue = Catalogue.Open(cataloguePath, session.Repository.RepositoryId);

        IReadOnlyList<DamageFinding> findings;
        if (parse.GetValue(forensicOption))
        {
            ForensicTarget target = parse.GetValue(targetSnapshotOption) is { } snapshotHex
                ? new ForensicTarget.Snapshot(Convert.FromHexString(snapshotHex))
                : new ForensicTarget.Everything();

            using var rebuilder = new ForensicRebuilder(
                session.Store, session.Repository.RepositoryId, session.Repository.Hierarchy);
            var report = await rebuilder.RebuildAsync(catalogue, target, cancellationToken).ConfigureAwait(false);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"forensic rebuild: {report.RecordsIndexed} records from {report.MetadataBlobsScanned} metadata + {report.DataBlobsScanned} data blob(s); target satisfied: {report.TargetSatisfied}"));
            findings = report.Findings;
        }
        else
        {
            if (parse.GetValue(targetSnapshotOption) is not null)
            {
                throw new CliFailureException("--target-snapshot requires --forensic; the checkpoint-plus-delta rebuild is already bounded.");
            }

            var loader = new IndexLoader(session.Store, session.Repository.RepositoryId, session.Repository.Hierarchy);
            var report = await new CatalogueRebuilder(loader).RebuildAsync(
                catalogue,
                session.CurrentGeneration.Value,
                gapPatienceGenerations: 2,
                isSequenceAccountedAsync: null,
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"rebuild: {report.DeltasApplied} delta(s) + {report.CheckpointsApplied} checkpoint(s) applied, {report.LocationsRecorded} location(s)"));
            findings = report.Findings;
        }

        foreach (var finding in findings)
        {
            Console.WriteLine($"finding: {finding.Kind}: {finding.Detail}");
        }

        Console.WriteLine($"catalogue: {cataloguePath}");
        return findings.Count == 0 ? 0 : 2;
    }));
}

// -------------------------------------------------------------- verify

{
    var levelOption = new Option<string>("--level")
    {
        Description = "Blob verification level: locator | digest | records (05 §8).",
        DefaultValueFactory = _ => "digest",
    };
    var fileOption = new Option<string?>("--file")
    {
        Description = "Verify one file version (hex manifest object id) end to end instead of blobs.",
    };
    var command = WithSession(new Command("verify", "Verify blobs at a chosen level, or one file version end to end."));
    command.Options.Add(levelOption);
    command.Options.Add(fileOption);

    command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
    {
        using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);

        if (parse.GetValue(fileOption) is { } manifestIdHex)
        {
            using var reader = new RepositoryReader(session.Repository.RepositoryId, session.Repository.Keys, session.Store);
            await reader.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);

            var read = await reader.ReadSegmentAsync(ParseObjectId(manifestIdHex), cancellationToken).ConfigureAwait(false);
            if (read.Outcome != RecordReadOutcome.Ok)
            {
                throw new CliFailureException($"The manifest record failed to read: {read.Outcome}.");
            }

            using var engine = new VerifyEngine(session.Repository.RepositoryId, session.Repository.Keys, session.Store);
            var result = await engine.VerifyFileAsync(
                FileVersionManifestCodec.Decode(read.Plaintext!), reader, cancellationToken).ConfigureAwait(false);

            Console.WriteLine(result.Ok ? "file: OK (every segment and the whole-file hash verified)" : $"file: FAILED — {result.Detail}");
            return result.Ok ? 0 : 2;
        }

        var level = parse.GetValue(levelOption) switch
        {
            "locator" => VerifyLevel.LocatorAndFooter,
            "digest" => VerifyLevel.FooterAndDigest,
            "records" => VerifyLevel.EveryRecord,
            var other => throw new CliFailureException($"'{other}' is not a verify level (locator | digest | records)."),
        };

        using var verifier = new VerifyEngine(session.Repository.RepositoryId, session.Repository.Keys, session.Store);
        var blobs = 0;
        var failures = 0;

        await foreach (var entry in session.Store.ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            blobs++;
            var result = await verifier.VerifyBlobAsync(entry.Key, entry.Length, level, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                failures++;
                Console.WriteLine($"FAILED {entry.Key.Value}: {result.Detail}");
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"verified {blobs} blob(s) at level {level}; {failures} failure(s)"));
        return failures == 0 ? 0 : 2;
    }));
}

// -------------------------------------------------------- restore-file

{
    var manifestOption = new Option<string?>("--manifest") { Description = "Hex object id of the file-version manifest to restore." };
    var snapshotOption = new Option<string?>("--snapshot") { Description = "Hex snapshot id — restores that snapshot's file." };
    var outputOption = new Option<string>("--output") { Description = "Destination path.", Required = true };
    var command = WithSession(new Command("restore-file", "Restore one file, verified per segment and by whole-file hash."));
    command.Options.Add(manifestOption);
    command.Options.Add(snapshotOption);
    command.Options.Add(outputOption);

    command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
    {
        using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);

        using var reader = new RepositoryReader(session.Repository.RepositoryId, session.Repository.Keys, session.Store);
        await reader.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);

        ObjectId manifestId;
        if (parse.GetValue(manifestOption) is { } manifestHex)
        {
            manifestId = ParseObjectId(manifestHex);
        }
        else if (parse.GetValue(snapshotOption) is { } snapshotHex)
        {
            manifestId = await LocateSnapshotFileAsync(session, reader, snapshotHex, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new CliFailureException("Pass --manifest <object-id> or --snapshot <snapshot-id>.");
        }

        var read = await reader.ReadSegmentAsync(manifestId, cancellationToken).ConfigureAwait(false);
        if (read.Outcome != RecordReadOutcome.Ok)
        {
            throw new CliFailureException($"The manifest record failed to read: {read.Outcome}.");
        }

        var manifest = FileVersionManifestCodec.Decode(read.Plaintext!);
        var outputPath = parse.GetValue(outputOption)!;

        RestoreResult result;
        var destination = File.Create(outputPath);
        await using (destination.ConfigureAwait(false))
        {
            result = await new RestoreEngine(reader).RestoreFileAsync(manifest, destination, cancellationToken).ConfigureAwait(false);
        }

        if (!result.Success)
        {
            File.Delete(outputPath);
            throw new CliFailureException($"Restore refused: {result.FailureDetail}");
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"restored {result.Length} bytes to {outputPath} (whole-file hash verified)"));
        return 0;
    }));

    static async ValueTask<ObjectId> LocateSnapshotFileAsync(
        CliSession session, RepositoryReader reader, string snapshotHex, CancellationToken cancellationToken)
    {
        var wanted = Convert.FromHexString(snapshotHex);

        await foreach (var entry in session.Store.ListAsync(ObjectPrefix.Parse("snapshots/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            using var openRead = await session.Store.OpenReadAsync(entry.Key, range: null, cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await openRead.Content!.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

            var record = StandaloneRecordFraming.Parse(buffer.ToArray());
            var metadataKey = session.Repository.Keys.DeriveClassKey(BlobClass.Metadata, record.KeyGeneration);
            if (!StandaloneRecordCipher.TryOpen(record, session.Repository.RepositoryId, metadataKey, out var plaintext))
            {
                Console.Error.WriteLine($"warning: {entry.Key.Value} failed authentication — skipped.");
                continue;
            }

            var decoded = SnapshotManifestCodec.Decode(plaintext);
            if (!decoded.Manifest.SnapshotId.Span.SequenceEqual(wanted))
            {
                continue;
            }

            var treeRead = await reader.ReadSegmentAsync(decoded.Manifest.RootTree, cancellationToken).ConfigureAwait(false);
            if (treeRead.Outcome != RecordReadOutcome.Ok)
            {
                throw new CliFailureException($"The snapshot's root tree failed to read: {treeRead.Outcome}.");
            }

            var tree = TreeManifestCodec.Decode(treeRead.Plaintext!);
            var file = tree.Entries.FirstOrDefault(treeEntry => treeEntry.EntryKind == EntryKind.File)
                ?? throw new CliFailureException("The snapshot's tree holds no file entry.");
            return file.ObjectId;
        }

        throw new CliFailureException($"No snapshot {snapshotHex} exists in this repository.");
    }
}

return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
