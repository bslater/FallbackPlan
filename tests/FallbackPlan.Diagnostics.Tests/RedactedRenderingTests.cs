using FallbackPlan.Domain.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Diagnostics.Tests;

/// <summary>
/// What each rendering lets past (ADR-0043 §4 as amended, ADR-0081):
/// NFR-SEC-006 and NFR-PRIV-003 at the one place the decision is taken.
/// </summary>
/// <remarks>
/// <para>
/// The rule these tests pin is that a redacted rendering is <b>fail-closed</b>.
/// A value crosses the boundary as written only when its declared type says it
/// may: a number, an enum, a <see cref="LogLabel"/>. A <c>string</c> declares
/// nothing, and neither does an exception's message — the commonest carrier of
/// a full path there is, since the platform writes "Access to the path '…' is
/// denied." into it — so both are withheld. The alternative, rendering a string
/// unless somebody remembered to wrap it, is the filter list NFR-SEC-006
/// forbids, kept in reverse.
/// </para>
/// <para>
/// The third mode is the diagnostic bundle a person opted in to paths for:
/// paths and the text no type classifies come through, and identifiers still
/// shorten, because the opt-in is to paths and not to correlation.
/// </para>
/// </remarks>
[TestClass]
public sealed class RedactedRenderingTests
{
    /// <summary>A folder name nothing in the renderer could produce by accident.</summary>
    private const string TellingName = "a-folder-named-for-a-diagnosis";

    private static readonly string Secret = $"/home/someone/{TellingName}/scan.pdf";

    private static LogRecord Record(
        string template,
        IReadOnlyList<KeyValuePair<string, object?>> values,
        Exception? exception = null) => new(
            Sequence: 7,
            TimestampUnixMilliseconds: 1_722_600_000_000,
            Level: LogLevel.Warning,
            EventId: 4242,
            Category: "FallbackPlan.Test",
            Values: [.. values, new(LogRecord.OriginalFormatKey, template)],
            ExceptionType: exception?.GetType().FullName,
            ExceptionMessage: exception?.Message);

    private static LogRecord Hole(string name, object? value) =>
        Record($"saw {{{name}}}", [new(name, value)]);

