namespace Pgfs.Core.Api;

using Dapper;
using Logging;
using Models;
using Utility;
using Pgfs.Core.Config;
using System.Text.Json;

/// <summary>
/// The API that exposes PGFS filesystem operations.
///
/// A shared layer used from both Mount (Linux/macOS, Pgfs.Fuse) and Assign (Windows, DokanNet).
/// OS-specific concepts (uid/gid resolution, xattr details, symlinks, etc.) are intentionally left to the
/// caller; here it handles only DB read/write and inode cache management.
/// </summary>
public partial class Api : System.IDisposable
{
	private readonly RootConfig config;
	private readonly string connectionString;
	private readonly string tableNamePrefix;
	private readonly string schemaName;
	private readonly InodeCache inodeCache;
	private readonly ContentCache contentCache;
	/// <summary>
	/// LISTEN/NOTIFY for both the control channel and data-change notifications. It is always established,
	/// regardless of <c>notify_enabled</c>, because the control messages (reload/set/ping) need it. It is only
	/// null when the LISTEN could not be opened (startup continues when the channel is control-only).
	/// </summary>
	private NotifyChannel? notifyChannel;
	/// <summary>Whether data-change notifications (the i/p/x/d payloads) are sent and received (= database.notify_enabled). Control messages are handled regardless.</summary>
	private readonly bool dataNotifyEnabled;

	/// <summary>Whether the audit log (audit.enabled) is on. The condition under which each mutating method fires its hook after succeeding.</summary>
	private bool auditEnabled;
	/// <summary>The pgfs process's host name to put in an audit row's caller_host (resolved once at startup). Null when auditing is off.</summary>
	private readonly string? auditHost;
	/// <summary>Caches the already-CREATEd monthly partition keys ("yyyy_MM") within the process, so the DDL is issued only on the first time each month.</summary>
	private readonly System.Collections.Generic.HashSet<string> ensuredAuditPartitions = new();
	private readonly object auditPartitionLock = new();

	/// <summary>This process's row id in the {prefix}mounts registry (random hex). Used by register, heartbeat and deregister.</summary>
	private readonly string mountId = System.Guid.NewGuid().ToString("N");
	private readonly System.Threading.CancellationTokenSource heartbeatCts = new();
	private System.Threading.Tasks.Task? heartbeatTask;
	private bool registered;
	private bool disposed;
	private const int HeartbeatSeconds = 30;

	/// <summary>The unflushed ledger of write-back. It exists (empty) even when <c>mount.write_back</c> is false.</summary>
	private readonly DirtySet dirtySet = new();
	/// <summary>The reservation pool that lets write-back allocate a data_id for a new file without a database round trip.</summary>
	private readonly IdReservation dataIdReservation;
	/// <summary>The background flush loop of write-back (the time trigger).</summary>
	private System.Threading.Tasks.Task? flushTask;

	/// <summary>
	/// The queue that applies control messages (<c>reload</c> / <c>set</c> / <c>ping</c>) in order
	/// **off the NOTIFY listener thread** (B-8).
	/// <para>
	/// Without it, the two-phase flip of `write_back_metadata` (<see cref="ApplyMetadataWriteBackLive"/>) and
	/// back-pressure would **run directly on the listener thread**, and for up to
	/// <c>write_back_flush_timeout_ms</c> **not a single invalidate from another client would be processed**.
	/// Data-change notifications are cheap and latency-sensitive, so they stay inline and **only the heavy
	/// control messages** are routed through here.
	/// </para>
	/// <para>
	/// **Order is preserved** (there is a single consumer): two `set` messages are applied in the order they
	/// arrived. `pgfsctl config set` never took an ack in the first place (it only reports the fired count as a
	/// "most recent" figure), so making this asynchronous does not change the contract.
	/// </para>
	/// </summary>
	private readonly System.Collections.Concurrent.BlockingCollection<ControlWork> controlQueue = new();
	private System.Threading.Tasks.Task? controlTask;

	/// <summary>
	/// The queue of outgoing data-change notifications (<c>pg_notify</c>) (B-11). **There is a single consumer, so the send order is preserved.**
	/// <para>
	/// It is asynchronous because <c>pg_notify</c> is **a database round trip**. Sending is called from the
	/// cleanup of a metadata flush (<see cref="FinishPendingFlush"/>) while the NSGate is still held, so firing
	/// inline would mean **the NSGate stays held for as long as the database hangs, dragging every metadata
	/// operation down with it**.
	/// A dropped notification only leaves another client's cache stale (Publish swallows its own failures
	/// anyway), so when this backs up, **dropping** is the right answer.
	/// </para>
	/// </summary>
	private readonly System.Collections.Concurrent.BlockingCollection<NotifyMessage> notifyQueue = new(Api.NotifyQueueCapacity);
	private System.Threading.Tasks.Task? notifyTask;

	/// <summary>The cap on the notification queue. Beyond it messages are **dropped with a warning** (the producer is never made to wait).</summary>
	private const int NotifyQueueCapacity = 1024;
	/// <summary>How often the "dropped a notification" warning may repeat (logging every drop would bury the log).</summary>
	private const long NotifyDropWarnIntervalTicks = 10 * System.TimeSpan.TicksPerSecond;
	/// <summary>When the last "dropped" warning was emitted (ticks). Held as a long because several threads touch it.</summary>
	private long notifyDropWarnedAtTicks;
	private long notifyDropped;
	/// <summary>
	/// The mark for **a dropped notification = a receiver's cache is now lying**.
	/// <para>
	/// A dropped notification **never arrives again**, and a positive entry in the receiver's
	/// <see cref="InodeCache"/> has **no TTL**, so unless it is dropped it **keeps returning a stale value
	/// forever** (review M-2).
	/// The individual ids are no longer known, so this is **folded into a single "drop everything" message
	/// that is always sent**.
	/// Dropping a cache is the safe direction, so over-firing it breaks nothing.
	/// </para>
	/// </summary>
	private long notifyResyncPending;

	/// <summary>The pending-inode ledger of metadata write-back. It exists (empty) even when the feature is off.</summary>
	private readonly DirtyNamespace dirtyNamespace = new();

	/// <summary>
	/// The table of open handles (docs/handle-context.md, stage A). FUSE can only carry an integer in `fh`, so
	/// the table lives in Core and Dokan uses the same <see cref="OpenFileContext"/>. **Stage A only creates
	/// the place to put it and does not use it for any decision** (behaviour is unchanged).
	/// </summary>
	public HandleTable Handles { get; } = new();

	/// <summary>
	/// The reference count of open bodies (stage C-1). **It is separate from the handle table**, and counting
	/// happens because both adapters explicitly call <see cref="OpenHandle"/> and <see cref="CloseHandle"/>.
	/// </summary>
	private readonly OpenInodes openInodes = new();
	/// <summary>The reservation pool that allocates ids for pending inodes without a database round trip (for <c>{prefix}inode.id</c>).</summary>
	private readonly IdReservation inodeIdReservation;
	/// <summary>
	/// The gate that serializes namespace flushes. **The lock hierarchy is fixed as NSGate ->
	/// <see cref="DirtyFile.Gate"/> -> the database transaction** (taking them the other way round makes the
	/// background flush and fsync wait on each other and hangs the whole mount unrecoverably).
	/// A write-through operation that consults a pending entry participates in this gate too.
	/// </summary>
	private readonly object nsGate = new();
	/// <summary>The flag that keeps the "enabled <c>write_back_metadata</c> without <c>write_back</c>" warning to a single occurrence.</summary>
	private bool metadataWriteBackWarned;

	public Api(RootConfig config) {
		this.config = config;
		this.connectionString = config.Database.Connection.ConnectionString;
		this.tableNamePrefix = config.Database.GetPrefix();
		this.schemaName = config.Database.SchemaName;
		this.inodeCache = new InodeCache(config);
		this.contentCache = new ContentCache(config.Mount.CacheDataMaxBytes);
		// The audit log (docs/audit-log.md). caller_host is a process-wide constant that says which machine
		// performed the operation, so it is resolved exactly once at startup. It is left alone when auditing
		// is off.
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
		// The DB load of file_system.unknown_name and the like has already been done by [ConfigLoader] via ConfigStore,
		// so do nothing here.

		// The control channel (reload/set/ping) always LISTENs, regardless of notify_enabled. That is what
		// lets a live `config set` reach a single-client mount too (where notify defaults to OFF).
		// notify_enabled only gates sending and receiving data-change notifications (the i/p/x/d payloads);
		// see Notify / OnRemoteChange below.
		// The LISTEN stays open on a dedicated connection and is closed by Dispose at process exit.
		this.dataNotifyEnabled = config.Database.NotifyEnabled;
		this.notifyChannel = new NotifyChannel(this.connectionString, this.schemaName, this.tableNamePrefix);
		this.notifyChannel.Received += this.OnRemoteChange;
		this.notifyChannel.Reconnected += this.OnNotifyReconnected;
		try {
			this.notifyChannel.Start();
		} catch (System.Exception ex) {
			if (this.dataNotifyEnabled) {
				throw; // Data notifications were explicitly requested, so failing to LISTEN still aborts startup.
			}
			// Only the control-only LISTEN failed. The mount continues (all that is lost is live `config set`).
			Logger.Warning("could not open the LISTEN for the control channel (live config set is disabled; the mount continues): ", ex.Message);
			this.notifyChannel.Dispose();
			this.notifyChannel = null;
		}

		// Write-back. The reservation pools are always created, because write_back can be changed live and has
		// to work when it is enabled later.
		this.dataIdReservation = new IdReservation(this.connectionString, this.QualifiedTable("data"), "id");
		this.inodeIdReservation = new IdReservation(this.connectionString, this.QualifiedTable("inode"), "id");
		// InodeCache stays a view that can be rebuilt by re-merging the database with the ledger. A pending
		// inode that has no row in the database is resolved through these two hooks, so it can be looked up
		// again even after byId loses it to the LRU or an invalidate.
		this.inodeCache.PendingById = this.FindPendingInode;
		this.inodeCache.PendingByName = this.FindPendingChild;
		this.WarnMetadataCacheBudget();
		this.StartFlushLoop();
		this.StartControlLoop();
		this.StartNotifyLoop();

		// Register in the {prefix}mounts registry and start the heartbeat (a missing table is a warning and a skip).
		this.TryRegisterMount();
		// Warn at startup about "defer is on while other clients exist" too. It has to happen after registering,
		// otherwise this process counts itself.
		this.WarnExclusiveCreateDefer();
		// Warn when a past unmount lost unflushed data (B-2).
		this.WarnPastUnflushedLoss();
	}

	public RootConfig Config => this.config;
	public InodeCache InodeCache => this.inodeCache;

	/// <summary>
	/// The OS bridge called when a change notification from another client is received. On Assign it is used to call
	/// <c>DokanInstance.NotifyUpdate</c> etc. to ask Explorer to repaint.
	/// Mount (Linux) leaves it null because the Pgfs.Fuse high-level API has no counterpart (the kernel attr cache is
	/// disabled via <c>attr_timeout=0</c>, so just invalidating InodeCache makes stat/ls return the latest).
	/// </summary>
	public System.Action<RemoteChangeInfo>? OsBridge { get; set; }

	/// <summary>
	/// Whether libfuse's leftovers (<c>.fuse_hidden*</c>) are removed from listings (<see cref="ListChildren"/>).
	/// <b>True by default (= FUSE / Linux). Dokan sets it to false.</b>
	/// <para>
	/// <b>On Windows libfuse never creates `.fuse_hidden*`</b>, so there is nothing to hide in the first place.
	/// Hiding them would leave only the downside - **invisible in a listing yet impossible to delete** - and on
	/// Windows **anything that goes through a listing, such as <c>Remove-Item</c> or Explorer, cannot delete
	/// them** (<c>[System.IO.File]::Delete</c>, which is Win32 <c>DeleteFile</c> directly, still can).
	/// The judgement was that **"a way to delete it exists but nobody can reach it" is worse than "it cannot be
	/// deleted"**, so the Dokan side does not hide them.
	/// </para>
	/// <para>
	/// <b>Path resolution (<see cref="GetByPath"/>) never removes them, whatever this value is.</b> Removing
	/// them there would stop the very mount that is keeping the fd alive from looking up its own hidden file
	/// (libfuse issues <c>getattr</c> and <c>release</c> against the hidden name). **Only listings hide them.**
	/// </para>
	/// <para>The source of truth is <c>docs/namespace-policy.md</c>; the reasoning is in <c>docs/handle-context.md</c>.</para>
	/// </summary>
	public bool HideLibfuseLeftovers { get; set; } = true;

	public void Dispose() {
		if (this.disposed) {
			return;
		}
		this.disposed = true;
		// Write out whatever write-back still holds "before we go down". This is the last line of defence
		// against losing data on a clean unmount. It retries under a deadline, and whatever could not be
		// written is enumerated in the Error log as "what is being lost", with the count left in
		// UnflushedAtShutdown (which makes mount.pgfs exit non-zero).
		this.FlushAllForShutdown();
		try { this.controlQueue.CompleteAdding(); } catch { }
		try { this.controlTask?.Wait(System.TimeSpan.FromSeconds(2)); } catch { }
		try { this.heartbeatCts.Cancel(); } catch { }
		try { this.heartbeatTask?.Wait(System.TimeSpan.FromSeconds(2)); } catch { }
		try { this.flushTask?.Wait(System.TimeSpan.FromSeconds(2)); } catch { }
		// **Even when not registered, a loss still gets its gravestone** (MarkMountLoss creates the row). Before,
		// an unregistered mount did not call DeregisterMount at all, so **the loss was recorded nowhere**.
		if (this.registered || this.UnflushedAtShutdown > 0) {
			try {
				this.DeregisterMount();
			} catch (System.Exception ex) {
				Logger.Warning("failed to deregister from mounts: ", ex.Message);
			}
		}
		// The notification worker is stopped **before notifyChannel is closed**, so it can fire whatever is
		// still queued.
		try { this.notifyQueue.CompleteAdding(); } catch { }
		try { this.notifyTask?.Wait(System.TimeSpan.FromSeconds(2)); } catch { }
		try { this.heartbeatCts.Dispose(); } catch { }
		try { this.controlQueue.Dispose(); } catch { }
		try { this.notifyQueue.Dispose(); } catch { }
		this.notifyChannel?.Dispose();
	}

	/// <summary>
	/// Registers this process in the <c>{prefix}mounts</c> registry and starts the heartbeat task. When the
	/// table does not exist (an existing filesystem that has not been re-mkfs'd) it warns, skips registration
	/// and the heartbeat, and lets the mount continue - DDL belongs to mkfs.
	/// See docs/runtime-control-plane.md.
	/// </summary>
	private void TryRegisterMount() {
		try {
			this.InsertMountRow(null);
			this.registered = true;
			this.WriteHeartbeat(); // Fill the stats/config snapshots from the moment we start (status Layer 3).
			Logger.Information("registered in mounts: ", this.mountId, " @ ", this.config.Mount.MountPoint);
		} catch (System.Exception ex) {
			Logger.Warning("skipped registering in mounts (the table may not exist yet; it is retried every heartbeat): ", ex.Message);
		}
		// **The heartbeat loop runs even when registering failed** (<see cref="WriteHeartbeat"/> registers again).
		// A mount that failed to register because of a database blip at startup used to stay **invisible to
		// prune and status for good**, and did not count towards prune's "leave the data alone while any mount
		// is alive".
		this.heartbeatTask = System.Threading.Tasks.Task.Run(() => this.RunHeartbeatLoopAsync(this.heartbeatCts.Token));
	}

	/// <summary>
	/// Creates this process's row in <c>{prefix}mounts</c> (or only advances heartbeat_at when it exists). A failure
	/// is thrown to the caller. With <paramref name="stats"/>, **a newly created row** carries that JSON (used when
	/// creating a gravestone in <c>MarkMountLoss</c>).
	/// </summary>
	private void InsertMountRow(string? stats) {
		var mode = "fuse";
		if (System.OperatingSystem.IsWindows()) {
			mode = "dokan";
		}
		// Citus note: pgfs_mounts is registered in the metadata via citus_add_local_table_to_metadata, so
		// writing a non-IMMUTABLE function (current_timestamp / now()) into the ON CONFLICT DO UPDATE clause
		// "functions used in the DO UPDATE SET clause of INSERTs on distributed tables must be marked
		// IMMUTABLE" (the same restriction as ConfigStore.SaveJson and WriteFullChunk). current_timestamp is
		// allowed on the VALUES side, so DO UPDATE refers to EXCLUDED.heartbeat_at (= the constant from
		// VALUES). The same SQL works unchanged on plain PostgreSQL, so there is no need to branch on the
		// Citus flag.
		Pg.Execute(
			this.connectionString,
			$@"INSERT INTO {this.QualifiedTable("mounts")} (mount_id, host, pid, mountpoint, mode, started_at, heartbeat_at, stats)
			   VALUES (@mount_id, @host, @pid, @mountpoint, @mode, (current_timestamp AT TIME ZONE 'UTC'), (current_timestamp AT TIME ZONE 'UTC'), COALESCE(@stats::jsonb, '{{}}'::jsonb))
			   ON CONFLICT (mount_id) DO UPDATE SET heartbeat_at = EXCLUDED.heartbeat_at",
			new {
				mount_id = this.mountId,
				host = System.Net.Dns.GetHostName(),
				pid = System.Environment.ProcessId,
				mountpoint = this.config.Mount.MountPoint,
				mode,
				stats,
			}
		);
	}

	/// <summary>
	/// Registers again, at the heartbeat interval, a mount whose registration failed at startup.
	/// **On an existing filesystem without the table it fails every time**, so a failure here stays at Debug (the
	/// Warning is the one at startup).
	/// </summary>
	private void RetryRegisterMount() {
		try {
			this.InsertMountRow(null);
		} catch (System.Exception ex) {
			Logger.Debug("retrying the mounts registration failed (continuing): ", ex.Message);
			return;
		}
		this.registered = true;
		Logger.Information("registered in mounts (on a retry): ", this.mountId, " @ ", this.config.Mount.MountPoint);
		this.WriteHeartbeat();
	}

