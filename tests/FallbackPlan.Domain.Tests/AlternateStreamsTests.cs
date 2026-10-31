using System.Text;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The calls a restore writes a file's alternate data streams back with
/// (FR-RST-004; ADR-0174): only Windows keeps them, so only there is one
/// written. A stream's captured name is judged before it is ever joined to
/// the file's path, because a name that is not a stream's could reach
/// another file: <c>file:..\..\x</c> names <c>x</c> beside the file, and a
/// trailing period is stripped, so <c>file:..</c> names the file itself.
/// Where the platform writes extended attributes instead, as Linux and macOS
/// do, a file's streams are kept as attributes of the same names: Linux
/// takes only those in its user namespace, and a stream the file's own
/// captured attribute names is left to it (ADR-0179 Amendment 1). One
/// larger than the platform takes in any volume's attribute is neither read
/// nor written, and is named (ADR-0179 Amendment 2).
/// </summary>
[TestClass]
public sealed class AlternateStreamsTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-alternate-streams-tests", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    public void CanWrite_OnlyOnWindows() =>
        Assert.AreEqual(OperatingSystem.IsWindows(), AlternateStreams.CanWrite);

    [TestMethod]
    [DataRow("Zone.Identifier")]
    [DataRow("notes")]
    [DataRow("\u0005SummaryInformation")]
    public void TryName_AStreamsOwnName_IsTheName(string captured)
    {
        Assert.IsTrue(AlternateStreams.TryName(Encoding.UTF8.GetBytes(captured), out var name, out _));
        Assert.AreEqual(captured, name);
    }

    [TestMethod]
    [DataRow(@"..\..\escape")]
    [DataRow("../escape")]
    [DataRow("other:$DATA")]
    [DataRow("")]
    [DataRow("..")]
    [DataRow("trailing.")]
    [DataRow("trailing ")]
    [DataRow("nul\0inside")]
    public void TryName_ANameNoStreamCanHave_IsRefused_SayingWhy(string captured)
    {
        Assert.IsFalse(AlternateStreams.TryName(Encoding.UTF8.GetBytes(captured), out _, out var why));
        Assert.IsFalse(string.IsNullOrWhiteSpace(why));
    }

    [TestMethod]
    public void TryName_ANameLongerThanWindowsHolds_IsRefused()
    {
        Assert.IsTrue(AlternateStreams.TryName(Encoding.UTF8.GetBytes(new string('s', 255)), out _, out _));
        Assert.IsFalse(AlternateStreams.TryName(Encoding.UTF8.GetBytes(new string('s', 256)), out _, out _));
    }

    [TestMethod]
    public void TryName_BytesThatAreNotUtf8_AreRefused()
    {
        Assert.IsFalse(AlternateStreams.TryName([0x5A, 0xFF, 0x6F], out _, out var why));
        Assert.Contains("UTF-8", why, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps alternate data streams")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Write_OnWindows_PutsTheStreamOnTheFile_AndReadGivesItBack()
    {
        var file = Path.Combine(_root, "carrier.bin");
        File.WriteAllText(file, "main");

        Assert.IsNull(AlternateStreams.OfThisPlatform.Write(file, "Zone.Identifier", "[ZoneTransfer]"u8));

        Assert.AreEqual("main", File.ReadAllText(file));
        Assert.AreEqual("[ZoneTransfer]", File.ReadAllText(file + ":Zone.Identifier"));
        SequenceAssert.AreEqual("[ZoneTransfer]"u8.ToArray(), AlternateStreams.OfThisPlatform.Read(file, "Zone.Identifier", 14)!);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps alternate data streams")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Read_OnWindows_GivesNothing_ForAStreamNotThere_OrOfAnotherLength()
    {
        var file = Path.Combine(_root, "carrier.bin");
        File.WriteAllText(file, "main");
        File.WriteAllText(file + ":notes", "four");

        Assert.IsNull(AlternateStreams.OfThisPlatform.Read(file, "absent", 4));
        Assert.IsNull(AlternateStreams.OfThisPlatform.Read(file, "notes", 5));
        Assert.IsNotNull(AlternateStreams.OfThisPlatform.Read(file, "notes", 4));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps alternate data streams")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Write_OnWindows_OverAStreamAlreadyThere_LeavesOnlyWhatIsWritten()
    {
        var file = Path.Combine(_root, "carrier.bin");
        File.WriteAllText(file, "main");
        File.WriteAllText(file + ":notes", "a longer stream than the one written");

        Assert.IsNull(AlternateStreams.OfThisPlatform.Write(file, "notes", "short"u8));

        Assert.AreEqual("short", File.ReadAllText(file + ":notes"));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "POSIX keeps no alternate data streams, and a colon is a name's own character there")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void Write_OnPosix_IsRefused_AndMakesNoFileBesideTheOne_ItWasFor()
    {
        var file = Path.Combine(_root, "carrier.bin");
        File.WriteAllText(file, "main");

        var refusal = AlternateStreams.OfThisPlatform.Write(file, "Zone.Identifier", "[ZoneTransfer]"u8);

        Assert.IsNotNull(refusal);
        Assert.Contains("Windows", refusal, StringComparison.Ordinal);
        Assert.AreEqual("carrier.bin", Path.GetFileName(Assert.ContainsSingle(Directory.GetFiles(_root))));
        Assert.IsNull(AlternateStreams.OfThisPlatform.Read(file, "Zone.Identifier", 14));
    }

    [TestMethod]
    public async Task WriteBackAsync_WritesEachStreamOntoTheFile_AsItReads_AndLeavesNothingToSay()
    {
        var calls = new Held();
        var read = new List<ObjectId>();

        var left = await AlternateStreams.WriteBackAsync(
            "carrier.bin", [Stream("Zone.Identifier", 1), Stream("notes", 2)], Reads(read), calls, CancellationToken.None);

        Assert.IsEmpty(left);
        CollectionAssert.AreEqual(new[] { IdOf(1), IdOf(2) }, read);
        SequenceAssert.AreEqual(ContentOf(1), calls.Streams[("carrier.bin", "Zone.Identifier")]);
        SequenceAssert.AreEqual(ContentOf(2), calls.Streams[("carrier.bin", "notes")]);
    }

    [TestMethod]
    [DataRow(@"..\..\escape")]
    [DataRow("..")]
    [DataRow("other:$DATA")]
    public async Task WriteBackAsync_AStreamWhoseNameNoStreamCanHave_IsNeitherReadNorWritten_AndIsNamed(string name)
    {
        var calls = new Held();
        var read = new List<ObjectId>();

        var left = await AlternateStreams.WriteBackAsync(
            "carrier.bin", [Stream(name, 1), Stream("notes", 2)], Reads(read), calls, CancellationToken.None);

        Assert.Contains(name, Assert.ContainsSingle(left), StringComparison.Ordinal);
        CollectionAssert.AreEqual(new[] { IdOf(2) }, read);
        Assert.AreEqual(("carrier.bin", "notes"), Assert.ContainsSingle(calls.Streams.Keys));
    }

    [TestMethod]
    public async Task WriteBackAsync_AStreamWhoseContentWillNotRead_IsNotWritten_AndSaysWhy()
    {
        var calls = new Held();

        var left = await AlternateStreams.WriteBackAsync(
            "carrier.bin",
            [Stream("Zone.Identifier", 1), Stream("notes", 2)],
            (objectId, _) => ValueTask.FromResult<(byte[]?, string?)>(
                objectId == IdOf(1) ? (null, "its record read AuthenticationFailed") : (ContentOf(2), null)),
            calls,
            CancellationToken.None);

        var why = Assert.ContainsSingle(left);
        Assert.Contains("Zone.Identifier", why, StringComparison.Ordinal);
        Assert.Contains("its record read AuthenticationFailed", why, StringComparison.Ordinal);
        Assert.AreEqual(("carrier.bin", "notes"), Assert.ContainsSingle(calls.Streams.Keys));
    }

    [TestMethod]
    public async Task WriteBackAsync_AStreamTheCallsRefuse_SaysWhy()
    {
        var left = await AlternateStreams.WriteBackAsync(
            "carrier.bin", [Stream("Zone.Identifier", 1)], Reads([]), new Held { Refusal = "the volume keeps no streams" },
            CancellationToken.None);

        var why = Assert.ContainsSingle(left);
        Assert.Contains("Zone.Identifier", why, StringComparison.Ordinal);
        Assert.Contains("the volume keeps no streams", why, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task WriteBackAsync_NoStreams_ReadsAndWritesNothing()
    {
        var calls = new Held();
        var read = new List<ObjectId>();

        Assert.IsEmpty(await AlternateStreams.WriteBackAsync("carrier.bin", [], Reads(read), calls, CancellationToken.None));
        Assert.IsEmpty(read);
        Assert.IsEmpty(calls.Streams);
    }

    [TestMethod]
    public void TakesNamespaces_OnlyOnLinux() =>
        Assert.AreEqual(OperatingSystem.IsLinux(), ExtendedAttributes.TakesNamespaces);

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS write a file's extended attributes")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task WriteBackAsAttributesAsync_OnPosix_KeepsEachStreamAsTheAttributeOfItsName_AsItReads()
    {
        var file = Carrier();
        var read = new List<ObjectId>();

        var left = await AlternateStreams.WriteBackAsAttributesAsync(
            file, Streams(Stream("user.origin", 1), Stream("user.notes", 2)), Reads(read), new AttributeLimits(TakesNamespaces: true), CancellationToken.None);

        Assert.IsEmpty(left);
        CollectionAssert.AreEqual(new[] { IdOf(1), IdOf(2) }, read);
        SequenceAssert.AreEqual(ContentOf(1), Xattr.Get(file, "user.origin"));
        SequenceAssert.AreEqual(ContentOf(2), Xattr.Get(file, "user.notes"));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS write a file's extended attributes")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task WriteBackAsAttributesAsync_AStreamOutsideTheUserNamespace_IsNeitherReadNorWritten_WhereNamesTakeNamespaces_AndIsNamed()
    {
        var file = Carrier();
        var read = new List<ObjectId>();

        var left = await AlternateStreams.WriteBackAsAttributesAsync(
            file, Streams(Stream("Zone.Identifier", 1), Stream("user.notes", 2)), Reads(read), new AttributeLimits(TakesNamespaces: true), CancellationToken.None);

        var why = Assert.ContainsSingle(left);
        Assert.Contains("'Zone.Identifier'", why, StringComparison.Ordinal);
        Assert.Contains("user namespace", why, StringComparison.Ordinal);
        CollectionAssert.AreEqual(new[] { IdOf(2) }, read);
        SequenceAssert.AreEqual(ContentOf(2), Xattr.Get(file, "user.notes"));
    }

    [TestMethod]
    public async Task WriteBackAsAttributesAsync_AStreamWhoseNameTheFilesOwnAttributeHas_IsNeitherReadNorWritten_AndIsNamed()
    {
        var file = Carrier();
        var read = new List<ObjectId>();
        var metadata = Streams(Stream("user.tag", 1)) with
        {
            ExtendedAttributes = [new ExtendedAttributeEntry("user.tag"u8.ToArray(), "own"u8.ToArray())],
        };

        var left = await AlternateStreams.WriteBackAsAttributesAsync(file, metadata, Reads(read), new AttributeLimits(TakesNamespaces: true), CancellationToken.None);

        var why = Assert.ContainsSingle(left);
        Assert.Contains("'user.tag'", why, StringComparison.Ordinal);
        Assert.Contains("own captured attribute", why, StringComparison.Ordinal);
        Assert.IsEmpty(read);
    }

    [TestMethod]
    public async Task WriteBackAsAttributesAsync_AStreamWhoseContentWillNotRead_IsNotWritten_AndSaysWhy()
    {
        var left = await AlternateStreams.WriteBackAsAttributesAsync(
            Carrier(),
            Streams(Stream("user.origin", 1)),
            (_, _) => ValueTask.FromResult<(byte[]?, string?)>((null, "its record read AuthenticationFailed")),
            new AttributeLimits(TakesNamespaces: true),
            CancellationToken.None);

        var why = Assert.ContainsSingle(left);
        Assert.Contains("'user.origin'", why, StringComparison.Ordinal);
        Assert.Contains("its record read AuthenticationFailed", why, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux refuses an attribute named in no namespace")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task WriteBackAsAttributesAsync_OnLinux_AnAttributeTheVolumeRefuses_SaysSo()
    {
        var left = await AlternateStreams.WriteBackAsAttributesAsync(
            Carrier(), Streams(Stream("Zone.Identifier", 1)), Reads([]), new AttributeLimits(TakesNamespaces: false), CancellationToken.None);

        var why = Assert.ContainsSingle(left);
        Assert.Contains("'Zone.Identifier'", why, StringComparison.Ordinal);
        Assert.Contains("the volume refused it", why, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task WriteBackAsAttributesAsync_AStreamLargerThanTheLimitsTake_IsNeitherReadNorWritten_AndIsNamed()
    {
        var read = new List<ObjectId>();
        var metadata = new EntryMetadata
        {
            AlternateStreams =
            [
                new AlternateStreamEntry("user.big"u8.ToArray(), IdOf(1), 70_000),
                new AlternateStreamEntry(Encoding.UTF8.GetBytes("user." + new string('n', 300)), IdOf(2), 4),
            ],
        };

        var left = await AlternateStreams.WriteBackAsAttributesAsync(
            Carrier(), metadata, Reads(read), new AttributeLimits(TakesNamespaces: true, MaxNameBytes: 255, MaxValueBytes: 65_536),
            CancellationToken.None);

        Assert.HasCount(2, left);
        Assert.Contains("'user.big'", left[0], StringComparison.Ordinal);
        Assert.Contains("65536", left[0], StringComparison.Ordinal);
        Assert.Contains("255", left[1], StringComparison.Ordinal);
        Assert.IsEmpty(read);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "the limits are Linux's own, held on every volume")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task WriteBackAsAttributesAsync_OnLinux_TheKernelRefusesWhatItsLimitsRefuse()
    {
        // With no limits stated the rule passes both, and the volume refuses
        // them, as the limits say it will on any Linux volume.
        var metadata = new EntryMetadata
        {
            AlternateStreams =
            [
                new AlternateStreamEntry("user.big"u8.ToArray(), IdOf(1), 65_537),
                new AlternateStreamEntry(Encoding.UTF8.GetBytes("user." + new string('n', 251)), IdOf(2), 4),
            ],
        };

        var left = await AlternateStreams.WriteBackAsAttributesAsync(
            Carrier(),
            metadata,
            (objectId, _) => ValueTask.FromResult<(byte[]?, string?)>((new byte[objectId == IdOf(1) ? 65_537 : 4], null)),
            new AttributeLimits(TakesNamespaces: true),
            CancellationToken.None);

        Assert.HasCount(2, left);
        Assert.IsTrue(left.All(why => why.Contains("the volume refused it", StringComparison.Ordinal)), string.Join("; ", left));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux holds no attribute larger than 64 KiB")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task WriteBackOnThisPlatformAsync_OnLinux_NamesAStreamLargerThanLinuxHolds_WithoutReadingIt()
    {
        var read = new List<ObjectId>();
        var metadata = new EntryMetadata { AlternateStreams = [new AlternateStreamEntry("user.big"u8.ToArray(), IdOf(1), 70_000)] };

        var left = await AlternateStreams.WriteBackOnThisPlatformAsync(Carrier(), metadata, Reads(read), calls: null, CancellationToken.None);

        Assert.IsNotNull(left);
        Assert.Contains("65536", Assert.ContainsSingle(left), StringComparison.Ordinal);
        Assert.IsEmpty(read);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "macOS takes an attribute's name of at most 127 bytes")]
    [PlatformTrait(TestPlatforms.MacOs)]
    public async Task WriteBackOnThisPlatformAsync_OnMacOs_NamesAStreamWhoseNameIsLongerThanMacOsTakes_WithoutReadingIt()
    {
        var read = new List<ObjectId>();
        var metadata = new EntryMetadata
        {
            AlternateStreams = [new AlternateStreamEntry(Encoding.UTF8.GetBytes(new string('n', 128)), IdOf(1), 4)],
        };

        var left = await AlternateStreams.WriteBackOnThisPlatformAsync(Carrier(), metadata, Reads(read), calls: null, CancellationToken.None);

        Assert.IsNotNull(left);
        Assert.Contains("127", Assert.ContainsSingle(left), StringComparison.Ordinal);
        Assert.IsEmpty(read);
    }

    [TestMethod]
    public async Task WriteBackOnThisPlatformAsync_CallsStoodInForThePlatforms_AreGivenEveryStream()
    {
        var calls = new Held();

        var left = await AlternateStreams.WriteBackOnThisPlatformAsync(
            "carrier.bin", Streams(Stream("Zone.Identifier", 1), Stream("notes", 2)), Reads([]), calls, CancellationToken.None);

        Assert.IsNotNull(left);
        Assert.IsEmpty(left);
        SequenceAssert.AreEqual(ContentOf(1), calls.Streams[("carrier.bin", "Zone.Identifier")]);
        SequenceAssert.AreEqual(ContentOf(2), calls.Streams[("carrier.bin", "notes")]);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps alternate data streams")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task WriteBackOnThisPlatformAsync_OnWindows_WritesEachStreamOntoTheFile()
    {
        var file = Carrier();

        var left = await AlternateStreams.WriteBackOnThisPlatformAsync(
            file, Streams(Stream("notes", 2)), Reads([]), calls: null, CancellationToken.None);

        Assert.IsNotNull(left);
        Assert.IsEmpty(left);
        SequenceAssert.AreEqual(ContentOf(2), File.ReadAllBytes(file + ":notes"));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS write a file's extended attributes")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task WriteBackOnThisPlatformAsync_OnPosix_KeepsAStreamInTheUserNamespaceAsItsAttribute_AndMakesNoFileBeside()
    {
        var file = Carrier();

        var left = await AlternateStreams.WriteBackOnThisPlatformAsync(
            file, Streams(Stream("user.origin", 1)), Reads([]), calls: null, CancellationToken.None);

        Assert.IsNotNull(left);
        Assert.IsEmpty(left);
        SequenceAssert.AreEqual(ContentOf(1), Xattr.Get(file, "user.origin"));
        Assert.AreEqual("carrier.bin", Path.GetFileName(Assert.ContainsSingle(Directory.GetFiles(_root))));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux takes a file's own attribute only in its user namespace")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task WriteBackOnThisPlatformAsync_OnLinux_NamesAStreamOutsideTheUserNamespace()
    {
        var left = await AlternateStreams.WriteBackOnThisPlatformAsync(
            Carrier(), Streams(Stream("Zone.Identifier", 1)), Reads([]), calls: null, CancellationToken.None);

        Assert.IsNotNull(left);
        Assert.Contains("user namespace", Assert.ContainsSingle(left), StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "macOS takes an attribute of any name")]
    [PlatformTrait(TestPlatforms.MacOs)]
    public async Task WriteBackOnThisPlatformAsync_OnMacOs_KeepsAStreamOfAnyNameAsAnAttribute()
    {
        var file = Carrier();

        var left = await AlternateStreams.WriteBackOnThisPlatformAsync(
            file, Streams(Stream("Zone.Identifier", 1)), Reads([]), calls: null, CancellationToken.None);

        Assert.IsNotNull(left);
        Assert.IsEmpty(left);
        SequenceAssert.AreEqual(ContentOf(1), Xattr.Get(file, "Zone.Identifier"));
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

    private static ObjectId IdOf(byte seed) => ObjectId.FromBytes(Enumerable.Repeat(seed, 32).ToArray());

    private static byte[] ContentOf(byte seed) => Enumerable.Repeat((byte)(seed + 0x40), 17).ToArray();

    private static EntryMetadata Streams(params AlternateStreamEntry[] streams) => new() { AlternateStreams = streams };

    private string Carrier()
    {
        var file = Path.Combine(_root, "carrier.bin");
        File.WriteAllText(file, "main");
        return file;
    }

    private static AlternateStreamEntry Stream(string name, byte seed) =>
        new(Encoding.UTF8.GetBytes(name), IdOf(seed), (ulong)ContentOf(seed).Length);

    /// <summary>Reads each stream's content as <see cref="ContentOf"/> gives it, noting what was asked for.</summary>
    private static Func<ObjectId, CancellationToken, ValueTask<(byte[]? Content, string? Unread)>> Reads(List<ObjectId> asked) =>
        (objectId, _) =>
        {
            asked.Add(objectId);
            return ValueTask.FromResult<(byte[]?, string?)>((ContentOf(objectId.ToArray()[0]), null));
        };

    /// <summary>Calls that hold each stream in memory, or refuse every write.</summary>
    private sealed class Held : IAlternateStreams
    {
        public Dictionary<(string File, string Name), byte[]> Streams { get; } = [];

        public string? Refusal { get; init; }

        public byte[]? Read(string file, string name, ulong length) => null;

        public string? Write(string file, string name, ReadOnlySpan<byte> content)
        {
            if (Refusal is { } refusal)
            {
                return refusal;
            }

            Streams[(file, name)] = content.ToArray();
            return null;
        }
    }
}
