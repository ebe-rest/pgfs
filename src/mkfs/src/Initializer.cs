namespace Pgfs.Mkfs;

using Core.Config;
using Core.Logging;
using Core.Utility;
using Npgsql;

/// <summary>
/// Initializes a PGFS filesystem on PostgreSQL.
///
/// Flow:
///   1. Connect as a superuser and create the PGFS user (only if absent).
///   2. Create the tablespace as a superuser (only if specified and absent).
///   3. Create the database as a superuser (only if absent).
///   4. Connect to the target DB as the PGFS user and create the schema (only if absent).
///   5. Create each table (pgfs_inode / pgfs_data / pgfs_data_chunk / pgfs_settings).
///   6. Insert the root inode.
///   7. UPSERT the <see cref="Schema"/> rows that have SaveTo=Db into <c>pgfs_settings</c> (<see cref="ConfigStore.Save{T}"/>).
///   8. Write the settings that have SaveTo=File out to the TOML.
///
/// Every step is **idempotent** — anything that already exists is skipped.
///
/// See <c>docs/Mkfs.md</c> for the full specification.
/// </summary>
public partial class Initializer
{
	private readonly RootConfig config;

	/// <summary>Whether a setting was given explicitly on the CLI / in the TOML read (so that an option with no effect on an existing FS can be warned about).</summary>
	private readonly Func<Field, bool> wasProvided;

	/// <summary>Whether this is an existing FS (<c>pgfs_settings</c> had rows). When true, the FS-specific settings use the DB values and are not overwritten.</summary>
	private bool existingFs;

	/// <summary>
	/// **Whether this run creates the database** (<c>--clean</c>, or the target DB is absent on the coordinator).
	/// <c>--citus</c> / <c>--worker</c> / tablespace are instructions for "build it like this", so they take effect **only when creating** -
	/// previously the "ensure user / tablespace" steps, which run before the DB existence check, ran on every <c>--worker</c> node even for an existing DB.
	/// </summary>
	private bool creatingDatabase;

	/// <summary>Connection string for connecting as a superuser (the target is a maintenance DB such as template1).</summary>
	private string superConnectionString = "";

	/// <summary>Connection string for connecting to the target DB as the PGFS user.</summary>
	private string connectionString = "";

	public Initializer(RootConfig config, Func<Field, bool>? wasProvided = null) {
		this.config = config;
		this.wasProvided = wasProvided ?? (_ => false);
	}

	public async Task InitializeAsync() {
		Logger.Information("=== PGFS filesystem creation tool ===");

		this.ResolveConnectionStrings();
		this.ValidateConfigCombinations();
		this.creatingDatabase = this.config.Clean || !await this.CoordinatorDatabaseExistsAsync();

		// --clean / --purge: look up the topology in the DB -> check the workers are reachable -> what is connected (retry / --now)
		// -> confirmation (--yes) -> delete. **Delete before ensuring the user / tablespace** (--purge stops without creating
		// anything, and the deletion only needs the super connection).
		await this.TeardownAsync();
		if (this.config.Purge) {
			Logger.Information("--purge: deleted (roles / tablespaces / the settings file are left in place)");
			return;
		}

		await this.EnsureUserAsync();           // coordinator + (when Citus) each worker, inner guard
		await this.EnsureTablespaceAsync();      // no-op for pg_default; Citus forces pg_default anyway
		await this.EnsureDatabaseAsync();         // bundles the create/topology steps internally

		// The database is ready, so finalize the connection string and switch to operating on the target DB.
		this.RebuildUserConnectionString();

		await this.EnsureSchemaAsync();          // CREATE SCHEMA on the coordinator -> propagated to workers via Citus DDL

		var prefix = this.config.Database.GetPrefix();
		// Each CreateXxxTableAsync is responsible for following a fresh table creation with
		// create_distributed_table. For an existing table it leaves distribution untouched
		// (consistent with the existing-DB case).
		await this.CreateInodeTableAsync(prefix);
		await this.CreateDataTableAsync(prefix);
		await this.CreateDataChunkTableAsync(prefix);
		await this.CreateLockTableAsync(prefix);
		await this.CreateSettingsTableAsync(prefix);
		this.AdoptExistingFsSettings();      // for an existing FS, take the FS-specific settings from the DB (statfs / population below use those values)
		await this.CreateAuditTableAsync(prefix);
		await this.CreateMountsTableAsync(prefix);
		await this.CreateStatfsFunctionsAsync(prefix);

		await this.InsertRootInodeAsync(prefix);
		this.PopulateSettingsRows();
	}

	/// <summary>
	/// Validates the combination of settings early (fail-fast before any destructive operation).
	/// The ban on <c>--citus</c> + a custom <c>--tablespace</c> was removed: CREATE DATABASE WITH TABLESPACE is now
	/// the default (the per-table TABLESPACE clause was dropped) and the tablespace is created on the coordinator + every
	/// worker, so the two can be combined (design in docs/settings-and-plperlu.md).
	/// </summary>
	private void ValidateConfigCombinations() {
		var statfsMode = this.config.Statfs.Mode;
		if (statfsMode != "auto" && statfsMode != "require" && statfsMode != "nominal") {
			throw new InvalidOperationException(
				$"--statfs='{statfsMode}' is invalid. Specify one of auto / require / nominal (see docs/df-support.md)."
			);
		}
		// require needs plperlu. Combining it with plperlu denied (--deny-plperlu) is contradictory, so fail fast.
		if (statfsMode == "require" && !this.config.App.Plperlu) {
			throw new InvalidOperationException(
				"--statfs=require and plperlu denied (--deny-plperlu) cannot be combined " +
				"(require needs plperlu; docs/settings-and-plperlu.md)."
			);
		}
	}

	// ----------------------------------------------------------------------
	// Connection string resolution
	// ----------------------------------------------------------------------

	private void ResolveConnectionStrings() {
		// Superuser connection: connect to a maintenance DB for CREATE DATABASE, not the target DB.
		var su = new NpgsqlConnectionStringBuilder(this.config.Database.SuperConnection.ConnectionString);
		if (string.IsNullOrEmpty(su.Database)) {
			su.Database = "template1";
		}
		// Name our own connection - so that it can be excluded when --clean / --purge counts "what is connected".
		if (string.IsNullOrEmpty(su.ApplicationName)) {
			su.ApplicationName = "mkfs.pgfs";
		}
		this.ownApplicationName = su.ApplicationName;
		this.superConnectionString = su.ConnectionString;

		// Regular user connection: the target DB may not exist yet at this point, so connect to a
		// maintenance DB first. After the DB is created, RebuildUserConnectionString switches to the target DB.
		this.connectionString = this.config.Database.Connection.ConnectionString;

		// Log the targets explicitly. If super and user point at different hosts, you could accidentally
		// DROP/CREATE a database on the wrong server, so make the targets visible at startup (when --super
		// is omitted it has already inherited the user's Host/Port). The super connection matters in mkfs
		// (DROP/CREATE DATABASE etc.), so its connection string is printed too, with the password masked.
		var user = this.config.Database.Connection;
		Logger.Information($"targets: user = {user.Host}:{user.Port}/{user.Database}, super = {su.Host}:{su.Port}/{su.Database}");
		Logger.Information($"  user  connection: {MaskPassword(this.connectionString)}");
		Logger.Information($"  super connection: {MaskPassword(this.superConnectionString)}");
	}

	/// <summary>Masks <c>Password=...</c> in a connection string (for logging).</summary>
	private static string MaskPassword(string connectionString) {
		return System.Text.RegularExpressions.Regex.Replace(connectionString, "(?i)(password\\s*=)[^;]*", "$1***");
	}

	private void RebuildUserConnectionString() {
		this.connectionString = this.config.Database.Connection.ConnectionString;
	}

	/// <summary>The coordinator host (or "localhost" if empty). Used for Citus topology registration.</summary>
	private string CoordinatorHost() {
		var host = this.config.Database.Connection.Host;
		return string.IsNullOrEmpty(host) switch {
			true  => "localhost",
			false => host,
		};
	}

	/// <summary>The coordinator port (or 5432 if 0). Used for Citus topology registration.</summary>
	private int CoordinatorPort() {
		var port = this.config.Database.Connection.Port;
		return port switch {
			0 => 5432,
			_ => port,
		};
	}

	/// <summary>
	/// Connection string for a super connection to the target pgfs DB on the coordinator.
	/// (<see cref="superConnectionString"/> is initialized pointing at a maintenance DB.)
	/// </summary>
	private string CoordinatorSuperPgfsConnectionString() {
		var dbName = this.config.Database.Connection.Database;
		if (string.IsNullOrEmpty(dbName)) {
			dbName = "pgfs";
		}
		var b = new NpgsqlConnectionStringBuilder(this.superConnectionString) { Database = dbName };
		return b.ConnectionString;
	}

	/// <summary>Connection string for a super connection to the given worker's maintenance DB.</summary>
	private string WorkerSuperConnectionString((string Host, int Port) worker) {
		var b = new NpgsqlConnectionStringBuilder(this.superConnectionString) {
			Host = worker.Host,
			Port = worker.Port,
		};
		return b.ConnectionString;
	}

