#!/usr/bin/env bash
# Tests dedicated to the pgfs write-back cache
#
# Unlike tests/linux/e2e.sh, **this script mounts and remounts on its own**.
# The contract of write-back is "the write-back may be deferred until fsync / close / a clean unmount", so
# it cannot be verified without killing the mount or re-establishing it.
#
# Usage:
#   bash tests/linux/writeback.sh
#
# Environment variables:
#   PGFS_SETTING_FILE   the settings file handed to mount.pgfs (default $HOME/pgfs_test.toml)
#   MOUNT_ROOT          the mount target (default $HOME/mnt/pgfs)
#   PGFS_BIN            the directory holding the binaries (default ./bin/Debug)
#   TEST_FILTER         run only the tests whose name contains this string
#
# Prerequisites: the FS the settings file points at has been mkfs'd and may be destroyed
#       (the tests only touch what is under MOUNT_ROOT/wbtest).
#
# Exit codes: 0 = everything passed / 1 = something failed / 2 = a prerequisite is missing

set -u

SETTING_FILE="${PGFS_SETTING_FILE:-$HOME/pgfs_test.toml}"
MOUNT_ROOT="${MOUNT_ROOT:-$HOME/mnt/pgfs}"
BIN="${PGFS_BIN:-./bin/Debug}"
TEST_ROOT="$MOUNT_ROOT/wbtest"
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

# ===== looking at the database directly (used only by some tests; skipped when it cannot be found) =====
#
# Hammering `pgfsctl status` adds a process start-up's worth of latency per call and misses transitions that
# only last a few seconds. The test that watches the first phase of the two-phase flip polls with psql directly.

PSQL="${PGFS_PSQL:-${PSQL:-psql}}"
CONN=$(grep -E '^connection' "$SETTING_FILE" 2>/dev/null | head -1 | sed 's/^connection *= *//; s/^"//; s/"$//')
SCHEMA=$(grep -E '^schema' "$SETTING_FILE" 2>/dev/null | head -1 | sed 's/^schema *= *//; s/^"//; s/"$//')
PREFIX=$(grep -E '^prefix' "$SETTING_FILE" 2>/dev/null | head -1 | sed 's/^prefix *= *//; s/^"//; s/"$//')
[ -n "$SCHEMA" ] || SCHEMA="public"
[ -n "$PREFIX" ] || PREFIX="pgfs_"
kv() { echo "$CONN" | tr ';' '\n' | grep -iE "^$1=" | head -1 | cut -d= -f2-; }
DB_HOST=$(kv Host); DB_PORT=$(kv Port); DB_NAME=$(kv Database); DB_USER=$(kv Username)
[ -n "$DB_HOST" ] || DB_HOST=localhost
[ -n "$DB_PORT" ] || DB_PORT=5432

q() { "$PSQL" -U "$DB_USER" -h "$DB_HOST" -p "$DB_PORT" -d "$DB_NAME" -At -c "$1" 2>/dev/null; }
db_ready() { [ -n "$DB_NAME" ] && [ -n "$DB_USER" ] && command -v "$PSQL" >/dev/null 2>&1 && [ "$(q 'select 1')" = "1" ]; }

# ===== mount operations =====

mount_pid() {
	# Look it up by process name (`pgrep -f` also matches the calling shell, which contains the same command
	# line. Measured: it matched 3 times and head -1 returned the shell's pid).
	# This decides what gets kill -9'd, so getting it wrong takes an unrelated process down.
	pgrep -x mount.pgfs | head -1
}

# Bring the mount down (a clean unmount). With no process around, it only cleans up.
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
unmount_crash() {
	local pid=$(mount_pid)
	if [ -n "$pid" ]; then
		kill -9 "$pid" 2>/dev/null
	fi
	sleep 0.5
	# The kernel-side mount is left behind as "Transport endpoint is not connected", so it is cleaned up
	fusermount3 -u "$MOUNT_ROOT" 2>/dev/null
	return 0
}

