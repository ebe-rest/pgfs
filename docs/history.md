# Design decisions

A record of the key design decisions behind the current design, and why the alternatives were not taken. Organized by topic rather than chronology.

---

## Storage: bytea chunks (not Large Objects)

A file body is stored as one `pgfs_data` row + multiple `pgfs_data_chunk` rows (1 row = 1 bytea = 1 chunk, default chunk_size = 1 MiB), rather than PostgreSQL Large Objects.

**Why bytea, not Large Objects**: `pg_largeobject` is a PostgreSQL system catalog, so it cannot be a target of `create_distributed_table`. With Large Objects, the body would forever be concentrated on the single coordinator no matter how many workers are added — the capacity wall could not be crossed. bytea is a column of a user table, so it can be distributed by Citus. As a bonus, even in single-PG operation it is slightly faster for workloads without many partial writes (the LO `lo_seek+lo_read` 2 round-trips become a single `substring(payload from N for M)`).

Decisions:
- **each chunk's payload length = "the number of bytes written so far"**. An alternative of "fix all chunks to chunk_size (with zero padding)" was considered, but variable length was chosen for storage efficiency and `du` accuracy.
- **WriteData completes with one upsert SQL per chunk**: `INSERT ... ON CONFLICT (data_id, chunk_index) DO UPDATE SET payload = CASE ... END`, branching on 3 cases (central overlay / tail overwrite / zero-pad + concat). Concurrent WriteFile races are serialized automatically by the PG row lock.
- **`repeat(bytea, integer)` does not exist in PG** (only `repeat(text, integer)`) — zero padding is built with `decode(repeat('00', N), 'hex')`.

The implementation core and semantics are authoritatively in [support_for_citus.md §storage](support_for_citus.md) (DDL / SQL patterns / TOAST partial detoast).

Rejected:
- **keep LO and distribute/replicate it to workers**: Citus-distributing a custom LO table is equivalent to "bytea with a variable page size", so it is pointless.
- **fixed chunk payload of chunk_size (with zero padding)**: variable length was chosen to keep "`du` shows what you see".

---

## Horizontal distribution with Citus

`mkfs --citus` distributes 4 tables with `create_distributed_table` and places `pgfs_settings` locally. Both a **single-node setup (no worker)** and a **multi-node setup (coordinator + N workers, `--worker host[:port],...`)** can be brought up with mkfs alone.

The distribution strategy, the handling of Citus constraints, the mkfs flags, and the idempotency guarantees are authoritatively in [support_for_citus.md §distribution](support_for_citus.md) (composite PK / distribution key required for FOR UPDATE / IMMUTABLE restriction of the DO UPDATE clause / cross-shard rename DELETE+INSERT / coordinator registration + independent shouldhaveshards decision, etc.). The decisions at the "user judgment" level:

- **the "single-PG mode only gets a standalone UK on id" branch is not adopted**: the schema would diverge between single-PG and Citus, an in-place migration would be stuck on the UK, and a startup-time application validation only adds latency with no perceptible benefit — so neither mode has the UK. Uniqueness is kept by the BIGSERIAL sequence + transaction discipline. Confirm this decision before re-proposing "let's guarantee id uniqueness just in case" (details in support_for_citus.md).
- **`SetupCitusDistributionAsync` is dissolved and its responsibilities distributed**: cluster-topology setup moves inside `EnsureDatabaseAsync`, and the per-table `create_distributed_table` / `citus_add_local_table_to_metadata` move inside each `CreateXxxTableAsync`. `CreateTableAsync` was changed to `Task<bool>` so distribution runs only when a table is newly created (consistent with skipping Citus mutation when the DB already exists).
- **a per-worker `citus_set_coordinator_host` is unnecessary**: [multinode_probe.sh](../tests/citus/multinode_probe.sh) confirmed on real hardware that `citus_add_node`'s auto-sync synchronizes the coordinator into the worker's `pg_dist_node` automatically.

Test results:
- the mkfs matrix test ([test_matrix.sh](../tests/citus/test_matrix.sh)): 3 initial x 6 target = **18/18 PASS** (Citus 14.0.0 docker, linux_client)
- single-PG mode / 1-node Citus (pgsql_server, Citus 13.1.1): Linux 34/34, Windows 24/24 ALL PASSED

Rejected:
- distribute `pgfs_inode` by `id` and guarantee the UK in another layer — the `(parent_id, name)` UK would break, and application-layer race prevention would get complex.
- distribute `pgfs_settings` — unnecessary, since settings are only read/written by the coordinator.

