using System.Runtime.CompilerServices;
using Xunit.Abstractions;
using Xunit.Sdk;

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
/// A test that only applies to some platforms. Elsewhere it is reported as
/// skipped with its reason, never as a pass — see <see cref="TestPlatform"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PlatformFactAttribute : FactAttribute
{
    /// <summary>Restricts the test to <paramref name="platforms"/>.</summary>
    /// <param name="platforms">The platforms the test's subject exists on.</param>
    /// <param name="because">Why the subject is platform-specific.</param>
    public PlatformFactAttribute(TestPlatforms platforms, string because)
    {
        Platforms = platforms;
        Skip = TestPlatform.SkipReason(platforms, because);
    }

    /// <summary>The platforms this test applies to.</summary>
    public TestPlatforms Platforms { get; }
}

/// <summary>A <see cref="TheoryAttribute"/> restricted to some platforms.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PlatformTheoryAttribute : TheoryAttribute
{
    /// <summary>Restricts the theory to <paramref name="platforms"/>.</summary>
    /// <param name="platforms">The platforms the theory's subject exists on.</param>
    /// <param name="because">Why the subject is platform-specific.</param>
    public PlatformTheoryAttribute(TestPlatforms platforms, string because)
    {
        Platforms = platforms;
        Skip = TestPlatform.SkipReason(platforms, because);
    }

    /// <summary>The platforms this theory applies to.</summary>
    public TestPlatforms Platforms { get; }
}

/// <summary>
/// A test whose subject only holds for an unprivileged process — permission
/// denial is unobservable as root, which is the normal identity inside a
/// container. Skipped rather than silently vacuous, for the same reason as
/// the platform attributes.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UnprivilegedPlatformFactAttribute : FactAttribute
{
    /// <summary>Restricts the test to unprivileged runs on <paramref name="platforms"/>.</summary>
    /// <param name="platforms">The platforms the test's subject exists on.</param>
    /// <param name="because">Why the subject is platform-specific.</param>
    public UnprivilegedPlatformFactAttribute(TestPlatforms platforms, string because)
    {
        Platforms = platforms;
        Skip = TestPlatform.SkipReason(platforms, because)
            ?? (Environment.IsPrivilegedProcess
                ? "Requires an unprivileged process — a privileged one ignores permission bits."
                : null);
    }

    /// <summary>The platforms this test applies to.</summary>
    public TestPlatforms Platforms { get; }
}

/// <summary>
/// Marks a test class or method with the platform it targets, so a run can be
/// filtered to one platform's surface (<c>--filter Platform=Posix</c>) rather
/// than by guessing at test names.
/// </summary>
[TraitDiscoverer("FallbackPlan.TestSupport.PlatformTraitDiscoverer", "FallbackPlan.TestSupport")]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class PlatformTraitAttribute : Attribute, ITraitAttribute
{
    /// <summary>Records the platform this test targets.</summary>
    /// <param name="platforms">The platforms the test's subject exists on.</param>
    public PlatformTraitAttribute(TestPlatforms platforms) => Platforms = platforms;

    /// <summary>The platforms this test applies to.</summary>
    public TestPlatforms Platforms { get; }
}

/// <summary>Publishes <see cref="PlatformTraitAttribute"/> as the <c>Platform</c> trait.</summary>
public sealed class PlatformTraitDiscoverer : ITraitDiscoverer
{
    /// <inheritdoc />
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        ArgumentNullException.ThrowIfNull(traitAttribute);

        var platforms = (TestPlatforms)traitAttribute.GetConstructorArguments().First()!;
        yield return new KeyValuePair<string, string>("Platform", platforms.ToString());
    }
}

/// <summary>
/// Assembly-level marker so the attributes above are discoverable from a
/// single using directive.
/// </summary>
[CompilerGenerated]
internal static class AssemblyMarker;
