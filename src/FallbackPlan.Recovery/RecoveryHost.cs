using Bodu;
using System.Globalization;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Domain.Status;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;
using FallbackPlan.Recovery.Resources;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Recovery;

/// <summary>
/// The standalone recovery tool's command line, as a callable unit. This is
/// the last line of defence (architecture 08 §5; FR-DRL-001; ADR-0060), so the case
/// for testing it is the strongest in the codebase — and until now every
/// command lived in <c>Main</c>, where only launching a process could reach
/// one.
/// </summary>
/// <remarks>
/// Argument parsing stays hand-rolled: the smallest possible dependency
/// closure is the point of this executable, and a parser library would be a
/// dependency the clean-machine premise has to carry.
/// </remarks>
public static class RecoveryHost
{
    /// <summary>Runs one recovery command.</summary>
    /// <param name="args">The command line, as the process received it.</param>
    /// <param name="output">Where results and help are written.</param>
    /// <param name="error">Where operator-facing failures and warnings are written.</param>
    /// <param name="cancellationToken">Cancels the store reads.</param>
    /// <returns>0 on success, 1 for a failure with a stated reason, 2 when nothing was recoverable.</returns>
    public static async Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ThrowHelper.ThrowIfNull(args);
        ThrowHelper.ThrowIfNull(output);
        ThrowHelper.ThrowIfNull(error);
        // The standalone recovery tool (architecture 08 §5; ADR-0060): opens a
        // repository with nothing but a store location and the passphrase.
        // Argument parsing is by hand on purpose — the smallest possible
        // dependency closure is the point of this executable.

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine("""
                FallbackPlan standalone recovery tool

                usage:
                  fallbackplan-recover open      --repo <path> --passphrase-env <VAR>
                  fallbackplan-recover snapshots --repo <path> --passphrase-env <VAR>
                                                 (newest first)
                  fallbackplan-recover restore   --repo <path> --passphrase-env <VAR>
                                                 --snapshot <hex> --output <dir>

                The passphrase is the whole credential: the archive's own descriptor
                carries everything else the derivation needs, so one passphrase opens
                every archive it wrote, wherever that archive is now — a destination
                folder, a drive, or a replica copied back from a peer.

                Every verb accepts --log-level <trace|debug|information|warning|
                error|critical|none>, which also reads FALLBACKPLAN_LOG_LEVEL. Logs
                go to standard error and nowhere else — this tool writes no file it
                was not asked to write (ADR-0043 §6).

                Every verb also accepts --diagnostic-bundle <file>, which writes one
                zip describing the run, however it ended: what the archive said, how
                far the run got and why it stopped, for whoever is helping. It holds
                no passphrase, key or salt, shortens identifiers and turns paths into
                short digests; add --include-paths to keep paths as they are, which
                the tool says before it starts. It never writes over a file that is
                already there.
                """);
            return 0;
        }

        string? Get(string name)
        {
            for (var i = 1; i < args.Length - 1; i++)
            {
                if (args[i] == name)
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        if (!ConsoleLogging.TryResolveLevel(Get("--log-level"), out var logLevel, out var levelRefusal))
        {
            error.WriteLine($"error: {levelRefusal}");
            return 1;
        }

        // A diagnostic bundle is asked for on the run it describes (ADR-0082),
        // and everything that would stop it being written is refused before
        // the archive is touched.
        var bundle = Get("--diagnostic-bundle");
        var includePaths = args.Contains("--include-paths", StringComparer.Ordinal);
        if (includePaths && bundle is null)
        {
            error.WriteLine(
                "error: --include-paths changes only a diagnostic bundle — name one with --diagnostic-bundle <file>.");
            return 1;
        }

        if (args.Contains("--diagnostic-bundle", StringComparer.Ordinal)
            && (bundle is null || bundle.StartsWith("--", StringComparison.Ordinal)))
        {
            error.WriteLine("error: --diagnostic-bundle needs the file to write.");
            return 1;
        }

        if (bundle is not null)
        {
            if (File.Exists(bundle) || Directory.Exists(bundle))
            {
                error.WriteLine(
                    $"error: '{bundle}' already exists — a diagnostic bundle is never written over a file that is already there.");
                return 1;
            }

            if (includePaths)
            {
                error.WriteLine($"warning: {RecoveryBundle.PathsConsequence}");
            }
        }

        var log = ConsoleLogging.For(error, logLevel, typeof(RecoveryHost).FullName!);
        var run = new RecoveryRun(includePaths, logLevel);

        int exitCode;
        try
        {
            exitCode = await ExecuteAsync(args, Get, output, error, log, run, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (bundle is not null)
        {
            // Nothing anticipated this one, which is when a bundle is worth
            // most; the exception still leaves the way it always did.
            run.Fail(exception);
            WriteBundle(bundle, run, error);
            throw;
        }

        run.ExitCode = exitCode;
        if (bundle is not null && !WriteBundle(bundle, run, error) && exitCode == 0)
        {
            exitCode = 1;
        }

        return exitCode;
    }

    /// <summary>Runs the verb, recording into <paramref name="run"/> what it did and found.</summary>
    private static async Task<int> ExecuteAsync(
        string[] args,
        Func<string, string?> get,
        TextWriter output,
        TextWriter error,
        ILogger log,
        RecoveryRun run,
        CancellationToken cancellationToken)
    {
        string Require(string name) => get(name)
            ?? throw run.Refuse(
                RecoveryFailureReason.MissingOption, Strings.FormatRecoveryHost_MissingRequiredOption(name), name);

        try
        {
            var command = args[0];
            run.Verb = command;
            run.Repo = get("--repo");
            run.Snapshot = get("--snapshot");
            run.Output = get("--output");

            // A flag from the kit era is refused by name rather than
            // ignored: somebody following an old note must learn the
            // ceremony changed, not wonder why the file was never read.
            if (args.Contains("--kit", StringComparer.Ordinal))
            {
                throw run.Refuse(RecoveryFailureReason.KitWithdrawn, Strings.RecoveryHost_KitWithdrawn, "--kit");
            }

            var repoPath = Require("--repo");
            var passphraseVariable = Require("--passphrase-env");
            run.PassphraseVariableNamed = true;

            var passphraseValue = Environment.GetEnvironmentVariable(passphraseVariable);
            run.PassphraseVariableSet = !string.IsNullOrEmpty(passphraseValue);
            if (string.IsNullOrEmpty(passphraseValue))
            {
                throw run.Refuse(
                    RecoveryFailureReason.PassphraseVariableUnset,
                    Strings.FormatRecoveryHost_EnvironmentVariableUnset(passphraseVariable),
                    "--passphrase-env");
            }

            using var passphrase = Passphrase.Create(passphraseValue);
            using var session = await RecoverySession.OpenAsync(
                passphrase, new LocalFileSystemObjectStore(repoPath), run, cancellationToken)
                .ConfigureAwait(false);
            var repositoryHex = Convert.ToHexString(session.RepositoryId.ToArray()).ToLowerInvariant();
            var repository = LogId.Repository(repositoryHex);
            Log.DescriptorRead(log, repository, session.FormatVersion);
            Log.KeysDerived(log);

            switch (command)
            {
                case "open":
                {
                    output.WriteLine($"repository     {repositoryHex}");
                    // What it writes now, and what it was created at when an
                    // upgrade record has moved the two apart (11 §5.1).
                    var version = session.EffectiveFormatVersion == session.FormatVersion
                        ? string.Create(CultureInfo.InvariantCulture, $"{session.FormatVersion}")
                        : string.Create(
                            CultureInfo.InvariantCulture,
                            $"{session.EffectiveFormatVersion} (created at {session.FormatVersion})");
                    output.WriteLine($"format         {version}");
                    output.WriteLine(
                        "derivation     reproduced — this passphrase opens this archive, and every other "
                        + "archive this installation wrote");
                    run.Stage = RecoveryStage.Blobs;
                    var (blobs, notes) = await session.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);
                    run.Loaded(blobs, notes);
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"blobs          {blobs} readable"));
                    foreach (var note in notes)
                    {
                        output.WriteLine($"note           {note}");
                    }

                    return 0;
                }

                case "snapshots":
                {
                    run.Stage = RecoveryStage.Blobs;
                    var (blobs, notes) = await session.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);
                    run.Loaded(blobs, notes);
                    run.Stage = RecoveryStage.Snapshots;
                    var snapshots = await session.ListSnapshotsAsync(cancellationToken).ConfigureAwait(false);
                    run.Snapshots = snapshots;
                    foreach (var snapshot in snapshots)
                    {
                        var when = DateTimeOffset.FromUnixTimeMilliseconds((long)snapshot.Manifest.CaptureCompletedAt)
                            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                        var signature = snapshot.SignatureVerified ? "verified" : "SIGNATURE-FAILED";

                        // Last, so a script that takes the id and the time
                        // off the front reads every line as it always did.
                        var clock = ObservedClockSkewText.Token(snapshot.Manifest.ObservedClockSkewMs) is { } token
                            ? $"  {token}"
                            : string.Empty;
                        output.WriteLine(
                            $"{Convert.ToHexString(snapshot.Manifest.SnapshotId.Span).ToLowerInvariant()}  {when}  {signature}{clock}");
                    }

                    return snapshots.Count == 0 ? 2 : 0;
                }

                case "restore":
                {
                    var snapshotHex = Require("--snapshot");
                    var destination = Require("--output");
                    var wanted = Convert.FromHexString(snapshotHex);

                    run.Stage = RecoveryStage.Blobs;
                    var (blobs, notes) = await session.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);
                    run.Loaded(blobs, notes);
                    run.Stage = RecoveryStage.Snapshots;
                    var snapshots = await session.ListSnapshotsAsync(cancellationToken).ConfigureAwait(false);
                    run.Snapshots = snapshots;
                    var snapshot = snapshots.FirstOrDefault(row => row.Manifest.SnapshotId.Span.SequenceEqual(wanted))
                        ?? throw run.Refuse(
                            RecoveryFailureReason.SnapshotNotFound,
                            Strings.FormatRecoveryHost_NoSnapshotDiscoverableStore(snapshotHex));

                    if (!snapshot.SignatureVerified)
                    {
                        error.WriteLine("warning: this snapshot's signature does NOT verify — a security finding (06 §6.1); restoring anyway because recovery is the last line of defence.");
                    }

                    run.Stage = RecoveryStage.Restore;
                    var report = await session.RestoreTreeAsync(snapshot.Manifest.RootTree, destination, cancellationToken)
                        .ConfigureAwait(false);
                    run.Restore = report;

                    foreach (var note in report.Notes)
                    {
                        error.WriteLine(note);
                    }

                    Log.RecoveryComplete(log, report.Restored, report.Failed, report.Skipped);
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"restored {report.Restored} file(s), {report.Failed} failed, {report.Skipped} skipped, to {destination}"));
                    return report.Failed == 0 ? 0 : 2;
                }

                default:
                    throw run.Refuse(
                        RecoveryFailureReason.UnknownVerb, Strings.FormatRecoveryHost_UnknownCommandRunWithHelp(command));
            }
        }
        catch (RecoveryFailureException exception)
        {
            run.Fail(exception);
            error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (KeyUnwrapFailedException exception)
        {
            run.Fail(exception);
            Log.PassphraseRefused(log);
            error.WriteLine(
                "error: the passphrase does not reproduce this archive's keys — wrong passphrase, or an archive "
                + "another installation wrote.");
            return 1;
        }
        catch (FormatException exception)
        {
            run.Fail(exception);
            error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            run.Fail(exception);
            error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Writes the run's bundle where the person asked, and says so on standard
    /// error, so the verb's own output stays what a script reads.
    /// </summary>
    /// <returns>Whether the bundle was written.</returns>
    private static bool WriteBundle(string path, RecoveryRun run, TextWriter error)
    {
        try
        {
            var bytes = RecoveryBundle.Build(run, DateTimeOffset.UtcNow);

            // CreateNew, not Create: the check before the run is a courtesy,
            // and this is the refusal that cannot race.
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
            }

            error.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"wrote diagnostic bundle {path} ({bytes.Length} bytes); paths {(run.IncludePaths ? "included" : "left out")}."));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"error: the diagnostic bundle was not written: {exception.Message}");
            return false;
        }
    }
}
