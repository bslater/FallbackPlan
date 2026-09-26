using FallbackPlan.Protocol;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// The bound on a receipt pile (NFR-OPS-008; FR-GC-008;
/// [ADR-0063](../../docs/adr/0063-deletion-receipts.md),
/// [ADR-0064](../../docs/adr/0064-replication-receipts.md)). One receipt is
/// filed per exchange at both ends and nothing used to remove one, so a pair
/// that pushes hourly filed a file an hour for ever. The rule is three
/// numbers over the file names — the newest few survive any age, nothing
/// survives past the ceiling, and what lies between goes when it is older
/// than the window — and the sweep reads no receipt to apply it.
/// </summary>
[TestClass]
public sealed class ReceiptSweepTests : IDisposable
{
    private const ulong Day = 24UL * 3_600_000UL;

    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "fbp-receipt-sweep-tests", Guid.NewGuid().ToString("n"));

    // The real clock, because filing applies the real policy as it writes: a
    // fixture dated to 1972 would have every receipt outside the 365-day
    // window before the case under test ever ran.
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static readonly byte[] RepositoryId =
        [.. Enumerable.Repeat((byte)0x01, ReplicationOffer.RepositoryIdLength)];

    private static string RepositoryIdHex => Convert.ToHexStringLower(RepositoryId);

    private DeletionReceiptStore Open() => DeletionReceiptStore.Open(_stateDirectory);

    private string Directory() => Path.Combine(_stateDirectory, "receipts", "deletions", RepositoryIdHex);

    private static DeletionReceipt Receipt(ulong issuedAt) => new(
        SessionId: Enumerable.Repeat((byte)(issuedAt % 251), DeletionReceipt.SessionIdLength).ToArray(),
        RepositoryId: RepositoryId,
        CommanderPublicKey: Enumerable.Repeat((byte)0xC0, PeerIdentity.KeyLength).ToArray(),
        IssuedAtUnixMilliseconds: issuedAt,
        FloorGenerations: 0,
        ReclaimPublicKey: ReadOnlyMemory<byte>.Empty,
        PageDigests: [new byte[DeletionReceipt.DigestLength]],
        DeletedCount: 1,
        Deleted: ["snapshots/aa/bb/gone"],
        NotHeld: 0);

    /// <summary>Files one receipt issued at <paramref name="issuedAt"/> and answers its path.</summary>
    private string FileOne(PeerKeypair signer, ulong issuedAt)
    {
        var signed = Receipt(issuedAt).EncodeForSigning();
        return Open().File(
            DeletionReceiptRole.Destination, signed, signer.Sign(signed), signer.Identity,
            set: null, destination: null);
    }

    private string[] Names() =>
        [.. System.IO.Directory.GetFiles(Directory()).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    [TestMethod]
    public void Sweep_TheNewestFew_SurviveAnyAge()
    {
        using var signer = PeerKeypair.Generate();
        var policy = new ReceiptRetentionPolicy(MinimumRetained: 3, MaximumRetained: 100, RetainedDays: 30);
        var ancient = (ulong)Now.ToUnixTimeMilliseconds() - (900UL * Day);
        for (var i = 0UL; i < 3; i++)
        {
            _ = FileOne(signer, ancient + i);
        }

        var swept = Open().Sweep(policy, Now);

        Assert.AreEqual(0, swept, "the minimum is kept whatever its age: a pair that has gone quiet still has a history");
        Assert.HasCount(3, Names());
    }

    [TestMethod]
    public void Sweep_PastTheCeiling_GoesHoweverNew()
    {
        using var signer = PeerKeypair.Generate();
        var policy = new ReceiptRetentionPolicy(MinimumRetained: 2, MaximumRetained: 5, RetainedDays: 3_650);
        var issued = (ulong)Now.ToUnixTimeMilliseconds() - (10UL * Day);
        for (var i = 0UL; i < 9; i++)
        {
            _ = FileOne(signer, issued + i);
        }

        var swept = Open().Sweep(policy, Now);

        Assert.AreEqual(4, swept);
        var names = Names();
        Assert.HasCount(5, names);
        CollectionAssert.AreEqual(
            Enumerable.Range(4, 5).Select(i => $"{issued + (ulong)i:D20}").ToArray(),
            names.Select(name => name[..20]).ToArray(),
            "the ceiling keeps the newest five, not the first five filed");
    }

    [TestMethod]
    public void Sweep_InsideTheWindowAndTheCeiling_KeepsEverything()
    {
        using var signer = PeerKeypair.Generate();
        var policy = new ReceiptRetentionPolicy(MinimumRetained: 2, MaximumRetained: 100, RetainedDays: 30);
        var issued = (ulong)Now.ToUnixTimeMilliseconds() - (5UL * Day);
        for (var i = 0UL; i < 6; i++)
        {
            _ = FileOne(signer, issued + i);
        }

        Assert.AreEqual(0, Open().Sweep(policy, Now));
        Assert.HasCount(6, Names());
    }

    [TestMethod]
    public void Sweep_OlderThanTheWindowAndPastTheMinimum_Goes()
    {
        using var signer = PeerKeypair.Generate();
        var policy = new ReceiptRetentionPolicy(MinimumRetained: 2, MaximumRetained: 100, RetainedDays: 30);
        var nowMs = (ulong)Now.ToUnixTimeMilliseconds();
        for (var i = 0UL; i < 3; i++)
        {
            _ = FileOne(signer, nowMs - (200UL * Day) + i);
        }

        for (var i = 0UL; i < 2; i++)
        {
            _ = FileOne(signer, nowMs - (2UL * Day) + i);
        }

        var swept = Open().Sweep(policy, Now);

        Assert.AreEqual(3, swept);
        Assert.HasCount(2, Names());
        Assert.IsTrue(
            Names().All(name => string.CompareOrdinal(name[..20], $"{nowMs - (10UL * Day):D20}") > 0),
            "the three ancient receipts went and the two recent ones stayed");
    }

    [TestMethod]
    public void Sweep_AFileThatIsNotAReceiptsName_IsLeftAlone()
    {
        using var signer = PeerKeypair.Generate();
        var policy = new ReceiptRetentionPolicy(MinimumRetained: 1, MaximumRetained: 2, RetainedDays: 1);
        var nowMs = (ulong)Now.ToUnixTimeMilliseconds();
        for (var i = 0UL; i < 4; i++)
        {
            _ = FileOne(signer, nowMs - (200UL * Day) + i);
        }

        var notes = Path.Combine(Directory(), "notes.txt");
        var stray = Path.Combine(Directory(), "receipt.json");
        File.WriteAllText(notes, "an operator's own note");
        File.WriteAllText(stray, "{}");

        _ = Open().Sweep(policy, Now);

        Assert.IsTrue(File.Exists(notes), "a file whose name is not a receipt's is not this sweep's to delete");
        Assert.IsTrue(File.Exists(stray));
    }

    [TestMethod]
    public void Sweep_AReceiptThatNoLongerParses_IsAgedOutLikeAnySound()
    {
        using var signer = PeerKeypair.Generate();
        var policy = new ReceiptRetentionPolicy(MinimumRetained: 1, MaximumRetained: 100, RetainedDays: 30);
        var nowMs = (ulong)Now.ToUnixTimeMilliseconds();
        var rotten = FileOne(signer, nowMs - (200UL * Day));
        File.WriteAllText(rotten, "this is not an envelope");
        _ = FileOne(signer, nowMs);

        var swept = Open().Sweep(policy, Now);

        Assert.AreEqual(1, swept);
        Assert.IsFalse(File.Exists(rotten), "the sweep reads names, so a pile that has gone unreadable is still bounded");
    }

    [TestMethod]
    public void Sweep_AfterTheStateDirectoryWasCopied_StillAgesByTheNameAndNotTheFilesystem()
    {
        using var signer = PeerKeypair.Generate();
        var policy = new ReceiptRetentionPolicy(MinimumRetained: 1, MaximumRetained: 100, RetainedDays: 30);
        var nowMs = (ulong)Now.ToUnixTimeMilliseconds();
        var ancient = FileOne(signer, nowMs - (200UL * Day));
        _ = FileOne(signer, nowMs);

        // Copying a state directory — a restore, a migration, a backup of the
        // backup service — stamps every file with the copy's own time. Age
        // taken from the filesystem would then make the whole pile look new
        // and the bound would quietly stop applying.
        foreach (var path in System.IO.Directory.GetFiles(Directory()))
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }

        Assert.AreEqual(1, Open().Sweep(policy, Now));
        Assert.IsFalse(File.Exists(ancient), "the issue time the name carries is the age, not the file's own stamp");
    }

    [TestMethod]
    public void Sweep_ARootThatWasNeverWritten_IsANoOp() =>
        Assert.AreEqual(0, Open().Sweep(DeletionReceiptStore.Policy, Now));

    [TestMethod]
    public void File_ARecentOne_SweepsWhatTheWindowHasPassed()
    {
        using var signer = PeerKeypair.Generate();
        var nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ancient = FileOne(signer, nowMs - (2_000UL * Day));
        Assert.IsTrue(File.Exists(ancient));

        // Nobody calls Sweep here: the bound holds because filing applies it,
        // which is what keeps a live pair bounded between restarts.
        for (var i = 0UL; i <= (ulong)DeletionReceiptStore.Policy.MinimumRetained; i++)
        {
            _ = FileOne(signer, nowMs + i);
        }

        Assert.IsFalse(File.Exists(ancient));
        Assert.HasCount(DeletionReceiptStore.Policy.MinimumRetained + 1, Names());
    }

    [TestMethod]
    public void Policies_KeepADeletionLongerThanAReplication()
    {
        // A replication receipt attests what the peer holds now and is
        // superseded by the next push's; a deletion receipt attests a distinct
        // irreversible act and is FR-GC-008's audit record.
        Assert.IsTrue(DeletionReceiptStore.Policy.RetainedDays > ReplicationReceiptStore.Policy.RetainedDays);
        Assert.IsTrue(DeletionReceiptStore.Policy.MaximumRetained > ReplicationReceiptStore.Policy.MaximumRetained);
        Assert.AreEqual(DeletionReceiptStore.Policy.MinimumRetained, ReplicationReceiptStore.Policy.MinimumRetained);
    }

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(_stateDirectory))
            {
                System.IO.Directory.Delete(_stateDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temporary directory that will not go is the operating
            // system's business, not the test's verdict.
        }
    }
}
