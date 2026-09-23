#!/usr/bin/env bash
# Tests for pgfs `pgfsctl prune` (stage C-3)
#
# **Cleaning up what an abnormal exit left behind.** Three kinds (stale rows in `{prefix}mounts` / orphan
# data rows / `.fuse_hidden*`) are deleted **behind a different safety valve each** (docs/Pgfsctl.md, the prune section).
#
# The contracts checked here:
#   - **the default is a dry run** - it only counts and nothing is deleted
#   - **the gravestones (the rows that recorded a loss) are not deleted** - they are kept to tell the operator (B-2)
#   - **the live gate** - with even one live mount around, **the data side is left alone**
#   - stale mounts rows are deleted / orphan data is deleted / `.fuse_hidden*` is deleted
#
# ⚠ **"the gravestones survive" and "the live gate" break silently, so they get the most attention.**
#   The first looks like nobody would mind if it went, but **the record of the loss goes with it** = the
#   evidence of an accident disappears.
#   The second **deletes a body that is in use**, so it does the most damage when it breaks.
#
# Usage:
#   PGFS_PSQL=/usr/local/pgsql/bin/psql bash tests/linux/prune.sh
#
# Environment variables: PGFS_SETTING_FILE / MOUNT_ROOT / PGFS_BIN / PGFS_PSQL (**required**) / TEST_FILTER
# Exit codes: 0 = everything passed / 1 = something failed / 2 = a prerequisite is missing

set -u

SETTING_FILE="${PGFS_SETTING_FILE:-$HOME/pgfs_test.toml}"
MNT="${MOUNT_ROOT:-$HOME/mnt/pgfs}"
BIN="${PGFS_BIN:-./bin/Debug}"
PSQL_BIN="${PGFS_PSQL:-${PSQL:-psql}}"
FILTER="${TEST_FILTER:-}"

if [ -t 1 ]; then
	RED=$'\033[0;31m'; GREEN=$'\033[0;32m'; YELLOW=$'\033[0;33m'; BLUE=$'\033[0;34m'; NC=$'\033[0m'
else
	RED=''; GREEN=''; YELLOW=''; BLUE=''; NC=''
fi
declare -i TOTAL=0 PASSED=0 FAILED=0 SKIPPED=0
declare -a FAILED_NAMES=()
CURRENT=""
pass() { PASSED=$((PASSED+1)); echo "${GREEN}PASS${NC}: $CURRENT"; }
fail() { FAILED=$((FAILED+1)); FAILED_NAMES+=("$CURRENT"); echo "${RED}FAIL${NC}: $CURRENT - $*"; }
skip() { SKIPPED=$((SKIPPED+1)); echo "${YELLOW}SKIP${NC}: $CURRENT - $*"; }

CONN=$(grep -E '^connection' "$SETTING_FILE" 2>/dev/null | head -1 | sed 's/^connection *= *//; s/^"//; s/"$//')
SCHEMA=$(grep -E '^schema' "$SETTING_FILE" 2>/dev/null | head -1 | sed 's/^schema *= *//; s/^"//; s/"$//')
PREFIX=$(grep -E '^prefix' "$SETTING_FILE" 2>/dev/null | head -1 | sed 's/^prefix *= *//; s/^"//; s/"$//')
[ -n "$SCHEMA" ] || SCHEMA="public"
[ -n "$PREFIX" ] || PREFIX="pgfs_"
kv() { echo "$CONN" | tr ';' '\n' | grep -iE "^$1=" | head -1 | cut -d= -f2-; }
DB_HOST=$(kv Host); DB_PORT=$(kv Port); DB_NAME=$(kv Database); DB_USER=$(kv Username)
[ -n "$DB_HOST" ] || DB_HOST=localhost
[ -n "$DB_PORT" ] || DB_PORT=5432
q() { "$PSQL_BIN" -U "$DB_USER" -h "$DB_HOST" -p "$DB_PORT" -d "$DB_NAME" -At -c "$1" 2>/dev/null; }

