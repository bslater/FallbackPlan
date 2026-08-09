using Bodu;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace FallbackPlan.Keystore;

/// <summary>
/// Windows: DPAPI, scoped to the account the service runs as (ADR-0028 §9).
/// </summary>
/// <remarks>
/// <c>CryptProtectData</c> is called through P/Invoke rather than through the
/// <c>System.Security.Cryptography.ProtectedData</c> package, because that
/// package is a new dependency identity and this needs none: the entry point
/// has been in <c>crypt32.dll</c> since Windows 2000. The ciphertext lands in
/// the state directory, where only the service account can read it — and where
/// reading it is useless to any other account, which is the property a bare
/// file could not have offered.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsDataProtectionStore(string stateDirectory) : IPassphraseStore
{
    private const int CryptProtectUiForbidden = 0x1;

    public string Description => "the Windows data protection API (DPAPI), scoped to this service account";

    public bool TryRead(string account, out string? passphrase)
    {
        passphrase = null;
        var path = PathFor(account);
        if (!File.Exists(path))
        {
            return false;
        }

        var protectedBytes = File.ReadAllBytes(path);
        var plain = Unprotect(protectedBytes);
        try
        {
            passphrase = Encoding.UTF8.GetString(plain);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Write(string account, string passphrase)
    {
        ThrowHelper.ThrowIfNullOrEmpty(passphrase);

        Directory.CreateDirectory(stateDirectory);
        var plain = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            File.WriteAllBytes(PathFor(account), Protect(plain));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Delete(string account)
    {
        var path = PathFor(account);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string PathFor(string account)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(account);
        return Path.Combine(stateDirectory, $"unlock-{Sanitise(account)}.dpapi");
    }

    private static string Sanitise(string account) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account)).AsSpan(0, 8)).ToLowerInvariant();

    private static byte[] Protect(byte[] plain) => Transform(plain, protect: true);

    private static byte[] Unprotect(byte[] cipher) => Transform(cipher, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var pinned = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            var source = new DataBlob { Length = input.Length, Data = pinned.AddrOfPinnedObject() };
            var destination = default(DataBlob);

            var ok = protect
                ? CryptProtectData(ref source, null, nint.Zero, nint.Zero, nint.Zero, CryptProtectUiForbidden, out destination)
                : CryptUnprotectData(ref source, nint.Zero, nint.Zero, nint.Zero, nint.Zero, CryptProtectUiForbidden, out destination);

            if (!ok)
            {
                throw new KeystoreException(
                    protect
                        ? "DPAPI refused to protect the passphrase for this account."
                        : "DPAPI refused to unprotect the stored passphrase — it belongs to a different account "
                          + "or machine, and cannot be recovered here.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }

            try
            {
                var output = new byte[destination.Length];
                Marshal.Copy(destination.Data, output, 0, destination.Length);
                return output;
            }
            finally
            {
                LocalFree(destination.Data);
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public nint Data;
    }

    [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        ref DataBlob input, string? description, nint entropy, nint reserved, nint prompt, int flags, out DataBlob output);

    [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        ref DataBlob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out DataBlob output);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static partial nint LocalFree(nint memory);
}
