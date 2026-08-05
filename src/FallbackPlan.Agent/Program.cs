using FallbackPlan.Agent;

// The process entry point, and nothing else: the host lives in AgentHost so
// a test can drive it. Ctrl+C is a process concern, so it stays here.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return await AgentHost.RunAsync(args, Console.Out, Console.Error, cancellation.Token).ConfigureAwait(false);