prune() { "$BIN/pgfsctl" prune --json "$@" -c "$CONN" -s "$SCHEMA" 2>/dev/null; }
# $1 = a jq-style key (nesting separated by .). Read with python3 so that it does not depend on jq.
jval() { python3 -c "
import json,sys
d=json.load(sys.stdin)
for k in sys.argv[1].split('.'):
    d = d.get(k) if isinstance(d, dict) else None
    if d is None: break
print('' if d is None else d)
" "$1"; }

umount_all() {
	if mountpoint -q "$MNT" 2>/dev/null; then fusermount3 -u "$MNT" 2>/dev/null; fi
	local i=0
	while [ $i -lt 600 ]; do pgrep -x mount.pgfs >/dev/null 2>&1 || return 0; sleep 0.1; i=$((i+1)); done
	return 1
}
mount_fs() {
	mkdir -p "$MNT" 2>/dev/null
	"$BIN/mount.pgfs" --setting-file "$SETTING_FILE" --mount-point "$MNT" >/dev/null 2>&1
	local i=0
	while [ $i -lt 100 ]; do mountpoint -q "$MNT" 2>/dev/null && return 0; sleep 0.1; i=$((i+1)); done
	return 1
}

# Make artificial leftovers. **It copies a real row and puts an old heartbeat in**, so it does not break when a column is added.
MARK_PLAIN="PRUNETEST-PLAIN"
MARK_TOMB="PRUNETEST-TOMB"
make_stale_rows() {
	q "delete from ${SCHEMA}.${PREFIX}mounts where mount_id in ('$MARK_PLAIN','$MARK_TOMB')" >/dev/null
	# a plain stale row (should be deleted)
	q "insert into ${SCHEMA}.${PREFIX}mounts (mount_id, host, pid, mountpoint, mode, started_at, heartbeat_at, config, stats)
	   values ('$MARK_PLAIN', 'prunetest', 999001, '/tmp/prunetest', 'fuse',
	           (now() at time zone 'UTC') - interval '2 days',
	           (now() at time zone 'UTC') - interval '2 days', '{}'::jsonb, '{}'::jsonb)" >/dev/null
	# a gravestone (should survive)
	q "insert into ${SCHEMA}.${PREFIX}mounts (mount_id, host, pid, mountpoint, mode, started_at, heartbeat_at, config, stats)
	   values ('$MARK_TOMB', 'prunetest', 999002, '/tmp/prunetest', 'fuse',
	           (now() at time zone 'UTC') - interval '2 days',
	           (now() at time zone 'UTC') - interval '2 days', '{}'::jsonb,
	           '{\"unflushedLoss\": 3}'::jsonb)" >/dev/null
}
row_exists() { [ "$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts where mount_id = '$1'")" = "1" ]; }

# The registration row of a daemon that was `kill -9`ed survives **with a heartbeat from a few seconds ago**,
# so prune sees it as live.
# In the tests that want to look at the cleanup itself, the rows are dropped first to create a "nobody is running" state.
# (Waiting for the grace period would take the default 3600 seconds, so the tests drop the rows directly.)
drop_dead_rows() {
	q "delete from ${SCHEMA}.${PREFIX}mounts
	   where (stats->>'unflushedLoss') is null or (stats->>'unflushedLoss')::int = 0" >/dev/null
}

# **Make orphan data artificially.**
# It cannot arise naturally on Linux - with `hard_remove = 0` libfuse **turns** the unlink of an open file
# into a rename, so `Api.DeleteInode` is only called **when nobody has it open**.
# Orphan data really appears on **Dokan's delete-on-close path** (stage C-2).
# What is wanted here is **prune's detection and deletion**, so a row is inserted directly as a stand-in.
ORPHAN_ID=""
make_orphan_data() {
	# **The `head -1` is needed**: even with `-At` the `INSERT 0 1` command tag comes along at the end, and
	# using it as-is makes the following `where id = ...` a syntax error, so **it is misjudged as "there is
	# no row" and the test returns a false result** (this was actually hit).
	ORPHAN_ID=$(q "insert into ${SCHEMA}.${PREFIX}data (chunk_size, total_size, created_by, updated_by)
	               values (1048576, 4096, 'prunetest', 'prunetest') returning id" | head -1)
	case "$ORPHAN_ID" in
		''|*[!0-9]*) ORPHAN_ID=""; return 1 ;;
	esac
	return 0
}
orphan_row_exists() { [ "$(q "select count(*) from ${SCHEMA}.${PREFIX}data where id = ${ORPHAN_ID:-0}" | head -1)" = "1" ]; }
cleanup_rows() { q "delete from ${SCHEMA}.${PREFIX}mounts where mount_id in ('$MARK_PLAIN','$MARK_TOMB')" >/dev/null; }

run() {
	local name="$1"
	if [ -n "$FILTER" ] && [[ "$name" != *"$FILTER"* ]]; then return 0; fi
	TOTAL=$((TOTAL+1)); CURRENT="$name"; "$name"
}

# ===== tests =====

test_prune_dry_run_changes_nothing() {
	# **The default is a dry run.** It counts and shows, and not a single row is deleted.
	umount_all
	make_stale_rows
	local before_m before_d
	before_m=$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts")
	before_d=$(q "select count(*) from ${SCHEMA}.${PREFIX}data")
	local out; out=$(prune)
	local dry; dry=$(echo "$out" | jval dry_run)
	if [ "$dry" != "True" ] && [ "$dry" != "true" ]; then
		fail "the default is not a dry run (dry_run=$dry)"; cleanup_rows; return
	fi
	local after_m after_d
	after_m=$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts")
	after_d=$(q "select count(*) from ${SCHEMA}.${PREFIX}data")
	if [ "$before_m" != "$after_m" ] || [ "$before_d" != "$after_d" ]; then
		fail "rows went down although it was a dry run (mounts $before_m->$after_m / data $before_d->$after_d)"
		cleanup_rows; return
	fi
	cleanup_rows
	pass
}

test_prune_removes_stale_mount_rows() {
	# Stale mounts rows must be deleted (a row an order of magnitude older than the default grace of 3600 seconds is inserted).
	umount_all
	make_stale_rows
	prune --apply >/dev/null
	if row_exists "$MARK_PLAIN"; then
		fail "the row with a heartbeat from two days ago was not deleted"
		cleanup_rows; return
	fi
	cleanup_rows
	pass
}

test_prune_keeps_tombstones() {
	# * **A gravestone (unflushedLoss > 0) must not be deleted.**
	#   Losing it looks harmless at first glance, but **the evidence of how much could not be written back
	#   goes with it** (B-2).
	#   It breaks silently, so this is always checked.
	umount_all
	make_stale_rows
	prune --apply >/dev/null
	if ! row_exists "$MARK_TOMB"; then
		fail "the gravestone (unflushedLoss > 0) was deleted as well = the evidence of the loss is gone"
		cleanup_rows; return
	fi
	cleanup_rows
	pass
}

test_prune_live_gate_blocks_data() {
	# * **With even one live mount around, the data side must be left alone.**
	#   When this breaks it **deletes a body that is in use**, which does the most damage.
	#   (The stale mounts rows may be deleted = **the live decision differs per kind** by design.)
	umount_all
	drop_dead_rows
	make_orphan_data || { fail "cannot make artificial orphan data"; return; }
	make_stale_rows

	mount_fs || { fail "the mount used to have something live"; cleanup_rows; return; }
	prune --apply >/dev/null
	local still_there=1
	orphan_row_exists || still_there=0
	# The stale mounts rows may be deleted (their live decision is a separate one)
	local stale_gone=1
	row_exists "$MARK_PLAIN" && stale_gone=0
	umount_all

	if [ "$still_there" != "1" ]; then
		fail "orphan data was deleted although a live mount is around = it can delete a body that is in use"
		cleanup_rows; return
	fi
	if [ "$stale_gone" != "1" ]; then
		fail "the stale mounts rows survived as well because something is live (the design is to keep the live decision separate per kind)"
		cleanup_rows; return
	fi
	cleanup_rows
	pass
}

test_prune_live_gate_survives_stale_heartbeat() {
	# * **A live mount that merely dropped its heartbeat must not be treated as dead.**
	#
	#   Even when the database is congested and the heartbeat cannot be written for a few minutes, the
	#   process is alive and has files open. Deciding live on the 90-second heartbeat threshold alone leaves
	#   **the band from 90 seconds to the grace period (3600 seconds) as "neither live nor stale"**, and
	#   firing in there **deletes a body that is open right now** (review H-3). On the same host,
	#   **the pid being alive is the answer**.
	umount_all
	drop_dead_rows
	make_orphan_data || { fail "cannot make artificial orphan data"; return; }
	mount_fs || { fail "the mount used to have something live"; return; }

	# Push only our own registration row's heartbeat back by 10 minutes (90 seconds < 10 minutes < 3600 seconds = the middle of the band).
	# * **The daemon rewrites its heartbeat every 30 seconds**, so fire as soon as it has been pushed back.
	q "update ${SCHEMA}.${PREFIX}mounts
	   set heartbeat_at = (now() at time zone 'UTC') - interval '10 minutes'
	   where mountpoint = '$MNT'" >/dev/null
	local aged; aged=$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts
	                      where mountpoint = '$MNT'
	                        and extract(epoch from ((now() at time zone 'UTC') - heartbeat_at)) > 90" | head -1)
	if [ "$aged" != "1" ]; then
		umount_all
		skip "could not make the heartbeat stale ($aged matching row(s) = the test's premise no longer holds)"
		return
	fi

	local skipped; skipped=$(prune --apply --json | jval applied.skipped_because_live)
	local still_there=1
	orphan_row_exists || still_there=0
	umount_all

	if [ "$skipped" != "True" ] && [ "$skipped" != "true" ]; then
		fail "a live mount whose heartbeat is merely stale was not treated as live (skipped_because_live=$skipped)"
		return
	fi
	if [ "$still_there" != "1" ]; then
		fail "orphan data was deleted although a live mount is around = it can delete a body that is in use"
		return
	fi
	pass
}

test_prune_rejects_small_grace() {
	# ★ **`--mounts-older-than` has a lower bound (600 seconds).**
	#   The grace doubles as "the limit up to which a row from another host is treated as live", so a small
	#   value **treats a mount alive on another host as dead and removes the data side**. **The kind of failure
	#   is checked too** - it exits 1 with a reason, and **nothing was removed** (removing the mounts rows before
	#   rejecting would be an accident of its own).
	umount_all
	drop_dead_rows
	make_stale_rows
	local out rc
	out=$("$BIN/pgfsctl" prune --apply --mounts-older-than 10 -c "$CONN" -s "$SCHEMA" 2>&1); rc=$?
	if [ "$rc" != "1" ]; then
		fail "--mounts-older-than 10 was not rejected (rc=$rc)"
		cleanup_rows; return
	fi
	case "$out" in
		*"at least 600 seconds"*) ;;
		*) fail "the reason for the rejection (the lower bound) is not given: $out"; cleanup_rows; return ;;
	esac
	if ! row_exists "$MARK_PLAIN"; then
		fail "the stale mounts row was deleted although the run was rejected"
		cleanup_rows; return
	fi
	cleanup_rows
	pass
}

