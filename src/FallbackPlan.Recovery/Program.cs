using System.Globalization;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.RecoveryKit;
using FallbackPlan.Storage.Local;

// The standalone recovery tool (architecture 08 §5; FR-KIT-006): opens a
// repository with nothing but a store location, a recovery kit, and the
// passphrase. Argument parsing is by hand on purpose — the smallest
// possible dependency closure is the point of this executable.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        FallbackPlan standalone recovery tool

        usage:
          fallbackplan-recover open      --repo <path> --kit <file> --passphrase-env <VAR>
          fallbackplan-recover snapshots --repo <path> --kit <file> --passphrase-env <VAR>
          fallbackplan-recover restore   --repo <path> --kit <file> --passphrase-env <VAR>
                                         --snapshot <hex> --output <dir>

        The kit file may be the binary form (FBPKRKIT) or the transcribable
        text form. The kit is one factor; the passphrase is the other.
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
    ?? throw new RecoveryFailureException($"Missing required option {name}.");

try
{
    var command = args[0];

    var repoPath = Require("--repo");
    var kitPath = Require("--kit");
    var passphraseVariable = Require("--passphrase-env");

    var passphraseValue = Environment.GetEnvironmentVariable(passphraseVariable);
    if (string.IsNullOrEmpty(passphraseValue))
    {
        throw new RecoveryFailureException(
            $"Environment variable '{passphraseVariable}' is unset — the passphrase is passed by name, never on the command line.");
    }

    // The kit: binary or transcribed text, detected by content.
    var kitBytes = File.ReadAllBytes(kitPath);
    var framed = kitBytes.Length >= 8 && kitBytes.AsSpan(0, 8).SequenceEqual(RecoveryKitCodec.Magic)
        ? kitBytes
        : RecoveryKitText.ParseToFramed(System.Text.Encoding.UTF8.GetString(kitBytes));
    var kit = RecoveryKitCodec.Parse(framed);

    using var passphrase = Passphrase.Create(passphraseValue);
    using var session = RecoverySession.Open(kit, passphrase, new LocalFileSystemObjectStore(repoPath));

    switch (command)
    {
        case "open":
        {
            Console.WriteLine($"repository     {Convert.ToHexString(session.RepositoryId.ToArray()).ToLowerInvariant()}");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"kit version    {kit.KitFormatVersion} (issued {DateTimeOffset.FromUnixTimeMilliseconds((long)kit.IssuedAt):yyyy-MM-dd})"));
            Console.WriteLine("key object     unwrapped — the kit and passphrase open this repository");
            var (blobs, notes) = await session.LoadBlobsAsync(CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"blobs          {blobs} readable"));
            foreach (var note in notes)
            {
                Console.WriteLine($"note           {note}");
            }

            return 0;
        }

        case "snapshots":
        {
            await session.LoadBlobsAsync(CancellationToken.None).ConfigureAwait(false);
            var snapshots = await session.ListSnapshotsAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var snapshot in snapshots)
            {
                var when = DateTimeOffset.FromUnixTimeMilliseconds((long)snapshot.Manifest.CaptureCompletedAt)
                    .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                var signature = snapshot.SignatureVerified ? "verified" : "SIGNATURE-FAILED";
                Console.WriteLine(
                    $"{Convert.ToHexString(snapshot.Manifest.SnapshotId.Span).ToLowerInvariant()}  {when}  {signature}");
            }

            return snapshots.Count == 0 ? 2 : 0;
        }

        case "restore":
        {
            var snapshotHex = Require("--snapshot");
            var output = Require("--output");
            var wanted = Convert.FromHexString(snapshotHex);

            await session.LoadBlobsAsync(CancellationToken.None).ConfigureAwait(false);
            var snapshots = await session.ListSnapshotsAsync(CancellationToken.None).ConfigureAwait(false);
            var snapshot = snapshots.FirstOrDefault(row => row.Manifest.SnapshotId.Span.SequenceEqual(wanted))
                ?? throw new RecoveryFailureException($"No snapshot {snapshotHex} is discoverable in this store.");

            if (!snapshot.SignatureVerified)
            {
                Console.Error.WriteLine("warning: this snapshot's signature does NOT verify — a security finding (06 §6.1); restoring anyway because recovery is the last line of defence.");
            }

            var report = await session.RestoreTreeAsync(snapshot.Manifest.RootTree, output, CancellationToken.None)
                .ConfigureAwait(false);

            foreach (var note in report.Notes)
            {
                Console.Error.WriteLine(note);
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"restored {report.Restored} file(s), {report.Failed} failed, {report.Skipped} skipped, to {output}"));
            return report.Failed == 0 ? 0 : 2;
        }

        default:
            throw new RecoveryFailureException($"Unknown command '{command}'. Run with --help.");
    }
}
catch (RecoveryFailureException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
catch (KeyUnwrapFailedException)
{
    Console.Error.WriteLine("error: the passphrase does not open this kit's key object — wrong passphrase or wrong kit.");
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

/// <summary>An operator-facing failure — a message, never a stack trace.</summary>
internal sealed class RecoveryFailureException : Exception
{
    public RecoveryFailureException(string message)
        : base(message)
    {
    }

    public RecoveryFailureException()
    {
    }

    public RecoveryFailureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
