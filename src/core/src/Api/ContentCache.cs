namespace Pgfs.Core.Api;

using System;
using System.Collections.Generic;

/// <summary>
/// The read cache for file bodies (<c>{prefix}data_chunk</c>) and the dirty buffer for write-back.
/// The key is <c>(data_id, chunk_index)</c> and the value is a full chunk payload.
/// It is a separate layer from <see cref="InodeCache"/> (metadata) and saves reads from a database round trip.
///
/// <para>
/// **Eviction**: once the total byte count exceeds <c>mount.cache_data_max_bytes</c>, entries are evicted
/// in one batch by ascending <c>CacheTime</c> (LRU) down to the low-water mark (7/8 of the budget) - the
/// same batching <see cref="InodeCache"/> uses. <c>maxBytes &lt;= 0</c> disables it (no read caching).
/// **Dirty entries are never evicted** (dropping one would lose a write) and their bytes are accounted
/// separately (<see cref="DirtyBytes"/>). Write-back therefore still works with
/// <c>cache_data_max_bytes = 0</c>.
/// </para>
///
/// <para>
/// **Coherence**: on write, truncate and release the caller drops every entry of that data_id through
/// <see cref="InvalidateData"/> (write-invalidate). A write from another client arrives as a data-write
/// NOTIFY and goes through the same <see cref="InvalidateData"/>.
/// **The stale-read race** is closed by <see cref="Generation"/>: a read captures the generation when it
/// starts, and <see cref="PutIfGeneration"/> only accepts the payload when the generation has not changed
/// (<see cref="InvalidateData"/> does the <c>++</c>). That makes "a payload older than the last write is
/// still in the cache" impossible (= no permanent staleness). A single read racing a concurrent write may
/// still mix old and new bytes; that is accepted as being no worse than a page cache.
/// </para>
///
/// <para>
/// **Write-back**: a dirty entry always holds **the complete image of the chunk**. For a partial write to
/// an existing chunk the caller reads the full chunk from the database and passes it as <c>seed</c>
/// (measured at 0.4 ms - practically free against the 25-30 ms a write costs). That way the read path sees
/// the latest bytes without knowing about dirty state at all, and a flush is
/// **one statement replacing the whole payload of one chunk**. The design lives in
/// <see href="../../../../docs/design/runtime-control-plane.md"/>, the data write-back section.
/// </para>
/// </summary>
public sealed class ContentCache
{
	private long maxBytes;
	private readonly Dictionary<(long DataId, int ChunkIndex), Entry> entries = new();
	private long currentBytes;
	private long dirtyBytes;
	private long generation;
	// Cumulative statistics (for Layer 3 status). All of them are incremented under lock(this).
	private long hits;
	private long misses;
	private long evictions;

	/// <summary>The minimum allocation for a dirty buffer - a floor that keeps a small file from reserving a whole chunk.</summary>
	private const int MinDirtyCapacity = 8 * 1024;

	private sealed class Entry
	{
		/// <summary>Where the payload is stored. While dirty it may have slack beyond <see cref="Len"/> (reserved capacity).</summary>
		public required byte[] Payload { get; set; }
		/// <summary>The valid length. For a clean entry it equals <c>Payload.Length</c>.</summary>
		public required int Len { get; set; }
		public DateTime CacheTime { get; set; }
		/// <summary>Whether the entry holds an unflushed write. While true it is never evicted.</summary>
		public bool Dirty { get; set; }
		/// <summary>The payload length in the database (0 when the row does not exist). Used for the occupied-bytes delta at flush time.</summary>
		public long PrevLength { get; set; }

		/// <summary>How much of the byte budget this entry occupies (a dirty entry counts its reserved capacity).</summary>
		public int Weight {
			get { return this.Payload.Length; }
		}
	}

	public ContentCache(long maxBytes) {
		this.maxBytes = maxBytes;
	}

	/// <summary>Whether the read cache is enabled (<c>cache_data_max_bytes &gt; 0</c>). Write-back's dirty buffers work even when it is not.</summary>
	public bool Enabled {
		get { return this.maxBytes > 0; }
	}