---

## Cross-client locking

Cross-client mutual exclusion using row locks on `pgfs_lock(target_id BIGINT PK)`, integrated into [src/lib/src/Api/Api.cs](../src/lib/src/Api/Api.cs). The design details, the integration points, and the lock-acquisition SQL are authoritatively in [support_for_citus.md §locking](support_for_citus.md). The decisions:

- **why "a custom TTL table + heartbeat" and `pg_advisory_xact_lock` were not adopted**: a TTL race / coordinator-local constraints / not Citus-distributable (details in the support_for_citus.md alternatives comparison).
- **namespace separation implemented by "the sign of target_id", not "a separate table"**: Citus's single-column distribution constraint forces the 1-column `pgfs_lock(target_id BIGINT PK)`, so namespaces are separated by the sign of the value (`+data_id` / `-inode_id`). The "split pgfs_inode_lock into a separate table distributed + co-located by parent_id" option is left as room to reorganize if inode locks turn out to be the dominant workload with asymmetric cost.
- **lock scope is per the table in docs**: WriteData/TruncateData/ReleaseData (data lock) / Update{Mode,Owner,Size,Timestamps} (inode lock) / Rename / DeleteInode / CreateHardLink (multi inode lock). SetXAttr/RemoveXAttr/CreateFile/CreateDirectory/CreateSymlink are intentionally out of scope (a single UPDATE is atomic; the reason no parent lock is taken is in the docs "out-of-scope judgment" section).
- **single-shot writes of `Pg.Execute` promoted to tx-based**: the 4 methods UpdateMode/Owner/Size/Timestamps could not hold a lock under the old one-statement `Pg.Execute(...)` structure. They were converted to `using var conn = NewConnection(); using var tx = conn.BeginTransaction()` + `LockInode` + UPDATE + Commit.
- **the `LockTargets` SQL is split into 2 statements**: `INSERT ... ON CONFLICT DO NOTHING` + `SELECT ... FOR UPDATE`. Folding into one statement with a CTE was not adopted, preferring simplicity (each statement is a target_id-only WHERE, so it completes on a single shard even under Citus).
- **tolerating the `Rename` hintParentId stale race**: another client may complete a rename between getting cached.ParentId and `LockInodes`, but stale detection is absorbed by the subsequent `SELECT ... WHERE parent_id = @hint AND id = @id FOR UPDATE` returning empty → exiting false (a wrongly-locked old parent is auto-released when the tx ends).

**Verification** ([tests/citus/race_multinode.sh](../tests/citus/race_multinode.sh), 4/4 PASS):
- Linux e2e 34/34 on multi-node Citus (a comprehensive check that the cross-shard hop + locking work)
- under a concurrent write race (urandom vs zero, 8MiB x 4 rounds), the md5/size seen from both clients always match
- under a concurrent mkdir of the same name (20 rounds x serial + parallel), exactly 1 client always gets EEXIST
- pgfs_lock accumulation: after 4 tests rows=178 / size=768kB (consistent with the design figure "~50 bytes/row, under 100MB for 1M rows")

**Byproduct**: a `PGFS_TEST_PG_EXEC` environment-variable override was added to the `pg_exec` of [tests/linux/e2e.sh](../tests/linux/e2e.sh) (for test_fallback_uname_gname). race_multinode.sh sets `docker exec -i $COORD_NAME psql ...` and passes it in.

---

## The configuration model

The configuration is **static `Field<T>` descriptors + mutable POCOs + `ConfigLoader` (unifying CLI/TOML/DB/Default) + `ConfigStore` (DB I/O)**, under [src/lib/src/Config/](../src/lib/src/Config/). The Schema is [src/lib/src/Config/Schema.cs](../src/lib/src/Config/Schema.cs) (it enumerates all Fields automatically via reflection).

