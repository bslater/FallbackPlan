using System.Diagnostics;
using System.Text.Json;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The writer-role exclusion raced by <em>real operating-system
/// processes</em> (ADR-0028 §4; FR-SVC-002). Every other lock test is
/// in-process, where .NET's own <c>FileShare</c> bookkeeping answers before
/// the kernel is ever asked — these spawn the shipped apphosts and cross the
/// kernel path: a second process is refused naming the holder, a killed
/// holder's role frees itself with no stale-lock heuristic, and two
/// processes with different state directories write one repository at once,
/// because the lock is on the state directory and never the repository.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ProcessRaceTests : IDisposable
{
    private readonly HostHarness _harness = new();

    private static string ApphostPath(string name)
    {
        var fileName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        Assert.IsTrue(File.Exists(path), $"the {name} apphost is not in the test output directory.");
        return path;
    }

    private Process Start(string apphost, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = apphost,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // Children inherit the environment, but the passphrase variable is
        // the load-bearing part of the invocation — set it explicitly.
        info.Environment[_harness.PassphraseVariable] =
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable);

        return Process.Start(info)!;
    }

    private static async Task<(int ExitCode, string Output, string Error)> WaitAsync(Process process)
    {
        // Reads started before the wait: a child blocked on a full pipe
        // buffer never exits, and a wait that never returns names nothing.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await output, await error);
    }

    [TestMethod]
    public async Task WriterRole_ARealSecondProcess_IsRefusedNamingTheHolder()
    {
        await _harness.CreateRepositoryAsync();

        // This test process holds the role the way the service does. The
        // contender is a REAL child process running the shipped CLI apphost,
        // so the exclusion crossing is the kernel's file lock — the path no
        // in-process test can reach (FileShare is checked inside one runtime
        // before the operating system is ever asked).
        using var held = StateDirectoryLock.Acquire(_harness.StateDirectory, StateDirectoryLock.ServiceRole);

        var contender = Start(
            ApphostPath("FallbackPlan.Cli"),
            "rebuild-index",
            "--repo", _harness.RepositoryPath,
            "--passphrase-env", _harness.PassphraseVariable,
            "--state", _harness.StateDirectory);

        var (exitCode, _, error) = await WaitAsync(contender);

        // Refused, exit 1, and the holder is NAMED — role and pid, which the
        // owner file records; the process name is best-effort and not
        // asserted (ADR-0028 §4, phase-2 A3 acceptance).
        Assert.AreEqual(1, exitCode, error);
        Assert.Contains("writer role", error, StringComparison.Ordinal);
        Assert.Contains(StateDirectoryLock.ServiceRole, error, StringComparison.Ordinal);
        Assert.Contains($"pid {Environment.ProcessId}", error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task WriterRole_TheHolderProcessIsKilled_TheRoleFreesItselfWithNoStaleLockHeuristic()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        // A real Agent holds the role for its lifetime.
        var holder = Start(
            ApphostPath("FallbackPlan.Agent"),
            "run",
            "--archives", _harness.ArchivesRoot,
            "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable);

        // Reads started now so the child can never block on a full pipe.
        var holderOutput = holder.StandardOutput.ReadToEndAsync();
        var holderError = holder.StandardError.ReadToEndAsync();

        try
        {
            // Wait for the child to record itself as owner. Polling the lock
            // itself would be a race — a probe acquisition held at the wrong
            // moment would make the CHILD the refused party — so the poll
            // reads the informational owner file instead.
            var ownerPath = Path.Combine(_harness.StateDirectory, "writer.owner");
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);

            while (true)
            {
                Assert.IsFalse(
                    holder.HasExited,
                    $"the Agent exited before taking the role: {await holderError}");

                if (File.Exists(ownerPath) && TryReadOwnerPid(ownerPath) == holder.Id)
                {
                    break;
                }

                Assert.IsTrue(DateTime.UtcNow < deadline, "the Agent never recorded the writer role.");
                await Task.Delay(100);
            }

            // Kill — no orderly shutdown, no Dispose, no owner-file cleanup.
            holder.Kill(entireProcessTree: true);
            await holder.WaitForExitAsync();

            // The operating system releases the handle with the process, so
            // the role frees itself: no stale-lock heuristic, no timeout, no
            // pid-file second-guessing (ADR-0028 §4). The stale owner file
            // left behind must not matter — the handle is the truth.
            //
            // "With the process" is not "with WaitForExit": on Windows a tree
            // kill signals the process object before every handle has been
            // torn down, so the freed role is polled against a deadline. The
            // deadline absorbs release latency only — a genuinely stuck lock
            // still fails, because nothing here ever deletes the file.
            StateDirectoryLock? reacquired = null;
            var freedBy = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!StateDirectoryLock.TryAcquire(_harness.StateDirectory, StateDirectoryLock.DirectRole, out reacquired))
            {
                Assert.IsTrue(
                    DateTime.UtcNow < freedBy,
                    "the writer role stayed taken after its holder was killed.");
                await Task.Delay(100);
            }

            reacquired!.Dispose();
        }
        finally
        {
            if (!holder.HasExited)
            {
                holder.Kill(entireProcessTree: true);
            }

            await holderOutput;
            await holderError;
        }
    }

    [TestMethod]
    public async Task MultiDevice_TwoProcessesWithDifferentStateDirectories_BothWriteTheSameRepository()
    {
        // ADR-0028 §4's boundary, pinned from the other side: the lock is on
        // the STATE DIRECTORY, never the repository. Two state directories
        // are two writers, and 04 §8 lets two writers commit to one
        // repository concurrently with no coordination at all — a
        // repository-level lock must never appear.
        await _harness.CreateRepositoryAsync();

        var stateA = Path.Combine(_harness.WorkPath, "device-a-state");
        var stateB = Path.Combine(_harness.WorkPath, "device-b-state");
        var sourceA = Path.Combine(_harness.WorkPath, "device-a-source");
        var sourceB = Path.Combine(_harness.WorkPath, "device-b-source");

        WriteSource(sourceA, seed: 1);
        WriteSource(sourceB, seed: 2);

        var cli = ApphostPath("FallbackPlan.Cli");
        var first = Start(cli, "backup", sourceA, "--repo", _harness.RepositoryPath, "--passphrase-env", _harness.PassphraseVariable, "--state", stateA);
        var second = Start(cli, "backup", sourceB, "--repo", _harness.RepositoryPath, "--passphrase-env", _harness.PassphraseVariable, "--state", stateB);

        var (exitA, _, errorA) = await WaitAsync(first);
        var (exitB, _, errorB) = await WaitAsync(second);

        Assert.AreEqual(0, exitA, errorA);
        Assert.AreEqual(0, exitB, errorB);

        // Both snapshots are discoverable in the one store.
        var snapshots = Directory
            .EnumerateFiles(Path.Combine(_harness.RepositoryPath, "snapshots"), "*", SearchOption.AllDirectories)
            .Count();
        Assert.AreEqual(2, snapshots);
    }

    private static void WriteSource(string root, int seed)
    {
        Directory.CreateDirectory(root);
        var random = new Random(seed);

        // Enough incompressible data that the two backups genuinely overlap
        // in time rather than running back to back by accident.
        for (var i = 0; i < 4; i++)
        {
            var content = new byte[512 * 1024];
            random.NextBytes(content);
            File.WriteAllBytes(Path.Combine(root, $"file-{i}.bin"), content);
        }
    }

    private static int? TryReadOwnerPid(string ownerPath)
    {
        try
        {
            return JsonSerializer.Deserialize<WriterLockOwner>(File.ReadAllText(ownerPath))?.ProcessId;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _harness.Dispose();
}
