#!/usr/bin/env bash
#
# The recovery drill: proves the product's first principle — "Recovery is the
# product. Backup completion is not enough; recoverability must be verified."
#
# It builds a real installation, backs up a corpus chosen to exercise the
# segment and blob boundaries, DESTROYS the machine, and then recovers on a
# clean one using only what a person would still have: the destination drive,
# the recovery kit, and the passphrase. Every restored byte is compared against
# a manifest taken before the destruction.
#
# The shape it drills is the one a set created today actually has
# (ADR-0046): direct-ship, so the agent's state holds metadata only and the
# destination is the single complete copy in existence. Deleting the state
# directory is therefore not a simulation of loss — it is the loss.
#
# What it verifies, in order:
#   1  a headless setup writes both kit forms and nothing else holds the passphrase
#   2  two direct-ship sets capture; the destination holds the content and the
#      agent's own state holds no blob at all
#   3  the state directory, the archives root and the source tree are deleted
#   4  the standalone recovery tool — no engine, no catalogue, no service —
#      opens each archive from the destination with the kit and the passphrase,
#      including through a path with the trailing separator every shell's
#      tab-completion appends
#   5  snapshots enumerate without a catalogue, each signature verified
#   6  every file restores byte-identical to the pre-destruction manifest
#   7  one kit opens an archive it was never generated against
#   8  the printable page recovers as well as the binary file
#   9  a wrong passphrase, a foreign kit, and a foreign passphrase are each refused
#
# This is a manual/e2e drill, deliberately not wired into CI: it builds
# installations, writes outside the repository, and wants a second filesystem.
# To run it:
#
#   1. dotnet build FallbackPlan.slnx -c Release
#   2. eng/recovery-drill.sh [scratch-dir] [vault-dir]
#
# Exit code 0 means every step held; anything else names the step that did not.
# The in-process half of these guarantees lives in
# tests/FallbackPlan.Hosts.Tests/RecoveryHostTests.cs, which runs in CI.

set -euo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
DRILL=${1:-${TMPDIR:-/tmp}/fbp-recovery-drill}
# A second filesystem by preference: the destination standing in for the USB
# drive should not share a spindle with the source it protects (ADR-0051).
VAULT=${2:-/dev/shm/fbp-recovery-drill-vault}

AGENT="dotnet $REPO_ROOT/src/FallbackPlan.Agent/bin/Release/net10.0/FallbackPlan.Agent.dll"
RECOVER="dotnet $REPO_ROOT/src/FallbackPlan.Recovery/bin/Release/net10.0/FallbackPlan.Recovery.dll"

# Throwaway credentials for a throwaway installation. They are here so the
# drill is reproducible; nothing outlives it.
export DRILL_PASSPHRASE="Recovery Drill Passphrase 42!"
export DRILL_OWNER_PASSWORD="Drill-Owner-42!"
export DRILL_FOREIGN_PASSPHRASE="A Different Installation 42!"

step() { printf '\n\033[1m── %s ──\033[0m\n' "$*"; }
ok()   { printf '   ✓ %s\n' "$*"; }
die()  { printf '\n\033[31mDRILL FAILED at: %s\033[0m\n' "$*" >&2; exit 1; }

for binary in "$REPO_ROOT/src/FallbackPlan.Agent/bin/Release/net10.0/FallbackPlan.Agent.dll" \
              "$REPO_ROOT/src/FallbackPlan.Recovery/bin/Release/net10.0/FallbackPlan.Recovery.dll"; do
    [ -f "$binary" ] || die "missing $binary — run: dotnet build FallbackPlan.slnx -c Release"
done

rm -rf "$DRILL" "$VAULT"
mkdir -p "$DRILL"/{state,archives,source/docs/nested/deeper,source/photos,safe,clean} "$VAULT"

# ---------------------------------------------------------------- 1. a corpus

step "1. A corpus worth recovering, and its hashes"
SEG=$((64 * 1024))
random() { head -c "$2" /dev/urandom > "$DRILL/source/$1"; }
: > "$DRILL/source/docs/empty.bin"
random docs/one-byte.bin 1
random docs/under-segment.bin $((SEG - 1))
random docs/exact-segment.bin "$SEG"
random docs/over-segment.bin $((SEG + 1))
random docs/five-segments.bin $((5 * SEG))
random docs/nested/deeper/many-blobs.bin $((2 * 1024 * 1024))
printf 'plain text that a person would recognise\n' > "$DRILL/source/docs/notes.txt"
printf 'unicode content\n' > "$DRILL/source/docs/héllo wörld — ünïcode.txt"
random photos/holiday.jpg $((300 * 1024))
printf 'a second set, so one kit must open an archive it never saw\n' > "$DRILL/source/photos/caption.txt"

