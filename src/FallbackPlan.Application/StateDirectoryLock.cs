using Bodu;
using FallbackPlan.Domain;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FallbackPlan.Application;

/// <summary>Who holds the writer role, for the benefit of whoever could not take it.</summary>
public sealed record WriterLockOwner
{
    /// <summary>The holder's role: <c>service</c> or <c>cli-direct</c>.</summary>
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    /// <summary>The holder's process name.</summary>
    [JsonPropertyName("process")]
    public required string Process { get; init; }

    /// <summary>The holder's process identifier.</summary>
    [JsonPropertyName("pid")]
    public required int ProcessId { get; init; }

    /// <summary>When the holder acquired the role, ISO-8601.</summary>
    [JsonPropertyName("acquired_at")]
    public required string AcquiredAt { get; init; }
}

/// <summary>
/// The exclusive writer role for one state directory (ADR-0028 §4, FR-SVC-002).
/// </summary>
/// <remarks>
/// <para>
/// Writer identity is per state directory, so any two processes pointed at one
/// are the <i>same writer</i> by construction — drawing from the single
/// monotonic gapless sequence space architecture 04 §2 requires. Nothing
/// enforced that across processes, and the damage escalated from colliding
/// spool paths to a write intent reported durable when it was never written, to
/// void deltas published for sequence numbers another process was still using.
/// Because 04 §2 classifies a duplicate sequence as identity cloning, the first
/// symptom was <b>T-18's security alert</b>: an alarm built for a stolen device
/// key, raised by a user running two commands at once.
/// </para>
/// <para>
/// The exclusion is an <b>open file handle</b>, not a recorded process
/// identifier. The operating system releases it when the holder dies, so crash
/// recovery needs no stale-lock heuristic and no timeout — the failure mode
/// that makes PID-file schemes unreliable. The companion owner file is
/// <b>informational only</b>: the handle is the truth, and the file exists so a
/// process that loses the race can name who holds it rather than saying
/// "busy".
/// </para>
/// <para>
/// The lock is on the <b>state directory</b>, never on the repository. A second
/// device may still write the same repository at the same moment from anywhere
/// with no coordination at all (04 §8) — this decides how many processes on one
/// machine hold one device's writer role, and the answer is one.
/// </para>
/// </remarks>
public sealed class StateDirectoryLock : IDisposable
{
    /// <summary>The role name a long-running service records.</summary>
    public const string ServiceRole = "service";

    /// <summary>The role name a CLI in direct mode records.</summary>
    public const string DirectRole = "cli-direct";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly FileStream _handle;
    private readonly string _ownerPath;

    private StateDirectoryLock(FileStream handle, string ownerPath, WriterLockOwner owner)
    {
        _handle = handle;
        _ownerPath = ownerPath;
        Owner = owner;
    }

    /// <summary>What this holder recorded about itself.</summary>
    public WriterLockOwner Owner { get; }

    /// <summary>The lock file for a state directory.</summary>
    /// <param name="stateDirectory">The state directory.</param>
    /// <returns>The path of the lock file.</returns>
    public static string PathFor(string stateDirectory) => Path.Combine(stateDirectory, "writer.lock");

    /// <summary>
    /// Takes the writer role, or throws naming the holder. A caller that cannot
    /// acquire it and finds no service to talk to must refuse — never proceed
    /// anyway (FR-SVC-002).
    /// </summary>
    /// <param name="stateDirectory">The state directory whose writer role is claimed.</param>
    /// <param name="role">
    /// <see cref="ServiceRole"/> or <see cref="DirectRole"/> — recorded for the
    /// next process to read, so "who has it" is answerable.
    /// </param>
    /// <returns>The held role; dispose to release it.</returns>
    /// <exception cref="ClientStateException">Another process holds the role.</exception>
    public static StateDirectoryLock Acquire(string stateDirectory, string role)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        ThrowHelper.ThrowIfNullOrWhiteSpace(role);

        Directory.CreateDirectory(stateDirectory);
        var lockPath = PathFor(stateDirectory);
        var ownerPath = OwnerPathFor(stateDirectory);

        FileStream handle;
        try
        {
            handle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new ClientStateException(DescribeHolder(ownerPath, stateDirectory), exception);
        }

        var owner = new WriterLockOwner
        {
            Role = role,
            Process = Environment.ProcessPath is { } path ? Path.GetFileName(path) : "unknown",
            ProcessId = Environment.ProcessId,
            AcquiredAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };

        try
        {
            AtomicFile.WriteAllText(ownerPath, JsonSerializer.Serialize(owner, SerializerOptions));
        }
        catch (IOException)
        {
            // The owner file is a courtesy. Failing to write it must never
            // fail an acquisition that the operating system granted.
        }

        return new StateDirectoryLock(handle, ownerPath, owner);
    }

    /// <summary>
    /// Takes the writer role if it is free, without throwing when it is not —
    /// the shape a client needs when "a service already holds it" is the
    /// ordinary case rather than an error.
    /// </summary>
    /// <param name="stateDirectory">The state directory whose writer role is claimed.</param>
    /// <param name="role">The role name to record.</param>
    /// <param name="held">The held role when acquisition succeeded.</param>
    /// <returns><see langword="true"/> when the role was taken.</returns>
    public static bool TryAcquire(string stateDirectory, string role, out StateDirectoryLock? held)
    {
        try
        {
            held = Acquire(stateDirectory, role);
            return true;
        }
        catch (ClientStateException)
        {
            held = null;
            return false;
        }
    }

    /// <summary>
    /// Describes the current holder for a message, without acquiring anything.
    /// </summary>
    /// <param name="stateDirectory">The state directory.</param>
    /// <returns>A sentence naming the holder, or <see langword="null"/> when the role looks free.</returns>
    public static string? DescribeHolderOrNull(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);

        if (TryAcquire(stateDirectory, DirectRole, out var probe))
        {
            probe!.Dispose();
            return null;
        }

        return DescribeHolder(OwnerPathFor(stateDirectory), stateDirectory);
    }

    /// <summary>Releases the writer role.</summary>
    public void Dispose()
    {
        try
        {
            File.Delete(_ownerPath);
        }
        catch (IOException)
        {
            // The handle below is what actually releases the role.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        _handle.Dispose();
    }

    private static string OwnerPathFor(string stateDirectory) => Path.Combine(stateDirectory, "writer.owner");

    private static string DescribeHolder(string ownerPath, string stateDirectory)
    {
        var holder = ReadOwner(ownerPath);
        if (holder is null)
        {
            return $"Another process holds the writer role for '{stateDirectory}'. "
                + "It did not record who it is, so it cannot be named — but the role is taken and this command will not proceed.";
        }

        var age = DateTimeOffset.TryParse(
            holder.AcquiredAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var acquired)
            ? $", held since {acquired.ToLocalTime():u}"
            : string.Empty;

        var alive = IsRunning(holder.ProcessId) ? string.Empty : " (its process is no longer running; the role will free itself)";

        return $"The writer role for '{stateDirectory}' is held by {holder.Role} '{holder.Process}' "
            + $"(pid {holder.ProcessId}){age}{alive}.";
    }

    private static WriterLockOwner? ReadOwner(string ownerPath)
    {
        try
        {
            return File.Exists(ownerPath)
                ? JsonSerializer.Deserialize<WriterLockOwner>(File.ReadAllText(ownerPath), SerializerOptions)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
