using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace FallbackPlan.Keystore;

/// <summary>
/// Linux: an owner-only file in an owner-only directory, holding the
/// passphrase for the service account (ADR-0028 §9).
/// </summary>
/// <remarks>
/// <para>
/// This is the platform where "or an equivalent" had to be decided rather than
/// assumed, so the reasoning is here rather than in a commit message.
/// </para>
/// <para>
/// The <b>kernel keyring</b> is the obvious candidate and does not survive a
/// reboot without something to re-provision it — and ADR-0028 explicitly
/// rejected "unlock once per boot, held in memory", because silent cessation of
/// backup is the failure users discover when they need a restore.
/// <b>libsecret</b> wants a D-Bus session and a running agent, which a system
/// service does not have. What remains is a file the service account alone can
/// read, which is the same posture as every other daemon credential on the
/// machine.
/// </para>
/// <para>
/// It is weaker than DPAPI and Keychain in exactly one way — the material is
/// readable by anyone who can read the file rather than only through an OS
/// call — and identical in the way that matters: <b>an attacker who obtains the
/// service account obtains the backups</b> either way. That is T-19's accepted
/// residual, not a new one. A TPM-sealed variant is the natural upgrade and is
/// not pretended at here.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class LinuxOwnerOnlyFileStore(string stateDirectory) : IPassphraseStore
{
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnlyDirectory = OwnerOnlyFile | UnixFileMode.UserExecute;

    public string Description =>
        $"an owner-only file in '{Directory()}' — see ADR-0028 §9 for why this platform has no sealed keystore";

    public bool TryRead(string account, out string? passphrase)
    {
        passphrase = null;
        var path = PathFor(account);
        if (!File.Exists(path))
        {
            return false;
        }

        // A stored secret whose permissions have drifted is refused, not read.
        // Reading it anyway would silently keep working while the property the
        // whole design rests on had already been lost.
        var mode = File.GetUnixFileMode(path);
        if ((mode & ~OwnerOnlyFile) != 0)
        {
            throw new KeystoreException(
                $"'{path}' is readable beyond its owner ({mode}). It is refused rather than used: the only thing "
                + "protecting it is its permissions.");
        }

        passphrase = File.ReadAllText(path);
        return true;
    }

    public void Write(string account, string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        var directory = Directory();
        System.IO.Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, OwnerOnlyDirectory);

        var path = PathFor(account);
        var temporary = $"{path}.{Guid.NewGuid():n}.tmp";

        // Create with the right mode from the start. Writing first and
        // tightening after leaves a window in which the secret is world
        // readable, and a window is all an attacker needs.
        using (var stream = new FileStream(
            temporary,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = OwnerOnlyFile,
            }))
        {
            var bytes = Encoding.UTF8.GetBytes(passphrase);
            try
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        File.Move(temporary, path, overwrite: true);
    }

    public void Delete(string account)
    {
        var path = PathFor(account);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string Directory() => Path.Combine(stateDirectory, "unlock");

    private string PathFor(string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account)).AsSpan(0, 8))
            .ToLowerInvariant();
        return Path.Combine(Directory(), $"{name}.key");
    }
}
