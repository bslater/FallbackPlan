using System.Text.RegularExpressions;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The inspection and single-file commands — <c>inspect-blob</c>,
/// <c>inspect-manifest</c>, <c>restore-file</c> and <c>verify --file</c> —
/// plus the sub-path forms of <c>ls</c> and <c>restore</c>. These are the
/// commands an operator reaches for when something looks wrong, so they are
/// exactly the ones that must not themselves be broken.
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
    public async Task Inspect_manifest_refuses_an_identifier_it_cannot_use(string objectId)
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
    public async Task Verify_accepts_every_level(string level)
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
