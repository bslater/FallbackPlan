using System.Text;
using FallbackPlan.Storage.Abstractions;
using Xunit;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// The reusable provider contract suite (docs/architecture/05-storage-providers.md
/// §2; FR-REP-002, NFR-PORT-002, NFR-PORT-004): every <see cref="IObjectStore"/>
/// implementation inherits this and must pass unchanged. Expected outcomes are
/// asserted as results, never as exceptions; the content factory is asserted
/// as re-invocable, which is what makes retry possible at all.
/// </summary>
public abstract class ObjectStoreContractTests
{
    /// <summary>Creates a fresh, empty store for one test.</summary>
    protected abstract IObjectStore CreateStore();

    private static Func<CancellationToken, ValueTask<Stream>> ContentFactory(byte[] bytes) =>
        _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));

    private static async Task<byte[]> ReadAllAsync(OpenReadResult result)
    {
        Assert.Equal(OpenReadOutcome.Found, result.Outcome);

        using (result)
        {
            using var buffer = new MemoryStream();
            await result.Content!.CopyToAsync(buffer, CancellationToken.None);
            return buffer.ToArray();
        }
    }

    [Fact]
    public async Task Put_then_open_read_round_trips_bytes()
    {
        var store = CreateStore();
        var key = ObjectKey.Parse("blobs/data/abcd/roundtrip");
        var payload = Encoding.ASCII.GetBytes("segment record bytes");

        var put = await store.PutAsync(key, ContentFactory(payload), PutConditions.None, CancellationToken.None);
        Assert.Equal(PutOutcome.Created, put.Outcome);

        var read = await store.OpenReadAsync(key, range: null, CancellationToken.None);
        Assert.Equal(payload, await ReadAllAsync(read));
    }

    [Fact]
    public async Task Ranged_read_returns_exactly_the_requested_slice()
    {
        var store = CreateStore();

        if (!store.Capabilities.RangedReads)
        {
            return;
        }

        var key = ObjectKey.Parse("blobs/data/abcd/ranged");
        var payload = Enumerable.Range(0, 1000).Select(value => (byte)value).ToArray();
        await store.PutAsync(key, ContentFactory(payload), PutConditions.None, CancellationToken.None);

        var read = await store.OpenReadAsync(key, new ObjectRange(100, 250), CancellationToken.None);

        Assert.Equal(payload[100..350], await ReadAllAsync(read));
    }

    [Fact]
    public async Task A_range_past_the_end_reports_range_not_satisfiable_as_a_result()
    {
        var store = CreateStore();

        if (!store.Capabilities.RangedReads)
        {
            return;
        }

        var key = ObjectKey.Parse("blobs/data/abcd/short");
        await store.PutAsync(key, ContentFactory(new byte[10]), PutConditions.None, CancellationToken.None);

        using var read = await store.OpenReadAsync(key, new ObjectRange(5, 10), CancellationToken.None);

        Assert.Equal(OpenReadOutcome.RangeNotSatisfiable, read.Outcome);
    }

    [Fact]
    public async Task Metadata_reports_length_for_an_existing_object()
    {
        var store = CreateStore();
        var key = ObjectKey.Parse("index/delta/0000000000000001/meta");
        await store.PutAsync(key, ContentFactory(new byte[123]), PutConditions.None, CancellationToken.None);

        var metadata = await store.GetMetadataAsync(key, CancellationToken.None);

        Assert.True(metadata.Found);
        Assert.Equal(123, metadata.Metadata!.Length);
    }

    [Fact]
    public async Task Metadata_reports_not_found_as_a_result_not_an_exception()
    {
        var store = CreateStore();

        var metadata = await store.GetMetadataAsync(ObjectKey.Parse("absent"), CancellationToken.None);

        Assert.False(metadata.Found);
    }

    [Fact]
    public async Task Open_read_of_a_missing_object_reports_not_found_as_a_result()
    {
        var store = CreateStore();

        using var read = await store.OpenReadAsync(ObjectKey.Parse("absent"), range: null, CancellationToken.None);

        Assert.Equal(OpenReadOutcome.NotFound, read.Outcome);
    }

    [Fact]
    public async Task A_second_put_of_the_same_key_reports_already_exists_not_an_exception()
    {
        // The doc's own scenario (§2.2): an idempotent retry of a write that
        // in fact succeeded is the most common expected outcome.
        var store = CreateStore();
        var key = ObjectKey.Parse("snapshots/device/set/snap-1");
        var payload = "published snapshot"u8.ToArray();

        var first = await store.PutAsync(key, ContentFactory(payload), PutConditions.IfNotExists, CancellationToken.None);
        var second = await store.PutAsync(key, ContentFactory(payload), PutConditions.IfNotExists, CancellationToken.None);

        Assert.Equal(PutOutcome.Created, first.Outcome);
        Assert.Equal(PutOutcome.AlreadyExists, second.Outcome);

        var read = await store.OpenReadAsync(key, range: null, CancellationToken.None);
        Assert.Equal(payload, await ReadAllAsync(read));
    }

    [Fact]
    public async Task The_content_factory_is_invoked_and_its_stream_disposed()
    {
        var store = CreateStore();
        var key = ObjectKey.Parse("journal/writer/0000000000000001");
        var invocations = 0;
        TrackingStream? lastStream = null;

        var put = await store.PutAsync(
            key,
            _ =>
            {
                invocations++;
                lastStream = new TrackingStream("intent record"u8.ToArray());
                return ValueTask.FromResult<Stream>(lastStream);
            },
            PutConditions.None,
            CancellationToken.None);

        Assert.Equal(PutOutcome.Created, put.Outcome);
        Assert.True(invocations >= 1, "The factory must be invoked at least once.");
        Assert.True(lastStream!.Disposed, "The provider owns and disposes each stream the factory produces.");
    }

    [Fact]
    public async Task The_content_factory_may_be_invoked_more_than_once_without_error()
    {
        // The contract half of §2.1: the caller guarantees the factory can
        // produce the content again, and the provider may call it as many
        // times as its retry policy requires. This proves a multi-invocation
        // factory works; fault-injected retries arrive with Wave F.
        var store = CreateStore();
        var key = ObjectKey.Parse("blobs/meta/abcd/refetchable");
        var invocations = 0;

        Func<CancellationToken, ValueTask<Stream>> factory = _ =>
        {
            invocations++;
            return ValueTask.FromResult<Stream>(new MemoryStream("same bytes every time"u8.ToArray()));
        };

        _ = await factory(CancellationToken.None);
        var put = await store.PutAsync(key, factory, PutConditions.None, CancellationToken.None);

        Assert.Equal(PutOutcome.Created, put.Outcome);
        Assert.Equal(2, invocations);
    }

    [Fact]
    public async Task Listing_returns_ordinal_key_order_and_honours_the_prefix()
    {
        var store = CreateStore();
        var keys = new[] { "blobs/data/zz/2", "blobs/data/aa/1", "blobs/meta/aa/3", "index/delta/0000000000000001/d" };

        foreach (var value in keys)
        {
            await store.PutAsync(ObjectKey.Parse(value), ContentFactory([0x01]), PutConditions.None, CancellationToken.None);
        }

        var listed = new List<string>();
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            listed.Add(entry.Key.Value);
        }

        Assert.Equal(["blobs/data/aa/1", "blobs/data/zz/2", "blobs/meta/aa/3"], listed);
    }

    [Fact]
    public async Task A_resume_token_persisted_across_enumerators_resumes_strictly_after_its_entry()
    {
        var store = CreateStore();

        for (var i = 0; i < 5; i++)
        {
            await store.PutAsync(
                ObjectKey.Parse($"index/delta/0000000000000001/entry-{i}"),
                ContentFactory([0x01]),
                PutConditions.None,
                CancellationToken.None);
        }

        string? resumeToken = null;
        var firstPass = new List<string>();
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("index/"), ListOptions.Default, CancellationToken.None))
        {
            firstPass.Add(entry.Key.Value);
            if (firstPass.Count == 2)
            {
                resumeToken = entry.ResumeToken;
                break;
            }
        }

        // A brand-new enumeration — a different process, conceptually —
        // resumes strictly after the persisted position.
        var secondPass = new List<string>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("index/"),
            ListOptions.Default with { ResumeAfter = resumeToken },
            CancellationToken.None))
        {
            secondPass.Add(entry.Key.Value);
        }

        Assert.Equal(
            ["index/delta/0000000000000001/entry-2", "index/delta/0000000000000001/entry-3", "index/delta/0000000000000001/entry-4"],
            secondPass);
    }

    [Fact]
    public async Task Delete_reports_deleted_then_not_found_as_results()
    {
        var store = CreateStore();
        var key = ObjectKey.Parse("tombstones/blob-1");
        await store.PutAsync(key, ContentFactory([0x01]), PutConditions.None, CancellationToken.None);

        var first = await store.DeleteAsync(key, DeleteConditions.None, CancellationToken.None);
        var second = await store.DeleteAsync(key, DeleteConditions.None, CancellationToken.None);

        Assert.Equal(DeleteOutcome.Deleted, first.Outcome);
        Assert.Equal(DeleteOutcome.NotFound, second.Outcome);

        var metadata = await store.GetMetadataAsync(key, CancellationToken.None);
        Assert.False(metadata.Found);
    }

    /// <summary>A stream that records whether the provider disposed it.</summary>
    private sealed class TrackingStream : MemoryStream
    {
        internal TrackingStream(byte[] bytes)
            : base(bytes, writable: false)
        {
        }

        internal bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
