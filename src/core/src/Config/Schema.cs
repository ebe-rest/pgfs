namespace Pgfs.Core.Config;

using System.Collections.Generic;
using System.Reflection;

/// <summary>
/// The <see cref="Field{T}"/> declarations for every setting. Looked up by scope hierarchy, e.g. `Schema.Mount.MountPoint`.
///
/// <para>
/// This class is the single source of truth for settings.
/// It expresses the table in [docs/settings-matrix.md](../../../../docs/settings-matrix.md) as code.
/// To add a new setting, sync three places: add one <see cref="Field{T}"/> here, add one property on the corresponding POCO,
/// and add one line to the assembly logic in <see cref="ConfigLoader"/>. Forgetting the third makes
/// <see cref="ConfigLoader.UnresolvedFields"/> (the `Field` self-check) emit a warning at startup
/// (the diff of the <see cref="AllFields"/> reflection enumeration against what <see cref="ConfigLoader.BuildRootConfig"/> resolved).
/// </para>
/// </summary>
public static class Schema
{
	private static IReadOnlyList<Field>? allFields;

	/// <summary>
	/// Enumerates and returns every <see cref="Field"/> under `Schema` via reflection. It scans every
	/// `public static readonly Field` field of nested static classes such as `Schema.Mount` / `Schema.FileSystem`.
	/// A newly added Field is included automatically (= a missing declaration is easy to notice).
	/// The result is cached within the process.
	/// </summary>
	public static IReadOnlyList<Field> AllFields {
		get {
			allFields ??= CollectAll();
			return allFields;
		}
	}

	private static List<Field> CollectAll() {
		var result = new List<Field>();
		foreach (var nested in typeof(Schema).GetNestedTypes(BindingFlags.Public | BindingFlags.Static)) {
			foreach (var fi in nested.GetFields(BindingFlags.Public | BindingFlags.Static)) {
				if (!typeof(Field).IsAssignableFrom(fi.FieldType)) {
					continue;
				}
				if (fi.GetValue(null) is Field f) {
					result.Add(f);
				}
			}
		}
		return result;
	}

	/// <summary>
	/// Scope name = `root`. Holds top-level bool flags that do not belong to a scope (= not written to TOML or DB, CLI only),
	/// such as `Help` / `Clean`. They show up in `Schema.AllFields` reflection enumeration under keys like `root.help`, but
	/// unlike Setting / Logging etc., `Help` / `Clean` are placed directly as properties on <see cref="RootConfig"/>
	/// (they are not at the granularity of a dedicated aggregate Config).
	/// </summary>
	public static class Root
	{
		public static readonly BoolField Help = new() {
			Scope = "root",
			Key = "help",
			CliOptions = ["-?", "-h", "--help"],
			SaveTo = SaveTarget.None,
			DefaultFn = () => false,
			Comment = "Show help and exit",
		};

		/// <summary>
		/// mkfs only. With <c>--clean</c>, ignore the existing <c>pgfs.toml</c> + DROP DATABASE → re-create. It has no short form
		/// (only an explicit flag, to prevent an unintended `--clean`). Accepted but has no effect on mount / assign.
		/// </summary>
		public static readonly BoolField Clean = new() {
			Scope = "root",
			Key = "clean",
			CliOptions = ["--clean"],
			SaveTo = SaveTarget.None,
			AppliesTo = Tool.Mkfs,
			DefaultFn = () => false,
			Comment = "Re-create from scratch: ignore existing pgfs.toml and DROP DATABASE before re-init (mkfs only)",
		};
	}

	public static class Mount
	{
		public static readonly StringField MountPoint = new() {
			Scope = "mount",
			Key = "mount_point",
			CliOptions = ["-m", "--mount-point"],
			SaveTo = SaveTarget.File,
			AppliesTo = Tool.Mount | Tool.Assign,
			ArgName = "<path>",
			DefaultFn = () => System.OperatingSystem.IsWindows() ? "P:" : "/mnt/pgfs",
			Comment = "Specifies the mount point",
		};

