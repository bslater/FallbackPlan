using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Restore;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A restore's plan names each folder already in its way that a flag keeps,
/// once, with how many of the run's items it fails (FR-RST-003; ADR-0180
/// Amendment 8). An immutable folder takes no new name, so everything the run
/// would make in it fails: a file, a link, or the first folder of a path it
/// would make below. An append-only folder takes a new name but lets none be
/// renamed or removed, so a file, which lands by renaming its spool, fails,
/// and so does whatever the run would move aside or replace there; a link or a
/// folder made in it does not. No policy helps, since a copy kept beside lands
/// by a rename too. A folder that already stands makes nothing, a file a
/// resume landed is left, and a quarantined run makes only its own folder in
/// the output.
/// </summary>
/// <remarks>
/// What stands is read through a stand-in, so each case holds on every
/// platform. The platform's own reading is held by
/// <c>Domain.Tests/FileFlagReadTests</c>, and the run's failing of each item by
/// <c>Repository.Tests/RestoreRefusedWhereItStandsTests</c>.
/// </remarks>
[TestClass]
public sealed class RestoreFolderHeldTests : IDisposable
{
    private readonly string _output =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-folder-held", Guid.NewGuid().ToString("n"))).FullName;

    public void Dispose() => Directory.Delete(_output, recursive: true);

    [TestMethod]
    public void AnImmutableFolder_IsNamedOnce_CountingEverythingTheRunWouldMakeInIt_UnderEveryPolicy()
    {
        // locked and inner stand, so their items make nothing; sub does not,
        // so it, and b.txt below it, need a name made in locked first.
        var plan = Plan(
            ("locked", EntryKind.DirectoryPlaceholder),
            ("locked/inner", EntryKind.DirectoryPlaceholder),
            ("locked/a.txt", EntryKind.File),
            ("locked/l", EntryKind.Symlink),
            ("locked/sub", EntryKind.DirectoryPlaceholder),
            ("locked/sub/b.txt", EntryKind.File),
            ("free/c.txt", EntryKind.File));
        var locked = Folder("locked");
        Folder("locked/inner");
        Folder("free");
        var flags = new Standing { [locked] = FileFlagBits.Immutable | FileFlagBits.NoDump };

        foreach (var policy in Enum.GetValues<ExistingDestinationPolicy>())
        {
            var held = Assert.ContainsSingle(
                RestoreInTheWay.FoldersHeld([new RestoreSlice(plan, _output)], RestoreDestinationMode.InPlace, policy, flags),
                $"{policy}");
            Assert.AreEqual(locked, held.Folder, $"{policy}");
            Assert.AreEqual(FileFlagBits.Immutable, held.Flags, $"{policy}: only the flags that hold it");
            Assert.AreEqual(4, held.Items, $"{policy}");
        }
    }

    [TestMethod]
    public void AnAppendOnlyFolder_HoldsWhatLandsThereByARename_OrMovesAsideWhatStandsThere()
    {
        var plan = Plan(
            ("logs/new.txt", EntryKind.File),
            ("logs/old.txt", EntryKind.File),
            ("logs/link", EntryKind.Symlink),
            ("logs/kept", EntryKind.Symlink),
            ("logs/sub", EntryKind.DirectoryPlaceholder),
            ("logs/sub/x.txt", EntryKind.File));
        var logs = Folder("logs");
        Existing("logs/old.txt");
        Existing("logs/kept");
        var flags = new Standing { [logs] = FileFlagBits.AppendOnly };

        // A new file and one moved aside land by a rename; the link made new
        // and the folder made new do not, and x.txt lands in that new folder,
        // which carries no flag. The link moved aside is held.
        Assert.AreEqual(3, Held(plan, ExistingDestinationPolicy.Preserve, flags));
        Assert.AreEqual(3, Held(plan, ExistingDestinationPolicy.Replace, flags));

        // Kept beside, old.txt still lands by a rename, and kept is made new
        // beside the link that stands.
        Assert.AreEqual(2, Held(plan, ExistingDestinationPolicy.WriteBeside, flags));

        // Failing what is there fails old.txt and kept whatever the folder.
        Assert.AreEqual(1, Held(plan, ExistingDestinationPolicy.Fail, flags));
    }

