# ADR-0032 — MSTest is the test framework, and property tests keep FsCheck

**Status:** Accepted · **Date:** 2026-08 · **Supersedes:** the xUnit choice, which was never recorded

---

## Context

The suite was written on xUnit — not by decision, but because it was the default in the project template. 966 tests across thirteen projects accumulated on it.

Moving to MSTest is a house-standard call rather than a technical one, and the technical question that mattered was whether anything would be *lost* in the move. Two things nearly were.

## Decision

**MSTest 3.11 throughout.** `[Fact]` and `[Theory]` become `[TestMethod]`; `[InlineData]` becomes `[DataRow]`; `[MemberData]` becomes `[DynamicData]`; constructor-and-`IDisposable` lifecycle carries over unchanged, because MSTest builds an instance per test the same way.

**Platform gating collapses from two attributes into one.** Under xUnit, skipping lived on the attribute that declared the test, so restricting a fact and restricting a theory needed `PlatformFactAttribute` and `PlatformTheoryAttribute` separately. MSTest evaluates a `ConditionBaseAttribute` independently of how the test is fed, so both become one `PlatformConditionAttribute` that composes with anything. The trait discoverer that published the platform for filtering is gone too — MSTest reads test categories directly, so `PlatformTraitAttribute` is now a thin alias over the built-in category.

**Property tests keep FsCheck and lose only the attribute.** FsCheck ships runner integrations for xUnit and NUnit and none for MSTest, so `[Property]` has no equivalent. It does have what that attribute is built on: `Check.Method` takes a `MethodInfo`, generates arguments for its parameters, shrinks any counter-example, and throws on failure. `PropertyCheck.Holds` drives exactly that from an ordinary `[TestMethod]`, so all 22 properties still generate, still shrink, and still report their seed. The behaviour is unchanged; only the declaration moved.

**Sequence equality becomes explicit.** This is the one place the two frameworks genuinely disagree. xUnit's `Assert.Equal` special-cased sequences and compared them element by element; MSTest's `Assert.AreEqual` compares two arrays by reference, so two distinct arrays of identical bytes are unequal to it. A great deal of this suite means the former — a restored file matches the original, an encoding round-trips, a listing is in specification order — so those 100-odd sites now say `SequenceAssert.AreEqual`.

`SequenceAssert` is deliberately a **separate name**, not an overload of `AreEqual`. An overload taking `IEnumerable<T>` loses to the exact-match generic one whenever the argument is an array, so it would bind at some call sites and not others — the same assertion meaning two different things depending on the static type in front of it. A distinct name cannot be got silently wrong.

## Consequences

**Positive**

- `DataRow` is type-checked against the parameter where `InlineData` was not, which caught several rows passing `int` where the method took `byte` or `uint`. Those were latent conversions nobody had noticed.
- The framework is the house standard, so a contributor is not learning a second one to read the tests.

**Negative**

- A sequence comparison is now two characters longer to write and one more thing to remember. The alternative — an implicit overload — was rejected above for being worse.
- FsCheck properties are two members rather than one: a `[TestMethod]` naming the behaviour and a `…Property` method holding it. The pairing is by name, checked at run time rather than by the compiler.

**What the migration nearly lost, and how it was caught**

Two conversion defects would have left tests *silently absent* rather than failing:

1. `[TestClass]` went on the abstract `ObjectStoreContractTests` — which MSTest skips — and not on the concrete subclass that inherits its tests. **Fourteen contract tests stopped running.** Under xUnit no marker was needed, so there was nothing to get wrong.
2. Three property-test classes never received `[TestClass]` at all, because the pass that added it ran before the pass that turned `[Property]` into `[TestMethod]`. **Ten more stopped running.**

Neither showed up as a failure. Both were found by comparing the per-project test counts against the pre-migration run and refusing to accept a total that had dropped — 942 against 966. The count is the only thing that detects a test that has stopped existing, and it is now the check to repeat after any framework-level change.

A third defect *did* fail loudly, and is worth recording because it inverted an assertion: a rewrite turned `Assert.ThrowsAny<T>` into `Assert.Throws<T>` and then, in the same pass, `Assert.Throws<T>` into `Assert.ThrowsExactly<T>` — so three tests that accepted a derived exception came to demand an exact one. Order-dependent rewrites over the same text are how that happens.

## Alternatives considered

**Keep the five FsCheck projects on xUnit and run a mixed solution.** Rejected: the reason to standardise is that a reader meets one framework, and "except in these five projects" undoes it. The conversion turned out to cost one helper class.

**Write an xUnit-compatible `Assert` shim over MSTest.** It would have made the migration a package swap. Rejected as the worst of both — the tests would read as xUnit, be MSTest underneath, and the shim would be a third dialect to maintain and to get subtly wrong.

**Convert assertions mechanically and accept the risk.** Rejected for sequence equality specifically. Reference comparison of two equal arrays *fails* rather than passing, so the risk was noise rather than false confidence — but 100 red tests hide the handful that are red for a real reason.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | 966 tests across thirteen projects; count verified identical before and after |