		/// <summary>
		/// The maximum number of bytes in a single FUSE WRITE request (<c>fuse_conn_info.max_write</c>).
		/// In pgfs one FUSE WRITE = one <c>Api.WriteData</c> transaction, so this value is the granularity of a
		/// write transaction. <b>The default <c>0</c> means "do not set it" and leaves it to libfuse's
		/// negotiation</b>.
		/// <para>
		/// Measured (Linux 5.14 + libfuse 3.10): **libfuse3 negotiates up to the kernel limit (FUSE_MAX_PAGES =
		/// 1 MiB) by default**, so even left at the default the WRITEs arrive in 1 MiB units (= the same as pgfs's
		/// chunk size). This item is therefore not a knob "to make it faster" but one for when you **want to lower
		/// it under a memory constraint, or to pin it explicitly on an environment whose default is smaller because
		/// of a kernel or libfuse version difference**. The detailed measurements are in
		/// [performance.md](../../../../docs/design/performance.md).
		/// </para>
		/// <para>
		/// Note: libfuse3 **does not accept** <c>-o max_write=…</c> (fuse_new fails). The only way is to set it on
		/// <c>fuse_conn_info</c> in the init callback, which is why pgfs carries it as a configuration item.
		/// </para>
		/// </summary>
		public static readonly IntField MaxWrite = new() {
			Scope = "mount",
			Key = "max_write",
			CliOptions = ["--max-write"],
			SaveTo = SaveTarget.File,
			AppliesTo = Tool.Mount,
			DefaultFn = () => 0,
			Comment = "Max bytes per FUSE write request (0 = let libfuse negotiate; kernel caps at 1MiB)",
		};

		public static readonly IntField CacheMaxEntries = new() {
			Reload = ReloadPolicy.Live,
			Scope = "mount",
			Key = "cache_max_entries",
			CliOptions = ["--cache-max-entries"],
			SaveTo = SaveTarget.File,
			AppliesTo = Tool.Mount | Tool.Assign,
			DefaultFn = () => 1024,
			Comment = "Specifies the number of entries to cache in memory",
		};

		public static readonly LongField CacheDataMaxBytes = new() {
			Reload = ReloadPolicy.Live,
			Scope = "mount",
			Key = "cache_data_max_bytes",
			CliOptions = ["--cache-data-max-bytes"],
			SaveTo = SaveTarget.File,
			AppliesTo = Tool.Mount | Tool.Assign,
			DefaultFn = () => 64L * 1024 * 1024,
			Comment = "Max bytes of file content (data_chunk) cached in memory (0 = disabled)",
		};

		/// <summary>
		/// The TTL (in milliseconds) of the negative lookup (ENOENT) cache. The default 0 disables it.
		/// Enabling it caches the lookup result for "a path that does not exist" for the length of the TTL, which
		/// cuts database round trips out of create-heavy workloads (rsync and friends issue about 6 non-existence
		/// SELECTs per file). This client's own create / rename invalidate it immediately, so it is safe with a
		/// single client. **A file another client created becomes visible up to one TTL late** (with
		/// `database.notify_enabled` the notification invalidates it immediately). The trade-off is spelled out in
		/// docs/design/performance.md.
		/// </summary>
		public static readonly IntField NegativeCacheTtlMs = new() {
			Reload = ReloadPolicy.Live,
			Scope = "mount",
			Key = "negative_cache_ttl_ms",
			CliOptions = ["--negative-cache-ttl-ms"],
			SaveTo = SaveTarget.File,
			AppliesTo = Tool.Mount | Tool.Assign,
			DefaultFn = () => 0,
			Comment = "TTL (ms) for caching negative (ENOENT) lookups. 0 = disabled. Files created by other clients may stay invisible up to TTL",
		};

		/// <summary>
		/// Enables the write-back cache. The default <c>false</c> is the traditional write-through
		/// (1 FUSE write = 1 transaction).
		/// <para>
		/// <b>Why it helps</b>: writing 128 KiB at a time into a 1 MiB chunk row rewrites **the whole TOAST chain
		/// on every partial update**, because bytea is TOAST-able (read-modify-write amplification). Write-back
		/// assembles the chunk in memory and makes it **one chunk = one statement**, so the amplification is gone.
		/// The projection from the measurements is **6.5× on a single PG / 8.1× on Citus rf=2**
		/// ([performance.md](../../../../docs/design/performance.md)).
		/// </para>
		/// <para>
		/// <b>The price</b>: whatever has not been flushed is lost if the process dies before the flush (that is the
		/// essence of write-back). The loss window is bounded by <see cref="WriteBackMaxBytes"/> /
		/// <see cref="WriteBackIntervalMs"/>. `fsync` and `close` flush synchronously, so the POSIX durability
		/// contract is kept. That is why the default is off.
		/// </para>
		/// </summary>
		public static readonly BoolField WriteBack = new() {
			Reload = ReloadPolicy.Live,
			Scope = "mount",
			Key = "write_back",
			CliOptions = ["--write-back"],
			SaveTo = SaveTarget.File,
			AppliesTo = Tool.Mount | Tool.Assign,
			DefaultFn = () => false,
			Comment = "Buffer writes in memory and flush one transaction per file (faster, but unflushed data is lost on crash)",
		};

