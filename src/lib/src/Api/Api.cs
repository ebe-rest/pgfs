namespace Pgfs.Lib.Api;

using Dapper;
using Logging;
using Models;
using Utility;
using Pgfs.Lib.Config;
using System.Text.Json;

/// <summary>
/// The API that exposes PGFS filesystem operations.
///
/// A shared layer used from both Mount (Linux/macOS, Tmds.Fuse) and Assign (Windows, DokanNet).
/// OS-specific concepts (uid/gid resolution, xattr details, symlinks, etc.) are intentionally left to the
/// caller; here it handles only DB read/write and inode cache management.
/// </summary>
public class Api : System.IDisposable
{
	private readonly RootConfig config;
	private readonly string connectionString;
	private readonly string tableNamePrefix;
	private readonly string schemaName;
	private readonly InodeCache inodeCache;
	private readonly NotifyChannel? notifyChannel;

	/// <summary>Whether the audit log (audit.enabled) is on. The condition under which each mutating method fires its hook after success.</summary>
	private readonly bool auditEnabled;
	/// <summary>The pgfs process's host name to put in an audit row's caller_host (resolved once at startup). null when auditing is disabled.</summary>
	private readonly string? auditHost;
	/// <summary>Caches the already-CREATEd monthly partition keys ("yyyy_MM") within the process, so the DDL is issued only on the first time each month.</summary>
	private readonly System.Collections.Generic.HashSet<string> ensuredAuditPartitions = new();
	private readonly object auditPartitionLock = new();

	public Api(RootConfig config) {
		this.config = config;
		this.connectionString = config.Database.Connection.ConnectionString;
		this.tableNamePrefix = config.Database.GetPrefix();
		this.schemaName = config.Database.SchemaName;
		this.inodeCache = new InodeCache(config);
		// Audit log (docs/audit-log.md). caller_host is a process constant representing "which machine performed the operation",
		// so resolve it only once at startup. When disabled, do not touch it.
		this.auditEnabled = config.Audit.Enabled;
		if (this.auditEnabled) {
			this.auditHost = System.Net.Dns.GetHostName();
		}
		// Retry transient failures at connection-open time (PG restart / network blip) via the Pg.OpenConnection family.
		// Settings come from database.retry_*. Applied globally once.
		Retry.Configure(
			config.Database.RetryMaxAttempts,
			config.Database.RetryInitialDelayMs,
			config.Database.RetryMaxDelayMs
		);
		// The DB load of mount.fallback_uname / fallback_gname has already been done by [ConfigLoader] via ConfigStore,
		// so do nothing here.

		// Cross-client change notification (only when database.notify_enabled is true).
		// LISTEN stays open on a dedicated connection and is closed via Dispose at process exit.
		if (config.Database.NotifyEnabled) {
			this.notifyChannel = new NotifyChannel(this.connectionString, this.schemaName, this.tableNamePrefix);
			this.notifyChannel.Received += this.OnRemoteChange;
			this.notifyChannel.Start();
		}
	}

	public RootConfig Config => this.config;
	public InodeCache InodeCache => this.inodeCache;

	/// <summary>
	/// The OS bridge called when a change notification from another client is received. On Assign it is used to call
	/// <c>DokanInstance.NotifyUpdate</c> etc. to ask Explorer to repaint.
	/// Mount (Linux) leaves it null because the Tmds.Fuse high-level API has no counterpart (the kernel attr cache is
	/// disabled via <c>attr_timeout=0</c>, so just invalidating InodeCache makes stat/ls return the latest).
	/// </summary>
	public System.Action<RemoteChangeInfo>? OsBridge { get; set; }

	public void Dispose() {
		this.notifyChannel?.Dispose();
	}

	// ------------------------------------------------------------------
	// Cross-client change notification (LISTEN/NOTIFY)
	// ------------------------------------------------------------------

	/// <summary>
	/// Applies the received <see cref="NotifyMessage"/> to the local <see cref="InodeCache"/>, and if <see cref="OsBridge"/>
	/// is registered, passes it a path-resolved <see cref="RemoteChangeInfo"/>.
	/// Self-messages are pre-filtered by <see cref="NotifyChannel"/>, so they never arrive here.
	/// </summary>
	private void OnRemoteChange(NotifyMessage msg) {
		// Step 1: resolve paths before invalidation (once deleted, byId can no longer be looked up)
		var resolvedPaths = new System.Collections.Generic.Dictionary<long, string>();
		foreach (var id in msg.InodeIds) {
			if (this.inodeCache.TryGetPath(id, out var path)) {
				resolvedPaths[id] = path;
			}
		}
		var resolvedParents = new System.Collections.Generic.Dictionary<long, string>();
		foreach (var pid in msg.ParentIds) {
			if (this.inodeCache.TryGetPath(pid, out var path)) {
				resolvedParents[pid] = path;
			}
		}

		// Step 2: invalidate the local InodeCache
		foreach (var id in msg.InodeIds) {
			this.inodeCache.Invalidate(id, path: null);
		}
		foreach (var pid in msg.ParentIds) {
			this.inodeCache.InvalidateChildren(pid);
		}
		foreach (var prefix in msg.PathPrefixes) {
			this.inodeCache.InvalidatePrefix(prefix);
		}

		// Step 3: notify the OS bridge (Assign's DokanInstance.NotifyUpdate etc.)
		var bridge = this.OsBridge;
		if (bridge == null) {
			return;
		}
		try {
			bridge(new RemoteChangeInfo {
				InodeIds = msg.InodeIds,
				ParentIds = msg.ParentIds,
				ResolvedPaths = resolvedPaths,
				ResolvedParentPaths = resolvedParents,
			});
		} catch (System.Exception ex) {
			Logger.Warning("exception in Api.OsBridge: ", ex.Message);
		}
	}

	/// <summary>
	/// Notifies other clients of changes this client made. no-op if <see cref="NotifyChannel"/> is disabled
	/// (database.notify_enabled=false). Exceptions are swallowed internally (the policy is not to fail the write itself
	/// just because notification failed).
	/// </summary>
	private void Notify(
		System.Collections.Generic.IEnumerable<long>? inodeIds = null,
		System.Collections.Generic.IEnumerable<long>? parentIds = null,
		System.Collections.Generic.IEnumerable<string>? pathPrefixes = null
	) {
		var ch = this.notifyChannel;
		if (ch == null) {
			return;
		}
		var msg = new NotifyMessage();
		if (inodeIds != null) {
			msg.InodeIds.AddRange(inodeIds);
		}
		if (parentIds != null) {
			msg.ParentIds.AddRange(parentIds);
		}
		if (pathPrefixes != null) {
			msg.PathPrefixes.AddRange(pathPrefixes);
		}
		// nothing worth sending if all are empty
		if (msg.InodeIds.Count == 0 && msg.ParentIds.Count == 0 && msg.PathPrefixes.Count == 0) {
			return;
		}
		ch.Publish(msg);
	}

	private string QualifiedTable(string baseName)
		=> $"{Pg.QuoteIdentifier(this.schemaName)}.{Pg.QuoteIdentifier(this.tableNamePrefix + baseName)}";

	// ------------------------------------------------------------------
	// cross-client mutual exclusion (pgfs_lock + SELECT FOR UPDATE)
	// ------------------------------------------------------------------
	//
	// Serializes the race where multiple clients (mount.pgfs / pgfs.assign sharing the same DB) rewrite the same
	// inode / data at the same time. It takes a row lock on pgfs_lock(target_id BIGINT PK), released automatically
	// at tx end (COMMIT/ROLLBACK).
	//
	// The design is described in [docs/support_for_citus.md](../../../../docs/support_for_citus.md):
	//   - data lock: target_id = data_id (a positive integer)
	//   - inode lock: target_id = -inode_id (a negative integer, namespace-separated)
	//
	// On Citus, target_id is distributed → SELECT FOR UPDATE completes on a worker (no coordinator centralization).
	// When taking multiple locks, deadlock is avoided by **fixing target_id to ascending order**.

