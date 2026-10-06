namespace FallbackPlan.Web;

/// <summary>
/// Slows a run of wrong passphrases at the console's gate (FR-WOR-007,
/// ADR-0089), one count per signed-in account, whichever session it signed in
/// with: three slips are free, then each further try waits twice as long as
/// the one before, up to half a minute, and a passphrase that opens something
/// clears the count. An account's tries take turns, so a burst of them cannot
/// all be judged against the same free count. Nothing locks: the passphrase's
/// owner always gets in, later at worst — the posture FR-USR-005 takes for
/// passwords.
/// </summary>
/// <remarks>
/// What this deters is guessing at the console. It cannot stop guessing
/// offline against the sealing key the service publishes to anyone signed
/// in; against that, the passphrase's own strength under Argon2id is the
/// defence, as it is against anyone holding a copy of the backup.
/// </remarks>
public sealed class RestoreGateThrottle
{
    /// <summary>The wrong tries that cost nothing: people mistype.</summary>
    private const int FreeSlips = 3;

    /// <summary>The longest any try waits.</summary>
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);

    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _wrong = new(StringComparer.Ordinal);

    // One turn per account, kept for the life of the console: the accounts
    // are the service's few, and an installation with none has one key.
    private readonly Dictionary<string, SemaphoreSlim> _turns = new(StringComparer.Ordinal);

    /// <summary>A throttle that waits by <paramref name="wait"/>; real time when none is given.</summary>
    /// <param name="wait">How a try waits its turn.</param>
    public RestoreGateThrottle(Func<TimeSpan, CancellationToken, Task>? wait = null) =>
        _wait = wait ?? Task.Delay;

    /// <summary>How long a try waits after <paramref name="consecutiveWrong"/> wrong ones in a row.</summary>
    /// <param name="consecutiveWrong">The wrong tries since the last that opened something.</param>
    /// <returns>The wait.</returns>
    public static TimeSpan DelayAfter(int consecutiveWrong)
    {
        var beyond = consecutiveWrong - FreeSlips;
        return beyond < 0 ? TimeSpan.Zero
            : beyond >= 5 ? Cap
            : TimeSpan.FromSeconds(Math.Min(Cap.TotalSeconds, 1 << beyond));
    }

    /// <summary>
    /// Takes <paramref name="key"/>'s turn: waits for the account's try still
    /// being judged, if any, and then as long as its run of wrong tries says.
    /// Dispose the turn once the try has been counted.
    /// </summary>
    /// <param name="key">The account trying.</param>
    /// <param name="cancellationToken">Ends the wait with the request.</param>
    /// <returns>The turn, held until disposed.</returns>
    public async Task<IDisposable> TakeTurnAsync(string key, CancellationToken cancellationToken)
    {
        SemaphoreSlim? turn;
        lock (_gate)
        {
            if (!_turns.TryGetValue(key, out turn))
            {
                turn = new SemaphoreSlim(1, 1);
                _turns[key] = turn;
            }
        }

        await turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        var taken = false;
        try
        {
            int wrong;
            lock (_gate)
            {
                wrong = _wrong.GetValueOrDefault(key);
            }

            var delay = DelayAfter(wrong);
            if (delay > TimeSpan.Zero)
            {
                await _wait(delay, cancellationToken).ConfigureAwait(false);
            }

            taken = true;
            return new Turn(turn);
        }
        finally
        {
            if (!taken)
            {
                turn.Release();
            }
        }
    }

    /// <summary>Counts a wrong try against <paramref name="key"/>.</summary>
    /// <param name="key">The account that tried.</param>
    public void Wrong(string key)
    {
        lock (_gate)
        {
            _wrong[key] = Math.Min(_wrong.GetValueOrDefault(key) + 1, FreeSlips + 5);
        }
    }

    /// <summary>Clears <paramref name="key"/>'s count: its passphrase opened something.</summary>
    /// <param name="key">The account that tried.</param>
    public void Opened(string key)
    {
        lock (_gate)
        {
            _wrong.Remove(key);
        }
    }

    /// <summary>An account's turn at the gate, given back once.</summary>
    private sealed class Turn(SemaphoreSlim turn) : IDisposable
    {
        private int _returned;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
            {
                turn.Release();
            }
        }
    }
}
