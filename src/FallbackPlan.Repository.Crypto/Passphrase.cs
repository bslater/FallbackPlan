using Bodu;
using System.Security.Cryptography;
using System.Text;
using FallbackPlan.Repository.Crypto.Resources;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// A repository passphrase, held as the exact bytes the KDF consumes:
/// UTF-8, NFC-normalised, with no trailing newline (specification 03 §2).
/// NFC normalisation exists because the same passphrase typed on different
/// operating systems can differ in Unicode composition; without it, a
/// repository created on one machine will not open on another.
/// </summary>
/// <remarks>
/// An empty passphrase is refused here, at the engine level: Argon2id itself
/// accepts a zero-length password, so refusing is this type's job, not the
/// primitive's (specification 03 §2.1). <b>Nothing further is refused here,
/// deliberately.</b> A minimum length is policy, it is decided (ADR-0044 §6,
/// settling open question Q14), and it is enforced where a passphrase is
/// <em>chosen</em> rather than here, where one is <em>used</em> — this
/// constructor is on the restore path, and a policy tightened on it would
/// refuse to open repositories that were created legitimately under the older
/// rule.
/// </remarks>
public sealed class Passphrase : IDisposable
{
    /// <summary>
    /// The minimum length policy enforces at setup, forwarded from its single
    /// source so the two cannot drift
    /// (<see cref="Domain.Configuration.PassphraseStrength.MinimumLength"/>).
    /// Not enforced <em>here</em>: only the empty passphrase is refused, which
    /// is the specification's MUST — see the remarks on this type for why the
    /// rest is applied elsewhere.
    /// </summary>
    public const int RecommendedMinimumLength = Domain.Configuration.PassphraseStrength.MinimumLength;

    private readonly byte[] _utf8;

    private Passphrase(byte[] utf8) => _utf8 = utf8;

    /// <summary>
    /// Creates a passphrase from user input, normalising to NFC and encoding
    /// as UTF-8.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty (specification 03 §2.1).</exception>
    public static Passphrase Create(string value)
    {
        ThrowHelper.ThrowIfNull(value);

        if (value.Length == 0)
        {
            throw new ArgumentException(Strings.Passphrase_EmptyPassphraseRefusedRepositoryCreation,
                nameof(value));
        }

        var normalised = value.IsNormalized(NormalizationForm.FormC)
            ? value
            : value.Normalize(NormalizationForm.FormC);

        return new Passphrase(Encoding.UTF8.GetBytes(normalised));
    }

    /// <summary>The exact bytes the KDF consumes.</summary>
    public ReadOnlySpan<byte> Utf8 => _utf8;

    /// <summary>
    /// A copy with its own buffer and its own lifetime, for a holder that
    /// outlives the caller's — the service keeps one so it can open and
    /// create per-set archives lazily (ADR-0028 §9, ADR-0034).
    /// </summary>
    /// <returns>An independent passphrase the caller owns and must dispose.</returns>
    public Passphrase Clone() => new((byte[])_utf8.Clone());

    /// <summary>
    /// Deliberately redacted: a passphrase must never reach a log, crash dump,
    /// or durable object (specification 03 §8).
    /// </summary>
    public override string ToString() => "passphrase(redacted)";

    /// <summary>Zeroes the held passphrase bytes.</summary>
    public void Dispose() => CryptographicOperations.ZeroMemory(_utf8);
}
