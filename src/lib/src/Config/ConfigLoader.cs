namespace Pgfs.Lib.Config;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// The settings resolver. It integrates values from the four sources CLI / TOML / DB / Default and returns one value per <see cref="Field{T}"/>.
///
/// <para>
/// At construction it accumulates the values obtainable from each source into internal dictionaries, and on a <see cref="Resolve{T}"/>
/// call returns them in priority order (CLI &gt; TOML &gt; DB &gt; <see cref="Field{T}.DefaultFn"/>). CLI parsing handles
/// helper-context detection, the comma split of `-o key=val,...`, and feeding positional[1] → `mount.mount_point`.
/// </para>
/// </summary>
public sealed class ConfigLoader
{
	// Resolved raw strings. Holds a value that came from any of CLI / TOML / DB, keyed by full-key.
	private readonly Dictionary<string, string> rawByFullKey = new();

	// Resolved bool flags (a CLI flag given alone with no value). A bool with a value goes into rawByFullKey as true/false.
	private readonly HashSet<string> cliBoolFlags = new();

	// The FullKeys of Fields that Resolve() actually touched. Used by the `Field` self-check (UnresolvedFields) to detect
	// dead config "declared in Schema but never referenced from the BuildRootConfig path".
	private readonly HashSet<string> resolvedFullKeys = new();

	/// <summary>Warnings collected during parsing. Accumulated here instead of `Console.Error.WriteLine`.</summary>
	public List<string> Warnings { get; } = new();

	/// <summary>
	/// Accumulation of the FUSE options from `-o key=val,flag,...` that are forwarded verbatim to libfuse
	/// (`allow_other` / `default_permissions` / `ro` etc.). Copied into <see cref="MountConfig.FuseFlags"/>.
	/// What is forwarded / ignored / routed to a pgfs Field is classified by the compatibility map in
	/// <see cref="ParseDashOOptions"/> (the design is docs/Mount.md §mount options).
	/// </summary>
	public List<string> FuseFlags { get; } = new();

	/// <summary>Whether it was launched as a mount(8) helper context (parent comm = "mount" and a positional present).</summary>
	public bool IsHelperContext { get; }

	/// <summary>The list of Fields this scope handles (used for CLI / TOML / DB matching).</summary>
	private readonly IReadOnlyList<Field> fields;

	/// <summary>Reverse lookup from a CLI flag to its Field (expands all CliOptions flags, case-insensitive).</summary>
	private readonly Dictionary<string, Field> fieldByCliFlag;

	/// <summary>Reverse lookup from a negated bool alias (NegatedCliOptions) to its Field. On a match, the bool is set to false.</summary>
	private readonly Dictionary<string, Field> fieldByNegatedCliFlag;

	/// <summary>Reverse lookup from a normalized `-o key` (`_` → `-`) to its Field.</summary>
	private readonly Dictionary<string, Field> fieldByDashOName;

	/// <summary>Reverse lookup from FullKey to its Field.</summary>
	private readonly Dictionary<string, Field> fieldByFullKey;

	/// <summary>The path of the TOML actually loaded. null if it was not found / not specified.</summary>
	public string? ResolvedTomlPath { get; private set; }

