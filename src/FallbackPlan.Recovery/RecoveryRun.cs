using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Recovery;

/// <summary>How far a recovery run got.</summary>
internal enum RecoveryStage
{
    /// <summary>Reading its own command line.</summary>
    Options = 0,

    /// <summary>Reading the archive's descriptor.</summary>
    Descriptor = 1,

    /// <summary>Deriving the archive's keys from the passphrase.</summary>
    Passphrase = 2,

    /// <summary>Opening every blob through its recovery footer.</summary>
    Blobs = 3,

    /// <summary>Listing the snapshots.</summary>
    Snapshots = 4,

    /// <summary>Restoring a snapshot's tree.</summary>
    Restore = 5,
}

/// <summary>Why a recovery run stopped.</summary>
internal enum RecoveryFailureReason
{
    /// <summary>A required option was not given.</summary>
    MissingOption = 0,

    /// <summary>A flag from the withdrawn recovery kit was given.</summary>
    KitWithdrawn = 1,

    /// <summary>The variable named for the passphrase was unset or empty.</summary>
    PassphraseVariableUnset = 2,

    /// <summary>The verb is not one the tool has.</summary>
    UnknownVerb = 3,

    /// <summary>The location holds no archive.</summary>
    NotAnArchive = 4,

    /// <summary>The descriptor's digest does not cover its bytes.</summary>
    DescriptorIntegrity = 5,

    /// <summary>The descriptor breaks a rule of the format.</summary>
    DescriptorFormatViolation = 6,

    /// <summary>The archive requires features this tool does not implement.</summary>
    UnsupportedFeatures = 7,

    /// <summary>The passphrase does not reproduce the archive's keys.</summary>
    PassphraseRefused = 8,

    /// <summary>The snapshot asked for is not among those the archive lists.</summary>
    SnapshotNotFound = 9,

    /// <summary>A value given on the command line did not parse.</summary>
    InvalidInput = 10,

    /// <summary>The platform refused a read or a write.</summary>
    Io = 11,

    /// <summary>Nothing anticipated it.</summary>
    Unexpected = 12,

    /// <summary>The person stopped the run.</summary>
    Cancelled = 13,
}

/// <summary>What the archive's descriptor said, or why it said nothing.</summary>
internal enum DescriptorOutcome
{
    /// <summary>The run stopped before reading it.</summary>
    NotRead = 0,

    /// <summary>It parsed and verified.</summary>
    Ok = 1,

    /// <summary>There is no descriptor object where an archive keeps one.</summary>
    Missing = 2,

    /// <summary>The object there is not a descriptor.</summary>
    NotARepository = 3,

    /// <summary>Its digest does not cover its bytes.</summary>
    IntegrityFailure = 4,

    /// <summary>It breaks a rule of the format.</summary>
    FormatViolation = 5,

    /// <summary>It requires features this tool does not implement.</summary>
    UnsupportedFeatures = 6,
}

/// <summary>Whether the passphrase reproduced the archive's keys.</summary>
internal enum PassphraseOutcome
{
    /// <summary>The run stopped before trying it.</summary>
    NotAttempted = 0,

    /// <summary>It reproduced them.</summary>
    Reproduced = 1,

    /// <summary>It did not.</summary>
    Refused = 2,
}

/// <summary>Why a run stopped, as the bundle reports it.</summary>
/// <param name="Stage">How far the run got.</param>
/// <param name="Reason">Why it stopped.</param>
/// <param name="Option">The option a refusal is about, in the tool's own spelling.</param>
/// <param name="ExceptionType">The exception's type, which is the code's name for it.</param>
/// <param name="Message">The exception's message, which no type classifies.</param>
internal sealed record RecoveryFailure(
    RecoveryStage Stage, RecoveryFailureReason Reason, string? Option, string ExceptionType, string Message);

/// <summary>
/// What one run of the recovery tool did and found, for its diagnostic
/// bundle (ADR-0082).
/// </summary>
/// <remarks>
/// It holds only what the bundle classifies. The passphrase never reaches it,
/// and nor does the name of the variable holding it, only whether one was
/// named and set. From the descriptor it takes the fields that describe the
/// archive and leaves the ones that identify an installation: the salt and the
/// sealing public key are the same in every archive an installation writes,
/// and the creator is free text, which the full client fills with the
/// machine's name.
/// </remarks>
internal sealed class RecoveryRun(bool includePaths, LogLevel logLevel)
{
    private (RecoveryFailureReason Reason, string? Option)? _refusal;

    /// <summary>Whether the person opted in to paths for this run's bundle.</summary>
    public bool IncludePaths { get; } = includePaths;

    /// <summary>The log level in force.</summary>
    public LogLevel LogLevel { get; } = logLevel;

    /// <summary>The verb as typed.</summary>
    public string? Verb { get; set; }

    /// <summary>The archive's location, as given.</summary>
    public string? Repo { get; set; }

    /// <summary>Whether a variable was named for the passphrase.</summary>
    public bool PassphraseVariableNamed { get; set; }

    /// <summary>Whether that variable held a value, once it was read.</summary>
    public bool? PassphraseVariableSet { get; set; }

