using System.Buffers.Binary;
using System.Globalization;
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

/// <summary>
/// What first-run setup left behind: the write credential the whole
/// installation derives from, with the salt and KDF parameters that produced
/// it (ADR-0044 §2). Owns the credential and disposes it.
/// </summary>
/// <remarks>
/// The salt and parameters travel with the credential because every archive
/// this installation creates stamps them into its own descriptor — they are
/// what lets a restore, years later and possibly on another machine,
/// reproduce this exact bundle from the passphrase alone.
/// </remarks>
public sealed class InstallationProvisioning : IDisposable
{
    /// <summary>The serialised length: magic, credential, salt, and three KDF fields.</summary>
    public const int SerializedLength =
        8 + RepositoryWriteCredential.SerializedLength + KekDerivation.SaltLength + 4 + 4 + 1;

    private static readonly byte[] Magic = "FBPINST1"u8.ToArray();

    private readonly byte[] _kdfSalt;

    /// <summary>Takes ownership of <paramref name="credential"/>.</summary>
    /// <param name="credential">The installation's write credential.</param>
    /// <param name="kdfSalt">The 16-byte salt it was derived with.</param>
    /// <param name="kdfParameters">The Argon2id parameters it was derived with.</param>
    /// <exception cref="ArgumentException"><paramref name="kdfSalt"/> is not 16 bytes.</exception>
    public InstallationProvisioning(
        RepositoryWriteCredential credential,
        ReadOnlySpan<byte> kdfSalt,
        Domain.Configuration.Argon2Parameters kdfParameters)
    {
        ThrowHelper.ThrowIfNull(credential);
        ThrowHelper.ThrowIfNull(kdfParameters);

        if (kdfSalt.Length != KekDerivation.SaltLength)
        {
            throw new ArgumentException(
                $"An installation KDF salt is exactly {KekDerivation.SaltLength} bytes; got {kdfSalt.Length}.",
                nameof(kdfSalt));
        }

        Credential = credential;
        _kdfSalt = kdfSalt.ToArray();
        KdfParameters = kdfParameters;
    }

    /// <summary>The installation's write credential.</summary>
    public RepositoryWriteCredential Credential { get; }

    /// <summary>The salt every archive of this installation records.</summary>
    public ReadOnlySpan<byte> KdfSalt => _kdfSalt;

    /// <summary>The Argon2id parameters every archive of this installation records.</summary>
    public Domain.Configuration.Argon2Parameters KdfParameters { get; }

    /// <summary>The serialised form the store persists.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[SerializedLength];
        var credential = Credential.ToBytes();
        try
        {
            Magic.CopyTo(bytes, 0);
            credential.CopyTo(bytes, 8);
            var offset = 8 + RepositoryWriteCredential.SerializedLength;
            _kdfSalt.CopyTo(bytes, offset);
            offset += KekDerivation.SaltLength;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), KdfParameters.MemoryKiB);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4), KdfParameters.Iterations);
            bytes[offset + 8] = KdfParameters.Parallelism;
            return bytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }

    /// <summary>Parses the serialised form.</summary>
    /// <param name="bytes">The stored bytes.</param>
    /// <exception cref="ArgumentException">The bytes are not an installation provisioning.</exception>
    public static InstallationProvisioning FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != SerializedLength || !bytes[..8].SequenceEqual(Magic))
        {
            throw new ArgumentException(
                $"A serialised installation provisioning is exactly {SerializedLength} bytes.", nameof(bytes));
        }

        var offset = 8 + RepositoryWriteCredential.SerializedLength;
        var credential = RepositoryWriteCredential.FromBytes(bytes[8..offset]);
        var salt = bytes.Slice(offset, KekDerivation.SaltLength);
        var kdf = offset + KekDerivation.SaltLength;

        return new InstallationProvisioning(
            credential,
            salt,
            new Domain.Configuration.Argon2Parameters
            {
                MemoryKiB = BinaryPrimitives.ReadUInt32LittleEndian(bytes[kdf..]),
                Iterations = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(kdf + 4)..]),
                Parallelism = bytes[kdf + 8],
            });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Credential.Dispose();
        CryptographicOperations.ZeroMemory(_kdfSalt);
    }
}