- **immutable enough with POCOs**: in the current code there is no setting that is rewritten during a mount (see the lifecycle aggregation in [settings-matrix.md](settings-matrix.md)), which is the rationale for plain mutable POCOs rather than a "smart" change-tracking tree.
- **`pgfs_settings` is a flat `(scope, key, value)` PK**: the flat key avoids a recursive CTE entirely (an earlier hierarchical `(id, parent_id, key, value)` form required a recursive CTE to read). The root inode uses `id = 0` for the same reason — BIGSERIAL never returns 0, so a self-referencing `(id=0, parent_id=0)` row cannot collide with real rows, and no cycle detection is needed.
- **loading order CLI → TOML → DB**: realized with "skip if a higher source already set the value", which yields the priority CLI > TOML > DB > Default. The chicken-and-egg (`setting.file` decides the TOML path) is resolved inside ConfigLoader, and `RootConfig` is built in two stages (a lite loader with store=null for the early `--help` decision, so a `--help` run does not try to connect to the DB; then the full loader).
- **persistence is explicit**: right after `config.X.Y = newValue;`, the caller calls `store.Save(Schema.X.Y, newValue)`. No setter hook, to keep Loader/Store separated. `Save<T>` UPSERTs with `INSERT ... ON CONFLICT (scope, key) DO UPDATE`; the JSON representation goes through `Field<T>.FormatJson(value)` (Int/Long/Bool native, everything else `JsonSerializer.Serialize(Format(value))`).

Details are in [architecture.md §Config](architecture.md).

---

## The Tmds.Fuse fork

The upstream `tmds/Tmds.Fuse 0.1.0-190711-50` (unmaintained since 2019) was switched to the active fork `securefolderfs-community/Tmds.Fuse` (.NET 10). It is taken in as a submodule at [vendor/Tmds.Fuse/](../vendor/Tmds.Fuse/), referenced by ProjectReference from [Mount.csproj](../src/mount/Mount.csproj), and added to `pgfs.sln` (to propagate the Release/Debug config).

This resolves 3 constraints of the upstream version:
- **no `MountOptions.Options`** → the fork adds it. `-o allow_other,attr_timeout=0,...` can be passed to libfuse.
- **cannot pass `attr_timeout=0`** → now passed by default. Disabling the kernel attr cache makes `stat a` right after `ln a b` immediately return the new `st_nlink`.
- **libfuse 3 rejects `-o use_ino` as `unknown option`** → a downstream patch in the fork's [vendor/Tmds.Fuse/src/Tmds.Fuse/FuseMount.cs](../vendor/Tmds.Fuse/src/Tmds.Fuse/FuseMount.cs) writes `fuse_config.use_ino` (offset 64) to 1 in the `Init` callback. This makes `stat -c '%i'` agree for hard links.

`Mount/Program.cs` builds the FUSE options: a default `attr_timeout=0` + the received `FuseFlags` joined with `,` and set on `Tmds.Fuse.MountOptions.Options`. See [fstab-support.md §The Tmds.Fuse fork](fstab-support.md) for more.

---

## /etc/fstab and mount(8) integration

[`ConfigLoader.ParseCli`](../src/lib/src/Config/ConfigLoader.cs) has a built-in parser for positional arguments (source=connection or setting.file / target=mount-point) and `-o key=val,flag,...`. The first positional is `database.connection` (URL form) if it starts with `postgresql:`, otherwise `setting.file` (a TOML path) — a heuristic letting `mount.pgfs postgresql://... /mnt/pgfs` and `mount.pgfs /etc/pgfs.toml /mnt/pgfs` coexist (the kv form is indistinguishable, so `-c` is required).

Unrelated flags from the fstab/mount(8) helper (`-i`, `-f`, `-n`, `-s`, `-v`, `-N`, `-t`, `_netdev`, `noauto`, ...) are silently skipped **only in helper context**. The helper-context decision is the AND of (a) `args` has a positional and (b) the parent process comm (`/proc/<ppid>/comm`) is `"mount"`. On non-Linux it is always treated as direct execution. So `-f` (= setting.file short form) / `-s` (= database.schema short form) work **only in direct execution**.

Automatic daemonization via child-process detach (the child prints `PGFS_MOUNTED_OK` as the first stdout line; the parent reads it then exits to release mount(8); `--foreground` keeps it foreground).

**PATH stripping on fstab mount**: `mount(8)` strips PATH entirely from the env when calling the helper. Tmds.Fuse's `HasFusermount` searches `$PATH` for `fusermount3`, so `CheckDependencies` returned false and exited immediately. Resolved by supplying `/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin` at the top of `Mount/Program.cs`'s `Main` when PATH is empty (also inherited by the child via `Process.Start(UseShellExecute=false)`).

Full spec in [fstab-support.md](fstab-support.md). Verified end to end with a real `sudo mount -t pgfs -o allow_other ...` (root mount + non-root user access + umount).

