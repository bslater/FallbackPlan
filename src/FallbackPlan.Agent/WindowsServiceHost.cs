using System.Runtime.Versioning;
using System.ServiceProcess;

namespace FallbackPlan.Agent;

/// <summary>
/// Bridges the Windows Service Control Manager to <see cref="AgentHost"/>
/// (ADR-0033). The SCM starts the process; this reports it running, and on the
/// SCM's stop it cancels the same token an interactive Ctrl+C would — then waits
/// for the agent to unwind and release the writer lock before the process exits.
/// </summary>
/// <remarks>
/// The agent's verb and options come from the service's own image path (the
/// command line the registration recorded), so <see cref="OnStart"/>'s
/// start-parameter <c>args</c> are deliberately ignored. If the agent exits on
/// its own — a startup failure such as a missing passphrase, or a
/// <c>--once</c> pass — the service transitions to stopped rather than lingering
/// as "running" over a process that has gone.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceHost : ServiceBase
{
    private readonly string[] _arguments;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly CancellationTokenSource _stopping = new();
    private Task<int>? _run;

    private WindowsServiceHost(string[] arguments, TextWriter output, TextWriter error)
    {
        _arguments = arguments;
        _output = output;
        _error = error;
        ServiceName = "FallbackPlan";
        CanShutdown = true;
    }

    /// <summary>
    /// Runs the agent under the SCM, blocking until the manager stops it, and
    /// returns the agent's exit code.
    /// </summary>
    public static int Run(string[] arguments, TextWriter output, TextWriter error)
    {
        using var host = new WindowsServiceHost(arguments, output, error);
        Run(host);
        return host._run?.GetAwaiter().GetResult() ?? 0;
    }

    /// <summary>Whether the SCM, rather than a console, started this process.</summary>
    /// <remarks>
    /// A Windows service runs in a non-interactive window station, so
    /// <see cref="Environment.UserInteractive"/> is false for it and true for an
    /// operator at a console. This is the same signal the framework's own
    /// <c>IsWindowsService</c> helper falls back to; if it ever proves wrong for
    /// a deployment, the parent-process check is the sturdier replacement.
    /// </remarks>
    public static bool LaunchedByServiceControlManager() => !Environment.UserInteractive;

    /// <inheritdoc/>
    protected override void OnStart(string[] args)
    {
        _run = AgentHost.RunAsync(_arguments, _output, _error, _stopping.Token);

        // If the agent stops on its own, tell the SCM rather than leaving the
        // service reported as running over a finished process.
        _run.ContinueWith(
            _ =>
            {
                try
                {
                    Stop();
                }
                catch (InvalidOperationException)
                {
                    // The SCM is already stopping us; nothing to do.
                }
            },
            TaskScheduler.Default);
    }

    /// <inheritdoc/>
    protected override void OnStop() => StopAndWait();

    /// <inheritdoc/>
    protected override void OnShutdown() => StopAndWait();

    private void StopAndWait()
    {
        _stopping.Cancel();
        try
        {
            _run?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // The clean-shutdown path; not a failure.
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stopping.Dispose();
        }

        base.Dispose(disposing);
    }
}
