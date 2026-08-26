using Bodu;
using FallbackPlan.Repository.Format.Cbor;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Resources;

namespace FallbackPlan.Repository.Format.Configuration;

/// <summary>One capture root: its label in the snapshot, and the path it had.</summary>
/// <param name="Label">
/// The root's name inside the snapshot tree. Authoritative — it is what the
/// tree is keyed by (ADR-0040), and it is persisted rather than derived.
/// </param>
/// <param name="Path">
/// Where that label pointed on the machine that wrote this. A recovery
/// <em>hint</em> and never an instruction: the rebuilt machine's layout may
/// legitimately differ, so it is presented for confirmation rather than
/// captured from (FR-DR-009).
/// </param>
public sealed record SetRoot(string Label, string Path);

/// <summary>A set's retention policy, as the repository records it.</summary>
/// <remarks>
/// Deliberately its own shape rather than the client configuration's type.
/// <c>Repository.Format</c> is what the standalone recovery tool links, and
/// its dependency closure has to stay small enough to build on a clean machine
/// when everything else has already failed (NFR-PORT-001). The service maps
/// between the two.
/// </remarks>
public sealed record SetRetention
{
    /// <summary>Keep one snapshot per day for this many days.</summary>
    public int? KeepDaily { get; init; }

    /// <summary>Keep one snapshot per week for this many weeks.</summary>
    public int? KeepWeekly { get; init; }

    /// <summary>Keep one snapshot per month for this many months.</summary>
    public int? KeepMonthly { get; init; }

    /// <summary>Keep at least this many snapshots regardless of age.</summary>
    public int? MinGenerations { get; init; }

    /// <summary>How many days retention may be deferred before the gap is warned about.</summary>
    public int? DeferralDays { get; init; }
}

/// <summary>
/// What a backup set was configured to do (specification 11 §5.3): the
/// payload sealed inside a set-configuration object, so a machine rebuilt
/// from nothing can resume doing it (ADR-0047; FR-DR-006).
/// </summary>
/// <remarks>
/// <para>
/// This type is the <em>plaintext</em>. It never reaches a destination in this
/// form — <see cref="SetConfigurationRecord.Envelope"/> carries it sealed to a
/// recipient only the passphrase reproduces.
/// </para>
/// <para>
/// It carries no destination in any form, and that is normative rather than an
/// omission (specification 11 §5.4, FR-DEST-006, FR-DR-007). The repository is
/// held <em>by</em> the destinations; naming them here would put the
/// household's network of peers into bytes those peers hold. Destinations come
/// from the recovery kit, which the user holds and no peer does.
/// </para>
/// </remarks>
public sealed record SetConfiguration
{
    /// <summary>The schema version this implementation writes.</summary>
    public const ushort CurrentSchemaVersion = 1;

    /// <summary>Schema version (key 1).</summary>
    public required ushort SchemaVersion { get; init; }

    /// <summary>The set this describes (key 2), 16 bytes.</summary>
    public required ReadOnlyMemory<byte> BackupSetId { get; init; }

    /// <summary>The set's human-readable name (key 3).</summary>
    public required string SetName { get; init; }

    /// <summary>The capture roots (key 4), in raw-UTF-8 label order.</summary>
    public IReadOnlyList<SetRoot> Roots { get; init; } = [];

    /// <summary>rules-v1 include rules (key 5).</summary>
    public IReadOnlyList<string> IncludeRules { get; init; } = [];

    /// <summary>rules-v1 exclude rules (key 6).</summary>
    public IReadOnlyList<string> ExcludeRules { get; init; } = [];

    /// <summary>The schedule (key 7); null means manual-only.</summary>
    public string? Schedule { get; init; }

    /// <summary>The retention policy (key 8); null means retention deferred.</summary>
    public SetRetention? Retention { get; init; }
}

/// <summary>
/// The outer set-configuration record (specification 11 §5.2): what actually
/// sits at <c>/config/&lt;backup-set-id&gt;/&lt;recorded-at&gt;/&lt;config-id&gt;</c>.
/// </summary>
public sealed record SetConfigurationRecord
{
    /// <summary>The schema version this implementation writes.</summary>
    public const ushort CurrentSchemaVersion = 1;

    /// <summary>An Ed25519 signature is 64 bytes.</summary>
    public const int SignatureLength = 64;

