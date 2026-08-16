# Naming and attribution

FallbackPlan's documentation, specifications, code comments and tests name **no
third-party product**. This page says why, and fixes the vocabulary used
instead, so the rule survives the next person who has a paragraph to write.

## Why

Two reasons, and the second is the one that matters.

The first is ordinary: naming a competitor, a predecessor, or a tool whose
changelog we read invites a comparison the reader cannot check and we cannot
control. Products change. A sentence that was fair when written becomes unfair
when the named product fixes the thing it was named for, and nobody goes back
to edit it.

The second is that **an argument that rests on a brand is a weaker argument
than one that rests on a mechanism.** "Product X shipped a fix for this" is an
appeal to authority. "A backup that reports success while having skipped a file
is a lie the operator acts on" is a reason. Where this repository used the
first form it was usually because the second was available and took more
thought. Removing the names forced the better sentence.

What is *not* a reason: pretending the design is unprecedented. It is not, and
several of its best rules are borrowed. The convention below keeps the
borrowing visible while keeping the lender anonymous.

## The vocabulary

| Instead of naming | Write |
|---|---|
| The backup product whose changelog was surveyed | **the surveyed product**, **the surveyed changelog** |
| Any comparable tool cited for a design rule | **prior art**, **a mature backup product**, **comparable tools** |
| The consumer service users migrate away from | **a legacy backup service**, **the legacy service** |
| A named cloud storage vendor | the interface: **S3-compatible object storage**, **major cloud object stores** |
| A named sync or transfer utility | **general-purpose sync tools** |

Protocol and interface names are **not** brands and stay: S3-compatible, SFTP,
WebDAV, SMB, systemd, launchd, TLS. These name a wire format or an operating
system facility a reader must be able to look up. "We support object storage"
is not a specification; "we support the S3 API" is.

## Vendor vocabulary in quoted material

Where a defect was learned from another product's changelog, the *mechanism* is
kept and the *vocabulary* is translated into this repository's own — which
[`01-domain-model.md`](architecture/01-domain-model.md) already defines as
normative:

| Foreign term | This repository |
|---|---|
| `dindex` file | index delta / index object |
| `dblock` file | data blob |
| fileset | snapshot |
| pack, pack file | blob |
| chunk | segment |

This is a translation, not a euphemism. "An index naming an object no blob
holds" says exactly what the original said, in terms this codebase's reader
already understands, and it is a better sentence for it.

## Attribution without names

Borrowed design rules keep their provenance as a **description of the source**,
not a name:

> Publication order — blobs, then index deltas, then snapshot — is the most
> valuable rule inherited from prior art.

The reader learns the rule was not invented here, which is the honest part. Who
invented it is not load-bearing for anything this repository asserts, and the
[architecture overview](architecture/00-overview.md) §5 discusses each borrowed
idea on its merits rather than by pedigree.

## The one place a name is unavoidable

An importer for a specific legacy format has to identify that format somewhere,
or it cannot claim to read it. That identification belongs in the importer's
own package and its user-facing help — never in the core, which is what
[ADR-0015](adr/0015-legacy-importer-isolation.md) isolates it for. No such
importer is built today; when one is, the neutral
[`ILegacyArchiveSource`](../src/FallbackPlan.Import.Abstractions/ILegacyArchiveSource.cs)
is the seam it plugs into, and the core stays unaware of what it is reading.

## Enforcement

There is no checker for this. A grep before a documentation commit is the
whole of it:

```
git grep -I -i -E "\b(duplicati|crashplan|restic|kopia|borg|backblaze|rclone|duplicity)\b"
```

Zero hits is the expected result.
