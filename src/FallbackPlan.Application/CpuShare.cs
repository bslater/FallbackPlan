using Bodu;
using System.Globalization;

namespace FallbackPlan.Application;

/// <summary>
/// A share of the machine's processors background work is held to
/// (NFR-PERF-013, ADR-0200): a whole percentage, <c>1%</c> to <c>99%</c>, of
/// every processor the machine has together. On a four-core laptop
/// <c>25%</c> is one core's worth.
/// </summary>
/// <remarks>
/// <para>
/// Of the whole machine rather than of one core, because the two conventions
/// people meet disagree by the core count and the setting has to mean one of
/// them. The console and the CLI say which wherever they show it.
/// </para>
/// <para>
/// Strict, with the defect named, as <see cref="ByteRate"/> is: a share this
/// build misreads is a limit nobody honours. <c>0%</c> is a stop rather than
/// a limit, and <c>100%</c> or more limits nothing. Leaving the setting out is
/// how "unlimited" is said.
/// </para>
/// </remarks>
public sealed class CpuShare
{
    private CpuShare(int percent) => Percent = percent;

    /// <summary>The share as written, trimmed — what a person wrote, for display.</summary>
    public required string Text { get; init; }

    /// <summary>The share, a whole percentage of the machine's processors, 1 to 99.</summary>
    public int Percent { get; }

    /// <summary>Reads a share, or names what is wrong with the text.</summary>
    /// <param name="text">The configured text, e.g. <c>25%</c>.</param>
    /// <param name="share">The share, when it reads.</param>
    /// <param name="defect">What is wrong with the text, when it does not.</param>
    /// <returns>Whether the text is a share this build reads.</returns>
    public static bool TryParse(string text, out CpuShare? share, out string? defect)
    {
        ThrowHelper.ThrowIfNull(text);
        share = null;
        defect = null;

        var trimmed = text.Trim();
        if (!trimmed.EndsWith('%'))
        {
            defect = Grammar(text);
            return false;
        }

        var digits = trimmed[..^1].TrimEnd();
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            defect = Grammar(text);
            return false;
        }

        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) || percent >= 100)
        {
            defect = $"'{text}': a share of the whole machine or more limits nothing — leave the setting out for no limit.";
            return false;
        }

        if (percent == 0)
        {
            defect = $"'{text}': a background CPU limit is at least 1%. None at all is a stop rather than a limit; "
                + "leave the setting out for no limit.";
            return false;
        }

        share = new CpuShare(percent) { Text = trimmed };
        return true;
    }

    private static string Grammar(string text) =>
        $"'{text}': a background CPU limit is a whole percentage of this machine's processors, 1% to 99%, as in '25%'.";
}
