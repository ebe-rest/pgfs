namespace Pgfs.Mkfs;

using Core.Logging;
using Core.Utility;
using Npgsql;

/// <summary>
/// The "before deleting" steps of <c>--clean</c> / <c>--purge</c> (v0.2.1).
/// <list type="number">
///   <item>Count what is connected (live mounts / other connections) and, if any, **ask whether to retry** (<c>--now</c> disconnects them and goes on; non-interactive aborts)</item>
///   <item>Show what will be deleted (the target / the schemas in the DB / the Citus workers) and **ask for confirmation** (skipped with <c>--yes</c>; non-interactive without it aborts)</item>
/// </list>
/// <c>--now</c> removes only step 1 and <c>--yes</c> only step 2 (to remove both, use <c>--yes --now</c>). Specification: docs/Mkfs.md.
/// </summary>
public partial class Initializer
{
	/// <summary>Heartbeat age in seconds under which a mount is considered live (the same as live in <c>pgfsctl status</c>).</summary>
	private const int LiveMountSeconds = 90;

	/// <summary>The application_name of our own (this mkfs's) connection. Excluded when counting what is connected.</summary>
	private string ownApplicationName = "mkfs.pgfs";

	/// <summary>The operation name shown in logs and confirmations.</summary>
	private string TeardownFlag => this.config.Purge switch {
		true  => "--purge",
		false => "--clean",
	};

	/// <summary>The target DB name (default <c>pgfs</c>).</summary>
	private string TargetDatabaseName() {
		var db = this.config.Database.Connection.Database;
		if (string.IsNullOrEmpty(db)) {
			return "pgfs";
		}
		return db;
	}

	/// <summary>The coordinator target (host:port/db) in human-readable form.</summary>
	private string TargetDescription() {
		var b = new NpgsqlConnectionStringBuilder(this.superConnectionString);
		return $"{b.Host}:{b.Port}/{this.TargetDatabaseName()}";
	}

	/// <summary>
	/// Waits until nothing is connected. With <c>--now</c> it only counts and shows them, then goes on (they are cut when deleting).
	/// Interactively it repeats "retry? (y/n)" and recounts on y. On n / non-interactive, <see cref="MkfsAbortedException"/>.
	/// </summary>
	private async Task WaitUntilNoConnectionsAsync(List<(string Host, int Port)> workers, bool coordExists) {
		while (true) {
			var busy = await this.DescribeConnectionsAsync(workers, coordExists);
			if (busy.Count == 0) {
				return;
			}
			Prompt($"{this.TeardownFlag}: there are connections to {this.TargetDescription()}:");
			foreach (var line in busy) {
				Prompt("  " + line);
			}
			if (this.config.Now) {
				Logger.Warning($"{this.TeardownFlag} --now: disconnecting them and going on");
				return;
			}
			if (Console.IsInputRedirected) {
				throw new MkfsAbortedException("There are connections, so nothing is deleted (use --now to disconnect them and go on, or unmount and run it again)");
			}
			if (!Ask("Retry after unmounting? (y/n): ")) {
				throw new MkfsAbortedException("Aborted (nothing was deleted)");
			}
		}
	}

	/// <summary>
	/// Describes what is connected, one line each (empty if nothing).
	/// **Live mounts** = rows with a recent heartbeat in the <c>*mounts</c> tables (those with a heartbeat_at column) of every schema in the target DB.
	/// **Other connections** = client backends in <c>pg_stat_activity</c> (excluding our own and Citus's internal ones). Under Citus, the same-named DB on each worker too.
	/// </summary>
	private async Task<List<string>> DescribeConnectionsAsync(List<(string Host, int Port)> workers, bool coordExists) {
		var lines = new List<string>();
		if (coordExists) {
			lines.AddRange(await this.DescribeLiveMountsAsync());
		}
		// Give back our own connection opened to read the mounts tables first (it is also excluded by application_name, but this leaves less room for miscounting).
		Pg.ClearPools();
		lines.AddRange(await this.DescribeSessionsAsync(this.superConnectionString, "coordinator"));
		foreach (var worker in workers) {
			lines.AddRange(await this.DescribeSessionsAsync(this.WorkerSuperConnectionString(worker), $"worker {worker.Host}:{worker.Port}"));
		}
		return lines;
	}