	/// <summary>Connection string for a super connection to the target pgfs DB on the given worker.</summary>
	private string WorkerSuperPgfsConnectionString((string Host, int Port) worker) {
		var dbName = this.config.Database.Connection.Database;
		if (string.IsNullOrEmpty(dbName)) {
			dbName = "pgfs";
		}
		var b = new NpgsqlConnectionStringBuilder(this.superConnectionString) {
			Host = worker.Host,
			Port = worker.Port,
			Database = dbName,
		};
		return b.ConnectionString;
	}

	// ----------------------------------------------------------------------
	// 1. PGFS user
	// ----------------------------------------------------------------------

	private async Task EnsureUserAsync() {
		// Create the user on the coordinator + (only under Citus) on each worker too.
		await this.EnsureUserOnAsync(this.superConnectionString, "coordinator");
		// Workers only when creating the DB (an existing DB is used as it is, Citus topology included).
		if (!this.creatingDatabase) { return; }
		foreach (var worker in this.config.Database.Workers) {
			if (!this.config.Database.Citus) { break; }
			await this.EnsureUserOnAsync(this.WorkerSuperConnectionString(worker), $"worker {worker.Host}:{worker.Port}");
		}
	}

	/// <summary>
	/// Creates the pgfs user (Connection.Username) on the given super connection. Idempotent.
	/// </summary>
	private async Task EnsureUserOnAsync(string superConn, string nodeLabel) {
		var userName = this.config.Database.Connection.Username;
		if (string.IsNullOrEmpty(userName)) {
			userName = "pgfs";
		}
		var password = this.config.Database.Connection.Password;
		if (string.IsNullOrEmpty(password)) {
			password = userName;
		}

		Logger.Information($"[{nodeLabel}] checking user '{userName}'");

		var exists = (await Pg.QueryAsync<long>(
			superConn,
			"SELECT usesysid FROM pg_user WHERE usename = @name",
			new { name = userName }
		)).Any();

		if (exists) {
			Logger.Information($"  [{nodeLabel}] user '{userName}' already exists");
			return;
		}

		Logger.Information($"  [{nodeLabel}] creating user '{userName}'");
		await Pg.ExecuteAsync(
			superConn,
			$"CREATE USER {Pg.QuoteIdentifier(userName)} WITH ENCRYPTED PASSWORD {Pg.QuoteLiteral(password)}"
		);
	}

	// ----------------------------------------------------------------------
	// 2. Tablespace
	// ----------------------------------------------------------------------

	private async Task EnsureTablespaceAsync() {
		var tablespaceName = this.config.Database.TablespaceName;
		if (string.IsNullOrEmpty(tablespaceName)) {
			tablespaceName = "pg_default";
		}
		Logger.Information($"checking tablespace '{tablespaceName}'");

		// pg_default always exists.
		if (tablespaceName == "pg_default") {
			Logger.Information("  using pg_default (the default)");
			return;
		}
		// The tablespace is used only by CREATE DATABASE. For an existing DB it is not created (EnsureDatabaseAsync warns about the ineffective option).
		if (!this.creatingDatabase) {
			Logger.Information("  the database already exists, so the tablespace is not ensured");
			return;
		}

		// A tablespace is node-local (Citus does not propagate CREATE TABLESPACE). Create it on the coordinator + every worker.
		// CREATE DATABASE WITH TABLESPACE requires this name, so it must precede the DB creation (EnsureDatabaseAsync)
		// (InitializeAsync's call order guarantees that). Tables carry no per-table TABLESPACE clause and inherit the DB default
		// (shards inherit the worker DB default too). Design: docs/settings-and-plperlu.md.
		var tablespacePath = this.config.Database.TablespacePath;
		await this.EnsureTablespaceOnNodeAsync("coordinator", this.superConnectionString, tablespaceName, tablespacePath);
		if (this.config.Database.Citus) {
			foreach (var worker in this.config.Database.Workers) {
				var label = $"worker {worker.Host}:{worker.Port}";
				await this.EnsureTablespaceOnNodeAsync(label, this.WorkerSuperConnectionString(worker), tablespaceName, tablespacePath);
			}
		}
	}

	/// <summary>
	/// Ensures the tablespace on the given node. Skips if it already exists. When the LOCATION dir is missing and
	/// <see cref="Schema.App.Plperlu"/> is allowed, the dir is created with plperlu (running as the postgres OS user)
	/// via <c>File::Path::make_path</c> + <c>chmod 0700</c> before <c>CREATE TABLESPACE</c> would fail (the created dir
	/// is postgres-owned 0700 = matches CREATE TABLESPACE's requirement). When denied, the dir is not created and, if
	/// absent, CREATE TABLESPACE rejects it.
	/// </summary>
	private async Task EnsureTablespaceOnNodeAsync(string label, string superConn, string tablespaceName, string tablespacePath) {
		var exists = (await Pg.QueryAsync<uint>(
			superConn,
			"SELECT oid FROM pg_tablespace WHERE spcname = @name",
			new { name = tablespaceName }
		)).Any();
		if (exists) {
			Logger.Information($"  [{label}] tablespace '{tablespaceName}' already exists");
			return;
		}
		if (string.IsNullOrWhiteSpace(tablespacePath)) {
			throw new InvalidOperationException(
				$"[{label}] tablespace '{tablespaceName}' does not exist and --tablespace-path was not given " +
				"(with --allow-plperlu, mkfs can create the dir automatically)."
			);
		}

		// Create the LOCATION dir with plperlu (only when allowed, best-effort). On failure, defer to CREATE TABLESPACE's error.
		if (this.config.App.Plperlu) {
			await TryMakeTablespaceDirAsync(label, superConn, tablespacePath);
		}

		var userName = this.config.Database.Connection.Username ?? "pgfs";
		Logger.Information($"  [{label}] creating tablespace '{tablespaceName}' at {tablespacePath}");
		await Pg.ExecuteAsync(
			superConn,
			$"CREATE TABLESPACE {Pg.QuoteIdentifier(tablespaceName)} " +
			$"OWNER {Pg.QuoteIdentifier(userName)} " +
			$"LOCATION {Pg.QuoteLiteral(tablespacePath)}"
		);
	}

	/// <summary>
	/// Creates the tablespace LOCATION dir with plperlu's <c>File::Path::make_path</c> (recursive) + <c>chmod 0700</c>.
	/// It runs with the postgres OS user's privileges, so the created dir ends up postgres-owned 0700. A failure is left
	/// as a warning, deferring to the explicit error of the subsequent CREATE TABLESPACE.
	/// </summary>
	private static async Task TryMakeTablespaceDirAsync(string label, string superConn, string tablespacePath) {
		// Escape \ and ' for a Perl single-quoted literal.
		var perlPath = tablespacePath.Replace("\\", "\\\\").Replace("'", "\\'");
		try {
			await Pg.ExecuteAsync(superConn, "CREATE EXTENSION IF NOT EXISTS plperlu");
			var doSql =
				"DO LANGUAGE plperlu $PL$ " +
				"use File::Path qw(make_path); " +
				$"my $d = '{perlPath}'; make_path($d); chmod 0700, $d; " +
				"$PL$";
			await Pg.ExecuteAsync(superConn, doSql);
			Logger.Information($"  [{label}] created the LOCATION dir with plperlu (postgres-owned 0700): {tablespacePath}");
		} catch (System.Exception ex) {
			Logger.Warning($"  [{label}] failed to create the dir via plperlu (deferring to CREATE TABLESPACE's decision): " + ex.Message);
		}
	}

	// ----------------------------------------------------------------------
	// 3. Database
	// ----------------------------------------------------------------------

	/// <summary>
	/// Under <c>--clean</c> / <c>--purge</c>, DROPs the target database. In addition to the coordinator itself,
	/// under Citus the same-named DB on every worker in <c>pg_dist_node</c> is dropped too. A DB that is absent on a node is skipped (idempotent).
	/// The tablespace and roles are left untouched (a requirement). Before deleting, it counts what is connected
	/// (<see cref="WaitUntilNoConnectionsAsync"/>) and asks for confirmation (<see cref="ConfirmTeardownAsync"/>).
	/// </summary>
	private async Task TeardownAsync() {
		if (!this.config.Clean && !this.config.Purge) {
			return;
		}
		// **Which nodes to delete is decided by what the DB actually is.** Previously only the `--citus` / `--worker` options were
		// iterated, so a `--clean` that forgot `--worker` **left the worker-side DBs behind** (the distributed toml does not carry
		// workers, so forgetting it is ordinary). `--citus` / `--worker` are used only as instructions for the rebuild (EnsureDatabaseAsync).
		var workers = await this.WorkersToDropAsync();
		// **Check that every worker is reachable before deleting.** Node names in pg_dist_node are the names as seen from the
		// coordinator, so the host running mkfs may not resolve them. Stopping halfway would delete only the coordinator and leave
		// garbage on the workers, so if even one is unreachable, nothing is deleted.
		var unreachable = new List<string>();
		foreach (var worker in workers) {
			if (await this.CanConnectAsync(this.WorkerSuperConnectionString(worker))) { continue; }
			unreachable.Add($"{worker.Host}:{worker.Port}");
		}
		if (unreachable.Count > 0) {
			throw new InvalidOperationException(
				$"{this.TeardownFlag}: some workers are unreachable, so nothing is deleted: " + string.Join(", ", unreachable) +
				" (use names reachable from this host, or, if a worker is no longer used, run citus_remove_node on the coordinator and try again)"
			);
		}
		var coordExists = await this.CoordinatorDatabaseExistsAsync();
		if (!coordExists && workers.Count == 0) {
			Logger.Information($"{this.TeardownFlag}: there is no database to delete");
			return;
		}
		await this.WaitUntilNoConnectionsAsync(workers, coordExists);
		await this.ConfirmTeardownAsync(workers, coordExists);
		// Drop the workers first (dropping the coordinator also drops pg_dist_node, so the workers must be
		// reached over their own super connection, independently of the coordinator).
		foreach (var worker in workers) {
			await this.DropDatabaseOnAsync(this.WorkerSuperConnectionString(worker), $"worker {worker.Host}:{worker.Port}");
		}
		await this.DropDatabaseOnAsync(this.superConnectionString, "coordinator");
		// **Empty the connection pools.** The connections opened to the target DB to look up the topology were cut by the DROP;
		// leaving them would make the first operation on the rebuilt DB pick up a dead pooled connection and fail with 57P01
		// (terminating connection due to administrator command) (observed).
		Pg.ClearPools();
	}

