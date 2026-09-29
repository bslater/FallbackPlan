using FallbackPlan.Domain;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// The byte rates background activity is limited to (NFR-PERF-013): the
/// grammar a person writes in the configuration file — a whole number and a
/// binary unit per second — and the two places it is written, a destination's
/// <c>transfer_limit</c> and the installation's <c>background_read_limit</c>.
/// </summary>
/// <remarks>
/// <para>
/// Strict, with the defect named, for the reason the background window is: a
/// limit this build misreads is either a limit nobody honours or a backup that
/// crawls, and neither is visible until somebody wonders why. So a form it
/// cannot read is refused at load, never approximated.
/// </para>
/// <para>
/// The compatibility case carries the most weight here too. Every file written
/// before schema 7 says "unlimited" by not mentioning a limit, and reading that
/// as anything else would slow every existing installation at once.
/// </para>
/// </remarks>
[TestClass]
public sealed class ByteRateTests
{
    [TestMethod]
    [DataRow("65536 B/s", 65_536L)]
    [DataRow("512 KiB/s", 524_288L)]
    [DataRow("2 MiB/s", 2_097_152L)]
    [DataRow("1 GiB/s", 1_073_741_824L)]
    [DataRow("2MiB/s", 2_097_152L)]
    [DataRow("  40 MiB/s ", 41_943_040L)]
    public void ARate_InEachUnit_ReadsAsBytesPerSecond(string text, long bytesPerSecond)
    {
        Assert.AreEqual(bytesPerSecond, Parse(text).BytesPerSecond);
    }

    [TestMethod]
    public void ARate_KeepsItsTextForDisplay()
    {
        Assert.AreEqual("2 MiB/s", Parse("2 MiB/s").Text);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("fast")]
    [DataRow("2 MiB")]
    [DataRow("2 mib/s")]
    [DataRow("1.5 MiB/s")]
    [DataRow("-1 MiB/s")]
    [DataRow("2 MiB/h")]
    public void ARate_ThatIsNotAWholeNumberAndABinaryUnit_IsRefusedByName(string text)
    {
        Assert.IsFalse(ByteRate.TryParse(text, out var rate, out var defect));
        Assert.IsNull(rate);
        Assert.IsNotNull(defect);
        Assert.Contains("MiB/s", defect, StringComparison.Ordinal, "the refusal names a form that would be read");
    }

    [TestMethod]
    public void ARate_TheCommonDecimalUnit_IsRefusedWithTheBinaryOneNamed()
    {
        // "MB/s" is what people type, and it is refused rather than guessed:
        // read as 10^6 it is 5% slower than the same text read as 2^20, and a
        // limit that silently means something other than it says is the
        // failure this grammar exists to prevent.
        Assert.IsFalse(ByteRate.TryParse("10 MB/s", out _, out var defect));
        Assert.Contains("MiB/s", defect!, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("0 MiB/s")]
    [DataRow("0 B/s")]
    [DataRow("512 B/s")]
    public void ARate_BelowOneKiBPerSecond_IsRefusedAsAStopRatherThanALimit(string text)
    {
        // Zero is "never", and a few hundred bytes a second is "never" in
        // practice — a gigabyte would take weeks. Leaving the setting out is
        // how "unlimited" is said, and a stop is not something to say here.
        Assert.IsFalse(ByteRate.TryParse(text, out _, out var defect));
        Assert.Contains("1 KiB/s", defect!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ARate_TooLargeToRepresent_IsRefusedRatherThanWrapped()
    {
        Assert.IsFalse(ByteRate.TryParse("99999999999 GiB/s", out _, out var defect));
        Assert.IsNotNull(defect);
    }

    [TestMethod]
    public void AConfiguration_WrittenBeforeTheLimitsExisted_LoadsAndLimitsNothing()
    {
        var directory = Directory.CreateTempSubdirectory("fbp-rate-tests-").FullName;
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(
                path,
                """
                { "schema_version": 6, "destinations": [
                    { "id": "dddddddddddddddddddddddddddddddd", "name": "vault", "kind": "local-path", "path": "/mnt/vault" } ],
                  "backup_sets": [] }
                """);

            var loaded = ClientConfiguration.Load(path);

            Assert.AreEqual(ClientConfiguration.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.IsNull(loaded.BackgroundReadLimit);
            Assert.IsNull(loaded.EffectiveBackgroundReadLimit);
            var vault = Assert.ContainsSingle(loaded.Destinations);
            Assert.IsNull(vault.TransferLimit);
            Assert.IsNull(vault.EffectiveTransferLimit);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void AConfiguration_WithLimits_RoundTripsThroughSaveAndLoad()
    {
        var directory = Directory.CreateTempSubdirectory("fbp-rate-tests-").FullName;
        try
        {
            var path = Path.Combine(directory, "config.json");
            new ClientConfiguration
            {
                SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
                BackgroundReadLimit = "40 MiB/s",
                Destinations =
                [
                    new DestinationConfiguration
                    {
                        Id = new string('d', 32), Name = "vault", Kind = DestinationKind.LocalPath,
                        Path = Path.Combine(directory, "vault"), TransferLimit = "2 MiB/s",
                    },
                ],
            }.Save(path);

            var loaded = ClientConfiguration.Load(path);

            Assert.AreEqual("40 MiB/s", loaded.BackgroundReadLimit);
            Assert.AreEqual(41_943_040L, loaded.EffectiveBackgroundReadLimit!.BytesPerSecond);
            var vault = Assert.ContainsSingle(loaded.Destinations);
            Assert.AreEqual("2 MiB/s", vault.TransferLimit);
            Assert.AreEqual(2_097_152L, vault.EffectiveTransferLimit!.BytesPerSecond);
            Assert.Contains("\"background_read_limit\"", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Contains("\"transfer_limit\"", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void AConfiguration_WithAReadLimitThisBuildCannotRead_IsRefusedByName()
    {
        var directory = Directory.CreateTempSubdirectory("fbp-rate-tests-").FullName;
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(
                path,
                $$"""
                { "schema_version": {{ClientConfiguration.CurrentSchemaVersion}}, "background_read_limit": "fast",
                  "destinations": [], "backup_sets": [] }
                """);

            var refusal = Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(path));
            Assert.Contains("background_read_limit", refusal.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ADestination_WithATransferLimitThisBuildCannotRead_IsRefusedNamingTheDestination()
    {
        var directory = Directory.CreateTempSubdirectory("fbp-rate-tests-").FullName;
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(
                path,
                $$"""
                { "schema_version": {{ClientConfiguration.CurrentSchemaVersion}}, "destinations": [
                    { "id": "dddddddddddddddddddddddddddddddd", "name": "friend-nas", "kind": "local-path",
                      "path": "/mnt/nas", "transfer_limit": "2 MB/s" } ],
                  "backup_sets": [] }
                """);

            var refusal = Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(path));
            Assert.Contains("transfer_limit", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("friend-nas", refusal.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ByteRate Parse(string text)
    {
        Assert.IsTrue(ByteRate.TryParse(text, out var rate, out var defect), defect);
        return rate!;
    }
}