		/// <summary>
		/// The cap on dirty bytes. Above it **the writing thread flushes before it continues** (back-pressure).
		/// It is **a separate budget** from the read cache budget <see cref="CacheDataMaxBytes"/> (dirty data cannot
		/// be thrown away, so it is not subject to LRU eviction).
		/// </summary>
		public static readonly LongField WriteBackMaxBytes = new() {
			Reload = ReloadPolicy.Live,
			Scope = "mount",
			Key = "write_back_max_bytes",
			CliOptions = ["--write-back-max-bytes"],
			SaveTo = SaveTarget.File,
			AppliesTo = Tool.Mount | Tool.Assign,
			DefaultFn = () => 64L * 1024 * 1024,
			Comment = "Max dirty bytes held in memory before a write blocks to flush (write_back only)",
		};

		/// <summary>
		/// How long (in milliseconds) dirty data may be left alone. A background flush runs at this interval.
		/// <c>0</c> disables the time trigger (= the only flush triggers left are exceeding
		/// <see cref="WriteBackMaxBytes"/>, `fsync`, `close` and unmount).
		/// </summary>
		public static readonly IntField WriteBackIntervalMs = new() {
			Reload = ReloadPolicy.Live,
			Scope = "mount",
			Key = "write_back_interval_ms",
			CliOptions = ["--write-back-interval-ms"],
			SaveTo = SaveTarget.File,
			AppliesTo = Tool.Mount | Tool.Assign,
			DefaultFn = () => 1000,
			Comment = "Background flush interval in milliseconds for dirty data (0 = no time trigger)",
		};

		/// <summary>
		/// Enables metadata write-back. The default is <c>false</c>.
		/// <para>
		/// <b>Prerequisite</b>: <see cref="WriteBack"/> (data write-back) must be enabled. **Turning this one on
		/// alone emits a warning and is treated as disabled** (the flush trigger for pending inodes rides on the
		/// same background loop as the data side).
		/// </para>
		/// <para>
		/// <b>What changes</b>: the inodes this mount creates (<c>create</c> / <c>mkdir</c> / <c>symlink</c>) are
		/// held in the ledger as pending, and <c>chmod</c> / <c>chown</c> / <c>utimens</c> / a target-less
		/// <c>rename</c> on such an inode coalesce in the ledger. The aim is to fold things into **one file = one
		/// tx** (inode INSERT + data row + chunk + attributes + audit), dropping a sequence such as rsync's
		/// "create -> write -> close -> chmod -> utimens -> rename" into a single background flush as a whole.
		/// Operations on an inode that is already in the database **all stay write-through**.
		/// </para>
		/// <para>
		/// <b>The price</b>: <c>close</c> no longer flushes synchronously (<c>fsync</c> / <c>fsyncdir</c> are the
		/// only hard barriers), so during the loss window **whole files** disappear even after they were closed.
		/// The gain is confined to "many small files × Citus / a high-latency database" (a projection of about
		/// 2.1×), hence the default off. The details are in
		/// [runtime-control-plane.md §metadata write-back](../../../../docs/design/runtime-control-plane.md).
		/// </para>
		/// </summary>
		public static readonly BoolField WriteBackMetadata = new() {
			Reload = ReloadPolicy.Live,
			Scope = "mount",
			Key = "write_back_metadata",
			CliOptions = ["--write-back-metadata"],
			SaveTo = SaveTarget.File,
			AppliesTo = Tool.Mount | Tool.Assign,
			DefaultFn = () => false,
			Comment = "Also defer metadata (create/mkdir/symlink + attrs on them) and flush one transaction per file. Requires write_back. close() no longer flushes: only fsync does",
		};

		/// <summary>
		/// Whether a create with <c>O_EXCL</c> (= <c>CREATE_NEW</c>) is subject to write-back.
		/// <list type="bullet">
		///   <item><c>write_through</c> (the default): creates synchronously rather than making it pending, and
		///     leaves the exclusion decision to the database's unique constraint</item>
		///   <item><c>defer</c>: makes it pending. **Exclusion within the same mount is maintained by the ledger**,
		///     but **cross-client exclusion is lost** (another mount cannot see a pending inode, so two clients'
		///     <c>O_EXCL</c> creates both succeed)</item>
		/// </list>
		/// <para>
		/// <c>rsync</c> and <c>cp</c> both use <c>O_CREAT|O_EXCL</c>, so with the default the metadata write-back
		/// coalescing never happens even once (measured 1.00×). <c>defer</c> is **a tuning knob for a bulk copy
		/// known to be single-client operation**, and must not be chosen when several mounts use the same
		/// filesystem. The details are in
		/// [runtime-control-plane.md §B-1, the settled design](../../../../docs/design/runtime-control-plane.md).
		/// </para>
		/// </summary>
		public static readonly EnumField WriteBackMetadataExclusiveCreate = new() {
			Reload = ReloadPolicy.Live,
			Scope = "mount",
			Key = "write_back_metadata_exclusive_create",
			CliOptions = ["--write-back-metadata-exclusive-create"],
			SaveTo = SaveTarget.File,
			AppliesTo = Tool.Mount | Tool.Assign,
			Allowed = ["write_through", "defer"],
			DefaultFn = () => "write_through",
			ArgName = "mode",
			Comment = "How O_EXCL/CREATE_NEW creates are handled when write_back_metadata is on: write_through (keep cross-client exclusion) or defer (faster, loses cross-client exclusion)",
		};

