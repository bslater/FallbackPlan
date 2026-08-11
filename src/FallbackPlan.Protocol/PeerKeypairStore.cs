using Bodu;
using System.Security.Cryptography;
using FallbackPlan.Application;
using FallbackPlan.Protocol.Resources;

namespace FallbackPlan.Protocol;

/// <summary>
/// The durable home of this device's long-lived peer keypair
/// (specification peer-protocol 01 §1; NFR-REL-007): <c>peer.key</c> in the
/// state directory, beside the grants it authenticates and never inside the
/// disposable catalogue.
/// </summary>
/// <remarks>
/// <para>
/// The seed is generated on first use and never leaves the state directory.
/// It is not derived from the repository master key (<see cref="PeerKeypair"/>
/// says why), so losing it loses the identity and every pairing — which is
/// exactly why a corrupt file is refused loudly here rather than silently
/// replaced with a fresh identity that would unpair the device without
/// telling anyone, the same posture <see cref="PeerGrantStore"/> holds for
/// the grants themselves.
/// </para>
/// <para>
/// The file holds the 32-byte seed as lowercase hex and nothing else. It is
/// written through <see cref="AtomicFile"/> and, on a POSIX host, restricted
/// to the owner — a private key world-readable is a private key lost.
/// </para>
/// </remarks>
public static class PeerKeypairStore
{
    /// <summary>Loads this device's keypair, generating and persisting one on first use.</summary>
    /// <param name="stateDirectory">The device's durable local state directory.</param>
    /// <returns>The keypair; the caller owns it and disposes it.</returns>
    /// <exception cref="ClientStateException">The file exists and is not a readable seed.</exception>
    public static PeerKeypair Open(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, "peer.key");

        if (File.Exists(path))
        {
            return Load(path);
        }

        // First use: mint one, persist the seed, and hand back the live key.
        // A concurrent creator is impossible under the writer lock the service
        // and CLI already hold before reaching here, so a plain create-if-absent
        // is enough — but the load path below tolerates a racing peer anyway.
        using var keypair = PeerKeypair.Generate();
        var seed = keypair.ExportPrivateKey();
        try
        {
            Persist(path, seed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }

        return Load(path);
    }

    private static PeerKeypair Load(string path)
    {
        byte[] seed;
        try
        {
            seed = Convert.FromHexString(File.ReadAllText(path).Trim());
        }
        catch (FormatException exception)
        {
            throw new ClientStateException(Strings.FormatPeerKeypairStore_DeviceKeyCouldNotRead(path), exception);
        }

        try
        {
            if (seed.Length != PeerKeypair.PrivateKeyLength)
            {
                throw new ClientStateException(Strings.FormatPeerKeypairStore_DeviceKeyCouldNotRead(path));
            }

            try
            {
                return PeerKeypair.FromPrivateKey(seed);
            }
            catch (ArgumentException exception)
            {
                throw new ClientStateException(Strings.FormatPeerKeypairStore_DeviceKeyCouldNotRead(path), exception);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    private static void Persist(string path, byte[] seed)
    {
        AtomicFile.WriteAllText(path, Convert.ToHexStringLower(seed));

        if (!OperatingSystem.IsWindows())
        {
            // Owner-only. A key another local account can read is a pairing
            // another local account can impersonate.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
