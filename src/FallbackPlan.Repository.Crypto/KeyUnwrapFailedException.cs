namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// Thrown when the key object fails to unwrap. A failed unwrap means the
/// passphrase is wrong <em>or</em> the object has been tampered with, and the
/// two are deliberately indistinguishable — a reader must not tell an attacker
/// which (specification 03 §3).
/// </summary>
public sealed class KeyUnwrapFailedException : Exception
{
    private const string IndistinguishableMessage =
        "The key object could not be unwrapped. Either the passphrase is wrong or the object has been altered; the two are deliberately indistinguishable (specification 03 §3).";

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
