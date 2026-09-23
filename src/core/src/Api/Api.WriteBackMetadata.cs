namespace Pgfs.Core.Api;

using Dapper;
using Logging;
using Models;
using Utility;
using System.Text.Json;

/// <summary>
/// The metadata write-back part of <see cref="Api"/>.
/// The design of record is <see href="../../../../docs/design/runtime-control-plane.md"/> §metadata write-back.
///
/// <para>
/// <b>Outline</b>: inodes created by this mount are held in <see cref="DirtyNamespace"/> as **pending**, so that
/// <c>create → write → close → chmod → utimens → rename</c> collapses into **one transaction per file**
/// (inode INSERT + data row + chunks + attributes + audit). **Pending-born only** —
/// operations on an inode that already has a row in the database (persisted) stay write-through.
/// </para>
///
/// <para>
/// <b>Lock hierarchy (fixed on every path; the reverse order hangs the whole mount unrecoverably)</b>:
/// <c>NSGate → DirtyFile.Gate → DB tx</c>, and the <see cref="DirtyNamespace"/> lock is the **innermost**.
/// <list type="bullet">
///   <item>Background flush / fsync / fsyncdir / unmount: NSGate → (Gate, if there is dirty data) → tx.
///     **Post-commit work happens after the Gate is released** (calling another transaction or
///     <see cref="Api.Rename(long, long, string)"/> from the post-commit step would grow a Gate → NSGate
///     back edge)</item>
///   <item>Write-through that touches a pending inode (rename-over-existing / hardlink / xattr / truncate&gt;0 /
///     write-through write / create after a failed reservation): <see cref="MaterializePending"/> takes the
///     NSGate and performs materialization and the main transaction under the same gate</item>
///   <item>Coalescing (chmod/chown/utimens/rename against a pending inode) and a pending create complete
///     atomically **under the ledger lock alone** = the hot path is never blocked by a background flush.
///     While a flush is in progress (<see cref="CoalesceResult.Busy"/>) we do not coalesce; we wait for it on
///     the NSGate and fall back to write-through (writing through without waiting would UPDATE a row that has
///     not been INSERTed yet, and the update would vanish silently)</item>
///   <item>Never touch <see cref="InodeCache"/> / <see cref="ContentCache"/> /
///     <see cref="DirtyFile.Gate"/> / the database while holding the ledger lock</item>
/// </list>
/// </para>
/// </summary>
public partial class Api
{
	// ------------------------------------------------------------------
	// Stage 2 state (close-no-flush / the four error floors / two-phase flip)
	// ------------------------------------------------------------------

	/// <summary>
	/// Whether **intake of new pending entries is stopped** (phase 1 of disabling <c>write_back_metadata</c> live).
	/// Setting this makes <see cref="MetadataWriteBack"/> return false so that later creates go write-through,
	/// but **the flush paths (ledger / NSGate / materialize) stay alive**, so the pending entries already held
	/// can be written out safely. Dropping <c>config.Mount.WriteBackMetadata</c> in a single phase can leave a
	/// create that races the flip behind as a "pending with no one to flush it".
	/// </summary>
	/// <summary>
	/// Whether intake is closed. **The authority is <see cref="DirtyNamespace.IntakeClosed"/> (under the ledger
	/// lock)**; this is only a read of it. <see cref="DirtyNamespace.TryAdd"/> guarantees that the test and the
	/// registration are atomic.
	/// </summary>
	private bool MetadataIntakeClosed {
		get { return this.dirtyNamespace.IntakeClosed; }
	}

	/// <summary>
	/// Put the mount into the **error state** after this many consecutive flush failures (error floor 3).
	/// With write-through the operation would have failed with -EIO right away, which is noticeable; with
	/// write-back it turns into "an operation that already reported success can never be flushed" (a permanent
	/// failure to ensure the audit partition, an endless retry on a kind mismatch, and so on). So we stop new
	/// writes and creates here to make operators notice.
	/// </summary>
	private const int FlushFailureStateThreshold = 5;

	/// <summary>
	/// Count consecutive flush failures **per flush target** (a success clears that target's entry).
	/// The key follows the same sign convention as <see cref="LockTargets"/> — negative for an inode,
	/// positive for a data body.
	/// <para>
	/// With a single process-wide counter, **one permanently failing target keeps being reset by the successes
	/// of the others and never reaches the threshold** (= neither the error state nor the back-pressure health
	/// check ever fires in practice). Counting per target lets the one broken entry reach the threshold on its
	/// own, no matter how often the others succeed.
	/// </para>
	/// </summary>
	private readonly Dictionary<long, int> flushFailuresByTarget = new();

	/// <summary>
	/// The lock that keeps the failure counters and the error state updated **under the same lock**.
	/// Moving them independently creates an order in which "the counter is 0 but the error state is ON" —
	/// a read-only state that cannot recover on its own, because it blocks the very writes that would produce
	/// the release trigger (a successful flush).
	/// </summary>
	private readonly object flushFailureGate = new();

	/// <summary>An upper bound on the failure counters (a safety valve so that memory does not grow without
	/// bound when broken targets keep accumulating).</summary>
	private const int FlushFailureTrackLimit = 65536;

	/// <summary>The reason for the error state (null = healthy). Blocks new writes and creates and shows up in
	/// red in status.</summary>
	private volatile string? writeBackErrorState;
	/// <summary>When the error state was entered (for the status display).</summary>
	private DateTime writeBackErrorAt;

	/// <summary>
	/// The mark for "this close must flush synchronously" (synchronization heuristic b).
	/// <c>O_TRUNC</c> / <c>truncate(2)</c> is a destructive operation that **drops the old chunks in the database
	/// immediately**, so delaying the close of the new contents leaves "neither the old nor the new = a zero
	/// length leftover" behind after a crash (the same shape as the ext4 delayed allocation incident of 2009).
	/// <para>
	/// The key follows the same sign convention as <see cref="LockTargets"/> — **negative for an inode,
	/// positive for a data body** — and we push both for a truncated target. With only the inode side,
	/// **a close that arrives through a hard link sibling does not see the mark** (A-5); with only the data side
	/// we cannot follow a target whose data row itself disappears on <c>truncate 0</c>. Both are needed.
	/// </para>
	/// </summary>
	private readonly HashSet<long> syncOnClose = new();

	/// <summary>An upper bound on the synchronous-close marks (so that file descriptors truncated but never
	/// flushed do not grow the set without limit).</summary>
	private const int SyncOnCloseLimit = 4096;

	/// <summary>
	/// Whether a mark could not be pushed because the limit was reached. **While this is set, every close is
	/// upgraded to a synchronous one** (fail-safe). Dropping marks silently means "nobody notices that
	/// heuristic b has been disabled" = a data-safety mechanism disappearing quietly. We degrade to the
	/// write-back behaviour of the data-only phase (flush synchronously on close) even at a cost in throughput.
	/// </summary>
	private bool syncOnCloseOverflow;

	/// <summary>
	/// The number of unflushed entries that **could not be written out within the deadline** at unmount.
	/// <c>mount.pgfs</c> exits non-zero when this is not 0 (<c>fusermount3 -u</c> completes on the kernel side,
	/// so the filesystem cannot refuse it; the error log and the exit code are the last reporting paths).
	/// </summary>
	public int UnflushedAtShutdown { get; private set; }

	/// <summary>The reason for the error state (null = healthy). Surfaced in status through
	/// <c>{prefix}mounts.stats</c>.</summary>
	public string? WriteBackErrorState {
		get { return this.writeBackErrorState; }
	}

	/// <summary>
	/// Whether metadata write-back is effectively enabled. It requires <c>mount.write_back</c> (the data side),
	/// and **enabling it alone is treated as disabled with a warning** (the flush triggers for pending inodes
	/// ride on the same background loop as the data side, so without data write-back there is nobody to flush).
	/// </summary>
	private bool MetadataWriteBack {
		get {
			// Phase 1 of disabling live (intake closed) means "create no new pending entries" = effectively off.
			if (this.MetadataIntakeClosed) { return false; }
			if (!this.config.Mount.WriteBackMetadata) { return false; }
			if (this.config.Mount.WriteBack) { return true; }
			this.WarnMetadataWithoutWriteBack();
			return false;
		}
	}

	/// <summary>Warn once about <c>write_back_metadata</c> without <c>write_back</c>.</summary>
	private void WarnMetadataWithoutWriteBack() {
		if (this.metadataWriteBackWarned) { return; }
		this.metadataWriteBackWarned = true;
		Logger.Warning("mount.write_back_metadata requires mount.write_back = true. write_back is disabled, so metadata write-back is treated as disabled");
	}

	/// <summary>
	/// Check at startup that the metadata write-back settings fit together.
	/// A pending inode cannot be evicted by the LRU (it has no row in the database), so when
	/// <c>cache_max_entries</c> is smaller than <c>write_back_max_inodes</c>, **dropping every unpinned entry
	/// still does not bring the cache under its limit** = the child list cache is evicted continuously, which
	/// works directly against the purpose of metadata write-back.
	/// </summary>
	private void WarnMetadataCacheBudget() {
		if (!this.config.Mount.WriteBackMetadata) { return; }
		var cap = this.config.Mount.CacheMaxEntries;
		if (cap <= 0) { return; }
		if (cap >= this.config.Mount.WriteBackMaxInodes) { return; }
		Logger.Warning("mount.cache_max_entries (", cap, ") < mount.write_back_max_inodes (", this.config.Mount.WriteBackMaxInodes, "). Pending inodes cannot be evicted, so the cache will always overflow. Setting cache_max_entries to at least write_back_max_inodes is recommended");
	}

	/// <summary>
	/// Assert that the NSGate is held. This is the entry guard of the methods that require it (<c>...Locked</c>),
	/// so that a break in the lock hierarchy is detected at run time (getting it wrong hangs the whole mount,
	/// so this is enforced in code rather than in a comment).
	/// </summary>
	private void AssertNsGateHeld() {
		if (System.Threading.Monitor.IsEntered(this.nsGate)) { return; }
		throw new InvalidOperationException("metadata write-back: a path that requires the NSGate was called from outside the gate (lock hierarchy NSGate → DirtyFile.Gate → DB tx)");
	}

	// ------------------------------------------------------------------
	// Visibility (the pending resolution hooks of InodeCache)
	// ------------------------------------------------------------------

	/// <summary>
	/// The pending resolution hook of <see cref="InodeCache"/> (by id). Returns a pending inode that has no row
	/// in the database from the ledger. Returns null once it is materialized (Persisted), because it can then be
	/// read from the database.
	/// </summary>
	private Inode? FindPendingInode(long id) {
		return this.dirtyNamespace.FindPendingRow(id);
	}

	/// <summary>
	/// The pending resolution hook of <see cref="InodeCache"/> (by parent + name).
	/// Because of this, a path where the pin does not apply (a NOTIFY invalidate and the like) that drops the
	/// byId entry does not turn into "created it, yet ENOENT".
	/// </summary>
	private Inode? FindPendingChild(long parentId, string name) {
		return this.dirtyNamespace.FindPendingRow(parentId, name);
	}

	// ------------------------------------------------------------------
	// Going pending (create / mkdir / symlink)
	// ------------------------------------------------------------------

	/// <summary>
	/// Register an inode in the ledger and in <see cref="InodeCache"/> as pending, without touching the database.
	/// The id is handed out by <see cref="IdReservation"/> (for <c>{prefix}inode.id</c>) without a database round
	/// trip. <c>created_at</c> / <c>st_ctime</c> are **set explicitly to the time of the operation** (they are not
	/// left to the database DEFAULT, which would be the time of the flush).
	/// <para>
	/// <paramref name="nameConflict"/> distinguishes the meanings of a null return: **a name conflict has to be
	/// reported as EEXIST** (falling back to write-through would not collide with the pending sibling, which has
	/// no row in the database, so two inodes with the same name would be created, and a later flush would DELETE
	/// the row, the data row and every chunk of whichever one won).
	/// </para>
	/// </summary>
	private Inode? InsertInodePending(long parentId, string name, string uname, string gname, int stMode, long? dataId, string? linkTarget, bool exclusive, out bool nameConflict) {
		nameConflict = false;
		long id;
		try {
			id = this.inodeIdReservation.Rent();
		} catch (Exception ex) {
			Logger.Warning("metadata write-back: reserving an inode id failed, creating write-through instead: ", ex.Message);
			return null;
		}
		var when = Pg.UtcNow;
		var inode = new Inode {
			id = id,
			parent_id = parentId,
			name = name,
			uname = uname,
			gname = gname,
			st_mode = stMode,
			st_nlink = 1,
			st_size = 0,
			st_mtime = when,
			st_ctime = when,
			link_target = linkTarget,
			is_junction = false,
			data_id = dataId,
			xattr_names = Array.Empty<string>(),
			xattr_values = Array.Empty<byte[]>(),
			created_at = when,
			created_by = uname,
			updated_at = when,
			updated_by = uname,
			// There is no data row in the database yet, so the occupied byte count is 0 and counts as loaded
			// (this avoids a pointless SELECT).
			OccupiedBytes = 0,
			OccupiedBytesLoaded = true,
		};
		var audit = this.BuildPendingAudit(id, AuditOp.Create, parentId, name, new {
			mode = Convert.ToString(stMode, 8),
			kind = Api.AuditKind(stMode),
			uname,
			gname,
		}, when);
		// **Burn in at creation time whether this was born with O_EXCL** (the knob is not read again at flush
		// time). Reading it again would mean that an inode created under `defer` turns into "last-flush-wins may
		// delete the other one" the moment the knob is switched to `write_through` before the flush, breaking
		// the "only I created it" that was already returned to the application.
		var entry = this.dirtyNamespace.TryAdd(inode, audit, exclusive, out var addResult);
		if (entry == null) {
			// EEXIST **only for a name conflict**. Closed intake (phase 1 of the two-phase flip) falls back to
			// write-through.
			nameConflict = addResult == PendingAddResult.NameConflict;
			if (addResult == PendingAddResult.IntakeClosed) {
				// Throw the reserved id away (it only leaves a hole in the sequence, which is harmless).
				if (Logger.IsDebugEnabled) { Logger.Debug("metadata write-back: intake is closed, creating write-through instead name:", name); }
			}
			return null;
		}
		this.inodeCache.Put(inode, path: null!);
		// If a pending inode were evicted by the LRU it could not be read back, because it has no row in the
		// database (child lists are not pinned).
		this.inodeCache.Pin(id);
		this.inodeCache.InvalidateChildren(parentId);
		if (Logger.IsDebugEnabled) { Logger.Debug("metadata write-back: pending inode id:", id, " parent:", parentId, " name:", name); }
		this.dirtyNamespace.VerifyIndexIfTracing();
		this.ApplyInodeBackPressure();
		return inode;
	}

