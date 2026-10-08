using System.Text;

namespace FallbackPlan.TestSupport;

/// <summary>
/// A text writer one thread may read while others write to it: a running
/// host's output, which the service writes from its own threads while the
/// test polls it for a line.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="StringWriter"/> is not safe for that. Its <c>ToString</c>
/// copies the builder's chunks while a write may be adding one, and then
/// throws <see cref="ArgumentOutOfRangeException"/>: in a loop reading one
/// while another thread wrote lines to it, about one read in a hundred did. A
/// test polling a host's output takes that throw for its own failure.
/// </para>
/// <para>
/// Every write and every read here take one lock. A line is written whole,
/// its text and its line break under the same lock, so a test searching for
/// a line never meets half of one beside half of another.
/// </para>
/// </remarks>
public sealed class SharedStringWriter : TextWriter
{
    private readonly StringBuilder _text = new();
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public override Encoding Encoding => Encoding.Unicode;

    /// <inheritdoc />
    public override void Write(char value)
    {
        lock (_gate)
        {
            _text.Append(value);
        }
    }

    /// <inheritdoc />
    public override void Write(char[] buffer, int index, int count)
    {
        lock (_gate)
        {
            _text.Append(buffer, index, count);
        }
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<char> buffer)
    {
        lock (_gate)
        {
            _text.Append(buffer);
        }
    }

    /// <inheritdoc />
    public override void Write(string? value)
    {
        lock (_gate)
        {
            _text.Append(value);
        }
    }

    /// <inheritdoc />
    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            _text.Append(value).Append(CoreNewLine);
        }
    }

    /// <inheritdoc />
    public override void WriteLine(ReadOnlySpan<char> buffer)
    {
        lock (_gate)
        {
            _text.Append(buffer).Append(CoreNewLine);
        }
    }

    /// <summary>Everything written so far.</summary>
    /// <returns>The text, read under the writers' lock.</returns>
    public override string ToString()
    {
        lock (_gate)
        {
            return _text.ToString();
        }
    }
}
