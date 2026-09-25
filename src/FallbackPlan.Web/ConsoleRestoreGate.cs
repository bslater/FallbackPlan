using Bodu;
using FallbackPlan.Api;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Web;

/// <summary>
/// The restore wizard's passphrase gate (ADR-0041): verified HERE, in the
/// console process on the operator's machine, against the staging archive's
/// own key files — the passphrase never crosses the command contract
/// (NFR-SEC-009 stands untouched) and never reaches the service, which
/// already holds its own copy. The same posture as key export (ADR-0028 §9):
/// passphrase work runs where the person typed it.
/// </summary>
/// <remarks>
/// This is the one class in the console permitted to reach below the client
/// contract — the dependency rule is scoped to it by name
/// (<c>DependencyRuleTests</c>), because a console that opened repositories
/// anywhere else would stop being a client. It reads the descriptor and the
/// wrapped key objects, derives, and answers; it opens no blob and derives
/// no state.
/// </remarks>
public static class ConsoleRestoreGate
{
    /// <summary>How a verification attempt resolved.</summary>
    public enum GateOutcome
    {
        /// <summary>The passphrase unwrapped a key object — it is the repository's.</summary>
        Verified = 0,

        /// <summary>The derivation ran and no key object opened.</summary>
        Wrong = 1,

        /// <summary>Nothing local to verify against — a remote console, or no archive yet.</summary>
        Unavailable = 2,
    }

    /// <summary>An attempt's answer.</summary>
    /// <param name="Outcome">How it resolved.</param>
    /// <param name="Detail">What an unavailable outcome met, for the page to show.</param>
    /// <param name="GrantEnvelope">
    /// A restore grant for a write-only archive (ADR-0042 §5): the derived
    /// scalar sealed to the service's recipient key, hex-rendered — minted
    /// only when the passphrase verified against a v2 archive and a recipient
    /// key was given. Opaque to the page and to the relay; only the service
    /// can open it. Null on v1 archives.
    /// </param>
    public sealed record GateAnswer(GateOutcome Outcome, string? Detail = null, string? GrantEnvelope = null);

    /// <summary>
    /// Verifies a typed passphrase against the first repository of the
    /// installation that will answer: a staging archive under
    /// <paramref name="archivesRoot"/>, or a direct-ship set's metadata
    /// store under <paramref name="stateDirectory"/><c>/sets</c> (ADR-0046 —
    /// on an install whose every set ships direct, the metadata stores are
    /// the only local key files there are). Every repository a service
    /// manages opens under the one service passphrase, so any is as good a
    /// witness as another; a damaged one is skipped for the next. A
    /// write-only repository is verified by derive-and-compare against its
    /// descriptor's sealing public key (ADR-0042 §1 — no key object exists
    /// to unwrap), and when <paramref name="grantRecipientHex"/> names the
    /// service's recipient key the verified scalar is sealed into a restore
    /// grant on the way out.
    /// </summary>
    /// <param name="archivesRoot">The service's archives root, from <c>describe_service</c>.</param>
    /// <param name="stateDirectory">The service's state directory, from <c>describe_service</c>; its <c>sets</c> child holds the metadata stores.</param>
    /// <param name="passphraseText">The typed passphrase; used for one derivation and released.</param>
    /// <param name="grantRecipientHex">The service's grant-recipient public key, from <c>describe_service</c>; null mints no grant.</param>
    /// <param name="cancellationToken">Cancels the derivation.</param>
    /// <returns>The answer.</returns>
    public static async Task<GateAnswer> VerifyAsync(
        string? archivesRoot,
        string? stateDirectory,
        string passphraseText,
        string? grantRecipientHex,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(passphraseText);

        if (RepositoryRoots(archivesRoot, stateDirectory).Count == 0)
        {
            return new GateAnswer(
                GateOutcome.Unavailable,
                "The service's archives are not readable from this console.");
        }

        // The recipient key is parsed ONCE, before any archive is touched: a
        // service publishing an unusable key is its own finding, never to be
        // mistaken for a damaged archive — and a wizard verified without a
        // mintable grant would only defer the failure to the restore.
        byte[]? recipient = null;
        if (grantRecipientHex is { Length: > 0 })
        {
            if (!TryParseRecipient(grantRecipientHex, out recipient))
            {
                return new GateAnswer(
                    GateOutcome.Unavailable,
                    "The service's grant-recipient key is not a usable 32-byte hex key — restart the service "
                    + "and try again (ADR-0042).");
            }
        }

        using var passphrase = Passphrase.Create(passphraseText);
        var sawAnArchive = false;
        foreach (var archive in LocalRepositories(archivesRoot, stateDirectory))
        {
            sawAnArchive = true;
            try
            {
                var store = OpenStore(archive);
                var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(store, cancellationToken)
                    .ConfigureAwait(false);

                // The verifier is equality, not decryption: derive and
                // compare the sealing public key (ADR-0042 §1). The same
                // derivation's scalar is the restore grant, so a verified
                // answer carries it sealed rather than making the wizard pay
                // the Argon2 cost twice.
                if (!RepositoryLifecycle.TryDeriveReadAuthority(descriptor, passphrase, out var authority))
                {
                    return new GateAnswer(GateOutcome.Wrong);
                }

                using (authority)
                {
                    return new GateAnswer(
                        GateOutcome.Verified,
                        GrantEnvelope: recipient is not null
                            ? Convert.ToHexStringLower(
                                WriteOnlyProvisioning.SealGrant(recipient, authority!.SealingPrivateKey))
                            : null);
                }
            }
            catch (Exception damaged) when (damaged is RepositoryOpenException or IOException or FormatException)
            {
                // A damaged archive proves nothing either way; try the next.
            }
        }

        return new GateAnswer(
            GateOutcome.Unavailable,
            sawAnArchive
                ? "No local archive could answer the check."
                : "No local repository exists yet to verify against — run a backup first.");
    }

