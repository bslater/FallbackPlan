using Bodu;

namespace FallbackPlan.Application;

/// <summary>
/// What the CPU limit measures (ADR-0200): the processor time this process
/// has used, all its threads together, and how many processors the machine
/// has.
/// </summary>
/// <remarks>
/// <para>
/// The whole process, because nothing finer is measurable portably: a
/// thread's processor time does not follow an asynchronous job from thread to
/// thread. So the limit holds background work to a share of what the service
/// spends, a person's work included. That work is never paced; background
/// work gives way to it, which NFR-PERF-013's other clause asks for anyway.
/// </para>
/// <para>
/// Injected for the reason <see cref="PacingClock"/> is: a test states what
/// was spent rather than spending it, which is the only way a container can
/// prove the arithmetic over a measurement it cannot itself settle.
/// </para>
/// </remarks>
public sealed class CpuMeter
{
    private readonly Func<TimeSpan> _processorTime;

    /// <summary>A meter over the given reading and processor count.</summary>
    /// <param name="processorTime">The process's processor time so far, never decreasing.</param>
    /// <param name="processorCount">How many processors the machine has, at least one.</param>
    public CpuMeter(Func<TimeSpan> processorTime, int processorCount)
    {
        ThrowHelper.ThrowIfNull(processorTime);
        ArgumentOutOfRangeException.ThrowIfLessThan(processorCount, 1);
        _processorTime = processorTime;
        ProcessorCount = processorCount;
    }

    /// <summary>The production meter: this process, on this machine.</summary>
    public static CpuMeter System { get; } =
        new(static () => Environment.CpuUsage.TotalTime, Environment.ProcessorCount);

    /// <summary>How many processors the machine has; a share is of all of them together.</summary>
    public int ProcessorCount { get; }

    /// <summary>The processor time this process has used so far, user and kernel together.</summary>
    public TimeSpan ProcessorTime() => _processorTime();
}