	// ------------------------------------------------------------------
	// Coalescing (attribute changes / rename against a pending inode)
	// ------------------------------------------------------------------

	/// <summary>
	/// Apply an overwrite of a pending inode atomically inside the ledger lock.
	/// If a flush is in progress, **wait for it to finish on the NSGate** and then return false (the caller falls
	/// back to write-through). Writing through without waiting would UPDATE a row that has not been INSERTed yet,
	/// affect 0 rows, and lose the change silently.
	/// </summary>
	private bool CoalescePending(long id, Action<Inode> mutate, PendingAudit? audit) {
		if (this.dirtyNamespace.IsEmpty) { return false; }
		var result = this.dirtyNamespace.TryCoalesce(id, mutate, audit);
		switch (result) {
			case CoalesceResult.Coalesced: return true;
			case CoalesceResult.Busy: this.WaitPendingSettled(id); return false;
			default: return false;
		}
	}

	/// <summary>
	/// Wait for a pending entry to "settle" (become materialized). If a flush is in progress, wait for it on the
	/// NSGate; if it failed and went back to Dirty, materialize it ourselves. This runs before falling back to
	/// write-through.
	/// </summary>
	private void WaitPendingSettled(long id) {
		lock (this.nsGate) {
			this.MaterializePendingLocked(id);
		}
	}

	/// <summary>chmod against a pending inode (zero transactions).</summary>
	private bool CoalesceMode(long id, int mode) {
		var when = Pg.UtcNow;
		var audit = this.BuildPendingAudit(id, AuditOp.Chmod, null, null, new { mode = Convert.ToString(mode, 8) }, when);
		return this.CoalescePending(id, inode => {
			inode.Mode = mode;
			inode.Ctime = when;
		}, audit);
	}

	/// <summary>chown against a pending inode (zero transactions).</summary>
	private bool CoalesceOwner(long id, string uname, string gname) {
		var when = Pg.UtcNow;
		var audit = this.BuildPendingAudit(id, AuditOp.Chown, null, null, new { uname, gname }, when);
		return this.CoalescePending(id, inode => {
			inode.UserName = uname;
			inode.GroupName = gname;
			inode.Ctime = when;
		}, audit);
	}

	/// <summary>
	/// An explicit size change against a pending inode (zero transactions). So that it does not disagree with
	/// the dirty data ledger either, <see cref="DirtyFile"/> is brought in line through
	/// <see cref="SyncDirtyFileSize"/> once the coalesce has succeeded (this takes two steps, because
	/// <see cref="DirtyFile.Gate"/> cannot be taken from inside the ledger lock).
	/// </summary>
	private bool CoalesceSize(long id, long length) {
		var when = Pg.UtcNow;
		var coalesced = this.CoalescePending(id, inode => {
			inode.Size = length;
			inode.Mtime = when;
			inode.Ctime = when;
		}, null);
		if (!coalesced) { return false; }
		this.SyncDirtyFileSize(id, length, when);
		return true;
	}

	/// <summary>utimens against a pending inode (zero transactions). There is no st_atime, so only mtime.</summary>
	private bool CoalesceTimestamps(long id, DateTime mtime) {
		var size = 0L;
		var coalesced = this.CoalescePending(id, inode => {
			inode.Mtime = mtime;
			size = inode.Size;
		}, null);
		if (!coalesced) { return false; }
		this.SyncDirtyFileSize(id, size, mtime);
		return true;
	}

	/// <summary>
	/// Rename of a pending inode (only when there is no existing target at the destination; zero transactions).
	/// It only rewires the name and the parent. Returns false when the destination name is already taken by
	/// another entry in the ledger (the caller falls back to write-through).
	/// Cleaning the path cache (byPath) is the responsibility of the OS layer (<c>InvalidatePrefix</c>); here we
	/// only drop the child lists.
	/// </summary>
	private bool CoalesceRename(long id, long newParentId, string newName) {
		if (this.dirtyNamespace.IsEmpty) { return false; }
		var entry = this.dirtyNamespace.Find(id);
		if (entry == null) { return false; }
		if (entry.State == PendingState.Persisted) { return false; }
		var when = Pg.UtcNow;
		var oldParentId = entry.Inode.ParentId;
		// Update the ctime and rewire the index in one interval (TryReindex does both inside the ledger lock).
		var audit = this.BuildPendingAudit(id, AuditOp.Rename, newParentId, newName, new {
			old_parent = oldParentId,
			new_parent = newParentId,
			new_name = newName,
		}, when);
		var touched = this.CoalescePending(id, inode => { inode.Ctime = when; }, null);
		if (!touched) { return false; }
		if (!this.dirtyNamespace.TryReindex(entry, newParentId, newName, audit)) {
			Logger.Warning("metadata write-back: the destination is taken in the ledger, falling back to a write-through rename id:", id, " name:", newName);
			this.WaitPendingSettled(id);
			return false;
		}
		this.inodeCache.InvalidateChildren(oldParentId);
		this.inodeCache.InvalidateChildren(newParentId);
		this.dirtyNamespace.VerifyIndexIfTracing();
		return true;
	}

	/// <summary>
	/// Reflect a size / mtime change of a pending inode in the dirty data ledger as well
	/// (so that the values of the inode row and the data row do not disagree at flush time).
	/// </summary>
	private void SyncDirtyFileSize(long inodeId, long size, DateTime when) {
		var inode = this.inodeCache.Get(inodeId);
		if (inode?.DataId is not long dataId) { return; }
		var file = this.dirtySet.Find(dataId);
		if (file == null) { return; }
		lock (file.Gate) {
			file.Size = size;
			// **If a truncate shrank the file, cut the written end down to it as well.** Without that, the
			// `GREATEST(st_size, @size)` of the flush pushes the shrunken size back up. Growing does not need
			// to be lifted here, because truncate itself writes st_size.
			file.WriteEnd = System.Math.Min(file.WriteEnd, size);
			file.Mtime = when;
		}
	}

	// ------------------------------------------------------------------
	// Pure cancellation (unlink / rmdir of a pending inode) / truncate 0
	// ------------------------------------------------------------------

	/// <summary>
	/// unlink / rmdir of a pending inode is a **pure cancellation** = nothing is written to the database.
	/// The audit keeps the create/delete pair in the orphan queue (without it, "create it, let it be read, and
	/// delete it, all within the interval" would become an evasion channel that leaves no audit trail at all).
	/// **Call this while holding the NSGate** (to rule out crossing a flush in progress).
	/// Returns false when it is not pending, or when it is a directory that still holds pending children (the
	/// caller then falls back to a write-through delete).
	/// </summary>
	private bool CancelPendingInode(Inode inode) {
		this.AssertNsGateHeld();
		var entry = this.dirtyNamespace.Find(inode.Id);
		if (entry == null) { return false; }
		if (entry.State == PendingState.Persisted) { return false; }
		// Forgetting a parent that still holds pending children would take the child tree down with it (a create
		// can cross between the caller's emptiness check and this point). Do not cancel when it is not empty.
		if (entry.Inode.IsDirectory && this.dirtyNamespace.HasPendingChildren(inode.Id)) {
			Logger.Warning("metadata write-back: refusing the cancellation because pending children exist id:", inode.Id, " name:", inode.Name);
			return false;
		}
		var when = Pg.UtcNow;
		// Commit the audit **before removing it from the ledger** (B-3). On failure the exception propagates as
		// it is and the cancellation does not happen = we never produce a state of "it is gone and there is no
		// trace of it".
		this.WriteCancelAudits(entry, when);
		this.DiscardPendingData(inode);
		// The target itself disappears, so drop the synchronous-close mark and the failure counter
		// (forgetting either leaves behind "a target that can never succeed again").
		this.ClearSyncOnClose(inode.Id, inode.DataId);
		this.ClearFlushFailure(inode.Id, inode.DataId);
		this.dirtyNamespace.Forget(inode.Id);
		this.dirtyNamespace.CountCancel();
		this.inodeCache.Unpin(inode.Id);
		this.inodeCache.Invalidate(inode.Id, path: null);
		this.inodeCache.InvalidateChildren(inode.ParentId);
		if (Logger.IsDebugEnabled) { Logger.Debug("metadata write-back: cancelled a pending inode id:", inode.Id, " name:", inode.Name); }
		return true;
	}

	/// <summary>
	/// Write the create/delete audit pair of a purely cancelled pending entry **to the database on the spot**
	/// (B-3).
	/// <para>
	/// Pushing it onto the in-memory orphan queue and leaving it to a background flush means it is lost to a
	/// <c>kill -9</c> or a PostgreSQL outage, and **`create → read → unlink` succeeds with no audit trace at
	/// all**. The design's audit section explicitly calls that an evasion channel to be closed, so this one
	/// case is committed synchronously. The number of rows is small (only files created and deleted within the
	/// interval), so the cost of doing it synchronously is effectively zero.
	/// </para>
	/// <para>
	/// **If it cannot be written, raise an exception** and fail the cancellation itself (the caller returns
	/// <c>-EIO</c>). This follows the same policy as the write-through audit — "if the audit cannot be kept, the
	/// operation does not happen either" — and it is consistent, because an unlink of a persisted inode fails in
	/// the same way when the database is down.
	/// **It has to be called before the entry is forgotten from the ledger** (removing it first would leave "it
	/// is gone and there is no trace of it" behind on failure).
	/// </para>
	/// </summary>
	/// <summary>
	/// Write the audit rows in **a transaction of their own** (including ensuring the partition).
	/// <para>
	/// <b>Ensuring the partition happens outside the transaction</b> — on Citus, **DDL inside a distributed
	/// write transaction is rejected**, and putting it inside the transaction makes **the whole mount freeze**
	/// waiting on <c>lock_timeout</c> (learned while building this).
	/// **Do not break this order.**
	/// </para>
	/// <para>
	/// The cancellation audit and the leftover orphan audit had **the same 11 lines** in two places, so they were
	/// merged into one. **The caller decides how to handle exceptions** — the former lets them propagate, while
	/// the latter puts the rows back on the queue and retries at the next opportunity.
	/// </para>
	/// </summary>
	private void WriteAuditsInOwnTx(List<PendingAudit> audits) {
		var months = new HashSet<DateTime>();
		foreach (var audit in audits) {
			months.Add(new DateTime(audit.When.Year, audit.When.Month, 1));
		}
		foreach (var month in months) {
			this.EnsureAuditPartition(month);
		}
		using var conn = this.NewConnection();
		using var tx = conn.BeginTransaction();
		this.WriteAuditRecordsInTx(conn, tx, audits);
		tx.Commit();
	}

	private void WriteCancelAudits(PendingInode entry, DateTime when) {
		if (!this.auditEnabled) { return; }
		var audits = this.dirtyNamespace.SnapshotAudits(entry);
		Api.AppendAudit(audits, this.BuildPendingAudit(entry.Inode.Id, AuditOp.Delete, entry.Inode.ParentId, entry.Inode.Name, null, when));
		if (audits.Count == 0) { return; }
		// Ensuring the partition happens **outside the transaction** (on Citus, DDL inside a distributed write
		// transaction is rejected, and putting it inside makes the whole mount freeze waiting on lock_timeout).
		this.WriteAuditsInOwnTx(audits);
		if (Logger.IsDebugEnabled) { Logger.Debug("metadata write-back: wrote ", audits.Count, " cancellation audit rows synchronously id:", entry.Inode.Id); }
	}

	/// <summary>Drop the unflushed data of a pending inode (memory only, because there is nothing in the
	/// database).</summary>
	private void DiscardPendingData(Inode inode) {
		if (inode.DataId is not long dataId) { return; }
		this.DiscardDirtyData(dataId);
	}