( cd "$DRILL/source" && find . -type f -print0 | sort -z | xargs -0 sha256sum ) > "$DRILL/safe/manifest.sha256"
CORPUS=$(wc -l < "$DRILL/safe/manifest.sha256")
ok "$CORPUS files hashed — the empty file, both sides of the segment boundary, a multi-blob file, unicode and spaces"

# ------------------------------------------------------------------ 2. set up

step "2. Headless setup writes the kit"
$AGENT setup --state "$DRILL/state" --archives "$DRILL/archives" \
    --passphrase-env DRILL_PASSPHRASE --acknowledge-loss \
    --kit-output "$DRILL/safe/recovery-kit.fbpkrkit" \
    --user ben --password-env DRILL_OWNER_PASSWORD > "$DRILL/setup.log" 2>&1 \
    || { cat "$DRILL/setup.log"; die "step 2 — setup"; }

[ -s "$DRILL/safe/recovery-kit.fbpkrkit" ]     || die "step 2 — no binary kit was written"
[ -s "$DRILL/safe/recovery-kit.fbpkrkit.txt" ] || die "step 2 — no printable kit was written"
for form in recovery-kit.fbpkrkit recovery-kit.fbpkrkit.txt; do
    grep -qF "$DRILL_PASSPHRASE" "$DRILL/safe/$form" && die "step 2 — $form contains the passphrase"
done
ok "both kit forms written, neither carrying the passphrase"

# ----------------------------------------------------- 3. two direct-ship sets

step "3. Two direct-ship sets capture into the vault"
python3 - "$DRILL" "$VAULT" <<'PYTHON'
import json, sys
drill, vault = sys.argv[1], sys.argv[2]
config = {
    "schema_version": 5,
    "destinations": [{
        "id": "d" * 32, "name": "vault", "kind": "local-path", "path": vault,
        "fingerprint": None, "endpoint": None, "failure_domain": None,
        "verification": None, "deep_verify_interval_days": None,
    }],
    "backup_sets": [
        {"id": "a" * 32, "name": "docs", "roots": [{"path": f"{drill}/source/docs", "label": None}],
         "include_rules": [], "exclude_rules": [], "schedule": "every 1h", "retention": None,
         "direct_ship": True, "destinations": ["vault"]},
        {"id": "b" * 32, "name": "photos", "roots": [{"path": f"{drill}/source/photos", "label": None}],
         "include_rules": [], "exclude_rules": [], "schedule": "every 1h", "retention": None,
         "direct_ship": True, "destinations": ["vault"]},
    ],
}
with open(f"{drill}/state/config.json", "w") as handle:
    json.dump(config, handle, indent=2)
PYTHON

# One scheduler pass: both sets are due because neither has ever run.
$AGENT run --once --state "$DRILL/state" --archives "$DRILL/archives" > "$DRILL/backup.log" 2>&1 \
    || { cat "$DRILL/backup.log"; die "step 3 — the backup pass"; }
grep -q "docs .*ran" "$DRILL/backup.log"   || { cat "$DRILL/backup.log"; die "step 3 — docs did not run"; }
grep -q "photos .*ran" "$DRILL/backup.log" || { cat "$DRILL/backup.log"; die "step 3 — photos did not run"; }

ARCHIVES=$(find "$VAULT" -mindepth 1 -maxdepth 1 -type d | wc -l)
[ "$ARCHIVES" -eq 2 ] || die "step 3 — expected two archives in the vault, found $ARCHIVES"
LOCAL_BLOBS=$(find "$DRILL/state/sets" -path '*/blobs/*' -type f 2>/dev/null | wc -l)
[ "$LOCAL_BLOBS" -eq 0 ] || die "step 3 — a direct-ship set left $LOCAL_BLOBS blob(s) on the machine"
[ -z "$(ls -A "$DRILL/archives")" ] || die "step 3 — something staged into the archives root"
ok "two archives in the vault; the machine holds metadata only and staged nothing"

# -------------------------------------------------------------- 4. destruction

step "4. Destroy the machine"
rm -rf "$DRILL/state" "$DRILL/archives" "$DRILL/source"
for relic in repository-format config.json installation.bin recovery-kit.confirmed; do
    found=$(find "$DRILL" -name "$relic" 2>/dev/null | wc -l)
    [ "$found" -eq 0 ] || die "step 4 — $found copy of $relic survived the deletion"
done
ok "state, archives and source are gone; only the vault, the kit and the passphrase remain"

# ---------------------------------------------------------------- 5. recovery

