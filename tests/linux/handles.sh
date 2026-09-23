#!/usr/bin/env bash
# The leak regression test of the pgfs handle table (handle-context ⑥)
#
# It reads `handles : N open / peak M` from Layer 3 of `pgfsctl status` and checks that
# **every handle that was opened comes back**.
#
# The contracts checked here:
#   - a mount that is doing nothing has 0 open
#   - a file open/close and a directory readdir **balance out** (go back to 0)
#   - **duplicating an fd and closing only one of them leaves the other one alive** (the most important one)
#     `Flush` is called once per duplicated fd while `Release` is only called on the last close, so
#     **the place a handle is returned has to be Release** (docs/design/handle-context.md, stage A).
#     Moving the Return into Flush makes this test fail.
#   - after a round of assorted operations it goes back to 0 open
#
# **Only Linux can verify the values.** Dokan does not go through the handle table, so a Windows mount
# always shows `0 open / peak 0` (docs/Pgfsctl.md).
#
# Usage:
#   PGFS_PSQL=/usr/local/pgsql/bin/psql bash tests/linux/handles.sh
#
# Environment variables:
#   PGFS_SETTING_FILE   the settings file handed to mount.pgfs (default $HOME/pgfs_test.toml)
#   MOUNT_ROOT          the mount target (default $HOME/mnt/pgfs)
#   PGFS_BIN            the directory holding the binaries (default ./bin/Debug)
#   PGFS_PSQL / PSQL    the psql command (**required** - it is used to make a snapshot be written)
#   TEST_FILTER         run only the tests whose name contains this string
#
# Exit codes: 0 = everything passed / 1 = something failed / 2 = a prerequisite is missing

set -u

SETTING_FILE="${PGFS_SETTING_FILE:-$HOME/pgfs_test.toml}"
MNT="${MOUNT_ROOT:-$HOME/mnt/pgfs}"
BIN="${PGFS_BIN:-./bin/Debug}"
PSQL_BIN="${PGFS_PSQL:-${PSQL:-psql}}"
FILTER="${TEST_FILTER:-}"
T="$MNT/htest"

# The upper bound (in seconds) for a snapshot to be written. **Asserting without waiting misjudges "slow"
# as "leaking"** (a close arrives asynchronously).
WAIT_MAX="${PGFS_H_WAIT:-10}"

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

# ===== mount operations =====

# Look it up by process name (`pgrep -f` also matches the calling shell, which contains the same command line).
umount_all() {
	if mountpoint -q "$MNT" 2>/dev/null; then fusermount3 -u "$MNT" 2>/dev/null; fi
	local i=0
	while [ $i -lt 600 ]; do
		pgrep -x mount.pgfs >/dev/null 2>&1 || return 0
		sleep 0.1; i=$((i+1))
	done
	return 1
}

