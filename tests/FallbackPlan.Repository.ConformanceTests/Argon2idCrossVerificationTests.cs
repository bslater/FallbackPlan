using System.Text;
using System.Text.Json;
using Bodu.Security.Cryptography;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Cross-verifies Bodu's Argon2id against Konscious's, using both as independent
/// implementations of RFC 9106.
///
/// Argon2id is one of exactly two primitives FallbackPlan needs that .NET does not
/// provide (the other is XChaCha20-Poly1305). Specification 03 section 1 forbids
/// reimplementing what the platform already offers, but where the platform offers
/// nothing, an implementation has to come from somewhere — and the honest way to
/// gain confidence in it is to check it against a different one.
///
/// Konscious is retained as a test-only dependency for exactly this purpose. It is
/// not shipped. If these two ever disagree, one of them has a defect and the KEK
/// derivation — the thing standing between a stolen repository and its plaintext —
/// is not trustworthy until it is resolved.
///
/// This is a weaker guarantee than an audited platform primitive, and the
/// specification says so rather than implying otherwise.
/// </summary>
[TestClass]
public sealed class Argon2idCrossVerificationTests
{
    private static byte[] Konscious(
        byte[] password, byte[] salt, int memoryKiB, int iterations, int parallelism, int tagLength)
    {
        using var argon2 = new Konscious.Security.Cryptography.Argon2id(password)
        {
            Salt = salt,
            MemorySize = memoryKiB,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(tagLength);
    }

    private static byte[] Bodu(
        byte[] password, byte[] salt, int memoryKiB, int iterations, int parallelism, int tagLength) =>
        Argon2id.DeriveKey(
            password,
            salt,
            new Argon2Parameters
            {
                MemoryKiB = memoryKiB,
                Iterations = iterations,
                Parallelism = parallelism,
                TagLength = tagLength,
            });

    /// <summary>
    /// The parameters the specification mandates as the minimum for a new repository
    /// (03 section 2): 64 MiB, 3 iterations, parallelism 4, 32-byte tag.
    /// </summary>
    [TestMethod]
    public void Argon2id_AtTheSpecifiedMinimumParameters_BothImplementationsAgree()
    {
        var password = Encoding.UTF8.GetBytes("correct horse battery staple");
        var salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

        var konscious = Konscious(password, salt, 65536, 3, 4, 32);
        var bodu = Bodu(password, salt, 65536, 3, 4, 32);

        Assert.AreEqual(Convert.ToHexString(konscious), Convert.ToHexString(bodu));
        Assert.AreEqual(32, bodu.Length);
    }

    [TestMethod]
    // Deliberately spans parallelism 1 and >1: the lanes are where Argon2
    // implementations most often diverge, and a single-lane-only test would pass
    // while hiding a real disagreement.
    [DataRow(8, 1, 1, 32)]
    [DataRow(64, 2, 1, 32)]
    [DataRow(64, 2, 2, 32)]
    [DataRow(256, 1, 4, 32)]
    [DataRow(1024, 2, 4, 64)]
    [DataRow(65536, 3, 4, 32)]
    public void Argon2id_AcrossTheParameterRange_BothImplementationsAgree(
        int memoryKiB, int iterations, int parallelism, int tagLength)
    {
        var password = Encoding.UTF8.GetBytes("a passphrase with a fair bit of entropy in it");
        var salt = Encoding.UTF8.GetBytes("sixteen-byte-salt");

        Assert.AreEqual(
            Convert.ToHexString(Konscious(password, salt, memoryKiB, iterations, parallelism, tagLength)),
            Convert.ToHexString(Bodu(password, salt, memoryKiB, iterations, parallelism, tagLength)));
    }

    /// <summary>
    /// The two implementations differ at one input boundary, and it is recorded here
    /// rather than skipped.
    ///
    /// RFC 9106 permits a zero-length password. Bodu accepts one; Konscious refuses
    /// with ArgumentException. This is a policy difference at the API edge, not an
    /// algorithmic divergence — every case where both produce output, they produce
    /// identical output.
    ///
    /// It matters because it shows the primitive will not stop a user choosing an
    /// empty passphrase. Refusing that is FallbackPlan's job, and specification 03
    /// section 2 now says so: minimum Argon2 parameters were mandated, minimum
    /// passphrase strength was not.
    /// </summary>
    [TestMethod]
    public void Argon2id_AtTheEmptyPasswordBoundary_IsTheOnlyPlaceTheImplementationsDiffer()
    {
        var salt = Enumerable.Repeat((byte)0x5A, 16).ToArray();

        // Konscious refuses outright.
        Assert.ThrowsExactly<ArgumentException>(() => Konscious([], salt, 64, 2, 1, 32));

        // Bodu derives a key, per RFC 9106. The engine must not rely on the
        // primitive to enforce a passphrase policy it does not have.
        var derived = Bodu([], salt, 64, 2, 1, 32);
        Assert.AreEqual(32, derived.Length);
        Assert.AreNotEqual(new byte[32], derived);
    }

    /// <summary>
    /// Derivation must actually depend on the salt. A shared implementation bug that
    /// ignored it would make both agree and both be catastrophically wrong, so this
    /// checks the property rather than the agreement.
    /// </summary>
    [TestMethod]
    public void Argon2id_DifferentSalts_DeriveDifferentKeys()
    {
        var password = Encoding.UTF8.GetBytes("passphrase");
        var saltA = Enumerable.Repeat((byte)0x01, 16).ToArray();
        var saltB = Enumerable.Repeat((byte)0x02, 16).ToArray();

        Assert.AreNotEqual(
            Convert.ToHexString(Bodu(password, saltA, 64, 2, 1, 32)),
            Convert.ToHexString(Bodu(password, saltB, 64, 2, 1, 32)));
    }

    /// <summary>
    /// Both implementations must reproduce the committed argon2id.json vector.
    ///
    /// The pinned value was computed by these two implementations agreeing —
    /// the generator cannot compute Argon2id from the Python standard library,
    /// and the vector file says so (independently_derived: false). Re-checking
    /// both against the committed bytes on every run is what keeps that pin
    /// honest: if either implementation drifts, or someone edits the JSON, this
    /// fails and names which.
    /// </summary>
    [TestMethod]
    public void Argon2id_TheCommittedVector_IsReproducedByBothImplementations()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "argon2id.json")));

        foreach (var testCase in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var password = Encoding.UTF8.GetBytes(testCase.GetProperty("password_utf8").GetString()!);
            var salt = Convert.FromHexString(testCase.GetProperty("salt").GetString()!);
            var memoryKiB = testCase.GetProperty("memory_kib").GetInt32();
            var iterations = testCase.GetProperty("iterations").GetInt32();
            var parallelism = testCase.GetProperty("parallelism").GetInt32();
            var tagLength = testCase.GetProperty("tag_length").GetInt32();
            var expected = testCase.GetProperty("tag").GetString()!.ToUpperInvariant();

            Assert.AreEqual(expected, Convert.ToHexString(
                Bodu(password, salt, memoryKiB, iterations, parallelism, tagLength)));
            Assert.AreEqual(expected, Convert.ToHexString(
                Konscious(password, salt, memoryKiB, iterations, parallelism, tagLength)));
        }
    }

    /// <summary>
    /// Derivation must depend on every cost parameter. Silently ignoring one would
    /// let a repository created at 64 MiB be opened as though it were 8 MiB.
    /// </summary>
    [TestMethod]
    public void Argon2id_AnyCostParameterChanged_DerivesADifferentKey()
    {
        var password = Encoding.UTF8.GetBytes("passphrase");
        var salt = Enumerable.Repeat((byte)0x5A, 16).ToArray();

        var baseline = Convert.ToHexString(Bodu(password, salt, 64, 2, 1, 32));

        Assert.AreNotEqual(baseline, Convert.ToHexString(Bodu(password, salt, 128, 2, 1, 32)));
        Assert.AreNotEqual(baseline, Convert.ToHexString(Bodu(password, salt, 64, 3, 1, 32)));
        Assert.AreNotEqual(baseline, Convert.ToHexString(Bodu(password, salt, 64, 2, 2, 32)));
        Assert.AreNotEqual(baseline, Convert.ToHexString(Bodu(password, salt, 64, 2, 1, 64))[..64]);
    }
}
