#!/usr/bin/env bash
#
# The recovery drill: proves the product's first principle — "Recovery is the
# product. Backup completion is not enough; recoverability must be verified."
#
# It builds a real installation, backs up a corpus chosen to exercise the
# segment and blob boundaries, DESTROYS the machine, and then recovers on a
# clean one using only what a person would still have: the destination drive
# and the passphrase. Nothing else — no kit, no exported file, no state
# (ADR-0060). Every restored byte is compared against a manifest taken before
# the destruction.
#
# The shape it drills is the one a set created today actually has
# (ADR-0046): direct-ship, so the agent's state holds metadata only and the
# destination is the single complete copy in existence. Deleting the state
# directory is therefore not a simulation of loss — it is the loss.
#
# What it verifies, in order:
#   1  a headless setup leaves nothing but the sealed credential behind, and
#      nothing on the machine holds the passphrase
#   2  two direct-ship sets capture; the destination holds the content and the
#      agent's own state holds no blob at all
#   3  the state directory, the archives root and the source tree are deleted
#   4  the standalone recovery tool — no engine, no catalogue, no service —
#      opens each archive from the destination with the passphrase alone,
#      including through a path with the trailing separator every shell's
#      tab-completion appends
#   5  snapshots enumerate without a catalogue, each signature verified
#   6  every file restores byte-identical to the pre-destruction manifest
#   7  one passphrase opens both archives, neither of which it was ever told about
#   8  a wrong passphrase and another installation's passphrase are each refused,
#      and a flag from the kit era is refused by name
#   9  the rebuilt machine RESUMES: set up afresh under the same passphrase,
#      pointed at the vault and nothing else, the service discovers both
#      archives by descriptor, adopts each under its original ids from the
#      shape the archive records, the next backup ships only what changed
#      into the same two archives, and the recovery tool restores the change
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
CLI="dotnet $REPO_ROOT/src/FallbackPlan.Cli/bin/Release/net10.0/FallbackPlan.Cli.dll"

# Throwaway credentials for a throwaway installation. They are here so the
# drill is reproducible; nothing outlives it.
export DRILL_PASSPHRASE="Recovery Drill Passphrase 42!"
export DRILL_OWNER_PASSWORD="Drill-Owner-42!"
export DRILL_FOREIGN_PASSPHRASE="A Different Installation 42!"

step() { printf '\n\033[1m── %s ──\033[0m\n' "$*"; }
ok()   { printf '   ✓ %s\n' "$*"; }
die()  { printf '\n\033[31mDRILL FAILED at: %s\033[0m\n' "$*" >&2; exit 1; }

for binary in "$REPO_ROOT/src/FallbackPlan.Agent/bin/Release/net10.0/FallbackPlan.Agent.dll" \
              "$REPO_ROOT/src/FallbackPlan.Recovery/bin/Release/net10.0/FallbackPlan.Recovery.dll" \
              "$REPO_ROOT/src/FallbackPlan.Cli/bin/Release/net10.0/FallbackPlan.Cli.dll"; do
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
printf 'a second set, so one passphrase must open an archive it was never told about\n' > "$DRILL/source/photos/caption.txt"

( cd "$DRILL/source" && find . -type f -print0 | sort -z | xargs -0 sha256sum ) > "$DRILL/safe/manifest.sha256"
CORPUS=$(wc -l < "$DRILL/safe/manifest.sha256")
ok "$CORPUS files hashed — the empty file, both sides of the segment boundary, a multi-blob file, unicode and spaces"

# ------------------------------------------------------------------ 2. set up

step "2. Headless setup leaves nothing but the sealed credential"
$AGENT setup --state "$DRILL/state" --archives "$DRILL/archives" \
    --passphrase-env DRILL_PASSPHRASE --acknowledge-loss \
    --user ben --password-env DRILL_OWNER_PASSWORD > "$DRILL/setup.log" 2>&1 \
    || { cat "$DRILL/setup.log"; die "step 2 — setup"; }