    /// <summary>The snapshot asked for, as typed.</summary>
    public string? Snapshot { get; set; }

    /// <summary>The output folder, as given.</summary>
    public string? Output { get; set; }

    /// <summary>How far the run got.</summary>
    public RecoveryStage Stage { get; set; }

    /// <summary>What the descriptor said.</summary>
    public DescriptorOutcome Descriptor { get; set; }

    /// <summary>The format version the archive was created at.</summary>
    public int? FormatVersion { get; private set; }

    /// <summary>The format version its owner writes now, once the keys could verify it.</summary>
    public int? EffectiveFormatVersion { get; set; }

    /// <summary>The features the descriptor requires.</summary>
    public IReadOnlyList<ushort>? RequiredFeatures { get; private set; }

    /// <summary>The features the descriptor offers.</summary>
    public IReadOnlyList<ushort>? OptionalFeatures { get; private set; }

    /// <summary>The required features this tool does not implement.</summary>
    public IReadOnlyList<ushort>? UnsupportedFeatures { get; set; }

    /// <summary>The key derivation's cost parameters, which are not secret.</summary>
    public Argon2Parameters? Kdf { get; private set; }

    /// <summary>When the archive was created, in Unix milliseconds.</summary>
    public ulong? CreatedAt { get; private set; }

    /// <summary>Whether the archive was written while its format was unfrozen.</summary>
    public bool? UnstableFormat { get; private set; }

    /// <summary>The archive's repository identity.</summary>
    public RepositoryId? Repository { get; private set; }

    /// <summary>Whether the passphrase reproduced the archive's keys.</summary>
    public PassphraseOutcome Passphrase { get; set; }

    /// <summary>How many blobs opened, once they were loaded.</summary>
    public int? ReadableBlobs { get; private set; }

    /// <summary>The blobs that did not open, once they were loaded.</summary>
    public IReadOnlyList<RecoveryNote>? BlobNotes { get; private set; }

    /// <summary>The snapshots the archive lists, once they were listed.</summary>
    public IReadOnlyList<RecoveredSnapshot>? Snapshots { get; set; }

    /// <summary>What the restore did, once it ran.</summary>
    public RecoveryRestoreReport? Restore { get; set; }

    /// <summary>The run's exit code, when it returned one.</summary>
    public int? ExitCode { get; set; }

    /// <summary>Why the run stopped, when it stopped short.</summary>
    public RecoveryFailure? Failure { get; private set; }

    /// <summary>Takes from a verified descriptor the fields that describe the archive.</summary>
    public void Read(RepositoryDescriptor descriptor)
    {
        Descriptor = DescriptorOutcome.Ok;
        FormatVersion = descriptor.FormatVersion;
        RequiredFeatures = descriptor.RequiredFeatures;
        OptionalFeatures = descriptor.OptionalFeatures;
        Kdf = descriptor.KdfParameters;
        CreatedAt = descriptor.CreatedAt;
        UnstableFormat = descriptor.UnstableFormat;
        Repository = descriptor.RepositoryId;
    }

    /// <summary>Records what the blob inventory found.</summary>
    public void Loaded(int readable, IReadOnlyList<RecoveryNote> notes)
    {
        ReadableBlobs = readable;
        BlobNotes = notes;
    }

    /// <summary>
    /// Records why the run is about to refuse, and returns the failure to
    /// throw: the reason is the tool's, so it is said here rather than
    /// guessed back from the message later.
    /// </summary>
    /// <param name="reason">Why.</param>
    /// <param name="message">What the operator is told.</param>
    /// <param name="option">The option the refusal is about, if it is about one.</param>
    public RecoveryFailureException Refuse(RecoveryFailureReason reason, string message, string? option = null)
    {
        _refusal = (reason, option);
        return new RecoveryFailureException(message);
    }

    /// <summary>Records the exception the run stopped on.</summary>
    public void Fail(Exception exception)
    {
        var (reason, option) = exception switch
        {
            RecoveryFailureException when _refusal is { } refusal => (refusal.Reason, refusal.Option),
            KeyUnwrapFailedException => (RecoveryFailureReason.PassphraseRefused, null),
            RecoveryFailureException when Stage == RecoveryStage.Descriptor => (ReasonFor(Descriptor), null),
            FormatException => (RecoveryFailureReason.InvalidInput, null),
            IOException => (RecoveryFailureReason.Io, null),
            OperationCanceledException => (RecoveryFailureReason.Cancelled, null),
            _ => (RecoveryFailureReason.Unexpected, (string?)null),
        };

        Failure = new RecoveryFailure(Stage, reason, option, exception.GetType().FullName!, exception.Message);
    }

    private static RecoveryFailureReason ReasonFor(DescriptorOutcome outcome) => outcome switch
    {
        DescriptorOutcome.IntegrityFailure => RecoveryFailureReason.DescriptorIntegrity,
        DescriptorOutcome.FormatViolation => RecoveryFailureReason.DescriptorFormatViolation,
        DescriptorOutcome.UnsupportedFeatures => RecoveryFailureReason.UnsupportedFeatures,
        _ => RecoveryFailureReason.NotAnArchive,
    };
}
