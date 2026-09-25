namespace Pgfs.Core.Api;

using Models;

/// <summary>
/// **The context of one open handle** (docs/handle-context.md, stage A).
///
/// <para>
/// Identifying an open file used to be **path-first**, and the only thing carried on the handle was
/// the resolved <see cref="Models.Inode"/>. That produced three problems: (1) once the name changed
/// after opening, the handle touched a different file; (2) there was only one place where the caller
/// could be obtained (on Windows <c>GetRequestor</c> only succeeds inside <c>CreateFile</c>); and
/// (3) Core did not know the per-handle sync policy (<c>WRITE_THROUGH</c>).
/// **Dokan's <c>OpenFile</c> was effectively the prototype**, and this class is that idea lifted into Core.
/// </para>
///
/// <para>
/// **Stage A only creates the place to put it and does not change behaviour.** In stage B
/// <see cref="InodeId"/> becomes **the primary identity** (the path becomes secondary), and the six
/// handle-based entry points of <c>Api</c> take this context.
/// </para>
/// </summary>
public sealed class OpenFileContext
{
	public OpenFileContext(Inode inode, AuditContext? audit, bool writeThrough = false) {
		this.InodeId = inode.Id;
		this.Inode = inode;
		this.Audit = audit;
		this.WriteThrough = writeThrough;
	}

	/// <summary>
	/// **The stable identity of the inode this handle points at** (stage B). **Fixed at open time and never changes.**
	/// <para>
	/// This is **the value that becomes the primary identity** in stage B. With the path as the primary,
	/// a rename plus a re-create under the same name by another client makes **the same fd touch a
	/// different inode** (problem 1). The path is consulted only on open and on name operations; all
	/// later I/O re-resolves from this id (<see cref="Api.ResolveHandle"/>).
	/// </para>
	/// </summary>
	public long InodeId { get; }

	/// <summary>
	/// A **snapshot** of the inode this handle points at. Attributes can change, hence the setter.
	/// <para>
	/// **Do not use this as the identity** - from stage B onwards every read and write has
	/// <see cref="Api.ResolveHandle"/> re-resolve it from <see cref="InodeId"/> and refill it.
	/// It becomes <c>null</c> when the inode is gone.
	/// </para>
	/// </summary>
	public Inode? Inode { get; set; }

	/// <summary>
	/// The caller that opened this handle (the subject of an audit row). Null when it could not be obtained.
	/// <para>
	/// **On Windows it can only be obtained while opening** - <c>GetRequestor</c> only succeeds inside
	/// <c>CreateFile</c> and fails from the other callbacks with `Invalid token for impersonation`
	/// (measured: before the fix, one e2e round produced 3400 failures and every audit row had a null
	/// caller). **There is no way to carry it around other than putting it on the handle**, which is the
	/// reason this field exists.
	/// </para>
	/// </summary>
	public AuditContext? Audit { get; }

	/// <summary>
	/// The **subject of permission checks** that opened this handle (<see cref="PermissionEvaluator"/>). null when it
	/// could not be obtained or on paths that do not check.
	/// <para>
	/// Carried on the handle for the same reason as <see cref="Audit"/> - on Windows the caller can only be obtained
	/// inside <c>CreateFile</c>, so a later <c>MoveFile</c> (the check on the destination parent) looks here.
	/// </para>
	/// </summary>
	public AccessCaller? Caller { get; init; }

	/// <summary>
	/// Whether the handle was opened with <c>FILE_FLAG_WRITE_THROUGH</c>.
	/// In that case **every write must be durable by the time it returns**, so a full barrier
	/// (<see cref="Api.FlushInode"/>) is placed after each write even when write-back is enabled.
	/// </summary>
	public bool WriteThrough { get; }

	/// <summary>
	/// **Whether this context is counted in the reference count** (stage C-1).
	/// <para>
	/// <see cref="Api.OpenHandle"/> sets it and <see cref="Api.CloseHandle"/> clears it. **The adapters
	/// never touch it.** It exists so that **closing a context that was never counted does not decrement
	/// the count** - Dokan's <c>FileSystem.Resolve</c> builds **ephemeral contexts** for things like
	/// attribute queries, and those go through <c>CloseFile</c> too. Without the mark, **closing something
	/// that was never opened drives the count negative**. A double <c>open</c> or double <c>close</c> is
	/// absorbed by the same mark.
	/// </para>
	/// </summary>
	public bool Counted { get; set; }

	// **`TruncatedByThisHandle` is deliberately absent.**
	//
	// The original design (docs/handle-context.md) considered carrying the diagnostic "this handle issued
	// the truncate", but stage A **does not carry it**. The reason is that it would give
	// **the same decision two sources of truth**:
	//
	// The mark behind sync heuristic (b), "escalate the close of a truncated inode to synchronous", is held
	// by `Api.WriteBackMetadata` under **both the inode key and the data key** (A-4 / A-5). Holding it per
	// handle instead means that with `truncate -s 0 f; cmd >> f` (truncate and write on different fds)
	// **the mark disappears when the truncating fd closes**, which reopens exactly the zero-length-garbage
	// window that A-6 closed. A close through a hardlink sibling is a different handle as well, so it
	// cannot see the mark either.
	//
	// "Carry it but never read it" is its own trap, because **somebody will read it later**, so
	// **it is not carried until there is a reader**. If the decision is ever moved onto the handle, that
	// belongs in stage B or later, and the representation of a "mark that spans handles" has to be settled first.
}
