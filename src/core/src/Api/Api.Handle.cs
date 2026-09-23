namespace Pgfs.Core.Api;

using Logging;
using Models;

/// <summary>
/// The **handle-context (stage B)** part of <see cref="Api"/>. The design lives in
/// <see href="../../../../docs/handle-context.md"/>, in the section on stage B's acceptance criteria and API surface.
///
/// <para>
/// <b>The gist</b>: identifying an open file used to be **path-first**; stage B flips that so that
/// **<see cref="OpenFileContext.InodeId"/> is primary and the path is secondary**. The OS layer passes the
/// handle context that was fixed at open time, and **resolving the inode stays inside this layer**.
/// </para>
///
/// <para>
/// <b>Why the context is passed rather than a handle id</b>: Dokan puts the object straight into
/// <c>DokanFileInfo.Context</c> and never goes through <see cref="HandleTable"/>. An id-based API would mean
/// **only Windows pays for a table lookup on every call**. The FUSE side converts at the top of each
/// callback with <c>Handles.Get(fi.fh)</c>.
/// </para>
///
/// <para>
/// <b>Why resolution is kept inside</b>: so that how attributes are re-read (a <see cref="GetById"/> every
/// time, or a generation-stamped cache) can be swapped later **without rewriting the FUSE and Dokan
/// adapters**. Whatever the performance gate
/// (<see href="../../../../docs/performance.md"/>) decides only changes what is in here.
/// </para>
/// </summary>
public partial class Api
{
	// ------------------------------------------------------------------
	// Resolution (InodeId -> Inode)
	// ------------------------------------------------------------------

	/// <summary>
	/// Resolves **the current** inode from a handle context (the main path of stage B).
	/// <para>
	/// **Re-reading every time is the whole job.** Continuing to use the <see cref="OpenFileContext.Inode"/>
	/// the handle is holding would mean that after a flush re-points the <c>data_id</c> (materialize, or a
	/// move to a hardlink sibling) or after a remote invalidate, **reads and writes keep using a stale Size
	/// and DataId** (problem 2).
	/// </para>
	/// <para>
	/// **An id-based lookup structurally cannot hit less often than a path-based one** - in
	/// <see cref="InodeCache"/> <c>byId</c> is authoritative, and <c>EvictIfOverCapacity</c> keeps the two
	/// consistent by cleaning up any <c>byPath</c> entry that points at an id no longer in <c>byId</c>.
	/// **Removing that cleanup breaks the premise**, so anything that relaxes it has to revisit this too.
	/// </para>
	/// </summary>
	/// <exception cref="Api.StaleHandleException">The inode is already gone (unlinked, and so on).</exception>
	public Inode ResolveHandle(OpenFileContext handle) {
		var inode = this.TryResolveHandle(handle);
		if (inode == null) { throw new Api.StaleHandleException(handle.InodeId); }
		return inode;
	}

	/// <summary>
	/// The "return <c>null</c> when it is gone" variant of <see cref="ResolveHandle"/>.
	/// <para>
	/// **Only the flush and close sync points use this.** Flushing an inode that is gone means "there is
	/// nothing to write", not an error (the delete path already discarded the dirty data). This entry point
	/// exists to **preserve exactly** the behaviour FUSE's <c>FlushPath</c> had up to stage A, which returned
	/// 0 when the file really was gone.
	/// </para>
	/// </summary>
	public Inode? TryResolveHandle(OpenFileContext handle) {
		var inode = this.GetById(handle.InodeId);
		if (inode != null) {
			handle.Inode = inode;
			return inode;
		}
		// **Stage C-2: the name is gone, but this handle is keeping the body alive** (POSIX's fd after an
		// unlink). Return the snapshot so reads and writes continue. The body is dropped on the last close.
		// The point here is **not to clobber the snapshot with null**.
		if (this.openInodes.IsOrphan(handle.InodeId)) { return handle.Inode; }
		// It really is gone. **Do not keep the stale Inode** (otherwise the next call keeps holding an inode
		// that no longer exists).
		handle.Inode = null;
		return null;
	}

	// ------------------------------------------------------------------
	// Opening and closing handles (stage C-1: the reference count of a body)
	// ------------------------------------------------------------------

