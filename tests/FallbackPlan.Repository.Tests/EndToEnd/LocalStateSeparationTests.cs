using FallbackPlan.Application;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The three-way local state separation (architecture 11 §3; NFR-REL-007,
/// NFR-SEC-006): configuration,
/// durable local state, and the catalogue are separate files with separate
/// lifecycles — deleting one never harms another, the configuration carries
/// no secret or identity, and losing durable state loses exactly the
/// device's identity and nothing else.
/// </summary>
public sealed class LocalStateSeparationTests : IDisposable
{
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "fbp-state-tests", Guid.NewGuid().ToString("n"));

    public LocalStateSeparationTests() => Directory.CreateDirectory(_stateDirectory);

    private string ConfigPath => Path.Combine(_stateDirectory, "config.json");

    private string StatePath => Path.Combine(_stateDirectory, "state.json");

    [Fact]
    public void Identities_are_created_once_and_stable_across_reloads()
    {
        var first = LocalState.LoadOrCreate(_stateDirectory);
        var second = LocalState.LoadOrCreate(_stateDirectory);

        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.Equal(first.WriterId, second.WriterId);
        Assert.Equal(first.DefaultBackupSetId, second.DefaultBackupSetId);
        Assert.Equal(16, first.DeviceId.Length);
        Assert.NotEqual(first.DeviceId, first.WriterId);
    }

    [Fact]
    public void Legacy_phase_0_identity_files_are_absorbed_not_replaced()
    {
        var legacyWriter = Enumerable.Repeat((byte)0xAB, 16).ToArray();
        File.WriteAllText(Path.Combine(_stateDirectory, "writer-id"), Convert.ToHexString(legacyWriter));

        var state = LocalState.LoadOrCreate(_stateDirectory);

        // The writer keeps its sequence space; the other identities are new.
        Assert.Equal(legacyWriter, state.WriterId);
        Assert.NotEqual(legacyWriter, state.DeviceId);
    }

    [Fact]
    public void Deleting_durable_state_loses_identity_but_touches_nothing_else()
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
        Assert.NotEqual(original.DeviceId, replacement.DeviceId);

        // The configuration survived untouched, its own file, its own life.
        var configuration = ClientConfiguration.Load(ConfigPath);
        Assert.Equal("docs", Assert.Single(configuration.BackupSets).Name);
    }

    [Fact]
    public void The_configuration_export_contains_no_identity_and_no_secret()
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

    [Fact]
    public void Unknown_configuration_fields_are_rejected_not_ignored()
    {
        File.WriteAllText(ConfigPath, """{ "schema_version": 1, "backup_sets": [], "shedule": "daily" }""");

        // A typo'd field silently dropped is a schedule that silently never
        // runs — named-field rejection is the guard (11 §3).
        Assert.Throws<ClientStateException>(() => ClientConfiguration.Load(ConfigPath));
    }

    [Fact]
    public void A_future_schema_version_is_refused_not_guessed()
    {
        File.WriteAllText(ConfigPath, """{ "schema_version": 999, "backup_sets": [] }""");
        Assert.Throws<ClientStateException>(() => ClientConfiguration.Load(ConfigPath));
    }

    [Fact]
    public void Invalid_rules_are_refused_at_configuration_load()
    {
        File.WriteAllText(ConfigPath, $$"""
            { "schema_version": 1, "backup_sets": [
              { "id": "{{new string('c', 32)}}", "name": "bad", "root": "/x", "exclude_rules": ["a**b"] } ] }
            """);
        var exception = Assert.Throws<ClientStateException>(() => ClientConfiguration.Load(ConfigPath));
        Assert.Contains("a**b", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Job_history_appends_and_survives_reload()
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
        var entry = Assert.Single(reloaded.JobHistory);
        Assert.Equal(42, entry.Files);
        Assert.Equal(1_722_600_000_000ul, entry.StartedAt);
    }

    [Fact]
    public void The_three_stores_are_three_files()
    {
        LocalState.LoadOrCreate(_stateDirectory);
        ClientConfiguration.Default.Save(ConfigPath);

        // The catalogue is a third, separate artefact — its lifecycle
        // (disposable cache) is proven by the rebuild tests; here the claim
        // is separation: three names, no sharing.
        Assert.True(File.Exists(StatePath));
        Assert.True(File.Exists(ConfigPath));
        Assert.False(File.ReadAllText(StatePath).Contains("backup_sets", StringComparison.Ordinal));
        Assert.False(File.ReadAllText(ConfigPath).Contains("device_id", StringComparison.Ordinal));
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
