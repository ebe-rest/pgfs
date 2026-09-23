namespace Pgfs.Core.Api;

using Collections;
using Logging;
using Models;
using Utility;
using Pgfs.Core.Config;

public class InodeCache
{
	private readonly string connectionString;
	private readonly string tableNamePrefix;
	private readonly string schemaName;
	private readonly RichDictionary<long, Inode> byId;
	private readonly RichDictionary<string, long> byPath;
	private readonly RichDictionary<long, List<Inode>> childrenByParent;
	// The negative lookup (ENOENT) cache: path -> (parent id, expiry). Only used when mount.negative_cache_ttl_ms > 0.
	// It carries the parent id so that InvalidateChildren(parentId) can sweep it (create, rename, delete and
	// remote notifications all go through there).
	private readonly Dictionary<string, NegativeEntry> negativeByPath = new();
	// Pending inodes of metadata write-back: evicting one by LRU would mean there is no row in the database, so
	// it turns into "created it, yet ENOENT". Only the byId entries are exempted from eviction (the children
	// lists are not pinned - pinning a merged list would make another client's changes invisible forever).
	private readonly HashSet<long> pinned = new();
	private readonly Inode root;
	private int cacheMaxEntries;
	private int negativeTtlMs;
	// Cumulative statistics (for Layer 3 status). All of them are incremented under lock(this).
	// Hits and misses are counted at the Load boundary (one Load = one unit): resolved from the cache = a hit,
	// needing a database fetch = a miss.
	private long hits;
	private long misses;
	private long evictions;
	private long negativeHits;

	/// <summary>The cap on negative cache entries when <c>cache_max_entries &lt;= 0</c> (unlimited).</summary>
	private const int NegativeFallbackLimit = 4096;

	private readonly record struct NegativeEntry(long ParentId, DateTime ExpiresAt);

	/// <summary>The schema-qualified inode table name (e.g. <c>"pgfs"."pgfs_inode"</c>).</summary>
	private string QualifiedInodeTable
		=> $"{Pg.QuoteIdentifier(this.schemaName)}.{Pg.QuoteIdentifier(this.tableNamePrefix + "inode")}";

	public InodeCache(RootConfig config) {
		this.connectionString = config.Database.Connection.ConnectionString;
		this.tableNamePrefix = config.Database.GetPrefix();
		this.schemaName = config.Database.SchemaName;
		this.cacheMaxEntries = config.Mount.CacheMaxEntries;
		this.negativeTtlMs = config.Mount.NegativeCacheTtlMs;
		this.byId = new(this.cacheMaxEntries);
		this.byPath = new(this.cacheMaxEntries);
		this.childrenByParent = new(this.cacheMaxEntries);

		var root = this.Load(0);
		// Remember first **whether an existing root was read** (the conventions forbid `else`).
		var rootWasLoaded = root != null;
		if (root == null) {
			root = this.put(
				new() {
					id = 0,
					parent_id = 0,
					name = "/",
					uname = "root",
					gname = "root",
					st_mode = 16877,
					st_nlink = 1,
					st_size = 0,
					st_mtime = DateTime.UtcNow,
					st_ctime = DateTime.UtcNow,
					link_target = null,
					is_junction = false,
					data_id = null,
					xattr_names = System.Array.Empty<string>(),
					xattr_values = System.Array.Empty<byte[]>(),
					created_at = DateTime.UtcNow,
					created_by = "system",
					updated_at = DateTime.UtcNow,
					updated_by = "system",
				},
				"/"
			);
		}
		if (rootWasLoaded) {
			// The paths Api and InodeCache handle are always normalized to `/` separators (Mount receives `/` from FUSE,
			// and Assign has already converted `\` to `/` in NormalizePath). The root is registered in byPath with `/` too.
			// **The create path already registers it through `put(..., "/")`**, so this only inserts it when the root was read.
			this.byPath["/"] = root.Id;
		}
		root.CacheTime = DateTime.MaxValue;
		this.root = root;
	}


	public Inode GetRoot() {
		return this.root;
	}

	/// <summary>
	/// The hook that resolves an inode which is not in the database (a pending entry of metadata write-back).
	/// <see cref="Api"/> installs a callback that consults <see cref="DirtyNamespace"/>. **That callback must
	/// never call back into <see cref="InodeCache"/>** (the lock order is InodeCache, then the ledger).
	/// </summary>
	public Func<long, Inode?>? PendingById { get; set; }