		/// <summary>
		/// The cap on the number of pending inodes. Above it the writing / creating side flushes and waits until it
		/// is back under (back-pressure). It plays the same role as the dirty byte cap
		/// (<see cref="WriteBackMaxBytes"/>); this one bounds **the count** (in a workload of many small files the
		/// count swells before the bytes do).
		/// </summary>
		public static readonly IntField WriteBackMaxInodes = new() {
			Reload = ReloadPolicy.Live,
			Scope = "mount",
			Key = "write_back_max_inodes",
			CliOptions = ["--write-back-max-inodes"],
			SaveTo = SaveTarget.File,
			AppliesTo = Tool.Mount | Tool.Assign,
			DefaultFn = () => 4096,
			Comment = "Max pending (not yet inserted) inodes before a create blocks to flush (write_back_metadata only)",
		};

		/// <summary>
		/// **The maximum time (in milliseconds) it is acceptable to wait for a flush to succeed**. Used in two places:
		/// <list type="number">
		///   <item>The upper bound on blocking write / create under back-pressure (exceeding
		///     <see cref="WriteBackMaxBytes"/> / <see cref="WriteBackMaxInodes"/>). **It always releases after this
		///     long** even if the flush keeps failing (blocking indefinitely cannot be told apart from the whole
		///     mount hanging)</item>
		///   <item>The deadline for retrying FlushAll in order to "write everything out" at unmount. Past it,
		///     **what is lost is enumerated in the Error log** and <c>mount.pgfs</c> exits non-zero
		///     (<c>fusermount3 -u</c> completes on the kernel side, so the filesystem cannot refuse it)</item>
		/// </list>
		/// <c>0</c> = do not wait (try one round and continue = the behaviour before metadata write-back).
		/// </summary>
		public static readonly IntField WriteBackFlushTimeoutMs = new() {
			Reload = ReloadPolicy.Live,
			Scope = "mount",
			Key = "write_back_flush_timeout_ms",
			CliOptions = ["--write-back-flush-timeout-ms"],
			SaveTo = SaveTarget.File,
			AppliesTo = Tool.Mount | Tool.Assign,
			DefaultFn = () => 30000,
			Comment = "Max time (ms) a write/create blocks on back-pressure, and the deadline for flushing everything at unmount (0 = do not wait)",
		};

		/// <summary>
		/// The fallback user name when a uname stored in pgfs_inode cannot be resolved by the OS.
		/// For safety, a resolution failure is not disguised as the running process's uid (the NFS `nobody` convention).
		/// Stored in the DB because we want a single FS-wide value.
		/// </summary>
		public static readonly StringField FallbackUname = new() {
			Scope = "mount",
			Key = "fallback_uname",
			CliOptions = ["--fallback-uname"],
			SaveTo = SaveTarget.Db,
			AppliesTo = Tool.Mount | Tool.Assign,
			ArgName = "<name>",
			DefaultFn = () => "nobody",
			Comment = "Username returned when an inode's uname cannot be resolved by the OS",
		};

		public static readonly StringField FallbackGname = new() {
			Scope = "mount",
			Key = "fallback_gname",
			CliOptions = ["--fallback-gname"],
			SaveTo = SaveTarget.Db,
			AppliesTo = Tool.Mount | Tool.Assign,
			ArgName = "<name>",
			DefaultFn = () => "nogroup",
			Comment = "Group name returned when an inode's gname cannot be resolved by the OS",
		};

		/// <summary>
		/// Foreground operation. true with `--foreground`. It has no `-f` (to avoid colliding with <see cref="Setting.File"/> /
		/// the fstab-passed `-f`; see [docs/fstab-support.md](../../../../docs/fstab-support.md) §short-form collision).
		/// </summary>
		public static readonly BoolField Foreground = new() {
			Scope = "mount",
			Key = "foreground",
			CliOptions = ["--foreground"],
			SaveTo = SaveTarget.None,
			AppliesTo = Tool.Mount,
			DefaultFn = () => false,
			Comment = "Run in foreground (do not daemonize)",
		};
	}

	public static class Database
	{
		/// <summary>
		/// The string for connecting to the target DB as the PGFS user. Accepts both the kv form and the `postgresql://` URL form
		/// (via <see cref="ConnectionField"/>). A positional[0] beginning with `postgresql:` also flows here.
		/// </summary>
		public static readonly ConnectionField Connection = new() {
			Scope = "database",
			Key = "connection",
			CliOptions = ["-c", "--connection", "--connection-string"],
			SaveTo = SaveTarget.File,
			DefaultFn = () => new Npgsql.NpgsqlConnectionStringBuilder {
				Username = "pgfs",
				Password = "pgfs",
				Host = "localhost",
				Port = 5432,
				Database = "pgfs",
				SslMode = Npgsql.SslMode.Prefer,
			},
			Comment = "Connection string for the PGFS database user",
		};

