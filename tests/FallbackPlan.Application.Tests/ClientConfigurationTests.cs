using FallbackPlan.Application;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// The v2 configuration schema: a top-level destinations table, per-set
/// destination references with optional retention overrides, and validation
/// that refuses what fan-out could not serve (FR-DEST-001/005/006, FR-GC-010,
/// ADR-0034 §5). The v1 tests here pin the migration refusal, not compatibility
/// — pre-1.0 the old schema is rejected with directions, never guessed at.
/// </summary>
[TestClass]
public sealed class ClientConfigurationTests
{
    private string _directory = null!;

    private string ConfigPath => Path.Combine(_directory, "config.json");

    [TestInitialize]
    public void Initialize()
    {
        _directory = Directory.CreateTempSubdirectory("fp-config-").FullName;
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private static DestinationConfiguration LocalPath(string name, char fill = '1') => new()
    {
        Id = new string(fill, 32),
        Name = name,
        Kind = DestinationKind.LocalPath,
        Path = "/mnt/vault",
    };

    private static DestinationConfiguration Peer(string name, char fill = '2') => new()
    {
        Id = new string(fill, 32),
        Name = name,
        Kind = DestinationKind.Peer,
        Fingerprint = "mfzq6ysbmfzq6ysbmfzq6ysbmf",
        Endpoint = "alice.example.com:7040",
    };

    private static BackupSetConfiguration Set(string name, params SetDestinationReference[] destinations) => new()
    {
        Id = new string('a', 32),
        Name = name,
        Root = "/data/docs",
        Destinations = destinations,
    };

    private static SetDestinationReference Ref(string name, RetentionConfiguration? retention = null) =>
        new() { Ref = name, Retention = retention };

    [TestMethod]
    public void SaveThenLoad_DestinationsAndRetention_RoundTrip()
    {
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [LocalPath("usb-vault"), Peer("alice")],
            BackupSets =
            [
                Set("docs", Ref("usb-vault"), Ref("alice", new RetentionConfiguration { KeepMonthly = 4 })) with
                {
                    Retention = new RetentionConfiguration { KeepDaily = 14, KeepWeekly = 8, MinGenerations = 3 },
                },
            ],
        }.Save(ConfigPath);

        var loaded = ClientConfiguration.Load(ConfigPath);

        var set = Assert.ContainsSingle(loaded.BackupSets);
        Assert.AreEqual(2, loaded.Destinations.Count);
        Assert.AreEqual(DestinationKind.Peer, loaded.FindDestination("alice")!.Kind);
        Assert.AreEqual("alice.example.com:7040", loaded.FindDestination("alice")!.Endpoint);
        Assert.AreEqual(14, set.Retention!.KeepDaily);
        Assert.AreEqual("usb-vault", set.Destinations[0].Ref);
        Assert.IsNull(set.Destinations[0].Retention);
        Assert.AreEqual(4, set.Destinations[1].Retention!.KeepMonthly);
    }

    [TestMethod]
    public void Load_PlainStringReference_ReadsAndWritesTheShortForm()
    {
        File.WriteAllText(ConfigPath, $$"""
            { "schema_version": 2,
              "destinations": [ { "id": "{{new string('1', 32)}}", "name": "usb", "kind": "local-path", "path": "/mnt/u" } ],
              "backup_sets": [ { "id": "{{new string('a', 32)}}", "name": "docs", "root": "/d", "destinations": [ "usb" ] } ] }
            """);

        var loaded = ClientConfiguration.Load(ConfigPath);
        Assert.AreEqual("usb", Assert.ContainsSingle(loaded.BackupSets).Destinations[0].Ref);

        // A reference with no override serialises back to the bare string.
        Assert.Contains("\"usb\"", loaded.ExportJson(), StringComparison.Ordinal);
    }

    [TestMethod]
    public void Load_SchemaVersion1_IsRefusedWithTheMigration()
    {
        File.WriteAllText(ConfigPath, """{ "schema_version": 1, "backup_sets": [] }""");

        var exception = Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(ConfigPath));

