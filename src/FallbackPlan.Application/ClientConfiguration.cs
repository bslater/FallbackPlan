using Bodu;
using System.Text.Json;
using System.Text.Json.Serialization;
using FallbackPlan.Domain;
using FallbackPlan.Application.Resources;

namespace FallbackPlan.Application;

/// <summary>
/// One capture root of a backup set (ADR-0040): the folder, and the label
/// that names it inside multi-root snapshots and anchors its rule subjects.
/// </summary>
public sealed record BackupRootConfiguration
{
    /// <summary>The folder to capture.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>
    /// The root's name inside the snapshot; required (and validated as a
    /// plain NFC path component, unique within the set) when the set has
    /// more than one root. Persisted rather than derived, so adding a
    /// sibling root can never silently re-coordinate this one's paths.
    /// </summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }
}

/// <summary>One configured backup set: its roots, rules, and a name.</summary>
public sealed record BackupSetConfiguration
{
    /// <summary>The set's 16-byte identity, lowercase hex.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Human-readable name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// The schema-2 capture root. Read for migration only —
    /// <see cref="ClientConfiguration.Load"/> rewrites it into
    /// <see cref="Roots"/> and nulls it; it is never written.
    /// </summary>
    [JsonPropertyName("root")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Root { get; init; }

    /// <summary>
    /// The capture roots (ADR-0040): one keeps the pre-multi-root snapshot
    /// shape exactly; several capture into one snapshot under their labels.
    /// </summary>
    [JsonPropertyName("roots")]
    public IReadOnlyList<BackupRootConfiguration> Roots { get; init; } = [];

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
    public const int CurrentSchemaVersion = 3;

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

        configuration = Migrate(configuration, path);
        configuration.Validate(path);
        return configuration;
    }

