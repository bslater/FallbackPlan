using Bodu;
using System.Text.Json;
using System.Text.Json.Serialization;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Agent;

/// <summary>What an account may do. One role today; the field exists so a second is a value, not a migration.</summary>
public enum UserRole
{
    /// <summary>The first account. Cannot be deleted, and is initially the only one that may manage accounts.</summary>
    Owner = 0,

    /// <summary>Every other account. Equal rights over backup, restore and configuration (ADR-0045 §6).</summary>
    Operator = 1,
}

/// <summary>One account, as a client is allowed to see it — never the hash.</summary>
/// <param name="Name">The account name as typed when it was created.</param>
/// <param name="Role">Its role.</param>
/// <param name="CreatedAtUnixMilliseconds">When it was created.</param>
public sealed record UserDescriptor(string Name, UserRole Role, long CreatedAtUnixMilliseconds);

/// <summary>Why an account operation was refused, so a caller can answer without parsing a message.</summary>
public enum UserStoreOutcome
{
    /// <summary>It worked.</summary>
    Ok = 0,

    /// <summary>No account by that name.</summary>
    NoSuchUser,

    /// <summary>An account by that name already exists.</summary>
    NameTaken,

    /// <summary>The name is empty, too long, or holds a character an account name may not.</summary>
    NameRefused,

    /// <summary>The password is empty or below the floor.</summary>
    PasswordRefused,

    /// <summary>The password did not match.</summary>
    WrongPassword,

    /// <summary>The owner cannot be deleted (ADR-0045 §6).</summary>
    OwnerIsPermanent,

    /// <summary>Only the owner may create or remove accounts today.</summary>
    NotPermitted,

    /// <summary>Too many recent failures for this account; a delay was imposed (FR-USR-005).</summary>
    Throttled,
}

/// <summary>The result of an account operation.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="User">The account, when there is one to report.</param>
public sealed record UserStoreResult(UserStoreOutcome Outcome, UserDescriptor? User = null)
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool IsOk => Outcome == UserStoreOutcome.Ok;
}

/// <summary>
/// The installation's accounts, over <c>&lt;state&gt;/users.json</c>
/// (FR-USR-001..005, NFR-SEC-012; ADR-0045 §3, §6, §7).
/// </summary>
/// <remarks>
/// <para>
/// This is the service's own state, not the repository metadata plane, and the
/// distinction is not stylistic: metadata replicates to destinations, so a
/// credential file there would be copied to every peer and every local-path
/// replica by the ordinary fan-out.
/// </para>
/// <para>
/// The store holds the <em>policy</em> — the owner rules and the throttle —
/// while the hashing itself is <see cref="PasswordHash"/> in the cryptography
/// assembly. That is the same split <c>KekDerivation</c> already has, and it is
/// what let the primitive stay inside the third-party-cryptography allowlist
/// without widening it (ADR-0045 §4).
/// </para>
/// <para>
/// <b>Throttle state is deliberately not persisted.</b> It exists to slow live
/// guessing, and live guessing does not survive a restart either. Writing it
/// down would mean an attacker's failures outlive the process and become a way
/// to slow an account's owner down tomorrow, which is the lockout this design
/// refuses (ADR-0045 §7) wearing different clothes.
/// </para>
/// </remarks>
public sealed class UserStore
{
    /// <summary>The schema this writes. A future field is a version, not a guess.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The longest an account name may be.</summary>
    public const int MaximumNameLength = 64;

    /// <summary>The shortest a password may be.</summary>
    /// <remarks>
    /// Lower than the passphrase floor on purpose. A passphrase is the master
    /// key for the whole installation and is unrecoverable; a password
    /// authorises a person on a channel the operating system or a pinned
    /// pairing has already admitted, is changeable, and is rate-limited by a
    /// memory-hard hash and the throttle below. Holding both to the same
    /// number would imply they carry the same weight, and they do not.
    /// </remarks>
    public const int MinimumPasswordLength = 8;

    /// <summary>The delay added after the first consecutive failure; it doubles from there.</summary>
    public static readonly TimeSpan FirstFailureDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>The cap the doubling stops at (ADR-0045 §7 — "a few seconds").</summary>
    public static readonly TimeSpan MaximumFailureDelay = TimeSpan.FromSeconds(4);