    [TestMethod]
    public void AQuarantinedRun_IsHeldOnlyByAnOutputThatTakesNoNewFolder_AndItsResumeAsInPlace()
    {
        var plan = Plan(("docs/a.txt", EntryKind.File), ("docs/b.txt", EntryKind.File));

        var refused = Assert.ContainsSingle(RestoreInTheWay.FoldersHeld(
            [new RestoreSlice(plan, _output)],
            RestoreDestinationMode.Quarantine,
            ExistingDestinationPolicy.Preserve,
            new Standing { [_output] = FileFlagBits.Immutable }));
        Assert.AreEqual(_output, refused.Folder);
        Assert.AreEqual(2, refused.Items, "the run makes nothing at all");

        Assert.IsEmpty(RestoreInTheWay.FoldersHeld(
            [new RestoreSlice(plan, _output)],
            RestoreDestinationMode.Quarantine,
            ExistingDestinationPolicy.Preserve,
            new Standing { [_output] = FileFlagBits.AppendOnly }),
            "an append-only output takes the run's new folder");

        // A resume lands where its run did, and leaves what that run landed.
        var resumed = Path.Combine(_output, ".fbp-quarantine", "run");
        var docs = Folder(".fbp-quarantine/run/docs");
        Existing(".fbp-quarantine/run/docs/a.txt", length: 5);
        var held = Assert.ContainsSingle(RestoreInTheWay.FoldersHeld(
            [new RestoreSlice(plan, _output, ResumedIn: resumed)],
            RestoreDestinationMode.Quarantine,
            ExistingDestinationPolicy.Preserve,
            new Standing { [docs] = FileFlagBits.Immutable }));
        Assert.AreEqual(docs, held.Folder);
        Assert.AreEqual(1, held.Items, "a.txt was landed at its length");
    }

    [TestMethod]
    public void NoFlagNoLine_AndEachLineSaysWhatTheFlagRefuses()
    {
        var plan = Plan(("plain/a.txt", EntryKind.File));
        Folder("plain");
        Assert.IsEmpty(RestoreInTheWay.FoldersHeld(
            [new RestoreSlice(plan, _output)], RestoreDestinationMode.InPlace, ExistingDestinationPolicy.Preserve,
            new Standing { [Path.Combine(_output, "plain")] = FileFlagBits.NoDump | FileFlagBits.Hidden }));

        var immutable = RestoreInTheWay.Describe(new FolderHeld("/x/locked", FileFlagBits.Immutable, 4));
        Assert.Contains("/x/locked", immutable, StringComparison.Ordinal);
        Assert.Contains("immutable", immutable, StringComparison.Ordinal);
        Assert.Contains("4 item(s)", immutable, StringComparison.Ordinal);
        Assert.Contains("add nothing to it", immutable, StringComparison.Ordinal);
        Assert.Contains("Clear the flag", immutable, StringComparison.Ordinal);

        var appended = RestoreInTheWay.Describe(new FolderHeld("/x/logs", FileFlagBits.AppendOnly, 2));
        Assert.Contains("append-only", appended, StringComparison.Ordinal);
        Assert.Contains("rename", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("add nothing", appended, StringComparison.Ordinal);
        Assert.Contains("2 item(s)", appended, StringComparison.Ordinal);
    }

    private int Held(RestorePlan plan, ExistingDestinationPolicy policy, Standing flags) =>
        RestoreInTheWay.FoldersHeld([new RestoreSlice(plan, _output)], RestoreDestinationMode.InPlace, policy, flags)
            is [var held] ? held.Items : 0;

    private string Folder(string path) =>
        Directory.CreateDirectory(Path.Combine(_output, path.Replace('/', Path.DirectorySeparatorChar))).FullName;

    private void Existing(string path, int length = 1)
    {
        var at = Path.Combine(_output, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(at)!);
        File.WriteAllBytes(at, new byte[length]);
    }

    private static RestorePlan Plan(params (string Path, EntryKind Kind)[] entries) => new()
    {
        SnapshotId = new byte[16],
        Items =
        [
            .. entries.Select((entry, index) => new RestorePlanItem(
                entry.Path, entry.Kind, ObjectId.FromBytes(Enumerable.Repeat((byte)(index + 1), 32).ToArray()),
                entry.Kind == EntryKind.File ? 5ul : 0ul)),
        ],
        Conflicts = [],
        Degradations = [],
    };

    /// <summary>What stands at each path, as a platform would read it; nothing elsewhere.</summary>
    private sealed class Standing : Dictionary<string, FileFlagBits>, IFileFlags
    {
        public FileFlagBits Apply(string path, FileFlagBits flags, EntryKind kind) => FileFlagBits.None;

        public FileFlagBits Read(string path) => TryGetValue(path, out var flags) ? flags : FileFlagBits.None;
    }
}
