using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using FallbackPlan.Application;
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
    catch (ClientStateException exception)
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

// -------------------------------------------------------------- backup

{
    var rootArgument = new Argument<string?>("root")
    {
        Description = "Directory to back up. Omit when --set names a configured backup set.",
        Arity = ArgumentArity.ZeroOrOne,
    };
    var setOption = new Option<string?>("--set") { Description = "Name of a configured backup set (config.json)." };
    var includeOption = new Option<string[]>("--include") { Description = "rules-v1 include rule (repeatable).", AllowMultipleArgumentsPerToken = true };
    var excludeOption = new Option<string[]>("--exclude") { Description = "rules-v1 exclude rule (repeatable).", AllowMultipleArgumentsPerToken = true };
    var fullOption = new Option<bool>("--full") { Description = "Ignore the prior snapshot; read every file." };
    var command = WithSession(new Command("backup", "Back up a directory tree as a snapshot (incremental against the latest catalogue snapshot)."));
    command.Arguments.Add(rootArgument);
    command.Options.Add(setOption);
    command.Options.Add(includeOption);
    command.Options.Add(excludeOption);
    command.Options.Add(fullOption);

    command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
    {
        using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);

        string rootPath;
        IReadOnlyList<string> include, exclude;
        byte[] backupSetId;

        if (parse.GetValue(setOption) is { } setName)
        {
            var configuration = ClientConfiguration.Load(session.ConfigurationPath);
            var set = configuration.FindSet(setName)
                ?? throw new CliFailureException($"No backup set named '{setName}' exists in {session.ConfigurationPath}.");
            rootPath = set.Root;
            include = set.IncludeRules;
            exclude = set.ExcludeRules;
            backupSetId = Convert.FromHexString(set.Id);
        }
        else
        {
            rootPath = parse.GetValue(rootArgument)
                ?? throw new CliFailureException("Pass a root directory or --set <name>.");
            include = parse.GetValue(includeOption) ?? [];
            exclude = parse.GetValue(excludeOption) ?? [];
            backupSetId = session.BackupSetId;
        }

        if (!Directory.Exists(rootPath))
        {
            throw new CliFailureException($"'{rootPath}' is not a directory.");
        }

        using var catalogue = Catalogue.Open(session.CataloguePath, session.Repository.RepositoryId);

        // Incremental against the newest snapshot of the same set the
        // catalogue knows — unless --full asks for a re-read.
        var prior = parse.GetValue(fullOption)
            ? null
            : catalogue.EnumerateSnapshots()
                .FirstOrDefault(row => row.BackupSetId.Span.SequenceEqual(backupSetId));

        var orchestrator = new PublicationOrchestrator(
            CapturePolicy.Default,
            session.Repository.RepositoryId,
            session.Writer,
            session.CurrentGeneration,
            session.Repository.Keys,
            session.Repository.Hierarchy,
            session.Store,
            session.CreateSequence(),
            session.SpoolDirectory,
            observer: null,
            catalogue);

        var snapshotId = RandomNumberGenerator.GetBytes(16);
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var published = await orchestrator.PublishAsync(
            new SnapshotJob
            {
                Source = new FallbackPlan.Filesystem.Local.LocalFileSystemSource(),
                RootPath = rootPath,
                IncludeRules = include,
                ExcludeRules = exclude,
                DeviceId = session.DeviceId,
                BackupSetId = backupSetId,
                SnapshotId = snapshotId,
                ParentSnapshots = prior is null ? [] : [prior.SnapshotId],
                PriorSnapshotId = prior?.SnapshotId,
                NowUnixMilliseconds = now,
                DeclaredMaxDurationMs = 3_600_000,
                ExpiryGeneration = session.CurrentGeneration.Value + 2,
                ClientVersion = "fallbackplan-cli/0.1",
            },
            cancellationToken).ConfigureAwait(false);

        session.State.RecordJob(new JobHistoryEntry
        {
            SnapshotId = Hex(snapshotId),
            BackupSetId = Hex(backupSetId),
            StartedAt = now,
            CaptureStatus = (byte)(published.ErrorManifestObjectId is null ? 1 : 2),
            Files = published.Files.Count,
            Failures = published.Failures.Count,
        });

        var reused = published.Files.Count(file => file.Reused);
        Console.WriteLine($"snapshot id    {Hex(snapshotId)}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"files          {published.Files.Count} ({reused} unchanged, {published.Failures.Count} failed)"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"data blobs     {published.ContentBlobs.Count} new"));
        if (prior is not null)
        {
            Console.WriteLine($"incremental    against {Hex(prior.SnapshotId)}");
        }

        Console.WriteLine(published.ErrorManifestObjectId is null
            ? "status         complete"
            : "status         PARTIAL — see the error manifest");
        return published.ErrorManifestObjectId is null ? 0 : 2;
    }));
}

