using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Format.Descriptor;

/// <summary>
/// The repository descriptor's logical content (specification 01 §3): the
/// only object with a fixed key, the only object a reader can locate without
/// prior knowledge, and deliberately unencrypted — it carries what a reader
/// needs <em>before</em> it holds any key, and nothing else.
/// </summary>
/// <param name="RepositoryId">Random at creation, never reused (key 1).</param>
/// <param name="FormatVersion">Repeated inside the digest-covered body as a corruption check (key 2).</param>
/// <param name="RequiredFeatures">Feature identifiers a reader MUST implement to proceed (key 3).</param>
/// <param name="OptionalFeatures">Feature identifiers a reader may ignore (key 4).</param>
/// <param name="KdfParameters">The public Argon2id parameters (key 5; specification 01 §3.3).</param>
/// <param name="KdfSalt">The public 16-byte KDF salt — not a secret (01 §3.3).</param>
/// <param name="CreatedAt">Informational creation timestamp, milliseconds since the epoch (key 6).</param>
/// <param name="CreatedBy">Informational implementation name and version (key 7).</param>
/// <param name="UnstableFormat">True while the format is unfrozen — readers surface a prominent warning (key 8).</param>
public sealed record RepositoryDescriptor(
    RepositoryId RepositoryId,
    ushort FormatVersion,
    IReadOnlyList<ushort> RequiredFeatures,
    IReadOnlyList<ushort> OptionalFeatures,
    Argon2Parameters KdfParameters,
    ReadOnlyMemory<byte> KdfSalt,
    ulong CreatedAt,
    string CreatedBy,
    bool UnstableFormat);

/// <summary>
/// The outcome of parsing a candidate descriptor — each refusal is a
/// distinct finding, because "not a FallbackPlan repository" and "a damaged
/// FallbackPlan repository" call for entirely different user action
/// (specification 01 §3.1).
/// </summary>
public abstract record DescriptorParseResult
{
    private DescriptorParseResult()
    {
    }

    /// <summary>The object does not begin with the magic — not a FallbackPlan repository descriptor at all.</summary>
    public sealed record NotARepository : DescriptorParseResult;

    /// <summary>
    /// The digest does not cover the bytes — accidental corruption
    /// (the digest is unkeyed and defends against nothing deliberate).
    /// </summary>
    public sealed record IntegrityFailure : DescriptorParseResult;

    /// <summary>The framing or body violates the specification; the message names the rule.</summary>
    public sealed record FormatViolation(string Message) : DescriptorParseResult;

    /// <summary>
    /// The descriptor is intact but requires features this reader does not
    /// implement — refused with the identifiers named, never proceeded past
    /// (specification 01 §3.2).
    /// </summary>
    public sealed record UnsupportedRequiredFeatures(IReadOnlyList<ushort> Features) : DescriptorParseResult;

    /// <summary>The descriptor parsed and verified.</summary>
    public sealed record Ok(RepositoryDescriptor Descriptor) : DescriptorParseResult;
}