[ -s "$DRILL/state/write-credentials/installation.bin" ] || die "step 2 — no installation credential was stored"
KITS=$(find "$DRILL" -iname '*kit*' | wc -l)
[ "$KITS" -eq 0 ] || die "step 2 — setup wrote $KITS kit file(s); there is no kit any more"
grep -rqF "$DRILL_PASSPHRASE" "$DRILL/state" && die "step 2 — the state directory contains the passphrase"
ok "the sealed credential is stored; no kit was written; nothing on the machine holds the passphrase"

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
for relic in repository-format config.json installation.bin; do
    found=$(find "$DRILL" -name "$relic" 2>/dev/null | wc -l)
    [ "$found" -eq 0 ] || die "step 4 — $found copy of $relic survived the deletion"
done
ok "state, archives and source are gone; only the vault and the passphrase remain"

# ---------------------------------------------------------------- 5. recovery

step "5. Recover on a clean machine"
RESTORED=0
for archive in "$VAULT"/*/; do
    id=$(basename "$archive")

    # Deliberately WITH the trailing separator: completing a directory in any
    # shell appends one, so this is the path a person actually types.
    $RECOVER open --repo "$archive" --passphrase-env DRILL_PASSPHRASE \
        > "$DRILL/open-$id.log" 2>&1 || { cat "$DRILL/open-$id.log"; die "step 5 — open $id"; }
    grep -q "derivation     reproduced" "$DRILL/open-$id.log" \
        || die "step 5 — $id did not report a reproduced derivation"

    $RECOVER snapshots --repo "$archive" --passphrase-env DRILL_PASSPHRASE \
        > "$DRILL/snapshots-$id.log" 2>&1 || die "step 5 — snapshots $id"
    grep -q "SIGNATURE-FAILED" "$DRILL/snapshots-$id.log" && die "step 5 — $id has an unverified snapshot"
    snapshot=$(awk 'NR==1 {print $1}' "$DRILL/snapshots-$id.log")
    [ -n "$snapshot" ] || die "step 5 — $id listed no snapshot"

    $RECOVER restore --repo "$archive" --passphrase-env DRILL_PASSPHRASE \
        --snapshot "$snapshot" --output "$DRILL/clean/$id" > "$DRILL/restore-$id.log" 2>&1 \
        || { cat "$DRILL/restore-$id.log"; die "step 5 — restore $id"; }
    grep -q ", 0 failed," "$DRILL/restore-$id.log" || die "step 5 — $id restored with failures"
    RESTORED=$((RESTORED + 1))
done
[ "$RESTORED" -eq 2 ] || die "step 5 — recovered $RESTORED archives, expected 2"
ok "both archives opened, enumerated and restored — one passphrase, two archives, no catalogue"

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

# ------------------------------------------------------------- 7. refusals

step "7. Wrong credentials are refused, and the kit era is refused by name"
FIRST=$(ls -d "$VAULT"/*/ | head -1)
export DRILL_WRONG_PASSPHRASE="Not The Drill Passphrase 42!"
$RECOVER open --repo "$FIRST" --passphrase-env DRILL_WRONG_PASSPHRASE \
    > /dev/null 2>&1 && die "step 7 — a wrong passphrase was accepted"
ok "a wrong passphrase is refused"

FOREIGN="$DRILL/foreign"
mkdir -p "$FOREIGN"/{state,archives}
$AGENT setup --state "$FOREIGN/state" --archives "$FOREIGN/archives" \
    --passphrase-env DRILL_FOREIGN_PASSPHRASE --acknowledge-loss \
    --user eve --password-env DRILL_OWNER_PASSWORD \
    > "$FOREIGN/setup.log" 2>&1 || { cat "$FOREIGN/setup.log"; die "step 7 — the foreign installation"; }

$RECOVER open --repo "$FIRST" --passphrase-env DRILL_FOREIGN_PASSPHRASE \
    > /dev/null 2>&1 && die "step 7 — another installation's passphrase opened this archive"
ok "another installation's passphrase is refused"

$RECOVER open --repo "$FIRST" --kit "$DRILL/safe/nothing.bin" --passphrase-env DRILL_PASSPHRASE \
    > "$DRILL/kit-refusal.log" 2>&1 && die "step 7 — a --kit flag was accepted"
