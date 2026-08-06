using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Repository.Format.Keys;
using FallbackPlan.Storage.Abstractions;

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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(passphrase);
        ArgumentNullException.ThrowIfNull(settings);

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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(passphrase);

        // Step 1: the descriptor — magic, digest, version, features.
        var descriptorBytes = await ReadWholeObjectAsync(store, DescriptorKey, cancellationToken).ConfigureAwait(false)
            ?? throw new RepositoryOpenException("No /repository-format object exists — this location does not hold a FallbackPlan repository (specification 01 §3).");

        var descriptor = RepositoryDescriptorCodec.Parse(descriptorBytes) switch
        {
            DescriptorParseResult.Ok ok => ok.Descriptor,
            DescriptorParseResult.NotARepository => throw new RepositoryOpenException(
                "The object at /repository-format is not a FallbackPlan repository descriptor (specification 01 §3.1)."),
            DescriptorParseResult.IntegrityFailure => throw new RepositoryOpenException(
                "The descriptor's digest does not verify — accidental corruption (specification 01 §3.1)."),
            DescriptorParseResult.UnsupportedRequiredFeatures unsupported => throw new RepositoryOpenException(
                "The repository requires unimplemented features: " +
                string.Join(", ", unsupported.Features.Select(feature => $"0x{feature:x4}")) +
                " — refused, not guessed (specification 01 §3.2)."),
            DescriptorParseResult.FormatViolation violation => throw new RepositoryOpenException(violation.Message),
            var other => throw new RepositoryOpenException($"Unrecognised descriptor parse outcome {other}."),
        };

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

        throw new RepositoryOpenException(
            "The descriptor is present but /keys/ listed no key object — a lagging store listing; retry rather than treating this as damage (ADR-0022 §Decision 3).");
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
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(passphrase);

        var descriptorBytes = await ReadWholeObjectAsync(store, DescriptorKey, cancellationToken).ConfigureAwait(false)
            ?? throw new RepositoryOpenException("No repository descriptor exists at /repository-format.");

        if (RepositoryDescriptorCodec.Parse(descriptorBytes) is not DescriptorParseResult.Ok { Descriptor: var descriptor })
        {
            throw new RepositoryOpenException("The descriptor does not verify — nothing is exported from an unverifiable repository.");
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
            throw new IOException(
                $"The store refused '{key}' with {result.Outcome} — an existing object at this key means the location already holds a repository (specification 01 §4).");
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
