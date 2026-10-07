using FallbackPlan.Agent;

namespace FallbackPlan.ArchitectureTests;

/// <summary>
/// A set's backup has the archive's catalogue connection to itself
/// (FR-SVC-012, [ADR-0010](../../docs/adr/0010-local-store-separation.md)
/// Amendment 5).
/// </summary>
/// <remarks>
/// <para>
/// A sync, a retention pass, a snapshot deletion and a heal from a
/// destination can each run while the same set's backup does. The transfer
/// lane runs beside the writer pool, the pool runs two jobs at once by
/// default, and the set gate keeps a sync from a retention apply but keeps
/// neither from a backup. A SQLite connection is not safe to share between
/// threads, and sharing one does not fail where it happens: it corrupts the
/// connection's own bookkeeping, and the failure surfaces later and
/// somewhere else. The one run that showed it threw a
/// <see cref="NullReferenceException"/> from inside the connection's close,
/// as the runtime disposed. All four reached the backup's connection
/// through <see cref="ArchiveHandle.Catalogue"/>.
/// </para>
/// <para>
/// The rule is held here, in the compiled code, rather than by racing two
/// jobs: the race is rare enough that such a test passes on the code it
/// should fail. The suites for each of the four show it working on a
/// connection of its own with the backup's closed.
/// </para>
/// </remarks>
[TestClass]
public sealed class CatalogueConnectionTests
{
    /// <summary>
    /// Everything that may use the backup's connection: the backup itself,
    /// the runtime opening the archive and rebuilding its catalogue before
    /// the handle is shared, and the handle disposing it.
    /// </summary>
    private static readonly string[] Allowed =
    [
        "FallbackPlan.Agent.BackupRunner::",
        "FallbackPlan.Agent.ServiceRuntime::ArchiveForAsync",
        "FallbackPlan.Agent.ServiceRuntime::RebuildCatalogueAsync",
        "FallbackPlan.Agent.ArchiveHandle::Dispose",
    ];

    [TestMethod]
    public void TheBackupsCatalogueConnection_IsUsedByTheBackupAndTheArchivesOpeningAlone()
    {
        // The service's own assembly alone: no other source assembly
        // references it, so nothing else can reach the handle.
        var connection = typeof(ArchiveHandle).GetProperty(nameof(ArchiveHandle.Catalogue))!.GetMethod!;
        var callers = CallSites.Of(connection, typeof(ArchiveHandle).Assembly);

        // The scan has to find the use that is allowed, or finding nothing
        // else proves nothing.
        Assert.IsTrue(
            callers.Any(caller => caller.StartsWith(Allowed[0], StringComparison.Ordinal)),
            $"the scan did not find the backup's own use of its connection; it found: {string.Join(", ", callers)}");

        var others = callers
            .Where(caller => !Allowed.Any(allowed => allowed.EndsWith("::", StringComparison.Ordinal)
                ? caller.StartsWith(allowed, StringComparison.Ordinal)
                : string.Equals(caller, allowed, StringComparison.Ordinal)))
            .ToList();
        Assert.IsEmpty(
            others,
            "these reach the connection the set's backup reads and writes through, and may run while it "
            + "does; each must open a catalogue connection of its own: " + string.Join(", ", others));
    }
}
