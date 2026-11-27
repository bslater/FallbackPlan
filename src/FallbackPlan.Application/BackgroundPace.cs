using FallbackPlan.Application.Resources;

namespace FallbackPlan.Application;

/// <summary>
/// Everything one seam of background work is paced by (NFR-PERF-013): the
/// byte rate it moves under, when one is set, and the installation's CPU
/// limit, when one is set (ADR-0074, ADR-0200). Each is optional, and a seam
/// with neither has no pace at all, which <see cref="Of"/> answers as null.
/// </summary>
/// <remarks>
/// The bytes are charged first and the processor time second, so a chunk a
/// byte rate held back is looked at after its wait, which is when the work it
/// stood for is about to be done.
/// </remarks>
public sealed class BackgroundPace
{
    /// <summary>A pace over the given limits, at least one of which is set.</summary>
    /// <param name="bytes">The byte rate the seam moves under, or null for none.</param>
    /// <param name="cpu">The installation's CPU limit, or null for none.</param>
    public BackgroundPace(ByteRateLimiter? bytes, CpuLimiter? cpu)
    {
        if (bytes is null && cpu is null)
        {
            throw new ArgumentException(Strings.BackgroundPace_NeedsALimit, nameof(cpu));
        }

        Bytes = bytes;
        Cpu = cpu;
    }

    /// <summary>The byte rate the seam moves under, or null for none.</summary>
    public ByteRateLimiter? Bytes { get; }

    /// <summary>The installation's CPU limit, or null for none.</summary>
    public CpuLimiter? Cpu { get; }

    /// <summary>A pace over the given limits, or null when neither is set.</summary>
    /// <param name="bytes">The byte rate, or null for none.</param>
    /// <param name="cpu">The CPU limit, or null for none.</param>
    /// <returns>The pace, or null.</returns>
    public static BackgroundPace? Of(ByteRateLimiter? bytes, CpuLimiter? cpu) =>
        bytes is null && cpu is null ? null : new BackgroundPace(bytes, cpu);

    /// <summary>
    /// Waits until <paramref name="bytes"/> more may pass at the byte rate,
    /// then until what the service has spent is inside the CPU share.
    /// </summary>
    /// <param name="bytes">How many bytes are about to move, or just did.</param>
    /// <param name="cancellationToken">Ends either wait.</param>
    public async ValueTask AcquireAsync(long bytes, CancellationToken cancellationToken)
    {
        if (Bytes is { } rate)
        {
            await rate.AcquireAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        if (Cpu is { } cpu)
        {
            await cpu.PaceAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
