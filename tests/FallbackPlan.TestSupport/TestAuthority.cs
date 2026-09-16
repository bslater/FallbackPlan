using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.TestSupport;

/// <summary>
/// One derivation root for every suite that needs repository keys and has no
/// passphrase to derive them from: the 32 bytes the write-only conformance
/// vector pins (specification 03 §9.1, <c>write-only.json</c>), so a unit
/// suite, the conformance vectors and the end-to-end fixtures all speak of
/// the same keys.
/// </summary>
/// <remarks>
/// <see cref="Shared"/> is never disposed and is handed out by reference.
/// Every consumer copies what it keeps — <c>RepositoryKeySet.FromWriteCredential</c>
/// and <c>KeyHierarchy.ForWriteOnly</c> clone the credential, and
/// <c>RepositoryReader</c> copies the sealing scalar — so sharing costs
/// nothing, and disposing it would zero the keys under every test still
/// running. A suite that needs to own and dispose an authority calls
/// <see cref="Create"/> instead.
/// </remarks>
public static class TestAuthority
{
    /// <summary>The pinned root, <c>c0…df</c>.</summary>
    public static ReadOnlySpan<byte> Root =>
    [
        0xc0, 0xc1, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xcb, 0xcc, 0xcd, 0xce, 0xcf,
        0xd0, 0xd1, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xdb, 0xdc, 0xdd, 0xde, 0xdf,
    ];

    /// <summary>The one authority every suite shares; never disposed.</summary>
    public static RepositoryReadAuthority Shared { get; } = WriteOnlyDerivation.FromRoot(Root);

    /// <summary>A fresh authority over the same root, for a caller that will dispose it.</summary>
    public static RepositoryReadAuthority Create() => WriteOnlyDerivation.FromRoot(Root);
}