	/// <summary>
	/// Acquires row locks for the given set of target_ids. Released automatically at tx end.
	/// To avoid order-induced deadlock, the input is deduplicated + sorted ascending, then acquired one at a time.
	/// Each SQL has a single target_id in its WHERE = it completes on a single shard even on Citus.
	/// </summary>
	private void LockTargets(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, params long[] targetIds) {
		if (targetIds.Length == 0) {
			return;
		}
		// fixed ascending order (with dedup) to avoid deadlock
		var sorted = targetIds.Distinct().OrderBy(x => x).ToArray();
		var lockTable = this.QualifiedTable("lock");
		foreach (var tid in sorted) {
			// Secure the row (no-op if it already exists) → take the row lock.
			// The WHERE of SELECT FOR UPDATE is the single distribution-key column target_id, so it completes on a single shard even on Citus.
			conn.Execute(
				$"INSERT INTO {lockTable}(target_id) VALUES (@id) ON CONFLICT DO NOTHING",
				new { id = tid }, tx
			);
			conn.Execute(
				$"SELECT 1 FROM {lockTable} WHERE target_id = @id FOR UPDATE",
				new { id = tid }, tx
			);
			if (Logger.IsTraceEnabled) { Logger.Trace("LOCK pgfs_lock target_id:", tid); }
		}
	}

	/// <summary>Locks the data_id row (for WriteData / TruncateData / ReleaseData).</summary>
	private void LockData(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId) {
		this.LockTargets(conn, tx, dataId);
	}

	/// <summary>Locks the inode_id row (for UpdateMode/Owner/Size/Timestamps).</summary>
	private void LockInode(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long inodeId) {
		this.LockTargets(conn, tx, -inodeId);
	}

	/// <summary>Locks multiple inodes in ascending target_id order in one go (for Rename / DeleteInode / CreateHardLink).</summary>
	private void LockInodes(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, params long[] inodeIds) {
		// Flip the sign into target space, then LockTargets sorts ascending + acquires
		var targets = new long[inodeIds.Length];
		for (int i = 0; i < inodeIds.Length; i++) {
			targets[i] = -inodeIds[i];
		}
		this.LockTargets(conn, tx, targets);
	}

	// ------------------------------------------------------------------
	// Read operations
	// ------------------------------------------------------------------

	public Inode GetRoot() => this.inodeCache.GetRoot();

	public Inode? GetByPath(string path) => this.inodeCache.Load(path);
	public Inode? GetById(long id) => this.inodeCache.Load(id);

	/// <summary>
	/// Enumerates the child inodes directly under the given directory.
	/// If they are cached in `InodeCache`'s childrenByParent, returns those; otherwise fetches from the DB and stores
	/// them in the cache. At the time a child is created / deleted / renamed, the Api side calls
	/// `InodeCache.InvalidateChildren(parentId)` to invalidate it.
	/// </summary>
	public IEnumerable<Inode> ListChildren(long parentId) {
		var cached = this.inodeCache.GetChildren(parentId);
		if (cached != null) {
			return cached;
		}
		var sql = $@"
			SELECT id, parent_id, name, uname, gname, st_mode, st_nlink, st_size,
			       st_mtime, st_ctime, link_target, is_junction, data_id, xattr_names, xattr_values,
			       created_at, created_by, updated_at, updated_by
			FROM {this.QualifiedTable("inode")}
			WHERE parent_id = @parent_id AND id <> 0
			ORDER BY name
		";
		var list = Pg.Query<Inode>(this.connectionString, sql, new { parent_id = parentId }).ToList();
		this.inodeCache.PutChildren(parentId, list);
		return list;
	}

	// ------------------------------------------------------------------
	// Creation
	// ------------------------------------------------------------------

	/// <summary>
	/// Creates a new directory inode under the parent directory.
	/// Returns null if the parent does not exist, or if an entry with the same name already exists.
	/// </summary>
	public Inode? CreateDirectory(long parentId, string name, string uname, string gname, int mode) {
		// Cross-platform: st_mode gets the directory bit (040000).
		var modeWithDir = (mode & 0xFFF) | Mode.S_IFDIR;
		return this.InsertInode(parentId, name, uname, gname, modeWithDir, dataId: null, linkTarget: null);
	}

	/// <summary>
	/// Creates a new regular-file inode under the parent directory (an empty file, no data).
	/// </summary>
	public Inode? CreateFile(long parentId, string name, string uname, string gname, int mode) {
		// Cross-platform: st_mode gets the regular-file bit (100000).
		var modeWithReg = (mode & 0xFFF) | Mode.S_IFREG;
		return this.InsertInode(parentId, name, uname, gname, modeWithReg, dataId: null, linkTarget: null);
	}

	private Inode? InsertInode(long parentId, string name, string uname, string gname, int stMode, long? dataId, string? linkTarget) {
		var sql = $@"
			INSERT INTO {this.QualifiedTable("inode")} (
				parent_id, name, uname, gname, st_mode, data_id, link_target,
				created_by, updated_by
			) VALUES (
				@parent_id, @name, @uname, @gname, @st_mode, @data_id, @link_target,
				@uname, @uname
			)
			ON CONFLICT (parent_id, name) DO NOTHING
			RETURNING id, parent_id, name, uname, gname, st_mode, st_nlink, st_size,
			          st_mtime, st_ctime, link_target, is_junction, data_id, xattr_names, xattr_values,
			          created_at, created_by, updated_at, updated_by
		";
		try {
			// To keep the audit log in the same tx as the operation, make the INSERT an explicit tx (the ON CONFLICT
			// itself is a single statement, but we want to commit the audit row atomically). On conflict, inserted == null and we rollback.
			Inode? inserted;
			using (var conn = this.NewConnection())
			using (var tx = conn.BeginTransaction()) {
				inserted = conn.QueryFirstOrDefault<Inode>(sql, new {
					parent_id = parentId,
					name,
					uname,
					gname,
					st_mode = stMode,
					data_id = dataId,
					link_target = linkTarget,
				}, tx);
				if (inserted == null) {
					tx.Rollback();
					return null;
				}
				this.WriteAudit(conn, tx, AuditOp.Create, inserted.Id, parentId, name, new {
					mode = Convert.ToString(stMode, 8),
					kind = AuditKind(stMode),
					uname,
					gname,
				});
				tx.Commit();
			}
			// The caller knows the full path for path-based cache updates, so here we cache only by id
			this.inodeCache.Put(inserted, path: null!);
			// Discard the parent directory's child-list cache (re-fetched next time by ListChildren)
			this.inodeCache.InvalidateChildren(parentId);
			// Notify other clients: the parent's child list changed
			this.Notify(parentIds: [parentId]);
			return inserted;
		} catch (Exception ex) {
			Logger.Error("CreateInode failed: ", ex);
			return null;
		}
	}

	// ------------------------------------------------------------------
	// Deletion
	// ------------------------------------------------------------------

	/// <summary>
	/// Physically deletes the inode. For a directory, the caller must ensure it is empty.
	/// If it was the last reference to the data body, the data row and its chunks are freed too.
	/// If other references remain via hardlinks, decrements st_nlink on the remaining inodes.
	/// </summary>
	public bool DeleteInode(Inode inode) {
		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();

		// Serialize concurrent changes to the parent children list and the body itself. Fixed ascending target_id order avoids deadlock.
		this.LockInodes(conn, tx, inode.Id, inode.ParentId);

		var rows = conn.Execute(
			$"DELETE FROM {this.QualifiedTable("inode")} WHERE id = @id AND id <> 0",
			new { id = inode.Id },
			tx
		);
		if (Logger.IsTraceEnabled) { Logger.Trace("DELETE inode id:", inode.Id, " rows:", rows); }

		// The ID list of sibling inodes remaining via hardlinks (taken inside the tx for cache invalidation)
		List<long> siblingIds = new();
		if (rows > 0 && inode.DataId is long dataId) {
			siblingIds = conn.Query<long>(
				$"SELECT id FROM {this.QualifiedTable("inode")} WHERE data_id = @data_id",
				new { data_id = dataId },
				tx
			).ToList();
			var refCount = siblingIds.Count;
			if (Logger.IsTraceEnabled) { Logger.Trace("SELECT COUNT inode WHERE data_id:", dataId, " = ", refCount); }
			if (refCount == 0) {
				// last reference: free the chunks and the data body row too
				DropAllChunks(conn, tx, dataId);
				DropDataRow(conn, tx, dataId);
			} else {
				// hardlinks remain: align the remaining inodes' st_nlink to refCount
				var updated = conn.Execute(
					$@"UPDATE {this.QualifiedTable("inode")}
					   SET st_nlink = @cnt, st_ctime = current_timestamp, updated_at = current_timestamp
					   WHERE data_id = @data_id",
					new { cnt = refCount, data_id = dataId },
					tx
				);
				if (Logger.IsTraceEnabled) { Logger.Trace("UPDATE inode SET st_nlink:", refCount, " WHERE data_id:", dataId, " rows:", updated); }
			}
		}

		if (rows > 0) {
			this.WriteAudit(conn, tx, AuditOp.Delete, inode.Id, inode.ParentId, inode.Name, null);
		}

		tx.Commit();
		if (rows > 0) {
			this.inodeCache.Invalidate(inode.Id, path: null);
			// The hardlink siblings' nlink was also updated, so drop them from the cache and let the next GetAttr re-fetch
			// from the DB (without this, b's stat would stay at the old nlink=2 even after rm a).
			foreach (var siblingId in siblingIds) {
				this.inodeCache.Invalidate(siblingId, path: null);
			}
			// Discard the parent directory's child-list cache (re-fetched next time by ListChildren)
			this.inodeCache.InvalidateChildren(inode.ParentId);
			// Notify other clients: the deleted target + siblings + parent
			var notifyIds = new List<long> { inode.Id };
			notifyIds.AddRange(siblingIds);
			this.Notify(inodeIds: notifyIds, parentIds: [inode.ParentId]);
		}
		return rows > 0;
	}