    /// <summary>A provisioning ceremony's client half, resolved.</summary>
    /// <param name="Outcome"><see cref="GateOutcome.Verified"/> when the envelope was minted.</param>
    /// <param name="Detail">Why not, when it was not.</param>
    /// <param name="Envelope">The sealed provisioning envelope, hex — the write bundle plus KDF salt and parameters.</param>
    public sealed record ProvisionAnswer(GateOutcome Outcome, string? Detail = null, string? Envelope = null);

    /// <summary>
    /// The client half of the write-only provisioning ceremony (ADR-0042 §4,
    /// §10): Argon2id runs here, where the person typed, and what leaves this
    /// process is the write bundle sealed to the service's recipient key. An
    /// existing v2 repository for the set — a staging archive under
    /// <paramref name="archivesRoot"/>, or a direct-ship metadata store under
    /// <paramref name="stateDirectory"/> — makes this an adoption: the
    /// derivation uses the descriptor's recorded salt and parameters and is
    /// proved against its public key before anything is sealed. No repository
    /// (or no local read access) makes it a creation with a fresh salt and the
    /// default parameters.
    /// </summary>
    /// <remarks>
    /// Which branch this takes matters more than it looks. A creation mints a
    /// <b>new salt</b>, so adopting-as-creating produces keys that cannot open
    /// what the set has already written — a failure with nothing to see, as
    /// against a refusal. Looking under both roots is what keeps the branch
    /// honest for a set that ships direct and therefore stages nothing.
    /// </remarks>
    /// <param name="archivesRoot">The service's archives root, from <c>describe_service</c>.</param>
    /// <param name="stateDirectory">The service's state directory, from <c>describe_service</c>; its <c>sets</c> child holds the metadata stores.</param>
    /// <param name="setId">The set's 32-hex identity, naming its repository directory under either root.</param>
    /// <param name="passphraseText">The typed passphrase; used for one derivation and released.</param>
    /// <param name="grantRecipientHex">The service's grant-recipient public key, from <c>describe_service</c>.</param>
    /// <param name="cancellationToken">Cancels the derivation.</param>
    /// <returns>The answer.</returns>
    public static async Task<ProvisionAnswer> BuildProvisionEnvelopeAsync(
        string? archivesRoot,
        string? stateDirectory,
        string setId,
        string passphraseText,
        string grantRecipientHex,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(setId);
        ThrowHelper.ThrowIfNull(passphraseText);
        ThrowHelper.ThrowIfNullOrWhiteSpace(grantRecipientHex);

        if (!TryParseRecipient(grantRecipientHex, out var recipient))
        {
            return new ProvisionAnswer(
                GateOutcome.Unavailable,
                "The service's grant-recipient key is not a usable 32-byte hex key — restart the service "
                + "and try again (ADR-0042).");
        }

        using var passphrase = Passphrase.Create(passphraseText);

        if (RepositoryForSet(archivesRoot, stateDirectory, setId) is { } archivePath)
        {
            try
            {
                var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(
                    OpenStore(archivePath), cancellationToken).ConfigureAwait(false);
                if (!RepositoryLifecycle.TryDeriveReadAuthority(descriptor, passphrase, out var derived))
                {
                    return new ProvisionAnswer(
                        GateOutcome.Wrong,
                        "That passphrase does not reproduce this archive's keys.");
                }

                using (derived)
                {
                    return new ProvisionAnswer(
                        GateOutcome.Verified,
                        Envelope: Convert.ToHexStringLower(
                            WriteOnlyProvisioning.SealProvision(
                                recipient!, derived!, descriptor.KdfSalt.Span, descriptor.KdfParameters)));
                }
            }
            catch (RepositoryOpenException damaged)
            {
                // A descriptor that does not read is the archive's problem,
                // named — a ceremony must never crash the endpoint over it.
                return new ProvisionAnswer(
                    GateOutcome.Unavailable,
                    $"This set's repository descriptor does not read: {damaged.Message}");
            }
        }

        // Creation: nothing exists yet (or the archives are not locally
        // readable and the service will refuse an accidental adoption
        // mismatch by name). Fresh salt, current default parameters.
        return BuildCreationEnvelope(passphrase, recipient!);
    }