test_prune_refuses_data_without_mounts_table() {
	# ★ **When the registry cannot be read, the data side is left alone even with `--force`.**
	#   An unreadable table = **nobody knows who is alive**. Before, `LiveMounts` stayed empty and the run went on,
	#   so firing it **while a v0.1.0 filesystem was mounted by v0.2.0 without the migration** removed the bodies
	#   running mounts were using. Here the table is renamed temporarily to make that state (**always put back**).
	umount_all
	drop_dead_rows
	make_orphan_data || { fail "cannot make artificial orphan data"; return; }
	local moved="${PREFIX}mounts_prunetest"
	q "alter table ${SCHEMA}.${PREFIX}mounts rename to ${moved}" >/dev/null
	if [ "$(q "select to_regclass('${SCHEMA}.${PREFIX}mounts') is null" | head -1)" != "t" ]; then
		skip "could not rename the mounts table temporarily (the test's premise no longer holds)"
		return
	fi
	local skipped; skipped=$(prune --apply --force | jval applied.skipped_because_unknown)
	local still_there=1
	orphan_row_exists || still_there=0
	q "alter table ${SCHEMA}.${moved} rename to ${PREFIX}mounts" >/dev/null
	if [ "$(q "select to_regclass('${SCHEMA}.${PREFIX}mounts') is not null" | head -1)" != "t" ]; then
		fail "could not rename the mounts table back (put it back by hand: ALTER TABLE ${SCHEMA}.${moved} RENAME TO ${PREFIX}mounts)"
		return
	fi
	if [ "$still_there" != "1" ]; then
		fail "orphan data was deleted with --force although the registry could not be read = it can delete bodies running mounts use"
		return
	fi
	if [ "$skipped" != "True" ] && [ "$skipped" != "true" ]; then
		fail "the reason for skipping the data side is not returned (skipped_because_unknown=$skipped)"
		return
	fi
	pass
}

