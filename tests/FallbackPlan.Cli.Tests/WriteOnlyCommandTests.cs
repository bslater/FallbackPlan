using System.Text.RegularExpressions;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The CLI's write-only surface (ADR-0042): <c>init --write-only</c> behind
/// its typed loss acknowledgement, and direct mode deriving the full read
/// authority from <c>--passphrase-env</c> — the same commands as v1, a
/// different open underneath, sealed content coming back byte-identical.
/// </summary>
[TestClass]
public sealed class WriteOnlyCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    [TestMethod]
    public async Task Init_WriteOnlyWithoutTheAcknowledgement_IsRefusedNamingTheLoss()
    {
        var refused = await _cli.RunWithoutStateAsync("init", "--write-only");

        Assert.AreNotEqual(0, refused.ExitCode);
        Assert.Contains("unrecoverable", refused.All, StringComparison.Ordinal);
        Assert.Contains("--acknowledge-loss", refused.All, StringComparison.Ordinal);
        Assert.IsFalse(
            File.Exists(Path.Combine(_cli.RepositoryPath, "repository-format")),
            "a refused ceremony must create nothing");
    }

    [TestMethod]
    public async Task WriteOnlyRepository_ArchiveAndRestoreFile_RoundTripsThroughDirectMode()
    {
        const string content = "sealed by the public key, back by the passphrase";

        var init = await _cli.RunWithoutStateAsync("init", "--write-only", "--acknowledge-loss");
        Assert.IsTrue(init.ExitCode == 0, init.All);
        Assert.Contains("write-only", init.Output, StringComparison.Ordinal);
        Assert.Contains("losing it loses the backup", init.Output, StringComparison.Ordinal);
        Assert.IsFalse(
            Directory.Exists(Path.Combine(_cli.RepositoryPath, "keys"))
            && Directory.EnumerateFileSystemEntries(Path.Combine(_cli.RepositoryPath, "keys")).Any(),
            "a write-only repository stores no key object (FR-WOR-001)");

        var source = _cli.WriteFile("sealed.txt", content);
        var archive = await _cli.RunAsync("archive", source);
        Assert.IsTrue(archive.ExitCode == 0, archive.All);

        var manifest = Regex.Match(archive.Output, @"^file version\s+([0-9a-f]+)\s*$", RegexOptions.Multiline);
        Assert.IsTrue(manifest.Success, archive.Output);

        var destination = Path.Combine(_cli.WorkPath, "restored-sealed.txt");
        var restore = await _cli.RunAsync(
            "restore-file", "--manifest", manifest.Groups[1].Value, "--output", destination);
        Assert.IsTrue(restore.ExitCode == 0, restore.All);
        Assert.AreEqual(content, File.ReadAllText(destination));
    }

    [TestMethod]
    public async Task WriteOnlyRepository_TheWrongPassphrase_IsRefusedByDeriveAndCompare()
    {
        var init = await _cli.RunWithoutStateAsync("init", "--write-only", "--acknowledge-loss");
        Assert.IsTrue(init.ExitCode == 0, init.All);

        var wrongVariable = "FBP_TEST_WRONG_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(wrongVariable, "not this repository's passphrase");
        try
        {
            var refused = await CliHarness.RunRawAsync(
                "snapshots", "--repo", _cli.RepositoryPath, "--passphrase-env", wrongVariable,
                "--state", _cli.StatePath);

            Assert.AreNotEqual(0, refused.ExitCode);
            Assert.Contains("does not reproduce", refused.All, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", refused.All, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(wrongVariable, null);
        }
    }
}