	/// <summary>
	/// Initialization. Parses <paramref name="args"/>, resolves <c>setting.file</c> / <c>setting.search_path</c> from the CLI,
	/// and goes to read the TOML (handling the chicken-and-egg internally). If <paramref name="store"/> is given, it also pulls
	/// the values of <see cref="SaveTarget.Db"/> Fields. null means "do not use that source".
	///
	/// Resolution priority is CLI &gt; TOML &gt; DB &gt; Default. The phases achieve this by running CLI first (to resolve setting.file),
	/// then applying TOML / DB in an "add only if a higher-priority source has not already set a value" manner.
	///
	/// If <paramref name="skipToml"/> is true, Phase 2 (TOML loading) is skipped entirely. Used for mkfs's
	/// <c>--clean</c> when you intentionally want to ignore the existing <c>pgfs.toml</c>.
	/// </summary>
	public ConfigLoader(string[] args, IReadOnlyList<Field> fields, ConfigStore? store, bool skipToml = false) {
		this.fields = fields;
		this.fieldByCliFlag = new(System.StringComparer.OrdinalIgnoreCase);
		foreach (var f in fields) {
			foreach (var opt in f.CliOptions) {
				this.fieldByCliFlag[opt] = f;
			}
		}
		this.fieldByNegatedCliFlag = new(System.StringComparer.OrdinalIgnoreCase);
		foreach (var f in fields) {
			foreach (var opt in f.NegatedCliOptions) {
				this.fieldByNegatedCliFlag[opt] = f;
			}
		}
		this.fieldByDashOName = new(System.StringComparer.OrdinalIgnoreCase);
		foreach (var f in fields) {
			this.fieldByDashOName[f.EffectiveDashOName] = f;
		}
		this.fieldByFullKey = new();
		foreach (var f in fields) {
			this.fieldByFullKey[f.FullKey] = f;
		}

		this.IsHelperContext = IsMountHelperContext(args);

		// Phase 1: CLI (highest priority). Running this first makes the CLI's `setting.file` / `setting.search_path`
		// visible when Phase 2 resolves the TOML path.
		ParseCli(args);

		// Phase 2: TOML. `setting.file` prefers what was passed on the CLI, otherwise Default.
		// When `skipToml` is set, do nothing (= the mode that intentionally ignores an existing pgfs.toml on mkfs `--clean`).
		if (!skipToml) {
			this.ResolvedTomlPath = ResolveTomlPath();
			if (this.ResolvedTomlPath != null) {
				LoadFromToml(this.ResolvedTomlPath);
			}
		}

		// Phase 3: DB (lowest priority). Pulled in here if a store was given. Attach it later via WithStore.
		if (store != null) {
			this.ApplyStoreFields(store);
		}
	}

	/// <summary>
	/// A fluent API to apply the DB source (Phase 3) afterward to a Loader that has already parsed CLI / TOML.
	/// Reads the <see cref="SaveTarget.Db"/> Fields from <paramref name="store"/> and fills only the keys not set by
	/// CLI / TOML (the higher-priority precedence is unchanged). Returns <c>this</c> so it can be chained.
	///
	/// <para>
	/// In the two-stage build (a lite Loader resolves <c>database.*</c> → build a <see cref="ConfigStore"/> from that connection),
	/// calling this instead of discarding the lite Loader and rebuilding lets the CLI parse / TOML read happen only once
	/// (mount / assign's <c>BuildRootConfig</c>). It is not idempotent (applying the same store twice yields the same result
	/// but is wasteful, so call it once).
	/// </para>
	/// </summary>
	public ConfigLoader WithStore(ConfigStore store) {
		this.ApplyStoreFields(store);
		return this;
	}

	/// <summary>The body of Phase 3 (DB). Reads the <see cref="SaveTarget.Db"/> Fields and fills only the unset keys.</summary>
	private void ApplyStoreFields(ConfigStore store) {
		var dbFields = this.fields.Where(f => f.SaveTo == SaveTarget.Db).ToList();
		if (dbFields.Count == 0) {
			return;
		}
		try {
			foreach (var kv in store.LoadAll(dbFields)) {
				// Do not overwrite if a higher-priority source (CLI / TOML) has already set it.
				if (!this.rawByFullKey.ContainsKey(kv.Key)) {
					this.rawByFullKey[kv.Key] = kv.Value;
				}
			}
		} catch (System.Exception ex) {
			this.Warnings.Add($"failed to read settings from the DB (using Default values): {ex.Message}");
		}
	}

	/// <summary>
	/// Searches for the actual TOML path, starting from the <see cref="Schema.Setting.File"/> passed on the CLI (or the Default).
	/// Rules: (1) empty file name → null, (2) adopt it if it exists as-is, (3) null if an absolute path does not exist,
	/// (4) if it is a relative path, try <see cref="Schema.Setting.SearchPath"/> in order.
	/// </summary>
	private string? ResolveTomlPath() {
		string fileName;
		if (this.rawByFullKey.TryGetValue(Schema.Setting.File.FullKey, out var fromCli)) {
			fileName = fromCli;
		} else {
			fileName = Schema.Setting.File.DefaultFn();
		}
		if (string.IsNullOrEmpty(fileName)) {
			return null;
		}
		if (File.Exists(fileName)) {
			return fileName;
		}
		if (Path.IsPathRooted(fileName)) {
			return null;
		}
		List<string> searchPath;
		if (this.rawByFullKey.TryGetValue(Schema.Setting.SearchPath.FullKey, out var fromCliSp)) {
			searchPath = Schema.Setting.SearchPath.Parse(fromCliSp);
		} else {
			searchPath = Schema.Setting.SearchPath.DefaultFn();
		}
		foreach (var dir in searchPath) {
			var fullPath = Path.Combine(dir, fileName);
			if (File.Exists(fullPath)) {
				return fullPath;
			}
		}
		return null;
	}

