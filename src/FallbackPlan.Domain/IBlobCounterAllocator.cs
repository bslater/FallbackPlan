namespace FallbackPlan.Domain;

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

    /// <summary>
    /// Marks <paramref name="blobCounter"/> accounted for: the blob embedding
    /// it is durable and named by a durable intent, which is the counter's
    /// accounting object (ADR-0022 §Decision 7 case 4). A counter never
    /// marked would be voided by the next run — a void delta falsely
    /// claiming a number a durable blob embeds was skipped.
    /// </summary>
    void MarkAccounted(ulong blobCounter);
}

/// <summary>An in-memory monotonic allocator for tests and single-run tools.</summary>
public sealed class MonotonicBlobCounterAllocator(ulong start) : IBlobCounterAllocator
{
    private ulong _next = start;

    /// <inheritdoc />
    public ulong AllocateNext() => _next++;

    /// <inheritdoc />
    /// <remarks>Nothing to persist: this allocator never owes a void delta.</remarks>
    public void MarkAccounted(ulong blobCounter)
    {
    }
}
