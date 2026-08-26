using FallbackPlan.Application;
using FallbackPlan.Domain;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// Schema 4's <c>logging</c> object (ADR-0043 §6, FR-SVC-010): where a level is
/// set once for a machine instead of once per invocation.
/// </summary>
/// <remarks>
/// The two things worth pinning here are that a schema-3 file keeps working
/// untouched, and that a level nobody recognises is refused by name. A backup
/// service that silently logs at the wrong level after a typo is one that
/// stays quiet on the night somebody needs to read it.
/// </remarks>
[TestClass]
public sealed class LoggingConfigurationTests
{
    private string _directory = null!;

    private string ConfigPath => Path.Combine(_directory, "config.json");

    [TestInitialize]
    public void Initialize() => _directory = Directory.CreateTempSubdirectory("fp-logcfg-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private void Write(string logging) => File.WriteAllText(ConfigPath, $$"""
        { "schema_version": 4, "destinations": [], "backup_sets": [], "logging": {{logging}} }
        """);

    [TestMethod]
    public void Load_ASchema3FileWithNoLoggingObject_MigratesAndLeavesEveryDecisionToTheHost()
    {
        // The whole of the 3 to 4 migration: nothing moves, because a schema-3
        // file simply never mentioned logging.
        File.WriteAllText(ConfigPath, """
            { "schema_version": 3, "destinations": [], "backup_sets": [] }
            """);

        var loaded = ClientConfiguration.Load(ConfigPath);

        Assert.AreEqual(ClientConfiguration.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.IsNull(loaded.Logging);
    }

    [TestMethod]
    public void Save_WithNoLoggingObject_WritesNoLoggingKeyAtAll()
    {
        // A file that says nothing about logging must stay a file that says
        // nothing about logging: writing an object full of defaults would turn
        // every host default into a decision frozen in someone's config.
        new ClientConfiguration { SchemaVersion = ClientConfiguration.CurrentSchemaVersion }.Save(ConfigPath);

        Assert.DoesNotContain("logging", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
    }

    [TestMethod]
    public void Load_ALevelAndCategoryOverrides_ParseToLevels()
    {
        Write("""
            { "level": "debug",
              "categories": { "FallbackPlan.Repository": "trace", "FallbackPlan.Api": "warning" } }
            """);

        var logging = ClientConfiguration.Load(ConfigPath).Logging!;

        Assert.AreEqual(LogLevel.Debug, logging.DefaultLevel());
        var categories = logging.CategoryLevels();
        Assert.AreEqual(LogLevel.Trace, categories["FallbackPlan.Repository"]);
        Assert.AreEqual(LogLevel.Warning, categories["FallbackPlan.Api"]);
    }

    [TestMethod]
    public void Load_TheSpellingsAPersonTypes_AreAccepted()
    {
        // The file and the flag take the same vocabulary, which is the reason
        // it lives in Domain rather than twice.
        foreach (var (written, expected) in new (string, LogLevel)[]
                 {
                     ("verbose", LogLevel.Trace),
                     ("info", LogLevel.Information),
                     ("WARN", LogLevel.Warning),
                     ("Fatal", LogLevel.Critical),
                     ("off", LogLevel.None),
                 })
        {
            Write($$"""{ "level": "{{written}}" }""");
            Assert.AreEqual(expected, ClientConfiguration.Load(ConfigPath).Logging!.DefaultLevel(), written);
        }
    }

    [TestMethod]
    public void Load_ALevelNobodyRecognises_IsRefusedNamingItAndTheOnesThatExist()
    {
        Write("""{ "level": "chatty" }""");

        var refusal = Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(ConfigPath));

        StringAssert.Contains(refusal.Message, "chatty");
        foreach (var name in FallbackPlan.Domain.Diagnostics.LogLevels.Names)
        {
            StringAssert.Contains(refusal.Message, name);
        }
    }

    [TestMethod]
    public void Load_ACategoryAskingForANonexistentLevel_IsRefusedNamingTheCategory()
    {
        Write("""{ "categories": { "FallbackPlan.Repository": "shouty" } }""");

        var refusal = Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(ConfigPath));

        StringAssert.Contains(refusal.Message, "FallbackPlan.Repository");
        StringAssert.Contains(refusal.Message, "shouty");
    }

    [TestMethod]
    public void Load_SizesAndCountsBelowTheirFloors_AreRefusedRatherThanClamped()
    {
        // Refused, not clamped: a number somebody typed and a number the
        // service silently replaced are the same file and different behaviour.
        Write("""{ "retain_files": 0 }""");
        StringAssert.Contains(
            Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(ConfigPath)).Message,
            "retain_files");

        Write("""{ "max_file_bytes": 1024 }""");
        StringAssert.Contains(
            Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(ConfigPath)).Message,
            "max_file_bytes");

        Write("""{ "ring_capacity": 4 }""");
        StringAssert.Contains(
            Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(ConfigPath)).Message,
            "ring_capacity");
    }

    [TestMethod]
    public void Load_SizesAndCountsAtTheirFloors_AreAccepted()
    {
        Write($$"""
            { "retain_files": 1,
              "max_file_bytes": {{LoggingConfiguration.MinimumFileBytes}},
              "ring_capacity": {{LoggingConfiguration.MinimumRingCapacity}} }
            """);

        var logging = ClientConfiguration.Load(ConfigPath).Logging!;

        Assert.AreEqual(1, logging.RetainFiles);
        Assert.AreEqual(LoggingConfiguration.MinimumFileBytes, logging.MaxFileBytes);
        Assert.AreEqual(LoggingConfiguration.MinimumRingCapacity, logging.RingCapacity);
    }

    [TestMethod]
    public void SaveThenLoad_AFullLoggingObject_RoundTrips()
    {
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Logging = new LoggingConfiguration
            {
                Level = "warning",
                Categories = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["FallbackPlan.Repository"] = "debug",
                },
                RetainFiles = 9,
                MaxFileBytes = 2 * 1024 * 1024,
                RingCapacity = 512,
            },
        }.Save(ConfigPath);

        var logging = ClientConfiguration.Load(ConfigPath).Logging!;

        Assert.AreEqual(LogLevel.Warning, logging.DefaultLevel());
        Assert.AreEqual(LogLevel.Debug, logging.CategoryLevels()["FallbackPlan.Repository"]);
        Assert.AreEqual(9, logging.RetainFiles);
        Assert.AreEqual(2 * 1024 * 1024, logging.MaxFileBytes);
        Assert.AreEqual(512, logging.RingCapacity);
    }

    [TestMethod]
    public void Load_AnUnknownFieldInsideLogging_IsRefusedLikeAnyOther()
    {
        // UnmappedMemberHandling.Disallow reaches into the nested object too:
        // a typo'd field silently ignored is a setting somebody believes is in
        // force and is not.
        Write("""{ "level": "debug", "levl": "trace" }""");

        Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(ConfigPath));
    }
}
