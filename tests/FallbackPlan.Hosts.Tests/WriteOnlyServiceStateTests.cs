using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The service's write-only state files taken on directly (ADR-0042 §4,
/// §10): the envelope recipient keypair — minted once, reopened stable,
/// owner-only, and refusing a damaged key file by name rather than crashing
/// service start — and the per-set write-credential store, whose corrupt
/// entries are damage to name (adopt again), never a silent "not
/// provisioned".
/// </summary>
[TestClass]
public sealed class WriteOnlyServiceStateTests : IDisposable
{
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "fbp-wo-state-tests", Guid.NewGuid().ToString("n"));

    public WriteOnlyServiceStateTests() => Directory.CreateDirectory(_state);

    public void Dispose()
    {
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
    }

    private string RecipientPath => Path.Combine(_state, "grant-recipient.key");

    private static RepositoryWriteCredential DeriveCredential(string passphraseText)
    {
        using var passphrase = Passphrase.Create(passphraseText);
        var salt = Enumerable.Repeat((byte)0x3C, KekDerivation.SaltLength).ToArray();
        using var authority = WriteOnlyDerivation.Derive(
            passphrase,
            new Argon2Parameters { MemoryKiB = 64, Iterations = 1, Parallelism = 1 },
            salt,
            KdfValidationMode.OpenRepository);

        // The store owns what it saves; hand back a clone the test owns.
        var bytes = authority.Credential.ToBytes();
        try
        {
            return RepositoryWriteCredential.FromBytes(bytes);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    [TestMethod]
    public void GrantRecipient_MintedOnceAndReopened_KeepsItsPublicKey()
    {
        string minted;
        using (var recipient = GrantRecipient.Open(_state))
        {
            minted = recipient.PublicKeyHex;
            Assert.AreEqual(64, minted.Length, "a 32-byte X25519 public key renders as 64 hex characters");
        }

        using (var reopened = GrantRecipient.Open(_state))
        {
            // Clients seal to the published key; a recipient that drifted
            // between opens would orphan every outstanding envelope.
            Assert.AreEqual(minted, reopened.PublicKeyHex);
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(RecipientPath),
                "a recipient key another local account can read is a grant it can open");
        }
    }

    [TestMethod]
    [DataRow("this is not hex at all!", DisplayName = "non-hex text")]
    [DataRow("abc", DisplayName = "odd-length hex")]
    [DataRow("a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4", DisplayName = "well-formed but 16 bytes")]
    [DataRow("", DisplayName = "empty file")]
    public void GrantRecipient_ADamagedKeyFile_IsRefusedByNameNotCrashedOver(string contents)
    {
        File.WriteAllText(RecipientPath, contents);

        // The pre-fix behaviour was a raw FormatException/ArgumentException
        // out of service start; the refusal now names the file and the
        // remedy instead.
        var refusal = Assert.ThrowsExactly<ClientStateException>(() => GrantRecipient.Open(_state));
        Assert.Contains("grant-recipient.key", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("ADR-0042", refusal.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void WriteCredentialStore_SaveLoadOverwriteDelete_RoundTripsAndForgets()
    {
        var store = new WriteCredentialStore(_state);
        var setId = new string('a', 32);
        Assert.IsFalse(store.Holds(setId));
        Assert.IsNull(store.TryLoad(setId));

        using (var first = DeriveCredential("the first passphrase of the drill"))
        {
            store.Save(setId, first);
            Assert.IsTrue(store.Holds(setId));
            using var loaded = store.TryLoad(setId);
            Assert.IsNotNull(loaded);
            SequenceAssert.AreEqual(first.SealingPublicKey.ToArray(), loaded!.SealingPublicKey.ToArray());
        }

        // Re-provisioning replaces the held credential whole.
        using (var second = DeriveCredential("a different passphrase entirely"))
        {
            store.Save(setId, second);
            using var reloaded = store.TryLoad(setId);
            SequenceAssert.AreEqual(second.SealingPublicKey.ToArray(), reloaded!.SealingPublicKey.ToArray());
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(_state, "write-credentials", $"{setId}.bin")));
        }

        store.Delete(setId);
        Assert.IsFalse(store.Holds(setId));
        Assert.IsNull(store.TryLoad(setId));
        store.Delete(setId); // forgetting the forgotten acknowledges
    }

    [TestMethod]
    public void WriteCredentialStore_ACorruptStoredCredential_IsDamageToNameNotSilentlyUnprovisioned()
    {
        var store = new WriteCredentialStore(_state);
        var setId = new string('b', 32);
        using (var credential = DeriveCredential("the passphrase that was provisioned"))
        {
            store.Save(setId, credential);
        }

        var path = Path.Combine(_state, "write-credentials", $"{setId}.bin");

        // Truncated: half the file gone.
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes.AsSpan(0, bytes.Length / 2).ToArray());
        var truncated = Assert.ThrowsExactly<RepositoryOpenException>(() => store.TryLoad(setId));
        Assert.Contains("adopt", truncated.Message, StringComparison.Ordinal);
        Assert.Contains(setId, truncated.Message, StringComparison.Ordinal);

        // Magic flipped: right length, wrong bytes. A silent null here would
        // flip the set onto the passphrase path and mask the loss.
        bytes[0] ^= 0x01;
        File.WriteAllBytes(path, bytes);
        var tampered = Assert.ThrowsExactly<RepositoryOpenException>(() => store.TryLoad(setId));
        Assert.Contains("adopt", tampered.Message, StringComparison.Ordinal);
    }
}
