using System.Text.Json;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Drives every <c>ed25519.json</c> case through the engine's signer
/// (specification 06 §6.1, 03 §4; ADR-0020, ADR-0022): the RFC 8032 §7.1
/// vectors prove the primitive, the format-real cases prove the seed
/// interpretation — the HKDF output is an RFC 8032 §5.1.5 seed, and the
/// public key is computed from it, never distributed.
/// </summary>
[TestClass]
public sealed class Ed25519ConformanceTests
{
    private static JsonDocument Vectors { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "ed25519.json")));
    /// <summary>The pinned root every vector group derives from (write-only.json).</summary>
    private static byte[] Root =>
        Convert.FromHexString(
            JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "write-only.json")))
                .RootElement.GetProperty("inputs").GetProperty("root").GetString()!);

    [TestMethod]
    public void Ed25519_EveryRfc8032Case_ReproducesItsPublicKeyAndSignature()
    {
        foreach (var vectorCase in Vectors.RootElement.GetProperty("rfc8032_cases").EnumerateArray())
        {
            var seed = Convert.FromHexString(vectorCase.GetProperty("seed").GetString()!);
            var message = Convert.FromHexString(vectorCase.GetProperty("message").GetString()!);
            var expectedPublic = vectorCase.GetProperty("public_key").GetString();
            var expectedSignature = vectorCase.GetProperty("signature").GetString();

            using var signer = RepositorySigner.FromSeed(seed, KeyGeneration.Zero);

            Assert.AreEqual(expectedPublic, Convert.ToHexStringLower(signer.PublicKey));
            Assert.AreEqual(expectedSignature, Convert.ToHexStringLower(signer.Sign(message)));
        }
    }

    [TestMethod]
    public void Ed25519_EveryFormatCase_ReproducesFromItsDerivedSeed()
    {
        using var authority = WriteOnlyDerivation.FromRoot(Root);
        using var credential = authority.Credential.Clone();

        foreach (var vectorCase in Vectors.RootElement.GetProperty("format_cases").EnumerateArray())
        {
            var generation = new KeyGeneration((uint)vectorCase.GetProperty("generation").GetInt32());
            var message = Convert.FromHexString(vectorCase.GetProperty("message").GetString()!);

            // The engine derives the seed itself — the vector's pinned seed
            // only cross-checks the derivation.
            Assert.AreEqual(
                vectorCase.GetProperty("seed").GetString(),
                Convert.ToHexStringLower(credential.DeriveSigningKeySeed(generation)));

            using var signer = RepositorySigner.Create(credential, generation);

            Assert.AreEqual(
                vectorCase.GetProperty("public_key").GetString(),
                Convert.ToHexStringLower(signer.PublicKey));
            Assert.AreEqual(
                vectorCase.GetProperty("signature").GetString(),
                Convert.ToHexStringLower(signer.Sign(message)));
        }
    }

    [TestMethod]
    public void Ed25519_AnyTamperedByte_FailsVerification()
    {
        using var authority = WriteOnlyDerivation.FromRoot(Root);
        using var credential = authority.Credential.Clone();
        using var signer = RepositorySigner.Create(credential, KeyGeneration.Zero);

        var message = "FallbackPlan tamper probe"u8.ToArray();
        var signature = signer.Sign(message);

        Assert.IsTrue(signer.Verify(message, signature));

        var tamperedMessage = (byte[])message.Clone();
        tamperedMessage[0] ^= 0x01;
        Assert.IsFalse(signer.Verify(tamperedMessage, signature));

        var tamperedSignature = (byte[])signature.Clone();
        tamperedSignature[0] ^= 0x01;
        Assert.IsFalse(signer.Verify(message, tamperedSignature));

        Assert.IsFalse(signer.Verify(message, signature.AsSpan(0, 63)));
    }

    [TestMethod]
    public void Ed25519_DifferentKeyGenerations_DeriveDifferentKeyPairs()
    {
        using var authority = WriteOnlyDerivation.FromRoot(Root);
        using var credential = authority.Credential.Clone();
        using var zero = RepositorySigner.Create(credential, KeyGeneration.Zero);
        using var one = RepositorySigner.Create(credential, new KeyGeneration(1));

        Assert.IsFalse(zero.PublicKey.SequenceEqual(one.PublicKey));

        // A generation-0 signature must not verify under generation 1 — the
        // generation descent used for journal records (08 §2 key 6) depends
        // on that separation to terminate at the right key.
        var message = "generation separation"u8.ToArray();
        Assert.IsFalse(one.Verify(message, zero.Sign(message)));
    }
}
