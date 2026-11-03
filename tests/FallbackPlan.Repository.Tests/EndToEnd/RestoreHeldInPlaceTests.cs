using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Restore;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A restore's plan names each file already in its way that a flag keeps where
/// it stands: immutable or append-only, which the platform will neither move
/// aside nor replace (FR-RST-003; ADR-0180 Amendment 6). It says so before
/// anything is written, under the existing-file policy that would meet it:
/// moving the file aside, the default, or replacing it. A run that keeps both
/// writes beside it and is not held, and one that fails such a file would fail
/// it anyway. A quarantined run lands in a folder of its own, where nothing is
/// in the way, and a resume leaves a file it already landed as it stands.
/// A link meets what stands in its place as a file does; a folder is made
/// where it is, which no policy reaches.
/// </summary>
/// <remarks>
/// What stands is read through a stand-in, so each case holds on every
/// platform. The platform's own reading is held by
/// <c>Domain.Tests/FileFlagReadTests</c>.
/// </remarks>
[TestClass]
public sealed class RestoreHeldInPlaceTests : IDisposable
{
    private readonly string _output =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-held-in-place", Guid.NewGuid().ToString("n"))).FullName;

    public void Dispose() => Directory.Delete(_output, recursive: true);

    [TestMethod]
    public void InPlace_MovingAside_NamesAFileItsFlagKeepsWhereItStands_AndNoOther()
    {
        var plan = Plan(("docs/kept.txt", 12), ("docs/loose.txt", 7), ("docs/new.txt", 3));
        var kept = Existing("docs/kept.txt");
        Existing("docs/loose.txt");
        var flags = new Standing { [kept] = FileFlagBits.Immutable | FileFlagBits.NoDump };

        var held = RestoreInTheWay.HeldByFlags(
            [new RestoreSlice(plan, _output)], RestoreDestinationMode.InPlace, ExistingDestinationPolicy.Preserve, flags);

        var only = Assert.ContainsSingle(held);
        Assert.AreEqual(kept, only.Destination);
        Assert.AreEqual(FileFlagBits.Immutable, only.Flags, "only the flags that hold it are named");

        var said = RestoreInTheWay.Describe(only, ExistingDestinationPolicy.Preserve);
        Assert.Contains(kept, said, StringComparison.Ordinal);
        Assert.Contains("immutable", said, StringComparison.Ordinal);
        Assert.Contains("move it aside", said, StringComparison.Ordinal);
        Assert.Contains("beside", said, StringComparison.Ordinal);
    }

    [TestMethod]
    public void InPlace_Replacing_NamesAnAppendOnlyFile_AsOneItCannotReplace()
    {
        var plan = Plan(("log.txt", 40));
        var log = Existing("log.txt");
        var flags = new Standing { [log] = FileFlagBits.AppendOnly };

        var held = Assert.ContainsSingle(RestoreInTheWay.HeldByFlags(
            [new RestoreSlice(plan, _output)], RestoreDestinationMode.InPlace, ExistingDestinationPolicy.Replace, flags));

        Assert.AreEqual(FileFlagBits.AppendOnly, held.Flags);
        var said = RestoreInTheWay.Describe(held, ExistingDestinationPolicy.Replace);
        Assert.Contains("append-only", said, StringComparison.Ordinal);
        Assert.Contains("replace it", said, StringComparison.Ordinal);
    }

    [TestMethod]
    public void InPlace_KeepingBoth_OrFailingWhatIsThere_HoldsNothing()
    {
        var plan = Plan(("docs/kept.txt", 12));
        var flags = new Standing { [Existing("docs/kept.txt")] = FileFlagBits.Immutable };

        foreach (var policy in new[] { ExistingDestinationPolicy.WriteBeside, ExistingDestinationPolicy.Fail })
        {
            Assert.IsEmpty(
                RestoreInTheWay.HeldByFlags([new RestoreSlice(plan, _output)], RestoreDestinationMode.InPlace, policy, flags),
                $"{policy}");
        }
    }