	/// <summary>
	/// <c>truncate 0</c> of a pending inode: it completes entirely in memory without touching the database
	/// (a pending inode has no chunks in the database at all — flushing the data implies materializing the
	/// inode). Returns false when it is not pending, or when the length is not 0 (the caller then materializes
	/// it and goes down the existing path).
	/// **This runs under the NSGate so that it does not cross a flush** (discarding the dirty data and updating
	/// the ledger take two steps, so a flush running in between would commit an intermediate state such as
	/// "size is 0, yet chunks get written").
	/// </summary>
	private bool TruncatePendingToZero(Inode inode, long newLength) {
		if (newLength != 0) { return false; }
		if (this.dirtyNamespace.IsEmpty) { return false; }
		if (this.dirtyNamespace.Find(inode.Id) == null) { return false; }
		lock (this.nsGate) {
			return this.TruncatePendingToZeroLocked(inode);
		}
	}

	/// <summary>The body of truncate 0, under the NSGate.</summary>
	private bool TruncatePendingToZeroLocked(Inode inode) {
		this.AssertNsGateHeld();
		var entry = this.dirtyNamespace.Find(inode.Id);
		if (entry == null) { return false; }
		if (entry.State == PendingState.Persisted) { return false; }
		var when = Pg.UtcNow;
		this.DiscardPendingData(inode);
		// **Do not drop data_id** (docs/design/data-id-lifecycle.md). The old implementation matched the
		// write-through TruncateData(0), which set it to null through ClearInodeDataId, but that write-through
		// path was changed to "do not delete the data row", so this one follows. Under the new invariant it is
		// normal for a flush to commit "an inode row with no data row in the database" (the row is created
		// lazily, and its absence means the contents are empty).
		if (!this.dirtyNamespace.TruncateToZero(entry, when)) { return false; }
		this.SetOccupiedBytes(entry.Inode, 0);
		this.SyncInodeFields(inode, 0, when);
		return true;
	}

	/// <summary>Remove a replaced / deleted inode from the ledger and from <see cref="InodeCache"/> (so that no
	/// ghost entry is left behind).</summary>
	private void ForgetReplaced(long id) {
		this.dirtyNamespace.Forget(id);
		this.inodeCache.Unpin(id);
		this.inodeCache.Invalidate(id, path: null);
	}

	/// <summary>
	/// After a write-through rename, rewire the (parent, name) index of a materialized entry that is still in
	/// the ledger to its new position (leaving it alone makes ghost names appear when ListChildren merges).
	/// If the destination is taken, remove it from the index instead (dropping it is better than leaving a
	/// ghost).
	/// </summary>
	private void ReindexPersisted(long id, long newParentId, string newName) {
		if (this.dirtyNamespace.IsEmpty) { return; }
		var entry = this.dirtyNamespace.Find(id);
		if (entry == null) { return; }
		if (this.dirtyNamespace.TryReindex(entry, newParentId, newName, null)) { return; }
		this.dirtyNamespace.Forget(id);
	}

	// ------------------------------------------------------------------
	// The entry points of materialization
	// ------------------------------------------------------------------

	/// <summary>
	/// Materialize a target (and its pending ancestors) in the database before a write-through operation that
	/// refers to a pending entry. It is a no-op when the ledger is empty or the target is not pending.
	/// **A failure raises an exception** and is passed straight back to the caller (a write-through that refers
	/// to an inode which has not been written must not be treated as successful).
	/// </summary>
	private void MaterializePending(params long[] inodeIds) {
		if (this.dirtyNamespace.IsEmpty) { return; }
		lock (this.nsGate) {
			foreach (var id in inodeIds) {
				this.MaterializePendingLocked(id);
			}
		}
	}

	/// <summary>
	/// Materialize a pending inode in one transaction: "pending ancestors (parent → child) → the inode itself →
	/// the data row → the chunks → the audit".
	/// **Call this while holding the NSGate** (enforced by <see cref="AssertNsGateHeld"/>). A no-op when it is
	/// not pending.
	/// </summary>
	private void MaterializePendingLocked(long id) {
		this.AssertNsGateHeld();
		var entry = this.dirtyNamespace.Find(id);
		if (entry == null) { return; }
		if (entry.State == PendingState.Persisted) { return; }
		this.FlushPendingEntry(entry);
	}

	/// <summary>
	/// Materialize the pending sibling that occupies (parent, name) (**call this while holding the NSGate**).
	/// It is required right before a write-through create or an O_EXCL create: a pending sibling has no row in
	/// the database, so <c>ON CONFLICT</c> does not fire, **two inodes with the same name** are created, and a
	/// later flush DELETEs the row, the data row and every chunk of whichever one won (a destruction that cannot
	/// happen at all with write-through).
	/// </summary>
	private void MaterializePendingChildLocked(long parentId, string name) {
		this.AssertNsGateHeld();
		if (this.dirtyNamespace.IsEmpty) { return; }
		var entry = this.dirtyNamespace.Find(parentId, name);
		if (entry == null) { return; }
		if (entry.State == PendingState.Persisted) { return; }
		this.MaterializePendingLocked(entry.Inode.Id);
	}

	/// <summary>
	/// The preparation for an operation that creates a new name write-through (hardlink and the like):
	/// materialize the given inodes **together with the pending sibling that occupies that name**.
	/// </summary>
	private void MaterializePendingForCreate(long parentId, string name, params long[] inodeIds) {
		if (this.dirtyNamespace.IsEmpty) { return; }
		lock (this.nsGate) {
			foreach (var id in inodeIds) {
				this.MaterializePendingLocked(id);
			}
			this.MaterializePendingChildLocked(parentId, name);
		}
	}

	/// <summary>Whether this inode is a pending entry that has not been materialized yet.</summary>
	private bool IsPendingInode(long id) {
		if (this.dirtyNamespace.IsEmpty) { return false; }
		var entry = this.dirtyNamespace.Find(id);
		if (entry == null) { return false; }
		return entry.State != PendingState.Persisted;
	}

	/// <summary>
	/// The preparation for rename-over-existing (synchronization heuristic a). Commit the unflushed part of the
	/// source before the replacement removes the old target (the contract of the data write-back phase).
	/// <para>
	/// **A pending source is not flushed here**, however — <see cref="ReplacePendingOverTargetLocked"/> performs
	/// "deleting the target + materializing the source + the dirty data + the audit" in a **single transaction**,
	/// so materializing here would split it into two (there is no window for loss, because the old target
	/// survives, but a moment in which a zero length temporary is visible appears and one more transaction is
	/// spent).
	/// </para>
	/// </summary>
	public void PrepareRenameReplace(Inode source) {
		if (this.MetadataWriteBack && this.IsPendingInode(source.Id)) { return; }
		this.FlushInode(source);
	}

	/// <summary>
	/// Synchronization heuristic (a): perform a rename-over-existing of a pending source synchronously, as
	/// **a single transaction of "deleting the target + materializing the source + the dirty data + the audit"**.
	/// **Call this while holding the NSGate**. When it returns false the caller falls back to the conventional
	/// "materialize → write-through rename" (two transactions).
	/// <para>
	/// How it works: a pending entry has no row in the database, so a "rename" is nothing more than **the choice
	/// of which name to write at INSERT time**. Rewiring it to (newParent, newName) in the ledger first and then
	/// flushing synchronously turns the name conflict resolution of the flush transaction (file/file = an
	/// explicit DELETE plus an INSERT) into an atomic replacement as it is.
	/// </para>
	/// <para>
	/// Replacing a directory needs decisions such as "a non-empty directory must not be deleted along with it"
	/// and "dir/dir adopts the existing id", so it is out of scope here (what an editor save, <c>sed -i</c>,
	/// dpkg and rsync hit is only the replacement of a file).
	/// </para>
	/// </summary>
	private bool ReplacePendingOverTargetLocked(long id, long newParentId, string newName, Inode replaceTarget) {
		this.AssertNsGateHeld();
		var entry = this.dirtyNamespace.Find(id);
		if (entry == null) { return false; }
		if (entry.State != PendingState.Dirty) { return false; }
		if (entry.Inode.IsDirectory || replaceTarget.IsDirectory) { return false; }
		var oldParentId = entry.Inode.ParentId;
		var oldName = entry.Inode.Name;
		// Rewire it to the destination name in the ledger (zero transactions up to here). If the destination is
		// taken by a pending sibling, CoalesceRename returns false and we leave it to the existing path.
		if (!this.CoalesceRename(id, newParentId, newName)) { return false; }
		try {
			this.FlushPendingEntry(entry, expectedReplaceId: replaceTarget.Id);
		} catch (Exception) {
			// If the replacement failed, put the ledger back under the old name (do not leave behind a state in
			// which "the rename only succeeded in memory" — a pending entry hides the target on the database
			// side when ListChildren merges, so leaving it alone turns into "the rename returned -EIO, yet ls
			// shows it as moved").
			this.dirtyNamespace.TryReindex(entry, oldParentId, oldName, null);
			this.inodeCache.InvalidateChildren(oldParentId);
			this.inodeCache.InvalidateChildren(newParentId);
			throw;
		}
		if (Logger.IsDebugEnabled) { Logger.Debug("metadata write-back: materialized a rename-over-existing in a single tx id:", id, " -> parent:", newParentId, " name:", newName, " replaced:", replaceTarget.Id); }
		return true;
	}

	/// <summary>Materialize the given inode if it is pending (the step before fsync / close / a data
	/// flush).</summary>
	private void FlushPendingTree(long id) {
		if (this.dirtyNamespace.IsEmpty) { return; }
		// A coarse check outside the gate (a false positive is re-checked by MaterializePendingLocked below and
		// becomes a no-op).
		if (this.dirtyNamespace.Find(id) == null) { return; }
		lock (this.nsGate) {
			this.MaterializePendingLocked(id);
		}
	}

	/// <summary>A pending flush that swallows failures (for the background loop / back-pressure /
	/// FlushAll).</summary>
	private void TryFlushPending(long id) {
		try {
			this.FlushPendingTree(id);
		} catch (Exception ex) {
			Logger.Warning("metadata write-back: the flush failed (it will be retried at the next opportunity) inode:", id, " ", ex.Message);
		}
	}

	/// <summary>Materialize every pending inode (unmount / Dispose / disabling live).</summary>
	private void FlushAllPendingInodes(DateTime? deadline = null) {
		if (this.dirtyNamespace.IsEmpty) { return; }
		foreach (var id in this.dirtyNamespace.PendingIdsOldestFirst()) {
			if (this.DeadlineReached(deadline)) { return; }
			this.TryFlushPending(id);
		}
	}

	/// <summary>
	/// Apply a live change of <c>write_back_metadata</c> in **two phases**:
	/// (1) stop the intake of new pending entries (<see cref="metadataIntakeClosed"/>) → (2) write out every
	/// pending entry held through <see cref="Api.FlushAll"/> → (3) switch the mode.
	/// <para>
	/// In a single phase (dropping the mode first and then calling FlushAll), a create running concurrently with
	/// the flip produces a state of "the path that makes entries pending is already dead, yet it was registered
	/// in the ledger as pending", and there is nobody left to flush it (it does not appear in the database until
	/// the next fsync or unmount).
	/// </para>
	/// </summary>
	private void ApplyMetadataWriteBackLive(bool enabled) {
		if (enabled) {
			this.config.Mount.WriteBackMetadata = true;
			this.dirtyNamespace.SetIntakeClosed(false);
			return;
		}
		// (1) Stop the intake — from this point on new creates flow write-through (the ledger stays alive).
		// **It is set under the ledger lock**, so there is no window between it and the test in TryAdd (B-9).
		this.dirtyNamespace.SetIntakeClosed(true);
		// **A change of the effective mode is written to {prefix}mounts on the spot** (B-9). The stats only
		// arrive with the heartbeat (30 seconds), so without this, **status tells the lie "on" for the whole
		// duration of the flip** (the longer the flip, the more you want that information, and the less you can
		// see it). The same reasoning as B-5.
		this.PublishStatsNow();
		try {
			// (2) Write out the pending entries held (FlushAll turns failures into warnings).
			this.FlushAll();
		} finally {
			// (3) Switch the mode. The intake flag is cleared after config has become false (the reverse order
			// opens a window).
			this.config.Mount.WriteBackMetadata = false;
			this.dirtyNamespace.SetIntakeClosed(false);
			this.PublishStatsNow();
		}
	}

