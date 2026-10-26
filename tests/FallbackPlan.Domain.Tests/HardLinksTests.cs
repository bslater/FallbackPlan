namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The call a restore gives a file a second name with (FR-RST-004;
/// ADR-0102): a hard link, the same file under another name, never a copy.
/// A name already taken, a file not there and a folder are refused, so a
/// restore that cannot link restores the name as a file of its own and says
/// so.
/// </summary>
[TestClass]
public sealed class HardLinksTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-hard-links-tests", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    public void TryCreate_GivesTheFileASecondName_ThatIsTheSameFile()
    {
        var first = Path.Combine(_root, "first.bin");
        File.WriteAllText(first, "shared");
        var second = Path.Combine(_root, "second.bin");

        Assert.IsTrue(HardLinks.TryCreate(first, second));

        // A copy would keep its own bytes. A link is the same file, so what
        // is written through one name is read through the other.
        File.AppendAllText(second, " and written through the second name");
        Assert.AreEqual("shared and written through the second name", File.ReadAllText(first));
    }

    [TestMethod]
    public void TryCreate_WhereTheNameIsTaken_AnswersFalse_AndLeavesWhatIsThere()
    {
        var first = Path.Combine(_root, "first.bin");
        File.WriteAllText(first, "shared");
        var taken = Path.Combine(_root, "taken.bin");
        File.WriteAllText(taken, "already here");

        Assert.IsFalse(HardLinks.TryCreate(first, taken));

        Assert.AreEqual("already here", File.ReadAllText(taken));
    }

    [TestMethod]
    public void TryCreate_WhereTheFileIsNotThere_AnswersFalse() =>
        Assert.IsFalse(HardLinks.TryCreate(Path.Combine(_root, "absent.bin"), Path.Combine(_root, "second.bin")));

    [TestMethod]
    public void TryCreate_ForAFolder_AnswersFalse()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "folder")).FullName;

        Assert.IsFalse(HardLinks.TryCreate(folder, Path.Combine(_root, "second")));
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
