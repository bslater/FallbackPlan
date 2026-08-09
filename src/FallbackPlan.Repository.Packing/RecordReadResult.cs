using Bodu;

namespace FallbackPlan.Repository.Packing;

/// <summary>How reading one record resolved (specification 04 §6–§7).</summary>
public enum RecordReadOutcome
{
    /// <summary>The record decrypted, decompressed, and content-verified.</summary>
    Ok,

    /// <summary>The stored bytes violate framing or compression rules — a damage finding.</summary>
    FormatViolation,

    /// <summary>
    /// The authentication tag failed: the record is damaged or was moved to a
    /// position its AAD does not cover. No plaintext was emitted.
    /// </summary>
    AuthenticationFailed,

    /// <summary>
    /// The tag verified but the decrypted plaintext does not hash to the
    /// content identifier its object identifier implies — the failure class
    /// 04 §6 step 7 exists to catch.
    /// </summary>
    ContentMismatch,

    /// <summary>The record uses a profile this implementation does not support; refused, not guessed.</summary>
    UnsupportedProfile,
}

/// <summary>
/// The result of reading one record. Corruption is local (specification
/// 04 §7): a failed record never affects its neighbours, so failure is a
/// result here, not an exception.
/// </summary>
public sealed record RecordReadResult
{
    private RecordReadResult(RecordReadOutcome outcome, byte[]? plaintext, string? detail)
    {
        Outcome = outcome;
        Plaintext = plaintext;
        Detail = detail;
    }

    /// <summary>How the read resolved.</summary>
    public RecordReadOutcome Outcome { get; }

    /// <summary>The verified plaintext when the outcome is <see cref="RecordReadOutcome.Ok"/>.</summary>
    public byte[]? Plaintext { get; }

    /// <summary>A human-readable description of a failure.</summary>
    public string? Detail { get; }

    /// <summary>Creates a success result carrying the verified plaintext.</summary>
    public static RecordReadResult Success(byte[] plaintext)
    {
        ThrowHelper.ThrowIfNull(plaintext);
        return new(RecordReadOutcome.Ok, plaintext, null);
    }

    /// <summary>Creates a failure result naming its finding.</summary>
    public static RecordReadResult Failure(RecordReadOutcome outcome, string detail)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(detail);
        return new(outcome, null, detail);
    }
}