        // Not a bare refusal: the message says what to do (ADR-0034).
        Assert.Contains("destinations", exception.Message, StringComparison.Ordinal);
        Assert.Contains("schema_version", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Validate_ASetWithNoDestinations_IsRefusedNamingTheSet()
    {
        var configuration = new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [LocalPath("usb")],
            BackupSets = [Set("docs")],
        };

        var exception = Assert.ThrowsExactly<ClientStateException>(() => configuration.Save(ConfigPath));
        Assert.Contains("docs", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Validate_APeerOnlySet_IsValid()
    {
        // Local is not mandatory (FR-DEST-001): the founding scenario —
        // backed up only to a friend's machine — must validate.
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [Peer("alice")],
            BackupSets = [Set("docs", Ref("alice"))],
        }.Save(ConfigPath);

        Assert.AreEqual(DestinationKind.Peer, ClientConfiguration.Load(ConfigPath).Destinations[0].Kind);
    }

    [TestMethod]
    public void Validate_AnUnknownDestinationReference_IsRefused()
    {
        var configuration = new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [LocalPath("usb")],
            BackupSets = [Set("docs", Ref("nas"))],
        };

        var exception = Assert.ThrowsExactly<ClientStateException>(() => configuration.Save(ConfigPath));
        Assert.Contains("nas", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Validate_ADuplicateDestinationReference_IsRefused()
    {
        var configuration = new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [LocalPath("usb")],
            BackupSets = [Set("docs", Ref("usb"), Ref("usb"))],
        };

        Assert.ThrowsExactly<ClientStateException>(() => configuration.Save(ConfigPath));
    }

    [TestMethod]
    public void Validate_DuplicateDestinationNames_AreRefused()
    {
        var configuration = new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [LocalPath("usb", '1'), LocalPath("usb", '3')],
        };

        Assert.ThrowsExactly<ClientStateException>(() => configuration.Save(ConfigPath));
    }

    [TestMethod]
    public void Validate_ALocalPathDestinationWithoutAPath_IsRefused()
    {
        var configuration = new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [LocalPath("usb") with { Path = null }],
        };

        Assert.ThrowsExactly<ClientStateException>(() => configuration.Save(ConfigPath));
    }

    [TestMethod]
    public void Validate_APeerDestinationWithoutItsIdentity_IsRefused()
    {
        var configuration = new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [Peer("alice") with { Endpoint = null }],
        };

        Assert.ThrowsExactly<ClientStateException>(() => configuration.Save(ConfigPath));
    }

    [TestMethod]
    public void Validate_AFieldForAnotherKind_IsRefused()
    {
        // A peer carrying a path is a misread configuration, not extra data.
        var configuration = new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [Peer("alice") with { Path = "/mnt/x" }],
        };

        Assert.ThrowsExactly<ClientStateException>(() => configuration.Save(ConfigPath));
    }

    [TestMethod]
    public void Validate_TheReservedCloudKinds_AreAcceptedBySchema()
    {
        // FR-DEST-005: configuration models the cloud kinds now; the runtime
        // refuses to serve them until a provider exists — but that is the
        // runtime's stated incapacity, never a configuration error.
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration { Id = new string('4', 32), Name = "s3-main", Kind = DestinationKind.S3 },
                new DestinationConfiguration { Id = new string('5', 32), Name = "az", Kind = DestinationKind.AzureBlob },
                new DestinationConfiguration { Id = new string('6', 32), Name = "db", Kind = DestinationKind.Dropbox },
            ],
        }.Save(ConfigPath);

        Assert.AreEqual(3, ClientConfiguration.Load(ConfigPath).Destinations.Count);
    }

    [TestMethod]
    public void Validate_AnUnknownKind_IsRefusedAtParse()
    {
        File.WriteAllText(ConfigPath, $$"""
            { "schema_version": 2,
              "destinations": [ { "id": "{{new string('1', 32)}}", "name": "x", "kind": "carrier-pigeon" } ],
              "backup_sets": [] }
            """);

        Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(ConfigPath));
    }

    [TestMethod]
    public void Validate_NonPositiveRetention_IsRefused()
    {
        var configuration = new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [LocalPath("usb")],
            BackupSets = [Set("docs", Ref("usb")) with
            {
                Retention = new RetentionConfiguration { KeepDaily = 0 },
            }],
        };

        Assert.ThrowsExactly<ClientStateException>(() => configuration.Save(ConfigPath));
    }

    [TestMethod]
    public void Load_AnUnknownFieldInAReference_IsRejectedRatherThanIgnored()
    {
        File.WriteAllText(ConfigPath, $$"""
            { "schema_version": 2,
              "destinations": [ { "id": "{{new string('1', 32)}}", "name": "usb", "kind": "local-path", "path": "/mnt/u" } ],
              "backup_sets": [ { "id": "{{new string('a', 32)}}", "name": "docs", "root": "/d",
                "destinations": [ { "ref": "usb", "retenshun": {} } ] } ] }
            """);

        Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(ConfigPath));
    }
}
