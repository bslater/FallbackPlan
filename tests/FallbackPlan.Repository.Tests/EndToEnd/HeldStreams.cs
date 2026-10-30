using FallbackPlan.Domain;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Calls that hold each file's alternate streams in memory, by the path the
/// writer names: what was written, and how often, is what a case reads. They
/// stand in for the platform's on any platform, so what a writer asks for and
/// when is judged everywhere (ADR-0174).
/// </summary>
internal sealed class HeldStreams : IAlternateStreams
{
    public Dictionary<(string File, string Name), byte[]> Streams { get; } = [];

    public string? Refusal { get; init; }

    public Action<string>? OnWrite { get; init; }

    public int Writes { get; private set; }

    public byte[]? Read(string file, string name, ulong length) =>
        Streams.TryGetValue((file, name), out var stream) && (ulong)stream.Length == length ? stream.ToArray() : null;

    public string? Write(string file, string name, ReadOnlySpan<byte> content)
    {
        if (Refusal is { } refusal)
        {
            return refusal;
        }

        Writes++;
        Streams[(file, name)] = content.ToArray();
        OnWrite?.Invoke(file);
        return null;
    }
}
