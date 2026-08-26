using Bodu;
using System.Text.Json;
using System.Text.Json.Serialization;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    /// The set's priority (ADR-0047): among waiting backups of the same
    /// initiation, higher runs first. Absent means 0. It never outranks a
    /// person — user-initiated work sorts ahead of any priority.
    /// </summary>
    [JsonPropertyName("priority")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Priority { get; init; }

    /// <summary>
    /// The destinations this set replicates to, by declared name — at least
    /// one, none of which has to be local (FR-DEST-001, ADR-0034).
    /// </summary>
    [JsonPropertyName("destinations")]
    public IReadOnlyList<SetDestinationReference> Destinations { get; init; } = [];
}

/// <summary>
/// What this installation logs, and how much of it it keeps (ADR-0043 §6,
/// FR-SVC-010) — the third of the four places a level can be named, after the
/// <c>--log-level</c> flag and <c>FALLBACKPLAN_LOG_LEVEL</c> and before the
/// host's own fallback.
/// </summary>
/// <remarks>
/// <para>
/// This is where a level is set once for the machine rather than for one
/// invocation. A flag lasts as long as the command; a variable lasts as long
/// as the shell; this survives a restart, which is what an installed service
/// needs — it is started by the operating system, and nobody is there to pass
/// it anything.
/// </para>
/// <para>
/// Every field is optional and every absent field means "what the host would
/// have chosen anyway", so a file that says nothing about logging behaves
/// exactly as it did before this object existed. Levels are held as the names
/// a person writes rather than as parsed values: this assembly cannot see
/// <c>FallbackPlan.Diagnostics</c> (ADR-0043 §1 keeps the concrete logging
/// package in that one project), and the names are validated here against the
/// same vocabulary the flag uses.
/// </para>
/// </remarks>
public sealed record LoggingConfiguration
{
    /// <summary>The smallest file size worth keeping — below this a file rolls before it holds a session.</summary>
    public const long MinimumFileBytes = 64 * 1024;

    /// <summary>The smallest ring worth reading — below this a client misses records between two reads.</summary>
    public const int MinimumRingCapacity = 16;

