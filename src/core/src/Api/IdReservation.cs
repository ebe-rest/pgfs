namespace Pgfs.Core.Api;

using System;
using System.Collections.Generic;
using Logging;
using Utility;

/// <summary>
/// Reserves ids of a <c>BIGSERIAL</c> column **in blocks, ahead of time**, for write-back.
///
/// <para>
/// **Why this is needed**: with write-back we do not want a database round trip even on the first
/// write to a new file. The <c>{prefix}data</c> row INSERT is meant to ride along in the flush
/// transaction, yet the key of a dirty chunk already needs a data_id (using the same key scheme as
/// <see cref="ContentCache"/> keeps the read path unchanged). So we **allocate only the id up front**
/// and defer creating the row until flush.
/// </para>
///
/// <para>
/// **How they are taken**: N at a time with a single
/// <c>SELECT nextval(seq) FROM generate_series(1, N)</c>. Every <c>nextval</c> is atomic, so this
/// needs **neither a lock nor serialization** (the ids do not have to form a contiguous range, they
/// only have to be unique). Measured on a Citus 13 distributed sequence: 4.0 ms for 1000 ids.
/// Reserving <c>[min,max)</c> with <c>nextval</c> + <c>setval</c> is faster for a single call, but
/// concurrent reservations overlap and would need serialization such as a table lock, so we do not
/// take that route.
/// </para>
///
/// <para>
/// **Batch size**: once a block runs out the next one is larger (doubling, capped at
/// <see cref="MaxBatch"/>). That keeps the number of round trips logarithmic for a sustained write
/// workload while a one-off write does not allocate ids it will never use.
/// **Unused reserved ids are simply dropped** - they only leave a gap in the sequence, which is harmless.
/// </para>
/// </summary>
internal sealed class IdReservation
{
	private readonly string connectionString;
	private readonly string qualifiedTable;
	private readonly string column;
	private readonly Queue<long> ids = new();
	private int nextBatch = IdReservation.InitialBatch;

	private const int InitialBatch = 64;
	private const int MaxBatch = 4096;

	public IdReservation(string connectionString, string qualifiedTable, string column) {
		this.connectionString = connectionString;
		this.qualifiedTable = qualifiedTable;
		this.column = column;
	}

	/// <summary>Hands out one reserved id. Fetches the next block from the database when the stock is empty (throws on failure).</summary>
	public long Rent() {
		lock (this) {
			if (this.ids.Count == 0) {
				this.Refill();
			}
			return this.ids.Dequeue();
		}
	}

	/// <summary>Allocates the next block and adds it to the stock. Call this while holding the lock.</summary>
	private void Refill() {
		var count = this.nextBatch;
		var rented = Pg.Query<long>(
			this.connectionString,
			$"SELECT nextval(pg_get_serial_sequence(@table, @column)) FROM generate_series(1, @count)",
			new { table = this.qualifiedTable, column = this.column, count }
		);
		foreach (var id in rented) {
			this.ids.Enqueue(id);
		}
		if (this.ids.Count == 0) {
			throw new InvalidOperationException($"IdReservation: could not reserve ids from the sequence of {this.qualifiedTable}.{this.column}");
		}
		if (Logger.IsDebugEnabled) { Logger.Debug("IdReservation: reserved ", this.ids.Count, " ids for ", this.qualifiedTable, ".", this.column); }
		this.nextBatch = Math.Min(IdReservation.MaxBatch, this.nextBatch * 2);
	}
}
