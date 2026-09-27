using FallbackPlan.Agent;
using FallbackPlan.Domain;
using FallbackPlan.Replication;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The two test hooks the product keeps, <see cref="ServiceRuntime.ArchiveFormatVersion"/>
/// and <see cref="FanOut.ReadBackBudget"/>, belong to the flow that sets them and to
/// the work that flow starts afterwards. That is what lets a test that sets one run
/// beside every other class (ADR-0032, 2026-09 amendments): an archive created, or a
/// read-back run, anywhere else still takes the default. Establishes no product
/// requirement; it holds the suite's own isolation.
/// </summary>
[TestClass]
public sealed class TestHookScopeTests
{
    [TestMethod]
    public async Task ArchiveFormatVersion_SetInOneFlow_ReachesItsOwnWorkAndNoOtherFlow()
    {
        var (inside, outside) = await ObserveAsync(
            () => ServiceRuntime.ArchiveFormatVersion = FormatVersions.SealedDataPlane,
            () => ServiceRuntime.ArchiveFormatVersion);

        Assert.AreEqual(FormatVersions.SealedDataPlane, inside);
        Assert.AreEqual(FormatLimits.FormatVersion, outside);
    }

    [TestMethod]
    public async Task ReadBackBudget_SetInOneFlow_ReachesItsOwnWorkAndNoOtherFlow()
    {
        var (inside, outside) = await ObserveAsync(
            () => FanOut.ReadBackBudget = VerificationSampler.PeerReservoirShare + 1,
            () => FanOut.ReadBackBudget);

        Assert.AreEqual(VerificationSampler.PeerReservoirShare + 1, inside);
        Assert.AreEqual(VerificationSampler.DefaultBudget, outside);
    }

    // One flow sets the hook and holds it. While it does, this flow reads the
    // hook; then the setting flow reads it again, from work it starts only
    // after setting it, which is how a runtime a test starts would read it.
    private static async Task<(T Inside, T Outside)> ObserveAsync<T>(Action setHook, Func<T> readHook)
    {
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var setter = Task.Run(async () =>
        {
            setHook();
            holding.SetResult();
            await release.Task;
            return await Task.Run(readHook);
        });

        await holding.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var outside = readHook();
        release.SetResult();

        return (await setter.WaitAsync(TimeSpan.FromSeconds(30)), outside);
    }
}
