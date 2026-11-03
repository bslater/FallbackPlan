using System.Buffers.Binary;
using System.Text;

namespace FallbackPlan.Filesystem.Local;

/// <summary>
/// One record of a Windows change journal, as <c>USN_RECORD_V2</c> carries
/// it (ADR-0187): its number, the file's and its folder's identities, why it
/// was written, the entry's attributes, and its name.
/// </summary>
internal readonly record struct UsnRecord(long Usn, ulong FileId, ulong ParentId, uint Reason, uint Attributes, string Name)
{
    private const uint DirectoryAttribute = 0x10;
    private const uint FileDeleteReason = 0x200;

    /// <summary>Whether the entry is a folder.</summary>
    public bool IsDirectory => (Attributes & DirectoryAttribute) != 0;

    /// <summary>Whether the entry was deleted.</summary>
    public bool IsDeleted => (Reason & FileDeleteReason) != 0;
}

/// <summary>
/// Takes apart what a read of a Windows change journal returns: the number
/// to read from next, then version 2 records, each as long as it says.
/// </summary>
/// <remarks>
/// It is a layout, not a platform call, so it is read the same everywhere
/// and tested everywhere.
/// </remarks>
internal static class UsnRecords
{
    /// <summary>A <c>USN_RECORD_V2</c> up to its name.</summary>
    private const int HeaderLength = 60;

    /// <summary>
    /// The number to read from next and every record in
    /// <paramref name="buffer"/>, or null where a record is of another
    /// version or does not fit, which the reader cannot vouch for.
    /// </summary>
    public static (long NextUsn, IReadOnlyList<UsnRecord> Records)? Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < sizeof(long))
        {
            return null;
        }

        var next = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        var records = new List<UsnRecord>();
        for (var at = sizeof(long); at < buffer.Length;)
        {
            var rest = buffer[at..];
            if (rest.Length < HeaderLength)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(rest);
            if (length < HeaderLength || length > rest.Length || BinaryPrimitives.ReadUInt16LittleEndian(rest[4..]) != 2)
            {
                return null;
            }

            var record = rest[..(int)length];
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[56..]);
            var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..]);
            if (nameOffset < HeaderLength || nameOffset + nameLength > record.Length || nameLength % 2 != 0)
            {
                return null;
            }

            records.Add(new UsnRecord(
                BinaryPrimitives.ReadInt64LittleEndian(record[24..]),
                BinaryPrimitives.ReadUInt64LittleEndian(record[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(record[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[40..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[52..]),
                Encoding.Unicode.GetString(record.Slice(nameOffset, nameLength))));
            at += (int)length;
        }

        return (next, records);
    }
}

/// <summary>
/// What a run of change journal records names (ADR-0187): the folders whose
/// listing or own metadata changed, the files changed, whose other names
/// are in folders the records do not name, and the entries deleted, which
/// will not be found and need not be.
/// </summary>
internal sealed class JournalChanges
{
    private readonly HashSet<ulong> _folders = [];
    private readonly HashSet<ulong> _files = [];
    private readonly HashSet<ulong> _gone = [];

    /// <summary>The folders whose listing or own metadata changed, by identity.</summary>
    public IReadOnlySet<ulong> Folders => _folders;

    /// <summary>The files changed, by identity.</summary>
    public IReadOnlySet<ulong> Files => _files;

    /// <summary>The entries deleted, folders and files, by identity.</summary>
    public IReadOnlySet<ulong> Gone => _gone;

    /// <summary>Takes in one record.</summary>
    public void Add(UsnRecord record)
    {
        // Whatever changed, the folder holding the entry lists it, and its
        // tree names the entry's version.
        _folders.Add(record.ParentId);
        (record.IsDirectory ? _folders : _files).Add(record.FileId);
        if (record.IsDeleted)
        {
            _gone.Add(record.FileId);
        }
    }
}

/// <summary>Whether a change journal answers a place in it (ADR-0187).</summary>
internal static class JournalCoverage
{
    /// <summary>
    /// Whether the journal answers <paramref name="since"/>: it is the same
    /// journal, it still holds every record from there, and it has come that
    /// far. A place older than the journal holds is not moved up to what it
    /// holds, since the records between are gone.
    /// </summary>
    public static bool Covers(JournalPosition since, ulong journalId, long lowestValid, long next) =>
        since.JournalId == journalId && since.Usn >= lowestValid && since.Usn <= next;
}

/// <summary>
/// Takes apart <c>FILE_LINKS_INFORMATION</c> (ADR-0187): how many bytes it
/// needs, how many entries it holds, then each entry, eight-byte aligned,
/// giving the offset to the next, the identity of the folder holding the
/// name, and the name.
/// </summary>
/// <remarks>A layout, read the same everywhere and tested everywhere.</remarks>
internal static class HardLinks
{
    /// <summary>An entry up to its name: the next entry's offset, padding, the folder's identity, the name's length.</summary>
    private const int EntryHeaderLength = 20;

    /// <summary>The folder holding each name, or null where an entry does not fit.</summary>
    public static IReadOnlyList<ulong>? ParentsOf(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8)
        {
            return null;
        }

        var entries = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        var parents = new List<ulong>();
        var at = 8;
        for (var entry = 0u; entry < entries; entry++)
        {
            if (at < 0 || at + EntryHeaderLength > buffer.Length)
            {
                return null;
            }

            var next = BinaryPrimitives.ReadUInt32LittleEndian(buffer[at..]);
            parents.Add(BinaryPrimitives.ReadUInt64LittleEndian(buffer[(at + 8)..]));
            if (next == 0)
            {
                return entry + 1 == entries ? parents : null;
            }

            at += (int)next;
        }

        return parents;
    }
}
