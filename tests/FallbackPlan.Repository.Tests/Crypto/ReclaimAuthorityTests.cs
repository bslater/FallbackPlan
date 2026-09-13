using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Crypto;

/// <summary>
/// The reclaim key (ADR-0055; FR-GC-008): the authority that deletes, on its
/// own derivation domain, and deliberately absent from a write-only
/// repository's write credential.
/// </summary>
/// <remarks>
/// <para>
/// The split exists because a tombstone's signature <em>is</em> the
/// authorisation to delete ([specification 11 §3]), while a publication's
/// signature only says an append was authentic. A write-only service holds
/// the signing key by design (ADR-0042) and must not therefore hold the other
/// one.
/// </para>
/// <para>
/// What these do not establish, said here so nobody reads them as more:
/// against a fully compromised <b>v1</b> service the split proves nothing,
/// because a v1 service holds the master key and derives both keys from it.
/// ADR-0055 §3 states that limit; the destination-side retention floor is
/// what holds there.
/// </para>
/// </remarks>
[TestClass]
public sealed class ReclaimAuthorityTests
{
    private static readonly Argon2Parameters TinyParameters =
        new() { MemoryKiB = 64, Iterations = 1, Parallelism = 1 };

    private static byte[] Salt(byte seed) => [.. Enumerable.Repeat(seed, KekDerivation.SaltLength)];

    private static RepositoryReadAuthority DeriveAuthority(string passphraseText)
    {
        using var passphrase = Passphrase.Create(passphraseText);
        return WriteOnlyDerivation.Derive(passphrase, TinyParameters, Salt(0x21), KdfValidationMode.OpenRepository);
    }

    [TestMethod]
    public void Reclaim_AndSigning_AreIndependentDomainsOfTheSameMaster()
    {
        var master = new byte[KeyHierarchy.DerivedKeyLength];
        RandomNumberGenerator.Fill(master);
        using var hierarchy = new KeyHierarchy(master);

        var signing = hierarchy.DeriveSigningKeySeed(new KeyGeneration(4));
        var reclaim = hierarchy.DeriveReclaimKeySeed(new KeyGeneration(4));

        // One master, two one-way domains. If these ever coincided the whole
        // decision would be a rename.
        Assert.AreEqual(KeyHierarchy.DerivedKeyLength, reclaim.Length);
        Assert.IsFalse(
            signing.AsSpan().SequenceEqual(reclaim),
            "the reclaim key must not be the signing key under another name");
    }

    [TestMethod]
    public void Reclaim_IsGenerational_LikeEveryOtherDerivedKey()
    {
        var master = new byte[KeyHierarchy.DerivedKeyLength];
        RandomNumberGenerator.Fill(master);
        using var hierarchy = new KeyHierarchy(master);

        Assert.IsFalse(
            hierarchy.DeriveReclaimKeySeed(new KeyGeneration(1)).AsSpan()
                .SequenceEqual(hierarchy.DeriveReclaimKeySeed(new KeyGeneration(2))),
            "a reclaim key that ignored the generation would survive a rotation that was meant to retire it");
    }

    [TestMethod]
    public void Reclaim_FromTheSameMaster_IsDeterministic()
    {
        var master = new byte[KeyHierarchy.DerivedKeyLength];
        RandomNumberGenerator.Fill(master);
        using var first = new KeyHierarchy(master);
        using var second = new KeyHierarchy(master);

        SequenceAssert.AreEqual(
            first.DeriveReclaimKeySeed(new KeyGeneration(7)),
            second.DeriveReclaimKeySeed(new KeyGeneration(7)));
    }

    [TestMethod]
    public void Reclaim_AWriteOnlyCredential_CannotDeriveOneAtAll()
    {
        // The decision, in one assertion. A service provisioned with the write
        // bundle can publish for ever and cannot author a deletion, because
        // the bundle does not carry the domain a tombstone signs under.
        using var authority = DeriveAuthority("one long passphrase to rule them");
        using var hierarchy = KeyHierarchy.ForWriteOnly(authority.Credential);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => hierarchy.DeriveReclaimKeySeed(new KeyGeneration(1)));

        // And the signing key is still there, or the service could not do its
        // job — which would make this a denial of service rather than a split.
        Assert.HasCount(
            KeyHierarchy.DerivedKeyLength, hierarchy.DeriveSigningKeySeed(new KeyGeneration(1)));
    }

    [TestMethod]
    public void Reclaim_TheSerialisedWriteCredential_CarriesNoTraceOfIt()
    {
        // The bundle crosses a process boundary when a service is provisioned
        // (ADR-0042 §4). If the reclaim seed rode along inside it, withholding
        // it from the derivation would be theatre.
        using var authority = DeriveAuthority("one long passphrase to rule them");
        var bundle = authority.Credential.ToBytes();
        try
        {
            var reclaim = authority.ReclaimKeySeed.ToArray();
            Assert.HasCount(KeyHierarchy.DerivedKeyLength, reclaim);

            for (var offset = 0; offset + reclaim.Length <= bundle.Length; offset++)
            {
                Assert.IsFalse(
                    bundle.AsSpan(offset, reclaim.Length).SequenceEqual(reclaim),
                    "the write credential must not carry the reclaim seed anywhere in its bytes");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bundle);
        }
    }

    [TestMethod]
    public void Reclaim_ThePassphrase_StillReachesItThroughTheReadAuthority()
    {
        // Withheld from the credential, not from the person. The read
        // authority is what a restore grant already carries (ADR-0042 §5) and
        // is where the reclaim grant of ADR-0055 §6 finds its scalar.
        using var first = DeriveAuthority("one long passphrase to rule them");
        using var second = DeriveAuthority("one long passphrase to rule them");

        SequenceAssert.AreEqual(first.ReclaimKeySeed.ToArray(), second.ReclaimKeySeed.ToArray());

        using var other = DeriveAuthority("a different passphrase entirely!");
        Assert.IsFalse(
            first.ReclaimKeySeed.SequenceEqual(other.ReclaimKeySeed),
            "two passphrases must not reach the same reclaim key");
    }

    [TestMethod]
    public void Reclaim_TheSealingScalarAndTheReclaimSeed_AreDifferentSecrets()
    {
        using var authority = DeriveAuthority("one long passphrase to rule them");

        Assert.IsFalse(
            authority.SealingPrivateKey.SequenceEqual(authority.ReclaimKeySeed),
            "reading content and authorising deletion are different powers and must be different keys");
    }
}
