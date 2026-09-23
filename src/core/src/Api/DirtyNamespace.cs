namespace Pgfs.Core.Api;

using System;
using System.Collections.Generic;
using System.Threading;
using Logging;
using Models;

/// <summary>
/// **The single source of truth for pending inodes** in metadata write-back.
/// It holds the inodes this mount created but has not INSERTed into the database yet, and
/// <see cref="InodeCache"/> stays a view that can always be rebuilt by re-merging the database with this
/// ledger.
///
/// <para>
/// **Locking discipline**: every read and write of the state completes inside <c>lock (this)</c>.
/// **This is the innermost lock**, and it must not be held while touching <see cref="InodeCache"/>,
/// <see cref="ContentCache"/>, <see cref="DirtyFile.Gate"/> or the database - the pending-resolution hook is
/// called in the direction InodeCache -> ledger, and a flush calls in the direction NSGate -> Gate -> ledger,
/// so taking them the other way round closes a cycle.
/// </para>
///
/// <para>
/// **State machine**: <c>Dirty -> Flushing -> Persisted</c>.
/// Coalescing is only allowed in <c>Dirty</c> (when <see cref="TryCoalesce"/> returns
/// <see cref="CoalesceResult.Busy"/> the caller waits for the flush to finish on the NSGate and falls back to
/// write-through), so **no modification can happen during a flush**.
/// That lets the flush transaction write from nothing but the row snapshot it fixed when it started
/// (reading the live <see cref="Inode"/> could produce an INSERT under a parent that was neither locked nor
/// checked for existence).
/// </para>
///
/// <para>
/// An entry lingers in the <c>Persisted</c> state for a while after being materialized, but that is only a
/// **best-effort narrowing** of the window where a create's existence check (ledger overlay, then database)
/// crosses a flush commit. It is never relied on for correctness
/// (the leftovers can be thrown away at any time by <see cref="PrunePersisted"/>).
/// </para>
///
/// <para>The design lives in <see href="../../../../docs/runtime-control-plane.md"/>, section on metadata write-back.</para>
/// </summary>
internal sealed class DirtyNamespace
{
	/// <summary>inode id -> ledger entry (including Persisted ones).</summary>
	private readonly Dictionary<long, PendingInode> byId = new();
	/// <summary>parent_id -> (name -> entry). The index used by fsyncdir, the ListChildren merge and name-collision detection.</summary>
	private readonly Dictionary<long, Dictionary<string, PendingInode>> byParent = new();
	/// <summary>
	/// Audit rows with no entry behind them (a create/delete pair that cancelled out exactly).
	/// They are kept to close the evasion channel where "create it, let someone read it, delete it" inside one
	/// interval would leave no audit trail at all.
	/// </summary>
	private readonly List<PendingAudit> orphanAudits = new();

	/// <summary>
	/// Whether intake of new pending entries is closed (phase 1 of the two-phase flip). It is only read and
	/// written **under this same lock**, which makes it atomic with the test in <see cref="TryAdd"/>.
	/// </summary>
	private bool intakeClosed;

	/// <summary>The generation counter that decides whether a merged ListChildren result may be handed to <c>PutChildren</c>.</summary>
	private long generation;
	/// <summary>A count that answers "is the ledger empty" without taking the lock (for the early return on the hot path).</summary>
	private int entryCount;
	/// <summary>The number of entries not yet materialized, for deciding back-pressure without taking the lock.</summary>
	private int pendingCount;

	private long flushes;
	private long flushFailures;
	private long conflicts;
	private long cancels;
	private long discards;
	private long droppedAudits;

	/// <summary>How many Persisted leftovers are kept (the best-effort narrowing of the existence-check window).</summary>
	private const int PersistedKeepLimit = 4096;
	/// <summary>The cap on audit rows one entry may hold, so that repeatedly touching a pending entry whose error is latched cannot grow without bound.</summary>
	private const int AuditsPerEntryLimit = 256;
	/// <summary>The cap on the orphan audit queue.</summary>
	private const int OrphanAuditLimit = 8192;

	/// <summary>
	/// Whether the ledger is empty (= neither pending entries nor Persisted leftovers). **It takes no lock**,
	/// so every hot path can pass straight through at almost zero cost when metadata write-back is disabled.
	/// </summary>
	public bool IsEmpty {
		get { return Volatile.Read(ref this.entryCount) == 0; }
	}

	/// <summary>The current generation (the guard between a ListChildren snapshot and PutChildren).</summary>
	public long Generation {
		get { lock (this) { return this.generation; } }
	}

	/// <summary>How many entries are not yet materialized (for back-pressure and status; no lock).</summary>
	public int PendingCount {
		get { return Volatile.Read(ref this.pendingCount); }
	}