	/// <summary>How many bytes the unflushed dirty buffers occupy (accounted separately from the read budget).</summary>
	public long DirtyBytes {
		get { lock (this) { return this.dirtyBytes; } }
	}

	/// <summary>The current invalidation generation. A read captures it at the start and passes it to <see cref="PutIfGeneration"/>.</summary>
	public long Generation {
		get { lock (this) { return this.generation; } }
	}

	/// <summary>
	/// Returns the cached full chunk payload, or false when there is none. A hit refreshes the LRU recency.
	/// <paramref name="length"/> is the valid length (any slack in <paramref name="payload"/> beyond it is meaningless).
	/// </summary>
	public bool TryGet(long dataId, int chunkIndex, out byte[] payload, out int length) {
		lock (this) {
			if (this.entries.TryGetValue((dataId, chunkIndex), out var e)) {
				e.CacheTime = DateTime.UtcNow;
				++this.hits;
				payload = e.Payload;
				length = e.Len;
				return true;
			}
			++this.misses;
			payload = Array.Empty<byte>();
			length = 0;
			return false;
		}
	}

	/// <summary>
	/// Caches a chunk payload, but only when <paramref name="expectedGeneration"/> still matches the current
	/// generation (= no <see cref="InvalidateData"/> cut in while the read was running). On a mismatch the
	/// payload could be stale, so it is dropped. A no-op when <c>maxBytes &lt;= 0</c>.
	/// **A dirty entry is never overwritten** (the unflushed write is the newer one).
	/// </summary>
	public void PutIfGeneration(long dataId, int chunkIndex, byte[] payload, long expectedGeneration) {
		if (!this.Enabled) {
			return;
		}
		lock (this) {
			if (this.generation != expectedGeneration) {
				return;
			}
			var key = (dataId, chunkIndex);
			if (this.entries.TryGetValue(key, out var old)) {
				if (old.Dirty) {
					return;
				}
				this.currentBytes -= old.Weight;
			}
			this.entries[key] = new Entry { Payload = payload, Len = payload.Length, CacheTime = DateTime.UtcNow };
			this.currentBytes += payload.Length;
			this.EvictIfOverBudget();
		}
	}

	/// <summary>
	/// Drops the **clean** chunks of a data_id and does the generation <c>++</c> (on write, truncate, release
	/// or a notification from another client).
	/// Dirty entries are kept (an unflushed write is never discarded). Use <see cref="DiscardDirty"/> when the
	/// dirty data really should go.
	/// </summary>
	public void InvalidateData(long dataId) {
		lock (this) {
			++this.generation;
			var toRemove = new List<(long DataId, int ChunkIndex)>();
			foreach (var kv in this.entries) {
				if (kv.Key.DataId != dataId) { continue; }
				if (kv.Value.Dirty) { continue; }
				toRemove.Add(kv.Key);
			}
			foreach (var k in toRemove) {
				this.currentBytes -= this.entries[k].Weight;
				this.entries.Remove(k);
			}
		}
	}

	/// <summary>
	/// **Drops every clean chunk** (used to recover after a missed notification).
	/// <b>Dirty entries are kept</b> - same rule as <see cref="InvalidateData"/>, because discarding unflushed
	/// data would lose what was written.
	/// </summary>
	public void InvalidateAllClean() {
		lock (this) {
			++this.generation;
			var toRemove = new List<(long DataId, int ChunkIndex)>();
			foreach (var kv in this.entries) {
				if (kv.Value.Dirty) { continue; }
				toRemove.Add(kv.Key);
			}
			foreach (var k in toRemove) {
				this.currentBytes -= this.entries[k].Weight;
				this.entries.Remove(k);
			}
		}
	}

	// ------------------------------------------------------------------
	// write-back (Phase 1d)
	// ------------------------------------------------------------------

	/// <summary>
	/// Whether the chunk is absent from the cache (= whether the full chunk has to be read from the database
	/// before a partial write to an existing chunk).
	/// </summary>
	public bool NeedsSeed(long dataId, int chunkIndex) {
		lock (this) { return !this.entries.ContainsKey((dataId, chunkIndex)); }
	}