	/// <summary>
	/// Checks whether the directory is empty. false if not empty or it does not exist.
	/// </summary>
	public bool IsDirectoryEmpty(long inodeId) {
		var sql = $"SELECT 1 FROM {this.QualifiedTable("inode")} WHERE parent_id = @parent_id AND id <> 0 LIMIT 1";
		return !Pg.Query<int>(this.connectionString, sql, new { parent_id = inodeId }).Any();
	}

	// ------------------------------------------------------------------
	// Attribute changes
	// ------------------------------------------------------------------

	/// <summary>
	/// Updates st_mode (file-type bits + permission).
	/// The caller must pass it preserving the file-type bits (S_IFDIR, etc.).
	/// </summary>
	public bool UpdateMode(long id, int mode) {
		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();
		this.LockInode(conn, tx, id);
		var sql = $@"
			UPDATE {this.QualifiedTable("inode")}
			SET st_mode = @mode, st_ctime = current_timestamp, updated_at = current_timestamp
			WHERE id = @id
		";
		var rows = conn.Execute(sql, new { id, mode }, tx);
		if (rows > 0) {
			this.WriteAudit(conn, tx, AuditOp.Chmod, id, null, null, new { mode = Convert.ToString(mode, 8) });
		}
		tx.Commit();
		if (rows > 0) {
			// Also sync the in-memory cached inode (avoid Invalidate, which would break consistency between childrenByParent and byId)
			var cached = this.inodeCache.Get(id);
			if (cached != null) {
				cached.Mode = mode;
				cached.Ctime = DateTime.UtcNow;
			}
			this.Notify(inodeIds: [id]);
		}
		return rows > 0;
	}

	/// <summary>
	/// Updates the owner user name / group name.
	/// Cross-platform: actual uid/gid resolution is done by the caller (Linux: getpwnam / Windows: WindowsIdentity).
	/// </summary>
	public bool UpdateOwner(long id, string uname, string gname) {
		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();
		this.LockInode(conn, tx, id);
		var sql = $@"
			UPDATE {this.QualifiedTable("inode")}
			SET uname = @uname, gname = @gname,
			    st_ctime = current_timestamp, updated_at = current_timestamp
			WHERE id = @id
		";
		var rows = conn.Execute(sql, new { id, uname, gname }, tx);
		if (rows > 0) {
			this.WriteAudit(conn, tx, AuditOp.Chown, id, null, null, new { uname, gname });
		}
		tx.Commit();
		if (rows > 0) {
			var cached = this.inodeCache.Get(id);
			if (cached != null) {
				cached.UserName = uname;
				cached.GroupName = gname;
				cached.Ctime = DateTime.UtcNow;
			}
			this.Notify(inodeIds: [id]);
		}
		return rows > 0;
	}

	/// <summary>
	/// Updates st_size. Syncing with the data body is not yet implemented (metadata only).
	/// </summary>
	public bool UpdateSize(long id, long length) {
		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();
		this.LockInode(conn, tx, id);
		var sql = $@"
			UPDATE {this.QualifiedTable("inode")}
			SET st_size = @length,
			    st_mtime = current_timestamp, st_ctime = current_timestamp,
			    updated_at = current_timestamp
			WHERE id = @id
		";
		var rows = conn.Execute(sql, new { id, length }, tx);
		tx.Commit();
		if (rows > 0) {
			var cached = this.inodeCache.Get(id);
			if (cached != null) {
				var now = DateTime.UtcNow;
				cached.Size = length;
				cached.Mtime = now;
				cached.Ctime = now;
			}
			this.Notify(inodeIds: [id]);
		}
		return rows > 0;
	}

	/// <summary>
	/// Updates st_mtime. There is no st_atime, so it is ignored (see the requirement in docs/database.md).
	/// </summary>
	public bool UpdateTimestamps(long id, DateTime mtime) {
		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();
		this.LockInode(conn, tx, id);
		var sql = $@"
			UPDATE {this.QualifiedTable("inode")}
			SET st_mtime = @mtime, updated_at = current_timestamp
			WHERE id = @id
		";
		var rows = conn.Execute(sql, new { id, mtime }, tx);
		tx.Commit();
		if (rows > 0) {
			var cached = this.inodeCache.Get(id);
			if (cached != null) {
				cached.Mtime = mtime;
			}
			this.Notify(inodeIds: [id]);
		}
		return rows > 0;
	}