	/// <summary>
	/// The workers to delete. If the target DB exists on the coordinator, **that DB's <c>pg_dist_node</c>** (every node except the
	/// coordinator = groupid 0); otherwise the <c>--worker</c> options (there is no way to look up the topology, so they are all we have). Empty if neither.
	/// </summary>
	private async Task<List<(string Host, int Port)>> WorkersToDropAsync() {
		if (!await this.CoordinatorDatabaseExistsAsync()) {
			if (this.config.Database.Workers.Count == 0) {
				Logger.Warning($"{this.TeardownFlag}: the database is absent on the coordinator, so DBs left on workers cannot be checked (pass --worker to delete those too)");
			}
			return this.config.Database.Workers;
		}
		if (!await this.HasCitusExtensionAsync()) {
			return new();
		}
		var rows = await Pg.QueryAsync<(string NodeName, int NodePort)>(
			this.CoordinatorSuperPgfsConnectionString(),
			"SELECT nodename, nodeport FROM pg_dist_node WHERE groupid <> 0 ORDER BY nodeid"
		);
		var workers = rows.Select(r => (r.NodeName, r.NodePort)).ToList();
		Logger.Information($"[coordinator] {this.TeardownFlag}: also deleting the {workers.Count} worker(s) in pg_dist_node" +
			string.Concat(workers.Select(w => $" {w.NodeName}:{w.NodePort}")));
		return workers;
	}

	/// <summary>Tries connecting once over the super connection (a reachability check).</summary>
	private async Task<bool> CanConnectAsync(string superConn) {
		try {
			await Pg.QueryAsync<int>(superConn, "SELECT 1");
			return true;
		} catch (Exception ex) {
			Logger.Warning("  cannot connect: ", ex.Message);
			return false;
		}
	}

	/// <summary>DROP DATABASE on the target DB over the given super connection.</summary>
	private async Task DropDatabaseOnAsync(string superConn, string nodeLabel) {
		var databaseName = this.config.Database.Connection.Database;
		if (string.IsNullOrEmpty(databaseName)) {
			databaseName = "pgfs";
		}
		Logger.Information($"[{nodeLabel}] {this.TeardownFlag}: dropping database '{databaseName}'");

		var exists = (await Pg.QueryAsync<uint>(
			superConn,
			"SELECT oid FROM pg_database WHERE datname = @name",
			new { name = databaseName }
		)).Any();

		if (!exists) {
			Logger.Information($"  [{nodeLabel}] database '{databaseName}' does not exist (skipped)");
			return;
		}

		// Disconnect other clients (DROP DATABASE fails while other connections exist).
		await Pg.ExecuteAsync(
			superConn,
			@"SELECT pg_terminate_backend(pid)
			  FROM pg_stat_activity
			  WHERE datname = @name AND pid <> pg_backend_pid()",
			new { name = databaseName }
		);

		// DROP DATABASE cannot run inside a transaction.
		await Pg.ExecuteAsync(
			superConn,
			$"DROP DATABASE IF EXISTS {Pg.QuoteIdentifier(databaseName)}"
		);
		Logger.Information($"  [{nodeLabel}] dropped database '{databaseName}'");
	}

	/// <summary>
	/// Ensures the pgfs DB exists and, under Citus, sets up the cluster topology as well.
	///
	/// <para>
	/// **Key design branch**: the coordinator pgfs DB's **existence is checked first**. If it already
	/// exists (run without --clean, or --clean failed to drop it, etc.), all Citus mutating operations
	/// (worker bootstrap / citus_add_node / shouldhaveshards / create_distributed_table) are **skipped
	/// entirely** and the method returns immediately after logging the current state. This guarantees:
	///   - mkfs never accidentally breaks an already-running Citus cluster on a re-run.
	///   - Recovering from "ran mkfs --citus --worker w1 against the wrong empty DB" requires --clean.
	///   - "running mkfs idempotently any number of times leaves the cluster state unchanged" holds.
	/// </para>
	///
	/// <para>
	/// When the DB does not exist, do a fresh setup:
	/// (1) Step 1: pgfs DB + CREATE EXTENSION citus on each worker.
	/// (2) Step 2: create the pgfs DB on the coordinator.
	/// (3) Step 3: on the coordinator, CREATE EXTENSION citus -> set_coordinator_host ->
	///             add_node x N -> (when there are 0 workers) shouldhaveshards=true.
	/// CREATE SCHEMA is left to DDL propagation via EnsureSchemaAsync.
	/// citus_set_coordinator_host is called only on the coordinator (citus_add_node auto-syncs it to the
	/// workers, confirmed by a probe — <see href="../../../../tests/citus/multinode_probe.sh"/>).
	/// </para>
	/// </summary>
	private async Task EnsureDatabaseAsync() {
		// === Existence check (precondition for mutating operations) ===
		var coordExists = await this.CoordinatorDatabaseExistsAsync();
		if (coordExists) {
			Logger.Information("[coordinator] database already exists — leaving Citus untouched, only logging the current state");
			// Instructions that apply only when creating have no effect on an existing DB (the tablespace is used only by CREATE DATABASE).
			foreach (var f in new Field[] { Schema.Database.TablespaceName, Schema.Database.TablespacePath }) {
				if (this.wasProvided(f)) {
					Logger.Warning($"--{f.Key.Replace('_', '-')} has no effect: the database already exists (it only applies when creating)");
				}
			}
			// Whether it is Citus is decided by what the DB actually is, not by the setting (**in both directions**). If it is
			// already Citus, tables added later are distributed too. Adding --citus to a non-Citus DB does not make it Citus -
			// previously it stayed true and went on, then failed trying to create_distributed_table the new schema's tables
			// (the extension is absent).
			var hasCitus = await this.HasCitusExtensionAsync();
			if (this.config.Database.Citus && !hasCitus) {
				Logger.Warning("--citus has no effect: the existing database is not Citus (rebuild with --clean to make it Citus)");
			}
			if (this.config.Database.Workers.Count > 0) {
				Logger.Warning("--worker has no effect: the database already exists (to add a worker, run citus_add_node on the coordinator)");
			}
			if (hasCitus && !this.config.Database.Citus) {
				Logger.Information("[coordinator] the citus extension is present, so this is treated as a Citus FS");
			}
			this.config.Database.Citus = hasCitus;
			if (this.config.Database.Citus) {
				await this.LogExistingCitusStateAsync();
			}
			return;
		}

		// === Step 1: ensure pgfs DB + the Citus extension on each worker ===
		// No-op with 0 workers or non-Citus. CREATE SCHEMA is left to DDL propagation in EnsureSchemaAsync.
		// citus_set_coordinator_host is not called on the workers either (citus_add_node in Step 3 auto-syncs it).
		foreach (var worker in this.config.Database.Workers) {
			if (!this.config.Database.Citus) { break; }
			var label = $"worker {worker.Host}:{worker.Port}";
			var workerSuper = this.WorkerSuperConnectionString(worker);
			await this.EnsureDatabaseOnAsync(workerSuper, label);
			var workerSuperPgfs = this.WorkerSuperPgfsConnectionString(worker);
			Logger.Information($"  [{label}] CREATE EXTENSION IF NOT EXISTS citus");
			await Pg.ExecuteAsync(workerSuperPgfs, "CREATE EXTENSION IF NOT EXISTS citus");
		}

		// === Step 2: create the coordinator pgfs DB ===
		await this.EnsureDatabaseOnAsync(this.superConnectionString, "coordinator");

		// Single-PG mode ends here.
		if (!this.config.Database.Citus) {
			return;
		}

		// === Step 3: Citus cluster topology (coordinator only; auto-synced to workers) ===
		var coordSuperPgfs = this.CoordinatorSuperPgfsConnectionString();
		Logger.Information("[coordinator] CREATE EXTENSION IF NOT EXISTS citus");
		await Pg.ExecuteAsync(coordSuperPgfs, "CREATE EXTENSION IF NOT EXISTS citus");

		// Register the coordinator (groupid=0) if it is not already in pg_dist_node.
		var coordRegistered = (await Pg.QueryAsync<long>(
			coordSuperPgfs,
			"SELECT count(*) FROM pg_dist_node WHERE groupid = 0"
		)).First() > 0;
		if (!coordRegistered) {
			var coordHost = this.CoordinatorHost();
			var coordPort = this.CoordinatorPort();
			Logger.Information($"[coordinator] citus_set_coordinator_host('{coordHost}', {coordPort})");
			await Pg.ExecuteAsync(
				coordSuperPgfs,
				"SELECT citus_set_coordinator_host(@host, @port)",
				new { host = coordHost, port = coordPort }
			);
		}

		// Register each worker (citus_add_node is idempotent and auto-syncs).
		foreach (var worker in this.config.Database.Workers) {
			Logger.Information($"[coordinator] citus_add_node('{worker.Host}', {worker.Port})");
			await Pg.ExecuteAsync(
				coordSuperPgfs,
				"SELECT citus_add_node(@host, @port)",
				new { host = worker.Host, port = worker.Port }
			);
		}

		// With 0 workers, make the coordinator itself a shard host (single-node setup).
		if (this.config.Database.Workers.Count == 0) {
			var coordHost = this.CoordinatorHost();
			var coordPort = this.CoordinatorPort();
			Logger.Information($"[coordinator] 0 workers, so shouldhaveshards = true (single-node setup)");
			await Pg.ExecuteAsync(
				coordSuperPgfs,
				"SELECT citus_set_node_property(@host, @port, 'shouldhaveshards', true)",
				new { host = coordHost, port = coordPort }
			);
		}
	}