	/// <summary>
	/// **Tells Core that a handle was opened** (docs/handle-context.md, stage C-1).
	/// <para>
	/// The OS layer must call this **only where it actually created a handle** - FUSE's <c>open</c>,
	/// <c>create</c> and <c>opendir</c>, Dokan's <c>CreateFile</c>. **Never call it from an ephemeral context
	/// built to answer an attribute query** (there is no place that closes it, so the count never comes back).
	/// </para>
	/// <para>
	/// <b>It is not folded into <see cref="HandleTable.Rent"/></b> for the same reason. Dokan does not go
	/// through the table at all, and on top of that <c>FileSystem.Resolve</c> has a path that builds ephemeral
	/// contexts. **The only places that count are the ones that can honestly be called "opened".**
	/// </para>
	/// <para>
	/// <b>Stage C-1 only counts and changes no behaviour.</b> Dropping the body on the last release is C-2.
	/// </para>
	/// </summary>
	public void OpenHandle(OpenFileContext handle) {
		// **Never count twice.** Two calls with the same context still count as one, so that a slip on the
		// caller's side does not turn into a broken count.
		if (handle.Counted) { return; }
		handle.Counted = true;
		this.openInodes.Acquire(handle.InodeId);
	}

	/// <summary>
	/// **Tells Core that a handle was closed.** Returns **how many references that body has left** (0 = this
	/// was the last release).
	/// <para>
	/// Call it from **exactly one place: close** - FUSE's <c>release</c> and <c>releasedir</c>, Dokan's
	/// <c>CloseFile</c>.
	/// **It must not be called from <c>flush</c>**: <c>flush</c> **arrives once per duplicated fd**, so the
	/// count would come back **while a live fd still exists** (measured by the handle-leak regression - bash's
	/// <c>exec 9&lt; file</c> closes the intermediate fd right after opening and sends one <c>flush</c> down).
	/// </para>
	/// </summary>
	public int CloseHandle(OpenFileContext handle) {
		// Uncounted (ephemeral) contexts and double closes are ignored. **Never let the count go negative.**
		if (!handle.Counted) { return this.openInodes.CountOf(handle.InodeId); }
		handle.Counted = false;
		var remaining = this.openInodes.Release(handle.InodeId);
		// **Stage C-2: on the last release, drop the body that was kept alive with its name gone.**
		if (remaining == 0) { this.DropOrphanIfGone(handle.InodeId); }
		return remaining;
	}

	/// <summary>
	/// Drops **a body that was kept with its name gone** when the last handle closes (stage C-2).
	/// <para>
	/// **It is only dropped when the inode row really is gone.** The mark is set inside the delete
	/// transaction, so **if that transaction rolled back, or the name was re-created**, the mark can survive
	/// on its own. Without this check the code would **delete the body of a live file**.
	/// </para>
	/// <para>A failure here still lets the close succeed (it is logged rather than swallowed). **Whatever is
	/// left behind is picked up by the prune of C-3.**</para>
	/// </summary>
	private void DropOrphanIfGone(long inodeId) {
		if (!this.openInodes.TryTakeOrphan(inodeId, out var dataId)) { return; }
		if (this.GetById(inodeId) != null) {
			Logger.Warning("last release: the inode is alive, so the body is kept inode:", inodeId, " data:", dataId);
			return;
		}
		try {
			this.DropOrphanData(dataId);
		} catch (System.Exception ex) {
			Logger.Error("could not drop the body on the last release data_id:", dataId, " ", ex);
		}
	}

	/// <summary>Drops an orphaned body (its chunks and its data row) in one transaction.</summary>
	private void DropOrphanData(long dataId) {
		// Discard the unflushed dirty data first. **Otherwise the background flush writes to a row that is gone.**
		this.ForgetDirtyUnderGate(dataId);
		this.contentCache.InvalidateData(dataId);
		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();
		this.LockData(conn, tx, dataId);
		DropAllChunks(conn, tx, dataId);
		this.DropDataRow(conn, tx, dataId);
		tx.Commit();
		if (Logger.IsDebugEnabled) { Logger.Debug("last release: dropped the body data:", dataId); }
	}

