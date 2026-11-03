using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using FallbackPlan.Filesystem.Local;

namespace FallbackPlan.Filesystem.Tests;

/// <summary>
/// How a read of a Windows change journal is taken apart, and which folders
/// its records name (ADR-0187, FR-SNP-011). A read's buffer is the next
/// record's number and then version 2 records, each eight-byte aligned and
/// as long as it says. A record of any other version, or one that runs past
/// the buffer, makes the read unreadable, so the run walks in full. Every
/// record names the folder holding its entry; a folder's own record names
/// the folder too; and an entry deleted is known gone, so that not finding
/// it, or a record from inside it, is not a doubt.
/// </summary>
/// <remarks>
/// The buffers are built here byte by byte, so this runs on every platform:
/// the layout is the journal's, not the machine's.
/// </remarks>
[TestClass]
public sealed class UsnRecordsTests
{
    private const uint DirectoryAttribute = 0x10;
    private const uint DataExtend = 0x2;
    private const uint FileCreate = 0x100;
    private const uint FileDelete = 0x200;
    private const uint SecurityChange = 0x800;

    [TestMethod]
    public void Parse_ABuffer_ReadsTheNextNumberAndEveryRecord()
    {
        var buffer = Buffer(
            nextUsn: 9_000,
            Record(usn: 8_100, file: 0x11, parent: 0x5, reason: DataExtend, attributes: 0x20, name: "report.docx"),
            Record(usn: 8_200, file: 0x12, parent: 0x5, reason: FileCreate, attributes: DirectoryAttribute, name: "Quarterly"));

        var read = UsnRecords.Parse(buffer);

        Assert.IsNotNull(read);
        Assert.AreEqual(9_000, read.Value.NextUsn);
        Assert.HasCount(2, read.Value.Records);
        Assert.AreEqual(new UsnRecord(8_100, 0x11, 0x5, DataExtend, 0x20, "report.docx"), read.Value.Records[0]);
        Assert.AreEqual(new UsnRecord(8_200, 0x12, 0x5, FileCreate, DirectoryAttribute, "Quarterly"), read.Value.Records[1]);
    }

    [TestMethod]
    public void Parse_ABufferOfNoRecords_ReadsTheNextNumberAlone()
    {
        var read = UsnRecords.Parse(Buffer(nextUsn: 77));

        Assert.IsNotNull(read);
        Assert.AreEqual(77, read.Value.NextUsn);
        Assert.IsEmpty(read.Value.Records);
    }

    [TestMethod]
    public void Parse_ARecordOfAnotherVersion_IsNotRead()
    {
        // Version 3 carries 128-bit identities, as ReFS writes them, which
        // this reader does not open by.
        var record = Record(usn: 1, file: 0x11, parent: 0x5, reason: DataExtend, attributes: 0x20, name: "a.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 3);

        Assert.IsNull(UsnRecords.Parse(Buffer(nextUsn: 2, record)));
    }

    [TestMethod]
    public void Parse_ARecordRunningPastTheBuffer_IsNotRead()
    {
        var record = Record(usn: 1, file: 0x11, parent: 0x5, reason: DataExtend, attributes: 0x20, name: "a.txt");
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)record.Length + 8);

        Assert.IsNull(UsnRecords.Parse(Buffer(nextUsn: 2, record)));
    }

    [TestMethod]
    public void Parse_ANameRunningPastItsRecord_IsNotRead()
    {
        var record = Record(usn: 1, file: 0x11, parent: 0x5, reason: DataExtend, attributes: 0x20, name: "a.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56), 200);

        Assert.IsNull(UsnRecords.Parse(Buffer(nextUsn: 2, record)));
    }

    [TestMethod]
    public void FileIdDescriptor_HasTheNativeLayout()
    {
        // FILE_ID_DESCRIPTOR (winbase.h): a DWORD size, a FILE_ID_TYPE, then
        // a union whose widest member is sixteen bytes, aligned to eight.
        Assert.AreEqual(24, Marshal.SizeOf<FileIdDescriptor>());
        Assert.AreEqual(8, Marshal.OffsetOf<FileIdDescriptor>(nameof(FileIdDescriptor.FileId)).ToInt32());
    }

    [TestMethod]
    public void Changes_AFilesRecord_NamesTheFolderHoldingIt_AndTheFile()
    {
        var changes = new JournalChanges();
        changes.Add(new UsnRecord(1, 0x11, 0x5, DataExtend, 0x20, "report.docx"));

        CollectionAssert.AreEquivalent(new ulong[] { 0x5 }, changes.Folders.ToArray());
        CollectionAssert.AreEquivalent(new ulong[] { 0x11 }, changes.Files.ToArray(), "a file may have other names, in other folders");
        Assert.IsEmpty(changes.Gone);
    }

    [TestMethod]
    public void Changes_AFoldersRecord_NamesTheFolder_AndTheFolderHoldingIt()
    {
        var changes = new JournalChanges();
        changes.Add(new UsnRecord(1, 0x12, 0x5, SecurityChange, DirectoryAttribute, "Quarterly"));

        CollectionAssert.AreEquivalent(new ulong[] { 0x5, 0x12 }, changes.Folders.ToArray());
        Assert.IsEmpty(changes.Files);
    }

    [TestMethod]
    public void Changes_AnEntryDeleted_IsKnownGone()
    {
        var changes = new JournalChanges();
        changes.Add(new UsnRecord(1, 0x13, 0x12, FileDelete, 0x20, "inside.txt"));
        changes.Add(new UsnRecord(2, 0x12, 0x5, FileDelete, DirectoryAttribute, "Quarterly"));

        // Neither will be found, and neither need be: the folder that held
        // the deleted folder is named.
        CollectionAssert.AreEquivalent(new ulong[] { 0x12, 0x13 }, changes.Gone.ToArray());
        Assert.Contains(0x5ul, changes.Folders);
    }

    private static byte[] Buffer(long nextUsn, params byte[][] records)
    {
        var buffer = new List<byte>(BitConverter.GetBytes(nextUsn));
        foreach (var record in records)
        {
            buffer.AddRange(record);
        }

        return [.. buffer];
    }

    /// <summary>A <c>USN_RECORD_V2</c>: 60 bytes, then the name, padded to eight.</summary>
    private static byte[] Record(long usn, ulong file, ulong parent, uint reason, uint attributes, string name)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var length = (60 + nameBytes.Length + 7) / 8 * 8;
        var record = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), file);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), parent);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), usn);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(32), 133_000_000_000_000_000);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(40), reason);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(52), attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(58), 60);
        nameBytes.CopyTo(record, 60);
        return record;
    }
}
