#!/usr/bin/env bash
# Contract tests around pgfs start-up (how mount options are treated / the liveness marker / the fallback of owner resolution)
#
# Unlike e2e.sh, **this script mounts and remounts on its own** (because it looks at start-up behaviour).
# The contracts checked here:
#   - `-o max_write=N` is **not forwarded to libfuse** but routed into pgfs's `mount.max_write`
#     (forwarding it makes libfuse take the whole mount down with unknown option)
#   - mount(8) options that **cannot be applied** (`noexec` / `sync` / `dirsync` ...) **warn instead of staying silent**
#     (dropping them silently makes "the execution restriction I specified is not in effect" impossible to notice)
#   - `started (pid N)` is **the real daemon's pid** (not the pid of the parent that daemonizes)
#   - a uname / gname that cannot be resolved falls back (the default nobody/nogroup -> 65534)
#
# Usage:
#   PGFS_PSQL=/usr/local/pgsql/bin/psql bash tests/linux/startup.sh
#
# Environment variables:
#   PGFS_SETTING_FILE / MOUNT_ROOT / PGFS_BIN / TEST_FILTER
#   PGFS_PSQL (or PSQL)  used by the tests that touch the database directly. Those tests are skipped when it cannot be found
#
# Exit codes: 0 = everything passed / 1 = something failed / 2 = a prerequisite is missing

set -u

SETTING_FILE="${PGFS_SETTING_FILE:-$HOME/pgfs_test.toml}"
MOUNT_ROOT="${MOUNT_ROOT:-$HOME/mnt/pgfs}"
BIN="${PGFS_BIN:-./bin/Debug}"
PSQL_BIN="${PGFS_PSQL:-${PSQL:-psql}}"
TEST_ROOT="$MOUNT_ROOT/sutest"
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

# ===== connection information (for the tests that look at the database directly) =====

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
db_ready() { [ -n "$DB_NAME" ] && [ -n "$DB_USER" ] && command -v "$PSQL_BIN" >/dev/null 2>&1 && [ "$(q 'select 1')" = "1" ]; }

# ===== mount operations =====

mount_pid() { pgrep -x mount.pgfs | head -1; }

unmount_clean() {
	if mountpoint -q "$MOUNT_ROOT" 2>/dev/null; then
		fusermount3 -u "$MOUNT_ROOT" 2>/dev/null
	fi
	local i=0
	while [ $i -lt 100 ]; do
		[ -n "$(mount_pid)" ] || return 0
		sleep 0.1
		i=$((i+1))
	done
	return 0
}

# Mounts and picks up **the parent process's output** (the banner / the warnings / the started line).
# Both the settings warnings and `started (pid ...)` are emitted by the parent before the fork, so everything is captured here.
mount_capture() {
	mkdir -p "$MOUNT_ROOT" 2>/dev/null
	"$BIN/mount.pgfs" --setting-file "$SETTING_FILE" "$@" 2>&1
}

wait_mounted() {
	local i=0
	while [ $i -lt 100 ]; do
		mountpoint -q "$MOUNT_ROOT" 2>/dev/null && return 0
		sleep 0.1
		i=$((i+1))
	done
	return 1
}

run() {
	local name="$1"
	if [ -n "$FILTER" ] && [[ "$name" != *"$FILTER"* ]]; then
		return 0
	fi
	TOTAL=$((TOTAL+1))
	CURRENT="$name"
	unmount_clean
	"$name"
	unmount_clean
}

# ===== tests =====

test_dash_o_max_write_maps_to_field() {
	# `-o max_write=N` must not be forwarded to libfuse (libfuse does not accept it as a mount option,
	# and `fuse: unknown option(s)` -> a failing fuse_new **takes the whole mount down**).
	# It must flow into pgfs's mount.max_write and the mount must come up.
	local out=$(mount_capture -o max_write=65536)
	if ! wait_mounted; then
		fail "cannot mount with -o max_write=65536 (suspect it is being forwarded to libfuse): $(echo "$out" | grep -iE 'unknown option|fuse_new' | head -1)"
		return
	fi
	if ! echo "$out" | grep -q "mount.max_write = 65536"; then
		fail "it did not flow into mount.max_write (it does not appear on the param line)"
		return
	fi
	pass
}