	/// <summary>The hook that resolves a pending inode not in the database by (parent_id, name). The counterpart of <see cref="PendingById"/>.</summary>
	public Func<long, string, Inode?>? PendingByName { get; set; }

	/// <summary>Exempts this id's byId entry from LRU eviction (for a pending inode).</summary>
	public void Pin(long id) {
		lock (this) {
			this.pinned.Add(id);
		}
	}

	/// <summary>Removes the pin (after materialization completes, or after an exact cancellation).</summary>
	public void Unpin(long id) {
		lock (this) {
			this.pinned.Remove(id);
		}
	}


	public Inode Put(Inode inode, string path) {
		lock (this) {
			return this.put(inode, path).UpdateCacheTime();
		}
	}
	private Inode put(Inode inode, string? path) {
		var id = inode.Id;
		inode.UpdateCacheTime();
		this.byId[id] = inode;
		this.putPath(path, id);
		this.EvictIfOverCapacity();
		return inode;
	}

	/// <summary>Registers in byPath and clears any negative marker for the same path (a no-op without a path). Call this under lock(this).</summary>
	private void putPath(string? path, long id) {
		if (path == null) { return; }
		this.byPath[path] = id;
		this.negativeByPath.Remove(path);
	}

	/// <summary>
	/// Removes the cache entry by both id and path.
	/// Used to clean up the old mapping after a rename or delete.
	/// </summary>
	public void Invalidate(long? id, string? path) {
		lock (this) {
			if (id is long targetId) {
				this.byId.Remove(targetId);
			}
			if (path != null) {
				this.byPath.Remove(path);
			}
		}
	}

	/// <summary>
	/// **Throws the whole cache away** (to recover after missed notifications).
	/// <para>
	/// The receiver calls this when the notification queue overflowed and **it no longer knows which ids changed**.
	/// A dropped notification only means "the cache goes stale", but **a positive entry has no TTL**, so unless it
	/// is dropped it **keeps returning a stale value forever** (review M-2).
	/// </para>
	/// <para>
	/// <b>Pinned entries (pending inodes of metadata write-back) are kept.</b>
	/// **They have no row in the database, so discarding them turns into "created it, yet ENOENT"** - the same
	/// reason the pin is not released on LRU eviction.
	/// Negative entries may be dropped (forgetting "it does not exist" only makes the next lookup consult the database again).
	/// </para>
	/// </summary>
	public void InvalidateAll() {
		lock (this) {
			var keepIds = new List<long>(this.pinned);
			var keep = new List<Inode>();
			foreach (var id in keepIds) {
				if (this.byId.TryGetValue(id, out var inode)) { keep.Add(inode); }
			}
			this.byId.Clear();
			this.byPath.Clear();
			this.childrenByParent.Clear();
			this.negativeByPath.Clear();
			foreach (var inode in keep) { this.byId[inode.Id] = inode; }
		}
	}

	/// <summary>
	/// Removes every cache entry rooted at the given path.
	/// For cases such as renaming a directory, where everything underneath is affected too.
	/// </summary>
	public void InvalidatePrefix(string pathPrefix) {
		lock (this) {
			var toRemove = new List<string>();
			foreach (var kv in this.byPath) {
				// Api/InodeCache paths are always `/`-separated (not Path.DirectorySeparatorChar)
				if (kv.Key == pathPrefix || kv.Key.StartsWith(pathPrefix + "/")) {
					toRemove.Add(kv.Key);
				}
			}
			foreach (var p in toRemove) {
				this.byPath.Remove(p);
			}
			this.RemoveNegativesUnder(pathPrefix);
		}
	}

	/// <summary>Sweeps the negative markers under a given prefix (for a remote rename). Call this under lock(this).</summary>
	private void RemoveNegativesUnder(string pathPrefix) {
		if (this.negativeByPath.Count == 0) { return; }
		var toRemove = new List<string>();
		foreach (var kv in this.negativeByPath) {
			if (kv.Key == pathPrefix || kv.Key.StartsWith(pathPrefix + "/")) {
				toRemove.Add(kv.Key);
			}
		}
		foreach (var p in toRemove) {
			this.negativeByPath.Remove(p);
		}
	}

