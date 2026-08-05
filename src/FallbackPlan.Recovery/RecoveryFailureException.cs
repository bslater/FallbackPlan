namespace FallbackPlan.Recovery;

/// <summary>An operator-facing failure — a message, never a stack trace.</summary>
internal sealed class RecoveryFailureException : Exception
{
    public RecoveryFailureException(string message)
        : base(message)
    {
    }

    public RecoveryFailureException()
    {
    }

    public RecoveryFailureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
