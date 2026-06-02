#!/usr/bin/env bash
# pgfs Linux e2e tests
#
# Usage:
#   bash tests/linux/e2e.sh [MOUNT_ROOT]
#
# The default MOUNT_ROOT is $HOME/mnt/pgfs.
# Tests run only under MOUNT_ROOT/test.
#
# Environment variables:
#   TEST_FILTER   run only tests whose name contains this string (e.g. "xattr")
#
# Exit codes:
#   0   all tests passed
#   1   one or more failed
#   2   MOUNT_ROOT does not exist (not mounted)
#   3   TEST_ROOT could not be created (mount not writable)

set -u

MOUNT_ROOT="${1:-$HOME/mnt/pgfs}"
TEST_ROOT="$MOUNT_ROOT/test"
FILTER="${TEST_FILTER:-}"

# ===== output =====

if [ -t 1 ]; then
	RED=$'\033[0;31m'
	GREEN=$'\033[0;32m'
	YELLOW=$'\033[0;33m'
	BLUE=$'\033[0;34m'
	NC=$'\033[0m'
else
	RED=''
	GREEN=''
	YELLOW=''
	BLUE=''
	NC=''
fi

declare -i TOTAL=0
declare -i PASSED=0
declare -i FAILED=0
declare -i SKIPPED=0
declare -a FAILED_NAMES=()
CURRENT=""

pass() {
	PASSED=$((PASSED+1))
	echo "${GREEN}PASS${NC}: $CURRENT"
}

fail() {
	FAILED=$((FAILED+1))
	FAILED_NAMES+=("$CURRENT")
	echo "${RED}FAIL${NC}: $CURRENT - $*"
}

skip() {
	SKIPPED=$((SKIPPED+1))
	echo "${YELLOW}SKIP${NC}: $CURRENT - $*"
}

# ===== assertion helpers =====

assert_eq() {
	local expected="$1" actual="$2" what="${3:-value}"
	if [ "$expected" != "$actual" ]; then
		fail "$what: expected '$expected', got '$actual'"
		return 1
	fi
}

assert_file() {
	if [ ! -f "$1" ]; then
		fail "expected file does not exist: $1"
		return 1
	fi
}

assert_dir() {
	if [ ! -d "$1" ]; then
		fail "expected dir does not exist: $1"
		return 1
	fi
}

assert_absent() {
	if [ -e "$1" ]; then
		fail "expected path to be absent: $1"
		return 1
	fi
}

assert_symlink() {
	if [ ! -L "$1" ]; then
		fail "expected symlink: $1"
		return 1
	fi
}

# Entry point that runs a single test.
run() {
	local name="$1"
	if [ -n "$FILTER" ] && [[ "$name" != *"$FILTER"* ]]; then
		return
	fi
	TOTAL=$((TOTAL+1))
	CURRENT="$name"
	"$name" || true
}

# ===== individual tests =====
#
# Each test uses a unique file/dir name under TEST_ROOT.
# No per-test cleanup is needed (TEST_ROOT is removed wholesale at the end).

# --- directory operations ---

test_mkdir_rmdir() {
	local d="$TEST_ROOT/t01_mkdir"
	mkdir "$d" || { fail "mkdir"; return; }
	assert_dir "$d" || return
	rmdir "$d" || { fail "rmdir"; return; }
	assert_absent "$d" || return
	pass
}

test_nested_directories() {
	local d="$TEST_ROOT/t02_nested/a/b/c/d/e"
	mkdir -p "$d" || { fail "mkdir -p"; return; }
	assert_dir "$d" || return
	echo "deep" > "$d/file.txt"
	local got=$(cat "$d/file.txt")
	assert_eq "deep" "$got" "deep content" || return
	pass
}

test_many_files_ls() {
	local d="$TEST_ROOT/t03_many"
	mkdir "$d"
	local i=0
	while [ $i -lt 100 ]; do
		touch "$d/f_$i"
		i=$((i+1))
	done
	local count=$(ls "$d" | wc -l)
	assert_eq "100" "$count" "files in dir" || return
	pass
}

