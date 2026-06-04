namespace Pgfs.Mkfs;

using Lib.Config;
using Lib.Logging;
using Lib.Utility;
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
public class Initializer
{
	private readonly RootConfig config;

	/// <summary>Connection string for connecting as a superuser (the target is a maintenance DB such as template1).</summary>
	private string superConnectionString = "";

	/// <summary>Connection string for connecting to the target DB as the PGFS user.</summary>
	private string connectionString = "";

	public Initializer(RootConfig config) {
		this.config = config;
	}

	public async Task InitializeAsync() {
		Logger.Information("=== PGFS filesystem creation tool ===");

		this.ResolveConnectionStrings();
		this.ValidateConfigCombinations();

		await this.EnsureUserAsync();           // coordinator + (when Citus) each worker, inner guard
		await this.EnsureTablespaceAsync();      // no-op for pg_default; Citus forces pg_default anyway
		await this.DropDatabaseAsync();          // only acts under --clean (coordinator + each worker), inner guard
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
		await this.CreateAuditTableAsync(prefix);
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
		return string.IsNullOrEmpty(host) ? "localhost" : host;
	}

	/// <summary>The coordinator port (or 5432 if 0). Used for Citus topology registration.</summary>
	private int CoordinatorPort() {
		var port = this.config.Database.Connection.Port;
		return port == 0 ? 5432 : port;
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
	/// DROPs the target database only when <c>--clean</c> is given. In addition to the coordinator itself,
	/// when Citus + workers are specified the pgfs DB on each worker is dropped too.
	/// A DB that is absent on a node is skipped (idempotent).
	/// The tablespace and roles are left untouched (a requirement).
	/// </summary>
	private async Task DropDatabaseAsync() {
		if (!this.config.Clean) {
			return;
		}
		// Drop the workers first (dropping the coordinator also drops pg_dist_node, so the workers must be
		// reached over their own super connection, independently of the coordinator).
		foreach (var worker in this.config.Database.Workers) {
			if (!this.config.Database.Citus) { break; }
			await this.DropDatabaseOnAsync(this.WorkerSuperConnectionString(worker), $"worker {worker.Host}:{worker.Port}");
		}
		await this.DropDatabaseOnAsync(this.superConnectionString, "coordinator");
	}

	/// <summary>DROP DATABASE on the target DB over the given super connection.</summary>
	private async Task DropDatabaseOnAsync(string superConn, string nodeLabel) {
		var databaseName = this.config.Database.Connection.Database;
		if (string.IsNullOrEmpty(databaseName)) {
			databaseName = "pgfs";
		}
		Logger.Information($"[{nodeLabel}] --clean: dropping database '{databaseName}'");

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
				new ColumnInfo("st_mtime", "TIMESTAMP", "NOT NULL DEFAULT current_timestamp"),
				new ColumnInfo("st_ctime", "TIMESTAMP", "NOT NULL DEFAULT current_timestamp"),
				new ColumnInfo("link_target", "TEXT", "NULL"),
				new ColumnInfo("is_junction", "BOOLEAN", "NOT NULL DEFAULT FALSE"),
				new ColumnInfo("data_id", "BIGINT", "NULL"),
				// Extended attributes (xattr): parallel arrays pairing names TEXT[] and values BYTEA[] at the same index
				// (migrated from the old JSONB + Base64; values kept faithfully as bytea. Design: docs/xattr-bytea.md).
				new ColumnInfo("xattr_names", "TEXT[]", "NOT NULL DEFAULT '{}'::TEXT[]"),
				new ColumnInfo("xattr_values", "BYTEA[]", "NOT NULL DEFAULT '{}'::BYTEA[]"),
				new ColumnInfo("created_at", "TIMESTAMP", "NOT NULL DEFAULT current_timestamp"),
				new ColumnInfo("created_by", "TEXT", "NOT NULL"),
				new ColumnInfo("updated_at", "TIMESTAMP", "NOT NULL DEFAULT current_timestamp"),
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
		if (!created) { return; }
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
				new ColumnInfo("created_at", "TIMESTAMP", "NOT NULL DEFAULT current_timestamp"),
				new ColumnInfo("created_by", "TEXT", "NOT NULL"),
				new ColumnInfo("updated_at", "TIMESTAMP", "NOT NULL DEFAULT current_timestamp"),
				new ColumnInfo("updated_by", "TEXT", "NOT NULL"),
			},
			primary: new[] { "id" }
		);
		if (!created) { return; }
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
				new ColumnInfo("created_at", "TIMESTAMP", "NOT NULL DEFAULT current_timestamp"),
				new ColumnInfo("created_by", "TEXT", "NOT NULL"),
				new ColumnInfo("updated_at", "TIMESTAMP", "NOT NULL DEFAULT current_timestamp"),
				new ColumnInfo("updated_by", "TEXT", "NOT NULL"),
			},
			primary: new[] { "data_id", "chunk_index" }
		);
		if (!created) { return; }
		// Co-located with pgfs_data so all chunks of one file land on the same shard.
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
		if (!created) { return; }
		await this.DistributeTableAsync(schemaName, tableName, "target_id");
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
				new ColumnInfo("created_at", "TIMESTAMP", "NOT NULL DEFAULT current_timestamp"),
				new ColumnInfo("created_by", "TEXT", "NOT NULL"),
				new ColumnInfo("updated_at", "TIMESTAMP", "NOT NULL DEFAULT current_timestamp"),
				new ColumnInfo("updated_by", "TEXT", "NOT NULL"),
			},
			primary: new[] { "scope", "key" }
		);
		if (!created) { return; }
		// pgfs_settings is coordinator-only local + metadata-registered (visible from workers via metadata for JOINs).
		await this.RegisterLocalTableAsync(schemaName, tableName);
	}

	/// <summary>
	/// Creates the audit log table <c>{prefix}audit</c>: a monthly RANGE-partitioned table keyed on
	/// occurred_at. No DEFAULT partition is created, to avoid the PG restriction that once rows have
	/// accumulated you can no longer CREATE a month partition covering that range (the application
	/// ensures each month partition before INSERT). The canonical design is in
	/// <see href="../../../../docs/audit-log.md"/>.
	/// </summary>
	private async Task CreateAuditTableAsync(string prefix) {
		var schemaName = this.SchemaNameOrDefault();
		var tableName = prefix + "audit";

		var created = await this.CreateTableAsync(
			schemaName,
			tableName,
			new[] {
				new ColumnInfo("id", "BIGSERIAL", "NOT NULL"),
				new ColumnInfo("occurred_at", "TIMESTAMP", "NOT NULL DEFAULT current_timestamp"),
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
		if (!created) { return; }
		// Under Citus, distribute by occurred_at (the classic time-series pattern: hash distribution x
		// monthly RANGE partitioning). Once the parent is distributed, Citus auto-distributes each month
		// partition as the application creates it.
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
		string sql;
		if (coLocateWith != null) {
			var qCo = Pg.QuoteIdentifier(schemaName) + "." + Pg.QuoteIdentifier(coLocateWith);
			sql = $"SELECT create_distributed_table('{qualified}', '{distributionColumn}', colocate_with => '{qCo}')";
			Logger.Information($"  {qualified} → distributed by {distributionColumn} (co-located with {qCo})");
		} else {
			sql = $"SELECT create_distributed_table('{qualified}', '{distributionColumn}')";
			Logger.Information($"  {qualified} → distributed by {distributionColumn}");
		}
		await Pg.ExecuteAsync(this.connectionString, sql);
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
		var ts = this.config.Database.TablespaceName;
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

		// 16877 = 0o40755 = directory + rwxr-xr-x.
		// ON CONFLICT uses the (parent_id, name) UK. Under Citus the PK is the composite (parent_id, id)
		// and no standalone `(id)` UNIQUE exists (Citus rejects a UK that does not include the distribution
		// column parent_id), so the natural (parent_id, name) UK is the conflict-resolution target. The root
		// is unique at (0, '/').
		await Pg.ExecuteAsync(
			this.connectionString,
			$@"
				INSERT INTO {schemaQ}.{tableQ} (
					id, parent_id, name, uname, gname, st_mode, st_nlink, st_size, is_junction, created_by, updated_by
				) VALUES (
					0, 0, '/', 'root', 'root', 16877, 1, 0, FALSE, 'system', 'system'
				)
				ON CONFLICT (parent_id, name) DO NOTHING
			"
		);
		Logger.Information("  ensured the root directory (inode id=0)");
	}

	// ----------------------------------------------------------------------
	// 7. SaveTo=Db row insertion
	// ----------------------------------------------------------------------

	/// <summary>
	/// UPSERTs the <see cref="SaveTarget.Db"/> fields of <see cref="Schema.AllFields"/> into
	/// <c>pgfs_settings</c>. The targets are: mount.fallback_uname / mount.fallback_gname /
	/// file_system.version / file_system.volume_label / audit.enabled.
	/// </summary>
	private void PopulateSettingsRows() {
		Logger.Information("inserting settings rows into pgfs_settings");
		var store = new ConfigStore(
			this.connectionString,
			this.SchemaNameOrDefault(),
			this.config.Database.GetPrefix()
		);
		store.Save(Schema.Mount.FallbackUname, this.config.Mount.FallbackUname);
		store.Save(Schema.Mount.FallbackGname, this.config.Mount.FallbackGname);
		// The tablespace settings are also FS identity, so DB-authoritative (not overridable via the settings file).
		store.Save(Schema.Database.TablespaceName, this.config.Database.TablespaceName);
		store.Save(Schema.Database.TablespacePath, this.config.Database.TablespacePath);
		store.Save(Schema.FileSystem.Version, this.config.FileSystem.Version);
		store.Save(Schema.FileSystem.VolumeLabel, this.config.FileSystem.VolumeLabel);
		// The FS sizes are FS identity, so DB-authoritative.
		// A value mismatch across clients could corrupt data via inconsistent chunk-boundary interpretation (docs/settings-and-plperlu.md).
		store.Save(Schema.FileSystem.ClusterSize, this.config.FileSystem.ClusterSize);
		store.Save(Schema.FileSystem.DefaultChunkSize, this.config.FileSystem.DefaultChunkSize);
		store.Save(Schema.FileSystem.MaxFileSize, this.config.FileSystem.MaxFileSize);
		// Audit log on/off (--audit). mount/assign read this row from the DB to enable their hooks.
		store.Save(Schema.Audit.Enabled, this.config.Audit.Enabled);
		// df (statfs) mode (--statfs, the app.statfs key). Recorded as an FS property common to all clients.
		store.Save(Schema.Statfs.Mode, this.config.Statfs.Mode);
		// Whether this FS is Citus-enabled (--citus, the database.citus key, SaveTo=Db). For after-the-fact confirmation.
		store.Save(Schema.Database.Citus, this.config.Database.Citus);
		// The plperlu allow gate (app.plperlu). For reuse on a mkfs re-run + a record.
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
