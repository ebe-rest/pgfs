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

# rename-over-existing (the tmp+rename pattern of an editor save or of rsync).
# Deleting what is being replaced and the rename happen in the same tx (made atomic earlier).
test_rename_replace_existing() {
	local src="$TEST_ROOT/t32_rn_src.txt"
	local dst="$TEST_ROOT/t32_rn_dst.txt"
	local link="$TEST_ROOT/t32_rn_link.txt"
	echo "new content" > "$src"
	echo "old content" > "$dst"
	ln "$dst" "$link"                     # put a hardlink on what is being replaced (to check the nlink path)
	mv "$src" "$dst" || { fail "mv over existing"; return; }
	assert_absent "$src" || return
	local got=$(cat "$dst")
	assert_eq "new content" "$got" "content after replace" || return
	# The hardlink sibling survives with the old data, and nlink goes back to 1
	local lgot=$(cat "$link")
	assert_eq "old content" "$lgot" "hardlink sibling keeps old data" || return
	local nlink=$(stat -c '%h' "$link")
	assert_eq "1" "$nlink" "sibling nlink after replace" || return
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

test_open_unlink_read_still_works() {
	# **A net that pins the current behaviour down** (the premise of handle-context stage C; docs/design/handle-context.md).
	#
	# POSIX says "even after an unlink, an open fd can still read and write". pgfs **does not implement this
	# itself** - it works because, with `fuse_config.hard_remove = 0` (the default), libfuse
	# **turns the unlink of a still-open file into a rename to `.fuse_hidden*`**.
	#
	# ⚠ **Setting `hard_remove = 1` makes this test fail on the spot** (measured). Do not set it until
	#   stage C brings in liveness management (a reference count plus DeletePending).
	#   **This test exists so that it is noticed when that happens.**
	#
	# **The matching "after close, `.fuse_hidden*` is gone" is deliberately not a test.**
	# The cleanup runs not at close but **when the kernel FORGETs the inode**, and the kernel decides when
	# that is (measured: both "gone within a second" and "still there after 5 seconds" happened).
	# **Waiting on something with no bound and asserting on it counts a merely slow run as a broken one.**
	local f="$TEST_ROOT/t65_open_unlink.txt"
	echo "STILL_HERE" > "$f"
	exec 9< "$f" || { fail "open"; return; }
	rm "$f" || { fail "rm"; exec 9<&-; return; }
	assert_absent "$f" || { exec 9<&-; return; }
	local content
	content=$(cat <&9)
	exec 9<&-
	assert_eq "STILL_HERE" "$content" "content read through fd opened before unlink" || return
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

# --- the data_id lifecycle (docs/design/data-id-lifecycle.md) ---
#
# A hardlink is expressed as "several inodes pointing at the same data_id". If the data_id is released and
# re-reserved by a per-link operation, the sharing quietly breaks and the siblings are left with lost data
# and a dangling reference. What follows is the regression net for that.

test_truncate_keeps_hardlink_shared() {
	local a="$TEST_ROOT/t63_dl_a.txt"
	local b="$TEST_ROOT/t63_dl_b.txt"
	echo "A" > "$a"
	ln "$a" "$b" || { fail "ln"; return; }
	# `>` implies O_TRUNC, so it goes through the truncate path. The body is shared, so the new content must be visible from a as well.
	echo "B" > "$b"
	local got=$(cat "$a")
	assert_eq "B" "$got" "content via other link after O_TRUNC rewrite" || return
	local na=$(stat -c '%h' "$a")
	local nb=$(stat -c '%h' "$b")
	assert_eq "2" "$na" "nlink(a) stays 2" || return
	assert_eq "2" "$nb" "nlink(b) stays 2" || return
	local ina=$(stat -c '%i' "$a")
	local inb=$(stat -c '%i' "$b")
	assert_eq "$ina" "$inb" "inode still shared" || return
	pass
}

test_truncate_zero_updates_all_links() {
	local a="$TEST_ROOT/t64_dl_a.txt"
	local b="$TEST_ROOT/t64_dl_b.txt"
	echo "HELLO" > "$a"
	ln "$a" "$b" || { fail "ln"; return; }
	truncate -s 0 "$a" || { fail "truncate"; return; }
	local sa=$(stat -c '%s' "$a")
	local sb=$(stat -c '%s' "$b")
	assert_eq "0" "$sa" "size(a) after truncate" || return
	# Without distributing it, b ends up "size is 6 yet there are no chunks" = cat returns NUL bytes.
	assert_eq "0" "$sb" "size(b) must follow (a shared body)" || return
	local got=$(cat "$b")
	assert_eq "" "$got" "content via other link is empty (not NUL bytes)" || return
	pass
}

test_empty_file_hardlink_shares() {
	local a="$TEST_ROOT/t65_dl_a.txt"
	local b="$TEST_ROOT/t65_dl_b.txt"
	: > "$a"
	ln "$a" "$b" || { fail "ln"; return; }
	local na=$(stat -c '%h' "$a")
	local nb=$(stat -c '%h' "$b")
	assert_eq "2" "$na" "nlink(a) for empty file" || return
	assert_eq "2" "$nb" "nlink(b) for empty file" || return
	local ina=$(stat -c '%i' "$a")
	local inb=$(stat -c '%i' "$b")
	assert_eq "$ina" "$inb" "inode shared for empty file" || return
	# Whether the body really is shared (writing through one is visible through the other).
	echo "shared" > "$a"
	local got=$(cat "$b")
	assert_eq "shared" "$got" "content shared after write" || return
	pass
}

# **Whether a decimal string is 2^63 or above** (= whether the top bit of `st_ino` is set).
#
# **It does not depend on `python3`**. The docker mount container has no python3, and
# **without it the comparison stays empty behind a `command not found` and a correct FS gets counted as broken**
# (measured: `test_ino_namespace_split` reported 9223372036854775934 as "not set").
# **bash arithmetic is signed 64-bit and cannot handle 2^63 or above**, so it compares by **digit count, then
# lexicographically** (both are digits only, so with the same number of digits lexicographic = numeric order).
ge_2pow63() {
	local v="$1"
	local limit="9223372036854775808"
	if [ "${#v}" -gt "${#limit}" ]; then return 0; fi
	if [ "${#v}" -lt "${#limit}" ]; then return 1; fi
	[ "$(LC_ALL=C; printf '%s\n%s\n' "$v" "$limit" | sort | head -1)" = "$limit" ]
}

test_ino_namespace_split() {
	# **A file's and a directory's st_ino must live in separate namespaces.**
	#
	#   A file is `data_id | 0x8000_0000_0000_0000` (= the top bit set),
	#   a directory is `inode.Id` as-is ([FillStat](../../src/fuse/src/FileSystem.cs)).
	#   **Without the split, the moment some data_id equals some inode.Id
	#   "a file and a directory share an st_ino"**, and the hardlink detection and the loop detection of
	#   `find` / `rsync` misbehave.
	#
	#   The Windows side uses the same formula (`ByHandleFileInformation.FileIndex`).
	#   **The same file gets the same value on both operating systems.**
	local d="$TEST_ROOT/t67_ns"
	mkdir -p "$d" || { fail "mkdir"; return; }
	echo "x" > "$d/f.txt"
	local fino=$(stat -c '%i' "$d/f.txt")
	local dino=$(stat -c '%i' "$d")
	# 2^63 = 9223372036854775808
	if ! ge_2pow63 "$fino"; then
		fail "the top bit is not set on the file's st_ino ($fino)"
		return
	fi
	if ge_2pow63 "$dino"; then
		fail "the directory's st_ino is not in the inode space ($dino)"
		return
	fi
	if [ "$fino" = "$dino" ]; then
		fail "the file and the directory share an st_ino ($fino)"
		return
	fi
	pass
}

test_ino_stable_across_write() {
	local f="$TEST_ROOT/t66_dl_ino.txt"
	: > "$f"
	local before=$(stat -c '%i' "$f")
	echo "HELLO" > "$f"
	local after=$(stat -c '%i' "$f")
	assert_eq "$before" "$after" "st_ino must not change when content is written" || return
	truncate -s 0 "$f" || { fail "truncate"; return; }
	echo "AGAIN" > "$f"
	local again=$(stat -c '%i' "$f")
	assert_eq "$before" "$again" "st_ino must not change across truncate + rewrite" || return
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

test_xattr_binary() {
	# Byte-string transparency (docs/xattr-bytea.md): does a value containing NUL (0x00) and a
	# high byte (0xff) round-trip faithfully? Inject the raw bytes with setfattr -v 0s<base64>,
	# read back with getfattr -e base64, and compare. This also passed under the old JSONB+Base64
	# implementation, so it guards against regression after the bytea migration.
	if ! command -v setfattr >/dev/null 2>&1; then
		skip "setfattr not installed"
		return
	fi
	local f="$TEST_ROOT/t73b_xa_bin.txt"
	touch "$f"
	# value = 0x00 0xff 0x41 0x00 0x42 (NUL even in the middle) → base64 "AP9BAEI="
	# NB: getfattr --only-values ignores -e base64 and returns raw bytes (NUL gets dropped by bash
	#     command substitution), so do not use --only-values; pick up the "user.bin=0s<base64>" line.
	# Confirm the round-trip with multiple keys + a NUL/high-byte mix.
	# NB: an empty-value xattr (setfattr -v '0s') is not enumerated/fetched stably by getfattr/the OS
	#     (even on plain ext4 it does not appear in `getfattr -d` and `-n` returns ENODATA), so it is
	#     not tested.
	local b64="AP9BAEI="  # 0x00 0xff 0x41 0x00 0x42 (NUL even in the middle)
	setfattr -n user.bin -v "0s$b64" "$f" || { fail "setfattr binary"; return; }
	# user.txt = also keep a normal text value to confirm there is no mix-up
	setfattr -n user.txt -v "plain" "$f" || { fail "setfattr txt"; return; }
	local got=$(getfattr -e base64 -n user.bin "$f" 2>/dev/null | grep '^user.bin=' | cut -d= -f2-)
	assert_eq "0s$b64" "$got" "binary xattr (NUL+high byte) round-trip" || return
	local txtv=$(getfattr --only-values -n user.txt "$f" 2>/dev/null)
	assert_eq "plain" "$txtv" "text xattr alongside binary" || return
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
	# df calls StatFS (Api.GetStatFs). It must not crash + capacity/free must be positive.
	# total>0 / avail>=0 hold whether measured (plperlu functions present via mkfs --statfs) or nominal fallback.
	df "$MOUNT_ROOT" > /dev/null || { fail "df"; return; }
	local total avail
	read -r total avail < <(df -B1 --output=size,avail "$MOUNT_ROOT" 2>/dev/null | tail -1)
	[[ "$total" =~ ^[0-9]+$ ]] || { fail "statfs total not numeric: '$total'"; return; }
	[[ "$avail" =~ ^[0-9]+$ ]] || { fail "statfs avail not numeric: '$avail'"; return; }
	[ "$total" -gt 0 ] || { fail "statfs total not positive: $total"; return; }
	[ "$avail" -le "$total" ] || { fail "statfs avail > total: $avail > $total"; return; }
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

test_timestamp_utc_roundtrip() {
	# The mtime/ctime the FS sets must be near the current time. The naive TIMESTAMP columns in the database
	# are stored as UTC and the reading side interprets them as UTC by convention
	# (docs/design/database.md) - if either becomes local time the value is off by the host's UTC offset
	# (9 hours under JST), and that is what this detects.
	# It creates 20 dummies to push it out of the InodeCache, so in the variant with a smaller
	# cache_max_entries (CACHE_MAX_ENTRIES=8 in docker run.sh) the read-back-from-database path is exercised too.
	local f="$TEST_ROOT/t82_tz.txt"
	local before=$(date +%s)
	echo tz > "$f"
	local i=0
	while [ $i -lt 20 ]; do
		echo pad > "$TEST_ROOT/t82_pad_$i.txt"
		i=$((i+1))
	done
	local m=$(stat -c '%Y' "$f")
	local c=$(stat -c '%Z' "$f")
	local after=$(date +%s)
	local slack=300
	if [ "$m" -lt $((before - slack)) ] || [ "$m" -gt $((after + slack)) ]; then
		fail "mtime $m out of [$((before - slack)), $((after + slack))] - the timestamp may be a UTC/local mix-up"
		return
	fi
	if [ "$c" -lt $((before - slack)) ] || [ "$c" -gt $((after + slack)) ]; then
		fail "ctime $c out of [$((before - slack)), $((after + slack))] - the timestamp may be a UTC/local mix-up"
		return
	fi
	# The explicit-setting (utimensat) path. The DateTime C# hands to the database must be UTC naive as well
	# (handing it over with Kind=Utc makes Npgsql send it as timestamptz and PG cast it with the session TZ).
	local want=$(date -d '2026-01-02 03:04:05' +%s)
	touch -d '2026-01-02 03:04:05' "$f" || { fail "touch -d"; return; }
	i=0
	while [ $i -lt 20 ]; do
		echo pad > "$TEST_ROOT/t82_pad2_$i.txt"
		i=$((i+1))
	done
	local m2=$(stat -c '%Y' "$f")
	if [ "$m2" != "$want" ]; then
		fail "explicit mtime $m2 != $want - the timestamp on the utimensat path shifts between storing and reading"
		return
	fi
	pass
}

test_sparse_du_blocks() {
	# st_blocks (= the value du looks at) must come from "the number of bytes actually occupied".
	# Deriving it from st_size would report hundreds of times the real thing for a sparse file
	# (docs/design/database.md, on occupied bytes and st_blocks). The default chunk size is 1MiB.
	local f="$TEST_ROOT/t84_sparse.img"
	truncate -s 67108864 "$f" || { fail "truncate -s 64MiB"; return; }
	local app=$(du -B1 --apparent-size "$f" | cut -f1)
	local occ=$(du -B1 "$f" | cut -f1)
	if [ "$app" -lt 67108864 ]; then
		fail "the apparent size is $app (should be at least 64MiB)"
		return
	fi
	if [ "$occ" -ge 1048576 ]; then
		fail "du of a file that is nothing but holes is $occ (should occupy 0 = it is coming from st_size)"
		return
	fi
	# Writing 4KiB at the end grows it by exactly one chunk at that position (the holes are not filled in)
	dd if=/dev/urandom of="$f" bs=4096 count=1 seek=16383 conv=notrunc status=none || { fail "dd seek write"; return; }
	occ=$(du -B1 "$f" | cut -f1)
	if [ "$occ" -lt 4096 ]; then
		fail "du after the write is $occ (should be at least 4KiB)"
		return
	fi
	if [ "$occ" -ge $((app / 4)) ]; then
		fail "du after the write is $occ (has it filled the holes in?)"
		return
	fi
	# A hole reads as zeros
	local nonzero=$(dd if="$f" bs=1M count=1 skip=32 status=none | tr -d '\0' | wc -c)
	if [ "$nonzero" != "0" ]; then
		fail "a hole does not read as zeros ($nonzero non-zero byte(s))"
		return
	fi
	# For a normal file du is about the size
	local g="$TEST_ROOT/t84_dense.bin"
	head -c 1048576 /dev/urandom > "$g"
	local dense=$(du -B1 "$g" | cut -f1)
	if [ "$dense" -lt 1048576 ]; then
		fail "du of a normal file is $dense (should be at least 1MiB)"
		return
	fi
	pass
}

test_partial_chunk_overwrite() {
	# Overwriting only part of a chunk must not corrupt the bytes outside it.
	# With write-back the chunk is assembled in memory and the payload is replaced wholesale, so if the path
	# that "reads the existing chunk out of the database and uses it as the base" (the seed) breaks, the range
	# that was not overwritten goes to zero = this fails
	# (docs/design/runtime-control-plane.md, the data write-back section). The default chunk size is 1MiB.
	local f="$TEST_ROOT/t85_partial.bin"
	# Fill 3 MiB with 'A' (spanning chunks 0/1/2)
	tr '\0' 'A' < /dev/zero | head -c 3145728 > "$f" || { fail "the initial write"; return; }
	sync
	# Replace only 4KiB in the middle of chunk 1 with 'B' (reopening the fd = creating a state where it is not in the cache)
	tr '\0' 'B' < /dev/zero | head -c 4096 | dd of="$f" bs=4096 seek=320 conv=notrunc status=none || { fail "the partial overwrite"; return; }
	sync
	local size=$(stat -c %s "$f")
	assert_eq "3145728" "$size" "the size after the overwrite" || return
	# The replaced 4KiB is 'B'
	local b=$(dd if="$f" bs=4096 skip=320 count=1 status=none | tr -d 'B' | wc -c)
	if [ "$b" != "0" ]; then
		fail "the overwritten 4KiB is not filled with 'B' ($b byte(s) left over)"
		return
	fi
	# Just before and just after it is still 'A' (= the range that was not overwritten is intact)
	local before=$(dd if="$f" bs=4096 skip=319 count=1 status=none | tr -d 'A' | wc -c)
	local after=$(dd if="$f" bs=4096 skip=321 count=1 status=none | tr -d 'A' | wc -c)
	if [ "$before" != "0" ] || [ "$after" != "0" ]; then
		fail "the range that was not overwritten got corrupted ($before before / $after after byte(s) are not 'A')"
		return
	fi
	# Chunks 0 and 2 are untouched as well
	local c0=$(dd if="$f" bs=1M skip=0 count=1 status=none | tr -d 'A' | wc -c)
	local c2=$(dd if="$f" bs=1M skip=2 count=1 status=none | tr -d 'A' | wc -c)
	if [ "$c0" != "0" ] || [ "$c2" != "0" ]; then
		fail "a neighbouring chunk got corrupted (chunk0 $c0 / chunk2 $c2 byte(s) are not 'A')"
		return
	fi
	pass
}

test_append_after_close() {
	# Appending to a file that was closed once. With write-back the delta of the occupied bytes is computed
	# from "the payload length in the database", so an append across a close must not double-count or lose any.
	local f="$TEST_ROOT/t86_append.txt"
	printf 'first\n' > "$f"
	sync
	printf 'second\n' >> "$f"
	sync
	printf 'third\n' >> "$f"
	sync
	assert_eq "first
second
third" "$(cat "$f")" "the contents after the append" || return
	assert_eq "19" "$(stat -c %s "$f")" "the size after the append" || return
	# The occupied bytes match the logical size (it is under one chunk, so du is rounded to the block size)
	local occ=$(du -B1 "$f" | cut -f1)
	if [ "$occ" -lt 19 ]; then
		fail "du after the append is $occ (should be at least 19 bytes = the occupancy is under-counted)"
		return
	fi
	if [ "$occ" -gt 1048576 ]; then
		fail "du after the append is $occ (over 1MiB = the occupancy is double-counted)"
		return
	fi
	pass
}

test_full_chunk_overwrite_du() {
	# Overwriting an existing chunk **across the whole chunk** must not corrupt the contents, the size or the
	# occupied bytes.
	# Note: the occupied bytes are double-counted only on the path that overwrites a whole chunk that is
	# **not in the cache** (while it is in the cache, the clean->dirty promotion carries the length in the
	# database over). Reaching that path needs a remount, so that case is covered by
	# test_full_chunk_overwrite_du_after_remount in tests/linux/writeback.sh. This one looks at consistency within a single mount.
	local f="$TEST_ROOT/t89_overwrite.bin"
	head -c 2097152 /dev/urandom > "$f" || { fail "the initial write"; return; }
	sync
	local first=$(du -B1 "$f" | cut -f1)
	if [ "$first" -lt 2097152 ]; then
		fail "the initial du is $first (should be at least 2MiB)"
		return
	fi
	# Overwrite the whole thing at the same size (no truncate; two 1MiB writes aligned to the chunk boundaries)
	head -c 2097152 /dev/urandom > "$f.new"
	dd if="$f.new" of="$f" bs=1M count=2 conv=notrunc,fsync status=none || { fail "the whole-range overwrite"; return; }
	sync
	assert_eq "2097152" "$(stat -c %s "$f")" "the size after the overwrite" || return
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

test_write_read_without_sync() {
	# It must be readable back without a sync in between (read-after-write). With write-back the unflushed
	# content only exists in the cache, so if the read path cannot see the dirty data it returns stale content or zeros.
	local f="$TEST_ROOT/t87_nosync.bin"
	head -c 262144 /dev/urandom > "$f.src"
	cp "$f.src" "$f" || { fail "cp"; return; }
	# Read it straight away without syncing
	if ! cmp -s "$f.src" "$f"; then
		fail "the read-back before the sync does not match (the dirty data is not visible from the read path)"
		return
	fi
	# Append and read it straight away
	head -c 4096 /dev/urandom > "$f.tail"
	cat "$f.tail" >> "$f"
	cat "$f.src" "$f.tail" > "$f.expected"
	if ! cmp -s "$f.expected" "$f"; then
		fail "the read-back right after the append does not match"
		return
	fi
	pass
}

test_truncate_discards_unflushed() {
	# When it is truncated to 0 while still unflushed, the discarded dirty data must not come back later.
	local f="$TEST_ROOT/t88_trunc.bin"
	head -c 524288 /dev/urandom > "$f"
	# Cut it to 0 without syncing
	: > "$f"
	sync
	assert_eq "0" "$(stat -c %s "$f")" "the size after the truncate" || return
	local occ=$(du -B1 "$f" | cut -f1)
	if [ "$occ" -gt 4096 ]; then
		fail "du after the truncate is $occ (the dirty data that should have been discarded was written back)"
		return
	fi
	# It can be written again
	printf 'after-truncate\n' > "$f"
	sync
	assert_eq "after-truncate" "$(cat "$f")" "rewriting after the truncate" || return
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
# pgfs_inode, then confirm that stat-ing that inode shows the names of the kernel's
# overflowuid / overflowgid (/proc/sys/kernel/overflow*, usually 65534) (nobody / nogroup on
# Debian-like systems, nobody / nobody on RHEL-like ones) (mount.fallback_* was removed in v0.2.1).
# The operation goes through psql on the database host (pgsql_server); the source-built psql is
# used explicitly because the distro psql can fail on a libpq version mismatch.

# **Which FS to look at** is decided from environment variables, then the settings file (previously it was
# hardcoded to DB `pgfs` / schema `pgfs`, so mounting another DB / schema looked at a different FS and failed).
#   PGFS_SCHEMA / PGFS_PREFIX / PGFS_DB   explicit (the docker / race_multinode paths use these)
#   PGFS_SETTING_FILE                     the settings file used for the mount (default $HOME/pgfs_test.toml)
E2E_SETTING_FILE="${PGFS_SETTING_FILE:-$HOME/pgfs_test.toml}"
toml_get() { grep -E "^$1 *=" "$E2E_SETTING_FILE" 2>/dev/null | head -1 | sed -e "s/^$1 *= *//" -e 's/^"//' -e 's/"$//'; }
E2E_SCHEMA="${PGFS_SCHEMA:-$(toml_get schema)}"
E2E_PREFIX="${PGFS_PREFIX:-$(toml_get prefix)}"
E2E_DB="${PGFS_DB:-$(toml_get connection | tr ';' '\n' | grep -i '^Database=' | head -1 | cut -d= -f2-)}"
[ -n "$E2E_SCHEMA" ] || E2E_SCHEMA=public
[ -n "$E2E_PREFIX" ] || E2E_PREFIX=pgfs_
E2E_INODE="${E2E_SCHEMA}.${E2E_PREFIX}inode"

pg_exec() {
	# Stream SQL on stdin. -tA is tuples-only + unaligned output, returning one ${name}\n per row.
	# If `PGFS_TEST_PG_EXEC` is set it is used instead (for the docker Citus path in race_multinode.sh).
	# The default goes through the source-built psql on pgsql_server (the distro psql can fail on a
	# libpq version mismatch, so this one is used explicitly).
	if [ -n "${PGFS_TEST_PG_EXEC:-}" ]; then
		eval "$PGFS_TEST_PG_EXEC"
		return
	fi
	# Do not fire at a default DB when the DB name is unknown (it would rewrite a different FS).
	if [ -z "$E2E_DB" ]; then
		return 1
	fi
	ssh -o BatchMode=yes -o ConnectTimeout=5 -o LogLevel=ERROR pgsql_server \
		"LD_LIBRARY_PATH=/usr/local/pgsql/lib PGPASSWORD=pgfs /usr/local/pgsql/bin/psql -h localhost -U pgfs -d $E2E_DB -tA -q"
}

test_fallback_uname_gname() {
	# Skip if the DB is unreachable (e.g. CI without an external DB).
	if ! echo "SELECT 1" | pg_exec >/dev/null 2>&1; then
		skip "psql not reachable (the DB name comes from PGFS_DB or Database= in $E2E_SETTING_FILE; currently '${E2E_DB:-unknown}')"
		return
	fi
	# Is the FS being looked at the same as the mount: if the table is missing, it is looking at a different DB / schema
	if [ "$(echo "SELECT to_regclass('$E2E_INODE') IS NOT NULL" | pg_exec | head -1)" != "t" ]; then
		fail "$E2E_INODE does not exist (check PGFS_SCHEMA / PGFS_PREFIX or $E2E_SETTING_FILE)"
		return
	fi
	# Look up the inode id of the test directory (directly under the mount root, parent_id=0).
	local test_id
	test_id=$(echo "SELECT id FROM $E2E_INODE WHERE name='test' AND parent_id=0" | pg_exec | head -1)
	if ! [[ "$test_id" =~ ^[0-9]+$ ]]; then
		fail "could not look up test/ dir id: '$test_id'"
		return
	fi
	# Directly INSERT an inode with a user/group name that does not exist on the OS.
	local ghost="t100_ghost_$$"
	echo "INSERT INTO $E2E_INODE (parent_id, name, uname, gname, st_mode, st_nlink, st_size, is_junction, xattr_names, xattr_values, created_by, updated_by) VALUES ($test_id, '$ghost', '__no_such_user_xyz__', '__no_such_group_xyz__', 33188, 1, 0, false, '{}'::text[], '{}'::bytea[], 'pgfs', 'pgfs')" | pg_exec >/dev/null
	# stat -> GetByPath -> child (cache miss) -> DB fetch -> uname resolution fails -> fallback.
	# Note: the childrenByParent cache is stale, but a direct-path stat goes through byPath/byId,
	#       so the freshly inserted row is still visible (ListChildren is not called).
	local owner=$(stat -c '%U' "$TEST_ROOT/$ghost" 2>/dev/null)
	local group=$(stat -c '%G' "$TEST_ROOT/$ghost" 2>/dev/null)
	# Cleanup: always DELETE (even on failure).
	echo "DELETE FROM $E2E_INODE WHERE parent_id=$test_id AND name='$ghost'" | pg_exec >/dev/null
	local want_u want_g
	want_u=$(getent passwd "$(cat /proc/sys/kernel/overflowuid)" | cut -d: -f1)
	want_g=$(getent group "$(cat /proc/sys/kernel/overflowgid)" | cut -d: -f1)
	assert_eq "$want_u" "$owner" "unknown uname -> overflowuid" || return
	assert_eq "$want_g" "$group" "unknown gname -> overflowgid" || return
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
run test_rename_replace_existing

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
# The net that pins down the premise of handle-context stage C (docs/design/handle-context.md)
run test_open_unlink_read_still_works

# The data_id lifecycle (hardlink sharing / st_ino stability)
run test_truncate_keeps_hardlink_shared
run test_truncate_zero_updates_all_links
run test_empty_file_hardlink_shares
run test_ino_namespace_split
run test_ino_stable_across_write

# extended attributes
run test_xattr_set_get
run test_xattr_list
run test_xattr_remove
run test_xattr_overwrite
run test_xattr_binary

# POSIX ACL
run test_posix_acl_named_user

# metadata
run test_statfs
run test_utime_explicit
run test_utime_now
run test_timestamp_utc_roundtrip
run test_sparse_du_blocks

# Consistency of chunk writes (the same result in both the write-back and the write-through mode)
run test_partial_chunk_overwrite
run test_full_chunk_overwrite_du
run test_append_after_close
run test_write_read_without_sync
run test_truncate_discards_unflushed

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
