using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A file whose name is as long as a target holds restores through the
/// executor (FR-RST-005, ADR-0173). Its spool is written beside it under a
/// short name of its own, so the file is not failed for a name longer than
/// its own. The common Linux filesystems hold 255 bytes in one name and NTFS
/// 255 UTF-16 units, so a name of 255 ASCII characters is the longest both
/// hold. On Linux, where the plan states that limit, a name longer than it
/// is named in the plan, and fails alone while the rest restores (FR-RST-003,
/// ADR-0170 Amendment 1). Under rename, the copy written beside a file in
/// the way keeps as much of the file's stem as fits within the limit the
/// target states, and a resume finds it there again (FR-RST-005, FR-RST-008;
/// ADR-0173 Amendment 2).
/// </summary>
[TestClass]
public sealed class RestoreLongNameTests : ArchiveTestHarness
{
    [TestMethod]
    [DataRow("n", 251, ".bin", DisplayName = "255 ASCII characters")]
    [DataRow("語", 84, ".bi", DisplayName = "255 UTF-8 bytes, most of them in three-byte characters")]
    public async Task AFileWhoseNameIsAsLongAsATargetHolds_IsRestoredByteForByte(string unit, int count, string extension)
    {
        var name = string.Concat(Enumerable.Repeat(unit, count)) + extension;
        Assert.AreEqual(255, System.Text.Encoding.UTF8.GetByteCount(name));
        var content = Deterministic(4_096, 7);
        var source = new FakeFileSystemSource();
        source.AddFile("data/" + name, content, fileId: 9_101);
        source.AddFile("data/short.bin", Deterministic(512, 8), fileId: 9_102);
        var (store, keys, catalogue, target) = await PublishAsync(source, 0xD1, "long");
        using var _keys = keys;
        using var _catalogue = catalogue;

        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat((byte)0xD1, 16).ToArray(), string.Empty, target);
        Assert.IsEmpty(plan.Conflicts, string.Join("; ", plan.Conflicts.Select(conflict => conflict.Reason)));

        var output = Path.Combine(SpoolDirectory, "long-out");
        var receipt = await ExecuteAsync(store, keys, target, plan, output, "long-run");

