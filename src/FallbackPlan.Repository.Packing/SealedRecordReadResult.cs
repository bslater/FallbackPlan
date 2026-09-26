using Bodu;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// What a read of one record's <b>sealed</b> bytes produced
/// (<see cref="BlobReader.ReadSealedRecordAsync"/>): the prefix, ciphertext
/// and tag exactly as the blob holds them, or the finding that stopped the
/// read. Distinct from <see cref="RecordReadResult"/> because nothing here
/// is plaintext and nothing here was opened — the bytes are carried, not
/// understood.
/// </summary>
public sealed record SealedRecordReadResult
{
    private SealedRecordReadResult(RecordReadOutcome outcome, byte[]? sealedRecord, string? detail)
    {
        Outcome = outcome;
        Sealed = sealedRecord;
        Detail = detail;
    }

    /// <summary>How the read resolved.</summary>
    public RecordReadOutcome Outcome { get; }

    /// <summary>The sealed bytes when the outcome is <see cref="RecordReadOutcome.Ok"/>.</summary>
    public byte[]? Sealed { get; }

    /// <summary>A human-readable description of a failure.</summary>
    public string? Detail { get; }

    /// <summary>Creates a success result carrying the sealed bytes.</summary>
    /// <param name="sealedRecord">Prefix, ciphertext and tag, as the blob holds them.</param>
    /// <returns>The result.</returns>
    public static SealedRecordReadResult Success(byte[] sealedRecord)
    {
        ThrowHelper.ThrowIfNull(sealedRecord);
        return new(RecordReadOutcome.Ok, sealedRecord, null);
    }

    /// <summary>Creates a failure result naming its finding.</summary>
    /// <param name="outcome">What went wrong.</param>
    /// <param name="detail">The human-readable description.</param>
    /// <returns>The result.</returns>
    public static SealedRecordReadResult Failure(RecordReadOutcome outcome, string detail)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(detail);
        return new(outcome, null, detail);
    }
}
