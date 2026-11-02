using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// A restore gives a file's and a folder's flags back where the platform and
/// the account let it (FR-RST-004; ADR-0180 Amendment 4). Linux sets the
/// no-dump flag for any owner, and the immutable and append-only flags only
/// for a process holding <c>CAP_LINUX_IMMUTABLE</c>; it has no call that sets
/// a link's own. macOS sets its owner's immutable, append-only, no-dump and
/// hidden flags, a link's through <c>lchflags</c>. Neither sets a file
/// compressed or encrypted by a flag, and Windows keeps none of them.
/// </summary>
[TestClass]
public sealed class FileFlagWriteTests
{
    private const FileFlagBits Owners = FileFlagBits.Immutable | FileFlagBits.AppendOnly | FileFlagBits.NoDump | FileFlagBits.Hidden;

    [TestMethod]
    public void OnLinux_ARestoreMaySetNoDump_ImmutableAndAppendOnlyOnlyWithTheCapability_AndNothingOnALink()
    {
        Assert.AreEqual(FileFlagBits.NoDump, FileFlags.SettableOnLinux(EntryKind.File, holdsImmutableCapability: false));
        Assert.AreEqual(
            FileFlagBits.NoDump | FileFlagBits.Immutable | FileFlagBits.AppendOnly,
            FileFlags.SettableOnLinux(EntryKind.File, holdsImmutableCapability: true));
        Assert.AreEqual(FileFlagBits.NoDump, FileFlags.SettableOnLinux(EntryKind.DirectoryPlaceholder, holdsImmutableCapability: false));
        Assert.AreEqual(FileFlagBits.None, FileFlags.SettableOnLinux(EntryKind.Symlink, holdsImmutableCapability: true));
    }

    [TestMethod]
    public void OnMacOs_ARestoreMaySetItsOwnersFlags_OnALinkToo()
    {
        foreach (var kind in new[] { EntryKind.File, EntryKind.DirectoryPlaceholder, EntryKind.Symlink })
        {
            Assert.AreEqual(Owners, FileFlags.SettableOnMacOs(kind), $"{kind}");
        }
    }

    [TestMethod]
    public void WhatThisPlatformMaySet_IsItsOwnRule()
    {
        foreach (var kind in new[] { EntryKind.File, EntryKind.DirectoryPlaceholder, EntryKind.Symlink })
        {
            var expected = OperatingSystem.IsLinux()
                ? FileFlags.SettableOnLinux(kind, FileFlags.HoldsImmutableCapability)
                : OperatingSystem.IsMacOS() ? FileFlags.SettableOnMacOs(kind) : FileFlagBits.None;
            Assert.AreEqual(expected, FileFlags.Settable(kind), $"{kind}");
        }

        Assert.IsFalse(FileFlags.Settable(EntryKind.File).HasFlag(FileFlagBits.Compressed));
        Assert.IsFalse(FileFlags.Settable(EntryKind.File).HasFlag(FileFlagBits.Encrypted));
    }

    [TestMethod]
    public void WhatARestoreLeft_NamesEachFlagThatDidNotLand_AndNothingWhereEveryOneDid()
    {
        Assert.IsNull(MetadataRules.FileFlagsNotApplied(FileFlagBits.NoDump, landed: FileFlagBits.NoDump));
        Assert.IsNull(MetadataRules.FileFlagsNotApplied(FileFlagBits.None, landed: FileFlagBits.None));
        Assert.AreEqual(
            "file flag(s) not applied: immutable, encrypted",
            MetadataRules.FileFlagsNotApplied(FileFlagBits.Immutable | FileFlagBits.NoDump | FileFlagBits.Encrypted, landed: FileFlagBits.NoDump));
    }

