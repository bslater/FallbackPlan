using Bodu;
using System.Globalization;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.RecoveryKit;
using FallbackPlan.Storage.Local;
using FallbackPlan.Recovery.Resources;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Recovery;

/// <summary>
/// The standalone recovery tool's command line, as a callable unit. This is
/// the last line of defence (architecture 08 §5; FR-KIT-006), so the case
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
        // The standalone recovery tool (architecture 08 §5; FR-KIT-006): opens a
        // repository with nothing but a store location, a recovery kit, and the
        // passphrase. Argument parsing is by hand on purpose — the smallest
        // possible dependency closure is the point of this executable.

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine("""
                FallbackPlan standalone recovery tool

                usage:
                  fallbackplan-recover open      --repo <path> --kit <file> --passphrase-env <VAR>
                  fallbackplan-recover snapshots --repo <path> --kit <file> --passphrase-env <VAR>
                  fallbackplan-recover restore   --repo <path> --kit <file> --passphrase-env <VAR>
                                                 --snapshot <hex> --output <dir>

                The kit file may be the binary form (FBPKRKIT) or the transcribable
                text form. The kit is one factor; the passphrase is the other.

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

            var repoPath = Require("--repo");
            var kitPath = Require("--kit");
            var passphraseVariable = Require("--passphrase-env");

            var passphraseValue = Environment.GetEnvironmentVariable(passphraseVariable);
            if (string.IsNullOrEmpty(passphraseValue))
            {
                throw new RecoveryFailureException(Strings.FormatRecoveryHost_EnvironmentVariableUnset(passphraseVariable));
            }

            // The kit: binary or transcribed text, detected by content.
            var kitBytes = File.ReadAllBytes(kitPath);
            var framed = kitBytes.Length >= 8 && kitBytes.AsSpan(0, 8).SequenceEqual(RecoveryKitCodec.Magic)
                ? kitBytes
                : RecoveryKitText.ParseToFramed(System.Text.Encoding.UTF8.GetString(kitBytes));
            var kit = RecoveryKitCodec.Parse(framed);
            Log.KitRead(log, kit.RepositoryFormatVersion, kit.Destinations.Count);

            using var passphrase = Passphrase.Create(passphraseValue);
            // OpenAsync serves both kit formats: a repository kit is
            // delegated to the synchronous path unchanged, and an
            // installation kit reads the archive's own identity from its
            // descriptor first (recovery-kit §6).
            using var session = await RecoverySession.OpenAsync(
                kit, passphrase,
                // The tool's one composition point (ADR-0012): a standalone
                // binary over a local path is the whole product here, so the
                // provider choice is made once, inline, where it is visible.
                new LocalFileSystemObjectStore(repoPath), cancellationToken)
                .ConfigureAwait(false);
            Log.KeysDerived(log);

            switch (command)
            {
                case "open":
                {
                    output.WriteLine($"repository     {Convert.ToHexString(session.RepositoryId.ToArray()).ToLowerInvariant()}");
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"kit version    {kit.KitFormatVersion} (issued {DateTimeOffset.FromUnixTimeMilliseconds((long)kit.IssuedAt):yyyy-MM-dd})"));
                    output.WriteLine(kit.IsInstallationKit
                        ? "derivation     reproduced — this kit and passphrase open this archive, and every "
                            + "other archive this installation wrote"
                        : "key object     unwrapped — the kit and passphrase open this repository");
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
            error.WriteLine("error: the passphrase does not open this kit's key object — wrong passphrase or wrong kit.");
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