	/// <summary>How many rows sit in the orphan audit queue (no lock-free approximation, because this is called rarely).</summary>
	public int QueuedAuditCount {
		get { lock (this) { return this.orphanAudits.Count; } }
	}

	// ------------------------------------------------------------------
	// Registration and lookup
	// ------------------------------------------------------------------

	/// <summary>
	/// Registers a pending inode. Returns null when the same (parent, name) is already in the ledger
	/// (the caller must **report that as EEXIST** - falling back to write-through would not collide with the
	/// pending sibling, because it has no row in the database, and two inodes with the same name would result).
	/// </summary>
	public PendingInode? TryAdd(Inode inode, PendingAudit? audit, bool bornExclusive, out PendingAddResult result) {
		lock (this) {
			// **The intake-closed test happens inside the ledger lock** (B-9). The two-phase flip can run to
			// completion between the caller deciding "it is fine to go pending now" and arriving here, so
			// testing outside would let **a pending entry be born after the flip finished**. Making the test
			// and the registration atomic removes the window.
			if (this.intakeClosed) {
				result = PendingAddResult.IntakeClosed;
				return null;
			}
			var names = this.NamesOf(inode.ParentId);
			if (names.ContainsKey(inode.Name)) {
				result = PendingAddResult.NameConflict;
				return null;
			}
			var entry = new PendingInode { Inode = inode, FirstDirtyAt = DateTime.UtcNow, BornExclusive = bornExclusive };
			this.byId[inode.Id] = entry;
			names[inode.Name] = entry;
			this.AppendAuditLocked(entry, audit);
			// **A create bumps the generation too** - without it, a child list can slip past the ListChildren
			// generation guard and be baked in as "stat succeeds but ls does not show it".
			++this.generation;
			this.Recount();
			result = PendingAddResult.Added;
			return entry;
		}
	}

	/// <summary>
	/// Closes or reopens intake of new pending entries (phase 1 of the two-phase flip, B-9).
	/// **The switch happens under the ledger lock**, so there is no window against the test in <see cref="TryAdd"/>.
	/// </summary>
	public void SetIntakeClosed(bool closed) {
		lock (this) {
			this.intakeClosed = closed;
		}
	}

	/// <summary>Whether intake is closed (for the effective-mode display in status).</summary>
	public bool IntakeClosed {
		get { lock (this) { return this.intakeClosed; } }
	}

	/// <summary>Looks up a ledger entry by id (Persisted ones are returned too). Null when there is none.</summary>
	public PendingInode? Find(long id) {
		lock (this) {
			this.byId.TryGetValue(id, out var entry);
			return entry;
		}
	}

	/// <summary>Looks up a ledger entry by (parent, name) (Persisted ones are returned too). Null when there is none.</summary>
	public PendingInode? Find(long parentId, string name) {
		lock (this) {
			if (!this.byParent.TryGetValue(parentId, out var names)) { return null; }
			names.TryGetValue(name, out var entry);
			return entry;
		}
	}

	/// <summary>Returns an <see cref="Inode"/> that is not materialized yet (for <see cref="InodeCache"/>'s pending-resolution hook).</summary>
	public Inode? FindPendingRow(long id) {
		lock (this) {
			if (!this.byId.TryGetValue(id, out var entry)) { return null; }
			if (entry.State == PendingState.Persisted) { return null; }
			return entry.Inode;
		}
	}

	/// <summary>Returns a not-yet-materialized <see cref="Inode"/> by (parent, name) (for the pending-resolution hook).</summary>
	public Inode? FindPendingRow(long parentId, string name) {
		lock (this) {
			if (!this.byParent.TryGetValue(parentId, out var names)) { return null; }
			if (!names.TryGetValue(name, out var entry)) { return null; }
			if (entry.State == PendingState.Persisted) { return null; }
			return entry.Inode;
		}
	}

	// ------------------------------------------------------------------
	// Coalescing (attribute changes, rename, truncate to 0)
	// ------------------------------------------------------------------

	/// <summary>
	/// Applies an overwrite to a pending inode **atomically inside the ledger lock**.
	/// <paramref name="mutate"/> must be a short action that does nothing but write <see cref="Inode"/> fields
	/// (the database and the other caches must not be touched from inside this lock).
	/// </summary>
	public CoalesceResult TryCoalesce(long id, Action<Inode> mutate, PendingAudit? audit) {
		lock (this) {
			if (!this.byId.TryGetValue(id, out var entry)) { return CoalesceResult.NotPending; }
			if (entry.State == PendingState.Persisted) { return CoalesceResult.NotPending; }
			// Modification during a flush is not allowed (it would disagree with the snapshot the flush
			// transaction fixed). The caller waits for completion on the NSGate and falls back to write-through.
			if (entry.State != PendingState.Dirty) { return CoalesceResult.Busy; }
			mutate(entry.Inode);
			this.AppendAuditLocked(entry, audit);
			++this.generation;
			return CoalesceResult.Coalesced;
		}
	}

