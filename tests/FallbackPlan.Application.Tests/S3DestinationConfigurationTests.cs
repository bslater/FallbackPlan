using FallbackPlan.Application;
using FallbackPlan.Domain;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// An S3-compatible destination as the configuration declares it (FR-DEST-005,
/// FR-REP-002, ADR-0091): an endpoint and a bucket it cannot do without, a
/// region and a prefix it may name, and no secret anywhere in the file — the
/// credentials live in the service's state directory (NFR-SEC-012), so the
/// file stays exportable as it is (NFR-OPS-003). Schema 9 is the version that
/// first carries these fields, and a schema-8 file loads unchanged.
/// </summary>
[TestClass]
public sealed class S3DestinationConfigurationTests
{
    private string _directory = null!;

    private string ConfigPath => Path.Combine(_directory, "config.json");

    [TestInitialize]
    public void Initialize() => _directory = Directory.CreateTempSubdirectory("fp-s3-config-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private static DestinationConfiguration Cloud(string name = "cloud") => new()
    {
        Id = new string('7', 32),
        Name = name,
        Kind = DestinationKind.S3,
        Endpoint = "https://objects.example.net",
        Bucket = "family-backups",
        Region = "eu-test-1",
        Prefix = "site-a",
    };

    private static ClientConfiguration With(params DestinationConfiguration[] destinations) => new()
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations = destinations,
    };

    [TestMethod]
    public void RoundTrip_AnS3Destination_KeepsItsAddress_AndNoSecret()
    {
        With(Cloud() with { Addressing = BucketAddressing.VirtualHost }).Save(ConfigPath);

        var loaded = Assert.ContainsSingle(ClientConfiguration.Load(ConfigPath).Destinations);
        Assert.AreEqual(DestinationKind.S3, loaded.Kind);
        Assert.AreEqual("https://objects.example.net", loaded.Endpoint);
        Assert.AreEqual("family-backups", loaded.Bucket);
        Assert.AreEqual("eu-test-1", loaded.EffectiveRegion);
        Assert.AreEqual("site-a", loaded.Prefix);
        Assert.AreEqual(BucketAddressing.VirtualHost, loaded.Addressing);
        Assert.IsNull(loaded.AddressDefect);

        var written = File.ReadAllText(ConfigPath);
        Assert.DoesNotContain("secret", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("access_key", written, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void RoundTrip_AnS3DestinationNamingNoRegion_TakesTheApisDefaultRegion()
    {
        With(Cloud() with { Region = null }).Save(ConfigPath);

        Assert.AreEqual(
            DestinationConfiguration.DefaultS3Region,
            Assert.ContainsSingle(ClientConfiguration.Load(ConfigPath).Destinations).EffectiveRegion);
    }

    [TestMethod]
    public void Validate_AnS3DestinationWithoutABucket_IsRefusedByName()
    {
        var refused = Assert.ThrowsExactly<ClientStateException>(() => With(Cloud() with { Bucket = null }).Save(ConfigPath));

        Assert.Contains("cloud", refused.Message, StringComparison.Ordinal);
        Assert.Contains("bucket", refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Validate_AnS3DestinationWithoutAnEndpoint_IsRefusedByName()
    {
        var refused = Assert.ThrowsExactly<ClientStateException>(() => With(Cloud() with { Endpoint = null }).Save(ConfigPath));

        Assert.Contains("cloud", refused.Message, StringComparison.Ordinal);
        Assert.Contains("endpoint", refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Validate_AnS3DestinationCarryingAPathOrAFingerprint_IsRefused()
    {
        Assert.ThrowsExactly<ClientStateException>(() => With(Cloud() with { Path = "/mnt/x" }).Save(ConfigPath));
        Assert.ThrowsExactly<ClientStateException>(() =>
            With(Cloud() with { Fingerprint = "mgr7e7euwdpfkggmp4astkz5ia" }).Save(ConfigPath));
    }

    [TestMethod]
    public void Validate_TheS3FieldsOnAnotherKind_AreRefused()
    {
        var local = new DestinationConfiguration
        {
            Id = new string('1', 32), Name = "usb", Kind = DestinationKind.LocalPath, Path = "/mnt/vault",
        };

        foreach (var misread in new[]
        {
            local with { Bucket = "family-backups" },
            local with { Region = "eu-test-1" },
            local with { Prefix = "site-a" },
            local with { Addressing = BucketAddressing.Path },
            local with { Endpoint = "https://objects.example.net" },
        })
        {
            Assert.ThrowsExactly<ClientStateException>(() => With(misread).Save(ConfigPath));
        }
    }

    [TestMethod]
    public void Validate_AnS3DestinationDecliningVerification_IsRefused()
    {
        // The hub reads a sample of it back on every sync, a few small ranges:
        // there is nothing to excuse.
        Assert.ThrowsExactly<ClientStateException>(() =>
            With(Cloud() with { Verification = VerificationPolicy.AcknowledgedNone }).Save(ConfigPath));
    }

    [TestMethod]
    public void AddressDefect_AnEndpointInClear_IsADefect_UnlessTheStoreIsThisMachine()
    {
        Assert.Contains("https", (Cloud() with { Endpoint = "http://objects.example.net" }).AddressDefect!, StringComparison.Ordinal);
        Assert.IsNull((Cloud() with { Endpoint = "http://127.0.0.1:9000" }).AddressDefect);
        Assert.IsNull((Cloud() with { Endpoint = "http://localhost:9000" }).AddressDefect);
    }

    [TestMethod]
    public void AddressDefect_AnEndpointThatIsNotABaseUrl_IsADefect()
    {
        foreach (var endpoint in new[]
        {
            "objects.example.net", "https://objects.example.net/backups", "https://objects.example.net?x=1",
            "ftp://objects.example.net", "https://user:pass@objects.example.net",
        })
        {
            Assert.IsNotNull((Cloud() with { Endpoint = endpoint }).AddressDefect, endpoint);
        }
    }

    [TestMethod]
    public void AddressDefect_ABucketRegionOrPrefixOutsideTheirGrammar_IsADefect()
    {
        Assert.IsNotNull((Cloud() with { Bucket = "No_Capitals" }).AddressDefect);
        Assert.IsNotNull((Cloud() with { Bucket = "ab" }).AddressDefect);
        Assert.IsNotNull((Cloud() with { Region = "Eu West" }).AddressDefect);
        Assert.IsNotNull((Cloud() with { Prefix = "/site-a" }).AddressDefect);
        Assert.IsNotNull((Cloud() with { Prefix = "site-a/.hidden" }).AddressDefect);
        Assert.IsNull((Cloud() with { Prefix = "site-a/host_1" }).AddressDefect);
    }

    [TestMethod]
    public void Load_ASchemaEightFile_ReadsAsTheCurrentSchema()
    {
        File.WriteAllText(ConfigPath, $$"""
            { "schema_version": 8,
              "destinations": [ { "id": "{{new string('1', 32)}}", "name": "usb", "kind": "local-path", "path": "/mnt/vault" } ],
              "backup_sets": [] }
            """);

        Assert.AreEqual(ClientConfiguration.CurrentSchemaVersion, ClientConfiguration.Load(ConfigPath).SchemaVersion);
        Assert.AreEqual(9, ClientConfiguration.CurrentSchemaVersion);
    }
}
