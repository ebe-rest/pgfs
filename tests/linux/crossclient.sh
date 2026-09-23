#!/usr/bin/env bash
# pgfs cross-client tests (the same DB-FS under two mounts)
#
# The Linux counterpart of tests/windows/crossclient.ps1 on the Windows side.
# **This script establishes both mounts itself** (A = MOUNT_ROOT / B = MOUNT_ROOT2).
# Both are started with `--notify` - cross-client visibility depends entirely on
# `database.notify_enabled`, and with the default (false) another mount's changes
# stay invisible indefinitely (the same note as in docs/Assign.md).
#
# The contracts checked here:
#   - a create / delete / replacement rename becomes visible from the other mount
#   - **the st_size of a hardlink sibling propagates to the other mount too**
#     (the regression guard for the fix that puts the distributed sibling ids into the notification.
#      docs/design/data-id-lifecycle.md)
#   - O_EXCL create exclusion works cross-client (the default write_through)
#
# Usage:
#   PGFS_PSQL=/usr/local/pgsql/bin/psql bash tests/linux/crossclient.sh
#
# Environment variables:
#   PGFS_SETTING_FILE   the settings file handed to mount.pgfs (default $HOME/pgfs_test.toml)
#   MOUNT_ROOT          mount A (default $HOME/mnt/pgfs)
#   MOUNT_ROOT2         mount B (default $HOME/mnt/pgfs2)
#   PGFS_BIN            the directory holding the binaries (default ./bin/Debug)
#   PGFS_PSQL / PSQL    the psql command (only used by the database-checking tests. Skipped when absent)
#   TEST_FILTER         run only the tests whose name contains this string
#
# Prerequisites: the FS the settings file points at has been mkfs'd and may be destroyed
#       (the tests only touch what is under <mount>/xctest).
#
# Exit codes: 0 = everything passed / 1 = something failed / 2 = a prerequisite is missing

set -u

SETTING_FILE="${PGFS_SETTING_FILE:-$HOME/pgfs_test.toml}"
MOUNT_A="${MOUNT_ROOT:-$HOME/mnt/pgfs}"
MOUNT_B="${MOUNT_ROOT2:-$HOME/mnt/pgfs2}"
BIN="${PGFS_BIN:-./bin/Debug}"
PSQL_BIN="${PGFS_PSQL:-${PSQL:-psql}}"
FILTER="${TEST_FILTER:-}"
A="$MOUNT_A/xctest"
B="$MOUNT_B/xctest"

# The upper bound (in seconds) for something to reach the other mount. The notification is immediate, but
# this absorbs the jitter of the database and the scheduler. **Asserting without waiting misjudges "slow" as "broken".**
WAIT_MAX="${PGFS_XC_WAIT:-10}"

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

# ===== mount operations =====

# Look it up by process name (`pgrep -f` also matches the calling shell, which contains the same command line).
mount_count() { pgrep -x mount.pgfs | wc -l; }

umount_one() {
	local mp="$1"
	if mountpoint -q "$mp" 2>/dev/null; then
		fusermount3 -u "$mp" 2>/dev/null
	fi
}

umount_all() {
	umount_one "$MOUNT_A"
	umount_one "$MOUNT_B"
	local i=0
	while [ $i -lt 100 ]; do
		[ "$(mount_count)" = "0" ] && return 0
		sleep 0.1
		i=$((i+1))
	done
	return 0
}

# Establish A and B. **notify is mandatory** (with the default off, another mount's changes are invisible).
mount_both() {
	mkdir -p "$MOUNT_A" "$MOUNT_B" 2>/dev/null
	"$BIN/mount.pgfs" --setting-file "$SETTING_FILE" --notify >/dev/null 2>&1
	"$BIN/mount.pgfs" --setting-file "$SETTING_FILE" --mount-point "$MOUNT_B" --notify >/dev/null 2>&1
	local i=0
	while [ $i -lt 100 ]; do
		if mountpoint -q "$MOUNT_A" 2>/dev/null && mountpoint -q "$MOUNT_B" 2>/dev/null; then
			return 0
		fi
		sleep 0.1
		i=$((i+1))
	done
	echo "${RED}could not establish the two mounts${NC}" >&2
	return 1
}

# Re-establish A as **write-back with the time trigger off** and B as write-through.
# **Turning the time trigger off is the crux** - at the default 1000ms the window "the other side touches it
# while dirty data is still held" closes on its own and **the test goes green even on a pre-fix build**
# (the Windows side hit the same false negative through .NET FileStream's buffering).
mount_a_writeback() {
	umount_all
	mkdir -p "$MOUNT_A" "$MOUNT_B" 2>/dev/null
	"$BIN/mount.pgfs" --setting-file "$SETTING_FILE" --write-back --write-back-interval-ms 0 --notify >/dev/null 2>&1
	"$BIN/mount.pgfs" --setting-file "$SETTING_FILE" --mount-point "$MOUNT_B" --notify >/dev/null 2>&1
	local i=0
	while [ $i -lt 100 ]; do
		if mountpoint -q "$MOUNT_A" 2>/dev/null && mountpoint -q "$MOUNT_B" 2>/dev/null; then
			return 0
		fi
		sleep 0.1
		i=$((i+1))
	done
	return 1
}

# Re-establish A and B **without notify**.
# The window "create a child after the parent has been deleted" **closes with notify ON** - B's rmdir
# notification drops A's InodeCache, A re-reads the database and correctly fails. Opening the window
# deterministically needs **A's cache left stale**, so this one test alone re-establishes them with notify off.
mount_both_nonotify() {
	umount_all
	mkdir -p "$MOUNT_A" "$MOUNT_B" 2>/dev/null
	"$BIN/mount.pgfs" --setting-file "$SETTING_FILE" >/dev/null 2>&1
	"$BIN/mount.pgfs" --setting-file "$SETTING_FILE" --mount-point "$MOUNT_B" >/dev/null 2>&1
	local i=0
	while [ $i -lt 100 ]; do
		if mountpoint -q "$MOUNT_A" 2>/dev/null && mountpoint -q "$MOUNT_B" 2>/dev/null; then
			return 0
		fi
		sleep 0.1
		i=$((i+1))
	done
	return 1
}

