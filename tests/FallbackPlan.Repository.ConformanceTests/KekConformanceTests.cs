using System.Text.Json;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using Xunit;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Drives <c>argon2id.json</c> through the engine's real KEK path — string
/// passphrase in, <see cref="Passphrase"/> normalisation,
/// <see cref="KekDerivation"/> out — rather than through the raw primitive,
/// which <c>Argon2idCrossVerificationTests</c> already covers (specification
/// 03 §2; FR-ARCH-008).
/// </summary>
public sealed class KekConformanceTests
{
    [Fact]
    public void The_engine_kek_path_reproduces_the_pinned_vector()
    {
        using var vectors = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "argon2id.json")));
        var vectorCase = vectors.RootElement.GetProperty("cases").EnumerateArray().Single();

        Assert.Equal("mandated_minimum_parameters", vectorCase.GetProperty("name").GetString());

        using var passphrase = Passphrase.Create(vectorCase.GetProperty("password_utf8").GetString()!);
        var parameters = new Argon2Parameters
        {
            MemoryKiB = vectorCase.GetProperty("memory_kib").GetUInt32(),
            Iterations = vectorCase.GetProperty("iterations").GetUInt32(),
            Parallelism = vectorCase.GetProperty("parallelism").GetByte(),
        };
        var salt = Convert.FromHexString(vectorCase.GetProperty("salt").GetString()!);

        Assert.Equal(32u, vectorCase.GetProperty("tag_length").GetUInt32());

        using var result = KekDerivation.Derive(passphrase, parameters, salt, KdfValidationMode.CreateRepository);

        Assert.False(result.BelowCreationMinimums);
        Assert.Equal(
            vectorCase.GetProperty("tag").GetString(),
            Convert.ToHexStringLower(result.Kek.Bytes.ToArray()));
    }
}
