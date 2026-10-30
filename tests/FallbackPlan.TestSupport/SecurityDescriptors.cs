using System.Buffers.Binary;
using System.Globalization;

namespace FallbackPlan.TestSupport;

/// <summary>
/// Self-relative Windows security descriptors built byte by byte, in the
/// layout <c>GetFileSecurityW</c> returns, so how a restore reads one can be
/// held to known bytes on every platform.
/// </summary>
public static class SecurityDescriptors
{
    /// <summary>An ACE that allows.</summary>
    public const byte Allowed = 0x00;

    /// <summary>An ACE that denies.</summary>
    public const byte Denied = 0x01;

    /// <summary>An ACE that allows where its condition holds.</summary>
    public const byte AllowedCallback = 0x09;

    /// <summary>An ACE that allows on an object type.</summary>
    public const byte AllowedObject = 0x05;

    /// <summary>The flag of an ACE a file made in its folder takes.</summary>
    public const byte ObjectInherit = 0x01;

    /// <summary>The flag of an ACE a folder made in its folder takes.</summary>
    public const byte ContainerInherit = 0x02;

    /// <summary>The flag of an ACE that governs only what is made in its folder.</summary>
    public const byte InheritOnly = 0x08;

    /// <summary>The flag an ACE carries when its folder gave it.</summary>
    public const byte Inherited = 0x10;

    /// <summary>The control bit of a DACL that takes nothing from its folder.</summary>
    public const ushort Protected = 0x1000;

    /// <summary>The SID <paramref name="text"/> names, as <c>S-1-5-21-1-2-3-1001</c>, in its binary form.</summary>
    public static byte[] Sid(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);

        var parts = text.Split('-');
        return Sid(
            ulong.Parse(parts[2], CultureInfo.InvariantCulture),
            [.. parts.Skip(3).Select(part => uint.Parse(part, CultureInfo.InvariantCulture))]);
    }

    /// <summary>A SID of <paramref name="authority"/> and <paramref name="subAuthorities"/>, in its binary form.</summary>
    public static byte[] Sid(ulong authority, params uint[] subAuthorities)
    {
        ArgumentNullException.ThrowIfNull(subAuthorities);

        var sid = new byte[8 + (4 * subAuthorities.Length)];
        sid[0] = 1;
        sid[1] = (byte)subAuthorities.Length;
        for (var i = 0; i < 6; i++)
        {
            sid[2 + i] = (byte)(authority >> (8 * (5 - i)));
        }

        for (var i = 0; i < subAuthorities.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(sid.AsSpan(8 + (4 * i)), subAuthorities[i]);
        }

        return sid;
    }

    /// <summary>
    /// An ACE of <paramref name="type"/> naming <paramref name="sid"/>, with
    /// <paramref name="beforeSid"/> between its mask and its SID, as an
    /// object ACE's flags and GUIDs, and <paramref name="afterSid"/> after
    /// it, as a callback ACE's condition.
    /// </summary>
    public static byte[] Ace(byte type, byte flags, byte[] sid, byte[]? beforeSid = null, byte[]? afterSid = null, uint mask = 0x1F01FF)
    {
        ArgumentNullException.ThrowIfNull(sid);

        beforeSid ??= [];
        afterSid ??= [];
        var ace = new byte[8 + beforeSid.Length + sid.Length + afterSid.Length];
        ace[0] = type;
        ace[1] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(ace.AsSpan(2), (ushort)ace.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(ace.AsSpan(4), mask);
        beforeSid.CopyTo(ace, 8);
        sid.CopyTo(ace, 8 + beforeSid.Length);
        afterSid.CopyTo(ace, 8 + beforeSid.Length + sid.Length);
        return ace;
    }

    /// <summary>An ACE of <paramref name="type"/> naming the SID <paramref name="sid"/> names.</summary>
    public static byte[] Ace(byte type, byte flags, string sid) => Ace(type, flags, Sid(sid));

    /// <summary>An ACE of <paramref name="type"/> giving <paramref name="sid"/> <paramref name="mask"/>.</summary>
    public static byte[] Ace(byte type, byte flags, string sid, uint mask) => Ace(type, flags, Sid(sid), mask: mask);

    /// <summary>An ACL holding <paramref name="aces"/>, in order.</summary>
    public static byte[] Acl(params byte[][] aces)
    {
        ArgumentNullException.ThrowIfNull(aces);

        var acl = new byte[8 + aces.Sum(ace => ace.Length)];
        acl[0] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(2), (ushort)acl.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(4), (ushort)aces.Length);
        var at = 8;
        foreach (var ace in aces)
        {
            ace.CopyTo(acl, at);
            at += ace.Length;
        }

        return acl;
    }

    /// <summary>
    /// A self-relative descriptor of <paramref name="owner"/>,
    /// <paramref name="group"/> and <paramref name="dacl"/>, laid out in that
    /// order after its header. A DACL said present but given as null is a
    /// null DACL, which grants everyone everything.
    /// </summary>
    public static byte[] Build(string? owner, string? group, byte[]? dacl, ushort control = 0, bool daclPresent = true)
    {
        var ownerSid = owner is null ? [] : Sid(owner);
        var groupSid = group is null ? [] : Sid(group);
        dacl ??= [];
        var descriptor = new byte[20 + ownerSid.Length + groupSid.Length + dacl.Length];
        descriptor[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(
            descriptor.AsSpan(2), (ushort)(0x8000 | (daclPresent ? 0x0004 : 0) | control));
        var at = 20;
        at = Place(ownerSid, 4);
        at = Place(groupSid, 8);
        Place(dacl, 16);
        return descriptor;

        int Place(byte[] part, int offsetField)
        {
            if (part.Length > 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(offsetField), (uint)at);
                part.CopyTo(descriptor, at);
            }

            return at + part.Length;
        }
    }
}
