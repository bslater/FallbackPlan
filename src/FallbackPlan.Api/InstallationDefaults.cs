namespace FallbackPlan.Api;

/// <summary>
/// Where a path-less invocation finds the installation (FR-SVC-016) — every
/// process of it: the service, the web console and the CLI resolve the SAME
/// default state directory, so a default install of the whole solution works
/// together without a single path argument. A command line is for doing
/// something specific — a second install, a harness, a service unit naming
/// other paths — so the path flags override these, and the environment
/// variables sit in between for redirecting a whole session.
/// </summary>
/// <remarks>
/// <para>
/// The default is the <b>machine-wide</b> data directory —
/// <c>%ProgramData%\FallbackPlan</c> on Windows,
/// <c>/Library/Application Support/FallbackPlan</c> on macOS,
/// <c>/var/lib/fallbackplan</c> elsewhere — because the installation is
/// machine-wide by design: one service holds the writer role and every
/// client on the machine talks to it (ADR-0028). An existing machine root is
/// always used; a missing one is created on first touch where permissions
/// allow.
/// </para>
/// <para>
/// Only when the machine root neither exists nor can be created (an
/// unelevated first run on a platform whose system directory is root-only)
/// does the resolution fall back to the per-user profile
/// (<c>~/.local/share/fallbackplan</c> and its platform equivalents). The
/// existence check runs first precisely so this split cannot divide an
/// installation: once anything has created the machine root, every later
/// process resolves to it regardless of privilege.
/// </para>
/// </remarks>
public static class InstallationDefaults
{
    /// <summary>Environment override for the default state directory.</summary>
    public const string StateVariable = "FALLBACKPLAN_STATE";

    /// <summary>Environment override for the default archives root.</summary>
    public const string ArchivesVariable = "FALLBACKPLAN_ARCHIVES";

    /// <summary>The state directory a path-less invocation uses.</summary>
    public static string StateDirectory =>
        Environment.GetEnvironmentVariable(StateVariable) is { Length: > 0 } state
            ? state
            : Path.Combine(ResolveRoot(), "state");

    /// <summary>The archives root a path-less invocation uses.</summary>
    public static string ArchivesRoot =>
        Environment.GetEnvironmentVariable(ArchivesVariable) is { Length: > 0 } archives
            ? archives
            : Path.Combine(ResolveRoot(), "archives");

    /// <summary>The machine-wide root the resolution prefers.</summary>
    public static string MachineRoot =>
        OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "FallbackPlan")
            : OperatingSystem.IsMacOS()
                ? "/Library/Application Support/FallbackPlan"
                : "/var/lib/fallbackplan";

    /// <summary>The per-user root the resolution falls back to.</summary>
    public static string ProfileRoot => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "fallbackplan");

    private static string ResolveRoot()
    {
        var machine = MachineRoot;
        if (Directory.Exists(machine))
        {
            return machine;
        }

        try
        {
            Directory.CreateDirectory(machine);
            return machine;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ProfileRoot;
        }
    }
}
