namespace FallbackPlan.Api.Transport;

/// <summary>
/// The remote binding (ADR-0028 §5) — <b>off until explicitly enabled</b>.
/// </summary>
/// <remarks>
/// <para>
/// Topologies 3 and 4 need it: a web console managing several services, and a
/// client alone talking to a service on another machine. Topologies 1 and 2 —
/// which will be most installs — need nothing but the local binding, and
/// enabling a listening port on a machine that previously had none is a
/// decision, not a default (FR-SVC-003).
/// </para>
/// <para>
/// This build parses and validates the options and <b>binds nothing</b>. Remote
/// clients are paired, not passworded, and pairing reuses the mechanism
/// architecture 09 §3 defines for peers — which is not built yet. Enabling the
/// binding therefore fails with that stated, rather than opening a port with no
/// authentication behind it and calling it progress.
/// </para>
/// </remarks>
public sealed record RemoteBindingOptions
{
    /// <summary>The binding as it exists on a default install: absent.</summary>
    public static RemoteBindingOptions Disabled { get; } = new();

    /// <summary>Whether the operator asked for the remote binding.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The interface to bind. Named explicitly, never inferred: "which
    /// interface is this on?" must be answerable from configuration alone.
    /// </summary>
    public string? Interface { get; init; }

    /// <summary>The port to bind.</summary>
    public int Port { get; init; }

    /// <summary>
    /// Whether these options can be honoured, and why not when they cannot.
    /// </summary>
    /// <param name="reason">The refusal, when the options cannot be honoured.</param>
    /// <returns><see langword="true"/> when the service may proceed with them.</returns>
    public bool TryValidate(out string? reason)
    {
        if (!Enabled)
        {
            reason = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(Interface))
        {
            reason = "The remote binding must name the interface it binds; it is never inferred.";
            return false;
        }

        if (Port is <= 0 or > 65535)
        {
            reason = $"The remote binding port {Port} is not a port number.";
            return false;
        }

        reason = "The remote binding requires paired device identity (ADR-0028 §5), which this build does not "
            + "implement. It is refused rather than opened without authentication.";
        return false;
    }
}