	/// <summary>Whether the target DB has the citus extension (Citus is decided by what the DB actually is, not by the setting).</summary>
	private async Task<bool> HasCitusExtensionAsync() {
		var n = (await Pg.QueryAsync<long>(
			this.CoordinatorSuperPgfsConnectionString(),
			"SELECT count(*) FROM pg_extension WHERE extname = 'citus'"
		)).FirstOrDefault();
		return n > 0;
	}

	/// <summary>Returns whether the target pgfs DB already exists on the coordinator.</summary>
	private async Task<bool> CoordinatorDatabaseExistsAsync() {
		var databaseName = this.config.Database.Connection.Database;
		if (string.IsNullOrEmpty(databaseName)) {
			databaseName = "pgfs";
		}
		return (await Pg.QueryAsync<uint>(
			this.superConnectionString,
			"SELECT oid FROM pg_database WHERE datname = @name",
			new { name = databaseName }
		)).Any();
	}

	/// <summary>
	/// CREATEs the pgfs DB over the given super connection. Does nothing if it already exists.
	/// Returns true when CREATE DATABASE actually ran, false when an existing DB was skipped.
	/// </summary>
	private async Task<bool> EnsureDatabaseOnAsync(string superConn, string nodeLabel) {
		var databaseName = this.config.Database.Connection.Database;
		if (string.IsNullOrEmpty(databaseName)) {
			databaseName = "pgfs";
		}
		Logger.Information($"[{nodeLabel}] checking database '{databaseName}'");

		var exists = (await Pg.QueryAsync<uint>(
			superConn,
			"SELECT oid FROM pg_database WHERE datname = @name",
			new { name = databaseName }
		)).Any();

		if (exists) {
			Logger.Information($"  [{nodeLabel}] database '{databaseName}' already exists");
			return false;
		}

		var userName = this.config.Database.Connection.Username ?? "pgfs";
		var tablespaceName = this.config.Database.TablespaceName;
		if (string.IsNullOrEmpty(tablespaceName)) {
			tablespaceName = "pg_default";
		}

		Logger.Information($"  [{nodeLabel}] creating database '{databaseName}'");
		await Pg.ExecuteAsync(
			superConn,
			$"CREATE DATABASE {Pg.QuoteIdentifier(databaseName)} " +
			$"WITH OWNER = {Pg.QuoteIdentifier(userName)} " +
			$"ENCODING = 'UTF8' " +
			$"TABLESPACE = {Pg.QuoteIdentifier(tablespaceName)} " +
			$"TEMPLATE = template0"
		);
		await Pg.ExecuteAsync(
			superConn,
			$"GRANT ALL PRIVILEGES ON DATABASE {Pg.QuoteIdentifier(databaseName)} TO {Pg.QuoteIdentifier(userName)}"
		);
		return true;
	}

	/// <summary>
	/// Reports the current state of an existing DB (called only on the existing-DB path + under Citus).
	/// Only logs whether the Citus extension is installed / pg_dist_node registrations / shard placement;
	/// it does not change any state.
	/// </summary>
	private async Task LogExistingCitusStateAsync() {
		var coordSuperPgfs = this.CoordinatorSuperPgfsConnectionString();
		try {
			var citusEnabled = (await Pg.QueryAsync<bool>(
				coordSuperPgfs,
				"SELECT EXISTS(SELECT 1 FROM pg_extension WHERE extname='citus')"
			)).First();
			Logger.Information($"[coordinator] state: citus extension = {citusEnabled}");
			if (!citusEnabled) {
				return;
			}
			var nodes = (await Pg.QueryAsync<dynamic>(
				coordSuperPgfs,
				"SELECT nodename, nodeport, groupid, noderole::text AS noderole, shouldhaveshards FROM pg_dist_node ORDER BY nodeid"
			)).ToList();
			Logger.Information($"[coordinator] state: {nodes.Count} pg_dist_node registration(s)");
			foreach (var n in nodes) {
				Logger.Information($"  - {n.nodename}:{n.nodeport} groupid={n.groupid} role={n.noderole} shouldhaveshards={n.shouldhaveshards}");
			}
		} catch (System.Exception ex) {
			Logger.Warning($"[coordinator] exception while reading state (continuing): {ex.Message}");
		}
	}

	// ----------------------------------------------------------------------
	// 4. Schema
	// ----------------------------------------------------------------------

	private async Task EnsureSchemaAsync() {
		var schemaName = this.config.Database.SchemaName;
		if (string.IsNullOrEmpty(schemaName)) {
			schemaName = "public";
		}

		Logger.Information($"checking schema '{schemaName}'");

		// The public schema exists by default in PostgreSQL.
		if (schemaName == "public") {
			Logger.Information("  using the public schema (the default)");
			return;
		}

		var exists = (await Pg.QueryAsync<uint>(
			this.connectionString,
			"SELECT oid FROM pg_namespace WHERE nspname = @name",
			new { name = schemaName }
		)).Any();

		if (exists) {
			Logger.Information($"  schema '{schemaName}' already exists");
			return;
		}

		var userName = this.config.Database.Connection.Username ?? "pgfs";
		Logger.Information($"  creating schema '{schemaName}'");
		await Pg.ExecuteAsync(
			this.connectionString,
			$"CREATE SCHEMA {Pg.QuoteIdentifier(schemaName)} AUTHORIZATION {Pg.QuoteIdentifier(userName)}"
		);
	}

	// ----------------------------------------------------------------------
	// 5. Tables
	// ----------------------------------------------------------------------

	private async Task CreateInodeTableAsync(string prefix) {
		var schemaName = this.SchemaNameOrDefault();
		var tableName = prefix + "inode";

		var created = await this.CreateTableAsync(
			schemaName,
			tableName,
			new[] {
				new ColumnInfo("id", "BIGSERIAL", "NOT NULL"),
				new ColumnInfo("parent_id", "BIGINT", "NOT NULL"),
				new ColumnInfo("name", "TEXT", "NOT NULL"),
				new ColumnInfo("uname", "TEXT", "NOT NULL"),
				new ColumnInfo("gname", "TEXT", "NOT NULL"),
				new ColumnInfo("st_mode", "INTEGER", "NOT NULL"),
				new ColumnInfo("st_nlink", "INTEGER", "NOT NULL DEFAULT 1"),
				new ColumnInfo("st_size", "BIGINT", "NOT NULL DEFAULT 0"),
				new ColumnInfo("st_mtime", "TIMESTAMP", "NOT NULL DEFAULT (current_timestamp AT TIME ZONE 'UTC')"),
				new ColumnInfo("st_ctime", "TIMESTAMP", "NOT NULL DEFAULT (current_timestamp AT TIME ZONE 'UTC')"),
				new ColumnInfo("link_target", "TEXT", "NULL"),
				new ColumnInfo("is_junction", "BOOLEAN", "NOT NULL DEFAULT FALSE"),
				new ColumnInfo("data_id", "BIGINT", "NULL"),
				// Extended attributes (xattr): parallel arrays pairing names TEXT[] and values BYTEA[] at the same index
				// (migrated from the old JSONB + Base64; values kept faithfully as bytea. Design: docs/xattr-bytea.md).
				new ColumnInfo("xattr_names", "TEXT[]", "NOT NULL DEFAULT '{}'::TEXT[]"),
				new ColumnInfo("xattr_values", "BYTEA[]", "NOT NULL DEFAULT '{}'::BYTEA[]"),
				new ColumnInfo("created_at", "TIMESTAMP", "NOT NULL DEFAULT (current_timestamp AT TIME ZONE 'UTC')"),
				new ColumnInfo("created_by", "TEXT", "NOT NULL"),
				new ColumnInfo("updated_at", "TIMESTAMP", "NOT NULL DEFAULT (current_timestamp AT TIME ZONE 'UTC')"),
				new ColumnInfo("updated_by", "TEXT", "NOT NULL"),
			},
			// The PK is the composite (parent_id, id), to satisfy Citus's constraint that the PK must
			// include the distribution column (parent_id). id is BIGSERIAL and globally unique (the
			// sequence lives on the coordinator), so (parent_id, id) is effectively unique on id alone.
			// A standalone id INDEX is kept for WHERE id = @id lookups. A standalone parent_id INDEX is
			// unnecessary because it is covered by the PK's leftmost prefix.
			primary: new[] { "parent_id", "id" },
			unique: new[] { new[] { "parent_id", "name" } },
			indexes: new[] { new[] { "id" }, new[] { "uname" }, new[] { "gname" } }
		);
		// Citus-ify only what was newly created. To Citus-ify existing tables too, use
		// `--distribute-existing` (the Distribute/Register below are idempotent, so re-running is safe).
		if (!created && !this.config.Database.DistributeExisting) { return; }
		await this.DistributeTableAsync(schemaName, tableName, "parent_id");
	}

