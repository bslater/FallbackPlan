using System.Text.Json;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Format.Descriptor;
using static FallbackPlan.Hosts.Tests.BundleInspection;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The standalone recovery tool's diagnostic bundle (architecture 08 §5,
/// ADR-0082): a report of one run, asked for on that run with
/// <c>--diagnostic-bundle</c>, and read back entry by entry. Establishes
/// NFR-PRIV-003 for the recovery tool.
/// </summary>
/// <remarks>
/// <para>
/// Each test plants what must not travel. That is the passphrase and the
/// name of the variable holding it, and the archive's salt and sealing public
/// key, which are the same in every archive an installation writes and so are
/// a handle across stores. It is the repository and snapshot identities the
/// tool prints in full on the operator's own terminal, and the machine's name,
/// which the harness writes into the archive's descriptor as its creator. And
/// it is a folder whose name says more about its owner than its files do. The
/// data blobs are then damaged, so the restore fails file by file and every
/// failure names a path under that folder.
/// </para>
/// <para>
/// The scratch directory's random name is in every path the harness makes, so
/// it is the canary: if it appears anywhere in a default bundle, some path
/// crossed. The opt-in test is the positive control that keeps that assertion
/// honest — the same planted paths do appear once a person asks for them.
/// </para>
/// </remarks>
[TestClass]
public sealed class RecoveryBundleTests : IDisposable
{
    /// <summary>A folder name that could only reach a bundle as a path.</summary>
    private const string TellingFolder = "a-folder-named-for-a-diagnosis";

    private static readonly string[] ExpectedEntries =
    [
        "README.txt", "manifest.json", "environment.json", "run.json",
        "archive.json", "snapshots.json", "restore.json",
    ];

    private readonly HostHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    /// <summary>The scratch directory's random name, which every harness path carries.</summary>
    private string Canary => Path.GetFileName(Path.GetDirectoryName(_harness.StateDirectory))!;

    private static Task<HostHarness.Invocation> RunAsync(params string[] args) =>
        HostHarness.RunAsync(RecoveryHost.RunAsync, args);

    private string[] Arguments(string command) =>
    [
        command,
        "--repo", _harness.RepositoryPath,
        "--passphrase-env", _harness.PassphraseVariable,
    ];

    private string Work(string name)
    {
        Directory.CreateDirectory(_harness.WorkPath);
        return Path.Combine(_harness.WorkPath, name);
    }

    private async Task<string> BackUpTellingFolderAsync()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile($"{TellingFolder}/scan-2026.pdf", "what the scan said");
        _harness.WriteSourceFile($"{TellingFolder}/letter.txt", "what the letter said");
        await _harness.BackUpAsync();

        var listing = await RunAsync(Arguments("snapshots"));
        Assert.AreEqual(0, listing.ExitCode, listing.All);
        return listing.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    /// <summary>
    /// A real backup, then every data blob cut short: the trees still read, so
    /// each file fails by name, and each damaged blob is skipped with the
    /// platform's own words for why.
    /// </summary>
    private async Task<string> BackUpAndDamageAsync()
    {
        var snapshot = await BackUpTellingFolderAsync();

        var data = Directory.GetFiles(
            Path.Combine(_harness.RepositoryPath, "blobs", "data"), "*", SearchOption.AllDirectories);
        Assert.IsNotEmpty(data, "the backup wrote no data blob to damage");
        foreach (var blob in data)
        {
            using var file = new FileStream(blob, FileMode.Open, FileAccess.Write);
            file.SetLength(file.Length / 2);
        }

        return snapshot;
    }

    /// <summary>Everything a careless bundle would carry, by name.</summary>
    private Dictionary<string, string> PlantedSecrets(string snapshot)
    {
        Assert.IsInstanceOfType<DescriptorParseResult.Ok>(
            RepositoryDescriptorCodec.Parse(File.ReadAllBytes(Path.Combine(_harness.RepositoryPath, "repository-format"))),
            out var parsed);
        var descriptor = parsed.Descriptor;

        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["passphrase"] = Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!,
            ["passphrase variable"] = _harness.PassphraseVariable,
            ["installation salt (hex)"] = Convert.ToHexStringLower(descriptor.KdfSalt.Span),
            ["installation salt (HEX)"] = Convert.ToHexString(descriptor.KdfSalt.Span),
            ["installation salt (base64)"] = Convert.ToBase64String(descriptor.KdfSalt.Span),
            ["sealing public key (hex)"] = Convert.ToHexStringLower(descriptor.SealingPublicKey.Span),
            ["sealing public key (HEX)"] = Convert.ToHexString(descriptor.SealingPublicKey.Span),
            ["sealing public key (base64)"] = Convert.ToBase64String(descriptor.SealingPublicKey.Span),
            ["repository id"] = Convert.ToHexStringLower(descriptor.RepositoryId.ToArray()),
            ["snapshot id"] = snapshot,
        };

