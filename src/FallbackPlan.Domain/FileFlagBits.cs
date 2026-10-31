namespace FallbackPlan.Domain;

/// <summary>
/// The flags a Linux or macOS file carries beyond its mode, as capture reads
/// them (ADR-0180; architecture 06 §3): Linux's inode flags, as <c>statx</c>
/// reports them, and macOS's <c>st_flags</c>. No format key keeps them, so
/// capture names them in a diagnostic.
/// </summary>
[Flags]
public enum FileFlagBits
{
    /// <summary>No flag.</summary>
    None = 0,

    /// <summary>The file may not be changed, renamed or removed.</summary>
    Immutable = 1 << 0,

    /// <summary>The file may only be appended to.</summary>
    AppendOnly = 1 << 1,

    /// <summary>A dump-style backup passes over the file.</summary>
    NoDump = 1 << 2,

    /// <summary>The volume keeps the file compressed.</summary>
    Compressed = 1 << 3,

    /// <summary>The volume keeps the file encrypted.</summary>
    Encrypted = 1 << 4,

    /// <summary>macOS hides the file from its file browser.</summary>
    Hidden = 1 << 5,
}