	/// <summary>
	/// Re-points a pending entry's parent and name (a coalesced rename). When the destination name is taken by
	/// **another entry that is not materialized yet**, it returns **false rather than overwriting** (the
	/// caller falls back to write-through).
	/// <para>
	/// When the name is held by a <see cref="PendingState.Persisted"/> leftover, **that leftover is removed
	/// from the ledger and the rename proceeds**. A leftover is only the best-effort narrowing of the
	/// existence-check window and is never relied on for correctness (the row is in the database, so
	/// <c>ls</c> and <c>stat</c> resolve there). Giving up here would mean
	/// "a rename onto a name this mount materialized always falls back to write-through",
	/// which effectively kills doing rename-over-existing in a single transaction (heuristic a).
	/// </para>
	/// </summary>
	public bool TryReindex(PendingInode entry, long newParentId, string newName, PendingAudit? audit) {
		lock (this) {
			if (entry.State == PendingState.Persisted && !this.byId.ContainsKey(entry.Inode.Id)) { return false; }
			var names = this.NamesOf(newParentId);
			if (names.TryGetValue(newName, out var occupant) && !ReferenceEquals(occupant, entry)) {
				if (occupant.State != PendingState.Persisted) { return false; }
				this.byId.Remove(occupant.Inode.Id);
				this.RemoveFromParentIndexLocked(occupant);
				this.Recount();
			}
			this.RemoveFromParentIndexLocked(entry);
			entry.Inode.ParentId = newParentId;
			entry.Inode.Name = newName;
			this.NamesOf(newParentId)[newName] = entry;
			this.AppendAuditLocked(entry, audit);
			++this.generation;
			return true;
		}
	}

	/// <summary>
	/// Makes a pending inode "size 0, no body" (truncate to 0). **Call this while holding the NSGate**,
	/// because it is one of two steps together with discarding the dirty data.
	/// </summary>
	public bool TruncateToZero(PendingInode entry, DateTime when) {
		lock (this) {
			if (entry.State != PendingState.Dirty) { return false; }
			entry.Inode.Size = 0;
			// **The data_id is not cleared** (docs/data-id-lifecycle.md). It is the identity of the file body,
			// so a truncate leaves it unchanged. The invariant "no {prefix}data row = the content is empty"
			// keeps everything consistent.
			entry.Inode.Mtime = when;
			entry.Inode.Ctime = when;
			++this.generation;
			return true;
		}
	}

	// ------------------------------------------------------------------
	// Flush state transitions
	// ------------------------------------------------------------------

	/// <summary>
	/// Starts a flush. Moves every entry to <see cref="PendingState.Flushing"/> and returns **a snapshot of
	/// the rows at that moment (a copy of each <see cref="Inode"/>) together with a snapshot of the audit
	/// rows**. The flush transaction writes from this return value alone (reading the live
	/// <see cref="Inode"/> could cross a coalesce and INSERT under a different parent).
	/// </summary>
	public List<PendingFlush> BeginFlush(List<PendingInode> chain) {
		var items = new List<PendingFlush>(chain.Count);
		lock (this) {
			foreach (var entry in chain) {
				if (entry.State == PendingState.Persisted) { continue; }
				entry.State = PendingState.Flushing;
				items.Add(new PendingFlush {
					Entry = entry,
					Row = DirtyNamespace.CloneRow(entry.Inode),
					Audits = new List<PendingAudit>(entry.Audits),
				});
			}
			this.Recount();
		}
		return items;
	}

	/// <summary>Marks a flush as successful (the audit rows that were written are dropped too).</summary>
	public void MarkPersisted(List<PendingFlush> items) {
		lock (this) {
			foreach (var item in items) {
				item.Entry.State = PendingState.Persisted;
				item.Entry.Error = null;
				item.Entry.Audits.Clear();
			}
			++this.generation;
			++this.flushes;
			this.Recount();
		}
	}

	/// <summary>Marks a flush as failed (back to dirty, to be retried at the next trigger).</summary>
	public void MarkFailed(List<PendingFlush> items, string error) {
		lock (this) {
			foreach (var item in items) {
				if (item.Entry.State == PendingState.Persisted) { continue; }
				item.Entry.State = PendingState.Dirty;
				item.Entry.Error = error;
			}
			++this.flushFailures;
			this.Recount();
		}
	}

	/// <summary>Removes an entry from the ledger entirely (an exact cancellation, a discard because the parent vanished, or cleanup after a write-through delete).</summary>
	public void Forget(long id) {
		lock (this) {
			if (!this.byId.Remove(id, out var entry)) { return; }
			this.RemoveFromParentIndexLocked(entry);
			++this.generation;
			this.Recount();
		}
	}

