# Coverage audit — is the test coverage real?

**Subject:** the test suite as it stands at `9761508`, measured rather than estimated — `eng/coverage.py` over coverlet reports from all fifteen MSTest projects, plus a per-file parse of the Cobertura output, plus the structural checks that decide whether a coverage number means anything
**Purpose:** answer whether the coverage behind the [destination-fitness arc](../adr/0035-destination-fitness.md) and everything before it is genuine, and name what it does not reach
**Outcome:** **85.94%** line coverage over 23 production assemblies, from 1 300 passing tests and 4 skipped. The number is trustworthy — traceability has zero drift, no test passes without asserting, and a test that does not run is recorded as skipped rather than green. Two low modules are single-OS measurement artefacts, not defects. Six real gaps are named and ranked; the worst of them is the primitive every durable state file writes through.

---

## Why this pass exists

A coverage percentage is the easiest number in a codebase to believe and one of the easiest to fake. This repository has already been burnt by exactly that: the traceability matrix once carried 86 test citations of which **73 named classes nobody had written**, and it read as coverage for as long as nobody checked. [`eng/check-requirements.py`](../../eng/check-requirements.py) exists because of that, and this pass exists for the same reason one layer down — the matrix now proves the *citations* are real, and says nothing about whether the tests behind them reach the code.

So this is not a request for a number. It is four questions:

1. What is the coverage, measured?
2. Is the instrument honest — do the tests assert, do they run, does the matrix still tell the truth?
3. Where the coverage is low, is that a gap or an artefact of measuring on one operating system?
4. Of the genuine gaps, which would cost something if a defect lived there?

Measured in this environment: SDK 10.0.110, Linux container, full suite built Release and run with `--collect:"XPlat Code Coverage"`.

---

## 1. The number

| module | line% | covered | uncovered |
|--------|------:|--------:|----------:|
| Keystore | 24.88% | 54 | 163 |
| Filesystem.Local | 50.67% | 494 | 481 |
| Api | 74.05% | 388 | 136 |
| Agent | 81.18% | 2 238 | 519 |
| Storage.Abstractions | 83.04% | 93 | 19 |
| Recovery | 84.73% | 233 | 42 |
| Restore | 85.33% | 256 | 44 |
| Storage.Local | 86.21% | 150 | 24 |
| Repository.Catalogue | 87.48% | 615 | 88 |
| Repository.Crypto | 87.79% | 187 | 26 |
| Repository.Format | 88.36% | 1 929 | 254 |
| Repository.Index | 89.08% | 1 044 | 128 |
| Cli | 89.59% | 1 471 | 171 |
| Application | 89.61% | 837 | 97 |
| Protocol | 90.09% | 1 636 | 180 |
| Retention | 91.10% | 614 | 60 |
| Repository.Packing | 91.34% | 897 | 85 |
| Repository | 93.00% | 2 152 | 162 |
| Repository.Segmentation | 95.68% | 133 | 6 |
| Replication | 98.12% | 209 | 4 |
| Domain | 99.36% | 776 | 5 |
| Import.Abstractions | 100.00% | 19 | 0 |
| Filesystem | 100.00% | 35 | 0 |
| **TOTAL** | **85.94%** | **16 460** | **2 694** |

Suite shape: 1 300 passing, 4 skipped, across 15 projects — 1 070 `[TestMethod]`s plus roughly 254 data rows, 36 749 lines of test against 48 054 lines of source, a ratio of 0.76.

Two things are worth reading off this table rather than the total. The **format and engine core is the best-covered part of the product** — Domain 99.4%, Repository 93.0%, Segmentation 95.7%, Packing 91.3% — which is the right shape for a backup tool, because that is where a defect is unrecoverable rather than merely annoying. And the **newest code is at the top, not the bottom**: Replication 98.1% and Retention 91.1% are the two projects the last three arcs touched most.

---

## 2. Is the instrument honest?

A coverage figure is worth what the tests behind it are worth. Three checks, all of which pass.

**Traceability has no drift in either direction.** 164 of 164 requirements reach a row of the [matrix](../requirements/traceability.md); 145 of those name a test class that exists on disk, and the rest carry an explicit untested marker with the phase that owes it. `check-requirements.py --drift` — which looks for the *opposite* failure, a requirement a test proves that its row does not name — reports zero. The instrument that caught the historic 73-of-86 fiction now passes in both directions.

**No test passes without asserting.** All 1 070 test methods were scanned for an assertion token. Forty-two came back without one, and every one of the forty-two turned out to be a false positive: expression-bodied delegation to an assertion helper (`AssertRejected`, `SequenceAssert`, `PropertyCheck`), or a dependency-rule test whose subject throws on violation. The single genuine case is `KeystoreTests.Remove_WhenNothingIsStored_ShouldSucceedWithoutError`, a deliberate "does not throw", which is a weak test but not a dishonest one.

