using System.Buffers.Binary;
using System.Collections;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Repository.Resources;
using Microsoft.Win32.SafeHandles;

namespace FallbackPlan.Repository;

/// <summary>
/// The record tables of the blobs a session has uploaded, and the segment
/// references of the files a tree snapshot has captured, kept in a file in its
/// spool rather than in memory until the session publishes them (ADR-0201). A
/// snapshot can store millions of records and reference millions of segments,
/// and every one of them waits here for index-delta publication or for the
/// catalogue.
/// </summary>
/// <remarks>
/// <para>
/// The file is the session's alone: opened exclusively and deleted when it
/// closes, so a session that finished leaves nothing of it. A session that
/// died cannot delete its own, so the first spill each session makes removes
/// any the spool still holds that no live session has open. What a spill
/// holds is object identifiers and positions, which name content without
/// revealing it.
/// </para>
/// <para>
/// Entries are fixed-width and a blob's table, or a file's references, is
/// written once, whole, so either is a position and a count, and reading one
/// back is one positional read. Appends and reads may come from several upload
/// workers and the walk at once.
/// </para>
/// </remarks>
internal sealed class RecordTableSpill : IDisposable
{
    private const string Prefix = "record-tables-";
    private const string Extension = ".spill";

    // ObjectId, ordinal, physical offset, stored length, logical length, the
    // two profiles and the object type, little-endian.
    private const int EntryBytes = ObjectId.Size + 4 + 8 + 4 + 8 + 2 + 2 + 1;

    // ObjectId, logical offset and logical length, little-endian.
    private const int ReferenceBytes = ObjectId.Size + 8 + 8;

    private const int ContentIdBytes = ContentId.Size;

    private const string WrittenSetExtension = ".written";

    private readonly SafeFileHandle _handle;
    private readonly Lock _gate = new();
    private long _length;

    private RecordTableSpill(SafeFileHandle handle, string path)
    {
        _handle = handle;
        WrittenSetPath = Path.ChangeExtension(path, WrittenSetExtension);
    }

    /// <summary>
    /// Where the same session keeps its written set (ADR-0201): beside the
    /// spill and named for it, so the spill's exclusive hold says whether
    /// either belongs to a live session.
    /// </summary>
    public string WrittenSetPath { get; }

