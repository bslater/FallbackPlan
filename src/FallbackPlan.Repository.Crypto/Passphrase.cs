using Bodu;
using System.Security.Cryptography;
using System.Text;

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
/// primitive's (specification 03 §2.1). A minimum <em>length</em> is a policy
/// question tracked as Q14 in docs/open-questions.md;
/// <see cref="RecommendedMinimumLength"/> carries the working value.
/// </remarks>
public sealed class Passphrase : IDisposable
{
    /// <summary>
    /// The working minimum length the engine will recommend once passphrase
    /// policy is decided (open question Q14). Not yet enforced — only the
    /// empty passphrase is refused, which is the specification's MUST.
    /// </summary>
    public const int RecommendedMinimumLength = 12;

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
            throw new ArgumentException(
                "An empty passphrase is refused at repository creation. The primitive accepts one, so refusing is the engine's job (specification 03 §2.1).",
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
    /// Deliberately redacted: a passphrase must never reach a log, crash dump,
    /// or durable object (specification 03 §8).
    /// </summary>
    public override string ToString() => "passphrase(redacted)";

    /// <summary>Zeroes the held passphrase bytes.</summary>
    public void Dispose() => CryptographicOperations.ZeroMemory(_utf8);
}
