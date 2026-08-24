using Bodu;
using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
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
using FallbackPlan.Protocol;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Cli.Resources;
using RestoreResult = FallbackPlan.Repository.RestoreResult;
using FallbackPlan.Diagnostics;
using Microsoft.Extensions.Logging;

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
    /// <summary>
    /// Interface-typed on purpose: device probing goes through the seam like
    /// every other filesystem question, so a test can substitute it. The
    /// concrete construction here is composition, which is a host's job.
    /// </summary>
    private static readonly FallbackPlan.Filesystem.IFileSystemSource DeviceProbe =
        new FallbackPlan.Filesystem.Local.LocalFileSystemSource();

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

        // --repo and --passphrase-env are required for a verb that works the
        // local repository, but a verb reaching a remote service over --connect
        // has neither (the service holds the repository). Requiredness is
        // therefore conditional and enforced in the handler — see Repo/
        // PassphraseEnv below and ResolveRemote — rather than by the parser,
        // which can only say "always" or "never".
        var repoOption = new Option<string?>("--repo")
        {
            Description = "Path of the repository store root. Required unless --connect names a remote service.",
        };
        var passphraseEnvOption = new Option<string?>("--passphrase-env")
        {
            Description = "Name of the environment variable holding the passphrase (never the passphrase itself). Required unless --connect names a remote service.",
        };
        var stateOption = new Option<string?>("--state")
        {
            Description = "Client-local state directory (writer identity, sequence, catalogue, spool). Defaults per repository under the user profile; required with --connect (it holds this console's peer identity and pairings).",
        };
        var directOption = new Option<bool>("--direct")
        {
            Description = "Do the work in this process even if a service is running. Refused if the service holds the writer role.",
        };
        var connectOption = new Option<string?>("--connect")
        {
            Description = "Reach a remote paired service at host:port over the peer protocol instead of the local repository (ADR-0028 §5). Requires --state and --fingerprint.",
        };
        var fingerprintOption = new Option<string?>("--fingerprint")
        {
            Description = "Fingerprint of the pinned service to expect when using --connect (the key it was paired to must answer).",
        };
        var logLevelOption = new Option<string?>("--log-level")
        {
            Description =
                "How much to log to standard error: trace, debug, information, warning, error, critical or none. "
                + "Reads FALLBACKPLAN_LOG_LEVEL when absent; warnings and above otherwise.",
            Recursive = true,
        };

        // The level is read straight from the arguments rather than from the
        // parse result: every handler below closes over the composition, so it
        // has to exist before the commands that use it are built. The option is
        // still declared — recursively, on the root — so the parser accepts it
        // wherever it is written and `--help` describes it.
        string? LogLevelArgument()
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--log-level")
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        if (!LoggingOptions.TryResolveLevel(
                LogLevelArgument(),
                Environment.GetEnvironmentVariable(LoggingOptions.LevelVariable),
                configured: null,
                out var logLevel,
                out var levelRefusal,
                fallback: LogLevel.Warning))
        {
            error.WriteLine($"error: {levelRefusal}");
            return 1;
        }

        // The console's own output is its result, not its log, so nothing
        // below Warning reaches standard error unless somebody asked for it —
        // and no file sink at all, because `<state>/logs` belongs to the
        // service that owns the state directory, not to a command passing
        // through it (ADR-0043 §6).
        using var logging = LoggingComposition.Create(
            new LoggingOptions { Default = logLevel, Directory = null, Console = true },
            error);
        var sessionLogger = logging.Factory.CreateLogger<CliSession>();
        var catalogueLogger = logging.Factory.CreateLogger<Catalogue>();

        var root = new RootCommand(
            "FallbackPlan — encrypted backup: repository tooling, backup and restore, and the console for a running service");
        root.Options.Add(logLevelOption);

        Command WithSession(Command command)
        {
            command.Options.Add(repoOption);
            command.Options.Add(passphraseEnvOption);
            command.Options.Add(stateOption);
            root.Subcommands.Add(command);
            return command;
        }

        // A verb that can also reach a remote service: everything WithSession
        // gives, plus the two options that name one. The repo/passphrase it
        // inherits are only needed on the local path, so they stay optional and
        // are validated there.
        Command WithRemoteCapableSession(Command command)
        {
            WithSession(command);
            command.Options.Add(connectOption);
            command.Options.Add(fingerprintOption);
            return command;
        }

        // Requiredness the parser cannot express: needed on the local path,
        // absent on the remote one.
        string Repo(ParseResult parse) => parse.GetValue(repoOption) is { Length: > 0 } value
            ? value
            : throw new CliFailureException("--repo is required.");
        string PassphraseEnv(ParseResult parse) => parse.GetValue(passphraseEnvOption) is { Length: > 0 } value
            ? value
            : throw new CliFailureException("--passphrase-env is required.");

        // Resolves the remote target when --connect is given, or null for the
        // local path. Refuses the combinations that cannot mean anything: mixing
        // --connect with --direct (both claim to decide where the work runs), or
        // omitting the --state and --fingerprint --connect depends on.
        (string Host, int Port, string State, string Fingerprint)? ResolveRemote(ParseResult parse, bool direct)
        {
            if (parse.GetValue(connectOption) is not { } connect)
            {
                return null;
            }

            if (direct)
            {
                throw new CliFailureException(
                    "--connect and --direct cannot be combined; --connect already names where the work runs.");
            }

            if (!TryParseEndpoint(connect, out var host, out var port))
            {
                throw new CliFailureException($"'{connect}' is not host:port.");
            }

            var state = parse.GetValue(stateOption)
                ?? throw new CliFailureException(
                    "--connect requires --state (the console's peer identity and pairings).");
            var fingerprint = parse.GetValue(fingerprintOption)
                ?? throw new CliFailureException(
                    "--connect requires --fingerprint (the pinned service to expect).");

            return (host, port, state, fingerprint);
        }

        // A query verb over the remote binding: dial the pinned service, send one
        // command, and insist on the result it should answer with. The read verbs
        // that go through the gateway do not need this — the gateway carries them
        // — but snapshots, ls and status read the catalogue directly on the local
        // path, so the remote path is theirs to drive.
        async Task<TResult> QueryRemoteAsync<TResult>(
            (string Host, int Port, string State, string Fingerprint) target,
            ServiceCommand command,
            CancellationToken cancellationToken)
            where TResult : ServiceResult
        {
            await using var connection = await RemotePeer.ConnectAsync(
                target.Host, target.Port, target.State, target.Fingerprint, "fallbackplan-cli", cancellationToken)
                .ConfigureAwait(false);

            var result = await connection.Client.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            return result switch
            {
                TResult expected => expected,
                ServiceError error => throw new CliFailureException(error.Message),
                _ => throw new CliFailureException($"the service answered with {result.GetType().Name}."),
            };
        }

        ValueTask<CliSession> OpenSessionAsync(ParseResult parse, CancellationToken cancellationToken) => CliSession.OpenAsync(
            Repo(parse), PassphraseEnv(parse), parse.GetValue(stateOption), cancellationToken, sessionLogger);

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
                Repo(parse),
                PassphraseEnv(parse),
                parse.GetValue(stateOption),
                writerRole: true,
                cancellationToken,
                sessionLogger).ConfigureAwait(false);

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
            var remote = ResolveRemote(parse, parse.GetValue(directOption));
            var gateway = remote is { } target
                ? await OperationGateway.OpenForRemoteAsync(
                    target.Host, target.Port, target.State, target.Fingerprint, cancellationToken).ConfigureAwait(false)
                : await OperationGateway.OpenForReadAsync(
                    Repo(parse),
                    PassphraseEnv(parse),
                    parse.GetValue(stateOption),
                    parse.GetValue(directOption),
                    cancellationToken,
                    sessionLogger).ConfigureAwait(false);

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

        // The specification's own vocabulary (06 §6), not a friendlier
        // paraphrase: "best-effort live capture" and "application-consistent"
        // are materially different promises, and softening either is how a
        // person ends up trusting a database restore they should not.
        // An unassigned value is printed rather than guessed at — a future
        // writer may use one this build has never heard of.
        static string ConsistencyName(byte method) => method switch
        {
            1 => "live",
            2 => "vss",
            3 => "filesystem-snapshot",
            4 => "application-quiesced",
            _ => string.Create(CultureInfo.InvariantCulture, $"unknown({method})"),
        };

        static bool TryParseEndpoint(string target, out string host, out int port)
        {
            host = string.Empty;
            port = 0;
            var colon = target.LastIndexOf(':');
            if (colon <= 0 || colon == target.Length - 1)
            {
                return false;
            }

            host = target[..colon];
            return int.TryParse(target[(colon + 1)..], CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
        }

        static ObjectId ParseObjectId(string hex)
        {
            try
            {
                return ObjectId.FromBytes(Convert.FromHexString(hex));
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                throw new CliFailureException(Strings.FormatCliApplication_NotHexDigitObjectIdentifier(hex));
            }
        }

        // ---------------------------------------------------------------- init

        {
            var createdByOption = new Option<string>("--created-by")
            {
                Description = "Informational creator string recorded in the descriptor.",
                DefaultValueFactory = _ => "fallbackplan-cli/0.1",
            };
            var writeOnlyOption = new Option<bool>("--write-only")
            {
                Description = "Create a write-only (format 2) repository (ADR-0042): every key derives from the "
                    + "passphrase, nothing is stored, content seals to a public key. Requires --acknowledge-loss.",
            };
            var acknowledgeLossOption = new Option<bool>("--acknowledge-loss")
            {
                Description = "Acknowledge that a write-only repository's passphrase can never change and that "
                    + "losing it loses the backup irrecoverably.",
            };
            var command = new Command("init", "Create a new repository at --repo (keys first, descriptor last).");
            command.Options.Add(repoOption);
            command.Options.Add(passphraseEnvOption);
            command.Options.Add(createdByOption);
            command.Options.Add(writeOnlyOption);
            command.Options.Add(acknowledgeLossOption);
            root.Subcommands.Add(command);

            command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
            {
                var store = StoreComposition.OpenLocal(Repo(parse));
                using var passphrase = CliSession.ReadPassphrase(PassphraseEnv(parse));
                var settings = RepositoryCreationSettings.Default with { CreatedBy = parse.GetValue(createdByOption)! };

                if (parse.GetValue(writeOnlyOption))
                {
                    // The loss acknowledgement is the ceremony, not a speed
                    // bump (ADR-0042 §11, architecture 03 §1 rule 6): there
                    // is no recovery path to offer later, so consent is
                    // collected before the descriptor exists.
                    if (!parse.GetValue(acknowledgeLossOption))
                    {
                        throw new CliFailureException(
                            "A write-only repository's passphrase can never change, and if it is lost the backup "
                            + "is unrecoverable — there is no reset and no export. Re-run with --acknowledge-loss "
                            + "to accept this (ADR-0042).");
                    }

                    var (created, authority) = await RepositoryLifecycle.CreateWriteOnlyAsync(
                        store, passphrase, settings, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        cancellationToken).ConfigureAwait(false);
                    using (created)
                    using (authority)
                    {
                        output.WriteLine($"created write-only repository {Base32.Encode(created.RepositoryId.ToArray())}");
                        output.WriteLine(
                            "the passphrase is the only key: it can never change, and losing it loses the backup.");
                    }

                    output.WriteLine("note: format is UNSTABLE (phase 0) — the descriptor says so (specification 01 §3.2).");
                    return 0;
                }

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
                    throw new CliFailureException(Strings.FormatCliApplication_DoesNotExist(filePath));
                }

                using var session = await OpenWritingSessionAsync(parse, "archive", cancellationToken).ConfigureAwait(false);
                var policy = parse.GetValue(cdcOption)
                    ? CapturePolicy.Default with
                    {
                        SegmentationProfile = FallbackPlan.Domain.Profiles.SegmentationProfile.CdcV1,
                        CdcParameters = CdcParameters.Default,
                    }
                    : CapturePolicy.Default;

                // A write-only repository takes the device trust domain
                // (ADR-0042): verify-on-reuse reads content, which it cannot.
                if (session.Repository.Keys.WriteOnly)
                {
                    policy = policy with { DedupTrustDomain = Domain.Configuration.DedupTrustDomain.Device };
                }

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
                    throw new CliFailureException(Strings.FormatCliApplication_NoObjectExists(key.Value));
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

                using var reader = new RepositoryReader(session.Repository.RepositoryId, session.Repository.Keys, session.Store, session.ReadAuthority);
                await reader.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);

                if (!reader.TryLocateRecord(objectId, out _, out var entry))
                {
                    throw new CliFailureException(Strings.CliApplication_NoRecordWithObjectIdentifier);
                }

                var read = await reader.ReadSegmentAsync(objectId, cancellationToken).ConfigureAwait(false);
                if (read.Outcome != RecordReadOutcome.Ok)
                {
                    throw new CliFailureException(Strings.FormatCliApplication_RecordFailedRead(read.Outcome));
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
                        output.WriteLine($"consistency    {ConsistencyName(snapshot.Manifest.ConsistencyMethod)}");
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
                Description = "Catalogue database path. Defaults to the state directory's per-archive catalogue.",
            };
            var command = WithSession(new Command("rebuild-index", "Rebuild the local catalogue from the store."));
            command.Options.Add(forensicOption);
            command.Options.Add(targetSnapshotOption);
            command.Options.Add(catalogueOption);

            command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
            {
                using var session = await OpenWritingSessionAsync(parse, "rebuild-index", cancellationToken).ConfigureAwait(false);
                var cataloguePath = parse.GetValue(catalogueOption) ?? session.CataloguePath;
                using var catalogue = Catalogue.Open(cataloguePath, session.Repository.RepositoryId, catalogueLogger);

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
                        throw new CliFailureException(Strings.CliApplication_TargetSnapshotRequiresForensicCheckpoint);
                    }

                    var loader = new IndexLoader(
                        session.Store, session.Repository.RepositoryId, session.Repository.Hierarchy,
                        logging.Factory.CreateLogger<IndexLoader>());

                    // Precedence rule 3 (specification 07 §3) needs to know
                    // which blobs the store still holds, and this is the only
                    // place that knows. Without it the rebuild assumes every
                    // blob is live and serves locations into blobs collection
                    // removed — the index naming an object no blob holds.
                    using var inventory = new RepositoryReader(
                        session.Repository.RepositoryId, session.Repository.Keys, session.Store, session.ReadAuthority);
                    await inventory.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);

                    var report = await new CatalogueRebuilder(
                        loader, logging.Factory.CreateLogger<CatalogueRebuilder>()).RebuildAsync(
                        catalogue,
                        session.CurrentGeneration.Value,
                        gapPatienceGenerations: 2,
                        isSequenceAccountedAsync: null,
                        cancellationToken,
                        CatalogueRebuilder.KnownBlobs(
                            inventory.Blobs.Select(blob => blob.BlobId),
                            inventoryComplete: inventory.SkippedBlobs.Count == 0)).ConfigureAwait(false);

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
            var command = WithRemoteCapableSession(new Command("verify", "Verify blobs at a chosen level, or one file version end to end."));
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

                    using var reader = new RepositoryReader(session.Repository.RepositoryId, session.Repository.Keys, session.Store, session.ReadAuthority);
                    await reader.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);

                    var read = await reader.ReadSegmentAsync(ParseObjectId(manifestIdHex), cancellationToken).ConfigureAwait(false);
                    if (read.Outcome != RecordReadOutcome.Ok)
                    {
                        throw new CliFailureException(Strings.FormatCliApplication_ManifestRecordFailedRead(read.Outcome));
                    }

                    using var engine = new VerifyEngine(session.Repository.RepositoryId, session.Repository.Keys, session.Store);
                    var fileResult = await engine.VerifyFileAsync(
                        FileVersionManifestCodec.Decode(read.Plaintext!), reader, cancellationToken).ConfigureAwait(false);

                    // Sealed content is a stated incapacity, not damage
                    // (ADR-0042): still not a success — the file was NOT
                    // verified — but never reported as corrupt.
                    output.WriteLine(fileResult.Ok
                        ? "file: OK (every segment and the whole-file hash verified)"
                        : fileResult.NeedsRestoreGrant
                            ? $"file: NOT CHECKED — {fileResult.Detail}"
                            : $"file: FAILED — {fileResult.Detail}");
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

                using var reader = new RepositoryReader(session.Repository.RepositoryId, session.Repository.Keys, session.Store, session.ReadAuthority);
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
                    throw new CliFailureException(Strings.CliApplication_PassManifestObjectIdSnapshot);
                }

                var read = await reader.ReadSegmentAsync(manifestId, cancellationToken).ConfigureAwait(false);
                if (read.Outcome != RecordReadOutcome.Ok)
                {
                    throw new CliFailureException(Strings.FormatCliApplication_ManifestRecordFailedRead(read.Outcome));
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
                    throw new CliFailureException(Strings.FormatCliApplication_RestoreRefused(result.FailureDetail));
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
                        throw new CliFailureException(Strings.FormatCliApplication_SnapshotSRootTreeFailed(treeRead.Outcome));
                    }

                    var tree = TreeManifestCodec.Decode(treeRead.Plaintext!);
                    var file = tree.Entries.FirstOrDefault(treeEntry => treeEntry.EntryKind == EntryKind.File)
                        ?? throw new CliFailureException(Strings.CliApplication_SnapshotSTreeHoldsNo);
                    return file.ObjectId;
                }

                throw new CliFailureException(Strings.FormatCliApplication_NoSnapshotExistsRepository(snapshotHex));
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
            var command = WithRemoteCapableSession(new Command("backup", "Back up a directory tree as a snapshot (incremental against the latest catalogue snapshot). With --connect, runs a configured set on the remote service."));
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
                // behind the gateway; what is left here is the same either way —
                // a remote service, like a local one, runs only a configured set.
                var remote = ResolveRemote(parse, parse.GetValue(directOption));
                var gateway = remote is { } target
                    ? await OperationGateway.OpenForRemoteAsync(
                        target.Host, target.Port, target.State, target.Fingerprint, cancellationToken).ConfigureAwait(false)
                    : await OperationGateway.OpenForWriteAsync(
                        Repo(parse),
                        PassphraseEnv(parse),
                        parse.GetValue(stateOption),
                        parse.GetValue(directOption),
                        cancellationToken,
                        sessionLogger).ConfigureAwait(false);

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
            var command = WithRemoteCapableSession(new Command("snapshots", "List the catalogue's known snapshots, newest first. With --connect, lists the remote service's snapshots."));

            command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
            {
                if (ResolveRemote(parse, direct: false) is { } target)
                {
                    error.WriteLine($"mode: service (remote) — {target.Host}:{target.Port}");
                    var result = await QueryRemoteAsync<SnapshotsResult>(
                        target, new ListSnapshotsCommand(), cancellationToken).ConfigureAwait(false);

                    if (result.Snapshots.Count == 0)
                    {
                        output.WriteLine("no snapshots known to the service.");
                        return 0;
                    }

                    // The service carries no signature state on the wire, so the
                    // remote listing shows the file count where the local one
                    // shows the signature column.
                    foreach (var snapshot in result.Snapshots)
                    {
                        var capturedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)snapshot.CapturedAt)
                            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                        var captureStatus = snapshot.CaptureStatus == 1 ? "complete" : "partial";
                        var destinations = snapshot.Destinations is { Count: > 0 }
                            ? "  " + string.Join(", ", snapshot.Destinations)
                            : string.Empty;

                        // A service too old to report the method says nothing
                        // rather than claiming "live" on its behalf: those are
                        // different answers (specification 06 §6).
                        var consistency = snapshot.ConsistencyMethod is { } method
                            ? $"  {ConsistencyName(method)}"
                            : string.Empty;
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"{snapshot.SnapshotId}  {capturedAt}  {captureStatus,-8}  {snapshot.Files} file(s){consistency}{destinations}"));
                    }

                    return 0;
                }

                using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);
                using var catalogue = Catalogue.Open(session.CataloguePath, session.Repository.RepositoryId, catalogueLogger);

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
                    output.WriteLine(
                        $"{Hex(snapshot.SnapshotId)}  {when}  {status,-8}  {signature,-10}  " +
                        ConsistencyName(snapshot.ConsistencyMethod));
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
            var command = WithRemoteCapableSession(new Command("ls", "List a directory within a snapshot, from the catalogue. With --connect, lists it from the remote service."));
            command.Arguments.Add(snapshotArgument);
            command.Arguments.Add(pathArgument);

            command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
            {
                if (ResolveRemote(parse, direct: false) is { } target)
                {
                    error.WriteLine($"mode: service (remote) — {target.Host}:{target.Port}");
                    var result = await QueryRemoteAsync<DirectoryResult>(
                        target,
                        new ListDirectoryCommand(parse.GetValue(snapshotArgument)!, parse.GetValue(pathArgument)),
                        cancellationToken).ConfigureAwait(false);

                    // The service names each entry by its leaf; a size is shown
                    // only for files, as on the local path.
                    foreach (var entry in result.Entries)
                    {
                        var entryKind = entry.Kind switch
                        {
                            "directory" => "dir ",
                            "symlink" => "link",
                            "special" => "spec",
                            _ => "file",
                        };
                        var entrySize = entry.Kind == "file"
                            ? entry.Length.ToString(CultureInfo.InvariantCulture)
                            : string.Empty;
                        output.WriteLine($"{entryKind}  {entrySize,12}  {entry.Name}");
                    }

                    return 0;
                }

                using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);
                using var catalogue = Catalogue.Open(session.CataloguePath, session.Repository.RepositoryId, catalogueLogger);

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
                        throw new CliFailureException(Strings.FormatCliApplication_SnapshotNotCatalogue(Hex(snapshotId)));
                    }

                    if (path.Length > 0 && catalogue.LookupPath(snapshotId, path) is null)
                    {
                        throw new CliFailureException(Strings.FormatCliApplication_DoesNotExistSnapshot(path, Hex(snapshotId)));
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
            var outputOption = new Option<string>("--output") { Description = "Destination directory. With --connect this is a path on the service's machine (ADR-0028 §6) — the console is told where, never sent the files.", Required = true };
            var command = WithRemoteCapableSession(new Command(
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
            var command = WithRemoteCapableSession(new Command(
                "check", "Repository health: blob verification sweep, journal survey, and the catalogue's damage findings."));
            command.Options.Add(levelOption);
            command.Options.Add(directOption);

            command.SetAction((parse, cancellationToken) => GuardAsync(() => ReadThroughGatewayAsync(
                parse,
                (gateway, token) => gateway.CheckAsync(parse.GetValue(levelOption)!, token),
                cancellationToken)));
        }

        // ----------------------------------------------------------------- sync

        {
            var setOption = new Option<string>("--set")
            {
                Description = "Sync only this backup set; every configured set otherwise.",
            };
            var destinationOption = new Option<string>("--destination")
            {
                Description = "Sync only this declared destination; each set's every destination otherwise.",
            };
            var command = WithRemoteCapableSession(new Command(
                "sync",
                "Converge declared destinations now, outside the schedule (ADR-0034 §3) — one pass per (set, destination) pair, answered from the sync ledger."));
            command.Options.Add(setOption);
            command.Options.Add(destinationOption);
            command.Options.Add(directOption);

            command.SetAction((parse, cancellationToken) => GuardAsync(() => ReadThroughGatewayAsync(
                parse,
                (gateway, token) => gateway.SyncAsync(
                    parse.GetValue(setOption), parse.GetValue(destinationOption), token),
                cancellationToken)));
        }

        // -------------------------------------------------------------- changes

        {
            var setOption = new Option<string>("--set")
            {
                Description = "Compare only this backup set; the default (first) set otherwise.",
            };
            var limitOption = new Option<int?>("--limit")
            {
                Description = "The most paths listed per bucket; counts stay exact past it.",
            };
            var command = WithRemoteCapableSession(new Command(
                "changes",
                "What changed under a set's source since its last backup (ADR-0038): new, updated, moved, "
                + "deleted, and files the rules no longer include. Read-only; nothing is captured."));
            command.Options.Add(setOption);
            command.Options.Add(limitOption);
            command.Options.Add(directOption);

            command.SetAction((parse, cancellationToken) => GuardAsync(() => ReadThroughGatewayAsync(
                parse,
                (gateway, token) => gateway.PreviewSetChangesAsync(
                    parse.GetValue(setOption), parse.GetValue(limitOption), token),
                cancellationToken)));
        }

        // -------------------------------------------------- verify-destination

        {
            var setOption = new Option<string>("--set")
            {
                Description = "Verify only this backup set; every configured set otherwise.",
            };
            var destinationOption = new Option<string>("--destination")
            {
                Description = "Verify only this declared destination; each set's every destination otherwise.",
            };
            var fullOption = new Option<bool>("--full")
            {
                Description = "Read every stored object now, rather than the next bounded segment (FR-VER-004).",
            };
            var probeOption = new Option<bool>("--probe")
            {
                Description =
                    "Read nothing: confirm only that the destination could take a backup — the address works, "
                    + "the directory exists and accepts writes, or the peer is reachable and honours the grant.",
            };
            var command = WithRemoteCapableSession(new Command(
                "verify-destination",
                "Ask what a destination can still be trusted for (peer-protocol 04): --probe confirms it could "
                + "take a backup, the default re-reads a bounded segment of its stored bytes, --full re-reads "
                + "every one. `verify` sweeps this hub's own archives instead."));
            command.Options.Add(setOption);
            command.Options.Add(destinationOption);
            command.Options.Add(fullOption);
            command.Options.Add(probeOption);
            command.Options.Add(directOption);

            command.SetAction((parse, cancellationToken) => GuardAsync(() => ReadThroughGatewayAsync(
                parse,
                (gateway, token) => gateway.VerifyDestinationAsync(
                    parse.GetValue(setOption), parse.GetValue(destinationOption), parse.GetValue(fullOption),
                    parse.GetValue(probeOption), token),
                cancellationToken)));
        }

        // ------------------------------------------------------------ retention

        {
            var applyOption = new Option<bool>("--apply")
            {
                Description = "Tombstone, sweep and trim — the destructive half. Without it the pass reports only (FR-GC-005).",
            };
            var command = WithRemoteCapableSession(new Command(
                "retention",
                "Run a retention pass per configured set (architecture 07): the report always, deletion only with --apply."));
            command.Options.Add(applyOption);
            command.Options.Add(directOption);

            command.SetAction((parse, cancellationToken) => GuardAsync(() => ReadThroughGatewayAsync(
                parse,
                (gateway, token) => gateway.RetentionAsync(parse.GetValue(applyOption), token),
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
                using var passphrase = CliSession.ReadPassphrase(PassphraseEnv(parse));

                var kit = await RecoveryKitFactory.BuildAsync(
                    session.Store,
                    passphrase,
                    session.DeviceId,
                    (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    [new FallbackPlan.Repository.Format.RecoveryKit.KitDestination(
                        "local-path", Path.GetFullPath(Repo(parse)), string.Empty, string.Empty)],
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

        // ---------------------------------------------------------------- pair



        // Opens a connection for a session verb. Unlike every other CLI path
        // this has no direct-mode fallback and should not: a session is minted
        // by a running service and means nothing without one, so "no service"
        // is the answer rather than a reason to do the work here instead.
        static async Task<IFallbackPlanClient> ConnectForSessionAsync(
            string stateDirectory, CancellationToken cancellationToken)
        {
            try
            {
                return await LocalServiceClient
                    .ConnectAsync(stateDirectory, "fallbackplan-cli", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ServiceConnectionException unreachable)
            {
                throw new CliFailureException(
                    $"{unreachable.Message} Sessions are held by the running service, so there is nobody "
                    + "to sign in to until one is up.",
                    unreachable);
            }
        }
        // `login` and `logout` — who is acting, rather than which process may
        // connect (ADR-0045 §1). Neither opens a repository: the service holds
        // the accounts, and this end holds only the token it is handed back.
        {
            var loginStateOption = new Option<string>("--state")
            {
                Description = "The service's state directory — where its local socket and this session live.",
                Required = true,
            };
            var userOption = new Option<string>("--user")
            {
                Description = "The account name.",
                Required = true,
            };
            var passwordVariableOption = new Option<string>("--password-env")
            {
                Description =
                    "The environment variable holding the password. A password is named by variable and "
                    + "never given on a command line, where it would reach the shell history and every "
                    + "process listing on the machine (FR-USR-006).",
                Required = true,
            };

            var login = new Command(
                "login",
                "Sign in to a running service and cache the session, so later commands need no password.");
            login.Options.Add(loginStateOption);
            login.Options.Add(userOption);
            login.Options.Add(passwordVariableOption);
            root.Subcommands.Add(login);

            login.SetAction((parse, cancellationToken) => GuardAsync(async () =>
            {
                var state = parse.GetValue(loginStateOption)!;
                var user = parse.GetValue(userOption)!;
                var variable = parse.GetValue(passwordVariableOption)!;

                if (Environment.GetEnvironmentVariable(variable) is not { Length: > 0 } password)
                {
                    throw new CliFailureException(
                        $"the environment variable '{variable}' is not set. The password is passed by name, "
                        + "never on the command line (FR-USR-006).");
                }

                var client = await ConnectForSessionAsync(state, cancellationToken).ConfigureAwait(false);
                await using (client.ConfigureAwait(false))
                {
                    var answered = await client
                        .ExecuteAsync(new LoginCommand(user, password), cancellationToken)
                        .ConfigureAwait(false);

                    switch (answered)
                    {
                        case SessionResult session:
                            new SessionCache(state).Save(session);
                            output.WriteLine($"signed in as {session.User} ({session.Role.ToLowerInvariant()})");
                            return 0;

                        case ServiceError refusal:
                            throw new CliFailureException(refusal.Message);

                        default:
                            throw new CliFailureException(
                                $"the service answered with {answered.GetType().Name}.");
                    }
                }
            }));

            var logoutState = new Option<string>("--state")
            {
                Description = "The service's state directory.",
                Required = true,
            };
            var logout = new Command("logout", "End this session at the service and forget it here.");
            logout.Options.Add(logoutState);
            root.Subcommands.Add(logout);

            logout.SetAction((parse, cancellationToken) => GuardAsync(async () =>
            {
                var state = parse.GetValue(logoutState)!;
                var cache = new SessionCache(state);
                var who = cache.User;

                // The local cache is cleared whatever the service says. A
                // service that is down cannot revoke, and leaving a token here
                // that the operator believes they have signed out of is the
                // worse of the two failures — the session dies with that
                // process anyway (ADR-0045 §5).
                try
                {
                    var client = await ConnectForSessionAsync(state, cancellationToken).ConfigureAwait(false);
                    await using (client.ConfigureAwait(false))
                    {
                        await cache.PresentAsync(client, cancellationToken).ConfigureAwait(false);
                        await client.ExecuteAsync(new LogoutCommand(), cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (CliFailureException)
                {
                    error.WriteLine(
                        "note: the service could not be reached, so nothing was revoked there. The session "
                        + "is forgotten here, and no session survives a service restart.");
                }

                cache.Clear();
                output.WriteLine(who is null ? "signed out" : $"signed out {who}");
                return 0;
            }));
        }
        {
            // Not a session verb: pairing needs no repository or passphrase,
            // only the console's own state directory to hold its identity and
            // the grant it is about to pin (ADR-0030 §1).
            var connectArgument = new Argument<string>("host:port")
            {
                Description = "The service's remote binding, as host:port.",
            };
            var stateArgOption = new Option<string>("--state")
            {
                Description = "The console's state directory (its peer identity and pairings).",
                Required = true,
            };
            var labelOption = new Option<string?>("--label")
            {
                Description = "A human label for this service, for display only.",
            };
            var roleOption = new Option<string?>("--role")
            {
                Description = "The role this console records for the service: stores-here, stores-for-us (default), "
                    + "or both. Declared on the wire and approved by both humans (ADR-0030 Amendment 2).",
            };

            var command = new Command("pair", "Pair this console with a service's remote binding (ADR-0030).");
            command.Arguments.Add(connectArgument);
            command.Options.Add(stateArgOption);
            command.Options.Add(labelOption);
            command.Options.Add(roleOption);
            root.Subcommands.Add(command);

            command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
            {
                var target = parse.GetValue(connectArgument)!;
                if (!TryParseEndpoint(target, out var host, out var port))
                {
                    throw new CliFailureException($"'{target}' is not host:port.");
                }

                var state = parse.GetValue(stateArgOption)!;
                var label = parse.GetValue(labelOption);
                if (!PeerRoles.TryParse(parse.GetValue(roleOption), out var role))
                {
                    throw new CliFailureException(
                        $"--role '{parse.GetValue(roleOption)}' is not stores-here, stores-for-us, or both.");
                }

                using var keypair = PeerKeypairStore.Open(state);
                var grants = PeerGrantStore.Open(state);

                output.WriteLine($"this console is peer {keypair.Identity.Fingerprint}");
                output.WriteLine($"dialling {host}:{port} …");

                await using var connection = await PeerTlsConnection.DialAsync(
                    host, port, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

                var result = await PairingCeremony.OfferAsync(
                    connection.Stream, keypair, grants, label ?? Environment.MachineName, role,
                    (prospect, _) =>
                    {
                        output.WriteLine($"pairing with {prospect.PeerLabel} (peer {prospect.PeerIdentity.Fingerprint})");
                        output.WriteLine($"they will record this console as: {prospect.TheirRoleForUs}");
                        output.WriteLine($"compare this string on both devices: {prospect.ShortAuthenticationString}");
                        output.Write("do the strings match, and do you approve? [y/N] ");
                        output.Flush();
                        var answer = Console.In.ReadLine();
                        return ValueTask.FromResult(answer is not null && answer.Trim().StartsWith('y'));
                    },
                    (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false);

                if (result.Approved)
                {
                    output.WriteLine($"paired with {result.Grant!.Label} ({result.Grant.Identity.Fingerprint}).");
                    return 0;
                }

                output.WriteLine($"pairing did not complete: {result.Refusal?.Text ?? "the peer went away"}.");
                return 1;
            }));
        }

        // ---------------------------------------------------------------- logs

        {
            var levelFilterOption = new Option<string?>("--level")
            {
                Description = "Show records at this level and above: trace, debug, information, warning, error, critical.",
            };
            var sinceOption = new Option<long>("--since")
            {
                Description = "Start after this sequence number. 0 starts at the oldest record still held.",
            };
            var tailOption = new Option<int>("--tail")
            {
                Description = "Show at most this many of the most recent records.",
                DefaultValueFactory = _ => 50,
            };
            var followOption = new Option<bool>("--follow")
            {
                Description = "Keep reading as records arrive, until interrupted.",
            };

            var command = WithRemoteCapableSession(new Command(
                "logs",
                "Read the service's diagnostic log (ADR-0043 §6). Needs a running service: the records live "
                + "in its memory, so there is no --direct equivalent."));
            command.Options.Add(levelFilterOption);
            command.Options.Add(sinceOption);
            command.Options.Add(tailOption);
            command.Options.Add(followOption);

            command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
            {
                // The one read verb with no local path. Every other one can fall
                // back to reading the repository itself; this cannot, because the
                // ring is in the running service's memory and a service that is
                // not running has no log to show. So it takes the service's
                // address directly — --state for the local binding, --connect for
                // a paired one — and never opens a repository it does not need.
                var remote = ResolveRemote(parse, direct: false);
                var state = parse.GetValue(stateOption);
                if (remote is null && state is not { Length: > 0 })
                {
                    throw new CliFailureException(
                        "logs reads a running service, so it needs to know which one: pass --state <dir> for the "
                        + "service on this machine, or --connect <host:port> --fingerprint <fp> for a paired one. "
                        + "There is no --direct mode — the records are in the service's memory, not in the repository.");
                }

                var level = parse.GetValue(levelFilterOption);
                var tail = Math.Max(parse.GetValue(tailOption), 1);
                var follow = parse.GetValue(followOption);
                var cursor = parse.GetValue(sinceOption);

                async ValueTask<LogRecordsResult> ReadAsync(long since, int maximum)
                {
                    var request = new ReadLogCommand(since, maximum, level);
                    if (remote is { } target)
                    {
                        return await QueryRemoteAsync<LogRecordsResult>(target, request, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    LocalServiceClient client;
                    try
                    {
                        client = await LocalServiceClient.ConnectAsync(
                            state!, "fallbackplan-cli", cancellationToken).ConfigureAwait(false);
                        await new SessionCache(state!)
                            .PresentAsync(client, cancellationToken).ConfigureAwait(false);
                    }
                    catch (ServiceConnectionException unreachable)
                    {
                        // Nothing listening is an ordinary condition here, not a
                        // crash. Everywhere else in the CLI it means "do the work
                        // in this process instead"; this verb has no such
                        // fallback, so it has to say what it found.
                        throw new CliFailureException(
                            $"{unreachable.Message} Logs are held by the running service, so there is nothing "
                            + "to read until one is up.",
                            unreachable);
                    }

                    await using (client.ConfigureAwait(false))
                    {
                        return await client.ExecuteAsync(request, cancellationToken).ConfigureAwait(false) switch
                        {
                            LogRecordsResult page => page,
                            ServiceError failure => throw new CliFailureException(failure.Message),
                            var other => throw new CliFailureException(
                                $"the service answered with {other.GetType().Name}."),
                        };
                    }
                }

                if (remote is not null)
                {
                    // Said once, because it changes what the records mean: a
                    // paired console reads redacted, so a path is a hash here
                    // and the whole path on the machine that holds the files.
                    error.WriteLine("mode: service (remote) — records are redacted for a paired caller (ADR-0043 §4).");
                }

                var first = await ReadAsync(cursor, tail).ConfigureAwait(false);
                WriteLogPage(first, output, error);
                cursor = first.NextSequence;

                while (follow && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    var page = await ReadAsync(cursor, 200).ConfigureAwait(false);
                    WriteLogPage(page, output, error);
                    cursor = page.NextSequence;
                }

                return 0;
            }));
        }

        // -------------------------------------------------------------- status

        {
            var command = WithRemoteCapableSession(new Command(
                "status", "Per-set protection status (architecture 10 §1) — captured is never protected, degraded is never unrecoverable. With --connect, the remote service's status."));

            command.SetAction((parse, cancellationToken) => GuardAsync(async () =>
            {
                if (ResolveRemote(parse, direct: false) is { } target)
                {
                    var result = await QueryRemoteAsync<StatusResult>(
                        target, new GetStatusCommand(), cancellationToken).ConfigureAwait(false);

                    error.WriteLine($"mode: service (remote) — {result.MachineName}");
                    foreach (var notice in result.Notices)
                    {
                        output.WriteLine($"notice: {notice}");
                    }

                    foreach (var set in result.Sets)
                    {
                        // A verification claim is a coverage and a date, never
                        // a bare tick (10 §1.2) — rendered wherever it exists.
                        output.WriteLine(
                            $"{set.SetName,-20} {set.Status.State,-14} next: {set.NextRun ?? "manual"}{DescribeVerification(set.Status.Verification)}");
                        foreach (var row in set.Destinations)
                        {
                            // The matrix beneath the roll-up (ADR-0028 §8):
                            // the detail is what the summary was computed from.
                            output.WriteLine(
                                $"  -> {row.Name,-18} {row.Kind,-11} {row.State,-13} {row.FailureDomain,-13} {row.Verification,-21}{(row.Detail is null ? string.Empty : $" {row.Detail}")}");
                        }
                    }

                    return 0;
                }

                using var session = await OpenSessionAsync(parse, cancellationToken).ConfigureAwait(false);
                using var catalogue = Catalogue.Open(session.CataloguePath, session.Repository.RepositoryId, catalogueLogger);
                var configuration = ClientConfiguration.Load(session.ConfigurationPath);
                var jobs = JobStateStore.Open(session.StateDirectory);
                var repoPath = Repo(parse);

                var findings = catalogue.Findings();
                var requiredMissing = findings.Any(finding =>
                    finding.Kind is DamageKind.MissingBlob or DamageKind.MissingIndexObject);
                var ledger = DestinationSyncStore.Open(session.StateDirectory);
                var snapshots = catalogue.EnumerateSnapshots();

                var sets = configuration.BackupSets.Count > 0
                    ? configuration.BackupSets
                    : [new BackupSetConfiguration
                       {
                           Id = Hex(session.BackupSetId), Name = "(default)", Roots = [],
                       }];

                foreach (var set in sets)
                {
                    var setId = Convert.FromHexString(set.Id);
                    var latest = snapshots.FirstOrDefault(row => row.BackupSetId.Span.SequenceEqual(setId));

                    // The destination matrix (ADR-0034): one row per declared
                    // destination, from the sync ledger. The failure-domain
                    // fact (PT-8) is a device-identity comparison for a local
                    // path, conservative when the platform cannot say; a peer
                    // or cloud destination is another machine by construction.
                    var lastCompleted = jobs.LastCompleted(set.Id)?.UpdatedAt ?? 0;
                    var destinations = new List<DestinationStatusInput>();
                    foreach (var reference in set.Destinations)
                    {
                        destinations.Add(DestinationStatus.Describe(
                            reference.Ref,
                            configuration.FindDestination(reference.Ref),
                            [.. set.Roots.Select(root => root.Path)],
                            ledger.Find(set.Id, reference.Ref),
                            lastCompleted,
                            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            DeviceProbe.DeviceOf));
                    }

                    var status = StatusDeriver.Derive(new StatusInputs
                    {
                        LatestSnapshotAt = latest?.CapturedAt,
                        LatestCaptureStatus = latest?.CaptureStatus,
                        Destinations = destinations,
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
                        var anchor = jobs.ScheduleAnchor(set.Id);
                        nextRun = schedule!.NextRun(anchor, DateTimeOffset.Now)
                            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    }

                    output.WriteLine(
                        $"{set.Name,-20} {status.State,-14} last: {lastProtected}  next: {nextRun}{DescribeVerification(status.Verification)}");
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

    /// <summary>
    /// Writes one page of log records, and says when records were missed.
    /// </summary>
    /// <param name="page">The page the service answered with.</param>
    /// <param name="output">Where the records go.</param>
    /// <param name="error">Where the gap notice goes, so a redirect keeps the records clean.</param>
    /// <remarks>
    /// The dropped notice is not decoration. A reader that has fallen behind the
    /// ring has missed records, which is a different fact from a quiet service,
    /// and a client that renders the two alike will one day have somebody
    /// reporting that nothing happened during the hour everything did.
    /// </remarks>
    private static void WriteLogPage(LogRecordsResult page, TextWriter output, TextWriter error)
    {
        if (page.Dropped)
        {
            error.WriteLine(
                "warning: records were dropped before this page — the service logged faster than this reader "
                + "was reading. Raise ring_capacity in config.json, or read more often.");
        }

        foreach (var record in page.Records)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{record.Sequence,-8}  " +
                $"{DateTimeOffset.FromUnixTimeMilliseconds(record.TimestampUnixMilliseconds):u}  " +
                $"{record.Level,-11}  {record.EventId,-5}  {record.Category}  {record.Message}"));

            if (record.ExceptionType is { Length: > 0 })
            {
                output.WriteLine($"{string.Empty,-8}  {record.ExceptionType}: {record.ExceptionMessage}");
            }
        }
    }

    /// <summary>
    /// A verification claim rendered as coverage and age — the only form
    /// `verified` is allowed to take (10 §1.2) — or nothing when no pass has
    /// ever proven bytes at a destination.
    /// </summary>
    private static string DescribeVerification(FallbackPlan.Domain.Status.VerificationDetail? verification) =>
        verification is { } detail
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"  verified: {detail.Coverage:P0} of objects at {DateTimeOffset.FromUnixTimeMilliseconds((long)detail.VerifiedAtUnixMilliseconds):yyyy-MM-dd HH:mm}")
            : string.Empty;
}