grep -q -- "--kit" "$DRILL/kit-refusal.log" || die "step 7 — the --kit refusal did not name the flag"
ok "a --kit flag from an old note is refused by name, with the remedy"

# ------------------------------------------------------------- 8. resume

step "8. The rebuilt machine resumes its sets from the vault (ADR-0061)"
# The person has their files back (step 6) and the drive. Put the files
# where the archives recorded them, set the product up again — a NEW salt
# under the SAME passphrase — point it at the vault with no sets declared,
# and let it find and adopt what is there.
mkdir -p "$DRILL/source"
cp -a "$DRILL/reassembled/docs" "$DRILL/source/docs"
cp -a "$DRILL/reassembled/photos" "$DRILL/source/photos"
mkdir -p "$DRILL"/{state,archives}
$AGENT setup --state "$DRILL/state" --archives "$DRILL/archives" \
    --passphrase-env DRILL_PASSPHRASE --acknowledge-loss \
    --user ben --password-env DRILL_OWNER_PASSWORD > "$DRILL/setup-again.log" 2>&1 \
    || { cat "$DRILL/setup-again.log"; die "step 8 — setup on the rebuilt machine"; }
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
    "backup_sets": [],
}
with open(f"{drill}/state/config.json", "w") as handle:
    json.dump(config, handle, indent=2)
PYTHON

# A listening service: adoption is the service's to do. Polled up by the
# sign-in the CLI's verbs need (FR-USR-001), torn down at the end of the
# step whatever happens.
$AGENT run --state "$DRILL/state" --archives "$DRILL/archives" --poll-seconds 3600 \
    > "$DRILL/service.log" 2>&1 &
SERVICE_PID=$!
trap 'kill "$SERVICE_PID" 2>/dev/null || true' EXIT
for _ in $(seq 1 60); do
    $CLI login --user ben --password-env DRILL_OWNER_PASSWORD --state "$DRILL/state" \
        > "$DRILL/login.log" 2>&1 && break
    sleep 0.5
done
grep -q "no service is listening" "$DRILL/login.log" && { cat "$DRILL/service.log"; die "step 8 — the service never listened"; }
$CLI status --state "$DRILL/state" > /dev/null 2>&1 || { cat "$DRILL/login.log"; die "step 8 — the CLI could not sign in"; }

$CLI discover --destination vault --state "$DRILL/state" > "$DRILL/discover.log" 2>&1 \
    || { cat "$DRILL/discover.log"; die "step 8 — discover"; }
FOUND=$(grep -c "nobody yet" "$DRILL/discover.log")
[ "$FOUND" -eq 2 ] || { cat "$DRILL/discover.log"; die "step 8 — discovery listed $FOUND unowned archives, expected 2"; }
ok "discovery lists both archives by descriptor alone, owned by nobody yet"

VAULT_BYTES_BEFORE=$(du -sb "$VAULT" | cut -f1)
for id in $(grep -oE '^[0-9a-f]{32}' "$DRILL/discover.log"); do
    $CLI adopt --destination vault --repository "$id" --state "$DRILL/state" \
        --passphrase-env DRILL_PASSPHRASE > "$DRILL/adopt-$id.log" 2>&1 \
        || { cat "$DRILL/adopt-$id.log"; die "step 8 — adopt $id"; }
    grep -q "writer identity resumed: yes" "$DRILL/adopt-$id.log" \
        || { cat "$DRILL/adopt-$id.log"; die "step 8 — $id did not resume the writer identity"; }
done
python3 - "$DRILL" <<'PYTHON'
import json, sys
drill = sys.argv[1]
with open(f"{drill}/state/config.json") as handle:
    sets = {s["name"]: s for s in json.load(handle)["backup_sets"]}
