using Microsoft.Extensions.Logging;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// Key-material diagnostics (ADR-0043; event ids 1300–1349).
/// </summary>
/// <remarks>
/// <para>
/// This file is deliberately thin, and every message in it was written by
/// asking what it would tell somebody who had stolen the log.
/// </para>
/// <para>
/// Nothing here takes a key, a passphrase, a salt or a derived scalar as a
/// parameter — not redacted, not hashed, not truncated. Specification 03 §8
/// forbids key material in any log, and the safest way to honour a rule like
/// that is to leave no parameter it could be passed through. Argon2 cost
/// parameters are recorded because a slow unlock is a real support question
/// and the parameters are public in the descriptor anyway.
/// </para>
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 1300, Level = LogLevel.Debug,
        Message = "Deriving the root key: Argon2id m={MemoryKiB} KiB, t={Iterations}, p={Parallelism}")]
    internal static partial void DerivingRoot(
        ILogger logger, uint memoryKiB, uint iterations, uint parallelism);

    [LoggerMessage(
        EventId = 1301, Level = LogLevel.Debug,
        Message = "Root key derived in {ElapsedMs} ms")]
    internal static partial void RootDerived(ILogger logger, long elapsedMs);

    [LoggerMessage(
        EventId = 1302, Level = LogLevel.Warning,
        Message = "The key object did not unwrap — a wrong passphrase and a tampered key object are "
            + "indistinguishable here, by design (specification 03 §3)")]
    internal static partial void UnwrapFailed(ILogger logger);

    [LoggerMessage(
        EventId = 1303, Level = LogLevel.Warning,
        Message = "A sealed envelope did not open under this service's recipient key")]
    internal static partial void SealedEnvelopeRefused(ILogger logger);

    [LoggerMessage(
        EventId = 1304, Level = LogLevel.Information,
        Message = "Write-only authority derived; the sealing private key is held for this operation only")]
    internal static partial void WriteOnlyAuthorityDerived(ILogger logger);
}
