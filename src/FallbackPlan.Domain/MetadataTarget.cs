namespace FallbackPlan.Domain;

/// <summary>
/// What a restore's target lets it write back of an entry's captured
/// metadata: the part of a target a <see cref="MetadataWriter"/> reads
/// (ADR-0149).
/// </summary>
public sealed record MetadataTarget
{
    /// <summary>Whether the target applies POSIX permissions and ownership.</summary>
    public required bool SupportsPosixMetadata { get; init; }

    /// <summary>
    /// Whether a file's creation time is written back: Windows and macOS have
    /// a call that sets one, Linux does not (<see cref="FileTimes"/>).
    /// </summary>
    public bool SupportsCreationTimes { get; init; }

    /// <summary>
    /// Whether extended attributes are written back: Linux and macOS have a
    /// call that writes one, Windows does not (<see cref="ExtendedAttributes"/>).
    /// </summary>
    public bool SupportsExtendedAttributes { get; init; }

    /// <summary>
    /// Whether the target keeps a file's alternate streams: Windows does, and
    /// nothing else. Where it does and writes no extended attributes of its
    /// own, a file's and a folder's captured attributes are kept as streams
    /// of the same names (ADR-0177, and Amendment 2 for a folder).
    /// </summary>
    public bool SupportsAlternateStreams { get; init; }

    /// <summary>
    /// The calls a file's attributes are written through as streams: the
    /// platform's unless a caller says otherwise, as a case judged on a
    /// platform that keeps none does.
    /// </summary>
    public IAlternateStreams Streams { get; init; } = AlternateStreams.OfThisPlatform;

    /// <summary>
    /// Whether a file's and a folder's NTFS compression is given back as
    /// their captured bits say: Windows keeps it, and nothing else does
    /// (ADR-0175 Amendment 2).
    /// </summary>
    public bool SupportsCompression { get; init; }

    /// <summary>
    /// The calls compression is set through: the platform's unless a caller
    /// says otherwise, as a case judged on a platform that keeps none does.
    /// </summary>
    public IFileCompression Compression { get; init; } = FileCompression.OfThisPlatform;

    /// <summary>
    /// Whether a file's and a folder's Windows attribute bits are written
    /// back: Windows keeps them, and nothing else does (ADR-0175).
    /// </summary>
    public bool SupportsFileAttributes { get; init; }

    /// <summary>
    /// Whether a file's and a folder's Windows security descriptor is written
    /// back: Windows keeps one, and nothing else does (ADR-0176).
    /// </summary>
    public bool SupportsSecurityDescriptors { get; init; }

    /// <summary>
    /// Whether this machine knows an account a captured descriptor names, by
    /// its SID: a descriptor is written back only where it knows every one
    /// (<see cref="WindowsAccounts.Knows"/>; ADR-0176).
    /// </summary>
    public Func<string, bool> KnowsAccount { get; init; } = WindowsAccounts.Knows;

    /// <summary>
    /// Whether the account the restore runs as holds SeRestorePrivilege,
    /// which lets it give a file an owner it is not. A descriptor whose owner
    /// is refused without it is written again with it enabled, for that write
    /// alone (<see cref="WindowsPrivileges.RunWith(string, Func{bool})"/>; ADR-0176 Amendment 1).
    /// </summary>
    public bool HoldsRestorePrivilege { get; init; }

    /// <summary>
    /// Whether the account the restore runs as holds SeSecurityPrivilege,
    /// which lets it write a file's audit list (SACL). A descriptor's audit
    /// list is written with it enabled, for that write alone; without it the
    /// rest of the descriptor lands and the audit list is named (ADR-0176
    /// Amendment 3).
    /// </summary>
    public bool HoldsSecurityPrivilege { get; init; }

    /// <summary>
    /// Whether this installation captured what is restored, so that a Linux
    /// ACL naming accounts by number names the accounts it meant (ADR-0087).
    /// False unless a caller that knows who captured it says so.
    /// </summary>
    public bool CapturedHere { get; init; }

    /// <summary>
    /// The account the restore runs as, which decides whose files it may give
    /// away and to which groups, or null where the target gives no file an
    /// owner by name.
    /// </summary>
    public RestoreAccount? Account { get; init; }

    /// <summary>What this process's platform lets a restore write back, as the account it runs as.</summary>
    public static MetadataTarget ForLocalPlatform() => new()
    {
        SupportsPosixMetadata = !OperatingSystem.IsWindows(),
        SupportsCreationTimes = FileTimes.CanSetCreationTime,
        SupportsExtendedAttributes = ExtendedAttributes.CanSet,
        SupportsFileAttributes = OperatingSystem.IsWindows(),
        SupportsSecurityDescriptors = OperatingSystem.IsWindows(),
        SupportsAlternateStreams = AlternateStreams.CanWrite,
        SupportsCompression = FileCompression.CanSet,
        HoldsRestorePrivilege = WindowsPrivileges.Holds(WindowsPrivileges.Restore),
        HoldsSecurityPrivilege = WindowsPrivileges.Holds(WindowsPrivileges.Security),
        Account = RestoreAccount.OfThisProcess(),
    };
}