test_heartbeat_reregisters_after_row_loss() {
	# ★ **When this mount's registry row disappears, the heartbeat registers it again.**
	#   This reproduces "removed by prune as stale after this host could not reach the database for a long time".
	#   Before, the heartbeat only fired an UPDATE that touched 0 rows, so the mount **stayed invisible to prune and
	#   status from then on** and did not count towards prune's "leave the data alone while any mount is alive".
	#   The heartbeat runs every 30 seconds, so it waits up to 45 seconds.
	umount_all
	drop_dead_rows
	mount_fs || { fail "mount"; return; }
	q "delete from ${SCHEMA}.${PREFIX}mounts where mountpoint = '$MNT'" >/dev/null
	if [ "$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts where mountpoint = '$MNT'" | head -1)" != "0" ]; then
		umount_all
		skip "could not delete the registry row (the test's premise no longer holds)"
		return
	fi
	local i=0 back=0
	while [ $i -lt 45 ]; do
		if [ "$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts where mountpoint = '$MNT'" | head -1)" = "1" ]; then
			back=1
			break
		fi
		sleep 1
		i=$((i+1))
	done
	umount_all
	if [ "$back" != "1" ]; then
		fail "the registry row did not come back within 45 seconds = this mount becomes invisible to prune's live gate"
		return
	fi
	pass
}

