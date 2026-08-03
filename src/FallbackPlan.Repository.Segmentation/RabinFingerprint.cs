namespace FallbackPlan.Repository.Segmentation;

/// <summary>
/// The <c>cdc-v1</c> Rabin fingerprint over GF(2) with a 64-bit state
/// (specification 09 §3.1; ADR-0023): modulus
/// <c>P(x) = x⁶⁴ + x⁴ + x³ + x + 1</c>, 64-byte window, tables derived from
/// the polynomial alone at type initialisation — so the committed vector
/// tables and these can only agree because the derivation rule agrees.
/// </summary>
/// <remarks>
/// The state after consuming the byte at position <em>p</em> equals the
/// window's 64 bytes (ending at and including <em>p</em>) interpreted
/// most-significant-byte-first as a GF(2) polynomial, reduced mod P. The
/// window rolls continuously and is never reset — a boundary must be a pure
/// function of local content, which is the entire resynchronisation property
/// (09 §3.2).
/// </remarks>
public static class RabinFingerprint
{
    /// <summary>The low 64 coefficient bits of P(x); the x⁶⁴ term is implicit (ADR-0023).</summary>
    public const ulong PolynomialLow = 0x1B;

    /// <summary>The fixed v1 window size in bytes.</summary>
    public const int WindowSize = 64;

    private static readonly ulong[] Push = BuildTable(XPower64());
    private static readonly ulong[] Pop = BuildTable(XPower512());

    /// <summary>The push table, <c>push_table[b] = (b·x⁶⁴) mod P</c> — exposed for conformance checks.</summary>
    public static ReadOnlySpan<ulong> PushTable => Push;

    /// <summary>The pop table, <c>pop_table[b] = (b·x⁵¹²) mod P</c> — exposed for conformance checks.</summary>
    public static ReadOnlySpan<ulong> PopTable => Pop;

    /// <summary>
    /// Advances the rolling state by one byte:
    /// <c>H′ = ((H &lt;&lt; 8) ⊕ push[H ≫ 56]) ⊕ incoming ⊕ pop[outgoing]</c>,
    /// where <paramref name="outgoing"/> is the byte leaving the 64-byte
    /// window (zero while the window is still filling).
    /// </summary>
    public static ulong Advance(ulong state, byte incoming, byte outgoing) =>
        unchecked((state << 8) ^ Push[(int)(state >> 56)] ^ incoming ^ Pop[outgoing]);

    private static ulong[] BuildTable(ulong multiplier)
    {
        var table = new ulong[256];

        for (var b = 1; b < 256; b++)
        {
            table[b] = MultiplyMod((ulong)b, multiplier);
        }

        return table;
    }

    // x^64 ≡ P(x) − x^64 = x^4 + x^3 + x + 1 (mod P), i.e. the low bits.
    private static ulong XPower64() => PolynomialLow;

    private static ulong XPower512()
    {
        var x128 = MultiplyMod(XPower64(), XPower64());
        var x256 = MultiplyMod(x128, x128);
        return MultiplyMod(x256, x256);
    }

    /// <summary>Carry-less multiplication of two GF(2) polynomials, reduced mod P.</summary>
    private static ulong MultiplyMod(ulong a, ulong b)
    {
        var result = 0UL;

        while (b != 0)
        {
            if ((b & 1) != 0)
            {
                result ^= a;
            }

            b >>= 1;

            var overflow = (a & 0x8000_0000_0000_0000UL) != 0;
            a <<= 1;

            if (overflow)
            {
                a ^= PolynomialLow;
            }
        }

        return result;
    }
}