		/// <summary>
		/// mkfs only. Superuser credentials. Because it is <see cref="SaveTarget.None"/>, it is persisted neither to TOML nor DB,
		/// and is given only via CLI / Default at mkfs time. Not used by mount / assign.
		/// </summary>
		public static readonly ConnectionField SuperConnection = new() {
			Scope = "database",
			Key = "super_connection",
			CliOptions = [
				"-su", "--su", "--super", "--super-connection",
				"--super-connection-string", "--super-user",
				"--super-user-connection", "--super-user-connection-string",
			],
			SaveTo = SaveTarget.None,
			AppliesTo = Tool.Mkfs,
			DefaultFn = () => new Npgsql.NpgsqlConnectionStringBuilder {
				Username = "postgres",
				Password = "postgres",
				Host = "localhost",
				Port = 5432,
				Database = "template1",
				SslMode = Npgsql.SslMode.Prefer,
			},
			Comment = "Superuser connection for mkfs only (not persisted)",
		};

		public static readonly StringField SchemaName = new() {
			Scope = "database",
			Key = "schema",
			CliOptions = ["-s", "--schema", "--schema-name"],
			SaveTo = SaveTarget.File,
			ArgName = "<name>",
			DefaultFn = () => "public",
			Comment = "Database schema name",
		};

		public static readonly StringField Prefix = new() {
			Scope = "database",
			Key = "prefix",
			CliOptions = ["-x", "--prefix", "--table-prefix", "--table-name-prefix"],
			SaveTo = SaveTarget.File,
			ArgName = "<prefix>",
			DefaultFn = () => "pgfs_",
			Comment = "Table name prefix",
		};

		public static readonly StringField TablespaceName = new() {
			Scope = "database",
			Key = "tablespace",
			CliOptions = ["--tablespace", "--tablespace-name"],
			SaveTo = SaveTarget.Db,
			AppliesTo = Tool.Mkfs,
			ArgName = "<name>",
			DefaultFn = () => "pg_default",
			Comment = "Database tablespace name",
		};

		public static readonly StringField TablespacePath = new() {
			Scope = "database",
			Key = "tablespace_path",
			CliOptions = ["--tablespace-path"],
			SaveTo = SaveTarget.Db,
			AppliesTo = Tool.Mkfs,
			ArgName = "<path>",
			DefaultFn = () => "",
			Comment = "Database tablespace path (empty: do not create a new one)",
		};

		public static readonly IntField RetryMaxAttempts = new() {
			Reload = ReloadPolicy.Live,
			Scope = "database",
			Key = "retry_max_attempts",
			CliOptions = ["--retry-max-attempts"],
			SaveTo = SaveTarget.File,
			DefaultFn = () => 5,
			Comment = "Max attempts (incl. first) for transient connection-open failures",
		};

		public static readonly IntField RetryInitialDelayMs = new() {
			Reload = ReloadPolicy.Live,
			Scope = "database",
			Key = "retry_initial_delay_ms",
			CliOptions = ["--retry-initial-delay-ms"],
			SaveTo = SaveTarget.File,
			DefaultFn = () => 200,
			Comment = "Initial backoff delay (ms) for connection-open retry",
		};

		public static readonly IntField RetryMaxDelayMs = new() {
			Reload = ReloadPolicy.Live,
			Scope = "database",
			Key = "retry_max_delay_ms",
			CliOptions = ["--retry-max-delay-ms"],
			SaveTo = SaveTarget.File,
			DefaultFn = () => 2000,
			Comment = "Cap (ms) for exponential backoff between retries",
		};

		/// <summary>
		/// The enable flag for cross-client change notifications (via PostgreSQL LISTEN/NOTIFY). When `true`:
		/// <list type="bullet">
		///   <item>keeps one dedicated Npgsql connection open and issues <c>LISTEN {schema}_{prefix}notify</c></item>
		///   <item>appends <c>SELECT pg_notify(...)</c> to the end of every write operation, sending the delta to the other clients</item>
		///   <item>the receiving side invalidates the matching entry in <see cref="Pgfs.Core.Api.InodeCache"/>. Assign
		///   additionally calls <c>DokanInstance.NotifyUpdate</c> and friends to ask Explorer to redraw</item>
		/// </list>
		/// Default <c>false</c> (opt-in). With one PG / one client there is nothing to gain, so OFF; intended to be ON only when sharing over the network.
		/// </summary>
		public static readonly BoolField NotifyEnabled = new() {
			Scope = "database",
			Key = "notify_enabled",
			CliOptions = ["--notify", "--notify-enabled"],
			SaveTo = SaveTarget.File,
			AppliesTo = Tool.Mount | Tool.Assign,
			DefaultFn = () => false,
			Comment = "Enable cross-client change notifications via PostgreSQL LISTEN/NOTIFY",
		};

