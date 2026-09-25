#!/usr/bin/env bash
# Tests dedicated to pgfs metadata write-back
#
# Like tests/linux/writeback.sh, **this script mounts and remounts on its own**
# (because mount.write_back_metadata / cache_max_entries change per test).
# It is limited to the things that reproduce deterministically.
#
# * The premise of the contract (from stage 2 onwards): with write_back_metadata = on, **close(2) is not a
#   durability boundary**. Only fsync / fsyncdir are hard barriers, and "it was closed yet kill -9 lost it" is **correct behaviour**.
#   So test_meta_close_only_is_lost_on_crash positively asserts that **it disappears**.
#   The contract when it is off (close flushes synchronously) is held by test_close_survives_crash in writeback.sh.
#
# * test_meta_truncate_syscall_close_is_synchronous **used to be the only FAIL**
#   (`truncate -s 0 f; cmd >> f` left zero-length junk). **It was resolved in A-7** and
#   is green now. Changing the condition for clearing the mark to "only when unflushed work was actually
#   written out" stopped the mark being consumed by the truncate side's close (docs/design/metadata-write-back-reviews.md).
#
# * Producing a pending entry needs **a create with neither O_EXCL nor O_TRUNC** (`cp` uses O_CREAT|O_EXCL and
#   `> file` carries O_TRUNC, so neither becomes pending). plain_create / mkdir are used.
#
# Deliberately not here: the regression for the race "a create with the same name as a pending sibling slips
# through as write-through". The window past the FUSE layer's existence check (GetByPath) did not reproduce
# even with 8 threads on a barrier, and even if it were hit, two dirs self-heal through the adoption of the
# existing id at flush time, while two files cannot be told apart from "the loser of a same-name race
# disappears" from a shell. = it cannot be decided across the FS, so it does not live here (it needs a unit test layer).
#
# Usage:
#   bash tests/linux/wbmeta.sh
#
# Environment variables:
#   PGFS_SETTING_FILE   the settings file handed to mount.pgfs (default $HOME/pgfs_test.toml)
#   MOUNT_ROOT          the mount target (default $HOME/mnt/pgfs)
#   PGFS_BIN            the directory holding the binaries (default ./bin/Debug)
#   PGFS_PSQL           the path to psql (default psql. The database-checking tests need it)
#   TEST_FILTER         run only the tests whose name contains this string
#
# Prerequisites: the FS the settings file points at has been mkfs'd and may be destroyed
#       (the tests only touch what is under MOUNT_ROOT/wbmeta).
#
# Exit codes: 0 = everything passed / 1 = something failed / 2 = a prerequisite is missing

set -u

SETTING_FILE="${PGFS_SETTING_FILE:-$HOME/pgfs_test.toml}"
MOUNT_ROOT="${MOUNT_ROOT:-$HOME/mnt/pgfs}"
BIN="${PGFS_BIN:-./bin/Debug}"
PSQL="${PGFS_PSQL:-psql}"
TEST_ROOT="$MOUNT_ROOT/wbmeta"
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

# ===== reading the connection information from the settings file =====

CONN=$(grep -E '^connection' "$SETTING_FILE" 2>/dev/null | head -1 | sed 's/^connection *= *//; s/^"//; s/"$//')
SCHEMA=$(grep -E '^schema' "$SETTING_FILE" 2>/dev/null | head -1 | sed 's/^schema *= *//; s/^"//; s/"$//')
PREFIX=$(grep -E '^prefix' "$SETTING_FILE" 2>/dev/null | head -1 | sed 's/^prefix *= *//; s/^"//; s/"$//')
[ -n "$SCHEMA" ] || SCHEMA="public"
[ -n "$PREFIX" ] || PREFIX="pgfs_"

kv() { echo "$CONN" | tr ';' '\n' | grep -iE "^$1=" | head -1 | cut -d= -f2-; }
DB_HOST=$(kv Host); DB_PORT=$(kv Port); DB_NAME=$(kv Database); DB_USER=$(kv Username)
[ -n "$DB_HOST" ] || DB_HOST=localhost
[ -n "$DB_PORT" ] || DB_PORT=5432

# Return a query result one value per line. Empty when the database cannot be reached.
q() {
	"$PSQL" -U "$DB_USER" -h "$DB_HOST" -p "$DB_PORT" -d "$DB_NAME" -At -c "$1" 2>/dev/null
}

# Set audit.enabled to true temporarily (the mount reads the setting from the database at start-up). Returns **whether it really became true**.
# **created_by / updated_by are NOT NULL** - previously the columns were not passed, and since the NOT NULL check runs before the conflict
# check, the INSERT always failed whether the row existed or not (it was only a meaningful check where `audit.enabled` was already true,
# and B-4 passed silently as "auditing stays off, 0 gravestones").
audit_on() {
	q "insert into ${SCHEMA}.${PREFIX}settings (scope, key, value, created_by, updated_by) values ('audit','enabled','true'::jsonb,'wbmeta','wbmeta') on conflict (scope, key) do update set value = 'true'::jsonb" >/dev/null
	[ "$(q "select value::text from ${SCHEMA}.${PREFIX}settings where scope = 'audit' and key = 'enabled'")" = "true" ]
}

# Put audit.enabled back. $1 = the value before the change (empty if there was no row = the default false, so delete the added row).
audit_restore() {
	if [ -n "$1" ]; then
		q "update ${SCHEMA}.${PREFIX}settings set value = '$1'::jsonb where scope = 'audit' and key = 'enabled'" >/dev/null
		return
	fi
	q "delete from ${SCHEMA}.${PREFIX}settings where scope = 'audit' and key = 'enabled'" >/dev/null
}

db_ready() {
	[ -n "$DB_NAME" ] && [ -n "$DB_USER" ] && command -v "$PSQL" >/dev/null 2>&1 && [ "$(q 'select 1')" = "1" ]
}

# Make a running mount write a heartbeat snapshot (bringing status Layer 3 up to date).
ping_mount() {
	q "select pg_notify('${SCHEMA}_${PREFIX}notify', '{\"s\":\"wbmeta00\",\"c\":\"ping\"}')" >/dev/null
	sleep 1
}

stat_field() {
	q "select stats->'$1'->>'$2' from ${SCHEMA}.${PREFIX}mounts order by heartbeat_at desc limit 1"
}

# ===== mount operations =====

# The pid of the mount daemon. **Looked up by process name** (`pgrep -x`).
# `pgrep -f` would also match **the calling shell**, which contains the same command line
# (measured: it matched 3 times and head -1 returned the shell's pid).
# unmount_crash kills this, so getting it wrong takes an unrelated process down.
mount_pid() { pgrep -x mount.pgfs | head -1; }

unmount_clean() {
	if mountpoint -q "$MOUNT_ROOT" 2>/dev/null; then
		fusermount3 -u "$MOUNT_ROOT" 2>/dev/null
	fi
	local i=0
	while [ $i -lt 50 ]; do
		[ -n "$(mount_pid)" ] || return 0
		sleep 0.1
		i=$((i+1))
	done
	return 0
}

# Bring the mount down with SIGKILL (= the equivalent of a crash; Dispose's flush is not reached).
# The same practice as unmount_crash in writeback.sh.
unmount_crash() {
	local pid=$(mount_pid)
	if [ -n "$pid" ]; then
		kill -9 "$pid" 2>/dev/null
	fi
	sleep 0.5
	fusermount3 -u "$MOUNT_ROOT" 2>/dev/null
	return 0
}

# ===== cleaning up after the fault injection (for the error-state tests) =====
#
# A "same name, different kind" row inserted directly with psql has to be removed even when the test dies
# halfway, or every test after it (and the next run) fails on that collision. An EXIT trap guarantees it.

INJECT_CLEANUP_SQL=""

cleanup_injection() {
	if [ -n "$INJECT_CLEANUP_SQL" ]; then
		q "$INJECT_CLEANUP_SQL" >/dev/null
		INJECT_CLEANUP_SQL=""
	fi
}

# fsync a directory (= the FUSE fsyncdir).
fsyncdir() {
	python3 -c "
import os, sys
fd = os.open(sys.argv[1], os.O_RDONLY)
os.fsync(fd)
os.close(fd)
" "$1"
}

# Mount by passing the arguments straight through to mount.pgfs.
# Note: a bool flag consumes no value (the `true` of `--write-back true` falls through as a positional and
#     turns into mount_point), so **always pass them as bare flags**.
mount_meta() {
	mkdir -p "$MOUNT_ROOT" 2>/dev/null
	"$BIN/mount.pgfs" --setting-file "$SETTING_FILE" --write-back --write-back-metadata "$@" >/dev/null 2>&1
	local i=0
	while [ $i -lt 100 ]; do
		if mountpoint -q "$MOUNT_ROOT" 2>/dev/null; then
			return 0
		fi
		sleep 0.1
		i=$((i+1))
	done
	echo "${RED}could not mount${NC}" >&2
	return 1
}

run() {
	local name="$1"
	if [ -n "$FILTER" ] && [[ "$name" != *"$FILTER"* ]]; then
		return 0
	fi
	TOTAL=$((TOTAL+1))
	CURRENT="$name"
	"$name"
}

# ===== tests =====

test_pending_same_name_eexist() {
	# Creating something with the same name as a pending inode (which has no row in the database) has to be **EEXIST**.
	# Falling back to write-through does not fire the ON CONFLICT and creates two inodes with the same name,
	# and a later flush DELETEs the winning row, its data row and every chunk (destruction that write-through cannot produce).
	mount_meta --write-back-interval-ms 0 || { fail "mount"; return; }
	rm -rf "$TEST_ROOT" 2>/dev/null
	mkdir -p "$TEST_ROOT" || { fail "mkdir"; return; }
	mkdir "$TEST_ROOT/wbm_d" || { fail "the first mkdir failed"; return; }
	if mkdir "$TEST_ROOT/wbm_d" 2>/dev/null; then
		fail "a mkdir with the same name as a pending directory succeeded"
		return
	fi
	python3 -c "
import os, sys
p = '$TEST_ROOT/wbm_f'
fd = os.open(p, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o644)
os.write(fd, b'first')
os.close(fd)
try:
    fd = os.open(p, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o644)
    os.close(fd)
except FileExistsError:
    sys.exit(0)
sys.exit(1)
" || { fail "an O_EXCL create with the same name as a pending file succeeded"; return; }
	# Materialize it and then check the row count in the database (that two rows with the same name were not created)
	unmount_clean
	if ! db_ready; then
		skip "the database cannot be checked with psql, so the database-side check is omitted (the FS-side EEXIST has been confirmed)"
		return
	fi
	local dn=$(q "select count(*) from ${SCHEMA}.${PREFIX}inode where name = 'wbm_d'")
	local fn=$(q "select count(*) from ${SCHEMA}.${PREFIX}inode where name = 'wbm_f'")
	if [ "$dn" != "1" ] || [ "$fn" != "1" ]; then
		fail "the database has duplicate inodes with the same name (d=$dn f=$fn)"
		return
	fi
	pass
}