	/// <summary>
	/// Gets the child-list cache for the given parent inode. null if not cached.
	/// Effective for cases like `ReadDir` / `FindFiles` that enumerate the same directory many times in a short span.
	/// </summary>
	public IReadOnlyList<Inode>? GetChildren(long parentId) {
		lock (this) {
			if (this.childrenByParent.TryGetValue(parentId, out var list)) { return list; }
			return null;
		}
	}

	/// <summary>
	/// Registers the child-list cache. At the same time, registers each child inode into <see cref="byId"/>.
	/// </summary>
	public void PutChildren(long parentId, List<Inode> children) {
		lock (this) {
			this.childrenByParent[parentId] = children;
			foreach (var child in children) {
				// A pinned entry (= a pending entry of metadata write-back) must stay the same instance as the one in the
				// ledger - that is the core invariant - so it is never overwritten with an instance that came from the database.
				if (this.pinned.Contains(child.Id)) { continue; }
				this.byId[child.Id] = child;
			}
			// The freshly re-read listing is authoritative, so this parent's negative markers can no longer be trusted
			// and are discarded (this prevents "a file another client created shows up in the listing yet stat says ENOENT").
			this.RemoveNegativesOf(parentId);
			this.EvictIfOverCapacity();
		}
	}

	/// <summary>
	/// Discards the child-list cache for the given parent inode.
	/// Call it after a child is added / removed / renamed.
	/// </summary>
	public void InvalidateChildren(long parentId) {
		lock (this) {
			this.childrenByParent.Remove(parentId);
			// The namespace moved under this parent = any memory of ENOENT below it is no longer trustworthy.
			// Local creates, renames and deletes as well as remote notifications (OnRemoteChange) all pass through here,
			// so invalidating the negative cache is concentrated in this one place.
			this.RemoveNegativesOf(parentId);
		}
	}

	/// <summary>Sweeps the negative markers of a given parent id. Call this under lock(this).</summary>
	private void RemoveNegativesOf(long parentId) {
		if (this.negativeByPath.Count == 0) { return; }
		var toRemove = new List<string>();
		foreach (var kv in this.negativeByPath) {
			if (kv.Value.ParentId == parentId) { toRemove.Add(kv.Key); }
		}
		foreach (var p in toRemove) {
			this.negativeByPath.Remove(p);
		}
	}

	/// <summary>Changes the cache limit (<c>cache_max_entries</c>) at run time (live reload). Shrinking evicts by LRU immediately.</summary>
	public void SetCapacity(int maxEntries) {
		lock (this) {
			this.cacheMaxEntries = maxEntries;
			this.EvictIfOverCapacity();
		}
	}

	/// <summary>Changes the negative cache TTL (<c>negative_cache_ttl_ms</c>) at run time (live reload). Zero or below disables it and clears everything.</summary>
	public void SetNegativeTtl(int ttlMs) {
		lock (this) {
			this.negativeTtlMs = ttlMs;
			if (ttlMs <= 0) { this.negativeByPath.Clear(); }
		}
	}

	/// <summary>True when the path is in the negative cache and still within its TTL. An expired entry is swept on the spot. Call this under lock(this).</summary>
	private bool IsNegativeFresh(string path) {
		if (this.negativeTtlMs <= 0) { return false; }
		if (!this.negativeByPath.TryGetValue(path, out var entry)) { return false; }
		if (entry.ExpiresAt < DateTime.UtcNow) {
			this.negativeByPath.Remove(path);
			return false;
		}
		return true;
	}

	/// <summary>
	/// Records an ENOENT. **Call it inside the same lock(this) region as the lookup's database SELECT** - a
	/// concurrent create's <see cref="InvalidateChildren"/> waits on that lock, so the race "an invalidation cuts
	/// in between the SELECT and the record, leaving a stale ENOENT" cannot happen structurally (that invariant
	/// is what makes the negative cache safe for a single client).
	/// </summary>
	private void RememberNegative(string path, long parentId) {
		if (this.negativeTtlMs <= 0) { return; }
		this.PruneNegativesIfFull();
		this.negativeByPath[path] = new NegativeEntry(parentId, DateTime.UtcNow.AddMilliseconds(this.negativeTtlMs));
	}

