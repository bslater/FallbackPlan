namespace FallbackPlan.Cli.Tests;

/// <summary>
/// <c>restore --from</c> reads a destination's copy through the service
/// (FR-RST-001, ADR-0199 Amendment 9), as the console's source step does. In
/// direct mode, which reads the repository it is pointed at and opens no
/// source, it is refused before anything is read, naming what to do instead.
/// </summary>
[TestClass]
public sealed class RestoreFromCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    [TestMethod]
    public async Task RestoreFrom_InDirectMode_IsRefused_BeforeAnythingIsRead()
    {
        await _cli.InitAsync();
        var output = Path.Combine(_cli.WorkPath, "restored");

        foreach (var plan in new[] { false, true })
        {
            string[] args = ["restore", new string('e', 32), "--output", output, "--from", "vault", .. plan ? new[] { "--plan" } : []];
            var refused = await _cli.RunAsync(args);

            Assert.AreNotEqual(0, refused.ExitCode, refused.All);
            Assert.Contains("--from", refused.All, StringComparison.Ordinal);
            Assert.Contains("through the service", refused.All, StringComparison.Ordinal);
            Assert.DoesNotContain("no snapshot", refused.All, StringComparison.OrdinalIgnoreCase);
            Assert.IsFalse(Directory.Exists(output), "a refused restore wrote something");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _cli.Dispose();
}