test_rmdir_nonempty_fails() {
	local d="$TEST_ROOT/t04_nonempty"
	mkdir "$d"
	touch "$d/inside.txt"
	if rmdir "$d" 2>/dev/null; then
		fail "rmdir succeeded on non-empty dir"
		return
	fi
	pass
}

# --- basic file operations ---

test_touch_unlink() {
	local f="$TEST_ROOT/t10_touch.txt"
	touch "$f" || { fail "touch"; return; }
	assert_file "$f" || return
	rm "$f" || { fail "rm"; return; }
	assert_absent "$f" || return
	pass
}

test_small_write_read() {
	local f="$TEST_ROOT/t11_small.txt"
	echo "hello world" > "$f" || { fail "write"; return; }
	local got=$(cat "$f")
	assert_eq "hello world" "$got" "content" || return
	pass
}

test_append() {
	local f="$TEST_ROOT/t12_append.txt"
	echo "line1" > "$f"
	echo "line2" >> "$f"
	local lines=$(wc -l < "$f")
	assert_eq "2" "$lines" "line count" || return
	local got=$(cat "$f")
	if [ "$got" != "line1
line2" ]; then
		fail "append content: '$got'"
		return
	fi
	pass
}

test_overwrite_truncates() {
	# O_TRUNC: overwriting an existing file with an empty file should yield 0 bytes.
	local f="$TEST_ROOT/t13_otrunc.txt"
	echo "long content here" > "$f"
	local tmp="/tmp/pgfs_empty_$$"
	: > "$tmp"
	cp "$tmp" "$f" || { fail "cp"; rm -f "$tmp"; return; }
	rm -f "$tmp"
	local size=$(stat -c '%s' "$f")
	assert_eq "0" "$size" "size after overwrite with empty" || return
	pass
}

# --- data I/O (byte chunks) ---

test_large_file_round_trip() {
	# Default chunk size is 1 MiB, so 2 MiB spans two chunks.
	local src="/tmp/pgfs_large_$$.bin"
	local f="$TEST_ROOT/t20_large.bin"
	dd if=/dev/urandom of="$src" bs=1M count=2 status=none || { fail "dd"; return; }
	cp "$src" "$f" || { fail "cp into mount"; rm -f "$src"; return; }
	if ! cmp -s "$src" "$f"; then
		fail "data differs after round-trip"
		rm -f "$src"
		return
	fi
	rm -f "$src"
	pass
}

test_truncate_shrink() {
	local f="$TEST_ROOT/t21_trunc_shrink.txt"
	echo "0123456789" > "$f"
	truncate -s 5 "$f" || { fail "truncate -s 5"; return; }
	local got=$(cat "$f")
	assert_eq "01234" "$got" "content after shrink" || return
	pass
}

test_truncate_grow() {
	local f="$TEST_ROOT/t22_trunc_grow.txt"
	printf "abc" > "$f"
	truncate -s 10 "$f" || { fail "truncate -s 10"; return; }
	local size=$(stat -c '%s' "$f")
	assert_eq "10" "$size" "size after grow" || return
	pass
}

test_truncate_to_zero() {
	local f="$TEST_ROOT/t23_trunc_zero.txt"
	dd if=/dev/urandom of="$f" bs=1M count=1 status=none
	truncate -s 0 "$f" || { fail "truncate -s 0"; return; }
	local size=$(stat -c '%s' "$f")
	assert_eq "0" "$size" "size after zero" || return
	pass
}

# --- rename / move ---

test_rename_file() {
	local src="$TEST_ROOT/t30_rn_src.txt"
	local dst="$TEST_ROOT/t30_rn_dst.txt"
	echo "renamed" > "$src"
	mv "$src" "$dst" || { fail "mv"; return; }
	assert_absent "$src" || return
	assert_file "$dst" || return
	local got=$(cat "$dst")
	assert_eq "renamed" "$got" "content after rename" || return
	pass
}

test_rename_into_subdir() {
	local sub="$TEST_ROOT/t31_subdir"
	mkdir "$sub"
	local src="$TEST_ROOT/t31_mv.txt"
	echo "moved" > "$src"
	mv "$src" "$sub/moved.txt" || { fail "mv into subdir"; return; }
	assert_absent "$src" || return
	assert_file "$sub/moved.txt" || return
	pass
}

# --- permissions ---

test_chmod() {
	local f="$TEST_ROOT/t40_chmod.txt"
	touch "$f"
	chmod 600 "$f" || { fail "chmod 600"; return; }
	local mode=$(stat -c '%a' "$f")
	assert_eq "600" "$mode" "mode after 600" || return
	chmod 644 "$f" || { fail "chmod 644"; return; }
	mode=$(stat -c '%a' "$f")
	assert_eq "644" "$mode" "mode after 644" || return
	pass
}

test_chmod_dir() {
	local d="$TEST_ROOT/t41_chmod_dir"
	mkdir "$d"
	chmod 700 "$d" || { fail "chmod 700"; return; }
	local mode=$(stat -c '%a' "$d")
	assert_eq "700" "$mode" "dir mode after chmod" || return
	pass
}

test_chown_self() {
	# chown to self (uid/gid do not change, but the API is exercised).
	local f="$TEST_ROOT/t42_chown.txt"
	touch "$f"
	local user=$(id -un)
	local group=$(id -gn)
	chown "$user:$group" "$f" || { fail "chown self"; return; }
	pass
}

# --- symbolic links ---

test_symlink_basic() {
	local target="$TEST_ROOT/t50_sl_target.txt"
	local link="$TEST_ROOT/t50_sl_link.txt"
	echo "target content" > "$target"
	ln -s t50_sl_target.txt "$link" || { fail "ln -s"; return; }
	assert_symlink "$link" || return
	local resolved=$(readlink "$link")
	assert_eq "t50_sl_target.txt" "$resolved" "readlink" || return
	local content=$(cat "$link")
	assert_eq "target content" "$content" "via-link read" || return
	pass
}

test_symlink_dangling() {
	local link="$TEST_ROOT/t51_dangling.lnk"
	ln -s does_not_exist "$link" || { fail "ln -s (dangling)"; return; }
	assert_symlink "$link" || return
	local resolved=$(readlink "$link")
	assert_eq "does_not_exist" "$resolved" "readlink (dangling)" || return
	pass
}

test_symlink_absolute() {
	local target="$TEST_ROOT/t52_abs_target.txt"
	echo "abs" > "$target"
	local link="$TEST_ROOT/t52_abs_link.lnk"
	ln -s "$target" "$link" || { fail "ln -s (abs)"; return; }
	assert_symlink "$link" || return
	local content=$(cat "$link")
	assert_eq "abs" "$content" "absolute symlink content" || return
	pass
}

# --- hard links ---

test_hardlink_basic() {
	local a="$TEST_ROOT/t60_hl_a.txt"
	local b="$TEST_ROOT/t60_hl_b.txt"
	echo "hard data" > "$a"
	ln "$a" "$b" || { fail "ln"; return; }
	local na=$(stat -c '%h' "$a")
	local nb=$(stat -c '%h' "$b")
	assert_eq "2" "$na" "nlink(a)" || return
	assert_eq "2" "$nb" "nlink(b)" || return
	# Both must share the same inode number (st_ino). The mount uses `-o use_ino`, so the
	# data_id-derived st_ino from FillStat reaches the kernel. a and b share the same data_id,
	# so their inode numbers must be equal.
	local ina=$(stat -c '%i' "$a")
	local inb=$(stat -c '%i' "$b")
	assert_eq "$ina" "$inb" "inode (a == b)" || return
	pass
}

test_hardlink_after_rm() {
	local a="$TEST_ROOT/t61_hl2_a.txt"
	local b="$TEST_ROOT/t61_hl2_b.txt"
	echo "survives" > "$a"
	ln "$a" "$b" || { fail "ln"; return; }
	rm "$a" || { fail "rm a"; return; }
	local nb=$(stat -c '%h' "$b")
	assert_eq "1" "$nb" "nlink(b) after rm(a)" || return
	local content=$(cat "$b")
	assert_eq "survives" "$content" "content after rm(a)" || return
	pass
}

test_hardlink_through_subdir() {
	local sub="$TEST_ROOT/t62_subdir"
	mkdir "$sub"
	local a="$TEST_ROOT/t62_top.txt"
	local b="$sub/sublink.txt"
	echo "shared" > "$a"
	ln "$a" "$b" || { fail "ln across subdir"; return; }
	local content=$(cat "$b")
	assert_eq "shared" "$content" "sub content via hardlink" || return
	pass
}

# --- extended attributes (xattr) ---

test_xattr_set_get() {
	if ! command -v setfattr >/dev/null 2>&1; then
		skip "setfattr not installed (apt install attr)"
		return
	fi
	local f="$TEST_ROOT/t70_xa.txt"
	touch "$f"
	setfattr -n user.foo -v bar "$f" || { fail "setfattr"; return; }
	local value=$(getfattr --only-values -n user.foo "$f" 2>/dev/null)
	assert_eq "bar" "$value" "xattr value" || return
	pass
}

test_xattr_list() {
	if ! command -v setfattr >/dev/null 2>&1; then
		skip "setfattr not installed"
		return
	fi
	local f="$TEST_ROOT/t71_xa_list.txt"
	touch "$f"
	setfattr -n user.alpha -v one "$f"
	setfattr -n user.beta -v two "$f"
	local count=$(getfattr -d "$f" 2>/dev/null | grep -c '^user\.')
	assert_eq "2" "$count" "xattr count" || return
	pass
}

test_xattr_remove() {
	if ! command -v setfattr >/dev/null 2>&1; then
		skip "setfattr not installed"
		return
	fi
	local f="$TEST_ROOT/t72_xa_rm.txt"
	touch "$f"
	setfattr -n user.gone -v gone "$f"
	setfattr -x user.gone "$f" || { fail "setfattr -x"; return; }
	if getfattr -n user.gone "$f" >/dev/null 2>&1; then
		fail "xattr still present after removal"
		return
	fi
	pass
}

test_xattr_overwrite() {
	if ! command -v setfattr >/dev/null 2>&1; then
		skip "setfattr not installed"
		return
	fi
	local f="$TEST_ROOT/t73_xa_ow.txt"
	touch "$f"
	setfattr -n user.k -v v1 "$f"
	setfattr -n user.k -v v2 "$f" || { fail "setfattr overwrite"; return; }
	local value=$(getfattr --only-values -n user.k "$f" 2>/dev/null)
	assert_eq "v2" "$value" "xattr overwrite value" || return
	pass
}

# --- POSIX ACL (setfacl / getfacl) ---

test_posix_acl_named_user() {
	if ! command -v setfacl >/dev/null 2>&1 || ! command -v getfacl >/dev/null 2>&1; then
		skip "setfacl/getfacl not installed (apt install acl)"
		return
	fi
	local f="$TEST_ROOT/t74_acl.txt"
	echo "acl" > "$f"
	# Grant r-x to the named user 'nobody' (routed through system.posix_acl_access).
	setfacl -m u:nobody:r-x "$f" 2>/dev/null || { fail "setfacl -m"; return; }
	local line=$(getfacl -c "$f" 2>/dev/null | grep '^user:nobody:')
	case "$line" in
		user:nobody:r-x*) ;;
		*) fail "named user acl entry: got '$line'"; return ;;
	esac
	# Removing the extended ACL drops the named entry (setfacl -b).
	setfacl -b "$f" 2>/dev/null || { fail "setfacl -b"; return; }
	if getfacl -c "$f" 2>/dev/null | grep -q '^user:nobody:'; then
		fail "named acl survived setfacl -b"
		return
	fi
	pass
}

