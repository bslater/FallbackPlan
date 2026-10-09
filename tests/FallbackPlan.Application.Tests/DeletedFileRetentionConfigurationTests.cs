using System.Text.Json;
using FallbackPlan.Application;
using FallbackPlan.Domain;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// How long a file deleted from the sources stays restorable (FR-GC-014,
/// ADR-0094), as the configuration declares it: <c>keep_deleted_days</c> on
/// a set's retention, and on a destination's own override of it (FR-GC-010),
/// positive or absent. Schema 11 is the version that first carries the field,
/// and a schema-10 file loads unchanged.
/// </summary>
[TestClass]
public sealed class DeletedFileRetentionConfigurationTests
{
    private string _directory = null!;

    private string ConfigPath => Path.Combine(_directory, "config.json");

    [TestInitialize]
    public void Initialize() => _directory = Directory.CreateTempSubdirectory("fp-deleted-config-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private static ClientConfiguration With(
        RetentionConfiguration? setRetention, RetentionConfiguration? overrideRetention = null) => new()
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32), Name = "usb", Kind = DestinationKind.LocalPath, Path = "/mnt/vault",
            },
            new DestinationConfiguration
            {
                Id = new string('2', 32), Name = "nas", Kind = DestinationKind.LocalPath, Path = "/mnt/nas",
            },
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = new string('a', 32),
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = "/data/docs" }],
                Retention = setRetention,
                Destinations =
                [
                    new SetDestinationReference { Ref = "usb" },
                    new SetDestinationReference { Ref = "nas", Retention = overrideRetention },
                ],
            },
        ],
    };

    [TestMethod]
    public void RoundTrip_ADeletedFileDuration_AtTheSetAndInAnOverride()
    {
        With(
            new RetentionConfiguration { KeepDaily = 14, KeepDeletedDays = 90 },
            new RetentionConfiguration { KeepMonthly = 4, KeepDeletedDays = 30 }).Save(ConfigPath);

        var set = Assert.ContainsSingle(ClientConfiguration.Load(ConfigPath).BackupSets);
        Assert.AreEqual(90, set.Retention!.KeepDeletedDays);
        Assert.AreEqual(14, set.Retention.KeepDaily);
        Assert.IsNull(set.Destinations[0].Retention);
        Assert.AreEqual(30, set.Destinations[1].Retention!.KeepDeletedDays);
        Assert.AreEqual(4, set.Destinations[1].Retention!.KeepMonthly);

        using var written = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        var declared = Assert.ContainsSingle(written.RootElement.GetProperty("backup_sets").EnumerateArray());
        Assert.AreEqual(90, declared.GetProperty("retention").GetProperty("keep_deleted_days").GetInt32());
    }

    [TestMethod]
    public void RoundTrip_ARetentionDeclaringNoDuration_WritesNone()
    {
        // Absent is the whole of "no deleted-file rule": nothing is written
        // for it, so a policy that never asked reads as it always did.
        With(new RetentionConfiguration { KeepDaily = 14 }).Save(ConfigPath);

        Assert.IsNull(Assert.ContainsSingle(ClientConfiguration.Load(ConfigPath).BackupSets).Retention!.KeepDeletedDays);
        Assert.DoesNotContain("keep_deleted_days", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
    }

    [TestMethod]
    public void Validate_AZeroOrNegativeDuration_IsRefused_AtTheSetAndInAnOverride()
    {
        // A zero duration is a typo, as a zero daily rule is: a rule that
        // keeps a deleted file for no days is no rule, and the absent field
        // already says that.
        foreach (var days in new[] { 0, -1 })
        {
            Assert.ThrowsExactly<ClientStateException>(
                () => With(new RetentionConfiguration { KeepDaily = 7, KeepDeletedDays = days }).Save(ConfigPath));
            Assert.ThrowsExactly<ClientStateException>(
                () => With(null, new RetentionConfiguration { KeepDeletedDays = days }).Save(ConfigPath));
        }

        Assert.IsFalse(new RetentionConfiguration { KeepDeletedDays = 0 }.IsValid);
        Assert.IsTrue(new RetentionConfiguration { KeepDeletedDays = 1 }.IsValid);
    }

    [TestMethod]
    public void Load_ASchemaTenFile_ReadsAsTheCurrentSchema()
    {
        File.WriteAllText(ConfigPath, $$"""
            { "schema_version": 10,
              "destinations": [ { "id": "{{new string('1', 32)}}", "name": "usb", "kind": "local-path", "path": "/mnt/vault" } ],
              "backup_sets": [ { "id": "{{new string('a', 32)}}", "name": "docs", "roots": [ { "path": "/data/docs" } ],
                "retention": { "keep_daily": 7 }, "destinations": [ "usb" ] } ] }
            """);

        var loaded = ClientConfiguration.Load(ConfigPath);
        Assert.AreEqual(ClientConfiguration.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.IsNull(Assert.ContainsSingle(loaded.BackupSets).Retention!.KeepDeletedDays);
    }

    [TestMethod]
    public void TheSchemaRises_WithTheDuration()
    {
        // Unknown fields are refused, so a file carrying the duration is a
        // compatibility event for any build that cannot read it, and the
        // version has to say so.
        Assert.AreEqual(11, ClientConfiguration.CurrentSchemaVersion);
    }
}