step "5. Recover on a clean machine"
KIT="$DRILL/safe/recovery-kit.fbpkrkit"
RESTORED=0
for archive in "$VAULT"/*/; do
    id=$(basename "$archive")

    # Deliberately WITH the trailing separator: completing a directory in any
    # shell appends one, so this is the path a person actually types.
    $RECOVER open --repo "$archive" --kit "$KIT" --passphrase-env DRILL_PASSPHRASE \
        > "$DRILL/open-$id.log" 2>&1 || { cat "$DRILL/open-$id.log"; die "step 5 — open $id"; }
    grep -q "derivation     reproduced" "$DRILL/open-$id.log" \
        || die "step 5 — $id did not report a reproduced derivation"

    $RECOVER snapshots --repo "$archive" --kit "$KIT" --passphrase-env DRILL_PASSPHRASE \
        > "$DRILL/snapshots-$id.log" 2>&1 || die "step 5 — snapshots $id"
    grep -q "SIGNATURE-FAILED" "$DRILL/snapshots-$id.log" && die "step 5 — $id has an unverified snapshot"
    snapshot=$(awk 'NR==1 {print $1}' "$DRILL/snapshots-$id.log")
    [ -n "$snapshot" ] || die "step 5 — $id listed no snapshot"

    $RECOVER restore --repo "$archive" --kit "$KIT" --passphrase-env DRILL_PASSPHRASE \
        --snapshot "$snapshot" --output "$DRILL/clean/$id" > "$DRILL/restore-$id.log" 2>&1 \
        || { cat "$DRILL/restore-$id.log"; die "step 5 — restore $id"; }
    grep -q ", 0 failed," "$DRILL/restore-$id.log" || die "step 5 — $id restored with failures"
    RESTORED=$((RESTORED + 1))
done
[ "$RESTORED" -eq 2 ] || die "step 5 — recovered $RESTORED archives, expected 2"
ok "both archives opened, enumerated and restored — one kit, two archives, no catalogue"

# ---------------------------------------------------------- 6. compare bytes

step "6. Compare every byte against the pre-destruction manifest"
mkdir -p "$DRILL/reassembled"
for archive in "$DRILL/clean"/*/; do
    # Each archive restores its own set's tree; name them back to the layout
    # the manifest was taken over.
    if [ -f "$archive/caption.txt" ]; then
        cp -a "$archive" "$DRILL/reassembled/photos"
    else
        cp -a "$archive" "$DRILL/reassembled/docs"
    fi
done

( cd "$DRILL/reassembled" && sha256sum -c "$DRILL/safe/manifest.sha256" ) > "$DRILL/compare.log" 2>&1 \
    || { cat "$DRILL/compare.log"; die "step 6 — a restored file does not match its original"; }
MATCHED=$(grep -c ': OK$' "$DRILL/compare.log")
[ "$MATCHED" -eq "$CORPUS" ] || die "step 6 — matched $MATCHED of $CORPUS"
ok "$MATCHED of $CORPUS files byte-identical"

# ------------------------------------------------------- 7. the printable page

step "7. The printable page recovers as well as the file"
FIRST=$(ls -d "$VAULT"/*/ | head -1)
$RECOVER open --repo "$FIRST" --kit "$DRILL/safe/recovery-kit.fbpkrkit.txt" \
    --passphrase-env DRILL_PASSPHRASE > "$DRILL/open-text.log" 2>&1 \
    || { cat "$DRILL/open-text.log"; die "step 7 — the transcribable form did not open the archive"; }
ok "the page a person could retype opens the archive"

# ------------------------------------------------------------- 8. refusals

step "8. Wrong credentials are refused"
export DRILL_WRONG_PASSPHRASE="Not The Drill Passphrase 42!"
$RECOVER open --repo "$FIRST" --kit "$KIT" --passphrase-env DRILL_WRONG_PASSPHRASE \
    > /dev/null 2>&1 && die "step 8 — a wrong passphrase was accepted"
ok "a wrong passphrase is refused"

FOREIGN="$DRILL/foreign"
mkdir -p "$FOREIGN"/{state,archives}
$AGENT setup --state "$FOREIGN/state" --archives "$FOREIGN/archives" \
    --passphrase-env DRILL_FOREIGN_PASSPHRASE --acknowledge-loss \
    --kit-output "$FOREIGN/kit.fbpkrkit" --user eve --password-env DRILL_OWNER_PASSWORD \
    > "$FOREIGN/setup.log" 2>&1 || { cat "$FOREIGN/setup.log"; die "step 8 — the foreign installation"; }

$RECOVER open --repo "$FIRST" --kit "$FOREIGN/kit.fbpkrkit" --passphrase-env DRILL_PASSPHRASE \
    > /dev/null 2>&1 && die "step 8 — another installation's kit opened this archive"
ok "another installation's kit is refused, even with the right passphrase"

$RECOVER open --repo "$FIRST" --kit "$KIT" --passphrase-env DRILL_FOREIGN_PASSPHRASE \
    > /dev/null 2>&1 && die "step 8 — another installation's passphrase opened this archive"
ok "another installation's passphrase is refused, even with the right kit"

printf '\n\033[32mDRILL COMPLETE\033[0m — %s files recovered byte-identical from the destination alone.\n' "$MATCHED"
printf 'Scratch left at %s for inspection; the vault at %s.\n' "$DRILL" "$VAULT"
