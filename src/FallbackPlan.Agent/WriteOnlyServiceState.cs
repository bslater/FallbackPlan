using System.Security.Cryptography;
using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Agent;

/// <summary>
/// The service's envelope recipient (ADR-0042 §4): a long-lived X25519
/// keypair, minted on first use and persisted owner-only in the state
/// directory — the <c>peer.key</c> pattern. Provisioning and restore-grant
/// envelopes seal to its public half, which <c>describe_service</c>
/// publishes; only this service's state directory can open them, which is
/// what makes the sealed envelope end-to-end even through a relaying
/// console host.
/// </summary>
public sealed class GrantRecipient : IDisposable
{
    private readonly byte[] _privateKey;

    private GrantRecipient(byte[] privateKey, byte[] publicKey)
    {
        _privateKey = privateKey;
        PublicKey = publicKey;
    }

    /// <summary>The public half, sealed to by clients.</summary>
    public IReadOnlyList<byte> PublicKey { get; }

    /// <summary>The public half as lowercase hex — the contract's rendering.</summary>
    public string PublicKeyHex => Convert.ToHexStringLower([.. PublicKey]);

    /// <summary>Loads this service's recipient keypair, minting one on first use.</summary>
    public static GrantRecipient Open(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, "grant-recipient.key");

        byte[] scalar;
        if (File.Exists(path))
        {
            // A recipient key that does not read back is named, not thrown
            // raw out of service start: the operator's remedy is to restore
            // the state directory, or delete the file so a fresh keypair is
            // minted (outstanding envelopes sealed to the old key then no
            // longer open, which re-provisioning repairs).
            try
            {
                scalar = Convert.FromHexString(File.ReadAllText(path).Trim());
            }
            catch (FormatException)
            {
                throw new ClientStateException(
                    $"The envelope recipient key at '{path}' is not readable hex — restore the state "
                    + "directory, or delete the file to mint a fresh keypair and re-provision (ADR-0042).");
            }

            if (scalar.Length != 32)
            {
                CryptographicOperations.ZeroMemory(scalar);
                throw new ClientStateException(
                    $"The envelope recipient key at '{path}' is {scalar.Length} bytes, not 32 — restore the "
                    + "state directory, or delete the file to mint a fresh keypair and re-provision (ADR-0042).");
            }
        }
        else
        {
            scalar = RandomNumberGenerator.GetBytes(32);
            AtomicFile.WriteAllText(path, Convert.ToHexStringLower(scalar));

            if (!OperatingSystem.IsWindows())
            {
                // Owner-only: a recipient key another local account can read
                // is a grant another local account can open.
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        try
        {
            return new GrantRecipient(scalar, ContentSealing.PublicKeyOf(scalar));
        }
        catch
        {
            CryptographicOperations.ZeroMemory(scalar);
            throw;
        }
    }

    /// <summary>Opens a provisioning envelope sealed to this recipient.</summary>
    /// <exception cref="SealedContentException">The envelope does not open.</exception>
    public (RepositoryWriteCredential Credential, byte[] KdfSalt, FallbackPlan.Domain.Configuration.Argon2Parameters KdfParameters)
        OpenProvision(ReadOnlySpan<byte> sealedBytes) =>
        WriteOnlyProvisioning.OpenProvision(_privateKey, sealedBytes);

    /// <summary>Opens a restore-grant envelope sealed to this recipient.</summary>
    /// <exception cref="SealedContentException">The envelope does not open.</exception>
    public byte[] OpenGrant(ReadOnlySpan<byte> sealedBytes) =>
        WriteOnlyProvisioning.OpenGrant(_privateKey, sealedBytes);

    /// <inheritdoc />
    public void Dispose() => CryptographicOperations.ZeroMemory(_privateKey);
}

/// <summary>
/// The per-set write credentials a provisioned service holds (ADR-0042 §5,
/// §10): serialised bundles under <c>&lt;state&gt;/write-credentials/</c>,
/// owner-only, deliberately absent from the repository and every replica —
/// which is exactly why moving an archive to a new machine is an adoption
/// that costs the passphrase once.
/// </summary>
public sealed class WriteCredentialStore(string stateDirectory)
{
    private string Root => Path.Combine(stateDirectory, "write-credentials");

    private string PathFor(string setId) => Path.Combine(Root, $"{setId}.bin");

    /// <summary>Whether a credential is held for <paramref name="setId"/>.</summary>
    public bool Holds(string setId) => File.Exists(PathFor(setId));

    /// <summary>Loads a set's credential, or null when none is held. The caller disposes.</summary>
    public RepositoryWriteCredential? TryLoad(string setId)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(setId);
        var path = PathFor(setId);
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        try
        {
            return RepositoryWriteCredential.FromBytes(bytes);
        }
        catch (ArgumentException)
        {
            // A credential file that no longer parses is damage to NAME, not
            // a silent "not provisioned" — pretending would flip the set onto
            // the passphrase path and mask the loss. The remedy is the same
            // as any state loss: adopt again with the passphrase (ADR-0042 §10).
            throw new RepositoryOpenException(
                $"The stored write credential for set '{setId}' at '{path}' is damaged — adopt the set "
                + "again with the passphrase (ADR-0042).");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Persists a set's credential, replacing any held one.</summary>
    public void Save(string setId, RepositoryWriteCredential credential)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(setId);
        ThrowHelper.ThrowIfNull(credential);

        Directory.CreateDirectory(Root);
        var path = PathFor(setId);
        var bytes = credential.ToBytes();
        try
        {
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Forgets a set's credential.</summary>
    public void Delete(string setId)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(setId);
        var path = PathFor(setId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
