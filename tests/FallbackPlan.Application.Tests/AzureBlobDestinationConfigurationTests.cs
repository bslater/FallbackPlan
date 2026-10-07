using FallbackPlan.Application;
using FallbackPlan.Domain;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// An Azure Blob destination as the configuration declares it (FR-DEST-005,
/// FR-REP-002, ADR-0093): a storage account and a container it cannot do
/// without, a prefix it may name, an endpoint only for an account the public
/// service does not host, and no secret anywhere in the file — the account
/// key or shared access signature lives in the service's state directory
/// (NFR-SEC-012), so the file stays exportable as it is (NFR-OPS-003).
/// Schema 10 is the version that first carries these fields, and a schema-9
/// file loads unchanged.
/// </summary>
[TestClass]
public sealed class AzureBlobDestinationConfigurationTests
{
    private string _directory = null!;

    private string ConfigPath => Path.Combine(_directory, "config.json");

    [TestInitialize]
    public void Initialize() => _directory = Directory.CreateTempSubdirectory("fp-azure-config-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private static DestinationConfiguration Cloud(string name = "cloud") => new()
    {
        Id = new string('7', 32),
        Name = name,
        Kind = DestinationKind.AzureBlob,
        Account = "fbptestaccount",
        Container = "family-backups",
        Prefix = "site-a",
    };

    private static ClientConfiguration With(params DestinationConfiguration[] destinations) => new()
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations = destinations,
    };

