using Xunit;

namespace FallbackPlan.Repository.FuzzTests;

/// <summary>
/// Placeholder proving this project is wired into the solution and discovered by
/// the test runner. Parser fuzzing is Wave F3.
///
/// An empty test project reports "no test available" and still leaves CI green,
/// so this exists to make the absence of real tests visible rather than silent.
/// Delete it once real tests land.
/// </summary>
public sealed class WiringTests
{
    [Fact]
    public void Project_is_discovered_by_the_test_runner()
    {
        Assert.True(true, "Replace this project's placeholder with real tests.");
    }
}
