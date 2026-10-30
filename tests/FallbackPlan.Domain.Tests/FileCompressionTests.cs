using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// A restore gives a file and a folder back the NTFS compression captured
/// with their attribute bits, where the target keeps it (FR-RST-004;
/// ADR-0175 Amendment 2): compressed as captured and uncompressed where not,
/// before the times, since compressing rewrites a file's clusters. One the
/// target refuses is named, and leaves the attribute bits not applied. An
/// entry captured with no bits, a link and a target that keeps no
/// compression are given none, and encryption is still named and not set.
/// </summary>
[TestClass]
public sealed class FileCompressionTests : IDisposable
{
    private const uint Hidden = 0x2;
    private const uint Archive = 0x20;
    private const uint Directory = 0x10;
    private const uint Compressed = 0x800;
    private const uint Encrypted = 0x4000;

    private static readonly DateTime Modified = new(2024, 8, 3, 9, 41, 12, 125, DateTimeKind.Utc);

    private readonly string _root =
        System.IO.Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-file-compression-tests", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    public void ThisPlatform_KeepsCompression_OnlyOnWindows() =>
        Assert.AreEqual(OperatingSystem.IsWindows(), MetadataTarget.ForLocalPlatform().SupportsCompression);

    [TestMethod]
    public void UnsetFileAttributes_WhereTheTargetCompresses_NamesEncryptionAlone()
    {
        CollectionAssert.AreEqual(new[] { "Encrypted" }, MetadataRules.UnsetFileAttributes(Archive | Compressed | Encrypted, compresses: true).ToArray());
        Assert.IsEmpty(MetadataRules.UnsetFileAttributes(Archive | Compressed, compresses: true));
        CollectionAssert.AreEqual(
            new[] { "Compressed", "Encrypted" }, MetadataRules.UnsetFileAttributes(Archive | Compressed | Encrypted, compresses: false).ToArray());
    }

    [TestMethod]
    [DataRow(Archive | Compressed, true)]
    [DataRow(Archive, false)]
    [DataRow(0x80u, false)]
    public void Write_AFile_IsGivenItsCompressionAsCaptured_BeforeItsTimes(uint captured, bool compressed)
    {
        var calls = new Held();
        var file = Landed("file.bin");

        var written = Writer(calls).Write(file, new EntryMetadata { ModifiedAt = Millis(Modified), FileAttributes = captured });

        var call = Assert.ContainsSingle(calls.Made);
        Assert.AreEqual(file, call.Path);
        Assert.AreEqual(compressed, call.Compressed);
        Assert.AreNotEqual(Modified, call.ModifiedThen, "compression goes on before the times");
        Assert.AreEqual(Modified, File.GetLastWriteTimeUtc(file));
        Assert.IsNull(written.Detail);
    }

    [TestMethod]
    public void Write_AFolder_IsGivenItsCompressionAsCaptured()
    {
        var calls = new Held();
        var folder = System.IO.Directory.CreateDirectory(Path.Combine(_root, "folder")).FullName;

        Writer(calls).Write(folder, new EntryMetadata { FileAttributes = Directory | Compressed }, EntryKind.DirectoryPlaceholder);

        var call = Assert.ContainsSingle(calls.Made);
        Assert.AreEqual(folder, call.Path);
        Assert.IsTrue(call.Compressed);
    }

