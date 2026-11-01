namespace FallbackPlan.Domain;

/// <summary>
/// The flags a Linux or macOS file carries beyond its mode, as capture reads
/// them (ADR-0180; architecture 06 §3): Linux's inode flags, as <c>statx</c>
/// reports them, and macOS's <c>st_flags</c>. Below format 6 no format key
/// keeps them, so capture names them in a diagnostic; format 6 keeps them in
/// a manifest's metadata key 11, whose bits are these values (ADR-0184).
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
