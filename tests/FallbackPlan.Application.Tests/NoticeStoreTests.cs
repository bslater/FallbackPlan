using FallbackPlan.Application;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// Notices are the channel that survives being ignored, which is exactly why
/// they must not accumulate: one that outlives its cause, or one that shows a
/// figure from last week, teaches an operator to stop reading them.
/// </summary>
[TestClass]
public sealed class NoticeStoreTests
{
    private string _state = null!;

    [TestInitialize]
    public void SetUp()
    {
        _state = Path.Combine(Path.GetTempPath(), $"fbp-notices-{Guid.NewGuid():N}");
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
    public void Raise_TheSameKeyTwice_KeepsOneNoticeAndShowsTheLatestMessage()
    {
        // The figures in a notice move — bytes short, objects unproven. The
        // store used to return the existing notice untouched, so the first
        // observation was displayed forever while reality drifted away from it.
        var store = NoticeStore.Open(_state);

        var first = store.Raise("quota-low:friend", "3 GiB short", 1_000);
        var second = store.Raise("quota-low:friend", "9 GiB short", 5_000);

        Assert.AreEqual(first.Id, second.Id, "one condition is one notice, not a pile");
        Assert.AreEqual("9 GiB short", second.Message);
        Assert.AreEqual(1_000UL, second.RaisedAt, "the first sighting is the useful timestamp — since when");
        Assert.HasCount(1, store.Unacknowledged);
        Assert.AreEqual("9 GiB short", NoticeStore.Open(_state).Unacknowledged.Single().Message);
    }

    [TestMethod]
    public void Resolve_AConditionThatCleared_StopsItSurfacingWithoutAHuman()
    {
        // The drive is plugged back in. Nobody should have to dismiss a warning
        // about a problem that no longer exists.
        var store = NoticeStore.Open(_state);
        store.Raise("destination-unavailable:vault", "the drive is unplugged", 1_000);

        Assert.IsTrue(store.Resolve("destination-unavailable:vault", 2_000));

        Assert.IsEmpty(store.Unacknowledged);
        Assert.IsEmpty(NoticeStore.Open(_state).Unacknowledged);

        // On record, not erased: what was once wrong stays visible in history.
        Assert.AreEqual(2_000UL, store.Notices.Single().AcknowledgedAt);
    }

    [TestMethod]
    public void Resolve_NothingOutstanding_SaysSoRatherThanInventingANotice()
    {
        var store = NoticeStore.Open(_state);

        Assert.IsFalse(store.Resolve("destination-unavailable:vault", 2_000));
        Assert.IsEmpty(store.Notices);
    }

    [TestMethod]
    public void Resolve_ThenTheConditionReturns_RaisesAFreshNotice()
    {
        // A recurrence is news again — it must not be swallowed by the
        // resolved one sharing its key.
        var store = NoticeStore.Open(_state);
        var first = store.Raise("destination-unavailable:vault", "the drive is unplugged", 1_000);
        store.Resolve("destination-unavailable:vault", 2_000);

        var second = store.Raise("destination-unavailable:vault", "the drive is unplugged again", 3_000);

        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreEqual(3_000UL, second.RaisedAt);
        Assert.HasCount(1, store.Unacknowledged);
    }
}