---

## Connection retry

A lightweight in-house helper ([src/lib/src/Utility/Retry.cs](../src/lib/src/Utility/Retry.cs)) wraps `Pg.OpenConnection` / `OpenConnectionAsync` (rather than pulling in Polly). The transient decision is `SocketException` / `TimeoutException` / a list of `PostgresException` SqlStates (`57P03` / `57P01` / `57P02` / `08000` / `08003` / `08006` / `08001` / `08004` / `53300`) / other `NpgsqlException`.

**Exceptions during query execution are not retried** (to keep write idempotency, retry is limited to connection-open time). The exponential backoff is controlled by `database.retry_max_attempts` / `_initial_delay_ms` / `_max_delay_ms` (default 5 / 200ms / 2000ms), read by the `Api` ctor and applied globally via `Retry.Configure`.

---

## Cross-client change notification

Cross-client change propagation using PostgreSQL `LISTEN` / `NOTIFY` ([src/lib/src/Api/NotifyChannel.cs](../src/lib/src/Api/NotifyChannel.cs) + [src/lib/src/Api/RemoteChangeInfo.cs](../src/lib/src/Api/RemoteChangeInfo.cs)).

- **opt-in**: `database.notify_enabled` (default false). Enabled by any of CLI `--notify`, TOML `[database] notify_enabled = true`, `-o notify-enabled`. There is the overhead of 1 dedicated connection + a `SELECT pg_notify(...)` on each write, so OFF is preferable for single-client operation.
- **channel name**: `{schema}_{prefix}notify` (e.g. `pgfs_pgfs_notify`). No collision even with multiple pgfs instances in one DB.
- **payload**: JSON `{"s":<sender>,"i":[ids],"p":[parent_ids],"x":[path_prefixes]}` (within the 8000-byte limit). The sender id is 8 hex chars; self-messages are filtered on the receiving side.
- **tied to writes**: `this.Notify(...)` is embedded at the end of `InsertInode` / `DeleteInode` / `UpdateMode|Owner|Size|Timestamps` / `Rename` / `WriteData` / `TruncateData` / `SetXAttr|RemoveXAttr` / `CreateHardLink`. Published outside the transaction (after commit), via `Pg.Execute` autocommit.
- **receiver processing**: `OnRemoteChange` (in Api) ① resolves the path with `InodeCache.TryGetPath` → ② applies `InodeCache.Invalidate` / `InvalidateChildren` / `InvalidatePrefix` → ③ if `Api.OsBridge` is registered, calls it with the pre-resolved path.
- **OS notification bridge**:
  - **Assign (Windows)**: [FileSystem.PropagateRemoteChange](../src/assign/src/FileSystem.cs) calls `DokanInstance.NotifyUpdate(WindowsPath)` to ask Explorer to repaint. The `/` → `\` conversion is the `ToWindowsPath` helper.
  - **Mount (Linux)**: the Tmds.Fuse high-level API has no equivalent of `fuse_lowlevel_notify_inval_*`, so the OS bridge is unimplemented. Thanks to `attr_timeout=0`, **active queries** such as `stat` / `ls` return the latest value (a DB hit via InodeCache). It does **not** reach passive subscribers such as `inotify` — that remains a limitation. Moving to the low-level API (or adding a notify patch to the vendored Tmds.Fuse) is future work.
- **LISTEN establishment is synchronous**: `NotifyChannel.Start()` completes the connection open + `LISTEN` + Information log on the calling thread; only the subsequent wait loop is in the background (`Task.Run`). The log appears before the MOUNTED signal at `mount.pgfs` startup, so startup can be confirmed in `tests/linux/mount.log` whether via `-f` or via fstab.
- **reconnection on disconnect**: the wait loop catches exceptions, waits 2 seconds → new connection → re-`LISTEN`. Separate from the exponential-backoff retry of the `Pg.OpenConnection` family (the LISTEN-dedicated connection is long-lived, so managed independently).

Verified: Linux 34/34 / Windows 24/24 ALL PASSED (no regression even with `--notify`). The `NotifyChannel: LISTEN pgfs_pgfs_notify (sender=...)` Information log appears in `tests/linux/mount.log`. **An automated test of the real cross-client behavior (2 mount.pgfs + one's write reaching the other) is not in place** (the e2e harness assumes 1 mount), covered by manual verification.

---

## uname/gname fallback

`mount.fallback_uname` (default `nobody`) / `mount.fallback_gname` (default `nogroup`) are implemented with `SaveTarget.Db` (a security setting, intended to be one value for the whole FS). The Linux `UserResolver` / Windows `WindowsUserResolver` constructors were rewritten to `(string fallbackUname, string fallbackGname)`, resolving the fallback names via the OS API (`getpwnam` / `NTAccount.Translate`) at startup and caching internally. The final hardcode on failure is Linux uid=65534 (the NFS `nobody` convention) / Windows `WellKnownSidType.AnonymousSid`, with a warning log.

This resolves the prior behavior that became a security incident ("impersonate the running process's uid" / "someone else's files appear as your own"). `test_fallback_uname_gname` was added to the Linux e2e (a direct DB INSERT creates an inode with a bogus uname, stat confirms `nobody`/`nogroup`, then a DELETE cleanup).

---

## Windows attribute persistence

The Windows e2e ([tests/windows/](../tests/windows/README.md)) has 24 tests symmetric with the Linux suite. POSIX-only features (symlink / hardlink / chmod / chown / xattr API) are out of scope for DokanNet, with Windows-specific ones added instead (SetFileAttributes / SetFileTime / FindFilesWithPattern).

**xattr persistence of Hidden / System / Archive**: the `user.win_attrs` key of `pgfs_inode.xattrs` JSONB stores the `FileAttributes` mask value (Hidden\|System\|Archive only) as a 4-byte little-endian int. **Even when the mask is empty (all OFF), the xattr is not removed — 4 zero bytes are written**: it is treated as a binary "xattr present = SetFileAttributes was touched on the Windows side" / "xattr absent = not yet touched", and the "leading dot = Hidden" heuristic is applied as a fallback only when the xattr is absent. A design that deletes the xattr at mask 0 would hit the UX bug "un-hide a dotfile in Explorer → the heuristic revives it back to Hidden next time". `ReadOnly` remains the st_mode write bit. The implementation is `WinAttrsXattrKey` / `LoadWinAttrs` / `SaveWinAttrs` in [src/assign/src/FileSystemUtils.cs](../src/assign/src/FileSystemUtils.cs).

---

## Write-path race prevention

- **`Api.EnsureDataRow`**: two or more concurrent transactions that each see the in-memory `inode.DataId` as null would both `INSERT pgfs_data`, and the last-wins `UPDATE inode SET data_id` would orphan the loser's data row, making the chunks written there unreachable — a silent leak + data-loss bug (it does not crash loudly because `pgfs_data.id` is serial and does not collide, so it could be broken even with tests passing). The fix locks the inode row with `SELECT data_id FROM inode WHERE id = @id FOR UPDATE` to serialize across transactions and re-reads the true `data_id` from the DB after acquiring the lock (the DB, not the cache, is authoritative). If the `UPDATE` hits 0 rows, the inode is judged to have been concurrently deleted and it throws → the tx rollback prevents orphan data rows.
- **chunk write idempotency**: the chunk upsert uses `INSERT ... ON CONFLICT (data_id, chunk_index) DO UPDATE` so concurrent WriteFile calls spanning chunk boundaries (which Windows `Copy-Item` / CopyFileEx issue) do not violate the PK constraint. The PG row lock serializes the writers.

---

## Linux e2e: symlink / hardlink fixes

- **`Mount.FileSystem.SymLink(path, target)` argument order**: the fork's `FuseMount.Symlink(path*, path*)` repacks the libfuse `(target, linkname)` in reverse order before passing to `IFuseFileSystem.SymLink`. Our override misread arg1 as the link content and arg2 as the link location, so the INSERT never ran. Corrected to the right order.
- **invalidate sibling-inode caches in `Api.DeleteInode` / `Api.CreateHardLink`**: previously only the source/target inode was `inodeCache.Invalidate`d, so 3-or-more hard links, or `stat b` after `rm a`, returned a stale `st_nlink`. The sibling IDs are fetched with `SELECT id FROM inode WHERE data_id = @did` inside the tx, and all are `Invalidate`d after commit.

---

## Coding conventions

[docs/coding-style.md](coding-style.md) is the authoritative coding-conventions document. `.editorconfig` enforces braces-required + the `this.` qualifier. The branching rules (an `if` body is "tail flow-exit + any N statements" or "a single statement only", `else` forbidden, ternary forbidden, multi-way is `switch`, log output is not counted as a side effect) are codified there.