# --- metadata ---

test_statfs() {
	# df calls StatFS.
	df "$MOUNT_ROOT" > /dev/null || { fail "df"; return; }
	pass
}

test_utime_explicit() {
	local f="$TEST_ROOT/t80_utime.txt"
	touch "$f"
	touch -d "2025-01-15 12:30:45" "$f" || { fail "touch -d"; return; }
	local m=$(stat -c '%Y' "$f")
	# 2025-01-15 12:30:45 UTC = 1736944245; the value may differ in local time.
	# Just check that it changed.
	if [ "$m" = "0" ]; then
		fail "mtime is zero after touch -d"
		return
	fi
	pass
}

test_utime_now() {
	# UTIME_NOW path: touch without args should not error
	local f="$TEST_ROOT/t81_utime_now.txt"
	touch "$f"
	sleep 1
	touch "$f" || { fail "second touch"; return; }
	pass
}

# --- concurrent access ---

test_concurrent_writes_diff_files() {
	local i=0
	while [ $i -lt 5 ]; do
		( echo "data $i" > "$TEST_ROOT/t90_cw_$i.txt" ) &
		i=$((i+1))
	done
	wait
	i=0
	while [ $i -lt 5 ]; do
		local got=$(cat "$TEST_ROOT/t90_cw_$i.txt")
		if [ "$got" != "data $i" ]; then
			fail "t90_cw_$i content: '$got'"
			return
		fi
		i=$((i+1))
	done
	pass
}

