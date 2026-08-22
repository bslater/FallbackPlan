using Bodu;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Repository.Format.Keys;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Repository.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>
/// An opened repository: the verified descriptor, the unwrapped key set, and
/// the bundle's current generations — everything discovery steps 1–3 of
/// specification 01 §6 produce.
/// </summary>
public sealed class OpenedRepository : IDisposable
{
    internal OpenedRepository(
        RepositoryDescriptor descriptor,
        RepositoryKeySet keys,
        KeyHierarchy hierarchy,
        KeyGeneration currentDataGeneration,
        KeyGeneration currentMetadataGeneration,
        bool kdfBelowCreationMinimums)
    {
        Descriptor = descriptor;
        Keys = keys;
        Hierarchy = hierarchy;
        CurrentDataGeneration = currentDataGeneration;
        CurrentMetadataGeneration = currentMetadataGeneration;
        KdfBelowCreationMinimums = kdfBelowCreationMinimums;
    }

    /// <summary>The verified descriptor.</summary>
    public RepositoryDescriptor Descriptor { get; }

    /// <summary>The repository identity, from the descriptor.</summary>
    public RepositoryId RepositoryId => Descriptor.RepositoryId;

    /// <summary>The derived key set.</summary>
    public RepositoryKeySet Keys { get; }

    /// <summary>The key hierarchy — what signers and per-generation keys derive from.</summary>
    public KeyHierarchy Hierarchy { get; }

    /// <summary>The bundle's current data-key generation (specification 03 §3.1).</summary>
    public KeyGeneration CurrentDataGeneration { get; }

    /// <summary>The bundle's current metadata-key generation (specification 03 §3.1).</summary>
    public KeyGeneration CurrentMetadataGeneration { get; }

    /// <summary>
    /// Whether the descriptor requires the unstable-format warning
    /// (specification 01 §3.2): pre-1.0 repositories carry no
    /// forward-compatibility guarantee, and a user pointing their only copy
    /// of something at one deserves to know.
    /// </summary>
    public bool UnstableFormatWarning => Descriptor.UnstableFormat;

    /// <summary>
    /// Whether the stored KDF parameters fall below today's creation
    /// minimums — accepted (stored parameters are facts) but worth a warning
    /// (specification 03 §2).
    /// </summary>
    public bool KdfBelowCreationMinimums { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        Keys.Dispose();
        Hierarchy.Dispose();
    }
}

/// <summary>
/// Creates and opens repositories (specification 01 §3, §6; FR-REP-002,
/// FR-ARCH-008). Creation writes the key object <b>before</b> the
/// descriptor, so a store that shows `/repository-format` has already
/// acknowledged `/keys/&lt;key-id&gt;` — the ordering that closes most of the
/// key-discovery listing window (ADR-0022 §Decision 3). Opening follows the
/// 01 §6 discovery order: descriptor, KEK, key object — in that order,
/// because each step gates the next.
/// </summary>
public static class RepositoryLifecycle
{
    /// <summary>The descriptor's fixed store key (specification 01 §2).</summary>
    public static readonly ObjectKey DescriptorKey = ObjectKey.Parse("repository-format");

    /// <summary>
    /// Creates a new repository: random identity, random master key, KEK from
    /// the passphrase at creation-validated parameters, wrapped bundle at
    /// <c>/keys/&lt;key-id&gt;</c>, then the descriptor.
    /// </summary>
    /// <exception cref="ArgumentException">The settings are invalid, or KDF parameters fall below the creation minimums (specification 03 §2).</exception>
    /// <exception cref="IOException">The store refused an object — including an already-present descriptor, which means the location already holds a repository.</exception>
    public static async ValueTask<OpenedRepository> CreateAsync(
        IObjectStore store,
        Passphrase passphrase,
        RepositoryCreationSettings settings,
        ulong createdAtUnixMilliseconds,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var created = await CreateCoreAsync(
            store, passphrase, settings, createdAtUnixMilliseconds, cancellationToken).ConfigureAwait(false);
        Log.RepositoryCreated(
            logger ?? NullLogger.Instance, created.RepositoryId, created.Descriptor.FormatVersion,
            writeOnly: false);
        return created;
    }

