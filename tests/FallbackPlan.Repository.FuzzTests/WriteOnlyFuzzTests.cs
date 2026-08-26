using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.FuzzTests;

/// <summary>
/// The write-only crypto surface under mutation (ADR-0042; NFR-REL-005):
/// every opener of attacker-influenceable sealed bytes —
/// <see cref="ContentSealing.Open"/>, <see cref="ContentSealing.OpenPayload"/>,
/// <see cref="WriteOnlyProvisioning.OpenProvision"/>,
/// <see cref="WriteOnlyProvisioning.OpenGrant"/> — refuses with exactly its
/// declared types (<see cref="SealedContentException"/> for anything that
/// does not open, <see cref="ArgumentException"/> for impossible shapes),
/// and <see cref="RepositoryWriteCredential.FromBytes"/> either parses or
/// refuses typed. Nothing else may escape: these bytes arrive over the
/// command contract and from replicas.
/// </summary>
[TestClass]
public sealed class WriteOnlyFuzzTests
{
    private static readonly byte[] RecipientPrivate = [.. Enumerable.Repeat((byte)0x60, 32)];
    private static readonly byte[] RecipientPublic = ContentSealing.PublicKeyOf(RecipientPrivate);
    private static readonly byte[] Context = "fuzz repo+blob context"u8.ToArray();

    private static readonly RepositoryReadAuthority Authority = DeriveAuthority();

    private static readonly byte[] SealedContentKeySeed =
        ContentSealing.Seal(RecipientPublic, [.. Enumerable.Repeat((byte)0x44, 32)], Context);

    private static readonly byte[] PayloadEnvelopeSeed =
        ContentSealing.SealPayload(RecipientPublic, "a payload of modest length"u8.ToArray(), Context);

    private static readonly byte[] ProvisionEnvelopeSeed = WriteOnlyProvisioning.SealProvision(
        RecipientPublic, Authority, [.. Enumerable.Repeat((byte)0x5A, KekDerivation.SaltLength)],
        new Argon2Parameters { MemoryKiB = 64, Iterations = 1, Parallelism = 1 });

    private static readonly byte[] GrantEnvelopeSeed =
        WriteOnlyProvisioning.SealGrant(RecipientPublic, Authority.SealingPrivateKey);

    private static readonly byte[] ClaimRoot = [.. Enumerable.Repeat((byte)0x7C, KekDerivation.KekLength)];

    private static readonly byte[] ClaimRootEnvelopeSeed =
        WriteOnlyProvisioning.SealClaimRoot(RecipientPublic, ClaimRoot);

    private static readonly byte[] CredentialSeed = SerializeCredential();

    private static RepositoryReadAuthority DeriveAuthority()
    {
        using var passphrase = Passphrase.Create("the fuzz corpus passphrase");
        return WriteOnlyDerivation.Derive(
            passphrase,
            new Argon2Parameters { MemoryKiB = 64, Iterations = 1, Parallelism = 1 },
            [.. Enumerable.Repeat((byte)0x5A, KekDerivation.SaltLength)],
            KdfValidationMode.OpenRepository);
    }

    private static byte[] SerializeCredential()
    {
        var bytes = Authority.Credential.ToBytes();
        return bytes;
    }

    private static byte[] Mutate(byte[] seed, (int Offset, byte Mask)[]? mutations, int resize)
    {
        var length = Math.Max(0, seed.Length + (int)((uint)resize % 64) - 32);
        var mutated = new byte[length];
        Array.Copy(seed, mutated, Math.Min(seed.Length, length));
        foreach (var (offset, mask) in mutations ?? [])
        {
            if (mutated.Length > 0)
            {
                mutated[(int)((uint)offset % mutated.Length)] ^= (byte)(mask | 1);
            }
        }

        return mutated;
    }

    private static void AssertOnlyTypedRefusal(string name, Action open)
    {
        try
        {
            open();
        }
        catch (Exception refusal) when (refusal is SealedContentException or ArgumentException)
        {
            // The two declared refusals: does-not-open, impossible shape.
        }
#pragma warning disable CA1031 // deliberate: name the opener that escaped
        catch (Exception exception)
        {
            Assert.Fail($"Opener '{name}' escaped with {exception.GetType().Name}: {exception.Message}");
        }
#pragma warning restore CA1031
    }

    [TestMethod]
    public void SealedOpeners_GivenMutatedSealedBytes_RefuseOnlyWithTheirDeclaredTypes() =>
        PropertyCheck.Holds(this, maxTest: 300);

    public static void SealedOpeners_GivenMutatedSealedBytes_RefuseOnlyWithTheirDeclaredTypesProperty(
        int seedIndex, (int Offset, byte Mask)[]? mutations, int resize)
    {
        var (name, seed, open) = ((uint)seedIndex % 5) switch
        {
            0u => ("content-key-open",
                SealedContentKeySeed,
                (Action<byte[]>)(bytes => ContentSealing.Open(Authority.SealingPrivateKey, bytes, Context))),
            1u => ("payload-open",
                PayloadEnvelopeSeed,
                bytes => ContentSealing.OpenPayload(RecipientPrivate, bytes, Context)),
            2u => ("provision-open",
                ProvisionEnvelopeSeed,
                bytes => WriteOnlyProvisioning.OpenProvision(RecipientPrivate, bytes).Credential.Dispose()),
            3u => ("grant-open",
                GrantEnvelopeSeed,
                bytes => WriteOnlyProvisioning.OpenGrant(RecipientPrivate, bytes)),
            _ => ("claim-root-open",
                ClaimRootEnvelopeSeed,
                bytes => WriteOnlyProvisioning.OpenClaimRoot(RecipientPrivate, bytes)),
        };

        var mutated = Mutate(seed, mutations, resize);
        AssertOnlyTypedRefusal(name, () => open(mutated));
    }

    [TestMethod]
    public void WriteCredentialFromBytes_GivenMutatedBytes_ParsesOrRefusesTyped() =>
        PropertyCheck.Holds(this, maxTest: 300);

    public static void WriteCredentialFromBytes_GivenMutatedBytes_ParsesOrRefusesTypedProperty(
        (int Offset, byte Mask)[]? mutations, int resize)
    {
        // A mutation that keeps the magic and length intact yields a
        // well-formed (if wrong) credential — that is the descriptor
        // comparison's problem, not the parser's. Everything else must be
        // one typed refusal.
        var mutated = Mutate(CredentialSeed, mutations, resize);
        try
        {
            RepositoryWriteCredential.FromBytes(mutated).Dispose();
        }
        catch (ArgumentException)
        {
        }
#pragma warning disable CA1031
        catch (Exception exception)
        {
            Assert.Fail($"FromBytes escaped with {exception.GetType().Name}: {exception.Message}");
        }
#pragma warning restore CA1031
    }
}