	/// <summary>
	/// Re-points a pending inode's id to a different id (the inode-level Rekey that adopts an existing id when
	/// directory names collide).
	/// **The <c>parent_id</c> of pending children, the parent index and the ids on audit rows are re-pointed
	/// as well** - without that the loser's subtree becomes unreachable. **Call it after the commit**:
	/// rewriting memory inside the transaction would, on a rollback, leave a pending entry carrying the id of
	/// a directory that really exists, and an rmdir of it would cancel out exactly and never touch the database.
	/// </summary>
	public void Rekey(PendingInode entry, long newId) {
		lock (this) {
			var oldId = entry.Inode.Id;
			if (oldId == newId) { return; }
			// Remove any leftover at the adopted id (a materialized entry, say) first, so that an unchecked
			// overwrite cannot leave the structure half-wired.
			if (this.byId.TryGetValue(newId, out var stale) && !ReferenceEquals(stale, entry)) {
				this.byId.Remove(newId);
				this.RemoveFromParentIndexLocked(stale);
				Logger.Warning("metadata write-back: discarded the ledger entry at the Rekey destination id:", newId, " state:", stale.State);
			}
			this.byId.Remove(oldId);
			entry.Inode.Id = newId;
			this.byId[newId] = entry;
			foreach (var audit in entry.Audits) {
				if (audit.TargetId != oldId) { continue; }
				audit.TargetId = newId;
			}
			this.RekeyChildrenLocked(oldId, newId);
			++this.generation;
			this.Recount();
		}
	}

	/// <summary>Moves entries whose parent is the old id under the new id as part of a Rekey. Call this while holding the lock.</summary>
	private void RekeyChildrenLocked(long oldParentId, long newParentId) {
		if (!this.byParent.Remove(oldParentId, out var names)) { return; }
		var target = this.NamesOf(newParentId);
		foreach (var kv in names) {
			// On a name collision the entry being moved is dropped rather than overwriting blindly (keeping
			// both would leave a half-wired entry).
			if (target.TryGetValue(kv.Key, out var occupant) && !ReferenceEquals(occupant, kv.Value)) {
				this.byId.Remove(kv.Value.Inode.Id);
				++this.discards;
				Logger.Warning("metadata write-back: discarded a pending entry because the Rekey destination already had that name name:", kv.Key, " id:", kv.Value.Inode.Id);
				continue;
			}
			kv.Value.Inode.ParentId = newParentId;
			target[kv.Key] = kv.Value;
			foreach (var audit in kv.Value.Audits) {
				if (audit.ParentId != oldParentId) { continue; }
				audit.ParentId = newParentId;
			}
		}
	}

	// ------------------------------------------------------------------
	// Ancestor and child indexes
	// ------------------------------------------------------------------

	/// <summary>
	/// Returns the pending ancestors of <paramref name="entry"/> **ordered from the root down** (excluding the
	/// entry itself). The flush transaction INSERTs in this order, because without the parent first the inode
	/// would be an unreachable orphan.
	/// When the depth limit is hit, <paramref name="truncated"/> is true (the caller materializes the parent
	/// side first).
	/// </summary>
	public List<PendingInode> AncestorChain(PendingInode entry, out bool truncated) {
		var chain = new List<PendingInode>();
		truncated = false;
		lock (this) {
			var parentId = entry.Inode.ParentId;
			for (var hop = 0; hop < DirtyNamespace.AncestorHopLimit; ++hop) {
				if (!this.byId.TryGetValue(parentId, out var parent)) { break; }
				if (parent.State == PendingState.Persisted) { break; }
				chain.Add(parent);
				parentId = parent.Inode.ParentId;
			}
			// The limit was hit = there are still pending ancestors further up.
			if (chain.Count == DirtyNamespace.AncestorHopLimit && this.byId.TryGetValue(parentId, out var more) && more.State != PendingState.Persisted) {
				truncated = true;
			}
		}
		chain.Reverse();
		return chain;
	}

	/// <summary>The cap on how many pending ancestors go into one flush transaction (beyond it, materialization proceeds in steps from the parent side).</summary>
	private const int AncestorHopLimit = 256;

	/// <summary>Whether the directory has any not-yet-materialized children directly under it (for rmdir and IsDirectoryEmpty).</summary>
	public bool HasPendingChildren(long parentId) {
		if (this.IsEmpty) { return false; }
		lock (this) {
			if (!this.byParent.TryGetValue(parentId, out var names)) { return false; }
			foreach (var kv in names) {
				if (kv.Value.State == PendingState.Persisted) { continue; }
				return true;
			}
			return false;
		}
	}