// ----------------------------------------------------------- snapshots

{
    var command = WithSession(new Command("snapshots", "List the catalogue's known snapshots, newest first."));

    command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
    {
        using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);
        using var catalogue = Catalogue.Open(session.CataloguePath, session.Repository.RepositoryId);

        var snapshots = catalogue.EnumerateSnapshots();
        if (snapshots.Count == 0)
        {
            Console.WriteLine("no snapshots known to the catalogue — run `rebuild-index` to learn them from the store.");
            return 0;
        }

        foreach (var snapshot in snapshots)
        {
            var when = DateTimeOffset.FromUnixTimeMilliseconds((long)snapshot.CapturedAt)
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var status = snapshot.CaptureStatus == 1 ? "complete" : "partial";
            var signature = snapshot.SignatureState == 1 ? "verified" : snapshot.SignatureState == 2 ? "BAD-SIG" : "unverified";
            Console.WriteLine($"{Hex(snapshot.SnapshotId)}  {when}  {status,-8}  {signature}");
        }

        return 0;
    }));
}

// ------------------------------------------------------------------ ls

{
    var snapshotArgument = new Argument<string>("snapshot") { Description = "Hex snapshot id." };
    var pathArgument = new Argument<string?>("path")
    {
        Description = "Directory path within the snapshot; omit for the root.",
        Arity = ArgumentArity.ZeroOrOne,
    };
    var command = WithSession(new Command("ls", "List a directory within a snapshot, from the catalogue."));
    command.Arguments.Add(snapshotArgument);
    command.Arguments.Add(pathArgument);

    command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
    {
        using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);
        using var catalogue = Catalogue.Open(session.CataloguePath, session.Repository.RepositoryId);

        var snapshotId = Convert.FromHexString(parse.GetValue(snapshotArgument)!);
        var path = parse.GetValue(pathArgument) ?? string.Empty;

        var entries = catalogue.ListDirectory(snapshotId, path);
        if (entries.Count == 0 && path.Length > 0 && catalogue.LookupPath(snapshotId, path) is null)
        {
            throw new CliFailureException(
                $"'{path}' does not exist in snapshot {Hex(snapshotId)} — or the catalogue is stale; run `rebuild-index`.");
        }

        foreach (var entry in entries)
        {
            var kind = entry.EntryKind switch
            {
                EntryKind.DirectoryPlaceholder => "dir ",
                EntryKind.Symlink => "link",
                EntryKind.Special => "spec",
                _ => "file",
            };
            var size = entry.LogicalLength is { } length
                ? length.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            Console.WriteLine($"{kind}  {size,12}  {entry.Path}");
        }

        return 0;
    }));
}

// ------------------------------------------------------------- restore

