namespace FallbackPlan.Application.Tests;

/// <summary>
/// The job journal's file as a second process meets it (ADR-0027 §2).
/// <c>status</c> given a repository opens the journal without the writer
/// role, so a running service may be replacing <c>jobs.json</c> under it.
/// </summary>
[TestClass]
public sealed class JobStateStoreTests
{
    private string _directory = null!;

    [TestInitialize]
    public void Initialize() =>
        _directory = Directory.CreateTempSubdirectory("fp-job-journal-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    [FallbackPlan.TestSupport.PlatformCondition(FallbackPlan.TestSupport.TestPlatforms.Windows,
        "a rename holds the file it renamed open for deletion until it returns, and only Windows refuses "
        + "a reader that does not share deletion with that handle")]
    public void Open_WhileAWriteHoldsTheJournalForDeletion_StillReadsIt()
    {
        // A replace renames the new journal into place and holds it open for
        // deletion until the rename returns, and an open in that moment must
        // still read the old rows or the new ones rather than fail the
        // command that asked.
        //
        // The handle below is that moment held still, as in AtomicFileTests;
        // delete-on-close is how .NET asks for deletion access. The first
        // assertion is the control: a read that does not share deletion is
        // refused under that handle, so the second one proves something.
        var job = JobStateStore.Open(_directory).Begin("set-1", 1_000);
        var path = Path.Combine(_directory, "jobs.json");

        using (File.OpenHandle(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.DeleteOnClose))
        {
            Assert.Throws<IOException>(
                () => File.ReadAllText(path),
                "the held handle must refuse a reader that does not share deletion, or this test proves nothing");
            Assert.AreEqual(job.Id, Assert.ContainsSingle(JobStateStore.Open(_directory).Jobs).Id);
        }
    }
}
