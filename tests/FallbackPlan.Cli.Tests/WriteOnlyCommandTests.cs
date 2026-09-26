using System.Text.RegularExpressions;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The CLI's creation and direct-mode surface (ADR-0042): <c>init</c> behind
/// its typed loss acknowledgement — the one creation path with no wizard in
/// front of it — and direct mode deriving the full read authority from
/// <c>--passphrase-env</c>, sealed content coming back byte-identical.
/// </summary>
[TestClass]
public sealed class WriteOnlyCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    [TestMethod]
    public async Task Init_WithoutTheAcknowledgement_IsRefusedNamingTheLoss()
    {
        var refused = await _cli.RunWithoutStateAsync("init");

        Assert.AreNotEqual(0, refused.ExitCode);
        Assert.Contains("unrecoverable", refused.All, StringComparison.Ordinal);
        Assert.Contains("--acknowledge-loss", refused.All, StringComparison.Ordinal);
        Assert.IsFalse(
            File.Exists(Path.Combine(_cli.RepositoryPath, "repository-format")),
            "a refused ceremony must create nothing");
    }

    [TestMethod]
    public async Task Init_AtFormatThree_CreatesARepositoryDeclaringRelocatableRecords()
    {
        // The opt-in, and the only way to ask for format 3 today: the service
        // still creates format 2 (ADR-0052 Amendment 1).
        var created = await _cli.RunWithoutStateAsync("init", "--acknowledge-loss", "--format-version", "3");

        Assert.AreEqual(0, created.ExitCode, created.All);

        var descriptor = await File.ReadAllBytesAsync(
            Path.Combine(_cli.RepositoryPath, "repository-format"), CancellationToken.None);
        var parsed = FallbackPlan.Repository.Format.Descriptor.RepositoryDescriptorCodec.Parse(descriptor);
        Assert.IsInstanceOfType<FallbackPlan.Repository.Format.Descriptor.DescriptorParseResult.Ok>(parsed, out var ok);

        Assert.AreEqual(FallbackPlan.Domain.FormatVersions.RelocatableRecords, ok.Descriptor.FormatVersion);
        Assert.Contains(
            FallbackPlan.Repository.Format.Descriptor.RepositoryDescriptorCodec.FeatureRelocatableRecords,
            ok.Descriptor.RequiredFeatures);
    }

    [TestMethod]
    public async Task Init_AtAFormatThisBuildCannotWrite_IsRefusedNamingTheRange()
    {
        // A version outside the writable range is refused by name before the
        // store is touched, not surfaced as an unhandled exception from the
        // lifecycle's own validation.
        var refused = await _cli.RunWithoutStateAsync("init", "--acknowledge-loss", "--format-version", "1");

        Assert.AreEqual(1, refused.ExitCode);
        Assert.Contains("cannot be created", refused.All, StringComparison.Ordinal);
        Assert.IsFalse(
            File.Exists(Path.Combine(_cli.RepositoryPath, "repository-format")),
            "a refused version must create nothing");
    }

    [TestMethod]
    public async Task WriteOnlyRepository_ArchiveAndRestoreFile_RoundTripsThroughDirectMode()
    {
        const string content = "sealed by the public key, back by the passphrase";

        var init = await _cli.RunWithoutStateAsync("init", "--acknowledge-loss");
        Assert.IsTrue(init.ExitCode == 0, init.All);
        Assert.Contains("created repository", init.Output, StringComparison.Ordinal);
        Assert.Contains("losing it loses the backup", init.Output, StringComparison.Ordinal);
        Assert.IsFalse(
            Directory.Exists(Path.Combine(_cli.RepositoryPath, "keys")),
            "a repository stores no key object (FR-WOR-001)");

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
    public async Task WriteOnlyRepository_VerifyAndCheck_NameTheSealedPlaneAndReportNoDamage()
    {
        var init = await _cli.RunWithoutStateAsync("init", "--acknowledge-loss");
        Assert.IsTrue(init.ExitCode == 0, init.All);

        var source = _cli.WriteFile("sealed.txt", "counted sealed, not damaged");
        var archive = await _cli.RunAsync("archive", source);
        Assert.IsTrue(archive.ExitCode == 0, archive.All);

        // A records-level sweep in direct mode holds the full authority
        // (derived from the passphrase), yet still REPORTS the sealed plane
        // by name — the honesty line, with zero failures (ADR-0042 §7).
        var verify = await _cli.RunAsync("verify", "--level", "records");
        Assert.IsTrue(verify.ExitCode == 0, verify.All);
        Assert.Contains("sealed", verify.Output, StringComparison.Ordinal);
        Assert.Contains("restore grant", verify.Output, StringComparison.Ordinal);
        Assert.Contains("Not damage", verify.Output, StringComparison.Ordinal);

        var check = await _cli.RunAsync("check", "--level", "records");
        Assert.IsTrue(check.ExitCode == 0, check.All);
        Assert.Contains("Not damage", check.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Init_OverAnExistingRepository_IsACleanRefusalNotAStackTrace()
    {
        var first = await _cli.RunWithoutStateAsync("init", "--acknowledge-loss");
        Assert.IsTrue(first.ExitCode == 0, first.All);

        var refused = await _cli.RunWithoutStateAsync("init", "--acknowledge-loss");
        Assert.AreNotEqual(0, refused.ExitCode);
        Assert.DoesNotContain("   at ", refused.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task WriteOnlyRepository_TheWrongPassphrase_IsRefusedByDeriveAndCompare()
    {
        var init = await _cli.RunWithoutStateAsync("init", "--acknowledge-loss");
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