	/// <summary>Resolves and returns the field's value in the order CLI / TOML / DB / Default.</summary>
	public T Resolve<T>(Field<T> field) {
		this.resolvedFullKeys.Add(field.FullKey);
		if (this.rawByFullKey.TryGetValue(field.FullKey, out var raw)) {
			return field.Parse(raw);
		}
		if (this.cliBoolFlags.Contains(field.FullKey) && field is BoolField b) {
			return (T)(object)true;
		}
		return field.DefaultFn();
	}

	/// <summary>Builds and returns the resolved MountConfig POCO.</summary>
	public MountConfig BuildMountConfig() {
		return new MountConfig {
			MountPoint = this.Resolve(Schema.Mount.MountPoint),
			CacheMaxEntries = this.Resolve(Schema.Mount.CacheMaxEntries),
			FallbackUname = this.Resolve(Schema.Mount.FallbackUname),
			FallbackGname = this.Resolve(Schema.Mount.FallbackGname),
			Foreground = this.Resolve(Schema.Mount.Foreground),
			FuseFlags = new List<string>(this.FuseFlags),
		};
	}

	/// <summary>Builds and returns the resolved FileSystemConfig POCO.</summary>
	public FileSystemConfig BuildFileSystemConfig() {
		return new FileSystemConfig {
			Version = this.Resolve(Schema.FileSystem.Version),
			VolumeLabel = this.Resolve(Schema.FileSystem.VolumeLabel),
			ClusterSize = this.Resolve(Schema.FileSystem.ClusterSize),
			DefaultChunkSize = this.Resolve(Schema.FileSystem.DefaultChunkSize),
			MaxFileSize = this.Resolve(Schema.FileSystem.MaxFileSize),
		};
	}

	/// <summary>
	/// Returns whether the field's value was explicitly given from one of CLI / TOML / DB (= not derived from
	/// <see cref="Field{T}.DefaultFn"/>).
	/// </summary>
	public bool WasProvided(Field field) {
		return this.rawByFullKey.ContainsKey(field.FullKey) || this.cliBoolFlags.Contains(field.FullKey);
	}

	/// <summary>
	/// Enumerates the values actually given from CLI / TOML / DB in the form "<c>scope.key = value</c>" (Default values are excluded).
	/// For logging at startup which parameter was interpreted how. The Password in a connection string is masked.
	/// </summary>
	public IEnumerable<string> DescribeProvided() {
		foreach (var kv in this.rawByFullKey.OrderBy(k => k.Key)) {
			yield return $"{kv.Key} = {MaskSecret(kv.Key, kv.Value)}";
		}
	}

	private static string MaskSecret(string fullKey, string value) {
		if (!fullKey.Contains("connection")) {
			return value;
		}
		return System.Text.RegularExpressions.Regex.Replace(value, "(?i)(password\\s*=)[^;]*", "$1***");
	}

	/// <summary>
	/// Reconstructs the settings given from CLI/TOML/DB into a single mkfs-style command line (a reference for distribution to
	/// other clients). Excludes <see cref="SaveTarget.None"/> items (`--clean` / `--super` / `setting.file` etc.) and masks the
	/// Password in a connection string. mkfs embeds it in the leading comment of the generated TOML.
	/// </summary>
	public string DescribeMkfsCommandLine() {
		var parts = new List<string> { "mkfs.pgfs" };
		foreach (var kv in this.rawByFullKey.OrderBy(k => k.Key)) {
			if (!this.fieldByFullKey.TryGetValue(kv.Key, out var f)) {
				continue;
			}
			if (f.SaveTo == SaveTarget.None) {
				continue;
			}
			var flag = PreferredFlag(f);
			if (f.IsBool) {
				if (kv.Value == "true") {
					parts.Add(flag);
					continue;
				}
				parts.Add($"{flag} false");
				continue;
			}
			parts.Add($"{flag} \"{MaskSecret(kv.Key, kv.Value)}\"");
		}
		return string.Join(" ", parts);
	}

