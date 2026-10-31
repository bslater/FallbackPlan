using FallbackPlan.Application;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// The restores under way of a repository that no service runs, kept where a
/// service looks for them (FR-GC-003; ADR-0172 Amendment 6). A restore in
/// direct mode holds a marker for its run in a state directory while it runs,
/// and a service whose state directory it is asks whether any is held for
/// a set's repository before it collects, syncs or instructs a peer. A
/// marker no process holds was left by one that has gone, and says nothing.
/// </summary>
[TestClass]
public sealed class RestoresUnderWayTests
{
    private static readonly RepositoryId Restored = RepositoryId.FromBytes(Enumerable.Repeat((byte)0x11, 16).ToArray());
    private static readonly RepositoryId Other = RepositoryId.FromBytes(Enumerable.Repeat((byte)0x22, 16).ToArray());

    private string _state = null!;

    [TestInitialize]
    public void SetUp()
    {
        _state = Path.Combine(Path.GetTempPath(), $"fbp-restores-under-way-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_state);
    }

    [TestCleanup]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    [TestMethod]
    public void AStateDirectoryNoRunHasMarked_HasNoRestoreUnderWay()
    {
        Assert.IsFalse(RestoresUnderWay.AnyFor(_state, Restored));
        Assert.IsFalse(RestoresUnderWay.AnyFor(Path.Combine(_state, "absent"), Restored));
    }

    [TestMethod]
    public void ARunHeld_SaysItsRepositoryIsBeingRestored_AndNoOther()
    {
        using var run = RestoresUnderWay.Hold(_state, Restored, "0123456789abcdef");

        Assert.IsTrue(RestoresUnderWay.AnyFor(_state, Restored));
        Assert.IsFalse(RestoresUnderWay.AnyFor(_state, Other), "a run of one repository says nothing of another");
    }

    [TestMethod]
    public void ARunLetGo_SaysNothing_AndLeavesNoMarker()
    {
        RestoresUnderWay.Hold(_state, Restored, "0123456789abcdef").Dispose();

        // Looked for before anything asks, since a look clears a marker nobody holds.
        Assert.IsEmpty(Directory.EnumerateFiles(_state, "*", SearchOption.AllDirectories));
        Assert.IsFalse(RestoresUnderWay.AnyFor(_state, Restored));
    }

    [TestMethod]
    public void TwoRunsOfOneRepository_HoldItUntilBothEnd()
    {
        var first = RestoresUnderWay.Hold(_state, Restored, "0123456789abcdef");
        using (RestoresUnderWay.Hold(_state, Restored, "fedcba9876543210"))
        {
            first.Dispose();
            Assert.IsTrue(RestoresUnderWay.AnyFor(_state, Restored), "the second run is still under way");
        }

        Assert.IsFalse(RestoresUnderWay.AnyFor(_state, Restored));
    }

    [TestMethod]
    public void ARunNamedOtherwiseThanByHexDigits_IsRefused_AndMarksNothing()
    {
        Assert.ThrowsExactly<ArgumentException>(() => RestoresUnderWay.Hold(_state, Restored, Path.Combine("..", "escaped")));
        Assert.ThrowsExactly<ArgumentException>(() => RestoresUnderWay.Hold(_state, Restored, "0123456789ABCDEF"));
        Assert.ThrowsExactly<ArgumentException>(() => RestoresUnderWay.Hold(_state, Restored, string.Empty));

        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(_state, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public void AMarkerNoProcessHolds_SaysNothing_AndIsCleared()
    {
        // What a run killed before it could let go leaves: the marker, with
        // nothing holding it.
        var left = Path.Combine(_state, "restores-under-way", Restored.ToString(), "0123456789abcdef.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(left)!);
        File.WriteAllBytes(left, []);

        Assert.IsFalse(RestoresUnderWay.AnyFor(_state, Restored));
        Assert.IsFalse(File.Exists(left), "a marker nobody holds is cleared");
    }
}
