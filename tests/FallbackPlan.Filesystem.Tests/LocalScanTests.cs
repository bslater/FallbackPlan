using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.TestSupport;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FallbackPlan.Filesystem.Tests;

/// <summary>
/// The local scanner (phase-1 wave S; architecture 06 §1–§3): deterministic
/// byte-sorted traversal, correct classification, metadata capture,
/// rules-v1 pruning with excluded-is-not-failed semantics, symlinks never
/// followed, hardlink identity, and the revalidation stat. Platform-specific
/// behaviour is skipped where the platform lacks it, never faked. On Windows
/// a file's audit list is captured with its descriptor where the account
/// holds SeSecurityPrivilege (FR-RST-004, ADR-0176 Amendment 3), and a link
/// is captured as a link, never followed, with its own security descriptor
/// read through a handle opened on the link itself (FR-MAN-003, ADR-0182).
/// A reparse point whose tag names no other path, as a deduplicated file's
/// does, is captured as the file it is, never as a link (ADR-0183). A
/// directory junction is named a mount point, and a folder's symbolic link
/// is not (ADR-0183 Amendment 1). A file whose Windows attributes say its
/// content is not on this device is named online-only, and an ordinary file
/// is not (FR-SNP-009, ADR-0185); and on Windows a scan sees a cloud
/// provider's placeholders as they are, even in a process told to disguise
/// them (ADR-0185 Amendment 4). A file whose flags say it is dataless is
/// online-only, and the walk does not open it (ADR-0185 Amendment 5). A
/// folder its provider holds, whose listing fails, is named unlisted and not
/// entered; an ordinary folder whose listing fails, and a provider's folder
/// the account may not list, are entered and fail as before, and a
/// provider's folder that lists is walked as any other (ADR-0185
/// Amendment 6). Once a provider has failed earlier in the run, a folder it
/// holds is not asked for its listing and is named unasked, while a folder
/// no provider holds is listed as ever (ADR-0185 Amendment 7). A folder the
/// run says is unchanged is
/// named and not entered (FR-SNP-010, ADR-0186). Freeing a file again does nothing on Linux
/// and macOS; on Windows a file no provider keeps is refused and left as it
/// was, and a file the person pinned is never asked (ADR-0185 Amendment 2).
/// </summary>
[TestClass]
public sealed partial class LocalScanTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-scan-tests", Guid.NewGuid().ToString("n"));

    private readonly LocalFileSystemSource _source = new();

    public LocalScanTests() => Directory.CreateDirectory(_root);

    private string Write(string relativePath, string content = "content")
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private async Task<List<ScanEvent>> ScanAsync(ScanOptions? options = null)
    {
        var events = new List<ScanEvent>();
        await foreach (var scanEvent in _source.ScanAsync(_root, options ?? new ScanOptions(), CancellationToken.None))
        {
            events.Add(scanEvent);
        }

        return events;
    }

    /// <summary>The Guests group, which no case's account is: an account only a file's own DACL names.</summary>
    private const string Guests = "S-1-5-32-546";

    /// <summary>What <paramref name="entry"/>'s captured security descriptor says.</summary>
    private static WindowsSecurityDescriptor Described(ScanEntry entry)
    {
        Assert.IsNotNull(entry.Metadata.WindowsSecurityDescriptor, $"{entry.RelativePath} carries a descriptor");
        var read = WindowsSecurityDescriptor.Read(entry.Metadata.WindowsSecurityDescriptor.Value.Span);
        Assert.IsNotNull(read, $"{entry.RelativePath}'s descriptor reads as one");
        return read;
    }

    private static IEnumerable<string> Paths(IEnumerable<ScanEvent> events) => events.Select(scanEvent => scanEvent switch
    {
        ScanEvent.Leaf leaf => "F " + leaf.Entry.RelativePath,
        ScanEvent.EnterDirectory enter => "> " + enter.Entry.RelativePath,
        ScanEvent.LeaveDirectory leave => "< " + leave.Entry.RelativePath,
        ScanEvent.Failure failure => "! " + failure.Detail.RelativePath,
        _ => "?",
    });

    [TestMethod]
    public async Task Scan_ATreeOfDirectories_EmitsDepthFirstInByteOrder()
    {
        Write("b/inner.txt");
        Write("a-file.txt");
        Write("c.txt");
        Directory.CreateDirectory(Path.Combine(_root, "B-upper"));

        var events = await ScanAsync();

        // 'B' (0x42) sorts before 'a' (0x61) and 'b' (0x62) in raw bytes —
        // exactly what tree entry ordering requires (06 §5).
        SequenceAssert.AreEqual(
            ["> ", "> B-upper", "< B-upper", "F a-file.txt", "> b", "F b/inner.txt", "< b", "F c.txt", "< "],
            Paths(events));
    }

    [TestMethod]
    public async Task Scan_TheSameTreeTwice_EmitsIdenticalEventSequences()
    {
        Write("x/deep/file1.bin", "one");
        Write("x/file2.bin", "two");
        Write("y.bin", "three");

        SequenceAssert.AreEqual(Paths(await ScanAsync()), Paths(await ScanAsync()));
    }

    [TestMethod]
    public async Task Scan_APathAnExcludeRuleMatches_PrunesItWithoutFailing()
    {
        Write("keep/data.txt");
        Write("skip/cache.tmp");
        Write("skip/deep/more.tmp");

        Assert.IsTrue(PathRuleSet.TryCreate([], ["skip"], caseSensitive: true, out var rules, out _));
        var events = await ScanAsync(new ScanOptions { Rules = rules });

        var paths = Paths(events).ToList();
        Assert.DoesNotContain(path => path.Contains("skip", StringComparison.Ordinal), paths);
        Assert.IsEmpty(events.OfType<ScanEvent.Failure>());
        Assert.Contains("F keep/data.txt", paths);
    }

    [TestMethod]
    public async Task Scan_APosixFile_CapturesTimesModeAndOwnership()
    {
        var full = Write("meta.txt", "metadata");

        var events = await ScanAsync();
        var entry = events.OfType<ScanEvent.Leaf>().Single().Entry;

        Assert.AreEqual(ScanEntryKind.File, entry.Kind);
        Assert.AreEqual(8, entry.Length);
        Assert.IsNotNull(entry.Metadata.ModifiedAt);
        Assert.IsTrue(entry.Identity.HasValue && entry.Identity.Value.FileId != 0);

        // The captured mtime agrees with the filesystem's own report to the
        // millisecond — the value NFR-PERF-003's short-circuit will compare.
        var expected = (ulong)new DateTimeOffset(File.GetLastWriteTimeUtc(full)).ToUnixTimeMilliseconds();
        Assert.AreEqual(expected, entry.Metadata.ModifiedAt!.Value);
    }

    [TestMethod]
    public async Task Scan_WhereNoSecurityDescriptorIsRead_AnEntryCarriesNone_RatherThanAnEmptyOne()
    {
        // Off Windows none is ever read, and on Windows none is read when the
        // options say not to. An empty one is no descriptor (06 §4.1 key 7):
        // it was written onto every manifest captured off Windows, and every
        // restore of one listed a descriptor it could not apply.
        Write("docs/plain.txt");
        var options = OperatingSystem.IsWindows() ? new ScanOptions { CaptureSecurityDescriptors = false } : new ScanOptions();

        var events = await ScanAsync(options);

        Assert.IsNull(events.OfType<ScanEvent.Leaf>().Single().Entry.Metadata.WindowsSecurityDescriptor);
        Assert.IsNull(
            events.OfType<ScanEvent.EnterDirectory>().Single(enter => enter.Entry.RelativePath == "docs").Entry.Metadata.WindowsSecurityDescriptor);
    }

    [TestMethod]
    [PrivilegedPlatformCondition(TestPlatforms.Windows, "only an elevated process holds the privilege an audit list is read with")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task Scan_OnWindows_CapturesAFilesAuditList_WhereTheAccountHoldsTheSecurityPrivilege()
    {
        // The audit list is read with SeSecurityPrivilege enabled for that
        // read alone (ADR-0176 Amendment 3); an account without the privilege
        // captures the descriptor as before, without it.
        var path = Write("docs/audited.txt");
        var info = new FileInfo(path);
        var security = info.GetAccessControl(System.Security.AccessControl.AccessControlSections.Audit);
        security.AddAuditRule(new System.Security.AccessControl.FileSystemAuditRule(
            new System.Security.Principal.SecurityIdentifier("S-1-1-0"),
            System.Security.AccessControl.FileSystemRights.Delete,
            System.Security.AccessControl.AuditFlags.Success));
        info.SetAccessControl(security);

        var events = await ScanAsync();

        var descriptor = events.OfType<ScanEvent.Leaf>().Single().Entry.Metadata.WindowsSecurityDescriptor;
        Assert.IsNotNull(descriptor);
        var read = WindowsSecurityDescriptor.Read(descriptor.Value.Span);
        Assert.IsNotNull(read);
        Assert.IsTrue(read.SaclPresent);
        Assert.Contains("S-1-1-0", read.AuditedAccounts);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "a link's own descriptor is read through Windows' own handle on it")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task Scan_OnWindows_ALink_CarriesItsOwnSecurityDescriptor_NotWhatItPointsAts()
    {
        Assert.IsTrue(SymbolicLinks.CanCreate, "the premise: this account may make a link");
        var target = Write("target/kept.txt");
        WindowsAcls.KeepTo(target, Guests);
        SymbolicLinks.Create(Path.Combine(_root, "link"), target, folder: false);

        var leaves = (await ScanAsync()).OfType<ScanEvent.Leaf>().ToList();

        var kept = Described(leaves.Single(leaf => leaf.Entry.RelativePath == "target/kept.txt").Entry);
        Assert.IsTrue(kept.DaclProtected);
        Assert.Contains(Guests, kept.Accounts);
        var link = leaves.Single(leaf => leaf.Entry.RelativePath == "link").Entry;
        Assert.AreEqual(ScanEntryKind.Symlink, link.Kind);
        var own = Described(link);
        Assert.IsFalse(own.DaclProtected, "a link takes its DACL from its folder; the file it points at keeps its own");
        Assert.DoesNotContain(Guests, own.Accounts);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "a link's own descriptor is read through Windows' own handle on it")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task Scan_OnWindows_AFoldersLink_IsCapturedAsALink_NeverFollowed_WithItsOwnSecurityDescriptor()
    {
        Assert.IsTrue(SymbolicLinks.CanCreate, "the premise: this account may make a link");
        Write("target/secret.txt");
        WindowsAcls.KeepFolderTo(Path.Combine(_root, "target"), Guests);
        SymbolicLinks.Create(Path.Combine(_root, "link"), Path.Combine(_root, "target"), folder: true);

        var events = await ScanAsync();
        var leaves = events.OfType<ScanEvent.Leaf>().ToList();

        var link = leaves.Single(leaf => leaf.Entry.RelativePath == "link").Entry;
        Assert.AreEqual(ScanEntryKind.Symlink, link.Kind);
        Assert.DoesNotContain("link-form: mount-point", link.Diagnostics, "a folder's symbolic link is no mount point");
        Assert.ContainsSingle(leaf => leaf.Entry.RelativePath.EndsWith("secret.txt", StringComparison.Ordinal), leaves);
        Assert.DoesNotContain(Guests, Described(link).Accounts, "the folder it points at keeps its own");
        Assert.Contains(
            Guests,
            Described(events.OfType<ScanEvent.EnterDirectory>().Single(enter => enter.Entry.RelativePath == "target").Entry).Accounts);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "a link's own descriptor is read through Windows' own handle on it")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task Scan_OnWindows_ALinkToNothing_StillCarriesItsOwnSecurityDescriptor()
    {
        // A read that followed the link would find nothing there.
        Assert.IsTrue(SymbolicLinks.CanCreate, "the premise: this account may make a link");
        SymbolicLinks.Create(Path.Combine(_root, "dangling"), Path.Combine(_root, "not-there.txt"), folder: false);

        var link = (await ScanAsync()).OfType<ScanEvent.Leaf>().Single().Entry;

        Assert.AreEqual(ScanEntryKind.Symlink, link.Kind);
        Assert.IsTrue(Described(link).DaclPresent);
    }

    [TestMethod]
    [PrivilegedPlatformCondition(TestPlatforms.Windows, "only an elevated process holds the privilege an audit list is read with")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task Scan_OnWindows_ALinkTakingAnAuditListFromItsFolder_IsCapturedWithIt()
    {
        // The link's handle asks the right to read an audit list only where
        // the read asks for one; without it the read falls back to none.
        Assert.IsTrue(SymbolicLinks.CanCreate, "the premise: this account may make a link");
        var folder = new DirectoryInfo(_root);
        var security = folder.GetAccessControl(System.Security.AccessControl.AccessControlSections.Audit);
        security.AddAuditRule(new System.Security.AccessControl.FileSystemAuditRule(
            new System.Security.Principal.SecurityIdentifier("S-1-1-0"),
            System.Security.AccessControl.FileSystemRights.Delete,
            System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AuditFlags.Success));
        folder.SetAccessControl(security);
        SymbolicLinks.Create(Path.Combine(_root, "audited-link"), Path.Combine(_root, "not-there.txt"), folder: false);

        var link = (await ScanAsync()).OfType<ScanEvent.Leaf>().Single().Entry;

        Assert.AreEqual(ScanEntryKind.Symlink, link.Kind);
        var read = Described(link);
        Assert.IsTrue(read.SaclPresent);
        Assert.Contains("S-1-1-0", read.AuditedAccounts);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps reparse points")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task Scan_OnWindows_AReparsePointWhoseTagNamesNoPath_IsCapturedAsTheFileItIs_NotAsALink()
    {
        const string content = "the content a filter keeps";
        var path = Write("kept-by-a-filter.bin", content);
        ForeignReparsePoint.Set(path);
        Assert.IsTrue(
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0,
            "the premise: the file carries the reparse bit");

        var entry = (await ScanAsync()).OfType<ScanEvent.Leaf>().Single().Entry;

        Assert.AreEqual(ScanEntryKind.File, entry.Kind);
        Assert.IsNull(entry.LinkTarget);
        Assert.AreEqual(content.Length, entry.Length);

        // No filter on this machine keeps the tag, so its content does not
        // open, and the read says so where a link would have kept nothing.
        Assert.Throws<IOException>(() => _source.OpenRead(entry));
    }

    [TestMethod]
    public async Task Scan_AFolderTheRunSaysIsUnchanged_IsNamedAndNotEntered()
    {
        Write("docs/a.txt");
        Write("docs/sub/b.txt");
        Write("photos/c.jpg");

        var events = await ScanAsync(new ScanOptions { Unchanged = path => path == "docs" });

        var unchanged = Assert.ContainsSingle(events.OfType<ScanEvent.UnchangedDirectory>());
        Assert.AreEqual("docs", unchanged.Entry.RelativePath);
        Assert.AreEqual(ScanEntryKind.Directory, unchanged.Entry.Kind);
        Assert.IsFalse(Paths(events).Any(path => path.Contains("docs/", StringComparison.Ordinal)), "nothing below it is scanned");
        Assert.Contains("F photos/c.jpg", Paths(events));
    }

    [TestMethod]
    public async Task Scan_AnOrdinaryFile_IsNotOnlineOnly()
    {
        Write("here.txt");

        var entry = (await ScanAsync()).OfType<ScanEvent.Leaf>().Single().Entry;

        Assert.IsFalse(entry.OnlineOnly);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps the offline attribute")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task Scan_OnWindows_AFileMarkedOffline_IsOnlineOnly()
    {
        // Offline is the one of the three bits a test can set. A cloud
        // provider's placeholder carries recall on data access, which only the
        // provider's filter gives a file.
        var path = Write("elsewhere.txt");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
        Assert.IsTrue(
            (File.GetAttributes(path) & FileAttributes.Offline) != 0,
            "the premise: the file carries the offline bit");

        var entry = (await ScanAsync()).OfType<ScanEvent.Leaf>().Single().Entry;

        Assert.IsTrue(entry.OnlineOnly);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "only a POSIX walk opens a file as it names it")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_AFileWhoseFlagsSayItIsDataless_IsOnlineOnly_AndIsNotOpened()
    {
        // macOS says by a file's flags alone that a provider keeps its
        // content. The walk opens each file it names, and opening this one
        // could ask the provider for it, so it is named from its flags and
        // left closed; the hole probe, which opens it too, is not run. No
        // runner has a provider, so the stat is marked here. Both files have
        // a hole, so a probe that ran would find one.
        WriteWithAHole("away.bin");
        WriteWithAHole("here.bin");
        LocalFileSystemSource.MarkedDataless = relativePath => relativePath == "away.bin";

        var entries = (await ScanAsync()).OfType<ScanEvent.Leaf>().Select(leaf => leaf.Entry).ToList();

        var here = entries.Single(entry => entry.RelativePath == "here.bin");
        Assert.IsFalse(here.OnlineOnly);
        Assert.IsNotNull(here.ContentHandle, "the premise: the walk opens a file it names");
        if (here.SparseExtents.Count == 0)
        {
            Assert.Inconclusive("This filesystem keeps no hole, so a probe that ran would find none either.");
        }

        var away = entries.Single(entry => entry.RelativePath == "away.bin");
        Assert.IsTrue(away.OnlineOnly);
        Assert.IsNull(away.ContentHandle, "the walk did not open it");
        Assert.IsEmpty(away.SparseExtents, "the hole probe was not run on it");
    }

    [TestMethod]
    public async Task Scan_AFolderItsProviderHolds_WhoseListingFails_IsNamedUnlisted_AndNotEntered()
    {
        // A provider can keep a folder's listing as it keeps a file's
        // content: macOS marks such a folder dataless, and listing it asks
        // the provider for the names. Where the listing does not come, the
        // walk names the folder unlisted, with why, so the run can keep the
        // tree it held before (ADR-0185 Amendment 6). No runner has a
        // provider, so the folder is marked here and its listing refused.
        Write("cloud/a.txt");
        Write("here.txt");
        LocalFileSystemSource.MarkedDataless = relativePath => relativePath == "cloud";
        LocalFileSystemSource.ListingRefused = relativePath => relativePath == "cloud" ? new IOException(TimedOut) : null;

        var events = await ScanAsync();

        var unlisted = Assert.ContainsSingle(events.OfType<ScanEvent.UnlistedDirectory>());
        Assert.AreEqual("cloud", unlisted.Entry.RelativePath);
        Assert.AreEqual(ScanEntryKind.Directory, unlisted.Entry.Kind);
        Assert.AreEqual("cloud", unlisted.Detail.RelativePath);
        Assert.AreEqual(CaptureFailureReason.IoError, unlisted.Detail.Reason);
        Assert.IsFalse(
            events.OfType<ScanEvent.EnterDirectory>().Any(enter => enter.Entry.RelativePath == "cloud"),
            "the walk did not enter it");
        Assert.IsFalse(events.OfType<ScanEvent.Leaf>().Any(leaf => leaf.Entry.RelativePath.StartsWith("cloud/", StringComparison.Ordinal)));
        Assert.IsEmpty(events.OfType<ScanEvent.Failure>(), "the folder is named once, as unlisted");
        Assert.IsTrue(events.OfType<ScanEvent.Leaf>().Any(leaf => leaf.Entry.RelativePath == "here.txt"), "the walk goes on");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps the offline attribute")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task Scan_OnWindows_AFolderMarkedOffline_WhoseListingFails_IsNamedUnlisted()
    {
        // Windows gives a folder whose listing its provider keeps the same
        // bits it gives a file. Offline is the one a test can set.
        Write("cloud/a.txt");
        var folder = Path.Combine(_root, "cloud");
        File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.Offline);
        Assert.IsTrue(
            (File.GetAttributes(folder) & FileAttributes.Offline) != 0,
            "the premise: the folder carries the offline bit");
        LocalFileSystemSource.ListingRefused = relativePath => relativePath == "cloud" ? new IOException(TimedOut) : null;

        var events = await ScanAsync();

        Assert.AreEqual("cloud", Assert.ContainsSingle(events.OfType<ScanEvent.UnlistedDirectory>()).Entry.RelativePath);
    }

    [TestMethod]
    public async Task Scan_AnOrdinaryFolderWhoseListingFails_IsEnteredAndFails_AsBefore()
    {
        // No provider keeps this folder: its listing failing is the device's,
        // and the folder is entered and fails as ever.
        Write("plain/a.txt");
        LocalFileSystemSource.ListingRefused = relativePath => relativePath == "plain" ? new IOException(TimedOut) : null;

        var events = await ScanAsync();

        Assert.IsEmpty(events.OfType<ScanEvent.UnlistedDirectory>());
        Assert.AreEqual("plain", Assert.ContainsSingle(events.OfType<ScanEvent.Failure>()).Detail.RelativePath);
        Assert.IsTrue(events.OfType<ScanEvent.EnterDirectory>().Any(enter => enter.Entry.RelativePath == "plain"));
    }

    [TestMethod]
    public async Task Scan_AFolderItsProviderHolds_ThatTheAccountMayNotList_IsEnteredAndFails_AsBefore()
    {
        // A refusal is the account's, not the provider failing (ADR-0185
        // Decision 3), and keeps no tree from before.
        Write("cloud/a.txt");
        LocalFileSystemSource.MarkedDataless = relativePath => relativePath == "cloud";
        LocalFileSystemSource.ListingRefused = relativePath =>
            relativePath == "cloud" ? new UnauthorizedAccessException("Access to the path is denied.") : null;

        var events = await ScanAsync();

        Assert.IsEmpty(events.OfType<ScanEvent.UnlistedDirectory>());
        var failure = Assert.ContainsSingle(events.OfType<ScanEvent.Failure>()).Detail;
        Assert.AreEqual("cloud", failure.RelativePath);
        Assert.AreEqual(CaptureFailureReason.Permission, failure.Reason);
    }

    [TestMethod]
    public async Task Scan_AFolderItsProviderHolds_WhoseListingComes_IsWalkedAsAnyOther()
    {
        Write("cloud/a.txt");
        LocalFileSystemSource.MarkedDataless = relativePath => relativePath == "cloud";

        var events = await ScanAsync();

        Assert.IsEmpty(events.OfType<ScanEvent.UnlistedDirectory>());
        Assert.IsTrue(events.OfType<ScanEvent.EnterDirectory>().Any(enter => enter.Entry.RelativePath == "cloud"));
        Assert.IsTrue(events.OfType<ScanEvent.Leaf>().Any(leaf => leaf.Entry.RelativePath == "cloud/a.txt"));
    }

    [TestMethod]
    public async Task Scan_AfterAProviderFailedEarlierInTheRun_AFolderItHolds_IsNotAsked_AndIsNamedUnasked()
    {
        // Once a provider has failed in the run, a folder it holds is not
        // asked for its listing: the provider would only wait out the same
        // time-out again (ADR-0185 Amendment 7). The run says so between
        // events, here as it does once the first listing fails.
        Write("a-cloud/a.txt");
        Write("b-cloud/b.txt");
        Write("c-plain/c.txt");
        var asked = new List<string>();
        LocalFileSystemSource.MarkedDataless = relativePath => relativePath is "a-cloud" or "b-cloud";
        LocalFileSystemSource.ListingRefused = relativePath =>
        {
            asked.Add(relativePath);
            return relativePath == "a-cloud" ? new IOException(TimedOut) : null;
        };

        var failed = false;
        var events = new List<ScanEvent>();
        await foreach (var scanEvent in _source.ScanAsync(
            _root, new ScanOptions { ProviderFailedEarlier = () => failed }, CancellationToken.None))
        {
            events.Add(scanEvent);
            failed |= scanEvent is ScanEvent.UnlistedDirectory;
        }

        Assert.AreEqual("a-cloud", Assert.ContainsSingle(events.OfType<ScanEvent.UnlistedDirectory>()).Entry.RelativePath);
        var unasked = Assert.ContainsSingle(events.OfType<ScanEvent.UnaskedDirectory>());
        Assert.AreEqual("b-cloud", unasked.Entry.RelativePath);
        Assert.AreEqual(ScanEntryKind.Directory, unasked.Entry.Kind);
        Assert.DoesNotContain("b-cloud", asked, "its provider was not asked for its listing");
        Assert.IsFalse(
            events.OfType<ScanEvent.EnterDirectory>().Any(enter => enter.Entry.RelativePath == "b-cloud"),
            "the walk did not enter it");
        Assert.IsFalse(events.OfType<ScanEvent.Leaf>().Any(leaf => leaf.Entry.RelativePath.StartsWith("b-cloud/", StringComparison.Ordinal)));
        Assert.Contains("c-plain", asked, "a folder no provider holds is listed as ever");
        Assert.IsTrue(events.OfType<ScanEvent.Leaf>().Any(leaf => leaf.Entry.RelativePath == "c-plain/c.txt"));
    }

    private const string TimedOut = "The cloud operation was not completed before the time-out period expired.";

    private void WriteWithAHole(string relativePath)
    {
        using var stream = File.Create(Path.Combine(_root, relativePath));
        stream.Write("head"u8);
        stream.Seek(1024 * 1024 + 4, SeekOrigin.Begin);
        stream.Write("tail"u8);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows disguises a cloud provider's placeholders")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    [DoNotParallelize] // The placeholder mode is the process's, shared by every thread.
    public async Task Scan_OnWindows_SeesPlaceholdersAsTheyAre_InAProcessToldToDisguiseThem()
    {
        // Windows may show a process its cloud placeholders as ordinary
        // files, without the recall bits that say the content is elsewhere.
        // A scan must see them, or no file would ever be named online-only.
        Write("here.txt");
        Assert.IsGreaterThanOrEqualTo(0, (int)RtlSetProcessPlaceholderCompatibilityMode(DisguisePlaceholders), "the premise: the mode can be set");
        Assert.AreEqual(DisguisePlaceholders, RtlQueryProcessPlaceholderCompatibilityMode(), "the premise: placeholders are disguised");

        _ = await ScanAsync();

        Assert.AreEqual(ExposePlaceholders, RtlQueryProcessPlaceholderCompatibilityMode());
    }

    private const sbyte DisguisePlaceholders = 1;
    private const sbyte ExposePlaceholders = 2;

    [LibraryImport("ntdll")]
    private static partial sbyte RtlSetProcessPlaceholderCompatibilityMode(sbyte mode);

    [LibraryImport("ntdll")]
    private static partial sbyte RtlQueryProcessPlaceholderCompatibilityMode();

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "only Windows keeps a cloud provider's placeholders to free")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task FreeAgain_OnLinuxAndMacOs_DoesNothing()
    {
        var path = Write("here.txt");

        var entry = (await ScanAsync()).OfType<ScanEvent.Leaf>().Single().Entry;

        Assert.IsNull(_source.FreeAgain(entry));
        Assert.AreEqual("content", File.ReadAllText(path));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a cloud provider's placeholders to free")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task FreeAgain_OnWindows_AFileNoProviderKeeps_IsRefusedAndLeftAsItWas()
    {
        var path = Write("kept-by-no-provider.txt");
        var before = File.GetAttributes(path);

        var entry = (await ScanAsync()).OfType<ScanEvent.Leaf>().Single().Entry;
        var refusal = _source.FreeAgain(entry);

        Assert.IsNotNull(refusal, "a file under no provider's folder has no provider to free it");
        Assert.AreEqual(before, File.GetAttributes(path));
        Assert.AreEqual("content", File.ReadAllText(path));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a cloud provider's placeholders to free")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task FreeAgain_OnWindows_AFileThePersonPinned_IsLeftAlone()
    {
        const FileAttributes pinned = (FileAttributes)0x8_0000;
        var path = Write("pinned.txt");
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | pinned);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            Assert.Inconclusive($"this volume takes no pinned bit on a plain file: {exception.Message}");
        }

        if ((File.GetAttributes(path) & pinned) == 0)
        {
            Assert.Inconclusive("this volume keeps no pinned bit on a plain file");
        }

        var entry = (await ScanAsync()).OfType<ScanEvent.Leaf>().Single().Entry;

        // Not refused, as a file under no provider's folder is: never asked.
        Assert.IsNull(_source.FreeAgain(entry), "the person asked to keep it here");
        Assert.IsTrue((File.GetAttributes(path) & pinned) != 0);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "creating a symlink on Windows needs a privilege the runner lacks")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_TreeContainsASymlink_CapturesItAsALinkAndNeverFollowsIt()
    {
        Write("target/secret.txt");
        File.CreateSymbolicLink(Path.Combine(_root, "link"), Path.Combine(_root, "target"));

        var events = await ScanAsync();
        var link = events.OfType<ScanEvent.Leaf>().Single(leaf => leaf.Entry.RelativePath == "link").Entry;

        Assert.AreEqual(ScanEntryKind.Symlink, link.Kind);
        Assert.IsNotNull(link.LinkTarget);
        Assert.Contains("target", Encoding.UTF8.GetString(link.LinkTarget!.Value.Span), StringComparison.Ordinal);

        // Followed content would appear twice; it must appear exactly once.
        Assert.ContainsSingle(leaf => leaf.Entry.RelativePath.EndsWith("secret.txt", StringComparison.Ordinal), events.OfType<ScanEvent.Leaf>());
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "the link(2) syscall this drives is POSIX; Windows hardlinks are covered separately")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_TwoNamesShareAnInode_ReportTheSameIdentityAndLinkCount()
    {
        var original = Write("one.bin", "linked");
        Assert.AreEqual(0, Link(original, Path.Combine(_root, "two.bin")));

        var events = await ScanAsync();
        var leaves = events.OfType<ScanEvent.Leaf>().ToList();

        var one = leaves.Single(leaf => leaf.Entry.RelativePath == "one.bin").Entry;
        var two = leaves.Single(leaf => leaf.Entry.RelativePath == "two.bin").Entry;

        Assert.AreEqual(one.Identity!.Value.FileId, two.Identity!.Value.FileId);
        Assert.AreEqual(one.Identity.Value.Device, two.Identity.Value.Device);
        Assert.AreEqual(2u, one.Identity.Value.LinkCount);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "FIFOs, sockets and device nodes are POSIX entry kinds with no Windows analogue")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_TreeContainsAFifo_CapturesItsKindAsADiagnosticRatherThanAFailure()
    {
        var fifo = Path.Combine(_root, "pipe");
        if (MkFifo(fifo, 0x1B6 /* 0666 */) != 0)
        {
            return; // filesystem refuses FIFOs (some CI mounts) — nothing to assert
        }

        var events = await ScanAsync();
        var entry = events.OfType<ScanEvent.Leaf>().Single().Entry;

        Assert.AreEqual(ScanEntryKind.Special, entry.Kind);
        Assert.Contains("special-kind: fifo", entry.Diagnostics);
        Assert.AreEqual(0, entry.Length);
        Assert.IsEmpty(events.OfType<ScanEvent.Failure>());
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "mkfifo", SetLastError = true,
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int MkFifo(string path, uint mode);

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "link", SetLastError = true,
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int Link(string existingPath, string newPath);

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "setxattr(2) is POSIX; the Windows analogue is alternate data streams, tested separately")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_FileCarriesAnExtendedAttribute_CapturesItsNameAndValueUnchanged()
    {
        var full = Write("attr.txt");
        if (SetXattr(full, "user.fbp-test", "hello"u8.ToArray(), 5, 0) != 0)
        {
            return; // tmpfs without user xattrs etc. — skip, never fake
        }

        var events = await ScanAsync();
        var entry = events.OfType<ScanEvent.Leaf>().Single().Entry;

        var attribute = Assert.ContainsSingle(entry.Metadata.ExtendedAttributes);
        Assert.AreEqual("user.fbp-test", Encoding.UTF8.GetString(attribute.Name.Span));
        SequenceAssert.AreEqual("hello"u8.ToArray(), attribute.Value.ToArray());
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "setxattr", SetLastError = true,
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int SetXattr(string path, string name, byte[] value, nuint size, int flags);

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "hole discovery here uses SEEK_HOLE; Windows sparse files use a different query")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_FileContainsASparseHole_ReportsTheHoleAsAnExtentInsideTheGap()
    {
        // 1 MiB hole between two data regions, created by seeking.
        var full = Path.Combine(_root, "sparse.bin");
        using (var stream = File.Create(full))
        {
            stream.Write("head"u8);
            stream.Seek(1024 * 1024 + 4, SeekOrigin.Begin);
            stream.Write("tail"u8);
        }

        var events = await ScanAsync();
        var entry = events.OfType<ScanEvent.Leaf>().Single().Entry;

        if (entry.SparseExtents.Count == 0)
        {
            return; // filesystem materialised the hole — legitimate, nothing to assert
        }

        var hole = entry.SparseExtents[0];
        Assert.IsTrue(hole.Offset >= 4 && hole.Offset + hole.Length <= 1024 * 1024 + 4,
            $"hole [{hole.Offset}, +{hole.Length}) must sit inside the written gap");
    }

    [TestMethod]
    [UnprivilegedPlatformCondition(TestPlatforms.Posix, "denial is expressed here with chmod, a POSIX permission shape")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task Scan_ADirectoryIsUnreadable_RaisesAFailureEventAndKeepsScanning()
    {
        Write("open/readable.txt");
        var denied = Path.Combine(_root, "denied");
        Directory.CreateDirectory(denied);
        File.WriteAllText(Path.Combine(denied, "hidden.txt"), "x");
        File.SetUnixFileMode(denied, UnixFileMode.None);

        try
        {
            var events = await ScanAsync();

            var failure = Assert.ContainsSingle(events.OfType<ScanEvent.Failure>());
            Assert.AreEqual(CaptureFailureReason.Permission, failure.Detail.Reason);
            Assert.StartsWith("denied", failure.Detail.RelativePath, StringComparison.Ordinal);

            // The rest of the tree still captured (architecture 06 §1).
            Assert.Contains(leaf => leaf.Entry.RelativePath == "open/readable.txt", events.OfType<ScanEvent.Leaf>());
        }
        finally
        {
            File.SetUnixFileMode(denied, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [TestMethod]
    public void Revalidate_WhenTheFileChangedAfterTheScan_ShouldReportTheNewLengthAndTime()
    {
        var full = Write("reval.txt", "before");
        Assert.IsTrue(LocalFileSystemSource.TryStat(full, out var before));

        File.WriteAllText(full, "after-longer");
        Assert.IsTrue(LocalFileSystemSource.TryStat(full, out var after));

        Assert.AreNotEqual(before.Size, after.Size);
        Assert.AreEqual(before.FileId, after.FileId);
    }

    [TestMethod]
    public void Probe_ARealFilesystem_ReportsItsNameAndLimits()
    {
        var info = _source.Probe(_root);

        Assert.IsFalse(string.IsNullOrEmpty(info.Name));
        Assert.IsTrue(info.MaxComponentBytes is null or > 0);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "POSIX mode bits and owner names have no NTFS equivalent — Windows carries a security descriptor instead")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_RegularFileOnPosix_CapturesItsModeBitsAndOwnerName()
    {
        Write("file.txt", "contents");

        var entry = (await ScanAsync()).OfType<ScanEvent.Leaf>().Single().Entry;

        Assert.IsNotNull(entry.Metadata.PosixMode);
        Assert.IsFalse(string.IsNullOrEmpty(entry.Metadata.OwnerName));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "the readdir ABI and non-UTF-8 filenames are POSIX shapes")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_NamesUseMultiByteAndCombiningSequences_CapturesTheFilesystemBytes()
    {
        // Names chosen to exercise the decode: ASCII, multi-byte UTF-8, and a
        // combining sequence that is a different byte string from its composed
        // form. All three are valid UTF-8, so the managed enumeration agrees —
        // which is the point. If the dirent name offset were wrong, this fails
        // loudly rather than silently mangling every filename in the tree.
        Write("plain.txt");
        Write("naïve-café.txt");
        Write("résumé.txt");

        var events = await ScanAsync();
        var scanned = events.OfType<ScanEvent.Leaf>()
            .Select(leaf => Encoding.UTF8.GetString(leaf.Entry.NameBytes.Span))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var expected = Directory.EnumerateFileSystemEntries(_root)
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        SequenceAssert.AreEqual(expected, scanned);
    }

    [TestMethod]
    [PlatformCondition(
        TestPlatforms.Linux,
        "a filename that is not valid UTF-8 is only expressible on POSIX, and macOS's APFS enforces valid UTF-8 at the filesystem, so the fixture cannot exist there")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task Scan_NameIsNotValidUtf8_ReportsItRatherThanManglesIt()
    {
        // 0xFF 0xFE is not a valid UTF-8 sequence and is a perfectly legal
        // POSIX filename. Created through the syscall directly, because the
        // managed API cannot express it either.
        byte[] raw = [.. "bad"u8, (byte)0xFF, (byte)0xFE, .. "name.txt"u8];

        var path = RawPath(_root, raw);
        Assert.IsTrue(CreateFileWithRawName(path), "could not create a file with a non-UTF-8 name");

        try
        {
            var events = await ScanAsync();

        // Before this, the name decoded to U+FFFD, re-encoded to different
        // bytes, and the path built from it did not open the file — so the
        // entry was captured under a name it did not have, with content that
        // had never been read.
        var failure = Assert.ContainsSingle(f => f.Detail.Reason == CaptureFailureReason.NameNotRepresentable, events.OfType<ScanEvent.Failure>());
        Assert.Contains("a name the file does not have", failure.Detail.Detail, StringComparison.Ordinal);

            // And it is not also captured under a substituted name.
            Assert.DoesNotContain(leaf => leaf.Entry.NameBytes.Span.StartsWith("bad"u8), events.OfType<ScanEvent.Leaf>());
        }
        finally
        {
            // Directory.Delete cannot remove it either — the same defect, from
            // the other end. The harness would otherwise fail on teardown.
            NativeUnlink(path);
        }
    }

    /// <summary>Builds a NUL-terminated path from a directory and raw name bytes.</summary>
    private static byte[] RawPath(string directory, byte[] nameBytes)
    {
        var path = new byte[Encoding.UTF8.GetByteCount(directory) + 1 + nameBytes.Length + 1];
        var written = Encoding.UTF8.GetBytes(directory, path);
        path[written++] = (byte)'/';
        nameBytes.CopyTo(path, written);
        path[written + nameBytes.Length] = 0;
        return path;
    }

    /// <summary>Creates a file whose name the managed API cannot express.</summary>
    private static bool CreateFileWithRawName(byte[] path)
    {
        var handle = NativeOpen(path, 0x40 | 0x1 /* O_CREAT | O_WRONLY */, 0x1A4 /* 0644 */);
        if (handle < 0)
        {
            return false;
        }

        NativeClose(handle);
        return true;
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "unlink", SetLastError = true)]
    private static partial int NativeUnlink(byte[] pathname);

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    private static partial int NativeOpen(byte[] pathname, int flags, uint mode);

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int NativeClose(int fd);

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "an unpaired surrogate is a UTF-16 filename shape; POSIX names are bytes")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task Scan_NameHasAnUnpairedSurrogate_ReportsItRatherThanSubstitutesIt()
    {
        // U+D800 with nothing after it. Legal in an NTFS name, and it has no
        // UTF-8 encoding at all — Encoding.UTF8.GetBytes replaces it with
        // U+FFFD, which is why a bytes-only round-trip check cannot see it:
        // by the time the bytes exist, the substitution has happened.
        var lone = Path.Combine(_root, "bad\uD800name.txt");

        try
        {
            File.WriteAllText(lone, "content");
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            return; // the platform refused to create it — nothing to assert
        }

        var events = await ScanAsync();

        var failure = Assert.ContainsSingle(f => f.Detail.Reason == CaptureFailureReason.NameNotRepresentable, events.OfType<ScanEvent.Failure>());
        Assert.Contains("a name the file does not have", failure.Detail.Detail, StringComparison.Ordinal);

        // 06 §4.3 forbids the substitute specifically: an entry stored as
        // "bad�name.txt" looks captured, lists, and restores as a file
        // the user never had.
        Assert.DoesNotContain(leaf => Encoding.UTF8.GetString(leaf.Entry.NameBytes.Span).Contains('�'), events.OfType<ScanEvent.Leaf>());
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "directory junctions are an NTFS reparse-point shape with no POSIX analogue")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task Scan_TreeContainsADirectoryJunction_CapturesItAsAMountPointsLink_AndDoesNotDescend()
    {
        Write("target/secret.txt");

        // A junction carries BOTH the Directory and ReparsePoint attributes.
        // Testing Directory first classified it as an ordinary directory, and
        // the scanner descended through it — out of the approved root, which
        // architecture 06 §2 forbids. Junctions need no privilege to create,
        // so this is the shape an unprivileged attacker actually has.
        //
        // It has to be a real junction, which means mklink: the managed
        // Directory.CreateSymbolicLink makes a directory *symlink*, and that
        // does need SeCreateSymbolicLinkPrivilege — a privilege an ordinary
        // Windows account and an ordinary test runner both lack. Building the
        // link that way tested a shape the attacker cannot make and threw
        // "A required privilege is not held by the client" everywhere else.
        var junction = Path.Combine(_root, "junction");
        Assert.IsTrue(Junctions.Make(junction, Path.Combine(_root, "target")), "could not create a directory junction");

        var events = await ScanAsync();
        var link = events.OfType<ScanEvent.Leaf>()
            .Single(leaf => leaf.Entry.RelativePath == "junction").Entry;

        Assert.AreEqual(ScanEntryKind.Symlink, link.Kind);
        Assert.IsNotNull(link.LinkTarget);

        // It is named a mount point, by the folder's path on its drive, which
        // a restore needs to make a junction rather than a symbolic link
        // (ADR-0183 Amendment 1).
        Assert.Contains("link-form: mount-point", link.Diagnostics);
        Assert.MatchesRegex(@"^[A-Za-z]:\\.*\\target$", Encoding.UTF8.GetString(link.LinkTarget.Value.Span));

        // Followed content would appear twice; it must appear exactly once.
        Assert.ContainsSingle(leaf => leaf.Entry.RelativePath.EndsWith("secret.txt", StringComparison.Ordinal), events.OfType<ScanEvent.Leaf>());
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "reserved device names (CON, NUL) and case-insensitive-by-default volumes are Windows filesystem shapes")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Probe_VolumeIsNtfs_ReportsReservedNamesAndCaseInsensitivity()
    {
        var info = _source.Probe(_root);

        Assert.IsTrue(info.ReservedNames);
        Assert.IsFalse(info.CaseSensitive);
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "symlink", SetLastError = true)]
    private static partial int NativeSymlink(byte[] target, byte[] linkPath);

    /// <summary>
    /// Advances a live scan to one leaf and stops there, leaving the walk
    /// suspended so the entry's handle is still open. Everything about the
    /// handle-relative walk is a claim about that moment, and a scan already
    /// collected into a list has passed it.
    /// </summary>
    private async Task<(ScanEntry Entry, IAsyncEnumerator<ScanEvent> Walk)> PauseAtLeafAsync(string relativePath)
    {
        var walk = _source.ScanAsync(_root, new ScanOptions(), CancellationToken.None).GetAsyncEnumerator();

        while (await walk.MoveNextAsync())
        {
            if (walk.Current is ScanEvent.Leaf leaf && leaf.Entry.RelativePath == relativePath)
            {
                return (leaf.Entry, walk);
            }
        }

        await walk.DisposeAsync();
        throw new InvalidOperationException($"The scan never reached '{relativePath}'.");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "openat and fstatat are the POSIX handle-relative calls")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_NameIsRepointedAfterClassification_ReadsContentFromTheOpenHandle()
    {
        Write("victim.bin", "the bytes that were classified");

        var (entry, walk) = await PauseAtLeafAsync("victim.bin");

        await using (walk.ConfigureAwait(false))
        {
            // The premise: this platform took a handle. Without one the rest
            // of this test would prove nothing about the handle-relative walk.
            Assert.IsNotNull(entry.ContentHandle);
            Assert.IsFalse(entry.ContentHandle!.IsInvalid);

            // The attack: between classification and read, the name is made to
            // refer to a different object. A path-based scanner re-opens the
            // name and stores the substitute's bytes under the original's
            // metadata — the time-of-check-to-time-of-use gap.
            var path = Path.Combine(_root, "victim.bin");
            File.Delete(path);
            File.WriteAllText(path, "bytes the attacker substituted");

            // The substitution took: the name now yields the attacker's bytes.
            Assert.AreEqual("bytes the attacker substituted", await File.ReadAllTextAsync(path));

            using (var reader = new StreamReader(_source.OpenRead(entry)))
            {
                Assert.AreEqual("the bytes that were classified", await reader.ReadToEndAsync());
            }

            // And revalidation describes the object that was read, not the one
            // now at the name — which is what makes it a revalidation.
            var probe = _source.Revalidate(entry);
            Assert.IsNotNull(probe);
            Assert.AreEqual(entry.Identity!.Value.FileId, probe!.Identity!.Value.FileId);
            Assert.AreEqual(entry.Length, probe.Length);
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link target is a byte string only on POSIX; Windows targets are UTF-16")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_LinkTargetIsNotValidUtf8_RecordsTheExactBytesItHolds()
    {
        // A link target is under exactly the same rules as a name: bytes that
        // are not NUL. Decoding it to a string first replaces the invalid
        // sequence with U+FFFD, and the stored target then points somewhere
        // the link does not.
        byte[] target = [(byte)'.', (byte)'/', 0xFF, 0xFE, (byte)'x', 0x00];
        var linkPath = Encoding.UTF8.GetBytes(Path.Combine(_root, "dangling") + "\0");

        if (NativeSymlink(target, linkPath) != 0)
        {
            return; // the filesystem refused the link — nothing to assert
        }

        var events = await ScanAsync();
        var link = events.OfType<ScanEvent.Leaf>().Single(leaf => leaf.Entry.RelativePath == "dangling").Entry;

        Assert.AreEqual(ScanEntryKind.Symlink, link.Kind);
        SequenceAssert.AreEqual(target[..^1], link.LinkTarget!.Value.ToArray());
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "O_NOFOLLOW on the descent is what refuses this, and it is POSIX")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_ASymlinkStandsWhereADirectoryWas_DoesNotDescendThroughIt()
    {
        Directory.CreateDirectory(Path.Combine(_root, "outside"));
        File.WriteAllText(Path.Combine(_root, "outside", "secret.txt"), "not in the backup set");

        Directory.CreateDirectory(Path.Combine(_root, "inside"));
        File.CreateSymbolicLink(Path.Combine(_root, "inside", "escape"), Path.Combine(_root, "outside"));

        var events = await ScanAsync(new ScanOptions
        {
            Rules = PathRuleSet.TryCreate([], ["outside/**"], caseSensitive: true, out var rules, out _)
                ? rules
                : throw new InvalidOperationException("The exclusion rule did not compile."),
        });

        // The link is captured as a link, and nothing beneath its target is
        // reachable through it — the excluded tree stays excluded.
        var escape = events.OfType<ScanEvent.Leaf>()
            .Single(leaf => leaf.Entry.RelativePath == "inside/escape").Entry;

        Assert.AreEqual(ScanEntryKind.Symlink, escape.Kind);
        Assert.DoesNotContain(leaf => leaf.Entry.RelativePath.EndsWith("secret.txt", StringComparison.Ordinal), events.OfType<ScanEvent.Leaf>());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        try
        {
            // Reparse points first, non-recursively: Windows' recursive
            // delete refuses a directory junction ("the parameter is
            // incorrect") and would fail the whole cleanup over a link this
            // suite created on purpose.
            foreach (var entry in new DirectoryInfo(_root).EnumerateDirectories("*", SearchOption.AllDirectories))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    entry.Delete();
                }
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // a permission-test directory left unreadable; best effort
        }
    }
}