test_pending_truncate_zero_consistency() {
	# After truncate(0) on a pending inode, the state of the body must be consistent.
	#
	# **The invariant changed** (docs/design/data-id-lifecycle.md): the data_id is the file's body id, settled
	# at create time and never dropped until the final unlink. The {prefix}data row is created lazily and
	# **"no row" is interpreted as "the contents are empty"**. So a data_id surviving a truncate(0) is normal,
	# and what to look at is **that there are 0 chunks** (no orphan chunks).
	# Whether the data row itself exists is not questioned.
	if ! db_ready; then
		mount_meta >/dev/null 2>&1
		skip "the database cannot be checked with psql"
		unmount_clean
		return
	fi
	mount_meta --write-back-interval-ms 0 || { fail "mount"; return; }
	mkdir -p "$TEST_ROOT" || { fail "mkdir"; return; }
	local f="$TEST_ROOT/trunc.bin"
	rm -f "$f"
	# Before the close (= the materialization), do write -> truncate(0) -> fsync
	python3 -c "
import os
fd = os.open('$f', os.O_CREAT | os.O_WRONLY, 0o644)
os.write(fd, b'x' * 200000)
os.ftruncate(fd, 0)
os.fsync(fd)
os.close(fd)
" || { fail "the python write/truncate/fsync failed"; return; }
	local size=$(stat -c %s "$f")
	local du_k=$(du -k "$f" | cut -f1)
	unmount_clean
	# On Citus, inode (distributed on parent_id) and data (distributed on id) cannot be joined in a correlated
	# subquery, so the queries are fired one table at a time.
	# **Narrow to one row by descending id**. Looking it up by name without narrowing the parent would grab a
	# leftover with the same name in another directory (from manual debugging, say) (B-5 hit "looked a directory
	# id up by name alone and grabbed a different directory with the same name" once).
	# What was just created has the largest id, so one row in descending order is enough.
	local inode_row=$(q "select coalesce(data_id::text, 'null') from ${SCHEMA}.${PREFIX}inode where name = 'trunc.bin' order by id desc limit 1")
	if [ "$size" != "0" ]; then
		fail "st_size is not 0 after the truncate ($size)"
		return
	fi
	if [ "$du_k" != "0" ]; then
		fail "du is not 0 after the truncate (${du_k}K)"
		return
	fi
	if [ -z "$inode_row" ]; then
		fail "the inode row is not in the database although it was fsync'd (it was not materialized)"
		return
	fi
	if [ "$inode_row" = "null" ]; then
		pass
		return
	fi
	# If the data_id survives, **there must be 0 chunks** (no orphan chunks).
	# When the data row exists, total_size has to be 0 as well. No row means "the contents are empty", which is normal.
	local data_rows=$(q "select count(*) from ${SCHEMA}.${PREFIX}data where id = $inode_row")
	local chunk_rows=$(q "select count(*) from ${SCHEMA}.${PREFIX}data_chunk where data_id = $inode_row")
	if [ "$chunk_rows" != "0" ]; then
		fail "$chunk_rows chunk(s) survive for inode.data_id=$inode_row (orphan chunks)"
		return
	fi
	if [ "$data_rows" = "1" ]; then
		local total=$(q "select total_size from ${SCHEMA}.${PREFIX}data where id = $inode_row")
		if [ "$total" != "0" ]; then
			fail "there are 0 chunks yet data.total_size is $total (an occupancy inconsistency)"
			return
		fi
	fi
	pass
}

test_pending_pin_does_not_blow_cache() {
	# A pending inode cannot be evicted by LRU. When there are more pending entries than cache_max_entries,
	# falling into "throw away everything that is not pinned" wipes out the child-list cache and collides head-on
	# with the purpose of metadata write-back.
	if ! db_ready; then
		mount_meta >/dev/null 2>&1
		skip "the status snapshot cannot be read with psql"
		unmount_clean
		return
	fi
	mount_meta --write-back-interval-ms 0 --cache-max-entries 64 || { fail "mount"; return; }
	rm -rf "$TEST_ROOT/pins" 2>/dev/null
	mkdir -p "$TEST_ROOT/pins" || { fail "mkdir"; return; }
	local i=0
	while [ $i -lt 200 ]; do
		mkdir "$TEST_ROOT/pins/d$i" || { fail "mkdir d$i"; return; }
		i=$((i+1))
	done
	# Build the child-list cache first and then take the snapshot
	ls "$TEST_ROOT/pins" >/dev/null || { fail "ls"; return; }
	local listed=$(ls "$TEST_ROOT/pins" | wc -l)
	ping_mount
	local entries=$(stat_field inode entries)
	local lists=$(stat_field inode childrenLists)
	local evicted=$(stat_field inode evictions)
	unmount_clean
	if [ "$listed" != "200" ]; then
		fail "not all the pending directories show up in ls ($listed / 200)"
		return
	fi
	if [ -z "$entries" ] || [ -z "$lists" ]; then
		skip "the stats snapshot cannot be read"
		return
	fi
	# 200 pinned + a cap of 64 + a little slack. It must not have swollen to several times the cap.
	if [ "$entries" -gt 300 ]; then
		fail "the InodeCache went well over its cap (entries=$entries / cap=64 / pinned=200)"
		return
	fi
	if [ "$lists" -lt 1 ]; then
		fail "the child-list cache was wiped out (childrenLists=$lists)"
		return
	fi
	# When the pinned entries occupy the cap, it must not fall into "throw away everything that is not pinned every time"
	# (without cutting the eviction short, every put keeps sorting the whole of byId and sweeping).
	if [ -n "$evicted" ] && [ "$evicted" -gt 50 ]; then
		fail "the eviction does not stop while the pinned entries exceed the cap (evictions=$evicted entries=$entries cap=64 pinned=200)"
		return
	fi
	pass
}

test_pending_mkdir_visible_in_ls() {
	# A pending create has to advance the ledger's generation. Without that, it slips past ListChildren's
	# generation guard and burns in a child list where "stat works but it does not show up in ls".
	mount_meta --write-back-interval-ms 0 || { fail "mount"; return; }
	rm -rf "$TEST_ROOT/vis" 2>/dev/null
	mkdir -p "$TEST_ROOT/vis" || { fail "mkdir"; return; }
	# A concurrent ls forces the timing "a create lands while the child list is being read from the database"
	# (without the generation guard, that ls burns the pre-creation listing into the cache)
	rm -f "$TEST_ROOT/.vis_done"
	( while [ ! -e "$TEST_ROOT/.vis_done" ]; do ls "$TEST_ROOT/vis" >/dev/null 2>&1; done ) &
	local lspid=$!
	local i=0
	while [ $i -lt 60 ]; do
		mkdir "$TEST_ROOT/vis/v$i" || { fail "mkdir v$i"; return; }
		if [ ! -d "$TEST_ROOT/vis/v$i" ]; then
			fail "the stat right after creating it does not work v$i"
			kill $lspid 2>/dev/null
			return
		fi
		if ! ls "$TEST_ROOT/vis" | grep -qx "v$i"; then
			touch "$TEST_ROOT/.vis_done" 2>/dev/null
			fail "it does not show up in the ls right after creating it v$i"
			kill $lspid 2>/dev/null
			return
		fi
		i=$((i+1))
	done
	touch "$TEST_ROOT/.vis_done" 2>/dev/null
	wait $lspid 2>/dev/null
	local listed=$(ls "$TEST_ROOT/vis" | wc -l)
	unmount_clean
	if [ "$listed" != "60" ]; then
		fail "the ls count does not add up ($listed / 60)"
		return
	fi
	pass
}

# ----- stage 2: the durability contract (write_back_metadata = on) -----

test_meta_fsync_survives_crash() {
	# What it can detect: that fsync(2) is a hard barrier even with metadata=on
	# (the pending inode, the dirty data and the ancestor chain are materialized in the same tx).
	# If fsync does not materialize the pending inode, the remount fails with "the file is missing".
	mount_meta --write-back-interval-ms 0 || { fail "mount"; return; }
	rm -rf "$TEST_ROOT" 2>/dev/null
	mkdir -p "$TEST_ROOT/dur" || { fail "mkdir"; return; }
	local f="$TEST_ROOT/dur/fsynced.bin"
	# dd conv=fsync = write, then fsync(2), then close
	dd if=/dev/urandom of="$f" bs=64k count=8 conv=fsync status=none || { fail "dd"; return; }
	local sum_before=$(md5sum < "$f" | cut -d' ' -f1)
	local size_before=$(stat -c %s "$f")
	unmount_crash
	mount_meta --write-back-interval-ms 0 || { fail "remount"; return; }
	if [ ! -f "$f" ]; then
		unmount_clean
		fail "the fsync'd file disappeared after kill -9 (the materialization including the ancestor chain is not working)"
		return
	fi
	local size_after=$(stat -c %s "$f")
	local sum_after=$(md5sum < "$f" | cut -d' ' -f1)
	unmount_clean
	if [ "$size_before" != "$size_after" ]; then
		fail "the size changed: $size_before -> $size_after"
		return
	fi
	if [ "$sum_before" != "$sum_after" ]; then
		fail "the contents changed (md5 $sum_before -> $sum_after)"
		return
	fi
	pass
}

test_meta_close_only_is_lost_on_crash() {
	# What it can detect: the contract itself that **close(2) has stopped being a durability boundary**
	# (with metadata=on it is close-no-flush = neither the data nor the metadata is written).
	# * This test asserts that it **disappears**. It detects an implementation that goes back to flushing
	#    synchronously on close (= an accidental regression to the pre-metadata behaviour, or a heuristic
	#    firing too eagerly).
	#    It is the test that FAILs when write_back_metadata is turned off.
	# Persisting only the parent directory with fsyncdir is the crux: it **prevents a vacuous PASS**
	# of "the file is gone because the parent went with it" (the parent's existence is asserted too).
	mount_meta --write-back-interval-ms 0 || { fail "mount"; return; }
	rm -rf "$TEST_ROOT/lost" 2>/dev/null
	mkdir -p "$TEST_ROOT/lost" || { fail "mkdir"; return; }
	fsyncdir "$TEST_ROOT/lost" || { fail "fsyncdir"; return; }
	local f="$TEST_ROOT/lost/close_only.txt"
	# A create without O_EXCL (a shell redirection) = it is closed while still pending
	echo "close-only payload" > "$f" || { fail "create"; return; }
	if [ ! -f "$f" ]; then
		unmount_clean
		fail "our own file is not visible right after the close"
		return
	fi
	unmount_crash
	mount_meta --write-back-interval-ms 0 || { fail "remount"; return; }
	local dir_ok=0
	[ -d "$TEST_ROOT/lost" ] && dir_ok=1
	local file_present=0
	[ -e "$f" ] && file_present=1
	unmount_clean
	if [ "$dir_ok" != "1" ]; then
		fail "the parent directory that was fsyncdir'd disappeared after kill -9 (fsyncdir is not acting as a barrier)"
		return
	fi
	if [ "$file_present" != "0" ]; then
		fail "a file that was only closed survives kill -9 = close is flushing synchronously (the contract with metadata=on is close-no-flush)"
		return
	fi
	pass
}

test_meta_fsyncdir_persists_pending_children() {
	# What it can detect: that fsyncdir(2) reliably persists "the pending entries directly under that directory"
	# (including their data). test_meta_close_only_is_lost_on_crash holds down the "without fsyncdir it
	# disappears" side, so the two of them sandwich fsyncdir's effect between them.
	mount_meta --write-back-interval-ms 0 || { fail "mount"; return; }
	rm -rf "$TEST_ROOT/fsd" 2>/dev/null
	mkdir -p "$TEST_ROOT/fsd" || { fail "mkdir"; return; }
	local i=0
	while [ $i -lt 3 ]; do
		echo "payload-$i" > "$TEST_ROOT/fsd/c$i.txt" || { fail "create c$i"; return; }
		i=$((i+1))
	done
	mkdir "$TEST_ROOT/fsd/sub" || { fail "mkdir sub"; return; }
	fsyncdir "$TEST_ROOT/fsd" || { fail "fsyncdir"; return; }
	unmount_crash
	mount_meta --write-back-interval-ms 0 || { fail "remount"; return; }
	local missing="" bad=""
	i=0
	while [ $i -lt 3 ]; do
		if [ ! -f "$TEST_ROOT/fsd/c$i.txt" ]; then
			missing="$missing c$i.txt"
		elif [ "$(cat "$TEST_ROOT/fsd/c$i.txt" 2>/dev/null)" != "payload-$i" ]; then
			bad="$bad c$i.txt"
		fi
		i=$((i+1))
	done
	local sub_ok=0
	[ -d "$TEST_ROOT/fsd/sub" ] && sub_ok=1
	unmount_clean
	if [ -n "$missing" ]; then
		fail "a pending entry directly underneath disappeared although it was fsyncdir'd:$missing"
		return
	fi
	if [ -n "$bad" ]; then
		fail "the contents were lost after the fsyncdir (only the inode was written and the data was left behind):$bad"
		return
	fi
	if [ "$sub_ok" != "1" ]; then
		fail "a pending directory directly underneath disappeared although it was fsyncdir'd"
		return
	fi
	pass
}

# ----- stage 2: synchronization heuristic (a) rename-over-existing -----

