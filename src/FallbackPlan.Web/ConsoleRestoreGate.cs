using Bodu;
using FallbackPlan.Api;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Web;

/// <summary>
/// The console's passphrase ceremonies: where a typed passphrase is derived
/// — in the console process, on the machine the person typed it on — and
/// what leaves for the service is only ever an envelope sealed to its
/// recipient key (NFR-SEC-009). The restore gate (FR-WOR-007, ADR-0089)
/// derives under the facts the service publishes for each set and proves the
/// derivation against that set's sealing key before it seals a grant, so a
/// console on any machine checks a passphrase alike and nothing passes a
/// passphrase that did not check out. The same posture as key export
/// (ADR-0028 §9): passphrase work runs where the person typed it. An
/// S3-compatible destination's secret access key is sealed here too, for the
/// same reason and with no derivation (ADR-0091).
/// </summary>
/// <remarks>
/// This is the one class in the console permitted to reach below the client
/// contract — the dependency rule is scoped to it by name
/// (<c>DependencyRuleTests</c>), because a console that opened repositories
/// anywhere else would stop being a client. The provisioning ceremony reads a
/// local descriptor when one is there; the grant ceremonies read nothing
/// local at all.
/// </remarks>
public static class ConsoleRestoreGate
{
    /// <summary>How a ceremony resolved.</summary>
    public enum GateOutcome
    {
        /// <summary>The passphrase reproduced the key it was proved against, and what it opens was sealed.</summary>
        Verified = 0,

        /// <summary>The derivation ran and reproduced no key it was proved against.</summary>
        Wrong = 1,

