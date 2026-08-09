using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FallbackPlan.TestSupport;

/// <summary>The platforms a test can be restricted to.</summary>
[Flags]
public enum TestPlatforms
{
    /// <summary>Linux and other non-Windows, non-macOS Unix hosts.</summary>
    Linux = 1,

    /// <summary>macOS.</summary>
    MacOs = 2,

    /// <summary>Windows.</summary>
    Windows = 4,

    /// <summary>Every POSIX host — the platforms sharing modes, xattrs and symlink semantics.</summary>
    Posix = Linux | MacOs,

    /// <summary>Every supported platform.</summary>
    Any = Linux | MacOs | Windows,
}

/// <summary>
/// Shared platform gating for tests whose subject is genuinely
/// platform-specific — POSIX permission bits, Windows alternate streams, the
/// privilege a symlink needs on one host and not another.
/// </summary>
/// <remarks>
/// <para>
/// The rule these types exist to enforce: <b>a test that does not run must
/// not report as passed</b>. The pattern they replace was an early
/// <c>return</c> inside the test body, which xUnit records as a pass — so a
/// green Windows run silently included several tests that asserted nothing,
/// and the count could not distinguish "verified here" from "not applicable
/// here". Skipping through the attribute records the test as skipped with
/// its reason, so the run reports what it actually checked.
/// </para>
/// <para>
/// The reason is required rather than defaulted. "Windows" states the
/// platform, which the attribute already knows; what a reader needs months
/// later is why the subject differs — and a reason that cannot be written is
/// usually a test that should have been platform-neutral.
/// </para>
/// </remarks>
public static class TestPlatform
{
    /// <summary>The platform this process is running on.</summary>
    public static TestPlatforms Current =>
        OperatingSystem.IsWindows() ? TestPlatforms.Windows
        : OperatingSystem.IsMacOS() ? TestPlatforms.MacOs
        : TestPlatforms.Linux;

    /// <summary>The skip reason for a test restricted to <paramref name="platforms"/>, or null when it should run.</summary>
    public static string? SkipReason(TestPlatforms platforms, string because) =>
        platforms.HasFlag(Current) || (platforms & Current) != 0
            ? null
            : $"Runs on {platforms} only (current: {Current}) — {because}";
}

/// <summary>
/// Restricts a test to the platforms its subject exists on. Elsewhere the
/// run reports it as skipped with its reason, never as a pass — see
/// <see cref="TestPlatform"/>.
/// </summary>
/// <remarks>
/// A condition rather than a test attribute, which is what lets one type
/// serve a plain test and a data-driven one alike. Under xUnit these were two
/// attributes, <c>PlatformFact</c> and <c>PlatformTheory</c>, because skipping
/// lived on the attribute that declared the test; MSTest evaluates a condition
/// independently of how the test is fed, so the pair collapses into this.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class PlatformConditionAttribute : ConditionBaseAttribute
{
    private readonly string? _skipReason;

    /// <summary>Restricts the test to <paramref name="platforms"/>.</summary>
    /// <param name="platforms">The platforms the test's subject exists on.</param>
    /// <param name="because">Why the subject is platform-specific.</param>
    public PlatformConditionAttribute(TestPlatforms platforms, string because)
        : base(ConditionMode.Include)
    {
        Platforms = platforms;
        _skipReason = TestPlatform.SkipReason(platforms, because);
    }

    /// <summary>The platforms this test applies to.</summary>
    public TestPlatforms Platforms { get; }

    /// <inheritdoc />
    public override bool ShouldRun => _skipReason is null;

    /// <inheritdoc />
    public override string? IgnoreMessage => _skipReason;

    /// <inheritdoc />
    public override string GroupName => "Platform";
}

/// <summary>
/// Restricts a test to an unprivileged run on the given platforms — permission
/// denial is unobservable as root, which is the normal identity inside a
/// container. Skipped rather than silently vacuous, for the same reason as
/// <see cref="PlatformConditionAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class UnprivilegedPlatformConditionAttribute : ConditionBaseAttribute
{
    private readonly string? _skipReason;

    /// <summary>Restricts the test to unprivileged runs on <paramref name="platforms"/>.</summary>
    /// <param name="platforms">The platforms the test's subject exists on.</param>
    /// <param name="because">Why the subject is platform-specific.</param>
    public UnprivilegedPlatformConditionAttribute(TestPlatforms platforms, string because)
        : base(ConditionMode.Include)
    {
        Platforms = platforms;
        _skipReason = TestPlatform.SkipReason(platforms, because)
            ?? (Environment.IsPrivilegedProcess
                ? "Requires an unprivileged process — a privileged one ignores permission bits."
                : null);
    }

    /// <summary>The platforms this test applies to.</summary>
    public TestPlatforms Platforms { get; }

    /// <inheritdoc />
    public override bool ShouldRun => _skipReason is null;

    /// <inheritdoc />
    public override string? IgnoreMessage => _skipReason;

    /// <inheritdoc />
    public override string GroupName => "Platform";
}

/// <summary>
/// Records the platform a test targets so a run can be filtered to one
/// platform's surface (<c>--filter TestCategory=Posix</c>) rather than by
/// guessing at test names.
/// </summary>
/// <remarks>
/// Under xUnit this needed a trait attribute and a discoverer to publish it.
/// MSTest reads categories directly, so the discoverer is gone and this is a
/// thin alias over the built-in category.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class PlatformTraitAttribute : TestCategoryBaseAttribute
{
    /// <summary>Records the platform this test targets.</summary>
    /// <param name="platforms">The platforms the test's subject exists on.</param>
    public PlatformTraitAttribute(TestPlatforms platforms) => Platforms = platforms;

    /// <summary>The platforms this test applies to.</summary>
    public TestPlatforms Platforms { get; }

    /// <inheritdoc />
    public override IList<string> TestCategories => [Platforms.ToString()];
}

/// <summary>
/// Assembly-level marker so the attributes above are discoverable from a
/// single using directive.
/// </summary>
[CompilerGenerated]
internal static class AssemblyMarker;