    /// <summary>The level for any category with no more specific rule, by name; null leaves it to the host.</summary>
    [JsonPropertyName("level")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Level { get; init; }

    /// <summary>
    /// Per-category levels by name, matched by longest declared prefix — so
    /// <c>FallbackPlan.Repository</c> covers <c>FallbackPlan.Repository.Packing</c>
    /// unless that names itself. This is how "quiet everywhere, verbose in the
    /// one place that is misbehaving" is said.
    /// </summary>
    [JsonPropertyName("categories")]
    public IReadOnlyDictionary<string, string> Categories { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>How many rolled log files to keep, newest first; null leaves it to the host.</summary>
    [JsonPropertyName("retain_files")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RetainFiles { get; init; }

    /// <summary>How large one log file may grow before it rolls; null leaves it to the host.</summary>
    [JsonPropertyName("max_file_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MaxFileBytes { get; init; }

    /// <summary>How many records the in-memory ring holds for clients to read; null leaves it to the host.</summary>
    [JsonPropertyName("ring_capacity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RingCapacity { get; init; }

    /// <summary>
    /// The default level this file declares, parsed, or null when it declares
    /// none. Only ever called after <see cref="ClientConfiguration.Load"/> has
    /// validated the file, so an unparseable name here cannot happen.
    /// </summary>
    public LogLevel? DefaultLevel() =>
        Level is { Length: > 0 } named && LogLevels.TryParse(named, out var level) ? level : null;

    /// <summary>The per-category levels this file declares, parsed.</summary>
    public IReadOnlyDictionary<string, LogLevel> CategoryLevels()
    {
        var levels = new Dictionary<string, LogLevel>(StringComparer.Ordinal);
        foreach (var (category, named) in Categories)
        {
            if (LogLevels.TryParse(named, out var level))
            {
                levels[category] = level;
            }
        }

        return levels;
    }

    internal void Validate()
    {
        if (Level is { Length: > 0 } && !LogLevels.TryParse(Level, out _))
        {
            throw new ClientStateException(
                Strings.FormatClientConfiguration_LoggingLevelUnknown(Level, LogLevels.NameList()));
        }

        foreach (var (category, named) in Categories)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                throw new ClientStateException(Strings.ClientConfiguration_LoggingCategoryMustNotBeEmpty);
            }

            if (!LogLevels.TryParse(named, out _))
            {
                throw new ClientStateException(
                    Strings.FormatClientConfiguration_LoggingCategoryLevelUnknown(
                        category, named, LogLevels.NameList()));
            }
        }

        // Refused rather than clamped. A number somebody typed and a number the
        // service silently replaced are the same file and different behaviour,
        // and the difference only shows up when the log is needed.
        if (RetainFiles is { } retain && retain < 1)
        {
            throw new ClientStateException(
                Strings.FormatClientConfiguration_LoggingRetainFilesOutOfRange(retain));
        }

        if (MaxFileBytes is { } bytes && bytes < MinimumFileBytes)
        {
            throw new ClientStateException(
                Strings.FormatClientConfiguration_LoggingMaxFileBytesOutOfRange(bytes, MinimumFileBytes));
        }

        if (RingCapacity is { } capacity && capacity < MinimumRingCapacity)
        {
            throw new ClientStateException(
                Strings.FormatClientConfiguration_LoggingRingCapacityOutOfRange(capacity, MinimumRingCapacity));
        }
    }
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
    public const int CurrentSchemaVersion = 5;

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

    /// <summary>
    /// What this installation logs (ADR-0043 §6); null means every logging
    /// decision is the host's, which is what a file written before schema 4
    /// says by simply not mentioning it.
    /// </summary>
    [JsonPropertyName("logging")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LoggingConfiguration? Logging { get; init; }

    /// <summary>
    /// How many backups may run at once (ADR-0047), 1..5; absent means 2.
    /// Read when the service starts — a change applies at the next start,
    /// which the pool's construction states rather than hides.
    /// </summary>
    [JsonPropertyName("max_concurrent_backups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxConcurrentBackups { get; init; }

    /// <summary>The pool width this configuration means, defaults applied.</summary>
    [JsonIgnore]
    public int EffectiveMaxConcurrentBackups => MaxConcurrentBackups ?? 2;

    /// <summary>A default configuration with no sets.</summary>
    public static ClientConfiguration Default { get; } = new() { SchemaVersion = CurrentSchemaVersion };

    /// <summary>Loads and validates; a missing file is the default configuration.</summary>
    /// <param name="path">The configuration file.</param>
    /// <param name="logger">Where the load, a migration or a refusal is recorded.</param>
    /// <exception cref="ClientStateException">The file is invalid — the message names the defect.</exception>
    public static ClientConfiguration Load(string path, ILogger? logger = null)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(path);
        var log = logger ?? NullLogger.Instance;

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
            Log.ConfigurationRefused(log, "unreadable", exception.Message);
            throw new ClientStateException(Strings.FormatClientConfiguration_NotValidConfigurationFile(path, exception.Message), exception);
        }

        var declared = configuration.SchemaVersion;
        configuration = Migrate(configuration, path);

        if (declared != configuration.SchemaVersion)
        {
            Log.ConfigurationMigrated(log, declared, configuration.SchemaVersion);
        }

        try
        {
            configuration.Validate(path);
        }
        catch (ClientStateException refusal)
        {
            // Logged where it is decided rather than left to each caller. A
            // configuration the service is about to refuse is one of the most
            // useful things a log can hold, and it is the same refusal either
            // way — this only records it.
            Log.ConfigurationRefused(log, "invalid", refusal.Message);
            throw;
        }

        Log.ConfigurationLoaded(
            log, configuration.SchemaVersion, configuration.BackupSets.Count, configuration.Destinations.Count);
        return configuration;
    }

    /// <summary>
    /// Brings an older file up to the current schema in memory; the next
    /// <see cref="Save"/> writes it so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>2 → 3</b> (ADR-0040): each set's single <c>root</c> becomes a
    /// one-entry <c>roots</c> list. A set speaking both forms, or neither, is
    /// a misread rather than a guess.
    /// </para>
    /// <para>
    /// <b>3 → 4</b> (ADR-0043): the file gains an optional <c>logging</c>
    /// object. There is nothing to move — a schema-3 file simply has no
    /// <c>logging</c> key, and the property stays null, which is precisely
    /// "leave every logging decision to the host". The version still has to
    /// rise, because <see cref="JsonUnmappedMemberHandling.Disallow"/> means a
    /// schema-4 file handed to an older build is a real compatibility event
    /// and must be refused by name rather than half-read.
    /// </para>
    /// </remarks>
    private static ClientConfiguration Migrate(ClientConfiguration configuration, string path)
    {
        if (configuration.SchemaVersion is not (2 or 3 or 4 or CurrentSchemaVersion))
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
                ? Strings.FormatClientConfiguration_SchemaVersion1NeedsDestinations(path, CurrentSchemaVersion)
                : Strings.FormatClientConfiguration_DeclaresSchemaVersionBuildReads(path, SchemaVersion, CurrentSchemaVersion));
        }

        Logging?.Validate();

        // 1..5 (ADR-0047): zero would be a service that never backs up
        // pretending to be configured, and past a handful the pool's workers
        // contend for the same disk and mostly make each other slower.
        if (MaxConcurrentBackups is { } concurrency and (< 1 or > 5))
        {
            throw new ClientStateException(
                Strings.FormatClientConfiguration_ConcurrencyOutOfRange(path, concurrency));
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
