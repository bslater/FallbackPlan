namespace FallbackPlan.Domain;

/// <summary>
/// What kind of link a link entry is (specification 06 §4 key 14; ADR-0184).
/// A manifest says nothing of a symbolic link's form, and names a Windows
/// mount point, a junction, by key 14.
/// </summary>
public enum LinkForm
{
    /// <summary>A symbolic link: the form a manifest that says nothing describes.</summary>
    SymbolicLink = 0,

    /// <summary>A Windows mount point, which a junction is: a link to a folder that names an absolute path.</summary>
    MountPoint = 1,
}