	/// <summary>
	/// Moves the inode to a different parent / name (rename).
	///
	/// <para>
	/// In a Citus distributed setup (pgfs_inode distributed by parent_id), an "UPDATE that changes parent_id" rewrites the
	/// distribution key and crosses shards, so it is rejected. When the parent changes, it switches to a "DELETE + INSERT
	/// (OVERRIDING SYSTEM VALUE to preserve id) within the same tx" path; when the parent is unchanged it uses the conventional
	/// UPDATE as-is (light, since it stays within the same shard).
	/// On a single PG, DELETE+INSERT is semantically identical, so the branch is unrelated to Citus.
	/// </para>
	/// </summary>
	public bool Rename(long id, long newParentId, string newName) {
		// Capture the old parent ID first (for child-list invalidation and the in-place update)
		var cached = this.inodeCache.Get(id);
		// Capture the rename source path for the notify payload (descendants also need to be dropped from byPath)
		string? oldPath = null;
		if (cached != null && this.inodeCache.TryGetPath(id, out var p)) {
			oldPath = p;
		}

		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();

		// Citus note: FOR UPDATE must include the distribution key parent_id in its WHERE
		// (otherwise it dies with "could not run distributed query with FOR UPDATE/SHARE commands").
		// If it cannot be taken from cached, resolve parent_id first via a broadcast SELECT, then lock.
		var inodeTable = this.QualifiedTable("inode");
		long? hintParentId = cached?.ParentId;
		if (hintParentId == null) {
			hintParentId = conn.QueryFirstOrDefault<long?>(
				$"SELECT parent_id FROM {inodeTable} WHERE id = @id",
				new { id }, tx
			);
			if (hintParentId == null) {
				tx.Rollback();
				return false;
			}
		}

		// Take the 3 locks for old parent / new parent / target inode in fixed ascending target_id order.
		// If hintParentId is stale (another client already renamed), the SELECT FOR UPDATE below returns empty and we exit false
		// (a wrong old-parent lock is released at tx end).
		this.LockInodes(conn, tx, id, hintParentId.Value, newParentId);

		// Fetch all columns while taking a row lock on the old row. If it does not exist, exit false.
		// On the DELETE+INSERT path, this value is fed straight into the new row.
		var old = conn.QueryFirstOrDefault<Pgfs.Lib.Models.Inode>(
			$@"SELECT id, parent_id, name, uname, gname, st_mode, st_nlink, st_size,
			          st_mtime, st_ctime, link_target, is_junction, data_id, xattr_names, xattr_values,
			          created_at, created_by, updated_at, updated_by
			   FROM {inodeTable} WHERE parent_id = @parent_id AND id = @id FOR UPDATE",
			new { parent_id = hintParentId.Value, id }, tx
		);
		if (old == null) {
			tx.Rollback();
			return false;
		}

		int rows;
		if (old.ParentId == newParentId) {
			// Within the same parent: just a name change → a light UPDATE. It does not cross shards, so it is safe on Citus too.
			rows = conn.Execute(
				$@"UPDATE {inodeTable}
				   SET name = @name, st_ctime = current_timestamp, updated_at = current_timestamp
				   WHERE parent_id = @parent_id AND id = @id",
				new { parent_id = old.ParentId, id, name = newName }, tx
			);
		} else {
			// Parent change: rewriting the distribution key, so Citus rejects the UPDATE. Use DELETE + INSERT to
			// move the row to the new shard, and OVERRIDING SYSTEM VALUE to preserve the BIGSERIAL id.
			conn.Execute($"DELETE FROM {inodeTable} WHERE parent_id = @parent_id AND id = @id",
				new { parent_id = old.ParentId, id }, tx);
			rows = conn.Execute(
				$@"INSERT INTO {inodeTable}
				   (id, parent_id, name, uname, gname, st_mode, st_nlink, st_size,
				    st_mtime, st_ctime, link_target, is_junction, data_id, xattr_names, xattr_values,
				    created_at, created_by, updated_at, updated_by)
				   OVERRIDING SYSTEM VALUE
				   VALUES (@id, @parent_id, @name, @uname, @gname, @st_mode, @st_nlink, @st_size,
				           @st_mtime, current_timestamp, @link_target, @is_junction, @data_id, @xattr_names, @xattr_values,
				           @created_at, @created_by, current_timestamp, @updated_by)",
				new {
					id,
					parent_id = newParentId,
					name = newName,
					uname = old.UserName,
					gname = old.GroupName,
					st_mode = old.Mode,
					st_nlink = old.NLink,
					st_size = old.Size,
					st_mtime = old.Mtime,
					link_target = old.LinkTarget,
					is_junction = old.IsJunction,
					data_id = old.DataId,
					xattr_names = old.xattr_names,
				xattr_values = old.xattr_values,
					created_at = old.created_at,
					created_by = old.created_by,
					updated_by = old.updated_by,
				},
				tx
			);
		}
		if (rows > 0) {
			this.WriteAudit(conn, tx, AuditOp.Rename, id, newParentId, newName, new {
				old_parent = old.ParentId,
				new_parent = newParentId,
				new_name = newName,
			});
		}
		tx.Commit();

		if (rows > 0) {
			// Also update the in-memory Inode. Name / ParentId are keys tied directly to cache consistency, so do not Invalidate
			if (cached != null) {
				cached.ParentId = newParentId;
				cached.Name = newName;
				cached.Ctime = DateTime.UtcNow;
			}
			// The child-list cache must be discarded for both since the parent changed; re-fetched next time by ListChildren
			this.inodeCache.InvalidateChildren(newParentId);
			var parents = new List<long> { newParentId };
			if (old.ParentId != newParentId) {
				this.inodeCache.InvalidateChildren(old.ParentId);
				parents.Add(old.ParentId);
			}
			// Notify other clients. If oldPath exists, have the descendant byPath entries invalidated in bulk
			var prefixes = oldPath != null ? new[] { oldPath } : null;
			this.Notify(inodeIds: [id], parentIds: parents, pathPrefixes: prefixes);
		}
		return rows > 0;
	}

	// ------------------------------------------------------------------
	// Extended attributes (xattr)
	// ------------------------------------------------------------------
	//
	// pgfs_inode holds xattr as two parallel arrays: xattr_names TEXT[] and xattr_values BYTEA[]
	// (the same index is a pair). Values are kept faithfully as bytea (migrated from the old JSONB + Base64;
	// any byte sequence including NUL round-trips unmodified. Design of record: docs/xattr-bytea.md).
	// The mutators keep both arrays atomically with a "single UPDATE statement, no subquery" (array_position + array slicing).

	/// <summary>Gets the value of the given attribute. null if it does not exist.</summary>
	public byte[]? GetXAttr(long inodeId, string name) {
		// SELinux etc. query xattr very frequently, so always use the cache.
		// The inode's two arrays were fetched at load time.
		var inode = this.inodeCache.Get(inodeId);
		if (inode != null) {
			return inode.GetXattr(name);
		}
		// array_position returning NULL (= absent) makes the subscript NULL too → the value is NULL.
		var sql = $@"
			SELECT xattr_values[array_position(xattr_names, @name)]
			FROM {this.QualifiedTable("inode")}
			WHERE id = @id
		";
		return Pg.Query<byte[]?>(this.connectionString, sql, new { id = inodeId, name }).FirstOrDefault();
	}

	/// <summary>Sets / updates an attribute. `replaceOnly` fails if it does not exist; `createOnly` fails if it exists.</summary>
	public bool SetXAttr(long inodeId, string name, ReadOnlySpan<byte> value, bool createOnly, bool replaceOnly) {
		// existence check (for the createOnly/replaceOnly decision). null if the inode is absent.
		var existsSql = $"SELECT array_position(xattr_names, @name) IS NOT NULL FROM {this.QualifiedTable("inode")} WHERE id = @id";
		var existing = Pg.Query<bool?>(this.connectionString, existsSql, new { id = inodeId, name }).FirstOrDefault();
		if (existing == null) {
			return false; // no such inode
		}
		if (createOnly && existing == true) {
			return false;
		}
		if (replaceOnly && existing == false) {
			return false;
		}

		// A single UPDATE (atomic): replace the value at the same index if it exists, else append at the end.
		// The array slice arr[1:i-1] || elem || arr[i+1:] replaces the i-th element (the RHS evaluates against the pre-update row).
		var bytes = value.ToArray();
		var sql = $@"
			UPDATE {this.QualifiedTable("inode")}
			SET xattr_names = CASE WHEN array_position(xattr_names, @name) IS NULL
			                       THEN array_append(xattr_names, @name) ELSE xattr_names END,
			    xattr_values = CASE WHEN array_position(xattr_names, @name) IS NULL
			                        THEN array_append(xattr_values, @value)
			                        ELSE xattr_values[1:array_position(xattr_names, @name)-1]
			                             || @value
			                             || xattr_values[array_position(xattr_names, @name)+1:] END,
			    updated_at = current_timestamp
			WHERE id = @id
		";
		var rows = Pg.Execute(this.connectionString, sql, new { id = inodeId, name, value = bytes });
		if (rows > 0) {
			this.inodeCache.Invalidate(inodeId, path: null);
			this.Notify(inodeIds: [inodeId]);
		}
		return rows > 0;
	}

	/// <summary>Enumerates all xattr names.</summary>
	public IReadOnlyList<string> ListXAttr(long inodeId) {
		// Return the name array directly via the cache (avoids a DB hit).
		var inode = this.inodeCache.Get(inodeId);
		if (inode != null) {
			return inode.xattr_names.ToList();
		}
		var sql = $"SELECT xattr_names FROM {this.QualifiedTable("inode")} WHERE id = @id";
		var names = Pg.Query<string[]>(this.connectionString, sql, new { id = inodeId }).FirstOrDefault();
		if (names == null) {
			return Array.Empty<string>();
		}
		return names;
	}

	/// <summary>Removes an attribute.</summary>
	public bool RemoveXAttr(long inodeId, string name) {
		// A single UPDATE (atomic) that removes the name's index from both arrays at once. The RHS evaluates against the pre-update row.
		var sql = $@"
			UPDATE {this.QualifiedTable("inode")}
			SET xattr_names  = xattr_names[1:array_position(xattr_names, @name)-1]
			                 || xattr_names[array_position(xattr_names, @name)+1:],
			    xattr_values = xattr_values[1:array_position(xattr_names, @name)-1]
			                 || xattr_values[array_position(xattr_names, @name)+1:],
			    updated_at = current_timestamp
			WHERE id = @id AND array_position(xattr_names, @name) IS NOT NULL
		";
		var rows = Pg.Execute(this.connectionString, sql, new { id = inodeId, name });
		if (rows > 0) {
			this.inodeCache.Invalidate(inodeId, path: null);
			this.Notify(inodeIds: [inodeId]);
		}
		return rows > 0;
	}

	// ------------------------------------------------------------------
	// Symbolic links / hard links
	// ------------------------------------------------------------------

