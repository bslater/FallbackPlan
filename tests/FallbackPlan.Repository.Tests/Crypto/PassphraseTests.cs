using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.Tests.Crypto;

/// <summary>
/// Exercises passphrase construction (specification 03 §2, §2.1): the
/// empty-passphrase refusal that is the engine's job, NFC normalisation, and
/// redaction.
/// </summary>
public sealed class PassphraseTests
{
    [Fact]
    public void Empty_passphrase_is_rejected_at_repository_creation()
    {
        // The acceptance criterion, verbatim: Argon2id itself accepts a
        // zero-length password, so refusing is the engine's job.
        Assert.Throws<ArgumentException>(() => Passphrase.Create(string.Empty));
    }

    [Fact]
    public void Composed_and_decomposed_input_produce_identical_bytes()
    {
        // "café" with a precomposed é (U+00E9) and with e + combining acute
        // (U+0301): the same passphrase typed on different operating systems.
        using var composed = Passphrase.Create("café");
        using var decomposed = Passphrase.Create("café");

        Assert.True(composed.Utf8.SequenceEqual(decomposed.Utf8));
    }

    [Fact]
    public void Ascii_input_is_utf8_verbatim_with_no_trailing_newline()
    {
        using var passphrase = Passphrase.Create("correct horse battery staple");

        Assert.Equal("correct horse battery staple"u8.ToArray(), passphrase.Utf8.ToArray());
    }

    [Fact]
    public void Rendering_is_redacted()
    {
        using var passphrase = Passphrase.Create("secret words");

        Assert.DoesNotContain("secret", passphrase.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redacted", passphrase.ToString(), StringComparison.Ordinal);
    }
}
