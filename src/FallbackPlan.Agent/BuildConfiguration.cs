namespace FallbackPlan.Agent;

/// <summary>
/// What this build of the service was compiled as, for the one rule a
/// developer's build relaxes (ADR-0051 Amendment 2).
/// </summary>
/// <remarks>
/// A property rather than a constant: a constant folds every test of it at
/// compile time, leaving the other configuration's branch unreachable, and
/// this repository builds that warning as an error.
/// </remarks>
internal static class BuildConfiguration
{
    /// <summary>
    /// Whether a local destination may be chosen on a volume or physical
    /// drive one of its set's roots lives on. Only a Debug build allows it,
    /// so that a developer's one disk can run the product end to end. A
    /// Release build (every build CI makes, and every one that ships)
    /// refuses it.
    /// </summary>
    public static bool AllowsSameDrivePlacement =>
#if DEBUG
        true;
#else
        false;
#endif
}