	/// <summary>The preferred display flag of a Field (the first long form `--xxx`, else the first, else the FullKey).</summary>
	private static string PreferredFlag(Field f) {
		foreach (var o in f.CliOptions) {
			if (o.StartsWith("--", System.StringComparison.Ordinal)) {
				return o;
			}
		}
		if (f.CliOptions.Length > 0) {
			return f.CliOptions[0];
		}
		return f.FullKey;
	}

	/// <summary>Aligns <paramref name="target"/>'s server (Host/Port/SslMode) to <paramref name="source"/>.</summary>
	private static void InheritServerFrom(Npgsql.NpgsqlConnectionStringBuilder target, Npgsql.NpgsqlConnectionStringBuilder source) {
		target.Host = source.Host;
		target.Port = source.Port;
		target.SslMode = source.SslMode;
	}

	/// <summary>Builds and returns the resolved DatabaseConfig POCO.</summary>
	public DatabaseConfig BuildDatabaseConfig() {
		var connection = this.Resolve(Schema.Database.Connection);
		var superConnection = this.Resolve(Schema.Database.SuperConnection);
		// If --super was not specified, the super connection defaults to localhost. That is a source of a split-brain accident
		// ("--connection points at a remote, but super DROP/CREATEs a different DB on localhost"), so inherit the user connection's
		// Host/Port/SslMode to point at the same server (only the credentials and maintenance DB stay at the super default).
		// If --super was specified, respect it completely.
		if (!this.WasProvided(Schema.Database.SuperConnection)) {
			InheritServerFrom(superConnection, connection);
		}
		return new DatabaseConfig {
			Connection = connection,
			SuperConnection = superConnection,
			SchemaName = this.Resolve(Schema.Database.SchemaName),
			Prefix = this.Resolve(Schema.Database.Prefix),
			TablespaceName = this.Resolve(Schema.Database.TablespaceName),
			TablespacePath = this.Resolve(Schema.Database.TablespacePath),
			RetryMaxAttempts = this.Resolve(Schema.Database.RetryMaxAttempts),
			RetryInitialDelayMs = this.Resolve(Schema.Database.RetryInitialDelayMs),
			RetryMaxDelayMs = this.Resolve(Schema.Database.RetryMaxDelayMs),
			NotifyEnabled = this.Resolve(Schema.Database.NotifyEnabled),
			Citus = this.Resolve(Schema.Database.Citus),
			Workers = this.Resolve(Schema.Database.Workers).Select(ParseWorker).ToList(),
		};
	}

	/// <summary>
	/// Splits `"host"` or `"host:port"` into `(string Host, int Port)`. Port defaults to 5432 when omitted.
	/// An empty string or malformed input throws <see cref="System.FormatException"/>.
	/// </summary>
	private static (string Host, int Port) ParseWorker(string spec) {
		if (string.IsNullOrWhiteSpace(spec)) {
			throw new System.FormatException("worker spec is empty");
		}
		var trimmed = spec.Trim();
		var colon = trimmed.LastIndexOf(':');
		if (colon < 0) {
			return (trimmed, 5432);
		}
		var host = trimmed.Substring(0, colon);
		var portStr = trimmed.Substring(colon + 1);
		if (string.IsNullOrEmpty(host)) {
			throw new System.FormatException($"worker spec '{spec}' has empty host");
		}
		if (!int.TryParse(portStr, out var port) || port <= 0 || port > 65535) {
			throw new System.FormatException($"worker spec '{spec}' has invalid port '{portStr}'");
		}
		return (host, port);
	}

	/// <summary>Builds and returns the resolved LoggingConfig POCO.</summary>
	public LoggingConfig BuildLoggingConfig() {
		return new LoggingConfig {
			MinLevel = this.Resolve(Schema.Logging.MinLevel),
			Output = this.Resolve(Schema.Logging.Output),
		};
	}

	/// <summary>Builds and returns the resolved AuditConfig POCO.</summary>
	public AuditConfig BuildAuditConfig() {
		return new AuditConfig {
			Enabled = this.Resolve(Schema.Audit.Enabled),
		};
	}

	/// <summary>Builds and returns the resolved StatfsConfig POCO.</summary>
	public StatfsConfig BuildStatfsConfig() {
		return new StatfsConfig {
			Mode = this.Resolve(Schema.Statfs.Mode),
		};
	}

