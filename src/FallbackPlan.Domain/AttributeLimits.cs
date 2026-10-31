namespace FallbackPlan.Domain;

/// <summary>
/// What a platform takes in a file's own extended attributes, which a
/// file's alternate streams kept as attributes are judged by (ADR-0179 and
/// its Amendment 2).
/// </summary>
/// <param name="TakesNamespaces">
/// Whether a name is taken only in a namespace, and a file's own only in its
/// user namespace, as on Linux.
/// </param>
/// <param name="MaxNameBytes">The most bytes a name may have, its namespace with it; null where none is known.</param>
/// <param name="MaxValueBytes">The most bytes a value may hold on any volume; null where none is known.</param>
public sealed record AttributeLimits(bool TakesNamespaces, uint? MaxNameBytes = null, uint? MaxValueBytes = null)
{
    /// <summary>This platform's.</summary>
    public static AttributeLimits OfThisPlatform { get; } =
        new(ExtendedAttributes.TakesNamespaces, ExtendedAttributes.MaxNameBytes, ExtendedAttributes.MaxValueBytes);
}