	/// <summary>
	/// Bounds the number of negative cache entries. The cap is <c>cache_max_entries</c> (or
	/// <see cref="NegativeFallbackLimit"/> when that is unlimited). Expired entries are swept first, and if it
	/// still overflows everything is thrown away (it is only a memory of ENOENT, so discarding it does not affect
	/// correctness). Call this under lock(this).
	/// </summary>
	private void PruneNegativesIfFull() {
		var limit = InodeCache.NegativeFallbackLimit;
		if (this.cacheMaxEntries > 0) { limit = this.cacheMaxEntries; }
		if (this.negativeByPath.Count < limit) { return; }
		var now = DateTime.UtcNow;
		var expired = new List<string>();
		foreach (var kv in this.negativeByPath) {
			if (kv.Value.ExpiresAt < now) { expired.Add(kv.Key); }
		}
		foreach (var p in expired) {
			this.negativeByPath.Remove(p);
		}
		if (this.negativeByPath.Count < limit) { return; }
		this.negativeByPath.Clear();
	}

	/// <summary>A snapshot of the cumulative statistics (for Layer 3 status). The caller computes the hit ratio as hits/(hits+misses).</summary>
	public InodeCacheStats Stats() {
		lock (this) {
			return new InodeCacheStats {
				Entries = this.byId.Count,
				PathEntries = this.byPath.Count,
				ChildrenLists = this.childrenByParent.Count,
				Capacity = this.cacheMaxEntries,
				Hits = this.hits,
				Misses = this.misses,
				Evictions = this.evictions,
				NegativeEntries = this.negativeByPath.Count,
				NegativeHits = this.negativeHits,
			};
		}
	}

	/// <summary>
	/// When <see cref="byId"/> exceeds the cap (<c>cache_max_entries</c>), entries are evicted in batches,
	/// oldest first by LRU (<see cref="Inode.CacheTime"/>). byId is authoritative: after the eviction,
	/// <see cref="byPath"/> and <see cref="childrenByParent"/> are swept of entries pointing at ids that are no
	/// longer in byId, which keeps them consistent (three dictionaries bounded indirectly by byId's single cap).
	/// The root is never evicted, because its <c>CacheTime = DateTime.MaxValue</c>.
	/// <c>cache_max_entries &lt;= 0</c> disables eviction (unlimited, as before).
	/// Always call it under <c>lock(this)</c> (the calling put / PutChildren already holds it).
	/// </summary>
	private void EvictIfOverCapacity() {
		if (this.cacheMaxEntries <= 0 || this.byId.Count <= this.cacheMaxEntries) {
			return;
		}

		// Only runs on overflow. It drops down to the low-water mark (about 7/8 of the cap) in one batch, which avoids sorting on every insert.
		var lowWater = this.cacheMaxEntries - (this.cacheMaxEntries / 8);
		if (lowWater < 1) {
			lowWater = 1;
		}
		// **Pinned entries (= pending entries of metadata write-back) cannot be dropped**, so when subtracting them
		// already satisfies the low-water mark, nothing is done. Without this, the moment pending entries exceed the
		// cap the code falls into "sort all of byId every time and throw away everything that is not pinned", and the
		// child-list cache is continuously wiped (measured: with 2000 pending entries childrenLists went to 0 and
		// mkdir became 28% slower).
		if (this.byId.Count - this.pinned.Count <= lowWater) {
			return;
		}
		var evictCount = this.byId.Count - lowWater;

		// Ascending CacheTime = oldest first. The root is MaxValue, so it lands at the end and is never selected.
		var entries = new List<KeyValuePair<long, Inode>>(this.byId);
		entries.Sort((a, b) => a.Value.CacheTime.CompareTo(b.Value.CacheTime));
		var removed = 0;
		for (int i = 0; i < entries.Count && removed < evictCount; ++i) {
			// A pending inode (pinned) has no row in the database, so dropping it turns into "created it, yet ENOENT".
			if (this.pinned.Contains(entries[i].Key)) { continue; }
			// The root lands at the end with CacheTime = MaxValue, but skipping pinned entries can run the loop all the
			// way to the end and drop it (it is the anchor of the path chain and must never be evicted).
			if (entries[i].Key == 0) { continue; }
			this.byId.Remove(entries[i].Key);
			++removed;
			++this.evictions;
		}

		// After the eviction, sweep byPath and childrenByParent of entries pointing at ids no longer in byId.
		var stalePaths = new List<string>();
		foreach (var kv in this.byPath) {
			if (!this.byId.ContainsKey(kv.Value)) {
				stalePaths.Add(kv.Key);
			}
		}
		foreach (var p in stalePaths) {
			this.byPath.Remove(p);
		}

		var staleParents = new List<long>();
		foreach (var kv in this.childrenByParent) {
			if (!this.byId.ContainsKey(kv.Key)) {
				staleParents.Add(kv.Key);
			}
		}
		foreach (var pid in staleParents) {
			this.childrenByParent.Remove(pid);
		}
	}

