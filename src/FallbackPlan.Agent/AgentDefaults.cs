namespace FallbackPlan.Agent;

/// <summary>
/// Where a bare invocation finds the installation (FR-SVC-016): the platform
/// profile's data directory. A command line is for doing something specific —
/// a second install, a harness, a service unit naming system paths — so the
/// path flags override these, and the environment variables sit in between
/// for redirecting a whole session without composing a command line.
/// </summary>
/// <remarks>
/// The profile root is the per-user local application data directory —
/// <c>%LocalAppData%</c> on Windows, <c>$XDG_DATA_HOME</c> or
/// <c>~/.local/share</c> elsewhere — because the interactive first run is
/// what the defaults serve: it needs no elevation. A system service install
/// keeps naming its paths explicitly in the unit `install` prints, which is
/// exactly the "something specific" the flags are for.
/// </remarks>
public static class AgentDefaults
{
    /// <summary>Environment override for the default state directory.</summary>
    public const string StateVariable = "FALLBACKPLAN_STATE";

    /// <summary>Environment override for the default archives root.</summary>
    public const string ArchivesVariable = "FALLBACKPLAN_ARCHIVES";

    /// <summary>The state directory a path-less invocation uses.</summary>
    public static string StateDirectory =>
        Environment.GetEnvironmentVariable(StateVariable) is { Length: > 0 } state
            ? state
            : Path.Combine(ProfileRoot(), "state");

    /// <summary>The archives root a path-less invocation uses.</summary>
    public static string ArchivesRoot =>
        Environment.GetEnvironmentVariable(ArchivesVariable) is { Length: > 0 } archives
            ? archives
            : Path.Combine(ProfileRoot(), "archives");

    private static string ProfileRoot() => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "fallbackplan");
}
