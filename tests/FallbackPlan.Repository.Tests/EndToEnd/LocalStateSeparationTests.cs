using FallbackPlan.Application;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The three-way local state separation (architecture 11 §3; NFR-REL-007,
/// NFR-SEC-006): configuration,
/// durable local state, and the catalogue are separate files with separate
/// lifecycles — deleting one never harms another, the configuration carries
/// no secret or identity, and losing durable state loses exactly the
/// device's identity and nothing else.
/// </summary>
[TestClass]
public sealed class LocalStateSeparationTests : IDisposable
{
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "fbp-state-tests", Guid.NewGuid().ToString("n"));

    public LocalStateSeparationTests() => Directory.CreateDirectory(_stateDirectory);

    private string ConfigPath => Path.Combine(_stateDirectory, "config.json");

    private string StatePath => Path.Combine(_stateDirectory, "state.json");

    [TestMethod]
    public void LocalState_ReloadedRepeatedly_KeepsTheIdentityItFirstCreated()
    {
        var first = LocalState.LoadOrCreate(_stateDirectory);
        var second = LocalState.LoadOrCreate(_stateDirectory);

        SequenceAssert.AreEqual(first.DeviceId, second.DeviceId);
        SequenceAssert.AreEqual(first.WriterId, second.WriterId);
        SequenceAssert.AreEqual(first.DefaultBackupSetId, second.DefaultBackupSetId);
        Assert.AreEqual(16, first.DeviceId.Length);
        Assert.AreNotEqual(first.DeviceId, first.WriterId);
    }

    [TestMethod]
    public void LocalState_ALegacyIdentityFileExists_AbsorbsItRatherThanReplacingIt()
    {
        var legacyWriter = Enumerable.Repeat((byte)0xAB, 16).ToArray();
        File.WriteAllText(Path.Combine(_stateDirectory, "writer-id"), Convert.ToHexString(legacyWriter));

        var state = LocalState.LoadOrCreate(_stateDirectory);

        // The writer keeps its sequence space; the other identities are new.
        SequenceAssert.AreEqual(legacyWriter, state.WriterId);
        Assert.AreNotEqual(legacyWriter, state.DeviceId);
    }

    [TestMethod]
    public void LocalState_DurableStateIsDeleted_LosesTheIdentityAndNothingElse()
    {
        var original = LocalState.LoadOrCreate(_stateDirectory);
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            BackupSets = [new BackupSetConfiguration
            {
                Id = new string('a', 32), Name = "docs", Root = "/data/docs",
            }],
        }.Save(ConfigPath);

        File.Delete(StatePath);
        var replacement = LocalState.LoadOrCreate(_stateDirectory);

        // Identity loss is real — that is why the file is called durable.
        Assert.AreNotEqual(original.DeviceId, replacement.DeviceId);

        // The configuration survived untouched, its own file, its own life.
        var configuration = ClientConfiguration.Load(ConfigPath);
        Assert.AreEqual("docs", Assert.ContainsSingle(configuration.BackupSets).Name);
    }

    [TestMethod]
    public void ConfigurationExport_AnyConfiguration_ContainsNoIdentityAndNoSecret()
    {
        var state = LocalState.LoadOrCreate(_stateDirectory);
        var configuration = new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            BackupSets = [new BackupSetConfiguration
            {
                Id = new string('b', 32), Name = "home", Root = "/home/user",
                ExcludeRules = ["**/*.tmp"],
            }],
        };
        configuration.Save(ConfigPath);

        var export = ClientConfiguration.Load(ConfigPath).ExportJson();

        // Exportable without secrets (architecture 11 §3): nothing derived
        // from the device, the writer, or any key appears in the export.
        Assert.DoesNotContain(Convert.ToHexString(state.DeviceId), export, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexString(state.WriterId), export, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("**/*.tmp", export, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ConfigurationLoad_AnUnknownField_IsRejectedRatherThanIgnored()
    {
        File.WriteAllText(ConfigPath, """{ "schema_version": 1, "backup_sets": [], "shedule": "daily" }""");

        // A typo'd field silently dropped is a schedule that silently never
        // runs — named-field rejection is the guard (11 §3).
        Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(ConfigPath));
    }

    [TestMethod]
    public void ConfigurationLoad_TheSchemaVersionIsFromTheFuture_IsRefusedRatherThanGuessed()
    {
        File.WriteAllText(ConfigPath, """{ "schema_version": 999, "backup_sets": [] }""");
        Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(ConfigPath));
    }

    [TestMethod]
    public void ConfigurationLoad_ABackupSetCarriesInvalidRules_IsRefused()
    {
        File.WriteAllText(ConfigPath, $$"""
            { "schema_version": 1, "backup_sets": [
              { "id": "{{new string('c', 32)}}", "name": "bad", "root": "/x", "exclude_rules": ["a**b"] } ] }
            """);
        var exception = Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(ConfigPath));
        Assert.Contains("a**b", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void JobHistory_AppendedThenReloaded_Survives()
    {
        var state = LocalState.LoadOrCreate(_stateDirectory);
        state.RecordJob(new JobHistoryEntry
        {
            SnapshotId = new string('d', 32),
            BackupSetId = new string('e', 32),
            StartedAt = 1_722_600_000_000,
            CaptureStatus = 1,
            Files = 42,
            Failures = 0,
        });

        var reloaded = LocalState.LoadOrCreate(_stateDirectory);
        var entry = Assert.ContainsSingle(reloaded.JobHistory);
        Assert.AreEqual(42, entry.Files);
        Assert.AreEqual(1_722_600_000_000ul, entry.StartedAt);
    }

    [TestMethod]
    public void LocalState_DurableCacheAndConfiguration_AreThreeSeparateFiles()
    {
        LocalState.LoadOrCreate(_stateDirectory);
        ClientConfiguration.Default.Save(ConfigPath);

        // The catalogue is a third, separate artefact — its lifecycle
        // (disposable cache) is proven by the rebuild tests; here the claim
        // is separation: three names, no sharing.
        Assert.IsTrue(File.Exists(StatePath));
        Assert.IsTrue(File.Exists(ConfigPath));
        Assert.IsFalse(File.ReadAllText(StatePath).Contains("backup_sets", StringComparison.Ordinal));
        Assert.IsFalse(File.ReadAllText(ConfigPath).Contains("device_id", StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory))
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
    }
}