    /// <summary>Schema version (key 1).</summary>
    public required ushort SchemaVersion { get; init; }

    /// <summary>The set this describes (key 2), 16 bytes — equal to the key's own component.</summary>
    public required ReadOnlyMemory<byte> BackupSetId { get; init; }

    /// <summary>When it was recorded (key 3), epoch milliseconds — equal to the key's own component.</summary>
    public required ulong RecordedAt { get; init; }

    /// <summary>
    /// Which signing generation signed key 5 (key 4). Present for the reason a
    /// snapshot manifest carries <c>publication_generation</c>: signing keys
    /// are generational, and a verifier that has to guess which one signed a
    /// record cannot verify it at all.
    /// </summary>
    public required uint SigningGeneration { get; init; }

    /// <summary>The sealed <see cref="SetConfiguration"/> (key 5).</summary>
    public required ReadOnlyMemory<byte> Envelope { get; init; }

    /// <summary>Ed25519 over the canonical encoding of keys 1–5 (key 6).</summary>
    public required ReadOnlyMemory<byte> Signature { get; init; }
}

/// <summary>Encodes and decodes set-configuration objects (specification 11 §5).</summary>
public static class SetConfigurationCodec
{
    private const int MaxRoots = 256;
    private const int MaxRules = 65_536;
    private const int MaxTextBytes = 4096;
    private const int MaxEnvelopeBytes = 1 << 20;
    private const int IdentifierLength = 16;

    /// <summary>
    /// Encodes the bytes a signature covers: the record's keys 1–5, without
    /// key 6. Both the signer and the verifier build the message this way, so
    /// there is one definition of what is signed.
    /// </summary>
    public static byte[] EncodeSignedPrefix(SetConfigurationRecord record)
    {
        ThrowHelper.ThrowIfNull(record);

        var writer = new CanonicalCborWriter();
        WriteSignedPrefix(writer, record);
        return writer.Encode();
    }

    /// <summary>Encodes a complete set-configuration record canonically.</summary>
    public static byte[] Encode(SetConfigurationRecord record)
    {
        ThrowHelper.ThrowIfNull(record);

        if (record.Signature.Length != SetConfigurationRecord.SignatureLength)
        {
            throw new ArgumentException(
                Strings.SetConfigurationCodec_SignatureExactlyBytes, nameof(record));
        }

        var writer = new CanonicalCborWriter();
        WriteSignedPrefix(writer, record, entryCount: 6);
        writer.WriteKey(6);
        writer.WriteByteString(record.Signature.Span);
        writer.WriteEndMap();
        return writer.Encode();
    }

