namespace Pgfs.Core.Api;

using System;
using System.Collections.Generic;

/// <summary>
/// The ledger that groups write-back's unflushed state **per file**.
/// The chunk payloads themselves live in <see cref="ContentCache"/>; this class holds which chunks are
/// dirty, the inode's latest size/mtime, whether the data row does not exist yet, and the latch for a
/// failed flush.
///
/// <para>
/// **Locking discipline**: updates to the ledger itself take <c>lock (this)</c>. Writes to an individual
/// file and its flush are serialized by <see cref="DirtyFile.Gate"/> (so that a write during a flush
/// cannot move the buffer out from under it).
/// <see cref="Forget"/> must be called **while still holding the Gate of that file** - otherwise a second
/// instance can be created while a thread still holds the old Gate, and two Gates can flush at the same time.
/// </para>
///
/// <para>The design lives in <see href="../../../../docs/runtime-control-plane.md"/>, section on data write-back.</para>
/// </summary>
internal sealed class DirtySet
{
	private readonly Dictionary<long, DirtyFile> byDataId = new();
	private long flushes;
	private long flushFailures;

	/// <summary>How many clean <see cref="DirtyFile"/> entries are kept around (reused as a ChunkSize cache).</summary>
	private const int CleanKeepLimit = 4096;

	/// <summary>Returns the known state of a file, or null when there is none.</summary>
	public DirtyFile? Find(long dataId) {
		lock (this) {
			this.byDataId.TryGetValue(dataId, out var file);
			return file;
		}
	}

	/// <summary>Gets the state of a file, creating it when absent.</summary>
	public DirtyFile GetOrAdd(long dataId, long inodeId, int chunkSize, bool dataRowPending) {
		lock (this) {
			if (this.byDataId.TryGetValue(dataId, out var found)) {
				return found;
			}
			var file = new DirtyFile {
				DataId = dataId,
				InodeId = inodeId,
				ChunkSize = chunkSize,
				DataRowPending = dataRowPending,
			};
			this.byDataId[dataId] = file;
			return file;
		}
	}

	/// <summary>
	/// Reserves a data_id for a new file and creates its state. To **prevent a double reservation for the
	/// same inode**, it checks under the ledger lock that no reserved row exists yet before calling
	/// <paramref name="rentId"/>.
	/// (With FUSE's multi-threaded writes, several threads arriving at the same new file would otherwise
	/// reserve twice and orphan a data row - the same problem <c>EnsureDataRow</c> used to prevent with an
	/// inode lock.)
	/// </summary>
	public DirtyFile ReserveFor(long inodeId, Func<long> rentId, int chunkSize) {
		lock (this) {
			foreach (var kv in this.byDataId) {
				if (kv.Value.InodeId != inodeId) { continue; }
				if (!kv.Value.DataRowPending) { continue; }
				return kv.Value;
			}
			var dataId = rentId();
			var file = new DirtyFile {
				DataId = dataId,
				InodeId = inodeId,
				ChunkSize = chunkSize,
				DataRowPending = true,
			};
			this.byDataId[dataId] = file;
			return file;
		}
	}

	/// <summary>Removes a file's state from the ledger. **Call this while holding that file's <see cref="DirtyFile.Gate"/>.**</summary>
	public void Forget(long dataId) {
		lock (this) {
			this.byDataId.Remove(dataId);
		}
	}

	/// <summary>
	/// Re-points a reserved data_id to the real data_id that another client committed
	/// (used together with <see cref="ContentCache.RekeyData"/>).
	/// </summary>
	public void Rekey(long fromDataId, long toDataId) {
		lock (this) {
			if (!this.byDataId.Remove(fromDataId, out var file)) { return; }
			file.DataId = toDataId;
			this.byDataId[toDataId] = file;
		}
	}

	/// <summary>Returns the data_ids that have unflushed data, oldest-dirty first.</summary>
	public List<long> DirtyDataIdsOldestFirst() {
		lock (this) {
			var list = new List<DirtyFile>();
			foreach (var kv in this.byDataId) {
				if (!kv.Value.HasPending) { continue; }
				list.Add(kv.Value);
			}
			list.Sort((a, b) => a.FirstDirtyAt.CompareTo(b.FirstDirtyAt));
			var ids = new List<long>(list.Count);
			foreach (var f in list) {
				ids.Add(f.DataId);
			}
			return ids;
		}
	}

