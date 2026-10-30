using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FallbackPlan.Domain;

/// <summary>
/// What a captured Windows security descriptor names, read from its
/// self-relative form (specification 06 §4.1 key 7): the accounts a restore
/// has to know before it writes the descriptor back, and whether its DACL
/// takes nothing from its folder (ADR-0176).
/// </summary>
/// <remarks>
/// A descriptor that does not read is none: every offset and length a write
/// would hand the platform is held inside the bytes, and an ACE of a kind
/// whose account this cannot find leaves the whole descriptor unread rather
/// than one of its accounts unjudged.
/// </remarks>
public sealed record WindowsSecurityDescriptor
{
    private const int HeaderLength = 20;
    private const ushort DaclPresentBit = 0x0004;
    private const ushort DaclProtectedBit = 0x1000;
    private const ushort SelfRelativeBit = 0x8000;

    /// <summary>The owner's SID, as <c>S-1-5-21-…</c>, or null where none was captured.</summary>
    public string? Owner { get; init; }

    /// <summary>The primary group's SID, or null where none was captured.</summary>
    public string? Group { get; init; }

    /// <summary>
    /// Whether the descriptor carries a DACL. One carried with no ACL at all
    /// is a null DACL, which grants everyone everything.
    /// </summary>
    public bool DaclPresent { get; init; }

    /// <summary>Whether the DACL takes nothing from the folder it is in.</summary>
    public bool DaclProtected { get; init; }

    /// <summary>
    /// Every account the descriptor names, once each: its owner, its group,
    /// and then each its DACL grants or denies, in the DACL's order.
    /// </summary>
    public IReadOnlyList<string> Accounts { get; init; } = [];

    /// <summary>
    /// What <paramref name="selfRelative"/> names, or null where it does not
    /// read as a self-relative descriptor.
    /// </summary>
    /// <param name="selfRelative">The descriptor as capture recorded it.</param>
    public static WindowsSecurityDescriptor? Read(ReadOnlySpan<byte> selfRelative)
    {
        if (selfRelative.Length < HeaderLength || selfRelative[0] != 1)
        {
            return null;
        }

        var control = BinaryPrimitives.ReadUInt16LittleEndian(selfRelative[2..]);
        if ((control & SelfRelativeBit) == 0)
        {
            return null;
        }

        var accounts = new List<string>();
        if (!TrySidAt(selfRelative, BinaryPrimitives.ReadUInt32LittleEndian(selfRelative[4..]), out var owner)
            || !TrySidAt(selfRelative, BinaryPrimitives.ReadUInt32LittleEndian(selfRelative[8..]), out var group))
        {
            return null;
        }

        Add(owner);
        Add(group);
        var present = (control & DaclPresentBit) != 0;
        var daclOffset = BinaryPrimitives.ReadUInt32LittleEndian(selfRelative[16..]);
        if (present && daclOffset != 0 && !TryDaclAt(selfRelative, daclOffset, accounts))
        {
            return null;
        }

        return new WindowsSecurityDescriptor
        {
            Owner = owner,
            Group = group,
            DaclPresent = present,
            DaclProtected = (control & DaclProtectedBit) != 0,
            Accounts = accounts,
        };

        void Add(string? account)
        {
            if (account is not null && !accounts.Contains(account, StringComparer.Ordinal))
            {
                accounts.Add(account);
            }
        }
    }

    /// <summary>The SID at <paramref name="offset"/>, or none where the offset is zero.</summary>
    private static bool TrySidAt(ReadOnlySpan<byte> descriptor, uint offset, out string? sid)
    {
        sid = null;
        return offset == 0
            || (offset >= HeaderLength && offset < descriptor.Length && TrySid(descriptor[(int)offset..], out sid));
    }

    /// <summary>Each account the ACL at <paramref name="offset"/> names, added to <paramref name="accounts"/> once.</summary>
    private static bool TryDaclAt(ReadOnlySpan<byte> descriptor, uint offset, List<string> accounts)
    {
        if (offset < HeaderLength || offset > descriptor.Length - 8)
        {
            return false;
        }

        var acl = descriptor[(int)offset..];
        var size = BinaryPrimitives.ReadUInt16LittleEndian(acl[2..]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(acl[4..]);
        if (acl[0] is not (2 or 4) || size < 8 || size > acl.Length)
        {
            return false;
        }

        acl = acl[..size];
        var at = 8;
        for (var i = 0; i < count; i++)
        {
            if (at > acl.Length - 8)
            {
                return false;
            }

            var aceSize = BinaryPrimitives.ReadUInt16LittleEndian(acl[(at + 2)..]);
            if (aceSize < 8 || aceSize > acl.Length - at
                || SidOffset(acl.Slice(at, aceSize)) is not { } sidAt
                || sidAt > aceSize
                || !TrySid(acl.Slice(at + sidAt, aceSize - sidAt), out var sid))
            {
                return false;
            }

            if (!accounts.Contains(sid!, StringComparer.Ordinal))
            {
                accounts.Add(sid!);
            }

            at += aceSize;
        }

        return true;
    }

    /// <summary>
    /// Where the SID of <paramref name="ace"/> begins, by its kind: after the
    /// mask for an ACE that names one account, and after the flags and the
    /// GUIDs they say it carries for an object ACE. Null for a kind this does
    /// not read.
    /// </summary>
    private static int? SidOffset(ReadOnlySpan<byte> ace)
    {
        switch (ace[0])
        {
            // Allowed, denied, audit, and their callback forms; a mandatory
            // label, a resource attribute and a scoped policy are laid out alike.
            case 0x00 or 0x01 or 0x02 or 0x09 or 0x0A or 0x0D or 0x11 or 0x12 or 0x13:
                return 8;

            // The object forms of allowed, denied and audit, and their callback forms.
            case 0x05 or 0x06 or 0x07 or 0x0B or 0x0C or 0x0F:
                if (ace.Length < 12)
                {
                    return null;
                }

                var flags = BinaryPrimitives.ReadUInt32LittleEndian(ace[8..]);
                return 12 + ((flags & 1) != 0 ? 16 : 0) + ((flags & 2) != 0 ? 16 : 0);

            default:
                return null;
        }
    }

    /// <summary>The SID at the start of <paramref name="bytes"/>, in its string form.</summary>
    private static bool TrySid(ReadOnlySpan<byte> bytes, out string? sid)
    {
        sid = null;
        if (bytes.Length < 8 || bytes[0] != 1 || bytes[1] > 15 || bytes.Length < 8 + (4 * bytes[1]))
        {
            return false;
        }

        ulong authority = 0;
        for (var i = 2; i < 8; i++)
        {
            authority = (authority << 8) | bytes[i];
        }

        var text = new StringBuilder("S-1-");
        text.Append(authority <= uint.MaxValue
            ? authority.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"0x{authority:X12}"));
        for (var i = 0; i < bytes[1]; i++)
        {
            text.Append('-').Append(BinaryPrimitives.ReadUInt32LittleEndian(bytes[(8 + (4 * i))..]).ToString(CultureInfo.InvariantCulture));
        }

        sid = text.ToString();
        return true;
    }
}
