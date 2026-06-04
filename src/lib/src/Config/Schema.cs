namespace Pgfs.Lib.Config;

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
	private static IReadOnlyList<Field>? _all;

	/// <summary>
	/// Enumerates and returns every <see cref="Field"/> under `Schema` via reflection. It scans every
	/// `public static readonly Field` field of nested static classes such as `Schema.Mount` / `Schema.FileSystem`.
	/// A newly added Field is included automatically (= a missing declaration is easy to notice).
	/// The result is cached within the process.
	/// </summary>
	public static IReadOnlyList<Field> AllFields {
		get {
			_all ??= CollectAll();
			return _all;
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

		public static readonly IntField CacheMaxEntries = new() {
			Scope = "mount",
			Key = "cache_max_entries",
			CliOptions = ["--cache-max-entries"],
			SaveTo = SaveTarget.File,
			AppliesTo = Tool.Mount | Tool.Assign,
			DefaultFn = () => 1024,
			Comment = "Specifies the number of entries to cache in memory",
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
			Scope = "database",
			Key = "retry_max_attempts",
			CliOptions = ["--retry-max-attempts"],
			SaveTo = SaveTarget.File,
			DefaultFn = () => 5,
			Comment = "Max attempts (incl. first) for transient connection-open failures",
		};

		public static readonly IntField RetryInitialDelayMs = new() {
			Scope = "database",
			Key = "retry_initial_delay_ms",
			CliOptions = ["--retry-initial-delay-ms"],
			SaveTo = SaveTarget.File,
			DefaultFn = () => 200,
			Comment = "Initial backoff delay (ms) for connection-open retry",
		};

		public static readonly IntField RetryMaxDelayMs = new() {
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
		///   <item>appends <c>SELECT pg_notify(...)</c> at the end of each write operation to stream the diff to other clients</item>
		///   <item>the receiving side invalidates the matching entry in <see cref="Pgfs.Lib.Api.InodeCache"/>. On Assign it additionally
		///   calls <c>DokanInstance.NotifyUpdate</c> etc. to ask Explorer to repaint</item>
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
			Scope = "logging",
			Key = "level",
			CliOptions = ["--log-level", "--log-min-level", "--min-log-level"],
			SaveTo = SaveTarget.File,
			DefaultFn = () => Pgfs.Lib.Logging.Level.Information,
			Comment = "Minimum log level. all / trace / debug / information / warning / error / critical / none",
		};

		/// <summary>
		/// The log output destination. Specified as a string: `stdout` / `stderr` / `none` / `<cycle>:<dir>/<pattern>`.
		/// </summary>
		public static readonly LoggingOutputField Output = new() {
			Scope = "logging",
			Key = "output",
			CliOptions = ["--log-output"],
			SaveTo = SaveTarget.File,
			DefaultFn = () => new Pgfs.Lib.Models.SettingLoggingOutput(),
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
			Scope = "file_system",
			Key = "version",
			CliOptions = ["--version"],
			SaveTo = SaveTarget.Db,
			AppliesTo = Tool.Mkfs,
			DefaultFn = () => "1.0.0",
			Comment = "Specifies the PGFS version, currently only 1.0.0 is allowed",
		};

		public static readonly StringField VolumeLabel = new() {
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