	/// <summary>The not-yet-materialized child inodes directly under the directory (for the ListChildren merge).</summary>
	public List<Inode> PendingChildren(long parentId) {
		var list = new List<Inode>();
		lock (this) {
			if (!this.byParent.TryGetValue(parentId, out var names)) { return list; }
			foreach (var kv in names) {
				if (kv.Value.State == PendingState.Persisted) { continue; }
				list.Add(kv.Value.Inode);
			}
			return list;
		}
	}

	/// <summary>The ids of the not-yet-materialized children directly under the directory (for fsyncdir).</summary>
	public List<long> PendingChildIds(long parentId) {
		var list = new List<long>();
		lock (this) {
			if (!this.byParent.TryGetValue(parentId, out var names)) { return list; }
			foreach (var kv in names) {
				if (kv.Value.State == PendingState.Persisted) { continue; }
				list.Add(kv.Value.Inode.Id);
			}
			return list;
		}
	}

	/// <summary>Returns the not-yet-materialized entries oldest first (for FlushAll and back-pressure).</summary>
	public List<long> PendingIdsOldestFirst() {
		lock (this) {
			var entries = new List<PendingInode>();
			foreach (var kv in this.byId) {
				if (kv.Value.State == PendingState.Persisted) { continue; }
				entries.Add(kv.Value);
			}
			entries.Sort((a, b) => a.FirstDirtyAt.CompareTo(b.FirstDirtyAt));
			var ids = new List<long>(entries.Count);
			foreach (var entry in entries) {
				ids.Add(entry.Inode.Id);
			}
			return ids;
		}
	}

	/// <summary>
	/// Whether this inode has a flush failure latched. Used to decide whether to escalate <c>close</c> from
	/// "mark only" to "synchronous flush plus report" (error floor 1).
	/// </summary>
	public bool HasErrorLatch(long id) {
		if (this.IsEmpty) { return false; }
		lock (this) {
			if (!this.byId.TryGetValue(id, out var entry)) { return false; }
			return entry.Error != null;
		}
	}

	/// <summary>
	/// Renders each not-yet-materialized entry as one human-readable line, so that an unmount which could not
	/// write everything out in time leaves **what is being lost** in the log. Stops after
	/// <paramref name="limit"/> entries.
	/// </summary>
	public PendingLossSummary SnapshotPendingLoss(int limit) {
		var summary = new PendingLossSummary();
		lock (this) {
			foreach (var kv in this.byId) {
				if (kv.Value.State == PendingState.Persisted) { continue; }
				var row = kv.Value.Inode;
				++summary.Total;
				if (row.IsDirectory) { ++summary.Dirs; }
				if (!row.IsDirectory) { ++summary.Files; summary.Bytes += row.Size; }
				if (summary.Sample.Count >= limit) { continue; }
				summary.Sample.Add(new PendingLossRow { Row = row, State = kv.Value.State, Error = kv.Value.Error });
			}
			return summary;
		}
	}

	/// <summary>Entries that have been pending since before <paramref name="cutoff"/> (for the time-based trigger).</summary>
	public List<long> PendingIdsOlderThan(DateTime cutoff) {
		lock (this) {
			var ids = new List<long>();
			foreach (var kv in this.byId) {
				if (kv.Value.State == PendingState.Persisted) { continue; }
				if (kv.Value.FirstDirtyAt > cutoff) { continue; }
				ids.Add(kv.Value.Inode.Id);
			}
			return ids;
		}
	}

	// ------------------------------------------------------------------
	// Audit rows
	// ------------------------------------------------------------------

	/// <summary>
	/// Appends an audit row to an entry. It must happen **under the same lock as the enumeration
	/// (<see cref="BeginFlush"/>) and the Clear in <see cref="MarkPersisted"/>**, otherwise it produces
	/// <c>Collection was modified</c> or silently drops rows, so this API is the only way in.
	/// </summary>
	public void AppendAudit(PendingInode entry, PendingAudit? audit) {
		lock (this) {
			this.AppendAuditLocked(entry, audit);
		}
	}

	/// <summary>Appends an audit row under the lock (with a cap).</summary>
	private void AppendAuditLocked(PendingInode entry, PendingAudit? audit) {
		if (audit == null) { return; }
		if (entry.Audits.Count >= DirtyNamespace.AuditsPerEntryLimit) {
			++this.droppedAudits;
			Logger.Warning("metadata write-back: discarded audit rows because one inode exceeded the cap of ", DirtyNamespace.AuditsPerEntryLimit, " id:", entry.Inode.Id, " op:", audit.Op);
			return;
		}
		entry.Audits.Add(audit);
	}

	/// <summary>Returns a snapshot of an entry's audit rows (for moving them to the orphan queue on an exact cancellation).</summary>
	public List<PendingAudit> SnapshotAudits(PendingInode entry) {
		lock (this) {
			return new List<PendingAudit>(entry.Audits);
		}
	}

