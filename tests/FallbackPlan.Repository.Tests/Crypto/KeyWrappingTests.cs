using System.Security.Cryptography;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Crypto;

/// <summary>
/// Exercises key-bundle wrapping (specification 03 §3): round-trip, and the
/// single indistinguishable failure for every way an unwrap can go wrong.
/// </summary>
[TestClass]
public sealed class KeyWrappingTests
{
    private static int s_kekCounter;

    private static Kek CreateKek()
    {
        // A real derivation at deliberately tiny parameters: fast, and it
        // keeps Kek construction inside the crypto layer where it belongs.
        using var passphrase = Passphrase.Create($"test passphrase {Interlocked.Increment(ref s_kekCounter)}");
        var salt = new byte[KekDerivation.SaltLength];
        RandomNumberGenerator.Fill(salt);

        var result = KekDerivation.Derive(
            passphrase,
            new FallbackPlan.Domain.Configuration.Argon2Parameters { MemoryKiB = 64, Iterations = 1, Parallelism = 1 },
            salt,
            FallbackPlan.Domain.Configuration.KdfValidationMode.OpenRepository);

        return result.Kek;
    }

    [TestMethod]
    public void WrapThenUnwrap_TheSamePassphrase_RoundTripsTheKey()
    {
        using var kek = CreateKek();
        var nonce = new byte[KeyWrapping.NonceLength];
        var aad = "framing-aad"u8.ToArray();
        var bundle = "bundle cbor bytes"u8.ToArray();
        var ciphertext = new byte[bundle.Length];
        var tag = new byte[KeyWrapping.TagLength];

        KeyWrapping.Wrap(kek, nonce, aad, bundle, ciphertext, tag);

        SequenceAssert.AreEqual(bundle, KeyWrapping.Unwrap(kek, nonce, aad, ciphertext, tag));
    }

    [TestMethod]
    public void Unwrap_AnyWrongInput_FailsIndistinguishably()
    {
        using var kek = CreateKek();
        using var wrongKek = CreateKek();
        var nonce = new byte[KeyWrapping.NonceLength];
        var aad = "framing-aad"u8.ToArray();
        var bundle = "bundle cbor bytes"u8.ToArray();
        var ciphertext = new byte[bundle.Length];
        var tag = new byte[KeyWrapping.TagLength];

        KeyWrapping.Wrap(kek, nonce, aad, bundle, ciphertext, tag);

        var flippedCiphertext = (byte[])ciphertext.Clone();
        flippedCiphertext[0] ^= 0x01;
        var flippedTag = (byte[])tag.Clone();
        flippedTag[0] ^= 0x01;
        var differentAad = "framing-aae"u8.ToArray();

        var wrongPassphrase = Assert.ThrowsExactly<KeyUnwrapFailedException>(() =>
            KeyWrapping.Unwrap(wrongKek, nonce, aad, ciphertext, tag));
        var tamperedCiphertext = Assert.ThrowsExactly<KeyUnwrapFailedException>(() =>
            KeyWrapping.Unwrap(kek, nonce, aad, flippedCiphertext, tag));
        var tamperedTag = Assert.ThrowsExactly<KeyUnwrapFailedException>(() =>
            KeyWrapping.Unwrap(kek, nonce, aad, ciphertext, flippedTag));
        var renamedObject = Assert.ThrowsExactly<KeyUnwrapFailedException>(() =>
            KeyWrapping.Unwrap(kek, nonce, differentAad, ciphertext, tag));

        // Wrong passphrase and tampering are deliberately indistinguishable:
        // one message for all of them (specification 03 §3).
        Assert.AreEqual(wrongPassphrase.Message, tamperedCiphertext.Message);
        Assert.AreEqual(wrongPassphrase.Message, tamperedTag.Message);
        Assert.AreEqual(wrongPassphrase.Message, renamedObject.Message);
    }
}
