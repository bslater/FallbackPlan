using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A large file is restored and verified through its reference runs
/// (ADR-0202 decision 4; FR-ARCH-010): the restore engine reads every run in
/// its order and each segment the runs name, and the file it puts down is the
/// one the whole-file hash names, holes and all (FR-RST-002). A run that will
/// not read, or does not follow the one before it, refuses the restore and
/// puts nothing down (FR-RST-005), and a part kept from a stopped restore is
/// taken up across runs, so far as it holds the file, without reading again
/// what it holds. Verification reads the runs as a restore does.
/// </summary>
/// <remarks>
/// The snapshot is <see cref="ReferenceRunHarness"/>'s, built by hand: no
/// writer in this build writes runs.
/// </remarks>
[TestClass]
public sealed class ReferenceRunRestoreTests : ReferenceRunHarness
{
    private static string StorePath(ArchivedBlob blob, string root) =>
        Path.Combine(root, blob.StoreKey.Value.Replace('/', Path.DirectorySeparatorChar));

    private async Task<(Built Built, RepositoryReader Reader)> OpenAsync(
        LocalFileSystemObjectStore store,
        RepositoryKeySet keys,
        bool writeSecondRun = true,
        bool gapBeforeSecondRun = false,
        params Func<Built, ArchivedBlob>[] lose)
    {
        var built = await BuildAsync(store, keys, writeSecondRun, gapBeforeSecondRun);
        foreach (var blob in lose)
        {
            File.Delete(StorePath(blob(built), StoreRoot));
        }

        var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        return (built, reader);
    }

    /// <summary>Where the file's second run begins: after its first segment and the holes.</summary>
    private static long SecondRunStart(Built built) => built.Content.Length - 30_000;

    [TestMethod]
    public async Task AFileOfTwoRuns_RestoresByteForByte_AndItsWholeFileHashVerifies()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var (built, reader) = await OpenAsync(store, keys);
        using var _ = reader;
        using var restored = new MemoryStream();

        var result = await new RestoreEngine(reader, SpoolDirectory).RestoreFileAsync(built.Manifest, restored, CancellationToken.None);

