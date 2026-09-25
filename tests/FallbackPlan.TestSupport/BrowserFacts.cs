using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FallbackPlan.TestSupport;

/// <summary>
/// Restricts a test to runs where a real browser is provisioned — the
/// dedicated DOM-suite CI job, or a developer who has installed one and set
/// <c>FALLBACKPLAN_DOM_TESTS=1</c>. Everywhere else — the three-OS build
/// matrix, the source-archive sandbox, the hostile-locale run — the test
/// reports as <b>skipped</b> with this reason, never as a pass (the same rule
/// <see cref="PlatformConditionAttribute"/> exists for: a test that does not
/// run must not report as passed).
/// </summary>
/// <remarks>
/// An environment opt-in rather than a probe for the browser binary, because
/// the probe would be a guess about another tool's install layout and a wrong
/// guess fails a job that never asked for a browser. The one job that sets
/// the variable is also the one that installs the browser, so the two cannot
/// drift apart.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class BrowserConditionAttribute : ConditionBaseAttribute
{
    /// <summary>The opt-in variable the DOM-suite job sets.</summary>
    public const string Variable = "FALLBACKPLAN_DOM_TESTS";

    /// <summary>Restricts the test to browser-provisioned runs.</summary>
    public BrowserConditionAttribute()
        : base(ConditionMode.Include)
    {
    }

    /// <inheritdoc />
    public override bool ShouldRun => Environment.GetEnvironmentVariable(Variable) == "1";

    /// <inheritdoc />
    public override string? IgnoreMessage =>
        $"Runs only where a browser is provisioned ({Variable}=1) — the DOM-suite job installs one; this run did not.";

    /// <inheritdoc />
    public override string GroupName => "Browser";
}
