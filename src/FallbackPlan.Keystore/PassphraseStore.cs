using Bodu;
using FallbackPlan.Keystore.Resources;

namespace FallbackPlan.Keystore;

/// <summary>Raised when a platform keystore refuses or is unavailable.</summary>
public sealed class KeystoreException : Exception
{
    /// <summary>Creates the exception.</summary>
    public KeystoreException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public KeystoreException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public KeystoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Where a service keeps the passphrase that unlocks its repository, so that
/// scheduled backups run with no human present (ADR-0028 §9).
/// </summary>
/// <remarks>
/// <para>
/// This replaces <c>--passphrase-env</c> for the service, which required an
/// environment variable to be set for the process's whole lifetime and was
/// inherited by every child it spawned.
/// </para>
/// <para>
/// The consequence is stated rather than buried: <b>an attacker who obtains the
/// service account obtains the backups.</b> That is a real reduction in secrecy
/// against a local attacker, accepted because the alternative — prompting a
/// human for every scheduled run — is not a backup product. It is recorded as
/// T-19 in the threat model, and it is why operations that mint new access, key
/// export above all, re-derive from a passphrase supplied per invocation and
/// never from here.
/// </para>
/// </remarks>
public interface IPassphraseStore
{
    /// <summary>What this store is, for a message a human has to act on.</summary>
    string Description { get; }

    /// <summary>Reads the stored passphrase for an account.</summary>
    /// <param name="account">The account name, scoping one repository's entry.</param>
    /// <param name="passphrase">The passphrase, when one is stored.</param>
    /// <returns><see langword="true"/> when an entry existed.</returns>
    bool TryRead(string account, out string? passphrase);

    /// <summary>Stores (or replaces) the passphrase for an account.</summary>
    /// <param name="account">The account name.</param>
    /// <param name="passphrase">The passphrase to store.</param>
    void Write(string account, string passphrase);

    /// <summary>Removes an account's entry. Removing what is not there is not an error.</summary>
    /// <param name="account">The account name.</param>
    void Delete(string account);
}

/// <summary>Chooses the platform's keystore.</summary>
public static class PlatformKeystore
{
    /// <summary>
    /// The keystore for the running platform.
    /// </summary>
    /// <param name="stateDirectory">
    /// Where a file-backed store keeps its material. Used only by the Linux
    /// implementation; the others keep nothing here.
    /// </param>
    /// <returns>The platform's store.</returns>
    /// <exception cref="KeystoreException">The platform has no implementation.</exception>
    public static IPassphraseStore For(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);

        if (OperatingSystem.IsWindows())
        {
            return new WindowsDataProtectionStore(stateDirectory);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsKeychainStore();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxOwnerOnlyFileStore(stateDirectory);
        }

        // Never a silent fallback to something weaker. A platform this does not
        // support says so, and the operator keeps using --passphrase-env
        // knowingly rather than unknowingly.
        throw new KeystoreException(Strings.FormatPlatformKeystore_NoKeystoreImplementedUnattendedUnlock(Environment.OSVersion.Platform));
    }
}