    /// <summary>
    /// Opens a new spill in <paramref name="spoolDirectory"/>, first removing
    /// every spill there that no live session holds.
    /// </summary>
    public static RecordTableSpill Create(string spoolDirectory)
    {
        Directory.CreateDirectory(spoolDirectory);
        RemoveAbandoned(spoolDirectory);

        var path = Path.Combine(spoolDirectory, $"{Prefix}{Guid.NewGuid():N}{Extension}");
        return new RecordTableSpill(
            File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose),
            path);
    }

    private delegate T Decoder<out T>(ReadOnlySpan<byte> source);

    /// <summary>Writes <paramref name="table"/> to the spill and returns it as read back from there.</summary>
    public IReadOnlyList<RecordTableEntry> Append(IReadOnlyList<RecordTableEntry> table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var bytes = new byte[table.Count * EntryBytes];
        for (var index = 0; index < table.Count; index++)
        {
            Encode(table[index], bytes.AsSpan(index * EntryBytes, EntryBytes));
        }

        return new Spilled<RecordTableEntry>(
            this, Write(bytes), table.Count, EntryBytes, Decode, Strings.RecordTableSpill_SessionHasEnded);
    }

    /// <summary>Writes a file's segment references to the spill and returns them as read back from there.</summary>
    public IReadOnlyList<SegmentReference> AppendReferences(IReadOnlyList<SegmentReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        var bytes = new byte[references.Count * ReferenceBytes];
        for (var index = 0; index < references.Count; index++)
        {
            EncodeReference(references[index], bytes.AsSpan(index * ReferenceBytes, ReferenceBytes));
        }

        return new Spilled<SegmentReference>(
            this, Write(bytes), references.Count, ReferenceBytes, DecodeReference,
            Strings.RecordTableSpill_ReferencesSessionHasEnded);
    }

    /// <summary>Writes a file's segment content identifiers to the spill and returns them as read back from there.</summary>
    public IReadOnlyList<ContentId> AppendContentIds(IReadOnlyList<ContentId> contentIds)
    {
        ArgumentNullException.ThrowIfNull(contentIds);
        var bytes = new byte[contentIds.Count * ContentIdBytes];
        for (var index = 0; index < contentIds.Count; index++)
        {
            contentIds[index].CopyTo(bytes.AsSpan(index * ContentIdBytes, ContentIdBytes));
        }

        return new Spilled<ContentId>(
            this, Write(bytes), contentIds.Count, ContentIdBytes, static source => ContentId.FromBytes(source),
            Strings.RecordTableSpill_ContentIdsSessionHasEnded);
    }

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();

    private long Write(byte[] bytes)
    {
        long offset;
        lock (_gate)
        {
            offset = _length;
            _length += bytes.Length;
        }

        RandomAccess.Write(_handle, bytes, offset);
        return offset;
    }

    private T[] Read<T>(long offset, int count, int width, Decoder<T> decode, string sessionHasEnded)
    {
        if (_handle.IsClosed)
        {
            throw new InvalidOperationException(sessionHasEnded);
        }

        var bytes = new byte[count * width];
        var read = 0;
        while (read < bytes.Length)
        {
            var got = RandomAccess.Read(_handle, bytes.AsSpan(read), offset + read);
            if (got == 0)
            {
                throw new EndOfStreamException();
            }

            read += got;
        }

        var items = new T[count];
        for (var index = 0; index < count; index++)
        {
            items[index] = decode(bytes.AsSpan(index * width, width));
        }

        return items;
    }

    private static void RemoveAbandoned(string spoolDirectory)
    {
        foreach (var path in Directory.EnumerateFiles(spoolDirectory, $"{Prefix}*{Extension}"))
        {
            try
            {
                // A live session holds its spill exclusively, so an open that
                // succeeds finds one nobody holds, and closing it deletes it.
                using (File.OpenHandle(
                    path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose))
                {
                }
            }
            catch (IOException)
            {
                // Another session's, still running.
            }
            catch (UnauthorizedAccessException)
            {
                // Not this account's to remove.
            }
        }

        // A session creates its spill before its written set and deletes it
        // after, so a written set with no spill beside it belongs to a session
        // that is gone: one whose spill was just removed, or one that died
        // where the system deletes a spill when its holder dies, as Windows does.
        foreach (var path in Directory.EnumerateFiles(spoolDirectory, $"{Prefix}*{WrittenSetExtension}"))
        {
            if (File.Exists(Path.ChangeExtension(path, Extension)))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void Encode(in RecordTableEntry entry, Span<byte> destination)
    {
        entry.ObjectId.CopyTo(destination);
        var rest = destination[ObjectId.Size..];
        BinaryPrimitives.WriteUInt32LittleEndian(rest, entry.Ordinal);
        BinaryPrimitives.WriteUInt64LittleEndian(rest[4..], entry.PhysicalOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(rest[12..], entry.StoredLength);
        BinaryPrimitives.WriteUInt64LittleEndian(rest[16..], entry.LogicalLength);
        BinaryPrimitives.WriteUInt16LittleEndian(rest[24..], entry.CompressionProfileValue);
        BinaryPrimitives.WriteUInt16LittleEndian(rest[26..], entry.EncryptionProfileValue);
        rest[28] = (byte)entry.ObjectType;
    }

    private static void EncodeReference(in SegmentReference reference, Span<byte> destination)
    {
        reference.ObjectId.CopyTo(destination);
        BinaryPrimitives.WriteInt64LittleEndian(destination[ObjectId.Size..], reference.LogicalOffset);
        BinaryPrimitives.WriteInt64LittleEndian(destination[(ObjectId.Size + 8)..], reference.LogicalLength);
    }

    private static SegmentReference DecodeReference(ReadOnlySpan<byte> source) =>
        new(
            BinaryPrimitives.ReadInt64LittleEndian(source[ObjectId.Size..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[(ObjectId.Size + 8)..]),
            ObjectId.FromBytes(source[..ObjectId.Size]));

    private static RecordTableEntry Decode(ReadOnlySpan<byte> source)
    {
        var rest = source[ObjectId.Size..];
        return new RecordTableEntry(
            ObjectId.FromBytes(source[..ObjectId.Size]),
            BinaryPrimitives.ReadUInt32LittleEndian(rest),
            BinaryPrimitives.ReadUInt64LittleEndian(rest[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(rest[12..]),
            BinaryPrimitives.ReadUInt64LittleEndian(rest[16..]),
            BinaryPrimitives.ReadUInt16LittleEndian(rest[24..]),
            BinaryPrimitives.ReadUInt16LittleEndian(rest[26..]),
            (ObjectType)rest[28]);
    }

    /// <summary>
    /// One blob's table, or one file's references, read back from the spill
    /// each time it is read, so that holding it holds a position and a count.
    /// Readable while its session runs; once the session has ended, its spill
    /// is gone, and reading says where else to look.
    /// </summary>
    private sealed class Spilled<T>(
        RecordTableSpill spill, long offset, int count, int width, Decoder<T> decode, string sessionHasEnded)
        : IReadOnlyList<T>
    {
        public int Count => count;

        public T this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
                return spill.Read(offset + ((long)index * width), 1, width, decode, sessionHasEnded)[0];
            }
        }

        public IEnumerator<T> GetEnumerator() =>
            ((IEnumerable<T>)spill.Read(offset, count, width, decode, sessionHasEnded)).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
