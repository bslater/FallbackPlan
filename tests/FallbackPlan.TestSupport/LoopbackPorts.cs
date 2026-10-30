namespace FallbackPlan.TestSupport;

/// <summary>Loopback ports a test dials to meet a peer or a store that is not there.</summary>
public static class LoopbackPorts
{
    /// <summary>
    /// A port nothing on the machine listens on, so a connection to it is
    /// refused. A port bound and released to find one free is no such port:
    /// the operating system may hand it straight to another process that asks
    /// for any free port, a test runner's own loopback channel among them,
    /// and a test's retried requests then reach that process. This one is
    /// below every platform's range of ports handed out that way.
    /// </summary>
    public const int Refused = 1;
}
