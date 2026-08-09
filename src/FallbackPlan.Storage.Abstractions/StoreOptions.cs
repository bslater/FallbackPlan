using Bodu;

namespace FallbackPlan.Storage.Abstractions;

/// <summary>
/// A byte range within a stored object, for providers reporting
/// <see cref="StoreCapabilities.RangedReads"/>.
/// </summary>
public readonly record struct ObjectRange
{
    /// <summary>Creates a range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The offset is negative or the length not positive.</exception>
    public ObjectRange(long offset, long length)
    {
        ThrowHelper.ThrowIfNegative(offset);
        ThrowHelper.ThrowIfZeroOrNegative(length);
        Offset = offset;
        Length = length;
    }

    /// <summary>The zero-based byte offset.</summary>
    public long Offset { get; }

    /// <summary>The number of bytes.</summary>
    public long Length { get; }
}

/// <summary>
/// Conditions on a put. Conditional create is the primitive publication
/// depends on (docs/architecture/05-storage-providers.md §2.2); further
/// conditions arrive with the providers that support them.
/// </summary>
public sealed record PutConditions
{
    /// <summary>No conditions.</summary>
    public static readonly PutConditions None = new();

    /// <summary>Create-if-absent, for providers reporting <see cref="StoreCapabilities.ConditionalCreate"/>.</summary>
    public static readonly PutConditions IfNotExists = new() { CreateIfAbsent = true };

    /// <summary>Whether the put must only create, never observe an existing object as success.</summary>
    public bool CreateIfAbsent { get; init; }
}

/// <summary>Conditions on a delete; extensible per provider capability.</summary>
public sealed record DeleteConditions
{
    /// <summary>No conditions.</summary>
    public static readonly DeleteConditions None = new();
}

/// <summary>
/// Listing options. Continuation belongs to the enumerator
/// (docs/architecture/05-storage-providers.md §2.3): a caller that must
/// persist a position across process restarts reads
/// <see cref="ObjectEntry.ResumeToken"/> and passes it back here.
/// </summary>
public sealed record ListOptions
{
    /// <summary>The default options: list everything from the start.</summary>
    public static readonly ListOptions Default = new();

    /// <summary>Resume strictly after the entry that produced this token.</summary>
    public string? ResumeAfter { get; init; }

    /// <summary>A page-size hint for providers that page; advisory only.</summary>
    public int? PageSizeHint { get; init; }
}

/// <summary>
/// One listing entry: the key, its length, and the opaque token that resumes a
/// listing strictly after this entry.
/// </summary>
public sealed record ObjectEntry(ObjectKey Key, long Length, string ResumeToken);