# ===== checking the notification channel up front =====
#
# **The visibility tests assume the notification channel is connected.** When it is not, the visibility tests
# **fail together**, but the symptom only says "not visible", so **a disconnected notification gets misread as
# a visibility bug** (the Windows side actually hit that shape once).
# It is checked first, and if it is not connected this **fails with that stated plainly**.

PSQL_BIN="${PGFS_PSQL:-${PSQL:-psql}}"
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

# Make a running mount write a stats snapshot (rather than waiting out the heartbeat's 30 seconds).
ping_mounts() { q "select pg_notify('${SCHEMA}_${PREFIX}notify', '{\"s\":\"xcpre000\",\"c\":\"ping\"}')" >/dev/null; }

# Confirm that **both** mounts are in a state where they can send and receive data-change notifications.
#
# * **Do not look at `connected` alone**. The control channel's LISTEN is **always established** regardless of
#   `database.notify_enabled`, so **even a mount with notify OFF shows `connected: true` / `control_listen: true`**
#   (measured: two OFF mounts gave `{"connected": true, "data_enabled": false, "control_listen": true}`).
#   What the visibility tests need is **`data_enabled`**, so both are checked.
# In an environment where psql cannot be found this cannot be checked, so **it is waved through** (the tests still run).
preflight_notify() {
	db_ready || { echo "${YELLOW}NOTE${NC}: psql cannot be found, so the notification channel pre-check is skipped"; return 0; }
	local live="heartbeat_at > (now() AT TIME ZONE 'UTC') - interval '2 minutes'"
	local i=0
	while [ $i -lt 100 ]; do
		ping_mounts
		sleep 0.2
		if [ "$(q "select count(*) from ${SCHEMA}.${PREFIX}mounts where $live and stats->'notify'->>'connected' = 'true' and stats->'notify'->>'data_enabled' = 'true'")" -ge 2 ] 2>/dev/null; then
			return 0
		fi
		i=$((i+1))
	done
	echo "${RED}could not get two mounts able to send and receive data-change notifications${NC}" >&2
	echo "  (running the visibility tests in this state would **misread a disconnected notification as a visibility bug**, so it stops here)" >&2
	q "select mount_id, coalesce(stats->'notify'::text, '(no stats)') from ${SCHEMA}.${PREFIX}mounts where $live" >&2
	return 1
}

# ===== waiting (visibility is observed by polling) =====

# Wait until a predicate becomes true. Returns 1 when it never does.
wait_until() {
	local deadline=$(( $(date +%s) + WAIT_MAX ))
	while [ "$(date +%s)" -lt "$deadline" ]; do
		if eval "$1"; then return 0; fi
		sleep 0.1
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
	"$name"
}

# ===== tests =====

test_xc_create_visible() {
	# A file created on A must be visible from B.
	rm -f "$A/c1.txt" 2>/dev/null
	echo "hello" > "$A/c1.txt" || { fail "creating it on A"; return; }
	if ! wait_until '[ -f "$B/c1.txt" ]'; then
		fail "still not visible from B after waiting ${WAIT_MAX} seconds"
		return
	fi
	local got=$(cat "$B/c1.txt" 2>/dev/null)
	if [ "$got" != "hello" ]; then
		fail "the contents read from B differ ('$got')"
		return
	fi
	pass
}

test_xc_delete_visible() {
	# A file deleted on A must disappear from B as well.
	echo "bye" > "$A/d1.txt" || { fail "creating it on A"; return; }
	wait_until '[ -f "$B/d1.txt" ]' || { fail "the creation is not visible from B (a premise)"; return; }
	rm -f "$A/d1.txt" || { fail "deleting it on A"; return; }
	if ! wait_until '[ ! -e "$B/d1.txt" ]'; then
		fail "still not gone from B after waiting ${WAIT_MAX} seconds"
		return
	fi
	pass
}

test_xc_overwrite_visible() {
	# An overwrite on A must be visible from B (including when the size shrinks).
	echo "0123456789" > "$A/o1.txt" || { fail "creating it on A"; return; }
	wait_until '[ "$(cat "$B/o1.txt" 2>/dev/null)" = "0123456789" ]' || { fail "the initial contents are not visible from B (a premise)"; return; }
	echo "ab" > "$A/o1.txt" || { fail "overwriting it on A"; return; }
	if ! wait_until '[ "$(cat "$B/o1.txt" 2>/dev/null)" = "ab" ]'; then
		fail "B still returns the old contents after waiting ${WAIT_MAX} seconds ('$(cat "$B/o1.txt" 2>/dev/null)')"
		return
	fi
	pass
}

test_xc_rename_replace_visible() {
	# A replacement rename on A (delete the target and move the source) must be visible from B.
	echo "new" > "$A/r_src.txt" || { fail "creating it on A (src)"; return; }
	echo "old" > "$A/r_dst.txt" || { fail "creating it on A (dst)"; return; }
	wait_until '[ "$(cat "$B/r_dst.txt" 2>/dev/null)" = "old" ]' || { fail "dst is not visible from B (a premise)"; return; }
	mv "$A/r_src.txt" "$A/r_dst.txt" || { fail "the replacement rename on A"; return; }
	if ! wait_until '[ "$(cat "$B/r_dst.txt" 2>/dev/null)" = "new" ] && [ ! -e "$B/r_src.txt" ]'; then
		fail "the replacement has not reached B after waiting ${WAIT_MAX} seconds (dst='$(cat "$B/r_dst.txt" 2>/dev/null)' src still there=$([ -e "$B/r_src.txt" ] && echo yes || echo no))"
		return
	fi
	pass
}

