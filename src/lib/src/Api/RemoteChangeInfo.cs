namespace Pgfs.Lib.Api;

using System.Collections.Generic;

/// <summary>
/// Information passed to the OS bridge (Assign's <c>DokanInstance.NotifyUpdate</c> etc.) when <see cref="NotifyChannel"/>
/// receives another client's write.
///
/// <para>
/// <see cref="InodeIds"/> / <see cref="ParentIds"/> are the ID lists straight from the received message.
/// <see cref="ResolvedPaths"/> contains only the ones whose path could be looked up in <see cref="InodeCache"/>
/// (= only if this client has touched the relevant inode before; otherwise no OS notification is needed —
/// this client's kernel/Explorer does not know the target path in the first place).
/// </para>
/// </summary>
public sealed class RemoteChangeInfo
{
	/// <summary>The inode ID list in the received message (targets of delete / attribute change / data rewrite, etc.).</summary>
	public IReadOnlyList<long> InodeIds { get; init; } = System.Array.Empty<long>();

	/// <summary>The parent ID list in the received message (targets of child-list changes).</summary>
	public IReadOnlyList<long> ParentIds { get; init; } = System.Array.Empty<long>();

	/// <summary>
	/// The <c>id → path</c> map for those of <see cref="InodeIds"/> that could be resolved to a full path in the local
	/// <see cref="InodeCache"/>. `/`-separated (Mount passes it to libfuse as-is; Assign converts to `\` on the caller side).
	/// </summary>
	public IReadOnlyDictionary<long, string> ResolvedPaths { get; init; } = new Dictionary<long, string>();

	/// <summary>
	/// The <c>parent_id → path</c> map for those of <see cref="ParentIds"/> that could be resolved to a full path in the
	/// local <see cref="InodeCache"/>.
	/// </summary>
	public IReadOnlyDictionary<long, string> ResolvedParentPaths { get; init; } = new Dictionary<long, string>();
}
