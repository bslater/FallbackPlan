using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FallbackPlan.Domain;

namespace FallbackPlan.Repository.Format.RecoveryKit;

/// <summary>
/// The transcribable text form (specifications/recovery-kit §4): lowercase
/// base32 of the framed binary in 4-character groups, 12 per line, each
/// line carrying a check that localises a typo to that line; the framed
/// SHA-256 still guarantees the whole.
/// </summary>
public static class RecoveryKitText
{
    private const string Begin = "FALLBACKPLAN RECOVERY KIT v1";
    private const string End = "END FALLBACKPLAN RECOVERY KIT";
    private const int GroupsPerLine = 12;
    private const int GroupLength = 4;

    /// <summary>Renders the framed binary as the canonical text layout.</summary>
    public static string Render(ReadOnlySpan<byte> framedKit, string? instructionHeader = null)
    {
        var encoded = Base32.Encode(framedKit);
        var builder = new StringBuilder();
        builder.Append(Begin).Append('\n');

        if (!string.IsNullOrEmpty(instructionHeader))
        {
            builder.Append(instructionHeader.TrimEnd()).Append('\n');
        }

        builder.Append('\n');

        var perLine = GroupsPerLine * GroupLength;
        var lineCount = (encoded.Length + perLine - 1) / perLine;
        var numberWidth = lineCount >= 100 ? 3 : 2;

        for (var line = 0; line < lineCount; line++)
        {
            var payload = encoded.Substring(line * perLine, Math.Min(perLine, encoded.Length - line * perLine));
            var number = (line + 1).ToString(CultureInfo.InvariantCulture).PadLeft(numberWidth, '0');

            builder.Append(number).Append(": ");
            for (var offset = 0; offset < payload.Length; offset += GroupLength)
            {
                builder.Append(payload, offset, Math.Min(GroupLength, payload.Length - offset)).Append(' ');
            }

            builder.Append(LineCheck(number, payload)).Append('\n');
        }

        builder.Append(End).Append('\n');
        return builder.ToString();
    }

    /// <summary>
    /// Parses the text form back to the framed binary, verifying every
    /// per-line check and reporting failures by line number (FR-KIT-003).
    /// Case and whitespace are forgiven; nothing else is.
    /// </summary>
    /// <exception cref="RecoveryKitFormatException">A line check fails, or the payload does not decode.</exception>
    public static byte[] ParseToFramed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var payload = new StringBuilder();
        var sawPayload = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon is < 1 or > 3 || !line[..colon].All(char.IsAsciiDigit))
            {
                continue; // instruction text, markers, blank lines
            }

            var number = line[..colon];
            var rest = line[(colon + 1)..].Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
            if (rest.Length <= GroupLength)
            {
                throw new RecoveryKitFormatException($"Line {number}: too short to carry payload and check.");
            }

            var linePayload = rest[..^GroupLength];
            var check = rest[^GroupLength..];

            if (!string.Equals(check, LineCheck(number, linePayload), StringComparison.Ordinal))
            {
                throw new RecoveryKitFormatException(
                    $"Line {number}: the line check does not verify — a transcription error on this line (specifications/recovery-kit §4).");
            }

            payload.Append(linePayload);
            sawPayload = true;
        }

        if (!sawPayload)
        {
            throw new RecoveryKitFormatException("No payload lines found — not a recovery-kit text form.");
        }

        var encoded = payload.ToString();
        var framed = new byte[encoded.Length * 5 / 8];
        if (!Base32.TryDecode(encoded, framed, out var written))
        {
            throw new RecoveryKitFormatException("The payload does not decode as base32 (specifications/recovery-kit §4).");
        }

        return framed.AsSpan(0, written).ToArray();
    }

    private static string LineCheck(string number, string payload)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(number + ":" + payload.ToLowerInvariant()));
        return Base32.Encode(digest)[..GroupLength];
    }
}