test_prune_removes_orphan_data_when_idle() {
	# With nothing live, orphan data must be deleted (this is where what the test above left behind gets cleaned up).
	umount_all
	drop_dead_rows
	if [ -z "$ORPHAN_ID" ] || ! orphan_row_exists; then
		make_orphan_data || { fail "cannot make artificial orphan data"; return; }
	fi
	prune --apply >/dev/null
	if orphan_row_exists; then
		fail "orphan data survived although nothing is live (id=$ORPHAN_ID)"
		return
	fi
	pass
}

test_prune_keeps_user_named_fuse_hidden() {
	# **prune must not delete a file the user created themselves that happens to be named `.fuse_hidden...`.**
	#
	# The enumerating side (`Api.IsLibfuseHidden`) checks "the prefix plus **16 hex digits**" strictly, so all
	# three below **show up perfectly normally in `ls`**. If the deleting side only matches the prefix,
	# **something visible disappears along with its contents** (inode -> orphan data -> chunk. Not even an audit row is left).
	umount_all
	mount_fs || { fail "mount"; return; }
	local d="$MNT/prunetest"
	rm -rf "$d" 2>/dev/null; mkdir -p "$d"
	# (1) a different digit count (2) the right digit count but not hex (3) **the `_` of a LIKE acting as a wildcard and matching**
	printf 'USERBODY1' > "$d/.fuse_hidden_notes.txt"
	printf 'USERBODY2' > "$d/.fuse_hiddenZZZZZZZZZZZZZZZZ"
	printf 'USERBODY3' > "$d/.fuseAhidden0123456789abcdef"
	sleep 0.5
	umount_all

	# It **must not even be enumerated** at the dry-run stage (what is shown is what gets deleted, so this is the entrance).
	local listed; listed=$(prune | jval fuse_hidden)
	if [ "$listed" != "0" ]; then
		fail "a user's file was enumerated as a .fuse_hidden leftover (count=$listed / expected 0)"
		return
	fi
	# * **Check explicitly that `--apply` was not skipped by the live gate.**
	#   prune **skips the whole data-deleting side when even one live mount is around**, so measuring while
	#   still mounted would make **even a pre-fix build report "the files survived" = green** (zero detection power).
	#   The dry-run enumeration check above is the real detector, but to make the "the contents survived"
	#   below mean something, this confirms it was not skipped.
	local skipped; skipped=$(prune --apply --json | jval applied.skipped_because_live)
	if [ "$skipped" != "False" ] && [ "$skipped" != "false" ]; then
		fail "--apply was skipped by the live gate (skipped_because_live=$skipped) = surviving means nothing"
		return
	fi

	mount_fs || { fail "remount" ; return; }
	local n=0 name body
	for name in ".fuse_hidden_notes.txt" ".fuse_hiddenZZZZZZZZZZZZZZZZ" ".fuseAhidden0123456789abcdef"; do
		body=$(cat "$d/$name" 2>/dev/null)
		case "$body" in
			USERBODY?) n=$((n+1)) ;;
			*) fail "a user's file was corrupted or deleted by prune: $name (body='$body')"; return ;;
		esac
	done
	if [ "$n" != "3" ]; then
		fail "$n survived (expected 3)"
		return
	fi
	rm -f "$d/.fuse_hidden_notes.txt" "$d/.fuse_hiddenZZZZZZZZZZZZZZZZ" "$d/.fuseAhidden0123456789abcdef" 2>/dev/null
	pass
}