	/// <summary>Builds and returns the resolved AppConfig POCO.</summary>
	public AppConfig BuildAppConfig() {
		return new AppConfig {
			Plperlu = this.Resolve(Schema.App.Plperlu),
		};
	}

	/// <summary>Builds and returns the resolved SettingFileConfig POCO.</summary>
	public SettingFileConfig BuildSettingFileConfig() {
		return new SettingFileConfig {
			File = this.Resolve(Schema.Setting.File),
			SearchPath = this.Resolve(Schema.Setting.SearchPath),
			Path = this.ResolvedTomlPath,
		};
	}

	/// <summary>Builds the <see cref="RootConfig"/> aggregating the sub-Configs in a single call.</summary>
	public RootConfig BuildRootConfig() {
		var config = new RootConfig {
			Setting = this.BuildSettingFileConfig(),
			Logging = this.BuildLoggingConfig(),
			Database = this.BuildDatabaseConfig(),
			Mount = this.BuildMountConfig(),
			FileSystem = this.BuildFileSystemConfig(),
			Audit = this.BuildAuditConfig(),
			Statfs = this.BuildStatfsConfig(),
			App = this.BuildAppConfig(),
			Help = this.Resolve(Schema.Root.Help),
			Clean = this.Resolve(Schema.Root.Clean),
		};
		// Having built every sub-Config = we have gone through the resolution path common to all tools. Any Field still
		// unresolved here is dead config "declared in Schema but not wired into BuildRootConfig", so warn about it (#13).
		foreach (var f in this.UnresolvedFields()) {
			this.Warnings.Add($"config self-check: '{f.FullKey}' is declared in Schema but is not referenced from ConfigLoader (possibly not wired into BuildRootConfig).");
		}
		return config;
	}

	/// <summary>
	/// Returns the <see cref="Schema.AllFields"/> Fields not yet touched by <see cref="Resolve{T}"/>.
	/// Called after <see cref="BuildRootConfig"/>, it lists dead config "declared in Schema yet never referenced from any
	/// Build*Config" (the body of the `Field` self-check: the diff of Schema's reflection enumeration against the Resolve record).
	/// </summary>
	public IReadOnlyList<Field> UnresolvedFields() {
		return Schema.AllFields.Where(f => !this.resolvedFullKeys.Contains(f.FullKey)).ToList();
	}

	// ------------------------------------------------------------------
	// TOML
	// ------------------------------------------------------------------

	private void LoadFromToml(string tomlPath) {
		string text;
		try {
			text = File.ReadAllText(tomlPath);
		} catch (FileNotFoundException) {
			return; // missing file is silent; expected to run on Default.
		} catch (DirectoryNotFoundException) {
			return;
		} catch (System.Exception ex) {
			this.Warnings.Add($"failed to read TOML (path: {tomlPath}): {ex.Message}");
			return;
		}
		TomlTable model;
		try {
			model = Toml.ToModel(text);
		} catch (System.Exception ex) {
			this.Warnings.Add($"failed to parse TOML (path: {tomlPath}): {ex.Message}");
			return;
		}
		foreach (var f in this.fields) {
			if (!model.TryGetValue(f.Scope, out var scopeObj) || scopeObj is not TomlTable scopeTable) {
				continue;
			}
			if (!scopeTable.TryGetValue(f.Key, out var leaf)) {
				continue;
			}
			// Do not overwrite if a higher-priority source (CLI) has already set it.
			if (this.rawByFullKey.ContainsKey(f.FullKey)) {
				continue;
			}
			this.rawByFullKey[f.FullKey] = TomlValueToString(leaf);
		}
	}

	/// <summary>
	/// Normalizes a Tomlyn model value into a string that <see cref="Field{T}.Parse"/> can take.
	/// An array (<see cref="TomlArray"/>) has its elements recursively stringified and comma-joined
	/// (= matches the form <see cref="StringListField"/> expects).
	/// </summary>
	private static string TomlValueToString(object v) {
		if (v is bool b) {
			if (b) {
				return "true";
			}
			return "false";
		}
		if (v is string s) {
			return s;
		}
		if (v is TomlArray arr) {
			var parts = new List<string>();
			foreach (var item in arr) {
				if (item == null) {
					continue;
				}
				parts.Add(TomlValueToString(item));
			}
			return string.Join(",", parts);
		}
		return v.ToString() ?? "";
	}

