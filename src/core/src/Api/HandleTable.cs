namespace Pgfs.Core.Api;

using System.Collections.Concurrent;

/// <summary>
/// The table of open handles (id -> <see cref="OpenFileContext"/>). See docs/handle-context.md, stage A.
///
/// <para>
/// FUSE **cannot carry an object** in <c>fuse_file_info.fh</c> (only a 64-bit integer), so the table
/// lives in Core and <c>fh</c> holds its key. Dokan can put an object straight into
/// <c>DokanFileInfo.Context</c> and needs no table, but the storage is unified here so that
/// **both operating systems use the same <see cref="OpenFileContext"/>**.
/// </para>
///
/// <para>
/// <see cref="Api"/> holds it as a <c>private readonly</c> field, in **exactly the same shape** as
/// <c>InodeCache</c> / <c>ContentCache</c> / <c>DirtySet</c> / <c>DirtyNamespace</c> /
/// <c>IdReservation</c>, so that no new idiom is introduced.
/// </para>
/// </summary>
public sealed class HandleTable
{
	private readonly ConcurrentDictionary<ulong, OpenFileContext> handles = new();
	private long next;
	private int peak;
	private int openCount;

	/// <summary>
	/// **Ids start at 1. Zero is reserved as the sentinel for "no handle attached".**
	/// <para>
	/// Using 0 would make **the handle of the root inode (<c>id = 0</c>) indistinguishable from
	/// "nothing attached"**. That is not hypothetical: back when <c>fh</c> carried the raw inode id,
	/// the reverse lookup in <c>FSyncDir</c> was dead for the root. The meaning of <c>fh</c> changes
	/// from "inode id" to "handle id" here, so **starting at 1 makes that trap disappear by itself**.
	/// </para>
	/// </summary>
	public const ulong NoHandle = 0;

	/// <summary>Registers a handle and hands out its id. **Every id handed out must be given back with <see cref="Return"/>.**</summary>
	public ulong Rent(OpenFileContext context) {
		// Increment happens before use, so the first id handed out is 1 (= it never collides with NoHandle).
		var id = unchecked((ulong)System.Threading.Interlocked.Increment(ref this.next));
		this.handles[id] = context;
		// The high-water mark of concurrently open handles (for diagnostics and leak hunting).
		// **`ConcurrentDictionary.Count` takes every lock**, so it is never called on the open hot path -
		// the count is kept in an Interlocked counter instead. Updating peak is best-effort (even if a
		// race drops one, "at least this many were open" still holds).
		var count = System.Threading.Interlocked.Increment(ref this.openCount);
		if (count > this.peak) { this.peak = count; }
		return id;
	}

	/// <summary>Looks up a handle context by id. **Null when there is none** (nothing attached, or already returned).</summary>
	public OpenFileContext? Get(ulong id) {
		if (id == HandleTable.NoHandle) { return null; }
		if (!this.handles.TryGetValue(id, out var context)) { return null; }
		return context;
	}

	/// <summary>
	/// Removes a handle from the table and returns it (on close). Null when it was already removed.
	/// <para>**Leaving it in the table leaks one <see cref="OpenFileContext"/> per handle**, so the
	/// close / release path of the OS layer must always call this.</para>
	/// </summary>
	public OpenFileContext? Return(ulong id) {
		if (id == HandleTable.NoHandle) { return null; }
		if (!this.handles.TryRemove(id, out var context)) { return null; }
		System.Threading.Interlocked.Decrement(ref this.openCount);
		return context;
	}

	/// <summary>
	/// The number of open handles (for diagnostics and leak detection).
	/// <para>**Reads the counter rather than `handles.Count`** - `ConcurrentDictionary.Count` takes every
	/// lock, and we do not want to stall the open / close hot path just to obtain the same value. Ids are
	/// unique per hand-out and <see cref="Return"/> only decrements when the entry existed, so the counter
	/// always matches the size of the dictionary.</para>
	/// </summary>
	public int Count => System.Threading.Volatile.Read(ref this.openCount);

	/// <summary>
	/// The highest number of handles that were open at the same time (best-effort).
	/// <para>
	/// **This is the value that lets a leak be found after the fact.** `Count` alone only tells you how
	/// many are open *now*, so it cannot answer **whether a mount that has since gone quiet used to leak**.
	/// </para>
	/// </summary>
	public int Peak => this.peak;
}
