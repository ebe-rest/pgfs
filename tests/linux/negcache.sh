#!/usr/bin/env bash
# Tests dedicated to the pgfs negative lookup cache (mount.negative_cache_ttl_ms)
#
# Unlike tests/linux/e2e.sh, **this script mounts and remounts on its own**
# (because the TTL setting has to change and the mount has to be redone). The contracts:
#   - this client's own create / mkdir / rename invalidate the negative marker immediately
#     (a stale ENOENT cannot be observed with a single client)
#   - another client's creation may stay invisible for up to one TTL (that is by design)
#   - readdir (re-fetching the child list) and a live reload (TTL=0) sweep the markers
#
# Usage:
#   bash tests/linux/negcache.sh
#
# Environment variables:
#   PGFS_SETTING_FILE   the settings file handed to mount.pgfs (default $HOME/pgfs_test.toml)
#   MOUNT_ROOT          the mount target (default $HOME/mnt/pgfs)
#   PGFS_BIN            the directory holding the binaries (default ./bin/Debug)
#   PSQL                the psql command (default psql)
#   TEST_FILTER         run only the tests whose name contains this string
#
# Prerequisites: the FS the settings file points at has been mkfs'd and may be destroyed
#       (the tests only touch what is under MOUNT_ROOT/negtest). "Another client" is stood in for by a
#       direct INSERT through psql, so the connection information must be readable from the settings file.
#
# Exit codes: 0 = everything passed / 1 = something failed / 2 = a prerequisite is missing

set -u

SETTING_FILE="${PGFS_SETTING_FILE:-$HOME/pgfs_test.toml}"
MOUNT_ROOT="${MOUNT_ROOT:-$HOME/mnt/pgfs}"
BIN="${PGFS_BIN:-./bin/Debug}"
# The path to psql. It can also be given as `PGFS_PSQL`, the same name wbmeta.sh uses (getting the two mixed up
# only fails the prerequisite check, so both are accepted).
# Note: in an environment where psql is only on the interactive shell's PATH, this script - which runs
# non-interactively - cannot find it.
PSQL_BIN="${PGFS_PSQL:-${PSQL:-psql}}"
TEST_ROOT="$MOUNT_ROOT/negtest"
FILTER="${TEST_FILTER:-}"

if [ -t 1 ]; then
	RED=$'\033[0;31m'; GREEN=$'\033[0;32m'; YELLOW=$'\033[0;33m'; NC=$'\033[0m'
else
	RED=''; GREEN=''; YELLOW=''; NC=''
fi

declare -i TOTAL=0 PASSED=0 FAILED=0 SKIPPED=0
declare -a FAILED_NAMES=()
CURRENT=""

pass() { PASSED=$((PASSED+1)); echo "${GREEN}PASS${NC}: $CURRENT"; }
fail() { FAILED=$((FAILED+1)); FAILED_NAMES+=("$CURRENT"); echo "${RED}FAIL${NC}: $CURRENT - $*"; }
skip() { SKIPPED=$((SKIPPED+1)); echo "${YELLOW}SKIP${NC}: $CURRENT - $*"; }

# ===== connection information (from the settings file) =====

CONN=$(grep -E '^connection' "$SETTING_FILE" | head -1 | sed 's/^connection *= *//; s/^"//; s/"$//')
SCHEMA=$(grep -E '^schema' "$SETTING_FILE" | head -1 | sed 's/^schema *= *//; s/^"//; s/"$//')

# a kv-form connection string -> psql arguments
pg_field() { echo "$CONN" | tr ';' '\n' | grep -i "^$1=" | head -1 | cut -d= -f2; }
PG_ARGS=(-h "$(pg_field Host)" -p "${PGPORT_OVERRIDE:-$(pg_field Port)}" -U "$(pg_field Username)" -d "$(pg_field Database)" -qtA)

sql() { "$PSQL_BIN" "${PG_ARGS[@]}" -c "$1"; }

# ===== mount operations =====

# Look it up by process name (`pgrep -f` also matches the calling shell, which contains the same command line.
# Measured: it matched 3 times and head -1 returned the shell's pid).
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

# The inode id of TEST_ROOT (used as the parent of the direct psql INSERT)
test_root_id() {
	sql "select id from $SCHEMA.pgfs_inode where parent_id = 0 and name = 'negtest'"
}

