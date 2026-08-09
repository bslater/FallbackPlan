using Bodu;
using System.Text.Json;
using System.Text.Json.Serialization;
using FallbackPlan.Domain;

namespace FallbackPlan.Application;

/// <summary>One configured backup set: a root, its rules, and a name.</summary>
public sealed record BackupSetConfiguration
{
    /// <summary>The set's 16-byte identity, lowercase hex.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Human-readable name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>The capture root path.</summary>
    [JsonPropertyName("root")]
    public required string Root { get; init; }

    /// <summary>rules-v1 include rules (specification 06 §7.1).</summary>
    [JsonPropertyName("include_rules")]
    public IReadOnlyList<string> IncludeRules { get; init; } = [];

    /// <summary>rules-v1 exclude rules.</summary>
    [JsonPropertyName("exclude_rules")]
    public IReadOnlyList<string> ExcludeRules { get; init; } = [];

    /// <summary>Schedule placeholder — semantics land with the Agent (phase-1 push 2).</summary>
    [JsonPropertyName("schedule")]
    public string? Schedule { get; init; }
}

/// <summary>
/// The client configuration file (architecture 11 §3): backup sets, roots,
/// rules — schema-versioned, validated on load with unknown fields
/// <b>rejected</b> (a typo'd field name silently ignored is a backup that
/// silently stops matching), and exportable as-is because it contains no
/// secrets by construction: no passphrase, no keys, no identities.
/// </summary>
public sealed record ClientConfiguration
{
    /// <summary>The current schema version; a mismatch is an error, never a guess.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The schema version this file was written under.</summary>
    [JsonPropertyName("schema_version")]
    public required int SchemaVersion { get; init; }

    /// <summary>The configured backup sets.</summary>
    [JsonPropertyName("backup_sets")]
    public IReadOnlyList<BackupSetConfiguration> BackupSets { get; init; } = [];

    /// <summary>A default configuration with no sets.</summary>
    public static ClientConfiguration Default { get; } = new() { SchemaVersion = CurrentSchemaVersion };

    /// <summary>Loads and validates; a missing file is the default configuration.</summary>
    /// <exception cref="ClientStateException">The file is invalid — the message names the defect.</exception>
    public static ClientConfiguration Load(string path)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return Default;
        }

        ClientConfiguration configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<ClientConfiguration>(File.ReadAllText(path), SerializerOptions)
                ?? throw new ClientStateException($"'{path}' holds no configuration object.");
        }
        catch (JsonException exception)
        {
            throw new ClientStateException($"'{path}' is not a valid configuration file: {exception.Message}", exception);
        }

        configuration.Validate(path);
        return configuration;
    }

    /// <summary>Writes the configuration; also the export path — the file holds no secrets.</summary>
    public void Save(string path)
    {
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
    }

    /// <summary>The configuration as indented JSON — the export form; secret-free by construction.</summary>
    public string ExportJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>Finds a set by name; null when unknown.</summary>
    public BackupSetConfiguration? FindSet(string name) =>
        BackupSets.FirstOrDefault(set => string.Equals(set.Name, name, StringComparison.Ordinal));

    private void Validate(string path)
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new ClientStateException(
                $"'{path}' declares schema_version {SchemaVersion}; this build reads version {CurrentSchemaVersion} only.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in BackupSets)
        {
            if (set.Id.Length != 32 || !set.Id.All(Uri.IsHexDigit))
            {
                throw new ClientStateException($"Backup set '{set.Name}': id must be 32 hex digits.");
            }

            if (string.IsNullOrWhiteSpace(set.Name) || !names.Add(set.Name))
            {
                throw new ClientStateException($"Backup set names must be non-empty and unique; '{set.Name}' is not.");
            }

            if (string.IsNullOrWhiteSpace(set.Root))
            {
                throw new ClientStateException($"Backup set '{set.Name}': root must not be empty.");
            }

            // Rule validity is dialect-level and case-independent — a writer
            // MUST refuse invalid rules (06 §7.1), and refusing at load time
            // beats refusing mid-backup.
            if (!PathRuleSet.TryCreate(set.IncludeRules, set.ExcludeRules, caseSensitive: true, out _, out var defects))
            {
                throw new ClientStateException($"Backup set '{set.Name}': {string.Join("; ", defects)}");
            }
        }
    }
}