    [TestMethod]
    public void Write_ACompressionTheTargetRefuses_IsNamed_WithItsReason_AndTheBitsAreNotApplied()
    {
        var calls = new Held { Refusal = "the volume keeps no compression" };
        var file = Landed("refused.bin");

        var written = Writer(calls).Write(file, new EntryMetadata { FileAttributes = Archive | Compressed });

        Assert.IsFalse(written.Applied.HasFlag(CapturedMetadata.FileAttributes));
        Assert.IsNotNull(written.Detail);
        Assert.Contains("compression", written.Detail, StringComparison.Ordinal);
        Assert.Contains("the volume keeps no compression", written.Detail, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Write_AnEntryCapturedWithNoBits_IsGivenNoCompression()
    {
        var calls = new Held();

        Writer(calls).Write(Landed("unknown.bin"), new EntryMetadata { ModifiedAt = Millis(Modified) });

        Assert.IsEmpty(calls.Made);
    }

    [TestMethod]
    public void Write_ATargetThatKeepsNoCompression_AsksForNone()
    {
        var calls = new Held();

        new MetadataWriter(new MetadataTarget { SupportsPosixMetadata = false, SupportsCompression = false, Compression = calls })
            .Write(Landed("elsewhere.bin"), new EntryMetadata { FileAttributes = Archive | Compressed });

        Assert.IsEmpty(calls.Made);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a symlink is made without a privilege here")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void Write_ALink_IsGivenNoCompression()
    {
        var calls = new Held();
        var link = Path.Combine(_root, "link");
        File.CreateSymbolicLink(link, Landed("pointed-at.bin"));

        Writer(calls).Write(link, new EntryMetadata { FileAttributes = Archive | Compressed }, EntryKind.Symlink);

        Assert.IsEmpty(calls.Made);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's compression")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Write_OnWindows_AFileCapturedCompressed_IsCompressed_AndKeepsItsTimeAndItsBits()
    {
        var file = Landed("packed.bin");

        var written = new MetadataWriter(MetadataTarget.ForLocalPlatform())
            .Write(file, new EntryMetadata { ModifiedAt = Millis(Modified), FileAttributes = Archive | Compressed });

        Assert.AreEqual(FileAttributes.Archive | FileAttributes.Compressed, File.GetAttributes(file));
        Assert.IsTrue(written.Applied.HasFlag(CapturedMetadata.FileAttributes));
        Assert.IsNull(written.Detail);
        Assert.AreEqual(Modified, File.GetLastWriteTimeUtc(file));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's compression")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Write_OnWindows_AFileCapturedUncompressed_InACompressedFolder_IsUncompressed()
    {
        var folder = System.IO.Directory.CreateDirectory(Path.Combine(_root, "packed")).FullName;
        Assert.IsNull(FileCompression.OfThisPlatform.Apply(folder, compressed: true));
        var file = Path.Combine(folder, "plain.bin");
        File.WriteAllText(file, "landed");
        Assert.IsTrue(File.GetAttributes(file).HasFlag(FileAttributes.Compressed), "a file made in a compressed folder is compressed");

        var written = new MetadataWriter(MetadataTarget.ForLocalPlatform()).Write(file, new EntryMetadata { FileAttributes = Archive });

        Assert.AreEqual(FileAttributes.Archive, File.GetAttributes(file));
        Assert.IsTrue(written.Applied.HasFlag(CapturedMetadata.FileAttributes));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows writes a file's attribute bits")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Write_OnWindows_ACompressionRefused_LeavesTheBitsNotApplied_ThoughTheRestLand()
    {
        var file = Landed("refused-here.bin");

        var written = new MetadataWriter(MetadataTarget.ForLocalPlatform() with { Compression = new Held { Refusal = "the volume keeps no compression" } })
            .Write(file, new EntryMetadata { FileAttributes = Hidden | Archive | Compressed });

        Assert.IsTrue(File.GetAttributes(file).HasFlag(FileAttributes.Hidden), "the bits a restore sets still land");
        Assert.IsFalse(written.Applied.HasFlag(CapturedMetadata.FileAttributes));
        Assert.IsNotNull(written.Detail);
        Assert.Contains("the volume keeps no compression", written.Detail, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a folder's compression")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Write_OnWindows_AFolderCapturedCompressed_IsCompressed()
    {
        var folder = System.IO.Directory.CreateDirectory(Path.Combine(_root, "folder")).FullName;

        var written = new MetadataWriter(MetadataTarget.ForLocalPlatform())
            .Write(folder, new EntryMetadata { FileAttributes = Directory | Compressed }, EntryKind.DirectoryPlaceholder);

        Assert.AreEqual(FileAttributes.Directory | FileAttributes.Compressed, File.GetAttributes(folder));
        Assert.IsTrue(written.Applied.HasFlag(CapturedMetadata.FileAttributes));
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    /// <summary>A target that keeps compression through <paramref name="calls"/>, on any platform.</summary>
    private static MetadataWriter Writer(Held calls) => new(new MetadataTarget { SupportsPosixMetadata = false, SupportsCompression = true, Compression = calls });

    private static ulong Millis(DateTime at) => (ulong)new DateTimeOffset(at).ToUnixTimeMilliseconds();

    private string Landed(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "landed");
        return path;
    }

    private sealed class Held : IFileCompression
    {
        public List<(string Path, bool Compressed, DateTime ModifiedThen)> Made { get; } = [];

        public string? Refusal { get; init; }

        public string? Apply(string path, bool compressed)
        {
            Made.Add((path, compressed, File.GetLastWriteTimeUtc(path)));
            return Refusal;
        }
    }
}
