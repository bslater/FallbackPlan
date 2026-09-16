using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.RecoveryKit;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Reaching the claim key from what a person actually keeps
/// ([ADR-0053](../../../docs/adr/0053-peer-claim-and-configuration-recovery.md);
/// FR-KIT-006): a printed kit and a passphrase, with no archive, no state
/// directory and no repository id.
/// </summary>
/// <remarks>
/// The claimant is by definition a machine that has nothing else, so this is
/// the whole of what the <c>claim</c> verb may assume. Both kit shapes answer
/// through one entry point, because the person holding the kit should not have
/// to know which kind they were given — and the destination cannot tell
/// either, since it recorded only a public key.
/// </remarks>
[TestClass]
public sealed class ClaimFromKitTests : IDisposable
{
    private const string PassphraseText = "one long passphrase to rule them";

    private static readonly Argon2Parameters TinyParameters =
        new() { MemoryKiB = 64, Iterations = 1, Parallelism = 1 };

    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fbp-claim-kit", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    [TestMethod]
    public void AnInstallationKit_AndThePassphrase_ReachTheClaimSeed()
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        using var passphrase = Passphrase.Create(PassphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, TinyParameters, salt, KdfValidationMode.OpenRepository);

        var kit = RecoveryKitFactory.BuildForInstallation(
            authority.Credential, salt, TinyParameters, new byte[16], 0);

        var seed = RecoveryKitClaim.SeedFrom(kit, passphrase);
        try
        {
            SequenceAssert.AreEqual(authority.ClaimKeySeed.ToArray(), seed);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(seed);
        }
    }

    [TestMethod]
    public void AFormatOneKit_IsRefusedByNameRatherThanYieldingASeed()
    {
        // A format-1 kit carried the master key inside a wrapped key object,
        // and format 1 is withdrawn: nothing can unwrap it, and no peer holds
        // a replica of one. The kit still parses — the wire shape is pinned
        // until the kit goes — and the claimant refuses it by name.
        var kit = new RecoveryKit
        {
            KitFormatVersion = 1,
            MinimumToolVersion = "0.1.0",
            RepositoryId = Domain.Identifiers.RepositoryId.FromBytes(Enumerable.Repeat((byte)0x0C, 16).ToArray()),
            RepositoryFormatVersion = 1,
            KeyObject = "FBPKKEYS-withdrawn"u8.ToArray(),
            KdfMemoryKiB = 8 * 1024,
            KdfIterations = 1,
            KdfParallelism = 1,
            KdfSalt = new byte[16],
            IssuingDeviceId = new byte[16],
            IssuedAt = 0,
            Instructions = string.Empty,
        };
        using var passphrase = Passphrase.Create(PassphraseText);

        var refusal = Assert.ThrowsExactly<RecoveryKitFormatException>(() => RecoveryKitClaim.SeedFrom(kit, passphrase));

        Assert.Contains("withdrawn", refusal.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void AWrongPassphrase_IsRefusedRatherThanYieldingAKeyThatProvesNothing()
    {
        // It would otherwise yield a perfectly well-formed seed that no
        // destination has ever heard of, and the claimant would be told "no
        // replica here is claimable under that key" — true, and the wrong
        // diagnosis entirely.
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        using var right = Passphrase.Create(PassphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            right, TinyParameters, salt, KdfValidationMode.OpenRepository);
        var kit = RecoveryKitFactory.BuildForInstallation(
            authority.Credential, salt, TinyParameters, new byte[16], 0);

        using var wrong = Passphrase.Create("a different passphrase entirely!");
        Assert.ThrowsExactly<KeyUnwrapFailedException>(() => RecoveryKitClaim.SeedFrom(kit, wrong));
    }

    public void Dispose()
    {
        _timeout.Dispose();
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }
}
