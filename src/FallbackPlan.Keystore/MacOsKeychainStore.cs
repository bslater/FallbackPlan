using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace FallbackPlan.Keystore;

/// <summary>
/// macOS: a generic password item in the login keychain of the account the
/// service runs as (ADR-0028 §9).
/// </summary>
/// <remarks>
/// This uses the <c>SecKeychain*</c> generic-password functions rather than the
/// modern <c>SecItemAdd</c>/<c>SecItemCopyMatching</c> pair. Both are in
/// <c>Security.framework</c>; the modern pair takes a <c>CFDictionary</c>,
/// which means marshalling Core Foundation types by hand for no behavioural
/// gain here. The functions used are deprecated by Apple and still present and
/// working, and the trade is recorded rather than left for someone to discover
/// in a header.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed partial class MacOsKeychainStore : IPassphraseStore
{
    private const string Framework = "/System/Library/Frameworks/Security.framework/Security";
    private const string Service = "FallbackPlan";
    private const int ItemNotFound = -25300;

    public string Description => "the macOS keychain of this service account";

    public bool TryRead(string account, out string? passphrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        passphrase = null;

        var serviceBytes = Encoding.UTF8.GetBytes(Service);
        var accountBytes = Encoding.UTF8.GetBytes(account);

        var status = SecKeychainFindGenericPassword(
            nint.Zero,
            (uint)serviceBytes.Length, serviceBytes,
            (uint)accountBytes.Length, accountBytes,
            out var length, out var data, out var item);

        if (status == ItemNotFound)
        {
            return false;
        }

        if (status != 0)
        {
            throw new KeystoreException($"The macOS keychain refused a lookup for '{account}' (status {status}).");
        }

        try
        {
            var buffer = new byte[length];
            Marshal.Copy(data, buffer, 0, (int)length);
            try
            {
                passphrase = Encoding.UTF8.GetString(buffer);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
        finally
        {
            _ = SecKeychainItemFreeContent(nint.Zero, data);
            if (item != nint.Zero)
            {
                CFRelease(item);
            }
        }
    }

    public void Write(string account, string passphrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        // Replace rather than add-and-hope: an existing item would make the add
        // fail with duplicate-item, and a stale passphrase that silently
        // survives a rotation is exactly the failure that stops backups later.
        Delete(account);

        var serviceBytes = Encoding.UTF8.GetBytes(Service);
        var accountBytes = Encoding.UTF8.GetBytes(account);
        var secretBytes = Encoding.UTF8.GetBytes(passphrase);

        try
        {
            var status = SecKeychainAddGenericPassword(
                nint.Zero,
                (uint)serviceBytes.Length, serviceBytes,
                (uint)accountBytes.Length, accountBytes,
                (uint)secretBytes.Length, secretBytes,
                out var item);

            if (status != 0)
            {
                throw new KeystoreException($"The macOS keychain refused to store '{account}' (status {status}).");
            }

            if (item != nint.Zero)
            {
                CFRelease(item);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    public void Delete(string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);

        var serviceBytes = Encoding.UTF8.GetBytes(Service);
        var accountBytes = Encoding.UTF8.GetBytes(account);

        var status = SecKeychainFindGenericPassword(
            nint.Zero,
            (uint)serviceBytes.Length, serviceBytes,
            (uint)accountBytes.Length, accountBytes,
            out _, out var data, out var item);

        if (status == ItemNotFound)
        {
            return;
        }

        if (status != 0)
        {
            throw new KeystoreException($"The macOS keychain refused a lookup for '{account}' (status {status}).");
        }

        _ = SecKeychainItemFreeContent(nint.Zero, data);
        if (item != nint.Zero)
        {
            _ = SecKeychainItemDelete(item);
            CFRelease(item);
        }
    }

    [LibraryImport(Framework)]
    private static partial int SecKeychainFindGenericPassword(
        nint keychain,
        uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName,
        out uint passwordLength, out nint passwordData, out nint itemRef);

    [LibraryImport(Framework)]
    private static partial int SecKeychainAddGenericPassword(
        nint keychain,
        uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName,
        uint passwordLength, byte[] passwordData, out nint itemRef);

    [LibraryImport(Framework)]
    private static partial int SecKeychainItemDelete(nint itemRef);

    [LibraryImport(Framework)]
    private static partial int SecKeychainItemFreeContent(nint attributeList, nint data);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial void CFRelease(nint reference);
}
