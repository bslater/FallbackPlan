using Bodu;
using System.Globalization;
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

        string Require(string name) => Get(name)
            ?? throw new RecoveryFailureException(Strings.FormatRecoveryHost_MissingRequiredOption(name));

        if (!ConsoleLogging.TryResolveLevel(Get("--log-level"), out var logLevel, out var levelRefusal))
        {
            error.WriteLine($"error: {levelRefusal}");
            return 1;
        }

        var log = ConsoleLogging.For(error, logLevel, typeof(RecoveryHost).FullName!);

        try
        {
            var command = args[0];

            // A flag from the kit era is refused by name rather than
            // ignored: somebody following an old note must learn the
            // ceremony changed, not wonder why the file was never read.
            if (args.Contains("--kit", StringComparer.Ordinal))
            {
                throw new RecoveryFailureException(Strings.RecoveryHost_KitWithdrawn);
            }

            var repoPath = Require("--repo");
            var passphraseVariable = Require("--passphrase-env");

            var passphraseValue = Environment.GetEnvironmentVariable(passphraseVariable);
            if (string.IsNullOrEmpty(passphraseValue))
            {
                throw new RecoveryFailureException(Strings.FormatRecoveryHost_EnvironmentVariableUnset(passphraseVariable));
            }

            using var passphrase = Passphrase.Create(passphraseValue);
            using var session = await RecoverySession.OpenAsync(
                passphrase, new LocalFileSystemObjectStore(repoPath), cancellationToken)
                .ConfigureAwait(false);
            var repositoryHex = Convert.ToHexString(session.RepositoryId.ToArray()).ToLowerInvariant();
            Log.DescriptorRead(log, repositoryHex, session.FormatVersion);
            Log.KeysDerived(log);

            switch (command)
            {
                case "open":
                {
                    output.WriteLine($"repository     {repositoryHex}");
                    // What it writes now, and what it was created at when an
                    // upgrade record has moved the two apart (11 §4.1).
                    var version = session.EffectiveFormatVersion == session.FormatVersion
                        ? string.Create(CultureInfo.InvariantCulture, $"{session.FormatVersion}")
                        : string.Create(
                            CultureInfo.InvariantCulture,
                            $"{session.EffectiveFormatVersion} (created at {session.FormatVersion})");
                    output.WriteLine($"format         {version}");
                    output.WriteLine(
                        "derivation     reproduced — this passphrase opens this archive, and every other "
                        + "archive this installation wrote");
                    var (blobs, notes) = await session.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"blobs          {blobs} readable"));
                    foreach (var note in notes)
                    {
                        output.WriteLine($"note           {note}");
                    }

                    return 0;
                }

                case "snapshots":
                {
                    await session.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);
                    var snapshots = await session.ListSnapshotsAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var snapshot in snapshots)
                    {
                        var when = DateTimeOffset.FromUnixTimeMilliseconds((long)snapshot.Manifest.CaptureCompletedAt)
                            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                        var signature = snapshot.SignatureVerified ? "verified" : "SIGNATURE-FAILED";
                        output.WriteLine(
                            $"{Convert.ToHexString(snapshot.Manifest.SnapshotId.Span).ToLowerInvariant()}  {when}  {signature}");
                    }

                    return snapshots.Count == 0 ? 2 : 0;
                }

                case "restore":
                {
                    var snapshotHex = Require("--snapshot");
                    var destination = Require("--output");
                    var wanted = Convert.FromHexString(snapshotHex);

                    await session.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);
                    var snapshots = await session.ListSnapshotsAsync(cancellationToken).ConfigureAwait(false);
                    var snapshot = snapshots.FirstOrDefault(row => row.Manifest.SnapshotId.Span.SequenceEqual(wanted))
                        ?? throw new RecoveryFailureException(Strings.FormatRecoveryHost_NoSnapshotDiscoverableStore(snapshotHex));

                    if (!snapshot.SignatureVerified)
                    {
                        error.WriteLine("warning: this snapshot's signature does NOT verify — a security finding (06 §6.1); restoring anyway because recovery is the last line of defence.");
                    }

                    var report = await session.RestoreTreeAsync(snapshot.Manifest.RootTree, destination, cancellationToken)
                        .ConfigureAwait(false);

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
                    throw new RecoveryFailureException(Strings.FormatRecoveryHost_UnknownCommandRunWithHelp(command));
            }
        }
        catch (RecoveryFailureException exception)
        {
            error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (KeyUnwrapFailedException)
        {
            Log.PassphraseRefused(log);
            error.WriteLine(
                "error: the passphrase does not reproduce this archive's keys — wrong passphrase, or an archive "
                + "another installation wrote.");
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
}