	/// <summary>
	/// Removes the entry from the dirty ledger. **The contract of <see cref="DirtySet.Forget"/> is to be
	/// called while holding that entry's <c>Gate</c>**, so it is taken here first.
	/// <para>
	/// Removing it without the Gate means **a second instance can be created while a thread still holds the
	/// old Gate, and two Gates can flush at the same time** (see the comment on <see cref="DirtySet.Forget"/>).
	/// No actual failure could be constructed today because <c>LockData</c> serializes it, but
	/// **a contract violation is not left standing** (review L-1).
	/// </para>
	/// <para>
	/// The lock order is **Gate, then DirtySet's internal lock**, the same direction the existing paths
	/// (<c>FlushLocked</c> and friends) use.
	/// </para>
	/// </summary>
	private void ForgetDirtyUnderGate(long dataId) {
		var file = this.dirtySet.Find(dataId);
		// Nothing to remove when it is not in the ledger (Forget is idempotent, but there is no Gate to take either).
		if (file == null) { return; }
		lock (file.Gate) {
			this.dirtySet.Forget(dataId);
		}
	}

	/// <summary>How many bodies are currently open (diagnostics; the <c>handles.inodes</c> of `pgfsctl status`).</summary>
	public int OpenInodeCount => this.openInodes.Count;

	// **There is deliberately no API for finding an orphan by path** (added once, then withdrawn after
	// measuring).
	// `Api.IsOrphanInode` and `Api.OrphanSnapshot` existed briefly, but **libfuse's high-level API sends both
	// `lookup` and `fstat` down as "the same path with fi = NULL"**, so once the OS layer remembers an
	// orphan's path it **answers `stat` as well as `fstat`, and a name that was deleted comes back to life**
	// (measured).
	// This is exactly why libfuse **renames** to `.fuse_hidden` instead of remembering.
	// The reasoning is in docs/handle-context.md, in the section on why `hard_remove` is not set.

	// ------------------------------------------------------------------
	// append (the filesystem decides where the end is)
	// ------------------------------------------------------------------

	/// <summary>
	/// **Appends at the end of the handle's file** (`O_APPEND` / `FILE_APPEND_DATA`).
	/// <para>
	/// **The point is that no offset is taken from the caller.** Using the end the OS decided on
	/// **overwrites whatever another mount appended**:
	/// </para>
	/// <list type="bullet">
	///   <item><b>Linux</b>: the kernel sends down an offset it computed from its own <c>i_size</c>. It knows
	///     nothing about another mount's appends, so the offset is stale (measured: after B extended the file
	///     by 6 bytes, A's append arrived with <c>off=5</c> and overwrote B's bytes).</item>
	///   <item><b>Windows</b>: Dokan only says "write at the end" via <c>WriteToEndOfFile</c>, so the
	///     filesystem decides. Up to stage A it used the stale <c>Inode.Size</c> the handle was holding and
	///     produced the same loss (reproduced by crossclient's
	///     <c>test_x_append_handle_sees_peer_growth</c> as a loss of 11 bytes down to 6).</item>
	/// </list>
	/// <para>
	/// <b>What is guaranteed, and where the guarantee stops</b> (docs/Mount.md, the append contract, is
	/// authoritative): <b>landing at the end is guaranteed</b>, but <b>atomicity is only guaranteed when the
	/// write is (1) write-through and (2) small enough to fit in a single callback</b>.
	/// </para>
	/// <list type="bullet">
	///   <item><b>With write-back enabled it cannot be made atomic</b> - the other mount's bytes are not in
	///     the database yet, so there is no way even in principle to know the real end. The local dirty size
	///     becomes authoritative.</item>
	///   <item><b>A single `write(2)` larger than `max_write` is split by FUSE into several callbacks</b>
	///     (measured: the negotiated value was 1 MiB, and a 4 MiB append arrived as 1 MiB four times).
	///     **The kernel computes the offsets of every callback at once from the initial `i_size`**, so if the
	///     peer appends partway through the split, **this method turns a loss into interleaving** (no bytes
	///     are lost, but the order is mixed).
	///     A local filesystem holds `i_rwsem` for the duration of one `write(2)` and therefore does not
	///     interleave, but **FUSE callbacks are independent requests, so pgfs cannot manufacture that
	///     guarantee**.</item>
	/// </list>
	/// <para>
	/// **Full atomicity (locking the inode row inside the write transaction to fix the end) is designed
	/// together with stage C** - it touches **the same place** as the inode row work needed for the reference
	/// count, and doing them separately would mean reasoning about Citus's shard-touch order (a write touches
	/// the inode shard exactly once, at the end of the transaction) twice.
	/// </para>
	/// </summary>
	/// <exception cref="Api.StaleHandleException">The inode is already gone.</exception>
	public int AppendData(OpenFileContext handle, ReadOnlySpan<byte> source) {
		var inode = this.ResolveHandle(handle);
		return this.WriteDataAppend(inode, source);
	}

