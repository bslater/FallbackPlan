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

## Amendment (2026-09): the two largest suites run their classes concurrently

Every project ran its tests one at a time: MSTest does unless an assembly asks otherwise, and none did. CI's test step lasts as long as its slowest assembly, which is Hosts.Tests on every platform. Run that way on Linux, its 605 test durations summed to 480 seconds of a 481-second run. Three of its classes, measured alone, used between 0.4 and 1.3 of four cores. The least busy was a peer suite, which spends most of its time waiting.

Hosts.Tests and Repository.Tests now run their classes concurrently, one worker per core, while the tests inside a class still run in order (`Parallelism.cs` in each). That is safe because every test owns its directories, stores, sockets and passphrase variable, so all a class can share with another is the process. What runs alone says which part of the process it shares, in a comment beside its `[DoNotParallelize]`:

- **A process-wide listener**: `NetworkSilence` hears every socket in the process, and a `MeterListener` every meter.
- **Process-wide settings**: the installation-wide environment variables that every host resolving a default location reads, and `CultureScope`, which sets the default culture for every thread.
- **The two test hooks the product keeps as process-wide properties**, `ServiceRuntime.ArchiveFormatVersion` and `FanOut.ReadBackBudget`. Only the nine methods that set them are marked, not their classes, so the rest of those classes still runs concurrently. Each hook's own documentation now says so.
- **Four drills whose assertions are about real durations**: the background window's parking, a real capture's preemption, and the max-pause bound and escalation delay at the scheduler.

Fifty-four host classes had carried `[DoNotParallelize]` with no reason recorded. While nothing ran in parallel it did nothing, and it had been copied from class to class; forty-eight lost it. One failure in the first parallel run found a hook the audit had missed. That failure is why the hooks are named above rather than assumed away.

The audit also found a defect in the product, not a test. The scanner's owner and group name cache was unlocked, so the writer pool's concurrent scans could corrupt it or record the wrong name. It is fixed under FR-MAN-003, drilled by `Filesystem.Tests/PosixNameCacheTests`.

Measured on Linux with four cores, each suite alone on the machine, as the duration the test run reports. Before is the same build with parallelisation switched off at run time, once for Hosts.Tests and three times for Repository.Tests. After is three runs each.

| Suite | Before | After | Tests |
|---|---|---|---|
| Hosts.Tests | 8m00s | 2m49s to 2m53s | 605 before and after |
| Repository.Tests | 53s to 59s | 25s to 26s | 738 before and after (731 passed, 7 skipped) |

A whole-solution run is not a baseline for one assembly, because the other assemblies share the machine during it. In one, Hosts.Tests reported 9m01s, against 8m00s alone.

What runs alone is paid for in full. In one parallel Hosts.Tests run, the 556 tests that run concurrently took 117 seconds, and the 49 that run alone took 57 more. MSTest starts them only once the concurrent batch has finished, and they never overlap one another.

To rule concurrency in or out of a failure, run the suite serially without editing it: `dotnet test` with `-- RunConfiguration.DisableParallelization=true` after its other arguments.

This record's own rule after a framework-level change is to compare the counts, and they match.

## Amendment 2 (2026-09): every suite runs its classes concurrently

After the amendment above, CI's test step was bounded by Hosts.Tests on every platform, at 6m27s to 6m53s, and the longest suite still running serially came next: Retention.Tests, at 3m00s to 4m12s.

Every test project now carries the same `Parallelism.cs`, except two: PerformanceTests, whose benchmarks measure real durations, and Web.DomTests, which drives one browser. The audit found the suites already built for it. Every test writes under a name of its own, and the only process-wide state the product lets a test change is the two hooks named above. What runs alone says why, beside its `[DoNotParallelize]`:

- **CultureScope's users**: `HostileCultureTests`, and one method each in `PassphraseStrengthTests` and `PathRuleHostileNameTests`.
- **The installation-wide state variable**: the one method in `WebConsoleOptionsTests` that sets it.
- **A real duration**: `RollingFileSinkTests`' bound on how long 10,000 queued lines keep the caller.
- **`LocalEndpointTests`** already ran alone, with its reason: the per-user fallback directory is machine-global.

`PeerRetentionTests` lost a marker that recorded no reason, as forty-eight host classes did above. The CLI suite's harnesses share one passphrase variable across the process. They can, because each writes the same value and none clears it, and the harness now says so beside the name.

Measured on Linux with four cores, each suite alone on the machine: the same build serially, then three runs in parallel.

| Suite | Before | After | Tests |
|---|---|---|---|
| Retention.Tests | 1m28s | 41s to 42s | 102 before and after (101 passed, 1 skipped) |
| Cli.Tests | 28s | 15s to 16s | 77 before and after |
| InterruptionTests | 15s | 7s to 9s | 111 before and after |
| Web.Tests | 10s | 4s | 148 before and after |
| Protocol.Tests | 10s | 6s | 245 before and after |

The other ten suites take a few seconds either way, and their counts match too. On CI the gain will be smaller, because there every assembly already runs beside the others on four cores.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | 966 tests across thirteen projects; count verified identical before and after |
| 2026-09 | Amended | Hosts.Tests and Repository.Tests run their classes concurrently; what runs alone says why beside `[DoNotParallelize]`; on Linux, each suite alone, 8m00s to under 3m and under 1m to under 30s, counts identical before and after |
| 2026-09 | Amended | Every test project but PerformanceTests and Web.DomTests runs its classes concurrently; on Linux, Retention.Tests 1m28s to 42s or less and Cli.Tests 28s to 16s or less, counts identical before and after |