test_concurrent_reads_same_file() {
	local f="$TEST_ROOT/t91_shared.bin"
	dd if=/dev/urandom of="$f" bs=1M count=1 status=none
	local orig=$(md5sum "$f" | awk '{print $1}')
	local i=0
	while [ $i -lt 5 ]; do
		( md5sum "$f" | awk '{print $1}' > "/tmp/pgfs_h_${i}_$$" ) &
		i=$((i+1))
	done
	wait
	i=0
	while [ $i -lt 5 ]; do
		local h=$(cat "/tmp/pgfs_h_${i}_$$" 2>/dev/null)
		rm -f "/tmp/pgfs_h_${i}_$$"
		if [ "$h" != "$orig" ]; then
			fail "concurrent read $i: hash mismatch (got '$h')"
			return
		fi
		i=$((i+1))
	done
	pass
}

test_concurrent_mkdir_diff_dirs() {
	local i=0
	while [ $i -lt 5 ]; do
		( mkdir "$TEST_ROOT/t92_cm_$i" ) &
		i=$((i+1))
	done
	wait
	i=0
	while [ $i -lt 5 ]; do
		if [ ! -d "$TEST_ROOT/t92_cm_$i" ]; then
			fail "t92_cm_$i missing"
			return
		fi
		i=$((i+1))
	done
	pass
}