    /// <summary>
    /// Normalises a schema-2 file in memory (ADR-0040): each set's single
    /// <c>root</c> becomes a one-entry <c>roots</c> list, and the file reads
    /// as schema 3 from here on — the next <see cref="Save"/> writes it so.
    /// A set speaking both forms, or neither, is a misread rather than a
    /// guess.
    /// </summary>
    private static ClientConfiguration Migrate(ClientConfiguration configuration, string path)
    {
        if (configuration.SchemaVersion is not (2 or CurrentSchemaVersion))
        {
            return configuration; // Validate names the version defect
        }

        var sets = new List<BackupSetConfiguration>(configuration.BackupSets.Count);
        foreach (var set in configuration.BackupSets)
        {
            if (set.Root is not null && set.Roots.Count > 0)
            {
                throw new ClientStateException(Strings.FormatClientConfiguration_BackupSetRootAndRoots(set.Name));
            }

            sets.Add(set.Root is { } root
                ? set with { Root = null, Roots = [new BackupRootConfiguration { Path = root }] }
                : set);
        }

        return configuration with { SchemaVersion = CurrentSchemaVersion, BackupSets = sets };
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

            ValidateRoots(set);

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

    /// <summary>
    /// The roots' shape (ADR-0040): at least one, paths unique; several
    /// require labels — plain NFC components a snapshot can name and a
    /// restore can lay out on any filesystem, unique both by raw bytes and
    /// case-insensitively because a case-insensitive restore target would
    /// collapse two labels differing only in case into one folder.
    /// </summary>
    private static void ValidateRoots(BackupSetConfiguration set)
    {
        if (set.Root is not null)
        {
            throw new ClientStateException(Strings.FormatClientConfiguration_BackupSetRootAndRoots(set.Name));
        }

        if (set.Roots.Count == 0)
        {
            throw new ClientStateException(Strings.FormatClientConfiguration_BackupSetRootMustNot(set.Name));
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in set.Roots)
        {
            if (string.IsNullOrWhiteSpace(root.Path))
            {
                throw new ClientStateException(Strings.FormatClientConfiguration_BackupSetRootMustNot(set.Name));
            }

            if (!paths.Add(root.Path))
            {
                throw new ClientStateException(
                    Strings.FormatClientConfiguration_BackupSetRootPathsMustUnique(set.Name, root.Path));
            }
        }

        if (set.Roots.Count == 1)
        {
            return; // a single root's label is ignored by capture; anything goes
        }

        var labels = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        foreach (var root in set.Roots)
        {
            if (root.Label is not { } label || LabelDefect(label) is { } defect)
            {
                throw new ClientStateException(Strings.FormatClientConfiguration_BackupSetLabelInvalid(
                    set.Name, root.Label ?? "(none)", root.Label is null ? "every root of a multi-root set needs one" : LabelDefect(root.Label)!));
            }

            if (!labels.Add(label))
            {
                throw new ClientStateException(
                    Strings.FormatClientConfiguration_BackupSetLabelsMustUnique(set.Name, label));
            }
        }
    }

    /// <summary>What is wrong with a root label, or null when nothing is.</summary>
    public static string? LabelDefect(string label)
    {
        ThrowHelper.ThrowIfNull(label);
        if (string.IsNullOrWhiteSpace(label))
        {
            return "it is empty";
        }

        if (label is "." or "..")
        {
            return "'.' and '..' are path steps, not names";
        }

        if (label.AsSpan().IndexOfAny('/', '\\', ':') >= 0 || label.AsSpan().IndexOfAny('*', '?') >= 0)
        {
            return "it may not contain / \\ : * or ?";
        }

        if (!label.IsNormalized(System.Text.NormalizationForm.FormC))
        {
            return "it must be NFC-normalised, the spelling rules and trees speak";
        }

        if (System.Text.Encoding.UTF8.GetByteCount(label) > 255)
        {
            return "it exceeds 255 UTF-8 bytes, the smallest component limit a restore may meet";
        }

        return null;
    }

    /// <summary>
    /// Fills in the labels a multi-root set needs (ADR-0040): each unlabelled
    /// root gets its folder's leaf name, sanitised to a plain component, with
    /// a numeric suffix where leaves collide. Run once at edit time and
    /// persisted — never derived on read, so a later sibling cannot shift an
    /// existing root's coordinates.
    /// </summary>
    public static IReadOnlyList<BackupRootConfiguration> DeriveLabels(IReadOnlyList<BackupRootConfiguration> roots)
    {
        ThrowHelper.ThrowIfNull(roots);
        if (roots.Count <= 1)
        {
            return roots;
        }

        var taken = new HashSet<string>(
            roots.Where(root => root.Label is not null).Select(root => root.Label!),
            StringComparer.InvariantCultureIgnoreCase);

        var result = new List<BackupRootConfiguration>(roots.Count);
        foreach (var root in roots)
        {
            if (root.Label is not null)
            {
                result.Add(root);
                continue;
            }

            var leaf = root.Path
                .TrimEnd('/', '\\')
                .Split('/', '\\')
                .LastOrDefault(part => part.Length > 0) ?? string.Empty;
            var sanitised = new string([.. leaf.Where(ch => ch is not ('/' or '\\' or ':' or '*' or '?'))])
                .Trim();
            if (sanitised.Length == 0 || sanitised is "." or "..")
            {
                sanitised = "root";
            }

            sanitised = sanitised.Normalize(System.Text.NormalizationForm.FormC);

            var candidate = sanitised;
            for (var suffix = 2; !taken.Add(candidate); suffix++)
            {
                candidate = $"{sanitised}-{suffix}";
            }

            result.Add(root with { Label = candidate });
        }

        return result;
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

        // The acknowledgement exists for destinations that genuinely cannot be
        // challenged — an older peer, a kind with no verification path. A
        // directory this hub owns is neither: the hub reads it back to verify
        // and the check costs sixteen ranges of a few kilobytes. Accepting the
        // excuse here would buy nothing measurable and permanently forfeit the
        // staging trim, so it is refused at load rather than regretted later
        // (FR-VER-006).
        if (requiresPath && !destination.RequiresVerification)
        {
            throw new ClientStateException(
                Strings.FormatClientConfiguration_DestinationCannotDeclineVerification(destination.Name));
        }

        // Zero would read as "never" to anyone writing it and as "every pass"
        // to the arithmetic. Refused rather than guessed at.
        if (destination.DeepVerifyIntervalDays is { } interval && interval <= 0)
        {
            throw new ClientStateException(
                Strings.FormatClientConfiguration_DestinationIntervalMustBePositive(destination.Name));
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
