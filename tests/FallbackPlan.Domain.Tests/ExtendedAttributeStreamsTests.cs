using FallbackPlan.Domain.Identifiers;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// On a target that keeps a file's alternate streams and writes no extended
/// attributes of its own, as Windows does, a restore keeps a file's captured
/// extended attributes as streams of the same names (FR-RST-004; ADR-0177;
/// architecture 06 §3). One in Linux's security or trusted namespace, or a
/// POSIX ACL, means something to Linux's kernel alone and is not kept; a name
/// no stream may have, one the file's own captured stream already has, and a
/// stream the target refuses are each not written, and are named. A target
/// that writes attributes of its own writes them as attributes. The plan
/// counts by the writer's own judgement of each name (ADR-0177 Amendment 1).
/// A folder's attributes are kept as its streams by the same rule (ADR-0177
/// Amendment 2).
/// </summary>
[TestClass]
public sealed class ExtendedAttributeStreamsTests : IDisposable
{
    private static readonly DateTime Modified = new(2024, 8, 2, 11, 15, 30, 250, DateTimeKind.Utc);

    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-attribute-stream-tests", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    public void ThisPlatform_KeepsAttributesAsStreams_OnlyWhereItKeepsStreams() =>
        Assert.AreEqual(AlternateStreams.CanWrite, MetadataTarget.ForLocalPlatform().SupportsAlternateStreams);

