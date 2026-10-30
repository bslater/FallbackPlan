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