	/// <summary>
	/// Queues audit rows that have no entry behind them. Anything above the cap is dropped with a warning.
	/// <para>
	/// **No path queues into this any more.** The only producer, the exactly cancelled create/delete pair, was
	/// changed to a synchronous write in B-3. It is kept as the place <c>Api.FlushOrphanAudits</c> puts rows
	/// back when a write fails, and as the landing spot for any future path that has no sync point.
	/// **Before adding a path that queues here, decide whether those audit rows may be lost in a crash**
	/// (if they may not, write them synchronously).
	/// </para>
	/// </summary>
	public void QueueOrphanAudits(IEnumerable<PendingAudit> audits) {
		lock (this) {
			foreach (var audit in audits) {
				if (this.orphanAudits.Count >= DirtyNamespace.OrphanAuditLimit) {
					++this.droppedAudits;
					Logger.Warning("metadata write-back: the orphan audit queue hit its cap of ", DirtyNamespace.OrphanAuditLimit, ", so rows were discarded op:", audit.Op, " target:", audit.TargetId);
					return;
				}
				this.orphanAudits.Add(audit);
			}
		}
	}

	/// <summary>Takes the queued orphan audit rows (the flush side writes them; on failure it puts them back with <see cref="QueueOrphanAudits"/>).</summary>
	public List<PendingAudit> TakeOrphanAudits() {
		lock (this) {
			if (this.orphanAudits.Count == 0) { return new List<PendingAudit>(); }
			var taken = new List<PendingAudit>(this.orphanAudits);
			this.orphanAudits.Clear();
			return taken;
		}
	}

	// ------------------------------------------------------------------
	// Trimming, statistics and invariants
	// ------------------------------------------------------------------

	/// <summary>
	/// Trims the oldest Persisted leftovers once they exceed <see cref="PersistedKeepLimit"/>
	/// (entries that are not materialized are never dropped). Leftovers are not relied on for correctness, so
	/// dropping them is safe.
	/// </summary>
	public void PrunePersisted() {
		lock (this) {
			var persisted = new List<PendingInode>();
			foreach (var kv in this.byId) {
				if (kv.Value.State != PendingState.Persisted) { continue; }
				persisted.Add(kv.Value);
			}
			if (persisted.Count <= DirtyNamespace.PersistedKeepLimit) { return; }
			persisted.Sort((a, b) => a.FirstDirtyAt.CompareTo(b.FirstDirtyAt));
			var dropCount = persisted.Count - DirtyNamespace.PersistedKeepLimit;
			for (var i = 0; i < dropCount; ++i) {
				this.byId.Remove(persisted[i].Inode.Id);
				this.RemoveFromParentIndexLocked(persisted[i]);
			}
			this.Recount();
		}
	}

	public void CountConflict() { lock (this) { ++this.conflicts; } }
	public void CountCancel() { lock (this) { ++this.cancels; } }
	public void CountDiscard() { lock (this) { ++this.discards; } }

	/// <summary>A statistics snapshot (for Layer 3 status).</summary>
	public DirtyNamespaceStats Stats() {
		lock (this) {
			var pending = 0;
			foreach (var kv in this.byId) {
				if (kv.Value.State == PendingState.Persisted) { continue; }
				++pending;
			}
			return new DirtyNamespaceStats {
				PendingInodes = pending,
				TrackedEntries = this.byId.Count,
				QueuedAudits = this.orphanAudits.Count,
				DroppedAudits = this.droppedAudits,
				Flushes = this.flushes,
				FlushFailures = this.flushFailures,
				Conflicts = this.conflicts,
				Cancels = this.cancels,
				Discards = this.discards,
			};
		}
	}

	/// <summary>
	/// Checks that <c>byId</c> and <c>byParent</c> still correspond both ways (regression detection).
	/// It is O(n), so it only runs **when Trace is enabled**. A broken correspondence produces a warning,
	/// because a half-wired entry becomes "a ghost that never appears in ls yet still gets flushed", and that
	/// must not pass silently.
	/// </summary>
	public void VerifyIndexIfTracing() {
		if (!Logger.IsTraceEnabled) { return; }
		lock (this) {
			foreach (var kv in this.byId) {
				if (!this.byParent.TryGetValue(kv.Value.Inode.ParentId, out var names)) {
					Logger.Warning("DirtyNamespace inconsistency: the parent is missing from byParent id:", kv.Key, " parent:", kv.Value.Inode.ParentId);
					continue;
				}
				if (!names.TryGetValue(kv.Value.Inode.Name, out var found) || !ReferenceEquals(found, kv.Value)) {
					Logger.Warning("DirtyNamespace inconsistency: the name in byParent does not match id:", kv.Key, " name:", kv.Value.Inode.Name);
				}
			}
			foreach (var pkv in this.byParent) {
				foreach (var nkv in pkv.Value) {
					if (this.byId.TryGetValue(nkv.Value.Inode.Id, out var back) && ReferenceEquals(back, nkv.Value)) { continue; }
					Logger.Warning("DirtyNamespace inconsistency: a byParent entry that is not in byId parent:", pkv.Key, " name:", nkv.Key);
				}
			}
		}
	}

