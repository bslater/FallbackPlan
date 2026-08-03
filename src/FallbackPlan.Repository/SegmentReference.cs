using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository;

/// <summary>
/// One logical segment of a file version — exactly the triple a file-version
/// manifest carries (specification 06 §3): logical offset, logical length,
/// object identifier. Deliberately logical-only: no blob identifier and no
/// physical offset, ever — where a record physically lives is the index's
/// concern alone (standing constraint 3; FR-ARCH-010).
/// </summary>
public readonly record struct SegmentReference(long LogicalOffset, long LogicalLength, ObjectId ObjectId);