MOUNT_PID=""
mount_fs() {
	mkdir -p "$MNT" 2>/dev/null
	"$BIN/mount.pgfs" --setting-file "$SETTING_FILE" --mount-point "$MNT" >/dev/null 2>&1
	local i=0
	while [ $i -lt 100 ]; do
		if mountpoint -q "$MNT" 2>/dev/null; then
			# **The pid is taken from the registry as "the newest row for this mount point".**
			# With `pgrep -x mount.pgfs | head -1` it would **grab a daemon running on a different
			# mount point** (crossclient's B side, or one left behind), and every status read after
			# that would be looking at someone else's row entirely (this was actually hit and turned 6 tests into mysterious FAILs).
			MOUNT_PID=$(q "select pid from ${SCHEMA}.${PREFIX}mounts
			               where mountpoint = '$MNT' order by started_at desc limit 1")
			[ -n "$MOUNT_PID" ] && return 0
		fi
		sleep 0.1; i=$((i+1))
	done
	return 1
}

# ===== reading the handle count =====
#
# Layer 3 is **the snapshot written at heartbeat time**, so reading it straight away gives a stale value.
# A control-NOTIFY ping is fired to **make it be written now** before reading (rather than waiting a heartbeat period).
# The LISTEN of the control channel is always established regardless of `database.notify_enabled`, so
# **it works with the default of notify off**.
ping_mount() { q "select pg_notify('${SCHEMA}_${PREFIX}notify', '{\"s\":\"htest000\",\"c\":\"ping\"}')" >/dev/null; }

# **The number of open bodies** (stage C-1). Opening the same file three times still counts as 1.
# **It does not appear in the text output of `pgfsctl status`**, so it is read from the JSON in `{prefix}mounts.stats`.
read_open_inodes() {
	q "select stats->'handles'->>'inodes' from ${SCHEMA}.${PREFIX}mounts
	   where pid = ${MOUNT_PID:-0} order by heartbeat_at desc limit 1"
}

# Wait until it reaches the expected value (a close arrives asynchronously).
wait_inodes() {
	local want="$1"
	local deadline=$(( $(date +%s) + WAIT_MAX ))
	local got=""
	while [ "$(date +%s)" -lt "$deadline" ]; do
		ping_mount
		sleep 0.3
		got=$(read_open_inodes)
		[ "$got" = "$want" ] && return 0
	done
	echo "$got"
	return 1
}

# $1 = open|peak
read_handles() {
	local which="$1"
	local line
	line=$("$BIN/pgfsctl" status -c "$CONN" -s "$SCHEMA" 2>/dev/null | grep -A 8 "pid $MOUNT_PID " | grep -E '^ *handles *:' | head -1)
	[ -n "$line" ] || { echo ""; return; }
	if [ "$which" = "open" ]; then
		echo "$line" | sed -E 's/.*: *([0-9]+) open.*/\1/'
		return
	fi
	echo "$line" | sed -E 's/.*peak *([0-9]+).*/\1/'
}

# Wait until it reaches the expected value. **A close arrives asynchronously**, so reading once and failing would be a false FAIL.
wait_handles_open() {
	local want="$1"
	local deadline=$(( $(date +%s) + WAIT_MAX ))
	local got=""
	while [ "$(date +%s)" -lt "$deadline" ]; do
		ping_mount
		sleep 0.3
		got=$(read_handles open)
		[ "$got" = "$want" ] && return 0
	done
	echo "$got"
	return 1
}

run() {
	local name="$1"
	if [ -n "$FILTER" ] && [[ "$name" != *"$FILTER"* ]]; then return 0; fi
	TOTAL=$((TOTAL+1)); CURRENT="$name"; "$name"
}

# ===== tests =====

test_handles_idle_is_zero() {
	# A mount that is doing nothing has 0 open. Without a 0 here none of the deltas below can be read.
	local got
	if ! got=$(wait_handles_open 0); then
		fail "open is not 0 although it is idle (got '$got')"
		return
	fi
	pass
}

test_handles_file_open_close_balances() {
	# Opening three files gives 3 open, and closing them goes back to 0.
	printf 'a' > "$T/f1"; printf 'b' > "$T/f2"; printf 'c' > "$T/f3"
	wait_handles_open 0 >/dev/null || true
	exec 9< "$T/f1"; exec 8< "$T/f2"; exec 7< "$T/f3"
	local got
	if ! got=$(wait_handles_open 3); then
		fail "open is not 3 although three fds are open (got '$got')"
		exec 9<&-; exec 8<&-; exec 7<&-
		return
	fi
	exec 9<&-; exec 8<&-; exec 7<&-
	if ! got=$(wait_handles_open 0); then
		fail "open did not go back to 0 although all three were closed (got '$got') = a handle is leaking"
		return
	fi
	pass
}

test_handles_dup_fd_survives_first_close() {
	# * The most important one. **Duplicating an fd and closing one of them leaves the other one alive.**
	#   `Flush` is called on every close (= on every duplicate) while `Release` is only called on the last
	#   close. **If the handle is returned in Flush rather than Release, the first close removes it from
	#   the table and a read through the remaining fd falls back to the path**
	#   (= the window stage B closed opens again).
	printf 'DUPDATA' > "$T/dup.txt"
	wait_handles_open 0 >/dev/null || true
	exec 9< "$T/dup.txt"
	exec 8<&9            # duplicate (Open does not increase = there is one handle)
	local got
	if ! got=$(wait_handles_open 1); then
		fail "open should still be 1 after duplicating, but it is $got"
		exec 9<&-; exec 8<&-
		return
	fi
	exec 9<&-            # close the first one -> Flush is called but Release is not
	sleep 0.5
	got=$(read_handles open)
	if [ "$got" != "1" ]; then
		fail "open became $got just from closing the first of the duplicates (suspect it is returned in Flush rather than Release)"
		exec 8<&-
		return
	fi
	# The remaining fd must still read (this is the point - once it is gone from the table it either cannot read or falls back to the path)
	local body
	body=$(cat <&8)
	exec 8<&-
	if [ "$body" != "DUPDATA" ]; then
		fail "cannot read through the remaining duplicated fd ('$body')"
		return
	fi
	if ! got=$(wait_handles_open 0); then
		fail "open did not go back to 0 although both duplicates were closed (got '$got')"
		return
	fi
	pass
}

test_handles_readdir_balances() {
	# A directory enumeration (OpenDir / ReleaseDir) must balance out too.
	# **It borrows in OpenDir and returns in ReleaseDir**, so this repeats ls and watches that it does not keep growing.
	wait_handles_open 0 >/dev/null || true
	local i=0
	while [ $i -lt 20 ]; do ls "$T" >/dev/null 2>&1; i=$((i+1)); done
	local got
	if ! got=$(wait_handles_open 0); then
		fail "open did not go back to 0 after 20 ls calls (got '$got') = a directory handle is leaking"
		return
	fi
	pass
}

test_handles_zero_after_mixed_workload() {
	# A round of create / write / read / delete / directory operations must go back to 0.
	local d="$T/mixed"
	rm -rf "$d" 2>/dev/null; mkdir -p "$d"
	local i=0
	while [ $i -lt 10 ]; do
		printf 'payload-%s' "$i" > "$d/f$i"
		cat "$d/f$i" >/dev/null
		i=$((i+1))
	done
	ls "$d" >/dev/null
	rm -f "$d"/f1 "$d"/f2
	printf 'append' >> "$d/f0"
	rm -rf "$d"
	local got
	if ! got=$(wait_handles_open 0); then
		fail "open did not go back to 0 after a round of operations (got '$got')"
		return
	fi
	pass
}

test_handles_peak_records_concurrent_opens() {
	# peak is "the largest number ever open at the same time", so it must not go down when things are closed.
	# **It is the value that lets a leak be found after the fact** (docs/design/handle-context.md ⑥).
	local before
	before=$(read_handles peak)
	[ -n "$before" ] || { fail "cannot read peak"; return; }
	exec 9< "$T/f1"; exec 8< "$T/f2"; exec 7< "$T/f3"; exec 6< "$T/dup.txt"
	wait_handles_open 4 >/dev/null || true
	exec 9<&-; exec 8<&-; exec 7<&-; exec 6<&-
	wait_handles_open 0 >/dev/null || true
	local after
	after=$(read_handles peak)
	if [ -z "$after" ] || [ "$after" -lt 4 ]; then
		fail "peak is $after although four were open at once (should be >= 4)"
		return
	fi
	if [ "$after" -lt "$before" ]; then
		fail "peak went down ($before -> $after). peak should be monotonically increasing"
		return
	fi
	pass
}

test_handles_inodes_counted() {
	# Stage C-1: **the number of open bodies** must be counted.
	printf 'a' > "$T/i1"; printf 'b' > "$T/i2"; printf 'c' > "$T/i3"
	wait_inodes 0 >/dev/null || true
	exec 9< "$T/i1"; exec 8< "$T/i2"; exec 7< "$T/i3"
	local got
	if ! got=$(wait_inodes 3); then
		fail "inodes is not 3 although three separate files are open (got '$got')"
		exec 9<&-; exec 8<&-; exec 7<&-
		return
	fi
	exec 9<&-; exec 8<&-; exec 7<&-
	if ! got=$(wait_inodes 0); then
		fail "inodes did not go back to 0 although all three were closed (got '$got') = the reference count is leaking"
		return
	fi
	pass
}

test_handles_inodes_dedupe_same_file() {
	# * **This is where the handle count and the body count differ.** Opening the same file three times
	#   gives `open` 3 but `inodes` must be **1**.
	printf 'same' > "$T/same.txt"
	wait_inodes 0 >/dev/null || true
	exec 9< "$T/same.txt"; exec 8< "$T/same.txt"; exec 7< "$T/same.txt"
	local h i
	h=$(wait_handles_open 3 && echo 3 || read_handles open)
	if ! i=$(wait_inodes 1); then
		fail "inodes is not 1 although the same file is open three times (got '$i' / handles open=$h)"
		exec 9<&-; exec 8<&-; exec 7<&-
		return
	fi
	exec 9<&-; exec 8<&-; exec 7<&-
	if ! i=$(wait_inodes 0); then
		fail "inodes did not go back to 0 although all three were closed (got '$i')"
		return
	fi
	pass
}

test_handles_inodes_dup_fd_is_one() {
	# Duplicating an fd keeps **open at 1 and inodes at 1 too**.
	# `Release` only comes on the last close, so both of them amount to one.
	printf 'dupi' > "$T/dupi.txt"
	wait_inodes 0 >/dev/null || true
	exec 9< "$T/dupi.txt"
	exec 8<&9
	local i
	if ! i=$(wait_inodes 1); then
		fail "inodes should still be 1 after duplicating, but it is '$i'"
		exec 9<&-; exec 8<&-
		return
	fi
	exec 9<&-              # close the first one (Flush comes but Release does not)
	sleep 0.5
	ping_mount; sleep 0.3
	i=$(read_open_inodes)
	if [ "$i" != "1" ]; then
		fail "inodes became $i just from closing the first of the duplicates (suspect it is counted in Flush rather than Release)"
		exec 8<&-
		return
	fi
	exec 8<&-
	if ! i=$(wait_inodes 0); then
		fail "inodes did not go back to 0 although both duplicates were closed (got '$i')"
		return
	fi
	pass
}

test_handles_inodes_readdir_balances() {
	# A directory enumeration must be counted as a body too, and must balance out (OpenDir / ReleaseDir).
	#
	# ⚠ **"it goes back to 0" alone cannot detect a missing `OpenHandle` count** (if it is never counted it
	#   is always 0). **To watch it go up as well**, a directory fd is opened first and held
	#   (on Linux `open(2)` can open a directory O_RDONLY = `OpenDir` runs).
	wait_inodes 0 >/dev/null || true
	exec 9< "$T" || { fail "opening the directory"; return; }
	local got
	if ! got=$(wait_inodes 1); then
		fail "inodes is not 1 although a directory is open (got '$got') = OpenDir is not counting"
		exec 9<&-
		return
	fi
	exec 9<&-
	if ! got=$(wait_inodes 0); then
		fail "inodes did not go back to 0 although the directory fd was closed (got '$got')"
		return
	fi
	# It must not leak over repeated enumerations either
	local i=0
	while [ $i -lt 20 ]; do ls "$T" >/dev/null 2>&1; i=$((i+1)); done
	if ! got=$(wait_inodes 0); then
		fail "inodes did not go back to 0 after 20 ls calls (got '$got') = the directory side is missing a count"
		return
	fi
	pass
}

test_handles_status_text_shows_inodes() {
	# The body count must appear in the **text output** of `pgfsctl status` as well
	# (`handles      : N open / peak M / K inodes`).
	#
	# This is the only test that looks at it - every other body-count test reads the **JSON** in
	# `{prefix}mounts.stats`, so **a broken text rendering would go unnoticed**.
	#
	# ⚠ **An older snapshot (a row left by a mount from before C-1) has no `inodes`**.
	#   On such a row the correct thing is **not to print the item at all** (drawing the missing value as -1
	#   would print `-1 inodes`). Here only **our own row** is looked at.
	printf 'st' > "$T/st.txt"
	exec 9< "$T/st.txt"
	wait_inodes 1 >/dev/null || { fail "prerequisite: inodes does not reach 1"; exec 9<&-; return; }
	local line
	line=$("$BIN/pgfsctl" status -c "$CONN" -s "$SCHEMA" 2>/dev/null | grep -A 8 "pid $MOUNT_PID " | grep -E '^ *handles *:' | head -1)
	exec 9<&-
	if ! echo "$line" | grep -qE '[0-9]+ open / peak [0-9]+ / [0-9]+ inodes'; then
		fail "inodes does not appear in the status text (line = '$line')"
		return
	fi
	local shown
	shown=$(echo "$line" | sed -E 's|.*/ ([0-9]+) inodes.*|\1|')
	if [ "$shown" != "1" ]; then
		fail "the inodes in the text does not agree with the JSON (text='$shown' / expected 1)"
		return
	fi
	wait_inodes 0 >/dev/null || true
	pass
}

# ===== run =====

if ! command -v fusermount3 >/dev/null 2>&1; then
	echo "${RED}fusermount3 is missing${NC} (the fuse3 package is required)" >&2; exit 2
fi
if [ -z "$DB_NAME" ] || [ -z "$DB_USER" ] || ! command -v "$PSQL_BIN" >/dev/null 2>&1 || [ "$(q 'select 1')" != "1" ]; then
	echo "${RED}cannot connect to the database with psql${NC} (pass the real path of psql in PGFS_PSQL)" >&2
	echo "  psql is needed to make a snapshot be written, so this suite does not run without it" >&2
	exit 2
fi

echo "${BLUE}=== pgfs handle leak tests ===${NC}"
echo "setting : $SETTING_FILE"
echo "mount   : $MNT"
echo ""

umount_all
mount_fs || { echo "${RED}cannot mount${NC}" >&2; exit 2; }
rm -rf "$T" 2>/dev/null
mkdir -p "$T" || { echo "${RED}cannot create the test directory${NC}: $T" >&2; umount_all; exit 2; }

run test_handles_idle_is_zero
run test_handles_file_open_close_balances
run test_handles_dup_fd_survives_first_close
run test_handles_readdir_balances
run test_handles_zero_after_mixed_workload
run test_handles_peak_records_concurrent_opens
# Stage C-1: the reference count of bodies (docs/design/handle-context.md)
run test_handles_inodes_counted
run test_handles_inodes_dedupe_same_file
run test_handles_inodes_dup_fd_is_one
run test_handles_inodes_readdir_balances
run test_handles_status_text_shows_inodes

rm -rf "$T" 2>/dev/null
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