	/// <summary>
	/// When the number of pending inodes exceeds <c>mount.write_back_max_inodes</c>, **block the creating thread
	/// until the flush catches up** (back-pressure; error floor 4).
	/// <para>
	/// Returning after a single one-shot pass would keep breaking through the limit in a situation where the
	/// flush keeps failing, so we repeat until we are under the limit or <c>mount.write_back_flush_timeout_ms</c>
	/// is used up. **We never wait indefinitely** (an indefinite block cannot be told apart from the whole mount
	/// hanging) — passing the deadline logs a warning and continues, and a permanent failure is picked up by the
	/// error state of <see cref="NoteFlushFailure"/>.
	/// A 40P01 distributed deadlock is retried with exponential backoff on the flush side
	/// (<see cref="FlushPendingChain"/>).
	/// </para>
	/// </summary>
	private void ApplyInodeBackPressure() {
		var limit = this.config.Mount.WriteBackMaxInodes;
		if (limit <= 0) { return; }
		if (this.dirtyNamespace.PendingCount <= limit) { return; }
		var target = limit - limit / 8;
		var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, this.config.Mount.WriteBackFlushTimeoutMs));
		for (var round = 1; ; ++round) {
			var before = this.dirtyNamespace.PendingCount;
			foreach (var id in this.dirtyNamespace.PendingIdsOldestFirst()) {
				if (this.dirtyNamespace.PendingCount <= target) { break; }
				// The deadline is checked **inside** a pass as well. Checking it only after firing at every
				// entry could block for hours in round 1 alone when each failure is slow (a pending limit of
				// 4096 × a lock_timeout of 5 seconds).
				if (this.DeadlineReached(deadline)) { break; }
				this.TryFlushPending(id);
			}
			var after = this.dirtyNamespace.PendingCount;
			if (after <= limit) { return; }
			if (this.DeadlineReached(deadline)) {
				Logger.Warning("metadata write-back: the back-pressure did not clear within the deadline (", this.config.Mount.WriteBackFlushTimeoutMs, " ms). Continuing above the limit pending:", after, " limit:", limit);
				return;
			}
			// If we made progress, go around again immediately. No reduction at all = the flush is failing, so
			// wait before retrying.
			if (after < before) { continue; }
			Logger.Warning("metadata write-back: pending inodes are still above the limit ", limit, " (", after, "). Waiting for a successful flush (round ", round, ")");
			Api.SleepBeforeFlushRetry(round);
		}
	}

	// ------------------------------------------------------------------
	// Error floors 1 and 3: the error state and downgrading close to synchronous
	// ------------------------------------------------------------------

	/// <summary>
	/// Count flush failures **per target**, and enter the error state when the same target fails
	/// <see cref="FlushFailureStateThreshold"/> times in a row (this blocks new writes and creates, and shows up
	/// in red in <c>pgfsctl status</c> through the heartbeat). <paramref name="target"/> is negative for an
	/// inode and positive for a data body.
	/// </summary>
	private void NoteFlushFailure(long target, string reason) {
		var entered = false;
		lock (this.flushFailureGate) {
			var count = this.BumpFlushFailure(target);
			if (count >= Api.FlushFailureStateThreshold && this.writeBackErrorState == null) {
				this.writeBackErrorAt = DateTime.UtcNow;
				this.writeBackErrorState = reason;
				entered = true;
				Logger.Error("write-back: the flush of the same target (", target, ") failed ", count, " times in a row, putting the mount into the error state (new writes and creates are blocked): ", reason);
			}
		}
		if (entered) { this.PublishStatsNow(); }
	}

	/// <summary>
	/// Write a state transition to <c>{prefix}mounts</c> **on the spot** (B-5 / B-9).
	/// The heartbeat runs every 15 seconds by default, so making a transition wait for it means
	/// <c>pgfsctl status</c> shows **a green or a red that is up to one period stale**.
	/// **Call this outside the lock** (doing database I/O under <see cref="flushFailureGate"/> would stop the
	/// failure counter updates as well once the database is congested).
	/// <para>
	/// **In an outage where nothing at all can be written to the database, this cannot be written either.**
	/// That status then shows a stale value is
	/// unavoidable, so **status also prints the age of the heartbeat in seconds** to let the reader notice.
	/// </para>
	/// </summary>
	private void PublishStatsNow() {
		if (!this.registered) { return; }
		this.WriteHeartbeat();
	}

	/// <summary>Increment and return the consecutive failure count of a target. Call this while holding
	/// <see cref="flushFailureGate"/>.</summary>
	private int BumpFlushFailure(long target) {
		if (this.flushFailuresByTarget.TryGetValue(target, out var current)) {
			var next = current + 1;
			this.flushFailuresByTarget[target] = next;
			return next;
		}
		if (this.flushFailuresByTarget.Count >= Api.FlushFailureTrackLimit) {
			// By the time we get here the error state must already have been entered (there is certainly a
			// target that reached the threshold of 5). Missing a count does not change the outcome, so we give
			// up counting in order to protect memory.
			Logger.Warning("write-back: the number of tracked flush failures reached the limit of ", Api.FlushFailureTrackLimit, ", so new targets are not counted target:", target);
			return 1;
		}
		this.flushFailuresByTarget[target] = 1;
		return 1;
	}

	/// <summary>When the error state was entered (ISO 8601). null when healthy (for the stats snapshot).</summary>
	private string? WriteBackErrorSinceText() {
		if (this.writeBackErrorState == null) { return null; }
		return this.writeBackErrorAt.ToString("o");
	}

	/// <summary>
	/// A successful flush clears that target's consecutive failure counter, and **once no target is at the
	/// threshold any more** the error state is released. The release condition is derived from the state of the
	/// counters themselves, so no order exists in which "the counters are empty yet the error state remains".
	/// </summary>
	private void NoteFlushSuccess(long target) {
		var cleared = false;
		lock (this.flushFailureGate) {
			this.flushFailuresByTarget.Remove(target);
			cleared = this.ReevaluateErrorStateLocked("every failing target has recovered");
		}
		if (cleared) { this.PublishStatsNow(); }
	}

	/// <summary>
	/// Drop the failure counter of a target **when the flush target itself disappears** (a deletion, a pure
	/// cancellation, a discard).
	/// <para>
	/// Forgetting this leaves **the counter of a target that can never succeed again sitting at the threshold,
	/// and the error state is never released**. This was actually hit: after a pending entry latched by a name
	/// conflict under `defer` was `unlink`ed, new creates stayed at `-EIO` (found during hands-on verification
	/// on Windows). In the sense of "drop the mark once the target is gone" it has the same nature as the
	/// synchronous-close mark (<see cref="ClearSyncOnClose"/>).
	/// </para>
	/// </summary>
	private void ClearFlushFailure(long inodeId, long? dataId) {
		var cleared = false;
		lock (this.flushFailureGate) {
			this.flushFailuresByTarget.Remove(-inodeId);
			if (dataId is long id) { this.flushFailuresByTarget.Remove(id); }
			cleared = this.ReevaluateErrorStateLocked("the failing target is gone");
		}
		if (cleared) { this.PublishStatsNow(); }
	}

	/// <summary>
	/// Bring the error state in line with "release it when no target is at the threshold".
	/// Call this while holding <see cref="flushFailureGate"/>.
	/// </summary>
	/// <returns>true when it was released (the caller rewrites the heartbeat **outside the lock**).</returns>
	private bool ReevaluateErrorStateLocked(string reason) {
		if (this.writeBackErrorState == null) { return false; }
		foreach (var kv in this.flushFailuresByTarget) {
			if (kv.Value >= Api.FlushFailureStateThreshold) { return false; }
		}
		Logger.Information("write-back: releasing the error state because ", reason, " (it lasted ", (long)(DateTime.UtcNow - this.writeBackErrorAt).TotalSeconds, " seconds)");
		this.writeBackErrorState = null;
		return true;
	}

	/// <summary>
	/// Refuse new writes and creates while in the error state (error floor 3).
	/// Accepting them would only keep growing the data for which "an operation that reported success can never
	/// be flushed".
	/// </summary>
	private void ThrowIfWriteBackErrorState() {
		var reason = this.writeBackErrorState;
		if (reason == null) { return; }
		// Once write-back is turned off, writes go straight to the database, so "data that cannot be flushed"
		// no longer grows. Continuing to block here would produce a read-only filesystem with no way to recover.
		if (!this.config.Mount.WriteBack) { return; }
		throw new System.IO.IOException($"write-back: refusing new writes and creates because flushes keep failing: {reason}");
	}

	/// <summary>
	/// Whether the setting makes an <c>O_EXCL</c> create pending (`defer`).
	/// The default is `write_through`, because `defer` **loses the cross-client exclusion**.
	/// </summary>
	private bool ExclusiveCreateDeferred {
		get { return string.Equals(this.config.Mount.WriteBackMetadataExclusiveCreate, "defer", StringComparison.OrdinalIgnoreCase); }
	}

	/// <summary>
	/// Warn when `defer` is selected **while other mounts are live**. `defer` loses the cross-client
	/// <c>O_EXCL</c> exclusion, so it must not be chosen where several clients use the same filesystem.
	/// <para>
	/// **It does not refuse** — a leftover registration row (one that was not DELETEd after an abnormal exit)
	/// would otherwise stop a perfectly legitimate single-client setup. The material for the decision
	/// (`{prefix}mounts`) belongs to Core, so this lives here in order to emit the same warning whether the
	/// mount was started through FUSE or through Dokan.
	/// </para>
	/// </summary>
	private void WarnExclusiveCreateDefer() {
		if (!this.ExclusiveCreateDeferred) { return; }
		if (!this.config.Mount.WriteBackMetadata) { return; }
		var others = this.CountOtherLiveMounts();
		if (others <= 0) { return; }
		Logger.Warning(
			"mount.write_back_metadata_exclusive_create = defer, but there are ", others,
			" other live mounts. defer loses the cross-client O_EXCL / CREATE_NEW exclusion (an exclusive create from two clients can both succeed), ",
			"so tools that use lock files may both conclude that they took it. Switch back to write_through if you use several clients"
		);
	}

	/// <summary>
	/// The number of live mounts other than this one. Returns 0 when it cannot be obtained (this query exists
	/// only for the warning, so it must not fail and prevent startup).
	/// </summary>
	private int CountOtherLiveMounts() {
		try {
			return Pg.Query<int>(
				this.connectionString,
				$"SELECT count(*)::int FROM {this.QualifiedTable("mounts")} WHERE mount_id <> @mount_id",
				new { mount_id = this.mountId }
			).FirstOrDefault();
		} catch (Exception ex) {
			if (Logger.IsDebugEnabled) { Logger.Debug("the mounts query for the defer warning failed (ignored): ", ex.Message); }
			return 0;
		}
	}

	/// <summary>
	/// Whether this inode is **pending-born** (created by this mount and not yet materialized in the database).
	/// false = persisted = a body that already exists in the database.
	/// </summary>
	private bool IsPendingBorn(long inodeId) {
		if (!this.MetadataWriteBack) { return false; }
		var entry = this.dirtyNamespace.Find(inodeId);
		if (entry == null) { return false; }
		return entry.State != PendingState.Persisted;
	}

	/// <summary>
	/// Refuse **destructive operations** while in the error state (the complement of error floor 3; B-7).
	/// <para>
	/// The error state exists to "stop new writes and creates so that data which cannot be flushed does not pile
	/// up", but **deletes, truncates and replacing renames used to pass straight through**. The result is the
	/// worst possible state: **"new data is not accepted, yet old data keeps disappearing"**.
	/// </para>
	/// <para>
	/// What is stopped is **only the operations that destroy a persisted body**. **Operations that merely
	/// discard this mount's own unflushed state (deleting a pending inode, truncating a pending entry) always
	/// pass** — they do not touch the database at all, so they cannot make the situation worse; if anything they
	/// reduce the pending count. **The recovery path of B-1 (unlinking the loser of a conflict) falls in here**,
	/// so the framing is not "make unlink an exception" but "operations that discard a pending entry were never
	/// destructive in the first place".
	/// </para>
	/// </summary>
	private void ThrowIfErrorStateBlocksDestroy(Inode target, string what) {
		if (this.CanDestroy(target, out var reason)) { return; }
		throw new System.IO.IOException($"write-back: refusing {what} because flushes keep failing (so that an existing body is not destroyed; unflushed pending entries can still be deleted): {reason}");
	}

	/// <summary>
	/// Return **without any side effect** whether an operation that **destroys this target (a delete, a truncate,
	/// a replacing rename) can be accepted right now** (the predicate form of the B-7 decision).
	/// <para>
	/// **The OS adapters use this to refuse before acting.** Dokan's <c>Cleanup</c> is <c>void</c> and cannot
	/// return the exception of <see cref="Api.DeleteInode"/> to its caller, so it would **report success even
	/// though nothing was deleted** (the same lie as fail-open). Consult this and refuse while a return value
	/// can still be given, as in `DeleteFile` / `SetEndOfFile` / `MoveFile`.
	/// </para>
	/// <para>
	/// **The adapters must not assemble the same decision themselves.** "Whether it is pending"
	/// (<see cref="IsPendingBorn"/>) is internal state of Core; all that is visible from outside is
	/// <see cref="WriteBackErrorState"/>. Deciding on the error state alone **also stops deleting pending
	/// entries and thereby blocks the recovery path of B-1**.
	/// </para>
	/// </summary>
	/// <param name="reason">The reason when refusing (null when allowing).</param>
	public bool CanDestroy(Inode target, out string? reason) {
		reason = null;
		var state = this.writeBackErrorState;
		if (state == null) { return true; }
		// Once write-back is turned off, writes go straight to the database, so the reason to stop disappears
		// as well (the same escape hatch as ThrowIfWriteBackErrorState).
		if (!this.config.Mount.WriteBack) { return true; }
		// pending = not in the database yet = discarding it does not reduce what we hold.
		if (this.IsPendingBorn(target.Id)) { return true; }
		reason = state;
		return false;
	}

	/// <summary>
	/// Put the "close must flush synchronously" mark on a target whose **old chunks in the database were
	/// discarded** by a <c>truncate</c> (synchronization heuristic b). It is pushed for both the inode and the
	/// data body (so that it also applies to a close arriving through a sibling).
	/// The mark is only dropped when <see cref="Api.FlushInode"/> **actually wrote out the unflushed part**.
	/// </summary>
	private void MarkSyncOnClose(Inode inode) {
		if (!this.MetadataWriteBack) { return; }
		lock (this.syncOnClose) {
			this.AddSyncOnCloseKey(-inode.Id);
			if (inode.DataId is long dataId) { this.AddSyncOnCloseKey(dataId); }
		}
	}

	/// <summary>Push one mark. Call this while holding <see cref="syncOnClose"/>.</summary>
	private void AddSyncOnCloseKey(long key) {
		if (this.syncOnClose.Contains(key)) { return; }
		if (this.syncOnClose.Count >= Api.SyncOnCloseLimit) {
			if (!this.syncOnCloseOverflow) {
				Logger.Error("metadata write-back: the synchronous-close marks reached the limit of ", Api.SyncOnCloseLimit, ". To avoid missing any, **every close is upgraded to a synchronous flush** until we are back under the limit");
			}
			this.syncOnCloseOverflow = true;
			return;
		}
		this.syncOnClose.Add(key);
	}

	/// <summary>
	/// Drop a synchronous-close mark (the flush wrote it out / the target disappeared / a pending entry was
	/// cancelled). **Call this on disappearance and cancellation too** — forgetting it makes the marks grow
	/// monotonically until they reach the limit and heuristic b degrades (inode ids are never reused, so the
	/// set never shrinks on its own).
	/// </summary>
	private void ClearSyncOnClose(long inodeId, long? dataId) {
		lock (this.syncOnClose) {
			this.syncOnClose.Remove(-inodeId);
			if (dataId is long id) { this.syncOnClose.Remove(id); }
			if (!this.syncOnCloseOverflow) { return; }
			if (this.syncOnClose.Count >= Api.SyncOnCloseLimit) { return; }
			Logger.Information("metadata write-back: the synchronous-close marks are back under the limit, returning to the normal close-no-flush (", this.syncOnClose.Count, "/", Api.SyncOnCloseLimit, ")");
			this.syncOnCloseOverflow = false;
		}
	}

	/// <summary>
	/// Whether <c>close(2)</c> has to be upgraded from "mark only" to "flush synchronously and report". Only
	/// three cases: (1) the mount is in the error state (2) this inode or its data has latched a flush failure
	/// (error floor 1) (3) an <c>O_TRUNC</c> / <c>truncate(2)</c> discarded the old chunks (heuristic b).
	/// </summary>
	private bool RequiresSyncClose(Inode inode) {
		if (this.writeBackErrorState != null) { return true; }
		lock (this.syncOnClose) {
			// While the marks have overflowed we cannot say "no mark = safe", so we make everything synchronous.
			if (this.syncOnCloseOverflow) { return true; }
			if (this.syncOnClose.Contains(-inode.Id)) { return true; }
			if (inode.DataId is long markedDataId && this.syncOnClose.Contains(markedDataId)) { return true; }
		}
		if (this.dirtyNamespace.HasErrorLatch(inode.Id)) { return true; }
		if (inode.DataId is not long dataId) { return false; }
		var file = this.dirtySet.Find(dataId);
		if (file == null) { return false; }
		return file.Error != null;
	}

	// ------------------------------------------------------------------
	// Error floor 2: unmount retries with a deadline and enumerates what is lost
	// ------------------------------------------------------------------

	/// <summary>
	/// The last line of defence at unmount / Dispose. Retry <see cref="Api.FlushAll"/> with exponential backoff
	/// up to a deadline of <c>mount.write_back_flush_timeout_ms</c>, and if anything is still left, enumerate
	/// **what will be lost** in the error log and record the count in <see cref="UnflushedAtShutdown"/>.
	/// <para>
	/// The design says "refuse with the equivalent of EBUSY when anything failed to flush", but
	/// <c>fusermount3 -u</c> and <c>umount(8)</c> **complete on the kernel side**, so a FUSE daemon cannot
	/// refuse them (believing it refused and waiting forever would produce "a mount that can never be
	/// unmounted"). So the reporting path is secured in the shape of "persist up to a deadline + enumerate what
	/// is lost + make the process exit code non-zero".
	/// </para>
	/// </summary>
	private void FlushAllForShutdown() {
		var timeout = Math.Max(0, this.config.Mount.WriteBackFlushTimeoutMs);
		var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
		for (var round = 1; ; ++round) {
			this.FlushAll(deadline);
			var remaining = this.UnflushedCount();
			if (remaining == 0) { return; }
			if (this.DeadlineReached(deadline)) {
				// Even when we leave because of an abandon request (B-12), we finish **only after emitting the
				// loss report** (the second signal means "stop waiting", not "discard it silently").
				if (this.flushAbandoned) { Logger.Warning("write-back: an abandon request was received, stopping the flush at unmount (", remaining, " left)"); }
				this.ReportUnflushedLoss(remaining);
				return;
			}
			Logger.Warning("write-back: ", remaining, " entries are still unflushed at unmount. Retrying (round ", round, " / deadline ", timeout, " ms)");
			Api.SleepBeforeFlushRetry(round);
		}
	}

	/// <summary>The total number of unflushed entries (pending inodes + dirty files + unwritten audit
	/// rows).</summary>
	private int UnflushedCount() {
		return this.dirtyNamespace.PendingCount + this.dirtySet.DirtyFileCount + this.dirtyNamespace.QueuedAuditCount;
	}

	/// <summary>
	/// Enumerate in the error log what could not be written out within the deadline (**never discard it
	/// silently**). With metadata write-back on, "a whole file disappears behind a single log line", so we print
	/// the id, the parent, the name and the most recent error as well.
	/// </summary>
	private void ReportUnflushedLoss(int remaining) {
		this.UnflushedAtShutdown = remaining;
		// **With the default way of starting the daemon, neither the log nor the exit code reaches anyone**, so
		// we also leave a record on the database side (B-2). The tombstone (not deleting {prefix}mounts) is done
		// by the deregistration in Dispose.
		this.WriteLossAudit(remaining);
		Logger.Error("write-back: ", remaining, " entries could not be flushed within the deadline. The following will be lost (remounting does not bring them back)");
		foreach (var line in this.DescribeLostPending()) {
			Logger.Error("  ", line);
		}
		foreach (var line in this.DescribeLostDirty()) {
			Logger.Error("  ", line);
		}
		var audits = this.dirtyNamespace.QueuedAuditCount;
		if (audits > 0) {
			Logger.Error("  audit rows that could not be written: ", audits);
		}
	}

	/// <summary>
	/// Enumerate the pending inodes that will be lost, **with their paths** (B-6). For the part that is cut off,
	/// **do not stop at "and N others"; print the breakdown (dir / file / bytes)** — a rounded number can
	/// neither be reconciled nor corrected by whoever receives it.
	/// <para>
	/// The path is assembled by <see cref="InodeCache.TryGetPath"/>. A pending inode is pinned, so it and its
	/// pending ancestors are certainly in the cache, but **it cannot be resolved when a persisted ancestor has
	/// been dropped by the LRU**. In that case we print the form <c>?/name</c> so that **it is visible that the
	/// path could not be reconstructed** (saying it could not be resolved is better than printing a plausible
	/// but false path).
	/// </para>
	/// </summary>
	private List<string> DescribeLostPending() {
		var summary = this.dirtyNamespace.SnapshotPendingLoss(Api.LossReportLimit);
		var lines = new List<string>();
		if (summary.Total == 0) { return lines; }
		foreach (var item in summary.Sample) {
			var row = item.Row;
			var kind = "file";
			if (row.IsDirectory) { kind = "dir"; }
			lines.Add($"lost pending {kind}: {this.DescribePath(row)} (id:{row.Id} size:{row.Size} state:{item.State} error:{item.Error ?? "(none)"})");
		}
		if (summary.Total > summary.Sample.Count) {
			lines.Add($"… and {summary.Total - summary.Sample.Count} more ({summary.Total} pending in total = {summary.Dirs} dir / {summary.Files} file, {summary.Bytes} logical bytes)");
		}
		return lines;
	}

	/// <summary>Enumerate the dirty data that will be lost (B-6). Cutting off is handled the same way as on the
	/// pending side.</summary>
	private List<string> DescribeLostDirty() {
		var summary = this.dirtySet.SnapshotDirtyLoss(Api.LossReportLimit);
		var lines = new List<string>();
		if (summary.Total == 0) { return lines; }
		foreach (var file in summary.Sample) {
			var path = "?";
			var inode = this.inodeCache.Get(file.InodeId);
			if (inode != null) { path = this.DescribePath(inode); }
			lines.Add($"lost dirty data: {path} (data_id:{file.DataId} inode:{file.InodeId} chunks:{file.Chunks.Count} size:{file.Size} error:{file.Error ?? "(none)"})");
		}
		if (summary.Total > summary.Sample.Count) {
			lines.Add($"… and {summary.Total - summary.Sample.Count} more ({summary.Total} dirty files / {summary.Chunks} chunks, {summary.Bytes} logical bytes in total)");
		}
		return lines;
	}

	/// <summary>The path for the loss report. <c>?/name</c> when it cannot be resolved (we do not print a false
	/// path).</summary>
	private string DescribePath(Inode row) {
		if (this.inodeCache.TryGetPath(row.Id, out var path) && !string.IsNullOrEmpty(path)) { return path; }
		return $"?/{row.Name}";
	}

	/// <summary>The maximum number of entries enumerated in the loss report (a cut-off so that the log does not
	/// overflow).</summary>
	private const int LossReportLimit = 32;

	// ------------------------------------------------------------------
	// B-2: keep the loss on the database side (with the default way of starting the daemon, neither the log
	// nor the exit code reaches anyone)
	// ------------------------------------------------------------------

	/// <summary>
	/// Leave one row about the loss in <c>{prefix}audit</c> (<c>op = writeback_loss</c>).
	/// This exists because **with the default way of starting the daemon the loss reaches nobody**: the daemon
	/// discards stdout and stderr before exiting, so the error log disappears, and exit 4 does not reach the
	/// parent either (it already returned 0 and exited once the mount succeeded).
	/// <para>
	/// Nothing is written when auditing is disabled. The only remaining clue is then the tombstone on the
	/// <c>{prefix}mounts</c> side written by <see cref="MarkMountLoss"/>.
	/// </para>
	/// </summary>
	private void WriteLossAudit(int remaining) {
		if (!this.auditEnabled) { return; }
		try {
			using var conn = NewConnection();
			using var tx = conn.BeginTransaction();
			this.WriteAudit(conn, tx, AuditOp.WritebackLoss, targetId: null, parentId: null, name: this.config.Mount.MountPoint, detail: new {
				mount_id = this.mountId,
				unflushed = remaining,
				pending_inodes = this.dirtyNamespace.PendingCount,
				dirty_files = this.dirtySet.DirtyFileCount,
				queued_audits = this.dirtyNamespace.QueuedAuditCount,
				timeout_ms = this.config.Mount.WriteBackFlushTimeoutMs,
				lost = this.DescribeLostPending(),
				lost_data = this.DescribeLostDirty(),
			});
			tx.Commit();
		} catch (Exception ex) {
			// Failing here does not stop the unmount (only recording the loss failed; the loss itself is already
			// in the log).
			Logger.Error("write-back: could not write the audit row for the loss: ", ex.Message);
		}
	}

	/// <summary>
	/// **Keep our own row in <c>{prefix}mounts</c> as a tombstone instead of deleting it** (B-2).
	/// <c>ended</c> / <c>unflushedLoss</c> ride in <c>stats</c>, so **it works on existing filesystems without a
	/// DDL change** (the DDL of this table is concentrated in mkfs, so adding a column would require another
	/// mkfs).
	/// <para>
	/// **It is never removed automatically.** A record of an incident disappearing before anyone reads it is the
	/// worst outcome, so removing it is left to an explicit operational step
	/// (<c>DELETE FROM {prefix}mounts WHERE mount_id = ...</c>).
	/// Tombstones only accumulate when a loss actually happened, so they grow differently from the stale rows
	/// left behind by an abnormal exit.
	/// </para>
	/// </summary>
	private void MarkMountLoss(int remaining) {
		var detail = JsonSerializer.Serialize(new {
			ended = true,
			// **Make it declare that it is UTC.** `Pg.UtcNow` has Kind=Unspecified to match the timestamp
			// columns of the database, so writing it with "o" as it is produces **a string with no `Z`**. Log
			// timestamps are local, so **it looks like a different run nine hours away within the same log**
			// (it was actually misread as "no tombstone was written"). The value itself is correct UTC, so only
			// the notation is fixed here.
			endedAt = DateTime.SpecifyKind(Pg.UtcNow, DateTimeKind.Utc).ToString("o"),
			unflushedLoss = remaining,
			lost = this.DescribeLostPending(),
			lostData = this.DescribeLostDirty(),
		});
		var rows = Pg.Execute(
			this.connectionString,
			$"UPDATE {this.QualifiedTable("mounts")} SET stats = stats || @detail::jsonb WHERE mount_id = @mount_id",
			new { mount_id = this.mountId, detail }
		);
		// **When this process has no row, one is created as the gravestone.** Unregistered (registration failed
		// at startup) or removed by prune as stale, the UPDATE touches 0 rows. Before, that was not checked and
		// the "recorded ... on the row" below was printed anyway - **claiming a record that did not exist**.
		if (rows == 0) { this.InsertMountRow(detail); }
		Logger.Error("write-back: recorded ", remaining, " lost entries on the {prefix}mounts row (mount_id:", this.mountId, "). Delete it by hand once you have reviewed it: DELETE FROM ", this.QualifiedTable("mounts"), " WHERE mount_id = '", this.mountId, "'");
	}

	/// <summary>
	/// Deregistration (B-2). **The row is only kept when there was a loss**; otherwise it is DELETEd as before.
	/// </summary>
	private void DeregisterMount() {
		if (this.UnflushedAtShutdown > 0) {
			this.MarkMountLoss(this.UnflushedAtShutdown);
			return;
		}
		Pg.Execute(this.connectionString, $"DELETE FROM {this.QualifiedTable("mounts")} WHERE mount_id = @mount_id", new { mount_id = this.mountId });
	}

	/// <summary>
	/// Render the <c>endedAt</c> of a tombstone **so that it is recognizably UTC** for display.
	/// <para>
	/// **Log line timestamps are local while <c>endedAt</c> is UTC**, so without the marker **it looks like a
	/// different run nine hours away within the same log** (this actually happened). Values written from that
	/// point on carry a <c>Z</c>, but **rows written before that do not**, so **we add it here when it is
	/// missing**. The value was always UTC; this is purely a matter of notation.
	/// </para>
	/// </summary>
	public static string FormatUtcStamp(string? raw) {
		if (string.IsNullOrEmpty(raw)) { return "(unknown)"; }
		if (raw.EndsWith("Z", System.StringComparison.Ordinal)) { return raw; }
		return raw + "Z";
	}

	/// <summary>
	/// Warn at startup when <c>{prefix}mounts</c> still holds records of unflushed data lost at a past unmount
	/// (B-2). The point is to let operations notice it here first, and **it never removes them itself**.
	/// </summary>
	private void WarnPastUnflushedLoss() {
		try {
			var rows = Pg.Query<dynamic>(
				this.connectionString,
				$@"SELECT mount_id, host, mountpoint, stats->>'endedAt' AS ended_at, (stats->>'unflushedLoss')::int AS loss
				   FROM {this.QualifiedTable("mounts")}
				   WHERE (stats->>'unflushedLoss')::int > 0
				   ORDER BY stats->>'endedAt'"
			).ToList();
			if (rows.Count == 0) { return; }
			Logger.Error("write-back: ", rows.Count, " records of data that could not be flushed at a past unmount are still present (the lost contents cannot be recovered)");
			foreach (var row in rows) {
				Logger.Error("  lost ", (int)row.loss, " entries: host=", (string)row.host, " mountpoint=", (string)row.mountpoint, " endedAt=", Api.FormatUtcStamp((string?)row.ended_at), " mount_id=", (string)row.mount_id);
			}
			Logger.Error("  delete them once you have reviewed them: DELETE FROM ", this.QualifiedTable("mounts"), " WHERE (stats->>'unflushedLoss')::int > 0");
		} catch (Exception ex) {
			if (Logger.IsDebugEnabled) { Logger.Debug("the query for past loss records failed (ignored): ", ex.Message); }
		}
	}

	/// <summary>The wait before retrying a flush (exponential backoff with jitter, capped at 2 seconds).</summary>
	private static void SleepBeforeFlushRetry(int round) {
		var ms = Math.Min(2000, 100 * (1 << Math.Min(round - 1, 5)));
		System.Threading.Thread.Sleep(ms + System.Random.Shared.Next(50));
	}

	// ------------------------------------------------------------------
	// The flush transaction (the body)
	// ------------------------------------------------------------------

	/// <summary>
	/// Materialize one pending inode. **Call this while holding the NSGate**. When it holds dirty data, take
	/// <see cref="DirtyFile.Gate"/> as well and include it in the same transaction
	/// (**the inode INSERT comes before the data row and is always in the same transaction** = invariant 2 of
	/// the flush transaction).
	/// </summary>
	private void FlushPendingEntry(PendingInode entry, long? expectedReplaceId = null) {
		this.AssertNsGateHeld();
		var chain = this.dirtyNamespace.AncestorChain(entry, out var truncated);
		if (truncated) {
			// The ancestors are too deep to fit in one transaction. **Do not discard them**; materialize them
			// step by step from the parent side.
			Logger.Warning("metadata write-back: the pending ancestors are too deep, materializing from the parent side id:", entry.Inode.Id);
			this.MaterializePendingLocked(chain[0].Inode.ParentId);
			chain = this.dirtyNamespace.AncestorChain(entry, out _);
		}
		chain.Add(entry);
		var file = this.FindDirtyFile(entry.Inode);
		if (file == null) {
			this.FlushPendingChain(chain, null, new List<DirtyChunk>(), expectedReplaceId);
			return;
		}
		lock (file.Gate) {
			this.FlushPendingChain(chain, file, this.contentCache.SnapshotDirty(file.DataId, file.Chunks), expectedReplaceId);
		}
	}

	/// <summary>Return the unflushed data of this inode, if any.</summary>
	private DirtyFile? FindDirtyFile(Inode inode) {
		if (inode.DataId is not long dataId) { return null; }
		return this.dirtySet.Find(dataId);
	}

	/// <summary>
	/// Run the flush transaction and the state transitions around it. 40P01 / 40001 are retried a bounded number
	/// of times (the victim's transaction has been rolled back entirely, so re-running is safe).
	/// <para>
	/// **The transaction only sees the snapshot frozen by <see cref="DirtyNamespace.BeginFlush"/>.**
	/// Reflecting the result in memory after the commit (Rekey / cache invalidation / notification) happens
	/// outside the transaction.
	/// </para>
	/// </summary>
	private void FlushPendingChain(List<PendingInode> chain, DirtyFile? file, List<DirtyChunk> chunks, long? expectedReplaceId) {
		var indexes = new List<int>();
		if (file != null) { indexes.AddRange(file.Chunks); }
		var items = this.dirtyNamespace.BeginFlush(chain);
		if (items.Count == 0) { return; }
		var outcome = new PendingFlushOutcome();
		try {
			for (var attempt = 1; ; attempt++) {
				try {
					// Rebuild the side-effect list on every retry (so that audit rows and Rekeys are not
					// duplicated once per attempt).
					outcome = new PendingFlushOutcome { ExpectedReplaceId = expectedReplaceId };
					this.FlushPendingTransaction(items, file, chunks, outcome);
					break;
				} catch (Npgsql.PostgresException ex) when (Api.IsRetryableDeadlock(ex) && attempt < Api.DeadlockMaxAttempts) {
					Logger.Warning("metadata flush: retrying after a distributed deadlock (", ex.SqlState, ") (attempt ", attempt, "/", Api.DeadlockMaxAttempts, ") inode:", items[items.Count - 1].Row.Id);
					Api.SleepBeforeDeadlockRetry(attempt);
				}
			}
		} catch (PendingDiscardedException) {
			// DiscardPendingChain has already emitted the statistics and the warning for a discard. We do not
			// latch (having nowhere to write is not "a failure to flush", so it is not counted as a consecutive
			// failure either).
			this.FinishPendingDiscard(items, file);
			throw;
		} catch (Exception ex) {
			// Keep the pending entry = it can be retried at the next opportunity. Latch the error for diagnosis.
			this.dirtyNamespace.MarkFailed(items, ex.Message);
			this.NoteFlushFailure(-items[items.Count - 1].Row.Id, ex.Message);
			throw;
		}
		// ---- everything below here has already been committed (B-11) ----
		// Even if the cleanup (Rekey / cache invalidation / notification) throws, the flush has succeeded, so
		// **the success is recorded first**. In the reverse order the consecutive failure counter would survive
		// a committed flush, and five of those would fabricate an error state out of nothing.
		this.NoteFlushSuccess(-items[items.Count - 1].Row.Id);
		this.FinishPendingData(items, file, indexes, outcome.Occupied);
		this.FinishPendingFlush(items, file, outcome);
	}

	/// <summary>
	/// One transaction of a namespace flush. **Invariants**:
	/// <list type="number">
	///   <item>Pending ancestors are INSERTed in the same transaction in parent-to-child order, and the topmost
	///     persisted ancestor is checked for existence while holding <c>{prefix}lock</c> (−inode_id). Since the
	///     design has no foreign keys, this is the only defence that stops "another client's rmdir × our own
	///     pending child" from creating an unreachable orphan inode. When the parent is gone, throw
	///     <see cref="PendingDiscardedException"/> (the caller discards it, records the statistics and logs; on
	///     a synchronous trigger it becomes -EIO)</item>
	///   <item>**The inode INSERT comes before the data row and is always in the same transaction.** The
	///     existing flush on the data side has a path that "discards the dirty data and commits when the inode
	///     is gone", and letting a pending inode through there would silently throw away data that has already
	///     been fsynced</item>
	///   <item>Name conflicts: dir/dir = adopt the existing id (the inode version of Rekey) / file/file = an
	///     explicit DELETE plus an INSERT plus a conflict audit. A kind mismatch and a non-empty directory are
	///     refused (error latch)</item>
	///   <item><c>created_at</c> / <c>st_ctime</c> are set explicitly to the time of the operation (they are not
	///     left to the database DEFAULT, which would be the time of the flush)</item>
	/// </list>
	/// </summary>
	private void FlushPendingTransaction(List<PendingFlush> items, DirtyFile? file, List<DirtyChunk> chunks, PendingFlushOutcome outcome) {
		// Ensuring the audit partitions is done **first, outside the transaction**.
		// `CREATE TABLE ... PARTITION OF` requires a lock on the parent table that conflicts with the
		// RowExclusiveLock of an INSERT (measured), so firing it from another connection while our own
		// transaction holds a RowExclusiveLock on the parent splits the wait graph across two sessions, the
		// deadlock detector of PostgreSQL cannot find the cycle, and we wait forever while holding the NSGate =
		// **the whole mount hangs unrecoverably**.
		this.EnsureAuditPartitionsFor(items);

		using var conn = this.NewConnection();
		using var tx = conn.BeginTransaction();
		// Take the locks from {prefix}lock in one go, in ascending order (inodes are negative, so they always
		// come before the data).
		this.LockPendingTargets(conn, tx, items, file);

		// (1) Is the parent of the topmost pending entry (= the persisted ancestor) alive? ResolveParentId goes
		//     through the cache and can return "a cached copy of a deleted row", so it cannot be used to check
		//     for existence.
		var topParentId = items[0].Row.ParentId;
		if (this.ResolveParentIdFresh(conn, tx, topParentId) == null) {
			tx.Rollback();
			throw new PendingDiscardedException($"metadata write-back: discarded the pending entry because the parent inode {topParentId} does not exist");
		}

		// (2) The inode INSERTs (parent to child; name conflicts are resolved here)
		foreach (var item in items) {
			this.InsertPendingInodeInTx(conn, tx, items, item, outcome);
		}

		// (3) The data row → the chunks → the occupied byte count (after the inode, in the same transaction)
		outcome.Occupied = this.WritePendingDataInTx(conn, tx, items, file, chunks);

		// (4) The audit rows (a batch INSERT with the time of the operation and the caller at that time)
		foreach (var item in items) {
			this.WriteAuditRecordsInTx(conn, tx, item.Audits);
		}
		this.WriteAuditRecordsInTx(conn, tx, outcome.ConflictAudits);
		tx.Commit();
		if (Logger.IsDebugEnabled) { Logger.Debug("metadata write-back: materialized inodes:", items.Count, " id:", items[items.Count - 1].Row.Id, " chunks:", chunks.Count); }
	}

	/// <summary>
	/// Take the locks of the flush targets from <c>{prefix}lock</c> **in one go, in ascending order**:
	/// each pending inode (−id) + the parent of the topmost pending entry (−id) + the data row (+id).
	/// Only ids taken from the snapshot are used (reading a live Inode would make the locked target and the
	/// INSERT target diverge).
	/// </summary>
	private void LockPendingTargets(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, List<PendingFlush> items, DirtyFile? file) {
		var targets = new List<long>();
		foreach (var item in items) {
			targets.Add(-item.Row.Id);
			targets.Add(-item.Row.ParentId);
		}
		if (file != null) { targets.Add(file.DataId); }
		this.LockTargets(conn, tx, targets.ToArray());
	}

	/// <summary>
	/// Check inside the transaction whether an inode is alive in the database (root = 0 always exists).
	/// <see cref="ResolveParentId(Npgsql.NpgsqlConnection, Npgsql.NpgsqlTransaction, long)"/> consults
	/// <see cref="InodeCache"/> first, so **it cannot be used to check for existence**.
	/// </summary>
	private long? ResolveParentIdFresh(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long id) {
		if (id == 0) { return 0; }
		return conn.QueryFirstOrDefault<long?>(
			$"SELECT parent_id FROM {this.QualifiedTable("inode")} WHERE id = @id",
			new { id }, tx
		);
	}

	/// <summary>
	/// INSERT one pending inode row (a conflict goes through the resolution of decision 4).
	/// <c>ON CONFLICT DO NOTHING</c> is used **only to detect the conflict**; once the row is dropped it is
	/// always resolved explicitly (writing the data row and the chunks while the row stayed dropped would orphan
	/// them, so we must never carry on silently).
	/// </summary>
	private void InsertPendingInodeInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, List<PendingFlush> items, PendingFlush item, PendingFlushOutcome outcome) {
		var occupant = this.LoadInodeByName(conn, tx, item.Row.ParentId, item.Row.Name);
		if (occupant != null && this.AdoptOrRemoveOccupant(conn, tx, items, item, occupant, outcome)) { return; }
		if (this.InsertPendingRow(conn, tx, item.Row)) { return; }
		// We lost to a write-through create outside the lock (another client). Try to resolve it once more.
		var raced = this.LoadInodeByName(conn, tx, item.Row.ParentId, item.Row.Name);
		if (raced == null) { throw new InvalidOperationException($"metadata write-back: could not INSERT inode {item.Row.Id} (parent:{item.Row.ParentId} name:{item.Row.Name})"); }
		if (this.AdoptOrRemoveOccupant(conn, tx, items, item, raced, outcome)) { return; }
		if (this.InsertPendingRow(conn, tx, item.Row)) { return; }
		throw new InvalidOperationException($"metadata write-back: could not INSERT inode {item.Row.Id} even after resolving the name conflict (parent:{item.Row.ParentId} name:{item.Row.Name})");
	}

	/// <summary>Read the current occupant of (parent, name) inside the transaction (the full row, because the
	/// conflict resolution also uses it for the delete).</summary>
	private Inode? LoadInodeByName(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long parentId, string name) {
		return conn.QueryFirstOrDefault<Inode>(
			$@"SELECT id, parent_id, name, uname, gname, st_mode, st_nlink, st_size,
			          st_mtime, st_ctime, link_target, is_junction, data_id, xattr_names, xattr_values,
			          created_at, created_by, updated_at, updated_by
			   FROM {this.QualifiedTable("inode")} WHERE parent_id = @parent_id AND name = @name",
			new { parent_id = parentId, name }, tx
		);
	}

	/// <summary>
	/// INSERT the row of a pending inode. The id, created_at and st_ctime are all given explicitly
	/// (<c>OVERRIDING SYSTEM VALUE</c> puts the reserved id into the BIGSERIAL column).
	/// Returns false when the row was dropped by a conflict.
	/// </summary>
	private bool InsertPendingRow(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, Inode row) {
		var inserted = conn.QueryFirstOrDefault<long?>(
			$@"INSERT INTO {this.QualifiedTable("inode")}
			   (id, parent_id, name, uname, gname, st_mode, st_nlink, st_size,
			    st_mtime, st_ctime, link_target, is_junction, data_id, xattr_names, xattr_values,
			    created_at, created_by, updated_at, updated_by)
			   OVERRIDING SYSTEM VALUE
			   VALUES (@id, @parent_id, @name, @uname, @gname, @st_mode, @st_nlink, @st_size,
			           @st_mtime, @st_ctime, @link_target, @is_junction, @data_id, @xattr_names, @xattr_values,
			           @created_at, @created_by, @updated_at, @updated_by)
			   ON CONFLICT (parent_id, name) DO NOTHING
			   RETURNING id",
			new {
				id = row.Id,
				parent_id = row.ParentId,
				name = row.Name,
				uname = row.UserName,
				gname = row.GroupName,
				st_mode = row.Mode,
				st_nlink = Math.Max(1, row.NLink),
				st_size = row.Size,
				st_mtime = Pg.ToDbUtc(row.Mtime),
				st_ctime = Pg.ToDbUtc(row.Ctime),
				link_target = row.LinkTarget,
				is_junction = row.IsJunction,
				data_id = row.DataId,
				xattr_names = row.xattr_names,
				xattr_values = row.xattr_values,
				created_at = Pg.ToDbUtc(row.created_at),
				created_by = row.created_by,
				updated_at = Pg.ToDbUtc(row.updated_at),
				updated_by = row.updated_by,
			}, tx
		);
		if (Logger.IsTraceEnabled) { Logger.Trace("metadata write-back: INSERT inode id:", row.Id, " parent:", row.ParentId, " name:", row.Name, " = ", inserted?.ToString() ?? "(conflict)"); }
		return inserted != null;
	}

	/// <summary>
	/// Resolve a name conflict (decision 4). true = no INSERT is needed / false = the occupant was removed, so
	/// carry on with the INSERT.
	/// <para>
	/// **dir/dir** adopts the existing id and rewires our own pending child tree onto it (the inode version of
	/// Rekey) — discarding the loser would make an already flushed child tree underneath it entirely invisible.
	/// **file/file** does an explicit DELETE plus an INSERT (the data row and the chunks need cleaning up, so
	/// <c>DO NOTHING</c> is not enough).
	/// **A kind mismatch** and **a non-empty directory** are refused with an error latch (decision 4 only
	/// covers a conflict between the same kinds; deleting a directory as collateral damage or crushing a file
	/// with a directory is not a choice one can make semantically).
	/// </para>
	/// </summary>
	private bool AdoptOrRemoveOccupant(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, List<PendingFlush> items, PendingFlush item, Inode occupant, PendingFlushOutcome outcome) {
		// We are already there ourselves = a previous attempt committed, or a retry re-entered. Treat it
		// idempotently (missing this would DELETE the row and the data row that we committed ourselves).
		if (occupant.Id == item.Row.Id) {
			if (Logger.IsTraceEnabled) { Logger.Trace("metadata write-back: our own row already exists, skipping the INSERT id:", occupant.Id); }
			return true;
		}
		// A row we already know we are going to replace as part of a rename-over-existing (heuristic a) is not
		// an accident, so it is not counted as a conflict (it must not pollute the last-flush-wins statistics).
		if (outcome.ExpectedReplaceId == occupant.Id && !item.Row.IsDirectory && !occupant.IsDirectory) {
			if (Logger.IsDebugEnabled) { Logger.Debug("metadata write-back: deleting the target of a rename-over-existing in the same tx parent:", item.Row.ParentId, " name:", item.Row.Name, " replaced:", occupant.Id); }
			return this.RemoveOccupantInTx(conn, tx, item, occupant, outcome, conflict: false);
		}
		this.dirtyNamespace.CountConflict();
		Logger.Warning("metadata write-back: name conflict (last-flush-wins) parent:", item.Row.ParentId, " name:", item.Row.Name, " pending:", item.Row.Id, " existing:", occupant.Id);
		if (item.Row.IsDirectory && occupant.IsDirectory) {
			this.AdoptExistingDirectoryInTx(conn, tx, items, item, occupant, outcome);
			return true;
		}
		if (item.Row.IsDirectory != occupant.IsDirectory) {
			throw new InvalidOperationException($"metadata write-back: cannot materialize inode {item.Row.Id} because it collided with the existing inode {occupant.Id} of a different kind (parent:{item.Row.ParentId} name:{item.Row.Name})");
		}
		if (item.Entry.BornExclusive) {
			// A conflict of an O_EXCL create that was deferred under `defer`. **The occupant must not be
			// deleted** — the application was told "only I created it", so silently crushing the other side
			// (which may well be a lock file) with a DELETE plus an INSERT is the worst possible outcome. Like a
			// kind mismatch, we latch the error to make operations notice (the error state plus the red display
			// in status).
			// The way to recover: **unlink this file on the losing side** (that is a pure cancellation of a
			// pending entry, so it does not touch the database).
			throw new InvalidOperationException($"metadata write-back: the name of inode {item.Row.Id}, created with O_EXCL, is occupied by the existing inode {occupant.Id} (parent:{item.Row.ParentId} name:{item.Row.Name}). write_back_metadata_exclusive_create = defer does not guarantee cross-client exclusion. Unlink this pending entry to clear it");
		}
		if (occupant.IsDirectory && !this.IsDirectoryEmptyInTx(conn, tx, occupant.Id)) {
			// We cannot delete a non-empty directory as collateral damage. Latch the error to make operations
			// notice (this is what the error state and the red display in status make visible).
			throw new InvalidOperationException($"metadata write-back: cannot materialize inode {item.Row.Id} because it collided with the non-empty directory {occupant.Id} (parent:{item.Row.ParentId} name:{item.Row.Name})");
		}
		return this.RemoveOccupantInTx(conn, tx, item, occupant, outcome, conflict: true);
	}

	/// <summary>
	/// Delete the existing inode that occupies the name **in the same transaction** to clear the way for the
	/// INSERT (returns false = carry on with the INSERT).
	/// <paramref name="conflict"/> = false is the deliberate replacement of a rename-over-existing (heuristic a),
	/// and only the reason in the audit detail differs. The nlink of hard link siblings changes, so they are
	/// dropped from the cache after the commit (<see cref="PendingFlushOutcome.InvalidateIds"/>).
	/// </summary>
	private bool RemoveOccupantInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, PendingFlush item, Inode occupant, PendingFlushOutcome outcome, bool conflict) {
		var reason = "write_back_metadata_rename_replace";
		if (conflict) { reason = "write_back_metadata_name_conflict"; }
		// The record of the conflict goes onto a **list outside the transaction** (appending it to entry.Audits
		// would duplicate it on a 40P01 retry).
		Api.AppendAudit(outcome.ConflictAudits, this.BuildPendingAudit(occupant.Id, AuditOp.Delete, occupant.ParentId, occupant.Name, new {
			reason,
			replaced_by = item.Row.Id,
		}, Pg.UtcNow));
		// Take an extra row lock on the one being deleted (this path only happens on a conflict or a
		// replacement, so coming after the single ascending acquisition is acceptable and we leave it to the
		// 40P01 retry).
		this.LockTargets(conn, tx, -occupant.Id);
		// Keep the delete audit to the single row appended above (letting DeleteInodeInTx write one as well
		// would emit the same deletion twice).
		var siblingIds = this.DeleteInodeInTx(conn, tx, occupant, out _, writeAudit: false);
		outcome.ReplacedIds.Add(occupant.Id);
		outcome.InvalidateIds.AddRange(siblingIds);
		return false;
	}

	/// <summary>
	/// A directory name conflict: adopt the existing id. **Inside the transaction we only run the SQL and
	/// rewrite the snapshot**; rewiring the ledger and the cache is deferred until after the commit
	/// (<see cref="FinishPendingFlush"/>) — rewriting memory inside the transaction would leave "a pending entry
	/// holding the id of a directory that really exists" behind after a rollback, and its <c>rmdir</c> would
	/// succeed as a pure cancellation without touching the database, so **the directory would come back at the
	/// next ls**.
	/// </summary>
	private void AdoptExistingDirectoryInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, List<PendingFlush> items, PendingFlush item, Inode occupant, PendingFlushOutcome outcome) {
		var oldId = item.Row.Id;
		// The adopted id becomes the destination of the child INSERTs in this transaction, so take its lock.
		this.LockTargets(conn, tx, -occupant.Id);
		// Redirect the INSERT destination of the following children to the adopted id (this rewrites the
		// snapshot, so it is idempotent).
		item.Row.Id = occupant.Id;
		foreach (var other in items) {
			if (other.Row.ParentId != oldId) { continue; }
			other.Row.ParentId = occupant.Id;
		}
		outcome.Adoptions.Add((item.Entry, oldId, occupant.Id));
		Logger.Warning("metadata write-back: adopting the existing id of the directory ", oldId, " -> ", occupant.Id);
	}

	/// <summary>Decide inside the transaction whether a directory is empty (pending children included).</summary>
	private bool IsDirectoryEmptyInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long inodeId) {
		var found = conn.QueryFirstOrDefault<int?>(
			$"SELECT 1 FROM {this.QualifiedTable("inode")} WHERE parent_id = @parent_id AND id <> 0 LIMIT 1",
			new { parent_id = inodeId }, tx
		);
		if (found != null) { return false; }
		return !this.dirtyNamespace.HasPendingChildren(inodeId);
	}

	/// <summary>
	/// Write the data row → the chunks → the occupied byte count (**after the inode INSERT, in the same
	/// transaction**). A pending inode has no data row in the database, so the row is always created here.
	/// <c>created_at</c> is also set explicitly to the time of the operation (invariant 4 — it is not left to
	/// the database DEFAULT, which would be the time of the flush).
	/// </summary>
	private long? WritePendingDataInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, List<PendingFlush> items, DirtyFile? file, List<DirtyChunk> chunks) {
		if (file == null) { return null; }
		if (file.DataRowPending) {
			var when = Pg.ToDbUtc(items[items.Count - 1].Row.Mtime);
			conn.Execute(
				$@"INSERT INTO {this.QualifiedTable("data")} (id, chunk_size, total_size, created_at, created_by, updated_at, updated_by)
				   VALUES (@id, @chunk_size, 0, @when, @user, @when, @user)",
				new { id = file.DataId, chunk_size = file.ChunkSize, when, user = Environment.UserName ?? "pgfs" },
				tx
			);
			if (Logger.IsTraceEnabled) { Logger.Trace("metadata write-back: INSERT data id:", file.DataId, " chunk_size:", file.ChunkSize); }
		}
		var delta = 0L;
		foreach (var chunk in chunks) {
			delta += this.WriteFullChunkFlush(conn, tx, file.DataId, chunk);
		}
		return this.AddOccupiedBytes(conn, tx, file.DataId, delta);
	}

	// ------------------------------------------------------------------
	// Cleanup after a successful flush or a discard
	// ------------------------------------------------------------------

	/// <summary>
	/// Clear the dirty marks on the data side after a successful flush (called **while holding
	/// <see cref="DirtyFile.Gate"/>**). Neither the database nor any other lock is touched here.
	/// </summary>
	private void FinishPendingData(List<PendingFlush> items, DirtyFile? file, List<int> indexes, long? occupied) {
		if (file == null) { return; }
		this.contentCache.MarkFlushed(file.DataId, indexes);
		file.ClearDirty();
		// The INSERT of the data row becomes final once the commit is reached, so the flag is cleared here
		// (it is kept on a rollback).
		file.DataRowPending = false;
		file.Error = null;
		this.dirtySet.CountFlush();
		if (occupied == null) { return; }
		this.SetOccupiedBytes(items[items.Count - 1].Entry.Inode, occupied.Value);
	}

	/// <summary>
	/// The cleanup after the commit: Rekey (directory adoption) / unpinning / cache invalidation / statistics /
	/// notification. Call it **after releasing <see cref="DirtyFile.Gate"/> and while still holding the NSGate**
	/// (calling another transaction or a write-through while holding the Gate would grow a Gate → NSGate back
	/// edge).
	/// </summary>
	private void FinishPendingFlush(List<PendingFlush> items, DirtyFile? file, PendingFlushOutcome outcome) {
		foreach (var adoption in outcome.Adoptions) {
			this.ApplyAdoption(adoption.Entry, adoption.OldId, adoption.NewId);
		}
		foreach (var replacedId in outcome.ReplacedIds) {
			this.ForgetReplaced(replacedId);
		}
		// The hard link siblings of an inode removed by a replacement have a different nlink now, so they are
		// dropped from the cache (keeping them produces the same kind of divergence as `stat b` returning a
		// stale nlink=2 after `rm a`).
		foreach (var siblingId in outcome.InvalidateIds) {
			this.inodeCache.Invalidate(siblingId, path: null);
		}
		var ids = new List<long>();
		var parents = new List<long>();
		foreach (var item in items) {
			ids.Add(item.Entry.Inode.Id);
			parents.Add(item.Entry.Inode.ParentId);
			this.inodeCache.Unpin(item.Entry.Inode.Id);
			this.inodeCache.InvalidateChildren(item.Entry.Inode.ParentId);
		}
		// Put the inodes removed by a replacement and their siblings into the notification as well (so that
		// other clients drop them from their caches).
		ids.AddRange(outcome.ReplacedIds);
		ids.AddRange(outcome.InvalidateIds);
		this.dirtyNamespace.MarkPersisted(items);
		this.dirtyNamespace.PrunePersisted();
		this.dirtyNamespace.VerifyIndexIfTracing();
		this.Notify(inodeIds: ids, parentIds: parents, dataIds: this.NotifyDataIds(file));
	}

	/// <summary>
	/// Reflect a directory adoption (Rekey) in memory after the commit. **The attributes of the adopted existing
	/// row are not overwritten**, so it is dropped from the cache and read back from the database (putting the
	/// loser's attributes under the winner's id would permanently return "a stat that exists nowhere in the
	/// database").
	/// </summary>
	private void ApplyAdoption(PendingInode entry, long oldId, long newId) {
		this.dirtyNamespace.Rekey(entry, newId);
		this.inodeCache.Unpin(oldId);
		this.inodeCache.Invalidate(oldId, path: null);
		this.inodeCache.InvalidateChildren(oldId);
		// Invalidate the adopted one as well so that the real values are read back from the database, and so
		// that the rewired pending children show up in ls.
		this.inodeCache.Invalidate(newId, path: null);
		this.inodeCache.InvalidateChildren(newId);
		Logger.Warning("metadata write-back: adopted the existing id of the directory ", oldId, " -> ", newId);
	}

	/// <summary>The data_id to put into the notification (null when there is none).</summary>
	private List<long>? NotifyDataIds(DirtyFile? file) {
		if (file == null) { return null; }
		return new List<long> { file.DataId };
	}

	/// <summary>
	/// Discard a pending entry that has nowhere to be written (its persisted ancestor was gone). It is treated
	/// as "it never happened", the same as after a crash, but **it is not passed over in silence** — the
	/// statistics and a warning are kept, and <see cref="PendingDiscardedException"/> is thrown to the caller
	/// (on a synchronous trigger it becomes -EIO).
	/// </summary>
	private void FinishPendingDiscard(List<PendingFlush> items, DirtyFile? file) {
		foreach (var item in items) {
			Logger.Warning("metadata write-back: discarding a pending inode because its parent does not exist id:", item.Entry.Inode.Id, " parent:", item.Entry.Inode.ParentId, " name:", item.Entry.Inode.Name);
			this.dirtyNamespace.Forget(item.Entry.Inode.Id);
			this.dirtyNamespace.CountDiscard();
			// Discarded = it will never be flushed again, so the failure counter and the synchronous-close mark
			// are dropped as well.
			this.ClearFlushFailure(item.Entry.Inode.Id, item.Entry.Inode.DataId);
			this.ClearSyncOnClose(item.Entry.Inode.Id, item.Entry.Inode.DataId);
			this.inodeCache.Unpin(item.Entry.Inode.Id);
			this.inodeCache.Invalidate(item.Entry.Inode.Id, path: null);
			this.inodeCache.InvalidateChildren(item.Entry.Inode.ParentId);
		}
		if (file == null) { return; }
		this.contentCache.DiscardDirty(file.DataId);
		file.ClearDirty();
		file.DataRowPending = false;
		this.dirtySet.Forget(file.DataId);
	}

	// ------------------------------------------------------------------
	// Audit (captured at operation time → batch INSERT in the flush transaction)
	// ------------------------------------------------------------------

	/// <summary>Append to the audit row list, filtering out a disabled audit (null).</summary>
	private static void AppendAudit(List<PendingAudit> list, PendingAudit? audit) {
		if (audit == null) { return; }
		list.Add(audit);
	}

	private PendingAudit? BuildPendingAudit(long targetId, string op, long? parentId, string? name, object? detail, DateTime when) {
		if (!this.auditEnabled) { return null; }
		var ctx = AuditContext.Current;
		string? detailJson = null;
		if (detail != null) { detailJson = JsonSerializer.Serialize(detail); }
		return new PendingAudit {
			Op = op,
			TargetId = targetId,
			ParentId = parentId,
			Name = name,
			DetailJson = detailJson,
			When = when,
			Uid = ctx?.Uid,
			Uname = ctx?.Uname,
			Domain = ctx?.Domain,
		};
	}

	/// <summary>
	/// Reserve the monthly partitions of the audit rows we are about to write, all at once, **before opening the
	/// transaction**. Metadata write-back ensures them by "the month of each row's occurred_at", so a pending
	/// entry that latched an error and crossed a month boundary mixes several months into one transaction.
	/// Firing DDL from another connection inside the transaction collides with our own RowExclusiveLock and
	/// produces **a deadlock that is never detected**, so this is always done up front.
	/// </summary>
	private void EnsureAuditPartitionsFor(List<PendingFlush> items) {
		if (!this.auditEnabled) { return; }
		var months = new HashSet<DateTime>();
		foreach (var item in items) {
			foreach (var audit in item.Audits) {
				months.Add(new DateTime(audit.When.Year, audit.When.Month, 1));
			}
		}
		// A conflict audit is only discovered inside the transaction, but its occurred_at is "now", so the
		// current month is always included.
		months.Add(new DateTime(Pg.UtcNow.Year, Pg.UtcNow.Month, 1));
		foreach (var month in months) {
			this.EnsureAuditPartition(month);
		}
	}

	/// <summary>INSERT the captured audit rows in one go (the partitions have been ensured beforehand).</summary>
	private void WriteAuditRecordsInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, List<PendingAudit> audits) {
		if (!this.auditEnabled) { return; }
		foreach (var audit in audits) {
			this.WriteAuditRecordInTx(conn, tx, audit);
		}
	}

	/// <summary>
	/// INSERT one captured audit row. <c>occurred_at</c> is the time of the operation.
	/// Only caller_ip stays evaluated on the server side (it is the source of the pgfs process's connection, so
	/// it is the same value at operation time and at flush time).
	/// **Ensuring the partition is not called here** (to avoid the deadlock of DDL inside a transaction;
	/// <see cref="EnsureAuditPartitionsFor"/> has already done it outside the transaction).
	/// </summary>
	private void WriteAuditRecordInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, PendingAudit audit) {
		conn.Execute(
			$@"INSERT INTO {this.QualifiedTable("audit")} (
				occurred_at, op, target_id, parent_id, name, detail,
				caller_ip, caller_host, caller_uid, caller_uname, caller_domain
			   ) VALUES (
				@occurred_at, @op, @target_id, @parent_id, @name, @detail::jsonb,
				inet_client_addr(), @caller_host, @caller_uid, @caller_uname, @caller_domain
			   )",
			new {
				occurred_at = Pg.ToDbUtc(audit.When),
				op = audit.Op,
				target_id = audit.TargetId,
				parent_id = audit.ParentId,
				name = audit.Name,
				detail = audit.DetailJson ?? "{}",
				caller_host = this.auditHost,
				caller_uid = audit.Uid,
				caller_uname = audit.Uname,
				caller_domain = audit.Domain,
			}, tx
		);
	}

	/// <summary>
	/// Write the audit rows that have no entry of their own (the create/delete pair of a pure cancellation) in
	/// one transaction. On failure they go back on the queue and are retried at the next opportunity. Called
	/// from fsync / fsyncdir / the background loop / unmount (the background loop runs this one regardless of
	/// the state of <c>write_back</c> — otherwise no audit would appear until unmount when
	/// <c>write_back_interval_ms = 0</c>).
	/// </summary>
	private void FlushOrphanAudits() {
		// **Do not return early on `auditEnabled`** (B-4). The rows queued here **were captured while auditing
		// was enabled**, so turning auditing off afterwards is no reason to leave them unwritten.
		// (1) With it off they are never written, yet the queue is still counted by UnflushedCount(), so
		//     **unmount runs out its deadline, emits a loss report and exits with 4 even though nothing was
		//     actually lost** (that is the symptom of B-4).
		// (2) Worse still, `config set audit.enabled false` would **become a way to erase evidence that had
		//     already been captured**.
		// The capture side still stops at `auditEnabled` as before, so turning it off does stop new audits.
		var audits = this.dirtyNamespace.TakeOrphanAudits();
		if (audits.Count == 0) { return; }
		try {
			this.WriteAuditsInOwnTx(audits);
		} catch (Exception ex) {
			Logger.Warning("metadata write-back: could not write the audit rows of the cancelled entries (they will be retried at the next opportunity): ", ex.Message);
			this.dirtyNamespace.QueueOrphanAudits(audits);
		}
	}
}

