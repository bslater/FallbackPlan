using FallbackPlan.Web;

// The process entry point, and nothing else: the host lives in WebConsoleHost
// so a test can drive it against a fake service client. Ctrl+C and SIGTERM are
// the hosting layer's business — the web host's lifetime already turns both
// into a clean shutdown.
return await WebConsoleHost.RunAsync(args, Console.Out, Console.Error).ConfigureAwait(false);