test_xc_hardlink_size_propagates() {
	# * The main event here. A hardlink shares its body, so **writing through one must update the other's
	#   st_size on every mount**.
	#   Without putting the distributed sibling ids into the notification, B's InodeCache keeps holding a
	#   stale st_size and **cat returns nothing although the chunks are shared**
	#   (docs/design/data-id-lifecycle.md).
	#   Stat it on B first **to put it into the cache** before writing - the crux; without that B re-reads the
	#   database and goes straight past this defect.
	rm -f "$A/h_a.txt" "$A/h_b.txt" 2>/dev/null
	: > "$A/h_a.txt" || { fail "creating the empty file on A"; return; }
	ln "$A/h_a.txt" "$A/h_b.txt" || { fail "the hardlink on A"; return; }
	wait_until '[ -f "$B/h_b.txt" ]' || { fail "the link is not visible from B (a premise)"; return; }
	# Put "h_b with size 0" into B's cache
	local pre=$(stat -c '%s' "$B/h_b.txt" 2>/dev/null)
	if [ "$pre" != "0" ]; then
		fail "the premise no longer holds (the initial size seen from B is $pre)"
		return
	fi
	echo "shared" > "$A/h_a.txt" || { fail "writing on A"; return; }
	if ! wait_until '[ "$(stat -c "%s" "$B/h_b.txt" 2>/dev/null)" = "7" ]'; then
		fail "B's sibling still has the old st_size after waiting ${WAIT_MAX} seconds ($(stat -c '%s' "$B/h_b.txt" 2>/dev/null))"
		return
	fi
	local got=$(cat "$B/h_b.txt" 2>/dev/null)
	if [ "$got" != "shared" ]; then
		fail "the contents read from B's sibling differ ('$got')"
		return
	fi
	pass
}

test_xc_hardlink_truncate_propagates() {
	# A truncate operates on the body too, so it must reach the sibling on the other mount.
	rm -f "$A/t_a.txt" "$A/t_b.txt" 2>/dev/null
	echo "HELLO" > "$A/t_a.txt" || { fail "creating it on A"; return; }
	ln "$A/t_a.txt" "$A/t_b.txt" || { fail "the hardlink on A"; return; }
	wait_until '[ "$(stat -c "%s" "$B/t_b.txt" 2>/dev/null)" = "6" ]' || { fail "the link's initial size is not visible from B (a premise)"; return; }
	truncate -s 0 "$A/t_a.txt" || { fail "the truncate on A"; return; }
	if ! wait_until '[ "$(stat -c "%s" "$B/t_b.txt" 2>/dev/null)" = "0" ]'; then
		fail "B's sibling still has the old st_size after waiting ${WAIT_MAX} seconds ($(stat -c '%s' "$B/t_b.txt" 2>/dev/null))"
		return
	fi
	pass
}

test_xc_exclusive_create_races() {
	# Creating the same name from A and B at the same time with O_EXCL must let **exactly one** succeed
	# (the default mount.write_back_metadata_exclusive_create = write_through).
	# The loser gets EEXIST. Both succeeding means cross-client exclusion is broken.
	local wins=0
	local rounds=5
	local i=0
	while [ $i -lt $rounds ]; do
		local name="x_$i.lock"
		rm -f "$A/$name" 2>/dev/null
		wait_until '[ ! -e "$B/'"$name"'" ]' || true
		local oa=/tmp/.xc_a.$$ ob=/tmp/.xc_b.$$
		( set -o noclobber; : > "$A/$name" ) 2>/dev/null && echo win > $oa || echo lose > $oa &
		local pa=$!
		( set -o noclobber; : > "$B/$name" ) 2>/dev/null && echo win > $ob || echo lose > $ob &
		local pb=$!
		wait $pa; wait $pb
		local n=0
		[ "$(cat $oa 2>/dev/null)" = "win" ] && n=$((n+1))
		[ "$(cat $ob 2>/dev/null)" = "win" ] && n=$((n+1))
		rm -f $oa $ob
		if [ $n -ne 1 ]; then
			fail "round $i had $n successes (it should be 1)"
			return
		fi
		wins=$((wins+1))
		i=$((i+1))
	done
	if [ $wins -ne $rounds ]; then
		fail "$wins of $rounds rounds had exactly one success"
		return
	fi
	pass
}

test_xc_open_fd_sticks_to_inode() {
	# * The main event of handle-context stage B (problem 1).
	#   **An fd left open on A must keep pointing at the first inode even after B renames and re-creates the same name.**
	#
	#   Why it only reproduces cross-client: with a rename inside the same mount **the kernel's dentry moves
	#   along**, so the path libfuse hands over becomes the new name and even a path-based lookup lands on the
	#   right inode. **When B does the rename, A's kernel does not know the name changed**, so a path-based A
	#   writes into "a different inode with the same name" (= the one created later).
	#
	#   notify is required: B's rename plus create drops A's InodeCache and A re-reads the database, which
	#   makes a stage-A build land on the wrong inode **deterministically**. Without notify, A can keep a stale
	#   cache and hit the right inode by chance, which makes the test unstable.
	rm -f "$A/fdr.txt" "$A/fdr-moved.txt" 2>/dev/null
	printf 'ORIGINAL' > "$A/fdr.txt" || { fail "creating it on A"; return; }
	wait_until '[ "$(cat "$B/fdr.txt" 2>/dev/null)" = "ORIGINAL" ]' || { fail "the creation is not visible from B (a premise)"; return; }

	# Keep it open on A (O_RDWR, no truncate)
	exec 9<> "$A/fdr.txt" || { fail "opening it on A"; return; }

	# B renames it and creates a different file under the same name
	mv "$B/fdr.txt" "$B/fdr-moved.txt" || { fail "the rename on B"; exec 9>&-; return; }
	printf 'IMPOSTOR' > "$B/fdr.txt" || { fail "re-creating the same name on B"; exec 9>&-; return; }

	# **Wait until A can see the new state** - without waiting here, "A happens to be right because it has not
	# noticed yet" would pass and the detection power would be zero.
	if ! wait_until '[ "$(cat "$A/fdr.txt" 2>/dev/null)" = "IMPOSTOR" ]'; then
		fail "A still cannot see B's re-creation after waiting ${WAIT_MAX} seconds (the premise no longer holds)"
		exec 9>&-
		return
	fi

	# Write through the fd that is still open (offset 0 = overwriting the first byte)
	printf 'X' >&9 || { fail "writing through the fd that is still open"; exec 9>&-; return; }
	exec 9>&-

	# The check is done **from B's side** (to avoid A's page cache).
	# It has to have reached the first inode (= fdr-moved.txt now).
	if ! wait_until '[ "$(cat "$B/fdr-moved.txt" 2>/dev/null)" = "XRIGINAL" ]'; then
		fail "the write through the still-open fd did not reach the first inode (fdr-moved.txt as seen from B = '$(cat "$B/fdr-moved.txt" 2>/dev/null)')"
		return
	fi
	local impostor
	impostor=$(cat "$B/fdr.txt" 2>/dev/null)
	if [ "$impostor" != "IMPOSTOR" ]; then
		fail "the file created later under the same name was overwritten (fdr.txt = '$impostor')"
		return
	fi
	pass
}

