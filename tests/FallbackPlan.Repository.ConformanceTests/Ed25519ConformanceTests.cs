using System.Text.Json;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto;
using Xunit;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Drives every <c>ed25519.json</c> case through the engine's signer
/// (specification 06 §6.1, 03 §4; ADR-0020, ADR-0022): the RFC 8032 §7.1
/// vectors prove the primitive, the format-real cases prove the seed
/// interpretation — the HKDF output is an RFC 8032 §5.1.5 seed, and the
/// public key is computed from it, never distributed.
/// </summary>
public sealed class Ed25519ConformanceTests
{
    private static JsonDocument Vectors { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "ed25519.json")));

    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    [Fact]
    public void Every_rfc8032_case_reproduces_public_key_and_signature()
    {
        foreach (var vectorCase in Vectors.RootElement.GetProperty("rfc8032_cases").EnumerateArray())
        {
            var seed = Convert.FromHexString(vectorCase.GetProperty("seed").GetString()!);
            var message = Convert.FromHexString(vectorCase.GetProperty("message").GetString()!);
            var expectedPublic = vectorCase.GetProperty("public_key").GetString();
            var expectedSignature = vectorCase.GetProperty("signature").GetString();

            using var signer = RepositorySigner.FromSeed(seed, KeyGeneration.Zero);

            Assert.Equal(expectedPublic, Convert.ToHexStringLower(signer.PublicKey));
            Assert.Equal(expectedSignature, Convert.ToHexStringLower(signer.Sign(message)));
        }
    }

    [Fact]
    public void Every_format_case_reproduces_from_the_derived_seed()
    {
        using var hierarchy = new KeyHierarchy(MasterKey);

        foreach (var vectorCase in Vectors.RootElement.GetProperty("format_cases").EnumerateArray())
        {
            var generation = new KeyGeneration((uint)vectorCase.GetProperty("generation").GetInt32());
            var message = Convert.FromHexString(vectorCase.GetProperty("message").GetString()!);

            // The engine derives the seed itself — the vector's pinned seed
            // only cross-checks the derivation.
            Assert.Equal(
                vectorCase.GetProperty("seed").GetString(),
                Convert.ToHexStringLower(hierarchy.DeriveSigningKeySeed(generation)));

            using var signer = RepositorySigner.Create(hierarchy, generation);

            Assert.Equal(
                vectorCase.GetProperty("public_key").GetString(),
                Convert.ToHexStringLower(signer.PublicKey));
            Assert.Equal(
                vectorCase.GetProperty("signature").GetString(),
                Convert.ToHexStringLower(signer.Sign(message)));
        }
    }

    [Fact]
    public void A_signature_verifies_and_any_tampered_byte_is_rejected()
    {
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var signer = RepositorySigner.Create(hierarchy, KeyGeneration.Zero);

        var message = "FallbackPlan tamper probe"u8.ToArray();
        var signature = signer.Sign(message);

        Assert.True(signer.Verify(message, signature));

        var tamperedMessage = (byte[])message.Clone();
        tamperedMessage[0] ^= 0x01;
        Assert.False(signer.Verify(tamperedMessage, signature));

        var tamperedSignature = (byte[])signature.Clone();
        tamperedSignature[0] ^= 0x01;
        Assert.False(signer.Verify(message, tamperedSignature));

        Assert.False(signer.Verify(message, signature.AsSpan(0, 63)));
    }

    [Fact]
    public void Different_generations_derive_different_keypairs()
    {
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var zero = RepositorySigner.Create(hierarchy, KeyGeneration.Zero);
        using var one = RepositorySigner.Create(hierarchy, new KeyGeneration(1));

        Assert.False(zero.PublicKey.SequenceEqual(one.PublicKey));

        // A generation-0 signature must not verify under generation 1 — the
        // generation descent used for journal records (08 §2 key 6) depends
        // on that separation to terminate at the right key.
        var message = "generation separation"u8.ToArray();
        Assert.False(one.Verify(message, zero.Sign(message)));
    }
}