	/// <summary>
	/// Builds the full path of the given inode from root by following the `parent_id` chain in the cache.
	/// If any link on the chain is absent from <see cref="byId"/>, the path cannot be resolved, so it returns false.
	/// The separator is `/`. root (id=0) is `"/"`.
	///
	/// <para>
	/// Purpose: when a write notification from another client arrives via <see cref="NotifyChannel"/>, it is used to resolve
	/// the path passed to the OS (e.g. Dokan's Explorer notification). IDs arrive but paths do not, so a notification can only
	/// be propagated for inodes this client has touched before (= OK if it is in byId).
	/// </para>
	/// </summary>
	public bool TryGetPath(long id, out string path) {
		lock (this) {
			if (id == 0) {
				path = "/";
				return true;
			}
			var parts = new List<string>();
			var cur = id;
			// Cap at 1024 to avoid cycles or excessive depth (practical path depth is a few dozen)
			for (int hop = 0; hop < 1024; ++hop) {
				if (!this.byId.TryGetValue(cur, out var inode)) {
					path = "";
					return false;
				}
				if (inode.Id == 0) {
					break;
				}
				parts.Add(inode.Name);
				cur = inode.ParentId;
				if (cur == 0) {
					break;
				}
			}
			parts.Reverse();
			path = "/" + string.Join("/", parts);
			return true;
		}
	}

	public Inode? Get(long id) {
		lock (this) {
			var inode = this.get(id, null);
			if (inode == null) {
				return null;
			}
			return inode.UpdateCacheTime();
		}
	}
	public Inode? Get(string path) {
		lock (this) {
			var inode = this.get(null, path);
			if (inode == null) {
				return null;
			}
			return inode.UpdateCacheTime();
		}
	}
	public Inode? Get(long id, string path) {
		lock (this) {
			var inode = this.get(id, path);
			if (inode == null) {
				return null;
			}
			return inode.UpdateCacheTime();
		}
	}
	private Inode? get(long? id, string? path) {
		if (id is not long targetId) {
			if (path == null) {
				return null;
			}

			var foundId = this.byPath.GetValueOrDefault(path, -1);
			if (foundId != -1) {
				return this.byId[foundId];
			}

			return null;
		}

		var inode = this.byId[targetId];
		if (inode == null) {
			return null;
		}

		if (path == null) {
			return inode;
		}

		ref var storedId = ref this.byPath.GetValueRefOrAddDefault(path, out var exists);
		// **Both branches were `storedId = targetId`**, so only the warning is conditional and the assignment is
		// unconditional (the conventions forbid `else`; the behaviour is exactly the same).
		if (exists && storedId != targetId) {
			Logger.Warning($"Path mapping conflict: {path} points to {storedId}, but requested {targetId}. Updating cache.");
		}
		storedId = targetId;

		return inode;
	}

