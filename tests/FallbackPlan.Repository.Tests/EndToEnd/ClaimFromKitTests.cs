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
    public async Task APerRepositoryKit_AndThePassphrase_ReachTheOtherRootsClaimSeed()
    {
        // The v1 shape: the master key rides the kit inside a key object, so
        // the claimant unwraps it and expands fbp/claim/v1. A different root
        // and a different label — and the caller does not have to care.
        var store = new LocalFileSystemObjectStore(Directory.CreateDirectory(_scratch).FullName);
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.CreateAsync(
            store,
            passphrase,
            // The real Argon2 parameters, because creating a repository
            // enforces the creation minimums — so this test pays two genuine
            // derivations, which is also what a claimant pays.
            RepositoryCreationSettings.Default,
            0,
            _timeout.Token);

        var kit = await RecoveryKitFactory.BuildAsync(store, passphrase, new byte[16], 0, [], _timeout.Token);
        Assert.IsFalse(kit.IsInstallationKit);

        var seed = RecoveryKitClaim.SeedFrom(kit, passphrase);
        try
        {
            SequenceAssert.AreEqual(repository.Hierarchy.DeriveClaimKeySeed(), seed);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(seed);
        }
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
