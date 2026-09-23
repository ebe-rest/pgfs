namespace Pgfs.Core.Api;

using System.Collections.Concurrent;

/// <summary>
/// **The reference count of open inodes** (docs/handle-context.md, stage C-1).
///
/// <para>
/// <b>Why this is separate from <see cref="HandleTable"/></b>: there can be many handles but **only one
/// inode**, and "is anybody still holding it open" can only be asked **per inode**. On top of that
/// **Dokan never goes through the handle table** (it puts the object straight into
/// <c>DokanFileInfo.Context</c>), so counting table entries would not produce the same number on both
/// operating systems. Only because **both adapters explicitly call <see cref="Api.OpenHandle"/> /
/// <see cref="Api.CloseHandle"/>** does "when is it safe to delete" agree across the two.
/// </para>
///
/// <para>
/// <b>Stage C-1 only counts</b> and does not change behaviour. The reader (dropping the inode on the
/// last release) arrives in C-2.
/// </para>
///
/// <para>
/// <b>This is per-mount memory.</b> It cannot tell whether another mount holds the inode open (counting
/// in the database would add a transaction to every open and close, so that is not done). Therefore
/// **a cross-mount open-then-unlink does not behave the way POSIX describes** - that is not a regression
/// introduced by stage C, it is **already the case today** (docs/handle-context.md).
/// </para>
/// </summary>
public sealed class OpenInodes
{
	private readonly ConcurrentDictionary<long, int> counts = new();

	/// <summary>One more handle opened it.</summary>
	public void Acquire(long inodeId) {
		this.counts.AddOrUpdate(inodeId, 1, (_, current) => current + 1);
	}

	/// <summary>
	/// One handle closed it. Returns **the remaining reference count** (0 = this was the last release).
	/// <para>**Entries that reach 0 are dropped from the table** - remembering every inode that was ever
	/// opened would grow without bound on a long-running mount.</para>
	/// </summary>
	public int Release(long inodeId) {
		while (true) {
			if (!this.counts.TryGetValue(inodeId, out var current)) { return 0; }
			if (current <= 1) {
				// **Remove the key entirely.** If TryRemove loses a race, somebody else is touching it
				// concurrently, so re-read.
				if (this.counts.TryRemove(new System.Collections.Generic.KeyValuePair<long, int>(inodeId, current))) { return 0; }
				continue;
			}
			if (this.counts.TryUpdate(inodeId, current - 1, current)) { return current - 1; }
		}
	}

	/// <summary>How many handles hold this inode open (0 = nobody).</summary>
	public int CountOf(long inodeId) {
		if (!this.counts.TryGetValue(inodeId, out var current)) { return 0; }
		return current;
	}


	// ------------------------------------------------------------------
	// Inodes kept alive while open even though the name is gone (stage C-2)
	// ------------------------------------------------------------------

	/// <summary>
	/// inode -> data_id for inodes whose **name is gone but whose body is kept because a handle is still open**.
	/// <para>
	/// This is the ledger that lets the filesystem itself provide the POSIX behaviour of "unlinked but the
	/// fd is still alive". In pgfs **one row is "name + attributes"**, so removing the name means removing
	/// the inode row. The answer is to **remove only the inode row and keep <c>{prefix}data</c> and its
	/// chunks**, dropping them on the last close (option O-3 in docs/handle-context.md).
	/// </para>
	/// </summary>
	private readonly ConcurrentDictionary<long, long> orphans = new();

	/// <summary>
	/// Records that a name was removed while its body was kept.
	/// <para>
	/// **No snapshot of the attributes is kept** (one was added and then withdrawn). The intent was to use
	/// it as a fallback for `fstat`, but **libfuse's high-level API cannot distinguish `lookup` from
	/// `fstat`**, so the whole idea of finding an orphan by path was dropped. The reasoning is in
	/// docs/handle-context.md, in the section on why <c>hard_remove</c> is not set.
	/// </para>
	/// </summary>
	public void MarkOrphan(long inodeId, long dataId) {
		this.orphans[inodeId] = dataId;
	}

	/// <summary>Whether this inode is "name gone but still alive" (the fallback test during resolution).</summary>
	public bool IsOrphan(long inodeId) => this.orphans.ContainsKey(inodeId);

	/// <summary>**Takes and forgets** a remembered body (so the last close can drop it).</summary>
	public bool TryTakeOrphan(long inodeId, out long dataId) => this.orphans.TryRemove(inodeId, out dataId);

	/// <summary>How many bodies are alive with their name gone (diagnostics).</summary>
	public int OrphanCount => this.orphans.Count;

	/// <summary>How many inodes are currently open (diagnostics - **distinct inodes**, not handles).</summary>
	public int Count => this.counts.Count;
}
