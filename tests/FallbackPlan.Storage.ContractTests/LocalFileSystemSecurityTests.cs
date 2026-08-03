using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using Xunit;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// The local provider's hardening duties
/// (docs/architecture/05-storage-providers.md §4.1): symlink redirection is
/// refused, the write spool is invisible, and a completed put is durably
/// readable.
/// </summary>
public sealed class LocalFileSystemSecurityTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-security-tests", Guid.NewGuid().ToString("n"));

    private readonly string _outside =
        Path.Combine(Path.GetTempPath(), "fbp-security-tests", Guid.NewGuid().ToString("n") + "-outside");

    private static Func<CancellationToken, ValueTask<Stream>> Content(byte[] bytes) =>
        _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));

    [Fact]
    public async Task A_symlinked_directory_inside_the_root_refuses_the_operation()
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
        await Assert.ThrowsAsync<IOException>(async () =>
            await store.PutAsync(
                ObjectKey.Parse("blobs/data/escape"),
                Content([0x01]),
                PutConditions.None,
                CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
    }

    [Fact]
    public async Task The_write_spool_never_appears_in_listings()
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

        Assert.Equal(["repository-format"], listed);
    }

    [Fact]
    public async Task A_completed_put_is_immediately_readable_with_the_correct_length()
    {
        var store = new LocalFileSystemObjectStore(_root);
        var payload = new byte[65_536];
        payload[^1] = 0x5A;

        var put = await store.PutAsync(ObjectKey.Parse("blobs/data/abcd/durable"), Content(payload), PutConditions.None, CancellationToken.None);
        Assert.Equal(PutOutcome.Created, put.Outcome);

        var metadata = await store.GetMetadataAsync(ObjectKey.Parse("blobs/data/abcd/durable"), CancellationToken.None);
        Assert.True(metadata.Found);
        Assert.Equal(payload.Length, metadata.Metadata!.Length);
    }

    [Fact]
    public async Task A_failed_content_factory_leaves_no_spool_residue()
    {
        var store = new LocalFileSystemObjectStore(_root);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.PutAsync(
                ObjectKey.Parse("blobs/data/abcd/failed"),
                _ => throw new InvalidOperationException("source vanished"),
                PutConditions.None,
                CancellationToken.None));

        var spool = Path.Combine(_root, ".fbp-tmp");
        Assert.True(!Directory.Exists(spool) || !Directory.EnumerateFileSystemEntries(spool).Any());

        var metadata = await store.GetMetadataAsync(ObjectKey.Parse("blobs/data/abcd/failed"), CancellationToken.None);
        Assert.False(metadata.Found);
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