# The shared part that goes through rename-over-existing once and inspects the target after kill -9.
# $1 = the case name / $2 = how the temp is created (redirect | excl)
# The results go into RN_* (a subshell would not return them, so no command substitution).
rename_over_existing_case() {
	local case="$1" how="$2"
	local dir="$TEST_ROOT/rn_$case"
	local target="$dir/target.txt"
	local tmp="$dir/tmp.new"
	local old="OLD" new="NEW-NEW-NEW"
	RN_ERR=""; RN_PRESENT=""; RN_CONTENT=""; RN_SIZE=""; RN_DBSIZE=""
	RN_OLD="$old"; RN_NEW="$new"
	mount_meta --write-back-interval-ms 0 || { RN_ERR="mount"; return 1; }
	rm -rf "$dir" 2>/dev/null
	mkdir -p "$dir" || { RN_ERR="mkdir"; unmount_clean; return 1; }
	# Make the target persisted (fsync = a hard barrier. The ancestor chain goes in too)
	python3 -c "
import os, sys
fd = os.open(sys.argv[1], os.O_CREAT | os.O_WRONLY | os.O_TRUNC, 0o644)
os.write(fd, sys.argv[2].encode())
os.fsync(fd)
os.close(fd)
" "$target" "$old" || { RN_ERR="creating the target"; unmount_clean; return 1; }
	if [ "$how" = "excl" ]; then
		# An O_EXCL create = write-through under heuristic (c) = the **persisted source** path
		# (sed -i and editors that use mkstemp take this one)
		python3 -c "
import os, sys
fd = os.open(sys.argv[1], os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o644)
os.write(fd, sys.argv[2].encode())
os.close(fd)
" "$tmp" "$new" || { RN_ERR="creating the tmp (O_EXCL)"; unmount_clean; return 1; }
	fi
	if [ "$how" = "redirect" ]; then
		# A create without O_EXCL = the pending source path (the side replaced in a single tx)
		printf '%s' "$new" > "$tmp" || { RN_ERR="creating the tmp (redirect)"; unmount_clean; return 1; }
	fi
	mv "$tmp" "$target" || { RN_ERR="mv"; unmount_clean; return 1; }
	# It was not fsync'd, but (a) materializes the replacement synchronously, so the new st_size should be in the database
	local db_size="skipped"
	if db_ready; then
		db_size=$(q "select st_size from ${SCHEMA}.${PREFIX}inode where name = 'target.txt' and parent_id in (select id from ${SCHEMA}.${PREFIX}inode where name = 'rn_$case')")
	fi
	unmount_crash
	mount_meta --write-back-interval-ms 0 || { RN_ERR="remount"; return 1; }
	local present=0 content=""
	if [ -e "$target" ]; then
		present=1
		content=$(cat "$target" 2>/dev/null)
	fi
	local size=0
	[ "$present" = "1" ] && size=$(stat -c %s "$target" 2>/dev/null)
	unmount_clean
	RN_PRESENT="$present"; RN_CONTENT="$content"; RN_SIZE="$size"; RN_DBSIZE="$db_size"
	return 0
}

test_meta_rename_over_existing_pending_source() {
	# What it can detect: that the ext4-2009-style window of "losing both the old and the new" is not open
	# (a pending temp is put over a persisted target and it crashes immediately -> the target is either the old
	#  or the new one. Neither disappearing nor zero length is allowed). On top of that, the st_size in the
	#  database confirms that "the replacement was materialized synchronously"
	#  = that heuristic (a) actually fired (without it the entry stays pending = the database has the old size -> FAIL).
	rename_over_existing_case pend redirect || { fail "${RN_ERR:-setup}"; return; }
	if [ "$RN_PRESENT" != "1" ]; then
		fail "the target vanished on a kill -9 right after rename-over-existing (both the old and the new were lost)"
		return
	fi
	if [ "$RN_SIZE" = "0" ]; then
		fail "the target became zero length (both the old and the new were lost)"
		return
	fi
	if [ "$RN_CONTENT" != "$RN_OLD" ] && [ "$RN_CONTENT" != "$RN_NEW" ]; then
		fail "the target's contents are neither the old nor the new one ('$RN_CONTENT')"
		return
	fi
	if [ "$RN_DBSIZE" != "skipped" ] && [ "$RN_DBSIZE" != "${#RN_NEW}" ]; then
		fail "the st_size in the database right after the mv is not the new size ($RN_DBSIZE / expected ${#RN_NEW}) = the replacement was not materialized synchronously"
		return
	fi
	pass
}

test_meta_rename_over_existing_excl_source() {
	# What it can detect: that there is no loss window even on the two-tx path where the source is write-through
	# (O_EXCL = sed -i and editors that use mkstemp) (the order "commit the new data -> delete the target and commit the rename").
	# If the order were reversed (deleting the target first), it would show up here as a disappearance or zero length.
	rename_over_existing_case excl excl || { fail "${RN_ERR:-setup}"; return; }
	if [ "$RN_PRESENT" != "1" ]; then
		fail "the target vanished on a kill -9 right after rename-over-existing (both the old and the new were lost)"
		return
	fi
	if [ "$RN_SIZE" = "0" ]; then
		fail "the target became zero length (both the old and the new were lost)"
		return
	fi
	if [ "$RN_CONTENT" != "$RN_OLD" ] && [ "$RN_CONTENT" != "$RN_NEW" ]; then
		fail "the target's contents are neither the old nor the new one ('$RN_CONTENT')"
		return
	fi
	pass
}

# ----- stage 2: synchronization heuristic (b) the close of a truncated inode is synchronous -----

# The shared part that "throws the old chunks away, writes, and only closes" and then kill -9s.
# $1 = the case name / $2 = how it is thrown away (otrunc | truncate_cmd)
# The results go into TR_*.
truncate_sync_close_case() {
	local case="$1" how="$2"
	local dir="$TEST_ROOT/tr_$case"
	local f="$dir/file.txt"
	local old="OLD-OLD-OLD-OLD" new="NEWDATA"
	TR_ERR=""; TR_CONTENT=""; TR_DBSIZE=""; TR_OLD="$old"; TR_NEW="$new"
	mount_meta --write-back-interval-ms 0 || { TR_ERR="mount"; return 1; }
	rm -rf "$dir" 2>/dev/null
	mkdir -p "$dir" || { TR_ERR="mkdir"; unmount_clean; return 1; }
	# Prepare a persisted old file (materialized with fsync)
	python3 -c "
import os, sys
fd = os.open(sys.argv[1], os.O_CREAT | os.O_WRONLY | os.O_TRUNC, 0o644)
os.write(fd, sys.argv[2].encode())
os.fsync(fd)
os.close(fd)
" "$f" "$old" || { TR_ERR="creating the old file"; unmount_clean; return 1; }
	if [ "$how" = "otrunc" ]; then
		# open with O_TRUNC -> write -> **close only** (no fsync)
		python3 -c "
import os, sys
fd = os.open(sys.argv[1], os.O_WRONLY | os.O_TRUNC)
os.write(fd, sys.argv[2].encode())
os.close(fd)
" "$f" "$new" || { TR_ERR="O_TRUNC open/write/close"; unmount_clean; return 1; }
	fi
	if [ "$how" = "truncate_cmd" ]; then
		# truncate(2) on its own (a path that does not go through O_TRUNC) -> write with O_APPEND and only close
		truncate -s 0 "$f" || { TR_ERR="truncate -s 0"; unmount_clean; return 1; }
		printf '%s' "$new" >> "$f" || { TR_ERR="append"; unmount_clean; return 1; }
	fi
	if db_ready; then
		TR_DBSIZE=$(q "select st_size from ${SCHEMA}.${PREFIX}inode where name = 'file.txt' and parent_id in (select id from ${SCHEMA}.${PREFIX}inode where name = 'tr_$case')")
	fi
	unmount_crash
	mount_meta --write-back-interval-ms 0 || { TR_ERR="remount"; return 1; }
	TR_CONTENT=$(cat "$f" 2>/dev/null)
	unmount_clean
	return 0
}

test_meta_otrunc_close_is_synchronous() {
	# What it can detect: "the close of an inode whose old chunks were thrown away is degraded to a synchronous flush" (heuristic b).
	# Without it, O_TRUNC erases the old one while the new one is deferred, and kill -9 leaves
	# **zero-length junk** (the classic accident of an editor or a `>` redirection save).
	truncate_sync_close_case otrunc otrunc || { fail "${TR_ERR:-setup}"; return; }
	if [ "$TR_CONTENT" != "$TR_NEW" ]; then
		fail "the contents of O_TRUNC + close only did not survive kill -9 ('$TR_CONTENT' / expected '$TR_NEW')"
		return
	fi
	if [ -n "$TR_DBSIZE" ] && [ "$TR_DBSIZE" != "${#TR_NEW}" ]; then
		fail "the st_size in the database right after the close is $TR_DBSIZE (expected ${#TR_NEW}) = the close was not degraded to synchronous"
		return
	fi
	pass
}

test_meta_truncate_syscall_close_is_synchronous() {
	# What it can detect: that the mark for "the close of an inode whose old chunks were thrown away is
	# synchronous" **also applies to a write after the fd that issued the truncate has been closed**.
	# `truncate -s 0 f; cmd >> f` (a log rotation, or a `truncate` plus append script) uses **a different fd**
	# for the truncate and for the write, so if the mark is consumed by "the close of the fd that truncated",
	# zero-length junk is left behind.
	#
	# ** **When this was found it FAILed (it was a reproduction test for an unfixed defect).**
	#    **It was resolved in A-7 and is green now.** The condition for clearing the mark was changed to
	#    "only when unflushed work was actually written out", so it is no longer consumed by the truncate
	#    side's close (A-6 / A-7 in docs/design/metadata-write-back-reviews.md). What was measured back then:
	#      after fsync   db=15 fs=15
	#      truncate -s 0 db=0  fs=0    <- discarding the old chunks is **persisted synchronously** and the mark is consumed here
	#      append        db=0  fs=7    <- the new data is pending
	#      after kill -9 exists=yes size=0 content=''  <- **zero-length junk** (both the old and the new were lost)
	#    The window "erase the old at once and defer the new", which stage 2's scope forbids, is open on this path.
	truncate_sync_close_case cmd truncate_cmd || { fail "${TR_ERR:-setup}"; return; }
	if [ "$TR_CONTENT" = "$TR_NEW" ]; then
		pass
		return
	fi
	if [ "$TR_CONTENT" = "$TR_OLD" ]; then
		# If the old one is entirely intact, no loss happened (the heuristic did not fire, but that is not a contract violation)
		pass
		return
	fi
	fail "truncate(2) + append + close lost both the old and the new (content='$TR_CONTENT' / old='$TR_OLD' new='$TR_NEW' / the st_size in the database right after the close=${TR_DBSIZE:-?})"
}

# ----- stage 2: synchronization heuristic (c) an O_EXCL create is write-through -----