	/// <summary>Returns the data_ids that have been dirty since before <paramref name="cutoff"/> (for the time-based trigger).</summary>
	public List<long> DirtyDataIdsOlderThan(DateTime cutoff) {
		lock (this) {
			var ids = new List<long>();
			foreach (var kv in this.byDataId) {
				if (!kv.Value.HasPending) { continue; }
				if (kv.Value.FirstDirtyAt > cutoff) { continue; }
				ids.Add(kv.Value.DataId);
			}
			return ids;
		}
	}

	/// <summary>
	/// Renders each file with unflushed data as one human-readable line, so that an unmount which could not
	/// write everything out in time leaves **what is being lost** in the log. Stops after
	/// <paramref name="limit"/> entries.
	/// </summary>
	public DirtyLossSummary SnapshotDirtyLoss(int limit) {
		var summary = new DirtyLossSummary();
		lock (this) {
			foreach (var kv in this.byDataId) {
				if (!kv.Value.HasPending) { continue; }
				++summary.Total;
				summary.Chunks += kv.Value.Chunks.Count;
				summary.Bytes += kv.Value.Size;
				if (summary.Sample.Count >= limit) { continue; }
				summary.Sample.Add(kv.Value);
			}
			return summary;
		}
	}

	/// <summary>How many files have dirty data.</summary>
	public int DirtyFileCount {
		get {
			lock (this) {
				var n = 0;
				foreach (var kv in this.byDataId) {
					if (kv.Value.HasPending) { ++n; }
				}
				return n;
			}
		}
	}

	/// <summary>Counts a successful flush (for Layer 3 status).</summary>
	public void CountFlush() {
		lock (this) { ++this.flushes; }
	}

	/// <summary>Counts a failed flush (for Layer 3 status).</summary>
	public void CountFailure() {
		lock (this) { ++this.flushFailures; }
	}

	/// <summary>
	/// Trims clean <see cref="DirtyFile"/> entries once too many pile up (they are only kept as a ChunkSize
	/// cache). Entries that still hold dirty data are never dropped.
	/// </summary>
	public void PruneClean() {
		lock (this) {
			if (this.byDataId.Count <= DirtySet.CleanKeepLimit) { return; }
			var toRemove = new List<long>();
			foreach (var kv in this.byDataId) {
				if (kv.Value.HasPending) { continue; }
				toRemove.Add(kv.Key);
			}
			foreach (var id in toRemove) {
				this.byDataId.Remove(id);
			}
		}
	}

	/// <summary>A statistics snapshot (for Layer 3 status). The dirty byte count lives in <see cref="ContentCache"/>.</summary>
	public DirtySetStats Stats() {
		lock (this) {
			var files = 0;
			foreach (var kv in this.byDataId) {
				if (kv.Value.HasPending) { ++files; }
			}
			return new DirtySetStats {
				DirtyFiles = files,
				TrackedFiles = this.byDataId.Count,
				Flushes = this.flushes,
				FlushFailures = this.flushFailures,
			};
		}
	}
}

/// <summary>The unflushed state of one file body (= one <c>{prefix}data</c> row).</summary>
internal sealed class DirtyFile
{
	/// <summary>The lock that serializes writes against flushes. It is held across the database I/O too (it is per file, so the impact stays local).</summary>
	public readonly object Gate = new();

	/// <summary>The id of the file body. Mutable, because a reserved id may have to be re-pointed after losing a race with another client.</summary>
	public required long DataId { get; set; }
	/// <summary>
	/// The writing inode. **Mutable, because it may be re-pointed to a hardlink sibling** - even if the
	/// inode it was pinned to is unlinked, the dirty data still has somewhere to go as long as another
	/// link is alive.
	/// </summary>
	public required long InodeId { get; set; }
	/// <summary>The chunk size of this body (immutable, so it is safe to cache).</summary>
	public required int ChunkSize { get; init; }
	/// <summary>Whether the <c>{prefix}data</c> row does not exist in the database yet (a reserved id is in use). The flush transaction INSERTs it.</summary>
	public required bool DataRowPending { get; set; }

