namespace Pgfs.Lib.Utility;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Logging;
using Npgsql;

public static partial class Pg
{
	private static readonly ConcurrentDictionary<string, NpgsqlDataSource> dataSources = new();

	private static readonly ReadOnlyIndexer<string, NpgsqlDataSource> DataSource = new(
		delegate(string connectionString) {
			return Pg.dataSources.GetOrAdd(
				connectionString,
				delegate(string connectionString) {
					return new NpgsqlDataSourceBuilder(connectionString).Build();
				}
			);
		}
	);

	// ---
	//
	// Opening a connection is wrapped in [Retry.Sync](Retry.cs) / [Retry.Async](Retry.cs):
	// it retries with exponential backoff on transient failures such as a PG blip, restart, or network blip.
	// Query execution (the Query / Execute calls) is not wrapped, because of idempotency concerns.

	private static NpgsqlConnection OpenSync(string cs) {
		return Retry.Sync(() => Pg.DataSource[cs].OpenConnection());
	}

	private static Task<NpgsqlConnection> OpenAsync(string cs) {
		return Retry.Async(() => Pg.DataSource[cs].OpenConnectionAsync().AsTask());
	}

	public static async Task<IEnumerable<T>> QueryAsync<T>(string connectionString, string sql, object? args = null) {
		await using var conn = await OpenAsync(connectionString);
		TraceQuery(sql, args);
		return await conn.QueryAsync<T>(sql, args);
	}

	public static async Task<IEnumerable<dynamic>> QueryAsync(string connectionString, string sql, object? args = null) {
		await using var conn = await OpenAsync(connectionString);
		TraceQuery(sql, args);
		return await conn.QueryAsync(sql, args);
	}

	public static IEnumerable<T> Query<T>(string connectionString, string sql, object? args = null) {
		using var conn = OpenSync(connectionString);
		TraceQuery(sql, args);
		return conn.Query<T>(sql, args);
	}

	public static IEnumerable<dynamic> Query(string connectionString, string sql, object? args = null) {
		using var conn = OpenSync(connectionString);
		TraceQuery(sql, args);
		return conn.Query(sql, args);
	}

	public static async Task<int> ExecuteAsync(string connectionString, string sql, object? args = null) {
		await using var conn = await OpenAsync(connectionString);
		TraceQuery(sql, args);
		return await conn.ExecuteAsync(sql, args);
	}

	public static int Execute(string connectionString, string sql, object? args = null) {
		using var conn = OpenSync(connectionString);
		TraceQuery(sql, args);
		return conn.Execute(sql, args);
	}

	// ---

	/// <summary>
	/// Opens a connection through the pool. The caller must <c>Dispose</c> it.
	/// For when you want to handle it as `using var conn = ...`, e.g. for Large Object operations.
	/// Transient failures are retried via [Retry.Sync](Retry.cs).
	/// </summary>
	public static NpgsqlConnection OpenConnection(string connectionString) {
		return OpenSync(connectionString);
	}

	/// <summary>
	/// Acquires a single connection and passes it to the callback. Does not open a transaction.
	/// </summary>
	public static T WithConnection<T>(string connectionString, Func<NpgsqlConnection, T> body) {
		using var conn = OpenSync(connectionString);
		return body(conn);
	}

	/// <summary>
	/// Opens a transaction on a single connection and runs the callback.
	/// Rolls back on exception, commits on normal completion.
	/// Large Object operations <b>must</b> run inside a transaction, so use this for them.
	/// </summary>
	public static T WithTransaction<T>(string connectionString, Func<NpgsqlConnection, NpgsqlTransaction, T> body) {
		using var conn = OpenSync(connectionString);
		using var tx = conn.BeginTransaction();
		try {
			var result = body(conn, tx);
			tx.Commit();
			return result;
		} catch {
			try {
				tx.Rollback();
			} catch {
				// ignore rollback failures (the connection may already be broken)
			}
			throw;
		}
	}

	// ---


	public static string QuoteIdentifier(string identifier) {
		if (identifier.AsSpan().ContainsAny(['\'', '\"', ' ', ';', '$'])) {
			return "\"" + identifier.Replace("\"", "\"\"") + "\"";
		}
		return identifier;
	}

	public static string QuoteLiteral(object? literal) {
		if (literal == null) {
			return "NULL";
		}

		if (literal is not string s) {
			var t = literal.ToString();
			if (t == null) {
				return "NULL";
			}

			return t;
		}

		return "'" + s.Replace("'", "''") + "'";
	}

	public static void TraceQuery(string sql, object? args) {
		// When the Trace level is disabled, skip Regex.Replace and JsonSerializer.Serialize entirely.
		// SQL can fire hundreds of times per second on a hot path, so the overhead must be zero when disabled.
		if (!Logger.IsTraceEnabled) {
			return;
		}
		Logger.Trace(
			string.Join(" ", Pg.Spaces.Replace(sql, " ").Trim()),
			"; ",
			JsonSerializer.Serialize(args)
		);
	}

	private static readonly Regex Spaces = RegexPattern();

	[GeneratedRegex("\\s+", RegexOptions.Singleline)]
	private static partial Regex RegexPattern();
}
