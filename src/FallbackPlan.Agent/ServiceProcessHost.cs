using System.Runtime.InteropServices;
using Bodu;

namespace FallbackPlan.Agent;

/// <summary>
/// Hosts <see cref="AgentHost"/> for the operating system's process model
/// (ADR-0033): a foreground process whose stop signals — Ctrl+C and the
/// <c>SIGTERM</c> that systemd and launchd send — become a clean shutdown, or,
/// when the Windows Service Control Manager launched it, the SCM Start/Stop
/// handshake. Either way the agent stops the same way: one cancellation token,
/// unwound through the same disposals that release the writer lock and return 0.
/// </summary>
/// <remarks>
/// This is the whole of what used to live in <c>Main</c>. The point of moving it
/// here is the same as everywhere else in this solution — a process concern that
/// is nonetheless worth a test, so the entry point stays a single line and the
/// lifetime is exercised by driving the real host.
/// </remarks>
public static class ServiceProcessHost
{
    /// <summary>Runs the agent under the lifetime this process was started with.</summary>
    /// <param name="args">The command line, as the process received it.</param>
    /// <param name="output">Where run lines and help are written.</param>
    /// <param name="error">Where operator-facing failures are written.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        ThrowHelper.ThrowIfNull(args);
        ThrowHelper.ThrowIfNull(output);
        ThrowHelper.ThrowIfNull(error);

        if (OperatingSystem.IsWindows() && WindowsServiceHost.LaunchedByServiceControlManager())
        {
            // The Service Control Manager owns the lifetime: it starts us and
            // stops us through the control handler, not through a console signal.
            return WindowsServiceHost.Run(args, output, error);
        }

        using var cancellation = new CancellationTokenSource();

        // Ctrl+C (SIGINT), as the Agent always handled, plus the signal a service
        // manager uses to stop a foreground process: systemd and launchd both
        // send SIGTERM by default. Without the SIGTERM handler the default action
        // is to terminate the process outright — the writer lock would be
        // released by the kernel, but no in-flight backup would finish and no
        // disposal would run. Routing it to the same cancellation the run loop
        // and listeners already unwind makes the manager's stop a clean one.
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });

        return await AgentHost.RunAsync(args, output, error, cancellation.Token).ConfigureAwait(false);
    }
}