        // Only a name long enough to be unmistakable is asserted on: a short
        // one like "vm" is a word any bundle might contain by chance.
        if (Environment.MachineName.Length >= 8)
        {
            secrets["machine name"] = Environment.MachineName;
        }

        return secrets;
    }

    private static async Task<Dictionary<string, string>> ReadBundleAsync(string path) =>
        Open(await File.ReadAllBytesAsync(path));

    [TestMethod]
    public async Task Restore_WithABundle_CarriesNoSecretNoPathAndNoIdentifierInFull()
    {
        var snapshot = await BackUpAndDamageAsync();
        var secrets = PlantedSecrets(snapshot);
        var bundle = Work("recovery.zip");

        var result = await RunAsync(
        [
            .. Arguments("restore"), "--snapshot", snapshot, "--output", Work("recovered"),
            "--diagnostic-bundle", bundle,
        ]);

        // The damaged run is the one worth sending: it failed, and the bundle
        // was written all the same, said on standard error so the verb's own
        // output stays what a script reads.
        Assert.AreEqual(2, result.ExitCode, result.All);
        Assert.Contains("recovery.zip", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("recovery.zip", result.Output, StringComparison.Ordinal);

        var entries = await ReadBundleAsync(bundle);
        CollectionAssert.AreEquivalent(ExpectedEntries, entries.Keys.ToArray());

        AssertNowhere("scratch directory (so a path)", Canary, null, entries);
        AssertNowhere("telling folder name", TellingFolder, null, entries);
        foreach (var (what, value) in secrets)
        {
            AssertNowhere(what, value, null, entries);
        }

        // What makes the bundle worth sending survives, shortened where it
        // correlates and withheld where no type said it may cross.
        using var run = JsonDocument.Parse(entries["run.json"]);
        Assert.AreEqual("restore", run.RootElement.GetProperty("verb").GetString());
        Assert.AreEqual(2, run.RootElement.GetProperty("exit_code").GetInt32());
        Assert.StartsWith("snap#", run.RootElement.GetProperty("options").GetProperty("snapshot").GetString()!, StringComparison.Ordinal);
        Assert.StartsWith("path#", run.RootElement.GetProperty("options").GetProperty("output").GetString()!, StringComparison.Ordinal);

        using var archive = JsonDocument.Parse(entries["archive.json"]);
        Assert.AreEqual("ok", archive.RootElement.GetProperty("descriptor").GetString());
        Assert.AreEqual("reproduced", archive.RootElement.GetProperty("passphrase").GetString());
        Assert.AreEqual("repo#" + secrets["repository id"][..8], archive.RootElement.GetProperty("repository").GetString());
        Assert.StartsWith("path#", archive.RootElement.GetProperty("store").GetString()!, StringComparison.Ordinal);
        Assert.IsGreaterThan(0, archive.RootElement.GetProperty("format_version").GetInt32());

        var blobs = archive.RootElement.GetProperty("blobs");
        Assert.IsGreaterThan(0, blobs.GetProperty("skipped").GetInt32());
        foreach (var note in blobs.GetProperty("notes").EnumerateArray())
        {
            Assert.AreEqual("blob_skipped", note.GetProperty("kind").GetString());
            Assert.StartsWith("blobs#", note.GetProperty("subject").GetString()!, StringComparison.Ordinal);
            Assert.AreEqual(RedactedRendering.Withheld, note.GetProperty("detail").GetString());
        }

        using var snapshots = JsonDocument.Parse(entries["snapshots.json"]);
        var listed = Assert.ContainsSingle(snapshots.RootElement.GetProperty("snapshots").EnumerateArray().ToList());
        Assert.AreEqual("snap#" + snapshot[..8], listed.GetProperty("snapshot").GetString());
        Assert.AreEqual("verified", listed.GetProperty("signature").GetString());

        using var restore = JsonDocument.Parse(entries["restore.json"]);
        Assert.IsTrue(restore.RootElement.GetProperty("ran").GetBoolean());
        Assert.AreEqual(2, restore.RootElement.GetProperty("failed").GetInt32());
        var failures = restore.RootElement.GetProperty("notes").EnumerateArray().ToList();
        Assert.HasCount(2, failures);
        foreach (var note in failures)
        {
            Assert.AreEqual("segment_missing", note.GetProperty("kind").GetString());
            Assert.StartsWith("path#", note.GetProperty("path").GetString()!, StringComparison.Ordinal);
            Assert.StartsWith("obj#", note.GetProperty("subject").GetString()!, StringComparison.Ordinal);
        }

        using var manifest = JsonDocument.Parse(entries["manifest.json"]);
        Assert.IsFalse(manifest.RootElement.GetProperty("includes_paths").GetBoolean());
        CollectionAssert.AreEquivalent(
            ExpectedEntries,
            manifest.RootElement.GetProperty("entries").EnumerateArray().Select(entry => entry.GetString()).ToArray());
    }

    [TestMethod]
    public async Task Restore_WithABundleAndPaths_CarriesThePathsAndStillNoSecretOrIdentifierInFull()
    {
        var snapshot = await BackUpAndDamageAsync();
        var secrets = PlantedSecrets(snapshot);
        var bundle = Work("recovery-with-paths.zip");
        var output = Work("recovered");

        var result = await RunAsync(
        [
            .. Arguments("restore"), "--snapshot", snapshot, "--output", output,
            "--diagnostic-bundle", bundle, "--include-paths",
        ]);

        Assert.AreEqual(2, result.ExitCode, result.All);

        // The consequence is said where the person typing the flag reads it.
        Assert.Contains("plaintext paths", result.Error, StringComparison.Ordinal);

        var entries = await ReadBundleAsync(bundle);

        // The positive control for the default test's absences: the same
        // planted paths, and once asked for they are there.
        Assert.Contains(_harness.RepositoryPath, Readable(entries, "archive.json"), StringComparison.Ordinal);
        Assert.Contains(output, Readable(entries, "run.json"), StringComparison.Ordinal);
        Assert.Contains($"{TellingFolder}/scan-2026.pdf", Readable(entries, "restore.json"), StringComparison.Ordinal);
        Assert.Contains($"{TellingFolder}/letter.txt", Readable(entries, "restore.json"), StringComparison.Ordinal);

        // The opt-in is to paths. Secrets never depended on it, and
        // identifiers still shorten because correlation was never offered.
        foreach (var (what, value) in secrets)
        {
            AssertNowhere(what, value, null, entries);
        }

        using var manifest = JsonDocument.Parse(entries["manifest.json"]);
        Assert.IsTrue(manifest.RootElement.GetProperty("includes_paths").GetBoolean());
        Assert.Contains("paths", entries["README.txt"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("included", entries["README.txt"], StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Open_TheWrongPassphrase_TheBundleSaysHowFarTheRunGot()
    {
        await _harness.CreateRepositoryAsync();
        var variable = "FBP_RECOVERY_GUESS_" + Guid.NewGuid().ToString("N");
        const string guess = "not the passphrase, only somebody's best guess at it";
        Environment.SetEnvironmentVariable(variable, guess);

        try
        {
            var bundle = Work("refused.zip");
            var result = await RunAsync(
                "open", "--repo", _harness.RepositoryPath, "--passphrase-env", variable,
                "--diagnostic-bundle", bundle);

            Assert.AreEqual(1, result.ExitCode);
            var entries = await ReadBundleAsync(bundle);
            AssertNowhere("the guess", guess, null, entries);
            AssertNowhere("the guess's variable", variable, null, entries);

            // The archive was read, so what it says is there; the passphrase
            // is where it stopped, which is what the helper needs to know.
            using var archive = JsonDocument.Parse(entries["archive.json"]);
            Assert.AreEqual("ok", archive.RootElement.GetProperty("descriptor").GetString());
            Assert.AreEqual("refused", archive.RootElement.GetProperty("passphrase").GetString());
            Assert.IsGreaterThan(0, archive.RootElement.GetProperty("format_version").GetInt32());

            using var run = JsonDocument.Parse(entries["run.json"]);
            var failure = run.RootElement.GetProperty("failure");
            Assert.AreEqual("passphrase", failure.GetProperty("stage").GetString());
            Assert.AreEqual("passphrase_refused", failure.GetProperty("reason").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [TestMethod]
    public async Task Open_AFolderThatIsNotAnArchive_TheBundleSaysTheDescriptorWasMissing()
    {
        var empty = Work("not-an-archive");
        Directory.CreateDirectory(empty);
        var bundle = Work("missing.zip");

        var result = await RunAsync(
            "open", "--repo", empty, "--passphrase-env", _harness.PassphraseVariable,
            "--diagnostic-bundle", bundle);

        Assert.AreEqual(1, result.ExitCode);
        var entries = await ReadBundleAsync(bundle);
        AssertNowhere("scratch directory (so a path)", Canary, null, entries);

        using var archive = JsonDocument.Parse(entries["archive.json"]);
        Assert.AreEqual("missing", archive.RootElement.GetProperty("descriptor").GetString());
        Assert.AreEqual("not_attempted", archive.RootElement.GetProperty("passphrase").GetString());

        using var run = JsonDocument.Parse(entries["run.json"]);
        var failure = run.RootElement.GetProperty("failure");
        Assert.AreEqual("descriptor", failure.GetProperty("stage").GetString());
        Assert.AreEqual("not_an_archive", failure.GetProperty("reason").GetString());
    }

    [TestMethod]
    public async Task Open_ARequiredOptionMissing_TheBundleNamesWhichOne()
    {
        var bundle = Work("options.zip");

        var result = await RunAsync("open", "--repo", _harness.RepositoryPath, "--diagnostic-bundle", bundle);

        Assert.AreEqual(1, result.ExitCode);
        var entries = await ReadBundleAsync(bundle);

        using var run = JsonDocument.Parse(entries["run.json"]);
        var failure = run.RootElement.GetProperty("failure");
        Assert.AreEqual("options", failure.GetProperty("stage").GetString());
        Assert.AreEqual("missing_option", failure.GetProperty("reason").GetString());
        Assert.AreEqual("--passphrase-env", failure.GetProperty("option").GetString());

        using var archive = JsonDocument.Parse(entries["archive.json"]);
        Assert.AreEqual("not_read", archive.RootElement.GetProperty("descriptor").GetString());
    }

    [TestMethod]
    public async Task Snapshots_WithABundle_LeavesTheListingExactlyAsAScriptReadsIt()
    {
        await BackUpTellingFolderAsync();
        var bundle = Work("listing.zip");

        var plain = await RunAsync(Arguments("snapshots"));
        var bundled = await RunAsync([.. Arguments("snapshots"), "--diagnostic-bundle", bundle]);

        Assert.AreEqual(0, bundled.ExitCode, bundled.All);
        Assert.AreEqual(plain.Output, bundled.Output);

        var entries = await ReadBundleAsync(bundle);
        using var snapshots = JsonDocument.Parse(entries["snapshots.json"]);
        Assert.IsTrue(snapshots.RootElement.GetProperty("listed").GetBoolean());
        Assert.HasCount(1, snapshots.RootElement.GetProperty("snapshots").EnumerateArray().ToList());

        using var restore = JsonDocument.Parse(entries["restore.json"]);
        Assert.IsFalse(restore.RootElement.GetProperty("ran").GetBoolean());
    }

    [TestMethod]
    public async Task Restore_ABundleFileAlreadyThere_IsRefusedBeforeAnythingRuns()
    {
        var snapshot = await BackUpTellingFolderAsync();
        var bundle = Work("somebodys.zip");
        await File.WriteAllTextAsync(bundle, "somebody's file");
        var output = Work("never-written");

        var result = await RunAsync(
        [
            .. Arguments("restore"), "--snapshot", snapshot, "--output", output,
            "--diagnostic-bundle", bundle,
        ]);

        // A file already there is somebody's: refused before the archive is
        // even opened, so the restore it would have described never ran.
        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("already exists", result.Error, StringComparison.Ordinal);
        Assert.AreEqual("somebody's file", await File.ReadAllTextAsync(bundle));
        Assert.IsFalse(Directory.Exists(output), "the restore ran although the bundle was refused");
    }

    [TestMethod]
    public async Task IncludePaths_WithoutABundle_IsRefusedRatherThanIgnored()
    {
        var result = await RunAsync([.. Arguments("open"), "--include-paths"]);

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("--diagnostic-bundle", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RecoveryHost_Help_NamesTheBundleAndItsOptIn()
    {
        var result = await RunAsync("--help");

        Assert.AreEqual(0, result.ExitCode);
        Assert.Contains("--diagnostic-bundle", result.Output, StringComparison.Ordinal);
        Assert.Contains("--include-paths", result.Output, StringComparison.Ordinal);
    }
}