	private async Task CreateDataTableAsync(string prefix) {
		var schemaName = this.SchemaNameOrDefault();
		var tableName = prefix + "data";

		var created = await this.CreateTableAsync(
			schemaName,
			tableName,
			new[] {
				new ColumnInfo("id", "BIGSERIAL", "NOT NULL"),
				new ColumnInfo("chunk_size", "INTEGER", "NOT NULL"),
				new ColumnInfo("total_size", "BIGINT", "NOT NULL"),
				new ColumnInfo("created_at", "TIMESTAMP", "NOT NULL DEFAULT (current_timestamp AT TIME ZONE 'UTC')"),
				new ColumnInfo("created_by", "TEXT", "NOT NULL"),
				new ColumnInfo("updated_at", "TIMESTAMP", "NOT NULL DEFAULT (current_timestamp AT TIME ZONE 'UTC')"),
				new ColumnInfo("updated_by", "TEXT", "NOT NULL"),
			},
			primary: new[] { "id" }
		);
		// Citus-ify only what was newly created. To Citus-ify existing tables too, use
		// `--distribute-existing` (the Distribute/Register below are idempotent, so re-running is safe).
		if (!created && !this.config.Database.DistributeExisting) { return; }
		await this.DistributeTableAsync(schemaName, tableName, "id");
	}

	private async Task CreateDataChunkTableAsync(string prefix) {
		var schemaName = this.SchemaNameOrDefault();
		var tableName = prefix + "data_chunk";

		var created = await this.CreateTableAsync(
			schemaName,
			tableName,
			new[] {
				new ColumnInfo("data_id", "BIGINT", "NOT NULL"),
				new ColumnInfo("chunk_index", "INTEGER", "NOT NULL"),
				new ColumnInfo("payload", "BYTEA", "NOT NULL"),
				new ColumnInfo("created_at", "TIMESTAMP", "NOT NULL DEFAULT (current_timestamp AT TIME ZONE 'UTC')"),
				new ColumnInfo("created_by", "TEXT", "NOT NULL"),
				new ColumnInfo("updated_at", "TIMESTAMP", "NOT NULL DEFAULT (current_timestamp AT TIME ZONE 'UTC')"),
				new ColumnInfo("updated_by", "TEXT", "NOT NULL"),
			},
			primary: new[] { "data_id", "chunk_index" }
		);
		// Citus-ify only what was newly created. To Citus-ify existing tables too, use
		// `--distribute-existing` (the Distribute/Register below are idempotent, so re-running is safe).
		if (!created && !this.config.Database.DistributeExisting) { return; }
		// Co-located with pgfs_data, so every chunk of one file lands on the same shard
		await this.DistributeTableAsync(schemaName, tableName, "data_id", coLocateWith: prefix + "data");
	}

	/// <summary>
	/// Creates the cross-client mutual-exclusion lock token table <c>{prefix}lock</c>.
	/// It carries no audit columns because it holds lock tokens, not data (see
	/// <see href="../../../../docs/ddl/pgfs_lock.sql"/> / <see href="../../../../docs/support_for_citus.md"/>).
	/// </summary>
	private async Task CreateLockTableAsync(string prefix) {
		var schemaName = this.SchemaNameOrDefault();
		var tableName = prefix + "lock";

		var created = await this.CreateTableAsync(
			schemaName,
			tableName,
			new[] {
				new ColumnInfo("target_id", "BIGINT", "NOT NULL"),
			},
			primary: new[] { "target_id" }
		);
		// Citus-ify only what was newly created. To Citus-ify existing tables too, use
		// `--distribute-existing` (the Distribute/Register below are idempotent, so re-running is safe).
		if (!created && !this.config.Database.DistributeExisting) { return; }
		// {prefix}lock is **not distributed**: a row lock (SELECT ... FOR UPDATE) is refused on a distributed
		// table with shard_replication_factor > 1, so it is made a Citus local table holding a single copy on the
		// coordinator, which guarantees that "the locking mechanism does not depend on rf". Registering it in the
		// metadata routes queries that enter through a worker to that same single row as well, so the exclusion
		// holds whichever node is the entrance.
		// The design of record is docs/design/support_for_citus.md, the exclusion control section.
		await this.RegisterLocalTableAsync(schemaName, tableName);
	}

	/// <summary>
	/// Creates the settings persistence table <c>{prefix}settings</c>.
	/// Each row holds the value of one <see cref="Field"/>, keyed by a flat <c>(scope, key)</c> PK.
	/// The layout matches <see cref="ConfigStore.LoadAll"/> / <see cref="ConfigStore.Save{T}"/>.
	/// </summary>
	private async Task CreateSettingsTableAsync(string prefix) {
		var schemaName = this.SchemaNameOrDefault();
		var tableName = prefix + "settings";

		var created = await this.CreateTableAsync(
			schemaName,
			tableName,
			new[] {
				new ColumnInfo("scope", "TEXT", "NOT NULL"),
				new ColumnInfo("key", "TEXT", "NOT NULL"),
				new ColumnInfo("value", "JSONB", "NOT NULL DEFAULT 'null'::JSONB"),
				new ColumnInfo("created_at", "TIMESTAMP", "NOT NULL DEFAULT (current_timestamp AT TIME ZONE 'UTC')"),
				new ColumnInfo("created_by", "TEXT", "NOT NULL"),
				new ColumnInfo("updated_at", "TIMESTAMP", "NOT NULL DEFAULT (current_timestamp AT TIME ZONE 'UTC')"),
				new ColumnInfo("updated_by", "TEXT", "NOT NULL"),
			},
			primary: new[] { "scope", "key" }
		);
		// Citus-ify only what was newly created. To Citus-ify existing tables too, use
		// `--distribute-existing` (the Distribute/Register below are idempotent, so re-running is safe).
		if (!created && !this.config.Database.DistributeExisting) { return; }
		// pgfs_settings is coordinator-only local + metadata registration (a worker sees it through the metadata, for JOINs)
		await this.RegisterLocalTableAsync(schemaName, tableName);
	}

	/// <summary>
	/// Creates <c>{prefix}mounts</c>, the registry of running mounts. Each mount/assign process INSERTs one row
	/// at start-up, updates <c>heartbeat_at</c> on a periodic heartbeat, and DELETEs it on a clean exit (a
	/// volatile registry).
	/// Registration and heartbeat are low-frequency, so like <c>pgfs_settings</c> it is coordinator local +
	/// metadata registration (not distributed).
	/// The design of record is docs/design/runtime-control-plane.md, the mount registry section.
	/// </summary>
	private async Task CreateMountsTableAsync(string prefix) {
		var schemaName = this.SchemaNameOrDefault();
		var tableName = prefix + "mounts";

		var created = await this.CreateTableAsync(
			schemaName,
			tableName,
			new[] {
				new ColumnInfo("mount_id", "TEXT", "NOT NULL"),
				new ColumnInfo("host", "TEXT", "NOT NULL"),
				new ColumnInfo("pid", "BIGINT", "NOT NULL"),
				new ColumnInfo("mountpoint", "TEXT", "NOT NULL"),
				new ColumnInfo("mode", "TEXT", "NOT NULL"),
				new ColumnInfo("started_at", "TIMESTAMP", "NOT NULL DEFAULT (current_timestamp AT TIME ZONE 'UTC')"),
				new ColumnInfo("heartbeat_at", "TIMESTAMP", "NOT NULL DEFAULT (current_timestamp AT TIME ZONE 'UTC')"),
				new ColumnInfo("config", "JSONB", "NOT NULL DEFAULT '{}'::JSONB"),
				new ColumnInfo("stats", "JSONB", "NOT NULL DEFAULT '{}'::JSONB"),
			},
			primary: new[] { "mount_id" }
		);
		// Citus-ify only what was newly created. To Citus-ify existing tables too, use
		// `--distribute-existing` (the Distribute/Register below are idempotent, so re-running is safe).
		if (!created && !this.config.Database.DistributeExisting) { return; }
		await this.RegisterLocalTableAsync(schemaName, tableName);
	}