    [TestMethod]
    public void Redacted_AStringNoTypeDeclares_IsWithheld()
    {
        // The real filesystem's reason for an unreadable folder, as the
        // engine passes it today: the path rides inside the prose.
        var record = Hole("Reason", $"Access to the path '{Secret}' is denied.");

        var redacted = LogRecordRenderer.Render(record, RenderMode.Redacted);

        Assert.DoesNotContain(TellingName, redacted, StringComparison.Ordinal);
        Assert.Contains(LogRecordRenderer.Withheld, redacted, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Redacted_ALabel_CrossesAsWritten()
    {
        var redacted = LogRecordRenderer.Render(Hole("SetName", LogLabel.Of("docs")), RenderMode.Redacted);

        Assert.AreEqual("saw docs", redacted);
    }

    [TestMethod]
    public void Redacted_NumbersEnumsAndDurations_CrossAsWritten()
    {
        var record = Record(
            "{Count} of {Total} at {Level} after {Elapsed}, done {Done}",
            [
                new("Count", 3),
                new("Total", 4_000_000_000L),
                new("Level", LogLevel.Debug),
                new("Elapsed", TimeSpan.FromSeconds(90)),
                new("Done", true),
            ]);

        Assert.AreEqual(
            "3 of 4000000000 at Debug after 00:01:30, done True",
            LogRecordRenderer.Render(record, RenderMode.Redacted));
    }

    [TestMethod]
    public void Redacted_AValueOfATypeNobodyClassified_IsWithheld()
    {
        // A FileInfo's ToString is its path. Nothing declared it safe, so the
        // redacted rendering does not ask what it would have said.
        var redacted = LogRecordRenderer.Render(Hole("File", new FileInfo(Secret)), RenderMode.Redacted);

        Assert.DoesNotContain(TellingName, redacted, StringComparison.Ordinal);
        Assert.AreEqual($"saw {LogRecordRenderer.Withheld}", redacted);
    }

    [TestMethod]
    public void Redacted_AnExceptionsMessage_IsWithheldAndItsTypeKept()
    {
        var record = Record(
            "could not list {Directory}",
            [new("Directory", new LogPath(Secret))],
            new UnauthorizedAccessException($"Access to the path '{Secret}' is denied."));

        var redacted = LogRecordRenderer.Render(record, RenderMode.Redacted);

        Assert.DoesNotContain(TellingName, redacted, StringComparison.Ordinal);
        Assert.Contains("System.UnauthorizedAccessException", redacted, StringComparison.Ordinal);
        Assert.Contains(LogRecordRenderer.Withheld, redacted, StringComparison.Ordinal);
        Assert.AreEqual(LogRecordRenderer.Withheld, LogRecordRenderer.RenderExceptionMessage(record, RenderMode.Redacted));
    }

    [TestMethod]
    public void Full_EveryValueAndTheExceptionsMessage_RenderAsWritten()
    {
        // The service's own file is inside the trust boundary: the fail-closed
        // rule is about crossing it, and must not cost the local log a word.
        var record = Record(
            "could not read {Path}: {Reason}",
            [new("Path", new LogPath(Secret)), new("Reason", "denied by policy")],
            new UnauthorizedAccessException($"Access to the path '{Secret}' is denied."));

        var full = LogRecordRenderer.Render(record, RenderMode.Full);

        Assert.Contains($"could not read {Secret}: denied by policy", full, StringComparison.Ordinal);
        Assert.Contains($"Access to the path '{Secret}' is denied.", full, StringComparison.Ordinal);
        Assert.AreEqual(
            $"Access to the path '{Secret}' is denied.",
            LogRecordRenderer.RenderExceptionMessage(record, RenderMode.Full));
    }

    [TestMethod]
    public void RedactedWithPaths_APathAndTextNoTypeDeclares_RenderAsWritten()
    {
        var record = Record(
            "could not read {Path}: {Reason}",
            [new("Path", new LogPath(Secret)), new("Reason", "denied by policy")],
            new UnauthorizedAccessException($"Access to the path '{Secret}' is denied."));

        var opted = LogRecordRenderer.Render(record, RenderMode.RedactedWithPaths);

        Assert.Contains($"could not read {Secret}: denied by policy", opted, StringComparison.Ordinal);
        Assert.Contains($"Access to the path '{Secret}' is denied.", opted, StringComparison.Ordinal);
    }

    [TestMethod]
    public void RedactedWithPaths_AnIdentifier_StaysShortened()
    {
        // The opt-in is to paths, not to correlation: a set's id is as much a
        // durable handle with paths shown as without them (NFR-PRIV-002).
        var setId = new string('c', 32);

        var opted = LogRecordRenderer.Render(Hole("SetId", LogId.BackupSet(setId)), RenderMode.RedactedWithPaths);

        Assert.AreEqual("saw set#cccccccc", opted);
    }

    [TestMethod]
    public void RedactedWithPaths_ASecret_IsStillRedactedByItsOwnType()
    {
        // A secret never depended on the renderer: its ToString redacts at the
        // declaration, which is why no mode can bring one through.
        var opted = LogRecordRenderer.Render(Hole("Credential", new SelfRedacting()), RenderMode.RedactedWithPaths);

        Assert.AreEqual("saw secret(redacted)", opted);
    }

    [TestMethod]
    public void RenderExceptionMessage_NoExceptionLogged_IsNull()
    {
        Assert.IsNull(LogRecordRenderer.RenderExceptionMessage(Hole("Count", 1), RenderMode.Redacted));
    }

    [TestMethod]
    public void RenderLine_InEveryMode_LeadsWithTheSequenceTheFileLineCarries()
    {
        // The bundle's log and the service's own file must line up record for
        // record, and once one side is redacted the sequence is all they share.
        var record = Hole("Reason", "a reason");

        foreach (var mode in new[] { RenderMode.Full, RenderMode.Redacted, RenderMode.RedactedWithPaths })
        {
            var line = LogRecordRenderer.RenderLine(record, mode);

            Assert.StartsWith("7 ", line, StringComparison.Ordinal);
            Assert.Contains("warning", line, StringComparison.Ordinal);
            Assert.Contains("4242", line, StringComparison.Ordinal);
            Assert.Contains("FallbackPlan.Test", line, StringComparison.Ordinal);
            Assert.EndsWith(LogRecordRenderer.Render(record, mode), line, StringComparison.Ordinal);
        }
    }

    /// <summary>The idiom every secret type follows: redacted at its own declaration.</summary>
    private sealed class SelfRedacting
    {
        public override string ToString() => "secret(redacted)";
    }
}
