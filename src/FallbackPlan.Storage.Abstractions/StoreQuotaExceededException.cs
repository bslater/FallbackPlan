namespace FallbackPlan.Storage.Abstractions;

/// <summary>
/// A store refused an object because it would cross a quota its owner set
/// (ADR-0012 Amendment 5). A limit, not a gap: it holds until a person raises
/// it or keeps less there, so it is not a <see cref="StoreUnavailableException"/>
/// (FR-QUOTA-001). A put it refuses leaves no object behind, partial or whole
/// (FR-QUOTA-002).
/// </summary>
public sealed class StoreQuotaExceededException : IOException
{
    /// <summary>Creates an empty exception.</summary>
    public StoreQuotaExceededException()
    {
    }

    /// <summary>Creates the exception with its message.</summary>
    /// <param name="message">The object the quota would not take, and what the store answered.</param>
    public StoreQuotaExceededException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with its message and the underlying failure.</summary>
    /// <param name="message">The object the quota would not take, and what the store answered.</param>
    /// <param name="innerException">The underlying failure.</param>
    public StoreQuotaExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
