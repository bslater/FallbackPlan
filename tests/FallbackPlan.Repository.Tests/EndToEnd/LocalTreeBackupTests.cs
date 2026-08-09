using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The real thing: <see cref="LocalFileSystemSource"/> scanning an actual
/// directory into a published snapshot, restored byte-identical from a cold
/// reader — the wave-S scanner and the wave-T1 publisher joined with no
/// fake in between.
/// </summary>
[TestClass]
public sealed class LocalTreeBackupTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private readonly string _sourceRoot =
        Path.Combine(Path.GetTempPath(), "fbp-tree-backup-tests", Guid.NewGuid().ToString("n"));

    private void WriteSource(string relativePath, byte[] content)
    {
        var full = Path.Combine(_sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    [TestMethod]
    public async Task A_real_directory_tree_publishes_and_every_file_restores_byte_identical()
    {
        var random = new Random(7);
        var files = new Dictionary<string, byte[]>
        {
            ["report.pdf"] = new byte[300_000],
            ["projects/main.cs"] = new byte[40_000],
            ["projects/notes/todo.txt"] = new byte[100],
            ["projects/notes/done.txt"] = [],
        };
        foreach (var (path, content) in files)
        {
            random.NextBytes(content);
            WriteSource(path, content);
        }

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var orchestrator = new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory);

        var job = new SnapshotJob
        {
            Source = new LocalFileSystemSource(),
            RootPath = _sourceRoot,
            DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
            BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
            SnapshotId = Enumerable.Repeat((byte)0x11, 16).ToArray(),
            NowUnixMilliseconds = 1_722_600_000_000,
            DeclaredMaxDurationMs = 3_600_000,
            ExpiryGeneration = 5,
            ClientVersion = "fallbackplan-tests/1.0",
        };

        var published = await orchestrator.PublishAsync(job, CancellationToken.None);

        Assert.AreEqual(files.Count, published.Files.Count);
        Assert.IsEmpty(published.Failures);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        // Restore every file by walking the tree from the root, proving the
        // graph alone reaches the content.
        var restoredPaths = new List<string>();
        await WalkAndRestoreAsync(reader, published.RootTreeObjectId, string.Empty, restoredPaths, files);

        SequenceAssert.AreEqual(files.Keys.OrderBy(path => path, StringComparer.Ordinal), restoredPaths.OrderBy(path => path, StringComparer.Ordinal));
    }

    private static async Task WalkAndRestoreAsync(
        RepositoryReader reader,
        ObjectId treeId,
        string prefix,
        List<string> restoredPaths,
        IReadOnlyDictionary<string, byte[]> expected)
    {
        var chain = new List<TreeManifest>();
        ObjectId? next = treeId;
        while (next is { } id)
        {
            var read = await reader.ReadSegmentAsync(id, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
            var manifest = TreeManifestCodec.Decode(read.Plaintext!);
            chain.Add(manifest);
            next = manifest.Continuation;
        }

        foreach (var entry in TreeChain.ValidateAndFlatten(chain))
        {
            var name = Encoding.UTF8.GetString(entry.Name.Span);
            var path = prefix.Length == 0 ? name : prefix + "/" + name;

            if (entry.EntryKind == EntryKind.DirectoryPlaceholder)
            {
                await WalkAndRestoreAsync(reader, entry.ObjectId, path, restoredPaths, expected);
                continue;
            }

            var manifestRead = await reader.ReadSegmentAsync(entry.ObjectId, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, manifestRead.Outcome);
            var fileVersion = FileVersionManifestCodec.Decode(manifestRead.Plaintext!);

            using var restored = new MemoryStream();
            var restore = await new RestoreEngine(reader).RestoreFileAsync(fileVersion, restored, CancellationToken.None);
            Assert.IsTrue(restore.Success, restore.FailureDetail);
            SequenceAssert.AreEqual(expected[path], restored.ToArray());
            restoredPaths.Add(path);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(_sourceRoot))
        {
            Directory.Delete(_sourceRoot, recursive: true);
        }

        base.Dispose(disposing);
    }
}