	/// <summary>
	/// Creates <c>{prefix}audit</c>, the audit log table. A monthly RANGE partitioned table keyed on
	/// occurred_at. A DEFAULT partition is not created, in order to avoid PG's restriction that "once rows have
	/// accumulated in it, the month partition for that range can no longer be CREATEd afterwards" (the
	/// application ensures the month partition before it INSERTs). The design of record is
	/// <see href="../../../../docs/design/audit-log.md"/>.
	/// </summary>
	private async Task CreateAuditTableAsync(string prefix) {
		var schemaName = this.SchemaNameOrDefault();
		var tableName = prefix + "audit";

		var created = await this.CreateTableAsync(
			schemaName,
			tableName,
			new[] {
				new ColumnInfo("id", "BIGSERIAL", "NOT NULL"),
				new ColumnInfo("occurred_at", "TIMESTAMP", "NOT NULL DEFAULT (current_timestamp AT TIME ZONE 'UTC')"),
				new ColumnInfo("op", "TEXT", "NOT NULL"),
				new ColumnInfo("target_id", "BIGINT", "NULL"),
				new ColumnInfo("parent_id", "BIGINT", "NULL"),
				new ColumnInfo("name", "TEXT", "NULL"),
				new ColumnInfo("detail", "JSONB", "NOT NULL DEFAULT '{}'::JSONB"),
				new ColumnInfo("caller_ip", "INET", "NULL"),
				new ColumnInfo("caller_host", "TEXT", "NULL"),
				new ColumnInfo("caller_uid", "BIGINT", "NULL"),
				new ColumnInfo("caller_uname", "TEXT", "NULL"),
				new ColumnInfo("caller_domain", "TEXT", "NULL"),
			},
			// The PK is a composite including the partition key occurred_at (partitioned-table constraint +
			// Citus distribution-column requirement). A standalone id INDEX is kept for id-only lookups.
			primary: new[] { "occurred_at", "id" },
			indexes: new[] { new[] { "id" }, new[] { "op" }, new[] { "target_id" } },
			partitionBy: "occurred_at"
		);
		// Citus-ify only what was newly created. To Citus-ify existing tables too, use
		// `--distribute-existing` (the Distribute/Register below are idempotent, so re-running is safe).
		if (!created && !this.config.Database.DistributeExisting) { return; }
		// On Citus it is distributed on occurred_at (the classic time-series pattern: hash distribution ×
		// monthly RANGE partitioning).
		// When the parent is already distributed, Citus distributes a month partition automatically as the
		// application creates it.
		await this.DistributeTableAsync(schemaName, tableName, "occurred_at");
	}

	/// <summary>
	/// Registers the given schema.table as a Citus distributed table.
	/// Called from each CreateXxxTableAsync only when the table was freshly created.
	/// No-op without <c>--citus</c> (inner guard).
	/// </summary>
	private async Task DistributeTableAsync(string schemaName, string tableName, string distributionColumn, string? coLocateWith = null) {
		if (!this.config.Database.Citus) {
			return;
		}
		var qualified = Pg.QuoteIdentifier(schemaName) + "." + Pg.QuoteIdentifier(tableName);
		if (await this.IsCitusManagedAsync(qualified)) {
			Logger.Information($"  {qualified} is already under Citus management - skipping");
			return;
		}
		string sql;
		if (coLocateWith != null) {
			var qCo = Pg.QuoteIdentifier(schemaName) + "." + Pg.QuoteIdentifier(coLocateWith);
			sql = $"SELECT create_distributed_table('{qualified}', '{distributionColumn}', colocate_with => '{qCo}')";
			Logger.Information($"  {qualified} → distributed by {distributionColumn} (co-located with {qCo})");
		} else {
			sql = $"SELECT create_distributed_table('{qualified}', '{distributionColumn}')";
			Logger.Information($"  {qualified} → distributed by {distributionColumn}");
		}
		// The shard count / replication factor are decided by session GUCs (the values in effect when
		// create_distributed_table runs are the ones that stick).
		// **Sending them in the same command is the point**: Pg.ExecuteAsync takes a connection from the pool
		// afresh on every call, so putting the SET in a separate call gives no guarantee that it rides the same
		// physical connection.
		// When it is 0 nothing is set = the cluster default (postgresql.conf and so on) is respected.
		await Pg.ExecuteAsync(this.connectionString, this.CitusShardGucPrefix() + sql);
	}

	/// <summary>
	/// Assembles the <c>SET</c> statements for <c>citus.shard_count</c> / <c>citus.shard_replication_factor</c>
	/// (an empty string when they are not specified). <see cref="DistributeTableAsync"/> concatenates it to the
	/// front of the SQL it issues.
	/// </summary>
	private string CitusShardGucPrefix() {
		var sb = new System.Text.StringBuilder();
		if (this.config.Database.ShardCount > 0) {
			sb.Append($"SET citus.shard_count = {this.config.Database.ShardCount}; ");
			Logger.Information($"  citus.shard_count = {this.config.Database.ShardCount}");
		}
		if (this.config.Database.ShardReplicationFactor > 0) {
			sb.Append($"SET citus.shard_replication_factor = {this.config.Database.ShardReplicationFactor}; ");
			Logger.Information($"  citus.shard_replication_factor = {this.config.Database.ShardReplicationFactor}");
		}
		return sb.ToString();
	}

	/// <summary>
	/// Returns whether the target table is already under Citus management (distributed, reference, or Citus
	/// local). <c>pg_dist_partition</c> has a row for all three kinds (<c>partmethod='h'</c> for distributed,
	/// <c>'n'</c> for reference / local), so this one query is enough for an idempotency check.
	/// </summary>
	private async Task<bool> IsCitusManagedAsync(string qualified) {
		return (await Pg.QueryAsync<bool>(
			this.connectionString,
			"SELECT EXISTS(SELECT 1 FROM pg_dist_partition WHERE logicalrelid = @rel::regclass)",
			new { rel = qualified }
		)).First();
	}

	/// <summary>
	/// Makes the given schema.table a Citus metadata-registered local table.
	/// Called from CreateSettingsTableAsync only when the table was freshly created.
	/// No-op without <c>--citus</c> (inner guard).
	/// </summary>
	private async Task RegisterLocalTableAsync(string schemaName, string tableName) {
		if (!this.config.Database.Citus) {
			return;
		}
		var qualified = Pg.QuoteIdentifier(schemaName) + "." + Pg.QuoteIdentifier(tableName);
		if (await this.IsCitusManagedAsync(qualified)) {
			Logger.Information($"  {qualified} is already under Citus management - skipping");
			return;
		}
		await Pg.ExecuteAsync(this.connectionString, $"SELECT citus_add_local_table_to_metadata('{qualified}')");
		Logger.Information($"  {qualified} → local (metadata registered)");
	}

	// ----------------------------------------------------------------------
	// 5.5 Server-side functions for df (statfs) (plperlu) — design of record: docs/df-support.md
	// ----------------------------------------------------------------------