    /// <summary>Decodes and validates a set-configuration record.</summary>
    /// <exception cref="ManifestValidationException">The bytes violate specification 11 §5.2.</exception>
    public static SetConfigurationRecord Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            return DecodeRecordCore(data);
        }
        catch (CborFormatException exception)
        {
            throw new ManifestValidationException(
                Strings.FormatSetConfigurationCodec_RecordNotCanonicalCBOR(exception.Message), exception);
        }
    }

    /// <summary>Encodes the sealed payload canonically.</summary>
    public static byte[] EncodeConfiguration(SetConfiguration configuration)
    {
        ThrowHelper.ThrowIfNull(configuration);

        if (configuration.BackupSetId.Length != IdentifierLength)
        {
            throw new ArgumentException(
                Strings.SetConfigurationCodec_BackupSetIdExactlyBytes, nameof(configuration));
        }

        var writer = new CanonicalCborWriter();
        var entryCount = 6
            + (configuration.Schedule is not null ? 1 : 0)
            + (configuration.Retention is not null ? 1 : 0);
        writer.WriteStartMap(entryCount);

        writer.WriteKey(1);
        writer.WriteUnsignedInteger(configuration.SchemaVersion);
        writer.WriteKey(2);
        writer.WriteByteString(configuration.BackupSetId.Span);
        writer.WriteKey(3);
        writer.WriteTextString(configuration.SetName);

        writer.WriteKey(4);
        writer.WriteStartArray(configuration.Roots.Count);
        foreach (var root in configuration.Roots)
        {
            writer.WriteStartArray(2);
            writer.WriteTextString(root.Label);
            writer.WriteTextString(root.Path);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();

        WriteRules(writer, 5, configuration.IncludeRules);
        WriteRules(writer, 6, configuration.ExcludeRules);

        if (configuration.Schedule is { } schedule)
        {
            writer.WriteKey(7);
            writer.WriteTextString(schedule);
        }

        if (configuration.Retention is { } retention)
        {
            writer.WriteKey(8);
            WriteRetention(writer, retention);
        }

        writer.WriteEndMap();
        return writer.Encode();
    }

    /// <summary>Decodes and validates the sealed payload.</summary>
    /// <exception cref="ManifestValidationException">The bytes violate specification 11 §5.3.</exception>
    public static SetConfiguration DecodeConfiguration(ReadOnlyMemory<byte> data)
    {
        try
        {
            return DecodeConfigurationCore(data);
        }
        catch (CborFormatException exception)
        {
            throw new ManifestValidationException(
                Strings.FormatSetConfigurationCodec_ConfigurationNotCanonicalCBOR(exception.Message), exception);
        }
    }

    private static void WriteSignedPrefix(
        CanonicalCborWriter writer, SetConfigurationRecord record, int? entryCount = null)
    {
        if (record.BackupSetId.Length != IdentifierLength)
        {
            throw new ArgumentException(
                Strings.SetConfigurationCodec_BackupSetIdExactlyBytes, nameof(record));
        }

        writer.WriteStartMap(entryCount ?? 5);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(record.SchemaVersion);
        writer.WriteKey(2);
        writer.WriteByteString(record.BackupSetId.Span);
        writer.WriteKey(3);
        writer.WriteUnsignedInteger(record.RecordedAt);
        writer.WriteKey(4);
        writer.WriteUnsignedInteger(record.SigningGeneration);
        writer.WriteKey(5);
        writer.WriteByteString(record.Envelope.Span);

        if (entryCount is null)
        {
            writer.WriteEndMap();
        }
    }

    private static void WriteRules(CanonicalCborWriter writer, uint key, IReadOnlyList<string> rules)
    {
        writer.WriteKey(key);
        writer.WriteStartArray(rules.Count);
        foreach (var rule in rules)
        {
            writer.WriteTextString(rule);
        }

        writer.WriteEndArray();
    }

    private static void WriteRetention(CanonicalCborWriter writer, SetRetention retention)
    {
        var present = new (uint Key, int? Value)[]
        {
            (1, retention.KeepDaily),
            (2, retention.KeepWeekly),
            (3, retention.KeepMonthly),
            (4, retention.MinGenerations),
            (5, retention.DeferralDays),
        };

        writer.WriteStartMap(present.Count(entry => entry.Value is not null));
        foreach (var (key, value) in present)
        {
            if (value is { } declared)
            {
                writer.WriteKey(key);
                writer.WriteUnsignedInteger((ulong)declared);
            }
        }

        writer.WriteEndMap();
    }

    private static SetConfigurationRecord DecodeRecordCore(ReadOnlyMemory<byte> data)
    {
        var reader = new CanonicalCborReader(data);
        var count = reader.ReadStartMap();

        ushort? schemaVersion = null;
        byte[]? backupSetId = null, envelope = null, signature = null;
        ulong? recordedAt = null;
        uint? signingGeneration = null;

        for (var i = 0; i < count; i++)
        {
            switch (reader.ReadKey())
            {
                case 1:
                    schemaVersion = reader.ReadUInt16();
                    break;
                case 2:
                    backupSetId = reader.ReadFixedByteString(IdentifierLength);
                    break;
                case 3:
                    recordedAt = reader.ReadUnsignedInteger();
                    break;
                case 4:
                    signingGeneration = reader.ReadUInt32();
                    break;
                case 5:
                    envelope = reader.ReadByteString(MaxEnvelopeBytes);
                    break;
                case 6:
                    signature = reader.ReadFixedByteString(SetConfigurationRecord.SignatureLength);
                    break;
                default:
                    throw new ManifestValidationException(Strings.SetConfigurationCodec_RecordCarriesUnknownKey);
            }
        }

        reader.ReadEndMap();
        reader.AssertEndOfDocument();

        if (schemaVersion is null || backupSetId is null || recordedAt is null
            || signingGeneration is null || envelope is null || signature is null)
        {
            throw new ManifestValidationException(Strings.SetConfigurationCodec_RecordIsMissingARequiredKey);
        }

        return new SetConfigurationRecord
        {
            SchemaVersion = schemaVersion.Value,
            BackupSetId = backupSetId,
            RecordedAt = recordedAt.Value,
            SigningGeneration = signingGeneration.Value,
            Envelope = envelope,
            Signature = signature,
        };
    }

    private static SetConfiguration DecodeConfigurationCore(ReadOnlyMemory<byte> data)
    {
        var reader = new CanonicalCborReader(data);
        var count = reader.ReadStartMap();

        ushort? schemaVersion = null;
        byte[]? backupSetId = null;
        string? setName = null, schedule = null;
        List<SetRoot> roots = [];
        List<string> include = [], exclude = [];
        SetRetention? retention = null;

        for (var i = 0; i < count; i++)
        {
            switch (reader.ReadKey())
            {
                case 1:
                    schemaVersion = reader.ReadUInt16();
                    break;
                case 2:
                    backupSetId = reader.ReadFixedByteString(IdentifierLength);
                    break;
                case 3:
                    setName = reader.ReadTextString(MaxTextBytes);
                    break;
                case 4:
                    var rootCount = reader.ReadStartArray(MaxRoots);
                    for (var r = 0; r < rootCount; r++)
                    {
                        var pair = reader.ReadStartArray(maxCount: 2);
                        if (pair != 2)
                        {
                            throw new ManifestValidationException(Strings.SetConfigurationCodec_RootIsNotALabelAndPath);
                        }

                        var label = reader.ReadTextString(MaxTextBytes);
                        var path = reader.ReadTextString(MaxTextBytes);
                        reader.ReadEndArray();
                        roots.Add(new SetRoot(label, path));
                    }

                    reader.ReadEndArray();
                    break;
                case 5:
                    include = ReadRules(reader);
                    break;
                case 6:
                    exclude = ReadRules(reader);
                    break;
                case 7:
                    schedule = reader.ReadTextString(MaxTextBytes);
                    break;
                case 8:
                    retention = ReadRetention(reader);
                    break;
                default:
                    throw new ManifestValidationException(Strings.SetConfigurationCodec_ConfigurationCarriesUnknownKey);
            }
        }

        reader.ReadEndMap();
        reader.AssertEndOfDocument();

        if (schemaVersion is null || backupSetId is null || setName is null)
        {
            throw new ManifestValidationException(Strings.SetConfigurationCodec_ConfigurationIsMissingARequiredKey);
        }

        return new SetConfiguration
        {
            SchemaVersion = schemaVersion.Value,
            BackupSetId = backupSetId,
            SetName = setName,
            Roots = roots,
            IncludeRules = include,
            ExcludeRules = exclude,
            Schedule = schedule,
            Retention = retention,
        };
    }

    private static List<string> ReadRules(CanonicalCborReader reader)
    {
        var count = reader.ReadStartArray(MaxRules);
        List<string> rules = [];
        for (var i = 0; i < count; i++)
        {
            rules.Add(reader.ReadTextString(MaxTextBytes));
        }

        reader.ReadEndArray();
        return rules;
    }

    private static SetRetention ReadRetention(CanonicalCborReader reader)
    {
        var count = reader.ReadStartMap();
        int? daily = null, weekly = null, monthly = null, minimum = null, deferral = null;

        for (var i = 0; i < count; i++)
        {
            var value = 0;
            var key = reader.ReadKey();
            if (key is >= 1 and <= 5)
            {
                var raw = reader.ReadUnsignedInteger();
                if (raw > int.MaxValue)
                {
                    throw new ManifestValidationException(Strings.SetConfigurationCodec_RetentionValueOutOfRange);
                }

                value = (int)raw;
            }

            switch (key)
            {
                case 1:
                    daily = value;
                    break;
                case 2:
                    weekly = value;
                    break;
                case 3:
                    monthly = value;
                    break;
                case 4:
                    minimum = value;
                    break;
                case 5:
                    deferral = value;
                    break;
                default:
                    throw new ManifestValidationException(Strings.SetConfigurationCodec_RetentionCarriesUnknownKey);
            }
        }

        reader.ReadEndMap();
        return new SetRetention
        {
            KeepDaily = daily,
            KeepWeekly = weekly,
            KeepMonthly = monthly,
            MinGenerations = minimum,
            DeferralDays = deferral,
        };
    }
}