	/// <summary>
	/// Creates a new symbolic-link inode.
	/// `target` is the link target path string (passed by the FUSE side).
	/// </summary>
	public Inode? CreateSymlink(long parentId, string name, string uname, string gname, string target) {
		// Cross-platform: a symlink's mode is S_IFLNK | 0777.
		var stMode = Mode.S_IFLNK | 0x1FF; // 0777
		return this.InsertInode(parentId, name, uname, gname, stMode, dataId: null, linkTarget: target);
	}

	/// <summary>
	/// Creates a hard link to an existing inode as a new directory entry.
	/// Fails if an entry with the same name already exists in the parent inode.
	/// Shares the data body (data_id) and increments the source inode's st_nlink by 1.
	/// </summary>
	public Inode? CreateHardLink(Inode source, long newParentId, string newName) {
		// Cross-platform: a hard link is the operation of "adding an inode pointing at the same data_id".
		if (source.IsDirectory) {
			// A hard link to a directory is normally forbidden in POSIX
			return null;
		}

		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();

		// Lock the source inode (nlink update) and the new parent (children update) in fixed ascending target_id order.
		this.LockInodes(conn, tx, source.Id, newParentId);

		var sql = $@"
			INSERT INTO {this.QualifiedTable("inode")} (
				parent_id, name, uname, gname, st_mode, st_nlink, st_size, data_id, xattr_names, xattr_values,
				created_by, updated_by
			) VALUES (
				@parent_id, @name, @uname, @gname, @st_mode, 1, @st_size, @data_id, @xattr_names, @xattr_values,
				@uname, @uname
			)
			ON CONFLICT (parent_id, name) DO NOTHING
			RETURNING id, parent_id, name, uname, gname, st_mode, st_nlink, st_size,
			          st_mtime, st_ctime, link_target, is_junction, data_id, xattr_names, xattr_values,
			          created_at, created_by, updated_at, updated_by
		";
		var newInode = conn.QueryFirstOrDefault<Inode>(sql, new {
			parent_id = newParentId,
			name = newName,
			uname = source.UserName,
			gname = source.GroupName,
			st_mode = source.Mode,
			st_size = source.Size,
			data_id = source.DataId,
			xattr_names = source.xattr_names,
			xattr_values = source.xattr_values,
		}, tx);
		if (Logger.IsTraceEnabled) { Logger.Trace("INSERT inode (hardlink) parent_id:", newParentId, " name:", newName, " = ", newInode?.Id); }

		if (newInode == null) {
			return null; // conflict
		}

		// Increment st_nlink for every inode sharing the same data_id
		// (requirement: a hard link expresses the reference count via st_nlink)
		List<long> siblingIds = new();
		if (source.DataId != null) {
			var bumped = conn.Execute(
				$@"UPDATE {this.QualifiedTable("inode")}
				   SET st_nlink = st_nlink + 1, st_ctime = current_timestamp, updated_at = current_timestamp
				   WHERE data_id = @data_id AND id <> @new_id",
				new { data_id = source.DataId, new_id = newInode.Id },
				tx
			);
			if (Logger.IsTraceEnabled) { Logger.Trace("UPDATE inode SET st_nlink+=1 WHERE data_id:", source.DataId, " rows:", bumped); }
			// Align the new inode's nlink to the total number of inodes with the same data_id
			var synced = conn.Execute(
				$@"UPDATE {this.QualifiedTable("inode")}
				   SET st_nlink = (SELECT COUNT(*) FROM {this.QualifiedTable("inode")} WHERE data_id = @data_id)
				   WHERE data_id = @data_id",
				new { data_id = source.DataId },
				tx
			);
			if (Logger.IsTraceEnabled) { Logger.Trace("UPDATE inode SET st_nlink=count WHERE data_id:", source.DataId, " rows:", synced); }
			// To invalidate all siblings' caches in the next step, fetch the IDs inside the tx
			// (include all but source.Id; newInode.Id is also re-fetched as refreshed, so include it too)
			siblingIds = conn.Query<long>(
				$"SELECT id FROM {this.QualifiedTable("inode")} WHERE data_id = @data_id",
				new { data_id = source.DataId },
				tx
			).ToList();
		}

		this.WriteAudit(conn, tx, AuditOp.Hardlink, newInode.Id, newParentId, newName, new {
			source_id = source.Id,
			new_parent = newParentId,
			new_name = newName,
		});

		tx.Commit();
		// Also drop from the cache existing siblings whose nlink changed via the hard link (prevents siblings other than
		// source from keeping a stale nlink on the 3rd+ hard link). newInode.Id is re-read by GetById below, so including it is fine.
		foreach (var siblingId in siblingIds) {
			this.inodeCache.Invalidate(siblingId, path: null);
		}
		// Just in case, source explicitly too (it should be in siblingIds, but as insurance for the source.DataId == null path)
		this.inodeCache.Invalidate(source.Id, path: null);
		// Discard the child-list cache of the new link target's parent directory
		this.inodeCache.InvalidateChildren(newParentId);
		// Notify other clients: siblings + source + the new inode + the new parent
		var notifyIds = new List<long>(siblingIds);
		notifyIds.Add(source.Id);
		notifyIds.Add(newInode.Id);
		this.Notify(inodeIds: notifyIds, parentIds: [newParentId]);
		// Re-read the new inode to get the nlink-reflected value
		var refreshed = this.GetById(newInode.Id);
		return refreshed ?? newInode;
	}

	// ------------------------------------------------------------------
	// Volume information
	// ------------------------------------------------------------------

	/// <summary>
	/// Gets the current DB usage. Not the tablespace size but the size of the whole database.
	/// Cross-platform: uses PostgreSQL's pg_database_size, so it is OS-independent.
	/// </summary>
	public long GetTotalUsedBytes() {
		var dbName = this.config.Database.Connection.Database ?? "pgfs";
		var sql = "SELECT pg_database_size(@db)";
		return Pg.Query<long>(this.connectionString, sql, new { db = dbName }).FirstOrDefault();
	}

	/// <summary>
	/// The filesystem's nominal capacity (derived from <see cref="Pgfs.Lib.Config.FileSystemConfig.MaxFileSize"/>, saved in pgfs.toml).
	/// </summary>
	public long GetCapacityBytes() {
		var max = this.config.FileSystem.MaxFileSize;
		// When `-1` (unlimited), return 1PiB for convenience. The FUSE/Dokan APIs have a 64-bit limit.
		if (max <= 0) {
			return 1L << 50;
		}
		return max;
	}

	// Caches the statfs() result for a few seconds (df is hammered by tools, so avoid an all-node Citus round-trip each time).
	private (long Total, long Avail, System.DateTime At)? statFsCache;
	private static readonly System.TimeSpan StatFsCacheTtl = System.TimeSpan.FromSeconds(5);

	/// <summary>
	/// The (total, available) bytes for df / statvfs. If the server-side <c>{prefix}statfs()</c> (plperlu, mkfs `--statfs`)
	/// exists, use its real measurement; otherwise fall back to the nominal capacity
	/// (<see cref="GetCapacityBytes"/> − <see cref="GetTotalUsedBytes"/>). Cached for a few seconds. Design of record: docs/df-support.md.
	/// </summary>
	public (long Total, long Avail) GetStatFs() {
		var now = System.DateTime.UtcNow;
		if (this.statFsCache is { } c && (now - c.At) < StatFsCacheTtl) {
			return (c.Total, c.Avail);
		}
		if (!this.TryGetStatFsFromServer(out var total, out var avail)) {
			total = this.GetCapacityBytes();
			avail = System.Math.Max(0, total - this.GetTotalUsedBytes());
		}
		this.statFsCache = (total, avail, now);
		return (total, avail);
	}

	/// <summary>Calls the server-side <c>{prefix}statfs()</c>. Returns false (= fall back) when the function is absent / fails / NULL.</summary>
	private bool TryGetStatFsFromServer(out long total, out long avail) {
		total = 0;
		avail = 0;
		// nominal mode contracts not to create the server-side function, so avoid a wasteful query + exception and fall back immediately.
		if (this.config.Statfs.Mode == "nominal") {
			return false;
		}
		try {
			var row = Pg.Query<dynamic>(
				this.connectionString,
				$"SELECT total, avail FROM {this.QualifiedTable("statfs")}()"
			).FirstOrDefault();
			if (row == null) {
				return false;
			}
			object? tv = row.total;
			object? av = row.avail;
			if (tv is not long t || av is not long a || t <= 0) {
				return false;
			}
			total = t;
			avail = a;
			return true;
		} catch (System.Exception ex) {
			if (Logger.IsTraceEnabled) { Logger.Trace("statfs() unavailable, falling back to the nominal capacity: ", ex.Message); }
			return false;
		}
	}