		/// <summary>
		/// mkfs-only flag. When `true`, mkfs registers the PGFS tables as Citus distributed tables
		/// (see [docs/support_for_citus.md](../../../../docs/support_for_citus.md)).
		/// <list type="bullet">
		///   <item>issues <c>CREATE EXTENSION IF NOT EXISTS citus</c> with superuser privileges</item>
		///   <item>if <c>pg_dist_node</c> is empty, registers the coordinator with <c>citus_set_coordinator_host</c> (preparing a 1-node setup)</item>
		///   <item>distributes 4 tables with <c>create_distributed_table('pgfs_inode', 'parent_id')</c> etc. (data/data_chunk are co-located; lock is a separate shard set)</item>
		///   <item>registers the local table into metadata with <c>citus_add_local_table_to_metadata('pgfs_settings')</c></item>
		/// </list>
		/// Default <c>false</c> (opt-in). Ignored if received on mount/assign (= the Api is written so the same SQL runs whether the
		/// target is Citus or not; a cross-shard rename always switches to the INSERT+DELETE path that preserves id, only when the parent changes).
		/// It is **saved to the DB** (<see cref="SaveTarget.Db"/>), but only for after-the-fact
		/// confirmation that "this DB is Citus-enabled" (mount/assign do not branch on it).
		/// </summary>
		public static readonly BoolField Citus = new() {
			Scope = "database",
			Key = "citus",
			CliOptions = ["--citus"],
			SaveTo = SaveTarget.Db,
			AppliesTo = Tool.Mkfs,
			DefaultFn = () => false,
			Comment = "Initialize the DB as Citus distributed tables (mkfs only)",
		};

		/// <summary>
		/// mkfs only. The list of Citus worker nodes. Each element is <c>"host"</c> or <c>"host:port"</c>.
		/// CLI / TOML receive it as a comma-separated string (e.g. <c>--worker "w1:5432,w2:5432"</c>). If empty, mkfs treats it as a
		/// 1-node (coordinator only) setup and sets shouldhaveshards=true on the coordinator itself.
		/// Ignored when not Citus (<see cref="Citus"/>=false).
		/// </summary>
		public static readonly StringListField Workers = new() {
			Scope = "database",
			Key = "workers",
			CliOptions = ["-w", "--worker", "--workers"],
			SaveTo = SaveTarget.File,
			AppliesTo = Tool.Mkfs,
			ArgName = "<host[:port],...>",
			DefaultFn = () => new List<string>(),
			Comment = "Citus worker nodes as comma-separated 'host[:port]' entries (mkfs only)",
		};

		/// <summary>
		/// mkfs only. Sets <c>citus.shard_count</c> to this value before calling <c>create_distributed_table</c>.
		/// <c>0</c> follows the cluster default (<c>postgresql.conf</c> and so on) = sets nothing.
		/// It only takes effect in mkfs's own session, so distributed tables of other applications using the same database are unaffected.
		/// </summary>
		public static readonly IntField ShardCount = new() {
			Scope = "database",
			Key = "shard_count",
			CliOptions = ["--shard-count"],
			SaveTo = SaveTarget.Db,
			AppliesTo = Tool.Mkfs,
			DefaultFn = () => 0,
			Comment = "Citus shard count for distributed tables (0 = cluster default, mkfs only)",
		};

		/// <summary>
		/// mkfs only. Sets <c>citus.shard_replication_factor</c> to this value before calling
		/// <c>create_distributed_table</c> (= how many nodes one shard is placed on; 1 means no replica, N means N copies).
		/// <c>0</c> follows the cluster default = sets nothing.
		/// <para>
		/// Exclusion is concentrated in <c>{prefix}lock</c> (a Citus local table), so this value is
		/// **purely a choice about storage redundancy** and the locking mechanism is not affected by it
		/// (the design is in [docs/support_for_citus.md](../../../../docs/design/support_for_citus.md)).
		/// </para>
		/// </summary>
		public static readonly IntField ShardReplicationFactor = new() {
			Scope = "database",
			Key = "shard_replication_factor",
			CliOptions = ["--shard-replication-factor", "--rf"],
			SaveTo = SaveTarget.Db,
			AppliesTo = Tool.Mkfs,
			DefaultFn = () => 0,
			Comment = "Citus copies per shard (0 = cluster default, mkfs only)",
		};

