using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Cbor;

namespace FallbackPlan.Repository.Index.Journal;

/// <summary>
/// Encodes and decodes journal records (specification 08 §2–§6): keys 1–5
/// signed with the 06 §6.1 two-pass construction, the signature at key 6,
/// and each kind's payload map validated against its exact shape.
/// </summary>
public static class JournalRecordCodec
{
    /// <summary>Encodes keys 1–5 — the exact bytes the signature covers.</summary>
    public static byte[] EncodeForSigning(JournalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);

        var writer = new CanonicalCborWriter();
        WriteBody(writer, record, signature: null);
        return writer.Encode();
    }

    /// <summary>Encodes the stored form: keys 1–5 plus the signature at key 6.</summary>
    public static byte[] Encode(JournalRecord record, ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);

        if (signature.Length != 64)
        {
            throw new IndexFormatException("A journal signature is exactly 64 bytes (specification 08 §2).");
        }

        var writer = new CanonicalCborWriter();
        WriteBody(writer, record, signature.ToArray());
        return writer.Encode();
    }

    /// <summary>Decodes a stored journal record and rebuilds the signed prefix.</summary>
    /// <exception cref="IndexFormatException">The bytes violate specification 08 — a damage finding for a reader; for a collector, an unparseable intent is LIVE (08 §8).</exception>
    public static DecodedJournalRecord Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            return DecodeCore(data);
        }
        catch (CborFormatException exception)
        {
            throw new IndexFormatException($"The journal record is not canonical CBOR: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Verifies a journal record by generation descent (08 §2 key 6): the
    /// record carries no generation field, so the signing key of each
    /// generation from <paramref name="maxGeneration"/> down to zero is
    /// tried, and the first that verifies wins. Generations are few,
    /// monotonic, and enumerable from the key bundle.
    /// </summary>
    /// <returns>The generation that verified, or null when none did.</returns>
    public static uint? VerifyByDescent(
        ReadOnlySpan<byte> signedBytes,
        ReadOnlySpan<byte> signature,
        KeyHierarchy hierarchy,
        uint maxGeneration)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);

        for (var generation = (long)maxGeneration; generation >= 0; generation--)
        {
            using var signer = RepositorySigner.Create(hierarchy, new KeyGeneration((uint)generation));
            if (signer.Verify(signedBytes, signature))
            {
                return (uint)generation;
            }
        }

        return null;
    }

    private static void Validate(JournalRecord record)
    {
        var matches = record.Kind switch
        {
            JournalRecordKind.WriteIntent => record.Payload is JournalPayload.WriteIntent,
            JournalRecordKind.IntentExtension => record.Payload is JournalPayload.IntentExtension,
            JournalRecordKind.IntentRetirement => record.Payload is JournalPayload.IntentRetirement,
            JournalRecordKind.Audit => record.Payload is JournalPayload.Audit,
            _ => false,
        };

        if (!matches)
        {
            throw new IndexFormatException($"record_kind {record.Kind} does not match its payload shape (specification 08 §2).");
        }

        if (record.Payload is JournalPayload.WriteIntent intent && intent.BackupSetId.Length != 16)
        {
            throw new IndexFormatException("backup_set_id is exactly 16 bytes (specification 08 §3).");
        }
    }

    private static void WriteBody(CanonicalCborWriter writer, JournalRecord record, byte[]? signature)
    {
        writer.WriteStartMap(signature is null ? 5 : 6);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger((ushort)record.Kind);
        writer.WriteKey(2);
        writer.WriteByteString(record.WriterId.ToArray());
        writer.WriteKey(3);
        writer.WriteUnsignedInteger(record.Sequence);
        writer.WriteKey(4);
        writer.WriteUnsignedInteger(record.IssuedAt);
        writer.WriteKey(5);
        WritePayload(writer, record.Payload);

        if (signature is not null)
        {
            writer.WriteKey(6);
            writer.WriteByteString(signature);
        }

        writer.WriteEndMap();
    }

    private static void WritePayload(CanonicalCborWriter writer, JournalPayload payload)
    {
        switch (payload)
        {
            case JournalPayload.WriteIntent intent:
                writer.WriteStartMap(5);
                writer.WriteKey(1);
                writer.WriteByteString(intent.BackupSetId.Span);
                writer.WriteKey(2);
                writer.WriteStartArray(intent.IntendedBlobIds.Count);
                foreach (var blobId in intent.IntendedBlobIds)
                {
                    writer.WriteByteString(blobId.ToArray());
                }

                writer.WriteEndArray();
                writer.WriteKey(3);
                writer.WriteUnsignedInteger(intent.DeclaredMaxDurationMs);
                writer.WriteKey(4);
                writer.WriteUnsignedInteger(intent.ExpiryGeneration);
                writer.WriteKey(5);
                writer.WriteUnsignedInteger((ushort)intent.Purpose);
                writer.WriteEndMap();
                break;

            case JournalPayload.IntentExtension extension:
                writer.WriteStartMap(3);
                writer.WriteKey(1);
                writer.WriteUnsignedInteger(extension.ExtendsSequence);
                writer.WriteKey(2);
                writer.WriteStartArray(extension.AdditionalBlobIds.Count);
                foreach (var blobId in extension.AdditionalBlobIds)
                {
                    writer.WriteByteString(blobId.ToArray());
                }

                writer.WriteEndArray();
                writer.WriteKey(3);
                writer.WriteUnsignedInteger(extension.DeclaredMaxDurationMs);
                writer.WriteEndMap();
                break;

            case JournalPayload.IntentRetirement retirement:
                writer.WriteStartMap(2);
                writer.WriteKey(1);
                writer.WriteUnsignedInteger(retirement.RetiresSequence);
                writer.WriteKey(2);
                writer.WriteUnsignedInteger((ushort)retirement.Outcome);
                writer.WriteEndMap();
                break;

            case JournalPayload.Audit audit:
                writer.WriteStartMap(4);
                writer.WriteKey(1);
                writer.WriteUnsignedInteger((ushort)audit.Action);
                writer.WriteKey(2);
                writer.WriteTextString(audit.Actor);
                writer.WriteKey(3);
                writer.WriteStartMap(0); // per-action parameters, empty in phase 0 (ADR-0022 §Decision 6)
                writer.WriteEndMap();
                writer.WriteKey(4);
                writer.WriteUnsignedInteger(audit.ObjectsAffected);
                writer.WriteEndMap();
                break;

            default:
                throw new IndexFormatException("Unknown journal payload shape.");
        }
    }

    private static DecodedJournalRecord DecodeCore(ReadOnlyMemory<byte> data)
    {
        var reader = new CanonicalCborReader(data);
        var count = reader.ReadStartMap();

        JournalRecordKind? kind = null;
        WriterId? writerId = null;
        ulong? sequence = null, issuedAt = null;
        JournalPayload? payload = null;
        byte[]? signature = null;

        for (var i = 0; i < count; i++)
        {
            switch (reader.ReadKey())
            {
                case 1:
                    var kindValue = reader.ReadUInt16();
                    if (kindValue is < 1 or > 4)
                    {
                        throw new IndexFormatException($"record_kind {kindValue} is unassigned (specification 08 §2).");
                    }

                    kind = (JournalRecordKind)kindValue;
                    break;
                case 2:
                    writerId = WriterId.FromBytes(reader.ReadFixedByteString(16));
                    break;
                case 3:
                    sequence = reader.ReadUnsignedInteger();
                    break;
                case 4:
                    issuedAt = reader.ReadUnsignedInteger();
                    break;
                case 5:
                    // The payload's shape depends on key 1, which canonical
                    // ordering guarantees was already read.
                    if (kind is null)
                    {
                        throw new IndexFormatException("The payload precedes record_kind — not canonical (00 §4.1).");
                    }

                    payload = ReadPayload(reader, kind.Value);
                    break;
                case 6:
                    signature = reader.ReadFixedByteString(64);
                    break;
                default:
                    throw new IndexFormatException(
                        "The journal record carries an unknown key; specification 08 §2 assigns keys 1-6 only.");
            }
        }

        reader.ReadEndMap();
        reader.AssertEndOfDocument();

        if (kind is null || writerId is null || sequence is null || issuedAt is null || payload is null || signature is null)
        {
            throw new IndexFormatException("The journal record omits a mandatory key (specification 08 §2).");
        }

        var record = new JournalRecord(kind.Value, writerId.Value, sequence.Value, issuedAt.Value, payload);
        Validate(record);

        return new DecodedJournalRecord(record, EncodeForSigning(record), signature);
    }

    private static JournalPayload ReadPayload(CanonicalCborReader reader, JournalRecordKind kind)
    {
        var count = reader.ReadStartMap();

        switch (kind)
        {
            case JournalRecordKind.WriteIntent:
            {
                ReadOnlyMemory<byte>? backupSet = null;
                List<BlobId> intended = [];
                ulong? duration = null, expiry = null;
                ushort? purpose = null;

                for (var i = 0; i < count; i++)
                {
                    switch (reader.ReadKey())
                    {
                        case 1:
                            backupSet = reader.ReadFixedByteString(16);
                            break;
                        case 2:
                            var blobCount = reader.ReadStartArray(maxCount: 1_000_000);
                            for (var j = 0; j < blobCount; j++)
                            {
                                intended.Add(BlobId.FromBytes(reader.ReadFixedByteString(16)));
                            }

                            reader.ReadEndArray();
                            break;
                        case 3:
                            duration = reader.ReadUnsignedInteger();
                            break;
                        case 4:
                            expiry = reader.ReadUnsignedInteger();
                            break;
                        case 5:
                            purpose = reader.ReadUInt16();
                            break;
                        default:
                            throw new IndexFormatException("The write-intent payload carries an unknown key (specification 08 §3).");
                    }
                }

                reader.ReadEndMap();

                if (backupSet is null || duration is null || expiry is null || purpose is null or < 1 or > 4)
                {
                    throw new IndexFormatException("The write-intent payload is incomplete or carries an unassigned purpose (specification 08 §3).");
                }

                return new JournalPayload.WriteIntent(backupSet.Value, intended, duration.Value, expiry.Value, (IntentPurpose)purpose.Value);
            }

            case JournalRecordKind.IntentExtension:
            {
                ulong? extends = null, duration = null;
                List<BlobId> additional = [];

                for (var i = 0; i < count; i++)
                {
                    switch (reader.ReadKey())
                    {
                        case 1:
                            extends = reader.ReadUnsignedInteger();
                            break;
                        case 2:
                            var blobCount = reader.ReadStartArray(maxCount: 1_000_000);
                            for (var j = 0; j < blobCount; j++)
                            {
                                additional.Add(BlobId.FromBytes(reader.ReadFixedByteString(16)));
                            }

                            reader.ReadEndArray();
                            break;
                        case 3:
                            duration = reader.ReadUnsignedInteger();
                            break;
                        default:
                            throw new IndexFormatException("The intent-extension payload carries an unknown key (specification 08 §4).");
                    }
                }

                reader.ReadEndMap();

                if (extends is null || duration is null)
                {
                    throw new IndexFormatException("The intent-extension payload is incomplete (specification 08 §4).");
                }

                return new JournalPayload.IntentExtension(extends.Value, additional, duration.Value);
            }

            case JournalRecordKind.IntentRetirement:
            {
                ulong? retires = null;
                ushort? outcome = null;

                for (var i = 0; i < count; i++)
                {
                    switch (reader.ReadKey())
                    {
                        case 1:
                            retires = reader.ReadUnsignedInteger();
                            break;
                        case 2:
                            outcome = reader.ReadUInt16();
                            break;
                        default:
                            throw new IndexFormatException("The intent-retirement payload carries an unknown key (specification 08 §5).");
                    }
                }

                reader.ReadEndMap();

                if (retires is null || outcome is null or < 1 or > 3)
                {
                    throw new IndexFormatException("The intent-retirement payload is incomplete or carries an unassigned outcome (specification 08 §5).");
                }

                return new JournalPayload.IntentRetirement(retires.Value, (IntentOutcome)outcome.Value);
            }

            case JournalRecordKind.Audit:
            {
                ushort? action = null;
                string? actor = null;
                ulong? affected = null;

                for (var i = 0; i < count; i++)
                {
                    switch (reader.ReadKey())
                    {
                        case 1:
                            action = reader.ReadUInt16();
                            break;
                        case 2:
                            actor = reader.ReadTextString(maxUtf8Length: 256);
                            break;
                        case 3:
                            var parameterCount = reader.ReadStartMap();
                            for (var j = 0; j < parameterCount; j++)
                            {
                                reader.ReadKey();
                                reader.SkipValue();
                            }

                            reader.ReadEndMap();
                            break;
                        case 4:
                            affected = reader.ReadUnsignedInteger();
                            break;
                        default:
                            throw new IndexFormatException("The audit payload carries an unknown key (specification 08 §6).");
                    }
                }

                reader.ReadEndMap();

                if (action is null or < 1 or > 4 || actor is null || affected is null)
                {
                    throw new IndexFormatException("The audit payload is incomplete or carries an unassigned action (specification 08 §6; ADR-0022 §Decision 6).");
                }

                return new JournalPayload.Audit((AuditAction)action.Value, actor, affected.Value);
            }

            default:
                throw new IndexFormatException("Unknown record kind.");
        }
    }
}
