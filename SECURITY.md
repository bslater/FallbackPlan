# Security policy

## Status

FallbackPlan is in **design and early implementation**. There is no release, no user data, and no supported version. This policy describes how reports will be handled and is published now so the process exists before it is needed.

## Reporting a vulnerability

Please report privately via GitHub's [private vulnerability reporting](https://docs.github.com/code-security/security-advisories/guidance-on-reporting-and-writing/privately-reporting-a-security-vulnerability) on this repository. Do not open a public issue.

Include what you have: affected component, what an attacker can achieve, and a reproduction if you have one. A partial report is worth sending.

**Response commitment** once the project reaches beta: acknowledgement within 3 working days, an initial assessment within 10, and a disclosure timeline agreed with you. Until then reports are handled on a best-effort basis and we will say plainly if we cannot act quickly.

## What is in scope

The [threat model](docs/threat-model.md) is the authority. Broadly in scope:

- anything letting a store, peer, or relay read plaintext content, filenames, or paths;
- anything letting a party without repository keys forge, substitute, or replay an object;
- **nonce reuse or key separation failures** — the highest-severity class in this design, because a repeated `(key, nonce)` pair under AES-GCM permits forgery of arbitrary records ([03 §5](specifications/repository-format/03-keys.md#5-per-blob-keys));
- data loss from garbage collection, compaction, retention, or index handling;
- a repository member corrupting another member's backups ([T-10](docs/threat-model.md#t-10-malicious-repository-member-poisons-deduplication));
- parser vulnerabilities reachable from repository objects or legacy archives;
- secrets reaching logs, telemetry, diagnostics, or crash dumps.

## What is known and documented

These are recorded limitations, not vulnerabilities. Reporting them is welcome if you can show the analysis is wrong.

| Limitation | Where |
|-----------|-------|
| Stored record lengths leak compressed sizes | [T-11](docs/threat-model.md#t-11-metadata-side-channels) |
| Deduplication hits confirm content to repository members | [T-12](docs/threat-model.md#t-12-dedup-confirmation-by-a-repository-member) |
| Relays observe the communication graph | [T-13](docs/threat-model.md#t-13-relay-traffic-analysis) |
| AES-GCM is not key-committing | [03 §6.1](specifications/repository-format/03-keys.md#61-a-note-for-the-security-review) |
| A compromised source reads plaintext before encryption | [Threat model](docs/threat-model.md#threats-not-solvable-by-backup-software) |

## What this software cannot do

Stated plainly because a backup product that overstates its guarantees is itself a hazard:

- it cannot protect data on a machine that is already compromised;
- it cannot recover a repository whose keys and recovery kit are all lost — that is by design;
- it cannot prevent an administrator with access to every device and to retention controls from destroying data, only make it attributable;
- it cannot detect malware inside historical snapshots, which is why restore defaults to a quarantine path.

## Cryptographic review

The construction in [03 — Keys](specifications/repository-format/03-keys.md) has **not** yet had external cryptographic review. It is a [freeze-gate](docs/roadmap.md#format-v1-freeze-gate) requirement and a prerequisite for the first beta.

If you have relevant expertise and are willing to look at it, that is more valuable to this project right now than almost anything else.