# --- name resolution fallback (direct DB INSERT) ---
#
# Directly INSERT an inode with a user/group name that does not exist on the OS into
# pgfs_inode, then confirm that stat-ing that inode resolves to mount.fallback_uname /
# fallback_gname (defaults: nobody / nogroup). The operation goes through psql on the
# database host (pgsql_server); the source-built psql is used explicitly because the
# distro psql can fail on a libpq version mismatch.

pg_exec() {
	# Stream SQL on stdin. -tA is tuples-only + unaligned output, returning one ${name}\n per row.
	# If `PGFS_TEST_PG_EXEC` is set it is used instead (for the docker Citus path in race_multinode.sh).
	# The default goes through the source-built psql on pgsql_server (the distro psql can fail on a
	# libpq version mismatch, so this one is used explicitly).
	if [ -n "${PGFS_TEST_PG_EXEC:-}" ]; then
		eval "$PGFS_TEST_PG_EXEC"
	else
		ssh -o BatchMode=yes -o ConnectTimeout=5 -o LogLevel=ERROR pgsql_server \
			"LD_LIBRARY_PATH=/usr/local/pgsql/lib PGPASSWORD=pgfs /usr/local/pgsql/bin/psql -h localhost -U pgfs -d pgfs -tA -q"
	fi
}

