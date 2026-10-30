using System.Text;
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
}
