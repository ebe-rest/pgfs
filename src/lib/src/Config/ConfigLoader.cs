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

	/// <summary>Warnings collected during parsing. Accumulated here instead of `Console.Error.WriteLine`.</summary>
	public List<string> Warnings { get; } = new();

	/// <summary>
	/// Accumulation of FUSE flags from `-o key=val,flag,...` such as `allow_other` / `default_permissions` / `ro` / `rw` /
	/// `nonempty` / `auto_unmount` / `suid`. Copied into <see cref="MountConfig.FuseFlags"/>.
	/// </summary>
	public List<string> FuseFlags { get; } = new();

	/// <summary>Whether it was launched as a mount(8) helper context (parent comm = "mount" and a positional present).</summary>
	public bool IsHelperContext { get; }

	/// <summary>The list of Fields this scope handles (used for CLI / TOML / DB matching).</summary>
	private readonly IReadOnlyList<Field> fields;

	/// <summary>Reverse lookup from a CLI flag to its Field (expands all CliOptions flags, case-insensitive).</summary>
	private readonly Dictionary<string, Field> fieldByCliFlag;

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

		// Phase 3: DB (lowest priority).
		if (store != null) {
			var dbFields = fields.Where(f => f.SaveTo == SaveTarget.Db).ToList();
			if (dbFields.Count > 0) {
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
		return new RootConfig {
			Setting = this.BuildSettingFileConfig(),
			Logging = this.BuildLoggingConfig(),
			Database = this.BuildDatabaseConfig(),
			Mount = this.BuildMountConfig(),
			FileSystem = this.BuildFileSystemConfig(),
			Audit = this.BuildAuditConfig(),
			Help = this.Resolve(Schema.Root.Help),
			Clean = this.Resolve(Schema.Root.Clean),
		};
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

	private static readonly HashSet<string> FstabIgnoredFlags = new(System.StringComparer.Ordinal) {
		"_netdev", "noauto", "auto", "user", "users", "owner", "group",
		"atime", "relatime", "noatime", "strictatime",
		"nosuid", "nodev", "noexec", "exec", "suid", "dev",
		"async", "sync", "dirsync",
	};

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

			// CLI flag matching
			if (this.fieldByCliFlag.TryGetValue(arg, out var matched)) {
				if (matched.IsBool) {
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

			// ignore fstab / kernel hints
			if (FstabIgnoredFlags.Contains(key)) {
				continue;
			}
			// FUSE flags (`allow_other` etc.) are buffered until they are copied into MountConfig.FuseFlags.
			if (key is "allow_other" or "default_permissions" or "ro" or "rw"
				or "nonempty" or "auto_unmount" or "suid") {
				if (val == null) {
					this.FuseFlags.Add(key);
					continue;
				}
				this.FuseFlags.Add($"{key}={val}");
				continue;
			}

			// normalize - and _ (fstab convention)
			var normalized = key.Replace('-', '_');
			if (!this.fieldByDashOName.TryGetValue(normalized.Replace('_', '-'), out var matched)
				&& !this.fieldByFullKey.TryGetValue(normalized, out matched)) {
				// may be a -o key for another scope (e.g. -o schema=foo): silent.
				continue;
			}
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
		}
	}

	private void AssignPositional(int index, string value) {
		switch (index) {
			case 0:
				// positional[0] (source) is connection / setting.file. Not handled in the Mount scope.
				return;
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