        Assert.IsTrue(result.Success, result.FailureDetail);
        Assert.AreEqual(built.Content.LongLength, result.Length);
        SequenceAssert.AreEqual(built.Content, restored.ToArray());
        SequenceAssert.AreEqual(built.Manifest.WholeFileHash.ToArray(), result.WholeFileHash!.ToArray());
    }

    [TestMethod]
    public async Task AFileOfTwoRuns_EmittedIntoANewFile_IsTheFile_ItsHolesLeftAsHoles()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var (built, reader) = await OpenAsync(store, keys);
        using var _ = reader;
        var landed = Path.Combine(SpoolDirectory, "landed.bin");

        RestoreResult result;
        using (var destination = new FileStream(landed, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            result = await new RestoreEngine(reader, SpoolDirectory).RestoreFileAsync(built.Manifest, destination, CancellationToken.None);
        }

        Assert.IsTrue(result.Success, result.FailureDetail);
        SequenceAssert.AreEqual(built.Content, File.ReadAllBytes(landed));

        // The holes are the first run's, so the spool may hold them only if
        // it was made able to before any run was read; a dense copy would
        // allocate the whole file. Its last hole is two thirds of its length
        // and one every filesystem keeps.
        var allocated = AllocatedSize.Of(landed);
        Assert.IsLessThan(
            built.Content.LongLength / 2, allocated,
            $"{allocated} bytes allocated for a {built.Content.LongLength}-byte file holding 70,000 bytes of data");
    }

    [TestMethod]
    public async Task AFileOfTwoRuns_AssembledWhereItLands_IsTheFile()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var (built, reader) = await OpenAsync(store, keys);
        using var _ = reader;
        var landed = Path.Combine(SpoolDirectory, "assembled.bin");

        var result = await new RestoreEngine(reader, SpoolDirectory).AssembleFileAsync(built.Manifest, landed, CancellationToken.None);

        Assert.IsTrue(result.Success, result.FailureDetail);
        SequenceAssert.AreEqual(built.Content, File.ReadAllBytes(landed));
    }

    [TestMethod]
    public async Task AFileWhoseRunWillNotRead_IsRefused_NamingTheRun_AndNothingIsPutDown()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var (built, reader) = await OpenAsync(store, keys, writeSecondRun: false);
        using var _ = reader;
        using var restored = new MemoryStream();

        var result = await new RestoreEngine(reader, SpoolDirectory).RestoreFileAsync(built.Manifest, restored, CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.Contains(built.SecondRun.ToString(), result.FailureDetail!, StringComparison.Ordinal);
        Assert.AreEqual(0, restored.Length, "a file whose runs did not all read is never a shorter file");
    }

    [TestMethod]
    public async Task AFileWhoseRunDoesNotFollowTheOneBefore_IsRefused()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var (built, reader) = await OpenAsync(store, keys, gapBeforeSecondRun: true);
        using var _ = reader;
        var landed = Path.Combine(SpoolDirectory, "gapped.bin");

        var result = await new RestoreEngine(reader, SpoolDirectory).AssembleFileAsync(built.Manifest, landed, CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.Contains("run 1", result.FailureDetail!, StringComparison.Ordinal);
        Assert.IsFalse(File.Exists(landed), "what was assembled of a refused file is removed");
    }

    [TestMethod]
    public async Task ASegmentOnlyTheSecondRunNames_ThatWillNotRead_RefusesTheRestore()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var (built, reader) = await OpenAsync(store, keys, lose: b => b.SecondData);
        using var _ = reader;
        using var restored = new MemoryStream();

        var result = await new RestoreEngine(reader, SpoolDirectory).RestoreFileAsync(built.Manifest, restored, CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.Contains($"offset {SecondRunStart(built)}", result.FailureDetail!, StringComparison.Ordinal);
        Assert.AreEqual(0, restored.Length);
    }

    [TestMethod]
    public async Task AKeptPartHoldingTheFirstRun_IsTakenUp_AndOnlyTheSecondRunsSegmentIsRead()
    {
        var store = CreateStore();
        using var keys = CreateKeys();

        // The first run's segment is gone from the store: the restore can
        // succeed only by taking it up from the part.
        var (built, reader) = await OpenAsync(store, keys, lose: b => b.FirstData);
        using var _ = reader;
        var part = Path.Combine(SpoolDirectory, "huge.part");
        File.WriteAllBytes(part, built.Content.AsSpan(0, (int)SecondRunStart(built) + 10_000).ToArray());
        using var restored = new MemoryStream();

        var result = await new RestoreEngine(reader, SpoolDirectory).RestoreFileAsync(
            built.Manifest, restored, part, takeUp: true, CancellationToken.None);

        Assert.IsTrue(result.Success, result.FailureDetail);
        Assert.AreEqual(SecondRunStart(built), result.Resumed, "the part holds the first run whole and the second's segment only in part");
        SequenceAssert.AreEqual(built.Content, restored.ToArray());
    }

    [TestMethod]
    public async Task AKeptPartIsCutBackWhereTakingUpStops_SoTheHolesAfterItAreZeroes()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var (built, reader) = await OpenAsync(store, keys);
        using var _ = reader;
        var part = Path.Combine(SpoolDirectory, "stray.part");

        // The part holds the first segment and ten of the holes, and then
        // bytes that are not the file's, where its holes go: a hole is
        // skipped as the file is assembled, never written.
        File.WriteAllBytes(part, [.. built.Content.AsSpan(0, 40_010), .. Enumerable.Repeat((byte)0xEE, 100_000)]);
        using var restored = new MemoryStream();

        var result = await new RestoreEngine(reader, SpoolDirectory).RestoreFileAsync(
            built.Manifest, restored, part, takeUp: true, CancellationToken.None);

        Assert.IsTrue(result.Success, result.FailureDetail);
        Assert.AreEqual(40_010L, result.Resumed);
        SequenceAssert.AreEqual(built.Content, restored.ToArray());
    }

    [TestMethod]
    public async Task AKeptPartHoldingTheWholeFile_IsTakenUpWhole_AndNoSegmentIsRead()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var (built, reader) = await OpenAsync(store, keys, lose: [b => b.FirstData, b => b.SecondData]);
        using var _ = reader;
        var part = Path.Combine(SpoolDirectory, "whole.part");
        File.WriteAllBytes(part, built.Content);
        using var restored = new MemoryStream();

        var result = await new RestoreEngine(reader, SpoolDirectory).RestoreFileAsync(
            built.Manifest, restored, part, takeUp: true, CancellationToken.None);

        Assert.IsTrue(result.Success, result.FailureDetail);
        Assert.AreEqual(built.Content.LongLength, result.Resumed);
        SequenceAssert.AreEqual(built.Content, restored.ToArray());
    }

    [TestMethod]
    public async Task AFileOfTwoRuns_Verifies_EverySegmentAndTheWholeFileHash()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var (built, reader) = await OpenAsync(store, keys);
        using var _ = reader;
        using var engine = new VerifyEngine(Repo, keys, store);

        var result = await engine.VerifyFileAsync(built.Manifest, reader, CancellationToken.None);

        Assert.IsTrue(result.Ok, result.Detail);
    }

    [TestMethod]
    public async Task AFileWhoseRunWillNotRead_FailsVerification_NamingTheRun()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var (built, reader) = await OpenAsync(store, keys, writeSecondRun: false);
        using var _ = reader;
        using var engine = new VerifyEngine(Repo, keys, store);

        var result = await engine.VerifyFileAsync(built.Manifest, reader, CancellationToken.None);

        Assert.IsFalse(result.Ok);
        Assert.Contains(built.SecondRun.ToString(), result.Detail!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ASegmentOnlyTheSecondRunNames_ThatWillNotRead_FailsVerification()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var (built, reader) = await OpenAsync(store, keys, lose: b => b.SecondData);
        using var _ = reader;
        using var engine = new VerifyEngine(Repo, keys, store);

        var result = await engine.VerifyFileAsync(built.Manifest, reader, CancellationToken.None);

        Assert.IsFalse(result.Ok);
        Assert.Contains($"offset {SecondRunStart(built)}", result.Detail!, StringComparison.Ordinal);
    }
}