	public Inode? Load(long id) {
		lock (this) {
			var inode = this.load(id, null);
			if (inode == null) {
				return null;
			}
			return inode.UpdateCacheTime();
		}
	}
	public Inode? Load(string path) {
		lock (this) {
			var inode = this.load(null, path);
			if (inode == null) {
				return null;
			}
			return inode.UpdateCacheTime();
		}
	}
	public Inode? Load(long id, string path) {
		lock (this) {
			var inode = this.load(id, path);
			if (inode == null) {
				return null;
			}
			return inode.UpdateCacheTime();
		}
	}
	private Inode? load(long? id, string? path) {
		var inode = this.get(id, path);
		if (inode != null) {
			++this.hits;
			return inode;
		}
		if (id == null) {
			if (path == null) {
				return null; // Neither an id nor a path = this is not a real lookup, so it is not counted in the statistics.
			}
			if (this.IsNegativeFresh(path)) {
				++this.negativeHits;
				return null;
			}
			++this.misses;
			// The API layer always deals in `/`-separated paths (Mount receives `/` from FUSE, and Assign has already
			// converted `\` to `/` in NormalizePath).
			// `PathParser.FromPath(path)` alone would adopt `Path.DirectorySeparatorChar = '\'` on Windows and treat
			// `/Images/Camera` as a single element instead of splitting it.
			var parser = PathParser.FromPath(path, "/");
			(inode, _, var done) = this.load(parser, parser.Count - 1);
			if (!done) {
				return null;
			}
			return inode;
		}
		++this.misses;
		inode = Pg.Query<Inode>(this.connectionString, this.selectInodeByIdQuery, new { id }).SingleOrDefault();
		// Even when it is not in the database, the ledger has it if it is pending (not yet INSERTed by metadata write-back).
		inode ??= this.PendingById?.Invoke(id.Value);
		if (inode == null) {
			return null;
		}
		return this.put(inode, path);
	}
	private (Inode inode, int index, bool done) load(PathParser parser, int lastIndex) {
		if (lastIndex <= 0) {
			return (this.GetRoot(), 0, true);
		}

		// Walk from the leaf up to the root directory, looking for where the cache has an entry
		Inode? inode = null;
		int index = lastIndex;
		while (index > 0) {
			--index;
			inode = this.Get(parser.PathList[index]);
			if (inode != null) {
				break;
			}
		}

		// if none, start from the root directory
		if (inode == null) {
			inode = this.GetRoot();
			index = 0;
		}

		// walk down in order from the directory that was found
		while (index < lastIndex) {
			var parentInode = inode;
			var parentIndex = index;

			++index;

			var parentId = parentInode.id;
			var path = parser.PathList[index];
			var name = parser.NameList[index];
			inode = Pg.Query<Inode>(this.connectionString, this.selectInodeByNameQuery, new { parent_id = parentId, name = name }).SingleOrDefault();
			// Even when it is not in the database, the ledger has it if it is pending (not yet INSERTed by metadata write-back).
			// Without going through here, the moment byId loses it to the LRU or an invalidate it becomes "created it, yet ENOENT".
			inode ??= this.PendingByName?.Invoke(parentId, name);
			if (inode == null) {
				this.RememberNegative(path, parentId);
				return (parentInode, parentIndex, false);
			}

			inode = this.Put(inode, path);
		}

		return (inode, index, true);
	}


	private const string inodeSelectColumns = @"
		    inode.id,
		    inode.parent_id,
		    inode.name,
		    inode.uname,
		    inode.gname,
		    inode.st_mode,
		    inode.st_nlink,
		    inode.st_size,
		    inode.st_mtime,
		    inode.st_ctime,
		    inode.link_target,
		    inode.is_junction,
		    inode.data_id,
		    inode.xattr_names,
		    inode.xattr_values,
		    inode.created_at,
		    inode.created_by,
		    inode.updated_at,
		    inode.updated_by";

	private string selectInodeByIdQuery => $@"
		select {InodeCache.inodeSelectColumns}
		from
			{this.QualifiedInodeTable} AS inode
		where
			inode.id = @id
	";

	private string selectInodeByNameQuery => $@"
		select {InodeCache.inodeSelectColumns}
		from
			 {this.QualifiedInodeTable} AS inode
		where
			inode.parent_id = @parent_id
			and inode.name = @name
	";
}

/// <summary>The result of <see cref="InodeCache.Stats"/> (a statistics snapshot of the metadata cache).</summary>
public sealed record InodeCacheStats
{
	/// <summary>The number of <c>byId</c> entries (the authority for the cap).</summary>
	public required int Entries { get; init; }
	/// <summary>The number of <c>byPath</c> entries.</summary>
	public required int PathEntries { get; init; }
	/// <summary>The number of child lists in <c>childrenByParent</c>.</summary>
	public required int ChildrenLists { get; init; }
	/// <summary>The cap (<c>cache_max_entries</c>). Zero or below means unlimited.</summary>
	public required int Capacity { get; init; }
	public required long Hits { get; init; }
	public required long Misses { get; init; }
	/// <summary>The cumulative number of inodes evicted for exceeding the cap.</summary>
	public required long Evictions { get; init; }
	/// <summary>The current number of negative (ENOENT) markers.</summary>
	public required int NegativeEntries { get; init; }
	/// <summary>The cumulative number of lookups the negative cache saved a database round trip for.</summary>
	public required long NegativeHits { get; init; }
}
