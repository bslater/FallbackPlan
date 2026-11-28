using FallbackPlan.Repository.Catalogue.Forensic;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// What a restore plans for a large file is read through its reference runs,
/// as its restore is (ADR-0202 decision 4; FR-ARCH-010): the blobs a plan
/// needs are its runs' and the segments' they name, and a file a run or one of
/// its segments has gone from is a path the plan cannot reach (FR-RST-003);
/// what the file writes and what a kept part already holds of it are counted
/// across its runs (FR-RST-008); and a run restores it, fresh or resumed
/// where its part stops holding it (FR-RST-002).
/// </summary>
/// <remarks>
/// The snapshot is <see cref="ReferenceRunHarness"/>'s, built by hand, and its
/// catalogue is rebuilt forensically from its blobs: it has no index.
/// </remarks>
[TestClass]
public sealed class ReferenceRunPlanTests : ReferenceRunHarness
{
    private const string Path0 = "huge.bin";

    /// <summary>The data a file of two runs holds: its two segments, 40,000 and 30,000 bytes.</summary>
    private const ulong DataBytes = 70_000;

    private sealed record Planned(Built Built, CatalogueDb Catalogue, RestorePlan Plan, RestoreTargetProfile Target) : IDisposable
    {
        public void Dispose() => Catalogue.Dispose();
    }

