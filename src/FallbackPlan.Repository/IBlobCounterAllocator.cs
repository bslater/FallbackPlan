namespace FallbackPlan.Repository;

/// <summary>
/// Allocates monotonic per-writer blob counters (specification 02 §4). The
/// counter space belongs to the writer's journal sequence machinery in Wave
/// D; until then callers supply an allocator whose persistence matches their
/// needs.
/// </summary>
public interface IBlobCounterAllocator
{
    /// <summary>Allocates the next counter; never repeats within a writer.</summary>
    ulong AllocateNext();
}

/// <summary>An in-memory monotonic allocator for tests and single-run tools.</summary>
public sealed class MonotonicBlobCounterAllocator(ulong start) : IBlobCounterAllocator
{
    private ulong _next = start;

    /// <inheritdoc />
    public ulong AllocateNext() => _next++;
}
