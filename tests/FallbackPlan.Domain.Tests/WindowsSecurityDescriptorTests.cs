using System.Buffers.Binary;
using FallbackPlan.TestSupport;
using static FallbackPlan.TestSupport.SecurityDescriptors;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// A restore reads a captured Windows security descriptor for the accounts
/// it names before it writes the descriptor back (FR-RST-004; ADR-0176): its
/// owner, its group and each account its DACL grants or denies, once each,
/// and whether the DACL takes nothing from its folder. Bytes that do not read
/// as a self-relative descriptor, in every part a write would hand the
/// platform, are no descriptor, so nothing of them is written. A SACL is
/// read too, whether it is there, whether it takes nothing from its folder,
/// and the accounts it audits, apart from those the rest names (ADR-0176
/// Amendment 3).
/// </summary>
[TestClass]
public sealed class WindowsSecurityDescriptorTests
{
    private const string Ana = "S-1-5-21-1-2-3-1001";
    private const string Users = "S-1-5-21-1-2-3-513";
    private const string Everyone = "S-1-1-0";
    private const string LocalSystem = "S-1-5-18";
    private const string Administrators = "S-1-5-32-544";

    [TestMethod]
    public void Read_NamesTheOwner_TheGroup_AndEachAccountItsDaclNames_OnceEach_InOrder()
    {
        var read = WindowsSecurityDescriptor.Read(Build(Ana, Users, Acl(
            Ace(Allowed, 0, Ana), Ace(Denied, 0, Everyone), Ace(Allowed, Inherited, LocalSystem), Ace(Allowed, Inherited, Ana))));

        Assert.IsNotNull(read);
        Assert.AreEqual(Ana, read.Owner);
        Assert.AreEqual(Users, read.Group);
        Assert.IsTrue(read.DaclPresent);
        Assert.IsFalse(read.DaclProtected);
        CollectionAssert.AreEqual(new[] { Ana, Users, Everyone, LocalSystem }, read.Accounts.ToArray());
    }

    [TestMethod]
    public void Read_ASacl_SaysItIsThere_WhetherItIsProtected_AndNamesTheAccountsItAudits()
    {
        var read = WindowsSecurityDescriptor.Read(Build(
            Ana, Users, Acl(Ace(Allowed, 0, Ana)), AuditProtected, sacl: Acl(Ace(Audit, AuditSuccess, Everyone))));

        Assert.IsNotNull(read);
        Assert.IsTrue(read.SaclPresent);
        Assert.IsTrue(read.SaclProtected);
        Assert.IsFalse(read.DaclProtected);
        CollectionAssert.AreEqual(new[] { Ana, Users }, read.Accounts.ToArray(), "an audit grants and denies nothing");
        CollectionAssert.AreEqual(new[] { Everyone }, read.AuditedAccounts.ToArray());
        Assert.IsEmpty(read.Entries.Where(entry => entry.Account == Everyone));

        var none = WindowsSecurityDescriptor.Read(Build(Ana, Users, Acl(Ace(Allowed, 0, Ana))));
        Assert.IsNotNull(none);
        Assert.IsFalse(none.SaclPresent);
        Assert.IsFalse(none.SaclProtected);
        Assert.IsEmpty(none.AuditedAccounts);
    }

    [TestMethod]
    public void Read_AnAuditListWithNoEntries_IsNone()
    {
        // Asked for a file's audit list, Windows hands back an empty one
        // where it audits nothing, which is nothing to write back.
        var read = WindowsSecurityDescriptor.Read(Build(Ana, Users, Acl(Ace(Allowed, 0, Ana)), sacl: Acl()));

        Assert.IsNotNull(read);
        Assert.IsFalse(read.SaclPresent);
    }

