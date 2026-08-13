using System.Text.Json;
using System.Text.Json.Serialization;

namespace FallbackPlan.Application;

/// <summary>
/// The destination kinds a configuration may declare (ADR-0034 §5).
/// <see cref="LocalPath"/> and <see cref="Peer"/> are operational; the cloud
/// kinds are accepted by validation and refused at runtime as a stated
/// incapacity until their providers exist (FR-DEST-005).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DestinationKind>))]
public enum DestinationKind
{
    /// <summary>A directory on a local or removable drive.</summary>
    [JsonStringEnumMemberName("local-path")]
    LocalPath,

    /// <summary>A paired FallbackPlan instance, reached over the peer protocol.</summary>
    [JsonStringEnumMemberName("peer")]
    Peer,

    /// <summary>Amazon S3 or an S3-compatible store — reserved, not yet served.</summary>
    [JsonStringEnumMemberName("s3")]
    S3,

    /// <summary>Azure Blob Storage — reserved, not yet served.</summary>
    [JsonStringEnumMemberName("azure-blob")]
    AzureBlob,

    /// <summary>Dropbox — reserved, not yet served.</summary>
    [JsonStringEnumMemberName("dropbox")]
    Dropbox,
}

/// <summary>
/// Where a destination sits relative to the source (FR-SNP-007, ADR-0018):
/// the four answers to "if this machine is destroyed, does a copy survive?".
/// Ordered — a larger value survives strictly more.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FailureDomain>))]
public enum FailureDomain
{
    /// <summary>On the source's own volume — survives mistakes, nothing physical.</summary>
    [JsonStringEnumMemberName("same-volume")]
    SameVolume = 0,

    /// <summary>A second disk in the source machine — survives disk failure, not theft, fire, or ransomware.</summary>
    [JsonStringEnumMemberName("same-machine")]
    SameMachine = 1,

    /// <summary>A NAS or peer on the same LAN — survives machine loss, not site loss.</summary>
    [JsonStringEnumMemberName("same-site")]
    SameSite = 2,

    /// <summary>An offsite peer or cloud store.</summary>
    [JsonStringEnumMemberName("independent")]
    Independent = 3,
}

/// <summary>
/// One declared destination: a named place backup sets replicate to,
/// referenced from sets by name (FR-DEST-001). Addresses live here and only
/// here — a pairing grant holds a key and terms, never an endpoint
/// (FR-DEST-006) — which is why the exported configuration, while still
/// secret-free, now names who stores the backups and where.
/// </summary>
public sealed record DestinationConfiguration
{
    /// <summary>The destination's 16-byte identity, lowercase hex.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Human-readable name; what sets reference.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>What this destination is.</summary>
    [JsonPropertyName("kind")]
    public required DestinationKind Kind { get; init; }

    /// <summary>The directory, for <see cref="DestinationKind.LocalPath"/>.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>The pinned peer fingerprint, for <see cref="DestinationKind.Peer"/>.</summary>
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }

    /// <summary>The endpoint to dial (host:port), for <see cref="DestinationKind.Peer"/>.</summary>
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; init; }

    /// <summary>
    /// The declared failure domain (FR-SNP-007, ADR-0018 Amendment 2) — only
    /// the user knows where the NAS actually sits. Absent, the default is
    /// derived by kind: a local path by device-identity comparison
    /// (same-volume or same-machine, never further), a peer <c>same-site</c>
    /// — a LAN friend does not survive the house fire, so calling one
    /// independent is a declaration, not an assumption — and a cloud kind
    /// <c>independent</c>.
    /// </summary>
    [JsonPropertyName("failure_domain")]
    public FailureDomain? FailureDomain { get; init; }
}

/// <summary>
/// A retention policy: which snapshots stay protected (architecture 07 §2).
/// Declared per set, optionally overridden per destination reference
/// (FR-GC-010). Selection only — nothing here deletes anything.
/// </summary>
public sealed record RetentionConfiguration
{
    /// <summary>Keep one snapshot per day for this many days.</summary>
    [JsonPropertyName("keep_daily")]
    public int? KeepDaily { get; init; }

    /// <summary>Keep one snapshot per week for this many weeks.</summary>
    [JsonPropertyName("keep_weekly")]
    public int? KeepWeekly { get; init; }

    /// <summary>Keep one snapshot per month for this many months.</summary>
    [JsonPropertyName("keep_monthly")]
    public int? KeepMonthly { get; init; }

    /// <summary>Keep at least this many snapshots regardless of age — the floor the other rules cannot override (07 §2).</summary>
    [JsonPropertyName("min_generations")]
    public int? MinGenerations { get; init; }

    /// <summary>
    /// How many days retention may be deferred while a destination has not
    /// received an expiring snapshot, before the gap is raised as a warning
    /// (FR-GC-009, ADR-0011 Amendment 2). Default 30.
    /// </summary>
    [JsonPropertyName("deferral_days")]
    public int? DeferralDays { get; init; }

    /// <summary>Whether every declared value is positive; a zero rule is a typo, not a policy.</summary>
    [JsonIgnore]
    public bool IsValid =>
        KeepDaily is null or > 0 && KeepWeekly is null or > 0 &&
        KeepMonthly is null or > 0 && MinGenerations is null or > 0 && DeferralDays is null or > 0;
}

/// <summary>
/// A backup set's reference to a declared destination: the plain string
/// <c>"usb-vault"</c> in JSON, or <c>{ "ref": "usb-vault", "retention": … }</c>
/// when the destination follows its own retention policy for this set.
/// </summary>
[JsonConverter(typeof(SetDestinationReferenceConverter))]
public sealed record SetDestinationReference
{
    /// <summary>The name of the declared destination.</summary>
    public required string Ref { get; init; }

    /// <summary>The per-destination retention override, when one applies (FR-GC-010).</summary>
    public RetentionConfiguration? Retention { get; init; }
}

/// <summary>
/// Reads a reference from either form and writes the shortest one that holds
/// the content. Unknown fields are rejected here for the same reason the
/// configuration rejects them everywhere: a typo'd field name silently
/// ignored is behaviour that silently never happens.
/// </summary>
internal sealed class SetDestinationReferenceConverter : JsonConverter<SetDestinationReference>
{
    public override SetDestinationReference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new SetDestinationReference { Ref = reader.GetString()! };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A destination reference is a name or an object with 'ref'.");
        }

        string? name = null;
        RetentionConfiguration? retention = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var property = reader.GetString();
            reader.Read();
            switch (property)
            {
                case "ref":
                    name = reader.GetString();
                    break;
                case "retention":
                    retention = JsonSerializer.Deserialize<RetentionConfiguration>(ref reader, options);
                    break;
                default:
                    throw new JsonException($"A destination reference has no field '{property}'.");
            }
        }

        return name is null
            ? throw new JsonException("A destination reference names no 'ref'.")
            : new SetDestinationReference { Ref = name, Retention = retention };
    }

    public override void Write(Utf8JsonWriter writer, SetDestinationReference value, JsonSerializerOptions options)
    {
        if (value.Retention is null)
        {
            writer.WriteStringValue(value.Ref);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("ref", value.Ref);
        writer.WritePropertyName("retention");
        JsonSerializer.Serialize(writer, value.Retention, options);
        writer.WriteEndObject();
    }
}