	// ------------------------------------------------------------------
	// CLI
	// ------------------------------------------------------------------

	private static readonly HashSet<string> MountHelperFlagsNoValue = new(System.StringComparer.Ordinal) {
		"-i", "-f", "-n", "-s", "-v",
	};
	private static readonly HashSet<string> MountHelperFlagsWithValue = new(System.StringComparer.Ordinal) {
		"-N", "-t",
	};

	// ------------------------------------------------------------------
	// -o option compatibility map (design of record: docs/Mount.md §mount options)
	//
	// Each -o key is classified into exactly one of (ParseDashOOptions):
	//   (1)  FUSE passthrough        — forwarded verbatim to libfuse (FusePassthroughFlags / FusePassthroughKv)
	//   (2)  accepted but ignored    — kernel mount layer / fstab conventions (IgnoredMountHints). Not passed to FUSE
	//   (2') userspace prefix        — x-systemd.* / x-gvfs-* etc. (UserspaceOptionPrefix). Ignored like (2)
	//   (3)  pgfs Field map          — routed to a setting Field, e.g. `-o schema=foo`
	//   (4)  unknown                 — Warning (typo detection)
	//   (5)  unsupported mount ops   — remount/bind/rbind/move (UnsupportedMountOps). An explicit Warning
	// ------------------------------------------------------------------

	/// <summary>
	/// **valueless** FUSE options valid in libfuse3. Forwarded verbatim.
	/// Carelessly forwarding a "fuse-looking" key not listed here makes <c>fuse_new</c> fail the mount with
	/// "unknown option" (e.g. <c>nonempty</c>, removed in libfuse3, is not put here but ignored in (2)).
	/// </summary>
	private static readonly HashSet<string> FusePassthroughFlags = new(System.StringComparer.Ordinal) {
		"allow_other", "allow_root", "default_permissions", "ro", "auto_unmount",
		"kernel_cache", "auto_cache",
	};

	/// <summary>**valued** FUSE options valid in libfuse3. Forwarded only in the <c>key=value</c> form.</summary>
	private static readonly HashSet<string> FusePassthroughKv = new(System.StringComparer.Ordinal) {
		"umask", "uid", "gid", "max_read", "entry_timeout", "attr_timeout",
		"fsname", "subtype", "max_write", "max_readahead",
	};

	/// <summary>
	/// mount(8) / fstab / kernel hints that are accepted but ignored. Not passed to FUSE (the kernel mount layer handles
	/// them, or they are irrelevant to pgfs). <c>rw</c> is the default; <c>nonempty</c> is the default behavior in libfuse3.
	/// </summary>
	private static readonly HashSet<string> IgnoredMountHints = new(System.StringComparer.Ordinal) {
		"_netdev", "noauto", "auto", "user", "users", "owner", "group",
		"atime", "relatime", "noatime", "strictatime", "nostrictatime",
		"nosuid", "nodev", "noexec", "exec", "suid", "dev",
		"async", "sync", "dirsync",
		// nonempty/direct_io were removed as mount-wide -o options in libfuse3 (the former is the default behavior,
		// the latter moved to the per-file fi->direct_io). Forwarding them verbatim makes fuse_new fail the whole mount
		// with "unknown option", so they are not put in (1) but ignored. Verified on the real machine that libfuse 3.14.0's
		// .so has no such option token.
		"rw", "nonempty", "direct_io",
		// mount(8)/fstab conventions (the staples seen in a real fstab). Irrelevant to FUSE, so silently accepted.
		"defaults", "nofail", "lazytime", "nolazytime",
		"mand", "nomand", "iversion", "noiversion",
		// fstab userspace comment (`comment=...`). Matched by key only, so the value is dropped.
		"comment",
	};

	/// <summary>
	/// mount(8) mount-operation options. They cannot apply to pgfs (a fresh FUSE mount), so make "unsupported" explicit,
	/// distinct from the (4) typo warning. Silently ignoring them would create the misunderstanding "I thought I remounted".
	/// <c>bind</c>/<c>rbind</c>/<c>move</c> are normally handled by mount(8) itself without calling the helper, but are made
	/// explicit too in case they are passed on a direct launch where they carry no meaning.
	/// </summary>
	private static readonly HashSet<string> UnsupportedMountOps = new(System.StringComparer.Ordinal) {
		"remount", "bind", "rbind", "move",
	};

