using System.Globalization;
using System.Runtime.CompilerServices;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// A store made of other stores lists them as one, in the ordinal key order
/// every store's listing promises (docs/architecture/05-storage-providers.md
/// §2.3; NFR-PORT-004; ADR-0012 Amendment 8). Each part lists in that order
/// already, so the parts are merged rather than read one after another, and a
/// key two parts hold is listed once, as the first of them lists it.
/// </summary>
[TestClass]
public sealed class ListingMergeTests
{
    [TestMethod]
    public async Task Merge_InterleavesItsListings_InOrdinalKeyOrder()
    {
        var merged = await KeysAsync(
            Listing(("blobs/data/bbbb", 1), ("index/delta/1", 1), ("snapshots/a", 1)),
            Listing(("blobs/data/aaaa", 1), ("blobs/data/cccc", 1)));

        CollectionAssert.AreEqual(
            new[] { "blobs/data/aaaa", "blobs/data/bbbb", "blobs/data/cccc", "index/delta/1", "snapshots/a" },
            merged);
    }

    [TestMethod]
    public async Task Merge_ListsAKeyTwoListingsHoldOnce_AsTheFirstListsIt()
    {
        var entries = await EntriesAsync(
            Listing(("blobs/data/aaaa", 10)),
            Listing(("blobs/data/aaaa", 20), ("blobs/data/bbbb", 30)));

        Assert.HasCount(2, entries);
        Assert.AreEqual(10L, entries[0].Length, "the second listing answered for a key the first holds");
        Assert.AreEqual("blobs/data/bbbb", entries[1].Key.Value);
    }

    [TestMethod]
    public async Task Merge_ComparesKeysOrdinally_WhateverTheCulture()
    {
        // A digit sorts before an underscore ordinally; a culture's collation
        // puts punctuation before digits.
        Assert.IsGreaterThan(
            0,
            CultureInfo.InvariantCulture.CompareInfo.Compare("blobs/0", "blobs/_", CompareOptions.None),
            "the premise: a culture orders these otherwise");
        var merged = await KeysAsync(
            Listing(("blobs/0", 1), ("blobs/a", 1)),
            Listing(("blobs/_", 1), ("blobs/z", 1)));

        CollectionAssert.AreEqual(new[] { "blobs/0", "blobs/_", "blobs/a", "blobs/z" }, merged);
    }

    [TestMethod]
    public async Task Merge_OfNothing_OrOfEmptyListings_ListsNothing()
    {
        Assert.IsEmpty(await KeysAsync());
        Assert.IsEmpty(await KeysAsync(Listing(), Listing()));
    }

    [TestMethod]
    public async Task Merge_StoppedEarly_LetsGoOfEveryListing()
    {
        var released = new List<int>();

        await foreach (var _ in ListingMerge.MergeAsync(
            [Tracked(0, released, "a", "c"), Tracked(1, released, "b", "d")], CancellationToken.None))
        {
            break;
        }

        CollectionAssert.AreEquivalent(new[] { 0, 1 }, released);
    }

    private static async Task<List<string>> KeysAsync(params IAsyncEnumerable<ObjectEntry>[] listings) =>
        [.. (await EntriesAsync(listings)).Select(entry => entry.Key.Value)];

    private static async Task<List<ObjectEntry>> EntriesAsync(params IAsyncEnumerable<ObjectEntry>[] listings)
    {
        var entries = new List<ObjectEntry>();
        await foreach (var entry in ListingMerge.MergeAsync(listings, CancellationToken.None))
        {
            entries.Add(entry);
        }

        return entries;
    }

    private static async IAsyncEnumerable<ObjectEntry> Listing(params (string Key, long Length)[] entries)
    {
        foreach (var (key, length) in entries)
        {
            await Task.Yield();
            yield return new ObjectEntry(ObjectKey.Parse(key), length, key);
        }
    }

    private static async IAsyncEnumerable<ObjectEntry> Tracked(
        int id, List<int> released, string first, string second, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var key in new[] { first, second })
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ObjectEntry(ObjectKey.Parse(key), 1, key);
            }
        }
        finally
        {
            released.Add(id);
        }
    }
}