    [TestMethod]
    public void AQuarantinedRun_MeetsNothing_ButAResumeMeetsWhatItsRunDidNotLand()
    {
        // A quarantined run lands in a folder of its own. A resume of one lands
        // where its run did: a file there at another length is replaced, and
        // one at its length the resume leaves as it stands.
        var plan = Plan(("docs/kept.txt", 12), ("docs/landed.txt", 5));
        var resumed = Path.Combine(_output, ".fbp-quarantine", "run");
        var flags = new Standing
        {
            [Existing("docs/kept.txt", under: resumed, length: 3)] = FileFlagBits.Immutable,
            [Existing("docs/landed.txt", under: resumed, length: 5)] = FileFlagBits.Immutable,
        };

        Assert.IsEmpty(RestoreInTheWay.HeldByFlags(
            [new RestoreSlice(plan, _output)], RestoreDestinationMode.Quarantine, ExistingDestinationPolicy.Preserve, flags));

        var held = Assert.ContainsSingle(RestoreInTheWay.HeldByFlags(
            [new RestoreSlice(plan, _output, ResumedIn: resumed)],
            RestoreDestinationMode.Quarantine,
            ExistingDestinationPolicy.Preserve,
            flags));
        Assert.AreEqual(Path.Combine(resumed, "docs", "kept.txt"), held.Destination);
    }

    [TestMethod]
    public void ALink_ThatWouldLandWhereAFlagKeepsAFile_IsNamedToo()
    {
        // A link moves aside or replaces what stands in its place, as a file
        // does, so a file a flag keeps there fails it alike.
        var plan = Plan(("docs/link", 0, EntryKind.Symlink));
        var kept = Existing("docs/link");
        var flags = new Standing { [kept] = FileFlagBits.Immutable };

        var held = Assert.ContainsSingle(RestoreInTheWay.HeldByFlags(
            [new RestoreSlice(plan, _output)], RestoreDestinationMode.InPlace, ExistingDestinationPolicy.Preserve, flags));

        Assert.AreEqual(kept, held.Destination);
    }

    [TestMethod]
    public void AFolder_IsNotAsked_WhateverStandsWhereItWouldBeMade()
    {
        // A folder is made where it is, which no policy reaches (ADR-0086):
        // nothing is moved aside for it, flagged or not.
        var plan = Plan(("docs", 0, EntryKind.DirectoryPlaceholder), ("logs", 0, EntryKind.DirectoryPlaceholder));
        Directory.CreateDirectory(Path.Combine(_output, "docs"));
        var aFileWhereAFolderGoes = Existing("logs");
        var flags = new Standing
        {
            [Path.Combine(_output, "docs")] = FileFlagBits.Immutable,
            [aFileWhereAFolderGoes] = FileFlagBits.Immutable,
        };

        Assert.IsEmpty(RestoreInTheWay.HeldByFlags(
            [new RestoreSlice(plan, _output)], RestoreDestinationMode.InPlace, ExistingDestinationPolicy.Preserve, flags));
        Assert.IsEmpty(flags.Asked, "nothing a run does not move aside is read");
    }

    private string Existing(string path, string? under = null, int length = 1)
    {
        var at = Path.Combine(under ?? _output, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(at)!);
        File.WriteAllBytes(at, new byte[length]);
        return at;
    }

    private static RestorePlan Plan(params (string Path, ulong Length)[] files) =>
        Plan([.. files.Select(file => (file.Path, file.Length, EntryKind.File))]);

    private static RestorePlan Plan(params (string Path, ulong Length, EntryKind Kind)[] entries) => new()
    {
        SnapshotId = new byte[16],
        Items =
        [
            .. entries.Select((entry, index) => new RestorePlanItem(
                entry.Path, entry.Kind, ObjectId.FromBytes(Enumerable.Repeat((byte)(index + 1), 32).ToArray()), entry.Length)),
        ],
        Conflicts = [],
        Degradations = [],
    };

    /// <summary>What stands at each path, as a platform would read it; nothing elsewhere.</summary>
    private sealed class Standing : Dictionary<string, FileFlagBits>, IFileFlags
    {
        public List<string> Asked { get; } = [];

        public FileFlagBits Apply(string path, FileFlagBits flags, EntryKind kind) => FileFlagBits.None;

        public FileFlagBits Read(string path)
        {
            Asked.Add(path);
            return TryGetValue(path, out var flags) ? flags : FileFlagBits.None;
        }
    }
}
