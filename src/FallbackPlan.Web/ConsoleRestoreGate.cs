using Bodu;
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

        if (roots.Count == 0)
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
        foreach (var archive in roots.SelectMany(Directory.GetDirectories))
        {
            if (!File.Exists(Path.Combine(archive, RepositoryLifecycle.DescriptorKey.Value)))
            {
                continue;
            }

            sawAnArchive = true;
            try
            {
                var store = new LocalFileSystemObjectStore(archive);
                var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(store, cancellationToken)
                    .ConfigureAwait(false);

                if (RepositoryLifecycle.IsWriteOnly(descriptor))
                {
                    // The v2 verifier is equality, not decryption: derive and
                    // compare the sealing public key (ADR-0042 §1). The same
                    // derivation's scalar is the restore grant, so a verified
                    // answer carries it sealed rather than making the wizard
                    // pay the Argon2 cost twice.
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

                // The genuine v1 check: derive the key-encryption key with the
                // archive's own KDF parameters and unwrap a key object. There
                // is no cheaper honest answer, and the cost is the point —
                // this is the same wall a stolen archive presents.
                _ = await RepositoryLifecycle.ExportVerifiedKeyObjectAsync(
                    store, passphrase, cancellationToken).ConfigureAwait(false);
                return new GateAnswer(GateOutcome.Verified);
            }
            catch (KeyUnwrapFailedException)
            {
                return new GateAnswer(GateOutcome.Wrong);
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
    /// existing v2 archive at <paramref name="archivesRoot"/>/<paramref name="setId"/>
    /// makes this an adoption — the derivation uses the descriptor's recorded
    /// salt and parameters and is proved against its public key before
    /// anything is sealed; no archive (or no local read access) makes it a
    /// creation with a fresh salt and the default parameters.
    /// </summary>
    /// <param name="archivesRoot">The service's archives root, from <c>describe_service</c>.</param>
    /// <param name="setId">The set's 32-hex identity, naming its staging archive directory.</param>
    /// <param name="passphraseText">The typed passphrase; used for one derivation and released.</param>
    /// <param name="grantRecipientHex">The service's grant-recipient public key, from <c>describe_service</c>.</param>
    /// <param name="cancellationToken">Cancels the derivation.</param>
    /// <returns>The answer.</returns>
    public static async Task<ProvisionAnswer> BuildProvisionEnvelopeAsync(
        string? archivesRoot,
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

        var archivePath = string.IsNullOrWhiteSpace(archivesRoot) ? null : Path.Combine(archivesRoot, setId);
        if (archivePath is not null
            && File.Exists(Path.Combine(archivePath, RepositoryLifecycle.DescriptorKey.Value)))
        {
            try
            {
                var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(
                    new LocalFileSystemObjectStore(archivePath), cancellationToken).ConfigureAwait(false);
                if (!RepositoryLifecycle.IsWriteOnly(descriptor))
                {
                    return new ProvisionAnswer(
                        GateOutcome.Unavailable,
                        "This set's staging archive is a format 1 repository — an existing repository cannot "
                        + "become write-only (ADR-0042).");
                }

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
                    $"This set's staging archive descriptor does not read: {damaged.Message}");
            }
        }

        // Creation: nothing exists yet (or the archives are not locally
        // readable and the service will refuse an accidental adoption
        // mismatch by name). Fresh salt, current default parameters.
        return BuildCreationEnvelope(passphrase, recipient!);
    }

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

    /// <summary>What building an installation kit produced.</summary>
    /// <param name="Framed">The framed machine form, or null when it could not be built.</param>
    /// <param name="Text">The printable transcribable form, a rendering of those exact bytes.</param>
    /// <param name="Checksum">The kit's SHA-256, lowercase hex — what the confirmation records.</param>
    public sealed record KitAnswer(byte[] Framed, string Text, string Checksum);

    /// <summary>Everything the setup ceremony derives, from one Argon2id pass.</summary>
    /// <param name="Outcome"><see cref="GateOutcome.Verified"/> when both were produced.</param>
    /// <param name="Detail">Why not, when they were not.</param>
    /// <param name="Envelope">The sealed provisioning envelope, hex.</param>
    /// <param name="Kit">The installation's recovery kit, in both forms.</param>
    public sealed record SetupAnswer(
        GateOutcome Outcome, string? Detail = null, string? Envelope = null, KitAnswer? Kit = null);

    /// <summary>
    /// The client half of first-run setup (ADR-0044 §5, FR-KIT-004): mints
    /// this installation's salt, derives from the passphrase here — where
    /// the person typed it — and produces both the sealed provisioning
    /// envelope and the recovery kit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both from <b>one</b> derivation, which is why they are one method.
    /// Argon2id is deliberately expensive; running it twice to produce two
    /// things from the same root would be paying that cost to learn nothing.
    /// </para>
    /// <para>
    /// There is no archives root, no set id and no descriptor here, which is
    /// the whole difference from <see cref="BuildProvisionEnvelopeAsync"/>:
    /// nothing exists yet to adopt or to prove against, so this is always a
    /// creation with a fresh salt. Every archive the installation goes on to
    /// create records that salt, which is what lets one passphrase — and one
    /// kit — open all of them.
    /// </para>
    /// <para>
    /// The kit is built here rather than by the service because a kit is
    /// never produced by a command (ADR-0028): building one re-derives from
    /// a passphrase, and that belongs where the person is.
    /// </para>
    /// </remarks>
    /// <param name="passphraseText">The typed passphrase; used for one derivation and released.</param>
    /// <param name="grantRecipientHex">The service's grant-recipient public key, from <c>describe_service</c>.</param>
    /// <param name="deviceIdHex">The service's device identity, from <c>describe_service</c>.</param>
    /// <returns>The envelope and the kit, or why neither could be made.</returns>
    public static SetupAnswer BuildInstallationSetup(
        string passphraseText, string grantRecipientHex, string deviceIdHex)
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

        byte[] deviceId;
        try
        {
            deviceId = Convert.FromHexString(deviceIdHex ?? string.Empty);
        }
        catch (FormatException)
        {
            return new SetupAnswer(GateOutcome.Unavailable, "The service's device identity is not readable hex.");
        }

        if (deviceId.Length != 16)
        {
            return new SetupAnswer(GateOutcome.Unavailable, "The service's device identity is not 16 bytes.");
        }

        var parameters = Domain.Configuration.RepositoryCreationSettings.Default.KdfParameters;
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);

        using var passphrase = Passphrase.Create(passphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, parameters, salt, Domain.Configuration.KdfValidationMode.CreateRepository);

        var framed = Repository.Format.RecoveryKit.RecoveryKitCodec.Serialize(
            RecoveryKitFactory.BuildForInstallation(
                authority.Credential, salt, parameters, deviceId,
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        return new SetupAnswer(
            GateOutcome.Verified,
            Envelope: Convert.ToHexStringLower(
                WriteOnlyProvisioning.SealProvision(recipient!, authority, salt, parameters)),
            Kit: new KitAnswer(
                framed,
                Repository.Format.RecoveryKit.RecoveryKitText.Render(
                    framed,
                    "This kit is ONE of the two things you need. The other is your passphrase, which is not "
                    + "in here. Keep them apart."),
                Convert.ToHexStringLower(framed.AsSpan(framed.Length - 32))));
    }

    /// <summary>
    /// Rebuilds an installation's kit from an archive it already wrote, for
    /// a ceremony interrupted before the kit was confirmed (FR-KIT-004).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The salt is not ours to mint here — minting a second one would
    /// produce a kit that opens nothing. It comes from the descriptor of an
    /// archive the installation already wrote, and the passphrase is proved
    /// against that descriptor's sealing key before a kit is built, so a
    /// wrong passphrase yields no kit rather than a useless one.
    /// </para>
    /// <para>
    /// An installation with no archive yet has no salt to recover, and this
    /// says so rather than inventing one. That case is real but narrow: it
    /// needs a ceremony abandoned after provisioning and before any set's
    /// first backup.
    /// </para>
    /// </remarks>
    /// <param name="archivesRoot">The service's archives root, from <c>describe_service</c>.</param>
    /// <param name="setIds">The configured sets, whose archives are searched for a descriptor.</param>
    /// <param name="passphraseText">The typed passphrase.</param>
    /// <param name="deviceIdHex">The service's device identity.</param>
    /// <param name="cancellationToken">Cancels the descriptor reads.</param>
    /// <returns>The kit, or why one could not be rebuilt.</returns>
    public static async Task<SetupAnswer> RebuildInstallationKitAsync(
        string? archivesRoot,
        IEnumerable<string> setIds,
        string passphraseText,
        string deviceIdHex,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(setIds);
        ThrowHelper.ThrowIfNull(passphraseText);

        byte[] deviceId;
        try
        {
            deviceId = Convert.FromHexString(deviceIdHex ?? string.Empty);
        }
        catch (FormatException)
        {
            return new SetupAnswer(GateOutcome.Unavailable, "The service's device identity is not readable hex.");
        }

        if (deviceId.Length != 16 || archivesRoot is not { Length: > 0 })
        {
            return new SetupAnswer(
                GateOutcome.Unavailable, "This console cannot read the service's archives from here.");
        }

        foreach (var setId in setIds)
        {
            var path = Path.Combine(archivesRoot, setId);
            if (!Directory.Exists(path))
            {
                continue;
            }

            Repository.Format.Descriptor.RepositoryDescriptor descriptor;
            try
            {
                descriptor = await Repository.RepositoryLifecycle.ReadDescriptorAsync(
                    new Storage.Local.LocalFileSystemObjectStore(path), cancellationToken).ConfigureAwait(false);
            }
            catch (Repository.RepositoryOpenException)
            {
                continue;
            }

            if (!Repository.RepositoryLifecycle.IsWriteOnly(descriptor))
            {
                continue;
            }

            using var passphrase = Passphrase.Create(passphraseText);
            using var authority = WriteOnlyDerivation.Derive(
                passphrase, descriptor.KdfParameters, descriptor.KdfSalt.Span,
                Domain.Configuration.KdfValidationMode.OpenRepository);

            if (!authority.Credential.SealingPublicKey.SequenceEqual(descriptor.SealingPublicKey.Span))
            {
                return new SetupAnswer(
                    GateOutcome.Wrong, "That passphrase does not reproduce this installation's keys.");
            }

            var framed = Repository.Format.RecoveryKit.RecoveryKitCodec.Serialize(
                RecoveryKitFactory.BuildForInstallation(
                    authority.Credential, descriptor.KdfSalt.Span, descriptor.KdfParameters, deviceId,
                    (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

            return new SetupAnswer(
                GateOutcome.Verified,
                Kit: new KitAnswer(
                    framed,
                    Repository.Format.RecoveryKit.RecoveryKitText.Render(
                        framed,
                        "This kit is ONE of the two things you need. The other is your passphrase, which is "
                        + "not in here. Keep them apart."),
                    Convert.ToHexStringLower(framed.AsSpan(framed.Length - 32))));
        }

        return new SetupAnswer(
            GateOutcome.Unavailable,
            "No archive of this installation exists yet, so its salt cannot be recovered — run a backup "
            + "first, then save the kit.");
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
}