test_dash_o_unapplied_options_warn() {
	# The ones that cannot be applied **must not be dropped silently**. Conversely the ones that match the
	# default, or that fusermount3 always adds (nosuid / nodev / relatime), must not warn (they would be noise).
	local out=$(mount_capture -o noexec,sync,dirsync,nosuid,nodev,relatime)
	if ! wait_mounted; then
		fail "cannot mount"
		return
	fi
	local missing=""
	for opt in noexec sync dirsync; do
		echo "$out" | grep -q "\-o '$opt' is not applied in pgfs" || missing="$missing $opt"
	done
	if [ -n "$missing" ]; then
		fail "some options produce no warning:$missing"
		return
	fi
	for opt in nosuid nodev relatime; do
		if echo "$out" | grep -q "\-o '$opt' is not applied in pgfs"; then
			fail "'$opt' warns although it should not"
			return
		fi
	done
	pass
}

test_dash_o_fuse_breaking_options_are_not_forwarded() {
	# The keys that **break the mount** when handed to libfuse must not be forwarded (confirmed on real hardware).
	#   max_readahead : fuse_new fails with `fuse: unknown option(s)` = the same as max_write
	#   max_read      : **worse**. The mount is established and success is reported to the parent, and
	#                   immediately afterwards the session ends and the process disappears with exit 0
	#                   (regardless of the value).
	#                   = seen from fstab it is "mount succeeded yet nothing is mounted"
	# So **"it mounted" is not enough**: it also waits a little and checks that it is **still alive**.
	local out=$(mount_capture -o max_read=65536,max_readahead=131072)
	if ! wait_mounted; then
		fail "cannot mount (suspect it is being forwarded to libfuse): $(echo "$out" | grep -iE 'unknown option|fuse_new' | head -1)"
		return
	fi
	sleep 2
	if ! mountpoint -q "$MOUNT_ROOT" 2>/dev/null; then
		fail "the session died right after the mount (suspect max_read is being forwarded)"
		return
	fi
	local missing=""
	for opt in max_read max_readahead; do
		echo "$out" | grep -q "\-o '$opt' is not applied in pgfs" || missing="$missing $opt"
	done
	if [ -n "$missing" ]; then
		fail "some options produce no warning:$missing"
		return
	fi
	pass
}

test_started_pid_is_the_daemon() {
	# `started (pid N)` must be the pid of **the real daemon** that survives the daemonization.
	# Printing the parent's pid makes it vanish the moment the mount comes up, so watch / stop scripts always miss.
	local out=$(mount_capture)
	if ! wait_mounted; then
		fail "cannot mount"
		return
	fi
	local shown=$(echo "$out" | grep -o 'started (pid [0-9]*)' | grep -o '[0-9]*')
	if [ -z "$shown" ]; then
		fail "started (pid N) is not printed"
		return
	fi
	if ! kill -0 "$shown" 2>/dev/null; then
		fail "the printed pid $shown does not exist (it is printing the parent from before the fork)"
		return
	fi
	local actual=$(mount_pid)
	if [ "$shown" != "$actual" ]; then
		fail "the printed pid $shown does not match the real daemon $actual"
		return
	fi
	pass
}

test_unknown_owner_falls_back() {
	# A uname / gname that cannot be resolved falls back (the default nobody/nogroup -> 65534 when even that fails).
	# It also confirms that getpw*_r / getgr*_r **do not mistake "not found" (rc=0 with result=NULL) for an
	# error** (mistaking it turns the owner into uid 0 = root).
	if ! db_ready; then
		skip "cannot check the database with psql"
		return
	fi
	mount_capture >/dev/null
	wait_mounted || { fail "cannot mount"; return; }
	rm -rf "$TEST_ROOT" 2>/dev/null
	mkdir -p "$TEST_ROOT" || { fail "mkdir"; return; }
	echo x > "$TEST_ROOT/owner.txt" || { fail "create"; return; }
	local parent=$(q "select id from ${SCHEMA}.${PREFIX}inode where name = 'sutest' order by id desc limit 1")
	if [ -z "$parent" ]; then
		fail "cannot look up the id of the test directory"
		return
	fi
	q "update ${SCHEMA}.${PREFIX}inode set uname = 'zz_no_such_user', gname = 'zz_no_such_group' where parent_id = $parent and name = 'owner.txt'" >/dev/null
	# Remount to empty the cache
	unmount_clean
	mount_capture >/dev/null
	wait_mounted || { fail "cannot remount"; return; }
	local ids=$(stat -c '%u %g' "$TEST_ROOT/owner.txt" 2>/dev/null)
	rm -rf "$TEST_ROOT" 2>/dev/null
	if [ "$ids" != "65534 65534" ]; then
		fail "an unresolved owner did not fall back (uid gid = '$ids', expected 65534 65534)"
		return
	fi
	pass
}