	/// <summary>Gets the (name -> entry) dictionary of the parent index, creating it when absent. Call this while holding the lock.</summary>
	private Dictionary<string, PendingInode> NamesOf(long parentId) {
		if (this.byParent.TryGetValue(parentId, out var names)) { return names; }
		names = new Dictionary<string, PendingInode>();
		this.byParent[parentId] = names;
		return names;
	}

	/// <summary>Removes an entry from the parent index (leaves it alone when a different entry has taken that name). Call this while holding the lock.</summary>
	private void RemoveFromParentIndexLocked(PendingInode entry) {
		if (!this.byParent.TryGetValue(entry.Inode.ParentId, out var names)) { return; }
		if (!names.TryGetValue(entry.Inode.Name, out var found)) { return; }
		if (!ReferenceEquals(found, entry)) { return; }
		names.Remove(entry.Inode.Name);
		if (names.Count > 0) { return; }
		this.byParent.Remove(entry.Inode.ParentId);
	}

	/// <summary>Updates the counters read without the lock. Call this while holding the lock.</summary>
	private void Recount() {
		var pending = 0;
		foreach (var kv in this.byId) {
			if (kv.Value.State == PendingState.Persisted) { continue; }
			++pending;
		}
		Volatile.Write(ref this.pendingCount, pending);
		Volatile.Write(ref this.entryCount, this.byId.Count);
	}

	/// <summary>The row snapshot a flush transaction uses (it detaches the live <see cref="Inode"/> from the transaction).</summary>
	private static Inode CloneRow(Inode inode) {
		return new Inode {
			id = inode.Id,
			parent_id = inode.ParentId,
			name = inode.Name,
			uname = inode.UserName,
			gname = inode.GroupName,
			st_mode = inode.Mode,
			st_nlink = inode.NLink,
			st_size = inode.Size,
			st_mtime = inode.Mtime,
			st_ctime = inode.Ctime,
			link_target = inode.LinkTarget,
			is_junction = inode.IsJunction,
			data_id = inode.DataId,
			xattr_names = inode.xattr_names,
			xattr_values = inode.xattr_values,
			created_at = inode.created_at,
			created_by = inode.created_by,
			updated_at = inode.updated_at,
			updated_by = inode.updated_by,
		};
	}
}

/// <summary>The result of <see cref="DirtyNamespace.TryCoalesce"/>.</summary>
internal enum CoalesceResult
{
	/// <summary>Overwritten in the ledger (the database is not touched).</summary>
	Coalesced,
	/// <summary>Not pending (absent, or already materialized) = go write-through.</summary>
	NotPending,
	/// <summary>A flush is in progress = wait for it on the NSGate, then go write-through.</summary>
	Busy,
}

/// <summary>The state of one pending inode.</summary>
internal enum PendingState
{
	/// <summary>Not flushed yet. The only state in which coalescing is allowed.</summary>
	Dirty,
	/// <summary>A flush transaction is running. Coalescing is refused (the caller waits, then goes write-through).</summary>
	Flushing,
	/// <summary>Materialized in the database. Later operations are write-through. The entry lingers briefly to narrow the existence-check window.</summary>
	Persisted,
}

/// <summary>
/// One ledger entry. Its <see cref="Inode"/> is **the same instance** as the one in
/// <see cref="InodeCache"/>, and coalescing rewrites that instance under the ledger lock, so the cache and
/// the ledger cannot structurally disagree.
/// The fields are only touched under <see cref="DirtyNamespace"/>'s lock.
/// </summary>
internal sealed class PendingInode
{
	public required Inode Inode { get; init; }
	public PendingState State { get; set; } = PendingState.Dirty;
	/// <summary>When it became pending (the reference point for the time trigger and for trimming).</summary>
	public required DateTime FirstDirtyAt { get; init; }
	/// <summary>
	/// Whether it was born from a create with <c>O_EXCL</c> (under the `defer` setting). **It is fixed at
	/// creation time** - re-reading the setting at flush time would let a live change alter this inode's
	/// conflict-resolution semantics after the fact.
	/// While it is true, a name collision during a flush **must not delete the occupant** (the application was
	/// told "only I created it", so silently crushing the other side is the worst possible outcome).
	/// </summary>
	public bool BornExclusive { get; init; }
	/// <summary>The most recent flush failure (a latch for diagnostics).</summary>
	public string? Error { get; set; }
	/// <summary>Audit rows captured at operation time. **Only touched under <see cref="DirtyNamespace"/>'s lock.**</summary>
	public List<PendingAudit> Audits { get; } = new();
}

