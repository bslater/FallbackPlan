using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Format.Manifests;

/// <summary>One extended attribute: raw name and value bytes (specification 06 §4.1 key 8).</summary>
public sealed record ExtendedAttributeEntry(ReadOnlyMemory<byte> Name, ReadOnlyMemory<byte> Value);

/// <summary>One alternate stream: name, its record's object identifier, and its length (06 §4.1 key 9).</summary>
public sealed record AlternateStreamEntry(ReadOnlyMemory<byte> Name, ObjectId ObjectId, ulong Length);

/// <summary>
/// The per-entry metadata map (specification 06 §4.1, keys 1–10). Absent
/// keys mean the source did not provide the value — they never mean zero,
/// which is why every field here is optional and never defaulted.
/// </summary>
public sealed record EntryMetadata
{
    /// <summary>An entirely empty map — a legitimate state for a source that reported nothing.</summary>
    public static readonly EntryMetadata Empty = new();

    /// <summary>Modification time, milliseconds since the epoch (key 1).</summary>
    public ulong? ModifiedAt { get; init; }

    /// <summary>Creation time (key 2).</summary>
    public ulong? CreatedAt { get; init; }

    /// <summary>Access time (key 3).</summary>
    public ulong? AccessedAt { get; init; }

    /// <summary>POSIX mode bits (key 4).</summary>
    public uint? PosixMode { get; init; }

    /// <summary>Owner name — a name rather than a UID, because a UID means nothing on the restore machine (key 5).</summary>
    public string? OwnerName { get; init; }

    /// <summary>Group name (key 6).</summary>
    public string? GroupName { get; init; }

    /// <summary>Self-relative Windows security descriptor (key 7).</summary>
    public ReadOnlyMemory<byte>? WindowsSecurityDescriptor { get; init; }

    /// <summary>Extended attributes (key 8); empty means none were captured.</summary>
    public IReadOnlyList<ExtendedAttributeEntry> ExtendedAttributes { get; init; } = [];

    /// <summary>Alternate streams (key 9); empty means none.</summary>
    public IReadOnlyList<AlternateStreamEntry> AlternateStreams { get; init; } = [];

    /// <summary>Platform attribute bits (key 10).</summary>
    public uint? FileAttributes { get; init; }
}
