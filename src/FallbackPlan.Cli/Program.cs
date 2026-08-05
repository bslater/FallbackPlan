using FallbackPlan.Cli;

// The process entry point, and nothing else: every command lives in
// CliApplication so it can be invoked directly by a test.
return await CliApplication.RunAsync(args).ConfigureAwait(false);