/// <summary>The result of <see cref="DirtyNamespace.TryAdd"/>. It exists so the caller can tell the reasons for a null apart.</summary>
internal enum PendingAddResult
{
	/// <summary>Registered successfully.</summary>
	Added,
	/// <summary>The same (parent, name) is already in the ledger -> **report EEXIST** (do not fall back to write-through).</summary>
	NameConflict,
	/// <summary>Intake is closed (phase 1 of the two-phase flip) -> **fall back to write-through**.</summary>
	IntakeClosed,
}

/// <summary>
/// One control message that arrived over NOTIFY (B-8). It is queued so that it can be applied in order
/// **off the listener thread**.
/// </summary>
internal sealed class ControlWork
{
	public required string Control { get; init; }
	public required string? Key { get; init; }
	public required string? Value { get; init; }
}

/// <summary>A snapshot of one loss-report line (<see cref="Api"/> does the formatting and adds the path).</summary>
internal sealed class PendingLossRow
{
	public required Inode Row { get; init; }
	public required PendingState State { get; init; }
	public required string? Error { get; init; }
}

/// <summary>
/// The aggregate behind the loss report. It exists so that **entries beyond the cut-off are not summarized
/// as "and N more" but reported with their totals** (a rounded number is something the reader can neither
/// verify nor correct).
/// </summary>
internal sealed class PendingLossSummary
{
	/// <summary>The detail lines, up to the limit.</summary>
	public List<PendingLossRow> Sample { get; } = new();
	/// <summary>The total number of pending entries (before the cut-off).</summary>
	public int Total { get; set; }
	/// <summary>Of those, how many are directories.</summary>
	public int Dirs { get; set; }
	/// <summary>Of those, how many are regular files.</summary>
	public int Files { get; set; }
	/// <summary>The sum of the files' logical sizes (an estimate of how many bytes are lost).</summary>
	public long Bytes { get; set; }
}

/// <summary>
/// One input to a flush transaction. <see cref="Row"/> is the copy fixed when the flush started, and
/// **the transaction INSERTs from that alone** (it never reads the live <see cref="PendingInode.Inode"/>).
/// </summary>
internal sealed class PendingFlush
{
	public required PendingInode Entry { get; init; }
	public required Inode Row { get; init; }
	public required List<PendingAudit> Audits { get; init; }
}

/// <summary>
/// An audit row captured at operation time. **Calling <c>WriteAudit</c> directly from the background flush
/// would turn occurred_at and the caller into the flush side's (the background thread's) values and make the
/// audit lie**, so the operation time and the caller are captured here instead.
/// </summary>
internal sealed class PendingAudit
{
	public required string Op { get; init; }
	/// <summary>The target inode id. Mutable, because a Rekey re-points it.</summary>
	public required long TargetId { get; set; }
	/// <summary>The parent inode id. Mutable, because a Rekey re-points it.</summary>
	public long? ParentId { get; set; }
	public string? Name { get; set; }
	/// <summary>The JSON that goes into the detail column (<c>{}</c> when null).</summary>
	public string? DetailJson { get; init; }
	/// <summary>**The time of the operation** (not the time of the flush). The monthly partition is ensured from this value too.</summary>
	public required DateTime When { get; init; }
	public long? Uid { get; init; }
	public string? Uname { get; init; }
	public string? Domain { get; init; }
}

/// <summary>The result of <see cref="DirtyNamespace.Stats"/>.</summary>
internal sealed record DirtyNamespaceStats
{
	/// <summary>How many inodes are not materialized yet.</summary>
	public required int PendingInodes { get; init; }
	/// <summary>The total number of entries in the ledger (including Persisted leftovers).</summary>
	public required int TrackedEntries { get; init; }
	/// <summary>How many orphan audit rows have not been written yet.</summary>
	public required int QueuedAudits { get; init; }
	/// <summary>The cumulative number of audit rows discarded for exceeding a cap.</summary>
	public required long DroppedAudits { get; init; }
	/// <summary>How many namespace flushes succeeded.</summary>
	public required long Flushes { get; init; }
	/// <summary>How many namespace flushes failed.</summary>
	public required long FlushFailures { get; init; }
	/// <summary>How many name collisions were resolved during a flush (cross-client last-flush-wins).</summary>
	public required long Conflicts { get; init; }
	/// <summary>How many pending entries were deleted before any flush (cancelled out exactly).</summary>
	public required long Cancels { get; init; }
	/// <summary>How many pending entries were discarded because their parent was gone or similar.</summary>
	public required long Discards { get; init; }
}