    [TestMethod]
    public void Write_AFilesAttributes_LandAsStreamsOfTheirNames_AndCountApplied()
    {
        var streams = new Held();
        var file = Landed("tagged.bin");

        var written = Writer(streams).Write(file, new EntryMetadata
        {
            ModifiedAt = Millis(Modified),
            ExtendedAttributes = [Attribute("user.comment", "kept"u8), Attribute("com.apple.FinderInfo", [1, 2, 3])],
        });

        Assert.IsTrue(written.Applied.HasFlag(CapturedMetadata.ExtendedAttributes));
        Assert.IsNull(written.Detail);
        CollectionAssert.AreEqual("kept"u8.ToArray(), streams.Written[(file, "user.comment")]);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, streams.Written[(file, "com.apple.FinderInfo")]);
        Assert.HasCount(2, streams.Written);
    }

    [TestMethod]
    [DataRow("security.capability")]
    [DataRow("trusted.overlay.opaque")]
    [DataRow("system.posix_acl_access")]
    [DataRow("system.posix_acl_default")]
    public void Write_AnAttributeOnlyLinuxsKernelReads_IsNotKept_AndIsNamed_WhileTheRestLand(string name)
    {
        var streams = new Held();
        var file = Landed("kernel.bin");

        var written = Writer(streams).Write(file, new EntryMetadata
        {
            ExtendedAttributes = [Attribute(name, [2, 0, 0, 0]), Attribute("user.tag", "kept"u8)],
        });

        Assert.IsFalse(written.Applied.HasFlag(CapturedMetadata.ExtendedAttributes));
        Assert.IsNotNull(written.Detail);
        Assert.Contains(name, written.Detail, StringComparison.Ordinal);
        Assert.Contains("Linux", written.Detail, StringComparison.Ordinal);
        Assert.IsFalse(streams.Written.ContainsKey((file, name)));
        Assert.IsTrue(streams.Written.ContainsKey((file, "user.tag")));
    }

    [TestMethod]
    [DataRow("com.apple.metadata:kMDItemWhereFroms")]
    [DataRow("user.ends.with.a.period.")]
    public void Write_AnAttributeWhoseNameNoStreamMayHave_IsNotWritten_AndIsNamed(string name)
    {
        var streams = new Held();
        var file = Landed("unnamed.bin");

        var written = Writer(streams).Write(file, new EntryMetadata
        {
            ExtendedAttributes = [Attribute(name, "where"u8), Attribute("user.tag", "kept"u8)],
        });

        Assert.IsFalse(written.Applied.HasFlag(CapturedMetadata.ExtendedAttributes));
        Assert.IsNotNull(written.Detail);
        Assert.Contains(name, written.Detail, StringComparison.Ordinal);
        Assert.ContainsSingle(streams.Written);
    }

    [TestMethod]
    [DataRow("Zone.Identifier")]
    [DataRow("zone.identifier")]
    public void Write_AnAttributeNamedAsTheFilesOwnStream_IsNotWrittenOverIt(string name)
    {
        var streams = new Held();
        var file = Landed("both.bin");

        var written = Writer(streams).Write(file, new EntryMetadata
        {
            AlternateStreams = [new AlternateStreamEntry("Zone.Identifier"u8.ToArray(), ObjectId.FromBytes(Enumerable.Repeat((byte)0xAA, 32).ToArray()), 4)],
            ExtendedAttributes = [Attribute(name, "over"u8)],
        });

        Assert.IsFalse(written.Applied.HasFlag(CapturedMetadata.ExtendedAttributes));
        Assert.IsNotNull(written.Detail);
        Assert.Contains(name, written.Detail, StringComparison.Ordinal);
        Assert.IsEmpty(streams.Written);
    }

    [TestMethod]
    public void Write_TwoAttributesWhoseNamesDifferOnlyInCase_KeepTheFirst_AndNameTheSecond()
    {
        var streams = new Held();
        var file = Landed("cased.bin");

        var written = Writer(streams).Write(file, new EntryMetadata
        {
            ExtendedAttributes = [Attribute("user.Tag", "first"u8), Attribute("user.tag", "second"u8)],
        });

        Assert.IsFalse(written.Applied.HasFlag(CapturedMetadata.ExtendedAttributes));
        Assert.IsNotNull(written.Detail);
        Assert.Contains("user.tag", written.Detail, StringComparison.Ordinal);
        var kept = Assert.ContainsSingle(streams.Written);
        Assert.AreEqual("user.Tag", kept.Key.Name);
        CollectionAssert.AreEqual("first"u8.ToArray(), kept.Value);
    }

    [TestMethod]
    public void AsStreams_NamesTheStreamEachAttributeIsKeptAs_OrWhyNot_InTheOrderTheyWereCaptured()
    {
        var judged = MetadataRules.AsStreams(Mixed);

        Assert.HasCount(5, judged);
        Assert.AreEqual(new AttributeKeptAs("user.tag", null), judged[0]);
        Assert.IsNull(judged[1].Stream);
        Assert.Contains("colon", judged[1].Why!, StringComparison.Ordinal);
        Assert.IsNull(judged[2].Stream);
        Assert.Contains("Windows compares names", judged[2].Why!, StringComparison.Ordinal);
        Assert.IsNull(judged[3].Stream);
        Assert.Contains("Linux", judged[3].Why!, StringComparison.Ordinal);
        Assert.IsNull(judged[4].Stream);
        Assert.Contains("captured with a stream of that name", judged[4].Why!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void RefusedAttributeNames_CountsTheAttributesNotKeptForTheirNames_NotTheKernels()
    {
        Assert.AreEqual(3, MetadataRules.RefusedAttributeNames(Mixed));
        Assert.AreEqual(0, MetadataRules.RefusedAttributeNames(new EntryMetadata
        {
            ExtendedAttributes = [Attribute("user.tag", "kept"u8), Attribute("security.selinux", "label"u8)],
        }));
        Assert.AreEqual(0, MetadataRules.RefusedAttributeNames(EntryMetadata.Empty));
    }

    [TestMethod]
    public void Write_AStreamTheTargetRefuses_IsNamed_WithItsReason()
    {
        var streams = new Held { Refusal = "the volume keeps no streams" };
        var file = Landed("refused.bin");

        var written = Writer(streams).Write(file, new EntryMetadata { ExtendedAttributes = [Attribute("user.tag", "kept"u8)] });

        Assert.IsFalse(written.Applied.HasFlag(CapturedMetadata.ExtendedAttributes));
        Assert.IsNotNull(written.Detail);
        Assert.Contains("user.tag", written.Detail, StringComparison.Ordinal);
        Assert.Contains("the volume keeps no streams", written.Detail, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Write_AFoldersAttributes_LandAsStreamsOfTheirNames_ButThoseOnlyLinuxsKernelReads()
    {
        var streams = new Held();
        var folder = Directory.CreateDirectory(Path.Combine(_root, "folder")).FullName;

        var kept = Writer(streams).Write(
            folder, new EntryMetadata { ExtendedAttributes = [Attribute("com.apple.FinderInfo", [1])] }, EntryKind.DirectoryPlaceholder);

        Assert.IsTrue(kept.Applied.HasFlag(CapturedMetadata.ExtendedAttributes));
        CollectionAssert.AreEqual(new byte[] { 1 }, streams.Written[(folder, "com.apple.FinderInfo")]);

        var withheld = Writer(streams).Write(
            folder,
            new EntryMetadata { ExtendedAttributes = [Attribute("user.tag", "kept"u8), Attribute("system.posix_acl_default", [2])] },
            EntryKind.DirectoryPlaceholder);

        Assert.IsFalse(withheld.Applied.HasFlag(CapturedMetadata.ExtendedAttributes));
        Assert.IsNotNull(withheld.Detail);
        Assert.Contains("system.posix_acl_default", withheld.Detail, StringComparison.Ordinal);
        CollectionAssert.AreEqual("kept"u8.ToArray(), streams.Written[(folder, "user.tag")]);
        Assert.HasCount(2, streams.Written);
    }

    [TestMethod]
    public void Write_ASymlinksAttributes_AreNotKeptAsStreams()
    {
        // A stream joined to a link's path would be written through the link.
        Assert.IsFalse(MetadataRules.KeepsAttributesAsStreams(EntryKind.Symlink, supportsAlternateStreams: true, writesAttributes: false));
        Assert.IsTrue(MetadataRules.KeepsAttributesAsStreams(EntryKind.DirectoryPlaceholder, supportsAlternateStreams: true, writesAttributes: false));
        Assert.IsFalse(MetadataRules.KeepsAttributesAsStreams(EntryKind.DirectoryPlaceholder, supportsAlternateStreams: false, writesAttributes: false));
        Assert.IsFalse(MetadataRules.KeepsAttributesAsStreams(EntryKind.DirectoryPlaceholder, supportsAlternateStreams: true, writesAttributes: true));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "only Linux and macOS write extended attributes of their own")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void Write_ATargetThatWritesAttributesOfItsOwn_WritesNoneAsStreams()
    {
        var streams = new Held();
        var file = Landed("native.bin");

        new MetadataWriter(MetadataTarget.ForLocalPlatform() with { SupportsAlternateStreams = true, Streams = streams })
            .Write(file, new EntryMetadata { ExtendedAttributes = [Attribute("user.tag", "kept"u8)] });

        Assert.IsEmpty(streams.Written);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's alternate streams")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Write_OnWindows_AFilesAttributes_LandAsItsStreams_AndItKeepsItsTime()
    {
        var file = Landed("tagged.bin");

        var written = new MetadataWriter(MetadataTarget.ForLocalPlatform()).Write(file, new EntryMetadata
        {
            ModifiedAt = Millis(Modified),
            ExtendedAttributes = [Attribute("user.comment", "kept on Windows"u8)],
        });

        Assert.IsTrue(written.Applied.HasFlag(CapturedMetadata.ExtendedAttributes));
        Assert.AreEqual("kept on Windows", File.ReadAllText($"{file}:user.comment"));
        Assert.AreEqual(Modified, File.GetLastWriteTimeUtc(file));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a folder's alternate streams")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Write_OnWindows_AFoldersAttributes_LandAsItsStreams_AndItKeepsItsTime()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "tagged")).FullName;

        var written = new MetadataWriter(MetadataTarget.ForLocalPlatform()).Write(
            folder,
            new EntryMetadata { ModifiedAt = Millis(Modified), ExtendedAttributes = [Attribute("user.comment", "kept on a folder"u8)] },
            EntryKind.DirectoryPlaceholder);

        Assert.IsTrue(written.Applied.HasFlag(CapturedMetadata.ExtendedAttributes), written.Detail);
        Assert.AreEqual("kept on a folder", File.ReadAllText($"{folder}:user.comment"));
        Assert.AreEqual(Modified, Directory.GetLastWriteTimeUtc(folder));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    /// <summary>
    /// A file whose attributes are one of each judgement: a name kept, a name
    /// no stream may have, a name an earlier attribute has but for case, one
    /// only Linux's kernel reads, and the file's own stream's name.
    /// </summary>
    private static EntryMetadata Mixed => new()
    {
        AlternateStreams = [new AlternateStreamEntry("Zone.Identifier"u8.ToArray(), ObjectId.FromBytes(Enumerable.Repeat((byte)0xAA, 32).ToArray()), 4)],
        ExtendedAttributes =
        [
            Attribute("user.tag", "kept"u8),
            Attribute("com.apple.metadata:kMDItemWhereFroms", "where"u8),
            Attribute("USER.TAG", "again"u8),
            Attribute("security.selinux", "label"u8),
            Attribute("zone.identifier", "over"u8),
        ],
    };

    /// <summary>A target that keeps streams and writes no attributes of its own, as Windows is.</summary>
    private static MetadataWriter Writer(Held streams) => new(new MetadataTarget
    {
        SupportsPosixMetadata = false,
        SupportsExtendedAttributes = false,
        SupportsAlternateStreams = true,
        Streams = streams,
    });

    private static ExtendedAttributeEntry Attribute(string name, ReadOnlySpan<byte> value) =>
        new(System.Text.Encoding.UTF8.GetBytes(name), value.ToArray());

    private static ulong Millis(DateTime at) => (ulong)new DateTimeOffset(at).ToUnixTimeMilliseconds();

    private string Landed(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "landed");
        return path;
    }

    private sealed class Held : IAlternateStreams
    {
        public Dictionary<(string File, string Name), byte[]> Written { get; } = [];

        public string? Refusal { get; init; }

        public byte[]? Read(string file, string name, ulong length) => null;

        public string? Write(string file, string name, ReadOnlySpan<byte> content)
        {
            if (Refusal is not null)
            {
                return Refusal;
            }

            Written[(file, name)] = content.ToArray();
            return null;
        }
    }
}