	/// <summary>
	/// A dirty write. Reserves the chunk buffer, places <paramref name="data"/> at
	/// <paramref name="offsetInChunk"/> and returns **the increase in the valid payload length**
	/// (= the increase in occupied bytes).
	/// When there is no entry yet it starts from <paramref name="seed"/> (the full chunk read from the
	/// database, or null when the row does not exist).
	/// </summary>
	/// <param name="prevLength">
	/// The payload length in the database. When <paramref name="seed"/> is supplied this matches its length
	/// and is effectively ignored, but it is a separate parameter for the case where
	/// **the whole chunk is overwritten - so no seed is needed - yet the database length still is**
	/// (to compute the occupied-bytes delta). It is unused when the entry already exists, because the
	/// entry's own value is authoritative then.
	/// </param>
	/// <param name="chunkSize">The chunk size of this file body. The upper bound for the buffer allocation.</param>
	public long WriteDirty(long dataId, int chunkIndex, int offsetInChunk, ReadOnlySpan<byte> data, byte[]? seed, long prevLength, int chunkSize) {
		lock (this) {
			var key = (dataId, chunkIndex);
			var requiredEnd = offsetInChunk + data.Length;
			var e = this.EnsureDirtyEntry(key, seed, prevLength, requiredEnd, chunkSize);
			var before = e.Len;
			data.CopyTo(e.Payload.AsSpan(offsetInChunk, data.Length));
			if (requiredEnd > e.Len) { e.Len = requiredEnd; }
			e.CacheTime = DateTime.UtcNow;
			return e.Len - before;
		}
	}

	/// <summary>
	/// Obtains a dirty entry (creating it when absent, growing its capacity when too small). Call this
	/// while holding the lock.
	/// Promoting a clean entry to dirty moves its bytes from the read budget to the dirty account.
	/// </summary>
	private Entry EnsureDirtyEntry((long DataId, int ChunkIndex) key, byte[]? seed, long prevLength, int requiredEnd, int chunkSize) {
		if (!this.entries.TryGetValue(key, out var e)) {
			var seeded = seed ?? Array.Empty<byte>();
			var entry = new Entry {
				Payload = ContentCache.AllocateBuffer(seeded, requiredEnd, chunkSize),
				Len = seeded.Length,
				CacheTime = DateTime.UtcNow,
				Dirty = true,
				// When a seed was supplied its length is the database length. When it was not (= a whole-chunk
				// overwrite, or no row at all) the length the caller looked up is used. Hard-coding 0 here would
				// make total_size be counted twice.
				PrevLength = Math.Max(prevLength, seeded.Length),
			};
			this.entries[key] = entry;
			this.dirtyBytes += entry.Weight;
			return entry;
		}
		if (!e.Dirty) {
			// Promotion from clean to dirty. The database length is the current payload length.
			this.currentBytes -= e.Weight;
			e.Dirty = true;
			e.PrevLength = e.Len;
			this.dirtyBytes += e.Weight;
		}
		if (e.Payload.Length >= requiredEnd) {
			return e;
		}
		this.dirtyBytes -= e.Weight;
		var grown = e.Payload;
		Array.Resize(ref grown, ContentCache.GrowTo(e.Payload.Length, requiredEnd, chunkSize));
		e.Payload = grown;
		this.dirtyBytes += e.Weight;
		return e;
	}

	/// <summary>The initial allocation of a dirty buffer. A full-chunk write allocates in one go to avoid a resize.</summary>
	private static byte[] AllocateBuffer(byte[] seed, int requiredEnd, int chunkSize) {
		var capacity = ContentCache.GrowTo(seed.Length, requiredEnd, chunkSize);
		var buf = new byte[capacity];
		seed.AsSpan().CopyTo(buf);
		return buf;
	}

	/// <summary>Returns a capacity that satisfies the required length. Doubling growth (floor 8 KiB, ceiling chunkSize) keeps the number of resizes down.</summary>
	private static int GrowTo(int current, int requiredEnd, int chunkSize) {
		if (requiredEnd >= chunkSize) { return chunkSize; }
		var capacity = Math.Max(current, ContentCache.MinDirtyCapacity);
		while (capacity < requiredEnd) {
			capacity *= 2;
		}
		if (capacity > chunkSize) { return chunkSize; }
		return capacity;
	}