**A test that does not run does not report as passed.** [`PlatformFacts`](../../tests/FallbackPlan.TestSupport/PlatformFacts.cs) exists for that rule and says so in its own doc comment — it replaced an early-`return` pattern that a runner records as a pass, which once made a green Windows run silently include tests that asserted nothing. Thirty-six tests are platform-gated; the four skips in this run are Windows and macOS subjects, recorded as skipped with their reason.

**Not present: mutation testing.** The discipline in practice is a manual mutation proof per behavioural commit — invert the guard, delete the branch, confirm the named tests fail — and the arcs to date have used it. That is real evidence and it is also unrepeatable: nothing re-runs it, and nothing stops it lapsing. Recorded here as a known limit of the instrument rather than a finding.

---

## 3. What the number understates

Two modules read low here and are not defects.

**Keystore, 24.88%.** Of its 217 measurable lines, `MacOsKeychainStore` (62) and `WindowsDataProtectionStore` (51) are 0% *by construction* on Linux, along with the generated `LibraryImports` interop. The reachable Linux surface is a little over half the file and most of it is covered.

**Filesystem.Local, 50.67%.** `WindowsInterop` is 0%, `PosixInterop` is 52.6% because it carries both the Linux and Darwin paths, and the generated `LibraryImports` adds 328 lines of which a third are reachable here.

This is precisely the case [`ci.yml`](../../.github/workflows/ci.yml) already anticipates: coverage is collected on all three operating systems and the reporting job merges the union, "because a single platform cannot reach the others' interop". **The merged matrix is the figure to quote for those two modules, and it cannot be produced from one container.** Nothing below treats either as a gap.

---

## 4. The gaps

Ranked by what a defect there would cost, not by how red the line is.

### G1 — `Application/AtomicFile.cs`, 52.9% — every failure path is untested

**Where:** `src/FallbackPlan.Application/AtomicFile.cs`, 16 of 34 lines uncovered.

This is the whole-file-replacement primitive that `destinations.json`, `jobs.json`, `notices.json` and `config.json` all write through. Its own doc comment states why it exists: `File.WriteAllText` truncates first and writes second, so a crash between the two leaves a zero-length state file, "and losing that file loses the device's writer identity". Four tests cover it, and all four cover the happy path — directory creation, no temp left behind, never observing a truncated read, no collision on the temp name.

Uncovered:

- the `catch { TryDelete(temporary); throw; }` around the write, so a failed write leaving its temp file behind is never exercised;
- **the entire `Replace` retry loop** — the `attempt < ReplaceAttempts` guard, the `Thread.Sleep(attempt)` back-off, and the rethrow once attempts run out;
- both catch arms of `TryDelete`.

The retry deserves its own sentence, because it is not merely untested *here*. POSIX `rename` over an open file succeeds, so the contention the loop guards against cannot be manufactured on Linux or macOS at all, and no Windows test creates the share violation it was written for. **It is untested on every platform in the matrix.** It is also the branch most likely to be reached in the field, on the operating system where file locking is real.

### G2 — `ServiceCommandHandler.UpsertBackupSet` — 21 contiguous uncovered lines, zero test references

**Where:** `src/FallbackPlan.Agent/ServiceCommandHandler.cs`, the `UpsertBackupSet` body.

This is the only programmatic route by which a client creates or edits a backup set over the command contract — the route a UI would use. It merges each destination reference's retention override **by name** so an upsert does not silently discard a per-destination policy, then validates and saves. `UpsertBackupSetCommand` appears in no test file anywhere, and neither does the `ClientStateException` path that turns a refused save into a `ServiceError` rather than an exception across the boundary.

The override-preserving merge is the part that matters: it is subtle, it is silent when wrong, and losing a per-destination retention policy would change what a destination is entitled to keep.

### G3 — `Agent/DestinationProbe.cs`, 72.1% — four of six refusal paths untested

**Where:** `src/FallbackPlan.Agent/DestinationProbe.cs`, 17 of 61 lines uncovered. Shipped in the destination-fitness arc; this is a gap in new code, not inherited.

Covered: the address defect, the missing directory, the file where a directory was declared, and the successful peer handshake. Uncovered:

- **"exists but will not accept a write"** — the branch whose reasoning is written into [ADR-0035 §2](../adr/0035-destination-fitness.md) as *existence is not permission*, and which is the whole point of probing a directory rather than stat-ing it;
- the no-matching-grant refusal, which the existing fingerprint test never reaches because the address-defect check returns first;
- the peer `PeerProtocolException` refusal;
- the unreachable-peer branch that records `Unavailable`.

The last two are the pair that distinguishes *a fault somebody must fix* from *an outage that heals itself* — the distinction ADR-0035 §2 rests on, and nothing currently proves it.

### G4 — refusal *reasons* are asserted as prose, which the specification forbids relying on

**Where:** `tests/FallbackPlan.Hosts.Tests/PeerQuotaTests.cs`, and the service boundary generally.