    [TestMethod]
    public void TheWriter_AsksOnlyForTheFlagsTheTargetSetsOnThatKind_AndAnswersWhichLanded()
    {
        var held = new Held(lands: FileFlagBits.NoDump);
        var writer = new MetadataWriter(new MetadataTarget
        {
            SupportsPosixMetadata = true,
            SettableFlags = FileFlagBits.NoDump | FileFlagBits.Immutable,
            SettableLinkFlags = FileFlagBits.None,
            Flags = held,
        });

        Assert.AreEqual(
            FileFlagBits.NoDump,
            writer.WriteFlags("/x", FileFlagBits.Immutable | FileFlagBits.NoDump | FileFlagBits.Encrypted, EntryKind.File));
        Assert.AreEqual(("/x", FileFlagBits.Immutable | FileFlagBits.NoDump, EntryKind.File), Assert.ContainsSingle(held.Asked));

        // Nothing the target sets on a link, and nothing wanted, asks nothing.
        Assert.AreEqual(FileFlagBits.None, writer.WriteFlags("/l", FileFlagBits.NoDump, EntryKind.Symlink));
        Assert.AreEqual(FileFlagBits.None, writer.WriteFlags("/y", FileFlagBits.None, EntryKind.File));
        Assert.AreEqual(FileFlagBits.None, writer.WriteFlags("/z", FileFlagBits.Encrypted, EntryKind.File));
        Assert.HasCount(1, held.Asked);

        // A target that sets none asks nothing.
        var none = new Held(lands: Owners);
        Assert.AreEqual(FileFlagBits.None, new MetadataWriter(new MetadataTarget { SupportsPosixMetadata = true, Flags = none }).WriteFlags("/x", Owners, EntryKind.File));
        Assert.IsEmpty(none.Asked);
    }

