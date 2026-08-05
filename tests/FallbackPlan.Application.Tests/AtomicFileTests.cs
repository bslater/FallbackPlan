using System.Text.Json;
using FallbackPlan.Application;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// Whole-file replacement that a crash cannot half-apply. The failure this
/// closes is not exotic: <c>File.WriteAllText</c> truncates first, so a crash
/// between truncate and write leaves a zero-length <c>state.json</c>, and that
/// file carries the device's writer identity.
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fbp-atomic-tests", Guid.NewGuid().ToString("n"));

    public AtomicFileTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void A_write_creates_the_file_and_its_directory()
    {
        var path = Path.Combine(_root, "nested", "state.json");

        AtomicFile.WriteAllText(path, "{}");

        Assert.Equal("{}", File.ReadAllText(path));
    }

    [Fact]
    public void A_write_leaves_no_temporary_file_behind()
    {
        var path = Path.Combine(_root, "state.json");

        AtomicFile.WriteAllText(path, "first");
        AtomicFile.WriteAllText(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void The_previous_contents_survive_until_the_replacement_is_complete()
    {
        // The property that matters: at no instant does the destination hold a
        // truncated file. Observing the instant directly needs a crash, so the
        // proxy is that a reader opening the path between writes always sees
        // one complete document, never an empty one.
        var path = Path.Combine(_root, "state.json");
        AtomicFile.WriteAllText(path, """{"schema_version":1}""");

        for (var i = 0; i < 50; i++)
        {
            AtomicFile.WriteAllText(path, $$"""{"schema_version":1,"n":{{i}}}""");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(1, document.RootElement.GetProperty("schema_version").GetInt32());
        }
    }

    [Fact]
    public void Concurrent_writers_in_one_process_do_not_collide_on_a_temporary_name()
    {
        // Under single ownership two writers here would be a bug, but a fixed
        // temp name is the kind of assumption that is safe until it is not —
        // FileSequenceStateStore.Save used one, and that is a hazard ADR-0028
        // names explicitly.
        var path = Path.Combine(_root, "state.json");

        Parallel.For(0, 32, i => AtomicFile.WriteAllText(path, $$"""{"n":{{i}}}"""));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(document.RootElement.TryGetProperty("n", out _));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