	/// <summary>
	/// The prefix of fstab/systemd userspace-only options (<c>x-systemd.*</c> / <c>x-gvfs-*</c> / <c>x-mount.*</c> etc.).
	/// The whole prefix is folded into (2) accepted-but-ignored.
	/// </summary>
	private const string UserspaceOptionPrefix = "x-";

	private void ParseCli(string[] args) {
		if (args.Length == 0) {
			return;
		}
		var positionalIndex = 0;
		for (int i = 0; i < args.Length; ++i) {
			var arg = args[i];

			// only the mount(8) helper swallows these silently
			if (this.IsHelperContext && MountHelperFlagsNoValue.Contains(arg)) {
				continue;
			}
			if (this.IsHelperContext && MountHelperFlagsWithValue.Contains(arg)) {
				if (i + 1 < args.Length) {
					++i;
				}
				continue;
			}

			// `-o key=val,...`
			if (arg == "-o" || arg == "--options") {
				if (i + 1 >= args.Length) {
					this.Warnings.Add($"option '{arg}' requires a value, but the next argument was not found.");
					continue;
				}
				ParseDashOOptions(args[++i]);
				continue;
			}

			// Negated bool alias (e.g. --deny-plperlu = false). Bare-only (takes no value).
			if (this.fieldByNegatedCliFlag.TryGetValue(arg, out var negated)) {
				this.cliBoolFlags.Remove(negated.FullKey);
				this.rawByFullKey[negated.FullKey] = "false";
				continue;
			}

			// CLI flag matching
			if (this.fieldByCliFlag.TryGetValue(arg, out var matched)) {
				if (matched.IsBool) {
					// If AcceptsInlineBool, consume the next token as the value when it is a bool literal (true/false/...).
					if (matched.AcceptsInlineBool && i + 1 < args.Length && TryBoolLiteral(args[i + 1], out var normalized)) {
						this.rawByFullKey[matched.FullKey] = normalized;
						++i;
						continue;
					}
					this.cliBoolFlags.Add(matched.FullKey);
					this.rawByFullKey[matched.FullKey] = "true";
					continue;
				}
				if (i + 1 >= args.Length) {
					this.Warnings.Add($"option '{arg}' requires a value, but the next argument was not found.");
					continue;
				}
				this.rawByFullKey[matched.FullKey] = args[++i];
				continue;
			}

			// positional
			if (!arg.StartsWith('-')) {
				AssignPositional(positionalIndex, arg);
				++positionalIndex;
				continue;
			}

			// Unknown option: do not swallow it silently; add a Warning (so a typo like `--cutus` is noticed at startup).
			this.Warnings.Add($"ignored unknown option '{arg}' (possible typo).");
		}
	}

	/// <summary>
	/// If the string is a bool literal (true/false/1/0/yes/no/on/off, case-insensitive), outs the normalized string
	/// ("true"/"false") and returns true. An empty string or non-bool returns false (= not consumed as a value).
	/// </summary>
	private static bool TryBoolLiteral(string s, out string normalized) {
		switch (s.Trim().ToLowerInvariant()) {
			case "true": case "1": case "yes": case "on": normalized = "true"; return true;
			case "false": case "0": case "no": case "off": normalized = "false"; return true;
		}
		normalized = "";
		return false;
	}