test_meta_exclusive_create_is_write_through() {
	# What it can detect: that an `O_EXCL` create **does not become pending and is INSERTed into the database
	# at once** (= it works as a locking primitive). In the same test it also confirms that
	#   - a create without O_EXCL has no row in the database (= still deferred)
	#   - a mkdir has no row in the database either (the ruling of as-built difference 5. A change here gets noticed)
	# which rules out the vacuous case of "actually everything was write-through / a background flush had run".
	# It also checks that a second O_EXCL gives EEXIST (confirming the shape that leaves the decision to the database's unique constraint).
	if ! db_ready; then
		skip "the database cannot be checked with psql (this test is about the immediate INSERT on the database side)"
		return
	fi
	mount_meta --write-back-interval-ms 0 || { fail "mount"; return; }
	local dir="$TEST_ROOT/ex"
	rm -rf "$dir" 2>/dev/null
	mkdir -p "$dir" || { fail "mkdir"; return; }
	fsyncdir "$dir" || { fail "fsyncdir"; return; }
	# The (c) path: an O_EXCL create -> write -> close (no fsync)
	python3 -c "
import os, sys
fd = os.open(sys.argv[1], os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o644)
os.write(fd, b'lock')
os.close(fd)
" "$dir/excl.txt" || { fail "O_EXCL create"; return; }
	# The controls: a create without O_EXCL and a mkdir (both still deferred)
	echo plain > "$dir/plain.txt" || { fail "plain create"; return; }
	mkdir "$dir/sub" || { fail "mkdir sub"; return; }
	local parent="select id from ${SCHEMA}.${PREFIX}inode where name = 'ex'"
	local n_excl=$(q "select count(*) from ${SCHEMA}.${PREFIX}inode where name = 'excl.txt' and parent_id in ($parent)")
	local n_plain=$(q "select count(*) from ${SCHEMA}.${PREFIX}inode where name = 'plain.txt' and parent_id in ($parent)")
	local n_sub=$(q "select count(*) from ${SCHEMA}.${PREFIX}inode where name = 'sub' and parent_id in ($parent)")
	# The second O_EXCL is EEXIST
	local excl_twice=ok
	python3 -c "
import os, sys
try:
    fd = os.open(sys.argv[1], os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o644)
    os.close(fd)
except FileExistsError:
    sys.exit(0)
sys.exit(1)
" "$dir/excl.txt" || excl_twice=ng
	local listed=$(ls "$dir" | tr '\n' ' ')
	unmount_clean
	if [ "$n_excl" != "1" ]; then
		fail "there is no database row right after the O_EXCL create ($n_excl row(s)) = heuristic (c) is not write-through"
		return
	fi
	if [ "$n_plain" != "0" ]; then
		fail "the create without O_EXCL showed up in the database ($n_plain row(s)) = it is not deferred (this test's control is broken)"
		return
	fi
	if [ "$n_sub" != "0" ]; then
		fail "the mkdir showed up in the database ($n_sub row(s)) = it contradicts the as-built ruling (mkdir is deferred)"
		return
	fi
	if [ "$excl_twice" != "ok" ]; then
		fail "the second O_EXCL create was not EEXIST"
		return
	fi
	if [[ "$listed" != *"plain.txt"* ]] || [[ "$listed" != *"sub"* ]]; then
		fail "the deferred create / mkdir do not show up in ls ($listed)"
		return
	fi
	pass
}

# ----- stage 2: regression (a read while the data_id is still only reserved) -----

test_meta_read_after_close_no_flush() {
	# What it can detect: that reading "a file that has been closed but whose {prefix}data row is not in the
	# database yet" does not give -EIO (the regression for the ReadData LoadChunkSize bug fixed in stage 2;
	# back when it did a QuerySingle on the reserved data_id, 12 tests failed).
	# **The read cache is turned off** (cache_data_max_bytes = 0) to push the chunk-size resolution onto the database path.
	# It also asserts that there is no inode row in the database, so it cannot become the vacuous "it had actually been flushed".
	mount_meta --write-back-interval-ms 0 --cache-data-max-bytes 0 || { fail "mount"; return; }
	local dir="$TEST_ROOT/rd"
	rm -rf "$dir" 2>/dev/null
	mkdir -p "$dir" || { fail "mkdir"; return; }
	local src=$(mktemp) || { fail "mktemp"; return; }
	head -c 3145728 /dev/urandom > "$src"
	# Producing a pending entry needs **a create with neither O_EXCL nor O_TRUNC**:
	#   - `cp` opens a new destination with O_CREAT|O_EXCL (confirmed with strace) = write-through under heuristic (c)
	#   - `> file` (a redirection) carries O_TRUNC
	# so python is used with a plain O_CREAT|O_WRONLY. It only closes, with neither fsync nor sync.
	python3 -c "
import os, sys
data = open(sys.argv[1], 'rb').read()
fd = os.open(sys.argv[2], os.O_CREAT | os.O_WRONLY, 0o644)
os.write(fd, data)
os.close(fd)
" "$src" "$dir/big.bin" || { fail "create+write+close"; rm -f "$src"; return; }
	local n_rows="skipped"
	if db_ready; then
		n_rows=$(q "select count(*) from ${SCHEMA}.${PREFIX}inode where name = 'big.bin' and parent_id in (select id from ${SCHEMA}.${PREFIX}inode where name = 'rd')")
	fi
	local cmp_rc=0
	cmp -s "$src" "$dir/big.bin" || cmp_rc=$?
	# A one-shot read (open/read/close) must not give -EIO either
	local head_rc=0
	head -c 4096 "$dir/big.bin" > /dev/null 2>&1 || head_rc=$?
	rm -f "$src"
	unmount_clean
	if [ "$n_rows" != "skipped" ] && [ "$n_rows" != "0" ]; then
		fail "there was a database row right after the close ($n_rows) = the close-no-flush state could not be produced (the test's premise no longer holds)"
		return
	fi
	if [ "$cmp_rc" != "0" ]; then
		fail "a closed, unflushed file cannot be read / the contents differ (cmp rc=$cmp_rc)"
		return
	fi
	if [ "$head_rc" != "0" ]; then
		fail "a one-shot read of a file whose data_id is still only reserved failed (rc=$head_rc / suspect -EIO)"
		return
	fi
	pass
}

test_meta_hardlink_sibling_survives_unlink() {
	# What it can detect: contents written through a hardlink sibling disappearing silently, **without any crash**,
	# merely from unlinking the inode that wrote them (A-1 of round A).
	# There is one dirty entry per data body, and its ledger entry pins "the inode that wrote first".
	# When the flush looks only at that inode's liveness and discards the dirty data, the contents disappear
	# although the sibling is still alive.
	# Measured (dev server, schema pgfs_test): before the fix cat g = 'A' (B disappeared and st_size=1) / after the fix 'AB'.
	#
	# * Write with `>>` (no O_TRUNC). Slipping a `>` in rebuilds the data body and breaks the hardlink sharing
	#   itself (a separate known defect),
	#   so it would not enter the path this test wants to see.
	# * The ln / rm are slipped in while the fd is still open in order to raise the unlink while the dirty data is still on the ledger.
	mount_meta --write-back-interval-ms 200 || { fail "mount"; return; }
	local dir="$TEST_ROOT/hlsib"
	rm -rf "$dir" 2>/dev/null
	mkdir -p "$dir" || { fail "mkdir"; return; }
	bash -c "exec 3>'$dir/f'; printf A >&3; ln '$dir/f' '$dir/g'; printf B >> '$dir/g'; rm '$dir/f'; exec 3>&-" || { fail "setup"; return; }
	sleep 2
	local content
	content=$(cat "$dir/g" 2>/dev/null)
	local dbsize="skipped"
	if db_ready; then
		dbsize=$(q "select st_size from ${SCHEMA}.${PREFIX}inode where name = 'g' and parent_id in (select id from ${SCHEMA}.${PREFIX}inode where name = 'hlsib')")
	fi
	unmount_clean
	if [ "$content" != "AB" ]; then
		fail "a write through a hardlink sibling disappeared (cat g = '$content' / expected 'AB')"
		return
	fi
	if [ "$dbsize" != "skipped" ] && [ "$dbsize" != "2" ]; then
		fail "the st_size in the database is $dbsize (expected 2) = the size was not distributed to the sibling"
		return
	fi
	pass
}

# ----- round B: B-9, the effective mode of the two-phase flip -----

test_meta_live_flip_publishes_effective_mode() {
	# What it can detect: that the first phase of the two-phase flip (intake closed) **is visible in status** (B-9).
	# The setting is still on but no new pending entries are accepted, so printing only `on` would be a lie.
	# The stats only reach the database on the heartbeat (30 seconds), so without **writing them immediately on
	# the transition** the state becomes "the longer the flip, the more you want the information and the less you can see it".
	#
	# * To make the observation possible, **the flip is deliberately slowed down** (by holding 800 pending entries).
	#   On Citus each one takes a little over 10 ms, so the first phase lasts several seconds.
	if ! db_ready; then
		skip "the stats cannot be checked with psql"
		return
	fi
	if [ -z "$CONN" ]; then
		skip "the connection cannot be read from the settings file (pgfsctl is needed for the live flip)"
		return
	fi
	mount_meta --write-back-interval-ms 0 || { fail "mount"; return; }
	local dir="$TEST_ROOT/b9"
	rm -rf "$dir" 2>/dev/null
	mkdir -p "$dir" || { fail "mkdir"; return; }
	python3 -c "
import os, sys
root = sys.argv[1]
for i in range(800):
    fd = os.open(root + '/f%d.txt' % i, os.O_CREAT | os.O_WRONLY, 0o644)
    os.write(fd, b'x')
    os.close(fd)
" "$dir" || { unmount_clean; fail "creating the pending entries"; return; }
	# * Do not query for a single value. {prefix}mounts can contain rows with a fresh heartbeat left by other
	#   tests, so a scalar select returns several rows and the comparison never holds (the shape B-5's test hit once).
	#   **Count whether at least one row matches the condition** instead.
	local live_where="heartbeat_at > (now() AT TIME ZONE 'UTC') - interval '2 minutes'"
	# Issue the live off (pgfsctl returns without waiting for an ack)
	"$BIN/pgfsctl" config set mount.write_back_metadata false -c "$CONN" -s "$SCHEMA" >/dev/null 2>&1
	# Watch for the first phase (intake closed) appearing in status
	local seen=no i=0
	while [ $i -lt 100 ]; do
		if [ "$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts where $live_where and stats->'writeBackMetadata'->>'intakeClosed' = 'true'")" != "0" ]; then
			seen=yes
			break
		fi
		sleep 0.1
		i=$((i+1))
	done
	# Wait for the flip to complete (enabled=false and the intake reopened)
	local done=no
	i=0
	while [ $i -lt 600 ]; do
		local settled=$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts where $live_where and stats->'writeBackMetadata'->>'enabled' = 'false' and stats->'writeBackMetadata'->>'intakeClosed' = 'false'")
		if [ "$settled" != "0" ]; then
			done=yes
			break
		fi
		sleep 0.2
		i=$((i+1))
	done
	# Cleanup: put the setting back
	"$BIN/pgfsctl" config set mount.write_back_metadata true -c "$CONN" -s "$SCHEMA" >/dev/null 2>&1
	sleep 1
	unmount_clean
	if [ "$seen" != "yes" ]; then
		fail "the two-phase flip's intake-closed (effectively off) does not appear in status = it reports the lie \"on\" for the whole flip"
		return
	fi
	if [ "$done" != "yes" ]; then
		fail "the flip never completes (it does not reach enabled=false with the intake reopened)"
		return
	fi
	pass
}

# ----- round B: B-7, destructive operations during the error state -----