		/// <summary>
		/// mkfs only. When combined with <c>--citus</c>, also Citus-ifies **tables that already exist**
		/// (<c>create_distributed_table</c> if they are not distributed, metadata registration for the ones treated as local).
		/// The default <c>false</c>: a normal mkfs only Citus-ifies "the tables it just created", so running
		/// <c>--citus</c> later against an existing database or an existing schema does nothing. Distributing an
		/// existing table is **a heavy operation that relocates data into shards**, so it is only permitted behind
		/// an explicit flag.
		/// </summary>
		public static readonly BoolField DistributeExisting = new() {
			Scope = "database",
			Key = "distribute_existing",
			CliOptions = ["--distribute-existing"],
			SaveTo = SaveTarget.None,
			AppliesTo = Tool.Mkfs,
			DefaultFn = () => false,
			Comment = "With --citus, also Citus-ify tables that already exist (mkfs only)",
		};
	}

	public static class Logging
	{
		/// <summary>
		/// The minimum log level. Accepts `all` / `trace` / `debug` / `information` / `warning` / `error` / `critical` /
		/// `none` (`Level.Parse` parses leniently by first character, case-insensitive).
		/// </summary>
		/// <summary>
		/// The default minimum log level is <c>Information</c>, kept high enough that startup notices such as
		/// mount / unmount / FUSE options are visible (Trace is noisy because it dumps every SQL, so opt in explicitly with `trace`).
		/// </summary>
		public static readonly LogLevelField MinLevel = new() {
			Reload = ReloadPolicy.Live,
			Scope = "logging",
			Key = "level",
			CliOptions = ["--log-level", "--log-min-level", "--min-log-level"],
			SaveTo = SaveTarget.File,
			DefaultFn = () => Pgfs.Core.Logging.Level.Information,
			Comment = "Minimum log level. all / trace / debug / information / warning / error / critical / none",
		};

		/// <summary>
		/// The log output destination. Specified as a string: `stdout` / `stderr` / `none` / `<cycle>:<dir>/<pattern>`.
		/// </summary>
		public static readonly LoggingOutputField Output = new() {
			Reload = ReloadPolicy.Live,
			Scope = "logging",
			Key = "output",
			CliOptions = ["--log-output"],
			SaveTo = SaveTarget.File,
			DefaultFn = () => new Pgfs.Core.Models.SettingLoggingOutput(),
			Comment = "Log output: stdout / stderr / none / <cycle>:<dir>/<pattern>",
		};
	}

	public static class Setting
	{
		public static readonly StringField File = new() {
			Scope = "setting",
			Key = "file",
			CliOptions = ["-f", "--setting", "--setting-file"],
			SaveTo = SaveTarget.None,
			DefaultFn = () => "pgfs.toml",
			Comment = "Specify the setting file name",
		};