    /// <summary>The throttle's two numbers, so a test can prove the shape without sleeping through it.</summary>
    /// <param name="First">The delay the first consecutive failure earns for the next attempt.</param>
    /// <param name="Maximum">The cap the doubling stops at.</param>
    /// <remarks>
    /// Configurable for the test suite and nowhere else — no configuration file
    /// or command surface reaches this. A throttle an operator can turn down is
    /// a throttle an attacker asks them to turn down.
    /// </remarks>
    public sealed record ThrottlePolicy(TimeSpan First, TimeSpan Maximum)
    {
        /// <summary>What a service uses.</summary>
        public static readonly ThrottlePolicy Default = new(FirstFailureDelay, MaximumFailureDelay);
    }

    private sealed record StoredUser
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("role")]
        public required string Role { get; init; }

        [JsonPropertyName("password")]
        public required string Password { get; init; }

        [JsonPropertyName("created_at")]
        public required long CreatedAtUnixMilliseconds { get; init; }
    }

    private sealed record Model
    {
        [JsonPropertyName("schema_version")]
        public required int SchemaVersion { get; init; }

        [JsonPropertyName("users")]
        public IReadOnlyList<StoredUser> Users { get; init; } = [];
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Failures> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<long> _clock;
    private readonly ThrottlePolicy _throttle;
    private List<StoredUser> _users;

    private sealed class Failures
    {
        public int Consecutive;
    }

    /// <summary>
    /// A hash no password matches, derived once, so an unknown account costs
    /// the same as a known one. Lazy because most installations never meet a
    /// login for an account that does not exist, and this is a memory-hard
    /// derivation to pay for at start-up on the chance that they do.
    /// </summary>
    private static readonly Lazy<PasswordHash> TimingEqualiser =
        new(() => PasswordHash.Create(Convert.ToHexStringLower(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));

    /// <summary>
    /// How many distinct names the throttle will track. Someone trying a
    /// dictionary of usernames must not be able to grow this without bound;
    /// past the cap a novel name simply is not throttled, which costs nothing,
    /// because a name with no account behind it cannot be guessed into.
    /// </summary>
    private const int MaximumThrottledNames = 1024;

    private UserStore(string path, List<StoredUser> users, Func<long> clock, ThrottlePolicy throttle)
    {
        _path = path;
        _users = users;
        _clock = clock;
        _throttle = throttle;
    }

    /// <summary>
    /// Opens the store for <paramref name="stateDirectory"/>, creating nothing.
    /// </summary>
    /// <param name="stateDirectory">The service's state directory.</param>
    /// <param name="clock">Unix milliseconds, for tests that need a fixed one.</param>
    /// <param name="throttle">The failure-delay schedule, or the one a service uses.</param>
    /// <exception cref="InvalidDataException">The file exists and does not parse.</exception>
    public static UserStore Open(
        string stateDirectory, Func<long>? clock = null, ThrottlePolicy? throttle = null)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);

        var path = Path.Combine(stateDirectory, "users.json");
        var users = new List<StoredUser>();

        if (File.Exists(path))
        {
            Model? model;
            try
            {
                model = JsonSerializer.Deserialize<Model>(File.ReadAllText(path), SerializerOptions);
            }
            catch (JsonException exception)
            {
                // Named, not silently replaced. Starting fresh would present an
                // installation that has accounts as one that has none, and the
                // first person to arrive would become its owner.
                throw new InvalidDataException(
                    $"The account file at '{path}' does not parse: {exception.Message}. "
                    + "Restore the state directory rather than deleting it — an installation with no "
                    + "accounts admits the next caller as its owner (ADR-0045 §6).",
                    exception);
            }

            if (model is null || model.SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"The account file at '{path}' is schema version "
                    + $"{model?.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "absent"}; "
                    + $"this service speaks {CurrentSchemaVersion}.");
            }

            users.AddRange(model.Users);
        }

        return new UserStore(
            path,
            users,
            clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            throttle ?? ThrottlePolicy.Default);
    }

    /// <summary>Whether any account exists. False means setup is not finished (FR-USR-001).</summary>
    public bool HasAccounts
    {
        get
        {
            lock (_gate)
            {
                return _users.Count > 0;
            }
        }
    }

    /// <summary>Every account, oldest first, without hashes.</summary>
    public IReadOnlyList<UserDescriptor> List()
    {
        lock (_gate)
        {
            return [.. _users.Select(Describe)];
        }
    }

    /// <summary>Looks one up by name, case-insensitively.</summary>
    public UserDescriptor? Find(string name)
    {
        lock (_gate)
        {
            var found = FindStored(name);
            return found is null ? null : Describe(found);
        }
    }

    /// <summary>
    /// Creates an account. The first one created is the owner, whatever
    /// <paramref name="role"/> asks for — an installation whose first account
    /// was not the owner would have no account that cannot be deleted.
    /// </summary>
    /// <param name="name">The account name, as typed.</param>
    /// <param name="password">The password.</param>
    /// <param name="role">The role for accounts after the first.</param>
    /// <param name="parameters">Hash cost, or the creation minimums.</param>
    public UserStoreResult Create(
        string name,
        string password,
        UserRole role = UserRole.Operator,
        Domain.Configuration.Argon2Parameters? parameters = null)
    {
        if (!IsNameAcceptable(name))
        {
            return new UserStoreResult(UserStoreOutcome.NameRefused);
        }

        if (password is null || password.Length < MinimumPasswordLength)
        {
            return new UserStoreResult(UserStoreOutcome.PasswordRefused);
        }

        // Hashed outside the lock: it is memory-hard by design, and holding a
        // lock across it would let one account creation stall every listing.
        var hash = PasswordHash.Create(password, parameters).Encode();

        lock (_gate)
        {
            if (FindStored(name) is not null)
            {
                return new UserStoreResult(UserStoreOutcome.NameTaken);
            }

            var stored = new StoredUser
            {
                Name = name.Trim(),
                Role = (_users.Count == 0 ? UserRole.Owner : role).ToString(),
                Password = hash,
                CreatedAtUnixMilliseconds = _clock(),
            };

            _users = [.. _users, stored];
            Save();
            return new UserStoreResult(UserStoreOutcome.Ok, Describe(stored));
        }
    }

    /// <summary>
    /// Verifies a password, applying the failure throttle (FR-USR-005).
    /// </summary>
    /// <param name="name">The account name.</param>
    /// <param name="password">The password offered.</param>
    /// <param name="cancellationToken">Cancels the throttle's wait.</param>
    /// <remarks>
    /// An unknown account still pays the delay and still takes the time a
    /// derivation would: answering "no such user" instantly turns the login
    /// into a name oracle.
    /// </remarks>
    public async ValueTask<UserStoreResult> VerifyAsync(
        string name, string password, CancellationToken cancellationToken)
    {
        string? encoded;
        StoredUser? stored;

        lock (_gate)
        {
            stored = FindStored(name);
            encoded = stored?.Password;
        }

        var delay = NextDelay(name);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        // Exactly one derivation either way. An unknown account is verified
        // against a fixed hash no password will ever match, so answering "no
        // such user" does not come back faster than answering "wrong password"
        // and turn the login into a name oracle. Best-effort rather than
        // airtight: an account stored under different cost parameters takes a
        // different time, and no amount of equalising here changes that.
        var accepted = PasswordHash.TryParse(encoded, out var hash)
            ? hash!.Verify(password ?? string.Empty)
            : TimingEqualiser.Value.Verify(password ?? string.Empty) && encoded is not null;

        if (!accepted)
        {
            RecordFailure(name);
            return new UserStoreResult(
                delay > TimeSpan.Zero ? UserStoreOutcome.Throttled : UserStoreOutcome.WrongPassword);
        }

        ClearFailures(name);
        return new UserStoreResult(UserStoreOutcome.Ok, Describe(stored!));
    }

    /// <summary>Changes an account's password, once the current one is proved.</summary>
    public async ValueTask<UserStoreResult> ChangePasswordAsync(
        string name, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var proved = await VerifyAsync(name, currentPassword, cancellationToken).ConfigureAwait(false);
        if (!proved.IsOk)
        {
            return proved;
        }

        if (newPassword is null || newPassword.Length < MinimumPasswordLength)
        {
            return new UserStoreResult(UserStoreOutcome.PasswordRefused);
        }

        var hash = PasswordHash.Create(newPassword).Encode();

        lock (_gate)
        {
            var stored = FindStored(name);
            if (stored is null)
            {
                return new UserStoreResult(UserStoreOutcome.NoSuchUser);
            }

            _users = [.. _users.Select(user => ReferenceEquals(user, stored)
                ? user with { Password = hash }
                : user)];
            Save();
            return new UserStoreResult(UserStoreOutcome.Ok, Describe(stored));
        }
    }

    /// <summary>Deletes an account. The owner is refused (FR-USR-004).</summary>
    public UserStoreResult Delete(string name)
    {
        lock (_gate)
        {
            var stored = FindStored(name);
            if (stored is null)
            {
                return new UserStoreResult(UserStoreOutcome.NoSuchUser);
            }

            if (RoleOf(stored) == UserRole.Owner)
            {
                return new UserStoreResult(UserStoreOutcome.OwnerIsPermanent, Describe(stored));
            }

            _users = [.. _users.Where(user => !ReferenceEquals(user, stored))];
            Save();
            return new UserStoreResult(UserStoreOutcome.Ok, Describe(stored));
        }
    }

    /// <summary>Whether <paramref name="name"/> may create and remove accounts (FR-USR-004).</summary>
    public bool MayManageAccounts(string name)
    {
        lock (_gate)
        {
            return FindStored(name) is { } stored && RoleOf(stored) == UserRole.Owner;
        }
    }

    /// <summary>
    /// The delay this account's next failure would incur, for a test or a
    /// diagnostic. Zero when nothing recent has failed.
    /// </summary>
    public TimeSpan PendingDelayFor(string name)
    {
        lock (_gate)
        {
            return _failures.TryGetValue(name ?? string.Empty, out var failures)
                ? DelayFor(failures.Consecutive)
                : TimeSpan.Zero;
        }
    }

    private static bool IsNameAcceptable(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaximumNameLength)
        {
            return false;
        }

        // No control characters and no whitespace inside: an account name goes
        // into a log line, a refusal message and a receipt, and one that can
        // carry a newline can forge a second line in all three.
        return name.Trim().All(character =>
            !char.IsControl(character) && !char.IsWhiteSpace(character));
    }

    private static UserRole RoleOf(StoredUser stored) =>
        Enum.TryParse<UserRole>(stored.Role, ignoreCase: true, out var role)
            ? role

            // An unrecognised role reads as the narrowest one that exists,
            // never as the owner: a file written by a later version that knows
            // roles this one does not must not be read as granting more.
            : UserRole.Operator;

    private static UserDescriptor Describe(StoredUser stored) =>
        new(stored.Name, RoleOf(stored), stored.CreatedAtUnixMilliseconds);

    private StoredUser? FindStored(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : _users.FirstOrDefault(user =>
                string.Equals(user.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    private TimeSpan DelayFor(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.Zero;
        }

        // Doubling from the first failure, capped. Computed in ticks rather
        // than by repeated multiplication so a long run of failures cannot
        // overflow its way back to a short delay.
        var doublings = Math.Min(consecutiveFailures - 1, 16);
        var ticks = _throttle.First.Ticks * (1L << doublings);
        return TimeSpan.FromTicks(Math.Min(ticks, _throttle.Maximum.Ticks));
    }

    private TimeSpan NextDelay(string? name)
    {
        lock (_gate)
        {
            return _failures.TryGetValue(name ?? string.Empty, out var failures)
                ? DelayFor(failures.Consecutive)
                : TimeSpan.Zero;
        }
    }

    private void RecordFailure(string? name)
    {
        lock (_gate)
        {
            var key = name ?? string.Empty;
            if (!_failures.TryGetValue(key, out var failures))
            {
                if (_failures.Count >= MaximumThrottledNames)
                {
                    return;
                }

                failures = new Failures();
                _failures[key] = failures;
            }

            failures.Consecutive++;
        }
    }

    private void ClearFailures(string? name)
    {
        lock (_gate)
        {
            _failures.Remove(name ?? string.Empty);
        }
    }

    private void Save()
    {
        var model = new Model { SchemaVersion = CurrentSchemaVersion, Users = _users };
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(model, SerializerOptions));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