# Mount by passing the arguments straight through to mount.pgfs.
mount_with() {
	mkdir -p "$MOUNT_ROOT" 2>/dev/null
	"$BIN/mount.pgfs" --setting-file "$SETTING_FILE" "$@" >/dev/null 2>&1
	local i=0
	while [ $i -lt 100 ]; do
		if mountpoint -q "$MOUNT_ROOT" 2>/dev/null; then
			return 0
		fi
		sleep 0.1
		i=$((i+1))
	done
	echo "${RED}could not mount${NC}: $BIN/mount.pgfs --setting-file $SETTING_FILE $*" >&2
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

test_fsync_survives_crash() {
	# A write whose fsync(2) returned must survive even if the process dies immediately afterwards.
	# The core of write-back's durability contract (dropping the FSync override makes this fail).
	mount_with --write-back || { fail "mount"; return; }
	mkdir -p "$TEST_ROOT" || { fail "mkdir"; return; }
	local f="$TEST_ROOT/wb_fsync.bin"
	rm -f "$f"
	# dd conv=fsync = issue fsync(2) after writing and then close
	dd if=/dev/urandom of="$f" bs=128k count=16 conv=fsync status=none || { fail "dd"; return; }
	local sum_before=$(md5sum < "$f" | cut -d' ' -f1)
	local size_before=$(stat -c %s "$f")
	unmount_crash
	mount_with --write-back || { fail "remount"; return; }
	if [ ! -f "$f" ]; then
		fail "the fsync'd file disappeared"
		return
	fi
	local size_after=$(stat -c %s "$f")
	local sum_after=$(md5sum < "$f" | cut -d' ' -f1)
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

test_close_survives_crash() {
	# It is flushed at close(2) as well (FUSE's Flush callback). A plain `cp` or a redirection, which do not
	# call fsync explicitly, take this path.
	mount_with --write-back || { fail "mount"; return; }
	mkdir -p "$TEST_ROOT" || { fail "mkdir"; return; }
	local f="$TEST_ROOT/wb_close.bin"
	rm -f "$f"
	head -c 1048576 /dev/urandom > "$f.src"
	cp "$f.src" "$f" || { fail "cp"; return; }
	local sum_before=$(md5sum < "$f.src" | cut -d' ' -f1)
	unmount_crash
	mount_with --write-back || { fail "remount"; return; }
	local sum_after=$(md5sum < "$f" 2>/dev/null | cut -d' ' -f1)
	if [ "$sum_before" != "$sum_after" ]; then
		fail "the contents were lost by a crash after the close (md5 $sum_before -> ${sum_after:-none})"
		return
	fi
	pass
}

test_graceful_unmount_flushes() {
	# A clean unmount (Api.Dispose) writes out whatever is unflushed.
	# A situation where umount happens while the file is still open cannot be created here, so
	# "closed but waiting on a background flush" is verified without using the shortest interval
	# (= the interval is made long to disable the time trigger, leaving only the flush at unmount).
	mount_with --write-back --write-back-interval-ms 0 || { fail "mount"; return; }
	mkdir -p "$TEST_ROOT" || { fail "mkdir"; return; }
	local f="$TEST_ROOT/wb_graceful.bin"
	rm -f "$f"
	head -c 524288 /dev/urandom > "$f.src"
	cp "$f.src" "$f" || { fail "cp"; return; }
	local sum_before=$(md5sum < "$f.src" | cut -d' ' -f1)
	unmount_clean
	mount_with --write-back || { fail "remount"; return; }
	local sum_after=$(md5sum < "$f" 2>/dev/null | cut -d' ' -f1)
	if [ "$sum_before" != "$sum_after" ]; then
		fail "the contents were lost across a clean unmount (md5 $sum_before -> ${sum_after:-none})"
		return
	fi
	pass
}

test_backpressure_small_limit() {
	# Set the dirty cap below one chunk so that the path where the writing side waits on a flush fires constantly.
	# It must not hang and the contents must match.
	mount_with --write-back --write-back-max-bytes 65536 || { fail "mount"; return; }
	mkdir -p "$TEST_ROOT" || { fail "mkdir"; return; }
	local f="$TEST_ROOT/wb_bp.bin"
	rm -f "$f"
	head -c 4194304 /dev/urandom > "$f.src"
	if ! timeout 300 cp "$f.src" "$f"; then
		fail "back-pressure hung (it does not finish within 300 seconds)"
		return
	fi
	sync
	if ! cmp -s "$f.src" "$f"; then
		fail "the contents were corrupted on the back-pressure path"
		return
	fi
	pass
}

test_dirty_visible_without_read_cache() {
	# Even with the read cache disabled (cache_data_max_bytes=0), one's own unflushed writes must be readable.
	# If the read path falls back to "with the cache disabled, substring from the database" it misses the dirty data.
	mount_with --write-back --cache-data-max-bytes 0 || { fail "mount"; return; }
	mkdir -p "$TEST_ROOT" || { fail "mkdir"; return; }
	local f="$TEST_ROOT/wb_nocache.bin"
	rm -f "$f"
	head -c 262144 /dev/urandom > "$f.src"
	cp "$f.src" "$f" || { fail "cp"; return; }
	# Read without syncing
	if ! cmp -s "$f.src" "$f"; then
		fail "the unflushed contents cannot be read with the read cache disabled"
		return
	fi
	sync
	if ! cmp -s "$f.src" "$f"; then
		fail "the contents after the flush do not match"
		return
	fi
	pass
}

test_partial_overwrite_after_remount() {
	# A partial overwrite of a chunk that only exists in the database (= the seed path). The mount is
	# re-established to empty the cache, then the middle is rewritten and the surroundings must survive.
	mount_with --write-back || { fail "mount"; return; }
	mkdir -p "$TEST_ROOT" || { fail "mkdir"; return; }
	local f="$TEST_ROOT/wb_seed.bin"
	rm -f "$f"
	tr '\0' 'A' < /dev/zero | head -c 1048576 > "$f" || { fail "the initial write"; return; }
	sync
	unmount_clean
	mount_with --write-back || { fail "remount"; return; }
	# With the cache empty, turn the middle 4KiB into 'B'
	tr '\0' 'B' < /dev/zero | head -c 4096 | dd of="$f" bs=4096 seek=64 conv=notrunc status=none || { fail "the partial overwrite"; return; }
	sync
	local size=$(stat -c %s "$f")
	if [ "$size" != "1048576" ]; then
		fail "the size changed: $size (should be 1048576)"
		return
	fi
	local b=$(dd if="$f" bs=4096 skip=64 count=1 status=none | tr -d 'B' | wc -c)
	local a=$(dd if="$f" bs=4096 skip=63 count=1 status=none | tr -d 'A' | wc -c)
	local c=$(dd if="$f" bs=4096 skip=65 count=1 status=none | tr -d 'A' | wc -c)
	if [ "$b" != "0" ]; then
		fail "the overwritten 4KiB is not 'B' ($b left over)"
		return
	fi
	if [ "$a" != "0" ] || [ "$c" != "0" ]; then
		fail "the surroundings got corrupted ($a before / $c after byte(s) are not 'A' = the seed path is not working)"
		return
	fi
	pass
}

test_full_chunk_overwrite_du_after_remount() {
	# The occupied bytes (du) must not be double-counted when **a chunk that is not in the cache is
	# overwritten across its whole range**. Write-back optimizes "do not read the payload from the database
	# when the whole range is being overwritten", and forgetting to fetch 'the payload length in the
	# database' then adds it into total_size wholesale and doubles du.
	# **This path cannot be reached without re-establishing the mount to empty the cache**, which is why it
	# lives here rather than in e2e (while it is in the cache, the clean->dirty promotion carries that length over and it never surfaces).
	mount_with --write-back || { fail "mount"; return; }
	mkdir -p "$TEST_ROOT" || { fail "mkdir"; return; }
	local f="$TEST_ROOT/wb_overwrite.bin"
	rm -f "$f" "$f.new"
	head -c 2097152 /dev/urandom > "$f" || { fail "the initial write"; return; }
	sync
	local first=$(du -B1 "$f" | cut -f1)
	if [ "$first" -lt 2097152 ]; then
		fail "the initial du is $first (should be at least 2MiB)"
		return
	fi
	unmount_clean
	mount_with --write-back || { fail "remount"; return; }
	# With the cache empty, overwrite the whole thing as 1MiB x 2 aligned to the chunk boundaries (no truncate)
	head -c 2097152 /dev/urandom > "$f.new"
	dd if="$f.new" of="$f" bs=1M count=2 conv=notrunc,fsync status=none || { fail "the whole-range overwrite"; return; }
	sync
	local size=$(stat -c %s "$f")
	if [ "$size" != "2097152" ]; then
		fail "the size changed: $size (should be 2097152)"
		return
	fi
	if ! cmp -s "$f.new" "$f"; then
		fail "the contents after the overwrite do not match"
		return
	fi
	local second=$(du -B1 "$f" | cut -f1)
	# Allowing for the block-rounding error (8KiB), it must not have grown
	if [ "$second" -gt $((first + 8192)) ]; then
		fail "the whole-range overwrite grew du from $first to $second (the occupied bytes are double-counted)"
		return
	fi
	pass
}

test_status_reports_write_back() {
	# `pgfsctl status` (Layer 3) must report the write-back state.
	mount_with --write-back || { fail "mount"; return; }
	local conn=$(grep -E '^connection' "$SETTING_FILE" | head -1 | sed 's/^connection *= *//; s/^"//; s/"$//')
	local schema=$(grep -E '^schema' "$SETTING_FILE" | head -1 | sed 's/^schema *= *//; s/^"//; s/"$//')
	if [ -z "$conn" ]; then
		skip "cannot read connection from the settings file"
		return
	fi
	local out=$("$BIN/pgfsctl" status -c "$conn" -s "${schema:-public}" 2>/dev/null)
	if ! echo "$out" | grep -q "mount.write_back = true"; then
		fail "mount.write_back = true does not appear in the effective config of status"
		return
	fi
	if ! echo "$out" | grep -qE "write-back *: on"; then
		fail "the write-back statistics line does not appear in status"
		return
	fi
	pass
}

test_live_off_flushes_and_publishes_effective_mode() {
	# Disabling mount.write_back live must be **two-phase** ((1) close intake -> (2) FlushAll -> (3) switch mode).
	#   - the first phase's "intake closed = effectively off" is **written immediately** into {prefix}mounts
	#     (a design that waits for the heartbeat period would report "on" as a lie for the whole flip)
	#   - nothing is left unflushed once the flip completes (= the second phase wrote everything out)
	#   - a write after the flip becomes write-through (it survives a kill -9)
	#
	# **metadata write-back is used alongside in order to make the flip deliberately slow.** With data
	# write-back alone, close (every time a duplicated fd closes) is a flush trigger, so from a shell the flip
	# cannot be fired while still holding dirty data (measured: `printf ... >&9` runs a flush every single
	# time and 8192 iterations took 10 minutes).
	# With metadata write-back it is close-no-flush, so pending inodes can be piled up at will.
	if ! db_ready; then
		skip "cannot check the stats with psql"
		return
	fi
	if [ -z "$CONN" ]; then
		skip "cannot read connection from the settings file (pgfsctl is needed for the live flip)"
		return
	fi
	# * Bring the previous test's mount down first. `mount_with` **returns success immediately when something
	#   is already mounted**, so without bringing it down this would run with the previous test's options
	#   (here, without write_back_metadata) (measured: it passed on its own yet failed only in a full run).
	unmount_clean
	# * Then empty {prefix}mounts. This suite uses `unmount_crash` (kill -9) routinely, so **rows of dead
	#   mounts are left behind** and sit there with a fresh heartbeat. Counting without narrowing the rows
	#   hits an `enabled=false` row left by an earlier test and **moves on without waiting for the flip to complete**
	#   (measured: it kill -9'ed in the middle of the second phase and falsely reported "it was not written out").
	#   This suite never runs more than one mount, so clearing the rows before mounting is fine.
	q "delete from ${SCHEMA}.${PREFIX}mounts" >/dev/null
	# The flush deadline is 30 seconds by default. 800 entries measure at about 12 seconds, but a congested
	# database can exceed it, so it is stretched (what this test wants to see is the two-phase flip, not the deadline behaviour).
	mount_with --write-back --write-back-metadata --write-back-interval-ms 0 --write-back-flush-timeout-ms 120000 || { fail "mount"; return; }
	rm -rf "$TEST_ROOT/liveoff" 2>/dev/null
	mkdir -p "$TEST_ROOT/liveoff" || { fail "mkdir"; return; }
	local i=0
	while [ $i -lt 800 ]; do
		echo "pending$i" > "$TEST_ROOT/liveoff/f$i"
		i=$((i+1))
	done
	# {prefix}mounts can contain rows left by other tests, so it counts **whether at least one row matches the
	# condition** (a scalar select would return several rows and the comparison would never hold).
	local live_where="heartbeat_at > (now() AT TIME ZONE 'UTC') - interval '2 minutes'"
	"$BIN/pgfsctl" config set mount.write_back false -c "$CONN" -s "$SCHEMA" >/dev/null 2>&1
	local seen=no
	i=0
	while [ $i -lt 200 ]; do
		if [ "$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts where $live_where and stats->'writeBack'->>'intakeClosed' = 'true'")" != "0" ]; then
			seen=yes
			break
		fi
		sleep 0.1
		i=$((i+1))
	done
	# Wait for the third phase (the mode switch) to complete
	i=0
	while [ $i -lt 600 ]; do
		[ "$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts where $live_where and stats->'writeBack'->>'enabled' = 'false'")" != "0" ] && break
		sleep 0.1
		i=$((i+1))
	done
	if [ "$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts where $live_where and stats->'writeBack'->>'enabled' = 'false'")" = "0" ]; then
		fail "the live off was not applied"
		return
	fi
	# What could not be written out within the second phase's deadline continues in the background as a drain (by design). **The end of the drain
	# is written immediately**, so wait until it clears. Previously there was no wait, and against a PG over the network the drain still had a few
	# hundred ms left, so it always failed (and the end of the drain was not written until the heartbeat). If it still remains after waiting, it really did not write everything out.
	i=0
	while [ $i -lt 600 ]; do
		[ "$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts where $live_where and stats->'writeBack'->>'drainPending' = 'true'")" = "0" ] && break
		sleep 0.1
		i=$((i+1))
	done
	if [ "$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts where $live_where and stats->'writeBack'->>'drainPending' = 'true'")" != "0" ]; then
		fail "something is still unflushed 60 seconds after the flip (the second phase's FlushAll and the drain did not write everything out)"
		return
	fi
	if [ "$seen" != "yes" ]; then
		fail "the first phase's \"intake closed\" does not appear in {prefix}mounts (suspect it has gone back to a one-phase flip)"
		return
	fi
	# A write after the flip is write-through = it survives a kill -9.
	echo "through" > "$TEST_ROOT/liveoff/after"
	unmount_crash
	mount_with --write-back || { fail "remount"; return; }
	local after=$(cat "$TEST_ROOT/liveoff/after" 2>/dev/null)
	if [ "$after" != "through" ]; then
		fail "a write after the live off is not write-through (lost to kill -9: '${after:-none}')"
		return
	fi
	# What the second phase wrote out (the pending work from before the flip) must survive too.
	local survived=$(cat "$TEST_ROOT/liveoff/f799" 2>/dev/null)
	if [ "$survived" != "pending799" ]; then
		fail "the unflushed work from before the flip was not written out (f799 = '${survived:-none}')"
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

echo "${BLUE}=== pgfs write-back tests ===${NC}"
echo "setting : $SETTING_FILE"
echo "mount   : $MOUNT_ROOT"
echo ""

# If something is already mounted, bring it down first and start from a known state
unmount_clean

run test_fsync_survives_crash
run test_close_survives_crash
run test_graceful_unmount_flushes
run test_backpressure_small_limit
run test_dirty_visible_without_read_cache
run test_partial_overwrite_after_remount
run test_full_chunk_overwrite_du_after_remount
run test_status_reports_write_back
run test_live_off_flushes_and_publishes_effective_mode

# Cleanup: remove the test directory and bring the mount down
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
