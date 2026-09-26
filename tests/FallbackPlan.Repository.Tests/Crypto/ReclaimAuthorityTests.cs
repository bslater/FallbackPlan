using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Crypto;

/// <summary>
/// The reclaim key (ADR-0055; FR-GC-008): the authority that deletes, on its
/// own derivation domain, and deliberately absent from the repository's
/// write credential.
/// </summary>
/// <remarks>
/// The split exists because a tombstone's signature <em>is</em> the
/// authorisation to delete ([specification 11 §3]), while a publication's
/// signature only says an append was authentic. A service holds the signing
/// key by design (ADR-0042) and must not therefore hold the other one; the
/// reclaim seed reaches a collection run only as a grant the passphrase
/// derives (ADR-0055 §6).
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
    public void Reclaim_AndSigning_AreIndependentDomainsOfTheSameRoot()
    {
        using var authority = DeriveAuthority("one long passphrase to rule them");
        using var grant = new ReclaimAuthority(authority.ReclaimKeySeed);

        var signing = authority.Credential.DeriveSigningKeySeed(new KeyGeneration(4));
        var reclaim = grant.SeedFor(new KeyGeneration(4));

        // One root, two one-way domains. If these ever coincided the whole
        // decision would be a rename.
        Assert.AreEqual(RepositoryWriteCredential.DerivedKeyLength, reclaim.Length);
        Assert.IsFalse(
            signing.AsSpan().SequenceEqual(reclaim),
            "the reclaim key must not be the signing key under another name");
    }

    [TestMethod]
    public void Reclaim_IsGenerational_LikeEveryOtherDerivedKey()
    {
        using var authority = DeriveAuthority("one long passphrase to rule them");
        using var grant = new ReclaimAuthority(authority.ReclaimKeySeed);

        Assert.IsFalse(
            grant.SeedFor(new KeyGeneration(1)).AsSpan().SequenceEqual(grant.SeedFor(new KeyGeneration(2))),
            "a reclaim key that ignored the generation would survive a rotation that was meant to retire it");
    }

    [TestMethod]
    public void Reclaim_FromTheSamePassphraseAndSalt_IsDeterministic()
    {
        using var first = DeriveAuthority("one long passphrase to rule them");
        using var second = DeriveAuthority("one long passphrase to rule them");
        using var firstGrant = new ReclaimAuthority(first.ReclaimKeySeed);
        using var secondGrant = new ReclaimAuthority(second.ReclaimKeySeed);

        SequenceAssert.AreEqual(
            firstGrant.SeedFor(new KeyGeneration(7)),
            secondGrant.SeedFor(new KeyGeneration(7)));
    }

    [TestMethod]
    public void Reclaim_TheWriteCredential_CannotDeriveOneAtAll()
    {
        // The decision, in one assertion. A service provisioned with the write
        // bundle can publish for ever and cannot author a deletion, because
        // the bundle does not carry the domain a tombstone signs under — and
        // since format 1 went, no hierarchy anywhere derives it: the member
        // does not exist to be called.
        using var authority = DeriveAuthority("one long passphrase to rule them");
        using var credential = authority.Credential.Clone();

        Assert.IsNull(typeof(RepositoryWriteCredential).GetMethod("DeriveReclaimKeySeed"));
        Assert.IsNull(typeof(RepositoryWriteCredential).GetMethod("DeriveReclaimKeySeed"));

        // And the signing key is still there, or the service could not do its
        // job — which would make this a denial of service rather than a split.
        Assert.HasCount(
            RepositoryWriteCredential.DerivedKeyLength, credential.DeriveSigningKeySeed(new KeyGeneration(1)));
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
            Assert.HasCount(RepositoryWriteCredential.DerivedKeyLength, reclaim);

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

    [TestMethod]
    public void Reclaim_APreReclaimCredentialRoundTripped_StillPublishesNoKeyAtAll()
    {
        // A credential written before the reclaim key carries five members
        // and is read back with the sixth ABSENT. Serialising it again used
        // to write the six-member shape with that slot left zero — so the
        // round trip invented a 32-byte all-zero "published key" out of
        // nothing.
        //
        // The round trip is not hypothetical: RepositoryWriteCredential.Clone
        // does one on every open. The invented key would then ride every
        // ReplicationOffer, be recorded permanently at first attribution
        // (ADR-0055 §5, never replaceable), and leave the destination
        // demanding signatures under a key no one holds the private half of.
        // Retention for that set would stop, and there would be no way back.
        var legacy = new byte[168];
        "FBPWCRD1"u8.CopyTo(legacy);
        RandomNumberGenerator.Fill(legacy.AsSpan(8));

        using var parsed = RepositoryWriteCredential.FromBytes(legacy);
        Assert.IsTrue(parsed.ReclaimPublicKey.IsEmpty, "the parse itself is not where this goes wrong");

        using var reserialized = RepositoryWriteCredential.FromBytes(parsed.ToBytes());
        Assert.IsTrue(
            reserialized.ReclaimPublicKey.IsEmpty,
            "a credential that published no reclaim key must not acquire an all-zero one by being re-read");

        using var credential = parsed.Clone();
        Assert.IsEmpty(
            credential.ReclaimPublicKey.ToArray(),
            "publishing zeros to a peer is worse than publishing nothing — the peer keeps them for ever");
    }

    [TestMethod]
    public void Grant_TheSeedItHandsOut_IsGenerational()
    {
        using var authority = DeriveAuthority("one long passphrase to rule them");
        using var grant = new ReclaimAuthority(authority.ReclaimKeySeed);

        var first = grant.SeedFor(new KeyGeneration(1));
        var second = grant.SeedFor(new KeyGeneration(2));

        // The sub-root is one value; what signs is expanded per generation,
        // exactly as the write credential expands its signing sub-root. A
        // grant that ignored the generation would outlive the rotation meant
        // to retire it.
        Assert.IsFalse(first.AsSpan().SequenceEqual(second));
        SequenceAssert.AreEqual(first, grant.SeedFor(new KeyGeneration(1)));
    }

    [TestMethod]
    public void Grant_AfterTheRunEnds_HandsOutNothing()
    {
        // The lifetime is as much the decision as the key: a service
        // compromised between runs must hold nothing that authorises a
        // deletion (ADR-0055 §6).
        using var authority = DeriveAuthority("one long passphrase to rule them");
        var grant = new ReclaimAuthority(authority.ReclaimKeySeed);
        grant.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => grant.SeedFor(new KeyGeneration(1)));
    }

    [TestMethod]
    public void Grant_TheWrongPassphrasesGrant_DoesNotVerifyTheRepositorysTombstone()
    {
        // How a wrong grant is caught before it writes anything: a tombstone
        // the repository already holds was signed under the real key, so a
        // grant that cannot verify it is not this repository's.
        using var real = DeriveAuthority("one long passphrase to rule them");
        using var wrong = DeriveAuthority("a different passphrase entirely!");
        var generation = new KeyGeneration(3);

        using var realGrant = new ReclaimAuthority(real.ReclaimKeySeed);
        using var wrongGrant = new ReclaimAuthority(wrong.ReclaimKeySeed);

        var payload = "a tombstone already on disk"u8.ToArray();
        var seed = realGrant.SeedFor(generation);
        byte[] signature;
        try
        {
            using var signer = RepositorySigner.FromSeed(seed, generation);
            signature = signer.Sign(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }

        Assert.IsTrue(realGrant.Verifies(payload, signature, generation));
        Assert.IsFalse(
            wrongGrant.Verifies(payload, signature, generation),
            "a grant from another passphrase must be rejected before it authors anything");
    }

    [TestMethod]
    public void Grant_AShortRoot_IsRefusedRatherThanPadded()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ReclaimAuthority(new byte[16]));
    }
}