	/// <summary>The numbers of the unflushed chunks.</summary>
	public HashSet<int> Chunks { get; } = new();
	/// <summary>
	/// **The size as seen from memory** (what <c>stat</c> returns while the file is dirty).
	/// <b>This must not be handed to the database by a flush</b> - use <see cref="WriteEnd"/> instead.
	/// </summary>
	public long Size { get; set; }
	/// <summary>
	/// **The furthest byte this dirty state actually wrote** (the maximum of <c>offset + length</c>).
	/// <para>
	/// <b>This is what a flush passes as <c>st_size</c>.</b> <see cref="Size"/> is
	/// <c>Math.Max(inode.Size, ...)</c> and therefore **mixes in the local cache**, so
	/// **flushing after another mount truncated the file rolls the shrunken size back**
	/// (measured on both operating systems: `st_size` went from 4 back to 32, and bytes that should have
	/// been gone were readable again).
	/// The monotonic clamp is done by GREATEST(st_size, @size) in SQL **against the database value**,
	/// so all this side has to pass is the end of what it wrote itself.
	/// </para>
	/// </summary>
	public long WriteEnd { get; set; }
	/// <summary>The st_mtime a flush writes (the time of the write, not the time of the flush).</summary>
	public DateTime Mtime { get; set; }
	/// <summary>Whether the inode side (size/mtime) has unflushed changes.</summary>
	public bool InodeDirty { get; set; }
	/// <summary>When the currently held dirty state appeared (the reference point for the time-based trigger).</summary>
	public DateTime FirstDirtyAt { get; set; } = DateTime.MaxValue;
	/// <summary>
	/// The most recent flush failure. With write-back there is no way to return an error at write time, so
	/// it is latched here and reported (then cleared) on the next <c>fsync</c> or <c>close</c> - the same
	/// contract NFS uses.
	/// </summary>
	public string? Error { get; set; }

	/// <summary>Whether there is anything that needs flushing.</summary>
	public bool HasPending {
		get { return this.Chunks.Count > 0 || this.InodeDirty || this.DataRowPending; }
	}

	/// <summary>Records when the file became dirty (keeps the first time when it is already dirty). Call this while holding the Gate.</summary>
	public void MarkDirtyNow() {
		if (this.FirstDirtyAt != DateTime.MaxValue) { return; }
		this.FirstDirtyAt = DateTime.UtcNow;
	}

	/// <summary>Clears the dirty marks after a successful flush. Call this while holding the Gate.</summary>
	public void ClearDirty() {
		this.Chunks.Clear();
		this.InodeDirty = false;
		this.FirstDirtyAt = DateTime.MaxValue;
		// **The written end is cleared along with the dirty marks.** Keeping it would make the next flush
		// hand the end it wrote last time to GREATEST again, **rolling back whatever another mount shrank
		// in the meantime**.
		this.WriteEnd = 0;
	}
}

/// <summary>
/// The aggregate behind the dirty-data loss report. **Entries beyond the cut-off are not summarized as
/// "and N more" - the totals are reported as well.**
/// </summary>
internal sealed class DirtyLossSummary
{
	/// <summary>The detail lines, up to the limit.</summary>
	public List<DirtyFile> Sample { get; } = new();
	/// <summary>The total number of files with unflushed data (before the cut-off).</summary>
	public int Total { get; set; }
	/// <summary>The total number of unflushed chunks.</summary>
	public int Chunks { get; set; }
	/// <summary>The sum of the logical sizes (an estimate of how many bytes are lost).</summary>
	public long Bytes { get; set; }
}

/// <summary>The result of <see cref="DirtySet.Stats"/>.</summary>
internal sealed record DirtySetStats
{
	/// <summary>How many files have unflushed data.</summary>
	public required int DirtyFiles { get; init; }
	/// <summary>How many files are in the ledger (including clean ChunkSize cache entries).</summary>
	public required int TrackedFiles { get; init; }
	/// <summary>The cumulative number of successful flushes (= the number of write transactions).</summary>
	public required long Flushes { get; init; }
	/// <summary>The cumulative number of failed flushes.</summary>
	public required long FlushFailures { get; init; }
}