    [TestMethod]
    public void RoundTrip_AnAzureBlobDestination_KeepsItsAddress_AndNoSecret()
    {
        With(Cloud() with { Endpoint = "https://fbptestaccount.objects.example.net" }).Save(ConfigPath);

        var loaded = Assert.ContainsSingle(ClientConfiguration.Load(ConfigPath).Destinations);
        Assert.AreEqual(DestinationKind.AzureBlob, loaded.Kind);
        Assert.AreEqual("fbptestaccount", loaded.Account);
        Assert.AreEqual("family-backups", loaded.Container);
        Assert.AreEqual("site-a", loaded.Prefix);
        Assert.AreEqual("https://fbptestaccount.objects.example.net", loaded.Endpoint);
        Assert.IsNull(loaded.AddressDefect);
        Assert.IsTrue(loaded.Kind.IsObjectStore(), "an Azure Blob container is an object store, as a bucket is");

        var written = File.ReadAllText(ConfigPath);
        Assert.Contains("\"account\": \"fbptestaccount\"", written, StringComparison.Ordinal);
        Assert.Contains("\"container\": \"family-backups\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sig", written, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void RoundTrip_AnAzureBlobDestinationNamingNoEndpoint_IsTheAccountAtThePublicService()
    {
        With(Cloud()).Save(ConfigPath);

        var loaded = Assert.ContainsSingle(ClientConfiguration.Load(ConfigPath).Destinations);
        Assert.IsNull(loaded.Endpoint);
        Assert.IsNull(loaded.AddressDefect);
        Assert.DoesNotContain("endpoint", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
    }

    [TestMethod]
    public void Validate_AnAzureBlobDestinationWithoutAnAccount_IsRefusedByName()
    {
        var refused = Assert.ThrowsExactly<ClientStateException>(() => With(Cloud() with { Account = null }).Save(ConfigPath));

        Assert.Contains("cloud", refused.Message, StringComparison.Ordinal);
        Assert.Contains("account", refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Validate_AnAzureBlobDestinationWithoutAContainer_IsRefusedByName()
    {
        var refused = Assert.ThrowsExactly<ClientStateException>(() => With(Cloud() with { Container = null }).Save(ConfigPath));

        Assert.Contains("cloud", refused.Message, StringComparison.Ordinal);
        Assert.Contains("container", refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Validate_AnAzureBlobDestinationCarryingAPathOrAFingerprint_IsRefused()
    {
        Assert.ThrowsExactly<ClientStateException>(() => With(Cloud() with { Path = "/mnt/x" }).Save(ConfigPath));
        Assert.ThrowsExactly<ClientStateException>(() =>
            With(Cloud() with { Fingerprint = "mgr7e7euwdpfkggmp4astkz5ia" }).Save(ConfigPath));
    }

    [TestMethod]
    public void Validate_ABucketsFieldsOnAnAzureBlobDestination_AreRefused()
    {
        // A region or an addressing style belongs to the S3 API; on a
        // container it is a misread configuration, not extra information.
        foreach (var misread in new[]
        {
            Cloud() with { Bucket = "family-backups" },
            Cloud() with { Region = "eu-test-1" },
            Cloud() with { Addressing = BucketAddressing.Path },
        })
        {
            Assert.ThrowsExactly<ClientStateException>(() => With(misread).Save(ConfigPath));
        }
    }

    [TestMethod]
    public void Validate_TheAzureBlobFieldsOnAnotherKind_AreRefused()
    {
        var local = new DestinationConfiguration
        {
            Id = new string('1', 32), Name = "usb", Kind = DestinationKind.LocalPath, Path = "/mnt/vault",
        };
        var bucket = new DestinationConfiguration
        {
            Id = new string('2', 32), Name = "bucket", Kind = DestinationKind.S3,
            Endpoint = "https://objects.example.net", Bucket = "family-backups",
        };

        foreach (var misread in new[]
        {
            local with { Account = "fbptestaccount" },
            local with { Container = "family-backups" },
            bucket with { Account = "fbptestaccount" },
            bucket with { Container = "family-backups" },
        })
        {
            Assert.ThrowsExactly<ClientStateException>(() => With(misread).Save(ConfigPath));
        }
    }

    [TestMethod]
    public void Validate_AnAzureBlobDestinationDecliningVerification_IsRefused()
    {
        // The hub reads a sample of it back on every sync, a few small ranges:
        // there is nothing to excuse.
        Assert.ThrowsExactly<ClientStateException>(() =>
            With(Cloud() with { Verification = VerificationPolicy.AcknowledgedNone }).Save(ConfigPath));
    }

    [TestMethod]
    public void AddressDefect_AnEndpointInClear_IsADefect_UnlessTheStoreIsThisMachine()
    {
        Assert.Contains(
            "https", (Cloud() with { Endpoint = "http://fbptestaccount.objects.example.net" }).AddressDefect!, StringComparison.Ordinal);
        Assert.IsNull((Cloud() with { Endpoint = "http://127.0.0.1:10000/fbptestaccount" }).AddressDefect);
        Assert.IsNull((Cloud() with { Endpoint = "http://localhost:10000/fbptestaccount" }).AddressDefect);
    }

    [TestMethod]
    public void AddressDefect_AnEndpointThatIsNotTheAccountsBlobEndpoint_IsADefect()
    {
        foreach (var endpoint in new[]
        {
            "fbptestaccount.objects.example.net", "https://objects.example.net/another-account",
            "https://objects.example.net/fbptestaccount/family-backups", "https://objects.example.net?sv=2024-11-04",
            "ftp://objects.example.net", "https://user:pass@objects.example.net",
        })
        {
            Assert.IsNotNull((Cloud() with { Endpoint = endpoint }).AddressDefect, endpoint);
        }

        Assert.IsNull((Cloud() with { Endpoint = "https://objects.example.net/fbptestaccount" }).AddressDefect);
    }

    [TestMethod]
    public void AddressDefect_AnAccountContainerOrPrefixOutsideTheirGrammar_IsADefect()
    {
        Assert.IsNotNull((Cloud() with { Account = "Has_Capitals" }).AddressDefect);
        Assert.IsNotNull((Cloud() with { Account = "ab" }).AddressDefect);
        Assert.IsNotNull((Cloud() with { Account = new string('a', 25) }).AddressDefect);
        Assert.IsNotNull((Cloud() with { Container = "two--hyphens" }).AddressDefect);
        Assert.IsNotNull((Cloud() with { Container = "-leading" }).AddressDefect);
        Assert.IsNotNull((Cloud() with { Container = "No_Capitals" }).AddressDefect);
        Assert.IsNotNull((Cloud() with { Prefix = "/site-a" }).AddressDefect);
        Assert.IsNotNull((Cloud() with { Prefix = "site-a/.hidden" }).AddressDefect);
        Assert.IsNull((Cloud() with { Prefix = "site-a/host_1" }).AddressDefect);
    }

    [TestMethod]
    public void Load_ASchemaNineFile_ReadsAsTheCurrentSchema()
    {
        File.WriteAllText(ConfigPath, $$"""
            { "schema_version": 9,
              "destinations": [ { "id": "{{new string('1', 32)}}", "name": "usb", "kind": "local-path", "path": "/mnt/vault" } ],
              "backup_sets": [] }
            """);

        Assert.AreEqual(ClientConfiguration.CurrentSchemaVersion, ClientConfiguration.Load(ConfigPath).SchemaVersion);
        Assert.AreEqual(10, ClientConfiguration.CurrentSchemaVersion);
    }
}