test_prune_removes_fuse_hidden_right_after_kill() {
	# **Actually create** a `.fuse_hidden*` leftover and check that it is deleted.
	# **Only Linux can create one** (because libfuse renames with hard_remove = 0).
	umount_all
	mount_fs || { fail "mount"; return; }
	local d="$MNT/prunetest2"
	rm -rf "$d" 2>/dev/null; mkdir -p "$d"
	printf 'HIDDENBODY' > "$d/h.txt"
	sleep 0.5
	exec 9< "$d/h.txt"
	rm "$d/h.txt"
	local pid; pid=$(pgrep -x mount.pgfs | head -1)
	kill -9 "$pid" 2>/dev/null
	exec 9<&-
	fusermount3 -u "$MNT" 2>/dev/null
	local i=0; while [ $i -lt 100 ]; do pgrep -x mount.pgfs >/dev/null || break; sleep 0.1; i=$((i+1)); done

	local before; before=$(prune | jval fuse_hidden)
	if [ -z "$before" ] || [ "$before" = "0" ]; then
		skip "could not create a .fuse_hidden leftover (count=$before)"
		return
	fi
	# * **Fire without calling `drop_dead_rows`.**
	#   A row left by `kill -9` still has **a heartbeat from a few seconds ago**, so deciding live on the
	#   heartbeat alone means **the first 90 seconds after a crash cannot be cleaned up** = **exactly when
	#   cleaning up is wanted most**.
	#   It now **also looks at whether the pid is alive when the host is the same** (docs/Pgfsctl.md, the prune section).
	#
	#   ⚠ **This decision breaks silently if host names are compared the wrong way.** The registering side
	#     writes `Dns.GetHostName()` (**the FQDN** on Linux), while `Environment.MachineName` returns
	#     **the short name**, so comparing against that **does not match, giving "a different host" = erring
	#     on the safe side and treating it as live**. **Windows returns the same value for both, so it never
	#     surfaces there** (this is exactly what happened).
	#     **If this fails, suspect the host name comparison first.**
	prune --apply >/dev/null
	local after; after=$(prune | jval fuse_hidden)
	if [ "$after" != "0" ]; then
		fail "the .fuse_hidden leftover was not deleted ($before -> $after)"
		return
	fi
	pass
}

# ===== run =====

if ! command -v fusermount3 >/dev/null 2>&1; then
	echo "${RED}fusermount3 is missing${NC}" >&2; exit 2
fi
if [ -z "$DB_NAME" ] || ! command -v "$PSQL_BIN" >/dev/null 2>&1 || [ "$(q 'select 1')" != "1" ]; then
	echo "${RED}cannot connect to the database with psql${NC} (pass the real path of psql in PGFS_PSQL)" >&2
	exit 2
fi

echo "${BLUE}=== pgfs prune tests ===${NC}"
echo "setting : $SETTING_FILE"
echo "schema  : $SCHEMA"
echo ""

run test_prune_dry_run_changes_nothing
run test_prune_removes_stale_mount_rows
run test_prune_keeps_tombstones
run test_prune_live_gate_blocks_data
run test_prune_live_gate_survives_stale_heartbeat
run test_prune_rejects_small_grace
run test_prune_refuses_data_without_mounts_table
run test_heartbeat_reregisters_after_row_loss
run test_prune_removes_orphan_data_when_idle
run test_prune_keeps_user_named_fuse_hidden
run test_prune_removes_fuse_hidden_right_after_kill

cleanup_rows
rm -rf "$MNT/prunetest" "$MNT/prunetest2" 2>/dev/null
umount_all

echo ""
echo "${BLUE}===========================================${NC}"
echo "Results: ${GREEN}$PASSED passed${NC}, ${RED}$FAILED failed${NC}, ${YELLOW}$SKIPPED skipped${NC} (out of $TOTAL)"
if [ $FAILED -gt 0 ]; then
	echo "${RED}Failed tests:${NC}"
	for name in "${FAILED_NAMES[@]}"; do echo "  - $name"; done
	exit 1
fi

# ===== do not go green on zero tests =====
#
# "It did not fall over" and "it was checked" are different things. When a typo in TEST_FILTER or a missing
# prerequisite (psql not being resolvable, say) means not a single test ran, or every one was skipped,
# without this exit 0 would be treated as success.
# The details are in tests/linux/README.md, the section on not going green on zero tests.
if [ $TOTAL -eq 0 ]; then
	echo "${RED}not a single test ran${NC} (does TEST_FILTER='$FILTER' match nothing?)" >&2
	exit 1
fi
if [ $PASSED -eq 0 ]; then
	echo "${RED}not a single test passed${NC} ($SKIPPED skipped = a missing environment, not a passing test)" >&2
	exit 1
fi
exit 0