    private async Task<Planned> PlanAsync(
        LocalFileSystemObjectStore store,
        RepositoryKeySet keys,
        bool writeSecondRun = true,
        params Func<Built, ArchivedBlob>[] lose)
    {
        var built = await BuildAsync(store, keys, writeSecondRun);
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"plan-{Guid.NewGuid():n}.db"), Repo);
        using (var credential = CreateCredential())
        using (var rebuilder = new ForensicRebuilder(store, Repo, credential))
        {
            await rebuilder.RebuildAsync(catalogue, new ForensicTarget.Everything(), CancellationToken.None);
        }

        foreach (var blob in lose)
        {
            File.Delete(Path.Combine(StoreRoot, blob(built).StoreKey.Value.Replace('/', Path.DirectorySeparatorChar)));
        }

        var target = RestoreTargetProfile.ForLocalPlatform() with { SupportsAlternateStreams = false, SupportsExtendedAttributes = false };
        var plan = RestorePlanner.Plan(catalogue, SnapshotId, string.Empty, target);
        Assert.AreEqual(Path0, Assert.ContainsSingle(plan.Items).Path);
        return new Planned(built, catalogue, plan, target);
    }

    private static RepositoryReader Reader(Planned planned, IObjectStore store, RepositoryKeySet keys)
    {
        var reader = new RepositoryReader(Repo, keys, store, Authority);
        reader.UseLocationSource(planned.Catalogue.ResolveLocation);
        return reader;
    }

    private static RestoreExecutionOptions Options(string runId, string? parts = null, bool resume = false) => new()
    {
        DestinationMode = RestoreDestinationMode.InPlace,
        RunId = runId,
        NowUnixMilliseconds = 1_722_700_000_000,
        PartsDirectory = parts,
        Resume = resume,
    };

    /// <summary>Where the file's second run begins: after its first segment and the holes.</summary>
    private static int SecondRunStart(Built built) => built.Content.Length - 30_000;

    private static string PartIn(string parts, Built built)
    {
        Directory.CreateDirectory(parts);
        return Path.Combine(parts, RestorePart.NameOf(built.Manifest));
    }

    [TestMethod]
    public async Task APlan_NeedsTheBlobsOfAFilesRuns_AndOfTheSegmentsTheyName()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var planned = await PlanAsync(store, keys);

        var result = await RestoreBlobSet.ResolveAsync(planned.Catalogue, planned.Plan, store, Repo, keys, CancellationToken.None);

        Assert.IsEmpty(result.Missing);
        foreach (var blob in new[] { planned.Built.FirstData, planned.Built.SecondData, planned.Built.Runs })
        {
            Assert.Contains(blob.StoreKey, result.Blobs.ToList(), $"{blob.StoreKey} is needed to restore the file");
        }

        Assert.AreEqual(DataBytes, result.Facts[planned.Built.Version].WrittenBytes, "the file writes its segments, its holes skipped");
    }

    [TestMethod]
    public async Task APlan_CannotReachAFile_ASegmentOnlyARunNamesHasGoneFrom()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var planned = await PlanAsync(store, keys, lose: b => b.SecondData);

        var result = await RestoreBlobSet.ResolveAsync(planned.Catalogue, planned.Plan, store, Repo, keys, CancellationToken.None);

        Assert.AreEqual(Path0, Assert.ContainsSingle(result.Missing));
    }

    [TestMethod]
    public async Task APlan_CannotReachAFile_WhoseRunHasGone()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var planned = await PlanAsync(store, keys, writeSecondRun: false);

        var result = await RestoreBlobSet.ResolveAsync(planned.Catalogue, planned.Plan, store, Repo, keys, CancellationToken.None);

        Assert.AreEqual(Path0, Assert.ContainsSingle(result.Missing));
    }

    [TestMethod]
    public async Task APlan_NamesAFileArchived_WhoseRunsAreHeldOnlyInAnArchivalTier()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var planned = await PlanAsync(store, keys);
        var runs = planned.Built.Runs.StoreKey.Value;

        // A rehydration brings the runs back, so the file is archived, not
        // missing, though no segment of it can be named until it does.
        var result = await RestoreBlobSet.ResolveAsync(
            planned.Catalogue, planned.Plan, new ArchivingObjectStore(store, key => key == runs), Repo, keys, CancellationToken.None);

        Assert.AreEqual(Path0, Assert.ContainsSingle(result.Archived));
        Assert.IsEmpty(result.Missing);
    }

    [TestMethod]
    public async Task WhatAFileWrites_AndWhatItsPartHolds_AreCountedAcrossItsRuns()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var planned = await PlanAsync(store, keys);
        using var reader = Reader(planned, store, keys);
        var parts = Path.Combine(SpoolDirectory, "parts");
        File.WriteAllBytes(PartIn(parts, planned.Built), planned.Built.Content.AsSpan(0, SecondRunStart(planned.Built) + 10_000).ToArray());

        var fresh = await RestoreSpace.WrittenBytesThrough(reader)(Assert.ContainsSingle(planned.Plan.Items), CancellationToken.None);
        var resumed = await RestoreSpace.WrittenBytesThrough(reader, parts)(Assert.ContainsSingle(planned.Plan.Items), CancellationToken.None);
        var probed = await RestoreBlobSet.ResolveAsync(
            planned.Catalogue, planned.Plan, store, [], Repo, keys, new RestoreHeld { PartsDirectory = parts }, CancellationToken.None);

        Assert.AreEqual(new RestoreFileBytes(DataBytes), fresh);
        Assert.AreEqual(new RestoreFileBytes(DataBytes, 40_000), resumed, "the part holds the first run's segment, and the second's only in part");
        Assert.AreEqual(40_000UL, probed.Facts[planned.Built.Version].HeldBytes);
        Assert.DoesNotContain(planned.Built.FirstData.StoreKey, probed.Blobs.ToList(), "what the part holds is not read again");
        Assert.Contains(planned.Built.SecondData.StoreKey, probed.Blobs.ToList());
    }

    [TestMethod]
    public async Task APartThatDoesNotHoldTheFilesStart_HoldsNoneOfIt_WhateverItHoldsAfter()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var planned = await PlanAsync(store, keys);
        using var reader = Reader(planned, store, keys);
        var parts = Path.Combine(SpoolDirectory, "stray-parts");

        // Every byte the file's is, but its first: a resume takes a part up
        // only from the file's start, so the second run's segment there is
        // nothing it holds.
        var held = planned.Built.Content.ToArray();
        held[0] ^= 0xFF;
        File.WriteAllBytes(PartIn(parts, planned.Built), held);

        var resumed = await RestoreSpace.WrittenBytesThrough(reader, parts)(Assert.ContainsSingle(planned.Plan.Items), CancellationToken.None);

        Assert.AreEqual(new RestoreFileBytes(DataBytes), resumed);
    }

    [TestMethod]
    public async Task AFileTheRunLanded_HoldsAllItWrites()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var planned = await PlanAsync(store, keys);

        var probed = await RestoreBlobSet.ResolveAsync(
            planned.Catalogue, planned.Plan, store, [], Repo, keys,
            new RestoreHeld { Landed = new HashSet<string>(StringComparer.Ordinal) { Path0 } }, CancellationToken.None);

        Assert.AreEqual(DataBytes, probed.Facts[planned.Built.Version].HeldBytes);
        Assert.DoesNotContain(planned.Built.SecondData.StoreKey, probed.Blobs.ToList(), "a landed file's segments are not read again");
    }

    [TestMethod]
    public async Task WhatAFileWrites_IsUnknown_WhereARunWillNotRead()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var planned = await PlanAsync(store, keys, writeSecondRun: false);
        using var reader = Reader(planned, store, keys);

        var measured = await RestoreSpace.WrittenBytesThrough(reader)(Assert.ContainsSingle(planned.Plan.Items), CancellationToken.None);

        Assert.IsNull(measured, "a file measured short would make a plan say it fits where it may not");
    }

    [TestMethod]
    public async Task ARestoreRun_PutsDownAFileOfTwoRuns_ByteForByte()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var planned = await PlanAsync(store, keys);
        using var reader = Reader(planned, store, keys);
        var root = Path.Combine(SpoolDirectory, "restored");

        var receipt = await new RestoreExecutor(reader, planned.Target).ExecuteAsync(planned.Plan, root, Options("fresh"), CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome, string.Join("; ", receipt.Items.Select(item => item.Detail)));
        SequenceAssert.AreEqual(planned.Built.Content, File.ReadAllBytes(Path.Combine(root, Path0)));
    }

    [TestMethod]
    public async Task ARestoreRunResumed_TakesUpItsPartAcrossRuns_AndSaysWhereItResumed()
    {
        var store = CreateStore();
        using var keys = CreateKeys();

        // The first run's segment is gone from the store: the run can put the
        // file down only by taking it up from its part.
        using var planned = await PlanAsync(store, keys, lose: b => b.FirstData);
        using var reader = Reader(planned, store, keys);
        var parts = Path.Combine(SpoolDirectory, "resumed-parts");
        File.WriteAllBytes(PartIn(parts, planned.Built), planned.Built.Content.AsSpan(0, SecondRunStart(planned.Built) + 10_000).ToArray());
        var root = Path.Combine(SpoolDirectory, "resumed");

        var receipt = await new RestoreExecutor(reader, planned.Target)
            .ExecuteAsync(planned.Plan, root, Options("resumed", parts, resume: true), CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome, string.Join("; ", receipt.Items.Select(item => item.Detail)));
        SequenceAssert.AreEqual(planned.Built.Content, File.ReadAllBytes(Path.Combine(root, Path0)));
        Assert.AreEqual((ulong)SecondRunStart(planned.Built), Assert.ContainsSingle(receipt.Items).ResumedAt);
    }
}