test_xc_open_fd_follows_data_repoint() {
	# **A guard** for problem 2 (not a reproduction test).
	#   Reusing the Inode snapshot the handle is holding makes **a write across a data_id reassignment land on
	#   the old data**. Stage B's `Api.ResolveHandle` re-resolves from `InodeId` every time and prevents that,
	#   so this being green is the regression guard for it.
	#
	#   ⚠ **This test is green on a stage-A build too** - on Linux, even in stage A, `Read` / `Write`
	#   re-resolved with `GetByPath` every time (problem 2 really bites on Windows's `FileSystem.Resolve`,
	#   which keeps holding the resolution result on the handle).
	#   **The detection power is against "changing `ResolveHandle` back to reusing a snapshot"**, and that is
	#   what it is here to protect.
	rm -f "$A/rp_a.txt" "$A/rp_b.txt" 2>/dev/null
	printf 'AAAAAAAA' > "$A/rp_a.txt" || { fail "creating it on A"; return; }
	ln "$A/rp_a.txt" "$A/rp_b.txt" || { fail "the hardlink on A"; return; }
	wait_until '[ "$(stat -c "%s" "$B/rp_b.txt" 2>/dev/null)" = "8" ]' || { fail "the link is not visible from B (a premise)"; return; }

	exec 9<> "$A/rp_a.txt" || { fail "opening it on A"; return; }
	# B truncates the sibling = an operation on the body (data).
	truncate -s 0 "$B/rp_b.txt" || { fail "the truncate on B"; exec 9>&-; return; }
	if ! wait_until '[ "$(stat -c "%s" "$A/rp_a.txt" 2>/dev/null)" = "0" ]'; then
		fail "A still cannot see B's truncate after waiting ${WAIT_MAX} seconds (the premise no longer holds)"
		exec 9>&-
		return
	fi
	# Write through the fd that is still open. It has to reach **the data after the reassignment**.
	printf 'ZZZZ' >&9 || { fail "writing through the fd that is still open"; exec 9>&-; return; }
	exec 9>&-
	if ! wait_until '[ "$(cat "$B/rp_b.txt" 2>/dev/null)" = "ZZZZ" ]'; then
		fail "it did not reach the data after the reassignment (rp_b.txt as seen from B = '$(cat "$B/rp_b.txt" 2>/dev/null)')"
		return
	fi
	pass
}

test_xc_append_lands_at_true_end() {
	# * An O_APPEND append has to land at **the real end** (docs/Mount.md, the append contract).
	#
	#   On Linux **the kernel decides the write offset for O_APPEND** (`generic_write_checks` reads
	#   `i_size_read(inode)`). The `i_size` A's kernel holds knows nothing about B's appends, so before the fix
	#   **an offset that tramples B's bytes** comes down.
	#
	#   ⚠ **Do not slip a stat in on A's side.** Doing so updates the kernel's `i_size` and makes it pass even
	#     before the fix, **reducing the detection power to zero**. Confirming that B grew it must be done
	#     **on B's side** (every wait_until below looks at $B).
	rm -f "$A/ap.txt" 2>/dev/null
	printf 'AAAA' > "$A/ap.txt" || { fail "creating it on A"; return; }
	wait_until '[ "$(cat "$B/ap.txt" 2>/dev/null)" = "AAAA" ]' || { fail "the creation is not visible from B (a premise)"; return; }

	exec 9>> "$A/ap.txt" || { fail "the O_APPEND open on A"; return; }
	printf '1' >&9 || { fail "A's first append"; exec 9>&-; return; }
	if ! wait_until '[ "$(stat -c "%s" "$B/ap.txt" 2>/dev/null)" = "5" ]'; then
		fail "A's append is not visible from B (the premise no longer holds)"
		exec 9>&-
		return
	fi

	# B grows it. **A's side is left alone** (leaving the kernel's i_size stale).
	printf 'BBBBBB' >> "$B/ap.txt" || { fail "the append on B"; exec 9>&-; return; }
	if ! wait_until '[ "$(stat -c "%s" "$B/ap.txt" 2>/dev/null)" = "11" ]'; then
		fail "B's append is not visible from B itself (the premise no longer holds)"
		exec 9>&-
		return
	fi

	# Write through the O_APPEND handle that is still open. **It has to land at the real end (11).**
	printf '2' >&9 || { fail "A's second append"; exec 9>&-; return; }
	exec 9>&-

	if ! wait_until '[ "$(cat "$B/ap.txt" 2>/dev/null)" = "AAAA1BBBBBB2" ]'; then
		fail "the append did not land at the end (the contents as seen from B = '$(cat "$B/ap.txt" 2>/dev/null)' / expected 'AAAA1BBBBBB2')"
		return
	fi
	pass
}