	/// <summary>
	/// Enumerates the dirty chunks of a data_id for a flush (**the payload is returned by reference, not
	/// copied**). The caller must hold that file's flush gate, so that a concurrent write cannot move the
	/// buffer underneath.
	/// </summary>
	public List<DirtyChunk> SnapshotDirty(long dataId, IEnumerable<int> chunkIndexes) {
		var list = new List<DirtyChunk>();
		lock (this) {
			foreach (var ix in chunkIndexes) {
				if (!this.entries.TryGetValue((dataId, ix), out var e)) { continue; }
				if (!e.Dirty) { continue; }
				list.Add(new DirtyChunk(ix, e.Payload, e.Len, e.PrevLength));
			}
		}
		return list;
	}

	/// <summary>
	/// Cleanup after a successful flush. Clears the dirty mark and, when the read cache is enabled,
	/// **keeps the entry as a clean one** (what was written is the newest content, so the cache stays warm).
	/// When the read cache is disabled the entry is dropped.
	/// </summary>
	public void MarkFlushed(long dataId, IEnumerable<int> chunkIndexes) {
		lock (this) {
			foreach (var ix in chunkIndexes) {
				var key = (dataId, ix);
				if (!this.entries.TryGetValue(key, out var e)) { continue; }
				if (!e.Dirty) { continue; }
				this.dirtyBytes -= e.Weight;
				if (!this.Enabled) {
					this.entries.Remove(key);
					continue;
				}
				// A clean entry carries the invariant "valid length == array length", so the slack is trimmed.
				e.Dirty = false;
				e.PrevLength = e.Len;
				this.TrimToLength(e);
				this.currentBytes += e.Weight;
			}
			this.EvictIfOverBudget();
		}
	}

	/// <summary>Trims the slack when an entry becomes clean. Call this while holding the lock.</summary>
	private void TrimToLength(Entry e) {
		if (e.Payload.Length == e.Len) { return; }
		var trimmed = new byte[e.Len];
		e.Payload.AsSpan(0, e.Len).CopyTo(trimmed);
		e.Payload = trimmed;
	}

	/// <summary>
	/// **Discards the dirty data of a data_id without writing it** (when the data itself is going away:
	/// truncate to 0, unlink or release). The clean entries go too, and the generation is bumped.
	/// </summary>
	public void DiscardDirty(long dataId) {
		lock (this) {
			++this.generation;
			var toRemove = new List<(long DataId, int ChunkIndex)>();
			foreach (var kv in this.entries) {
				if (kv.Key.DataId != dataId) { continue; }
				toRemove.Add(kv.Key);
			}
			foreach (var k in toRemove) {
				var e = this.entries[k];
				if (e.Dirty) { this.dirtyBytes -= e.Weight; }
				if (!e.Dirty) { this.currentBytes -= e.Weight; }
				this.entries.Remove(k);
			}
		}
	}

	/// <summary>
	/// Re-points dirty data accumulated under a reserved data_id to a different data_id (when another client
	/// committed the data row first).
	/// The reasoning lives in runtime-control-plane.md, in the data write-back section on cross-client conflicts.
	/// </summary>
	public void RekeyData(long fromDataId, long toDataId) {
		lock (this) {
			var moved = new List<(int ChunkIndex, Entry Entry)>();
			var toRemove = new List<(long DataId, int ChunkIndex)>();
			foreach (var kv in this.entries) {
				if (kv.Key.DataId != fromDataId) { continue; }
				moved.Add((kv.Key.ChunkIndex, kv.Value));
				toRemove.Add(kv.Key);
			}
			foreach (var k in toRemove) {
				this.entries.Remove(k);
			}
			foreach (var m in moved) {
				var key = (toDataId, m.ChunkIndex);
				if (this.entries.TryGetValue(key, out var old)) {
					if (old.Dirty) { this.dirtyBytes -= old.Weight; }
					if (!old.Dirty) { this.currentBytes -= old.Weight; }
				}
				// The database length at the new destination is unknown, so it is left at 0. The occupied bytes
				// are recounted from every chunk on the flush side (the rebuild path of ApplyFlushOccupancy).
				m.Entry.PrevLength = 0;
				this.entries[key] = m.Entry;
			}
		}
	}

