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
        Fingerprint = "mgr7e7euwdpfkggmp4astkz5ia",
        Endpoint = "alice.example.com:7040",
    };

    private static BackupSetConfiguration Set(string name, params SetDestinationReference[] destinations) => new()
    {
        Id = new string('a', 32),
        Name = name,
        Roots = [new BackupRootConfiguration { Path = "/data/docs" }],
        Destinations = destinations,
    };

    private static SetDestinationReference Ref(string name, RetentionConfiguration? retention = null) =>
        new() { Ref = name, Retention = retention };

    [TestMethod]
    public void SaveThenLoad_PrioritiesAndConcurrency_RoundTrip()
    {
        // ADR-0047: a set and a destination each carry a priority, a set's
        // reference may override the destination's, and the pool width is a
        // configured number. Absent everywhere means default — a v4 file is a
        // valid v5 file that simply says nothing.
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            MaxConcurrentBackups = 3,
            Destinations = [LocalPath("vault") with { Priority = 7 }],
            BackupSets =
            [
                Set("docs", Ref("vault") with { Priority = 2 }) with { Priority = 5 },
            ],
        }.Save(ConfigPath);

        var loaded = ClientConfiguration.Load(ConfigPath);
        Assert.AreEqual(3, loaded.MaxConcurrentBackups);
        Assert.AreEqual(7, loaded.Destinations.Single().Priority);
        var set = loaded.BackupSets.Single();
        Assert.AreEqual(5, set.Priority);
        Assert.AreEqual(2, set.Destinations.Single().Priority);
    }

    [TestMethod]
    public void Validate_ConcurrencyOutsideOneToFive_IsRefused()
    {
        foreach (var invalid in new[] { 0, 6, -1 })
        {
            var refused = Assert.ThrowsExactly<ClientStateException>(() => new ClientConfiguration
            {
                SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
                MaxConcurrentBackups = invalid,
                Destinations = [LocalPath("vault")],
                BackupSets = [Set("docs", Ref("vault"))],
            }.Save(ConfigPath));

            Assert.Contains("max_concurrent_backups", refused.Message, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public void Load_RecordsTheLoadAtDebug()
    {
        // The scheduler reads through this path every pass — every ten to
        // sixty seconds for the life of the service. At Information that one
        // message was 98% of the tier an operator reads (the 2026-08-24/25
        // service log: 347 of 353 Information records). The record stays, at
        // the level of routine mechanics; "the configuration changed" is the
        // operator-facing event, and the host that can see change logs it.
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [LocalPath("usb-vault")],
            BackupSets = [Set("docs", Ref("usb-vault"))],
        }.Save(ConfigPath);

        var log = new FallbackPlan.TestSupport.RecordingLogger();
        ClientConfiguration.Load(ConfigPath, log);

        var record = Assert.ContainsSingle(log.Records.Where(record => record.EventId == 3400));
        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Debug, record.Level);
    }

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
    public void SaveThenLoad_ADeclaredFailureDomain_RoundTrips()
    {
        // The declaration wins over any derived default (FR-SNP-007,
        // ADR-0018 Amendment 2): only the user knows where the NAS sits.
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [Peer("alice") with { FailureDomain = FailureDomain.Independent }],
            BackupSets = [Set("docs", Ref("alice"))],
        }.Save(ConfigPath);

        var loaded = ClientConfiguration.Load(ConfigPath);
        Assert.AreEqual(FailureDomain.Independent, loaded.FindDestination("alice")!.FailureDomain);
        Assert.Contains("\"independent\"", loaded.ExportJson(), StringComparison.Ordinal);

        // Undeclared stays undeclared — the default is derived at status
        // time by kind, never written back into the file.
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [Peer("bob", '3')],
            BackupSets = [Set("docs", Ref("bob"))],
        }.Save(ConfigPath);
        Assert.IsNull(ClientConfiguration.Load(ConfigPath).FindDestination("bob")!.FailureDomain);
    }

    [TestMethod]
    public void SaveThenLoad_TheVerificationPolicy_DefaultsToRequiredAndRoundTripsTheAcknowledgement()
    {
        // FR-VER-006: the safe answer is the one you get by not thinking
        // about it, and excusing a destination from proving itself takes a
        // word nobody types by accident.
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [Peer("alice")],
            BackupSets = [Set("docs", Ref("alice"))],
        }.Save(ConfigPath);

        var byDefault = ClientConfiguration.Load(ConfigPath).FindDestination("alice")!;
        Assert.IsNull(byDefault.Verification, "an unstated policy stays unstated in the file");
        Assert.IsTrue(byDefault.RequiresVerification, "and an unstated policy means proof is required");

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [Peer("bob", '3') with { Verification = VerificationPolicy.AcknowledgedNone }],
            BackupSets = [Set("docs", Ref("bob"))],
        }.Save(ConfigPath);

        var acknowledged = ClientConfiguration.Load(ConfigPath);
        Assert.AreEqual(VerificationPolicy.AcknowledgedNone, acknowledged.FindDestination("bob")!.Verification);
        Assert.IsFalse(acknowledged.FindDestination("bob")!.RequiresVerification);
        Assert.Contains("\"acknowledged-none\"", acknowledged.ExportJson(), StringComparison.Ordinal);
    }

    [TestMethod]
    public void Load_AMistypedEndpoint_LoadsAnywayAndReportsTheDefect()
    {
        // The load path is hot: `ServiceRuntime.Configuration` re-reads and
        // re-validates this file on every property access, several times per
        // scheduler pass. Throwing over one destination's typo would stop
        // every set backing up and stop `status` answering — including for
        // sets that do not use the destination at all. The defect is a report
        // instead, and the blast radius stays where it already was: one
        // (set, destination) pair.
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [Peer("alice") with { Endpoint = "alice.example.com" }],
            BackupSets = [Set("docs", Ref("alice"))],
        }.Save(ConfigPath);

        var loaded = ClientConfiguration.Load(ConfigPath);

        Assert.Contains(
            "host:port", loaded.FindDestination("alice")!.AddressDefect ?? "", StringComparison.Ordinal);
        Assert.HasCount(1, loaded.BackupSets, "the rest of the configuration is untouched by one bad address");
    }

    [TestMethod]
    public void Load_ALocalPathDestinationDecliningVerification_IsRefused()
    {
        // The acknowledgement is for destinations that genuinely cannot be
        // challenged. A directory this hub owns is not one: it reads it back
        // itself, at a cost of sixteen ranges of a few kilobytes. Accepting
        // the excuse would buy nothing and permanently forfeit the staging
        // trim, so it is refused at load rather than regretted later.
        var declining = new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [LocalPath("usb") with { Verification = VerificationPolicy.AcknowledgedNone }],
            BackupSets = [Set("docs", Ref("usb"))],
        };

        var refusal = Assert.ThrowsExactly<ClientStateException>(() => declining.Save(ConfigPath));
        Assert.Contains("usb", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("acknowledged-none", refusal.Message, StringComparison.Ordinal);

        // Stating the default explicitly is fine — it is the same policy.
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [LocalPath("usb") with { Verification = VerificationPolicy.Required }],
            BackupSets = [Set("docs", Ref("usb"))],
        }.Save(ConfigPath);
        Assert.IsTrue(ClientConfiguration.Load(ConfigPath).FindDestination("usb")!.RequiresVerification);
    }

    [TestMethod]
    public void Load_AVerificationPolicyOutsideTheVocabulary_IsRefused()
    {
        // Notably including the plausible-looking ones: there is no "none",
        // no "off", no "false" — only the word that carries its own
        // acknowledgement.
        foreach (var word in new[] { "none", "off", "optional", "false" })
        {
            File.WriteAllText(ConfigPath, $$"""
                { "schema_version": 2,
                  "destinations": [ { "id": "{{new string('1', 32)}}", "name": "usb", "kind": "local-path",
                                      "path": "/mnt/u", "verification": "{{word}}" } ],
                  "backup_sets": [ { "id": "{{new string('a', 32)}}", "name": "docs", "root": "/d", "destinations": [ "usb" ] } ] }
                """);

            Assert.ThrowsExactly<ClientStateException>(
                () => ClientConfiguration.Load(ConfigPath), $"'{word}' must not be accepted");
        }
    }

    [TestMethod]
    public void Load_AFailureDomainOutsideTheVocabulary_IsRefused()
    {
        File.WriteAllText(ConfigPath, $$"""
            { "schema_version": 2,
              "destinations": [ { "id": "{{new string('1', 32)}}", "name": "usb", "kind": "local-path",
                                  "path": "/mnt/u", "failure_domain": "very-safe" } ],
              "backup_sets": [ { "id": "{{new string('a', 32)}}", "name": "docs", "root": "/d", "destinations": [ "usb" ] } ] }
            """);

        Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(ConfigPath));
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

    [TestMethod]
    public void Load_ASchemaTwoRoot_MigratesToRootsAndStaysThere()
    {
        // The shape every existing install wrote. It must read as schema 3
        // without an edit, and the next save must write the new form
        // (ADR-0040).
        File.WriteAllText(ConfigPath, $$"""
            { "schema_version": 2,
              "destinations": [ { "id": "{{new string('1', 32)}}", "name": "usb", "kind": "local-path", "path": "/mnt/u" } ],
              "backup_sets": [ { "id": "{{new string('a', 32)}}", "name": "docs", "root": "/data/docs", "destinations": [ "usb" ] } ] }
            """);

        var loaded = ClientConfiguration.Load(ConfigPath);

        Assert.AreEqual(ClientConfiguration.CurrentSchemaVersion, loaded.SchemaVersion);
        var set = Assert.ContainsSingle(loaded.BackupSets);
        Assert.IsNull(set.Root);
        Assert.AreEqual("/data/docs", Assert.ContainsSingle(set.Roots).Path);

        loaded.Save(ConfigPath);
        var written = File.ReadAllText(ConfigPath);
        Assert.Contains("\"roots\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\"root\":", written, StringComparison.Ordinal);
        Assert.AreEqual("/data/docs", ClientConfiguration.Load(ConfigPath).BackupSets[0].Roots[0].Path);
    }

    [TestMethod]
    public void Load_ASetSpeakingBothRootForms_IsRefused()
    {
        // Both forms at once is a misread, not extra information — guessing
        // which wins would capture the wrong folders silently.
        File.WriteAllText(ConfigPath, $$"""
            { "schema_version": 3,
              "destinations": [ { "id": "{{new string('1', 32)}}", "name": "usb", "kind": "local-path", "path": "/mnt/u" } ],
              "backup_sets": [ { "id": "{{new string('a', 32)}}", "name": "docs", "root": "/data/docs",
                "roots": [ { "path": "/data/docs" } ], "destinations": [ "usb" ] } ] }
            """);

        var exception = Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(ConfigPath));
        Assert.Contains("both", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Validate_AMultiRootSetWithoutLabels_IsRefused()
    {
        // Labels are the roots' snapshot coordinates; a multi-root set cannot
        // be saved without them (they are materialized at edit time, never
        // derived on read).
        var configuration = new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [LocalPath("usb")],
            BackupSets = [Set("docs", Ref("usb")) with
            {
                Roots =
                [
                    new BackupRootConfiguration { Path = "/data/docs" },
                    new BackupRootConfiguration { Path = "/data/pics" },
                ],
            }],
        };

        var exception = Assert.ThrowsExactly<ClientStateException>(() => configuration.Save(ConfigPath));
        Assert.Contains("label", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void Validate_LabelsDifferingOnlyInCase_AreRefused()
    {
        // A case-insensitive restore target would collapse the two into one
        // folder, so uniqueness is case-insensitive on purpose.
        var configuration = new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [LocalPath("usb")],
            BackupSets = [Set("docs", Ref("usb")) with
            {
                Roots =
                [
                    new BackupRootConfiguration { Path = "/data/docs", Label = "Docs" },
                    new BackupRootConfiguration { Path = "/backup/docs", Label = "docs" },
                ],
            }],
        };

        Assert.ThrowsExactly<ClientStateException>(() => configuration.Save(ConfigPath));
    }

    [TestMethod]
    public void Validate_DuplicateRootPaths_AreRefused()
    {
        var configuration = new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [LocalPath("usb")],
            BackupSets = [Set("docs", Ref("usb")) with
            {
                Roots =
                [
                    new BackupRootConfiguration { Path = "/data/docs", Label = "a" },
                    new BackupRootConfiguration { Path = "/data/docs", Label = "b" },
                ],
            }],
        };

        Assert.ThrowsExactly<ClientStateException>(() => configuration.Save(ConfigPath));
    }

    [TestMethod]
    public void LabelDefect_EachPathology_IsNamed()
    {
        Assert.IsNotNull(ClientConfiguration.LabelDefect(""));
        Assert.IsNotNull(ClientConfiguration.LabelDefect("."));
        Assert.IsNotNull(ClientConfiguration.LabelDefect(".."));
        Assert.IsNotNull(ClientConfiguration.LabelDefect("a/b"));
        Assert.IsNotNull(ClientConfiguration.LabelDefect(@"a\b"));
        Assert.IsNotNull(ClientConfiguration.LabelDefect("c:"));
        Assert.IsNotNull(ClientConfiguration.LabelDefect("a*b"));
        Assert.IsNotNull(ClientConfiguration.LabelDefect("a?b"));
        Assert.IsNotNull(ClientConfiguration.LabelDefect("e\u0301"), "a decomposed sequence is not NFC");
        Assert.IsNotNull(ClientConfiguration.LabelDefect(new string('x', 256)));

        Assert.IsNull(ClientConfiguration.LabelDefect("Documents"));
        Assert.IsNull(ClientConfiguration.LabelDefect("caf\u00e9"));
    }

    [TestMethod]
    public void DeriveLabels_LeafNamesWithACollision_GetNumericSuffixes()
    {
        var derived = ClientConfiguration.DeriveLabels(
        [
            new BackupRootConfiguration { Path = "/data/docs" },
            new BackupRootConfiguration { Path = "/backup/docs" },
            new BackupRootConfiguration { Path = "/pics/", Label = "Photos" },
        ]);

        Assert.AreEqual("docs", derived[0].Label);
        Assert.AreEqual("docs-2", derived[1].Label);
        Assert.AreEqual("Photos", derived[2].Label, "an explicit label is never rewritten");
    }

    [TestMethod]
    public void DeriveLabels_ASingleRoot_IsLeftAlone()
    {
        // One root keeps the legacy snapshot shape; its label is unused and
        // must not be invented.
        var roots = new[] { new BackupRootConfiguration { Path = "/data/docs" } };

        Assert.IsNull(Assert.ContainsSingle(ClientConfiguration.DeriveLabels(roots)).Label);
    }

    [TestMethod]
    public void DeriveLabels_ADriveRoot_FallsBackRatherThanEmittingAnEmptyLabel()
    {
        var derived = ClientConfiguration.DeriveLabels(
        [
            new BackupRootConfiguration { Path = "C:\\" },
            new BackupRootConfiguration { Path = "/" },
        ]);

        // "C:" strips to "C"; a bare "/" has no leaf at all and takes the
        // fallback. Both must satisfy LabelDefect afterwards.
        Assert.AreEqual("C", derived[0].Label);
        Assert.AreEqual("root", derived[1].Label);
        Assert.IsNull(ClientConfiguration.LabelDefect(derived[0].Label!));
        Assert.IsNull(ClientConfiguration.LabelDefect(derived[1].Label!));
    }
}
