using FallbackPlan.Application;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// The writer-role exclusion (ADR-0028 §4, FR-SVC-002). These tests exist
/// because the failure they prevent does not look like a bug: two processes
/// sharing a state directory share a writer identity, and the first symptom is
/// T-18's identity-cloning alarm rather than an error message.
/// </summary>
[TestClass]
public sealed class StateDirectoryLockTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fbp-lock-tests", Guid.NewGuid().ToString("n"));

    public StateDirectoryLockTests() => Directory.CreateDirectory(_root);

    [TestMethod]
    public void The_first_caller_takes_the_writer_role()
    {
        using var held = StateDirectoryLock.Acquire(_root, StateDirectoryLock.ServiceRole);

        Assert.AreEqual(StateDirectoryLock.ServiceRole, held.Owner.Role);
        Assert.AreEqual(Environment.ProcessId, held.Owner.ProcessId);
        Assert.IsTrue(File.Exists(StateDirectoryLock.PathFor(_root)));
    }

    [TestMethod]
    public void A_second_caller_is_refused_and_the_holder_is_named()
    {
        using var held = StateDirectoryLock.Acquire(_root, StateDirectoryLock.ServiceRole);

        var refused = Assert.ThrowsExactly<ClientStateException>(
            () => StateDirectoryLock.Acquire(_root, StateDirectoryLock.DirectRole));

        // "Busy" is not an answer an operator can act on. The message must
        // say who holds it — that is the whole point of the owner file.
        Assert.Contains(StateDirectoryLock.ServiceRole, refused.Message, StringComparison.Ordinal);
        Assert.Contains(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            refused.Message, StringComparison.Ordinal);
        Assert.Contains(_root, refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Releasing_the_role_lets_the_next_caller_take_it()
    {
        using (StateDirectoryLock.Acquire(_root, StateDirectoryLock.DirectRole))
        {
            Assert.IsFalse(StateDirectoryLock.TryAcquire(_root, StateDirectoryLock.ServiceRole, out _));
        }

        Assert.IsTrue(StateDirectoryLock.TryAcquire(_root, StateDirectoryLock.ServiceRole, out var second));
        second!.Dispose();
    }

    [TestMethod]
    public void Describing_the_holder_reports_nothing_when_the_role_is_free()
    {
        Assert.IsNull(StateDirectoryLock.DescribeHolderOrNull(_root));

        using var held = StateDirectoryLock.Acquire(_root, StateDirectoryLock.ServiceRole);
        var description = StateDirectoryLock.DescribeHolderOrNull(_root);

        Assert.IsNotNull(description);
        Assert.Contains(StateDirectoryLock.ServiceRole, description, StringComparison.Ordinal);
    }

    [TestMethod]
    public void An_orphaned_owner_file_does_not_block_acquisition()
    {
        // The owner file is informational. If a crash leaves one behind, the
        // operating system has already released the handle — which is exactly
        // why the exclusion is a handle and not a recorded pid.
        File.WriteAllText(
            Path.Combine(_root, "writer.owner"),
            """{"role":"service","process":"ghost","pid":999999,"acquired_at":"2020-01-01T00:00:00.0000000+00:00"}""");

        using var held = StateDirectoryLock.Acquire(_root, StateDirectoryLock.DirectRole);
        Assert.AreEqual(StateDirectoryLock.DirectRole, held.Owner.Role);
    }

    [TestMethod]
    public void A_stale_holder_that_is_no_longer_running_is_reported_as_such()
    {
        using var held = StateDirectoryLock.Acquire(_root, StateDirectoryLock.ServiceRole);

        // Rewrite the owner file to name a process that cannot exist, while
        // the real handle stays held: the refusal must still happen, and the
        // message must not claim a live holder it cannot see.
        File.WriteAllText(
            Path.Combine(_root, "writer.owner"),
            """{"role":"service","process":"ghost","pid":2147483646,"acquired_at":"2020-01-01T00:00:00.0000000+00:00"}""");

        var refused = Assert.ThrowsExactly<ClientStateException>(
            () => StateDirectoryLock.Acquire(_root, StateDirectoryLock.DirectRole));

        Assert.Contains("no longer running", refused.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