	/// <summary>Changes the byte budget (<c>cache_data_max_bytes</c>) at run time (live reload). Shrinking evicts by LRU immediately (0 drops everything).</summary>
	public void SetMaxBytes(long maxBytes) {
		lock (this) {
			this.maxBytes = maxBytes;
			this.EvictIfOverBudget();
		}
	}

	/// <summary>
	/// When the clean byte total exceeds the budget, evicts in one batch by ascending CacheTime down to the
	/// low-water mark (7/8 of the budget).
	/// **Dirty entries are excluded** (dropping one would lose a write). Call this while holding the lock.
	/// </summary>
	private void EvictIfOverBudget() {
		if (this.currentBytes <= this.maxBytes) {
			return;
		}
		var lowWater = this.maxBytes - this.maxBytes / 8;
		var sorted = new List<KeyValuePair<(long DataId, int ChunkIndex), Entry>>(this.entries);
		sorted.Sort((a, b) => a.Value.CacheTime.CompareTo(b.Value.CacheTime));
		foreach (var kv in sorted) {
			if (this.currentBytes <= lowWater) {
				break;
			}
			if (kv.Value.Dirty) {
				continue;
			}
			this.currentBytes -= kv.Value.Weight;
			this.entries.Remove(kv.Key);
			++this.evictions;
		}
	}

	/// <summary>A snapshot of the cumulative statistics (for Layer 3 status). The caller computes the hit ratio as hits/(hits+misses).</summary>
	public ContentCacheStats Stats() {
		lock (this) {
			var dirtyChunks = 0;
			foreach (var kv in this.entries) {
				if (kv.Value.Dirty) { ++dirtyChunks; }
			}
			return new ContentCacheStats {
				Entries = this.entries.Count,
				Bytes = this.currentBytes,
				MaxBytes = this.maxBytes,
				Hits = this.hits,
				Misses = this.misses,
				Evictions = this.evictions,
				Generation = this.generation,
				DirtyBytes = this.dirtyBytes,
				DirtyChunks = dirtyChunks,
			};
		}
	}
}

/// <summary>One element of <see cref="ContentCache.SnapshotDirty"/> (a chunk to be flushed).</summary>
/// <param name="ChunkIndex">The chunk number.</param>
/// <param name="Payload">The payload buffer (it may contain slack beyond <paramref name="Length"/>).</param>
/// <param name="Length">The valid length. Only this much is written to the database.</param>
/// <param name="PrevLength">The payload length in the database (for the occupied-bytes delta).</param>
public readonly record struct DirtyChunk(int ChunkIndex, byte[] Payload, int Length, long PrevLength);

/// <summary>The result of <see cref="ContentCache.Stats"/> (a statistics snapshot of the body read cache).</summary>
public sealed record ContentCacheStats
{
	/// <summary>How many chunks are cached (including dirty ones).</summary>
	public required int Entries { get; init; }
	/// <summary>The total clean byte count (what the read budget applies to).</summary>
	public required long Bytes { get; init; }
	/// <summary>The byte budget (<c>cache_data_max_bytes</c>). 0 disables it.</summary>
	public required long MaxBytes { get; init; }
	public required long Hits { get; init; }
	public required long Misses { get; init; }
	/// <summary>The cumulative number of chunks evicted for exceeding the budget.</summary>
	public required long Evictions { get; init; }
	/// <summary>The current invalidation generation (diagnostics).</summary>
	public required long Generation { get; init; }
	/// <summary>How many bytes the unflushed dirty buffers occupy (write-back).</summary>
	public required long DirtyBytes { get; init; }
	/// <summary>How many chunks are unflushed (write-back).</summary>
	public required int DirtyChunks { get; init; }
}
