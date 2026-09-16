using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Crypto;

/// <summary>
/// The claim key ([ADR-0053](../../../docs/adr/0053-peer-claim-and-configuration-recovery.md);
/// FR-REP-001, FR-KIT-006): the authority a rebuilt machine proves its
/// ownership of a peer replica with, on a derivation domain of its own.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0053 §1 derived it from the repository master key. Building the drill
/// showed that unreachable: a machine claiming a replica has lost the
/// repository, and what it holds is an <em>installation</em> kit, which names
/// no repository. So the derivation is the installation's — the passphrase
/// and the installation's KDF salt, which is exactly what such a kit plus
/// its owner supply.
/// </para>
/// <para>
/// This is not a weakening. One passphrase already stamps every archive an
/// installation writes (ADR-0044), and an installation kit already opens
/// every one of them — "whoever holds this installation's kit and passphrase"
/// and "whoever can open this repository" name the same person.
/// </para>
/// <para>
/// What these do not establish: nothing here puts a claim on the wire. That
/// is the ceremony's own suite. These establish only that the key exists,
/// that it is reachable from the two things a claimant can still hold, and
/// that it is nobody else's key.
/// </para>
/// </remarks>
[TestClass]
public sealed class ClaimAuthorityTests
{
    private static readonly Argon2Parameters TinyParameters =
        new() { MemoryKiB = 64, Iterations = 1, Parallelism = 1 };

    private static byte[] Salt(byte seed) => [.. Enumerable.Repeat(seed, KekDerivation.SaltLength)];

    private static RepositoryReadAuthority Derive(string passphraseText, byte[] salt)
    {
        using var passphrase = Passphrase.Create(passphraseText);
        return WriteOnlyDerivation.Derive(passphrase, TinyParameters, salt, KdfValidationMode.OpenRepository);
    }

    [TestMethod]
    public void Claim_ThePassphraseAndTheSalt_ReproduceTheKeyExactly()
    {
        // The whole proposition: the two things that survive a dead machine —
        // the passphrase in someone's head and the salt printed on the kit —
        // are enough to reach the key again.
        using var first = Derive("one long passphrase to rule them", Salt(0x51));
        using var second = Derive("one long passphrase to rule them", Salt(0x51));

        Assert.HasCount(WriteOnlyDerivation.ClaimKeyLength, first.ClaimKeySeed.ToArray());
        SequenceAssert.AreEqual(first.ClaimKeySeed.ToArray(), second.ClaimKeySeed.ToArray());
        SequenceAssert.AreEqual(
            first.Credential.ClaimPublicKey.ToArray(), second.Credential.ClaimPublicKey.ToArray());
    }

    [TestMethod]
    public void Claim_AnotherPassphraseOrAnotherSalt_ReachesADifferentKey()
    {
        using var mine = Derive("one long passphrase to rule them", Salt(0x52));
        using var theirs = Derive("a different passphrase entirely!", Salt(0x52));
        using var elsewhere = Derive("one long passphrase to rule them", Salt(0x53));

        // The salt is what makes this the INSTALLATION's key rather than the
        // passphrase's: two households that chose the same words have
        // different salts and must not be able to claim each other's replicas.
        Assert.IsFalse(mine.ClaimKeySeed.SequenceEqual(theirs.ClaimKeySeed));
        Assert.IsFalse(mine.ClaimKeySeed.SequenceEqual(elsewhere.ClaimKeySeed));
    }

    [TestMethod]
    public void Claim_IsNoOtherKeyUnderAnotherName()
    {
        using var authority = Derive("one long passphrase to rule them", Salt(0x54));
        var credential = authority.Credential;

        var members = new List<byte[]>
        {
            authority.ClaimKeySeed.ToArray(),
            credential.ClaimPublicKey.ToArray(),
            authority.ReclaimKeySeed.ToArray(),
            credential.ReclaimPublicKey.ToArray(),
            authority.SealingPrivateKey.ToArray(),
            credential.SealingPublicKey.ToArray(),
            credential.ContentIdKey.ToArray(),
            credential.KeyIdKey.ToArray(),
            credential.DeriveSigningKeySeed(KeyGeneration.Zero),
            credential.DeriveMetadataKey(KeyGeneration.Zero),
        };

        for (var left = 0; left < members.Count; left++)
        {
            for (var right = left + 1; right < members.Count; right++)
            {
                Assert.IsFalse(
                    members[left].AsSpan().SequenceEqual(members[right]),
                    $"members {left} and {right} must be independent one-way domains");
            }
        }
    }