test_xc_append_keeps_all_bytes_when_split() {
	# **Not a single byte may be lost when another mount interleaves with an append larger than `max_write`.**
	#
	#   What the contract promises is **the total byte count** only. **The order is the part the contract
	#   admits will interleave**, so it is not checked here (docs/Mount.md, the append contract). The
	#   interleaving is timing-dependent and does not stay stably green or red, so the split is
	#   **to record it in the docs as a measurement rather than automate it**.
	#
	#   Measured (before the fix): a 4 MiB append splits into **four 1 MiB callbacks**, and
	#   **the kernel decides every offset at once from the first `i_size`**, so the 120 bytes B interleaved
	#   vanished entirely. This test watches those 120 bytes survive.
	local f="$A/apsplit.bin"
	local fb="$B/apsplit.bin"
	local src="$A/.apsplit.src"
	rm -f "$f" "$src" 2>/dev/null
	head -c 4096 /dev/urandom > "$f" || { fail "creating it on A"; return; }
	wait_until '[ "$(stat -c "%s" "$fb" 2>/dev/null)" = "4096" ]' || { fail "the creation is not visible from B (a premise)"; return; }

	# Keep the source **outside the mount** (inside it, that write itself would mix into the measurement)
	local tmp
	tmp=$(mktemp) || { fail "mktemp"; return; }
	head -c 4194304 /dev/urandom > "$tmp"

	# While A appends 4 MiB in a single write(2), B appends 10 bytes x 12
	dd if="$tmp" of="$f" bs=4M count=1 oflag=append conv=notrunc status=none &
	local ddpid=$!
	sleep 0.25
	local i=0
	while [ $i -lt 12 ]; do
		printf 'BBBBBBBBBB' >> "$fb" 2>/dev/null
		sleep 0.08
		i=$((i+1))
	done
	wait $ddpid
	rm -f "$tmp"

	# 4096 + 4194304 + 120 = 4198520
	local want=4198520
	if ! wait_until '[ "$(stat -c "%s" "'"$fb"'" 2>/dev/null)" = "'"$want"'" ]'; then
		local got
		got=$(stat -c '%s' "$fb" 2>/dev/null)
		fail "bytes were lost: expected ${want} byte(s) / actually ${got} byte(s) (a difference of $((want - ${got:-0})))"
		return
	fi
	rm -f "$f" 2>/dev/null
	pass
}

test_xc_fuse_hidden_not_listed() {
	# **libfuse's hidden file (`.fuse_hidden<16 digits>`) must not show up in the other mount's enumeration.**
	#
	#   pgfs does not set `hard_remove` (the reason and the background are in
	#   docs/design/handle-context.md, the section on why `hard_remove` is not set). Because of that,
	#   **unlinking a file that is still open makes libfuse rename it to `.fuse_hidden...`**.
	#   That is **a real file in the database**, so left alone **it shows up in the other mount's `ls`** -
	#   which is the real harm the chosen alternative closed (`Api.ListChildren` keeps it out of the enumeration).
	#
	#   ⚠ **It also checks that the fd can still be read.** Meaning to remove it only from the enumeration but
	#     removing it from path resolution as well makes **libfuse itself, which fires `getattr` / `release`
	#     against the hidden name, unable to resolve it, and the still-open fd breaks**. **That is the scarier
	#     one**, so the two are always checked as a pair.
	rm -f "$A/fh.txt" 2>/dev/null
	printf 'HIDDEN_BODY' > "$A/fh.txt" || { fail "creating it on A"; return; }
	wait_until '[ -f "$B/fh.txt" ]' || { fail "the creation is not visible from B (a premise)"; return; }

	exec 9< "$A/fh.txt" || { fail "opening it on A"; return; }
	rm "$A/fh.txt" || { fail "the unlink on A"; exec 9<&-; return; }
	sleep 0.5

	# (1) the hidden file shows up in **neither mount's enumeration**
	local leftA leftB
	leftA=$(ls -a "$A" 2>/dev/null | grep -c '^\.fuse_hidden')
	leftB=$(ls -a "$B" 2>/dev/null | grep -c '^\.fuse_hidden')
	if [ "$leftA" != "0" ] || [ "$leftB" != "0" ]; then
		fail "the hidden file shows up in an enumeration (A=$leftA / B=$leftB)"
		exec 9<&-
		return
	fi

	# (2) **the still-open fd can still be read** (it was not removed from path resolution)
	local body
	body=$(cat <&9 2>&1)
	exec 9<&-
	if [ "$body" != "HIDDEN_BODY" ]; then
		fail "the open fd cannot be read after the unlink ('$body') = suspect it was removed from path resolution too"
		return
	fi

	# (3) closing it removes the body as well
	if ! wait_until '[ ! -f "$B/fh.txt" ]'; then
		fail "the name is still visible from B after the close"
		return
	fi
	pass
}

test_xc_user_named_fuse_hidden_is_listed() {
	# **A confusingly named file a user created must not be hidden.**
	#   The check is strict about libfuse's format (`.fuse_hidden` plus 16 hex digits).
	#   Rejecting on the prefix alone makes **a user's file look as though it disappeared**.
	rm -f "$A/.fuse_hiddenZZZ" "$A/.fuse_hidden00000000000000ff" 2>/dev/null
	printf 'mine' > "$A/.fuse_hiddenZZZ" || { fail "creating the confusingly named file"; return; }
	if ! wait_until '[ -f "$B/.fuse_hiddenZZZ" ]'; then
		fail "a name with the wrong format does not show up in B's enumeration (a user's file is being hidden)"
		return
	fi
	local listed
	listed=$(ls -a "$B" 2>/dev/null | grep -c "^\.fuse_hiddenZZZ$")
	if [ "$listed" != "1" ]; then
		fail "a name with the wrong format does not show up in ls ($listed)"
		return
	fi
	rm -f "$A/.fuse_hiddenZZZ" 2>/dev/null
	pass
}