test_meta_error_state_blocks_destroy_but_allows_cancel() {
	# What it can detect: that during the error state **operations that destroy a persisted body are stopped**
	# and that **operations that merely discard a pending entry still go through** (B-7).
	#   - unlink / truncate of a persisted file is -EIO
	#   - **unlink of a pending entry goes through** = B-1's means of recovery is not blocked
	#   - after the recovery a create goes through
	# It is the contract that keeps the state "new data is not accepted yet old data keeps disappearing" from arising.
	# * The framing is not "unlink was made an exception" but "discarding a pending entry was never destructive
	#   in the first place", so **a persisted unlink being stopped** and **a pending unlink going through** are
	#   checked in the same test (either one alone would go green even if the rule were misread).
	if ! db_ready; then
		skip "an occupying row cannot be created with psql (the fault injection needs the database)"
		return
	fi
	mount_meta --write-back-interval-ms 0 --write-back-flush-timeout-ms 2000 --write-back-metadata-exclusive-create defer || { fail "mount"; return; }
	local dir="$TEST_ROOT/b7"
	rm -rf "$dir" 2>/dev/null
	mkdir -p "$dir" || { fail "mkdir"; return; }
	fsyncdir "$dir" || { fail "fsyncdir"; return; }
	local pid=$(q "select id from ${SCHEMA}.${PREFIX}inode where name = 'b7' and parent_id in (select id from ${SCHEMA}.${PREFIX}inode where name = 'wbmeta')")
	if [ -z "$pid" ]; then
		unmount_clean
		fail "the parent directory's id cannot be looked up in the database"
		return
	fi
	# Create one persisted body (fsync'd down into the database)
	plain_create "$dir/old.txt" keepme || { unmount_clean; fail "create old.txt"; return; }
	python3 -c "
import os, sys
fd = os.open(sys.argv[1], os.O_WRONLY)
os.fsync(fd)
os.close(fd)
" "$dir/old.txt" || { unmount_clean; fail "fsync old.txt"; return; }
	# Create a defer O_EXCL create while still pending and let psql occupy the same name to collide with it
	excl_create "$dir/race.lock" mine || { unmount_clean; fail "O_EXCL create"; return; }
	INJECT_CLEANUP_SQL="delete from ${SCHEMA}.${PREFIX}inode where parent_id = $pid and created_by = 'wbmeta-test'"
	q "insert into ${SCHEMA}.${PREFIX}inode (parent_id, name, uname, gname, st_mode, created_by, updated_by) values ($pid, 'race.lock', 'wbmeta', 'wbmeta', 33188, 'wbmeta-test', 'wbmeta-test')" >/dev/null
	python3 -c "
import os, sys
for _ in range(6):
    try:
        fd = os.open(sys.argv[1], os.O_WRONLY)
        os.fsync(fd)
        os.close(fd)
    except OSError:
        pass
" "$dir/race.lock" 2>/dev/null
	# --- the behaviour during the error state ---
	local rm_persisted=0
	rm -f "$dir/old.txt" 2>/dev/null || rm_persisted=$?
	local trunc_persisted
	trunc_persisted=$(python3 -c "
import os, sys
try:
    os.truncate(sys.argv[1], 0)
    print('OK')
except OSError as e:
    print('ERRNO%d' % e.errno)
" "$dir/old.txt" 2>/dev/null)
	local kept=$(cat "$dir/old.txt" 2>/dev/null)
	# The unlink of a pending entry = B-1's means of recovery. It has to go through
	local rm_pending=0
	rm -f "$dir/race.lock" 2>/dev/null || rm_pending=$?
	local after_create
	after_create=$(python3 -c "
import os, sys
try:
    fd = os.open(sys.argv[1], os.O_CREAT | os.O_WRONLY, 0o644)
    os.close(fd)
    print('OK')
except OSError as e:
    print('ERRNO%d' % e.errno)
" "$dir/after.txt" 2>/dev/null)
	unmount_clean
	cleanup_injection
	if [ "$rm_persisted" = "0" ]; then
		fail "the unlink of a persisted file went through during the error state (old data keeps disappearing)"
		return
	fi
	if [ "$trunc_persisted" = "OK" ]; then
		fail "the truncate of a persisted file went through during the error state"
		return
	fi
	if [ "$kept" != "keepme" ]; then
		fail "the persisted file's contents were corrupted ('$kept' / expected 'keepme')"
		return
	fi
	if [ "$rm_pending" != "0" ]; then
		fail "the unlink of a pending entry was refused during the error state (rc=$rm_pending) = B-1's means of recovery is blocked"
		return
	fi
	if [ "$after_create" != "OK" ]; then
		fail "a create does not go through after the recovery ($after_create)"
		return
	fi
	pass
}

# ----- round B: B-6, the loss report carries paths and a breakdown -----

test_meta_loss_report_has_paths_and_breakdown() {
	# What it can detect: that the loss report is **path-bearing** and reports **what it truncated, with a breakdown** (B-6).
	#   - it prints `/wbmeta/b6/many/f0.txt` rather than `id:123 parent:456 name:f0.txt`
	#   - after truncating at 32 it does not end with "and N more" but gives the total, the dir/file breakdown and the byte count
	# The observation reads B-2's gravestone (stats->'lost'). No log file has to be dug through, and
	# **the record that stays in the database** is what gets verified (which is what operations actually look at).
	#
	# How it is built: colliding a pending **directory** with a different kind makes the pending children below
	# it unmaterializable too, so one injection produces more than 40 losses.
	if ! db_ready; then
		skip "the gravestone cannot be checked with psql"
		return
	fi
	q "delete from ${SCHEMA}.${PREFIX}mounts where (stats->>'unflushedLoss')::int > 0" >/dev/null
	mount_meta --write-back-interval-ms 0 --write-back-flush-timeout-ms 1500 || { fail "mount"; return; }
	local dir="$TEST_ROOT/b6"
	rm -rf "$dir" 2>/dev/null
	mkdir -p "$dir" || { fail "mkdir"; return; }
	fsyncdir "$dir" || { fail "fsyncdir"; return; }
	# Look the parent up narrowed to TEST_ROOT (a same-named directory elsewhere would grab a different parent)
	local pid=$(q "select id from ${SCHEMA}.${PREFIX}inode where name = 'b6' and parent_id in (select id from ${SCHEMA}.${PREFIX}inode where name = 'wbmeta')")
	if [ -z "$pid" ]; then
		unmount_clean
		fail "the parent directory's id cannot be looked up in the database"
		return
	fi
	# A pending directory plus 40 files inside it (neither is fsync'd)
	python3 -c "
import os, sys
root = sys.argv[1]
os.mkdir(root + '/many')
for i in range(40):
    fd = os.open(root + '/many/f%d.txt' % i, os.O_CREAT | os.O_WRONLY, 0o644)
    os.write(fd, b'abc')
    os.close(fd)
" "$dir" || { unmount_clean; fail "creating the pending tree"; return; }
	# * The injection: a **regular file** row with the same name as the pending directory (a different kind = unresolvable)
	INJECT_CLEANUP_SQL="delete from ${SCHEMA}.${PREFIX}inode where parent_id = $pid and created_by = 'wbmeta-test'"
	q "insert into ${SCHEMA}.${PREFIX}inode (parent_id, name, uname, gname, st_mode, created_by, updated_by) values ($pid, 'many', 'wbmeta', 'wbmeta', 33188, 'wbmeta-test', 'wbmeta-test')" >/dev/null
	unmount_clean
	local lost=$(q "select stats->>'lost' from ${SCHEMA}.${PREFIX}mounts where (stats->>'unflushedLoss')::int > 0 limit 1")
	local n_path=$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts m, jsonb_array_elements_text(m.stats->'lost') e where (m.stats->>'unflushedLoss')::int > 0 and e like '%/many/f%'")
	local n_more=$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts m, jsonb_array_elements_text(m.stats->'lost') e where (m.stats->>'unflushedLoss')::int > 0 and e like '%in total%dir%file%'")
	# cleanup
	q "delete from ${SCHEMA}.${PREFIX}mounts where (stats->>'unflushedLoss')::int > 0" >/dev/null
	cleanup_injection
	if [ -z "$lost" ]; then
		fail "the gravestone carries no lost (no loss happened = the test's premise no longer holds)"
		return
	fi
	if [ "$n_path" = "0" ]; then
		fail "the loss report carries no paths (ids alone do not tell operations what was lost)"
		return
	fi
	if [ "$n_more" = "0" ]; then
		fail "the truncation breakdown (total / dir / file) is missing. A rounded number alone cannot be reconciled by whoever receives it"
		return
	fi
	pass
}

# ----- round B: B-5, the error state is written immediately on the transition -----

test_meta_error_state_is_published_immediately() {
	# What it can detect: that entering the error state reaches {prefix}mounts **without waiting for the
	# heartbeat period** (B-5). The heartbeat is every 30 seconds, so without writing it immediately on the
	# transition `pgfsctl status` shows **a stale green** for up to one period = operations misread it as "still fine".
	# What is looked at here is not status's rendering but **whether it landed in the database** (the rendering is StatusCommand's job).
	if ! db_ready; then
		skip "the mounts rows cannot be checked with psql"
		return
	fi
	mount_meta --write-back-interval-ms 0 --write-back-flush-timeout-ms 2000 || { fail "mount"; return; }
	# * Do not use the pid to identify the row. {prefix}mounts holds plenty of stale rows left by abnormal
	#    exits, and the OS reuses pids so they can collide (which shows up as failing only in a full run).
	#    **Count only the rows with a fresh heartbeat.**
	local live_where="heartbeat_at > (now() AT TIME ZONE 'UTC') - interval '2 minutes'"
	local dir="$TEST_ROOT/b5"
	rm -rf "$dir" 2>/dev/null
	mkdir -p "$dir" || { fail "mkdir"; return; }
	fsyncdir "$dir" || { fail "fsyncdir"; return; }
	# **Look the parent up narrowed to TEST_ROOT.** Looking it up by name alone grabs a different parent when a
	# same-named directory exists directly under the mount, and the injection has no effect = "a test that
	# cannot enter" (measured: it picked up a ~/mnt/pgfs/b5 created during manual verification and failed only in a full run).
	local pid=$(q "select id from ${SCHEMA}.${PREFIX}inode where name = 'b5' and parent_id in (select id from ${SCHEMA}.${PREFIX}inode where name = 'wbmeta')")
	if [ -z "$pid" ]; then
		unmount_clean
		fail "the parent directory's id cannot be looked up in the database"
		return
	fi
	plain_create "$dir/victim.txt" x || { unmount_clean; fail "create victim.txt"; return; }
	INJECT_CLEANUP_SQL="delete from ${SCHEMA}.${PREFIX}inode where parent_id = $pid and created_by = 'wbmeta-test'"
	q "insert into ${SCHEMA}.${PREFIX}inode (parent_id, name, uname, gname, st_mode, created_by, updated_by) values ($pid, 'victim.txt', 'wbmeta', 'wbmeta', 16877, 'wbmeta-test', 'wbmeta-test')" >/dev/null
	# Fire fsync at the same target until it passes the threshold (5) -> into the error state
	python3 -c "
import os, sys
for _ in range(6):
    try:
        fd = os.open(sys.argv[1], os.O_WRONLY)
        os.fsync(fd)
        os.close(fd)
    except OSError:
        pass
" "$dir/victim.txt" 2>/dev/null
	# * Read without waiting out the heartbeat period (30 seconds). Without the immediate write it stays empty.
	local state=$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts where $live_where and stats->'writeBack'->>'errorState' is not null")
	# The release must be written immediately too: remove the collision and let one fsync succeed
	q "$INJECT_CLEANUP_SQL" >/dev/null
	INJECT_CLEANUP_SQL=""
	python3 -c "
import os, sys
try:
    fd = os.open(sys.argv[1], os.O_WRONLY)
    os.fsync(fd)
    os.close(fd)
except OSError:
    pass
" "$dir/victim.txt" 2>/dev/null
	local cleared=$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts where $live_where and stats->'writeBack'->>'errorState' is not null")
	unmount_clean
	if [ "$state" != "1" ]; then
		fail "it entered the error state yet nothing was written immediately into mounts (matching rows $state / expected 1. It is waiting for the heartbeat period = status shows a stale green)"
		return
	fi
	if [ "$cleared" != "0" ]; then
		fail "the error state was released yet nothing was written immediately into mounts (matching rows $cleared / expected 0)"
		return
	fi
	pass
}

# ----- round B: B-4, a live off of audit does not fabricate a loss -----

test_meta_audit_live_off_does_not_fake_loss() {
	# What it can detect: that **an unmount after turning `audit.enabled` off live does not emit a "this will be
	# lost" report when the real loss is zero** (B-4).
	# The orphan audit queue is counted by UnflushedCount(), so once it stops being written with off, the unmount
	# turns into deadline expiry -> a loss report -> exit 4 (and systemd treats that as failed).
	# The observation uses B-2's gravestone: a fabricated loss would leave a row with unflushedLoss > 0 in {prefix}mounts.
	#
	# * As things stand, B-3 (writing the cancellation audit synchronously) removed the producer of the orphan
	#   queue, so this path **cannot arise structurally**. This test is a regression guard for "not reopening the
	#   same hole when a path that enqueues is added in the future".
	if ! db_ready; then
		skip "the gravestone cannot be checked with psql"
		return
	fi
	if [ -z "$CONN" ]; then
		skip "the connection cannot be read from the settings file (pgfsctl is needed for the live off)"
		return
	fi
	local audit_table="${PREFIX}audit"
	local has_audit=$(q "select count(*) from information_schema.tables where table_schema = '${SCHEMA}' and table_name = '${audit_table}'")
	if [ "$has_audit" != "1" ]; then
		skip "there is no audit table (this can only be verified on an FS created with mkfs --audit)"
		return
	fi
	q "delete from ${SCHEMA}.${PREFIX}mounts where (stats->>'unflushedLoss')::int > 0" >/dev/null
	local prev=$(q "select value::text from ${SCHEMA}.${PREFIX}settings where scope = 'audit' and key = 'enabled'")
	audit_on || { fail "could not set audit.enabled to true (the test's premise no longer holds)"; return; }
	mount_meta --write-back-interval-ms 0 --write-back-flush-timeout-ms 3000 || { fail "mount"; return; }
	local dir="$TEST_ROOT/b4"
	rm -rf "$dir" 2>/dev/null
	mkdir -p "$dir" || { fail "mkdir"; return; }
	fsyncdir "$dir" || { fail "fsyncdir"; return; }
	# While auditing is enabled, "create and delete" (which produces a cancellation audit)
	plain_create "$dir/tmp1.txt" x || { fail "create tmp1"; return; }
	rm -f "$dir/tmp1.txt" || { fail "unlink tmp1"; return; }
	# * This is where it goes live off
	local set_rc=0
	"$BIN/pgfsctl" config set audit.enabled false -c "$CONN" -s "$SCHEMA" >/dev/null 2>&1 || set_rc=$?
	# Create and delete once more after the off (a cancellation after the off does not enqueue an audit = correct)
	plain_create "$dir/tmp2.txt" y || { fail "create tmp2"; return; }
	rm -f "$dir/tmp2.txt" || { fail "unlink tmp2"; return; }
	unmount_clean
	local n_tomb=$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts where (stats->>'unflushedLoss')::int > 0")
	# cleanup
	q "delete from ${SCHEMA}.${PREFIX}mounts where (stats->>'unflushedLoss')::int > 0" >/dev/null
	q "delete from ${SCHEMA}.${audit_table} where name in ('tmp1.txt','tmp2.txt')" >/dev/null
	audit_restore "$prev"
	if [ "$set_rc" != "0" ]; then
		fail "the live off of audit.enabled failed (rc=$set_rc = the test's premise no longer holds)"
		return
	fi
	if [ "$n_tomb" != "0" ]; then
		fail "a loss gravestone appeared although the real loss is zero (count $n_tomb / expected 0). The live off of audit is fabricating a loss"
		return
	fi
	pass
}

# ----- round B: B-3, the cancellation audit is written immediately -----

test_meta_cancel_audit_is_written_immediately() {
	# What it can detect: that **`create -> read -> unlink` cannot happen with zero audit trace** (B-3).
	# Piling the create/delete pair of a pure cancellation (a pending entry created and deleted within the
	# interval) onto an in-memory orphan queue and leaving it to a background flush loses the whole thing to a
	# `kill -9` or a PG failure = the evasion channel the design's audit section explicitly said it closes comes back.
	# Measured (before the fix, dev server): there were already 0 audit rows **before** the kill -9 (= not written
	# synchronously). After the fix the create and delete rows are in the database right after the unlink and survive the kill -9.
	if ! db_ready; then
		skip "the audit rows cannot be checked with psql"
		return
	fi
	local audit_table="${PREFIX}audit"
	local has_audit=$(q "select count(*) from information_schema.tables where table_schema = '${SCHEMA}' and table_name = '${audit_table}'")
	if [ "$has_audit" != "1" ]; then
		skip "there is no audit table (this can only be verified on an FS created with mkfs --audit)"
		return
	fi
	# **Delete what the last run left first.** Audit rows are only ever appended, so leftovers throw the count
	# assertion off (when this test was first written it picked up rows left from manual verification and failed on the first run only).
	q "delete from ${SCHEMA}.${audit_table} where name = 'ghost.txt'" >/dev/null
	# Turn audit.enabled on temporarily (the mount reads the setting from the database at start-up). The cleanup always puts it back.
	local prev=$(q "select value::text from ${SCHEMA}.${PREFIX}settings where scope = 'audit' and key = 'enabled'")
	audit_on || { fail "could not set audit.enabled to true (the test's premise no longer holds)"; return; }
	mount_meta --write-back-interval-ms 0 || { fail "mount"; return; }
	local dir="$TEST_ROOT/b3"
	rm -rf "$dir" 2>/dev/null
	mkdir -p "$dir" || { fail "mkdir"; return; }
	fsyncdir "$dir" || { fail "fsyncdir"; return; }
	# Create it while pending, read it and delete it (it only becomes pending with a create that uses neither O_EXCL nor O_TRUNC)
	plain_create "$dir/ghost.txt" secret || { fail "create ghost.txt"; return; }
	local body=$(cat "$dir/ghost.txt" 2>/dev/null)
	rm -f "$dir/ghost.txt" || { fail "unlink ghost.txt"; return; }
	# There have to be 2 rows in the database **while nothing has been unmounted yet** (= they were written synchronously)
	local n_before=$(q "select count(*) from ${SCHEMA}.${audit_table} where name = 'ghost.txt'")
	local ops=$(q "select string_agg(op, ',' order by occurred_at) from ${SCHEMA}.${audit_table} where name = 'ghost.txt'")
	unmount_crash
	local n_after=$(q "select count(*) from ${SCHEMA}.${audit_table} where name = 'ghost.txt'")
	# Cleanup: put the audit rows and the setting back
	q "delete from ${SCHEMA}.${audit_table} where name = 'ghost.txt'" >/dev/null
	audit_restore "$prev"
	if [ "$body" != "secret" ]; then
		fail "it could not be read while pending ('$body' / expected 'secret' = the test's premise no longer holds)"
		return
	fi
	if [ "$n_before" != "2" ]; then
		fail "there are no audit rows in the database right after the unlink (count $n_before / expected 2 = create + delete). It is still waiting on the orphan queue"
		return
	fi
	if [ "$ops" != "create,delete" ]; then
		fail "the audit ops are not create,delete ('$ops')"
		return
	fi
	if [ "$n_after" != "2" ]; then
		fail "the audit rows disappeared on kill -9 (count $n_after / expected 2)"
		return
	fi
	pass
}

# ----- round B: B-2, recording the loss in the database -----

test_meta_loss_is_recorded_in_db() {
	# What it can detect: that unflushed work that could not be written out within the unmount's deadline
	# **stays on the database side** (B-2).
	#   - the {prefix}mounts row is not DELETEd, and stats carries unflushedLoss / endedAt / ended
	#   - at the next mount a warning appears on **the parent process's stderr** (the daemon's child throws
	#     stderr away, so unless it comes from the pre-fork parent that holds the terminal it reaches nobody = the heart of B-2)
	#   - deleting the gravestone makes the warning go away too (it does not warn forever)
	# That the row disappears on a clean exit (with no loss) is watched by another test (the status ones).
	if ! db_ready; then
		skip "a colliding row cannot be created with psql (the fault injection needs the database)"
		return
	fi
	# Delete the old gravestones first (leftovers from a previous failure throw the count assertion off)
	q "delete from ${SCHEMA}.${PREFIX}mounts where (stats->>'unflushedLoss')::int > 0" >/dev/null
	mount_meta --write-back-interval-ms 0 --write-back-flush-timeout-ms 1500 || { fail "mount"; return; }
	local dir="$TEST_ROOT/loss"
	rm -rf "$dir" 2>/dev/null
	mkdir -p "$dir" || { fail "mkdir"; return; }
	fsyncdir "$dir" || { fail "fsyncdir"; return; }
	local pid=$(q "select id from ${SCHEMA}.${PREFIX}inode where name = 'loss'")
	if [ -z "$pid" ]; then
		unmount_clean
		fail "the parent directory's id cannot be looked up in the database"
		return
	fi
	plain_create "$dir/victim.txt" data || { unmount_clean; fail "create victim.txt"; return; }
	# * The fault injection: a same-named row of a different kind = an unresolvable collision -> the flush fails permanently
	INJECT_CLEANUP_SQL="delete from ${SCHEMA}.${PREFIX}inode where parent_id = $pid and created_by = 'wbmeta-test'"
	q "insert into ${SCHEMA}.${PREFIX}inode (parent_id, name, uname, gname, st_mode, created_by, updated_by) values ($pid, 'victim.txt', 'wbmeta', 'wbmeta', 16877, 'wbmeta-test', 'wbmeta-test')" >/dev/null
	unmount_clean
	local n_tomb=$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts where (stats->>'unflushedLoss')::int > 0")
	local loss=$(q "select max((stats->>'unflushedLoss')::int) from ${SCHEMA}.${PREFIX}mounts where (stats->>'unflushedLoss')::int > 0")
	local ended=$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts where stats->>'endedAt' is not null and (stats->>'unflushedLoss')::int > 0")
	# * **endedAt has to declare itself as UTC** (the trailing Z). The times on log lines are local, so without
	#   the mark **it looks like a different run 9 hours off inside the same log** (it was actually misread once).
	local ended_z=$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts where stats->>'endedAt' like '%Z' and (stats->>'unflushedLoss')::int > 0")
	# A warning has to appear on **the parent's stderr** at the next mount
	mkdir -p "$MOUNT_ROOT" 2>/dev/null
	local warn
	warn=$("$BIN/mount.pgfs" --setting-file "$SETTING_FILE" 2>&1 1>/dev/null | grep -c 'pgfs: warning')
	sleep 1
	unmount_clean
	# Deleting the gravestone has to make the warning go away
	q "delete from ${SCHEMA}.${PREFIX}mounts where (stats->>'unflushedLoss')::int > 0" >/dev/null
	local warn_after
	warn_after=$("$BIN/mount.pgfs" --setting-file "$SETTING_FILE" 2>&1 1>/dev/null | grep -c 'pgfs: warning')
	sleep 1
	unmount_clean
	cleanup_injection
	if [ "$n_tomb" != "1" ]; then
		fail "a loss happened yet no gravestone survives in {prefix}mounts (count $n_tomb / expected 1)"
		return
	fi
	if [ -z "$loss" ] || [ "$loss" -lt 1 ]; then
		fail "unflushedLoss is missing ('$loss')"
		return
	fi
	if [ "$ended" != "1" ]; then
		fail "endedAt is missing (count $ended / expected 1)"
		return
	fi
	if [ "$ended_z" != "1" ]; then
		fail "endedAt carries no UTC mark (a trailing Z) (count $ended_z / expected 1) = it will be misread as local time"
		return
	fi
	if [ "$warn" = "0" ]; then
		fail "no warning appears on the parent stderr at the next mount (emitting it only to the daemon's child reaches nobody)"
		return
	fi
	if [ "$warn_after" != "0" ]; then
		fail "the warning keeps appearing after the gravestone was deleted ($warn_after line(s))"
		return
	fi
	pass
}

# ----- round B: B-1, the write_back_metadata_exclusive_create knob -----

excl_create() {
	python3 -c "
import os, sys
fd = os.open(sys.argv[1], os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o644)
os.write(fd, sys.argv[2].encode())
os.close(fd)
" "$1" "$2"
}

test_meta_exclusive_create_defer_is_pending() {
	# What it can detect: that with `--write-back-metadata-exclusive-create defer` **an O_EXCL create becomes
	# pending** (= heuristic (c) can be opted out of deliberately). The counterpart of
	# test_meta_exclusive_create_is_write_through on the default write_through side.
	# In the same test it also checks that "going back to the default INSERTs into the database at once",
	# which rules out the vacuous "it was actually always pending / always write-through".
	if ! db_ready; then
		skip "the database cannot be checked with psql (this test is about there being no row on the database side)"
		return
	fi
	mount_meta --write-back-interval-ms 0 --write-back-metadata-exclusive-create defer || { fail "mount (defer)"; return; }
	local dir="$TEST_ROOT/exdefer"
	rm -rf "$dir" 2>/dev/null
	mkdir -p "$dir" || { fail "mkdir"; return; }
	fsyncdir "$dir" || { fail "fsyncdir"; return; }
	local parent="select id from ${SCHEMA}.${PREFIX}inode where name = 'exdefer'"
	excl_create "$dir/deferred.lock" lock || { fail "O_EXCL create (defer)"; return; }
	local n_defer=$(q "select count(*) from ${SCHEMA}.${PREFIX}inode where name = 'deferred.lock' and parent_id in ($parent)")
	# It also checks that it can be read (being unable to read it while pending would make it useless in practice)
	local body_defer=$(cat "$dir/deferred.lock" 2>/dev/null)
	unmount_clean
	# Re-establish with the default (write_through) for comparison
	mount_meta --write-back-interval-ms 0 || { fail "mount (write_through)"; return; }
	excl_create "$dir/through.lock" lock || { fail "O_EXCL create (write_through)"; return; }
	local n_through=$(q "select count(*) from ${SCHEMA}.${PREFIX}inode where name = 'through.lock' and parent_id in ($parent)")
	unmount_clean
	if [ "$n_defer" != "0" ]; then
		fail "the O_EXCL create was INSERTed into the database at once although this is defer (rows $n_defer / expected 0)"
		return
	fi
	if [ "$body_defer" != "lock" ]; then
		fail "an O_EXCL create that is still pending cannot be read (contents '$body_defer' / expected 'lock')"
		return
	fi
	if [ "$n_through" != "1" ]; then
		fail "the O_EXCL create did not go into the database with the default (write_through) (rows $n_through / expected 1)"
		return
	fi
	pass
}

test_meta_exclusive_create_defer_same_mount_eexist() {
	# What it can detect: that what defer loses is **cross-client exclusion only**, and that
	# **O_EXCL exclusion within the same mount is kept** (the ledger's TryAdd returns EEXIST on a (parent, name) collision).
	# Breaking this means the same mount can take the lock twice = the knob's own description becomes a lie.
	mount_meta --write-back-interval-ms 0 --write-back-metadata-exclusive-create defer || { fail "mount"; return; }
	local dir="$TEST_ROOT/exsame"
	rm -rf "$dir" 2>/dev/null
	mkdir -p "$dir" || { fail "mkdir"; return; }
	excl_create "$dir/same.lock" first || { fail "the first O_EXCL create"; return; }
	local second
	second=$(python3 -c "
import os, sys
try:
    fd = os.open(sys.argv[1], os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o644)
    os.close(fd)
    print('SUCCEEDED')
except FileExistsError:
    print('EEXIST')
except OSError as e:
    print('ERRNO%d' % e.errno)
" "$dir/same.lock" 2>/dev/null)
	local body=$(cat "$dir/same.lock" 2>/dev/null)
	unmount_clean
	if [ "$second" != "EEXIST" ]; then
		fail "with defer, a second O_EXCL create within the same mount is not EEXIST ($second)"
		return
	fi
	if [ "$body" != "first" ]; then
		fail "the contents of the first one were lost ('$body' / expected 'first')"
		return
	fi
	pass
}

test_meta_exclusive_create_defer_conflict_latches() {
	# What it can detect, when a defer-born pending entry hits a name collision at flush time:
	#   - **the occupant (the row already in the database) is not deleted** (it does not degrade to last-flush-wins)
	#   - fsync returns -EIO (it does not silently succeed)
	#   - **it can be recovered with an unlink** = the error state is released and **a following create goes through**
	#     (a pure cancel of the pending entry, so the database is not touched)
	# Those three.
	# * **Do not look only at "the unlink succeeds".** When this test was first written it looked only at the
	#    unlink's rc and **waved through a real bug where the error state was not released and the following
	#    create stayed at -EIO** (found during the Windows-side verification on real hardware). The recovery is
	#    judged by "the next operation goes through". It is reproduced by INSERTing the occupying row directly
	#    with psql rather than standing up two cross-client mounts
	# (the same fault injection as test_meta_error_state_blocks_and_clears).
	# * Insert the occupant as **the same kind (a regular file)**. A different kind falls over on the existing
	#   "a different kind is refused" before BornExclusive, and the test would not enter the branch it wants to see.
	if ! db_ready; then
		skip "an occupying row cannot be created with psql (the fault injection needs the database)"
		return
	fi
	mount_meta --write-back-interval-ms 0 --write-back-flush-timeout-ms 2000 --write-back-metadata-exclusive-create defer || { fail "mount"; return; }
	local dir="$TEST_ROOT/exconf"
	rm -rf "$dir" 2>/dev/null
	mkdir -p "$dir" || { fail "mkdir"; return; }
	fsyncdir "$dir" || { fail "fsyncdir"; return; }
	local pid=$(q "select id from ${SCHEMA}.${PREFIX}inode where name = 'exconf'")
	if [ -z "$pid" ]; then
		unmount_clean
		fail "the parent directory's id cannot be looked up in the database"
		return
	fi
	excl_create "$dir/race.lock" mine || { unmount_clean; fail "O_EXCL create"; return; }
	# * The fault injection: INSERT **a regular file with the same name** directly, as if another client had flushed first.
	INJECT_CLEANUP_SQL="delete from ${SCHEMA}.${PREFIX}inode where parent_id = $pid and created_by = 'wbmeta-test'"
	local oid=$(q "with ins as (insert into ${SCHEMA}.${PREFIX}inode (parent_id, name, uname, gname, st_mode, created_by, updated_by) values ($pid, 'race.lock', 'wbmeta', 'wbmeta', 33188, 'wbmeta-test', 'wbmeta-test') returning id) select id from ins")
	if [ -z "$oid" ]; then
		unmount_clean
		fail "the occupying row could not be INSERTed (the injection failed)"
		return
	fi
	# fsync should be -EIO (the collision cannot be resolved).
	# **Fire it until it passes the error state's threshold (5 in a row on the same target)** - once only latches
	# it without entering the error state, and the "recovery" assertion below would mean nothing.
	local fsync_rc=$(python3 -c "
import os, sys
last = 'OK'
for _ in range(6):
    try:
        fd = os.open(sys.argv[1], os.O_WRONLY)
        os.fsync(fd)
        os.close(fd)
        last = 'OK'
    except OSError as e:
        last = 'ERRNO%d' % e.errno
print(last)
" "$dir/race.lock" 2>/dev/null)
	# **That the occupant survives** is the point of this test
	local n_occupant=$(q "select count(*) from ${SCHEMA}.${PREFIX}inode where id = $oid")
	# While latched, a new create must be refused (confirming the premise that it is in the error state)
	local before_create
	before_create=$(python3 -c "
import os, sys
try:
    fd = os.open(sys.argv[1], os.O_CREAT | os.O_WRONLY, 0o644)
    os.close(fd)
    print('OK')
except OSError as e:
    print('ERRNO%d' % e.errno)
" "$dir/before.txt" 2>/dev/null)
	# The recovery: unlink on the loser's side (a pure cancel of the pending entry)
	local unlink_rc=0
	rm -f "$dir/race.lock" 2>/dev/null || unlink_rc=$?
	# **The heart of the recovery**: the error state is released and a following create goes through
	local after_create
	after_create=$(python3 -c "
import os, sys
try:
    fd = os.open(sys.argv[1], os.O_CREAT | os.O_WRONLY, 0o644)
    os.close(fd)
    print('OK')
except OSError as e:
    print('ERRNO%d' % e.errno)
" "$dir/after.txt" 2>/dev/null)
	local n_after=$(q "select count(*) from ${SCHEMA}.${PREFIX}inode where id = $oid")
	unmount_clean
	q "$INJECT_CLEANUP_SQL" >/dev/null
	INJECT_CLEANUP_SQL=""
	if [ "$n_occupant" != "1" ]; then
		fail "the defer collision deleted the occupant (it degraded to last-flush-wins. rows $n_occupant / expected 1)"
		return
	fi
	if [ "$fsync_rc" = "OK" ]; then
		fail "fsync returned success although there is a collision (it is swallowing it silently)"
		return
	fi
	if [ "$unlink_rc" != "0" ]; then
		fail "a pending entry cannot be unlinked while the error is latched (rc=$unlink_rc)"
		return
	fi
	if [ "$before_create" = "OK" ]; then
		fail "a new create goes through although the collision is latched (it is not in the error state = the test's premise no longer holds)"
		return
	fi
	if [ "$after_create" != "OK" ]; then
		fail "the error state is not released by the unlink and a create does not go through ($after_create)"
		return
	fi
	if [ "$n_after" != "1" ]; then
		fail "the unlink deleted the occupant as well (rows $n_after / expected 1)"
		return
	fi
	pass
}

# ----- stage 2: the two-phase flip of a live off -----

# A create with neither O_EXCL nor O_TRUNC (= the way to make something pending).
plain_create() {
	python3 -c "
import os, sys
fd = os.open(sys.argv[1], os.O_CREAT | os.O_WRONLY, 0o644)
os.write(fd, sys.argv[2].encode())
os.close(fd)
" "$1" "$2"
}

# Wait up to $3 * 0.2 seconds for a single-value query to reach the expected value.
wait_q() {
	local sql="$1" want="$2" tries="$3" i=0
	while [ $i -lt "$tries" ]; do
		[ "$(q "$sql")" = "$want" ] && return 0
		sleep 0.2
		i=$((i+1))
	done
	return 1
}

test_meta_live_off_flushes_pending_then_write_through() {
	# What it can detect: **the two phases** of `pgfsctl config set mount.write_back_metadata false`
	#   phase 1: close the new intake / phase 2: flush every pending entry it holds, then drop the flag
	# Missing either one gives "the pending entries it held do not reach the database until the unmount" or
	# "it is off yet a new create still becomes pending".
	# It goes as far as checking that a create right after the off **shows up in the database at once**, which
	# also tells it apart from an implementation that merely called FlushAll.
	# (write_back_metadata is File+Live = ephemeral, so it leaves no row in {prefix}settings)
	if ! db_ready; then
		skip "the database cannot be checked with psql (this test is about observing the database side)"
		return
	fi
	if [ -z "$CONN" ]; then
		skip "the connection cannot be read from the settings file (it cannot be handed to pgfsctl)"
		return
	fi
	mount_meta --write-back-interval-ms 0 || { fail "mount"; return; }
	local dir="$TEST_ROOT/flip"
	rm -rf "$dir" 2>/dev/null
	mkdir -p "$dir" || { fail "mkdir"; return; }
	fsyncdir "$dir" || { fail "fsyncdir"; return; }
	local parent="select id from ${SCHEMA}.${PREFIX}inode where name = 'flip'"
	local cnt="select count(*) from ${SCHEMA}.${PREFIX}inode where parent_id in ($parent)"
	mkdir "$dir/psub" || { fail "mkdir psub"; return; }
	plain_create "$dir/p1.txt" one || { fail "create p1"; return; }
	plain_create "$dir/p2.txt" two || { fail "create p2"; return; }
	local before=$(q "$cnt")
	if [ "$before" != "0" ]; then
		unmount_clean
		fail "there are already database rows before the flip ($before) = the pending state could not be produced (the test's premise no longer holds)"
		return
	fi
	local set_out set_rc=0
	set_out=$("$BIN/pgfsctl" config set mount.write_back_metadata false -c "$CONN" -s "$SCHEMA" 2>&1) || set_rc=$?
	if [ "$set_rc" != "0" ]; then
		unmount_clean
		fail "pgfsctl config set failed (rc=$set_rc): $set_out"
		return
	fi
	local flushed=ok
	wait_q "$cnt" 3 50 || flushed=ng
	local after=$(q "$cnt")
	# A create after it goes off is write-through = it shows up in the database at once (no polling)
	plain_create "$dir/wt.txt" three || { unmount_clean; fail "the create after the off"; return; }
	local wt=$(q "select count(*) from ${SCHEMA}.${PREFIX}inode where name = 'wt.txt' and parent_id in ($parent)")
	unmount_clean
	if [ "$flushed" != "ok" ]; then
		fail "config set does not flush every pending entry (database rows $after / expected 3)"
		return
	fi
	if [ "$wt" != "1" ]; then
		fail "a create after the live off does not show up in the database at once ($wt row(s)) = the new intake is not closed"
		return
	fi
	pass
}

# ----- stage 2: error floor 4, blocking back-pressure -----

test_meta_backpressure_blocking_inodes() {
	# What it can detect: that creating far more than the pending cap (write_back_max_inodes)
	#   - **does not jam** (an indefinite block or a deadlock FAILs on the timeout)
	#   - **drops nothing** (no create is lost on the path that absorbs the overflow with a flush)
	# The background flush is disabled with interval 0, so the only flush triggers are back-pressure and the unmount.
	# An implementation that blocks until it is back under the cap but "waits without flushing" hangs here for certain.
	if ! db_ready; then
		skip "the database cannot be checked with psql (the point is the number that landed)"
		return
	fi
	mount_meta --write-back-interval-ms 0 --write-back-max-inodes 4 --write-back-flush-timeout-ms 10000 \
		|| { fail "mount"; return; }
	local dir="$TEST_ROOT/bp"
	rm -rf "$dir" 2>/dev/null
	mkdir -p "$dir" || { fail "mkdir"; return; }
	local n=60
	local start=$(date +%s)
	# n creates avoiding O_EXCL / O_TRUNC. One process runs them all (so process start-up time does not mix into the measurement)
	timeout 180 python3 -c "
import os, sys
d, n = sys.argv[1], int(sys.argv[2])
for i in range(n):
    fd = os.open(os.path.join(d, 'bp%03d.dat' % i), os.O_CREAT | os.O_WRONLY, 0o644)
    os.write(fd, b'x' * 128)
    os.close(fd)
" "$dir" "$n"
	local rc=$?
	local elapsed=$(( $(date +%s) - start ))
	if [ "$rc" != "0" ]; then
		unmount_clean
		fail "$n creates do not run to completion under back-pressure with a cap of 4 (rc=$rc / ${elapsed}s / 124 = timeout)"
		return
	fi
	local listed=$(ls "$dir" | wc -l)
	local subtree="select count(*) from ${SCHEMA}.${PREFIX}inode where parent_id in (select id from ${SCHEMA}.${PREFIX}inode where name = 'bp')"
	# Most of them should already be in the database before the unmount. There is no background flush and no
	# fsync, so a 0 here means "it does not flush even above the cap" = back-pressure is not working.
	local rows_running=$(q "$subtree")
	# A clean unmount writes out the remaining pending entries
	unmount_clean
	local rows=$(q "$subtree")
	if [ "$listed" != "$n" ]; then
		fail "the ls count does not add up ($listed / $n)"
		return
	fi
	if [ "$rows_running" = "0" ]; then
		fail "not one entry landed in the database even far above the cap of 4 = back-pressure's flush is not working"
		return
	fi
	if [ "$rows" != "$n" ]; then
		fail "creates were dropped on the back-pressure path (database $rows / $n)"
		return
	fi
	pass
}

# ----- stage 2: error floor 3, the error state -----

test_meta_error_state_blocks_and_clears() {
	# What it can detect, in a situation where the flush fails permanently (as-built manual scenario 6's
	# "create a same-named row of a different kind with psql" = an infinite retry):
	#   - fsync returns -EIO (it does not silently succeed)
	#   - 5 consecutive failures put it into **the error state** and new creates / writes are blocked with -EIO
	#   - `pgfsctl status` shows a red `!! write-back ERROR STATE`
	#   - removing the collision and one successful fsync **releases it automatically** and writing resumes
	# Those four. Both an implementation that swallows it silently and one that never releases FAIL.
	# The fault injection is always rolled back by the EXIT trap (cleanup_injection).
	if ! db_ready; then
		skip "a colliding row cannot be created with psql (the fault injection needs the database)"
		return
	fi
	if [ -z "$CONN" ]; then
		skip "the connection cannot be read from the settings file (it is needed to check status)"
		return
	fi
	mount_meta --write-back-interval-ms 0 --write-back-flush-timeout-ms 2000 || { fail "mount"; return; }
	local dir="$TEST_ROOT/errst"
	rm -rf "$dir" 2>/dev/null
	mkdir -p "$dir" || { fail "mkdir"; return; }
	fsyncdir "$dir" || { fail "fsyncdir"; return; }
	local pid=$(q "select id from ${SCHEMA}.${PREFIX}inode where name = 'errst'")
	if [ -z "$pid" ]; then
		unmount_clean
		fail "the id of the fsyncdir'd parent directory cannot be looked up in the database"
		return
	fi
	# Create one pending file (a create that uses neither O_EXCL nor O_TRUNC)
	plain_create "$dir/conflict.txt" hello || { unmount_clean; fail "create conflict.txt"; return; }
	# * The fault injection: insert a **directory** row with the same name directly (a different kind = the collision cannot be resolved).
	# The cleanup deletes by the created_by marker rather than by id (idempotent, and it does not depend on parsing psql's output).
	# Note: firing `insert ... returning` with -At also brings the "INSERT 0 1" status line to stdout,
	#     so when the value is needed it is wrapped in a CTE around a SELECT.
	INJECT_CLEANUP_SQL="delete from ${SCHEMA}.${PREFIX}inode where parent_id = $pid and created_by = 'wbmeta-test'"
	local iid=$(q "with ins as (insert into ${SCHEMA}.${PREFIX}inode (parent_id, name, uname, gname, st_mode, created_by, updated_by) values ($pid, 'conflict.txt', 'wbmeta', 'wbmeta', 16877, 'wbmeta-test', 'wbmeta-test') returning id) select id from ins")
	if [ -z "$iid" ]; then
		unmount_clean
		fail "the colliding row could not be INSERTed (the injection failed)"
		return
	fi
	# 5 fsyncs -> all -EIO, and 5 consecutive failures are expected to enter the error state
	local fsync_fails=$(python3 -c "
import os, sys
p = sys.argv[1]
fails = 0
for i in range(5):
    fd = -1
    try:
        fd = os.open(p, os.O_WRONLY)
        os.write(fd, b'y')
        os.fsync(fd)
    except OSError:
        fails += 1
    try:
        if fd >= 0:
            os.close(fd)
    except OSError:
        pass
print(fails)
" "$dir/conflict.txt")
	# During the error state a new create (InsertInode) / write is blocked
	local blocked=$(python3 -c "
import errno, os, sys
try:
    fd = os.open(sys.argv[1], os.O_CREAT | os.O_WRONLY, 0o644)
    os.write(fd, b'z')
    os.close(fd)
except OSError as e:
    print('EIO' if e.errno == errno.EIO else 'errno%d' % e.errno)
    sys.exit(0)
print('ok')
" "$dir/after_error.txt")
	ping_mount
	local status_out=$("$BIN/pgfsctl" status -c "$CONN" -s "$SCHEMA" 2>/dev/null)
	local status_red=no
	echo "$status_out" | grep -q "write-back ERROR STATE" && status_red=yes
	# Remove the collision -> one successful fsync should release it automatically.
	# During the error state a write is -EIO as well, so **only fsync is fired, with no write in between**
	# (a write first would make "it cannot be released" and "the write was rejected" indistinguishable).
	local cleanup_rc=0
	q "$INJECT_CLEANUP_SQL" >/dev/null || cleanup_rc=1
	local left=$(q "select count(*) from ${SCHEMA}.${PREFIX}inode where parent_id = $pid and created_by = 'wbmeta-test'")
	INJECT_CLEANUP_SQL=""
	local recovered=$(python3 -c "
import os, sys
try:
    fd = os.open(sys.argv[1], os.O_WRONLY)
except OSError as e:
    print('open-failed:%d' % e.errno)
    sys.exit(0)
try:
    os.fsync(fd)
except OSError as e:
    print('fsync-failed:%d' % e.errno)
    sys.exit(0)
finally:
    try:
        os.close(fd)
    except OSError:
        pass
print('ok')
" "$dir/conflict.txt")
	local write_back=$(python3 -c "
import os, sys
try:
    fd = os.open(sys.argv[1], os.O_CREAT | os.O_WRONLY, 0o644)
    os.write(fd, b'q')
    os.close(fd)
except OSError as e:
    print('failed:%d' % e.errno)
    sys.exit(0)
print('ok')
" "$dir/after_clear.txt")
	unmount_clean
	if [ "$fsync_fails" != "5" ]; then
		fail "fsync does not fail under a different-kind collision ($fsync_fails / 5 failures) = suspect the flush error is being swallowed"
		return
	fi
	if [ "$blocked" != "EIO" ]; then
		fail "a new create is not blocked during the error state ($blocked / expected EIO)"
		return
	fi
	if [ "$status_red" != "yes" ]; then
		fail "the error state does not appear in pgfsctl status (there is no write-back ERROR STATE line)"
		return
	fi
	if [ "$cleanup_rc" != "0" ] || [ "$left" != "0" ]; then
		fail "rolling the injection back failed ($left row(s) left) = everything after this is meaningless, so it stops"
		return
	fi
	if [ "$recovered" != "ok" ]; then
		fail "fsync still fails after the collision was removed ($recovered) = the error state is not released automatically"
		return
	fi
	if [ "$write_back" != "ok" ]; then
		fail "a new create is still blocked after the release ($write_back)"
		return
	fi
	pass
}

# ===== run =====

if [ ! -f "$SETTING_FILE" ]; then
	echo "${RED}the settings file is missing${NC}: $SETTING_FILE" >&2
	exit 2
fi
if [ ! -x "$BIN/mount.pgfs" ]; then
	echo "${RED}mount.pgfs is missing${NC}: $BIN/mount.pgfs" >&2
	exit 2
fi
if ! command -v fusermount3 >/dev/null 2>&1; then
	echo "${RED}fusermount3 is missing${NC} (the fuse3 package is required)" >&2
	exit 2
fi
if ! command -v python3 >/dev/null 2>&1; then
	echo "${RED}python3 is missing${NC} (it is needed to verify O_EXCL / ftruncate)" >&2
	exit 2
fi

echo "${BLUE}=== pgfs metadata write-back tests ===${NC}"
echo "setting : $SETTING_FILE"
echo "mount   : $MOUNT_ROOT"
echo "schema  : $SCHEMA (prefix $PREFIX)"
echo ""

trap 'cleanup_injection' EXIT INT TERM

unmount_clean

run test_pending_same_name_eexist
run test_pending_truncate_zero_consistency
run test_pending_pin_does_not_blow_cache
run test_pending_mkdir_visible_in_ls
run test_meta_fsync_survives_crash
run test_meta_close_only_is_lost_on_crash
run test_meta_fsyncdir_persists_pending_children
run test_meta_rename_over_existing_pending_source
run test_meta_rename_over_existing_excl_source
run test_meta_otrunc_close_is_synchronous
run test_meta_truncate_syscall_close_is_synchronous
run test_meta_exclusive_create_is_write_through
run test_meta_read_after_close_no_flush
run test_meta_hardlink_sibling_survives_unlink
run test_meta_exclusive_create_defer_is_pending
run test_meta_exclusive_create_defer_same_mount_eexist
run test_meta_exclusive_create_defer_conflict_latches
run test_meta_loss_is_recorded_in_db
run test_meta_cancel_audit_is_written_immediately
run test_meta_audit_live_off_does_not_fake_loss
run test_meta_error_state_is_published_immediately
run test_meta_loss_report_has_paths_and_breakdown
run test_meta_error_state_blocks_destroy_but_allows_cancel
run test_meta_live_flip_publishes_effective_mode
run test_meta_live_off_flushes_pending_then_write_through
run test_meta_backpressure_blocking_inodes
run test_meta_error_state_blocks_and_clears

# Cleanup: remove the test directory and bring the mount down
if ! mountpoint -q "$MOUNT_ROOT" 2>/dev/null; then
	mount_meta >/dev/null 2>&1
fi
if mountpoint -q "$MOUNT_ROOT" 2>/dev/null; then
	rm -rf "$TEST_ROOT" 2>/dev/null
fi
unmount_clean

echo ""
echo "${BLUE}===========================================${NC}"
echo "Results: ${GREEN}$PASSED passed${NC}, ${RED}$FAILED failed${NC}, ${YELLOW}$SKIPPED skipped${NC} (out of $TOTAL)"
if [ $FAILED -gt 0 ]; then
	echo "${RED}Failed tests:${NC}"
	for name in "${FAILED_NAMES[@]}"; do
		echo "  - $name"
	done
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
