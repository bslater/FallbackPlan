using FallbackPlan.Recovery;

// The process entry point, and nothing else: the commands live in
// RecoveryHost so a test can drive the tool that recovery depends on.
return await RecoveryHost.RunAsync(args, Console.Out, Console.Error).ConfigureAwait(false);
