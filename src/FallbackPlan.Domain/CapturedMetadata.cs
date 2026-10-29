namespace FallbackPlan.Domain;

/// <summary>
/// The attributes of an entry's captured metadata (specification 06 §4.1),
/// one flag each.
/// </summary>
[Flags]
public enum CapturedMetadata
{
    /// <summary>Nothing captured.</summary>
    None = 0,

    /// <summary>The modification time (key 1).</summary>
    ModifiedAt = 1 << 0,

    /// <summary>The creation time (key 2).</summary>
    CreatedAt = 1 << 1,

    /// <summary>The access time (key 3).</summary>
    AccessedAt = 1 << 2,

    /// <summary>The POSIX mode bits (key 4).</summary>
    PosixMode = 1 << 3,

    /// <summary>The owner's name (key 5).</summary>
    Owner = 1 << 4,

    /// <summary>The group's name (key 6).</summary>
    Group = 1 << 5,

    /// <summary>The Windows security descriptor (key 7).</summary>
    SecurityDescriptor = 1 << 6,

    /// <summary>Extended attributes (key 8).</summary>
    ExtendedAttributes = 1 << 7,

    /// <summary>Alternate data streams (key 9).</summary>
    AlternateStreams = 1 << 8,

    /// <summary>The platform's attribute bits (key 10).</summary>
    FileAttributes = 1 << 9,
}

/// <summary>
/// The kinds of extended attribute an item carries that a restore does not
/// write back everywhere (ADR-0087), one flag each.
/// </summary>
[Flags]
public enum ExtendedAttributeKinds
{
    /// <summary>None of them.</summary>
    None = 0,

    /// <summary>One in Linux's security or trusted namespace, which only root writes.</summary>
    Privileged = 1 << 0,

    /// <summary>A Linux POSIX ACL that names an account or group by number, or that will not parse.</summary>
    NumberedAcl = 1 << 1,

    /// <summary>A Linux POSIX ACL that names no account or group: only the owner, the group, the mask and everyone else.</summary>
    AnonymousAcl = 1 << 2,

    /// <summary>One in Linux's user namespace, which Linux keeps on no symlink.</summary>
    User = 1 << 3,
}
