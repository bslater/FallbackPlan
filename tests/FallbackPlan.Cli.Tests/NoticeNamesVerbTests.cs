using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Cli;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// <c>notice-names</c> names the backup's files a notice left out (FR-WOR-007,
/// ADR-0089 Amendment 1): it opens a source of the notice's set under the
/// grant <c>--passphrase-env</c> derives, asks through it, and closes it. A
/// notice that names no files is said to, and no passphrase is asked for.
/// </summary>
[TestClass]
public sealed class NoticeNamesVerbTests
{
    private const string ThePassphrase = "the scripted installation's passphrase";

    [TestMethod]
    public async Task NoticeNames_WithThePassphrase_PrintsTheNamesTheNoticeLeftOut_AndClosesItsSource()
    {
        await using var client = new ScriptedClient(namesWithheld: 1);

        var report = await NoticeNamesAsync(client, "n1", passphrase: ThePassphrase);

        Assert.IsTrue(report.Ok, string.Join(" | ", report.Lines));
        Assert.AreEqual("docs/report.txt", Assert.ContainsSingle(report.Lines));
        var asked = Assert.ContainsSingle(client.Received.OfType<NoticeNamesCommand>());
        Assert.AreEqual("n1", asked.NoticeId);
        Assert.AreEqual(ScriptedClient.SourceId, asked.Source, "asked through the source the passphrase unlocked");
        Assert.AreEqual(
            ScriptedClient.SourceId, Assert.ContainsSingle(client.Received.OfType<CloseRestoreSourceCommand>()).SourceId,
            "and that source is closed once the names are read");
    }

    [TestMethod]
    public async Task NoticeNames_ANoticeThatNamesNoFiles_SaysSo_WithoutAskingForThePassphrase()
    {
        await using var client = new ScriptedClient(namesWithheld: 0);

        var report = await NoticeNamesAsync(client, "n1", passphrase: null);

        Assert.IsTrue(report.Ok, string.Join(" | ", report.Lines));
        Assert.Contains("names no files", Assert.ContainsSingle(report.Lines), StringComparison.Ordinal);
        Assert.IsEmpty(client.Received.OfType<OpenRestoreSourceCommand>());
    }

    [TestMethod]
    public async Task NoticeNames_AnUnknownNotice_IsRefusedByItsId()
    {
        await using var client = new ScriptedClient(namesWithheld: 1);

        var refused = await Assert.ThrowsExactlyAsync<CliFailureException>(
            () => NoticeNamesAsync(client, "nonesuch", passphrase: ThePassphrase));

        Assert.Contains("nonesuch", refused.Message, StringComparison.Ordinal);
    }

    private static async Task<OperationReport> NoticeNamesAsync(ScriptedClient client, string noticeId, string? passphrase)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // A variable of this test's own, so no concurrent class reads it.
        var variable = "FBP_TEST_NOTICE_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(variable, passphrase);
        try
        {
            var gateway = new ServiceGateway(client, "scripted", client, passphrase is null ? null : variable);
            return await gateway.NoticeNamesAsync(noticeId, timeout.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// Answers as a set-up installation with one set and one notice about it
    /// (ADR-0042 §7): it publishes the facts a grant is derived under, opens a
    /// source only for a grant that proves the passphrase, and names the
    /// notice's files only through that source.
    /// </summary>
    private sealed class ScriptedClient(int namesWithheld) : IFallbackPlanClient
    {
        public const string SourceId = "scripted-source";

        private static readonly string SetId = new('a', 32);

        private static readonly byte[] RecipientScalar = RandomNumberGenerator.GetBytes(32);

        // The least cost Argon2id takes, which an opening derivation accepts
        // as a published fact, so the derivation stays quick.
        private static readonly Argon2Parameters Cost = new() { MemoryKiB = 64, Iterations = 1, Parallelism = 1 };

        private static readonly byte[] Salt = Enumerable.Repeat((byte)0x5A, KekDerivation.SaltLength).ToArray();

        private static readonly string SealingPublicKey = DeriveSealingPublicKey();

        public List<ServiceCommand> Received { get; } = [];

        public ContractVersion ServiceContractVersion => ContractVersion.Current;

        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken)
        {
            Received.Add(command);
            return ValueTask.FromResult<ServiceResult>(command switch
            {
                DescribeServiceCommand => new ServiceDescriptionResult(
                    ContractVersion.Current.ToString(), "test", "machine", "/state", false, 0,
                    RestoreGrantRecipient: Convert.ToHexStringLower(ContentSealing.PublicKeyOf(RecipientScalar)),
                    KdfSalt: Convert.ToHexStringLower(Salt),
                    KdfMemoryKib: Cost.MemoryKiB,
                    KdfIterations: Cost.Iterations,
                    KdfParallelism: Cost.Parallelism,
                    SealingPublicKey: SealingPublicKey),
                ListBackupSetsCommand => new BackupSetsResult(
                    [new BackupSetDescriptor(SetId, "docs", "/docs", null, [], [], [])]),
                ListNoticesCommand => new NoticesResult(
                    [
                        new NoticeDescriptor(
                            "n1", $"drill-failed:{SetId}:vault", "a sampled file would not restore", 1_000, null,
                            SetId: namesWithheld > 0 ? SetId : null, NamesWithheld: namesWithheld),
                    ]),
                OpenRestoreSourceCommand { Envelope: { } envelope } when Proves(envelope) =>
                    new RestoreSourceOpenedResult(SourceId, "docs", "/archives/docs", [], []),
                OpenRestoreSourceCommand => new ServiceError(ServiceErrorReason.Refused, "no grant proves the passphrase"),
                NoticeNamesCommand { Source: SourceId } => new NoticeNamesResult(["docs/report.txt"]),
                NoticeNamesCommand => new ServiceError(
                    ServiceErrorReason.Refused, "A notice's files need the set's passphrase."),
                _ => new AcknowledgedResult(),
            });
        }

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<JobProgressEvent>();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static string DeriveSealingPublicKey()
        {
            using var passphrase = Passphrase.Create(ThePassphrase);
            using var authority = WriteOnlyDerivation.Derive(passphrase, Cost, Salt, KdfValidationMode.OpenRepository);
            return Convert.ToHexStringLower(authority.Credential.SealingPublicKey);
        }

        private static bool Proves(string envelopeHex)
        {
            var scalar = WriteOnlyProvisioning.OpenGrant(RecipientScalar, Convert.FromHexString(envelopeHex));
            try
            {
                return Convert.ToHexStringLower(ContentSealing.PublicKeyOf(scalar)) == SealingPublicKey;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(scalar);
            }
        }
    }
}
