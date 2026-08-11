using FallbackPlan.Agent;

// The process entry point, and nothing else: the host and its lifetime live in
// ServiceProcessHost so a test can drive them. How the operating system starts
// and stops this process — a console, systemd, launchd, or the Windows SCM — is
// that type's concern (ADR-0033).
return await ServiceProcessHost.RunAsync(args, Console.Out, Console.Error).ConfigureAwait(false);