assert set(sets) == {"docs", "photos"}, sets.keys()
assert sets["docs"]["id"] == "a" * 32 and sets["photos"]["id"] == "b" * 32, "the original set ids were not kept"
assert sets["docs"]["roots"][0]["path"] == f"{drill}/source/docs", sets["docs"]["roots"]
assert sets["photos"]["roots"][0]["path"] == f"{drill}/source/photos", sets["photos"]["roots"]
assert sets["docs"]["schedule"] == "every 1h" and sets["photos"]["schedule"] == "every 1h"
assert all(s["direct_ship"] for s in sets.values())
PYTHON
ok "both sets adopted under their original ids, roots and schedules, from the archives alone"

# The wrong passphrase, refused before anything is sent.
export DRILL_WRONG_PASSPHRASE="Not The Drill Passphrase 42!"
$CLI adopt --destination vault --repository "$(grep -oE '^[0-9a-f]{32}' "$DRILL/discover.log" | head -1)" \
    --state "$DRILL/state" --passphrase-env DRILL_WRONG_PASSPHRASE > "$DRILL/adopt-wrong.log" 2>&1 \
    && die "step 8 — a wrong passphrase adopted an archive"
grep -q "Nothing was sent" "$DRILL/adopt-wrong.log" || { cat "$DRILL/adopt-wrong.log"; die "step 8 — the wrong-passphrase refusal did not say nothing was sent"; }
ok "a wrong passphrase is refused where it was typed"

# One change, one run per set, and only the change ships.
printf 'appended after the rebuild\n' > "$DRILL/source/docs/after-the-rebuild.txt"
for set in docs photos; do
    $CLI backup --set "$set" --state "$DRILL/state" > "$DRILL/resume-$set.log" 2>&1 \
        || { cat "$DRILL/resume-$set.log"; die "step 8 — the resumed backup of $set"; }
done
ARCHIVES=$(find "$VAULT" -mindepth 1 -maxdepth 1 -type d | wc -l)
[ "$ARCHIVES" -eq 2 ] || die "step 8 — the vault now holds $ARCHIVES archives; a set was re-seeded beside its own history"
VAULT_BYTES_AFTER=$(du -sb "$VAULT" | cut -f1)
GROWN=$((VAULT_BYTES_AFTER - VAULT_BYTES_BEFORE))
[ "$GROWN" -lt $((512 * 1024)) ] || die "step 8 — the resumed backups grew the vault by $GROWN bytes; the history was re-shipped"
ok "the resumed backups shipped $GROWN bytes into the same two archives — incremental, not a re-seed"

kill "$SERVICE_PID" 2>/dev/null || true
wait "$SERVICE_PID" 2>/dev/null || true
trap - EXIT

# And the recovery tool, with the passphrase alone, restores the change
# from the archive it was adopted into.
for archive in "$VAULT"/*/; do
    id=$(basename "$archive")
    $RECOVER snapshots --repo "$archive" --passphrase-env DRILL_PASSPHRASE \
        > "$DRILL/snapshots-after-$id.log" 2>&1 || die "step 8 — snapshots $id after the resume"
    COUNT=$(grep -c . "$DRILL/snapshots-after-$id.log")
    [ "$COUNT" -eq 2 ] || { cat "$DRILL/snapshots-after-$id.log"; die "step 8 — $id lists $COUNT snapshots after the resume, expected 2"; }
    snapshot=$(awk 'NR==1 {print $1}' "$DRILL/snapshots-after-$id.log")
    $RECOVER restore --repo "$archive" --passphrase-env DRILL_PASSPHRASE \
        --snapshot "$snapshot" --output "$DRILL/after/$id" > "$DRILL/restore-after-$id.log" 2>&1 \
        || { cat "$DRILL/restore-after-$id.log"; die "step 8 — restore $id after the resume"; }
done
[ "$(cat "$DRILL"/after/*/after-the-rebuild.txt 2>/dev/null)" = "appended after the rebuild" ] \
    || die "step 8 — the file added after the rebuild did not restore from the adopted archive"
ok "the newest snapshot of each archive restores, including the file added after the rebuild"

printf '\n\033[32mDRILL COMPLETE\033[0m — %s files recovered byte-identical from the destination and the passphrase alone, and the rebuilt machine resumed both sets incrementally.\n' "$MATCHED"
printf 'Scratch left at %s for inspection; the vault at %s.\n' "$DRILL" "$VAULT"