	// ------------------------------------------------------------------
	// Data I/O (bytea chunks)
	// ------------------------------------------------------------------
	//
	// One file's data body is held as one `pgfs_data` row and multiple `pgfs_data_chunk` rows under it
	// (1 chunk = 1 bytea). The chunk size is the `pgfs_data.chunk_size` column
	// (which adopts `Pgfs.Lib.Config.FileSystemConfig.DefaultChunkSize` at the first INSERT).
	//
	// The data body is stored as bytea so that it can be distributed with Citus. See
	// [docs/support_for_citus.md](../../../../docs/support_for_citus.md).
	//
	// Each chunk's payload length equals "the number of bytes written so far" (a trailing partial chunk is
	// short according to st_size; an interior hole is represented by length(payload) not yet reaching the write
	// offset). ReadData zero-fills the shortfall, and WriteData pads the payload with zeros as needed while
	// extending it. A 1MB-class bytea reliably exceeds PG's TOAST threshold (~2KB), so it is TOASTed
	// automatically, and PG 13+ partial detoast makes `substring(payload ...)` work as a server-side partial
	// read (= network transfer is only the requested portion).

	/// <summary>
	/// Reads the inode's data body and writes it into the destination span.
	/// If the range contains holes (chunks not yet written / unreached regions within a chunk), they are zero-filled.
	/// </summary>
	public int ReadData(Inode inode, long offset, Span<byte> destination) {
		if (destination.Length == 0 || offset < 0) {
			return 0;
		}
		if (inode.DataId == null) {
			// data body not yet allocated = treat as all zeros, but do not read past st_size
			return 0;
		}
		if (offset >= inode.Size) {
			return 0;
		}

		var maxLen = (int)Math.Min((long)destination.Length, inode.Size - offset);
		if (maxLen <= 0) {
			return 0;
		}

		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();

		var chunkSize = LoadChunkSize(conn, tx, inode.DataId.Value);

		var totalRead = 0;
		var curOffset = offset;
		var remaining = maxLen;
		var destPos = 0;

		while (remaining > 0) {
			var chunkIndex = (int)(curOffset / chunkSize);
			var offsetInChunk = (int)(curOffset % chunkSize);
			var bytesInThisChunk = Math.Min(remaining, chunkSize - offsetInChunk);

			var bytes = ReadChunkSlice(conn, tx, inode.DataId.Value, chunkIndex, offsetInChunk, bytesInThisChunk);
			if (bytes == null) {
				// hole (no chunk row) — fill with zeros
				destination.Slice(destPos, bytesInThisChunk).Clear();
			} else {
				bytes.AsSpan().CopyTo(destination.Slice(destPos, bytesInThisChunk));
				if (bytes.Length < bytesInThisChunk) {
					// payload does not reach the end of the requested range (= a hole within the chunk) — zero-fill the rest
					destination.Slice(destPos + bytes.Length, bytesInThisChunk - bytes.Length).Clear();
				}
			}

			destPos += bytesInThisChunk;
			curOffset += bytesInThisChunk;
			remaining -= bytesInThisChunk;
			totalRead += bytesInThisChunk;
		}

		tx.Commit();
		return totalRead;
	}

	/// <summary>
	/// Writes to the inode's data body, creating chunk rows as needed.
	/// After writing, extends st_size.
	/// </summary>
	public int WriteData(Inode inode, long offset, ReadOnlySpan<byte> source) {
		if (source.Length == 0 || offset < 0) {
			return 0;
		}

		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();

		var (dataId, chunkSize) = EnsureDataRow(conn, tx, inode);
		// Right after EnsureDataRow settles data_id, take the data lock to serialize with other clients'
		// concurrent WriteData/TruncateData/ReleaseData (released automatically at tx end).
		this.LockData(conn, tx, dataId);

		var totalWritten = 0;
		var curOffset = offset;
		var srcPos = 0;
		var remaining = source.Length;

		while (remaining > 0) {
			var chunkIndex = (int)(curOffset / chunkSize);
			var offsetInChunk = (int)(curOffset % chunkSize);
			var bytesInThisChunk = Math.Min(remaining, chunkSize - offsetInChunk);

			// Must escape from Span to byte[] to bind it as a parameter
			var slice = source.Slice(srcPos, bytesInThisChunk).ToArray();
			WriteChunkSlice(conn, tx, dataId, chunkIndex, offsetInChunk, slice);
			totalWritten += bytesInThisChunk;

			srcPos += bytesInThisChunk;
			curOffset += bytesInThisChunk;
			remaining -= bytesInThisChunk;
		}

		// Extend st_size as needed (never shrink it)
		var newSize = Math.Max(inode.Size, offset + totalWritten);
		if (newSize > inode.Size) {
			UpdateSizeInTx(conn, tx, inode.Id, newSize);
		} else {
			TouchMtimeInTx(conn, tx, inode.Id);
		}

		tx.Commit();
		// Also sync the in-memory Inode object with the DB.
		// Note: the `inode` the caller passes is usually the instance in the FUSE/Dokan Context.
		// Meanwhile `InodeCache.byId` and `childrenByParent` may hold a different instance loaded by a later
		// `ListChildren` reload. Update both to keep them consistent.
		this.SyncInodeFields(inode, newSize, DateTime.UtcNow);
		this.Notify(inodeIds: [inode.Id]);
		return totalWritten;
	}

	/// <summary>
	/// Syncs the in-memory Inode after a metadata update. Rewrites Size / Mtime / Ctime on both the argument inode and
	/// the different instance in the cache (if any). A countermeasure for the problem where a stale Inode referenced via
	/// `InodeCache`'s `byId` / `childrenByParent` returns old values.
	/// </summary>
	private void SyncInodeFields(Inode inode, long newSize, DateTime newMtime) {
		inode.Size = newSize;
		inode.Mtime = newMtime;
		var cached = this.inodeCache.Get(inode.Id);
		if (cached != null && !ReferenceEquals(cached, inode)) {
			cached.Size = newSize;
			cached.Mtime = newMtime;
		}
	}

	/// <summary>
	/// Truncates the data body to newLength bytes. Chunks beyond the new size are deleted, and the trailing chunk is
	/// adjusted to the specified length via substring / zero-padding.
	/// </summary>
	public bool TruncateData(Inode inode, long newLength) {
		if (newLength < 0) {
			return false;
		}

		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();

		if (newLength == 0) {
			// delete everything
			if (inode.DataId != null) {
				// take the data lock to serialize with concurrent WriteData/ReleaseData
				this.LockData(conn, tx, inode.DataId.Value);
				DropAllChunks(conn, tx, inode.DataId.Value);
				DropDataRow(conn, tx, inode.DataId.Value);
				ClearInodeDataId(conn, tx, inode.Id);
				inode.DataId = null;
			}
			UpdateSizeInTx(conn, tx, inode.Id, 0);
			tx.Commit();
			this.SyncInodeFields(inode, 0, DateTime.UtcNow);
			this.Notify(inodeIds: [inode.Id]);
			return true;
		}

		var (dataId, chunkSize) = EnsureDataRow(conn, tx, inode);
		// After EnsureDataRow, take the data lock to serialize with concurrent WriteData/ReleaseData
		this.LockData(conn, tx, dataId);
		var lastChunkIndex = (int)((newLength - 1) / chunkSize);
		var lastChunkLength = (int)(newLength - (long)lastChunkIndex * chunkSize);

		// Delete the chunks entirely beyond the tail
		var deletedChunks = conn.Execute(
			$"DELETE FROM {this.QualifiedTable("data_chunk")} WHERE data_id = @data_id AND chunk_index > @last",
			new { data_id = dataId, last = lastChunkIndex },
			tx
		);
		if (Logger.IsTraceEnabled) { Logger.Trace("DELETE data_chunk data_id:", dataId, " chunk_index>", lastChunkIndex, " rows:", deletedChunks); }

		// Adjust the trailing chunk to lastChunkLength bytes (zero-pad if short)
		TruncateChunk(conn, tx, dataId, lastChunkIndex, lastChunkLength);

		UpdateSizeInTx(conn, tx, inode.Id, newLength);
		tx.Commit();
		this.SyncInodeFields(inode, newLength, DateTime.UtcNow);
		this.Notify(inodeIds: [inode.Id]);
		return true;
	}

