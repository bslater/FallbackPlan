using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// The store contract's faults as types, which is how every caller tells them
/// apart (FR-QUOTA-001, ADR-0012 Amendment 5). A store that did not serve,
/// whether unreachable, busy or full, is a
/// <see cref="StoreUnavailableException"/>, recorded as a gap that closes
/// itself. A quota is an <see cref="IOException"/> and not that, because it
/// holds until a person acts. Callers catch on this shape: one that took a
/// quota for an unavailable store would wait on it for ever, and one that took
/// a busy store for a refusal would send a person looking for a fault. Each
/// type also keeps the three constructors an exception carries.
/// </summary>
[TestClass]
public sealed class StoreFaultTests
{
    [TestMethod]
    [DataRow(typeof(StoreUnreachableException))]
    [DataRow(typeof(StoreBusyException))]
    [DataRow(typeof(StoreFullException))]
    public void AStoreThatDidNotServe_IsUnavailable_WhateverTheReason(Type fault)
    {
        Assert.IsTrue(typeof(StoreUnavailableException).IsAssignableFrom(fault), fault.Name);
    }

    [TestMethod]
    public void AQuota_IsARefusal_NotAnUnavailableStore()
    {
        Assert.IsTrue(typeof(IOException).IsAssignableFrom(typeof(StoreQuotaExceededException)));
        Assert.IsFalse(typeof(StoreUnavailableException).IsAssignableFrom(typeof(StoreQuotaExceededException)));
    }

    [TestMethod]
    [DataRow(typeof(StoreUnavailableException))]
    [DataRow(typeof(StoreUnreachableException))]
    [DataRow(typeof(StoreBusyException))]
    [DataRow(typeof(StoreFullException))]
    [DataRow(typeof(StoreQuotaExceededException))]
    public void EachFault_KeepsItsMessageAndItsCause(Type fault)
    {
        var cause = new TimeoutException("the store took too long");

        Assert.IsInstanceOfType<IOException>(Activator.CreateInstance(fault));
        Assert.IsInstanceOfType<IOException>(Activator.CreateInstance(fault, "said"), out var said);
        Assert.AreEqual("said", said.Message);
        Assert.IsInstanceOfType<IOException>(Activator.CreateInstance(fault, "said", cause), out var wrapped);
        Assert.AreEqual("said", wrapped.Message);
        Assert.AreSame(cause, wrapped.InnerException);
    }
}