/// <summary>
/// The container that accumulates what the flush transaction "has to reflect in memory after the commit".
/// Rewriting memory inside the transaction would leave an inconsistency behind on a rollback, so every side
/// effect is pushed here and <see cref="Api"/> applies it after the commit.
/// It is rebuilt on every retry (so that nothing is duplicated once per attempt).
/// </summary>
internal sealed class PendingFlushOutcome
{
	/// <summary>The (entry, old id, adopted id) of a directory adoption (the inode version of Rekey).</summary>
	public List<(PendingInode Entry, long OldId, long NewId)> Adoptions { get; } = new();
	/// <summary>The ids of the existing inodes DELETEd by a name conflict or a rename-over-existing
	/// replacement.</summary>
	public List<long> ReplacedIds { get; } = new();
	/// <summary>
	/// The ids that only have to be dropped from the cache after the commit (the hard link siblings of a deleted
	/// inode — their <c>st_nlink</c> has changed, so the next <c>stat</c> must read them back from the database).
	/// </summary>
	public List<long> InvalidateIds { get; } = new();
	/// <summary>
	/// **The id of the existing inode that is being replaced deliberately** (the rename-over-existing of
	/// heuristic a). Deleting an occupant that matches this is not counted as a "name conflict", and the reason
	/// in the audit differs as well.
	/// </summary>
	public long? ExpectedReplaceId { get; init; }
	/// <summary>The audit rows that record a name conflict (they are only determined inside the transaction, so
	/// they are not appended to the entry).</summary>
	public List<PendingAudit> ConflictAudits { get; } = new();
	/// <summary>The occupied byte count of the data after the flush (null when there is no data).</summary>
	public long? Occupied { get; set; }
}

/// <summary>
/// The exception that means "the pending entry was discarded because there is nowhere to write it".
/// **On a synchronous trigger (fsync / fsyncdir / materialize) it has to turn into -EIO** (returning 0 silently
/// would make fsync lie). On a background flush, TryFlushPending in <see cref="Api"/> turns it into a warning.
/// </summary>
internal sealed class PendingDiscardedException : Exception
{
	public PendingDiscardedException(string message) : base(message) { }
}
