namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// Thrown when a passphrase does not reproduce a repository's keys: the
/// derived sealing public key differs from the descriptor's copy
/// (specification 03 §9.3). Equality is the whole verifier — nothing is
/// decrypted to find out — so a wrong passphrase and a descriptor altered to
/// carry another key are reported identically, and a reader must not tell an
/// attacker which.
/// </summary>
public sealed class KeyUnwrapFailedException : Exception
{
    private const string IndistinguishableMessage =
        "The passphrase does not reproduce this repository's keys. Either the passphrase is wrong or the descriptor has been altered; the two are deliberately indistinguishable (specification 03 §9.3).";

    /// <summary>Creates the exception with the single indistinguishable message.</summary>
    public KeyUnwrapFailedException()
        : base(IndistinguishableMessage)
    {
    }

    /// <summary>Creates the exception with the indistinguishable message, keeping the cause internal.</summary>
    public KeyUnwrapFailedException(Exception innerException)
        : base(IndistinguishableMessage, innerException)
    {
    }

    /// <summary>
    /// Creates the exception with a custom message. Callers must not use this
    /// to distinguish wrong-passphrase from tampering.
    /// </summary>
    public KeyUnwrapFailedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a custom message and cause.</summary>
    public KeyUnwrapFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