    /// <summary>
    /// Where this installation's local key files can be: the archives root
    /// holding staging archives, and <paramref name="stateDirectory"/>'s
    /// <c>sets</c> child holding direct-ship metadata stores (ADR-0046).
    /// </summary>
    /// <remarks>
    /// One definition, because more than one ceremony needs it. Each used to
    /// spell the layout itself, and when direct-ship arrived only the
    /// restore gate was taught the second root — quietly turning write-only
    /// adoption into creation against a fresh salt. A shape can now only be
    /// taught here.
    /// </remarks>
    private static List<string> RepositoryRoots(string? archivesRoot, string? stateDirectory)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(archivesRoot) && Directory.Exists(archivesRoot))
        {
            roots.Add(archivesRoot);
        }

        if (!string.IsNullOrWhiteSpace(stateDirectory)
            && Path.Combine(stateDirectory, "sets") is { } metadataRoot
            && Directory.Exists(metadataRoot))
        {
            roots.Add(metadataRoot);
        }

        return roots;
    }

    /// <summary>
    /// Every local directory carrying a repository descriptor, whichever
    /// shape wrote it. Order follows <see cref="RepositoryRoots"/>.
    /// </summary>
    private static IEnumerable<string> LocalRepositories(string? archivesRoot, string? stateDirectory) =>
        RepositoryRoots(archivesRoot, stateDirectory)
            .SelectMany(Directory.GetDirectories)
            .Where(candidate =>
                File.Exists(Path.Combine(candidate, RepositoryLifecycle.DescriptorKey.Value)));

    /// <summary>
    /// One named set's repository, under whichever root holds it, or null
    /// when the set has never been captured — or is not locally readable.
    /// </summary>
    private static string? RepositoryForSet(string? archivesRoot, string? stateDirectory, string setId) =>
        RepositoryRoots(archivesRoot, stateDirectory)
            .Select(root => Path.Combine(root, setId))
            .FirstOrDefault(candidate =>
                File.Exists(Path.Combine(candidate, RepositoryLifecycle.DescriptorKey.Value)));

    /// <summary>
    /// A recipient key is usable when it is hex and exactly 32 bytes —
    /// decided once, up front, so a service publishing garbage is its own
    /// named finding rather than a mystery blamed on an archive.
    /// </summary>
    private static bool TryParseRecipient(string grantRecipientHex, out byte[]? recipient)
    {
        try
        {
            recipient = Convert.FromHexString(grantRecipientHex);
        }
        catch (FormatException)
        {
            recipient = null;
            return false;
        }

        if (recipient.Length != 32)
        {
            recipient = null;
            return false;
        }

        return true;
    }

    /// <summary>What the setup ceremony derives.</summary>
    /// <param name="Outcome"><see cref="GateOutcome.Verified"/> when the envelope was produced.</param>
    /// <param name="Detail">Why not, when it was not.</param>
    /// <param name="Envelope">The sealed provisioning envelope, hex.</param>
    public sealed record SetupAnswer(GateOutcome Outcome, string? Detail = null, string? Envelope = null);

    /// <summary>
    /// The client half of first-run setup (ADR-0044 §5): mints this
    /// installation's salt, derives from the passphrase here — where the
    /// person typed it — and produces the sealed provisioning envelope.
    /// </summary>
    /// <remarks>
    /// There is no archives root, no set id and no descriptor here, which is
    /// the whole difference from <see cref="BuildProvisionEnvelopeAsync"/>:
    /// nothing exists yet to adopt or to prove against, so this is always a
    /// creation with a fresh salt. Every archive the installation goes on to
    /// create records that salt, which is what lets one passphrase open all
    /// of them — and is why nothing else has to be produced here
    /// (ADR-0060): the archive carries what a recovery needs beyond the
    /// passphrase.
    /// </remarks>
    /// <param name="passphraseText">The typed passphrase; used for one derivation and released.</param>
    /// <param name="grantRecipientHex">The service's grant-recipient public key, from <c>describe_service</c>.</param>
    /// <returns>The envelope, or why it could not be made.</returns>
    public static SetupAnswer BuildInstallationSetup(string passphraseText, string grantRecipientHex)
    {
        ThrowHelper.ThrowIfNull(passphraseText);
        ThrowHelper.ThrowIfNullOrWhiteSpace(grantRecipientHex);

        if (!TryParseRecipient(grantRecipientHex, out var recipient))
        {
            return new SetupAnswer(
                GateOutcome.Unavailable,
                "The service's grant-recipient key is not a usable 32-byte hex key — restart the service "
                + "and try again (ADR-0044).");
        }

        var parameters = Domain.Configuration.RepositoryCreationSettings.Default.KdfParameters;
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);

        using var passphrase = Passphrase.Create(passphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, parameters, salt, Domain.Configuration.KdfValidationMode.CreateRepository);

        return new SetupAnswer(
            GateOutcome.Verified,
            Envelope: Convert.ToHexStringLower(
                WriteOnlyProvisioning.SealProvision(recipient!, authority, salt, parameters)));
    }

    /// <summary>
    /// The client half of archive adoption (ADR-0061 §5): derive against the
    /// <em>discovered</em> archive's salt and parameters — not the
    /// installation's, whose salt a rebuilt machine has just minted afresh —
    /// prove the derivation against the discovered sealing public key, and
    /// only then seal the write bundle to the service's recipient key. Pure:
    /// nothing here touches the filesystem, because the facts came from the
    /// service's discovery answer.
    /// </summary>
    /// <param name="archive">The discovered archive, as <c>discover_archives</c> listed it.</param>
    /// <param name="passphraseText">The typed passphrase; used for one derivation and released.</param>
    /// <param name="grantRecipientHex">The service's grant-recipient public key, from <c>describe_service</c>.</param>
    /// <returns>The envelope, or why it could not be made.</returns>
    public static ProvisionAnswer BuildAdoptEnvelope(
        DiscoveredArchiveDescriptor archive, string passphraseText, string grantRecipientHex)
    {
        ThrowHelper.ThrowIfNull(archive);
        ThrowHelper.ThrowIfNull(passphraseText);
        ThrowHelper.ThrowIfNullOrWhiteSpace(grantRecipientHex);

        if (!TryParseRecipient(grantRecipientHex, out var recipient))
        {
            return new ProvisionAnswer(
                GateOutcome.Unavailable,
                "The service's grant-recipient key is not a usable 32-byte hex key — restart the service "
                + "and try again (ADR-0042).");
        }

        byte[] salt, sealingPublicKey;
        try
        {
            salt = Convert.FromHexString(archive.KdfSalt);
            sealingPublicKey = Convert.FromHexString(archive.SealingPublicKey);
        }
        catch (FormatException)
        {
            return new ProvisionAnswer(
                GateOutcome.Unavailable,
                $"Archive '{archive.RepositoryId}' was listed with derivation facts that do not parse — run discovery again.");
        }

        if (salt.Length != KekDerivation.SaltLength)
        {
            return new ProvisionAnswer(
                GateOutcome.Unavailable,
                $"Archive '{archive.RepositoryId}' was listed with a salt of {salt.Length} bytes; {KekDerivation.SaltLength} are expected.");
        }

        var parameters = new Domain.Configuration.Argon2Parameters
        {
            MemoryKiB = archive.KdfMemoryKib, Iterations = archive.KdfIterations, Parallelism = archive.KdfParallelism,
        };

        using var passphrase = Passphrase.Create(passphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, parameters, salt, Domain.Configuration.KdfValidationMode.OpenRepository);
        if (!authority.Credential.SealingPublicKey.SequenceEqual(sealingPublicKey))
        {
            return new ProvisionAnswer(
                GateOutcome.Wrong,
                "The passphrase does not reproduce this archive's sealing key — it is not the passphrase "
                + "this backup was written with. Nothing was sent.");
        }

        return new ProvisionAnswer(
            GateOutcome.Verified,
            Envelope: Convert.ToHexStringLower(
                WriteOnlyProvisioning.SealProvision(recipient!, authority, salt, parameters)));
    }

    private static ProvisionAnswer BuildCreationEnvelope(Passphrase passphrase, byte[] recipient)
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        var parameters = Domain.Configuration.RepositoryCreationSettings.Default.KdfParameters;
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, parameters, salt, Domain.Configuration.KdfValidationMode.CreateRepository);
        return new ProvisionAnswer(
            GateOutcome.Verified,
            Envelope: Convert.ToHexStringLower(
                WriteOnlyProvisioning.SealProvision(recipient, authority, salt, parameters)));
    }
    /// <summary>
    /// The gate's one decision about which provider serves an archive path
    /// (ADR-0012) — a private method rather than a shared type, because this
    /// class is the single console type the architecture rules permit below
    /// the client contract (ADR-0041), and composition must stay inside it.
    /// </summary>
    private static LocalFileSystemObjectStore OpenStore(string archivePath) =>
        new(archivePath);

}
