using Bodu;
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

namespace FallbackPlan.Cli;

/// <summary>
/// The CLI's commands, built as a callable unit rather than as top-level
/// statements. Everything here used to live in <c>Main</c>, where it could
/// only be reached by launching a process — which is why the whole surface
/// measured zero coverage: not because the commands were untested by
/// oversight, but because nothing could call them.
/// </summary>
/// <remarks>
/// <see cref="RunAsync"/> takes the argument list and an optional
/// <see cref="InvocationConfiguration"/>, so a test drives a real command
/// end to end — parsing, engine work, exit code — in process.
/// </remarks>
public static class CliApplication
{
    /// <summary>Parses <paramref name="args"/> and runs the matching command.</summary>
    /// <param name="args">The command line, as the process received it.</param>
    /// <param name="configuration">Where output and errors are written; defaults to the console.</param>
    /// <returns>The process exit code: 0 on success.</returns>
    public static async Task<int> RunAsync(string[] args, InvocationConfiguration? configuration = null)
    {
        ThrowHelper.ThrowIfNull(args);

        // Writers, not Console: tests capture output by passing them in,
        // rather than by mutating global console state — which would make
        // the CLI tests unsafe to run in parallel with anything else.
        var output = configuration?.Output ?? Console.Out;
        var error = configuration?.Error ?? Console.Error;
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
        var directOption = new Option<bool>("--direct")
        {
            Description = "Do the work in this process even if a service is running. Refused if the service holds the writer role.",
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

        // An engineering verb — one the service contract carries no command for
        // — takes the device's writer role for its duration and says so. Direct
        // mode is never a fallback that happens silently (ADR-0028 §3): "did my
        // backup run against the same state the service uses" is a question an
        // operator must not have to guess at, and if a service holds the role
        // this refuses naming the holder rather than proceeding anyway.
        async ValueTask<CliSession> OpenWritingSessionAsync(
            ParseResult parse, string verb, CancellationToken cancellationToken)
        {
            var session = await CliSession.OpenAsync(
                parse.GetValue(repoOption)!,
                parse.GetValue(passphraseEnvOption)!,
                parse.GetValue(stateOption),
                writerRole: true,
                cancellationToken).ConfigureAwait(false);

            error.WriteLine(
                $"mode: direct — '{verb}' has no service equivalent, so this command holds the writer role for "
                + $"'{session.StateDirectory}' itself.");
            return session;
        }

        // A read verb goes to the service when one is listening and reads the
        // repository itself when none is. Unlike a write, neither path takes the
        // writer role, so this is never refused for holding it — the choice is
        // about who does the reading, not about who is allowed to.
        async Task<int> ReadThroughGatewayAsync(
            ParseResult parse,
            Func<IOperationGateway, CancellationToken, ValueTask<OperationReport>> operation,
            CancellationToken cancellationToken)
        {
            var gateway = await OperationGateway.OpenForReadAsync(
                parse.GetValue(repoOption)!,
                parse.GetValue(passphraseEnvOption)!,
                parse.GetValue(stateOption),
                parse.GetValue(directOption),
                cancellationToken).ConfigureAwait(false);

            await using (gateway.ConfigureAwait(false))
            {
                error.WriteLine(gateway.Mode);

                var report = await operation(gateway, cancellationToken).ConfigureAwait(false);
                foreach (var line in report.Lines)
                {
                    output.WriteLine(line);
                }

                return report.Ok ? 0 : 2;
            }
        }

        async Task<int> GuardAsync(Func<Task<int>> action)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (CliFailureException exception)
            {
                error.WriteLine($"error: {exception.Message}");
                return 1;
            }
            catch (ClientStateException exception)
            {
                error.WriteLine($"error: {exception.Message}");
                return 1;
            }
            catch (FormatException exception)
            {
                error.WriteLine($"error: {exception.Message}");
                return 1;
            }
            catch (IOException exception)
            {
                error.WriteLine($"error: {exception.Message}");
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

                output.WriteLine($"created repository {Base32.Encode(repository.RepositoryId.ToArray())}");
                output.WriteLine("note: format is UNSTABLE (phase 0) — the descriptor says so (specification 01 §3.2).");
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

                using var session = await OpenWritingSessionAsync(parse, "archive", cancellationToken).ConfigureAwait(false);
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

                output.WriteLine($"snapshot id       {Convert.ToHexString(snapshotId).ToLowerInvariant()}");
                output.WriteLine($"snapshot object   {Hex(published.SnapshotObjectId.ToArray())}");
                output.WriteLine($"file version      {Hex(published.FileVersionObjectId.ToArray())}");
                output.WriteLine($"root tree         {Hex(published.RootTreeObjectId.ToArray())}");
                output.WriteLine($"index delta       {published.DeltaId.ToBase32()}");
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
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
                output.WriteLine($"blob id        {Convert.ToHexString(envelope.BlobId.ToArray()).ToLowerInvariant()}");
                output.WriteLine($"class          {envelope.BlobClass}");
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"key generation {envelope.KeyGeneration.Value}"));
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"counter        {envelope.BlobCounter}"));
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"records        {reader.RecordTable.Count} in {metadata.Metadata.Length} bytes"));

                foreach (var entry in reader.RecordTable)
                {
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
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

                output.WriteLine($"object type    {entry.ObjectType}");
                switch (entry.ObjectType)
                {
                    case ObjectType.FileVersionManifest:
                        var fileVersion = FileVersionManifestCodec.Decode(read.Plaintext!);
                        output.WriteLine($"name           {System.Text.Encoding.UTF8.GetString(fileVersion.Name.Span)}");
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"length         {fileVersion.LogicalLength}"));
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"segments       {fileVersion.SegmentReferences.Count} (+{fileVersion.SparseExtents.Count} sparse extents)"));
                        output.WriteLine($"whole-file     sha256:{Hex(fileVersion.WholeFileHash)}");
                        foreach (var diagnostic in fileVersion.CaptureDiagnostics)
                        {
                            output.WriteLine($"diagnostic     {diagnostic}");
                        }

                        break;

                    case ObjectType.TreeManifest:
                        var tree = TreeManifestCodec.Decode(read.Plaintext!);
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"entries        {tree.Entries.Count}"));
                        foreach (var treeEntry in tree.Entries)
                        {
                            output.WriteLine(
                                $"  {treeEntry.EntryKind,-10} {System.Text.Encoding.UTF8.GetString(treeEntry.Name.Span)}  {Hex(treeEntry.ObjectId.ToArray())}");
                        }

                        break;

                    case ObjectType.SnapshotManifest:
                        var snapshot = SnapshotManifestCodec.Decode(read.Plaintext!);
                        output.WriteLine($"snapshot id    {Hex(snapshot.Manifest.SnapshotId)}");
                        output.WriteLine($"root tree      {Hex(snapshot.Manifest.RootTree.ToArray())}");
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"generation     {snapshot.Manifest.PublicationGeneration}"));
                        output.WriteLine($"client         {snapshot.Manifest.ClientVersion}");
                        break;

                    case ObjectType.PolicyManifest:
                        var policy = PolicyManifestCodec.Decode(read.Plaintext!);
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"segmentation   0x{policy.SegmentationProfile:x4} (size/target {policy.SegmentSizeOrTarget})"));
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"blob targets   {policy.BlobTargetSize}/{policy.BlobMaxSize} (max {policy.BlobMaxRecordCount} records)"));
                        break;

                    case ObjectType.ErrorManifest:
                        var errors = ErrorManifestCodec.Decode(read.Plaintext!);
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"failures       {errors.Failures.Count}"));
                        break;

                    default:
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
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
                using var session = await OpenWritingSessionAsync(parse, "rebuild-index", cancellationToken).ConfigureAwait(false);
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

                    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
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

                    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"rebuild: {report.DeltasApplied} delta(s) + {report.CheckpointsApplied} checkpoint(s) applied, {report.LocationsRecorded} location(s)"));
                    findings = report.Findings;
                }

                foreach (var finding in findings)
                {
                    output.WriteLine($"finding: {finding.Kind}: {finding.Detail}");
                }

                output.WriteLine($"catalogue: {cataloguePath}");
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
            command.Options.Add(directOption);

            command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
            {
                // --file has no service equivalent: the contract's verify takes a
                // level and sweeps the store, with no way to name one manifest.
                // So this branch stays direct and says so rather than pretending
                // the flag routes.
                if (parse.GetValue(fileOption) is { } manifestIdHex)
                {
                    using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);
                    error.WriteLine("mode: direct — verifying one file version has no service equivalent.");

                    using var reader = new RepositoryReader(session.Repository.RepositoryId, session.Repository.Keys, session.Store);
                    await reader.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);

                    var read = await reader.ReadSegmentAsync(ParseObjectId(manifestIdHex), cancellationToken).ConfigureAwait(false);
                    if (read.Outcome != RecordReadOutcome.Ok)
                    {
                        throw new CliFailureException($"The manifest record failed to read: {read.Outcome}.");
                    }

                    using var engine = new VerifyEngine(session.Repository.RepositoryId, session.Repository.Keys, session.Store);
                    var fileResult = await engine.VerifyFileAsync(
                        FileVersionManifestCodec.Decode(read.Plaintext!), reader, cancellationToken).ConfigureAwait(false);

                    output.WriteLine(fileResult.Ok ? "file: OK (every segment and the whole-file hash verified)" : $"file: FAILED — {fileResult.Detail}");
                    return fileResult.Ok ? 0 : 2;
                }

                return await ReadThroughGatewayAsync(
                    parse,
                    (gateway, token) => gateway.VerifyAsync(parse.GetValue(levelOption)!, token),
                    cancellationToken).ConfigureAwait(false);
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

                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"restored {result.Length} bytes to {outputPath} (whole-file hash verified)"));
                return 0;
            }));

            async ValueTask<ObjectId> LocateSnapshotFileAsync(
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
                        error.WriteLine($"warning: {entry.Key.Value} failed authentication — skipped.");
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
            command.Options.Add(directOption);

            command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
            {
                // The one verb that both writes and has a service equivalent, so
                // the one whose side has to be resolved rather than assumed
                // (ADR-0028 §3). Everything the two sides do differently lives
                // behind the gateway; what is left here is the same either way.
                var gateway = await OperationGateway.OpenForWriteAsync(
                    parse.GetValue(repoOption)!,
                    parse.GetValue(passphraseEnvOption)!,
                    parse.GetValue(stateOption),
                    parse.GetValue(directOption),
                    cancellationToken).ConfigureAwait(false);

                await using (gateway.ConfigureAwait(false))
                {
                    error.WriteLine(gateway.Mode);

                    var outcome = await gateway.RunBackupAsync(
                        new BackupRequest
                        {
                            SetName = parse.GetValue(setOption),
                            Root = parse.GetValue(rootArgument),
                            IncludeRules = parse.GetValue(includeOption) ?? [],
                            ExcludeRules = parse.GetValue(excludeOption) ?? [],
                            Full = parse.GetValue(fullOption),
                        },
                        cancellationToken).ConfigureAwait(false);

                    foreach (var line in outcome.Lines)
                    {
                        output.WriteLine(line);
                    }

                    return outcome.Ok ? 0 : 2;
                }
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
                    output.WriteLine("no snapshots known to the catalogue — run `rebuild-index` to learn them from the store.");
                    return 0;
                }

                foreach (var snapshot in snapshots)
                {
                    var when = DateTimeOffset.FromUnixTimeMilliseconds((long)snapshot.CapturedAt)
                        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    var status = snapshot.CaptureStatus == 1 ? "complete" : "partial";
                    var signature = snapshot.SignatureState == 1 ? "verified" : snapshot.SignatureState == 2 ? "BAD-SIG" : "unverified";
                    output.WriteLine($"{Hex(snapshot.SnapshotId)}  {when}  {status,-8}  {signature}");
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
                if (entries.Count == 0)
                {
                    // An empty listing is ambiguous, so it is never reported
                    // as success without establishing which emptiness it is.
                    // Checking the snapshot first matters: the path guard
                    // below only runs when a path was given, so listing the
                    // ROOT of a snapshot that does not exist used to print
                    // nothing and exit 0 — indistinguishable, to a script,
                    // from an empty backup.
                    if (!catalogue.EnumerateSnapshots().Any(
                            snapshot => snapshot.SnapshotId.Span.SequenceEqual(snapshotId)))
                    {
                        throw new CliFailureException(
                            $"snapshot {Hex(snapshotId)} is not in the catalogue — check `snapshots`, or run `rebuild-index` if it is stale.");
                    }

                    if (path.Length > 0 && catalogue.LookupPath(snapshotId, path) is null)
                    {
                        throw new CliFailureException(
                            $"'{path}' does not exist in snapshot {Hex(snapshotId)} — or the catalogue is stale; run `rebuild-index`.");
                    }
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
                    output.WriteLine($"{kind}  {size,12}  {entry.Path}");
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
            command.Options.Add(directOption);

            command.SetAction((parse, cancellationToken) => GuardAsync(() => ReadThroughGatewayAsync(
                parse,
                (gateway, token) => gateway.RestoreAsync(
                    new RestoreRequest(
                        parse.GetValue(snapshotArgument)!,
                        parse.GetValue(pathArgument),
                        parse.GetValue(outputOption)!),
                    token),
                cancellationToken)));
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
            command.Options.Add(directOption);

            command.SetAction((parse, cancellationToken) => GuardAsync(() => ReadThroughGatewayAsync(
                parse,
                (gateway, token) => gateway.CheckAsync(parse.GetValue(levelOption)!, token),
                cancellationToken)));
        }

        // ------------------------------------------------------- config-export

        {
            var command = WithSession(new Command(
                "config-export", "Print the client configuration — schema-versioned and secret-free by construction."));

            command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
            {
                using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);
                output.WriteLine(ClientConfiguration.Load(session.ConfigurationPath).ExportJson());
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

                output.WriteLine($"kit (binary)   {outputPath}");
                output.WriteLine($"kit (text)     {outputPath}.txt");
                output.WriteLine("the kit is ONE factor — store it apart from the passphrase (FR-KIT-004).");
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

                    output.WriteLine($"{set.Name,-20} {status.State,-14} last: {lastProtected}  next: {nextRun}");
                    foreach (var warning in status.Warnings)
                    {
                        output.WriteLine($"{string.Empty,-20} warning: {warning}");
                    }
                }

                return 0;
            }));
        }

        return await root.Parse(args).InvokeAsync(configuration).ConfigureAwait(false);

    }
}