test_allow_other_without_default_permissions_warns() {
	# ★ **`-o allow_other` alone (without default_permissions) gives a warning.**
	#   pgfs does not decide access by itself (it leaves that to the kernel through default_permissions), so without
	#   it **the mode is not enforced and every local user can read and write every file**. The default behaviour is
	#   not changed; it only warns. **No warning when both are given** is checked too (a warning that is always there is
	#   noise that buries the real ones). The warning comes from the parent before the fork (reaching the terminal even
	#   when daemonized is the requirement), so whether the mount succeeds does not matter here.
	local bad; bad=$(mount_capture -o allow_other)
	unmount_clean
	local good; good=$(mount_capture -o allow_other,default_permissions)
	unmount_clean
	if ! echo "$bad" | grep -q "allow_other was given without default_permissions"; then
		fail "-o allow_other alone gives no warning (it is not in the parent's output)"
		return
	fi
	if echo "$good" | grep -q "allow_other was given without default_permissions"; then
		fail "-o allow_other,default_permissions gives the warning as well"
		return
	fi
	pass
}

# Builds, from the kv connection string of the settings, a libpq connection URI that points at the same place
# (sslmode is passed in the query too).
url_from_settings() {
	local pw; pw=$(kv Password)
	local ssl; ssl=$(kv "SSL Mode")
	[ -n "$ssl" ] || ssl=$(kv SslMode)
	local url="postgresql://${DB_USER}:${pw}@${DB_HOST}:${DB_PORT}/${DB_NAME}"
	if [ -n "$ssl" ]; then url="${url}?sslmode=$(echo "$ssl" | tr 'A-Z' 'a-z')"; fi
	echo "$url"
}

test_url_connection_mounts() {
	# ★ **A connection string in the URL form (a libpq connection URI) mounts.**
	#   Npgsql does not interpret URIs, so before, both `-c postgresql://...` and a URI in the first column of fstab
	#   **did not even start** with `Format of the initialization string does not conform to specification` (the fstab
	#   examples assumed the URI form). It is checked with `-c` and with the **positional argument** fstab uses.
	#   sslmode is passed in the query, so reading libpq's parameter names is checked at the same time.
	if [ -z "$(kv Password)" ] || [ -z "$DB_USER" ] || [ -z "$DB_NAME" ]; then
		skip "cannot read the user / password / database from the connection string of the settings"
		return
	fi
	local url; url=$(url_from_settings)
	local out; out=$(mount_capture -c "$url")
	if ! wait_mounted; then
		fail "the URI in -c does not mount: $(echo "$out" | grep -iE 'error|format|interpret' | head -1)"
		return
	fi
	if ! ls "$MOUNT_ROOT" >/dev/null 2>&1; then
		fail "the URI in -c mounted but ls fails"
		return
	fi
	unmount_clean
	# The same shape as fstab: `mount.pgfs <source> <target>` (a URI source goes into database.connection).
	mkdir -p "$MOUNT_ROOT" 2>/dev/null
	out=$("$BIN/mount.pgfs" "$url" "$MOUNT_ROOT" --setting-file "$SETTING_FILE" 2>&1)
	if ! wait_mounted; then
		fail "the URI as the positional argument (fstab's first column) does not mount: $(echo "$out" | grep -iE 'error|format|interpret' | head -1)"
		return
	fi
	pass
}

test_url_unknown_parameter_is_rejected_clearly() {
	# ★ **A query parameter of the URI that cannot be interpreted stops the start with a reason, not dropped silently.**
	#   Dropping a misspelt `sslmod=require` and starting leaves **a connection that was meant to be encrypted in plain
	#   text**. **The kind of failure is checked too** - it does not start **and** it says which parameter is wrong
	#   (a message that points at nothing, like Npgsql's "Format of the initialization string", does not count).
	if [ -z "$(kv Password)" ] || [ -z "$DB_USER" ] || [ -z "$DB_NAME" ]; then
		skip "cannot read the user / password / database from the connection string of the settings"
		return
	fi
	local pw; pw=$(kv Password)
	local url="postgresql://${DB_USER}:${pw}@${DB_HOST}:${DB_PORT}/${DB_NAME}?sslmod=require"
	local out; out=$(mount_capture -c "$url")
	if wait_mounted; then
		fail "an uninterpretable parameter (sslmod) was dropped silently and it mounted"
		return
	fi
	if ! echo "$out" | grep -q "The connection URI parameter 'sslmod' cannot be interpreted"; then
		fail "it does not say which parameter is wrong: $(echo "$out" | grep -iE 'error|format' | head -1)"
		return
	fi
	pass
}

