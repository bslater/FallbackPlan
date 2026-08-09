using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.Tests.Crypto;

/// <summary>
/// Exercises the KDF-parameter contract (specification 03 §2): creation
/// refuses below-minimum parameters with named defects; opening accepts them
/// and warns. The heavy parameter sets here are deliberately small — the
/// mandated minimums run in the conformance suite.
/// </summary>
[TestClass]
public sealed class KekDerivationTests
{
    private static readonly byte[] Salt = new byte[KekDerivation.SaltLength];

    [TestMethod]
    [DataRow(64u * 1024 - 1, 3u, (byte)4, "kdf_memory_below_creation_minimum")]
    [DataRow(64u * 1024, 2u, (byte)4, "kdf_iterations_below_creation_minimum")]
    [DataRow(64u * 1024, 3u, (byte)3, "kdf_parallelism_below_creation_minimum")]
    public void RepositoryCreation_KdfParametersBelowMinimum_RefusesNamingTheDefect(
        uint memoryKiB, uint iterations, byte parallelism, string expectedDefect)
    {
        using var passphrase = Passphrase.Create("a passphrase");
        var parameters = new Argon2Parameters { MemoryKiB = memoryKiB, Iterations = iterations, Parallelism = parallelism };

        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            KekDerivation.Derive(passphrase, parameters, Salt, KdfValidationMode.CreateRepository));

        Assert.Contains(expectedDefect, exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void RepositoryOpen_KdfParametersBelowMinimum_SucceedsWithAWarning()
    {
        // Stored parameters are facts about existing bytes: tiny values keep
        // this test fast while exercising the warn path.
        using var passphrase = Passphrase.Create("a passphrase");
        var parameters = new Argon2Parameters { MemoryKiB = 64, Iterations = 1, Parallelism = 1 };

        using var result = KekDerivation.Derive(passphrase, parameters, Salt, KdfValidationMode.OpenRepository);

        Assert.IsTrue(result.BelowCreationMinimums);
        Assert.AreEqual(KekDerivation.KekLength, result.Kek.Bytes.Length);
    }

    [TestMethod]
    public void KekDerivation_TheGeneratedSalt_IsExactlySixteenBytes()
    {
        using var passphrase = Passphrase.Create("a passphrase");

        Assert.ThrowsExactly<ArgumentException>(() =>
            KekDerivation.Derive(passphrase, Argon2Parameters.CreationMinimums, new byte[15], KdfValidationMode.OpenRepository));
    }

    [TestMethod]
    public void KekDerivation_DifferentSalts_DeriveDifferentKeys()
    {
        using var passphrase = Passphrase.Create("a passphrase");
        var parameters = new Argon2Parameters { MemoryKiB = 64, Iterations = 1, Parallelism = 1 };
        var otherSalt = new byte[KekDerivation.SaltLength];
        otherSalt[0] = 0x01;

        using var first = KekDerivation.Derive(passphrase, parameters, Salt, KdfValidationMode.OpenRepository);
        using var second = KekDerivation.Derive(passphrase, parameters, otherSalt, KdfValidationMode.OpenRepository);

        Assert.IsFalse(first.Kek.Bytes.SequenceEqual(second.Kek.Bytes));
    }
}