test_xc_writeback_flush_does_not_undo_remote_truncate() {
	# * **When another mount truncates while dirty data is held, the flush must not roll the shrunk size back.**
	#
	#   Write-back's flush was passing **`file.Size`** to `GREATEST(st_size, @size)`.
	#   `file.Size` is `Math.Max(inode.Size, ...)` = **mixed with the local cache**, so after B shrinks it to
	#   4 bytes, A's flush gives `GREATEST(4, 32)` = 32 and
	#   **st_size rolls back and the bytes the truncate should have removed become readable again**
	#   (measured on both operating systems. The same shape of error as H-1 had been left on the write-back side).
	if ! db_ready; then
		skip "cannot check the database with psql (this test is about the authoritative value in the database)"
		return
	fi
	mount_a_writeback || { fail "cannot re-establish it with write-back"; return; }
	local f="$A/xw_trunc.txt"
	local f_b="$B/xw_trunc.txt"
	rm -f "$f" 2>/dev/null
	printf 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' > "$f_b" || { fail "the initial creation on B"; return; }
	wait_until '[ "$(stat -c %s "'"$f"'" 2>/dev/null)" = "32" ]' || { fail "it does not look like 32 bytes from A"; return; }
	cat "$f" >/dev/null   # put the chunk's image into A's content cache

	# * **Dirty data cannot be held across a write from bash.** Two traps were hit:
	#     - `exec 8> file` carries **O_TRUNC**. The file goes to 0 the instant it is opened, and the whole
	#       scenario collapses while still reporting green.
	#     - `exec 8<> file` plus `printf >&8` does not truncate, but **the redirection closes the duplicated fd**.
	#       A flush is **per file**, so that close **flushes all of the dirty data**.
	#   So it **opens with O_WRONLY from python, pwrites, and waits while holding the fd**.
	local holder="${TMPDIR:-/tmp}/pgfs_xc_holder.$$.py"
	local flag="${TMPDIR:-/tmp}/pgfs_xc_holder.$$"
	rm -f "$flag".* 2>/dev/null
	cat > "$holder" <<'HOLDER'
import os, sys, time
path, flag = sys.argv[1], sys.argv[2]
fd = os.open(path, os.O_WRONLY)          # no O_TRUNC
os.pwrite(fd, b'BBBB', 0)
open(flag + ".written", "w").close()
while not os.path.exists(flag + ".close"):
    time.sleep(0.1)
os.close(fd)                              # only here does it flush
open(flag + ".closed", "w").close()
HOLDER
	python3 "$holder" "$f" "$flag" &
	local holder_pid=$!
	local waited=0
	while [ ! -f "$flag.written" ]; do
		sleep 0.1
		waited=$((waited+1))
		if [ $waited -gt 100 ]; then
			kill "$holder_pid" 2>/dev/null
			rm -f "$holder" "$flag".* 2>/dev/null
			fail "the write on A's side never returns"
			return
		fi
	done

	# * **Confirm it has not been flushed yet.** If it has, nothing below verifies anything.
	#   ⚠ **Do not open the file on A's side to check.** A flush is **per file**, so opening and closing it
	#     through another handle runs a flush on that close and **the dirty data being checked for is erased by
	#     our own hand** (actually hit: merely slipping in `head -c 4 "$f"` made the new bytes visible from B
	#     and it was misjudged as "no dirty data could be produced").
	#   **Look only at B and at the database.** Both still holding the old bytes means it is unflushed.
	local seen_b; seen_b=$(head -c 4 "$f_b" 2>/dev/null)
	# * **Do not join inode and data_chunk.** On Citus their distribution keys differ, so it is rejected with
	#   `complex joins are only supported when ...`, `q()` swallows the error and
	#   **returns an empty string = the premise check always fails**. It is fetched in two queries instead
	#   (`PruneAdmin.ScanOrphanData` splits into two queries for the same reason).
	local data_id; data_id=$(q "select data_id from ${SCHEMA}.${PREFIX}inode where name = 'xw_trunc.txt'" | head -1)
	local head_db; head_db=$(q "select encode(substring(payload from 1 for 4), 'escape')
	                            from ${SCHEMA}.${PREFIX}data_chunk
	                            where data_id = ${data_id:-0} and chunk_index = 0" | head -1)
	if [ "$seen_b" != "AAAA" ] || [ "$head_db" != "AAAA" ]; then
		touch "$flag.close"; wait "$holder_pid" 2>/dev/null; rm -f "$holder" "$flag".* 2>/dev/null
		fail "it has already been flushed right after the write (B='$seen_b' / DB='$head_db' / expected AAAA = no dirty data could be held)"
		return
	fi

	truncate -s 4 "$f_b" || { touch "$flag.close"; wait "$holder_pid" 2>/dev/null; fail "the truncate on B"; return; }
	local shrunk; shrunk=$(q "select st_size from ${SCHEMA}.${PREFIX}inode where name = 'xw_trunc.txt'" | head -1)
	if [ "$shrunk" != "4" ]; then
		touch "$flag.close"; wait "$holder_pid" 2>/dev/null; rm -f "$holder" "$flag".* 2>/dev/null
		fail "B's truncate has not reached the database (st_size=$shrunk, expected 4 = the test's premise no longer holds)"
		return
	fi

	touch "$flag.close"   # the holder closes = flush
	wait "$holder_pid" 2>/dev/null
	rm -f "$holder" "$flag".* 2>/dev/null
	sleep 1
	local after; after=$(q "select st_size from ${SCHEMA}.${PREFIX}inode where name = 'xw_trunc.txt'" | head -1)
	if [ "$after" != "4" ]; then
		fail "the flush rolled the truncate back (st_size $shrunk -> $after / expected 4)"
		return
	fi
	# Check **whether A's write really landed through the flush** as well. If it did not, "it did not roll
	# back" above might just mean "nothing was ever written" (the two only mean something as a pair with the premise check).
	local landed; landed=$(head -c 4 "$f_b" 2>/dev/null)
	if [ "$landed" != "BBBB" ]; then
		fail "A's write did not land through the flush (from B '$landed' / expected BBBB)"
		return
	fi
	rm -f "$f_b" 2>/dev/null
	pass
}

test_xc_resync_drops_stale_cache() {
	# * **There has to be a way to recover from a dropped notification.**
	#
	#   When the sending queue (1024) overflows, that notification **never arrives**. A positive entry in
	#   `InodeCache` has **no TTL**, so unless it is dropped it **keeps returning a stale value forever**
	#   (review M-2). The sender folds an overflow into a single "throw everything away". What is checked here
	#   is **the receiving side** (actually overflowing it would need 1024 entries piled up, which is heavy for a test).
	if ! db_ready; then
		skip "the database cannot be touched with psql (psql is what builds the stale cache)"
		return
	fi
	local f="$A/xr_resync.txt"
	local f_b="$B/xr_resync.txt"
	rm -f "$f" 2>/dev/null
	printf 'AAAAAAAA' > "$f_b" || { fail "the initial creation on B"; return; }
	wait_until '[ "$(stat -c %s "'"$f"'" 2>/dev/null)" = "8" ]' || { fail "it does not look like 8 bytes from A"; return; }

	# **Rewrite it directly with psql = no notification is sent.** That stands in for the "dropped" state.
	q "update ${SCHEMA}.${PREFIX}inode set st_size = 3 where name = 'xr_resync.txt'" >/dev/null
	local stale; stale=$(stat -c %s "$f" 2>/dev/null)
	if [ "$stale" != "8" ]; then
		fail "the psql change became visible without a notification ($stale) = the cache could not be built (the test's premise no longer holds)"
		return
	fi

	# Fire "throw everything away" while pretending to be another client (`s` is a sender id other than ours).
	q "select pg_notify('${SCHEMA}_${PREFIX}notify', '{\"s\":\"deadbeef\",\"r\":true}')" >/dev/null
	if ! wait_until '[ "$(stat -c %s "'"$f"'" 2>/dev/null)" = "3" ]'; then
		fail "it keeps returning the old st_size even after the throw-everything-away instruction ($(stat -c %s "$f" 2>/dev/null) / expected 3)"
		return
	fi
	rm -f "$f_b" 2>/dev/null
	pass
}

test_xc_relisten_drops_stale_cache() {
	# ★ **After LISTEN is re-established, the clean caches are dropped.**
	#
	#   While the LISTEN connection is down (a PG restart / a NAT timeout), PostgreSQL **does not deliver** the
	#   notifications, so they never arrive. Before, it waited two seconds, reconnected and did nothing more, and the
	#   cache with no TTL **kept returning the old size and the old contents**. This is **a different path** from the
	#   queue overflow above. Here the LISTEN backend is killed with psql, the change in between is written directly
	#   with psql (= no notification is sent), and the new value must be visible after LISTEN comes back.
	if ! db_ready; then
		skip "psql cannot touch the database (it is used to kill LISTEN)"
		return
	fi
	local f="$A/xr_relisten.txt"
	local f_b="$B/xr_relisten.txt"
	rm -f "$f" 2>/dev/null
	printf 'AAAAAAAA' > "$f_b" || { fail "the initial create on B"; return; }
	wait_until '[ "$(stat -c %s "'"$f"'" 2>/dev/null)" = "8" ]' || { fail "A does not see 8 bytes"; return; }

	# Kill the LISTEN backends (A and B listen on the same channel, so both drop; both reconnect).
	local killed; killed=$(q "select count(pg_terminate_backend(pid)) from pg_stat_activity
	                          where datname = current_database() and pid <> pg_backend_pid()
	                            and query ilike 'LISTEN %notify%'" | head -1)
	if [ -z "$killed" ] || [ "$killed" = "0" ]; then
		skip "no LISTEN backend was found (not a notify setup?) = the test's premise no longer holds"
		return
	fi
	# The change while it is down (directly with psql = no notification is sent).
	q "update ${SCHEMA}.${PREFIX}inode set st_size = 3 where name = 'xr_relisten.txt'" >/dev/null
	# LISTEN comes back after two seconds. It must read 3 within wait_until's default wait.
	if ! wait_until '[ "$(stat -c %s "'"$f"'" 2>/dev/null)" = "3" ]'; then
		fail "the old st_size keeps coming back after LISTEN is re-established ($(stat -c %s "$f" 2>/dev/null) / expected 3)"
		return
	fi
	rm -f "$f_b" 2>/dev/null
	pass
}

test_xc_write_after_remote_truncate_keeps_size() {
	# * **After another mount shrinks it, a "non-growing overwrite" must not make the written bytes unreachable.**
	#
	#   Whether the `st_size` clause was emitted **was decided from the local inode.Size (the cache)**, so
	#   after B truncated it, an overwrite from A **without O_TRUNC** that is shorter made
	#   `newSize > inode.Size` false and **the st_size clause fell out of the UPDATE entirely, leaving 0 in the database**.
	#   The chunk is written, yet the 0 that `RETURNING st_size` gives back is distributed into memory too, so
	#   **A's own ls and cat come back empty** (review H-1).
	#
	#   **Re-establish with notify OFF** - with ON, B's truncate notification drops A's cache and the window closes.
	mount_both_nonotify || { fail "cannot re-establish them without notify"; return; }
	local f="$A/xt_shrink.txt"
	local f_b="$B/xt_shrink.txt"
	rm -f "$f" 2>/dev/null
	printf '0123456789' > "$f" || { fail "the initial creation"; return; }
	# Put size = 10 into A's cache (this is what goes stale later).
	local first; first=$(stat -c %s "$f" 2>/dev/null)
	if [ "$first" != "10" ]; then
		fail "the initial size is not 10 ($first)"
		return
	fi
	# Shrink it from B. With notify OFF, **A's cache stays at 10**.
	if [ ! -e "$f_b" ]; then
		fail "the file is not visible from B (the test's premise no longer holds)"
		return
	fi
	: > "$f_b" || { fail "the truncate on B"; return; }

	# Overwrite 3 bytes from A **without O_TRUNC** (shorter than 10 = before the fix the st_size clause falls out).
	printf 'abc' | dd of="$f" bs=3 count=1 conv=notrunc status=none 2>/dev/null

	local size; size=$(stat -c %s "$f" 2>/dev/null)
	local body; body=$(cat "$f" 2>/dev/null)
	if [ "$size" != "3" ]; then
		fail "the st_size seen from A is $size (expected 3) = the written bytes are unreachable"
		return
	fi
	if [ "$body" != "abc" ]; then
		fail "the contents read from A are '$body' (expected abc)"
		return
	fi
	# **Look at the database side directly to see it is fixed.**
	#   * **Do not look through B's stat** - they were established with notify OFF, so B still has the 0 it
	#     truncated to cached, and **that is exactly as designed** (A's write is not notified).
	#     What is wanted here is not "is A's cache lying" but "**did 3 go into the database**", so the
	#     authoritative value is fetched with psql.
	if db_ready; then
		local size_db; size_db=$(q "select st_size from ${SCHEMA}.${PREFIX}inode where name = 'xt_shrink.txt'" | head -1)
		if [ "$size_db" != "3" ]; then
			fail "the st_size in the database is '$size_db' (expected 3) = the authoritative value is not fixed"
			return
		fi
	fi
	rm -f "$f" 2>/dev/null
	pass
}

test_xc_create_under_removed_parent_fails() {
	# * Being able to **create a child** under a directory another client deleted leaves
	#   **an unreachable orphan** in the database that **cannot be removed through the FS** (only SQL can clean it up).
	#   It checks that **all four paths** - create / mkdir / symlink / hardlink - are closed.
	#   **Re-establish with notify OFF** (with ON, the rmdir notification drops A's cache and the window closes).
	#
	#   What is expected is **ENOENT**. `-EIO` is the mark of "Core stopped it but the FUSE side did not map it",
	#   and that is treated as a failure too (because the caller sees a different error).
	mount_both_nonotify || { fail "cannot re-establish them without notify"; return; }
	local src="$A/xp_src.txt"
	rm -rf "$A/xp" 2>/dev/null
	echo src > "$src" || { fail "creating the link source"; return; }
	local failures=""
	local case_no=0
	# For each case: create the parent -> put it into A's cache -> rmdir it on B -> try to create on A.
	for kind in create mkdir symlink hardlink; do
		case_no=$((case_no+1))
		local dir="$A/xp$case_no"
		local dir_b="$B/xp$case_no"
		rm -rf "$dir" 2>/dev/null
		# **Do not use `mkdir -p`** - a recursive create rebuilds the parent and this window cannot be hit
		# (on the Windows side .NET's CreateDirectory is recursive and the test missed for the same reason).
		mkdir "$dir" || { fail "creating the parent ($kind)"; return; }
		ls "$dir" >/dev/null 2>&1          # put the parent into A's cache
		wait_until '[ -d "'"$dir_b"'" ]' || { fail "the parent is not visible from B ($kind)"; return; }
		rmdir "$dir_b" || { fail "the rmdir on B ($kind)"; return; }
		local out=""
		local rc=0
		# * Add `LC_ALL=C`. errno is only visible as a message string, so **the check breaks** when the locale
		#   translates "No such file or directory"
		#   (in a Japanese environment it becomes a translated sentence).
		case "$kind" in
			create)   out=$(LC_ALL=C touch "$dir/child" 2>&1) || rc=$? ;;
			mkdir)    out=$(LC_ALL=C mkdir "$dir/child" 2>&1) || rc=$? ;;
			symlink)  out=$(LC_ALL=C ln -s target "$dir/child" 2>&1) || rc=$? ;;
			hardlink) out=$(LC_ALL=C ln "$src" "$dir/child" 2>&1) || rc=$? ;;
		esac
		if [ $rc -eq 0 ]; then
			failures="$failures ${kind}(it succeeded = an orphan)"
			continue
		fi
		if ! echo "$out" | grep -qi "No such file or directory"; then
			failures="$failures ${kind}(not ENOENT: $(echo "$out" | tail -1))"
		fi
	done
	if [ -n "$failures" ]; then
		fail "creating under a deleted parent is not blocked:$failures"
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

