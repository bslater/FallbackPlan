using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// The device peer key's durable home (specification peer-protocol 01 §1;
/// NFR-REL-007): minted once, stable across reloads, distinct per device, and
/// refused loudly when corrupt rather than silently replaced — which would
/// unpair the device without telling anyone.
/// </summary>
[TestClass]
public sealed class PeerKeypairStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-keystore-tests", Guid.NewGuid().ToString("n"));

    private string StateDirectory(string name) => Path.Combine(_root, name);

    [TestMethod]
    public void Open_CalledTwice_ReturnsTheSameIdentity()
    {
        var directory = StateDirectory("stable");

        PeerIdentity first;
        using (var keypair = PeerKeypairStore.Open(directory))
        {
            first = keypair.Identity;
        }

        using var reloaded = PeerKeypairStore.Open(directory);

        // The identity persists — a device that minted a fresh key on every
        // start would lose every pairing it holds on every restart.
        Assert.AreEqual(first, reloaded.Identity);
    }

    [TestMethod]
    public void Open_TwoStateDirectories_MintDistinctIdentities()
    {
        using var one = PeerKeypairStore.Open(StateDirectory("device-one"));
        using var two = PeerKeypairStore.Open(StateDirectory("device-two"));

        Assert.AreNotEqual(one.Identity, two.Identity);
    }

    [TestMethod]
    public void Open_TheKeyFileIsCorrupt_IsRefusedRatherThanReplaced()
    {
        var directory = StateDirectory("corrupt");
        using (PeerKeypairStore.Open(directory))
        {
        }

        // Truncate the seed to something the wrong length — a torn write, a
        // botched restore. Starting fresh here would silently unpair the fleet.
        File.WriteAllText(Path.Combine(directory, "peer.key"), "deadbeef");

        Assert.ThrowsExactly<ClientStateException>(() => PeerKeypairStore.Open(directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
