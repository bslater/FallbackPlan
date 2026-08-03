namespace FallbackPlan.Domain;

/// <summary>
/// The class of a blob (specification 05 §2): the <c>u16</c> in the cleartext
/// envelope that selects whether the blob key derives from the data key or the
/// metadata key for the recorded generation.
/// </summary>
public enum BlobClass : ushort
{
    /// <summary>A data blob (<c>0x0001</c>): segment records.</summary>
    Data = 0x0001,

    /// <summary>A metadata blob (<c>0x0002</c>): manifests and index records.</summary>
    Metadata = 0x0002,
}
