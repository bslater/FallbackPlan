using System.Diagnostics;
using System.Runtime.InteropServices;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The agent's behaviour under a service manager's stop signal (ADR-0033): the
/// shipped apphost, run as it would be by systemd or launchd, shuts down cleanly
/// on <c>SIGTERM</c> — exit 0 — and releases the writer lock so the next start
/// can take it. This is the load-bearing proof that the manager's stop is
/// graceful rather than a kill; the Windows SCM stop path is verified manually
/// (it needs a live Service Control Manager, which CI has no way to provide).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AgentServiceLifetimeTests : IDisposable
{
    private const int Sigterm = 15;

    private readonly HostHarness _harness = new();

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix,
        "SIGTERM is a POSIX signal; the Windows SCM stop path is verified manually (ADR-0033).")]
    public async Task Run_OnSigterm_ShutsDownCleanlyAndFreesTheWriterLock()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteConfiguration("every 1h");

        using (var agent = StartAgentRun())
        {
            await WaitForListeningAsync(agent);

            // The signal systemd and launchd send to stop a service. Process.Kill
            // would send SIGKILL, which proves nothing about a graceful stop.
            Assert.AreEqual(0, kill(agent.Id, Sigterm), "kill(SIGTERM) failed");

            Assert.IsTrue(agent.WaitForExit(TimeSpan.FromSeconds(30)), "the agent did not exit after SIGTERM");
            Assert.AreEqual(0, agent.ExitCode, "SIGTERM is a clean shutdown, so exit 0");
        }

        // The writer lock is free: a fresh run acquires it and comes up. If the
        // first agent had left it held, this one would refuse and never listen.
        using var second = StartAgentRun();
        await WaitForListeningAsync(second);
        _ = kill(second.Id, Sigterm);
        second.WaitForExit(TimeSpan.FromSeconds(30));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix,
        "the apphost harness reads the agent's stdout line protocol; kept beside the SIGTERM test")]
    public async Task Run_CommandedToRestart_RecyclesInPlaceWithoutExiting()
    {
        // The restart is an in-process recycle (ADR-0049): the same process
        // tears its runtime down — listeners, queue, writer role — and
        // starts it again, so the behaviour is identical whatever the
        // platform's service manager would do about an exit. Proven on the
        // shipped apphost: one process, two "listening on" lines, and a
        // command answered after the second.
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteConfiguration("every 1h");

        using var agent = StartAgentRun();
        await WaitForListeningAsync(agent);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using (var client = await Api.Transport.LocalServiceClient.ConnectAsync(
            _harness.StateDirectory, "lifetime-test", timeout.Token))
        {
            // A fresh installation has no accounts: create the owner through
            // the bootstrap window, sign in, and restart as the owner.
            Assert.IsNotInstanceOfType<Api.ServiceError>(
                await client.ExecuteAsync(new Api.CreateUserCommand("ben", "A-good-passw0rd9"), timeout.Token));
            Assert.IsInstanceOfType<Api.SessionResult>(
                await client.ExecuteAsync(new Api.LoginCommand("ben", "A-good-passw0rd9"), timeout.Token));
            Assert.IsInstanceOfType<Api.AcknowledgedResult>(
                await client.ExecuteAsync(new Api.RestartServiceCommand(), timeout.Token));
        }

        // The same process listens again — never having exited in between.
        await WaitForListeningAsync(agent);
        Assert.IsFalse(agent.HasExited, "a restart recycles in place; the process must not exit");

        // And it answers: the recycled service is a working service.
        await using (var reconnected = await Api.Transport.LocalServiceClient.ConnectAsync(
            _harness.StateDirectory, "lifetime-test", timeout.Token))
        {
            Assert.IsInstanceOfType<Api.ServiceDescriptionResult>(
                await reconnected.ExecuteAsync(new Api.DescribeServiceCommand(), timeout.Token));
        }

        _ = kill(agent.Id, Sigterm);
        agent.WaitForExit(TimeSpan.FromSeconds(30));
    }

    private Process StartAgentRun()
    {
        var fileName = OperatingSystem.IsWindows() ? "FallbackPlan.Agent.exe" : "FallbackPlan.Agent";
        var apphost = Path.Combine(AppContext.BaseDirectory, fileName);
        Assert.IsTrue(File.Exists(apphost), $"the agent apphost is not in the test output directory: {apphost}");

        var info = new ProcessStartInfo
        {
            FileName = apphost,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in new[]
        {
            "run",
            "--archives", _harness.ArchivesRoot,
            "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable,
            "--poll-seconds", "3600",
        })
        {
            info.ArgumentList.Add(argument);
        }

        // The child inherits this process's environment, which the harness seeded
        // with the passphrase variable; make it explicit so the intent is clear.
        info.Environment[_harness.PassphraseVariable] =
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable);

        return Process.Start(info)!;
    }

    private static async Task WaitForListeningAsync(Process agent)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await agent.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
        {
            if (line.Contains("listening on", StringComparison.Ordinal))
            {
                return;
            }
        }

        Assert.Fail($"the agent exited before it began listening; stderr:\n{await agent.StandardError.ReadToEndAsync()}");
    }

    public void Dispose()
    {
        _harness.Dispose();
    }
}