	/// <summary>
	/// Where the append goes = the current end. **While write-back is active the local dirty size is authoritative.**
	/// <para>
	/// Looking only at `Inode.Size` means that **once the inode falls out of the LRU and is re-read from the
	/// database**, the append **lands in the middle of a dirty region that has not been flushed yet**
	/// (a pending inode is pinned, but a persisted inode is not write-back protected). While the entry is in
	/// `DirtySet`, that value wins.
	/// </para>
	/// </summary>
	private long AppendOffsetOf(Inode inode) {
		if (inode.DataId is not long dataId) { return inode.Size; }
		var file = this.dirtySet.Find(dataId);
		if (file == null) { return inode.Size; }
		return System.Math.Max(inode.Size, file.Size);
	}

	// ------------------------------------------------------------------
	// The six entry points that take a handle context (the existing Inode versions stay, as "ephemeral contexts")
	// ------------------------------------------------------------------

	/// <summary>
	/// The handle version of <see cref="ReadData(Inode, long, Span{byte})"/>. **Identity comes primarily from
	/// <see cref="OpenFileContext.InodeId"/>**, and the attributes are re-read on every call.
	/// </summary>
	public int ReadData(OpenFileContext handle, long offset, Span<byte> destination) => this.ReadData(this.ResolveHandle(handle), offset, destination);

	/// <summary>The handle version of <see cref="WriteData(Inode, long, ReadOnlySpan{byte})"/>.</summary>
	public int WriteData(OpenFileContext handle, long offset, ReadOnlySpan<byte> source) => this.WriteData(this.ResolveHandle(handle), offset, source);

	/// <summary>The handle version of <see cref="TruncateData(Inode, long)"/>.</summary>
	public bool TruncateData(OpenFileContext handle, long newLength) => this.TruncateData(this.ResolveHandle(handle), newLength);

	/// <summary>
	/// The handle version of <see cref="FlushInode(Inode)"/>. **A no-op when the inode is gone** (<see cref="TryResolveHandle"/>).
	/// </summary>
	public void FlushInode(OpenFileContext handle) {
		var inode = this.TryResolveHandle(handle);
		if (inode == null) { return; }
		this.FlushInode(inode);
	}

	/// <summary>
	/// The handle version of <see cref="CloseInode(Inode)"/>. **A no-op when the inode is gone** (<see cref="TryResolveHandle"/>).
	/// </summary>
	public void CloseInode(OpenFileContext handle) {
		var inode = this.TryResolveHandle(handle);
		if (inode == null) { return; }
		this.CloseInode(inode);
	}

	/// <summary>
	/// The handle version of <see cref="FlushDirectory(Inode)"/>. **A no-op when the inode is gone** (<see cref="TryResolveHandle"/>).
	/// </summary>
	public void FlushDirectory(OpenFileContext handle) {
		var dir = this.TryResolveHandle(handle);
		if (dir == null) { return; }
		this.FlushDirectory(dir);
	}

	// ------------------------------------------------------------------
	// Exceptions
	// ------------------------------------------------------------------

	/// <summary>
	/// Signals that the inode a handle points at **no longer exists** (stage B).
	/// <para>
	/// The caller (the OS layer) must map this to **the equivalent of ESTALE** (FUSE: <c>-ESTALE</c>,
	/// Dokan: <c>STATUS_FILE_INVALID</c>). **The POSIX semantics of "the fd stays alive after an unlink" are
	/// stage C**; stage B still fails with an error, **exactly as path resolution already failed with ENOENT**
	/// (so this is not a regression).
	/// </para>
	/// </summary>
	public sealed class StaleHandleException : System.Exception
	{
		public StaleHandleException(long inodeId)
			: base($"the inode {inodeId} this handle points at no longer exists") {
			this.InodeId = inodeId;
		}

		public long InodeId { get; }
	}
}
