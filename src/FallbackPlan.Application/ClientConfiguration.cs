using Bodu;
using System.Text.Json;
using System.Text.Json.Serialization;
using FallbackPlan.Domain;
using FallbackPlan.Application.Resources;

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

    /// <summary>The set's schedule, evaluated by the scheduler's due-ness pass (ADR-0027 §1); null means manual-only.</summary>
    [JsonPropertyName("schedule")]
    public string? Schedule { get; init; }

    /// <summary>The set's retention policy; null defers retention entirely (architecture 07 §2).</summary>
    [JsonPropertyName("retention")]
    public RetentionConfiguration? Retention { get; init; }

    /// <summary>
    /// The destinations this set replicates to, by declared name — at least
    /// one, none of which has to be local (FR-DEST-001, ADR-0034).
    /// </summary>
    [JsonPropertyName("destinations")]
    public IReadOnlyList<SetDestinationReference> Destinations { get; init; } = [];
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
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The schema version this file was written under.</summary>
    [JsonPropertyName("schema_version")]
    public required int SchemaVersion { get; init; }

    /// <summary>The declared destinations — the hub's address book (ADR-0034 §5, FR-DEST-006).</summary>
    [JsonPropertyName("destinations")]
    public IReadOnlyList<DestinationConfiguration> Destinations { get; init; } = [];

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
                ?? throw new ClientStateException(Strings.FormatClientConfiguration_HoldsNoConfigurationObject(path));
        }
        catch (JsonException exception)
        {
            throw new ClientStateException(Strings.FormatClientConfiguration_NotValidConfigurationFile(path, exception.Message), exception);
        }

        configuration.Validate(path);
        return configuration;
    }

    /// <summary>
    /// Validates then writes; an invalid configuration is refused here rather
    /// than discovered by the scheduler at two in the morning. Also the export
    /// path — the file holds no secrets, though it now names who stores the
    /// backups and where (FR-DEST-006).
    /// </summary>
    /// <exception cref="ClientStateException">The configuration is invalid — the message names the defect.</exception>
    public void Save(string path)
    {
        Validate(path);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
    }

    /// <summary>The configuration as indented JSON — the export form; secret-free by construction.</summary>
    public string ExportJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>Finds a set by name; null when unknown.</summary>
    public BackupSetConfiguration? FindSet(string name) =>
        BackupSets.FirstOrDefault(set => string.Equals(set.Name, name, StringComparison.Ordinal));

    /// <summary>Finds a declared destination by name; null when unknown.</summary>
    public DestinationConfiguration? FindDestination(string name) =>
        Destinations.FirstOrDefault(destination => string.Equals(destination.Name, name, StringComparison.Ordinal));

    private void Validate(string path)
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            // Version 1 gets the migration, not just the refusal: it is the
            // schema every pre-hub-and-spoke install wrote (ADR-0034).
            throw new ClientStateException(SchemaVersion == 1
                ? Strings.FormatClientConfiguration_SchemaVersion1NeedsDestinations(path)
                : Strings.FormatClientConfiguration_DeclaresSchemaVersionBuildReads(path, SchemaVersion, CurrentSchemaVersion));
        }

        var destinationNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var destination in Destinations)
        {
            ValidateDestination(destination, destinationNames);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in BackupSets)
        {
            if (set.Id.Length != 32 || !set.Id.All(Uri.IsHexDigit))
            {
                throw new ClientStateException(Strings.FormatClientConfiguration_BackupSetIdMustHex(set.Name));
            }

            if (string.IsNullOrWhiteSpace(set.Name) || !names.Add(set.Name))
            {
                throw new ClientStateException(Strings.FormatClientConfiguration_BackupSetNamesMustNon(set.Name));
            }

            if (string.IsNullOrWhiteSpace(set.Root))
            {
                throw new ClientStateException(Strings.FormatClientConfiguration_BackupSetRootMustNot(set.Name));
            }

            // Rule validity is dialect-level and case-independent — a writer
            // MUST refuse invalid rules (06 §7.1), and refusing at load time
            // beats refusing mid-backup.
            if (!PathRuleSet.TryCreate(set.IncludeRules, set.ExcludeRules, caseSensitive: true, out _, out var defects))
            {
                throw new ClientStateException(Strings.FormatClientConfiguration_BackupSet(set.Name, string.Join("; ", defects)));
            }

            ValidateSetDestinations(set, destinationNames);
        }
    }

    private static void ValidateDestination(DestinationConfiguration destination, HashSet<string> names)
    {
        if (destination.Id.Length != 32 || !destination.Id.All(Uri.IsHexDigit))
        {
            throw new ClientStateException(Strings.FormatClientConfiguration_DestinationIdMustHex(destination.Name));
        }

        if (string.IsNullOrWhiteSpace(destination.Name) || !names.Add(destination.Name))
        {
            throw new ClientStateException(Strings.FormatClientConfiguration_DestinationNamesMustNon(destination.Name));
        }

        // Each kind requires its own fields and refuses the others': a peer
        // carrying a path is a misread configuration, not extra information.
        var (requiresPath, requiresPeer) = destination.Kind switch
        {
            DestinationKind.LocalPath => (true, false),
            DestinationKind.Peer => (false, true),
            _ => (false, false),
        };

        if (requiresPath && string.IsNullOrWhiteSpace(destination.Path))
        {
            throw new ClientStateException(Strings.FormatClientConfiguration_DestinationNeedsPath(destination.Name));
        }

        if (requiresPeer && (string.IsNullOrWhiteSpace(destination.Fingerprint) || string.IsNullOrWhiteSpace(destination.Endpoint)))
        {
            throw new ClientStateException(Strings.FormatClientConfiguration_DestinationNeedsPeerIdentity(destination.Name));
        }

        if (!requiresPath && destination.Path is not null)
        {
            throw new ClientStateException(Strings.FormatClientConfiguration_DestinationFieldNotForKind(destination.Name, "path"));
        }

        if (!requiresPeer && (destination.Fingerprint is not null || destination.Endpoint is not null))
        {
            throw new ClientStateException(Strings.FormatClientConfiguration_DestinationFieldNotForKind(destination.Name, "fingerprint/endpoint"));
        }
    }

    private static void ValidateSetDestinations(BackupSetConfiguration set, HashSet<string> destinationNames)
    {
        // At least one destination, none of which has to be local — a set
        // with none is unprotectable and refused up front (FR-DEST-001).
        if (set.Destinations.Count == 0)
        {
            throw new ClientStateException(Strings.FormatClientConfiguration_BackupSetNeedsDestination(set.Name));
        }

        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in set.Destinations)
        {
            if (!destinationNames.Contains(reference.Ref))
            {
                throw new ClientStateException(Strings.FormatClientConfiguration_BackupSetUnknownDestination(set.Name, reference.Ref));
            }

            if (!references.Add(reference.Ref))
            {
                throw new ClientStateException(Strings.FormatClientConfiguration_BackupSetDuplicateDestination(set.Name, reference.Ref));
            }

            if (reference.Retention is { IsValid: false })
            {
                throw new ClientStateException(Strings.FormatClientConfiguration_RetentionMustBePositive(set.Name, reference.Ref));
            }
        }

        if (set.Retention is { IsValid: false })
        {
            throw new ClientStateException(Strings.FormatClientConfiguration_RetentionMustBePositive(set.Name, set.Name));
        }
    }
}