	private void ParseDashOOptions(string optsValue) {
		foreach (var raw in optsValue.Split(',')) {
			var entry = raw.Trim();
			if (entry.Length == 0) {
				continue;
			}
			string key;
			string? val;
			var eq = entry.IndexOf('=');
			if (eq < 0) {
				key = entry;
				val = null;
			} else {
				key = entry[..eq].Trim();
				val = entry[(eq + 1)..].Trim();
			}

			// (2) mount(8)/fstab/kernel hints, accepted but ignored (not passed to FUSE).
			if (IgnoredMountHints.Contains(key)) {
				continue;
			}
			// (2') userspace-only prefix (x-systemd.* / x-gvfs-* / x-mount.* etc.) is also ignored wholesale.
			if (key.StartsWith(UserspaceOptionPrefix, System.StringComparison.Ordinal)) {
				continue;
			}
			// (5) mount-operation options that cannot apply to pgfs. Not a typo, so make it explicit with a dedicated message.
			if (UnsupportedMountOps.Contains(key)) {
				this.Warnings.Add($"-o '{key}' is unsupported in pgfs (cannot apply to a fresh FUSE mount). Ignored.");
				continue;
			}
			// (1) valueless FUSE passthrough. If a val arrives, forward it as `key=val` (allowing a last-wins override).
			if (FusePassthroughFlags.Contains(key)) {
				if (val == null) {
					this.FuseFlags.Add(key);
					continue;
				}
				this.FuseFlags.Add($"{key}={val}");
				continue;
			}
			// (1') valued FUSE passthrough (`umask=022` etc.). Value required.
			if (FusePassthroughKv.Contains(key)) {
				if (val == null) {
					this.Warnings.Add($"-o '{key}' requires a value (specify it as '{key}=value').");
					continue;
				}
				this.FuseFlags.Add($"{key}={val}");
				continue;
			}

			// (3) pgfs Field map. Normalize `-`/`_` (fstab convention) and match by dash-o name / full-key.
			var normalized = key.Replace('-', '_');
			if (this.fieldByDashOName.TryGetValue(normalized.Replace('_', '-'), out var matched)
				|| this.fieldByFullKey.TryGetValue(normalized, out matched)) {
				if (matched.IsBool) {
					if (val == null) {
						this.cliBoolFlags.Add(matched.FullKey);
						this.rawByFullKey[matched.FullKey] = "true";
						continue;
					}
					this.rawByFullKey[matched.FullKey] = val;
					continue;
				}
				if (val == null) {
					this.Warnings.Add($"-o '{key}' requires a value (specify it as '{key}=value').");
					continue;
				}
				this.rawByFullKey[matched.FullKey] = val;
				continue;
			}

			// (4) matches no class = unknown. Do not swallow it; add a Warning (typo detection).
			this.Warnings.Add($"ignored unknown -o option '{key}' (possible typo).");
		}
	}

	private void AssignPositional(int index, string value) {
		switch (index) {
			case 0: {
				// positional[0] (source = fstab column 1). A `postgresql://` URL is treated as
				// database.connection, anything else as setting.file (a TOML path). Do not overwrite
				// if an explicit flag (-c / -f / -o) already set it (CLI takes precedence over positional).
				var srcKey = Schema.Setting.File.FullKey;
				if (value.StartsWith("postgresql:", System.StringComparison.OrdinalIgnoreCase)
					|| value.StartsWith("postgres:", System.StringComparison.OrdinalIgnoreCase)) {
					srcKey = Schema.Database.Connection.FullKey;
				}
				if (!this.rawByFullKey.ContainsKey(srcKey)) {
					this.rawByFullKey[srcKey] = value;
				}
				return;
			}
			case 1: {
				// positional[1] (target) → mount.mount_point. Do not overwrite if it already came from CLI/TOML/DB.
				var fullKey = Schema.Mount.MountPoint.FullKey;
				if (!this.rawByFullKey.ContainsKey(fullKey)) {
					this.rawByFullKey[fullKey] = value;
				}
				return;
			}
			default:
				return;
		}
	}

	// ------------------------------------------------------------------
	// Helper context detection (decide via `/proc/<ppid>/comm` whether we are called as a mount(8) helper)
	// ------------------------------------------------------------------

	private static bool IsMountHelperContext(string[] args) {
		if (!ArgsHavePositional(args)) {
			return false;
		}
		var parent = GetParentProcessName();
		if (parent == null) {
			return false;
		}
		return parent == "mount";
	}

	private static bool ArgsHavePositional(string[] args) {
		foreach (var a in args) {
			if (a.Length > 0 && a[0] != '-') {
				return true;
			}
		}
		return false;
	}

	private static string? GetParentProcessName() {
		if (!System.OperatingSystem.IsLinux()) {
			return null;
		}
		try {
			long ppid = -1;
			foreach (var line in File.ReadAllLines("/proc/self/status")) {
				if (!line.StartsWith("PPid:", System.StringComparison.Ordinal)) {
					continue;
				}
				var parts = line.Split(new[] { ':', '\t', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length < 2 || !long.TryParse(parts[1], out ppid)) {
					return null;
				}
				break;
			}
			if (ppid < 0) {
				return null;
			}
			return File.ReadAllText($"/proc/{ppid}/comm").TrimEnd('\n', '\r');
		} catch {
			return null;
		}
	}
}