{
    var snapshotArgument = new Argument<string>("snapshot") { Description = "Hex snapshot id." };
    var pathArgument = new Argument<string?>("path")
    {
        Description = "Path within the snapshot to restore; omit for everything.",
        Arity = ArgumentArity.ZeroOrOne,
    };
    var outputOption = new Option<string>("--output") { Description = "Destination directory.", Required = true };
    var command = WithSession(new Command(
        "restore", "Restore a snapshot (or a path within it), each file verified per segment and by whole-file hash."));
    command.Arguments.Add(snapshotArgument);
    command.Arguments.Add(pathArgument);
    command.Options.Add(outputOption);

    command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
    {
        using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);
        using var catalogue = Catalogue.Open(session.CataloguePath, session.Repository.RepositoryId);

        var snapshotId = Convert.FromHexString(parse.GetValue(snapshotArgument)!);
        var wanted = parse.GetValue(pathArgument);
        var outputRoot = parse.GetValue(outputOption)!;

        using var reader = new RepositoryReader(session.Repository.RepositoryId, session.Repository.Keys, session.Store);
        await reader.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);
        var engine = new RestoreEngine(reader);

        var restored = 0;
        var failed = 0;

        async ValueTask RestoreEntryAsync(FallbackPlan.Repository.Catalogue.CatalogueTreeEntry entry)
        {
            if (entry.EntryKind == EntryKind.DirectoryPlaceholder)
            {
                Directory.CreateDirectory(Path.Combine(outputRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
                foreach (var child in catalogue.ListDirectory(snapshotId, entry.Path))
                {
                    await RestoreEntryAsync(child).ConfigureAwait(false);
                }

                return;
            }

            var read = await reader.ReadSegmentAsync(entry.ObjectId, cancellationToken).ConfigureAwait(false);
            if (read.Outcome != RecordReadOutcome.Ok)
            {
                Console.Error.WriteLine($"FAILED {entry.Path}: manifest read {read.Outcome}");
                failed++;
                return;
            }

            var manifest = FileVersionManifestCodec.Decode(read.Plaintext!);
            if (manifest.EntryKind != EntryKind.File)
            {
                // Symlinks and specials materialise in wave R's planner;
                // reported, never silently dropped.
                Console.Error.WriteLine($"skipped {entry.Path}: {manifest.EntryKind} restore lands with the restore planner");
                return;
            }

            var destinationPath = Path.Combine(outputRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            RestoreResult result;
            var destination = File.Create(destinationPath);
            await using (destination.ConfigureAwait(false))
            {
                result = await engine.RestoreFileAsync(manifest, destination, cancellationToken).ConfigureAwait(false);
            }

            if (!result.Success)
            {
                File.Delete(destinationPath);
                Console.Error.WriteLine($"FAILED {entry.Path}: {result.FailureDetail}");
                failed++;
                return;
            }

            restored++;
        }

        if (wanted is { Length: > 0 })
        {
            var entry = catalogue.LookupPath(snapshotId, wanted)
                ?? throw new CliFailureException(
                    $"'{wanted}' does not exist in snapshot {Hex(snapshotId)} — or the catalogue is stale; run `rebuild-index`.");
            await RestoreEntryAsync(entry).ConfigureAwait(false);
        }
        else
        {
            var roots = catalogue.ListDirectory(snapshotId, string.Empty);
            if (roots.Count == 0)
            {
                throw new CliFailureException(
                    $"The catalogue knows nothing under snapshot {Hex(snapshotId)} — run `rebuild-index` first.");
            }

            foreach (var entry in roots)
            {
                await RestoreEntryAsync(entry).ConfigureAwait(false);
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"restored {restored} file(s) to {outputRoot}; {failed} failure(s)"));
        return failed == 0 ? 0 : 2;
    }));
}

// --------------------------------------------------------------- check