	/// <summary>
	/// Creates the function set that lets `df` return the real free space of the tablespace. With a superuser connection:
	///   <c>{prefix}statvfs(dir)</c> (plperlu: Filesys::Df → df fallback) /
	///   <c>{prefix}fs_free(ts)</c> (plpgsql: node-local dir resolution) /
	///   <c>{prefix}statfs()</c> (the entry point. Citus aggregates over workers, non-Citus is local).
	/// When <c>statfs.mode</c> is <c>nominal</c>, do nothing (the client falls back to the nominal capacity).
	/// With <c>require</c>, fail mkfs if plperlu cannot be enabled.
	/// </summary>
	private async Task CreateStatfsFunctionsAsync(string prefix) {
		var mode = this.config.Statfs.Mode;
		if (mode == "nominal") {
			Logger.Information("statfs.mode=nominal: dropping the df functions (if any); the client falls back to the nominal capacity");
			await this.DropStatfsFunctionsAsync(prefix);
			return;
		}

		// The plperlu gate (app.plperlu). When denied, do not use plperlu at all.
		// require + deny is already fail-fast'd in ValidateConfigCombinations, but branch defensively.
		// auto + deny behaves like nominal (no functions created, fall back to the nominal capacity). Design: docs/settings-and-plperlu.md.
		if (!this.config.App.Plperlu) {
			if (mode == "require") {
				throw new InvalidOperationException(
					"--statfs=require and plperlu denied (--deny-plperlu) cannot be combined (docs/settings-and-plperlu.md)."
				);
			}
			Logger.Information("app.plperlu=false: not using plperlu, so no df functions are created (nominal-capacity fallback)");
			await this.DropStatfsFunctionsAsync(prefix);
			return;
		}

		var schemaQ = Pg.QuoteIdentifier(this.SchemaNameOrDefault());
		var qStatvfs = $"{schemaQ}.{Pg.QuoteIdentifier(prefix + "statvfs")}";
		var qFsFree = $"{schemaQ}.{Pg.QuoteIdentifier(prefix + "fs_free")}";
		var qStatfs = $"{schemaQ}.{Pg.QuoteIdentifier(prefix + "statfs")}";
		// The tablespace df measures is taken from what the DB actually is, not from the setting (so it stays right when mkfs is
		// re-run on an existing DB without --tablespace).
		var ts = (await Pg.QueryAsync<string>(
			this.connectionString,
			"SELECT t.spcname FROM pg_database d JOIN pg_tablespace t ON t.oid = d.dattablespace WHERE d.datname = current_database()"
		)).FirstOrDefault();
		if (string.IsNullOrEmpty(ts)) { ts = "pg_default"; }
		var tsLit = Pg.QuoteLiteral(ts);
		var userQ = Pg.QuoteIdentifier(this.config.Database.Connection.Username ?? "pgfs");
		var coordSuperPgfs = this.CoordinatorSuperPgfsConnectionString();
		var citus = this.config.Database.Citus;

		// Ensure plperlu (coordinator). Citus auto-syncs CREATE EXTENSION to workers.
		// If it cannot be enabled: fail-fast with require, fall back to nominal and return with auto.
		Logger.Information("statfs: CREATE EXTENSION IF NOT EXISTS plperlu");
		try {
			await Pg.ExecuteAsync(coordSuperPgfs, "CREATE EXTENSION IF NOT EXISTS plperlu");
		} catch (System.Exception ex) {
			if (mode == "require") {
				throw new InvalidOperationException(
					"--statfs=require but plperlu cannot be enabled: " + ex.Message +
					" (check that plperlu is available; docs/df-support.md)."
				);
			}
			Logger.Warning("plperlu unavailable. statfs will fall back to the nominal capacity: " + ex.Message);
			return;
		}

		var statvfsSql = $$"""
			CREATE OR REPLACE FUNCTION {{qStatvfs}}(dir text)
			RETURNS TABLE(total bigint, avail bigint)
			LANGUAGE plperlu AS $PL$
			  my $dir = $_[0];
			  my $r = eval { require Filesys::Df; my $d = Filesys::Df::df($dir, 1);
			                 return [int($d->{blocks}), int($d->{bavail})]; };
			  if ($r && defined $r->[0]) { return [{ total => $r->[0], avail => $r->[1] }]; }
			  my @o = `df -B1 --output=size,avail "$dir" 2>/dev/null`;
			  if (@o >= 2 && $o[1] =~ /(\d+)\s+(\d+)/) { return [{ total => $1+0, avail => $2+0 }]; }
			  return [{ total => undef, avail => undef }];
			$PL$
			""";
		var fsFreeSql = $$"""
			CREATE OR REPLACE FUNCTION {{qFsFree}}(tablespace text DEFAULT 'pg_default')
			RETURNS TABLE(total bigint, avail bigint)
			LANGUAGE plpgsql SECURITY DEFINER AS $FF$
			DECLARE d text; ts_oid oid;
			BEGIN
			  IF tablespace IS NULL OR tablespace IN ('', 'pg_default', 'pg_global') THEN
			    d := current_setting('data_directory');
			  ELSE
			    SELECT t.oid INTO ts_oid FROM pg_tablespace t WHERE t.spcname = tablespace;
			    d := pg_tablespace_location(ts_oid);
			    IF d IS NULL OR d = '' THEN d := current_setting('data_directory'); END IF;
			  END IF;
			  RETURN QUERY SELECT s.total, s.avail FROM {{qStatvfs}}(d) s;
			END;
			$FF$
			""";

		// statvfs / fs_free are needed on every node (the worker aggregation calls each worker's fs_free).
		await this.DeployFnAsync(coordSuperPgfs, citus, statvfsSql);
		await this.DeployFnAsync(coordSuperPgfs, citus, fsFreeSql);

		// The entry-point statfs() is coordinator-only (clients connect to the coordinator).
		var statfsSql = StatfsEntrySql(citus, qStatfs, qFsFree, tsLit);
		await Pg.ExecuteAsync(coordSuperPgfs, statfsSql);

		// The functions access the OS with definer (postgres) privileges. The caller is the pgfs user, so GRANT EXECUTE.
		await Pg.ExecuteAsync(coordSuperPgfs, $"GRANT EXECUTE ON FUNCTION {qStatfs}() TO {userQ}");
		Logger.Information($"statfs: created {qStatfs}() (mode={mode}, citus={citus})");
	}

	/// <summary>
	/// Drops the df functions (nominal mode). So nominal is authoritative even if functions created earlier by
	/// `require`/`auto` remain. Idempotent via `IF EXISTS`. Drops in dependency order statfs → fs_free → statvfs.
	/// Under Citus, also remove them from every node (statfs is coordinator-only, but IF EXISTS makes it a no-op on workers).
	/// </summary>
	private async Task DropStatfsFunctionsAsync(string prefix) {
		var schemaQ = Pg.QuoteIdentifier(this.SchemaNameOrDefault());
		var coordSuperPgfs = this.CoordinatorSuperPgfsConnectionString();
		var drops = new[] {
			$"DROP FUNCTION IF EXISTS {schemaQ}.{Pg.QuoteIdentifier(prefix + "statfs")}()",
			$"DROP FUNCTION IF EXISTS {schemaQ}.{Pg.QuoteIdentifier(prefix + "fs_free")}(text)",
			$"DROP FUNCTION IF EXISTS {schemaQ}.{Pg.QuoteIdentifier(prefix + "statvfs")}(text)",
		};
		foreach (var d in drops) {
			await Pg.ExecuteAsync(coordSuperPgfs, d);
			if (this.config.Database.Citus) {
				await Pg.ExecuteAsync(coordSuperPgfs, $"SELECT run_command_on_all_nodes($RC${d}$RC$)");
			}
		}
	}

	/// <summary>Deploys a function-definition SQL. Citus uses run_command_on_all_nodes (every node); non-Citus runs locally.</summary>
	private async Task DeployFnAsync(string coordSuperPgfs, bool citus, string createFnSql) {
		if (citus) {
			await Pg.ExecuteAsync(coordSuperPgfs, $"SELECT run_command_on_all_nodes($RC${createFnSql}$RC$)");
			return;
		}
		await Pg.ExecuteAsync(coordSuperPgfs, createFnSql);
	}

	/// <summary>The definition SQL of the entry-point <c>{prefix}statfs()</c>. Non-Citus uses local fs_free; Citus aggregates over workers.</summary>
	private static string StatfsEntrySql(bool citus, string qStatfs, string qFsFree, string tsLit) {
		if (!citus) {
			return $$"""
				CREATE OR REPLACE FUNCTION {{qStatfs}}()
				RETURNS TABLE(total bigint, avail bigint)
				LANGUAGE sql SECURITY DEFINER AS $ST$
				  SELECT total, avail FROM {{qFsFree}}({{tsLit}});
				$ST$
				""";
		}
		// Citus: if there are workers holding shards, aggregate over workers (avoids double-counting the coordinator);
		// with 0 workers (a 1-node Citus = the coordinator holds shards) use local fs_free.
		return $$"""
			CREATE OR REPLACE FUNCTION {{qStatfs}}()
			RETURNS TABLE(total bigint, avail bigint)
			LANGUAGE plpgsql SECURITY DEFINER AS $ST$
			DECLARE nworkers int;
			BEGIN
			  SELECT count(*) INTO nworkers FROM pg_dist_node
			    WHERE groupid <> 0 AND isactive AND shouldhaveshards;
			  IF nworkers = 0 THEN
			    RETURN QUERY SELECT f.total, f.avail FROM {{qFsFree}}({{tsLit}}) f;
			  ELSE
			    -- sum() over bigint returns numeric, so cast explicitly to the declared type (bigint).
			    -- Omitting this fails at runtime with "structure of query does not match function result type"
			    -- (this aggregation branch only runs on a multi-worker Citus, so it was discovered late).
			    RETURN QUERY
			      SELECT sum(split_part(r.result, ',', 1)::bigint)::bigint,
			             sum(split_part(r.result, ',', 2)::bigint)::bigint
			      FROM run_command_on_workers(
			        $cmd$ SELECT total||','||avail FROM {{qFsFree}}({{tsLit}}) $cmd$) r
			      WHERE r.success AND r.result ~ '^[0-9]+,[0-9]+$';
			  END IF;
			END;
			$ST$
			""";
	}

	// ----------------------------------------------------------------------
	// 6. Root inode insertion
	// ----------------------------------------------------------------------

	private async Task InsertRootInodeAsync(string prefix) {
		var schemaName = this.SchemaNameOrDefault();
		var tableName = prefix + "inode";
		var schemaQ = Pg.QuoteIdentifier(schemaName);
		var tableQ = Pg.QuoteIdentifier(tableName);

		// --root-access: owner = 0o40755 (directory + rwxr-xr-x) / everyone = 0o41777 (directory + sticky + rwxrwxrwx, same as /tmp).
		// It takes effect only when the root is newly created (ON CONFLICT DO NOTHING leaves an existing one unchanged).
		var rootMode = 0x4000 | Convert.ToInt32("755", 8);
		if (this.config.FileSystem.RootAccess == "everyone") {
			rootMode = 0x4000 | Convert.ToInt32("1777", 8);
		}
		// ON CONFLICT uses the (parent_id, name) UK. Under Citus the PK is the composite (parent_id, id)
		// and no standalone `(id)` UNIQUE exists (Citus rejects a UK that does not include the distribution
		// column parent_id), so the natural (parent_id, name) UK is the conflict-resolution target. The root
		// is unique at (0, '/').
		var inserted = await Pg.ExecuteAsync(
			this.connectionString,
			$@"
				INSERT INTO {schemaQ}.{tableQ} (
					id, parent_id, name, uname, gname, st_mode, st_nlink, st_size, is_junction, created_by, updated_by
				) VALUES (
					0, 0, '/', 'root', 'root', @mode, 1, 0, FALSE, 'system', 'system'
				)
				ON CONFLICT (parent_id, name) DO NOTHING
			",
			new { mode = rootMode }
		);
		if (inserted > 0) {
			Logger.Information($"  created the root directory (inode id=0) (root:root {Convert.ToString(rootMode & 0xFFF, 8)})");
			return;
		}
		Logger.Information("  the root directory (inode id=0) already exists");
		if (!this.config.FileSystem.RootAccessGiven) {
			return;
		}
		// --root-access has no effect on an existing root (it is not rebuilt). Rather than ignoring it silently, show the current permissions and how to change them.
		var current = (await Pg.QueryAsync<int?>(
			this.connectionString,
			$"SELECT st_mode FROM {schemaQ}.{tableQ} WHERE parent_id = 0 AND id = 0"
		)).FirstOrDefault();
		var currentText = "?";
		if (current != null) {
			currentText = Convert.ToString(current.Value & 0xFFF, 8);
		}
		Logger.Warning(
			$"--root-access {this.config.FileSystem.RootAccess} has no effect: the root directory already exists (current permissions {currentText}). " +
			"To change them, mount it and chmod (e.g. sudo chmod 1777 <mount point>)."
		);

	}