	private async Task<List<string>> DescribeLiveMountsAsync() {
		var conn = this.CoordinatorSuperPgfsConnectionString();
		var tables = await Pg.QueryAsync<(string Schema, string Table)>(
			conn,
			@"SELECT c.table_schema, c.table_name FROM information_schema.columns c
			  WHERE c.column_name = 'heartbeat_at' AND c.table_name LIKE '%mounts'
			  ORDER BY 1, 2"
		);
		var lines = new List<string>();
		foreach (var (schema, table) in tables) {
			var rows = await Pg.QueryAsync<(string Host, string MountPoint)>(
				conn,
				$@"SELECT host, mountpoint FROM {Pg.QuoteIdentifier(schema)}.{Pg.QuoteIdentifier(table)}
				   WHERE heartbeat_at > (now() AT TIME ZONE 'UTC') - make_interval(secs => {LiveMountSeconds})
				   ORDER BY host, mountpoint"
			);
			foreach (var (host, mountPoint) in rows) {
				lines.Add($"mount: {host} {mountPoint} (schema {schema})");
			}
		}
		return lines;
	}

	private async Task<List<string>> DescribeSessionsAsync(string superConn, string nodeLabel) {
		var rows = await Pg.QueryAsync<(string App, long Count)>(
			superConn,
			@"SELECT COALESCE(NULLIF(application_name, ''), '(unnamed)'), count(*)
			  FROM pg_stat_activity
			  WHERE datname = @db AND pid <> pg_backend_pid() AND backend_type = 'client backend'
			    AND application_name NOT LIKE 'citus%' AND application_name <> @own
			  GROUP BY 1 ORDER BY 1",
			new { db = this.TargetDatabaseName(), own = this.ownApplicationName }
		);
		return rows.Select(r => $"connection [{nodeLabel}]: {r.App} x {r.Count}").ToList();
	}

	/// <summary>
	/// Shows what will be deleted and asks for confirmation. With <c>--yes</c> it only shows. Non-interactive without <c>--yes</c> gives <see cref="MkfsAbortedException"/>.
	/// **--clean deletes the whole DB**, so every schema in the DB is listed to make it clear that schemas other than the target go too.
	/// </summary>
	private async Task ConfirmTeardownAsync(List<(string Host, int Port)> workers, bool coordExists) {
		var what = this.config.Purge switch {
			true  => "delete it and stop",
			false => "delete and rebuild it",
		};
		Prompt($"{this.TeardownFlag}: about to {what}: database {this.TargetDescription()}.");
		if (coordExists) {
			var schemas = await Pg.QueryAsync<string>(
				this.CoordinatorSuperPgfsConnectionString(),
				@"SELECT nspname FROM pg_namespace
				  WHERE nspname NOT LIKE 'pg\_%' AND nspname NOT IN ('information_schema', 'citus', 'citus_internal', 'columnar', 'columnar_internal')
				  ORDER BY 1"
			);
			Prompt("  schemas in the DB (all of them are deleted): " + string.Join(", ", schemas));
		}
		if (workers.Count > 0) {
			Prompt("  Citus workers (the same-named DB is deleted too): " + string.Join(", ", workers.Select(w => $"{w.Host}:{w.Port}")));
		}
		Prompt("  Roles / tablespaces / the settings file are not deleted.");
		if (this.config.Yes) {
			return;
		}
		if (Console.IsInputRedirected) {
			throw new MkfsAbortedException("Cannot answer the confirmation, so nothing is deleted (use --yes to delete non-interactively)");
		}
		if (!Ask("Delete? (y/N): ")) {
			throw new MkfsAbortedException("Aborted (nothing was deleted)");
		}
	}

	/// <summary>Confirmations and lists go to stderr (so the person running it sees them even when logs go to a file). They are logged too.</summary>
	private static void Prompt(string line) {
		Console.Error.WriteLine(line);
		Logger.Information(line);
	}

	/// <summary>true for y / yes. Anything else (including empty = the default No) is false.</summary>
	private static bool Ask(string question) {
		Console.Error.Write(question);
		var answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
		return answer == "y" || answer == "yes";
	}
}

/// <summary>When mkfs ends without doing anything by the user's decision (or because it could not ask). Program turns it into exit 3.</summary>
public sealed class MkfsAbortedException(string message) : Exception(message);