{
    var levelOption = new Option<string>("--level")
    {
        Description = "Blob verification level: locator | digest | records (05 §8).",
        DefaultValueFactory = _ => "digest",
    };
    var command = WithSession(new Command(
        "check", "Repository health: blob verification sweep, journal survey, and the catalogue's damage findings."));
    command.Options.Add(levelOption);

    command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
    {
        using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);

        var level = parse.GetValue(levelOption) switch
        {
            "locator" => VerifyLevel.LocatorAndFooter,
            "digest" => VerifyLevel.FooterAndDigest,
            "records" => VerifyLevel.EveryRecord,
            var other => throw new CliFailureException($"'{other}' is not a verify level (locator | digest | records)."),
        };

        var problems = 0;

        using (var verifier = new VerifyEngine(session.Repository.RepositoryId, session.Repository.Keys, session.Store))
        {
            var blobs = 0;
            await foreach (var entry in session.Store
                .ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, cancellationToken).ConfigureAwait(false))
            {
                blobs++;
                var result = await verifier.VerifyBlobAsync(entry.Key, entry.Length, level, cancellationToken).ConfigureAwait(false);
                if (!result.Ok)
                {
                    problems++;
                    Console.WriteLine($"blob FAILED  {entry.Key.Value}: {result.Detail}");
                }
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"blobs      {blobs} verified at {level}"));
        }

        using (var journalReader = new FallbackPlan.Repository.Index.Journal.JournalReader(
            session.Store, session.Repository.RepositoryId, session.Repository.Hierarchy))
        {
            var (records, unparseable, journalFindings) = await journalReader.LoadAsync(
                session.CurrentGeneration.Value, cancellationToken).ConfigureAwait(false);
            problems += unparseable + journalFindings.Count;

            var survey = FallbackPlan.Repository.Index.Journal.IntentSurveyor.Survey(
                records, unparseable, session.CurrentGeneration.Value,
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), skewMarginMs: 300_000);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"journal    {records.Count} record(s), {unparseable} unparseable, {survey.LiveIntents.Count} live intent(s)"));
            foreach (var finding in journalFindings)
            {
                Console.WriteLine($"journal    {finding.Kind}: {finding.Detail}");
            }
        }

        if (File.Exists(session.CataloguePath))
        {
            using var catalogue = Catalogue.Open(session.CataloguePath, session.Repository.RepositoryId);
            var findings = catalogue.Findings();
            problems += findings.Count;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"catalogue  {findings.Count} damage finding(s)"));
            foreach (var finding in findings)
            {
                Console.WriteLine($"catalogue  {finding.Kind}: {finding.Detail}");
            }
        }
        else
        {
            Console.WriteLine("catalogue  absent (rebuildable cache — run `rebuild-index` to materialise it)");
        }

        Console.WriteLine(problems == 0 ? "check: OK" : string.Create(CultureInfo.InvariantCulture, $"check: {problems} problem(s)"));
        return problems == 0 ? 0 : 2;
    }));
}

// ------------------------------------------------------- config-export

{
    var command = WithSession(new Command(
        "config-export", "Print the client configuration — schema-versioned and secret-free by construction."));

    command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
    {
        using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(ClientConfiguration.Load(session.ConfigurationPath).ExportJson());
        return 0;
    }));
}

// ----------------------------------------------------------- key-export

{
    var outputOption = new Option<string>("--output")
    {
        Description = "Path for the binary kit file (FBPKRKIT). The text form goes to '<output>.txt'.",
        Required = true,
    };
    var command = WithSession(new Command(
        "key-export",
        "Export a recovery kit: the verbatim wrapped key object plus everything needed to use it (FR-KIT-001)."));
    command.Options.Add(outputOption);

    command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
    {
        // The export path re-derives the KEK and proves the passphrase
        // opens the exported object — a kit that cannot work is never
        // written.
        using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);
        using var passphrase = CliSession.ReadPassphrase(parse.GetValue(passphraseEnvOption)!);

        var kit = await RecoveryKitFactory.BuildAsync(
            session.Store,
            passphrase,
            session.DeviceId,
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            [new FallbackPlan.Repository.Format.RecoveryKit.KitDestination(
                "local-path", Path.GetFullPath(parse.GetValue(repoOption)!), string.Empty, string.Empty)],
            cancellationToken).ConfigureAwait(false);

        var framed = FallbackPlan.Repository.Format.RecoveryKit.RecoveryKitCodec.Serialize(kit);
        var outputPath = parse.GetValue(outputOption)!;
        await File.WriteAllBytesAsync(outputPath, framed, cancellationToken).ConfigureAwait(false);

        var text = FallbackPlan.Repository.Format.RecoveryKit.RecoveryKitText.Render(
            framed, "Keep this page with your passphrase manager, not with your passphrase.");
        await File.WriteAllTextAsync(outputPath + ".txt", text, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"kit (binary)   {outputPath}");
        Console.WriteLine($"kit (text)     {outputPath}.txt");
        Console.WriteLine("the kit is ONE factor — store it apart from the passphrase (FR-KIT-004).");
        return 0;
    }));
}