test_url_connection_password_is_masked() {
	# ★ **The password is hidden in the URL form too.**
	#   Before, only the kv form (`Password=...`) was hidden, and the password of `postgresql://user:secret@host/db`
	#   came out **in plain text** on the param line of the startup log. With a URI in the first column of fstab it
	#   leaks as it is. **It checks not only what is absent but that the hidden form is present** (so that a vanished
	#   param line does not pass as well).
	local pw; pw=$(kv Password)
	if [ -z "$pw" ] || [ -z "$DB_USER" ] || [ -z "$DB_NAME" ]; then
		skip "cannot read the user / password / database from the connection string of the settings"
		return
	fi
	local url="postgresql://${DB_USER}:${pw}@${DB_HOST}:${DB_PORT}/${DB_NAME}"
	local out; out=$(mount_capture -c "$url")
	unmount_clean
	local line; line=$(echo "$out" | grep "database.connection = " | head -1)
	if [ -z "$line" ]; then
		fail "there is no param line for database.connection in the startup output (cannot tell whether it was hidden)"
		return
	fi
	if echo "$line" | grep -qF ":${pw}@"; then
		fail "the password of the URL form comes out in plain text: $line"
		return
	fi
	if ! echo "$line" | grep -qF ":***@"; then
		fail "it is not in the hidden form (:***@): $line"
		return
	fi
	pass
}

test_sample_toml_is_loadable() {
	# * **The bundled sample (pgfs.toml.example) must be readable exactly as it is written.**
	#
	#   The sample used to be written in the table form `database.connection.host = "..."`, but
	#   **a settings file only interprets two levels, "scope.key = value"**, so the connection string
	#   turned into `"Tomlyn.Model.TomlTable"` and it fell over with
	#   `Format of the initialization string does not conform to specification`.
	#   **Anyone who copied the sample hit it on their very first start-up** (measured and fixed).
	#
	#   Here **only the sample's connection is swapped for this environment and it actually mounts**.
	#   If a three-level key comes back into the sample, the warning below fires and this fails.
	local sample="pgfs.toml.example"
	if [ ! -f "$sample" ]; then
		skip "the sample was not found ($sample. Run this from the repository root)"
		return
	fi
	local conn; conn=$(grep -E '^connection' "$SETTING_FILE" 2>/dev/null | head -1 | sed 's/^connection *= *//; s/^"//; s/"$//')
	local schema; schema=$(grep -E '^schema' "$SETTING_FILE" 2>/dev/null | head -1 | sed 's/^schema *= *//; s/^"//; s/"$//')
	if [ -z "$conn" ] || [ -z "$schema" ]; then
		skip "cannot read connection / schema from $SETTING_FILE"
		return
	fi
	unmount_clean
	local tmp="${TMPDIR:-/tmp}/pgfs_sample.$$.toml"
	# Swap only the sample's connection line and schema line (every other line stays as the sample has it = the way it is written is what is under test).
	sed -e "s|^database.connection = .*|database.connection = \"$conn\"|" \
	    -e "s|^database.schema = .*|database.schema = \"$schema\"|" \
	    "$sample" > "$tmp"
	# mount_point is commented out in the sample, so it is appended.
	echo "mount.mount_point = \"$MOUNT_ROOT\"" >> "$tmp"

	mkdir -p "$MOUNT_ROOT" 2>/dev/null
	local out; out=$("$BIN/mount.pgfs" --setting-file "$tmp" 2>&1)
	local mounted=0
	wait_mounted && mounted=1
	unmount_clean
	rm -f "$tmp" 2>/dev/null

	if echo "$out" | grep -q "is written as a table"; then
		fail "a three-level key has crept into the sample ($(echo "$out" | grep -oE '`[^`]*` in the setting file' | head -1))"
		return
	fi
	if echo "$out" | grep -q "initialization string"; then
		fail "the implementation cannot interpret the sample's connection (it turned into something else)"
		return
	fi
	if [ "$mounted" != "1" ]; then
		fail "cannot mount with the way the sample is written: $(echo "$out" | grep -iE 'error|Exception' | head -1)"
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

echo "${BLUE}=== pgfs startup / mount option tests ===${NC}"
echo "setting : $SETTING_FILE"
echo "mount   : $MOUNT_ROOT"
echo ""

run test_dash_o_max_write_maps_to_field
run test_dash_o_unapplied_options_warn
run test_dash_o_fuse_breaking_options_are_not_forwarded
run test_started_pid_is_the_daemon
run test_unknown_owner_falls_back
run test_allow_other_without_default_permissions_warns
run test_url_connection_mounts
run test_url_unknown_parameter_is_rejected_clearly
run test_url_connection_password_is_masked
run test_sample_toml_is_loadable

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
