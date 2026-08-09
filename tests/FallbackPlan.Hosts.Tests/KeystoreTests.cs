using System.Runtime.Versioning;
using FallbackPlan.Keystore;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Unattended unlock (ADR-0028 §9, NFR-SEC-009).
/// </summary>
/// <remarks>
/// Each platform's store can only be exercised on its own platform, so these
/// are gated rather than skipped by an early return — a test that does not run
/// must not report as passed. The round trip runs on every leg of the CI matrix
/// against whichever implementation that leg has.
/// </remarks>
[PlatformTrait(TestPlatforms.Any)]
[TestClass]
public sealed class KeystoreTests : IDisposable
{
    private readonly string _state = Path.Combine(
        Path.GetTempPath(), "fbp-keystore", Guid.NewGuid().ToString("n"));

    public KeystoreTests() => Directory.CreateDirectory(_state);

    [TestMethod]
    public void Keystore_APassphrase_RoundTripsThroughThisPlatformsStore()
    {
        var store = PlatformKeystore.For(_state);
        var account = _state;

        Assert.IsFalse(store.TryRead(account, out _));

        store.Write(account, "correct horse battery staple");
        Assert.IsTrue(store.TryRead(account, out var read));
        Assert.AreEqual("correct horse battery staple", read);

        // Rotation must replace, not accumulate. A stale passphrase that
        // survives a rotation is the failure that stops backups weeks later,
        // silently.
        store.Write(account, "a different passphrase");
        Assert.IsTrue(store.TryRead(account, out var rotated));
        Assert.AreEqual("a different passphrase", rotated);

        store.Delete(account);
        Assert.IsFalse(store.TryRead(account, out _));
    }

    [TestMethod]
    public void Remove_WhenNothingIsStored_ShouldSucceedWithoutError()
    {
        var store = PlatformKeystore.For(_state);
        store.Delete(_state);
        store.Delete(_state);
    }

    [TestMethod]
    public void Keystore_TwoStateDirectories_HoldDistinctEntries()
    {
        var other = Path.Combine(_state, "other");
        Directory.CreateDirectory(other);

        var store = PlatformKeystore.For(_state);
        store.Write(_state, "first");
        store.Write(other, "second");

        Assert.IsTrue(store.TryRead(_state, out var first));
        Assert.IsTrue(store.TryRead(other, out var second));
        Assert.AreEqual("first", first);
        Assert.AreEqual("second", second);

        store.Delete(_state);
        store.Delete(other);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Unix file modes decide who can read the stored material.")]
    [UnsupportedOSPlatform("windows")]
    public void On_posix_the_stored_material_is_owner_only()
    {
        var store = PlatformKeystore.For(_state);
        store.Write(_state, "secret");

        var files = Directory.EnumerateFiles(_state, "*", SearchOption.AllDirectories).ToList();
        Assert.IsNotEmpty(files);

        foreach (var file in files)
        {
            var mode = File.GetUnixFileMode(file);
            Assert.AreEqual(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead));
        }

        store.Delete(_state);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "The Linux store's only protection is its permissions, so it checks them.")]
    [UnsupportedOSPlatform("windows")]
    public void On_linux_material_whose_permissions_drifted_is_refused_not_read()
    {
        var store = PlatformKeystore.For(_state);
        store.Write(_state, "secret");

        var file = Directory.EnumerateFiles(_state, "*.key", SearchOption.AllDirectories).Single();
        File.SetUnixFileMode(file, File.GetUnixFileMode(file) | UnixFileMode.OtherRead);

        // Reading it anyway would keep working while the only property
        // protecting it had already been lost.
        var refused = Assert.ThrowsExactly<KeystoreException>(() => store.TryRead(_state, out _));
        Assert.Contains("readable beyond its owner", refused.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            PlatformKeystore.For(_state).Delete(_state);
        }
        catch (KeystoreException)
        {
            // Best effort.
        }
        catch (IOException)
        {
            // Same.
        }

        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