    [TestMethod]
    public void Claim_IsNotGenerational_BecauseTheDestinationRecordsItOnce()
    {
        // Every other derived key takes a generation. This one must not, and
        // the reason is its carrier rather than its cryptography: a
        // destination records the claim public key at first attribution and
        // never replaces it (ADR-0055 §5's rule, which the claim key shares).
        // A key that turned over with the generation would go stale on the
        // first rotation with no way to tell the peer, and the claim would
        // stop verifying exactly when it was needed.
        //
        // It could not be generational anyway: an installation root knows
        // nothing of any one repository's generations, and the claimant has
        // no repository to ask.
        using var authority = Derive("one long passphrase to rule them", Salt(0x57));
        using var credential = authority.Credential.Clone();
        using var reclaim = new ReclaimAuthority(authority.ReclaimKeySeed);

        SequenceAssert.AreEqual(credential.ClaimPublicKey.ToArray(), credential.ClaimPublicKey.ToArray());
        Assert.IsFalse(
            reclaim.SeedFor(KeyGeneration.Zero).AsSpan().SequenceEqual(reclaim.SeedFor(new KeyGeneration(1))),
            "the contrast is the point: the reclaim seed does turn over, and can, because a grant names its generation");
    }

    [TestMethod]
    public void Claim_TheWriteCredential_CarriesThePublicHalfAndNotTheSeed()
    {
        // Same rule as the reclaim key, for the same reason. The service must
        // be able to tell a keyless destination which key a claim will be
        // signed under, and must not be able to author one: a compromised
        // write-only service that could claim could re-point a replica's
        // attribution to a machine of its choosing.
        using var authority = Derive("one long passphrase to rule them", Salt(0x55));
        var bundle = authority.Credential.ToBytes();
        try
        {
            var seed = authority.ClaimKeySeed.ToArray();
            Assert.HasCount(WriteOnlyDerivation.ClaimKeyLength, authority.Credential.ClaimPublicKey.ToArray());

            for (var offset = 0; offset + seed.Length <= bundle.Length; offset++)
            {
                Assert.IsFalse(
                    bundle.AsSpan(offset, seed.Length).SequenceEqual(seed),
                    "the write credential must not carry the claim seed anywhere in its bytes");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bundle);
        }

        using var credential = authority.Credential.Clone();
        Assert.IsNull(typeof(RepositoryWriteCredential).GetMethod("DeriveClaimKeySeed"), "no hierarchy derives the claim seed");
        SequenceAssert.AreEqual(authority.Credential.ClaimPublicKey.ToArray(), credential.ClaimPublicKey.ToArray());
    }

    [TestMethod]
    public void Claim_TheSeedAndThePublicHalf_Agree()
    {
        // The public key a destination recorded has to be the one the seed
        // signs under — the destination neither knows nor cares how it was
        // derived.
        using var installation = Derive("one long passphrase to rule them", Salt(0x56));
        using var installationSigner = RepositorySigner.FromSeed(
            installation.ClaimKeySeed.ToArray(), KeyGeneration.Zero);
        SequenceAssert.AreEqual(
            installation.Credential.ClaimPublicKey.ToArray(), installationSigner.PublicKey.ToArray());
    }

    [TestMethod]
    public void Claim_ACredentialFromBeforeTheDecision_PublishesNoneRatherThanZeros()
    {
        // A set provisioned before this lands publishes no claim key, and its
        // replica is the case ADR-0053 §3 leaves to the destination's
        // operator. Publishing zeros instead would be worse than publishing
        // nothing: the destination records them permanently and would then
        // check claims against a key nobody holds.
        var preClaim = new byte[8 + (6 * 32)];
        "FBPWCRD2"u8.CopyTo(preClaim);
        RandomNumberGenerator.Fill(preClaim.AsSpan(8));

        using var parsed = RepositoryWriteCredential.FromBytes(preClaim);
        Assert.IsTrue(parsed.ClaimPublicKey.IsEmpty);
        Assert.IsFalse(parsed.ReclaimPublicKey.IsEmpty, "the sixth member is still there and still read");

        using var credential = parsed.Clone();
        Assert.IsEmpty(credential.ClaimPublicKey.ToArray());
    }
}
