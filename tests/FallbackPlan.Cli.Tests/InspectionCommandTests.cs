using System.Text.RegularExpressions;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The inspection and single-file commands — <c>inspect-blob</c>,
/// <c>inspect-manifest</c>, <c>restore-file</c> and <c>verify --file</c> —
/// plus the sub-path forms of <c>ls</c> and <c>restore</c>. These are the
/// commands an operator reaches for when something looks wrong, so they are
/// exactly the ones that must not themselves be broken. <c>restore-file</c>
/// writes a file's captured metadata back as the executor does (FR-RST-004;
/// ADR-0149), an ACL that names accounts by number included where this
/// installation captured the snapshot it names (FR-RST-004; ADR-0154). It
/// lands its file as the executor does, written whole beside the output and
/// moved there once verified, so one that fails leaves what stood at the
/// output as it was (FR-RST-002; ADR-0173 Amendment 1). It is assembled in
/// that spool itself, so it needs no room in the temporary folder
/// (FR-RST-002; ADR-0173 Amendment 3). It says which of a file's flags it
/// did not give back, as the executor's receipt does (FR-RST-004; ADR-0180
/// Amendment 2).
/// </summary>
[TestClass]
public sealed class InspectionCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    /// <summary>The identifiers <c>archive</c> reports, which the inspection commands take as arguments.</summary>
    private sealed record ArchivedFile(string SnapshotId, string SnapshotObject, string FileVersion, string RootTree);

    private async Task<ArchivedFile> ArchiveAsync(string content = "hello world content for inspection")
    {
        await _cli.InitAsync();
        var source = _cli.WriteFile("a.txt", content);

        var archive = await _cli.RunAsync("archive", source);
        Assert.IsTrue(archive.ExitCode == 0, archive.All);

        static string Field(string output, string label)
        {
            var match = Regex.Match(output, $@"^{label}\s+([0-9a-f]+)\s*$", RegexOptions.Multiline);
            Assert.IsTrue(match.Success, $"'{label}' was not reported by archive:\n{output}");
            return match.Groups[1].Value;
        }

        return new ArchivedFile(
            Field(archive.Output, "snapshot id"),
            Field(archive.Output, "snapshot object"),
            Field(archive.Output, "file version"),
            Field(archive.Output, "root tree"));
    }

    /// <summary>Turns every byte of each data blob past its envelope, so no record in it reads.</summary>
    private void DamageEveryDataBlob()
    {
        foreach (var blob in Directory.GetFiles(Path.Combine(_cli.RepositoryPath, "blobs", "data"), "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(blob);
            for (var i = 200; i < bytes.Length; i++)
            {
                bytes[i] ^= 0xFF;
            }

            File.WriteAllBytes(blob, bytes);
        }
    }

    private string FirstBlobKey(string blobClass)
    {
        var root = Path.Combine(_cli.RepositoryPath, "blobs", blobClass);
        var file = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).First();
        return Path.GetRelativePath(_cli.RepositoryPath, file).Replace(Path.DirectorySeparatorChar, '/');
    }

    [TestMethod]
    public async Task InspectBlob_AStoredBlob_OpensTheEnvelopeAndListsItsRecords()
    {
        await ArchiveAsync();

        var inspect = await _cli.RunAsync("inspect-blob", FirstBlobKey("data"));

        Assert.IsTrue(inspect.ExitCode == 0, inspect.All);
        Assert.IsFalse(string.IsNullOrWhiteSpace(inspect.Output), "inspect-blob printed nothing");
    }

    [TestMethod]
    public async Task InspectBlob_KeyIsNotInTheStore_RefusesWithAMessage()
    {
        await ArchiveAsync();

        var inspect = await _cli.RunAsync("inspect-blob", "blobs/data/zzzz/zzzzzzzzzzzzzzzzzzzzzzzzzz");

        Assert.AreNotEqual(0, inspect.ExitCode);
        Assert.DoesNotContain("   at ", inspect.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task InspectManifest_AFileVersionObject_DecodesAndPrintsIt()
    {
        var archived = await ArchiveAsync();

        var inspect = await _cli.RunAsync("inspect-manifest", archived.FileVersion);

        Assert.IsTrue(inspect.ExitCode == 0, inspect.All);
        Assert.IsFalse(string.IsNullOrWhiteSpace(inspect.Output), "inspect-manifest printed nothing");
    }

    [TestMethod]
    public async Task InspectManifest_ASnapshotAndATreeObject_DecodesAndPrintsBoth()
    {
        var archived = await ArchiveAsync();

        // The same command must decode every manifest kind, because an
        // operator reading a damaged repository does not know in advance
        // which one an identifier names.
        var snapshot = await _cli.RunAsync("inspect-manifest", archived.SnapshotObject);
        Assert.IsTrue(snapshot.ExitCode == 0, snapshot.All);

        var tree = await _cli.RunAsync("inspect-manifest", archived.RootTree);
        Assert.IsTrue(tree.ExitCode == 0, tree.All);
    }

    [TestMethod]
    [DataRow("not-hex-at-all")]
    [DataRow("abcd")]                                                        // well-formed hex, wrong length
    [DataRow("0000000000000000000000000000000000000000000000000000000000000000")]  // right shape, absent
    public async Task InspectManifest_IdentifierIsUnusable_RefusesWithoutAStackTrace(string objectId)
    {
        await ArchiveAsync();

        var inspect = await _cli.RunAsync("inspect-manifest", objectId);

        Assert.AreNotEqual(0, inspect.ExitCode);
        Assert.DoesNotContain("   at ", inspect.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RestoreFile_GivenAManifestId_WritesTheOriginalBytes()
    {
        const string content = "restore-file round trip content";
        var archived = await ArchiveAsync(content);
        var destination = Path.Combine(_cli.WorkPath, "restored-a.txt");

        var restore = await _cli.RunAsync("restore-file", "--manifest", archived.FileVersion, "--output", destination);

        Assert.IsTrue(restore.ExitCode == 0, restore.All);
        Assert.AreEqual(content, File.ReadAllText(destination));
    }

    [TestMethod]
    public async Task RestoreFile_GivenASnapshotId_WritesTheOriginalBytes()
    {
        const string content = "restore-file via snapshot";
        var archived = await ArchiveAsync(content);
        var destination = Path.Combine(_cli.WorkPath, "restored-by-snapshot.txt");

        var restore = await _cli.RunAsync("restore-file", "--snapshot", archived.SnapshotId, "--output", destination);

        Assert.IsTrue(restore.ExitCode == 0, restore.All);
        Assert.AreEqual(content, File.ReadAllText(destination));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "the temporary folder is named by TMPDIR on POSIX")]
    [PlatformTrait(TestPlatforms.Posix)]
    // Alone: it points TMPDIR, which every class in the process reads, at a folder that does not exist.
    [DoNotParallelize]
    public async Task RestoreFile_NeedsNoRoomInTheTemporaryFolder()
    {
        const string content = "restore-file with no temporary folder";
        var archived = await ArchiveAsync(content);
        var destination = Path.Combine(_cli.WorkPath, "restored-without-temp.txt");
        var temporary = Environment.GetEnvironmentVariable("TMPDIR");
        Environment.SetEnvironmentVariable("TMPDIR", Path.Combine(_cli.WorkPath, "no-such-folder"));
        CliHarness.Invocation restore;
        try
        {
            restore = await _cli.RunAsync("restore-file", "--manifest", archived.FileVersion, "--output", destination);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMPDIR", temporary);
        }

        Assert.IsTrue(restore.ExitCode == 0, restore.All);
        Assert.AreEqual(content, File.ReadAllText(destination));
    }

    [TestMethod]
    public async Task RestoreFile_ThatFails_LeavesWhatStoodAtTheOutputAsItWas()
    {
        // The file is written whole beside the output and moved there once
        // verified, as the executor lands one (ADR-0173 Amendment 1), so a
        // restore that fails never touches what the output already held.
        var archived = await ArchiveAsync();
        var folder = Directory.CreateDirectory(Path.Combine(_cli.WorkPath, "out")).FullName;
        var destination = Path.Combine(folder, "kept.txt");
        File.WriteAllText(destination, "the person's own file");
        DamageEveryDataBlob();

        var restore = await _cli.RunAsync("restore-file", "--manifest", archived.FileVersion, "--output", destination);

        Assert.AreNotEqual(0, restore.ExitCode, restore.All);
        Assert.AreEqual("the person's own file", File.ReadAllText(destination));
        Assert.AreEqual(destination, Assert.ContainsSingle(Directory.GetFiles(folder)), "no spool is left beside it");
    }

    [TestMethod]
    public async Task RestoreFile_OverAFileAlreadyThere_ReplacesItWithTheVerifiedFile()
    {
        const string content = "restore-file over an older file";
        var archived = await ArchiveAsync(content);
        var folder = Directory.CreateDirectory(Path.Combine(_cli.WorkPath, "over")).FullName;
        var destination = Path.Combine(folder, "older.txt");
        File.WriteAllText(destination, "an older file, longer than what replaces it");

        var restore = await _cli.RunAsync("restore-file", "--manifest", archived.FileVersion, "--output", destination);

        Assert.IsTrue(restore.ExitCode == 0, restore.All);
        Assert.AreEqual(content, File.ReadAllText(destination));
        Assert.AreEqual(destination, Assert.ContainsSingle(Directory.GetFiles(folder)), "no spool is left beside it");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link is made there without a privilege")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task RestoreFile_AtALink_LandsOverTheLink_AndWritesNothingThroughIt()
    {
        // A file lands over whatever stands at its name, a link included,
        // and never through it, as the executor's do (ADR-0152).
        const string content = "restore-file over a link";
        var archived = await ArchiveAsync(content);
        var folder = Directory.CreateDirectory(Path.Combine(_cli.WorkPath, "linked")).FullName;
        var elsewhere = Path.Combine(_cli.WorkPath, "elsewhere.txt");
        File.WriteAllText(elsewhere, "where the link leads");
        var destination = Path.Combine(folder, "link.txt");
        File.CreateSymbolicLink(destination, elsewhere);

        var restore = await _cli.RunAsync("restore-file", "--manifest", archived.FileVersion, "--output", destination);

        Assert.IsTrue(restore.ExitCode == 0, restore.All);
        Assert.IsNull(new FileInfo(destination).LinkTarget, "the link was written through, not replaced");
        Assert.AreEqual(content, File.ReadAllText(destination));
        Assert.AreEqual("where the link leads", File.ReadAllText(elsewhere));
    }

    [TestMethod]
    public async Task RestoreFile_GivesTheFileItsCapturedTimesAndPermissionsBack()
    {
        // The single-file restore writes back what the executor writes back,
        // by the same rule and in the same order (ADR-0149): here the
        // modification time everywhere, and the permissions where the target
        // keeps POSIX ones. A backup captures them; archive captures none.
        await _cli.InitAsync();
        var source = _cli.WriteFile("tree/a.txt", "restore-file metadata");
        var modified = new DateTime(2021, 3, 4, 5, 6, 7, 890, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, modified);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }

        var backup = await _cli.RunAsync("backup", Path.GetDirectoryName(source)!);
        Assert.IsTrue(backup.ExitCode == 0, backup.All);
        var snapshots = await _cli.RunAsync("snapshots");
        var snapshot = Regex.Match(snapshots.Output, @"\b[0-9a-f]{32}\b");
        Assert.IsTrue(snapshot.Success, snapshots.All);
        var destination = Path.Combine(_cli.WorkPath, "restored-with-metadata.txt");

        var restore = await _cli.RunAsync("restore-file", "--snapshot", snapshot.Value, "--output", destination);

        Assert.IsTrue(restore.ExitCode == 0, restore.All);
        Assert.AreEqual(modified, File.GetLastWriteTimeUtc(destination), restore.All);
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
                File.GetUnixFileMode(destination),
                restore.All);
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux keeps a file's inode flags")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task RestoreFile_AFileCapturedWithFlags_GetsBackWhatThisPlatformMaySet_AndSaysNothingOfThem()
    {
        await _cli.InitAsync();
        var source = _cli.WriteFile("flags/a.txt", "restore-file flags");
        DiskFlags.AddOnLinux(source, DiskFlags.LinuxNoDump);

        var backup = await _cli.RunAsync("backup", Path.GetDirectoryName(source)!);
        Assert.IsTrue(backup.ExitCode == 0, backup.All);
        var snapshots = await _cli.RunAsync("snapshots");
        var snapshot = Regex.Match(snapshots.Output, @"\b[0-9a-f]{32}\b");
        Assert.IsTrue(snapshot.Success, snapshots.All);
        var destination = Path.Combine(_cli.WorkPath, "restored-with-flags.txt");

        var restore = await _cli.RunAsync("restore-file", "--snapshot", snapshot.Value, "--output", destination);

        Assert.IsTrue(restore.ExitCode == 0, restore.All);
        Assert.AreEqual(DiskFlags.LinuxNoDump, DiskFlags.OnLinux(destination) & DiskFlags.LinuxNoDump, restore.All);
        Assert.DoesNotContain("file flag(s)", restore.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux keeps a file's and a folder's inode flags")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task InspectManifest_AFileAndAFolderBackedUpWithFlags_ShowTheFlagsKeyElevenKeeps()
    {
        // A repository is created at format 6, which keeps them in the
        // manifest's metadata rather than naming them (ADR-0180 Amendment 3).
        await _cli.InitAsync();
        var source = _cli.WriteFile("inspect-flags/kept/a.txt", "inspect flags");
        DiskFlags.AddOnLinux(source, DiskFlags.LinuxNoDump);
        DiskFlags.AddOnLinux(Path.GetDirectoryName(source)!, DiskFlags.LinuxNoDump);
        var backup = await _cli.RunAsync("backup", Path.GetDirectoryName(Path.GetDirectoryName(source))!);
        Assert.IsTrue(backup.ExitCode == 0, backup.All);

        var shown = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var blob in Directory.GetFiles(Path.Combine(_cli.RepositoryPath, "blobs", "meta"), "*", SearchOption.AllDirectories))
        {
            var listed = await _cli.RunAsync("inspect-blob", Path.GetRelativePath(_cli.RepositoryPath, blob).Replace('\\', '/'));
            Assert.IsTrue(listed.ExitCode == 0, listed.All);
            foreach (Match record in Regex.Matches(listed.Output, @"(FileVersionManifest|TreeManifest)\s.*\s([0-9a-f]{64})\s*$", RegexOptions.Multiline))
            {
                var inspect = await _cli.RunAsync("inspect-manifest", record.Groups[2].Value);
                Assert.IsTrue(inspect.ExitCode == 0, inspect.All);
                shown[record.Groups[2].Value] = inspect.Output;
            }
        }

        Assert.ContainsSingle(shown.Values.Where(output =>
            output.Contains("FileVersionManifest", StringComparison.Ordinal)
            && output.Contains("file flags     no-dump", StringComparison.Ordinal)));
        Assert.ContainsSingle(shown.Values.Where(output =>
            output.Contains("TreeManifest", StringComparison.Ordinal)
            && output.Contains("file flags     no-dump", StringComparison.Ordinal)));
        Assert.DoesNotContain(output => output.Contains("diagnostic     file-flags:", StringComparison.Ordinal), shown.Values);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps alternate data streams")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task RestoreFile_OnWindows_GivesTheFileItsAlternateStreamsBack_BeforeItsTimes()
    {
        // The single-file restore writes a file's streams back by the rule
        // the executor and the recovery tool hold to (ADR-0174 Amendment 2),
        // before the modification time, which writing a stream moves.
        await _cli.InitAsync();
        var source = _cli.WriteFile("streams/a.txt", "restore-file streams");
        File.WriteAllText(source + ":Zone.Identifier", "[ZoneTransfer]\nZoneId=3");
        File.WriteAllText(source + ":notes", "kept beside the file");
        var modified = new DateTime(2021, 3, 4, 5, 6, 7, 890, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, modified);

        var backup = await _cli.RunAsync("backup", Path.GetDirectoryName(source)!);
        Assert.IsTrue(backup.ExitCode == 0, backup.All);
        var snapshots = await _cli.RunAsync("snapshots");
        var snapshot = Regex.Match(snapshots.Output, @"\b[0-9a-f]{32}\b");
        Assert.IsTrue(snapshot.Success, snapshots.All);
        var destination = Path.Combine(_cli.WorkPath, "restored-with-streams.txt");

        var restore = await _cli.RunAsync("restore-file", "--snapshot", snapshot.Value, "--output", destination);

        Assert.IsTrue(restore.ExitCode == 0, restore.All);
        Assert.AreEqual("restore-file streams", File.ReadAllText(destination));
        Assert.AreEqual("[ZoneTransfer]\nZoneId=3", File.ReadAllText(destination + ":Zone.Identifier"), restore.All);
        Assert.AreEqual("kept beside the file", File.ReadAllText(destination + ":notes"), restore.All);
        Assert.AreEqual(modified, File.GetLastWriteTimeUtc(destination), restore.All);
        Assert.DoesNotContain("alternate_streams", restore.All, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux keeps a POSIX ACL as an extended attribute")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task RestoreFile_ASnapshotThisInstallationCaptured_GivesBackAnAclThatNamesAccountsByNumber()
    {
        // The executor writes such an ACL back only where this installation
        // captured the snapshot, since anywhere else the number may be
        // someone else (ADR-0087). The snapshot restore-file names says who
        // captured it (ADR-0154).
        var snapshot = await BackUpAFileWithANumberedAclAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored-with-acl.txt");

        var restore = await _cli.RunAsync("restore-file", "--snapshot", snapshot, "--output", destination);

        Assert.IsTrue(restore.ExitCode == 0, restore.All);
        CollectionAssert.AreEqual(NumberedAcl, Xattr.Get(destination, "system.posix_acl_access"), restore.All);
        Assert.DoesNotContain("system.posix_acl_access", restore.All, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux keeps a POSIX ACL as an extended attribute")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task RestoreFile_ASnapshotAnotherInstallationCaptured_WithholdsAnAclThatNamesAccountsByNumber_AndSaysSo()
    {
        var snapshot = await BackUpAFileWithANumberedAclAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored-elsewhere.txt");

        var restore = await _cli.RunAsAnotherInstallationAsync("restore-file", "--snapshot", snapshot, "--output", destination);

        Assert.IsTrue(restore.ExitCode == 0, restore.All);
        Assert.IsNull(Xattr.Get(destination, "system.posix_acl_access"), restore.All);
        Assert.Contains(
            "system.posix_acl_access (names accounts by number, and this installation did not capture the snapshot)",
            restore.All,
            StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux keeps a POSIX ACL as an extended attribute")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task RestoreFile_AManifestNamedAlone_WithholdsAnAclThatNamesAccountsByNumber_EvenWhereThisInstallationCapturedIt()
    {
        // A file version names no device, and more than one device's
        // snapshots may hold it, so a manifest named alone claims nothing.
        await BackUpAFileWithANumberedAclAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored-by-manifest.txt");

        var restore = await _cli.RunAsync("restore-file", "--manifest", await OnlyFileVersionAsync(), "--output", destination);

        Assert.IsTrue(restore.ExitCode == 0, restore.All);
        Assert.IsNull(Xattr.Get(destination, "system.posix_acl_access"), restore.All);
        Assert.Contains("this installation did not capture the snapshot", restore.All, StringComparison.Ordinal);
    }

    /// <summary>The one file version the repository's metadata blobs hold, as <c>inspect-blob</c> lists it.</summary>
    private async Task<string> OnlyFileVersionAsync()
    {
        var versions = new List<string>();
        foreach (var blob in Directory.EnumerateFiles(Path.Combine(_cli.RepositoryPath, "blobs", "meta"), "*", SearchOption.AllDirectories))
        {
            var inspect = await _cli.RunAsync(
                "inspect-blob", Path.GetRelativePath(_cli.RepositoryPath, blob).Replace(Path.DirectorySeparatorChar, '/'));
            Assert.IsTrue(inspect.ExitCode == 0, inspect.All);
            versions.AddRange(Regex.Matches(inspect.Output, @"\bFileVersionManifest\b.*\b([0-9a-f]{64})\s*$", RegexOptions.Multiline)
                .Select(match => match.Groups[1].Value));
        }

        return Assert.ContainsSingle(versions);
    }

    /// <summary>user::rw-, user:54321:r--, group::r--, mask::r--, other::---: an ACL naming an account by number.</summary>
    private static byte[] NumberedAcl => Xattr.Acl(
        (Xattr.AclUserObject, 6, Xattr.AclUndefinedId),
        (Xattr.AclUser, 4, 54_321),
        (Xattr.AclGroupObject, 4, Xattr.AclUndefinedId),
        (Xattr.AclMask, 4, Xattr.AclUndefinedId),
        (Xattr.AclOther, 0, Xattr.AclUndefinedId));

    /// <summary>Backs up one file carrying <see cref="NumberedAcl"/> from this installation, and answers the snapshot's id.</summary>
    private async Task<string> BackUpAFileWithANumberedAclAsync()
    {
        await _cli.InitAsync();
        var source = _cli.WriteFile("acl/a.txt", "restore-file acl");
        Xattr.Set(source, "system.posix_acl_access", NumberedAcl);

        var backup = await _cli.RunAsync("backup", Path.GetDirectoryName(source)!);
        Assert.IsTrue(backup.ExitCode == 0, backup.All);
        var snapshots = await _cli.RunAsync("snapshots");
        var snapshot = Regex.Match(snapshots.Output, @"\b[0-9a-f]{32}\b");
        Assert.IsTrue(snapshot.Success, snapshots.All);
        return snapshot.Value;
    }

    [TestMethod]
    public async Task RestoreFile_NeitherManifestNorSnapshotGiven_RefusesWithAMessage()
    {
        await ArchiveAsync();

        // Neither --manifest nor --snapshot: the command cannot guess, and
        // must say so rather than writing an empty file.
        var restore = await _cli.RunAsync("restore-file", "--output", Path.Combine(_cli.WorkPath, "nothing.txt"));

        Assert.AreNotEqual(0, restore.ExitCode);
        Assert.DoesNotContain("   at ", restore.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RestoreFile_SnapshotIsNotInTheRepository_RefusesWithAMessage()
    {
        await ArchiveAsync();

        var restore = await _cli.RunAsync(
            "restore-file", "--snapshot", new string('f', 32), "--output", Path.Combine(_cli.WorkPath, "absent.txt"));

        Assert.AreNotEqual(0, restore.ExitCode);
        Assert.DoesNotContain("   at ", restore.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Verify_OneFileVersion_ChecksItEndToEnd()
    {
        var archived = await ArchiveAsync();

        var verify = await _cli.RunAsync("verify", "--file", archived.FileVersion);

        Assert.IsTrue(verify.ExitCode == 0, verify.All);
    }

    [TestMethod]
    [DataRow("locator")]
    [DataRow("digest")]
    [DataRow("records")]
    public async Task Verify_EachDocumentedLevel_CompletesSuccessfully(string level)
    {
        await ArchiveAsync();

        var verify = await _cli.RunAsync("verify", "--level", level);

        Assert.IsTrue(verify.ExitCode == 0, verify.All);
    }

    [TestMethod]
    public async Task Verify_LevelIsNotOneOfTheThree_RefusesWithAMessage()
    {
        await ArchiveAsync();

        var verify = await _cli.RunAsync("verify", "--level", "paranoid");

        Assert.AreNotEqual(0, verify.ExitCode);
    }

    // ------------------------------------------------------- sub-path forms

    [TestMethod]
    public async Task Ls_ANestedDirectory_ListsItAndReportsAnAbsentOneSeparately()
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/top.txt", "top");
        _cli.WriteFile("tree/nested/inner.txt", "inner");
        await _cli.RunAsync("backup", Path.Combine(_cli.WorkPath, "tree"));

        var snapshot = await FirstSnapshotIdAsync();

        var nested = await _cli.RunAsync("ls", snapshot, "nested");
        Assert.IsTrue(nested.ExitCode == 0, nested.All);
        Assert.Contains("inner.txt", nested.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("top.txt", nested.Output, StringComparison.Ordinal);

        var absent = await _cli.RunAsync("ls", snapshot, "no-such-directory");
        Assert.AreNotEqual(0, absent.ExitCode);
    }

    [TestMethod]
    public async Task RestoreFile_GivenAPathWithinTheSnapshot_RestoresThatFile()
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/top.txt", "top");
        _cli.WriteFile("tree/nested/inner.txt", "inner");
        await _cli.RunAsync("backup", Path.Combine(_cli.WorkPath, "tree"));

        var snapshot = await FirstSnapshotIdAsync();
        var destination = Path.Combine(_cli.WorkPath, "subset");

        var restore = await _cli.RunAsync("restore", snapshot, "nested", "--output", destination);

        Assert.IsTrue(restore.ExitCode == 0, restore.All);

        // Paths stay relative to the SNAPSHOT root, not to the sub-path
        // selected: a file restored from a subset lands where a full
        // restore would have put it, so two partial restores into one
        // directory compose instead of colliding.
        Assert.AreEqual("inner", File.ReadAllText(Path.Combine(destination, "nested", "inner.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "top.txt")), "restoring a sub-path brought the whole tree");
    }

    [TestMethod]
    public async Task Ls_SnapshotIsNotInTheRepository_RefusesWithAMessage()
    {
        // An empty listing must never be reported as success without
        // establishing WHICH emptiness it is. This exited 0 with no output
        // for an unknown snapshot, which a script cannot tell apart from an
        // empty backup — `restore` refused the same input correctly.
        await _cli.InitAsync();

        var listing = await _cli.RunAsync("ls", new string('a', 32));

        Assert.AreNotEqual(0, listing.ExitCode);
        Assert.Contains("not in the catalogue", listing.All, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", listing.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Ls_TheRootOfARealSnapshot_ListsItsEntries()
    {
        // The other side of the fix: a snapshot that IS in the catalogue
        // lists its root without the new guard firing.
        await _cli.InitAsync();
        _cli.WriteFile("tree/top.txt", "top");
        await _cli.RunAsync("backup", Path.Combine(_cli.WorkPath, "tree"));

        var listing = await _cli.RunAsync("ls", await FirstSnapshotIdAsync());

        Assert.IsTrue(listing.ExitCode == 0, listing.All);
        Assert.Contains("top.txt", listing.Output, StringComparison.Ordinal);
    }

    private async Task<string> FirstSnapshotIdAsync()
    {
        var snapshots = await _cli.RunAsync("snapshots");
        Assert.IsTrue(snapshots.ExitCode == 0, snapshots.All);

        var first = snapshots.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .First();
        return first.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    /// <inheritdoc />
    public void Dispose() => _cli.Dispose();
}
