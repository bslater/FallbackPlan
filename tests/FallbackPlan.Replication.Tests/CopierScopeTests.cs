using FallbackPlan.Replication;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Replication.Tests;

/// <summary>
/// A pass between reconciliations carries every kind of object this build
/// writes (FR-REP-005, FR-MAN-019). The catch-all phase is the one an
/// incremental pass leaves out, and that is only safe for prefixes this build
/// has never heard of: an object this build writes under no named phase would
/// wait a day to reach a destination, and every sync in between would ask the
/// destination for it.
/// </summary>
[TestClass]
public sealed class CopierScopeTests
{
    /// <summary>
    /// One object under every prefix of specification 01 §2 that this build
    /// writes to a store a destination is copied from. The lifecycle prefixes
    /// never leave staging, and set-configuration objects are written by
    /// nothing since ADR-0061 superseded ADR-0071.
    /// </summary>
    private static readonly string[] Written =
    [
        "repository-format",
        "blobs/data/aaaa/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "blobs/meta/bbbb/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        "index/delta/0000000000000000/ugq2dinbugq2dinbugq2dinbue",
        "index/checkpoint/0000000000000000/ytb3hq5yytb3hq5yytb3hq5yyq",
        "snapshots/device-a/set-a/snapshot-a",
        "journal/2tknjvgu2tknjvgu2tknjvgu2q/0000000000000001",
        "audit/2026-10/record-a",
        "format-upgrade/0005",
        "hints/placement/snapshot-a",
        "hints/identity/cccc/source-key/0000000000000001/snapshot-a",
        "hints/identity-pack/device-a/0000000000000001/snapshot-a/0000000000000000",
    ];

    private string _root = null!;

    [TestInitialize]
    public void SetUp() => _root = Path.Combine(Path.GetTempPath(), $"fbp-copier-scope-{Guid.NewGuid():N}");

    [TestCleanup]
    public void TearDown()
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

    [TestMethod]
    public async Task AnIncrementalPass_CarriesEveryKindOfObjectThisBuildWrites()
    {
        var source = new LocalFileSystemObjectStore(Path.Combine(_root, "source"));
        var replica = new LocalFileSystemObjectStore(Path.Combine(_root, "replica"));
        foreach (var key in Written)
        {
            await PutAsync(source, key);
        }

        await StoreToStoreCopier.CopyAsync(source, replica, CancellationToken.None, scope: CopyScope.Incremental);

        foreach (var key in Written)
        {
            Assert.IsTrue(
                (await replica.GetMetadataAsync(ObjectKey.Parse(key), CancellationToken.None)).Found,
                $"{key} waits for the reconciling pass, though this build writes it");
        }
    }

    [TestMethod]
    public void WhatAPassCarries_IsWhatItsScopeWalks()
    {
        Assert.IsTrue(StoreToStoreCopier.Carries("format-upgrade/0005", CopyScope.Incremental));
        Assert.IsFalse(StoreToStoreCopier.Carries("something-else/object-c", CopyScope.Incremental));
        Assert.IsTrue(StoreToStoreCopier.Carries("something-else/object-c", CopyScope.Reconcile));
        Assert.IsFalse(StoreToStoreCopier.Carries("tombstones/03/object", CopyScope.Reconcile));
        Assert.IsFalse(StoreToStoreCopier.Carries("leases/set/lease", CopyScope.Reconcile));
    }

    private static async Task PutAsync(LocalFileSystemObjectStore store, string key)
    {
        var payload = new byte[64];
        Random.Shared.NextBytes(payload);
        await store.PutAsync(
            ObjectKey.Parse(key),
            _ => ValueTask.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            PutConditions.None,
            CancellationToken.None);
    }
}
