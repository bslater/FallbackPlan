using FallbackPlan.Repository.Index;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Index;

/// <summary>
/// The writer's sequence file is read whole while a save renames its
/// replacement into place, on Windows as elsewhere (NFR-PORT-001,
/// [ADR-0010](../../../docs/adr/0010-local-store-separation.md) Amendment 9).
/// </summary>
[TestClass]
public sealed class FileSequenceStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fbp-sequence-tests", Guid.NewGuid().ToString("n"));

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows,
        "a rename holds the file it renamed open for deletion until it returns, and only Windows refuses "
        + "a reader that does not share deletion with that handle")]
    public void Load_WhileASaveHoldsTheFileForDeletion_ReadsTheState()
    {
        var path = Path.Combine(_root, "sequence.txt");
        var store = new FileSequenceStateStore(path);
        store.Save(new SequenceState(7, [5, 6], null, 3));

        // The handle below is a save's rename held still, as the rename's own
        // handle is: opened for deletion and sharing everything. Delete-on-close
        // is how .NET asks for deletion access, so the file goes when it
        // closes, after the load this test is about.
        using (File.OpenHandle(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.DeleteOnClose))
        {
            var loaded = new FileSequenceStateStore(path).Load();

            Assert.AreEqual(7ul, loaded.NextSequence);
            CollectionAssert.AreEqual(new ulong[] { 5, 6 }, loaded.PendingSequences.ToArray());
            Assert.AreEqual(3ul, loaded.LastIndexGeneration);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written: the case is skipped off Windows.
        }
    }
}
