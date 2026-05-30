namespace Pgfs.Lib.Utility;

using System.Net.Sockets;
using Logging;
using Npgsql;

/// <summary>
/// A lightweight retry helper for opening connections. The minimal job, without taking a Polly dependency:
/// <list type="bullet">
/// <item>Automatically retries <b>only transient failures</b> such as "cannot connect", "server is starting up", or "network blip".</item>
/// <item>Does not touch exceptions thrown while executing a query. Silently retrying writes would break idempotency.</item>
/// </list>
/// Settings are applied globally via <see cref="Configure"/> (expected to be called from the Api ctor).
/// </summary>
public static class Retry
{
	public sealed class Settings
	{
		public int MaxAttempts { get; init; } = 5;
		public int InitialDelayMs { get; init; } = 200;
		public int MaxDelayMs { get; init; } = 2000;
	}

	private static Settings current = new();

	public static void Configure(int maxAttempts, int initialDelayMs, int maxDelayMs) {
		Retry.current = new Settings {
			MaxAttempts = Math.Max(1, maxAttempts),
			InitialDelayMs = Math.Max(0, initialDelayMs),
			MaxDelayMs = Math.Max(initialDelayMs, maxDelayMs),
		};
	}

	/// <summary>Synchronous version. Runs `body` and retries with exponential backoff only when an exception judged transient occurs.</summary>
	public static T Sync<T>(Func<T> body, string operation = "open") {
		var s = Retry.current;
		var delay = s.InitialDelayMs;
		Exception? last = null;
		for (var attempt = 1; attempt <= s.MaxAttempts; attempt++) {
			try {
				return body();
			} catch (Exception ex) when (Retry.IsTransient(ex) && attempt < s.MaxAttempts) {
				last = ex;
				Logger.Warning("Retry: ", operation, " transient failure (attempt ", attempt, "/", s.MaxAttempts, "): ", ex.GetType().Name, ": ", ex.Message);
				Thread.Sleep(delay);
				delay = Math.Min(delay * 2, s.MaxDelayMs);
			}
		}
		// The final attempt does not satisfy the `when` (IsTransient), so control normally never falls through here via a regular throw,
		// but just in case (for when the `when` condition evaluated to false).
		throw last ?? new InvalidOperationException("Retry.Sync: unreachable");
	}

	/// <summary>Asynchronous version. Same logic as <see cref="Sync"/>.</summary>
	public static async Task<T> Async<T>(Func<Task<T>> body, string operation = "open") {
		var s = Retry.current;
		var delay = s.InitialDelayMs;
		Exception? last = null;
		for (var attempt = 1; attempt <= s.MaxAttempts; attempt++) {
			try {
				return await body();
			} catch (Exception ex) when (Retry.IsTransient(ex) && attempt < s.MaxAttempts) {
				last = ex;
				Logger.Warning("Retry: ", operation, " transient failure (attempt ", attempt, "/", s.MaxAttempts, "): ", ex.GetType().Name, ": ", ex.Message);
				await Task.Delay(delay);
				delay = Math.Min(delay * 2, s.MaxDelayMs);
			}
		}
		throw last ?? new InvalidOperationException("Retry.Async: unreachable");
	}

	/// <summary>
	/// Decides whether a failure "might recover if retried".
	/// In the connection-open context it errs on the inclusive side (naively this even catches auth failures, but an
	/// auth failure quickly reaches the final attempt after a few tries and is thrown, so it is not fatal).
	/// </summary>
	private static bool IsTransient(Exception ex) {
		// Network layer: socket reset / refused / timeout
		if (ex is SocketException) {
			return true;
		}
		if (ex is TimeoutException) {
			return true;
		}
		// Npgsql: a PostgresException is usually at the SQL level (PG responded with a syntax/permission error).
		// Cannot-connect / server-not-started / admin shutdown are different PG errors, and those are worth retrying.
		if (ex is PostgresException pgex) {
			// some transient SqlStates
			return pgex.SqlState switch {
				"57P03" => true,  // cannot_connect_now (PG starting up)
				"57P01" => true,  // admin_shutdown
				"57P02" => true,  // crash_shutdown
				"08000" => true,  // connection_exception
				"08003" => true,  // connection_does_not_exist
				"08006" => true,  // connection_failure
				"08001" => true,  // sqlclient_unable_to_establish_sqlconnection
				"08004" => true,  // sqlserver_rejected_establishment_of_sqlconnection
				"53300" => true,  // too_many_connections
				_ => false,
			};
		}
		// An NpgsqlException that Npgsql itself throws at the IO layer (the non-PostgresException side) is a connection-failure kind.
		if (ex is NpgsqlException) {
			return true;
		}
		// cases where a socket / npgsql exception is nested inside (e.g. TaskCanceledException → network timeout)
		if (ex.InnerException is { } inner) {
			return IsTransient(inner);
		}
		return false;
	}
}
