using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// The local provider's hardening duties
/// (docs/architecture/05-storage-providers.md §4.1): symlink redirection is
/// refused, the write spool is invisible, and a completed put is durably
/// readable.
/// </summary>
[TestClass]
public sealed class LocalFileSystemSecurityTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-security-tests", Guid.NewGuid().ToString("n"));

    private readonly string _outside =
        Path.Combine(Path.GetTempPath(), "fbp-security-tests", Guid.NewGuid().ToString("n") + "-outside");

    private static Func<CancellationToken, ValueTask<Stream>> Content(byte[] bytes) =>
        _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));

    [TestMethod]
    public async Task Store_ASymlinkedDirectoryInsideTheRoot_RefusesTheOperation()
    {
        var store = new LocalFileSystemObjectStore(_root);
        Directory.CreateDirectory(_outside);

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "blobs"), _outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The platform or account cannot create symlinks; nothing to test.
            return;
        }

        // Writing through the symlinked component would land outside the
        // repository root: a genuine fault, reported as an exception.
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await store.PutAsync(
                ObjectKey.Parse("blobs/data/escape"),
                Content([0x01]),
                PutConditions.None,
                CancellationToken.None));

        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(_outside));
    }

    [TestMethod]
    public async Task List_WhileAPutIsSpooling_OmitsTheSpoolFile()
    {
        var store = new LocalFileSystemObjectStore(_root);
        await store.PutAsync(ObjectKey.Parse("repository-format"), Content([0x01]), PutConditions.None, CancellationToken.None);

        // Leave a stray spool file behind, as a crash mid-put would.
        Directory.CreateDirectory(Path.Combine(_root, ".fbp-tmp"));
        await File.WriteAllBytesAsync(Path.Combine(_root, ".fbp-tmp", "stray"), [0xFF], CancellationToken.None);

        var listed = new List<string>();
        await foreach (var entry in store.ListAsync(ObjectPrefix.All, ListOptions.Default, CancellationToken.None))
        {
            listed.Add(entry.Key.Value);
        }

        SequenceAssert.AreEqual(["repository-format"], listed);
    }

    [TestMethod]
    public async Task Put_OnceComplete_IsImmediatelyReadableAtItsFullLength()
    {
        var store = new LocalFileSystemObjectStore(_root);
        var payload = new byte[65_536];
        payload[^1] = 0x5A;

        var put = await store.PutAsync(ObjectKey.Parse("blobs/data/abcd/durable"), Content(payload), PutConditions.None, CancellationToken.None);
        Assert.AreEqual(PutOutcome.Created, put.Outcome);

        var metadata = await store.GetMetadataAsync(ObjectKey.Parse("blobs/data/abcd/durable"), CancellationToken.None);
        Assert.IsTrue(metadata.Found);
        Assert.AreEqual(payload.Length, metadata.Metadata!.Length);
    }

    [TestMethod]
    public async Task Put_WhenTheContentFactoryThrows_LeavesNoSpoolResidue()
    {
        var store = new LocalFileSystemObjectStore(_root);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await store.PutAsync(
                ObjectKey.Parse("blobs/data/abcd/failed"),
                _ => throw new InvalidOperationException("source vanished"),
                PutConditions.None,
                CancellationToken.None));

        var spool = Path.Combine(_root, ".fbp-tmp");
        Assert.IsTrue(!Directory.Exists(spool) || !Directory.EnumerateFileSystemEntries(spool).Any());

        var metadata = await store.GetMetadataAsync(ObjectKey.Parse("blobs/data/abcd/failed"), CancellationToken.None);
        Assert.IsFalse(metadata.Found);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var directory in new[] { _root, _outside })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