	/// <summary>The heartbeat loop. Updates heartbeat_at every <see cref="HeartbeatSeconds"/>. A failure is a warning and the loop continues.</summary>
	private async System.Threading.Tasks.Task RunHeartbeatLoopAsync(System.Threading.CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			try {
				await System.Threading.Tasks.Task.Delay(System.TimeSpan.FromSeconds(HeartbeatSeconds), ct);
			} catch (System.OperationCanceledException) {
				break;
			}
			if (ct.IsCancellationRequested) {
				break;
			}
			this.WriteHeartbeat();
		}
	}

	/// <summary>
	/// Updates heartbeat_at in {prefix}mounts immediately and writes the <c>stats</c> and <c>config</c>
	/// snapshots along with it (status Layer 3; docs/runtime-control-plane.md). Used by the heartbeat loop,
	/// on receiving a ping, and right after registering. **When this process is not registered, it registers again**
	/// (<see cref="RetryRegisterMount"/>).
	/// <para>
	/// **When the UPDATE touches 0 rows, the row is created again.** If this process's row has disappeared (removed by
	/// <c>pgfsctl prune</c> as stale after this host could not reach the database for a long time, say) and only the
	/// UPDATE keeps being fired, **this mount stays invisible to prune and status from then on** - it does not count
	/// towards prune's "leave the data alone while any mount is alive".
	/// </para>
	/// </summary>
	private void WriteHeartbeat() {
		if (!this.registered) {
			this.RetryRegisterMount();
			return;
		}
		try {
			if (this.UpdateHeartbeatRow() > 0) { return; }
			Logger.Warning("mounts: this process's row was gone, so it is registered again (mount_id:", this.mountId, ")");
			this.InsertMountRow(null);
			this.UpdateHeartbeatRow();
		} catch (System.Exception ex) {
			Logger.Warning("heartbeat update failed (continuing): ", ex.Message);
		}
	}

	/// <summary>Updates heartbeat_at and the stats/config snapshots and returns **the number of rows updated**.</summary>
	private int UpdateHeartbeatRow() {
		return Pg.Execute(
			this.connectionString,
			$"UPDATE {this.QualifiedTable("mounts")} SET heartbeat_at = (current_timestamp AT TIME ZONE 'UTC'), stats = @stats::jsonb, config = @config::jsonb WHERE mount_id = @mount_id",
			new { mount_id = this.mountId, stats = this.BuildStatsJson(), config = this.BuildConfigJson() }
		);
	}

	/// <summary>
	/// Renders this process's <see cref="InodeCache"/> and <see cref="ContentCache"/> statistics plus the
	/// NOTIFY connection state as JSON (the <c>{prefix}mounts.stats</c> snapshot of status Layer 3). The
	/// reader computes the hit ratio.
	/// </summary>
	private string BuildStatsJson() {
		var inode = this.inodeCache.Stats();
		var content = this.contentCache.Stats();
		var dirty = this.dirtySet.Stats();
		var ns = this.dirtyNamespace.Stats();
		var snapshot = new {
			inode = new { entries = inode.Entries, pathEntries = inode.PathEntries, childrenLists = inode.ChildrenLists, capacity = inode.Capacity, hits = inode.Hits, misses = inode.Misses, evictions = inode.Evictions, negativeEntries = inode.NegativeEntries, negativeHits = inode.NegativeHits },
			content = new { entries = content.Entries, bytes = content.Bytes, maxBytes = content.MaxBytes, hits = content.Hits, misses = content.Misses, evictions = content.Evictions, generation = content.Generation },
			writeBack = new {
				enabled = this.config.Mount.WriteBack,
				// The effective mode (the same idea as B-9). During phase 1 of the two-phase flip the setting is
				// still on yet no new dirty data is accepted, and while the drain continues the setting is off
				// yet flushing is still running.
				intakeClosed = this.dataIntakeClosed,
				effective = this.config.Mount.WriteBack && !this.dataIntakeClosed,
				drainPending = this.dataDrainPending,
				dirtyBytes = content.DirtyBytes,
				maxBytes = this.config.Mount.WriteBackMaxBytes,
				dirtyChunks = content.DirtyChunks,
				dirtyFiles = dirty.DirtyFiles,
				trackedFiles = dirty.TrackedFiles,
				flushes = dirty.Flushes,
				flushFailures = dirty.FlushFailures,
				// The error state (new writes and creates are blocked after N consecutive flush failures). Null = healthy.
				errorState = this.writeBackErrorState,
				errorSince = this.WriteBackErrorSinceText(),
			},
			writeBackMetadata = new {
				enabled = this.config.Mount.WriteBackMetadata,
				// **The effective mode** (B-9). During phase 1 of the two-phase flip enabled is still true while
				// no new pending entries are accepted, so reporting just "on" in status would be a lie.
				intakeClosed = this.MetadataIntakeClosed,
				effective = this.MetadataWriteBack,
				pendingInodes = ns.PendingInodes,
				maxInodes = this.config.Mount.WriteBackMaxInodes,
				trackedEntries = ns.TrackedEntries,
				queuedAudits = ns.QueuedAudits,
				droppedAudits = ns.DroppedAudits,
				flushes = ns.Flushes,
				flushFailures = ns.FlushFailures,
				conflicts = ns.Conflicts,
				cancels = ns.Cancels,
				discards = ns.Discards,
			},
			// Open handles. **The number in the handle table is the number on the FUSE side** - Dokan does not
			// go through the table but puts the object into DokanFileInfo.Context, so **on Windows this is
			// always 0** (docs/handle-context.md, the implementation status of the Dokan adapter).
			handles = new { open = this.Handles.Count, peak = this.Handles.Peak, inodes = this.OpenInodeCount },
			notify = new { control_listen = this.notifyChannel != null, data_enabled = this.dataNotifyEnabled, connected = this.notifyChannel?.Connected ?? false },
			snapshot_at = System.DateTime.UtcNow.ToString("o"),
		};
		return System.Text.Json.JsonSerializer.Serialize(snapshot);
	}

	/// <summary>
	/// Renders the effective values of the running <see cref="RootConfig"/> as JSON (the operations-relevant
	/// subset, values only, no password) - the <c>{prefix}mounts.config</c> snapshot of status Layer 3.
	/// Unlike <c>pgfsctl config list</c> (which shows DB plus defaults), this reflects the result after an
	/// ephemeral File+Live set and after CLI or toml overrides.
	/// </summary>
	private string BuildConfigJson() {
		var cfg = new System.Collections.Generic.Dictionary<string, object?> {
			["logging.level"] = this.config.Logging.MinLevel.ToString(),
			["logging.output"] = this.config.Logging.Output,
			["database.retry_max_attempts"] = this.config.Database.RetryMaxAttempts,
			["database.retry_initial_delay_ms"] = this.config.Database.RetryInitialDelayMs,
			["database.retry_max_delay_ms"] = this.config.Database.RetryMaxDelayMs,
			["database.notify_enabled"] = this.dataNotifyEnabled,
			["mount.mount_point"] = this.config.Mount.MountPoint,
			["mount.cache_max_entries"] = this.config.Mount.CacheMaxEntries,
			["mount.cache_data_max_bytes"] = this.config.Mount.CacheDataMaxBytes,
			["mount.negative_cache_ttl_ms"] = this.config.Mount.NegativeCacheTtlMs,
			["mount.write_back"] = this.config.Mount.WriteBack,
			["mount.write_back_max_bytes"] = this.config.Mount.WriteBackMaxBytes,
			["mount.write_back_interval_ms"] = this.config.Mount.WriteBackIntervalMs,
			["mount.write_back_metadata"] = this.config.Mount.WriteBackMetadata,
			["mount.write_back_metadata_exclusive_create"] = this.config.Mount.WriteBackMetadataExclusiveCreate,
			["mount.write_back_max_inodes"] = this.config.Mount.WriteBackMaxInodes,
			["mount.write_back_flush_timeout_ms"] = this.config.Mount.WriteBackFlushTimeoutMs,
			["app.statfs"] = this.config.Statfs.Mode,
			["app.enforce_permissions"] = this.config.App.EnforcePermissions,
			["audit.enabled"] = this.config.Audit.Enabled,
			["file_system.version"] = this.config.FileSystem.Version,
			["file_system.volume_label"] = this.config.FileSystem.VolumeLabel,
		};
		return System.Text.Json.JsonSerializer.Serialize(cfg);
	}

	/// <summary>
	/// Sends a control message (<c>"reload"</c> / <c>"ping"</c>) to the other clients. A no-op when
	/// NotifyChannel is disabled. In production <c>"reload"</c> is fired by the config subcommand; a test can
	/// also fire it with psql's <c>pg_notify</c>.
	/// </summary>
	public void PublishControl(string op) {
		var ch = this.notifyChannel;
		if (ch == null) {
			return;
		}
		ch.Publish(new NotifyMessage { Control = op });
	}

	/// <summary>Starts the control-message worker (B-8). There is a single consumer, so the order of application is preserved.</summary>
	private void StartControlLoop() {
		this.controlTask = System.Threading.Tasks.Task.Run(() => {
			foreach (var work in this.controlQueue.GetConsumingEnumerable()) {
				try {
					this.ApplyControl(work);
				} catch (System.Exception ex) {
					// Dying here would kill the worker and make every later live change stop working. Swallow and continue.
					Logger.Warning("failed to apply a control message (continuing): ", work.Control, " ", ex.Message);
				}
			}
		});
	}

	/// <summary>Queues a control message for the worker. Anything after the queue is closed (during Dispose) is dropped.</summary>
	private void EnqueueControl(string control, string? key, string? value) {
		try {
			this.controlQueue.Add(new ControlWork { Control = control, Key = key, Value = value });
		} catch (System.InvalidOperationException) {
			// CompleteAdding has already run = we are shutting down. That is no reason to fail, so drop it quietly.
		}
		var depth = this.controlQueue.Count;
		if (depth >= Api.ControlQueueWarnDepth) {
			Logger.Warning("the control queue is ", depth, " deep (applying live settings is falling behind)");
		}
	}

	/// <summary>Applies a control message taken from the queue (on the worker thread).</summary>
	private void ApplyControl(ControlWork work) {
		if (work.Control == "reload") { this.ReloadLiveConfig(); return; }
		if (work.Control == "set") { this.ApplyLiveSet(work.Key, work.Value); return; }
		if (work.Control == "ping") { this.WriteHeartbeat(); return; }
		Logger.Warning("ignoring an unknown control message: ", work.Control);
	}

	/// <summary>The queue depth at which a backed-up control queue is warned about.</summary>
	private const int ControlQueueWarnDepth = 32;

	/// <summary>
	/// Re-reads pgfs_settings and re-applies the Reload=Live fields while running (on receiving the
	/// <c>reload</c> control message).
	/// <see cref="ConfigStore.LoadAll"/> only returns SaveTo=Db fields, so what takes effect through this path
	/// are the Live settings stored in the database (<c>audit.enabled</c> and <c>app.statfs</c>). The Live
	/// settings stored in the file (logging, cache, retry) are applied inline by the <c>set</c> control message
	/// (<see cref="ApplyLiveSet"/>).
	/// See docs/runtime-control-plane.md.
	/// </summary>
	public void ReloadLiveConfig() {
		try {
			var store = new ConfigStore(this.connectionString, this.schemaName, this.tableNamePrefix);
			var values = new System.Collections.Generic.Dictionary<string, string>();
			foreach (var kv in store.LoadAll(Schema.AllFields)) {
				values[kv.Key] = kv.Value;
			}
			foreach (var field in Schema.AllFields) {
				if (field.Reload != ReloadPolicy.Live) {
					continue;
				}
				if (!values.TryGetValue(field.FullKey, out var raw)) {
					continue;
				}
				this.ApplySingleLive(field, raw);
			}
			Logger.Information("applied a live config reload");
		} catch (System.Exception ex) {
			Logger.Warning("live config reload failed: ", ex.Message);
		}
	}

	/// <summary>
	/// Re-applies a single Live field while running, on receiving the control message <c>{"c":"set","k":..,"v":..}</c>.
	/// The Live settings stored in the file (logging, cache, retry) only take effect through this path (an
	/// ephemeral application that creates no database row).
	/// A key that is not Live, an unknown key or an invalid value is swallowed with a warning (the sending
	/// pgfsctl has already done the first round of validation).
	/// </summary>
	private void ApplyLiveSet(string? key, string? raw) {
		if (string.IsNullOrEmpty(key) || raw == null) {
			Logger.Warning("control set: ignored because the key or the value was empty");
			return;
		}
		Field? target = null;
		foreach (var f in Schema.AllFields) {
			if (f.FullKey == key) {
				target = f;
				break;
			}
		}
		if (target == null) {
			Logger.Warning("control set: ignoring an unknown key: ", key);
			return;
		}
		if (target.Reload != ReloadPolicy.Live) {
			Logger.Warning("control set: ignoring a key that is not Live: ", key);
			return;
		}
		try {
			this.ApplySingleLive(target, raw);
			Logger.Information("control set applied: ", key, " = ", raw);
		} catch (System.Exception ex) {
			Logger.Warning("control set failed (", key, "): ", ex.Message);
		}
	}

	/// <summary>
	/// Applies the raw value of one Reload=Live field to the running runtime state. Shared by
	/// <see cref="ReloadLiveConfig"/> (the loop over every Live field coming from the database) and
	/// <see cref="ApplyLiveSet"/> (the single field of an inline set over NOTIFY).
	/// </summary>
	private void ApplySingleLive(Field field, string raw) {
		switch (field.FullKey) {
			case "logging.level":
				this.config.Logging.MinLevel = Schema.Logging.MinLevel.Parse(raw);
				Logger.MinLevel = this.config.Logging.MinLevel;
				break;
			case "logging.output":
				this.config.Logging.Output = Schema.Logging.Output.Parse(raw);
				LogSink.Configure(this.config.Logging.Output);
				break;
			case "database.retry_max_attempts":
				this.config.Database.RetryMaxAttempts = Schema.Database.RetryMaxAttempts.Parse(raw);
				this.ReconfigureRetry();
				break;
			case "database.retry_initial_delay_ms":
				this.config.Database.RetryInitialDelayMs = Schema.Database.RetryInitialDelayMs.Parse(raw);
				this.ReconfigureRetry();
				break;
			case "database.retry_max_delay_ms":
				this.config.Database.RetryMaxDelayMs = Schema.Database.RetryMaxDelayMs.Parse(raw);
				this.ReconfigureRetry();
				break;
			case "mount.cache_max_entries":
				this.config.Mount.CacheMaxEntries = Schema.Mount.CacheMaxEntries.Parse(raw);
				this.inodeCache.SetCapacity(this.config.Mount.CacheMaxEntries);
				// A pending inode cannot be evicted, so lowering the limit would put it at odds with metadata write-back.
				this.WarnMetadataCacheBudget();
				break;
			case "mount.cache_data_max_bytes":
				this.config.Mount.CacheDataMaxBytes = Schema.Mount.CacheDataMaxBytes.Parse(raw);
				this.contentCache.SetMaxBytes(this.config.Mount.CacheDataMaxBytes);
				break;
			case "mount.negative_cache_ttl_ms":
				this.config.Mount.NegativeCacheTtlMs = Schema.Mount.NegativeCacheTtlMs.Parse(raw);
				this.inodeCache.SetNegativeTtl(this.config.Mount.NegativeCacheTtlMs);
				break;
			case "mount.write_back":
				// The switch happens in **two phases** (1: stop accepting new dirty data, 2: FlushAll, 3: change the mode).
				this.ApplyWriteBackLive(Schema.Mount.WriteBack.Parse(raw));
				break;
			case "mount.write_back_max_bytes":
				this.config.Mount.WriteBackMaxBytes = Schema.Mount.WriteBackMaxBytes.Parse(raw);
				this.ApplyBackPressure();
				break;
			case "mount.write_back_interval_ms":
				this.config.Mount.WriteBackIntervalMs = Schema.Mount.WriteBackIntervalMs.Parse(raw);
				break;
			case "mount.write_back_metadata":
				// The switch happens in **two phases** (1: stop accepting new pending entries, 2: FlushAll,
				// 3: change the mode). With a single phase, a create running alongside the flip could be left
				// as a pending entry with nobody to flush it.
				this.ApplyMetadataWriteBackLive(Schema.Mount.WriteBackMetadata.Parse(raw));
				// Enabling it live runs the same budget check as startup (checking only at construction time would
				// mean no warning here).
				this.WarnMetadataCacheBudget();
				break;
			case "mount.write_back_metadata_exclusive_create":
				// This only changes where future creates go (a pending entry already in the ledger keeps the mark
				// it was born with), so it needs no two-phase flip like write_back_metadata does.
				this.config.Mount.WriteBackMetadataExclusiveCreate = Schema.Mount.WriteBackMetadataExclusiveCreate.Parse(raw);
				this.WarnExclusiveCreateDefer();
				break;
			case "mount.write_back_max_inodes":
				this.config.Mount.WriteBackMaxInodes = Schema.Mount.WriteBackMaxInodes.Parse(raw);
				this.ApplyInodeBackPressure();
				this.WarnMetadataCacheBudget();
				break;
			case "mount.write_back_flush_timeout_ms":
				this.config.Mount.WriteBackFlushTimeoutMs = Schema.Mount.WriteBackFlushTimeoutMs.Parse(raw);
				break;
			case "app.statfs":
				this.config.Statfs.Mode = Schema.Statfs.Mode.Parse(raw);
				break;
			case "app.enforce_permissions":
				this.config.App.EnforcePermissions = Schema.App.EnforcePermissions.Parse(raw);
				break;
			case "audit.enabled":
				this.config.Audit.Enabled = Schema.Audit.Enabled.Parse(raw);
				this.auditEnabled = this.config.Audit.Enabled;
				break;
		}
	}

	/// <summary>Re-applies the current values of database.retry_* to <see cref="Retry"/>.</summary>
	private void ReconfigureRetry() {
		Retry.Configure(this.config.Database.RetryMaxAttempts, this.config.Database.RetryInitialDelayMs, this.config.Database.RetryMaxDelayMs);
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
		// Control messages (config set and friends) travel a different path from inode changes and are always
		// processed, regardless of notify_enabled.
		// **Heavy control work never runs on the listener thread** (B-8). A two-phase flip, back-pressure or a
		// database write here would mean not a single invalidate from another client gets processed meanwhile.
		if (msg.Control == "reload" || msg.Control == "set" || msg.Control == "ping") {
			this.EnqueueControl(msg.Control, msg.Key, msg.Value);
			return;
		}
		// Data-change notifications (i/p/x/d) are only processed when notify_enabled is set: control always
		// LISTENs, but taking part in data coherence is opt-in, so that a mount with it OFF is never
		// invalidated behind its back by another client's change.
		if (!this.dataNotifyEnabled) {
			return;
		}
		// **"Drop everything" is handled before any individual invalidation, and then we are done.** The sender
		// is saying it does not know which ids were affected, so finer-grained work would be pointless
		// (review M-2).
		if (msg.Resync) {
			this.inodeCache.InvalidateAll();
			this.contentCache.InvalidateAllClean();
			Logger.Warning("another client dropped notifications, so the whole cache was discarded");
			return;
		}
		// Step 1: resolve the path before invalidating, because once the entry is removed byId can no longer find it.
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
		foreach (var dataId in msg.DataIds) {
			this.contentCache.InvalidateData(dataId);
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
	/// Notifies the other clients of a change this client made. A no-op when data-change notifications are
	/// disabled (database.notify_enabled=false) - the LISTEN is always established for control, but firing
	/// data changes is opt-in.
	/// Exceptions are swallowed internally, because a failed notification must not fail the write itself.
	/// </summary>
	private void Notify(
		System.Collections.Generic.IEnumerable<long>? inodeIds = null,
		System.Collections.Generic.IEnumerable<long>? parentIds = null,
		System.Collections.Generic.IEnumerable<string>? pathPrefixes = null,
		System.Collections.Generic.IEnumerable<long>? dataIds = null
	) {
		if (!this.dataNotifyEnabled) {
			return;
		}
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
		if (dataIds != null) {
			msg.DataIds.AddRange(dataIds);
		}
		// Nothing worth sending when everything is empty.
		if (!msg.Resync && msg.InodeIds.Count == 0 && msg.ParentIds.Count == 0 && msg.PathPrefixes.Count == 0 && msg.DataIds.Count == 0) {
			return;
		}
		this.EnqueueNotify(msg);
	}

	/// <summary>
	/// Sends exactly one "drop everything" when notifications were dropped. **The mark is cleared before
	/// sending** - anything dropped while the send is in flight is sent again on the next round (sending too
	/// often is safer than missing one).
	/// </summary>
	private void PublishResyncIfPending(NotifyChannel ch) {
		if (System.Threading.Interlocked.Exchange(ref this.notifyResyncPending, 0) == 0) { return; }
		if (!ch.Publish(new NotifyMessage { Resync = true })) {
			// If it could not be sent, put the obligation back so the next round fires it again.
			System.Threading.Interlocked.Exchange(ref this.notifyResyncPending, 1);
			Logger.Warning("could not send the discard-everything instruction (it is fired again with the next send)");
			return;
		}
		Logger.Warning("notifications were dropped, so the other clients were told to discard their whole cache");
	}

	/// <summary>
	/// **After LISTEN is re-established, the clean caches are dropped.** The notifications about what other
	/// clients wrote before LISTEN came back never arrived, so the local inode and content caches (which have no
	/// TTL) are **stale**. Before, a reconnect did nothing and kept returning old sizes and old contents. **Only
	/// the clean entries are dropped** - the unflushed dirty data and the pending inodes are this mount's own
	/// changes, so they stay (the same handling as a received "drop everything").
	/// </summary>
	private void OnNotifyReconnected() {
		if (!this.dataNotifyEnabled) { return; }
		this.inodeCache.InvalidateAll();
		this.contentCache.InvalidateAllClean();
		Logger.Warning("receiving notifications was re-established, so the whole cache was dropped in case changes were missed");
	}

	/// <summary>Starts the data-change notification worker (B-11). There is a single consumer, so the send order is preserved.</summary>
	private void StartNotifyLoop() {
		this.notifyTask = System.Threading.Tasks.Task.Run(() => {
			foreach (var msg in this.notifyQueue.GetConsumingEnumerable()) {
				var ch = this.notifyChannel;
				if (ch == null) { continue; }
				// **When notifications were dropped, send "drop everything" first.** It is fired here rather
				// than waiting for the queue to drain, because TryAdd fails while the queue is overflowing.
				this.PublishResyncIfPending(ch);
				// **A notification that could not be sent is folded into "drop everything"** (the ids are not
				// carried over; it is fired on the next round). A notification lost to a database blip or a payload
				// over 8000 bytes used to never reach the other clients. Publish swallows its own failures, but dying
				// here would stop every later notification, so it is guarded twice.
				try {
					if (!ch.Publish(msg)) { System.Threading.Interlocked.Exchange(ref this.notifyResyncPending, 1); }
				} catch (System.Exception ex) {
					System.Threading.Interlocked.Exchange(ref this.notifyResyncPending, 1);
					Logger.Warning("failed to send a notification (continuing): ", ex.Message);
				}
			}
			// **The obligation is honoured on the way out too** (Dispose only waits two seconds, so whatever
			// did not make it in time is folded into the "drop everything" message).
			var last = this.notifyChannel;
			if (last != null) { this.PublishResyncIfPending(last); }
		});
	}

	/// <summary>
	/// Queues a notification for the worker. **When the queue is full the message is dropped** - the producer
	/// may be holding the NSGate or a DirtyFile.Gate, so it is never made to wait. A drop is recorded as a
	/// rate-limited warning.
	/// </summary>
	private void EnqueueNotify(NotifyMessage msg) {
		try {
			if (this.notifyQueue.TryAdd(msg)) { return; }
		} catch (System.InvalidOperationException) {
			// CompleteAdding has already run = we are shutting down. Drop it.
			return;
		}
		// **Dropping one creates the obligation to send "drop everything"** (the ids are unknown, so it is folded).
		System.Threading.Interlocked.Exchange(ref this.notifyResyncPending, 1);
		var dropped = System.Threading.Interlocked.Increment(ref this.notifyDropped);
		var now = System.DateTime.UtcNow.Ticks;
		var last = System.Threading.Interlocked.Read(ref this.notifyDropWarnedAtTicks);
		if (now - last < Api.NotifyDropWarnIntervalTicks) { return; }
		if (System.Threading.Interlocked.CompareExchange(ref this.notifyDropWarnedAtTicks, now, last) != last) { return; }
		Logger.Warning("dropped change notifications because the queue was full (", dropped, " so far; other clients' caches may be stale)");
	}

	private string QualifiedTable(string baseName)
		=> $"{Pg.QuoteIdentifier(this.schemaName)}.{Pg.QuoteIdentifier(this.tableNamePrefix + baseName)}";

	// ------------------------------------------------------------------
	// cross-client mutual exclusion (pgfs_lock + SELECT FOR UPDATE)
	// ------------------------------------------------------------------
	//
	// Serializes the race where several clients (mount.pgfs and assign.pgfs sharing one database) modify the
	// same inode or data at the same time. It takes the row lock of pgfs_lock(target_id BIGINT PK), which is
	// released automatically when the transaction ends (COMMIT or ROLLBACK).
	//
	// The design is described in [docs/support_for_citus.md](../../../../docs/support_for_citus.md):
	//   - data lock: target_id = data_id (a positive integer)
	//   - inode lock: target_id = -inode_id (a negative integer, namespace-separated)
	//
	// On Citus, {prefix}lock is **not distributed**: it is placed on the coordinator as a single copy, a Citus
	// local table (citus_add_local_table_to_metadata). There are two reasons:
	//   - A distributed table refuses row locks when shard_replication_factor > 1 (statement-based replication
	//     could make the result differ between placements). We do not want the locking mechanism to change with
	//     the replication factor.
	//   - A Citus local table is "one row that really exists", so Citus routes a query entered through a worker
	//     to that same row as well, which means exclusion holds no matter which node was the entry point
	//     (measured: two different worker entry points serialize against each other). That is the decisive
	//     difference from an advisory lock, which is node-local PostgreSQL state.
	// Concentrating row locks in {prefix}lock alone means the replication factor of {prefix}inode / data /
	// data_chunk can be chosen freely (rf=1 behaves like RAID0, rf=N like an N-way mirror - a pure storage
	// redundancy choice).
	// When several locks are taken, **always in ascending target_id order** to avoid deadlock.

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
			// Ensure the row exists (a no-op when it already does), then take its row lock.
			// {prefix}lock is a Citus local table, so even on Citus this is the same single-row lock as on plain PostgreSQL.
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

	/// <summary>
	/// Resolves an inode's distribution key (<c>parent_id</c>). It looks in the in-memory
	/// <see cref="InodeCache"/> first and reads the database once when that misses (a read, so it takes no
	/// lock). Null when it cannot be found (= the inode is gone).
	///
	/// <para>
	/// <b>Why this is needed</b>: an UPDATE of <c>{prefix}inode</c> whose WHERE clause omits the distribution
	/// key makes Citus **fan out to every shard** (the Task Count in `EXPLAIN` equals the shard count). The
	/// consequences are (a) one metadata update becomes "shards x placements" remote statements and is slow,
	/// and (b) the order in which shard locks are taken becomes non-deterministic, so concurrency produces
	/// <c>40P01 distributed deadlock</c> (measured: it happened with an rsync of 700 files).
	/// Including <c>parent_id</c> in the WHERE clause turns it into a single-shard router query and fixes both.
	/// On plain PostgreSQL it is one extra condition and the behaviour is identical ((parent_id, id) is the PK).
	/// </para>
	/// </summary>
	private long? ResolveParentId(long id) {
		var cached = this.inodeCache.Get(id);
		if (cached != null) {
			return cached.ParentId;
		}
		return Pg.Query<long?>(
			this.connectionString,
			$"SELECT parent_id FROM {this.QualifiedTable("inode")} WHERE id = @id",
			new { id }
		).FirstOrDefault();
	}

	/// <summary>
	/// Confirms inside the transaction that the parent inode is still alive (it exists and is a directory), and
	/// throws <see cref="Api.ParentVanishedException"/> when it is not. **Lock the parent before calling this** -
	/// checking before the lock still leaves a window between the check and the INSERT.
	/// </summary>
	private void AssertParentAliveInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long parentId) {
		// The root (id = 0) cannot be deleted, so it needs no check.
		if (parentId == 0) { return; }
		if (this.ParentAliveInTx(conn, tx, parentId)) { return; }
		throw new Api.ParentVanishedException(parentId);
	}

	/// <summary>
	/// Whether the parent is alive. Two stages: **try the fast way first, and confirm with the reliable way when it misses.**
	/// <para>
	/// The fast way is a router query that supplies the grandparent id (so the distribution key
	/// <c>parent_id</c> applies; measured on the dev server at <c>Task Count 1</c> / 0.18 ms). The grandparent
	/// id comes from <see cref="InodeCache"/>, so it **does not find** a parent that a rename has moved under a
	/// different parent - but the reliable way below catches that case, so it **never produces a false ENOENT**.
	/// </para>
	/// <para>
	/// The reliable way is <c>WHERE id = @id</c> (no distribution key applies, so it is multi-shard on Citus;
	/// measured at <c>Task Count 8</c> / 0.50 ms). It is **only taken when the fast way missed**, so an ordinary
	/// create never pays for it.
	/// </summary>
	private bool ParentAliveInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long parentId) {
		var grandparent = this.inodeCache.Get(parentId)?.ParentId;
		if (grandparent is long gp && Api.IsDirectoryMode(this.LoadModeByRouterInTx(conn, tx, gp, parentId))) { return true; }
		return Api.IsDirectoryMode(this.LoadModeAnyShardInTx(conn, tx, parentId));
	}

	/// <summary>Reads <c>st_mode</c> with a router query that supplies the distribution key (<c>parent_id</c>).</summary>
	private int? LoadModeByRouterInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long grandparentId, long inodeId) {
		return conn.QueryFirstOrDefault<int?>(
			$"SELECT st_mode FROM {this.QualifiedTable("inode")} WHERE parent_id = @parent_id AND id = @id",
			new { parent_id = grandparentId, id = inodeId }, tx
		);
	}

	/// <summary>Reads <c>st_mode</c> without the distribution key (multi-shard on Citus).</summary>
	private int? LoadModeAnyShardInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long inodeId) {
		return conn.QueryFirstOrDefault<int?>(
			$"SELECT st_mode FROM {this.QualifiedTable("inode")} WHERE id = @id",
			new { id = inodeId }, tx
		);
	}

	/// <summary>Whether an <c>st_mode</c> (which may be null) denotes a directory. False when there is no row.</summary>
	private static bool IsDirectoryMode(int? stMode) {
		if (stMode is not int mode) { return false; }
		return Mode.IsDirectory(mode);
	}

	/// <summary>The in-transaction version of <see cref="ResolveParentId(long)"/> (the database read joins the same transaction).</summary>
	private long? ResolveParentId(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long id) {
		var cached = this.inodeCache.Get(id);
		if (cached != null) {
			return cached.ParentId;
		}
		return conn.QueryFirstOrDefault<long?>(
			$"SELECT parent_id FROM {this.QualifiedTable("inode")} WHERE id = @id",
			new { id }, tx
		);
	}

	/// <summary>Locks several inodes at once in ascending target_id order (for Rename, DeleteInode and CreateHardLink).</summary>
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
	/// **Whether this is a trace libfuse left when it deflected an <c>unlink</c> of an open file** (<c>.fuse_hiddenXXXXXXXXXXXXXXXX</c>).
	/// <para>
	/// libfuse with <c>hard_remove = 0</c> **turns an <c>unlink</c> of an open file into a rename to a hidden
	/// name**. That is how it provides POSIX's "the name is gone but the fd is alive" through the high-level
	/// API, but **in pgfs the hidden name is a real file in the database**, so **it shows up in the <c>ls</c> of
	/// other mounts** (measured). Removing it **from listings only** closes that.
	/// </para>
	/// <para>
	/// **Path resolution (<c>GetByPath</c>) never removes it.** Doing so would stop **the very mount that is
	/// keeping the fd alive from looking up its own hidden file** (libfuse issues <c>getattr</c> and
	/// <c>release</c> against the hidden name).
	/// </para>
	/// <para>
	/// <b>The test matches libfuse's format strictly</b> - rejecting on the prefix alone would also hide
	/// **a file the user named `.fuse_hidden...` themselves**. What libfuse creates is <c>.fuse_hidden</c>
	/// followed by **16 hex digits**.
	/// </para>
	/// </summary>
	public static bool IsLibfuseHidden(string? name) {
		const string prefix = ".fuse_hidden";
		if (name == null) { return false; }
		if (name.Length != prefix.Length + 16) { return false; }
		if (!name.StartsWith(prefix, System.StringComparison.Ordinal)) { return false; }
		for (var i = prefix.Length; i < name.Length; i++) {
			if (!System.Uri.IsHexDigit(name[i])) { return false; }
		}
		return true;
	}

	/// <summary>
	/// Lists the child inodes directly under a directory.
	/// It returns the entry cached in `InodeCache`'s childrenByParent when there is one, and otherwise reads
	/// from the database and stores the result in the cache. Api invalidates it with
	/// `InodeCache.InvalidateChildren(parentId)` whenever a child is created, deleted or renamed.
	/// </summary>
	public IEnumerable<Inode> ListChildren(long parentId) {
		var cached = this.inodeCache.GetChildren(parentId);
		if (cached != null) {
			return cached;
		}
		// Metadata write-back: capture the ledger's generation before reading the database. The merged result is
		// only put in the cache when the generation has not changed (the same technique as ContentCache's PutIfGeneration).
		var nsGeneration = this.dirtyNamespace.Generation;
		var sql = $@"
			SELECT id, parent_id, name, uname, gname, st_mode, st_nlink, st_size,
			       st_mtime, st_ctime, link_target, is_junction, data_id, xattr_names, xattr_values,
			       created_at, created_by, updated_at, updated_by
			FROM {this.QualifiedTable("inode")}
			WHERE parent_id = @parent_id AND id <> 0
			ORDER BY name
		";
		var list = Pg.Query<Inode>(this.connectionString, sql, new { parent_id = parentId }).ToList();
		// **libfuse's hidden files are removed from listings** so they never appear in another mount's `ls`. They
		// are not removed from path resolution - doing so would stop the very mount that is keeping the fd alive
		// from looking up its own hidden file.
		// **Dokan does not remove them** (<see cref="HideLibfuseLeftovers"/> = false): libfuse creates no leftovers
		// on Windows, so there is nothing to hide, and hiding would leave only "invisible yet undeletable".
		if (this.HideLibfuseLeftovers) {
			list.RemoveAll(child => Api.IsLibfuseHidden(child.Name));
		}
		// Prefetch the occupied bytes for st_blocks in a single query, so the getattr calls that follow do not go to the database.
		this.PrefetchOccupiedBytes(list);
		var merged = this.MergePendingChildren(parentId, list);
		// When the generation moved (= a create, rename or flush cut in during the merge) the result is not cached.
		// The value returned may be "the best available at that instant"; the next listing rebuilds it.
		if (this.dirtyNamespace.Generation == nsGeneration) { this.inodeCache.PutChildren(parentId, merged); }
		return merged;
	}

	/// <summary>
	/// Merges the pending children that have not been INSERTed yet into the child list read from the database
	/// (metadata write-back).
	/// **It dedupes by name and prefers the pending entry** - right after a flush commit the same name exists in
	/// both the database and the ledger.
	/// When the ledger is empty (= the feature is off) the database result is returned unchanged.
	/// </summary>
	private List<Inode> MergePendingChildren(long parentId, List<Inode> fromDb) {
		if (this.dirtyNamespace.IsEmpty) { return fromDb; }
		var pending = this.dirtyNamespace.PendingChildren(parentId);
		if (pending.Count == 0) { return fromDb; }
		var merged = new List<Inode>(fromDb.Count + pending.Count);
		var names = new HashSet<string>();
		var ids = new HashSet<long>();
		foreach (var inode in pending) {
			names.Add(inode.Name);
			ids.Add(inode.Id);
			merged.Add(inode);
		}
		// Deduping happens **by both name and id**. A coalesced rename between the flush commit and MarkPersisted
		// leaves different names on the database side and the ledger side, and deduping by name alone would list
		// the same inode twice under two names.
		foreach (var inode in fromDb) {
			if (names.Contains(inode.Name)) { continue; }
			if (ids.Contains(inode.Id)) { continue; }
			merged.Add(inode);
		}
		merged.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
		return merged;
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
		// mkdir is deliberately left **deferred (pending)**.
		// (Treating mkdir as a locking primitive would only take changing this to exclusive: true, which makes it
		//  materialize synchronously - but then a pending directory can never exist, and both the ancestor-chain
		//  INSERT and the dir/dir adoption of an existing id (Rekey) become unreachable code. The decision is
		//  recorded in docs/runtime-control-plane.md, the metadata write-back implementation status.)
		return this.InsertInode(parentId, name, uname, gname, modeWithDir, dataId: null, linkTarget: null, exclusive: false);
	}

	/// <summary>
	/// Creates a new regular-file inode under a parent directory (an empty file with no data).
	/// <paramref name="exclusive"/> means a create with <c>O_EXCL</c> (sync heuristic c): it is a locking
	/// primitive, so it is created **write-through rather than pending** and the exclusion is delegated to the
	/// database's unique constraint (deferring it would let two clients both succeed at taking the lock).
	/// </summary>
	public Inode? CreateFile(long parentId, string name, string uname, string gname, int mode, bool exclusive = false) {
		// Cross-platform: st_mode carries the regular-file bits (100000).
		var modeWithReg = (mode & 0xFFF) | Mode.S_IFREG;
		// The data_id is **fixed at create time** (docs/data-id-lifecycle.md). The row is not INSERTed; only the id
		// is allocated up front and put on the inode. That makes both of these hold at once:
		//   - a hardlink can be expressed even for an empty file (the link relationship can only be expressed as
		//     "the same data_id"),
		//   - st_ino (which derives from data_id) does not change when the content is written.
		// The inode INSERT already had a @data_id column, so **this costs no extra database round trip**.
		return this.InsertInode(parentId, name, uname, gname, modeWithReg, this.RentDataId(), linkTarget: null, exclusive);
	}

	/// <summary>
	/// Takes one <c>data_id</c> out of the reservation pool for a new file.
	/// **On failure it returns null and lets the create succeed** - a file must not fail to be created just
	/// because an id could not be allocated. An inode created with null gets its id the old way, on the first
	/// write through <see cref="EnsureDataRow"/> or through <see cref="CreateHardLink"/> (= exactly how a
	/// filesystem created before this change behaves).
	/// </summary>
	private long? RentDataId() {
		try {
			return this.dataIdReservation.Rent();
		} catch (System.Exception ex) {
			Logger.Warning("could not reserve a data_id, so the file is created without one: ", ex.Message);
			return null;
		}
	}

	private Inode? InsertInode(long parentId, string name, string uname, string gname, int stMode, long? dataId, string? linkTarget, bool exclusive) {
		// When flushes are failing repeatedly (the error state) no new creates are accepted. Accepting them would
		// only add more inodes that "succeeded" yet will never be flushed.
		this.ThrowIfWriteBackErrorState();
		// Metadata write-back: put it in the ledger as a pending inode without touching the database.
		// It falls back to write-through only when the id reservation fails, and in that case the parent has to be
		// materialized first, otherwise the row would carry a parent_id that does not exist in the database (an
		// unreachable orphan).
		// **O_EXCL is not deferred by default** (heuristic c) - if it went pending and materialized later, a name
		// collision would be resolved by the flush transaction's "DELETE + INSERT" and both clients could succeed.
		// It is only deferred when `mount.write_back_metadata_exclusive_create = defer` is chosen (trading
		// cross-client exclusion for coalescing, B-1).
		if (this.MetadataWriteBack && (!exclusive || this.ExclusiveCreateDeferred)) {
			return this.InsertInodePendingOrThrough(parentId, name, uname, gname, stMode, dataId, linkTarget, exclusive);
		}
		return this.InsertInodeThroughGated(parentId, name, uname, gname, stMode, dataId, linkTarget);
	}

	/// <summary>
	/// Tries to go pending and falls back to write-through when it cannot (the create path while metadata
	/// write-back is on).
	/// **A name collision alone does not fall back: it returns null (= EEXIST)**. A pending sibling has no row
	/// in the database, so <c>ON CONFLICT</c> would not fire and two inodes with the same name would exist -
	/// and a later flush would DELETE the winner's row, its data row and every chunk (destruction that
	/// write-through cannot produce even in principle).
	/// </summary>
	private Inode? InsertInodePendingOrThrough(long parentId, string name, string uname, string gname, int stMode, long? dataId, string? linkTarget, bool exclusive) {
		var pending = this.InsertInodePending(parentId, name, uname, gname, stMode, dataId, linkTarget, exclusive, out var nameConflict);
		if (pending != null) { return pending; }
		if (nameConflict) { return null; }
		return this.InsertInodeThroughGated(parentId, name, uname, gname, stMode, dataId, linkTarget);
	}

	/// <summary>
	/// The write-through create. When the ledger is not empty it **materializes the parent and any pending
	/// sibling with the same name first**, then INSERTs.
	/// The NSGate is held until the INSERT completes: if a background flush materialized a same-named pending
	/// entry midway, it would slip past this INSERT's <c>ON CONFLICT</c> and two rows with the same name would exist.
	/// </summary>
	private Inode? InsertInodeThroughGated(long parentId, string name, string uname, string gname, int stMode, long? dataId, string? linkTarget) {
		if (this.dirtyNamespace.IsEmpty) { return this.InsertInodeThrough(parentId, name, uname, gname, stMode, dataId, linkTarget); }
		lock (this.nsGate) {
			this.MaterializePendingLocked(parentId);
			this.MaterializePendingChildLocked(parentId, name);
			return this.InsertInodeThrough(parentId, name, uname, gname, stMode, dataId, linkTarget);
		}
	}

	/// <summary>The original write-through path that INSERTs the inode row. Null (= EEXIST) when the name is taken.</summary>
	private Inode? InsertInodeThrough(long parentId, string name, string uname, string gname, int stMode, long? dataId, string? linkTarget) {
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
			// The INSERT is put in an explicit transaction so the audit row lands in the same transaction as the
			// operation (ON CONFLICT is a single statement by itself, but the audit row has to commit atomically with it).
			// On Citus with rf >= 2 a concurrent write can make this the victim of a distributed deadlock, so 40P01 is
			// retried (the victim has rolled back completely = no INSERT went in, so retrying is safe).
			Inode? inserted = null;
			for (var attempt = 1; inserted == null; attempt++) {
				try {
					using var conn = this.NewConnection();
					using var tx = conn.BeginTransaction();
					// **Confirm inside the transaction that the parent is still alive.** The parent's lock is taken first, so
					// this serializes against a concurrent rmdir (the delete side locks its target = this parent).
					// Without it, "A caches the parent -> B rmdirs it -> A creates the child" leaves **an unreachable orphan**
					// that can no longer be removed through the filesystem (only with SQL).
					this.LockInode(conn, tx, parentId);
					this.AssertParentAliveInTx(conn, tx, parentId);
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
				} catch (Npgsql.PostgresException ex) when (Api.IsRetryableDeadlock(ex) && attempt < Api.DeadlockMaxAttempts) {
					Logger.Warning("create: retrying after a distributed deadlock (", ex.SqlState, ") (attempt ", attempt, "/", Api.DeadlockMaxAttempts, ") name:", name);
					Api.SleepBeforeDeadlockRetry(attempt);
				}
			}
			// The caller knows the full path for path-based cache updates, so here we cache only by id
			this.inodeCache.Put(inserted, path: null!);
			// Discard the parent directory's child-list cache (re-fetched next time by ListChildren)
			this.inodeCache.InvalidateChildren(parentId);
			// Notify other clients: the parent's child list changed
			this.Notify(parentIds: [parentId]);
			return inserted;
		} catch (Api.ParentVanishedException) {
			// **Do not let the catch-all swallow this.** A null here is read by the caller as a name collision and
			// turned into EEXIST, which produces the unreadable error "File exists" for a path whose parent is gone
			// (observed on real Linux hardware). It is rethrown so the caller (FUSE / Dokan) can tell them apart.
			throw;
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
		// While in the error state, **deleting a materialized body is refused** (B-7). An exact cancellation of a
		// pending entry is still allowed (it touches no database, so nothing gets worse, and it is the recovery
		// route of B-1).
		this.ThrowIfErrorStateBlocksDestroy(inode, "delete");
		// Metadata write-back: a pending entry is cancelled without writing anything to the database (the state is
		// re-checked under the NSGate to avoid crossing a flush). A materialized one takes the normal write-through delete.
		if (this.dirtyNamespace.IsEmpty) { return this.DeleteInodeThrough(inode); }
		lock (this.nsGate) {
			if (this.CancelPendingInode(inode)) { return true; }
			return this.DeleteInodeThrough(inode);
		}
	}

	/// <summary>The original write-through path that removes the inode row from the database.</summary>
	private bool DeleteInodeThrough(Inode inode) {
		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();

		// Serialize concurrent changes to the parent children list and the body itself. Fixed ascending target_id order avoids deadlock.
		this.LockInodes(conn, tx, inode.Id, inode.ParentId);

		var siblingIds = this.DeleteInodeInTx(conn, tx, inode, out var rows);

		tx.Commit();
		if (rows > 0) {
			// The target is gone, so the synchronous-close mark and the failure counter are cleared too.
			// Leaving them would make the mark grow monotonically (degrading heuristic b) and leave an error state that
			// is never released.
			this.ClearSyncOnClose(inode.Id, inode.DataId);
			this.ClearFlushFailure(inode.Id, inode.DataId);
			// Remove a materialized ledger entry if one is left (keeping it would make a ghost in the ListChildren merge).
			this.dirtyNamespace.Forget(inode.Id);
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
	/// Deletes the inode row inside the transaction and finishes the data references (releasing the chunks when
	/// this was the last reference, or recomputing st_nlink when hardlinks remain), including the delete audit.
	/// **The caller must already hold the locks on the target and its parent.**
	/// The return value is the list of hardlink sibling ids (invalidating caches and notifying after the commit
	/// is the caller's responsibility).
	/// <para>
	/// <paramref name="writeAudit"/> = false means "the caller has a delete audit row **captured at operation
	/// time** of its own" (a name collision in metadata write-back, or rename-over-existing). Writing one here
	/// would produce two rows for the same delete, and the background flush thread cannot obtain the caller anyway.
	/// </para>
	/// </summary>
	private List<long> DeleteInodeInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, Inode inode, out int rows, bool writeAudit = true) {
		// The caller checks whether the directory is empty outside the transaction. If another client (or this
		// mount's own namespace flush) INSERTed a child after that, deleting here would leave an unreachable orphan
		// subtree, so **the check is repeated inside the transaction**. With metadata write-back the window is wider,
		// because a pending entry can linger from the interval all the way to an error latch.
		this.AssertDirectoryEmptyInTx(conn, tx, inode);
		// **Have the DELETE return the st_nlink as it was just before the delete** (it decides whether the sibling search below can be skipped).
		var deletedNlinks = conn.Query<int>(
			$"DELETE FROM {this.QualifiedTable("inode")} WHERE id = @id AND id <> 0 RETURNING st_nlink",
			new { id = inode.Id },
			tx
		).ToList();
		rows = deletedNlinks.Count;
		if (Logger.IsTraceEnabled) { Logger.Trace("DELETE inode id:", inode.Id, " rows:", rows); }

		// The ids of sibling inodes left by hardlinks (collected inside the transaction, for cache invalidation).
		List<long> siblingIds = new();
		if (rows > 0 && inode.DataId is long dataId) {
			siblingIds = this.ReleaseOrRelinkDataInTx(conn, tx, dataId, deletedNlinks[0], inode);
		}

		if (rows > 0 && writeAudit) {
			this.WriteAudit(conn, tx, AuditOp.Delete, inode.Id, inode.ParentId, inode.Name, null);
		}
		return siblingIds;
	}

	/// <summary>
	/// Cleans up the body after the inode row is gone. When no sibling remains the body is released as well;
	/// when one does, <c>st_nlink</c> is recounted. The return value is the ids of the remaining siblings (for
	/// cache invalidation and notification).
	/// <para>
	/// <paramref name="deletedNlink"/> is **the <c>st_nlink</c> just before the delete, as returned by the
	/// DELETE**. At 1 or below there are no siblings, so the entire <c>data_id</c> search is skipped
	/// (<c>data_id</c> is not the inode's distribution key, so on Citus that search is multi-shard). Since
	/// <c>data_id</c> became fixed at create time, this search also runs for files that were never written, so
	/// skipping it matters under an <c>rm -rf</c>-heavy workload (docs/data-id-lifecycle.md). The key point is
	/// that **the decision never uses the cache**: it reads the database value in the same transaction, so a
	/// hardlink another client made cannot be missed (missing it would delete a sibling's body).
	/// </para>
	/// </summary>
	private List<long> ReleaseOrRelinkDataInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId, int deletedNlink, Inode inode) {
		if (deletedNlink <= 1) {
			// **Stage C-2: keep the body when a handle is still open** (POSIX's "unlinked but the fd is alive").
			// It is dropped on the last close (Api.CloseHandle). If the process dies, an orphaned data row is left, but
			// **it never appears in the namespace**, so it is not visible garbage like `.fuse_hidden` (the prune of C-3
			// cleans it up).
			if (this.openInodes.CountOf(inode.Id) > 0) {
				this.openInodes.MarkOrphan(inode.Id, dataId);
				if (Logger.IsDebugEnabled) { Logger.Debug("unlink: still open, so the body is kept inode:", inode.Id, " data:", dataId); }
				return new List<long>();
			}
			// The last reference: release the chunks and the data row as well.
			DropAllChunks(conn, tx, dataId);
			DropDataRow(conn, tx, dataId);
			return new List<long>();
		}
		var siblingIds = conn.Query<long>(
			$"SELECT id FROM {this.QualifiedTable("inode")} WHERE data_id = @data_id",
			new { data_id = dataId },
			tx
		).ToList();
		var refCount = siblingIds.Count;
		if (Logger.IsTraceEnabled) { Logger.Trace("SELECT COUNT inode WHERE data_id:", dataId, " = ", refCount); }
		if (refCount == 0) {
			// st_nlink was 2 or more yet no sibling exists = a broken nlink (a leftover of an old bug). Release the body.
			DropAllChunks(conn, tx, dataId);
			DropDataRow(conn, tx, dataId);
			return siblingIds;
		}
		// Hardlinks remain: bring the st_nlink of the surviving inodes in line with refCount.
		var updated = conn.Execute(
			$@"UPDATE {this.QualifiedTable("inode")}
			   SET st_nlink = @cnt, st_ctime = (current_timestamp AT TIME ZONE 'UTC'), updated_at = (current_timestamp AT TIME ZONE 'UTC')
			   WHERE data_id = @data_id",
			new { cnt = refCount, data_id = dataId },
			tx
		);
		if (Logger.IsTraceEnabled) { Logger.Trace("UPDATE inode SET st_nlink:", refCount, " WHERE data_id:", dataId, " rows:", updated); }
		return siblingIds;
	}

	/// <summary>
	/// Confirms inside the transaction that the directory being deleted really is empty. When it is not, it
	/// throws and rolls the whole transaction back (the caller turns that into -EIO; this race is rare enough
	/// that failing is better than silently orphaning a subtree).
	/// </summary>
	private void AssertDirectoryEmptyInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, Inode inode) {
		if (!inode.IsDirectory) { return; }
		var found = conn.QueryFirstOrDefault<int?>(
			$"SELECT 1 FROM {this.QualifiedTable("inode")} WHERE parent_id = @parent_id AND id <> 0 LIMIT 1",
			new { parent_id = inode.Id }, tx
		);
		if (found == null) { return; }
		throw new InvalidOperationException($"DeleteInodeInTx: directory {inode.Id} ({inode.Name}) cannot be deleted because a child was created after the emptiness check");
	}

	/// <summary>
	/// Checks whether a directory is empty. False when it is not empty or does not exist.
	/// </summary>
	public bool IsDirectoryEmpty(long inodeId) {
		// Metadata write-back: looking only at the database would allow rmdir of a directory that has pending children.
		if (this.dirtyNamespace.HasPendingChildren(inodeId)) { return false; }
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
		// Metadata write-back: a chmod of a pending inode is coalesced in the ledger (zero transactions).
		// A persisted inode keeps write-through (permissions are never deferred).
		if (this.CoalesceMode(id, mode)) { return true; }
		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();
		this.LockInode(conn, tx, id);
		// Include the distribution key parent_id in the WHERE clause to make it a router query (see the note on ResolveParentId).
		var parentId = this.ResolveParentId(conn, tx, id);
		if (parentId == null) {
			tx.Rollback();
			return false;
		}
		var sql = $@"
			UPDATE {this.QualifiedTable("inode")}
			SET st_mode = @mode, st_ctime = (current_timestamp AT TIME ZONE 'UTC'), updated_at = (current_timestamp AT TIME ZONE 'UTC')
			WHERE parent_id = @parent_id AND id = @id
		";
		var rows = conn.Execute(sql, new { parent_id = parentId.Value, id, mode }, tx);
		if (rows > 0) {
			this.WriteAudit(conn, tx, AuditOp.Chmod, id, null, null, new { mode = Convert.ToString(mode, 8) });
		}
		tx.Commit();
		if (rows > 0) {
			// Also sync the in-memory cached inode (avoid Invalidate, which would break consistency between childrenByParent and byId)
			var cached = this.inodeCache.Get(id);
			if (cached != null) {
				cached.Mode = mode;
				cached.Ctime = Pg.UtcNow;
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
		// Metadata write-back: a chown of a pending inode is coalesced in the ledger (zero transactions).
		if (this.CoalesceOwner(id, uname, gname)) { return true; }
		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();
		this.LockInode(conn, tx, id);
		var parentId = this.ResolveParentId(conn, tx, id);
		if (parentId == null) {
			tx.Rollback();
			return false;
		}
		var sql = $@"
			UPDATE {this.QualifiedTable("inode")}
			SET uname = @uname, gname = @gname,
			    st_ctime = (current_timestamp AT TIME ZONE 'UTC'), updated_at = (current_timestamp AT TIME ZONE 'UTC')
			WHERE parent_id = @parent_id AND id = @id
		";
		var rows = conn.Execute(sql, new { parent_id = parentId.Value, id, uname, gname }, tx);
		if (rows > 0) {
			this.WriteAudit(conn, tx, AuditOp.Chown, id, null, null, new { uname, gname });
		}
		tx.Commit();
		if (rows > 0) {
			var cached = this.inodeCache.Get(id);
			if (cached != null) {
				cached.UserName = uname;
				cached.GroupName = gname;
				cached.Ctime = Pg.UtcNow;
			}
			this.Notify(inodeIds: [id]);
		}
		return rows > 0;
	}

	/// <summary>
	/// Updates st_size. Syncing with the data body is not yet implemented (metadata only).
	/// </summary>
	public bool UpdateSize(long id, long length) {
		// Metadata write-back: an explicit size change on a pending inode is coalesced in the ledger (zero transactions).
		if (this.CoalesceSize(id, length)) { return true; }
		// Write-back: fix the size first, so an unflushed st_size cannot land on top of it later.
		this.FlushBeforeMetadataWrite(id);
		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();
		this.LockInode(conn, tx, id);
		var parentId = this.ResolveParentId(conn, tx, id);
		if (parentId == null) {
			tx.Rollback();
			return false;
		}
		var sql = $@"
			UPDATE {this.QualifiedTable("inode")}
			SET st_size = @length,
			    st_mtime = (current_timestamp AT TIME ZONE 'UTC'), st_ctime = (current_timestamp AT TIME ZONE 'UTC'),
			    updated_at = (current_timestamp AT TIME ZONE 'UTC')
			WHERE parent_id = @parent_id AND id = @id
		";
		var rows = conn.Execute(sql, new { parent_id = parentId.Value, id, length }, tx);
		tx.Commit();
		if (rows > 0) {
			var cached = this.inodeCache.Get(id);
			if (cached != null) {
				var now = Pg.UtcNow;
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
		// Metadata write-back: a utimens on a pending inode is coalesced in the ledger (zero transactions).
		if (this.CoalesceTimestamps(id, mtime)) { return true; }
		// Write-back: stop an unflushed st_mtime from landing later and overwriting the explicit value.
		this.FlushBeforeMetadataWrite(id);
		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();
		this.LockInode(conn, tx, id);
		var parentId = this.ResolveParentId(conn, tx, id);
		if (parentId == null) {
			tx.Rollback();
			return false;
		}
		var sql = $@"
			UPDATE {this.QualifiedTable("inode")}
			SET st_mtime = @mtime, updated_at = (current_timestamp AT TIME ZONE 'UTC')
			WHERE parent_id = @parent_id AND id = @id
		";
		var rows = conn.Execute(sql, new { parent_id = parentId.Value, id, mtime = Pg.ToDbUtc(mtime) }, tx);
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
	public bool Rename(long id, long newParentId, string newName) { return this.Rename(id, newParentId, newName, replaceTarget: null); }

	/// <summary>
	/// Moves or renames an inode to <paramref name="newName"/> under <paramref name="newParentId"/>.
	/// <paramref name="replaceTarget"/> is the victim of a rename-over-existing: **the delete and the rename
	/// happen in one transaction**. Doing the delete in an earlier transaction would open a window where a crash
	/// in between leaves "the old target is gone but the rename never arrived" = both the old and the new file
	/// are lost, which violates rename(2)'s atomic-replacement contract.
	/// </summary>
	public bool Rename(long id, long newParentId, string newName, Inode? replaceTarget) {
		// While in the error state, **a rename that would delete a persisted replacement target is refused** (B-7).
		// A pending replacement target is allowed (it is not in the database yet, so there is nothing to destroy).
		if (replaceTarget != null) { this.ThrowIfErrorStateBlocksDestroy(replaceTarget, "replacement by rename"); }
		// **When the source and the replacement target are the same inode, succeed without doing anything.**
		// A replacement deletes the target and then UPDATEs the source, so with the same inode it would delete
		// itself. On Linux the VFS rejects `rename("a", "a")` before it gets here, but **the Dokan path has no such
		// protection**, so Core is made the last line of defence (POSIX's rename(2) also succeeds, unchanged, when renaming a file onto itself).
		if (replaceTarget != null && replaceTarget.Id == id) {
			Logger.Warning("rename: the source and the replacement target are the same inode, so this returns success as a no-op id:", id, " name:", newName);
			return true;
		}
		// Metadata write-back: a rename of a pending entry with no existing target at the destination is coalesced in the ledger (zero transactions).
		if (replaceTarget == null && this.CoalesceRename(id, newParentId, newName)) { return true; }
		if (this.dirtyNamespace.IsEmpty) { return this.RenameThrough(id, newParentId, newName, replaceTarget); }
		// A write-through operation that consults pending entries: materialize the source, the new parent and the
		// replacement target first. The NSGate is held until the rename transaction completes so it cannot cross a
		// background flush (the lock hierarchy is NSGate -> Gate -> transaction).
		lock (this.nsGate) {
			// Sync heuristic (a): replacing a pending source does "delete the target + materialize + dirty data +
			// audit" in **a single transaction** (this is the shape an editor save, sed -i, dpkg and rsync's
			// tmp-plus-rename all produce).
			if (replaceTarget != null && this.ReplacePendingOverTargetLocked(id, newParentId, newName, replaceTarget)) { return true; }
			this.MaterializePendingLocked(id);
			this.MaterializePendingLocked(newParentId);
			this.MaterializePendingLocked(replaceTarget?.Id ?? 0);
			return this.RenameThrough(id, newParentId, newName, replaceTarget);
		}
	}

	/// <summary>The original write-through path that moves the inode row in the database.</summary>
	private bool RenameThrough(long id, long newParentId, string newName, Inode? replaceTarget) {
		// Capture the old parent id first (for invalidating its child list and for the in-place update).
		var cached = this.inodeCache.Get(id);
		// Capture the rename source path for the notify payload (descendants also need to be dropped from byPath)
		string? oldPath = null;
		if (cached != null && this.inodeCache.TryGetPath(id, out var p)) {
			oldPath = p;
		}

		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();

		// Citus note: including the distribution key parent_id in the WHERE clause routes this to a single shard
		// (avoiding a broadcast). When it cannot be taken from the cache, parent_id is fetched with a broadcast
		// SELECT first and the lock is taken after that.
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

		// Lock the old parent, the new parent and the target inode (plus any replacement target), always in
		// ascending target_id order.
		// When hintParentId is stale (another client already renamed it) the SELECT below returns nothing and this
		// bails out with false (the wrongly taken old-parent lock is released when the transaction ends).
		var lockIds = new List<long> { id, hintParentId.Value, newParentId };
		if (replaceTarget != null) { lockIds.Add(replaceTarget.Id); }
		this.LockInodes(conn, tx, lockIds.ToArray());

		// Read every column of the old row. Bail out with false when it does not exist.
		// The DELETE+INSERT path feeds these values straight into the new row.
		//
		// **No `FOR UPDATE` here**: LockInodes just above already took exclusive ownership of the target inode via
		// {prefix}lock, so a row lock would be redundant - and once {prefix}inode is distributed on Citus with
		// shard_replication_factor > 1, row locks are refused under statement-based replication
		// ("could not run distributed query with FOR UPDATE/SHARE commands"). Exclusion is concentrated in
		// {prefix}lock alone (the design is in docs/support_for_citus.md).
		var old = conn.QueryFirstOrDefault<Pgfs.Core.Models.Inode>(
			$@"SELECT id, parent_id, name, uname, gname, st_mode, st_nlink, st_size,
			          st_mtime, st_ctime, link_target, is_junction, data_id, xattr_names, xattr_values,
			          created_at, created_by, updated_at, updated_by
			   FROM {inodeTable} WHERE parent_id = @parent_id AND id = @id",
			new { parent_id = hintParentId.Value, id }, tx
		);
		if (old == null) {
			tx.Rollback();
			return false;
		}

		// rename-over-existing: delete the replacement target in the same transaction (abort when the occupant has changed).
		if (!this.TryReplaceRenameTarget(conn, tx, newParentId, newName, replaceTarget, out var replacedSiblingIds)) {
			tx.Rollback();
			return false;
		}

		int rows;
		if (old.ParentId == newParentId) {
			// Within the same parent: just a name change → a light UPDATE. It does not cross shards, so it is safe on Citus too.
			rows = conn.Execute(
				$@"UPDATE {inodeTable}
				   SET name = @name, st_ctime = (current_timestamp AT TIME ZONE 'UTC'), updated_at = (current_timestamp AT TIME ZONE 'UTC')
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
				           @st_mtime, (current_timestamp AT TIME ZONE 'UTC'), @link_target, @is_junction, @data_id, @xattr_names, @xattr_values,
				           @created_at, @created_by, (current_timestamp AT TIME ZONE 'UTC'), @updated_by)",
				new {
					id,
					parent_id = newParentId,
					name = newName,
					uname = old.UserName,
					gname = old.GroupName,
					st_mode = old.Mode,
					st_nlink = old.NLink,
					st_size = old.Size,
					st_mtime = Pg.ToDbUtc(old.Mtime),
					link_target = old.LinkTarget,
					is_junction = old.IsJunction,
					data_id = old.DataId,
					xattr_names = old.xattr_names,
					xattr_values = old.xattr_values,
					created_at = Pg.ToDbUtc(old.created_at),
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
				cached.Ctime = Pg.UtcNow;
			}
			// The child-list cache must be discarded for both since the parent changed; re-fetched next time by ListChildren
			this.inodeCache.InvalidateChildren(newParentId);
			var parents = new List<long> { newParentId };
			if (old.ParentId != newParentId) {
				this.inodeCache.InvalidateChildren(old.ParentId);
				parents.Add(old.ParentId);
			}
			// Drop the replaced target and its hardlink siblings from the cache, and include them in the notification.
			var replacedIds = new List<long>(replacedSiblingIds);
			if (replaceTarget != null) { replacedIds.Add(replaceTarget.Id); }
			foreach (var replacedId in replacedIds) {
				this.ForgetReplaced(replacedId);
			}
			// Move the ledger's (parent, name) index to the new position (when a materialized entry is still there).
			this.ReindexPersisted(id, newParentId, newName);
			var notifyIds = new List<long> { id };
			notifyIds.AddRange(replacedIds);
			// Notify the other clients. When oldPath is available, have them invalidate everything under that byPath prefix.
			string[]? prefixes = oldPath switch {
				null => null,
				_ => new[] { oldPath },
			};
			this.Notify(inodeIds: notifyIds, parentIds: parents, pathPrefixes: prefixes);
		}
		return rows > 0;
	}

	/// <summary>
	/// Deletes the replacement target of a rename-over-existing inside the transaction. When another client has
	/// moved (parent, name) since the caller took its snapshot (<paramref name="replaceTarget"/>), it returns
	/// false so an unrelated row is not deleted as collateral (= the whole rename is aborted). When the
	/// replacement target is already gone it does nothing and returns true.
	/// The caller must already hold the lock on <paramref name="replaceTarget"/>.
	/// </summary>
	private bool TryReplaceRenameTarget(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long newParentId, string newName, Inode? replaceTarget, out List<long> siblingIds) {
		siblingIds = new List<long>();
		if (replaceTarget == null) { return true; }
		// Re-read the current occupant of (newParentId, newName) after taking the lock (the snapshot can be stale).
		var occupant = conn.QueryFirstOrDefault<Pgfs.Core.Models.Inode>(
			$@"SELECT id, parent_id, name, uname, gname, st_mode, st_nlink, st_size,
			          st_mtime, st_ctime, link_target, is_junction, data_id, xattr_names, xattr_values,
			          created_at, created_by, updated_at, updated_by
			   FROM {this.QualifiedTable("inode")} WHERE parent_id = @parent_id AND name = @name",
			new { parent_id = newParentId, name = newName }, tx
		);
		if (occupant == null) { return true; }
		if (occupant.Id != replaceTarget.Id) { return false; }
		siblingIds = this.DeleteInodeInTx(conn, tx, occupant, out _);
		return true;
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
		// xattr stays write-through (coalescing an array UPDATE in the ledger is not worth it). A pending inode is materialized first.
		this.MaterializePending(inodeId);
		// The existence check (for the createOnly / replaceOnly decision). Null when the inode is absent.
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
			    updated_at = (current_timestamp AT TIME ZONE 'UTC')
			WHERE parent_id = @parent_id AND id = @id
		";
		// Include the distribution key parent_id to make it a router query (see the note on ResolveParentId).
		var parentId = this.ResolveParentId(inodeId);
		if (parentId == null) {
			return false;
		}
		var rows = Pg.Execute(this.connectionString, sql, new { parent_id = parentId.Value, id = inodeId, name, value = bytes });
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
		// xattr stays write-through. A pending inode is materialized first.
		this.MaterializePending(inodeId);
		// A single atomic UPDATE that removes the index of name from both arrays at once. The right-hand side is evaluated against the pre-update row.
		var sql = $@"
			UPDATE {this.QualifiedTable("inode")}
			SET xattr_names  = xattr_names[1:array_position(xattr_names, @name)-1]
			                 || xattr_names[array_position(xattr_names, @name)+1:],
			    xattr_values = xattr_values[1:array_position(xattr_names, @name)-1]
			                 || xattr_values[array_position(xattr_names, @name)+1:],
			    updated_at = (current_timestamp AT TIME ZONE 'UTC')
			WHERE parent_id = @parent_id AND id = @id AND array_position(xattr_names, @name) IS NOT NULL
		";
		var parentId = this.ResolveParentId(inodeId);
		if (parentId == null) {
			return false;
		}
		var rows = Pg.Execute(this.connectionString, sql, new { parent_id = parentId.Value, id = inodeId, name });
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
		return this.InsertInode(parentId, name, uname, gname, stMode, dataId: null, linkTarget: target, exclusive: false);
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
		// hardlink stays write-through. A pending source or new parent is materialized first (a data_id cannot be
		// shared with something that has no row in the database). **A pending sibling occupying the link name** is
		// materialized too - leaving it would stop the ON CONFLICT below from firing and produce two inodes with the same name.
		this.MaterializePendingForCreate(newParentId, newName, source.Id, newParentId);

		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();

		// Lock the source inode (nlink update) and the new parent (children update) in fixed ascending target_id order.
		this.LockInodes(conn, tx, source.Id, newParentId);

		// **Confirm the new parent is still alive.** This path INSERTs by itself rather than going through
		// `InsertInode`, so the check inside InsertInodeThrough does not apply. Without it,
		// `ln existing deleted-dir/name` leaves an unreachable orphan. The locks were taken above, so this check
		// serializes against a delete.
		this.AssertParentAliveInTx(conn, tx, newParentId);

		// Compatibility with older filesystems: a file that does not have a data_id yet (an empty file created
		// before this change) gets one allocated here and attached to the source. A link relationship in pgfs can
		// only be expressed as "pointing at the same data_id", so linking while it is still null would share
		// neither st_nlink nor st_ino (docs/data-id-lifecycle.md).
		this.AttachDataIdForLinkInTx(conn, tx, source);

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
				   SET st_nlink = st_nlink + 1, st_ctime = (current_timestamp AT TIME ZONE 'UTC'), updated_at = (current_timestamp AT TIME ZONE 'UTC')
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
	/// The filesystem's nominal capacity (derived from <see cref="Pgfs.Core.Config.FileSystemConfig.MaxFileSize"/>, <b>stored in the database</b>).
	/// <para>
	/// mkfs writes it into <c>{prefix}settings</c> (<c>SaveTo = SaveTarget.Db</c>). **It is not stored in pgfs.toml.**
	/// </para>
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
	// One file's body is held as a single `pgfs_data` row plus several `pgfs_data_chunk` rows underneath it
	// (one chunk = one bytea). The chunk size lives in the `pgfs_data.chunk_size` column (the first INSERT
	// takes it from `Pgfs.Core.Config.FileSystemConfig.DefaultChunkSize`).
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

		var chunkSize = this.ResolveChunkSizeForRead(conn, tx, inode.DataId.Value);

		var totalRead = 0;
		var curOffset = offset;
		var remaining = maxLen;
		var destPos = 0;
		var dataId = inode.DataId.Value;
		// When the content cache is enabled, capture the generation as the read starts (PutIfGeneration uses it to close the stale race).
		var cacheGen = this.contentCache.Generation;

		while (remaining > 0) {
			var chunkIndex = (int)(curOffset / chunkSize);
			var offsetInChunk = (int)(curOffset % chunkSize);
			var bytesInThisChunk = Math.Min(remaining, chunkSize - offsetInChunk);

			this.FillChunk(conn, tx, dataId, chunkIndex, offsetInChunk, cacheGen, destination.Slice(destPos, bytesInThisChunk));

			destPos += bytesInThisChunk;
			curOffset += bytesInThisChunk;
			remaining -= bytesInThisChunk;
			totalRead += bytesInThisChunk;
		}

		tx.Commit();
		return totalRead;
	}

	/// <summary>
	/// Writes into an inode's body, creating chunk rows as needed.
	/// st_size is extended afterwards.
	/// <para>
	/// When <c>mount.write_back</c> is on, the database is not touched at all: the write is put in
	/// <see cref="DirtySet"/> and returns (the flush runs on <c>fsync</c>, <c>close</c>, the timer, the dirty
	/// limit or unmount).
	/// </para>
	/// </summary>
	public int WriteData(Inode inode, long offset, ReadOnlySpan<byte> source) {
		if (source.Length == 0 || offset < 0) {
			return 0;
		}
		// While flushes keep failing (the error state) no new writes are accepted (error floor 3).
		// Accepting them would only pile up more dirty data that "succeeded" yet will never be flushed.
		this.ThrowIfWriteBackErrorState();
		// While intake is closed (phase 1 of a live disable) writes go write-through. **Without checking here, new
		// dirty data would be born after the flip completed** (the same check-then-act as B-9 on the metadata side).
		if (this.config.Mount.WriteBack && !this.dataIntakeClosed) {
			return this.WriteDataBuffered(inode, offset, source);
		}
		return this.WriteDataThrough(inode, offset, source);
	}

	/// <summary>
	/// **Append** (the filesystem decides where the end is). The body of <see cref="AppendData(OpenFileContext, ReadOnlySpan{byte})"/>.
	/// <para>
	/// Write-through **fixes the end inside the write transaction**. No signalling trick such as passing a
	/// negative offset to `WriteData` is used - **the `offset < 0` guard at the entrance returns 0, which FUSE
	/// sees as a short write = a failed write (`-EIO`)** (hit while probing on the Linux side).
	/// </para>
	/// <para>
	/// **While write-back is on, the local dirty size is authoritative.** There is no transaction to fix the end
	/// in, and since **the peer mount's bytes are not in the database yet**, cross-mount atomicity cannot be
	/// built even in principle.
	/// The contract in docs/Mount.md (the append contract) is authoritative.
	/// </para>
	/// </summary>
	private int WriteDataAppend(Inode inode, ReadOnlySpan<byte> source) {
		if (source.Length == 0) { return 0; }
		// While flushes keep failing (the error state) no new writes are accepted (same as WriteData).
		this.ThrowIfWriteBackErrorState();
		if (this.config.Mount.WriteBack && !this.dataIntakeClosed) {
			return this.WriteDataBuffered(inode, this.AppendOffsetOf(inode), source);
		}
		return this.WriteDataThrough(inode, 0, source, appendAtEnd: true);
	}

	/// <summary>
	/// Reads an inode's <c>st_size</c> inside the transaction (the append offset). Null when there is no row.
	/// <para>**No lock is taken.** <see cref="LockData"/> has already serialized this, so a plain SELECT reads
	/// a value that includes everything committed just before. Adding a row lock would touch the inode shard
	/// somewhere other than the end of the transaction and break the normalized shard-touch order Citus needs.</para>
	/// </summary>
	private long? ReadSizeInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long inodeId) {
		var parentId = this.ResolveParentId(conn, tx, inodeId);
		if (parentId == null) { return null; }
		return conn.QueryFirstOrDefault<long?>(
			$"SELECT st_size FROM {this.QualifiedTable("inode")} WHERE parent_id = @parent_id AND id = @id",
			new { parent_id = parentId.Value, id = inodeId }, tx
		);
	}

	/// <summary>The original write-through write (one FUSE write = one transaction). 40P01 is absorbed by a bounded retry.</summary>
	private int WriteDataThrough(Inode inode, long offset, ReadOnlySpan<byte> source, bool appendAtEnd = false) {
		// Writing write-through to a pending inode would touch a row whose parent_id is not in the database
		// (reachable when write_back is disabled live, or as the fallback when a data_id reservation fails).
		// Materialize it first.
		this.MaterializePending(inode.Id);
		// A ReadOnlySpan cannot be captured in a lambda, so the retry is written as an ordinary loop.
		// The victim's transaction has rolled back completely, so re-running is safe (EnsureDataRow fills in the
		// in-memory inode.DataId first, but on a re-run it converges on the same id through CreateDataRowWithId).
		for (var attempt = 1; ; attempt++) {
			try {
				return this.WriteDataThroughOnce(inode, offset, source, appendAtEnd);
			} catch (Npgsql.PostgresException ex) when (Api.IsRetryableDeadlock(ex) && attempt < Api.DeadlockMaxAttempts) {
				Logger.Warning("write: retrying after a distributed deadlock (", ex.SqlState, ") (attempt ", attempt, "/", Api.DeadlockMaxAttempts, ") inode:", inode.Id);
				Api.SleepBeforeDeadlockRetry(attempt);
			}
		}
	}

	private int WriteDataThroughOnce(Inode inode, long offset, ReadOnlySpan<byte> source, bool appendAtEnd) {
		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();

		var (dataId, chunkSize, linkPending) = EnsureDataRow(conn, tx, inode);
		// Take the data lock right after EnsureDataRow settles the data_id, so this serializes against another
		// client's concurrent WriteData / TruncateData / ReleaseData (released automatically when the transaction ends).
		this.LockData(conn, tx, dataId);

		// **An append decides the end inside the transaction.** Once `LockData` is held, **every writer of this data
		// is serialized**, so the `st_size` read here is the true end, including any append committed just before.
		// Using the local `inode.Size` would miss **an append the peer is in the middle of committing** (a large
		// write makes one transaction long) and overwrite that region (measured: behind a 4 MiB append, 40 bytes were
		// lost on Linux and 10 on Windows).
		// **No row lock is taken** - a plain SELECT adds no edge to the deadlock graph and so does not break the
		// Citus shard-touch convention (the inode is touched once, at the end of the transaction). That the read
		// really does see a fresh snapshot after waiting was confirmed on real hardware with Citus rf=2 (the peer's
		// commit was always ahead of the local value).
		if (appendAtEnd) { offset = this.ReadSizeInTx(conn, tx, inode.Id) ?? inode.Size; }

		var totalWritten = 0;
		var curOffset = offset;
		var srcPos = 0;
		var remaining = source.Length;
		// The total change in chunk payload length (= the delta of occupied bytes). Applied exactly once before the commit.
		var occupiedDelta = 0L;

		while (remaining > 0) {
			var chunkIndex = (int)(curOffset / chunkSize);
			var offsetInChunk = (int)(curOffset % chunkSize);
			var bytesInThisChunk = Math.Min(remaining, chunkSize - offsetInChunk);

			// The Span has to be copied into a byte[] before it can be bound as a parameter.
			var slice = source.Slice(srcPos, bytesInThisChunk).ToArray();
			occupiedDelta += WriteChunkSlice(conn, tx, dataId, chunkIndex, offsetInChunk, slice);
			totalWritten += bytesInThisChunk;

			srcPos += bytesInThisChunk;
			curOffset += bytesInThisChunk;
			remaining -= bytesInThisChunk;
		}

		var occupied = this.AddOccupiedBytes(conn, tx, dataId, occupiedDelta);

		// Extend st_size when needed (it is never shrunk here). The inode shard is touched by this one statement
		// alone = at the end of the transaction (the normalized shard-touch order; see the comment on EnsureDataRow).
		// **What is passed is "the end of this write", not "the max against the local size".**
		// The monotonic clamp is done by SQL's `GREATEST(st_size, @size)` **against the database value**. Mixing in
		// the local inode.Size would mean that **after another mount shrank the file**, a stale cache value (10, say)
		// turns into @size and st_size becomes 10 even though only 3 bytes were written (review H-1).
		// **The database's st_size is authoritative**, so the UPDATE makes it monotonic with GREATEST and
		// **takes back the value that was actually settled**, then distributes that to memory and to the siblings.
		// Without taking it back, the code would overwrite with "the shorter size it happens to know".
		var writeEnd = offset + totalWritten;
		var (writeSiblings, effectiveSize) = this.FinishWriteInodeInTx(conn, tx, inode, dataId, linkPending, writeEnd);

		tx.Commit();
		this.SetOccupiedBytes(inode, occupied);
		// The content cache is write-invalidate (drop every chunk of that data_id, and the next read reloads them).
		this.contentCache.InvalidateData(dataId);
		// Keep the in-memory Inode object in sync with the database too.
		// Note: the `inode` the caller passes is usually the instance held in FUSE's or Dokan's Context, while
		// `InodeCache.byId` and `childrenByParent` may hold a different instance that a later `ListChildren` reload
		// put there. Both are updated to keep them consistent.
		this.SyncInodeFields(inode, effectiveSize, Pg.UtcNow);
		// When st_size and st_mtime were distributed to the siblings (PropagateWriteToLinksInTx), drop the siblings'
		// caches as well. Fixing only the database and leaving the caches would make **a stat through another link
		// return a stale st_size, which truncates the read and makes the content look empty**
		// (docs/data-id-lifecycle.md). The truncate path does the same thing.
		this.InvalidateHardLinkSiblings(writeSiblings);
		// **The sibling ids go into the notification too.** Without them another mount's InodeCache is not dropped
		// and a stat through a different link returns a stale st_size (docs/data-id-lifecycle.md).
		this.Notify(inodeIds: [inode.Id, ..writeSiblings], dataIds: [dataId]);
		return totalWritten;
	}

	// ------------------------------------------------------------------
	// The write-back cache - docs/runtime-control-plane.md, the data write-back section.
	//
	// The gist: assemble a chunk in memory as "the complete image" so that a flush becomes **one statement per
	// chunk**. Today's write-through grows a 1 MiB chunk row 128 KiB at a time, and because bytea is subject to
	// TOAST, even a partial update rewrites the whole chain (read-modify-write amplification). Stopping that is
	// the point; cutting the number of transactions is secondary (measured contribution: 17%).
	// The source of truth is docs/performance.md.
	// ------------------------------------------------------------------

	/// <summary>
	/// The write-back write path. It never touches the database; it accumulates into
	/// <see cref="ContentCache"/>'s dirty buffer.
	/// When write-back cannot be established (no reserved id is available, and so on) it falls back to
	/// write-through, which is always a correct path - the result degrades but nothing breaks.
	/// </summary>
	private int WriteDataBuffered(Inode inode, long offset, ReadOnlySpan<byte> source) {
		var file = this.EnsureDirtyFile(inode);
		if (file == null) {
			return this.WriteDataThrough(inode, offset, source);
		}
		// **Overwriting a file that is already in the database (persisted) is flushed synchronously on close.**
		// Left as close-no-flush, it would be partially committed in a separate transaction every interval, so a crash
		// would leave a chimera whose first half is new and second half is old (atomicity for a single file only
		// improves for pending-born files = the ones this mount created). close-no-flush pays off on the create
		// side, so making this synchronous again costs nothing in bulk-copy throughput.
		if (!this.IsPendingBorn(inode.Id)) { this.MarkSyncOnClose(inode); }

		var occupiedDelta = 0L;
		var totalWritten = 0;
		var mtime = Pg.UtcNow;
		lock (file.Gate) {
			var curOffset = offset;
			var srcPos = 0;
			var remaining = source.Length;
			while (remaining > 0) {
				var chunkIndex = (int)(curOffset / file.ChunkSize);
				var offsetInChunk = (int)(curOffset % file.ChunkSize);
				var bytesInThisChunk = System.Math.Min(remaining, file.ChunkSize - offsetInChunk);
				occupiedDelta += this.WriteChunkDirty(file, chunkIndex, offsetInChunk, source.Slice(srcPos, bytesInThisChunk));
				srcPos += bytesInThisChunk;
				curOffset += bytesInThisChunk;
				remaining -= bytesInThisChunk;
				totalWritten += bytesInThisChunk;
			}
			// **The size as seen from memory** may be the max against the local view (that is what stat returns while
			// the file is dirty).
			// **What goes to the flush is WriteEnd instead** - passing a value with the local cache mixed in would make
			// the flush after another mount truncated the file compute `GREATEST(st_size, 32)` and
			// **roll the shrunken size back, letting bytes that should be gone be read again** (measured on both operating systems).
			file.Size = System.Math.Max(inode.Size, offset + totalWritten);
			file.WriteEnd = System.Math.Max(file.WriteEnd, offset + totalWritten);
			file.Mtime = mtime;
			file.InodeDirty = true;
			file.MarkDirtyNow();
		}

		this.SyncInodeFields(inode, file.Size, mtime);
		// Advance the occupied bytes in memory immediately as well, so du and st_blocks are already correct before the flush.
		this.AddOccupiedInMemory(inode, occupiedDelta);
		this.ApplyBackPressure();
		return totalWritten;
	}

	/// <summary>
	/// A write into one dirty chunk. The full chunk is read from the database as a base only when this is a
	/// partial write to an existing chunk that is not in the cache (measured at 0.4 ms - practically free
	/// against the 25-30 ms a write costs).
	/// That keeps the dirty buffer "the complete image of the chunk" at all times, so the read path sees the
	/// latest bytes without any change.
	/// </summary>
	/// <returns>The increase in the valid payload length (= the increase in occupied bytes).</returns>
	private long WriteChunkDirty(DirtyFile file, int chunkIndex, int offsetInChunk, ReadOnlySpan<byte> data) {
		var (seed, prevLength) = this.LoadChunkBase(file, chunkIndex, offsetInChunk, data.Length);
		var delta = this.contentCache.WriteDirty(file.DataId, chunkIndex, offsetInChunk, data, seed, prevLength, file.ChunkSize);
		file.Chunks.Add(chunkIndex);
		return delta;
	}

	/// <summary>
	/// Prepares the base for a dirty buffer from the database. It returns
	/// (the full chunk payload or null, the payload length in the database).
	/// <para>
	/// There are three cases: (1) there is no data row yet, or the chunk is already cached -> nothing is needed;
	/// (2) **the whole chunk is overwritten** -> the payload is not needed, but **the occupied-bytes delta still
	/// needs "the length in the database"** (treating it as 0 double-counts `total_size` and inflates `du`;
	/// `length(bytea)` only reads the raw size from the TOAST pointer, so nothing is detoasted);
	/// (3) a partial write -> read the full chunk and use it as the base (to keep the buffer "the complete image
	/// of the chunk").
	/// </para>
	/// </summary>
	private (byte[]? Seed, long PrevLength) LoadChunkBase(DirtyFile file, int chunkIndex, int offsetInChunk, int length) {
		if (file.DataRowPending) { return (null, 0); }
		if (!this.contentCache.NeedsSeed(file.DataId, chunkIndex)) { return (null, 0); }
		if (offsetInChunk == 0 && length >= file.ChunkSize) {
			return (null, this.LoadChunkLength(file.DataId, chunkIndex));
		}
		var seed = this.ReadFullChunkStandalone(file.DataId, chunkIndex);
		return (seed, seed?.Length ?? 0);
	}

	/// <summary>Reads only a chunk's payload length from the database (no detoast). 0 when there is no row.</summary>
	private long LoadChunkLength(long dataId, int chunkIndex) {
		return Pg.Query<long>(
			this.connectionString,
			$"SELECT length(payload) FROM {this.QualifiedTable("data_chunk")} WHERE data_id = @data_id AND chunk_index = @ix",
			new { data_id = dataId, ix = chunkIndex }
		).FirstOrDefault();
	}

	/// <summary>Reads a full chunk on a one-off connection (the seed for write-back).</summary>
	private byte[]? ReadFullChunkStandalone(long dataId, int chunkIndex) {
		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();
		var payload = this.ReadFullChunk(conn, tx, dataId, chunkIndex);
		tx.Commit();
		return payload;
	}

	/// <summary>
	/// Prepares the <see cref="DirtyFile"/> for the write. For a new file (no data_id assigned yet) an id is
	/// taken from the reservation pool and put on the inode (no database round trip). Null when it cannot be prepared.
	/// </summary>
	private DirtyFile? EnsureDirtyFile(Inode inode) {
		if (inode.DataId is long existingId) {
			var known = this.dirtySet.Find(existingId);
			if (known != null) { return known; }
			var chunkSize = this.LoadChunkSizeOrNull(existingId);
			if (chunkSize == null) {
				// The inode points at a data_id but there is no {prefix}data row = the id was reserved and the entry fell out
				// of the ledger before it was flushed. Creating the row in the flush transaction makes it consistent, so it
				// is treated as pending.
				return this.dirtySet.GetOrAdd(existingId, inode.Id, this.DefaultChunkSize(), true);
			}
			return this.dirtySet.GetOrAdd(existingId, inode.Id, chunkSize.Value, false);
		}
		return this.ReserveDirtyFile(inode);
	}

	/// <summary>Allocates a data_id from the reservation pool and builds the <see cref="DirtyFile"/> of a new file. Null on failure.</summary>
	private DirtyFile? ReserveDirtyFile(Inode inode) {
		try {
			var file = this.dirtySet.ReserveFor(inode.Id, this.dataIdReservation.Rent, this.DefaultChunkSize());
			inode.DataId = file.DataId;
			// There is no row in the database yet, so the occupied bytes start as "loaded, 0" (which avoids a pointless SELECT).
			this.SetOccupiedBytes(inode, 0);
			return file;
		} catch (System.Exception ex) {
			Logger.Warning("write-back: could not reserve a data_id, so this write goes write-through: ", ex.Message);
			return null;
		}
	}

	/// <summary>The chunk size to use for a new data row (computed exactly as write-through's <c>EnsureDataRow</c> does).</summary>
	private int DefaultChunkSize() {
		return (int)System.Math.Max(4096, this.config.FileSystem.DefaultChunkSize);
	}

	/// <summary>Reads a single chunk size. Null when there is no <c>{prefix}data</c> row.</summary>
	private int? LoadChunkSizeOrNull(long dataId) {
		return Pg.Query<int?>(
			this.connectionString,
			$"SELECT chunk_size FROM {this.QualifiedTable("data")} WHERE id = @id",
			new { id = dataId }
		).FirstOrDefault();
	}

	/// <summary>Adds a delta to the in-memory occupied bytes (so du is already correct before the flush).</summary>
	private void AddOccupiedInMemory(Inode inode, long delta) {
		if (delta == 0) { return; }
		this.SetOccupiedBytes(inode, System.Math.Max(0, inode.OccupiedBytes + delta));
	}

	/// <summary>
	/// When the dirty byte count exceeds <c>mount.write_back_max_bytes</c>, **the writing thread itself** flushes
	/// oldest-first and waits until it drops back below (back-pressure). Dirty data cannot be discarded, so this
	/// is the brake that stops memory from growing without bound.
	/// </summary>
	private void ApplyBackPressure() {
		var limit = this.config.Mount.WriteBackMaxBytes;
		if (limit <= 0) { return; }
		if (this.contentCache.DirtyBytes <= limit) { return; }
		var target = limit - limit / 8;
		foreach (var dataId in this.dirtySet.DirtyDataIdsOldestFirst()) {
			if (this.contentCache.DirtyBytes <= target) { break; }
			this.TryFlushData(dataId);
		}
	}

	/// <summary>Starts the time-triggered background flush loop (it always runs, because write_back can be changed live).</summary>
	private void StartFlushLoop() {
		this.flushTask = System.Threading.Tasks.Task.Run(() => this.RunFlushLoopAsync(this.heartbeatCts.Token));
	}

	/// <summary>
	/// Flushes files in the background once they have been dirty for longer than
	/// <c>mount.write_back_interval_ms</c>.
	/// It does nothing while write-back is off or the interval is 0 (the loop keeps turning so it can follow a
	/// live change of the setting).
	/// </summary>
	private async System.Threading.Tasks.Task RunFlushLoopAsync(System.Threading.CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			var interval = this.config.Mount.WriteBackIntervalMs;
			var wait = System.Math.Clamp(interval, 100, 5000);
			if ((!this.config.Mount.WriteBack && !this.dataDrainPending) || interval <= 0) { wait = 1000; }
			try {
				await System.Threading.Tasks.Task.Delay(wait, ct).ConfigureAwait(false);
			} catch (System.OperationCanceledException) {
				return;
			}
			// Orphan audits (the create/delete pair of a cancelled pending entry) are written **regardless of the state
			// of write_back**. Giving up here would mean that with write_back_interval_ms = 0 not a single audit row
			// appears until unmount.
			this.FlushOrphanAudits();
			// **Even when it is off, the drain keeps going.** If the flush of a live disable failed and dirty data was
			// left behind, stopping here would mean no flush trigger ever comes again (the review finding about data
			// write-back's live off).
			if (this.dataDrainPending) {
				this.FlushIdle(System.TimeSpan.Zero);
				this.ClearDrainIfEmpty();
				continue;
			}
			if (!this.config.Mount.WriteBack) { continue; }
			if (this.config.Mount.WriteBackIntervalMs <= 0) { continue; }
			this.FlushIdle(System.TimeSpan.FromMilliseconds(this.config.Mount.WriteBackIntervalMs));
		}
	}

	/// <summary>Flushes files that have been dirty for longer than the given time (for the background loop; a failure is latched and the loop continues).</summary>
	private void FlushIdle(System.TimeSpan age) {
		var cutoff = System.DateTime.UtcNow - age;
		foreach (var inodeId in this.dirtyNamespace.PendingIdsOlderThan(cutoff)) {
			this.TryFlushPending(inodeId);
		}
		foreach (var dataId in this.dirtySet.DirtyDataIdsOlderThan(cutoff)) {
			this.TryFlushData(dataId);
		}
		this.FlushOrphanAudits();
	}

	/// <summary>
	/// Writes out an inode's unflushed data (the sync points: <c>fsync</c>, <c>close</c>, before a
	/// <c>truncate</c>, and so on).
	/// On failure it throws, and the caller must return <c>-EIO</c> - with write-back there is no way to return
	/// an error at write time, so this is the only place it can be reported.
	/// </summary>
	public void FlushInode(Inode inode) {
		// Whatever decides if the mark may be cleared is captured **before the flush is fired** (capture-and-clear).
		var hadUnflushed = this.HasUnflushed(inode);
		// Metadata write-back: when it is pending, "pending ancestors -> itself -> the data row -> the chunks -> the
		// audit" are written in one transaction.
		// **It is materialized even when DataId == null**, so that an fsync of an empty file or a directory does not lie.
		this.FlushPendingTree(inode.Id);
		// The audit pair of a cancelled pending entry is settled at the sync point too, rather than waiting for the background loop.
		this.FlushOrphanAudits();
		if (inode.DataId is long dataId) { this.FlushData(dataId); }
		// The synchronous-close mark (heuristic b) is cleared **only when unflushed data really was written out**.
		// (1) Clearing it on a close that wrote nothing would let "a close that merely deleted the old content",
		//     as in `truncate -s 0 f; cmd >> f`, consume the mark, so the following append on a different fd falls
		//     back to deferred (= a crash leaves zero-length garbage: exactly the window stage 2 declared it closes).
		// (2) When dirty data that piled up during the flush is still there, that part was not written, so the mark stays.
		if (!hadUnflushed) { return; }
		if (this.HasUnflushed(inode)) { return; }
		this.ClearSyncOnClose(inode.Id, inode.DataId);
	}

	/// <summary>Whether this inode holds anything unflushed (a pending inode or dirty data).</summary>
	private bool HasUnflushed(Inode inode) {
		if (this.dirtyNamespace.Find(inode.Id) != null) { return true; }
		if (inode.DataId is not long dataId) { return false; }
		return this.HasPendingDirty(dataId);
	}

	/// <summary>
	/// The sync point of <c>close(2)</c> (FUSE's <c>Flush</c> / <c>Release</c>, Dokan's <c>Cleanup</c>).
	/// <para>
	/// <b>While metadata write-back is on, the rule is "mark only" and the database is not touched</b>
	/// (decision 2 = close-no-flush). Neither data nor metadata is written, so it becomes "one file = one
	/// background flush transaction", and <c>fsync</c> / <c>fsyncdir</c> become the only hard barriers.
	/// **This changes the durability contract**, so what is given up is spelled out in docs/Mount.md.
	/// </para>
	/// <para>
	/// The exceptions - escalated to the same synchronous flush plus report (<c>-EIO</c>) as data write-back - are the three in <see cref="RequiresSyncClose"/>:
	/// (1) the mount is in the error state, (2) a flush failure is latched, (3) a <c>truncate</c> discarded the
	/// old chunks.
	/// When <c>write_back_metadata</c> is off, the data write-back behaviour (a synchronous flush on close) is
	/// preserved exactly.
	/// </para>
	/// </summary>
	public void CloseInode(Inode inode) {
		if (!this.MetadataWriteBack) {
			this.FlushInode(inode);
			return;
		}
		if (this.RequiresSyncClose(inode)) {
			if (Logger.IsDebugEnabled) { Logger.Debug("close: escalating to a synchronous flush inode:", inode.Id, " (error latch / truncate / error state)"); }
			this.FlushInode(inode);
			return;
		}
		// Mark only. The time the data became dirty was recorded at write time, so the background flush (the
		// interval), fsync, back-pressure or unmount will pick this inode up.
		if (Logger.IsTraceEnabled) { Logger.Trace("close-no-flush: returning after marking only inode:", inode.Id); }
	}

	/// <summary>
	/// The barrier of <c>fsyncdir(2)</c>: it materializes the pending children directly under that directory and
	/// the directory's own pending ancestors.
	/// This is the only sync point that makes the usual "fsync the file, then fsync the parent directory" idiom work.
	/// </summary>
	public void FlushDirectory(Inode dir) {
		this.FlushOrphanAudits();
		if (this.dirtyNamespace.IsEmpty) { return; }
		lock (this.nsGate) {
			this.MaterializePendingLocked(dir.Id);
			foreach (var childId in this.dirtyNamespace.PendingChildIds(dir.Id)) {
				this.MaterializePendingLocked(childId);
			}
		}
	}

	/// <summary>Writes out one body's unflushed data in a single transaction. A no-op when there is nothing dirty.</summary>
	public void FlushData(long dataId) {
		var file = this.dirtySet.Find(dataId);
		if (file == null) { return; }
		// When the inode itself is pending, the inode has to be INSERTed before the data row (they must share one
		// transaction), so this is delegated to the namespace flush. That path writes the chunks too, which makes
		// the FlushLocked below a no-op.
		this.FlushPendingTree(file.InodeId);
		lock (file.Gate) {
			this.FlushLocked(file);
		}
	}

	/// <summary>A flush that swallows failures (for the background loop and back-pressure). The error is latched on the file.</summary>
	private void TryFlushData(long dataId) {
		try {
			this.FlushData(dataId);
		} catch (System.Exception ex) {
			Logger.Warning("write-back: the flush failed (it will be retried on the next fsync/close) data_id:", dataId, " ", ex.Message);
		}
	}

	/// <summary>Writes out whatever dirty data is held when <c>write_back</c> is disabled live, so nothing is dropped.</summary>
	/// <summary>
	/// A live change of <c>mount.write_back</c> is applied in **two phases** (the same shape as the two-phase
	/// flip of <c>write_back_metadata</c> on the metadata side).
	/// <list type="number">
	///   <item>1: stop accepting new dirty data (<see cref="dataIntakeClosed"/>) - every later write goes write-through</item>
	///   <item>2: write out whatever dirty data is held with <see cref="FlushAll(DateTime?)"/> (under a deadline)</item>
	///   <item>3: change the mode. **Whatever could not be written sets <see cref="dataDrainPending"/> and the
	///     drain continues** - the setting is never turned off leaving dirty data stranded</item>
	/// </list>
	/// <para>
	/// With a single phase (drop the mode first, then flush), a write running alongside the flip lands in the
	/// window where "the mode is off yet dirty data is being accumulated". Worse, when the flush fails,
	/// **the background loop stops at `WriteBack` and that dirty data is never flushed again**, and with the read
	/// cache at 0 a read from the same mount returns the old content from the database.
	/// </para>
	/// </summary>
	private void ApplyWriteBackLive(bool enabled) {
		if (enabled) {
			this.config.Mount.WriteBack = true;
			this.dataIntakeClosed = false;
			this.PublishStatsNow();
			return;
		}
		// 1: close intake. **The change of effective mode is written to {prefix}mounts right away** (the same idea as
		// B-5 / B-9) - stats only travel on the heartbeat (30 seconds), so without this, status would lie for the
		// whole duration of the flip.
		this.dataIntakeClosed = true;
		this.PublishStatsNow();
		try {
			// 2: write out the dirty data that is held. The deadline exists so that a single configuration change cannot
			// block indefinitely while the database is stalled (a failure is downgraded to a warning by FlushAll).
			this.FlushAll(DateTime.UtcNow.AddMilliseconds(Math.Max(0, this.config.Mount.WriteBackFlushTimeoutMs)));
		} finally {
			// 3: change the mode, then clear the intake-closed flag (doing it the other way round opens a window).
			this.config.Mount.WriteBack = false;
			this.dataIntakeClosed = false;
			this.NoteDrainAfterDisable();
			this.PublishStatsNow();
		}
	}

	/// <summary>
	/// Records the dirty data left after a live disable as a "drain still in progress" state.
	/// When nothing is left it clears the flag (an ordinary off ends here).
	/// </summary>
	private void NoteDrainAfterDisable() {
		// **It counts pending inodes and unwritten audit rows as well as dirty data** (UnflushedCount).
		// write_back_metadata presumes write_back, so turning data off stops the background loop and
		// **leaves pending inodes with no flush trigger either**. Looking only at dirty files would produce the state
		// "no flush trigger exists yet the drain flag is clear" (measured: it made a test fail intermittently).
		var remaining = this.UnflushedCount();
		this.dataDrainPending = remaining > 0;
		if (remaining == 0) { return; }
		Logger.Error("write-back: ", remaining, " item(s) remained after the flush of a live disable. The setting is off, but the background flush and the cache path are kept until they are written out (unmounting now loses them)");
	}

	/// <summary>Clears the flag once the dirty data drains while a drain is in progress (called from the background loop).</summary>
	private void ClearDrainIfEmpty() {
		if (!this.dataDrainPending) { return; }
		if (this.UnflushedCount() > 0) { return; }
		this.dataDrainPending = false;
		Logger.Information("write-back: the dirty data left by the live disable has been written out (drain finished)");
		// **Write the end of the drain to {prefix}mounts right away too** (same idea as stopping acceptance). Waiting for
		// the heartbeat (30 seconds) makes status keep falsely reporting "unflushed data remains" in the meantime
		// (found when a write-back test failed against a PG across the network).
		this.PublishStatsNow();
	}

	/// <summary>
	/// Writes out everything unflushed (one pass, used by sync, a live disable and unmount). Failures are warned
	/// about and the pass continues.
	/// At unmount <see cref="FlushAllForShutdown"/> **retries this under a deadline** and, if anything is left,
	/// enumerates "what is being lost" in the Error log (giving up after a single pass would, with metadata
	/// write-back on, turn into "the file itself disappears with one line in the log").
	/// </summary>
	public void FlushAll() {
		this.FlushAll(deadline: null);
	}

	/// <summary>
	/// The version of <see cref="FlushAll()"/> with **a deadline**. The deadline is checked **within** a pass as
	/// well: with "fire everything, then look at the deadline", a fault that makes each individual failure slow -
	/// the 5-second <c>lock_timeout</c> while ensuring an audit partition, say - would stretch one pass to hours
	/// and bring back the original problem of "a deadline was set yet this is indistinguishable from a hang".
	/// </summary>
	public void FlushAll(DateTime? deadline) {
		// Pending inodes go first (their data is written along with them).
		this.FlushAllPendingInodes(deadline);
		foreach (var dataId in this.dirtySet.DirtyDataIdsOldestFirst()) {
			if (this.DeadlineReached(deadline)) { return; }
			this.TryFlushData(dataId);
		}
		this.FlushOrphanAudits();
	}

	/// <summary>
	/// Whether the deadline (null = none) has been reached. **An abandon request (<see cref="AbandonFlush"/>) also counts as reached** (B-12).
	/// </summary>
	private bool DeadlineReached(DateTime? deadline) {
		if (this.flushAbandoned) { return true; }
		if (deadline is not DateTime at) { return false; }
		return DateTime.UtcNow >= at;
	}

	/// <summary>
	/// **Immediately abandons the persistence of an in-flight flush** (B-12). <c>mount.pgfs</c> calls this when a
	/// second SIGTERM or SIGINT arrives. Every later deadline check then reports "reached", so
	/// <see cref="FlushAllForShutdown"/> bails out at its next check point and can exit **after emitting the loss report**.
	/// <para>
	/// Leaving it set is deliberate - there is no reason to resume being persistent after being told "stop waiting".
	/// The individual flush transaction itself is not interrupted (interrupting it could leave the database inconsistent).
	/// </para>
	/// </summary>
	public void AbandonFlush() {
		this.flushAbandoned = true;
	}

	private volatile bool flushAbandoned;

	/// <summary>
	/// Whether phase 1 of the live disable of data write-back (the two-phase flip) is in progress = **intake of
	/// new dirty data is closed**.
	/// While it is set, <see cref="WriteData"/> goes write-through.
	/// </summary>
	private volatile bool dataIntakeClosed;

	/// <summary>
	/// Whether **dirty data that the live disable's flush could not write out is still held**.
	/// <para>
	/// While it is set the state is "the setting is off but the drain continues": (1) the background flush loop
	/// keeps running and (2) <see cref="UseChunkCache"/> stays true. Without it, **a live off during a database
	/// outage would leave the dirty data with no flush trigger (the background loop stops at `WriteBack`), and
	/// with the read cache at 0 a read from the same mount returns the old content from the database**.
	/// </para>
	/// </summary>
	private volatile bool dataDrainPending;

	/// <summary>Whether a body holds anything unflushed (used to decide whether the occupied-bytes prefetch may overwrite it).</summary>
	private bool HasPendingDirty(long dataId) {
		var file = this.dirtySet.Find(dataId);
		if (file == null) { return false; }
		return file.HasPending;
	}

	/// <summary>
	/// Discards the unflushed data (truncate to 0, unlink, release = the body itself is going away).
	/// There is no flush, because discarding without writing is both correct and faster.
	/// </summary>
	private void DiscardDirtyData(long dataId) {
		var file = this.dirtySet.Find(dataId);
		if (file == null) {
			this.contentCache.DiscardDirty(dataId);
			return;
		}
		// Discarded dirty data will never be flushed, so the failure counter must not be left behind.
		this.ClearFlushFailure(file.InodeId, dataId);
		lock (file.Gate) {
			file.ClearDirty();
			file.Error = null;
			this.contentCache.DiscardDirty(dataId);
			this.dirtySet.Forget(dataId);
		}
	}

	/// <summary>
	/// Settles an inode's unflushed data before an operation that rewrites its metadata directly (truncate,
	/// utimens and so on). This stops a dirty st_size or st_mtime from landing later and overwriting the explicit value.
	/// </summary>
	private void FlushBeforeMetadataWrite(long inodeId) {
		if (!this.config.Mount.WriteBack && !this.dataDrainPending) { return; }
		var inode = this.inodeCache.Get(inodeId);
		if (inode == null) { return; }
		this.FlushInode(inode);
	}

	/// <summary>
	/// The preparation for a truncate (write-back). At 0 the unflushed data is discarded (writing it would be
	/// pointless because it is about to be deleted); otherwise it is flushed first so the database is current,
	/// and then handed to the existing truncate path.
	/// </summary>
	private void PrepareTruncateWriteBack(Inode inode, long newLength) {
		if (!this.config.Mount.WriteBack && !this.dataDrainPending) { return; }
		if (newLength == 0) {
			if (inode.DataId is long dataId) { this.DiscardDirtyData(dataId); }
			return;
		}
		this.FlushInode(inode);
	}

	/// <summary>
	/// The body that writes the dirty data in a single transaction. Call it while holding <see cref="DirtyFile.Gate"/>.
	/// </summary>
	private void FlushLocked(DirtyFile file) {
		if (!file.HasPending) { return; }
		var chunks = this.contentCache.SnapshotDirty(file.DataId, file.Chunks);
		var indexes = new System.Collections.Generic.List<int>(file.Chunks);
		long? flushed = null;
		// The hardlink sibling ids the flush transaction distributed to (used for cache invalidation and the notification after the commit).
		var flushSiblings = new List<long>();
		try {
			for (var attempt = 1; ; attempt++) {
				try {
					// On a retry (attempt > 1) the occupied bytes are recomputed rather than applied as a delta: if the first
					// attempt advanced in-memory state (through a rekey, say) and then rolled back, the delta's baseline
					// (PrevLength) can no longer be trusted.
					flushed = this.FlushTransaction(file, chunks, forceRebuildOccupancy: attempt > 1, out flushSiblings);
					break;
				} catch (Npgsql.PostgresException ex) when (Api.IsRetryableDeadlock(ex) && attempt < Api.DeadlockMaxAttempts) {
					Logger.Warning("flush: retrying after a distributed deadlock (", ex.SqlState, ") (attempt ", attempt, "/", Api.DeadlockMaxAttempts, ") data_id:", file.DataId);
					Api.SleepBeforeDeadlockRetry(attempt);
				} catch (Api.FlushRetargetException) when (attempt < Api.DeadlockMaxAttempts) {
					// The inode this was pinned to is gone and the dirty data was re-pointed to a hardlink sibling. Honouring
					// the lock order (inode, then data) requires taking the locks again, so the same dirty data is simply
					// re-submitted.
					Logger.Warning("flush: re-pointing the target inode to a hardlink sibling and retrying (attempt ", attempt, "/", Api.DeadlockMaxAttempts, ") data_id:", file.DataId, " inode:", file.InodeId);
				}
			}
		} catch (System.Exception ex) {
			// The dirty data is kept = the next flush can retry it. The error is latched for diagnostics.
			file.Error = ex.Message;
			this.dirtySet.CountFailure();
			this.NoteFlushFailure(file.DataId, ex.Message);
			throw;
		}
		// ---- Everything past this point is already committed (B-11) ----
		// An exception during the cleanup does not mean "the flush failed", so **success is recorded first**.
		// In the opposite order, a failure in Notify or similar would increment the consecutive-failure counter even
		// though the commit went through, and five of those reach the error state = new writes return -EIO, which
		// would be a fabricated fault.
		if (flushed == null) {
			// The inode was gone = there is nowhere to write. Remove it from the ledger to stop any further retry
			// (leaving DataRowPending set keeps HasPending true and makes the flush repeat forever).
			file.ClearDirty();
			file.DataRowPending = false;
			file.Error = null;
			this.dirtySet.Forget(file.DataId);
			this.ClearFlushFailure(file.InodeId, file.DataId);
			return;
		}
		this.NoteFlushSuccess(file.DataId);
		this.contentCache.MarkFlushed(file.DataId, indexes);
		var inode = this.inodeCache.Get(file.InodeId);
		if (inode != null) { this.SetOccupiedBytes(inode, flushed.Value); }
		// As in write-through, drop the caches of the siblings that were distributed to, and **include them in the
		// notification** (without that another mount's InodeCache is not dropped and a stat through a different link
		// returns a stale st_size).
		this.InvalidateHardLinkSiblings(flushSiblings);
		this.Notify(inodeIds: [file.InodeId, ..flushSiblings], dataIds: [file.DataId]);
		file.ClearDirty();
		file.Error = null;
		this.dirtySet.CountFlush();
		this.dirtySet.PruneClean();
	}

	/// <summary>
	/// One flush transaction. The locks are taken from <c>{prefix}lock</c> **all at once, in ascending order**
	/// (the inode value is negative, so it always comes before the data). The return value is the occupied bytes
	/// after the update.
	/// **Null when the inode was deleted concurrently** (the caller then discards the dirty data and removes it
	/// from the ledger).
	/// </summary>
	private long? FlushTransaction(DirtyFile file, System.Collections.Generic.List<DirtyChunk> chunks, bool forceRebuildOccupancy, out List<long> siblingIds) {
		siblingIds = new List<long>();
		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();
		this.LockTargets(conn, tx, -file.InodeId, file.DataId);

		var parentId = this.ResolveParentId(conn, tx, file.InodeId);
		if (parentId == null) {
			// The inode this was pinned to was deleted concurrently. **As long as a hardlink sibling is alive, the dirty
			// data still has somewhere to go** (with `echo A > f; ln f g; echo B > g; rm f`, discarding B would lose
			// what was written, silently, without any crash). Look for a sibling and, when one exists, re-point to it and redo the whole thing (to keep the lock order).
			var sibling = this.ResolveFlushSibling(conn, tx, file.DataId);
			if (sibling != null) {
				file.InodeId = sibling.Value;
				throw new Api.FlushRetargetException();
			}
			// Not a single inode references it any more = there is nowhere to write, so it is discarded (the data row is already handled by DeleteInode).
			Logger.Warning("write-back: the inode is gone, so the dirty data is discarded inode:", file.InodeId);
			tx.Commit();
			this.contentCache.DiscardDirty(file.DataId);
			return null;
		}

		var (rebuildOccupancy, linkPending) = this.EnsureFlushDataRow(conn, tx, file, parentId.Value);
		var delta = 0L;
		foreach (var chunk in chunks) {
			delta += this.WriteFullChunkFlush(conn, tx, file.DataId, chunk);
		}
		var occupied = this.ApplyFlushOccupancy(conn, tx, file.DataId, delta, rebuildOccupancy || forceRebuildOccupancy);
		long? linkDataId = null;
		if (linkPending) { linkDataId = file.DataId; }
		// When hardlinks exist, size and mtime are distributed to every link (so a stat through any link returns the same value).
		long? sharedDataId = this.HasHardLinkSiblings(file.InodeId) switch {
			true  => file.DataId,
			false => null,
		};
		siblingIds = this.UpdateInodeSizeAndMtimeInTx(conn, tx, parentId.Value, file.InodeId, file.WriteEnd, file.Mtime, linkDataId, sharedDataId);
		tx.Commit();
		// An INSERTed data row is only settled once the commit lands, so the flag is cleared here
		// (clearing it inside the transaction would, on a rollback, leave "there is no row yet it is not pending" =
		// the next flush skips the INSERT and creates orphaned chunks).
		if (linkPending) { file.DataRowPending = false; }
		if (Logger.IsDebugEnabled) { Logger.Debug("write-back: flushed data_id:", file.DataId, " chunks:", chunks.Count, " size:", file.Size, " occupied:", occupied); }
		return occupied;
	}

	/// <summary>
	/// Signals that the parent directory of a create **was deleted by another client immediately before the check**.
	/// <para>
	/// The caller (the OS layer) must map this to **the equivalent of ENOENT**. It means something different
	/// from a <c>null</c> return (= a name collision = EEXIST), which is why the two are separated by an
	/// exception. Swallowing it and INSERTing anyway would leave **an orphaned inode that the filesystem cannot
	/// reach**, removable only with SQL.
	/// </para>
	/// </summary>
	public sealed class ParentVanishedException : System.Exception
	{
		public ParentVanishedException(long parentId)
			: base($"the parent directory {parentId} was deleted immediately before the create") {
			this.ParentId = parentId;
		}

		public long ParentId { get; }
	}

	/// <summary>
	/// Signals that the inode a flush was pinned to is gone and the dirty data was re-pointed to a hardlink sibling.
	/// Honouring the lock order (inode, then data) leaves no option but to start a new transaction, so this is
	/// the internal signal that returns control to <see cref="FlushLocked"/>'s retry loop.
	/// </summary>
	private sealed class FlushRetargetException : System.Exception
	{
	}

	/// <summary>
	/// Confirms that the <c>chunk_size</c> of the target <c>{prefix}data</c> row matches the local chunking (B-10).
	/// On a mismatch it throws <see cref="FlushChunkSizeMismatchException"/> and fails the flush (the dirty data
	/// stays local, so there is still room to write it out after a remount or after the configuration is corrected).
	/// </summary>
	private void AssertFlushChunkSize(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, DirtyFile file, long dataId) {
		var actual = this.LoadChunkSizeOrNull(conn, tx, dataId);
		if (actual == null) { return; }
		Api.AssertFlushChunkSize(file, dataId, actual.Value);
	}

	/// <summary>Compares an already-read <c>chunk_size</c> against the local chunking (B-10).</summary>
	private static void AssertFlushChunkSize(DirtyFile file, long dataId, int actual) {
		if (actual == file.ChunkSize) { return; }
		throw new Api.FlushChunkSizeMismatchException(
			$"write-back: the chunk_size ({actual}) of the target data_id {dataId} differs from the local chunking ({file.ChunkSize}). " +
			"Writing with misaligned chunk boundaries would corrupt the data, so the flush was aborted " +
			"(make file_system.default_chunk_size consistent across the clients)"
		);
	}

	/// <summary>
	/// Signals that the target <c>chunk_size</c> differs from the local chunking (= writing as-is would corrupt
	/// data) (B-10).
	/// It is a configuration mismatch, so retrying will not fix it - but **the dirty data is not discarded**: it
	/// is escalated as a consecutive failure into the error state, so that operations notice.
	/// </summary>
	private sealed class FlushChunkSizeMismatchException : System.Exception
	{
		public FlushChunkSizeMismatchException(string message) : base(message) {
		}
	}

	/// <summary>
	/// Allocates and attaches a <c>data_id</c> to a source that does not have one, immediately before creating a
	/// hardlink (compatibility with older filesystems).
	/// When no id can be allocated it does nothing (the link is created without a data_id as before = nothing is shared).
	/// </summary>
	private void AttachDataIdForLinkInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, Inode source) {
		if (source.DataId != null) { return; }
		if (this.RentDataId() is not long dataId) { return; }
		var rows = conn.Execute(
			$@"UPDATE {this.QualifiedTable("inode")}
			   SET data_id = @data_id, updated_at = (current_timestamp AT TIME ZONE 'UTC')
			   WHERE parent_id = @parent_id AND id = @id",
			new { data_id = dataId, parent_id = source.ParentId, id = source.Id },
			tx
		);
		if (rows == 0) { return; }
		source.DataId = dataId;
		if (Logger.IsTraceEnabled) { Logger.Trace("UPDATE inode SET data_id:", dataId, " (attached afterwards for a hardlink) WHERE id:", source.Id); }
	}

	/// <summary>
	/// Returns one other inode that references the same body (a hardlink sibling), or null when there is none.
	/// **Only called when the pinned inode is gone** - on Citus a data_id search is multi-shard, so the normal
	/// paths never go through it.
	/// </summary>
	private long? ResolveFlushSibling(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId) {
		return conn.QueryFirstOrDefault<long?>(
			$"SELECT id FROM {this.QualifiedTable("inode")} WHERE data_id = @data_id ORDER BY id LIMIT 1",
			new { data_id = dataId }, tx
		);
	}

	/// <summary>
	/// Whether this inode has hardlinks (= size and mtime have to be distributed to the siblings).
	/// The decision uses nothing but the cached <c>st_nlink</c> - the inode being flushed is the one that was
	/// just written to, so it is almost always in the cache. When it is absent, or stale and appears to be 1,
	/// the code falls back to the single-row update as before (= the behaviour prior to this fix; an accepted
	/// trade-off that avoids adding a database round trip to every flush).
	/// </summary>
	private bool HasHardLinkSiblings(long inodeId) {
		var cached = this.inodeCache.Get(inodeId);
		if (cached == null) { return false; }
		return cached.NLink > 1;
	}

	/// <summary>
	/// Creates the <c>{prefix}data</c> row and links it to the inode when the data was accumulated under a
	/// reserved id (inside the flush transaction).
	/// When another client settled a different data_id first, this re-points to that one (the same semantics as
	/// write-through's <c>EnsureDataRow</c>: "lost the race, so use the existing one").
	/// </summary>
	/// <returns>
	/// rebuild: whether the occupied bytes have to be recomputed instead of applied as a delta (true only when a
	/// re-point happened) / linkPending: whether the data row was newly INSERTed, so the inode's data_id link is
	/// still needed at the end of the transaction.
	/// </returns>
	private (bool rebuild, bool linkPending) EnsureFlushDataRow(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, DirtyFile file, long parentId) {
		if (!file.DataRowPending) { return (false, false); }
		var fresh = conn.QueryFirstOrDefault<long?>(
			$"SELECT data_id FROM {this.QualifiedTable("inode")} WHERE parent_id = @parent_id AND id = @id",
			new { parent_id = parentId, id = file.InodeId },
			tx
		);
		if (fresh is long winner && winner != file.DataId) {
			// **When the chunk_size at the re-point destination differs from the local one, nothing may be written** (B-10).
			// The local dirty data is already chunked by file.ChunkSize, so UPSERTing it by chunk_index into a winning
			// row with different boundaries would mean "overwriting a different region with a different length" = silent
			// data corruption. DirtyFile.ChunkSize is init-only, so it cannot be adjusted midway either. The flush is
			// failed and the dirty data is kept locally (five in a row drop into the error state, which is still better
			// than corrupting silently).
			this.AssertFlushChunkSize(conn, tx, file, winner);
			Logger.Warning("write-back: lost the race for the data_id, so re-pointing ", file.DataId, " -> ", winner);
			this.contentCache.RekeyData(file.DataId, winner);
			this.dirtySet.Rekey(file.DataId, winner);
			var inode = this.inodeCache.Get(file.InodeId);
			if (inode != null) { inode.DataId = winner; }
			file.DataRowPending = false;
			return (true, false);
		}
		// Even when the inode points at the same data_id as this flush, **the decision is made on whether the
		// {prefix}data row itself exists**. Looking only at inode.data_id would mistake the state "the row is gone
		// yet the inode still points at it" - which a pending truncate to 0 can produce - for "it is already
		// created", skip the INSERT, and leave orphaned chunks plus an inconsistent total_size that does not heal
		// itself even across a remount.
		if (fresh is long already && this.LoadChunkSizeOrNull(conn, tx, already) is int existingChunkSize) {
			// Even for an id this mount reserved, the row itself may have been created by another path (write-through,
			// or another client). Different boundaries would cause the same corruption as above, so it is refused the
			// same way (B-10).
			Api.AssertFlushChunkSize(file, already, existingChunkSize);
			if (Logger.IsTraceEnabled) { Logger.Trace("write-back: the data row already exists data_id:", already); }
			file.DataRowPending = false;
			return (false, false);
		}
		conn.Execute(
			$@"INSERT INTO {this.QualifiedTable("data")} (id, chunk_size, total_size, created_by, updated_by)
			   VALUES (@id, @chunk_size, 0, @user, @user)",
			new { id = file.DataId, chunk_size = file.ChunkSize, user = Environment.UserName ?? "pgfs" },
			tx
		);
		// Linking the data_id onto the inode is folded into UpdateInodeSizeAndMtimeInTx (at the end of the
		// transaction) to normalize the shard-touch order.
		// DataRowPending is not cleared here either - FlushTransaction clears it after the commit lands (so a
		// rollback is handled correctly).
		if (Logger.IsTraceEnabled) { Logger.Trace("write-back: INSERT data id:", file.DataId, " chunk_size:", file.ChunkSize); }
		return (false, true);
	}

	/// <summary>
	/// Writes a dirty chunk in **a single statement**. The local buffer is the complete image of the chunk, so
	/// the payload can be replaced wholesale (this is the heart of removing the amplification). When the
	/// database payload is longer than the local one - because another client extended it - it falls back to an
	/// overlay so the tail is preserved.
	/// </summary>
	/// <returns>The change in payload length (the delta of occupied bytes).</returns>
	private long WriteFullChunkFlush(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId, DirtyChunk chunk) {
		var payload = chunk.Payload;
		if (payload.Length != chunk.Length) {
			payload = payload.AsSpan(0, chunk.Length).ToArray();
		}
		var table = this.QualifiedTable("data_chunk");
		var now = Pg.UtcNow;
		// Citus note: a volatile function in DO UPDATE SET is rejected with "must be marked IMMUTABLE", so
		// updated_at is passed through EXCLUDED (the same reason as in WriteChunkSlice).
		var afterLen = conn.QuerySingle<long>(
			$@"INSERT INTO {table} (data_id, chunk_index, payload, created_at, created_by, updated_at, updated_by)
			   VALUES (@data_id, @ix, @payload, @now, @user, @now, @user)
			   ON CONFLICT (data_id, chunk_index) DO UPDATE SET
			       payload = CASE
			           WHEN length({table}.payload) > @len THEN overlay({table}.payload PLACING @payload FROM 1 FOR @len)
			           ELSE EXCLUDED.payload
			       END,
			       updated_at = EXCLUDED.updated_at,
			       updated_by = EXCLUDED.updated_by
			   RETURNING length(payload)",
			new { data_id = dataId, ix = chunk.ChunkIndex, payload, len = chunk.Length, user = Environment.UserName ?? "pgfs", now },
			tx
		);
		if (Logger.IsTraceEnabled) { Logger.Trace("write-back: UPSERT data_chunk data_id:", dataId, " chunk_index:", chunk.ChunkIndex, " payload:", chunk.PrevLength, "->", afterLen); }
		return afterLen - chunk.PrevLength;
	}

	/// <summary>
	/// Settles the occupied bytes after a flush. Normally it adds the delta (<see cref="AddOccupiedBytes"/>).
	/// Only when the data_id was re-pointed is the baseline of the delta lost, so it is **recounted** from every chunk.
	/// </summary>
	private long ApplyFlushOccupancy(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId, long delta, bool rebuild) {
		if (!rebuild) {
			return this.AddOccupiedBytes(conn, tx, dataId, delta);
		}
		// It is split into two statements for Citus (an UPDATE ... FROM subquery is avoided even when the tables are colocated).
		var total = conn.Query<long>(
			$"SELECT COALESCE(SUM(length(payload)), 0) FROM {this.QualifiedTable("data_chunk")} WHERE data_id = @id",
			new { id = dataId },
			tx
		).FirstOrDefault();
		var updated = conn.Query<long>(
			$@"UPDATE {this.QualifiedTable("data")}
			   SET total_size = @total, updated_at = (current_timestamp AT TIME ZONE 'UTC'), updated_by = @user
			   WHERE id = @id
			   RETURNING total_size",
			new { id = dataId, total, user = Environment.UserName ?? "pgfs" },
			tx
		).ToList();
		if (updated.Count == 0) { return total; }
		return updated[0];
	}

	/// <summary>
	/// A flush writes the inode's st_size and st_mtime. **The mtime is the time of the write**, not the time of
	/// the flush - so that the in-memory value and the database agree, and the mtime other clients see is the
	/// real time of the update.
	/// </summary>
	/// <summary>
	/// Writes the inode's size and mtime at the end of the flush transaction. Passing
	/// <paramref name="linkDataId"/> folds the data_id link into the same statement (the normalized shard-touch
	/// order = the inode shard is touched only at the end of the transaction; the reason is in the comment on
	/// <see cref="EnsureDataRow"/>).
	/// </summary>
	/// <returns>The ids of the hardlink siblings that were distributed to (excluding this inode). The caller
	/// uses them for the post-commit <see cref="InodeCache"/> invalidation and for <see cref="Notify"/>.</returns>
	private List<long> UpdateInodeSizeAndMtimeInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long parentId, long inodeId, long size, DateTime mtime, long? linkDataId, long? sharedDataId) {
		var set = "st_size = GREATEST(st_size, @size), st_mtime = @mtime, st_ctime = @mtime, updated_at = (current_timestamp AT TIME ZONE 'UTC')";
		if (linkDataId != null) { set = "data_id = @data_id, " + set; }
		// In POSIX hardlinks share one inode, so size and mtime must be identical across every link. In pgfs one
		// link is one inode row, so the rows that share the body are updated together (this closes "a stat through a
		// link other than the written fd shows a stale size").
		// The targets are looked up by data_id only when st_nlink > 1, because on Citus that is a multi-shard UPDATE
		// (the same shape is used for the st_nlink increment in CreateHardLink). The transaction that establishes the
		// data_id link itself **does not have the data_id in place yet**, so in that case the update stays single-row
		// as before.
		var where = "parent_id = @parent_id AND id = @id";
		if (sharedDataId != null && linkDataId == null) { where = "data_id = @shared_data_id"; }
		var updatedIds = conn.Query<long>(
			$@"UPDATE {this.QualifiedTable("inode")}
			   SET {set}
			   WHERE {where}
			   RETURNING id",
			new { parent_id = parentId, id = inodeId, size, mtime, data_id = linkDataId, shared_data_id = sharedDataId },
			tx
		).ToList();
		var rows = updatedIds.Count;
		if (Logger.IsTraceEnabled) { Logger.Trace("write-back: UPDATE inode st_size:", size, " st_mtime:", mtime, " link:", linkDataId, " id:", inodeId, " rows:", rows); }
		if (linkDataId != null && rows == 0) {
			// The inode was deleted concurrently. This transaction has already INSERTed the data row, so it is rolled back to prevent an orphan.
			throw new InvalidOperationException($"UpdateInodeSizeAndMtimeInTx: inode {inodeId} vanished before link");
		}
		// **In the transaction that folds in the link clause (= the first write to that body) the UPDATE above is
		// single-row**, so the where-clause switch above skips the distribution to siblings. Linking and distributing
		// are not mutually exclusive, so one extra statement is added (only when st_nlink > 1, i.e. only when
		// sharedDataId is non-null, so this adds no multi-shard work).
		if (linkDataId == null || sharedDataId == null) {
			return updatedIds.Where(id => id != inodeId).ToList();
		}
		var spreadIds = conn.Query<long>(
			$@"UPDATE {this.QualifiedTable("inode")}
			   SET st_size = GREATEST(st_size, @size), st_mtime = @mtime, st_ctime = @mtime, updated_at = (current_timestamp AT TIME ZONE 'UTC')
			   WHERE data_id = @shared_data_id AND id <> @id
			   RETURNING id",
			new { shared_data_id = sharedDataId, id = inodeId, size, mtime },
			tx
		).ToList();
		if (Logger.IsTraceEnabled) { Logger.Trace("write-back: UPDATE inode (sibling distribution in the link transaction) data_id:", sharedDataId, " rows:", spreadIds.Count); }
		return spreadIds;
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
		// While in the error state, **a truncate of a persisted body is refused** (B-7).
		// Shrinking destroys existing chunks and growing creates new data, so neither is accepted.
		this.ThrowIfErrorStateBlocksDestroy(inode, "truncate");
		// Metadata write-back: a truncate to 0 of a pending inode completes in memory without touching the database
		// (a pending inode holds no chunks in the database - flushing data always materializes the inode).
		if (this.TruncatePendingToZero(inode, newLength)) { return true; }
		// A truncate to anything but 0 needs the dirty buffer trimmed, so a pending inode is materialized first and handed to the existing path.
		this.MaterializePending(inode.Id);
		// Write-back: bring the baseline in line with the database before shrinking or growing. At 0, discarding before writing is both correct and faster.
		this.PrepareTruncateWriteBack(inode, newLength);
		// Sync heuristic (b): what follows **deletes the old chunks in the database immediately**, so this inode's
		// close is escalated to a synchronous flush. Deferring it would let a crash leave "neither the old nor the
		// new = zero-length garbage" (an open with O_TRUNC reaches this path from the FUSE layer and the kernel too).
		// **The mark must be set after PrepareTruncateWriteBack**, because that calls FlushInode, which clears it.
		this.MarkSyncOnClose(inode);

		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();

		if (newLength == 0) {
			// **The data row is not deleted.** The data_id is the identity of the file body and stays unchanged from create
			// to the final unlink (docs/data-id-lifecycle.md). Back when the row was deleted outright, a sibling
			// **kept pointing at the deleted row while holding a stale st_size** and reads returned NUL bytes (and the
			// recomputation of st_nlink counts "rows with the same data_id", so it broke the moment the sharing was cut).
			// Emptying a file is confined to "delete the chunks and set total_size to 0".
			var emptiedDataId = inode.DataId;
			if (inode.DataId is long zeroedId) {
				// Take the data lock to serialize against a concurrent WriteData / ReleaseData.
				this.LockData(conn, tx, zeroedId);
				DropAllChunks(conn, tx, zeroedId);
				this.ResetDataTotalSizeInTx(conn, tx, zeroedId);
			}
			var zeroSiblings = this.UpdateTruncatedSizeInTx(conn, tx, inode, 0);
			tx.Commit();
			// The chunks are gone, so the occupied bytes are 0 (the row remains).
			this.SetOccupiedBytes(inode, 0);
			this.SyncInodeFields(inode, 0, Pg.UtcNow);
			// The siblings' st_size became 0 too, so drop them from the cache and include them in the notification (this inode was already handled by SyncInodeFields).
			this.InvalidateHardLinkSiblings(zeroSiblings);
			if (emptiedDataId == null) {
				this.Notify(inodeIds: [inode.Id, ..zeroSiblings]);
				return true;
			}
			this.contentCache.InvalidateData(emptiedDataId.Value);
			this.Notify(inodeIds: [inode.Id, ..zeroSiblings], dataIds: [emptiedDataId.Value]);
			return true;
		}

		var (dataId, chunkSize, linkPending) = EnsureDataRow(conn, tx, inode);
		// Take the data lock after EnsureDataRow to serialize against a concurrent WriteData / ReleaseData.
		this.LockData(conn, tx, dataId);
		var lastChunkIndex = (int)((newLength - 1) / chunkSize);
		var lastChunkLength = (int)(newLength - (long)lastChunkIndex * chunkSize);

		// Delete every chunk beyond the new end (the payload length that disappears is subtracted from the occupied bytes).
		var deletedLengths = conn.Query<long>(
			$@"DELETE FROM {this.QualifiedTable("data_chunk")}
			   WHERE data_id = @data_id AND chunk_index > @last
			   RETURNING length(payload)",
			new { data_id = dataId, last = lastChunkIndex },
			tx
		).ToList();
		if (Logger.IsTraceEnabled) { Logger.Trace("DELETE data_chunk data_id:", dataId, " chunk_index>", lastChunkIndex, " rows:", deletedLengths.Count); }
		var occupiedDelta = -deletedLengths.Sum();

		// Trim the last chunk to lastChunkLength bytes (padding with zeros when it is shorter).
		occupiedDelta += TruncateChunk(conn, tx, dataId, lastChunkIndex, lastChunkLength);
		var occupied = this.AddOccupiedBytes(conn, tx, dataId, occupiedDelta);

		// When EnsureDataRow created the data row, link it here (the end of the transaction = the point where the inode shard is touched).
		if (linkPending) { this.LinkDataIdInTx(conn, tx, inode, dataId); }
		// A truncate to anything but 0 also shares the body, so with hardlinks st_size is distributed to every link.
		var truncSiblings = this.UpdateTruncatedSizeInTx(conn, tx, inode, newLength);
		tx.Commit();
		this.SetOccupiedBytes(inode, occupied);
		this.contentCache.InvalidateData(dataId);
		this.SyncInodeFields(inode, newLength, Pg.UtcNow);
		this.InvalidateHardLinkSiblings(truncSiblings);
		this.Notify(inodeIds: [inode.Id, ..truncSiblings], dataIds: [dataId]);
		return true;
	}

	/// <summary>
	/// Completely discards the inode's data (called on the final link at Unlink).
	/// </summary>
	public void ReleaseData(long dataId) {
		// The body itself is going away, so the unflushed data is discarded rather than written.
		this.DiscardDirtyData(dataId);
		using var conn = NewConnection();
		using var tx = conn.BeginTransaction();
		// data lock to serialize with concurrent WriteData/TruncateData (released automatically at tx end)
		this.LockData(conn, tx, dataId);
		DropAllChunks(conn, tx, dataId);
		DropDataRow(conn, tx, dataId);
		tx.Commit();
		// Discard it from the content cache too (the data_id is never reused, so no cross-client notification is needed).
		this.contentCache.InvalidateData(dataId);
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
		var when = Pg.UtcNow;
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
		// **A `lock_timeout` is always set**: `CREATE TABLE ... PARTITION OF` asks the parent table for a lock that
		// conflicts with the RowExclusiveLock an audit-row INSERT holds (measured on the dev server: it blocks for as
		// long as another session's INSERT transaction is open). This DDL runs on a separate connection (autocommit)
		// from the operation's transaction, so calling it while a flush transaction in the same process holds a
		// RowExclusiveLock on the parent **splits the wait graph, PostgreSQL's deadlock detector cannot see the
		// cycle, and with the default `lock_timeout = 0` it waits forever**.
		// The convention is that callers ensure the partition outside a transaction
		// (docs/runtime-control-plane.md), but even when that is broken the mount fails with an exception rather
		// than hanging. The RESET is the cleanup before the connection goes back to the pool.
		var sql = "SET lock_timeout = '5s'; " +
			$"CREATE TABLE IF NOT EXISTS {partQualified} PARTITION OF {this.QualifiedTable("audit")} " +
			$"FOR VALUES FROM ('{fromLit}') TO ('{toLit}'); RESET lock_timeout;";
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

	private (long dataId, int chunkSize, bool linkPending) EnsureDataRow(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, Inode inode) {
		if (inode.DataId is long cached) {
			var size = this.LoadChunkSizeOrNull(conn, tx, cached);
			if (size != null) { return (cached, size.Value, false); }
			// A data_id that write-back reserved, but whose row does not exist yet (the write-through path was reached
			// before the flush). Creating the row here makes everything behave normally from then on. Linking it onto
			// the inode is deferred to the end of the transaction (see the comment below).
			return (cached, this.CreateDataRowWithId(conn, tx, inode, cached), true);
		}
		// The in-memory inode.DataId is null = this is the create-a-new-data-row path.
		// When Dokan or FUSE issues concurrent WriteFile calls (CopyFileEx's 2 MiB writes, for example), several
		// transactions arrive here at once, both INSERT a data row, and the last-writer-wins UPDATE of the inode
		// leaves the loser's data row dangling. The chunks written into it are unreachable (a silent leak plus data
		// loss). It does not die loudly like a PK violation, so it looks like it works - right up until the
		// concurrency actually happens.
		// The prevention is to **lock the inode through {prefix}lock** so the transactions serialize, and then
		// re-read the true data_id from the database after taking the lock and use the existing one when there is one.
		//
		// Why there is no `FOR UPDATE` on {prefix}inode here: once {prefix}inode is distributed on Citus with
		// shard_replication_factor > 1, row locks are refused
		// ("could not run distributed query with FOR UPDATE/SHARE commands"). Exclusion is concentrated in
		// {prefix}lock (a Citus local table) alone (docs/support_for_citus.md).
		// LockTargets takes locks in ascending target_id order, so the order against the later LockData (a positive
		// data_id) is preserved too (an inode lock is -id, hence negative, hence always first).
		this.LockInode(conn, tx, inode.Id);
		var freshDataId = conn.QueryFirstOrDefault<long?>(
			$"SELECT data_id FROM {this.QualifiedTable("inode")} WHERE parent_id = @parent_id AND id = @id",
			new { parent_id = inode.ParentId, id = inode.Id },
			tx
		);
		if (freshDataId is long winning) {
			// A concurrent other tx created and committed it first. Use that.
			if (Logger.IsTraceEnabled) { Logger.Trace("EnsureDataRow: lost race, reusing data_id:", winning, " for inode:", inode.Id); }
			inode.DataId = winning;
			var size = LoadChunkSize(conn, tx, winning);
			return (winning, size, false);
		}
		// Create the new pgfs_data row while still holding the lock.
		//
		// **The data_id link onto the inode (UPDATE inode) is not issued here; it is folded into the size/mtime
		// update at the end of the transaction (FinishWriteInodeInTx).** Citus with rf >= 2 serializes changes to the
		// same shard per shard, so holding the inode shard from the start of the transaction while waiting on the
		// chunk/data shards becomes hold-and-wait ("holding the inode, waiting for the data") and closes a
		// distributed-deadlock (40P01) cycle with a transaction that goes the other way round (identified by
		// measuring with citus_lock_waits). Normalizing the shard-touch order to **chunk/data, then inode** removes
		// the source of the cycle.
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
		inode.DataId = dataId;
		return (dataId, defaultChunkSize, true);
	}

	private int LoadChunkSize(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId) {
		return conn.QuerySingle<int>(
			$"SELECT chunk_size FROM {this.QualifiedTable("data")} WHERE id = @id",
			new { id = dataId },
			tx
		);
	}

	/// <summary>
	/// Decides the chunk size to use for a read. **The <c>{prefix}data</c> row may not exist yet** - that is the
	/// state of a data_id write-back reserved for a file that has never been flushed, and with metadata
	/// write-back's close-no-flush it happens routinely (the file is closed yet has no row in the database).
	/// In that case the value from the ledger (<see cref="DirtyFile.ChunkSize"/>) is used. When neither exists,
	/// the default is used (there is no content to read either way; this is only about computing chunk boundaries).
	/// </summary>
	private int ResolveChunkSizeForRead(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId) {
		var fromDb = this.LoadChunkSizeOrNull(conn, tx, dataId);
		if (fromDb is int size) { return size; }
		var known = this.dirtySet.Find(dataId);
		if (known != null) { return known.ChunkSize; }
		return this.DefaultChunkSize();
	}

	/// <summary>Reads the chunk size. Null when there is no row (a data_id reserved by write-back can be in that state).</summary>
	private int? LoadChunkSizeOrNull(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId) {
		return conn.QueryFirstOrDefault<int?>(
			$"SELECT chunk_size FROM {this.QualifiedTable("data")} WHERE id = @id",
			new { id = dataId },
			tx
		);
	}

	/// <summary>
	/// Creates the <c>{prefix}data</c> row **with a specified id** and links it to the inode (for an id reserved
	/// by write-back). The return value is the chunk size. The inode lock is taken before creating it, so this
	/// serializes against a concurrent transaction touching the same inode.
	/// </summary>
	private int CreateDataRowWithId(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, Inode inode, long dataId) {
		this.LockInode(conn, tx, inode.Id);
		var raced = this.LoadChunkSizeOrNull(conn, tx, dataId);
		if (raced != null) { return raced.Value; }
		var chunkSize = this.DefaultChunkSize();
		conn.Execute(
			$@"INSERT INTO {this.QualifiedTable("data")} (id, chunk_size, total_size, created_by, updated_by)
			   VALUES (@id, @chunk_size, 0, @user, @user)",
			new { id = dataId, chunk_size = chunkSize, user = Environment.UserName ?? "pgfs" },
			tx
		);
		// The caller issues the data_id link onto the inode at the end of the transaction (the normalized shard-touch order; see the comment on EnsureDataRow).
		if (Logger.IsTraceEnabled) { Logger.Trace("INSERT data (reserved id):", dataId, " chunk_size:", chunkSize); }
		return chunkSize;
	}

	/// <summary>
	/// Reads the range [offsetInChunk, offsetInChunk + length) out of a chunk.
	/// Null when the row does not exist. When the payload only reaches partway into the requested range, only
	/// what is there is returned (the caller is expected to zero-fill the rest).
	/// On PostgreSQL 13+, `substring(payload from N for M)` triggers a partial detoast of the TOAST value, so a
	/// 4 KB read from a 1 MB chunk transfers roughly 4 KB.
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
	/// Fills a destination span with one chunk's worth of data. With the content cache enabled it goes through
	/// the cache as a full chunk (<see cref="GetOrLoadChunk"/>); with it disabled it uses the original
	/// <see cref="ReadChunkSlice"/> (a partial detoast via substring). Holes (no row, or a short payload) are zero-filled.
	/// </summary>
	private void FillChunk(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId, int chunkIndex, int offsetInChunk, long cacheGen, System.Span<byte> dest) {
		if (!this.UseChunkCache) {
			CopySliceInto(this.ReadChunkSlice(conn, tx, dataId, chunkIndex, offsetInChunk, dest.Length), dest);
			return;
		}
		var (full, length) = this.GetOrLoadChunk(conn, tx, dataId, chunkIndex, cacheGen);
		CopyChunkInto(full, length, offsetInChunk, dest);
	}

	/// <summary>
	/// Whether reads go through a full chunk (the cache). **Even with the read cache disabled, write-back makes
	/// the cache path mandatory** - unflushed dirty data exists only in the cache, so reading from the database
	/// with substring would make a client unable to see its own writes.
	/// </summary>
	private bool UseChunkCache {
		// It is also true while a drain is in progress (dirty data left by a live disable). Dropping it here would
		// produce the state "there is newer dirty data in memory yet reads return the old content from the database".
		get { return this.contentCache.Enabled || this.config.Mount.WriteBack || this.dataDrainPending; }
	}

	/// <summary>Gets a full chunk payload from the cache. On a miss it reads from the database and caches it (when the generation is unchanged).</summary>
	private (byte[]? Payload, int Length) GetOrLoadChunk(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId, int chunkIndex, long cacheGen) {
		if (this.contentCache.TryGet(dataId, chunkIndex, out var cached, out var cachedLen)) {
			return (cached, cachedLen);
		}
		var full = this.ReadFullChunk(conn, tx, dataId, chunkIndex);
		if (full == null) {
			return (null, 0);
		}
		this.contentCache.PutIfGeneration(dataId, chunkIndex, full, cacheGen);
		return (full, full.Length);
	}

	/// <summary>Copies the result of <see cref="ReadChunkSlice"/> (a slice relative to the offset) into dest. Null or short data is zero-filled.</summary>
	private static void CopySliceInto(byte[]? sliceBytes, System.Span<byte> dest) {
		if (sliceBytes == null) {
			dest.Clear();
			return;
		}
		sliceBytes.AsSpan().CopyTo(dest);
		if (sliceBytes.Length < dest.Length) {
			dest.Slice(sliceBytes.Length).Clear();
		}
	}

	/// <summary>
	/// Copies a full chunk payload from <paramref name="offsetInChunk"/> onwards into dest. Null, out of range
	/// or short data is zero-filled.
	/// <paramref name="length"/> is the valid length (a write-back dirty buffer can have slack at the end, so
	/// this is what matters rather than <c>full.Length</c>).
	/// </summary>
	private static void CopyChunkInto(byte[]? full, int length, int offsetInChunk, System.Span<byte> dest) {
		if (full == null || offsetInChunk >= length) {
			dest.Clear();
			return;
		}
		var avail = Math.Min(dest.Length, length - offsetInChunk);
		full.AsSpan(offsetInChunk, avail).CopyTo(dest.Slice(0, avail));
		if (avail < dest.Length) {
			dest.Slice(avail).Clear();
		}
	}

	/// <summary>Reads a chunk's full payload (to fill the cache). Null when there is no row.</summary>
	private byte[]? ReadFullChunk(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId, int chunkIndex) {
		return conn.QueryFirstOrDefault<byte[]>(
			$@"SELECT payload
			   FROM {this.QualifiedTable("data_chunk")}
			   WHERE data_id = @data_id AND chunk_index = @ix",
			new { data_id = dataId, ix = chunkIndex },
			tx
		);
	}

	/// <summary>
	/// Writes data into a chunk starting at offsetInChunk.
	/// INSERTs when there is no row; otherwise overlays it, padding the payload with zeros to extend it as needed.
	/// It is a single SQL statement (race-safe: a concurrent transaction's INSERT is funnelled into the UPDATE path by ON CONFLICT).
	/// </summary>
	/// <returns>
	/// The change in this chunk's payload length (bytes). The caller sums them and applies the total to
	/// <c>{prefix}data.total_size</c> (the occupied bytes) through <see cref="AddOccupiedBytes"/>.
	/// </returns>
	private long WriteChunkSlice(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId, int chunkIndex, int offsetInChunk, byte[] data) {
		// The payload on the INSERT side: offsetInChunk zero bytes followed by data.
		//   With offsetInChunk = 0 it is just data.
		// The payload on the UPDATE side (the CASE):
		//   1) the existing payload is at least off+len -> replace the middle with an overlay (no extension needed)
		//   2) the existing payload is at least off but below off+len -> keep the first part (1..off) and append data
		//      (anything beyond the end is dropped)
		//   3) the existing payload is below off -> existing + zero padding + data (the hole is filled with zeros and extended)
		// Citus note: writing `updated_at = current_timestamp` into the DO UPDATE clause dies on a distributed table
		// "functions used in the DO UPDATE SET clause of INSERTs on distributed tables must be marked
		// with "must be marked IMMUTABLE". Passing it through EXCLUDED makes it "a constant from VALUES" and it goes
		// through. The same SQL works unchanged on plain PostgreSQL, so there is no need to branch on the Citus flag.
		var now = Pg.UtcNow;
		// To compute the occupied-bytes delta, the payload length before the UPSERT is read (a single row = a router query even on Citus).
		// `length(bytea)` only reads the raw size from the TOAST pointer, so nothing is detoasted.
		var beforeLen = conn.Query<long>(
			$"SELECT length(payload) FROM {this.QualifiedTable("data_chunk")} WHERE data_id = @data_id AND chunk_index = @ix",
			new { data_id = dataId, ix = chunkIndex },
			tx
		).FirstOrDefault();
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
			RETURNING length(payload)
		";
		var afterLen = conn.QuerySingle<long>(sql, new {
			data_id = dataId,
			ix = chunkIndex,
			off = offsetInChunk,
			len = data.Length,
			data,
			user = Environment.UserName ?? "pgfs",
			now,
		}, tx);
		if (Logger.IsTraceEnabled) { Logger.Trace("UPSERT data_chunk data_id:", dataId, " chunk_index:", chunkIndex, " off:", offsetInChunk, " len:", data.Length, " payload:", beforeLen, "->", afterLen); }
		return afterLen - beforeLen;
	}

	/// <summary>
	/// Adjusts a chunk to exactly exactLen bytes (truncates via substring if longer; zero-pads if it needs to grow).
	/// Does nothing if the row does not exist (= for TruncateData's trailing handling).
	/// </summary>
	/// <returns>The change in payload length (bytes). 0 when there is no row.</returns>
	private long TruncateChunk(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId, int chunkIndex, int exactLen) {
		var beforeLen = conn.Query<long>(
			$"SELECT length(payload) FROM {this.QualifiedTable("data_chunk")} WHERE data_id = @data_id AND chunk_index = @ix",
			new { data_id = dataId, ix = chunkIndex },
			tx
		).FirstOrDefault();
		var after = conn.Query<long>(
			$@"UPDATE {this.QualifiedTable("data_chunk")} SET
				payload = CASE
					WHEN length(payload) >= @len THEN substring(payload FROM 1 FOR @len)
					ELSE payload || decode(repeat('00', @len - length(payload)), 'hex')
				END,
				updated_at = (current_timestamp AT TIME ZONE 'UTC'),
				updated_by = @user
			   WHERE data_id = @data_id AND chunk_index = @ix
			   RETURNING length(payload)",
			new { data_id = dataId, ix = chunkIndex, len = exactLen, user = Environment.UserName ?? "pgfs" },
			tx
		).ToList();
		if (Logger.IsTraceEnabled) { Logger.Trace("TruncateChunk data_id:", dataId, " chunk_index:", chunkIndex, " len:", exactLen, " rows:", after.Count); }
		if (after.Count == 0) { return 0; }
		return after[0] - beforeLen;
	}

	// ------------------------------------------------------------------
	// Occupied bytes ({prefix}data.total_size) - what backs st_blocks and du
	// ------------------------------------------------------------------

	/// <summary>
	/// Adds a delta to <c>{prefix}data.total_size</c> (= how many bytes this body actually occupies in
	/// PostgreSQL = the sum of the chunk payload lengths) and returns the value after the addition.
	/// For a sparse file it is smaller than <c>st_size</c>, because the holes have no chunk rows.
	/// The distribution key (id) is in the WHERE clause, so it is a router query on Citus too. The volatile
	/// function appears only on the VALUES/SET side, so it does not run into the DO UPDATE restriction (which requires IMMUTABLE).
	/// </summary>
	private long AddOccupiedBytes(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId, long delta) {
		if (delta == 0) {
			return conn.Query<long>(
				$"SELECT total_size FROM {this.QualifiedTable("data")} WHERE id = @id",
				new { id = dataId },
				tx
			).FirstOrDefault();
		}
		var updated = conn.Query<long>(
			$@"UPDATE {this.QualifiedTable("data")}
			   SET total_size = GREATEST(0, total_size + @delta),
			       updated_at = (current_timestamp AT TIME ZONE 'UTC'),
			       updated_by = @user
			   WHERE id = @id
			   RETURNING total_size",
			new { id = dataId, delta, user = Environment.UserName ?? "pgfs" },
			tx
		).ToList();
		if (Logger.IsTraceEnabled) { Logger.Trace("occupied data_id:", dataId, " delta:", delta, " ->", updated.Count switch { > 0 => updated[0], _ => 0L }); }
		if (updated.Count == 0) { return 0; }
		return updated[0];
	}

	/// <summary>
	/// Returns how many bytes an inode's body occupies (this is what FUSE's <c>st_blocks</c> = what du sees is
	/// computed from).
	/// When it is already loaded in memory that value is returned; otherwise a single row is read from
	/// <c>{prefix}data</c> (the distribution key is id, so it is a router query on Citus too). A directory
	/// listing prefetches it through <see cref="PrefetchOccupiedBytes"/>, so this normally causes no round trip.
	/// </summary>
	public long GetOccupiedBytes(Inode inode) {
		if (inode.OccupiedBytesLoaded) { return inode.OccupiedBytes; }
		if (inode.DataId == null) {
			this.SetOccupiedBytes(inode, 0);
			return 0;
		}
		var occupied = Pg.Query<long>(
			this.connectionString,
			$"SELECT total_size FROM {this.QualifiedTable("data")} WHERE id = @id",
			new { id = inode.DataId.Value }
		).FirstOrDefault();
		this.SetOccupiedBytes(inode, occupied);
		return occupied;
	}

	/// <summary>Applies the occupied bytes to the in-memory Inode (both the argument's instance and the separate instance in the cache).</summary>
	private void SetOccupiedBytes(Inode inode, long occupied) {
		inode.OccupiedBytes = occupied;
		inode.OccupiedBytesLoaded = true;
		var cached = this.inodeCache.Get(inode.Id);
		if (cached != null && !ReferenceEquals(cached, inode)) {
			cached.OccupiedBytes = occupied;
			cached.OccupiedBytesLoaded = true;
		}
	}

	/// <summary>
	/// Prefetches the occupied bytes of every child in a single query during a directory listing (avoiding the
	/// N+1 of one round trip per getattr). A child with no data is marked as loaded with 0.
	/// </summary>
	private void PrefetchOccupiedBytes(IReadOnlyList<Inode> list) {
		var ids = new List<long>();
		foreach (var inode in list) {
			if (inode.DataId == null) {
				inode.OccupiedBytes = 0;
				inode.OccupiedBytesLoaded = true;
				continue;
			}
			ids.Add(inode.DataId.Value);
		}
		if (ids.Count == 0) { return; }
		var map = new Dictionary<long, long>();
		var rows = Pg.Query<dynamic>(
			this.connectionString,
			$"SELECT id, total_size FROM {this.QualifiedTable("data")} WHERE id = ANY(@ids)",
			new { ids = ids.ToArray() }
		);
		foreach (var row in rows) {
			map[(long)row.id] = (long)row.total_size;
		}
		foreach (var inode in list) {
			if (inode.DataId == null) { continue; }
			// Write-back: for a file holding unflushed data the in-memory value is newer (overwriting it with the database value would make du go backwards).
			if (this.HasPendingDirty(inode.DataId.Value)) { continue; }
			if (!map.TryGetValue(inode.DataId.Value, out var occupied)) {
				// **No row = the content is empty** (the data_id is fixed at create time and the {prefix}data row is created
				// lazily; docs/data-id-lifecycle.md). Without settling 0 here, every file that has never been written would
				// add one GetOccupiedBytes round trip (which shows up in ls -l).
				inode.OccupiedBytes = 0;
				inode.OccupiedBytesLoaded = true;
				continue;
			}
			inode.OccupiedBytes = occupied;
			inode.OccupiedBytesLoaded = true;
		}
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
		var parentId = this.ResolveParentId(conn, tx, inodeId);
		if (parentId == null) { return; }
		var rows = conn.Execute(
			$"UPDATE {this.QualifiedTable("inode")} SET data_id = NULL, updated_at = (current_timestamp AT TIME ZONE 'UTC') WHERE parent_id = @parent_id AND id = @id",
			new { parent_id = parentId.Value, id = inodeId },
			tx
		);
		if (Logger.IsTraceEnabled) { Logger.Trace("UPDATE inode SET data_id:NULL WHERE id:", inodeId, " rows:", rows); }
	}

	/// <summary>
	/// Writes a single inode's <c>st_size</c> and **returns that row's <c>st_nlink</c>** (0 when there is no row).
	/// The return value decides whether the value has to be distributed to hardlink siblings - the key point is
	/// that it uses **the database value rather than the cache**: a stale cache that appears to be 1 would skip
	/// the distribution and leave the siblings holding a stale <c>st_size</c> (exactly the bug
	/// docs/data-id-lifecycle.md fixes). It is free, because it comes from RETURNING.
	/// </summary>
	private int UpdateSizeInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long inodeId, long size) {
		var parentId = this.ResolveParentId(conn, tx, inodeId);
		if (parentId == null) { return 0; }
		var nlinks = conn.Query<int>(
			$@"UPDATE {this.QualifiedTable("inode")}
			   SET st_size = @size, st_mtime = (current_timestamp AT TIME ZONE 'UTC'),
			       st_ctime = (current_timestamp AT TIME ZONE 'UTC'), updated_at = (current_timestamp AT TIME ZONE 'UTC')
			   WHERE parent_id = @parent_id AND id = @id
			   RETURNING st_nlink",
			new { parent_id = parentId.Value, id = inodeId, size },
			tx
		).ToList();
		if (Logger.IsTraceEnabled) { Logger.Trace("UPDATE inode SET st_size:", size, " WHERE id:", inodeId, " rows:", nlinks.Count); }
		if (nlinks.Count == 0) { return 0; }
		return nlinks[0];
	}

	/// <summary>
	/// Writes <c>st_size</c> after a truncate. With hardlinks it **distributes to every link**; otherwise it
	/// updates the single row as before.
	/// <para>
	/// In POSIX hardlinks share one body, so truncating through one link has to change the other's
	/// <c>st_size</c> too. Without distributing it, a sibling ends up in the state "it has a size but no chunks"
	/// and reads return NUL bytes (docs/data-id-lifecycle.md).
	/// </para>
	/// </summary>
	/// <returns>The ids of the siblings that were distributed to (empty when there are none).</returns>
	private List<long> UpdateTruncatedSizeInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, Inode inode, long size) {
		// Write this inode's own row first, and decide whether to distribute from **the database st_nlink obtained by its RETURNING**.
		var nlink = this.UpdateSizeInTx(conn, tx, inode.Id, size);
		if (nlink <= 1) { return new List<long>(); }
		if (inode.DataId is not long dataId) { return new List<long>(); }
		return this.UpdateSizeForAllLinksInTx(conn, tx, dataId, inode.Id, size);
	}

	/// <summary>
	/// Distributes <c>st_size</c> to every inode that references the same body.
	/// Look the targets up by <c>data_id</c> only when <c>st_nlink &gt; 1</c> (on Citus that is a multi-shard
	/// UPDATE; the same shape is used in <see cref="CreateHardLink"/> and
	/// <see cref="UpdateInodeSizeAndMtimeInTx"/>).
	/// </summary>
	/// <returns>The ids of the siblings that were distributed to (excluding this inode). Used exactly as in <see cref="PropagateWriteToLinksInTx"/>.</returns>
	private List<long> UpdateSizeForAllLinksInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId, long selfId, long size) {
		var siblingIds = conn.Query<long>(
			$@"UPDATE {this.QualifiedTable("inode")}
			   SET st_size = @size, st_mtime = (current_timestamp AT TIME ZONE 'UTC'),
			       st_ctime = (current_timestamp AT TIME ZONE 'UTC'), updated_at = (current_timestamp AT TIME ZONE 'UTC')
			   WHERE data_id = @data_id AND id <> @self_id
			   RETURNING id",
			new { data_id = dataId, self_id = selfId, size },
			tx
		).ToList();
		if (Logger.IsTraceEnabled) { Logger.Trace("UPDATE inode SET st_size:", size, " WHERE data_id:", dataId, " rows:", siblingIds.Count); }
		return siblingIds;
	}

	/// <summary>
	/// Resets a data row's occupied bytes to 0 (after a <c>truncate 0</c> deleted every chunk).
	/// When the row does not exist yet (still a reserved data_id) it updates 0 rows, which is harmless.
	/// </summary>
	private void ResetDataTotalSizeInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long dataId) {
		var rows = conn.Execute(
			$@"UPDATE {this.QualifiedTable("data")}
			   SET total_size = 0, updated_at = (current_timestamp AT TIME ZONE 'UTC'), updated_by = @user
			   WHERE id = @id",
			new { id = dataId, user = Environment.UserName ?? "pgfs" },
			tx
		);
		if (Logger.IsTraceEnabled) { Logger.Trace("UPDATE data SET total_size:0 WHERE id:", dataId, " rows:", rows); }
	}

	private void TouchMtimeInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, long inodeId) {
		var parentId = this.ResolveParentId(conn, tx, inodeId);
		if (parentId == null) { return; }
		var rows = conn.Execute(
			$@"UPDATE {this.QualifiedTable("inode")}
			   SET st_mtime = (current_timestamp AT TIME ZONE 'UTC'), updated_at = (current_timestamp AT TIME ZONE 'UTC')
			   WHERE parent_id = @parent_id AND id = @id",
			new { parent_id = parentId.Value, id = inodeId },
			tx
		);
		if (Logger.IsTraceEnabled) { Logger.Trace("UPDATE inode SET st_mtime WHERE id:", inodeId, " rows:", rows); }
	}

	/// <summary>
	/// At the end of a write transaction, writes the inode's st_size and mtime and returns **the size the
	/// database settled on** together with the hardlink sibling ids.
	/// <para>
	/// <b>st_size is updated monotonically with `GREATEST`.</b> The local <c>inode.Size</c> **knows nothing about
	/// what another mount appended**, so writing `st_size = @size` naively lets **a shorter write that arrives
	/// later roll the database size back**. Measured with two mounts appending concurrently, <c>st_size</c> went
	/// from 4198400 back to **4216**, and **4 MiB became unreachable from the filesystem even though the chunks
	/// were in the database**.
	/// </para>
	/// <para>
	/// <b>What is returned is the settled value.</b> When `GREATEST` rejected it (= another mount had extended
	/// further), distributing <paramref name="newSize"/> to memory and to the siblings would make **the next
	/// append believe the shorter size is the end**. Use the value received from `RETURNING`.
	/// </para>
	/// </summary>
	private (List<long> Siblings, long Size) FinishWriteInodeInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, Inode inode, long dataId, bool linkPending, long newSize) {
		var parentId = this.ResolveParentId(conn, tx, inode.Id);
		if (parentId == null && !linkPending) { return (new List<long>(), newSize); }
		if (parentId == null) {
			// The inode was deleted concurrently. This transaction has already INSERTed the data row, so it is rolled back to prevent an orphan.
			throw new InvalidOperationException($"FinishWriteInodeInTx: inode {inode.Id} no longer exists");
		}
		var set = "st_mtime = (current_timestamp AT TIME ZONE 'UTC'), updated_at = (current_timestamp AT TIME ZONE 'UTC')";
		// **Whether to emit the clause must not be decided from the local inode.Size.** The value is made monotonic
		// by `GREATEST`, but **the inode.Size used for the decision is a cache and can be stale** - overwriting
		// without O_TRUNC after another mount truncated the file makes `newSize > inode.Size` false, so **the
		// st_size clause drops out of the UPDATE and the database size stays 0**. The chunks that were written
		// remain, but the 0 received from `RETURNING st_size` is distributed to memory as well, so **this mount's own
		// ls and cat show nothing** and those bytes become unreachable (review H-1). `GREATEST` means **emitting it
		// always can never shrink anything** = this branch was a pure optimization, and that optimization was the hole.
		set = "st_size = GREATEST(st_size, @size), st_ctime = (current_timestamp AT TIME ZONE 'UTC'), " + set;
		if (linkPending) { set = "data_id = @data_id, " + set; }
		var updated = conn.Query(
			$"UPDATE {this.QualifiedTable("inode")} SET {set} WHERE parent_id = @parent_id AND id = @id RETURNING st_nlink, st_size",
			new { parent_id = parentId.Value, id = inode.Id, size = newSize, data_id = dataId },
			tx
		).ToList();
		var rows = updated.Count;
		if (Logger.IsTraceEnabled) { Logger.Trace("UPDATE inode (finish write) link:", linkPending, " size:", newSize, " id:", inode.Id, " rows:", rows); }
		if (linkPending && rows == 0) {
			throw new InvalidOperationException($"FinishWriteInodeInTx: inode {inode.Id} vanished before link");
		}
		if (rows == 0) { return (new List<long>(), newSize); }
		var effective = (long)updated[0].st_size;
		var nlink = (int)updated[0].st_nlink;
		return (this.PropagateWriteToLinksInTx(conn, tx, inode, dataId, effective, nlink), effective);
	}

	/// <summary>
	/// Drops the <see cref="InodeCache"/> entries of the hardlink siblings that were distributed to (the cleanup
	/// after distributing to the database). **The ids are given directly**, so the whole cache needs no scan.
	/// </summary>
	private void InvalidateHardLinkSiblings(List<long> siblingIds) {
		foreach (var siblingId in siblingIds) {
			this.inodeCache.Invalidate(siblingId, path: null);
		}
	}

	/// <summary>
	/// Distributes <c>st_size</c>, <c>st_mtime</c> and <c>st_ctime</c> to the hardlink siblings after a write.
	/// <para>
	/// Hardlinks share the body, so even when the chunks are shared, **a reader truncated by a stale
	/// <c>st_size</c>** sees empty content (docs/data-id-lifecycle.md). The truncate path distributes through
	/// <see cref="UpdateTruncatedSizeInTx"/>, but **an ordinary write needs the same distribution**.
	/// Look the targets up by <c>data_id</c> only when <c>st_nlink &gt; 1</c> (a multi-shard UPDATE on Citus).
	/// </para>
	/// </summary>
	/// <returns>
	/// The ids of the siblings actually distributed to. **They come free from `RETURNING id`** (this statement
	/// only runs when <c>nlink &gt; 1</c>, so it adds neither a round trip nor multi-shard work). The caller uses
	/// them both to
	/// (1) invalidate its own <see cref="InodeCache"/> (by id, so O(1) per entry) and
	/// (2) put them in <see cref="Notify"/>'s <c>inodeIds</c> so **the other mounts drop them too**.
	/// Without (2), stat-ing a hardlink sibling from another mount returns a stale <c>st_size</c>
	/// (docs/data-id-lifecycle.md).
	/// </returns>
	private List<long> PropagateWriteToLinksInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, Inode inode, long dataId, long newSize, int nlink) {
		if (nlink <= 1) { return new List<long>(); }
		var set = "st_mtime = (current_timestamp AT TIME ZONE 'UTC'), updated_at = (current_timestamp AT TIME ZONE 'UTC')";
		// **Whether to emit the clause must not be decided from the local inode.Size.** The value is made monotonic
		// by `GREATEST`, but **the inode.Size used for the decision is a cache and can be stale** - overwriting
		// without O_TRUNC after another mount truncated the file makes `newSize > inode.Size` false, so **the
		// st_size clause drops out of the UPDATE and the database size stays 0**. The chunks that were written
		// remain, but the 0 received from `RETURNING st_size` is distributed to memory as well, so **this mount's own
		// ls and cat show nothing** and those bytes become unreachable (review H-1). `GREATEST` means **emitting it
		// always can never shrink anything** = this branch was a pure optimization, and that optimization was the hole.
		set = "st_size = GREATEST(st_size, @size), st_ctime = (current_timestamp AT TIME ZONE 'UTC'), " + set;
		var siblingIds = conn.Query<long>(
			$"UPDATE {this.QualifiedTable("inode")} SET {set} WHERE data_id = @data_id AND id <> @id RETURNING id",
			new { data_id = dataId, id = inode.Id, size = newSize },
			tx
		).ToList();
		if (Logger.IsTraceEnabled) { Logger.Trace("UPDATE inode (distributed to hardlink siblings) size:", newSize, " data_id:", dataId, " rows:", siblingIds.Count); }
		return siblingIds;
	}

	/// <summary>
	/// Links a data_id onto an inode. **Call it at the end of the transaction, after the chunk/data shards have
	/// been touched** - the normalized shard-touch order (the reason is in the comment on
	/// <see cref="EnsureDataRow"/>). When the inode was deleted concurrently it throws to force a rollback,
	/// preventing the already-INSERTed data row from being orphaned.
	/// </summary>
	private void LinkDataIdInTx(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, Inode inode, long dataId) {
		var rows = conn.Execute(
			$"UPDATE {this.QualifiedTable("inode")} SET data_id = @data_id, updated_at = (current_timestamp AT TIME ZONE 'UTC') WHERE parent_id = @parent_id AND id = @id",
			new { data_id = dataId, parent_id = inode.ParentId, id = inode.Id },
			tx
		);
		if (Logger.IsTraceEnabled) { Logger.Trace("UPDATE inode SET data_id:", dataId, " WHERE id:", inode.Id, " rows:", rows); }
		if (rows == 0) { throw new InvalidOperationException($"LinkDataIdInTx: inode {inode.Id} no longer exists"); }
	}

	// ------------------------------------------------------------------
	// Retrying a distributed deadlock (40P01)
	// ------------------------------------------------------------------

	/// <summary>The maximum number of attempts for a 40P01/40001 retry.</summary>
	private const int DeadlockMaxAttempts = 4;

	/// <summary>
	/// Which errors may be retried: 40P01 (deadlock detected, including Citus's distributed deadlock detection)
	/// and 40001 (serialization failure). In both cases the victim's transaction has rolled back completely, so
	/// re-running is safe **only for a self-contained transaction** (one that opens its own connection and
	/// transaction and runs through to the commit).
	/// Normalizing the shard-touch order (<see cref="FinishWriteInodeInTx"/>) removed the main source of cycles,
	/// but Citus with rf >= 2 can still form one internally, for instance during the COMMIT phase of 2PC, so
	/// this stays as a safety net.
	/// </summary>
	private static bool IsRetryableDeadlock(Npgsql.PostgresException ex) {
		return ex.SqlState == "40P01" || ex.SqlState == "40001";
	}

	/// <summary>The wait before a retry (linear backoff plus jitter, so that all the victims do not re-enter at once and collide again).</summary>
	private static void SleepBeforeDeadlockRetry(int attempt) {
		System.Threading.Thread.Sleep(20 * attempt + System.Random.Shared.Next(30));
	}
}