[Peer-protocol 02 §8](../../specifications/peer-protocol/02-session.md) makes the refusal *code* normative and the accompanying prose explicitly not for parsing. The tests do the reverse: `PeerQuotaTests` asserts `Contains("quota")` and `Contains("cannot store")` on the human-readable message, and `TermsRefused`, `StorageExhausted` and `MessageUnknown` appear by name in no test in the repository. So the assertion is on the half that may change freely, and the half that may not is unpinned — reword the message and the test breaks; change the code and it does not.

The same holds one layer up: of the seven `ServiceErrorReason` values, only `NotFound` and `InvalidArgument` are ever asserted, while the handler returns four.

### G5 — `Api/Transport/PeerCredentials.cs`, 22.2% — including the Linux success path

**Where:** `src/FallbackPlan.Api/Transport/PeerCredentials.cs`, 28 of 36 lines uncovered; called from `LocalServiceListener` on every accepted local connection.

Most of the file is the Windows named-pipe and macOS `getpeereid` paths, which are unreachable here. What is odd is that the *Linux* branch is entered — the dispatch line is covered — while neither outcome of `ReadLinux` is: not the `SO_PEERCRED` success that builds the identity, nor the `SocketException` fallback to `Unknown`.

Severity looked low: the identity is informational until [Q19](../open-questions.md#q19--console-identity-and-multi-operator-access) settles console identity, and nothing gates on it. It was listed because the discrepancy wanted one look **before** it became an authorization input rather than after.

> **Resolved (2026-08) — and it was a defect, not a gap.** The look found that `ReadLinux` returned `Unknown` for *every* local connection ever accepted. It called `Socket.GetSocketOption`, which translates .NET's portable option names into native ones and therefore rejects a raw native number as "operation not supported" before any syscall happens. `GetRawSocketOption` is the accessor that passes the native level and name through — which is what the method's own comment ("read through the raw option accessor because .NET names no SocketOptionName for SO_PEERCRED") always meant. The intent was right and the call was wrong, and nothing noticed because nothing read the answer.
>
> T-16's mitigation says the service "reads peer credentials to identify them". The authenticating half was never affected — filesystem permissions decide who may connect — but the identifying half did not work on Linux. It does now, proven over a real Unix socket pair. This is the one finding in this document that the coverage number found rather than merely measured.

### G6 — `Agent/AgentHost.cs`, 67.7% — 149 lines, the largest single-file gap

**Where:** `src/FallbackPlan.Agent/AgentHost.cs`.

Three keystore-backed passphrase-acquisition blocks, the top-level error handler, `--remote-port` parsing, the notices output, and the launchd and Windows branches of `install`. Part of it is platform-unreachable here; the rest is genuinely untested argument handling. Ranked last because it is thin glue over engines that are themselves well covered — but it is also the file where an allow-listed verb without a dispatch branch once fell through into the service loop and read as a seven-minute hang, so "thin glue" is not the same as "harmless".

---

## Checked and cleared

Recorded so nobody re-finds them.

**The 99 public types no test names.** A name-based sweep found 99 of 473 public types never mentioned in a test file. Nearly all are result records and plans reached transitively — `SweepOutcome`, `TrimPlan`, `CopyOutcome` are returned by tested engines and asserted through a `var`. The line data contradicts the name data, and the line data is right. Not a finding; noted because the sweep is tempting to re-run and re-misread.

**`FallbackPlan.Agent` has no test project of its own.** It is the largest source project (6 579 lines) and is entirely internal, reachable only end to end through `AgentHost` from three other suites. That is deliberate — there is no `InternalsVisibleTo` anywhere — and it still measures 81.18%, so the end-to-end route reaches most of it. The rule the D arc drew from this stands: an *engine* goes where a test can reach it directly, which is why `ReplicaSweep` is in `Repository` and `VerificationSampler` is in `Replication`. Orchestration may stay internal.

**Timing-dependent tests.** Eleven files use `Task.Delay` or `Thread.Sleep`. Every one is a real socket, a real process race, or a back-off the test must outlast — not a sleep standing in for synchronisation. No flake was observed across the repeated full runs this audit required.

**Generated code in the denominator.** 3 386 lines of `Strings.g.cs` accessors count toward coverage. They are exercised in proportion to how often their messages are asserted, which is a fair enough proxy; excluding them would raise the total by roughly a point and tell nobody anything.

---

## What follows

The gaps above are being closed in order — G1 first, because it is the primitive under every durable state file and the only one where a defect corrupts state silently — and the CI gate is gaining **per-module floors** beside its existing global floor of 50, so that a named module regressing fails the build instead of being absorbed into a healthy total. The global floor stays: it catches a module dropping out of the run entirely, which a per-module table cannot.

The floors are pinned *after* the gaps close, not before, or they enshrine today's holes.

---

**See also:** [Traceability](../requirements/traceability.md) — requirements to tests · [Implementation status](../implementation-status.md) — decisions to code · [`eng/coverage.py`](../../eng/coverage.py) — the instrument
