using System.Buffers.Binary;
using System.Collections;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Repository.Resources;
using Microsoft.Win32.SafeHandles;

namespace FallbackPlan.Repository;

/// <summary>
/// The record tables of the blobs a session has uploaded, kept in a file in
/// its spool rather than in memory until the session publishes them
/// (ADR-0201). A snapshot can store millions of records, and every one of
/// them waits here for index-delta publication.
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
/// Entries are fixed-width and a blob's table is written once, whole, so a
/// table is a position and a count, and reading one back is one positional
/// read. Appends and reads may come from several upload workers at once.
/// </para>
/// </remarks>
internal sealed class RecordTableSpill : IDisposable
{
    private const string Prefix = "record-tables-";
    private const string Extension = ".spill";

    // ObjectId, ordinal, physical offset, stored length, logical length, the
    // two profiles and the object type, little-endian.
    private const int EntryBytes = ObjectId.Size + 4 + 8 + 4 + 8 + 2 + 2 + 1;

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

    /// <summary>Writes <paramref name="table"/> to the spill and returns it as read back from there.</summary>
    public IReadOnlyList<RecordTableEntry> Append(IReadOnlyList<RecordTableEntry> table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var bytes = new byte[table.Count * EntryBytes];
        for (var index = 0; index < table.Count; index++)
        {
            Encode(table[index], bytes.AsSpan(index * EntryBytes, EntryBytes));
        }

        long offset;
        lock (_gate)
        {
            offset = _length;
            _length += bytes.Length;
        }

        RandomAccess.Write(_handle, bytes, offset);
        return new SpilledTable(this, offset, table.Count);
    }

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();

    private RecordTableEntry[] Read(long offset, int count)
    {
        if (_handle.IsClosed)
        {
            throw new InvalidOperationException(Strings.RecordTableSpill_SessionHasEnded);
        }

        var bytes = new byte[count * EntryBytes];
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

        var entries = new RecordTableEntry[count];
        for (var index = 0; index < count; index++)
        {
            entries[index] = Decode(bytes.AsSpan(index * EntryBytes, EntryBytes));
        }

        return entries;
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
    /// One blob's table, read back from the spill each time it is read, so
    /// that holding it holds a position and a count. Readable while its
    /// session runs; once the session has ended, its spill is gone.
    /// </summary>
    private sealed class SpilledTable(RecordTableSpill spill, long offset, int count) : IReadOnlyList<RecordTableEntry>
    {
        public int Count => count;

        public RecordTableEntry this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
                return spill.Read(offset + ((long)index * EntryBytes), 1)[0];
            }
        }

        public IEnumerator<RecordTableEntry> GetEnumerator() =>
            ((IEnumerable<RecordTableEntry>)spill.Read(offset, count)).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