    [TestMethod]
    public void Read_ASaclThatDoesNotRead_IsNoDescriptor()
    {
        var descriptor = Build(Ana, Users, Acl(Ace(Allowed, 0, Ana)), sacl: Acl(Ace(Audit, AuditSuccess, Everyone)));
        var saclAt = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(12));
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan((int)saclAt + 2), 0xFFFF);

        Assert.IsNull(WindowsSecurityDescriptor.Read(descriptor));
    }

    [TestMethod]
    public void Read_AProtectedDacl_SaysSo()
    {
        var read = WindowsSecurityDescriptor.Read(Build(Ana, Users, Acl(Ace(Allowed, 0, Administrators)), Protected));

        Assert.IsNotNull(read);
        Assert.IsTrue(read.DaclProtected);
        CollectionAssert.AreEqual(new[] { Ana, Users, Administrators }, read.Accounts.ToArray());
    }

    [TestMethod]
    public void Read_ANullDacl_IsPresent_AndNamesNoAccountOfItsOwn()
    {
        var read = WindowsSecurityDescriptor.Read(Build(Ana, Users, dacl: null));

        Assert.IsNotNull(read);
        Assert.IsTrue(read.DaclPresent);
        CollectionAssert.AreEqual(new[] { Ana, Users }, read.Accounts.ToArray());
    }

    [TestMethod]
    public void Read_ADescriptorWithNoDacl_SaysSo_AndOneWithNoOwnerOrGroup_NamesOnlyItsDacl()
    {
        var bare = WindowsSecurityDescriptor.Read(Build(Ana, Users, dacl: null, daclPresent: false));
        var anonymous = WindowsSecurityDescriptor.Read(Build(null, null, Acl(Ace(Allowed, 0, Everyone))));

        Assert.IsNotNull(bare);
        Assert.IsFalse(bare.DaclPresent);
        Assert.IsNotNull(anonymous);
        Assert.IsNull(anonymous.Owner);
        Assert.IsNull(anonymous.Group);
        CollectionAssert.AreEqual(new[] { Everyone }, anonymous.Accounts.ToArray());
    }

    [TestMethod]
    public void Read_AnAuthorityPastThirtyTwoBits_IsWrittenInHex()
    {
        var descriptor = Build(null, null, Acl(Ace(Allowed, 0, Sid(0x0100_0000_0000, 5))));

        Assert.AreEqual("S-1-0x010000000000-5", Assert.ContainsSingle(WindowsSecurityDescriptor.Read(descriptor)!.Accounts));
    }

    [TestMethod]
    [DataRow(0u)]
    [DataRow(1u)]
    [DataRow(2u)]
    [DataRow(3u)]
    public void Read_AnObjectAce_FindsItsAccountPastTheGuidsItsFlagsSay(uint objectFlags)
    {
        var guids = BitCountOf(objectFlags);
        var beforeSid = new byte[4 + (16 * guids)];
        BinaryPrimitives.WriteUInt32LittleEndian(beforeSid, objectFlags);
        Array.Fill(beforeSid, (byte)0xEE, 4, 16 * guids);

        var read = WindowsSecurityDescriptor.Read(Build(null, null, Acl(Ace(AllowedObject, 0, Sid(Everyone), beforeSid))));

        Assert.AreEqual(Everyone, Assert.ContainsSingle(read!.Accounts));

        static int BitCountOf(uint flags) => (int)(flags & 1) + (int)((flags >> 1) & 1);
    }

    [TestMethod]
    public void Read_ACallbackAce_FindsItsAccountPastItsMask_AndLeavesItsConditionUnread()
    {
        var read = WindowsSecurityDescriptor.Read(Build(null, null, Acl(
            Ace(AllowedCallback, 0, Sid(Everyone), afterSid: "artx"u8.ToArray()))));

        Assert.AreEqual(Everyone, Assert.ContainsSingle(read!.Accounts));
    }

    [TestMethod]
    public void Read_WhatDoesNotReadAsADescriptor_IsNothing()
    {
        var whole = Build(Ana, Users, Acl(Ace(Allowed, 0, Everyone)));
        var dacl = (int)BinaryPrimitives.ReadUInt32LittleEndian(whole.AsSpan(16));
        var ownerOnly = Build(Ana, null, null, daclPresent: false);

        foreach (var (what, bytes) in new (string, byte[])[]
        {
            ("nothing", []),
            ("a header cut short", whole[..19]),
            ("another revision", With(whole, 0, 2)),
            ("a descriptor that is not self-relative", With(whole, 3, 0x00)),
            ("an owner past the end", With32(whole, 4, (uint)whole.Length)),
            ("an owner that claims more sub-authorities than it holds", With(ownerOnly, 21, 15)),
            ("an owner of another revision", With(whole, 20, 2)),
            ("a DACL of another revision", With(whole, dacl, 9)),
            ("a DACL that claims more than the descriptor holds", With16(whole, dacl + 2, (ushort)(whole.Length - dacl + 4))),
            ("a DACL that claims more entries than it holds", With16(whole, dacl + 4, 2)),
            ("an entry that claims more than its DACL holds", With16(whole, dacl + 8 + 2, 64)),
            ("an entry too small for its account", With16(whole, dacl + 8 + 2, 8)),
            ("an entry of a kind whose account cannot be found", With(whole, dacl + 8, 0x03)),
        })
        {
            Assert.IsNull(WindowsSecurityDescriptor.Read(bytes), what);
        }

        static byte[] With(byte[] bytes, int at, byte value)
        {
            var changed = (byte[])bytes.Clone();
            changed[at] = value;
            return changed;
        }

        static byte[] With16(byte[] bytes, int at, ushort value)
        {
            var changed = (byte[])bytes.Clone();
            BinaryPrimitives.WriteUInt16LittleEndian(changed.AsSpan(at), value);
            return changed;
        }

        static byte[] With32(byte[] bytes, int at, uint value)
        {
            var changed = (byte[])bytes.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(at), value);
            return changed;
        }
    }
}