/// <summary>
/// The one credential first-run setup provisions, from which every set's
/// staging archive is created (ADR-0044 §2).
/// </summary>
/// <remarks>
/// <para>
/// A sibling of <see cref="WriteCredentialStore"/> rather than an entry in
/// it, and the distinction is not filing: a per-set credential is bound to
/// one archive's descriptor and its salt came from that descriptor, while
/// this one minted its own salt and can stamp archives that do not exist
/// yet. They answer different questions, and a lookup that could return
/// either without saying which would make the ladder in
/// <c>ServiceRuntime.ArchiveForAsync</c> impossible to read.
/// </para>
/// <para>
/// It shares the per-set directory because it is the same kind of secret
/// with the same handling, and the file name cannot collide: a set id is
/// validated as thirty-two hex characters, and <c>installation</c> is not
/// hex.
/// </para>
/// </remarks>
public sealed class InstallationCredentialStore(string stateDirectory)
{
    private string Path_ =>
        Path.Combine(stateDirectory, "write-credentials", "installation.bin");

    /// <summary>Whether this installation has been set up.</summary>
    public bool Holds => File.Exists(Path_);

    /// <summary>Loads the installation provisioning, or null when there is none. The caller disposes.</summary>
    /// <exception cref="RepositoryOpenException">The file exists and does not parse.</exception>
    public InstallationProvisioning? TryLoad()
    {
        var path = Path_;
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        try
        {
            return InstallationProvisioning.FromBytes(bytes);
        }
        catch (ArgumentException)
        {
            // Damage to NAME, exactly as a per-set credential's is. Reporting
            // "not set up" would send the operator back through a setup
            // ceremony that would mint a NEW salt — and every archive already
            // written under the old one would become unopenable by the
            // passphrase that made it.
            throw new RepositoryOpenException(
                $"The installation write credential at '{path}' is damaged — restore the state directory, "
                + "or adopt each set again with the passphrase (ADR-0044, ADR-0042 §10).");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Persists the installation provisioning, unless one is already held.
    /// </summary>
    /// <param name="provisioning">What setup derived.</param>
    /// <returns><see langword="false"/> when this installation was already set up.</returns>
    /// <remarks>
    /// <b>Never overwrites</b>, and that is the point rather than caution. A
    /// second provisioning carries a second salt, so replacing this file
    /// would leave every archive already written unopenable by the passphrase
    /// that made it. The handler checks first and refuses politely; this is
    /// what makes the refusal true rather than merely likely, because two
    /// setups submitted at once would both pass a check-then-act. Once-ness
    /// belongs to the filesystem, where the rename is atomic.
    /// </remarks>
    public bool TrySave(InstallationProvisioning provisioning)
    {
        ThrowHelper.ThrowIfNull(provisioning);

        var path = Path_;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        var bytes = provisioning.ToBytes();
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            try
            {
                File.Move(temporary, path, overwrite: false);
                return true;
            }
            catch (IOException)
            {
                // Somebody got there first. The loser cleans up after itself
                // rather than leaving key material in a .tmp beside the real
                // one.
                File.Delete(temporary);
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

/// <summary>
/// Whether the operator has said they saved this installation's recovery
/// kit — the last thing first-run setup waits for (FR-KIT-004, ADR-0044).
/// </summary>
/// <remarks>
/// <para>
/// It records the kit's <b>checksum</b>, never the kit. A kit stored on the
/// machine being backed up is not a recovery kit; a service that could hand
/// one out would have made itself a second factor, and this file exists
/// precisely so it does not have to.
/// </para>
/// <para>
/// It is durable because the alternative is a modal nobody can be made to
/// read. A closed tab between provisioning and confirming leaves the
/// installation in <c>kit_required</c>, and the ceremony resumes there.
/// </para>
/// </remarks>
public sealed class RecoveryKitConfirmation(string stateDirectory)
{
    private string Path_ => Path.Combine(stateDirectory, "recovery-kit.confirmed");

    /// <summary>Whether a kit has been confirmed saved.</summary>
    public bool Holds => File.Exists(Path_);

    /// <summary>The confirmed kit's checksum, or null when none has been confirmed.</summary>
    public string? Checksum
    {
        get
        {
            var path = Path_;
            if (!File.Exists(path))
            {
                return null;
            }

            // First line is the checksum; the rest is for whoever opens the
            // file wondering what it is.
            var text = File.ReadAllText(path).AsSpan();
            var newline = text.IndexOfAny('\r', '\n');
            return (newline < 0 ? text : text[..newline]).Trim().ToString();
        }
    }

    /// <summary>
    /// The kit's status, in the two values an installation kit can have
    /// (FR-KIT-005): <c>"never_saved"</c> or <c>"saved"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two, not the three the requirement's wording implies, and the reason is
    /// in what an installation kit contains. FR-KIT-005 names the third state
    /// <em>stale</em> and says changing destinations causes it — but an
    /// installation kit carries no destinations at all (ADR-0013 as amended),
    /// so its stated trigger cannot fire.
    /// </para>
    /// <para>
    /// Nor can anything else cause it. The kit holds the KDF salt, the Argon2id
    /// parameters and the sealing public key, and all three are fixed for the
    /// life of the installation — that is exactly what makes one passphrase
    /// open every archive it ever writes. What is left is the issuing device
    /// and the issue time, both informational. Regenerating a kit therefore
    /// yields different bytes only because <c>issued_at</c> moved, which means
    /// comparing checksums across regenerations proves nothing and a
    /// freshness test would be theatre.
    /// </para>
    /// </remarks>
    public string Status => Holds ? "saved" : "never_saved";

    /// <summary>
    /// When the operator confirmed, Unix milliseconds, or null when none has
    /// been confirmed or the record predates the timestamp being written.
    /// </summary>
    /// <remarks>
    /// Read from the file's own annotation rather than from its modification
    /// time: a state directory restored from a backup carries somebody else's
    /// mtimes, and "when was this confirmed" is a question about the ceremony,
    /// not about the filesystem.
    /// </remarks>
    public ulong? ConfirmedAtUnixMilliseconds
    {
        get
        {
            var path = Path_;
            if (!File.Exists(path))
            {
                return null;
            }

            foreach (var line in File.ReadLines(path))
            {
                const string marker = "# Confirmed at ";
                if (!line.StartsWith(marker, StringComparison.Ordinal))
                {
                    continue;
                }

                var rest = line.AsSpan(marker.Length);
                var end = rest.IndexOf(' ');
                var digits = end < 0 ? rest : rest[..end];
                return ulong.TryParse(digits, CultureInfo.InvariantCulture, out var value) ? value : null;
            }

            return null;
        }
    }

    /// <summary>Records a confirmation, replacing any earlier one.</summary>
    /// <param name="kitChecksum">The kit's SHA-256, lowercase hex.</param>
    /// <param name="confirmedAtUnixMilliseconds">When the operator confirmed.</param>
    public void Save(string kitChecksum, ulong confirmedAtUnixMilliseconds)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(kitChecksum);

        Directory.CreateDirectory(stateDirectory);
        var path = Path_;
        var temporary = path + ".tmp";

        File.WriteAllText(
            temporary,
            kitChecksum + Environment.NewLine
                + "# The SHA-256 of the recovery kit this installation's operator confirmed saving."
                + Environment.NewLine
                + "# Confirmed at " + confirmedAtUnixMilliseconds.ToString(CultureInfo.InvariantCulture)
                + " (Unix ms). The kit itself is deliberately NOT here." + Environment.NewLine);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
