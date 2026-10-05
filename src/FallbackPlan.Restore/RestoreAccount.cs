using FallbackPlan.Domain;

namespace FallbackPlan.Restore;

/// <summary>
/// The account a restore runs as, which decides whose files it may give
/// away and to which groups (FR-RST-003, FR-RST-004; ADR-0085).
/// </summary>
/// <remarks>
/// A captured owner or group is a name. Each is given back where it resolves
/// to an account or group on the target and this account may give it: any
/// at all where <see cref="MayGiveFilesAway"/>, else only the account itself
/// and the groups it is in. The plan predicts every file by that, and the
/// executor resolves names through the same functions, so the two disagree
/// only where a volume refuses a write the rule allows.
/// </remarks>
public sealed record RestoreAccount
{
    /// <summary>The account's user id.</summary>
    public required uint UserId { get; init; }

    /// <summary>Every group the account is in.</summary>
    public required IReadOnlySet<uint> GroupIds { get; init; }

    /// <summary>Whether it may give a file to any account and group: root, or CAP_CHOWN on Linux.</summary>
    public required bool MayGiveFilesAway { get; init; }

    /// <summary>The id a captured owner's name has on the target, or null where no account has it.</summary>
    public Func<string, uint?> ResolveUser { get; init; } = FileOwnership.UserId;

    /// <summary>The id a captured group's name has on the target, or null where no group has it.</summary>
    public Func<string, uint?> ResolveGroup { get; init; } = FileOwnership.GroupId;

    /// <summary>
    /// The account this process runs as, or null where a file's owner is not
    /// an account by name: Windows keeps it in the security descriptor.
    /// </summary>
    public static RestoreAccount? OfThisProcess() =>
        FileOwnership.CanSetOwnership
            ? new RestoreAccount
            {
                UserId = FileOwnership.EffectiveUserId,
                GroupIds = FileOwnership.GroupIds,
                MayGiveFilesAway = FileOwnership.MayGiveFilesAway,
            }
            : null;

    /// <summary>Whether this account may give a file to the account <paramref name="userId"/>.</summary>
    /// <param name="userId">The account the file would belong to.</param>
    public bool MayGiveTo(uint userId) => MayGiveFilesAway || userId == UserId;

    /// <summary>Whether this account may give a file to the group <paramref name="groupId"/>.</summary>
    /// <param name="groupId">The group the file would belong to.</param>
    public bool MayGiveToGroup(uint groupId) => MayGiveFilesAway || GroupIds.Contains(groupId);
}