		/// <summary>
		/// The setting-file search path. Handled as comma-separated on every route (CLI / DB / TOML)
		/// (the TOML side can also be written as `["...", "..."]`, but the Loader normalizes a TomlArray to comma-separated).
		/// The default is CWD + the standard config directories on both Linux and Windows.
		/// </summary>
		public static readonly StringListField SearchPath = new() {
			Scope = "setting",
			Key = "search_path",
			CliOptions = ["--setting-path", "--setting-search-path", "--setting-file-path", "--setting-file-search-path"],
			SaveTo = SaveTarget.None,
			DefaultFn = () => new System.Collections.Generic.List<string> {
				".",
				System.IO.Path.Join(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".config", "pgfs"),
				System.IO.Path.Join(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".config"),
				System.IO.Path.Join(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)),
				System.IO.Path.Join(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "pgfs"),
				System.IO.Path.Join(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "pgfs"),
			},
			Comment = "Specify the setting file search path (comma-separated)",
		};
	}

	public static class FileSystem
	{
		public static readonly StringField Version = new() {
			Reload = ReloadPolicy.Format,
			Scope = "file_system",
			Key = "version",
			CliOptions = ["--version"],
			SaveTo = SaveTarget.Db,
			AppliesTo = Tool.Mkfs,
			DefaultFn = () => "1.0.0",
			Comment = "Specifies the PGFS version, currently only 1.0.0 is allowed",
		};

		public static readonly StringField VolumeLabel = new() {
			Reload = ReloadPolicy.Format,
			Scope = "file_system",
			Key = "volume_label",
			CliOptions = ["--volume-label"],
			SaveTo = SaveTarget.Db,
			AppliesTo = Tool.Mkfs,
			ArgName = "<label>",
			DefaultFn = () => "pgfs",
			Comment = "Specifies the volume label for the file system",
		};

		public static readonly LongField ClusterSize = new() {
			Reload = ReloadPolicy.Format,
			Scope = "file_system",
			Key = "cluster_size",
			CliOptions = ["--cluster-size"],
			SaveTo = SaveTarget.Db,
			AppliesTo = Tool.Mkfs,
			ArgName = "<bytes>",
			DefaultFn = () => 4096L,
			Comment = "Specifies the cluster size for the file system",
		};

		public static readonly LongField DefaultChunkSize = new() {
			Reload = ReloadPolicy.Format,
			Scope = "file_system",
			Key = "default_chunk_size",
			CliOptions = ["--default-chunk-size"],
			SaveTo = SaveTarget.Db,
			AppliesTo = Tool.Mkfs,
			ArgName = "<bytes>",
			DefaultFn = () => 1048576L,
			Comment = "Specifies the file system chunk size",
		};

		public static readonly LongField MaxFileSize = new() {
			Reload = ReloadPolicy.Format,
			Scope = "file_system",
			Key = "max_file_size",
			CliOptions = ["--max-file-size"],
			SaveTo = SaveTarget.Db,
			AppliesTo = Tool.Mkfs,
			ArgName = "<bytes>",
			DefaultFn = () => 1099511627776L,
			Comment = "Specifies the maximum file size for the file system",
		};
	}

	public static class Audit
	{
		/// <summary>
		/// on/off for the audit log (recording metadata-changing operations into {prefix}audit).
		/// <list type="bullet">
		///   <item>Because it is <see cref="SaveTarget.Db"/>, mkfs (`--audit`) saves it to <c>pgfs_settings</c>, and
		///   mount / assign read it from the DB at startup.</item>
		///   <item>When true, each mutating Api method INSERTs an audit row after success, within the same tx.</item>
		/// </list>
		/// Default <c>false</c> (opt-in). The design of record is [docs/audit-log.md](../../../../docs/audit-log.md).
		/// </summary>
		public static readonly BoolField Enabled = new() {
			Reload = ReloadPolicy.Live,
			Scope = "audit",
			Key = "enabled",
			CliOptions = ["--audit"],
			SaveTo = SaveTarget.Db,
			AppliesTo = Tool.Mkfs,
			DefaultFn = () => false,
			Comment = "Enable audit logging of metadata changes to the {prefix}audit table (mkfs sets it)",
		};
	}

	/// <summary>
	/// The descriptor for the statfs (df) mode. scope/key is <c>app.statfs</c> (app behavior = the same scope as <see cref="App"/>).
	/// It is the same for every client, so <see cref="SaveTarget.Db"/> is authoritative. The C# class name stays <c>Statfs</c>
	/// (class name != persistence key; same scope as <see cref="App.Plperlu"/> but kept as a separate C# class).
	/// </summary>
	public static class Statfs
	{
		/// <summary>
		/// The mode for whether `df` (statfs) returns the real free space of the underlying tablespace. It decides whether mkfs
		/// creates the <c>{prefix}statfs()</c> (plperlu) function. Values: <c>auto</c> / <c>require</c> / <c>nominal</c>.
		/// Whether plperlu may be used is gated higher by <see cref="App.Plperlu"/> (the matrix is in settings-and-plperlu.md).
		/// The authoritative design is [docs/df-support.md](../../../../docs/df-support.md).
		/// </summary>
		public static readonly StringField Mode = new() {
			Reload = ReloadPolicy.Live,
			Scope = "app",
			Key = "statfs",
			CliOptions = ["--statfs", "--statfs-mode"],
			SaveTo = SaveTarget.Db,
			AppliesTo = Tool.Mkfs,
			ArgName = "<auto|require|nominal>",
			DefaultFn = () => "auto",
			Comment = "Real free-space reporting for statfs/df: auto (plperlu if available else nominal), require (fail mkfs without plperlu), nominal (always nominal capacity)",
		};
	}

	/// <summary>Application-behavior settings.</summary>
	public static class App
	{
		/// <summary>
		/// Whether to allow plperlu (untrusted Perl). The **top-level gate** for whether mkfs may use plperlu for the
		/// real-measurement statfs function / tablespace auto-mkdir. Default <c>true</c> (allow). Persisted to
		/// <see cref="SaveTarget.Db"/> (reuse on a mkfs re-run + a record). CLI: <c>--plperlu [true|false]</c>
		/// (canonical, bare=true) / <c>--allow-plperlu</c> (bare=allow) / <c>--deny-plperlu</c> (bare=deny).
		/// The matrix is in [docs/settings-and-plperlu.md](../../../../docs/settings-and-plperlu.md).
		/// </summary>
		public static readonly BoolField Plperlu = new() {
			Scope = "app",
			Key = "plperlu",
			CliOptions = ["--plperlu", "--allow-plperlu"],
			NegatedCliOptions = ["--deny-plperlu"],
			AcceptsInlineBool = true,
			SaveTo = SaveTarget.Db,
			AppliesTo = Tool.Mkfs,
			DefaultFn = () => true,
			Comment = "Allow untrusted plperlu (real statfs free-space + tablespace auto-mkdir). --plperlu [true|false] / --allow-plperlu / --deny-plperlu",
		};
	}
}