	/// <summary>
	/// Completely discards the inode's data (called on the final link at Unlink).
	/// </summary>
	public void ReleaseData(long dataId) {
		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();
		// data lock to serialize with concurrent WriteData/TruncateData (released automatically at tx end)
		this.LockData(conn, tx, dataId);
		DropAllChunks(conn, tx, dataId);
		DropDataRow(conn, tx, dataId);
		tx.Commit();
	}

	// ------------------------------------------------------------------
	// Connection helper
	// ------------------------------------------------------------------

	private Npgsql.NpgsqlConnection NewConnection() {
		// Get a pooled connection via the DataSource cache in Pg.cs (the caller disposes it with using)
		return Pg.OpenConnection(this.connectionString);
	}

	// ------------------------------------------------------------------
	// Audit log (only when audit.enabled is true. The design of record is docs/audit-log.md)
	// ------------------------------------------------------------------

	/// <summary>
	/// INSERTs one audit row. Called after each mutating method confirms success (rows &gt; 0 / a non-null return value),
	/// in the <b>same transaction</b> (<paramref name="conn"/> / <paramref name="tx"/>) as that operation.
	/// Immediate no-op if <c>audit.enabled</c> is false.
	///
	/// <para>
	/// caller_ip is <c>inet_client_addr()</c> evaluated server-side (the connecting host as seen by PG); caller_host is the
	/// host name resolved at process startup; caller_uid/uname/domain come from the <see cref="AuditContext.Current"/> set by
	/// the OS layer. occurred_at is given explicitly as <paramref name="when"/>, and the monthly partition is ensured with the
	/// same value to keep routing consistent.
	/// </para>
	/// </summary>
	private void WriteAudit(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, string op, long? targetId, long? parentId, string? name, object? detail) {
		if (!this.auditEnabled) { return; }
		var when = DateTime.Now;
		this.EnsureAuditPartition(when);
		var ctx = AuditContext.Current;
		var detailJson = "{}";
		if (detail != null) { detailJson = JsonSerializer.Serialize(detail); }
		var sql = $@"
			INSERT INTO {this.QualifiedTable("audit")} (
				occurred_at, op, target_id, parent_id, name, detail,
				caller_ip, caller_host, caller_uid, caller_uname, caller_domain
			) VALUES (
				@occurred_at, @op, @target_id, @parent_id, @name, @detail::jsonb,
				inet_client_addr(), @caller_host, @caller_uid, @caller_uname, @caller_domain
			)
		";
		conn.Execute(sql, new {
			occurred_at = when,
			op,
			target_id = targetId,
			parent_id = parentId,
			name,
			detail = detailJson,
			caller_host = this.auditHost,
			caller_uid = ctx?.Uid,
			caller_uname = ctx?.Uname,
			caller_domain = ctx?.Domain,
		}, tx);
	}

	/// <summary>
	/// Creates the RANGE partition for the month <paramref name="when"/> falls in, if needed. Because Citus rejects DDL inside a
	/// distributed write tx, it is issued on a <b>separate connection from the operation tx</b> (autocommit), narrowed to the first time each month via an in-memory cache.
	///
	/// <para>
	/// There is no DEFAULT partition (to avoid the PG limitation that once rows accumulate in DEFAULT, a month partition for that
	/// range can no longer be CREATEd afterward). So if the ensure fails, the subsequent INSERT fails with "no partition" and the
	/// whole operation tx rolls back (= the atomic policy: if the audit cannot be recorded, the operation does not stand either).
	/// Failures are logged, but if it already exists (e.g. created concurrently) the INSERT succeeds. The cache is updated only on success.
	/// </para>
	/// </summary>
	private void EnsureAuditPartition(DateTime when) {
		var monthStart = new DateTime(when.Year, when.Month, 1);
		var key = monthStart.ToString("yyyy_MM");
		lock (this.auditPartitionLock) {
			if (this.ensuredAuditPartitions.Contains(key)) { return; }
		}
		var nextMonth = monthStart.AddMonths(1);
		var partName = this.tableNamePrefix + "audit_" + key;
		var partQualified = Pg.QuoteIdentifier(this.schemaName) + "." + Pg.QuoteIdentifier(partName);
		// The partition boundary values cannot be parameterized (literals required), but they are numeric-derived dates, so there is no room for injection.
		var fromLit = monthStart.ToString("yyyy-MM-dd");
		var toLit = nextMonth.ToString("yyyy-MM-dd");
		var sql = $"CREATE TABLE IF NOT EXISTS {partQualified} PARTITION OF {this.QualifiedTable("audit")} " +
			$"FOR VALUES FROM ('{fromLit}') TO ('{toLit}')";
		try {
			Pg.Execute(this.connectionString, sql);
		} catch (System.Exception ex) {
			Logger.Warning("audit partition ensure failed (rows fall back to DEFAULT): ", ex.Message);
			return;
		}
		lock (this.auditPartitionLock) {
			this.ensuredAuditPartitions.Add(key);
		}
	}

	/// <summary>Returns the audit kind string from st_mode's file-type bits (S_IFMT).</summary>
	private static string AuditKind(int stMode) {
		var type = stMode & 0xF000;
		if (type == Mode.S_IFDIR) { return "dir"; }
		if (type == Mode.S_IFLNK) { return "symlink"; }
		if (type == Mode.S_IFREG) { return "file"; }
		return "other";
	}

	private (long dataId, int chunkSize) EnsureDataRow(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, Inode inode) {
		if (inode.DataId is long cached) {
			var size = LoadChunkSize(conn, tx, cached);
			return (cached, size);
		}
		// In-memory inode.DataId is null = the new-data-row-creation path.
		// If Dokan / FUSE is issuing concurrent WriteFile (e.g. CopyFileEx's 2 MiB writes), multiple txs may reach here at
		// the same time and both INSERT a data row → the last-wins UPDATE inode leaves the loser's data row dangling.
		// Chunks written there become unreachable (a silent leak + data loss).
		// It does not die loudly like a PK violation, so it looks like it works — it just happens not to race.
		// As a preventive measure, lock the inode row with SELECT ... FOR UPDATE to serialize between txs,
		// and after acquiring the lock, re-read the true data_id from the DB and reuse the existing one if any.
		//
		// Citus note: without the distribution key parent_id in the WHERE, FOR UPDATE dies with an error
		// ("could not run distributed query with FOR UPDATE/SHARE commands"), so pass
		// inode.ParentId as a routing hint.
		var freshDataId = conn.QueryFirstOrDefault<long?>(
			$"SELECT data_id FROM {this.QualifiedTable("inode")} WHERE parent_id = @parent_id AND id = @id FOR UPDATE",
			new { parent_id = inode.ParentId, id = inode.Id },
			tx
		);
		if (freshDataId is long winning) {
			// A concurrent other tx created and committed it first. Use that.
			if (Logger.IsTraceEnabled) { Logger.Trace("EnsureDataRow: lost race, reusing data_id:", winning, " for inode:", inode.Id); }
			inode.DataId = winning;
			var size = LoadChunkSize(conn, tx, winning);
			return (winning, size);
		}
		// While holding the lock, create a new pgfs_data and link it to the inode.
		var defaultChunkSize = (int)Math.Max(4096, this.config.FileSystem.DefaultChunkSize);
		var sql = $@"
			INSERT INTO {this.QualifiedTable("data")} (chunk_size, total_size, created_by, updated_by)
			VALUES (@chunk_size, 0, @user, @user) RETURNING id
		";
		var dataId = conn.QuerySingle<long>(sql, new {
			chunk_size = defaultChunkSize,
			user = Environment.UserName ?? "pgfs",
		}, tx);
		if (Logger.IsTraceEnabled) { Logger.Trace("INSERT data chunk_size:", defaultChunkSize, " = id:", dataId); }
		// Citus note: UPDATE also requires parent_id (aims at the same shard as the row lock taken by the FOR UPDATE above)
		var linkRows = conn.Execute(
			$"UPDATE {this.QualifiedTable("inode")} SET data_id = @data_id, updated_at = current_timestamp WHERE parent_id = @parent_id AND id = @id",
			new { data_id = dataId, parent_id = inode.ParentId, id = inode.Id },
			tx
		);
		if (linkRows == 0) {
			// FOR UPDATE returned empty yet UPDATE also affected 0 rows = the inode was concurrently deleted.
			// This tx already INSERTed a data row, so throw to roll back and prevent orphaning.
			throw new InvalidOperationException($"EnsureDataRow: inode {inode.Id} no longer exists");
		}
		if (Logger.IsTraceEnabled) { Logger.Trace("UPDATE inode SET data_id:", dataId, " WHERE id:", inode.Id, " rows:", linkRows); }
		inode.DataId = dataId;
		return (dataId, defaultChunkSize);
	}

