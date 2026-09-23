namespace Pgfs.Core.Models;

/// <summary>
/// String constants for the operation kind that goes in the audit log's <c>op</c> column. Centralized here to prevent typos.
/// For the meaning of the values and the hook locations, see [docs/audit-log.md](../../../../docs/audit-log.md).
/// </summary>
public static class AuditOp
{
	public const string Create = "create";
	public const string Delete = "delete";
	public const string Rename = "rename";
	public const string Chmod = "chmod";
	public const string Chown = "chown";
	public const string Hardlink = "hardlink";
	/// <summary>
	/// The record of unflushed work that **was lost** because it could not be written out within the deadline at
	/// unmount (B-2). One unmount = one row.
	/// The subject is the mount itself rather than an individual inode, so <c>target_id</c> is null.
	/// </summary>
	public const string WritebackLoss = "writeback_loss";
}