// -------------------------------------------------------------- status

{
    var command = WithSession(new Command(
        "status", "Per-set protection status (architecture 10 §1) — captured is never protected, degraded is never unrecoverable."));

    command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
    {
        using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);
        using var catalogue = Catalogue.Open(session.CataloguePath, session.Repository.RepositoryId);
        var configuration = ClientConfiguration.Load(session.ConfigurationPath);
        var jobs = JobStateStore.Open(session.StateDirectory);
        var repoPath = parse.GetValue(repoOption)!;

        var findings = catalogue.Findings();
        var requiredMissing = findings.Any(finding =>
            finding.Kind is DamageKind.MissingBlob or DamageKind.MissingIndexObject);
        var reachable = File.Exists(Path.Combine(repoPath, "repository-format"));
        var snapshots = catalogue.EnumerateSnapshots();

        var sets = configuration.BackupSets.Count > 0
            ? configuration.BackupSets
            : [new BackupSetConfiguration
               {
                   Id = Hex(session.BackupSetId), Name = "(default)", Root = string.Empty,
               }];

        foreach (var set in sets)
        {
            var setId = Convert.FromHexString(set.Id);
            var latest = snapshots.FirstOrDefault(row => row.BackupSetId.Span.SequenceEqual(setId));

            // The failure-domain fact (PT-8): same device as the source is
            // never `protected`. Unknown roots stay conservative.
            var sameDomain = true;
            if (set.Root.Length > 0 &&
                FallbackPlan.Filesystem.Local.LocalFileSystemSource.TryStat(set.Root, out var rootStat) &&
                FallbackPlan.Filesystem.Local.LocalFileSystemSource.TryStat(repoPath, out var repoStat))
            {
                sameDomain = rootStat.Device == repoStat.Device;
            }

            var status = StatusDeriver.Derive(new StatusInputs
            {
                LatestSnapshotAt = latest?.CapturedAt,
                LatestCaptureStatus = latest?.CaptureStatus,
                DestinationReachable = reachable,
                SameFailureDomain = sameDomain,
                DamageFindings = findings.Count,
                RequiredObjectsMissing = requiredMissing,
            });

            var lastProtected = latest is null
                ? "never"
                : DateTimeOffset.FromUnixTimeMilliseconds((long)latest.CapturedAt)
                    .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            string nextRun = "manual";
            if (!string.IsNullOrWhiteSpace(set.Schedule) &&
                FallbackPlan.Application.Schedule.TryParse(set.Schedule!, out var schedule, out _))
            {
                var anchor = jobs.LastCompleted(set.Id) is { } completed
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)completed.UpdatedAt)
                    : (DateTimeOffset?)null;
                nextRun = schedule!.NextRun(anchor, DateTimeOffset.Now)
                    .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }

            Console.WriteLine($"{set.Name,-20} {status.State,-14} last: {lastProtected}  next: {nextRun}");
            foreach (var warning in status.Warnings)
            {
                Console.WriteLine($"{string.Empty,-20} warning: {warning}");
            }
        }

        return 0;
    }));
}

return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
