namespace FallbackPlan.Application;

/// <summary>
/// A client-state failure with a message for the operator — distinct from
/// a bug, which is allowed to escape with a stack trace.
/// </summary>
public sealed class ClientStateException : Exception
{
    public ClientStateException(string message)
        : base(message)
    {
    }

    public ClientStateException()
    {
    }

    public ClientStateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
