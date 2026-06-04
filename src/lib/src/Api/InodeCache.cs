namespace Pgfs.Lib.Api;

using Collections;
using Logging;
using Models;
using Utility;
using Pgfs.Lib.Config;

public class InodeCache
{
	private readonly string connectionString;
	private readonly string tableNamePrefix;
	private readonly string schemaName;
	private readonly RichDictionary<long, Inode> byId;
	private readonly RichDictionary<string, long> byPath;
	private readonly RichDictionary<long, List<Inode>> childrenByParent;
	private readonly Inode root;

	/// <summary>The schema-qualified inode table name (e.g. <c>"pgfs"."pgfs_inode"</c>).</summary>
	private string QualifiedInodeTable
		=> $"{Pg.QuoteIdentifier(this.schemaName)}.{Pg.QuoteIdentifier(this.tableNamePrefix + "inode")}";

	public InodeCache(RootConfig config) {
		this.connectionString = config.Database.Connection.ConnectionString;
		this.tableNamePrefix = config.Database.GetPrefix();
		this.schemaName = config.Database.SchemaName;
		var cacheMaxEntries = config.Mount.CacheMaxEntries;
		this.byId = new(cacheMaxEntries);
		this.byPath = new(cacheMaxEntries);
		this.childrenByParent = new(cacheMaxEntries);

		var root = this.Load(0);
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
					st_mtime = DateTime.Now,
					st_ctime = DateTime.Now,
					link_target = null,
					is_junction = false,
					data_id = null,
					xattr_names = System.Array.Empty<string>(),
					xattr_values = System.Array.Empty<byte[]>(),
					created_at = DateTime.Now,
					created_by = "system",
					updated_at = DateTime.Now,
					updated_by = "system",
				},
				"/"
			);
		} else {
			// The paths Api/InodeCache handles are always normalized to `/`-separated (Mount gets `/` from FUSE,
			// Assign converts `\` → `/` in NormalizePath). Register root in byPath under `/` too.
			this.byPath["/"] = root.Id;
		}
		root.CacheTime = DateTime.MaxValue;
		this.root = root;
	}


	public Inode GetRoot() {
		return this.root;
	}


	public Inode Put(Inode inode, string path) {
		lock (this) {
			return this.put(inode, path).UpdateCacheTime();
		}
	}
	private Inode put(Inode inode, string? path) {
		var id = inode.Id;
		this.byId[id] = inode;
		if (path != null) {
			this.byPath[path] = id;
		}
		return inode;
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
	/// Removes all cache entries rooted at the given path.
	/// For cases where descendants are also affected, such as a directory rename.
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
		}
	}

	/// <summary>
	/// Gets the child-list cache for the given parent inode. null if not cached.
	/// Effective for cases like `ReadDir` / `FindFiles` that enumerate the same directory many times in a short span.
	/// </summary>
	public IReadOnlyList<Inode>? GetChildren(long parentId) {
		lock (this) {
			return this.childrenByParent.TryGetValue(parentId, out var list) ? list : null;
		}
	}

	/// <summary>
	/// Registers the child-list cache. At the same time, registers each child inode into <see cref="byId"/>.
	/// </summary>
	public void PutChildren(long parentId, List<Inode> children) {
		lock (this) {
			this.childrenByParent[parentId] = children;
			foreach (var child in children) {
				this.byId[child.Id] = child;
			}
		}
	}

	/// <summary>
	/// Discards the child-list cache for the given parent inode.
	/// Call it after a child is added / removed / renamed.
	/// </summary>
	public void InvalidateChildren(long parentId) {
		lock (this) {
			this.childrenByParent.Remove(parentId);
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
		if (!exists) {
			storedId = targetId;
		} else if (storedId != targetId) {
			Logger.Warning($"Path mapping conflict: {path} points to {storedId}, but requested {targetId}. Updating cache.");
			storedId = targetId;
		}

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
		if (inode == null) {
			if (id == null) {
				if (path == null) {
					return null;
				}
				// The API layer always handles `/`-separated paths (Mount gets `/` from FUSE,
				// Assign converts `\` → `/` in NormalizePath).
				// With just `PathParser.FromPath(path)`, on Windows `Path.DirectorySeparatorChar = '\'`
				// would be used, so `/Images/Camera` would not be split and would be treated as a single element.
				var parser = PathParser.FromPath(path, "/");
				(inode, _, var done) = this.load(parser, parser.Count - 1);
				if (!done) {
					return null;
				}
				return inode;
			}
			inode = Pg.Query<Inode>(this.connectionString, this.selectInodeByIdQuery, new { id }).SingleOrDefault();
			if (inode == null) {
				return null;
			}
			return this.put(inode, path);
		}
		return inode;
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
			if (inode == null) {
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

	private string selectInodesByNameQuery => $@"
		select {InodeCache.inodeSelectColumns}
		from
			{this.QualifiedInodeTable} AS inode
		where
			inode.parent_id = ANY(@parent_id_list)
			and inode.name like @name
			and inode.id <> 0
	";

	private const string inodeInsertColumns = @"
			parent_id,
			name,
			uname,
			gname,
			st_mode,
			created_by,
			updated_by";

	private string insertInodeQuery => $@"
		insert into {this.QualifiedInodeTable}
		({inodeInsertColumns}
		) values (
			@parent_id,
			@name,
			@uname,
			@gname,
			@st_mode,
			@uname,
			@uname
		)
		returning {inodeSelectColumns}
	";
}