    // The public entry point above is the whole of this method's diagnostics:
    // one place that reports what opened or was refused, rather than a log call
    // beside every throw. Every refusal here is a RepositoryOpenException or a
    // KeyUnwrapFailedException by design, which is what makes that possible.
    private static async ValueTask<OpenedRepository> CreateCoreAsync(
        IObjectStore store,
        Passphrase passphrase,
        RepositoryCreationSettings settings,
        ulong createdAtUnixMilliseconds,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(passphrase);
        ThrowHelper.ThrowIfNull(settings);

        var validation = settings.Validate();
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                "The creation settings are invalid: " + string.Join(", ", validation.Defects.Select(defect => defect.Name)),
                nameof(settings));
        }

        Span<byte> repositoryIdBytes = stackalloc byte[RepositoryId.Size];
        RandomNumberGenerator.Fill(repositoryIdBytes);
        var repositoryId = RepositoryId.FromBytes(repositoryIdBytes);

        Span<byte> keyIdBytes = stackalloc byte[KeyId.Size];
        RandomNumberGenerator.Fill(keyIdBytes);
        var keyId = KeyId.FromBytes(keyIdBytes);

        var kdfSalt = new byte[KekDerivation.SaltLength];
        RandomNumberGenerator.Fill(kdfSalt);

        var masterKey = new byte[KeyHierarchy.MasterKeyLength];
        RandomNumberGenerator.Fill(masterKey);

        try
        {
            // Wrap the bundle under the KEK (03 §3): nonce random, AAD binds
            // magic, version, profile, and key id.
            byte[] keyObjectBytes;
            using (var derivation = KekDerivation.Derive(passphrase, settings.KdfParameters, kdfSalt, KdfValidationMode.CreateRepository))
            using (var bundle = new KeyBundle(masterKey, currentDataGeneration: 0, currentMetadataGeneration: 0, createdAtUnixMilliseconds))
            {
                var bundleCbor = KeyBundleCodec.Encode(bundle);

                try
                {
                    var nonce = new byte[KeyWrapping.NonceLength];
                    RandomNumberGenerator.Fill(nonce);
                    var aad = KeyObjectFraming.BuildAad(FormatLimits.FormatVersion, KeyObjectFraming.KekProfileAes256GcmV1, keyId);
                    var ciphertext = new byte[bundleCbor.Length];
                    var tag = new byte[KeyWrapping.TagLength];
                    KeyWrapping.Wrap(derivation.Kek, nonce, aad, bundleCbor, ciphertext, tag);

                    keyObjectBytes = KeyObjectFraming.Serialize(FormatLimits.FormatVersion, keyId, nonce, ciphertext, tag);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bundleCbor);
                }
            }

            // Key object first, descriptor last (ADR-0022 §Decision 3): a
            // visible descriptor implies the key object is durable.
            var keyObjectKey = ObjectKey.Parse($"keys/{Domain.Base32.Encode(keyId.ToArray())}");
            await PutWholeObjectAsync(store, keyObjectKey, keyObjectBytes, cancellationToken).ConfigureAwait(false);

            var descriptor = new RepositoryDescriptor(
                repositoryId,
                FormatLimits.FormatVersion,
                RequiredFeatures: [],
                OptionalFeatures: [],
                settings.KdfParameters,
                kdfSalt,
                createdAtUnixMilliseconds,
                settings.CreatedBy,
                UnstableFormat: true);

            await PutWholeObjectAsync(store, DescriptorKey, RepositoryDescriptorCodec.Serialize(descriptor), cancellationToken)
                .ConfigureAwait(false);

            return new OpenedRepository(
                descriptor,
                RepositoryKeySet.FromMasterKey(masterKey),
                new KeyHierarchy(masterKey),
                KeyGeneration.Zero,
                KeyGeneration.Zero,
                kdfBelowCreationMinimums: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    /// <summary>Whether a descriptor names a write-only (format v2) repository (ADR-0042).</summary>
    public static bool IsWriteOnly(RepositoryDescriptor descriptor)
    {
        ThrowHelper.ThrowIfNull(descriptor);
        return descriptor.FormatVersion >= FormatLimits.SealedFormatVersion;
    }

    /// <summary>
    /// Reads and verifies the descriptor alone — discovery step 1, for
    /// callers that need to know what they are looking at (which format,
    /// which public key, which KDF parameters) before deciding how to open.
    /// </summary>
    /// <exception cref="RepositoryOpenException">The store holds no verifiable descriptor.</exception>
    public static async ValueTask<RepositoryDescriptor> ReadDescriptorAsync(
        IObjectStore store, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);

        var descriptorBytes = await ReadWholeObjectAsync(store, DescriptorKey, cancellationToken).ConfigureAwait(false)
            ?? throw new RepositoryOpenException(Strings.RepositoryLifecycle_NoRepositoryFormatObjectExists);

        return ParseDescriptorOrThrow(descriptorBytes);
    }

    /// <summary>
    /// Creates a write-only (format v2) repository (ADR-0042 §1): random
    /// identity and salt, the whole key material derived from the passphrase,
    /// the sealing public key recorded in the descriptor — and <b>no</b>
    /// <c>/keys/</c> object, because nothing is wrapped and nothing is
    /// stored. The returned pair carries the write bundle for the service
    /// and, this once, the read authority — creation holds the passphrase
    /// anyway; the caller zeroes both by disposing.
    /// </summary>
    /// <exception cref="ArgumentException">The settings are invalid, or KDF parameters fall below the creation minimums.</exception>
    /// <exception cref="IOException">The store refused the descriptor — the location already holds a repository.</exception>
    public static async ValueTask<(OpenedRepository Repository, RepositoryReadAuthority Authority)> CreateWriteOnlyAsync(
        IObjectStore store,
        Passphrase passphrase,
        RepositoryCreationSettings settings,
        ulong createdAtUnixMilliseconds,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var created = await CreateWriteOnlyCoreAsync(
            store, passphrase, settings, createdAtUnixMilliseconds, cancellationToken).ConfigureAwait(false);
        Log.RepositoryCreated(
            logger ?? NullLogger.Instance, created.Repository.RepositoryId,
            created.Repository.Descriptor.FormatVersion, writeOnly: true);
        return created;
    }

    // The public entry point above is the whole of this method's diagnostics:
    // one place that reports what opened or was refused, rather than a log call
    // beside every throw. Every refusal here is a RepositoryOpenException or a
    // KeyUnwrapFailedException by design, which is what makes that possible.
    private static async ValueTask<(OpenedRepository Repository, RepositoryReadAuthority Authority)> CreateWriteOnlyCoreAsync(
        IObjectStore store,
        Passphrase passphrase,
        RepositoryCreationSettings settings,
        ulong createdAtUnixMilliseconds,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(passphrase);
        ThrowHelper.ThrowIfNull(settings);

        var validation = settings.Validate();
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                "The creation settings are invalid: " + string.Join(", ", validation.Defects.Select(defect => defect.Name)),
                nameof(settings));
        }

        Span<byte> repositoryIdBytes = stackalloc byte[RepositoryId.Size];
        RandomNumberGenerator.Fill(repositoryIdBytes);
        var repositoryId = RepositoryId.FromBytes(repositoryIdBytes);

        var kdfSalt = new byte[KekDerivation.SaltLength];
        RandomNumberGenerator.Fill(kdfSalt);

        var authority = WriteOnlyDerivation.Derive(
            passphrase, settings.KdfParameters, kdfSalt, KdfValidationMode.CreateRepository);
        try
        {
            var descriptor = new RepositoryDescriptor(
                repositoryId,
                FormatLimits.SealedFormatVersion,
                RequiredFeatures: [RepositoryDescriptorCodec.FeatureSealedDataPlane],
                OptionalFeatures: [],
                settings.KdfParameters,
                kdfSalt,
                createdAtUnixMilliseconds,
                settings.CreatedBy,
                UnstableFormat: true,
                authority.Credential.SealingPublicKey.ToArray());

            await PutWholeObjectAsync(store, DescriptorKey, RepositoryDescriptorCodec.Serialize(descriptor), cancellationToken)
                .ConfigureAwait(false);

            var opened = new OpenedRepository(
                descriptor,
                RepositoryKeySet.FromWriteCredential(authority.Credential),
                KeyHierarchy.ForWriteOnly(authority.Credential),
                KeyGeneration.Zero,
                KeyGeneration.Zero,
                kdfBelowCreationMinimums: false);

            return (opened, authority);
        }
        catch
        {
            authority.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a write-only repository from an already-derived write bundle —
    /// the service's half of the provisioning ceremony (ADR-0042 §4): the
    /// admin client ran Argon2id where the person typed, and what arrived
    /// here is the credential plus the KDF salt and parameters the descriptor
    /// must record so a later restore can re-derive. The service never held
    /// the passphrase, which is exactly why this overload exists.
    /// </summary>
    /// <exception cref="ArgumentException">The salt is not exactly <see cref="KekDerivation.SaltLength"/> bytes.</exception>
    /// <exception cref="IOException">The store refused the descriptor — the location already holds a repository.</exception>
    public static async ValueTask<OpenedRepository> CreateWriteOnlyFromCredentialAsync(
        IObjectStore store,
        RepositoryWriteCredential credential,
        ReadOnlyMemory<byte> kdfSalt,
        Argon2Parameters kdfParameters,
        string createdBy,
        ulong createdAtUnixMilliseconds,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var created = await CreateWriteOnlyFromCredentialCoreAsync(
            store, credential, kdfSalt, kdfParameters, createdBy, createdAtUnixMilliseconds, cancellationToken)
            .ConfigureAwait(false);
        Log.RepositoryCreated(
            logger ?? NullLogger.Instance, created.RepositoryId, created.Descriptor.FormatVersion,
            writeOnly: true);
        return created;
    }

    // The public entry point above is the whole of this method's diagnostics:
    // one place that reports what opened or was refused, rather than a log call
    // beside every throw. Every refusal here is a RepositoryOpenException or a
    // KeyUnwrapFailedException by design, which is what makes that possible.
    private static async ValueTask<OpenedRepository> CreateWriteOnlyFromCredentialCoreAsync(
        IObjectStore store,
        RepositoryWriteCredential credential,
        ReadOnlyMemory<byte> kdfSalt,
        Argon2Parameters kdfParameters,
        string createdBy,
        ulong createdAtUnixMilliseconds,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(credential);
        ThrowHelper.ThrowIfNull(kdfParameters);
        ThrowHelper.ThrowIfNullOrWhiteSpace(createdBy);

        if (kdfSalt.Length != KekDerivation.SaltLength)
        {
            throw new ArgumentException(
                $"The KDF salt must be exactly {KekDerivation.SaltLength} bytes.", nameof(kdfSalt));
        }

        Span<byte> repositoryIdBytes = stackalloc byte[RepositoryId.Size];
        RandomNumberGenerator.Fill(repositoryIdBytes);
        var repositoryId = RepositoryId.FromBytes(repositoryIdBytes);

        var descriptor = new RepositoryDescriptor(
            repositoryId,
            FormatLimits.SealedFormatVersion,
            RequiredFeatures: [RepositoryDescriptorCodec.FeatureSealedDataPlane],
            OptionalFeatures: [],
            kdfParameters,
            kdfSalt.ToArray(),
            createdAtUnixMilliseconds,
            createdBy,
            UnstableFormat: true,
            credential.SealingPublicKey.ToArray());

        await PutWholeObjectAsync(store, DescriptorKey, RepositoryDescriptorCodec.Serialize(descriptor), cancellationToken)
            .ConfigureAwait(false);

        return new OpenedRepository(
            descriptor,
            RepositoryKeySet.FromWriteCredential(credential),
            KeyHierarchy.ForWriteOnly(credential),
            KeyGeneration.Zero,
            KeyGeneration.Zero,
            kdfBelowCreationMinimums: !kdfParameters.ValidateCreationMinimums().IsValid);
    }

    /// <summary>
    /// Opens a write-only repository with its write bundle — the service's
    /// everyday open (ADR-0042 §5): no passphrase, no content capability.
    /// The credential is verified against the descriptor's sealing public
    /// key, so a bundle belonging to another repository — or derived from a
    /// wrong passphrase — is refused by name before anything is read.
    /// </summary>
    /// <exception cref="RepositoryOpenException">The store holds no verifiable write-only repository, or the credential does not belong to it.</exception>
    public static async ValueTask<OpenedRepository> OpenWriteOnlyAsync(
        IObjectStore store,
        RepositoryWriteCredential credential,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        try
        {
            var opened = await OpenWriteOnlyCoreAsync(store, credential, cancellationToken).ConfigureAwait(false);
            Log.RepositoryOpened(log, opened.RepositoryId, opened.Descriptor.FormatVersion, writeOnly: true);
            return opened;
        }
        catch (RepositoryOpenException refusal)
        {
            Log.RepositoryOpenRefused(log, refusal.Message);
            throw;
        }
    }

    // The public entry point above is the whole of this method's diagnostics:
    // one place that reports what opened or was refused, rather than a log call
    // beside every throw. Every refusal here is a RepositoryOpenException or a
    // KeyUnwrapFailedException by design, which is what makes that possible.
    private static async ValueTask<OpenedRepository> OpenWriteOnlyCoreAsync(
        IObjectStore store,
        RepositoryWriteCredential credential,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(credential);

        var descriptor = await ReadWriteOnlyDescriptorAsync(store, cancellationToken).ConfigureAwait(false);

        if (!credential.SealingPublicKey.SequenceEqual(descriptor.SealingPublicKey.Span))
        {
            throw new RepositoryOpenException(Strings.RepositoryLifecycle_CredentialNotThisRepository);
        }

        return new OpenedRepository(
            descriptor,
            RepositoryKeySet.FromWriteCredential(credential),
            KeyHierarchy.ForWriteOnly(credential),
            KeyGeneration.Zero,
            KeyGeneration.Zero,
            kdfBelowCreationMinimums: !descriptor.KdfParameters.ValidateCreationMinimums().IsValid);
    }

    /// <summary>
    /// Opens a write-only repository for reading — the restore path
    /// (ADR-0042 §4): derives the whole authority from the passphrase and
    /// the descriptor's public salt and parameters, and proves it by
    /// comparing the derived public key against the descriptor's copy. No
    /// decryption is involved in the proof; equality is the verifier.
    /// </summary>
    /// <exception cref="RepositoryOpenException">The store holds no verifiable write-only repository.</exception>
    /// <exception cref="KeyUnwrapFailedException">The passphrase does not reproduce this repository's keys.</exception>
    public static async ValueTask<(OpenedRepository Repository, RepositoryReadAuthority Authority)> OpenWriteOnlyForReadAsync(
        IObjectStore store,
        Passphrase passphrase,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        try
        {
            var opened = await OpenWriteOnlyForReadCoreAsync(store, passphrase, cancellationToken)
                .ConfigureAwait(false);
            Log.RepositoryOpened(
                log, opened.Repository.RepositoryId, opened.Repository.Descriptor.FormatVersion, writeOnly: true);
            return opened;
        }
        catch (Exception refusal) when (refusal is RepositoryOpenException or KeyUnwrapFailedException)
        {
            // The wrong passphrase reaches here as a KeyUnwrapFailedException,
            // and from an operator's side it is the same event as any other
            // refusal to open: they pointed at an archive and did not get in.
            Log.RepositoryOpenRefused(log, refusal.Message);
            throw;
        }
    }

    // The public entry point above is the whole of this method's diagnostics:
    // one place that reports what opened or was refused, rather than a log call
    // beside every throw. Every refusal here is a RepositoryOpenException or a
    // KeyUnwrapFailedException by design, which is what makes that possible.
    private static async ValueTask<(OpenedRepository Repository, RepositoryReadAuthority Authority)> OpenWriteOnlyForReadCoreAsync(
        IObjectStore store,
        Passphrase passphrase,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(passphrase);

        var descriptor = await ReadWriteOnlyDescriptorAsync(store, cancellationToken).ConfigureAwait(false);

        if (!TryDeriveReadAuthority(descriptor, passphrase, out var authority))
        {
            throw new KeyUnwrapFailedException(Strings.RepositoryLifecycle_PassphraseDoesNotReproduce);
        }

        try
        {
            var opened = new OpenedRepository(
                descriptor,
                RepositoryKeySet.FromWriteCredential(authority!.Credential),
                KeyHierarchy.ForWriteOnly(authority.Credential),
                KeyGeneration.Zero,
                KeyGeneration.Zero,
                kdfBelowCreationMinimums: !descriptor.KdfParameters.ValidateCreationMinimums().IsValid);

            return (opened, authority);
        }
        catch
        {
            authority!.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Derives a read authority from a passphrase against a write-only
    /// descriptor and verifies it by public-key equality — the
    /// derive-and-compare gate clients run where the person typed
    /// (ADR-0042 §1, §4). False means the wrong passphrase; the authority is
    /// disposed and nulled.
    /// </summary>
    public static bool TryDeriveReadAuthority(
        RepositoryDescriptor descriptor, Passphrase passphrase, out RepositoryReadAuthority? authority)
    {
        ThrowHelper.ThrowIfNull(descriptor);
        ThrowHelper.ThrowIfNull(passphrase);

        var derived = WriteOnlyDerivation.Derive(
            passphrase, descriptor.KdfParameters, descriptor.KdfSalt.Span, KdfValidationMode.OpenRepository);

        if (!derived.Credential.SealingPublicKey.SequenceEqual(descriptor.SealingPublicKey.Span))
        {
            derived.Dispose();
            authority = null;
            return false;
        }

        authority = derived;
        return true;
    }

    private static async ValueTask<RepositoryDescriptor> ReadWriteOnlyDescriptorAsync(
        IObjectStore store, CancellationToken cancellationToken)
    {
        var descriptorBytes = await ReadWholeObjectAsync(store, DescriptorKey, cancellationToken).ConfigureAwait(false)
            ?? throw new RepositoryOpenException(Strings.RepositoryLifecycle_NoRepositoryFormatObjectExists);

        var descriptor = ParseDescriptorOrThrow(descriptorBytes);

        return IsWriteOnly(descriptor)
            ? descriptor
            : throw new RepositoryOpenException(Strings.RepositoryLifecycle_NotWriteOnlyRepository);
    }

    private static RepositoryDescriptor ParseDescriptorOrThrow(byte[] descriptorBytes) =>
        RepositoryDescriptorCodec.Parse(descriptorBytes) switch
        {
            DescriptorParseResult.Ok ok => ok.Descriptor,
            DescriptorParseResult.NotARepository => throw new RepositoryOpenException(Strings.RepositoryLifecycle_ObjectRepositoryFormatNotFallbackPlan),
            DescriptorParseResult.IntegrityFailure => throw new RepositoryOpenException(Strings.RepositoryLifecycle_DescriptorSDigestDoesNot),
            DescriptorParseResult.UnsupportedRequiredFeatures unsupported => throw new RepositoryOpenException(
                "The repository requires unimplemented features: " +
                string.Join(", ", unsupported.Features.Select(feature => $"0x{feature:x4}")) +
                " — refused, not guessed (specification 01 §3.2)."),
            DescriptorParseResult.FormatViolation violation => throw new RepositoryOpenException(violation.Message),
            var other => throw new RepositoryOpenException(Strings.FormatRepositoryLifecycle_UnrecognisedDescriptorParseOutcome(other)),
        };

    /// <summary>
    /// Opens a repository through discovery steps 1–3 (specification 01 §6):
    /// fetch and verify the descriptor, derive the KEK from
    /// <c>kdf_parameters</c>, list <c>/keys/</c> and unwrap the key object.
    /// </summary>
    /// <exception cref="RepositoryOpenException">Any step refused — the message carries the distinct finding.</exception>
    /// <exception cref="KeyUnwrapFailedException">The passphrase is wrong or the key object tampered — deliberately indistinguishable (specification 03 §3).</exception>
    public static async ValueTask<OpenedRepository> OpenAsync(
        IObjectStore store,
        Passphrase passphrase,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        try
        {
            var opened = await OpenCoreAsync(store, passphrase, cancellationToken).ConfigureAwait(false);
            Log.RepositoryOpened(log, opened.RepositoryId, opened.Descriptor.FormatVersion, writeOnly: false);
            return opened;
        }
        catch (Exception refusal) when (refusal is RepositoryOpenException or KeyUnwrapFailedException)
        {
            Log.RepositoryOpenRefused(log, refusal.Message);
            throw;
        }
    }

    // The public entry point above is the whole of this method's diagnostics:
    // one place that reports what opened or was refused, rather than a log call
    // beside every throw. Every refusal here is a RepositoryOpenException or a
    // KeyUnwrapFailedException by design, which is what makes that possible.
    private static async ValueTask<OpenedRepository> OpenCoreAsync(
        IObjectStore store,
        Passphrase passphrase,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(passphrase);

        // Step 1: the descriptor — magic, digest, version, features.
        var descriptorBytes = await ReadWholeObjectAsync(store, DescriptorKey, cancellationToken).ConfigureAwait(false)
            ?? throw new RepositoryOpenException(Strings.RepositoryLifecycle_NoRepositoryFormatObjectExists);

        var descriptor = RepositoryDescriptorCodec.Parse(descriptorBytes) switch
        {
            DescriptorParseResult.Ok ok => ok.Descriptor,
            DescriptorParseResult.NotARepository => throw new RepositoryOpenException(Strings.RepositoryLifecycle_ObjectRepositoryFormatNotFallbackPlan),
            DescriptorParseResult.IntegrityFailure => throw new RepositoryOpenException(Strings.RepositoryLifecycle_DescriptorSDigestDoesNot),
            DescriptorParseResult.UnsupportedRequiredFeatures unsupported => throw new RepositoryOpenException(
                "The repository requires unimplemented features: " +
                string.Join(", ", unsupported.Features.Select(feature => $"0x{feature:x4}")) +
                " — refused, not guessed (specification 01 §3.2)."),
            DescriptorParseResult.FormatViolation violation => throw new RepositoryOpenException(violation.Message),
            var other => throw new RepositoryOpenException(Strings.FormatRepositoryLifecycle_UnrecognisedDescriptorParseOutcome(other)),
        };

        // A write-only repository has no key object to unwrap — the v1 walk
        // below would end in a misleading "no keys listed". Name the real
        // situation instead.
        if (IsWriteOnly(descriptor))
        {
            throw new RepositoryOpenException(Strings.RepositoryLifecycle_WriteOnlyNeedsDerivedOpen);
        }

        // Step 2: the KEK, from the descriptor's public parameters. Stored
        // parameters are facts — below-minimum values are accepted and
        // surfaced as a warning, never silently rejected (03 §2).
        using var derivation = KekDerivation.Derive(
            passphrase, descriptor.KdfParameters, descriptor.KdfSalt.Span, KdfValidationMode.OpenRepository);

        // Step 3: list /keys/ and unwrap (ADR-0022 §Decision 3). An empty
        // listing under a present descriptor is a transient open failure —
        // the caller retries — not a damage finding.
        KeyUnwrapFailedException? lastUnwrapFailure = null;

        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("keys/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            var keyObjectBytes = await ReadWholeObjectAsync(store, entry.Key, cancellationToken).ConfigureAwait(false);
            if (keyObjectBytes is null)
            {
                continue;
            }

            var keyObject = KeyObjectFraming.Parse(keyObjectBytes);
            var aad = KeyObjectFraming.BuildAad(keyObject.FormatVersion, keyObject.KekProfile, keyObject.KeyId);

            byte[] bundleCbor;
            try
            {
                bundleCbor = KeyWrapping.Unwrap(
                    derivation.Kek, keyObject.WrapNonce, aad, keyObject.Wrapped, keyObject.Tag);
            }
            catch (KeyUnwrapFailedException failure)
            {
                lastUnwrapFailure = failure;
                continue;
            }

            try
            {
                using var bundle = KeyBundleCodec.Decode(bundleCbor);

                return new OpenedRepository(
                    descriptor,
                    RepositoryKeySet.FromMasterKey(bundle.MasterKey),
                    new KeyHierarchy(bundle.MasterKey),
                    new KeyGeneration(bundle.CurrentDataGeneration),
                    new KeyGeneration(bundle.CurrentMetadataGeneration),
                    derivation.BelowCreationMinimums);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bundleCbor);
            }
        }

        if (lastUnwrapFailure is not null)
        {
            throw lastUnwrapFailure;
        }

        throw new RepositoryOpenException(Strings.RepositoryLifecycle_DescriptorPresentButKeysListed);
    }

    /// <summary>
    /// The key-export path (FR-KIT-001): re-derives the KEK from the
    /// passphrase, finds the key object it opens, and returns that object's
    /// <b>verbatim stored bytes</b> together with the verified descriptor —
    /// the two inputs a recovery kit carries. No re-wrapping: the kit
    /// reuses the FBPKKEYS object exactly as stored, so a kit is never
    /// exported that the passphrase cannot open, and the master key never
    /// outlives this call's stack.
    /// </summary>
    /// <exception cref="KeyUnwrapFailedException">The passphrase opens no stored key object.</exception>
    /// <exception cref="RepositoryOpenException">The store holds no verifiable repository.</exception>
    public static async ValueTask<(RepositoryDescriptor Descriptor, byte[] KeyObject)> ExportVerifiedKeyObjectAsync(
        IObjectStore store,
        Passphrase passphrase,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(passphrase);

        var descriptorBytes = await ReadWholeObjectAsync(store, DescriptorKey, cancellationToken).ConfigureAwait(false)
            ?? throw new RepositoryOpenException(Strings.RepositoryLifecycle_NoRepositoryDescriptorExistsRepository);

        if (RepositoryDescriptorCodec.Parse(descriptorBytes) is not DescriptorParseResult.Ok { Descriptor: var descriptor })
        {
            throw new RepositoryOpenException(Strings.RepositoryLifecycle_DescriptorDoesNotVerify);
        }

        // A write-only repository has no key object to export: its kit
        // carries no key material at all (ADR-0042 §8) and is built from the
        // descriptor alone.
        if (IsWriteOnly(descriptor))
        {
            throw new RepositoryOpenException(Strings.RepositoryLifecycle_WriteOnlyNeedsDerivedOpen);
        }

        using var derivation = KekDerivation.Derive(
            passphrase, descriptor.KdfParameters, descriptor.KdfSalt.Span, KdfValidationMode.OpenRepository);

        KeyUnwrapFailedException? lastUnwrapFailure = null;

        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("keys/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            var keyObjectBytes = await ReadWholeObjectAsync(store, entry.Key, cancellationToken).ConfigureAwait(false);
            if (keyObjectBytes is null)
            {
                continue;
            }

            var keyObject = KeyObjectFraming.Parse(keyObjectBytes);
            var aad = KeyObjectFraming.BuildAad(keyObject.FormatVersion, keyObject.KekProfile, keyObject.KeyId);

            byte[] bundleCbor;
            try
            {
                bundleCbor = KeyWrapping.Unwrap(
                    derivation.Kek, keyObject.WrapNonce, aad, keyObject.Wrapped, keyObject.Tag);
            }
            catch (KeyUnwrapFailedException failure)
            {
                lastUnwrapFailure = failure;
                continue;
            }

            CryptographicOperations.ZeroMemory(bundleCbor);
            return (descriptor, keyObjectBytes);
        }

        throw lastUnwrapFailure is not null
            ? lastUnwrapFailure
            : new RepositoryOpenException("The descriptor is present but /keys/ listed no key object.");
    }

    private static async ValueTask PutWholeObjectAsync(
        IObjectStore store,
        ObjectKey key,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var result = await store.PutAsync(
            key,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false)),
            PutConditions.IfNotExists,
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome != PutOutcome.Created)
        {
            throw new IOException(Strings.FormatRepositoryLifecycle_StoreRefusedWith(key, result.Outcome));
        }
    }

    private static async ValueTask<byte[]?> ReadWholeObjectAsync(
        IObjectStore store,
        ObjectKey key,
        CancellationToken cancellationToken)
    {
        using var result = await store.OpenReadAsync(key, range: null, cancellationToken).ConfigureAwait(false);

        if (result.Outcome != OpenReadOutcome.Found)
        {
            return null;
        }

        using var memory = new MemoryStream();
        await result.Content!.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return memory.ToArray();
    }
}

/// <summary>A repository failed to open; the message carries the distinct finding.</summary>
public sealed class RepositoryOpenException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RepositoryOpenException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with no detail.</summary>
    public RepositoryOpenException()
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public RepositoryOpenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
