namespace FallbackPlan.Domain;

/// <summary>The kind of filesystem entry a file-version manifest describes (specification 06 §4 key 1).</summary>
public enum EntryKind : ushort
{
    /// <summary>A regular file.</summary>
    File = 1,

    /// <summary>A symbolic link; the target rides in key 11.</summary>
    Symlink = 2,

    /// <summary>A directory placeholder.</summary>
    DirectoryPlaceholder = 3,

    /// <summary>A special file (device, socket, fifo).</summary>
    Special = 4,
}

/// <summary>How an entry name was normalised by the source (specification 06 §4 key 3).</summary>
public enum NameNormalisation : byte
{
    /// <summary>The source did not report a normalisation.</summary>
    Unknown = 0,

    /// <summary>Unicode NFC.</summary>
    Nfc = 1,

    /// <summary>Unicode NFD.</summary>
    Nfd = 2,
}

/// <summary>
/// One sparse extent — a hole materialised as zeroes on restore
/// (specification 06 §4 key 6, 09 §4).
/// </summary>
public readonly record struct SparseExtent(ulong Offset, ulong Length);

/// <summary>Why a path failed capture (specification 06 §8.1 key 2).</summary>
public enum CaptureFailureReason : ushort
{
    /// <summary>Permission denied.</summary>
    Permission = 1,

    /// <summary>The path vanished before it could be read.</summary>
    NotFound = 2,

    /// <summary>An I/O error.</summary>
    IoError = 3,

    /// <summary>The file changed during read.</summary>
    ChangedDuringRead = 4,

    /// <summary>An unsupported file type.</summary>
    UnsupportedType = 5,

    /// <summary>The file exceeds a configured limit.</summary>
    TooLarge = 6,

    /// <summary>Excluded by a size or count limit — not by policy, which is never a failure (06 §8).</summary>
    ExcludedByLimit = 7,

    /// <summary>
    /// The entry's name cannot be represented on this host, so the entry
    /// cannot be captured under the name it actually has.
    /// </summary>
    /// <remarks>
    /// A POSIX filename is any byte sequence without NUL or <c>/</c>, and is
    /// under no obligation to be valid UTF-8. The host runtime decodes such a
    /// name to U+FFFD replacement characters, which is irreversible — and a
    /// path built from that string does not open the file either. Recording
    /// the failure is the honest outcome: the alternative, and what happened
    /// before this reason existed, was to capture the entry under a name that
    /// was not its name and whose content could not be read.
    /// </remarks>
    NameNotRepresentable = 8,
}
