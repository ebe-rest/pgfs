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

		await this.InsertRootInodeAsync(prefix);
		this.PopulateSettingsRows();
	}

	/// <summary>
	/// Validates the combination of settings early (fail-fast before any destructive operation).
	/// Current check: with <c>--citus</c>, <c>--tablespace</c> cannot be anything but pg_default
	/// (Citus shard placement fails unless a tablespace of the same name exists on every worker, so we
	/// do not support combining the two).
	/// </summary>
	private void ValidateConfigCombinations() {
		if (this.config.Database.Citus) {
			var ts = this.config.Database.TablespaceName;
			if (!string.IsNullOrEmpty(ts) && ts != "pg_default") {
				throw new InvalidOperationException(
					$"--citus and --tablespace='{ts}' cannot be combined. Under Citus the tablespace is forced to pg_default " +
					"(see docs/support_for_citus.md)."
				);
			}
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

		var exists = (await Pg.QueryAsync<uint>(
			this.superConnectionString,
			"SELECT oid FROM pg_tablespace WHERE spcname = @name",
			new { name = tablespaceName }
		)).Any();

		if (exists) {
			Logger.Information($"  tablespace '{tablespaceName}' already exists");
			return;
		}

		var tablespacePath = this.config.Database.TablespacePath;

		if (string.IsNullOrWhiteSpace(tablespacePath)) {
			throw new InvalidOperationException(
				$"tablespace '{tablespaceName}' does not exist and --tablespace-path was not given."
			);
		}

		// --- Linux-specific ---
		// The tablespace directory must be owned by and writable for the postgres user. You normally need
		// to run the following **before** mkfs:
		//     sudo mkdir -p <tablespacePath>
		//     sudo chown postgres:postgres <tablespacePath>
		//     sudo chmod 0700 <tablespacePath>
		// On macOS the postgres uid/gid may differ.
		// On Windows you need to configure NTFS ACLs (this tool does not handle that yet).
		// -----------------------

		var userName = this.config.Database.Connection.Username ?? "pgfs";
		Logger.Information($"  creating tablespace '{tablespaceName}' at {tablespacePath}");
		await Pg.ExecuteAsync(
			this.superConnectionString,
			$"CREATE TABLESPACE {Pg.QuoteIdentifier(tablespaceName)} " +
			$"OWNER {Pg.QuoteIdentifier(userName)} " +
			$"LOCATION {Pg.QuoteLiteral(tablespacePath)}"
		);
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
				new ColumnInfo("xattrs", "JSONB", "NOT NULL DEFAULT '{}'::JSONB"),
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
		store.Save(Schema.FileSystem.Version, this.config.FileSystem.Version);
		store.Save(Schema.FileSystem.VolumeLabel, this.config.FileSystem.VolumeLabel);
		// Audit log on/off (--audit). mount/assign read this row from the DB to enable their hooks.
		store.Save(Schema.Audit.Enabled, this.config.Audit.Enabled);
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

		var tablespaceName = this.config.Database.TablespaceName;
		var tablespaceClause = string.IsNullOrEmpty(tablespaceName) || tablespaceName == "pg_default"
			? ""
			: $" TABLESPACE {Pg.QuoteIdentifier(tablespaceName)}";

		var columnDefs = string.Join(",\n\t",
			columns.Select(c => $"{Pg.QuoteIdentifier(c.Name)} {c.Type} {c.Constraints}")
		);
		// For a RANGE-partitioned table (the audit log), add a PARTITION BY clause. Place it before TABLESPACE.
		var partitionClause = "";
		if (!string.IsNullOrEmpty(partitionBy)) {
			partitionClause = $" PARTITION BY RANGE ({Pg.QuoteIdentifier(partitionBy)})";
		}
		Logger.Information($"  creating table '{tableName}'");
		await Pg.ExecuteAsync(
			this.connectionString,
			$"CREATE TABLE {schemaQ}.{tableQ} (\n\t{columnDefs}\n){partitionClause}{tablespaceClause}"
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