	private int LoadChunkSize(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId) {
		return conn.QuerySingle<int>(
			$"SELECT chunk_size FROM {this.QualifiedTable("data")} WHERE id = @id",
			new { id = dataId },
			tx
		);
	}

	/// <summary>
	/// Reads the [offsetInChunk, offsetInChunk + length) portion from a chunk.
	/// null if the row does not exist. If the payload only reaches partway into the requested range, returns just what is there
	/// (the caller is expected to zero-fill the rest).
	/// On PG 13+, `substring(payload from N for M)` triggers TOAST partial detoast, so even a 4KB read from a 1MB chunk
	/// keeps the actual transfer to around 4KB.
	/// </summary>
	private byte[]? ReadChunkSlice(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId, int chunkIndex, int offsetInChunk, int length) {
		var bytes = conn.QueryFirstOrDefault<byte[]>(
			$@"SELECT substring(payload FROM @off + 1 FOR @len)
			   FROM {this.QualifiedTable("data_chunk")}
			   WHERE data_id = @data_id AND chunk_index = @ix",
			new { data_id = dataId, ix = chunkIndex, off = offsetInChunk, len = length },
			tx
		);
		if (Logger.IsTraceEnabled) { Logger.Trace("SELECT substring(payload) data_id:", dataId, " chunk_index:", chunkIndex, " off:", offsetInChunk, " len:", length, " = ", bytes?.Length.ToString() ?? "(null)"); }
		return bytes;
	}

	/// <summary>
	/// Writes the data content starting at offsetInChunk of a chunk.
	/// INSERT if the row does not exist; if it exists, overlay while padding the payload with zeros and extending as needed.
	/// Completes in one SQL (race-safe: a concurrent tx's INSERT is routed to the UPDATE path via ON CONFLICT).
	/// </summary>
	private void WriteChunkSlice(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId, int chunkIndex, int offsetInChunk, byte[] data) {
		// INSERT-side payload: zero-fill the first offsetInChunk bytes, then append data.
		//   If offsetInChunk = 0, data as-is.
		// UPDATE-side payload (CASE):
		//   1) existing payload >= off+len → replace the middle via overlay (no extension needed)
		//   2) existing payload >= off but < off+len → keep the first part (1..off), then concatenate data (drop the extra tail)
		//   3) existing payload < off → existing + zero padding + data (fill the hole with zeros and extend)
		// Citus note: writing `updated_at = current_timestamp` in the DO UPDATE clause dies on a distributed table with
		// "functions used in the DO UPDATE SET clause of INSERTs on distributed tables must be marked IMMUTABLE".
		// Passing it via EXCLUDED makes it a "constant from VALUES" and it passes. The same SQL runs as-is on a single PG,
		// so no branch on the Citus flag is needed.
		var now = DateTime.UtcNow;
		var sql = $@"
			INSERT INTO {this.QualifiedTable("data_chunk")}
				(data_id, chunk_index, payload, created_at, created_by, updated_at, updated_by)
			VALUES (
				@data_id, @ix,
				CASE WHEN @off = 0 THEN @data ELSE decode(repeat('00', @off), 'hex') || @data END,
				@now, @user, @now, @user
			)
			ON CONFLICT (data_id, chunk_index) DO UPDATE SET
				payload = CASE
					WHEN length({this.QualifiedTable("data_chunk")}.payload) >= @off + @len THEN
						overlay({this.QualifiedTable("data_chunk")}.payload PLACING @data FROM @off + 1 FOR @len)
					WHEN length({this.QualifiedTable("data_chunk")}.payload) >= @off THEN
						substring({this.QualifiedTable("data_chunk")}.payload FROM 1 FOR @off) || @data
					ELSE
						{this.QualifiedTable("data_chunk")}.payload
							|| decode(repeat('00', @off - length({this.QualifiedTable("data_chunk")}.payload)), 'hex')
							|| @data
				END,
				updated_at = EXCLUDED.updated_at,
				updated_by = EXCLUDED.updated_by
		";
		var rows = conn.Execute(sql, new {
			data_id = dataId,
			ix = chunkIndex,
			off = offsetInChunk,
			len = data.Length,
			data,
			user = Environment.UserName ?? "pgfs",
			now,
		}, tx);
		if (Logger.IsTraceEnabled) { Logger.Trace("UPSERT data_chunk data_id:", dataId, " chunk_index:", chunkIndex, " off:", offsetInChunk, " len:", data.Length, " rows:", rows); }
	}

	/// <summary>
	/// Adjusts a chunk to exactly exactLen bytes (truncates via substring if longer; zero-pads if it needs to grow).
	/// Does nothing if the row does not exist (= for TruncateData's trailing handling).
	/// </summary>
	private void TruncateChunk(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId, int chunkIndex, int exactLen) {
		var rows = conn.Execute(
			$@"UPDATE {this.QualifiedTable("data_chunk")} SET
				payload = CASE
					WHEN length(payload) >= @len THEN substring(payload FROM 1 FOR @len)
					ELSE payload || decode(repeat('00', @len - length(payload)), 'hex')
				END,
				updated_at = current_timestamp,
				updated_by = @user
			   WHERE data_id = @data_id AND chunk_index = @ix",
			new { data_id = dataId, ix = chunkIndex, len = exactLen, user = Environment.UserName ?? "pgfs" },
			tx
		);
		if (Logger.IsTraceEnabled) { Logger.Trace("TruncateChunk data_id:", dataId, " chunk_index:", chunkIndex, " len:", exactLen, " rows:", rows); }
	}

	private void DropAllChunks(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId) {
		var rows = conn.Execute(
			$"DELETE FROM {this.QualifiedTable("data_chunk")} WHERE data_id = @data_id",
			new { data_id = dataId },
			tx
		);
		if (Logger.IsTraceEnabled) { Logger.Trace("DELETE data_chunk data_id:", dataId, " rows:", rows); }
	}

	private void DropDataRow(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId) {
		var rows = conn.Execute(
			$"DELETE FROM {this.QualifiedTable("data")} WHERE id = @id",
			new { id = dataId },
			tx
		);
		if (Logger.IsTraceEnabled) { Logger.Trace("DELETE data id:", dataId, " rows:", rows); }
	}

	private void ClearInodeDataId(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long inodeId) {
		var rows = conn.Execute(
			$"UPDATE {this.QualifiedTable("inode")} SET data_id = NULL, updated_at = current_timestamp WHERE id = @id",
			new { id = inodeId },
			tx
		);
		if (Logger.IsTraceEnabled) { Logger.Trace("UPDATE inode SET data_id:NULL WHERE id:", inodeId, " rows:", rows); }
	}

	private void UpdateSizeInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long inodeId, long size) {
		var rows = conn.Execute(
			$@"UPDATE {this.QualifiedTable("inode")}
			   SET st_size = @size, st_mtime = current_timestamp,
			       st_ctime = current_timestamp, updated_at = current_timestamp
			   WHERE id = @id",
			new { id = inodeId, size },
			tx
		);
		if (Logger.IsTraceEnabled) { Logger.Trace("UPDATE inode SET st_size:", size, " WHERE id:", inodeId, " rows:", rows); }
	}

	private void TouchMtimeInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long inodeId) {
		var rows = conn.Execute(
			$@"UPDATE {this.QualifiedTable("inode")}
			   SET st_mtime = current_timestamp, updated_at = current_timestamp
			   WHERE id = @id",
			new { id = inodeId },
			tx
		);
		if (Logger.IsTraceEnabled) { Logger.Trace("UPDATE inode SET st_mtime WHERE id:", inodeId, " rows:", rows); }
	}
}
