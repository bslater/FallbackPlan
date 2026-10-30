using Bodu;

namespace FallbackPlan.Domain;

/// <summary>
/// Writes an entry's captured metadata back onto what a restore made of it,
/// by the rule every way of restoring holds to (<see cref="MetadataRules"/>;
/// ADR-0149): the executor, the command line's single-file restore and
/// the recovery tool alike (ADR-0150).
/// </summary>
/// <param name="target">What the restore's target lets it write back.</param>
public sealed class MetadataWriter(MetadataTarget target)
{
    /// <summary>The owners' names this writer has resolved, so a tree of one owner asks the system once.</summary>
    private readonly Dictionary<string, uint?> _owners = new(StringComparer.Ordinal);

    /// <summary>The groups' names this writer has resolved.</summary>
    private readonly Dictionary<string, uint?> _groups = new(StringComparer.Ordinal);

    /// <summary>The SIDs this writer has asked the machine whether it knows.</summary>
    private readonly Dictionary<string, bool> _accounts = new(StringComparer.Ordinal);

    /// <summary>
    /// Writes back what the target takes of <paramref name="metadata"/> onto
    /// the file landed, the folder made or the symlink made at
    /// <paramref name="destination"/>, as <paramref name="kind"/> says, one
    /// attribute at a time, and answers which writes landed, and which
    /// extended attributes were left and why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is fixed by what each write disturbs. Ownership goes first:
    /// giving a file to another owner or group clears its set-user-id and
    /// set-group-id bits and strips its file capabilities, so the permissions
    /// and the extended attributes have to follow it, and the permissions
    /// keep a set-id bit only where its owner or group landed. Extended
    /// attributes go on before the permissions, because a captured mode that
    /// takes away the owner's write permission would refuse the owner's own
    /// attributes, and an ACL sets the mode's group bits that the
    /// permissions then set as captured. Permissions change no time.
    /// Modification and access times follow. The creation time goes last. On
    /// macOS a modification time set earlier than the creation time makes
    /// the volume move the creation time back to it, and .NET then restores
    /// the one before; written last, the captured creation time is the last
    /// word whichever of those holds. On Windows the security descriptor
    /// follows, because its DACL may take from this account the access the
    /// writes before it need, and the attribute bits go on last, because a
    /// read-only file refuses those writes and writing a descriptor sets the
    /// archive bit. A folder is
    /// written through the folder's own calls, which Windows needs to open
    /// one for its times.
    /// </para>
    /// <para>
    /// An owner or group is attempted wherever its name resolves, whatever
    /// the plan predicted, and the platform decides: the receipt records
    /// that. So is each extended attribute the rule does not withhold
    /// (<see cref="MetadataRules.Withheld"/>); any that is withheld or
    /// refused leaves extended attributes not applied, and the detail names
    /// it. A write that fails, a name that resolves to nothing, or a time no
    /// file can carry is that attribute not applied, never the item failed.
    /// </para>
    /// <para>
    /// Where the file's captured ACL did not come back, its group is given
    /// no more than the ACL gave it (<see cref="MetadataRules.WithoutAcl"/>),
    /// and its permissions are then not those captured.
    /// </para>
    /// <para>
    /// A symlink takes the same writes in the same order, each by a call that
    /// changes the link itself (ADR-0101). .NET's permission setter follows a
    /// link and would write onto whatever it points at, and its time setters
    /// promise nothing either way, so a link's permissions and times go
    /// through <see cref="SymlinkMetadata"/>. Its owner, group
    /// and extended attributes are written by calls that never follow one,
    /// and its creation time only on macOS, whose call does not. Linux gives
    /// every link the same permissions and has no call to change them, so
    /// there a link's captured ones are written by nothing and land only where
    /// they are those (<see cref="MetadataRules.LinkPermissionsLand"/>).
    /// </para>
    /// </remarks>
    public MetadataWritten Write(string destination, EntryMetadata metadata, EntryKind kind = EntryKind.File)
    {
        ThrowHelper.ThrowIfNullOrEmpty(destination);
        ThrowHelper.ThrowIfNull(metadata);

        var applied = CapturedMetadata.None;
        var link = kind == EntryKind.Symlink;
        FileSystemInfo landed = kind == EntryKind.DirectoryPlaceholder ? new DirectoryInfo(destination) : new FileInfo(destination);

        if (target.SupportsPosixMetadata && !OperatingSystem.IsWindows() && target.Account is { } account)
        {
            if (metadata.OwnerName is { } owner
                && Resolved(_owners, owner, account.ResolveUser) is { } userId
                && FileOwnership.TrySetOwner(destination, userId))
            {
                applied |= CapturedMetadata.Owner;
            }

            if (metadata.GroupName is { } group
                && Resolved(_groups, group, account.ResolveGroup) is { } groupId
                && FileOwnership.TrySetGroup(destination, groupId))
            {
                applied |= CapturedMetadata.Group;
            }
        }

        // Each alone, so a refusal leaves the rest. The access ACL is
        // remembered, because the permissions depend on whether it landed.
        // Where the target keeps streams and no attributes, a file's go on
        // as streams of the same names, before the times a stream moves.
        var native = target.SupportsExtendedAttributes && ExtendedAttributes.CanSet;
        var asStreams = MetadataRules.KeepsAttributesAsStreams(kind, target.SupportsAlternateStreams, native);
        var writes = native || asStreams;
        var left = new List<(string Name, string Why)>();
        ExtendedAttributeEntry? acl = null;
        var aclLanded = false;

        var streams = asStreams ? MetadataRules.AsStreams(metadata) : null;
        for (var at = 0; at < metadata.ExtendedAttributes.Count; at++)
        {
            var attribute = metadata.ExtendedAttributes[at];
            var isAcl = MetadataRules.IsAccessAcl(attribute.Name.Span);
            var why = streams is not null
                ? streams[at] is { Stream: { } stream } ? target.Streams.Write(destination, stream, attribute.Value.Span) : streams[at].Why
                : !native ? null : MetadataRules.Withheld(attribute, target, link) is { } withheld
                    ? withheld
                    : ExtendedAttributes.TrySet(destination, attribute.Name.Span, attribute.Value.Span) ? null : "refused by the target";
            var landedHere = writes && why is null;
            if (writes && !landedHere)
            {
                left.Add((System.Text.Encoding.UTF8.GetString(attribute.Name.Span), why!));
            }

            if (isAcl)
            {
                (acl, aclLanded) = (attribute, landedHere);
            }
        }

        if (writes && metadata.ExtendedAttributes.Count > 0 && left.Count == 0)
        {
            applied |= CapturedMetadata.ExtendedAttributes;
        }

        if (target.SupportsPosixMetadata && metadata.PosixMode is { } mode && !OperatingSystem.IsWindows())
        {
            // A set-id bit whose owner or group did not land is dropped, and
            // so is any group permission only an ACL that did not land gave,
            // so the mode that lands is not the one captured.
            var permitted = MetadataRules.PermittedMode(mode, applied);
            if (acl is not null && !aclLanded)
            {
                permitted = MetadataRules.WithoutAcl(permitted, acl);
            }

            var lands = !link
                ? Wrote(() => landed.UnixFileMode = (UnixFileMode)permitted)
                : SymlinkMetadata.CanSetPermissions
                    ? SymlinkMetadata.TrySetPermissions(destination, permitted)
                    : MetadataRules.LinkPermissionsLand(permitted);
            if (lands && permitted == (mode & 0xFFF))
            {
                applied |= CapturedMetadata.PosixMode;
            }
        }

        // Before the times, since compressing or expanding rewrites a file's
        // clusters, and before the descriptor and the bits, which may take
        // the access it needs.
        string? compressionDetail = null;
        if (target.SupportsCompression
            && !link
            && metadata.FileAttributes is { } kept
            && target.Compression.Apply(destination, (kept & MetadataRules.CompressedFileAttribute) != 0) is { } refused)
        {
            compressionDetail = $"compression not applied ({refused})";
        }

        if (link)
        {
            var modifiedAt = FileTimes.FromUnixMilliseconds(metadata.ModifiedAt);
            var accessedAt = FileTimes.FromUnixMilliseconds(metadata.AccessedAt);
            if ((modifiedAt ?? accessedAt) is not null && SymlinkMetadata.TrySetTimes(destination, modifiedAt, accessedAt))
            {
                applied |= (modifiedAt is null ? CapturedMetadata.None : CapturedMetadata.ModifiedAt)
                    | (accessedAt is null ? CapturedMetadata.None : CapturedMetadata.AccessedAt);
            }
        }
        else
        {
            if (FileTimes.FromUnixMilliseconds(metadata.ModifiedAt) is { } modified
                && Wrote(() => landed.LastWriteTimeUtc = modified))
            {
                applied |= CapturedMetadata.ModifiedAt;
            }

            if (FileTimes.FromUnixMilliseconds(metadata.AccessedAt) is { } accessed
                && Wrote(() => landed.LastAccessTimeUtc = accessed))
            {
                applied |= CapturedMetadata.AccessedAt;
            }
        }

        if (target.SupportsCreationTimes
            && (!link || OperatingSystem.IsMacOS())
            && FileTimes.FromUnixMilliseconds(metadata.CreatedAt) is { } created
            && FileTimes.TrySetCreationTime(destination, created))
        {
            applied |= CapturedMetadata.CreatedAt;
        }

        // After the times and streams, because a DACL may take from this
        // account the access they need. A SID names the account it meant only
        // where this machine or its domain issued it, so a descriptor naming
        // one this machine does not know would hand the file to nobody's
        // access; it is not written, and the file keeps its folder's. The
        // restore privilege is enabled only for a write refused without it,
        // so a file given its own account's owner never runs under it.
        string? descriptorDetail = null;
        if (target.SupportsSecurityDescriptors && OperatingSystem.IsWindows() && !link
            && metadata.WindowsSecurityDescriptor is { IsEmpty: false } descriptor)
        {
            var read = WindowsSecurityDescriptor.Read(descriptor.Span);
            var strangers = read?.Accounts.Where(account => !Known(account)).ToList() ?? [];
            if (read is null)
            {
                descriptorDetail = "security descriptor not applied: it does not read as one";
            }
            else if (strangers.Count > 0)
            {
                descriptorDetail = $"security descriptor not applied: it names {string.Join(", ", strangers)}, which this machine does not know";
            }
            else if (WindowsSecurity.TryWrite(destination, descriptor.Span, read, withOwner: true)
                || (target.HoldsRestorePrivilege
                    && WindowsSecurity.TryWrite(destination, descriptor.Span, read, withOwner: true, withRestorePrivilege: true)))
            {
                applied |= CapturedMetadata.SecurityDescriptor;
            }
            else
            {
                descriptorDetail = read.DaclPresent && WindowsSecurity.TryWrite(destination, descriptor.Span, read, withOwner: false)
                    ? "security descriptor's owner and group not applied (refused by the target); its access control list was"
                    : "security descriptor not applied (refused by the target)";
            }
        }

        // Last, because a read-only file refuses the writes before it, and
        // writing a descriptor sets the archive bit. A DACL that withholds the
        // right to write attributes from this account leaves them unwritten.
        // Off Windows .NET maps these bits onto permissions and flags of its
        // own, so none is written there whatever a target says.
        string? unsetDetail = null;
        if (target.SupportsFileAttributes && OperatingSystem.IsWindows() && !link && metadata.FileAttributes is { } bits)
        {
            var unset = MetadataRules.UnsetFileAttributes(bits, target.SupportsCompression);
            if (Wrote(() => File.SetAttributes(destination, (FileAttributes)MetadataRules.FileAttributesToSet(
                    (uint)File.GetAttributes(destination), bits)))
                && unset.Count == 0
                && compressionDetail is null)
            {
                applied |= CapturedMetadata.FileAttributes;
            }

            if (unset.Count > 0)
            {
                unsetDetail = $"file attribute(s) not applied: {string.Join(", ", unset)} (not set by a restore)";
            }
        }

        var attributesDetail = left.Count == 0
            ? null
            : "extended attribute(s) not applied: " + string.Join("; ", left
                .GroupBy(entry => entry.Why, StringComparer.Ordinal)
                .Select(reason => $"{string.Join(", ", reason.Select(entry => entry.Name))} ({reason.Key})"));
        var details = new[] { attributesDetail, unsetDetail, compressionDetail, descriptorDetail }.OfType<string>().ToList();
        return new MetadataWritten(applied, details.Count == 0 ? null : string.Join("; ", details));

        static bool Wrote(Action write)
        {
            try
            {
                write();
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        bool Known(string account)
        {
            if (!_accounts.TryGetValue(account, out var known))
            {
                _accounts[account] = known = target.KnowsAccount(account);
            }

            return known;
        }

        static uint? Resolved(Dictionary<string, uint?> known, string name, Func<string, uint?> resolve)
        {
            if (!known.TryGetValue(name, out var id))
            {
                known[name] = id = resolve(name);
            }

            return id;
        }
    }
}

/// <summary>What the metadata writes for one file, folder or symlink did.</summary>
/// <param name="Applied">The attributes that landed as captured.</param>
/// <param name="Detail">Which extended attributes were left and why, or null when none was.</param>
public readonly record struct MetadataWritten(CapturedMetadata Applied, string? Detail);