	// ----------------------------------------------------------------------
	// 7. SaveTo=Db row insertion
	// ----------------------------------------------------------------------

	/// <summary>
	/// For an existing FS (<c>pgfs_settings</c> has rows), **the FS-specific settings are taken from the DB**. If one was given explicitly
	/// on the CLI / in the TOML and differs from the DB, a Warning is printed ("no effect on an existing FS; use pgfsctl config set to change it").
	/// Creating the statfs functions and <see cref="PopulateSettingsRows"/> afterwards run with these values.
	/// The FS-specific settings are decided once when the FS is created; only pgfsctl changes them later (docs/Mkfs.md).
	/// </summary>
	private void AdoptExistingFsSettings() {
		var store = this.NewSettingsStore();
		var existing = store.LoadAll(Schema.AllFields).ToDictionary(kv => kv.Key, kv => kv.Value);
		this.existingFs = existing.Count > 0;
		if (!this.existingFs) {
			return;
		}
		Logger.Information("existing FS: the FS-specific settings use the DB values (use pgfsctl config set to change them)");
		var fs = this.config.FileSystem;
		this.Adopt(existing, Schema.Audit.Enabled, this.config.Audit.Enabled, v => this.config.Audit.Enabled = v);
		this.Adopt(existing, Schema.Statfs.Mode, this.config.Statfs.Mode, v => this.config.Statfs.Mode = v);
		this.Adopt(existing, Schema.App.Plperlu, this.config.App.Plperlu, v => this.config.App.Plperlu = v);
		this.Adopt(existing, Schema.FileSystem.Version, fs.Version, v => fs.Version = v);
		this.Adopt(existing, Schema.FileSystem.VolumeLabel, fs.VolumeLabel, v => fs.VolumeLabel = v);
		this.Adopt(existing, Schema.FileSystem.ClusterSize, fs.ClusterSize, v => fs.ClusterSize = v);
		this.Adopt(existing, Schema.FileSystem.DefaultChunkSize, fs.DefaultChunkSize, v => fs.DefaultChunkSize = v);
		this.Adopt(existing, Schema.FileSystem.MaxFileSize, fs.MaxFileSize, v => fs.MaxFileSize = v);
		this.Adopt(existing, Schema.FileSystem.UnknownName, fs.UnknownName, v => fs.UnknownName = v);
	}

	private void Adopt<T>(Dictionary<string, string> existing, Field<T> field, T current, Action<T> set) {
		if (!existing.TryGetValue(field.FullKey, out var raw)) {
			return; // No row in the DB (an item added in a later version) -> it is inserted with the value from this run
		}
		var fromDb = field.Parse(raw);
		if (this.wasProvided(field) && !EqualityComparer<T>.Default.Equals(fromDb, current)) {
			Logger.Warning($"{field.FullKey} = {field.Format(current)} has no effect: using the existing FS value {field.Format(fromDb)} (use pgfsctl config set to change it)");
		}
		set(fromDb);
	}

	private ConfigStore NewSettingsStore() {
		return new ConfigStore(
			this.connectionString,
			this.SchemaNameOrDefault(),
			this.config.Database.GetPrefix()
		);
	}

	/// <summary>
	/// Writes the FS-specific settings (<see cref="SaveTarget.Db"/>) to <c>pgfs_settings</c>. On an existing FS this runs after
	/// <see cref="AdoptExistingFsSettings"/> has taken the DB values, so existing rows are rewritten with the same values and only
	/// items added in later versions (e.g. file_system.unknown_name) are added.
	/// Instructions that apply only when creating (citus / shard_count / rf / tablespace / tablespace_path) are not saved since
	/// v0.2.1 (SaveTo=None; they can be read from what the DB actually is).
	/// </summary>
	private void PopulateSettingsRows() {
		Logger.Information("inserting settings rows into pgfs_settings");
		var store = this.NewSettingsStore();
		var fs = this.config.FileSystem;
		store.Save(Schema.FileSystem.Version, fs.Version);
		store.Save(Schema.FileSystem.VolumeLabel, fs.VolumeLabel);
		// The FS sizes are FS identity, so DB-authoritative. A value mismatch across clients could corrupt data via
		// inconsistent chunk-boundary interpretation.
		store.Save(Schema.FileSystem.ClusterSize, fs.ClusterSize);
		store.Save(Schema.FileSystem.DefaultChunkSize, fs.DefaultChunkSize);
		store.Save(Schema.FileSystem.MaxFileSize, fs.MaxFileSize);
		store.Save(Schema.FileSystem.UnknownName, fs.UnknownName);
		store.Save(Schema.Audit.Enabled, this.config.Audit.Enabled);
		store.Save(Schema.Statfs.Mode, this.config.Statfs.Mode);
		store.Save(Schema.App.Plperlu, this.config.App.Plperlu);
	}

	private string SchemaNameOrDefault() {
		var schemaName = this.config.Database.SchemaName;
		if (string.IsNullOrEmpty(schemaName)) {
			return "public";
		}
		return schemaName;
	}

	// ----------------------------------------------------------------------
	// Shared table-creation helper
	// ----------------------------------------------------------------------

	private record ColumnInfo(string Name, string Type, string Constraints);

	/// <summary>
	/// Shared table-creation helper. Returns false and skips if the table already exists; returns true
	/// when it was freshly created. The return value tells the caller "created vs. skipped" and drives
	/// whether Citus distribution / metadata registration is needed.
	/// </summary>
	private async Task<bool> CreateTableAsync(
		string schemaName,
		string tableName,
		IReadOnlyList<ColumnInfo> columns,
		IReadOnlyList<string>? primary = null,
		IReadOnlyList<IReadOnlyList<string>>? unique = null,
		IReadOnlyList<IReadOnlyList<string>>? indexes = null,
		string? partitionBy = null
	) {
		var schemaQ = Pg.QuoteIdentifier(schemaName);
		var tableQ = Pg.QuoteIdentifier(tableName);

		Logger.Information($"checking table '{tableName}'");

		var exists = (await Pg.QueryAsync<uint>(
			this.connectionString,
			@"
				SELECT c.oid
				FROM pg_class c
				JOIN pg_namespace n ON n.oid = c.relnamespace
				WHERE c.relname = @name AND n.nspname = @schema
			",
			new { name = tableName, schema = schemaName }
		)).Any();

		if (exists) {
			Logger.Information($"  table '{tableName}' already exists");
			return false;
		}

		// No per-table TABLESPACE clause. The pgfs DB has a default tablespace from CREATE DATABASE WITH
		// TABLESPACE, so tables inherit it (Citus shards inherit the worker DB default too).
		// This is what lets --citus + a custom tablespace coexist (design in docs/settings-and-plperlu.md).
		var columnDefs = string.Join(",\n\t",
			columns.Select(c => $"{Pg.QuoteIdentifier(c.Name)} {c.Type} {c.Constraints}")
		);
		// For a RANGE-partitioned table (the audit log), add a PARTITION BY clause.
		var partitionClause = "";
		if (!string.IsNullOrEmpty(partitionBy)) {
			partitionClause = $" PARTITION BY RANGE ({Pg.QuoteIdentifier(partitionBy)})";
		}
		Logger.Information($"  creating table '{tableName}'");
		await Pg.ExecuteAsync(
			this.connectionString,
			$"CREATE TABLE {schemaQ}.{tableQ} (\n\t{columnDefs}\n){partitionClause}"
		);

		if (primary is { Count: > 0 }) {
			await Pg.ExecuteAsync(
				this.connectionString,
				$"ALTER TABLE {schemaQ}.{tableQ} " +
				$"ADD CONSTRAINT {Pg.QuoteIdentifier($"pk_{tableName}")} " +
				$"PRIMARY KEY ({string.Join(", ", primary.Select(Pg.QuoteIdentifier))})"
			);
		}

		if (unique is { Count: > 0 }) {
			var i = 0;
			foreach (var cols in unique) {
				await Pg.ExecuteAsync(
					this.connectionString,
					$"CREATE UNIQUE INDEX {Pg.QuoteIdentifier($"uk_{tableName}_{++i}")} " +
					$"ON {schemaQ}.{tableQ} ({string.Join(", ", cols.Select(Pg.QuoteIdentifier))})"
				);
			}
		}

		if (indexes is { Count: > 0 }) {
			var i = 0;
			foreach (var cols in indexes) {
				await Pg.ExecuteAsync(
					this.connectionString,
					$"CREATE INDEX {Pg.QuoteIdentifier($"ix_{tableName}_{++i}")} " +
					$"ON {schemaQ}.{tableQ} ({string.Join(", ", cols.Select(Pg.QuoteIdentifier))})"
				);
			}
		}
		return true;
	}
}