        /// <summary>Nothing to derive under or seal to — a service not set up, or one publishing an unusable key.</summary>
        Unavailable = 2,
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
    /// Seals an object-store destination's credential to the service's
    /// recipient key, for the one destination it was typed for and under the
    /// purpose of its kind: an S3-compatible store's secret access key, bound
    /// to its key id too (ADR-0091), or an Azure Blob container's account key
    /// or shared access signature (ADR-0093). Nothing is derived and nothing
    /// is proved: the store is what will say whether the credential is right.
    /// </summary>
    /// <param name="destinationName">The destination the credential is for.</param>
    /// <param name="kind"><c>access-key</c>, <c>shared-key</c> or <c>sas</c>, as the contract spells them.</param>
    /// <param name="accessKeyId">The access key id, for an access key; null for the other two.</param>
    /// <param name="secret">The typed secret; sealed and released.</param>
    /// <param name="grantRecipientHex">The service's grant-recipient public key, from <c>describe_service</c>.</param>
    /// <returns>The envelope, or why it could not be made.</returns>
    public static SetupAnswer SealDestinationCredential(
        string destinationName, string kind, string? accessKeyId, string secret, string grantRecipientHex)
    {
        ThrowHelper.ThrowIfNull(secret);
        ThrowHelper.ThrowIfNullOrWhiteSpace(grantRecipientHex);

        if (!TryParseRecipient(grantRecipientHex, out var recipient))
        {
            return new SetupAnswer(
                GateOutcome.Unavailable,
                "The service's grant-recipient key is not a usable 32-byte hex key — restart the service "
                + "and try again (ADR-0091, ADR-0093).");
        }

        try
        {
            return new SetupAnswer(
                GateOutcome.Verified,
                Envelope: Convert.ToHexStringLower(kind switch
                {
                    "shared-key" => WriteOnlyProvisioning.SealAccountKey(recipient!, destinationName, secret),
                    "sas" => WriteOnlyProvisioning.SealSharedAccessSignature(recipient!, destinationName, secret),
                    _ => WriteOnlyProvisioning.SealAccessKeySecret(recipient!, destinationName, accessKeyId!, secret),
                }));
        }
        catch (ArgumentException malformed)
        {
            return new SetupAnswer(GateOutcome.Wrong, malformed.Message);
        }
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

    /// <summary>A grant ceremony's answer: one sealed grant per set the passphrase opens.</summary>
    /// <param name="Outcome"><see cref="GateOutcome.Verified"/> when at least one set's grant was minted.</param>
    /// <param name="Detail">Why not, when none was.</param>
    /// <param name="Grants">Each opened set's sealed grant, hex, keyed by set id.</param>
    public sealed record GrantsAnswer(
        GateOutcome Outcome, string? Detail = null, IReadOnlyDictionary<string, string>? Grants = null);

    /// <summary>
    /// The client half of applying retention on a set-up installation
    /// ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §6, Amendment 3):
    /// the service holds the key that publishes and not the key that
    /// authorises a deletion, so the reclaim sub-root is derived here and
    /// only the sealed grant goes to the service. One grant per set the
    /// passphrase opens, as <see cref="BuildRestoreGrants"/> mints them.
    /// </summary>
    /// <param name="description">The service's <c>describe_service</c> answer.</param>
    /// <param name="sets">The service's <c>list_backup_sets</c> answer.</param>
    /// <param name="passphraseText">The typed passphrase; used for the derivations and released.</param>
    /// <returns>The grants, or why there are none.</returns>
    public static GrantsAnswer BuildReclaimGrants(
        ServiceDescriptionResult description, IReadOnlyList<BackupSetDescriptor> sets, string passphraseText) =>
        BuildGrants(
            description, sets, passphraseText,
            (recipient, authority) => WriteOnlyProvisioning.SealReclaimGrant(recipient, authority.ReclaimKeySeed),
            "No backup set has an archive yet, so there is nothing to apply retention to.");

    /// <summary>
    /// The passphrase gate's client half (FR-WOR-007, ADR-0089; ADR-0042 §5):
    /// a restore grant per set the passphrase opens, each the set's sealing
    /// scalar sealed to the service's recipient key. Opening a restore source
    /// under one is what lets a person see that set's files.
    /// </summary>
    /// <param name="description">The service's <c>describe_service</c> answer.</param>
    /// <param name="sets">The service's <c>list_backup_sets</c> answer.</param>
    /// <param name="passphraseText">The typed passphrase; used for the derivations and released.</param>
    /// <returns>The grants, or why there are none.</returns>
    public static GrantsAnswer BuildRestoreGrants(
        ServiceDescriptionResult description, IReadOnlyList<BackupSetDescriptor> sets, string passphraseText) =>
        BuildGrants(
            description, sets, passphraseText,
            (recipient, authority) => WriteOnlyProvisioning.SealGrant(recipient, authority.SealingPrivateKey),
            "No backup set has anything to unlock yet.");

    /// <summary>
    /// One grant per set the passphrase opens, derived under the facts the
    /// service publishes for it: a set adopted from a destination keeps the
    /// salt it was born under (ADR-0061), and every other set has the
    /// installation's. Pure, like <see cref="BuildAdoptEnvelope"/>: the facts
    /// come from the service's own answers.
    /// </summary>
    /// <remarks>
    /// One derivation per distinct salt and parameters, so the ordinary
    /// installation runs Argon2id once. Each derivation is proved against the
    /// sealing public key published beside the salt before a set's grant is
    /// sealed; a set it does not reproduce is left out. A passphrase that
    /// opens no set is wrong, and nothing is minted.
    /// </remarks>
    private static GrantsAnswer BuildGrants(
        ServiceDescriptionResult description,
        IReadOnlyList<BackupSetDescriptor> sets,
        string passphraseText,
        Func<byte[], RepositoryReadAuthority, byte[]> seal,
        string nothingToJudge)
    {
        ThrowHelper.ThrowIfNull(description);
        ThrowHelper.ThrowIfNull(sets);
        ThrowHelper.ThrowIfNull(passphraseText);

        if (description.RestoreGrantRecipient is not { Length: > 0 } recipientHex
            || !TryParseRecipient(recipientHex, out var recipient))
        {
            return new GrantsAnswer(
                GateOutcome.Unavailable,
                "The service's grant-recipient key is not a usable 32-byte hex key — restart the service "
                + "and try again (ADR-0042).");
        }

        // The installation's facts stand for a set whose archive publishes
        // none of its own; a service provisioned set by set has no
        // installation credential, and every set with an archive says its own.
        (string Salt, Domain.Configuration.Argon2Parameters Parameters, string SealingPublicKey)? installation =
            description is
            {
                KdfSalt.Length: > 0,
                KdfMemoryKib: { } memoryKib,
                KdfIterations: { } iterations,
                KdfParallelism: { } parallelism,
                SealingPublicKey.Length: > 0,
            }
                ? (description.KdfSalt,
                    new Domain.Configuration.Argon2Parameters
                    {
                        MemoryKiB = memoryKib, Iterations = iterations, Parallelism = parallelism,
                    },
                    description.SealingPublicKey)
                : null;

        using var passphrase = Passphrase.Create(passphraseText);
        var derived = new Dictionary<string, (byte[] SealingPublicKey, string Grant)>(StringComparer.Ordinal);
        var grants = new Dictionary<string, string>(StringComparer.Ordinal);
        var judged = false;
        foreach (var set in sets)
        {
            var facts = set is
                {
                    KdfSalt.Length: > 0,
                    KdfMemoryKib: { } setMemory,
                    KdfIterations: { } setIterations,
                    KdfParallelism: { } setLanes,
                    SealingPublicKey.Length: > 0,
                }
                ? (set.KdfSalt,
                    new Domain.Configuration.Argon2Parameters
                    {
                        MemoryKiB = setMemory, Iterations = setIterations, Parallelism = setLanes,
                    },
                    set.SealingPublicKey)
                : installation;
            if (facts is not { } chosen)
            {
                continue;
            }

            var (saltHex, parameters, sealingHex) = chosen;
            judged = true;
            byte[] salt, sealingPublicKey;
            try
            {
                salt = Convert.FromHexString(saltHex);
                sealingPublicKey = Convert.FromHexString(sealingHex);
            }
            catch (FormatException)
            {
                return new GrantsAnswer(
                    GateOutcome.Unavailable,
                    $"Set '{set.Name}' was listed with derivation facts that do not parse.");
            }

            if (salt.Length != KekDerivation.SaltLength)
            {
                return new GrantsAnswer(
                    GateOutcome.Unavailable,
                    $"Set '{set.Name}' was listed with a salt of {salt.Length} bytes; {KekDerivation.SaltLength} are expected.");
            }

            var key = $"{saltHex.ToUpperInvariant()}/{parameters.MemoryKiB}/{parameters.Iterations}/{parameters.Parallelism}";
            if (!derived.TryGetValue(key, out var known))
            {
                RepositoryReadAuthority authority;
                try
                {
                    authority = WriteOnlyDerivation.Derive(
                        passphrase, parameters, salt, Domain.Configuration.KdfValidationMode.OpenRepository);
                }
                catch (ArgumentException refused)
                {
                    return new GrantsAnswer(
                        GateOutcome.Unavailable,
                        $"Set '{set.Name}' was listed with derivation parameters this console will not use: {refused.Message}");
                }

                using (authority)
                {
                    known = (
                        authority.Credential.SealingPublicKey.ToArray(),
                        Convert.ToHexStringLower(seal(recipient!, authority)));
                }

                derived[key] = known;
            }

            if (known.SealingPublicKey.AsSpan().SequenceEqual(sealingPublicKey))
            {
                grants[set.Id] = known.Grant;
            }
        }

        if (!judged)
        {
            return new GrantsAnswer(GateOutcome.Unavailable, nothingToJudge);
        }

        return grants.Count == 0
            ? new GrantsAnswer(
                GateOutcome.Wrong,
                "That passphrase does not open any backup set here — it does not reproduce a sealing key the "
                + "service publishes. Nothing was sent.")
            : new GrantsAnswer(GateOutcome.Verified, Grants: grants);
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