echo "${BLUE}=== pgfs cross-client tests ===${NC}"
echo "setting : $SETTING_FILE"
echo "mount A : $MOUNT_A"
echo "mount B : $MOUNT_B"
echo "wait    : ${WAIT_MAX}s"
echo ""

umount_all
mount_both || exit 2
preflight_notify || { umount_all; exit 2; }
rm -rf "$A" 2>/dev/null
mkdir -p "$A" || { echo "${RED}cannot create the test directory${NC}: $A" >&2; umount_all; exit 2; }
# Wait until it is visible on B's side too (so that the first test does not lose its premise)
wait_until '[ -d "$B" ]' || { echo "${RED}the test directory is not visible from B${NC}" >&2; umount_all; exit 2; }

run test_xc_create_visible
run test_xc_delete_visible
run test_xc_overwrite_visible
run test_xc_rename_replace_visible
run test_xc_hardlink_size_propagates
run test_xc_hardlink_truncate_propagates
run test_xc_exclusive_create_races
# handle-context stage B (docs/design/handle-context.md)
run test_xc_open_fd_sticks_to_inode
run test_xc_open_fd_follows_data_repoint
run test_xc_append_lands_at_true_end
run test_xc_append_keeps_all_bytes_when_split
run test_xc_fuse_hidden_not_listed
run test_xc_user_named_fuse_hidden_is_listed
run test_xc_resync_drops_stale_cache
run test_xc_relisten_drops_stale_cache
# * This one **re-establishes them with notify off**, so it goes last (so the tests after it do not break on assuming notify).
run test_xc_write_after_remote_truncate_keeps_size
# * This one **re-establishes with write-back**, so it sits at the end next to the no-notify group.
run test_xc_writeback_flush_does_not_undo_remote_truncate
run test_xc_create_under_removed_parent_fails

# cleanup
rm -rf "$A" 2>/dev/null
umount_all

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