        Assert.AreEqual(
            RestoreOutcome.Complete, receipt.Outcome,
            string.Join("; ", receipt.Items.Where(item => item.Outcome != "restored").Select(item => item.Detail)));
        SequenceAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(output, "data", name)));
        Assert.HasCount(2, Directory.GetFiles(Path.Combine(output, "data")), "no spool is left beside them");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux's filesystems hold 255 bytes in one name")]
    public async Task AFileWhoseNameIsLongerThanTheTargetHolds_IsNamedInThePlan_AndFailsAlone()
    {
        // 86 three-byte characters: 86 UTF-16 units, which Windows holds, and
        // 258 bytes, which Linux does not (ADR-0170 Amendment 1). The plan
        // says so before any byte moves, and the run fails that file alone.
        var name = new string('語', 86);
        var source = new FakeFileSystemSource();
        source.AddFile("data/" + name, Deterministic(4_096, 7), fileId: 9_201);
        var kept = Deterministic(512, 8);
        source.AddFile("data/short.bin", kept, fileId: 9_202);
        var (store, keys, catalogue, target) = await PublishAsync(source, 0xD2, "too-long");
        using var _keys = keys;
        using var _catalogue = catalogue;

        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat((byte)0xD2, 16).ToArray(), string.Empty, target);
        Assert.AreEqual("data/" + name, Assert.ContainsSingle(plan.Conflicts).Path);

        var output = Path.Combine(SpoolDirectory, "too-long-out");
        var receipt = await ExecuteAsync(store, keys, target, plan, output, "too-long-run");

        Assert.AreEqual("failed", Assert.ContainsSingle(receipt.Items.Where(item => item.Path == "data/" + name)).Outcome);
        Assert.AreEqual("restored", Assert.ContainsSingle(receipt.Items.Where(item => item.Path == "data/short.bin")).Outcome);
        Assert.AreNotEqual(RestoreOutcome.Complete, receipt.Outcome);
        SequenceAssert.AreEqual(kept, File.ReadAllBytes(Path.Combine(output, "data", "short.bin")));
        Assert.ContainsSingle(Directory.GetFiles(Path.Combine(output, "data")), "no spool is left beside them");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux | TestPlatforms.Windows, "Linux and Windows targets state their limit on one name; a macOS target states none")]
    [DataRow(251, 229, DisplayName = "255 characters: the stem is cut to fit")]
    [DataRow(230, 229, DisplayName = "one character over: the stem is cut by one")]
    [DataRow(229, 229, DisplayName = "a copy exactly as long as the limit: nothing is cut")]
    [DataRow(12, 12, DisplayName = "a short name: nothing is cut")]
    public async Task UnderRename_ACopyBesideALongName_KeepsAsMuchOfTheStemAsFits(int stem, int kept)
    {
        // " (restored 2024-08-03)" is 22 characters, so beside a name of 255
        // the stem keeps 255 - 22 - 4 for ".bin".
        var name = new string('n', stem) + ".bin";
        var content = Deterministic(4_096, 9);
        var source = new FakeFileSystemSource();
        source.AddFile("data/" + name, content, fileId: 9_301);
        var (store, keys, catalogue, target) = await PublishAsync(source, 0xD3, $"beside-{stem}");
        using var _keys = keys;
        using var _catalogue = catalogue;
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat((byte)0xD3, 16).ToArray(), string.Empty, target);
        var output = Path.Combine(SpoolDirectory, $"beside-{stem}-out");
        Directory.CreateDirectory(Path.Combine(output, "data"));
        File.WriteAllText(Path.Combine(output, "data", name), "the person's own file");

        var receipt = await ExecuteAsync(
            store, keys, target, plan, output, $"beside-{stem}-run", ExistingDestinationPolicy.WriteBeside);

        var beside = new string('n', kept) + " (restored 2024-08-03).bin";
        var item = Assert.ContainsSingle(receipt.Items.Where(each => each.Path == "data/" + name));
        Assert.AreEqual("restored", item.Outcome, item.Detail);
        Assert.AreEqual("data/" + beside, item.WrittenAs);
        SequenceAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(output, "data", beside)));
        Assert.AreEqual("the person's own file", File.ReadAllText(Path.Combine(output, "data", name)));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux's filesystems hold 255 bytes in one name")]
    [DataRow("語", 76, DisplayName = "three-byte characters: no character is split")]
    [DataRow("e\u0301", 76, DisplayName = "a letter and its combining accent: the two are kept together")]
    public async Task UnderRename_ACopyBesideANameOf255Bytes_IsCutWhereAWholeCharacterEnds(string unit, int kept)
    {
        // 84 units of three bytes and ".bi" are 255 bytes. Beside them, the
        // stem keeps at most 255 - 22 - 3 = 230 bytes: 76 whole units, 228.
        var stem = string.Concat(Enumerable.Repeat(unit, 84));
        var name = stem + ".bi";
        Assert.AreEqual(255, System.Text.Encoding.UTF8.GetByteCount(name));
        var content = Deterministic(4_096, 10);
        var source = new FakeFileSystemSource();
        source.AddFile("data/" + name, content, fileId: 9_302);
        var (store, keys, catalogue, target) = await PublishAsync(source, 0xD4, "beside-bytes");
        using var _keys = keys;
        using var _catalogue = catalogue;
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat((byte)0xD4, 16).ToArray(), string.Empty, target);
        var output = Path.Combine(SpoolDirectory, "beside-bytes-out");
        Directory.CreateDirectory(Path.Combine(output, "data"));
        File.WriteAllText(Path.Combine(output, "data", name), "the person's own file");

        var receipt = await ExecuteAsync(
            store, keys, target, plan, output, "beside-bytes-run", ExistingDestinationPolicy.WriteBeside);

        var beside = string.Concat(Enumerable.Repeat(unit, kept)) + " (restored 2024-08-03).bi";
        var item = Assert.ContainsSingle(receipt.Items.Where(each => each.Path == "data/" + name));
        Assert.AreEqual("restored", item.Outcome, item.Detail);
        Assert.AreEqual("data/" + beside, item.WrittenAs);
        SequenceAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(output, "data", beside)));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux | TestPlatforms.Windows, "Linux and Windows targets state their limit on one name; a macOS target states none")]
    public async Task Resume_UnderRename_ACopyAnEarlierRunWroteBesideALongName_IsFoundThere()
    {
        // The copy an earlier run wrote, on another day, under the stem cut
        // as it cuts one: a resume looks for it by the same cut, finds it,
        // and writes no second copy.
        var name = new string('n', 251) + ".bin";
        var content = Deterministic(4_096, 11);
        var source = new FakeFileSystemSource();
        source.AddFile("data/" + name, content, fileId: 9_303);
        var (store, keys, catalogue, target) = await PublishAsync(source, 0xD5, "beside-resume");
        using var _keys = keys;
        using var _catalogue = catalogue;
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat((byte)0xD5, 16).ToArray(), string.Empty, target);
        var output = Path.Combine(SpoolDirectory, "beside-resume-out");
        Directory.CreateDirectory(Path.Combine(output, "data"));
        File.WriteAllText(Path.Combine(output, "data", name), "the person's own file");
        var earlier = new string('n', 229) + " (restored 2024-08-02).bin";
        File.WriteAllBytes(Path.Combine(output, "data", earlier), content);

        var receipt = await ExecuteAsync(
            store, keys, target, plan, output, "beside-resume-run", ExistingDestinationPolicy.WriteBeside, resume: true);

        var item = Assert.ContainsSingle(receipt.Items.Where(each => each.Path == "data/" + name));
        Assert.IsTrue(item.FoundInPlace, item.Detail);
        Assert.AreEqual("data/" + earlier, item.WrittenAs);
        Assert.HasCount(2, Directory.GetFiles(Path.Combine(output, "data")), "no second copy is written beside");
    }

    /// <summary>
    /// Backs <paramref name="source"/> up as the snapshot <paramref name="seed"/>
    /// names, and answers what restores it on the platform the test runs on.
    /// </summary>
    private async Task<(LocalFileSystemObjectStore Store, RepositoryKeySet Keys, CatalogueDb Catalogue, RestoreTargetProfile Target)>
        PublishAsync(FakeFileSystemSource source, byte seed, string name)
    {
        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"catalogue-{name}.db"), Repo);
        var spool = Directory.CreateDirectory(Path.Combine(SpoolDirectory, name)).FullName;
        await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
                spool,
                FormatVersions.SealedDataPlane, observer: null, catalogue)
            .PublishAsync(
                new SnapshotJob
                {
                    Source = source,
                    Roots = [new ScanRoot("/")],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = Enumerable.Repeat(seed, 16).ToArray(),
                    NowUnixMilliseconds = 1_722_600_000_000,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "restore-long-name-tests/1.0",
                },
                CancellationToken.None);
        return (store, keys, catalogue, RestoreTargetProfile.ForLocalPlatform());
    }

    private static async Task<RestoreReceipt> ExecuteAsync(
        LocalFileSystemObjectStore store,
        RepositoryKeySet keys,
        RestoreTargetProfile target,
        RestorePlan plan,
        string output,
        string runId,
        ExistingDestinationPolicy existing = ExistingDestinationPolicy.Preserve,
        bool resume = false)
    {
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        return await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                ExistingDestination = existing,
                Resume = resume,
                RunId = runId,
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);
    }

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }
}