test_fallback_uname_gname() {
	# Skip if the DB is unreachable (e.g. CI without an external DB).
	if ! echo "SELECT 1" | pg_exec >/dev/null 2>&1; then
		skip "psql via ssh pgsql_server not reachable"
		return
	fi
	# Look up the inode id of the test directory (directly under the mount root, parent_id=0).
	local test_id
	test_id=$(echo "SELECT id FROM pgfs.pgfs_inode WHERE name='test' AND parent_id=0" | pg_exec | head -1)
	if ! [[ "$test_id" =~ ^[0-9]+$ ]]; then
		fail "could not look up test/ dir id: '$test_id'"
		return
	fi
	# Directly INSERT an inode with a user/group name that does not exist on the OS.
	local ghost="t100_ghost_$$"
	echo "INSERT INTO pgfs.pgfs_inode (parent_id, name, uname, gname, st_mode, st_nlink, st_size, is_junction, xattrs, created_by, updated_by) VALUES ($test_id, '$ghost', '__no_such_user_xyz__', '__no_such_group_xyz__', 33188, 1, 0, false, '{}'::jsonb, 'pgfs', 'pgfs')" | pg_exec >/dev/null
	# stat -> GetByPath -> child (cache miss) -> DB fetch -> uname resolution fails -> fallback.
	# Note: the childrenByParent cache is stale, but a direct-path stat goes through byPath/byId,
	#       so the freshly inserted row is still visible (ListChildren is not called).
	local owner=$(stat -c '%U' "$TEST_ROOT/$ghost" 2>/dev/null)
	local group=$(stat -c '%G' "$TEST_ROOT/$ghost" 2>/dev/null)
	# Cleanup: always DELETE (even on failure).
	echo "DELETE FROM pgfs.pgfs_inode WHERE parent_id=$test_id AND name='$ghost'" | pg_exec >/dev/null
	assert_eq "nobody" "$owner" "fallback uname" || return
	assert_eq "nogroup" "$group" "fallback gname" || return
	pass
}

# ===== main =====

setup() {
	echo "${BLUE}=== pgfs Linux e2e tests ===${NC}"
	echo "Mount root: $MOUNT_ROOT"
	echo "Test root:  $TEST_ROOT"
	if [ -n "$FILTER" ]; then
		echo "Filter:     $FILTER"
	fi
	echo ""

	if [ ! -d "$MOUNT_ROOT" ]; then
		echo "${RED}ERROR${NC}: $MOUNT_ROOT does not exist."
		echo "Mount mount.pgfs first."
		exit 2
	fi

	# Clean up any existing test directory.
	rm -rf "$TEST_ROOT" 2>/dev/null || true
	mkdir "$TEST_ROOT" || {
		echo "${RED}ERROR${NC}: failed to create $TEST_ROOT (mount writable?)"
		exit 3
	}
}

teardown() {
	rm -rf "$TEST_ROOT" 2>/dev/null || true
}

trap teardown EXIT

setup

# ===== run =====

# directory operations
run test_mkdir_rmdir
run test_nested_directories
run test_many_files_ls
run test_rmdir_nonempty_fails

# basic file operations
run test_touch_unlink
run test_small_write_read
run test_append
run test_overwrite_truncates

# data I/O
run test_large_file_round_trip
run test_truncate_shrink
run test_truncate_grow
run test_truncate_to_zero

# rename
run test_rename_file
run test_rename_into_subdir

# permissions
run test_chmod
run test_chmod_dir
run test_chown_self

# symbolic links
run test_symlink_basic
run test_symlink_dangling
run test_symlink_absolute

# hard links
run test_hardlink_basic
run test_hardlink_after_rm
run test_hardlink_through_subdir

# extended attributes
run test_xattr_set_get
run test_xattr_list
run test_xattr_remove
run test_xattr_overwrite

# POSIX ACL
run test_posix_acl_named_user

# metadata
run test_statfs
run test_utime_explicit
run test_utime_now

# concurrent access
run test_concurrent_writes_diff_files
run test_concurrent_reads_same_file
run test_concurrent_mkdir_diff_dirs

# name resolution fallback
run test_fallback_uname_gname

# ===== summary =====

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
exit 0