    [TestMethod]
    public void TheLocalTarget_SetsWhatThisPlatformMay()
    {
        var target = MetadataTarget.ForLocalPlatform();

        Assert.AreEqual(FileFlags.Settable(EntryKind.File), target.SettableFlags);
        Assert.AreEqual(FileFlags.Settable(EntryKind.Symlink), target.SettableLinkFlags);
        Assert.AreSame(FileFlags.OfThisPlatform, target.Flags);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux keeps a file's and a folder's inode flags")]
    [PlatformTrait(TestPlatforms.Linux)]
    public void OnLinux_AFilesAndAFoldersFlagsAreSet_AsTheAccountMay_AndNoneThroughALink()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-flag-writes", Guid.NewGuid().ToString("n"))).FullName;
        try
        {
            var file = Path.Combine(root, "a.bin");
            File.WriteAllBytes(file, [1, 2, 3]);
            var folder = Directory.CreateDirectory(Path.Combine(root, "d")).FullName;
            var link = Path.Combine(root, "l");
            File.CreateSymbolicLink(link, file);
            var wanted = FileFlagBits.NoDump | FileFlagBits.Immutable | FileFlagBits.AppendOnly;
            var settable = FileFlags.SettableOnLinux(EntryKind.File, FileFlags.HoldsImmutableCapability);

            // A link is never opened, so nothing reaches what it points at.
            Assert.AreEqual(FileFlagBits.None, FileFlags.OfThisPlatform.Apply(link, FileFlagBits.NoDump, EntryKind.Symlink));
            Assert.AreEqual(0, DiskFlags.OnLinux(file) & DiskFlags.LinuxNoDump);

            Assert.AreEqual(wanted & settable, FileFlags.OfThisPlatform.Apply(file, wanted, EntryKind.File));
            Assert.AreEqual(
                (settable.HasFlag(FileFlagBits.Immutable) ? DiskFlags.LinuxImmutable | DiskFlags.LinuxAppendOnly : 0) | DiskFlags.LinuxNoDump,
                DiskFlags.OnLinux(file) & (DiskFlags.LinuxImmutable | DiskFlags.LinuxAppendOnly | DiskFlags.LinuxNoDump),
                "each flag is tried on its own, so one refused leaves the others set");

            Assert.AreEqual(FileFlagBits.NoDump, FileFlags.OfThisPlatform.Apply(folder, FileFlagBits.NoDump, EntryKind.DirectoryPlaceholder));
            Assert.AreEqual(DiskFlags.LinuxNoDump, DiskFlags.OnLinux(folder) & DiskFlags.LinuxNoDump);
        }
        finally
        {
            DiskFlags.ClearUnder(root);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "macOS keeps a file's flags in st_flags")]
    [PlatformTrait(TestPlatforms.MacOs)]
    public void OnMacOs_AFilesAFoldersAndALinksOwnFlagsAreSet()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-flag-writes", Guid.NewGuid().ToString("n"))).FullName;
        try
        {
            var file = Path.Combine(root, "a.bin");
            File.WriteAllBytes(file, [1, 2, 3]);
            var folder = Directory.CreateDirectory(Path.Combine(root, "d")).FullName;
            var link = Path.Combine(root, "l");
            File.CreateSymbolicLink(link, file);

            Assert.AreEqual(FileFlagBits.Hidden, FileFlags.OfThisPlatform.Apply(link, FileFlagBits.Hidden, EntryKind.Symlink));
            Assert.AreEqual(DiskFlags.DarwinHidden, DiskFlags.OnMacOs(link) & DiskFlags.DarwinHidden);
            Assert.AreEqual(0u, DiskFlags.OnMacOs(file) & DiskFlags.DarwinHidden, "the link's own, not what it points at");

            Assert.AreEqual(
                FileFlagBits.NoDump | FileFlagBits.Immutable,
                FileFlags.OfThisPlatform.Apply(file, FileFlagBits.NoDump | FileFlagBits.Immutable | FileFlagBits.Compressed, EntryKind.File));
            Assert.AreEqual(DiskFlags.DarwinNoDump | DiskFlags.DarwinImmutable, DiskFlags.OnMacOs(file) & (DiskFlags.DarwinNoDump | DiskFlags.DarwinImmutable));

            Assert.AreEqual(FileFlagBits.NoDump, FileFlags.OfThisPlatform.Apply(folder, FileFlagBits.NoDump, EntryKind.DirectoryPlaceholder));
            Assert.AreEqual(DiskFlags.DarwinNoDump, DiskFlags.OnMacOs(folder) & DiskFlags.DarwinNoDump);
        }
        finally
        {
            DiskFlags.ClearUnder(root);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS have a call that sets a file's flags")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void WhereNothingStands_NoFlagIsSaidToLand()
    {
        var missing = Path.Combine(Path.GetTempPath(), "fbp-flag-writes", Guid.NewGuid().ToString("n"));

        Assert.AreEqual(FileFlagBits.None, FileFlags.OfThisPlatform.Apply(missing, FileFlagBits.NoDump, EntryKind.File));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux keeps a file's and a folder's inode flags")]
    [PlatformTrait(TestPlatforms.Linux)]
    public void OnLinux_WhatKeepsNoInodeFlags_IsSaidToGetNone()
    {
        // A device keeps none: FS_IOC_GETFLAGS refuses it, whoever asks, so
        // nothing is written to it.
        Assert.AreEqual(FileFlagBits.None, FileFlags.OfThisPlatform.Apply("/dev/null", FileFlagBits.NoDump, EntryKind.File));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "macOS keeps a file's flags in st_flags")]
    [PlatformTrait(TestPlatforms.MacOs)]
    public void OnMacOs_AFileOnAVolumeThatRefusesEveryFlag_IsSaidToGetOnlyWhatItAlreadyCarried()
    {
        // The system volume is mounted read-only, so each flag is refused,
        // together and alone, whoever asks.
        const string OnTheSystemVolume = "/System/Library/CoreServices/SystemVersion.plist";
        const FileFlagBits Asked = FileFlagBits.NoDump | FileFlagBits.Hidden;
        var before = DiskFlags.OnMacOs(OnTheSystemVolume);

        Assert.AreEqual(
            MetadataRules.FileFlagsFromDarwin(before) & Asked,
            FileFlags.OfThisPlatform.Apply(OnTheSystemVolume, Asked, EntryKind.File));
        Assert.AreEqual(before, DiskFlags.OnMacOs(OnTheSystemVolume));
    }

    private sealed class Held(FileFlagBits lands) : IFileFlags
    {
        public List<(string Path, FileFlagBits Flags, EntryKind Kind)> Asked { get; } = [];

        public FileFlagBits Apply(string path, FileFlagBits flags, EntryKind kind)
        {
            Asked.Add((path, flags, kind));
            return flags & lands;
        }
    }
}
