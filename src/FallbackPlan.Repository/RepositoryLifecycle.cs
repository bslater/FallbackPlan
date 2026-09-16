using Bodu;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Repository.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>
/// An opened repository: the verified descriptor, the derived key set, and
/// the current generations — everything discovery steps 1–2 of
/// specification 01 §6 produce.
/// </summary>
public sealed class OpenedRepository : IDisposable
{
    internal OpenedRepository(
        RepositoryDescriptor descriptor,
        RepositoryKeySet keys,
        RepositoryWriteCredential credential,
        KeyGeneration currentDataGeneration,
        KeyGeneration currentMetadataGeneration,
        bool kdfBelowCreationMinimums)
    {
        Descriptor = descriptor;
        Keys = keys;
        Credential = credential;
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

    /// <summary>The write credential — what signers and per-generation keys derive from.</summary>
    public RepositoryWriteCredential Credential { get; }

    /// <summary>The current data-plane generation (specification 03 §9).</summary>
    public KeyGeneration CurrentDataGeneration { get; }

    /// <summary>The current metadata-key generation (specification 03 §9).</summary>
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
        Credential.Dispose();
    }
}

/// <summary>
/// Creates and opens repositories (specification 01 §3, §6; FR-REP-002,
/// FR-ARCH-008). A repository is one object before it is anything else: the
/// descriptor at <c>/repository-format</c>, which carries the public salt,
/// the Argon2id parameters and the sealing public key (ADR-0042 §1). Every
/// open starts there — the write credential proves itself against the
/// descriptor's public key, and a passphrase re-derives the whole authority
/// from the descriptor's salt and proves it the same way. Nothing is
/// unwrapped; equality is the verifier.
/// </summary>
public static class RepositoryLifecycle
{
    /// <summary>The descriptor's fixed store key (specification 01 §2).</summary>
    public static readonly ObjectKey DescriptorKey = ObjectKey.Parse("repository-format");

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
    /// Creates a repository from a passphrase (ADR-0042 §1): random identity
    /// and salt, the whole key material derived from the passphrase, the
    /// sealing public key recorded in the descriptor — and nothing else
    /// stored, because nothing is wrapped. The returned pair carries the
    /// write bundle for the service and, this once, the read authority —
    /// creation holds the passphrase anyway; the caller zeroes both by
    /// disposing.
    /// </summary>
    /// <exception cref="ArgumentException">The settings are invalid, or KDF parameters fall below the creation minimums.</exception>
    /// <exception cref="IOException">The store refused the descriptor — the location already holds a repository.</exception>
    public static async ValueTask<(OpenedRepository Repository, RepositoryReadAuthority Authority)> CreateFromPassphraseAsync(
        IObjectStore store,
        Passphrase passphrase,
        RepositoryCreationSettings settings,
        ulong createdAtUnixMilliseconds,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var created = await CreateFromPassphraseCoreAsync(
            store, passphrase, settings, createdAtUnixMilliseconds, cancellationToken).ConfigureAwait(false);
        Log.RepositoryCreated(
            logger ?? NullLogger.Instance, created.Repository.RepositoryId,
            created.Repository.Descriptor.FormatVersion);
        return created;
    }

    // The public entry point above is the whole of this method's diagnostics:
    // one place that reports what was created or refused, rather than a log
    // call beside every throw.
    private static async ValueTask<(OpenedRepository Repository, RepositoryReadAuthority Authority)> CreateFromPassphraseCoreAsync(
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
                FormatLimits.FormatVersion,
                RequiredFeatures: [
                    RepositoryDescriptorCodec.FeatureSealedDataPlane,
                    RepositoryDescriptorCodec.FeatureReclaimAuthority,
                ],
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
                authority.Credential.Clone(),
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
    public static async ValueTask<OpenedRepository> CreateAsync(
        IObjectStore store,
        RepositoryWriteCredential credential,
        ReadOnlyMemory<byte> kdfSalt,
        Argon2Parameters kdfParameters,
        string createdBy,
        ulong createdAtUnixMilliseconds,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var created = await CreateCoreAsync(
            store, credential, kdfSalt, kdfParameters, createdBy, createdAtUnixMilliseconds, cancellationToken)
            .ConfigureAwait(false);
        Log.RepositoryCreated(
            logger ?? NullLogger.Instance, created.RepositoryId, created.Descriptor.FormatVersion);
        return created;
    }

    // The public entry point above is the whole of this method's diagnostics:
    // one place that reports what opened or was refused, rather than a log call
    // beside every throw. Every refusal here is a RepositoryOpenException or a
    // KeyUnwrapFailedException by design, which is what makes that possible.
    private static async ValueTask<OpenedRepository> CreateCoreAsync(
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
            FormatLimits.FormatVersion,
            RequiredFeatures: [
                    RepositoryDescriptorCodec.FeatureSealedDataPlane,
                    RepositoryDescriptorCodec.FeatureReclaimAuthority,
                ],
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
            credential.Clone(),
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
    public static async ValueTask<OpenedRepository> OpenAsync(
        IObjectStore store,
        RepositoryWriteCredential credential,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        try
        {
            var opened = await OpenCoreAsync(store, credential, cancellationToken).ConfigureAwait(false);
            Log.RepositoryOpened(log, opened.RepositoryId, opened.Descriptor.FormatVersion);
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
    private static async ValueTask<OpenedRepository> OpenCoreAsync(
        IObjectStore store,
        RepositoryWriteCredential credential,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(credential);

        var descriptor = await ReadDescriptorAsync(store, cancellationToken).ConfigureAwait(false);

        if (!credential.SealingPublicKey.SequenceEqual(descriptor.SealingPublicKey.Span))
        {
            throw new RepositoryOpenException(Strings.RepositoryLifecycle_CredentialNotThisRepository);
        }

        return new OpenedRepository(
            descriptor,
            RepositoryKeySet.FromWriteCredential(credential),
            credential.Clone(),
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
    public static async ValueTask<(OpenedRepository Repository, RepositoryReadAuthority Authority)> OpenForReadAsync(
        IObjectStore store,
        Passphrase passphrase,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        try
        {
            var opened = await OpenForReadCoreAsync(store, passphrase, cancellationToken)
                .ConfigureAwait(false);
            Log.RepositoryOpened(
                log, opened.Repository.RepositoryId, opened.Repository.Descriptor.FormatVersion);
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
    private static async ValueTask<(OpenedRepository Repository, RepositoryReadAuthority Authority)> OpenForReadCoreAsync(
        IObjectStore store,
        Passphrase passphrase,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(passphrase);

        var descriptor = await ReadDescriptorAsync(store, cancellationToken).ConfigureAwait(false);

        if (!TryDeriveReadAuthority(descriptor, passphrase, out var authority))
        {
            throw new KeyUnwrapFailedException(Strings.RepositoryLifecycle_PassphraseDoesNotReproduce);
        }

        try
        {
            var opened = new OpenedRepository(
                descriptor,
                RepositoryKeySet.FromWriteCredential(authority!.Credential),
                authority.Credential.Clone(),
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