# The stand-in for "another client": create the file row directly in the database without going through the mount
remote_create() {
	local parent_id="$1" name="$2"
	sql "insert into $SCHEMA.pgfs_inode (parent_id, name, uname, gname, st_mode, created_by, updated_by) values ($parent_id, '$name', 'negtest', 'negtest', 33188, 'negtest', 'negtest')" >/dev/null
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

# This client's own create invalidates the negative marker immediately (the basic single-client safety)
test_own_create_visible() {
	local f="$TEST_ROOT/t1_own.txt"
	stat "$f" >/dev/null 2>&1 && { fail "it exists beforehand"; return; }
	echo hello > "$f" || { fail "create"; return; }
	stat "$f" >/dev/null 2>&1 || { fail "ENOENT right after the create (the marker is still there)"; return; }
	[ "$(cat "$f")" = "hello" ] || { fail "the contents do not match"; return; }
	pass
}

# The same for mkdir plus a create below it (the marker of an intermediate component)
test_own_mkdir_visible() {
	local d="$TEST_ROOT/t2_dir"
	stat "$d/child.txt" >/dev/null 2>&1 && { fail "it exists beforehand"; return; }
	mkdir "$d" || { fail "mkdir"; return; }
	echo x > "$d/child.txt" || { fail "the create below it failed (the dir's marker is still there)"; return; }
	stat "$d/child.txt" >/dev/null 2>&1 || { fail "the stat below it is ENOENT"; return; }
	pass
}

# Even with a negative marker on a rename's destination, it is visible immediately after the rename
test_rename_over_negative() {
	local tmp="$TEST_ROOT/t3_tmp.txt"
	local final="$TEST_ROOT/t3_final.txt"
	stat "$final" >/dev/null 2>&1 && { fail "it exists beforehand"; return; }
	echo data > "$tmp"
	mv "$tmp" "$final" || { fail "mv"; return; }
	stat "$final" >/dev/null 2>&1 || { fail "ENOENT right after the rename (the marker is still there)"; return; }
	pass
}

# Another client's creation may be invisible within the TTL (by design) and becomes visible after it passes
test_remote_create_hidden_until_ttl() {
	local name="t4_remote.txt"
	local f="$TEST_ROOT/$name"
	local pid=$(test_root_id)
	[ -n "$pid" ] || { fail "cannot look up test_root_id"; return; }
	stat "$f" >/dev/null 2>&1 && { fail "it exists beforehand"; return; }
	remote_create "$pid" "$name" || { fail "remote INSERT"; return; }
	# Within the TTL (3s): the marker holds and it stays ENOENT = as the contract says
	stat "$f" >/dev/null 2>&1 && { fail "it became visible within the TTL (the marker is not holding)"; return; }
	sleep 4
	stat "$f" >/dev/null 2>&1 || { fail "still ENOENT after the TTL passed"; return; }
	pass
}

# readdir (re-fetching the child list) sweeps that parent's negative markers
test_remote_create_visible_after_readdir() {
	local d="$TEST_ROOT/t5_dir"
	mkdir "$d"
	local name="t5_remote.txt"
	local pid=$(sql "select id from $SCHEMA.pgfs_inode where name = 't5_dir'")
	[ -n "$pid" ] || { fail "cannot look up the id of t5_dir"; return; }
	stat "$d/$name" >/dev/null 2>&1 && { fail "it exists beforehand"; return; }
	remote_create "$pid" "$name" || { fail "remote INSERT"; return; }
	ls "$d" | grep -q "$name" || { fail "it does not show up in readdir"; return; }
	stat "$d/$name" >/dev/null 2>&1 || { fail "stat is still ENOENT after the readdir (PutChildren missed the sweep)"; return; }
	pass
}

# A live reload (TTL -> 0) clears every marker
test_live_reload_off_clears() {
	local name="t6_remote.txt"
	local f="$TEST_ROOT/$name"
	local pid=$(test_root_id)
	stat "$f" >/dev/null 2>&1 && { fail "it exists beforehand"; return; }
	remote_create "$pid" "$name" || { fail "remote INSERT"; return; }
	stat "$f" >/dev/null 2>&1 && { fail "it became visible within the TTL"; return; }
	"$BIN/pgfsctl" config set mount.negative_cache_ttl_ms 0 -c "$CONN" -s "$SCHEMA" >/dev/null 2>&1 || { fail "pgfsctl config set"; return; }
	sleep 1
	stat "$f" >/dev/null 2>&1 || { fail "still ENOENT after the live off (it was not cleared)"; return; }
	# Put the setting back for the tests that follow (the DB-saved value. The mount itself picks it up again at the next remount)
	"$BIN/pgfsctl" config set mount.negative_cache_ttl_ms 3000 -c "$CONN" -s "$SCHEMA" >/dev/null 2>&1
	pass
}

# Immediate ENOENT after a delete (no staleness in the positive -> negative direction)
test_unlink_immediately_gone() {
	local f="$TEST_ROOT/t7_gone.txt"
	echo x > "$f"
	rm "$f" || { fail "rm"; return; }
	stat "$f" >/dev/null 2>&1 && { fail "it is still visible after the rm"; return; }
	pass
}

# ===== prerequisite checks =====

if [ ! -x "$BIN/mount.pgfs" ]; then
	echo "${RED}mount.pgfs is missing${NC}: $BIN/mount.pgfs" >&2
	exit 2
fi
if [ -z "$CONN" ] || [ -z "$SCHEMA" ]; then
	echo "${RED}cannot read connection / schema from the settings file${NC}: $SETTING_FILE" >&2
	exit 2
fi
if ! sql "select 1" >/dev/null 2>&1; then
	echo "${RED}cannot connect to the database with psql${NC}" >&2
	exit 2
fi

# ===== run =====

unmount_clean
mount_with --negative-cache-ttl-ms 3000 || exit 2
rm -rf "$TEST_ROOT" 2>/dev/null
mkdir -p "$TEST_ROOT"

run test_own_create_visible
run test_own_mkdir_visible
run test_rename_over_negative
run test_remote_create_hidden_until_ttl
run test_remote_create_visible_after_readdir
run test_live_reload_off_clears
run test_unlink_immediately_gone

# ===== cleanup =====

rm -rf "$TEST_ROOT" 2>/dev/null
unmount_clean
# Put the setting that ended up saved in the database back to the default (0)
"$BIN/pgfsctl" config set mount.negative_cache_ttl_ms 0 -c "$CONN" -s "$SCHEMA" >/dev/null 2>&1

echo ""
echo "==========================================="
echo "Results: $PASSED passed, $FAILED failed, $SKIPPED skipped (out of $TOTAL)"
if [ $FAILED -gt 0 ]; then
	printf '%s\n' "${FAILED_NAMES[@]/#/  FAILED: }"
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
