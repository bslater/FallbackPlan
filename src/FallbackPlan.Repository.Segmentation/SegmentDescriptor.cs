namespace FallbackPlan.Repository.Segmentation;

/// <summary>
/// One segment produced by the streaming reader: its position in the file,
/// its byte offset, and its length — full segment size for every segment
/// except possibly the last (specification 09 §2.1).
/// </summary>
public readonly record struct SegmentDescriptor(long Index, long Offset, int Length);
