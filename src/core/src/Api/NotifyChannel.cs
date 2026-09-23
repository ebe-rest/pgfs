namespace Pgfs.Core.Api;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Pgfs.Core.Logging;
using Pgfs.Core.Utility;

/// <summary>
/// Cross-client change notification using PostgreSQL <c>LISTEN</c> / <c>NOTIFY</c>.
///
/// <para>
/// **Delivery model**: a writing client issues <c>SELECT pg_notify(channel, payload)</c> at the commit of each operation.
/// PostgreSQL delivers the JSON payload to other sessions <c>LISTEN</c>ing on the same channel (only on commit;
/// not on rollback — so transactional consistency is preserved).
/// The receiving side (this class) keeps one dedicated connection open and picks up messages via the
/// <see cref="NpgsqlConnection.Notification"/> event.
/// </para>
///
/// <para>
/// **Channel name**: <c>{schema}_{prefix}notify</c> (e.g. <c>public_pgfs_notify</c> / <c>pgfs_pgfs_notify</c>).
/// The NOTIFY namespace is per-DB, so both schema and prefix are included so that multiple pgfs instances (different schema / prefix)
/// in the same DB do not collide.
/// </para>
///
/// <para>
/// **Self-message filtering**: the constructor generates an 8-character sender id (random hex) and puts it in the payload's
/// <c>"s"</c> field. The receiver discards messages whose sender id matches its own
/// (= because a NOTIFY you issued also reaches you. pgfs's InodeCache is already updated in-place so re-applying does no harm,
/// but it is wasteful, so drop it).
/// </para>
///
/// <para>
/// **Payload**: JSON. <c>{"s":"<sender>","i":[<inode_ids>],"p":[<parent_ids>],"x":[<path_prefixes>]}</c>.
/// 8000-byte limit per message (a NOTIFY constraint). In pgfs's expected operation it is one message per operation with only a few IDs, so there is plenty of room.
/// </para>
/// </summary>
public sealed class NotifyChannel : IDisposable
{
	private readonly string connectionString;
	private readonly string channelName;
	private readonly string senderId;
	private readonly CancellationTokenSource cts = new();
	private NpgsqlConnection? listenConn;
	private Task? listenTask;
	private bool disposed;

	/// <summary>Handler for processing received cross-client changes. Register it before <see cref="Start"/>.</summary>
	public event Action<NotifyMessage>? Received;

	/// <summary>This client's sender id (for NOTIFY self-message detection, 8-char hex).</summary>
	public string SenderId {
		get { return this.senderId; }
	}

	public string ChannelName {
		get { return this.channelName; }
	}

	/// <summary>Whether the LISTEN connection is currently open (a diagnostic for status Layer 3). False for a moment while reconnecting.</summary>
	public bool Connected {
		get { return !this.disposed && this.listenConn != null; }
	}

	public NotifyChannel(string connectionString, string schemaName, string tablePrefix) {
		this.connectionString = connectionString;
		this.channelName = BuildChannelName(schemaName, tablePrefix);
		this.senderId = GenerateSenderId();
	}

	/// <summary>
	/// Opens a dedicated connection, issues <c>LISTEN</c> synchronously, and starts the receive loop in the background.
	/// On connection failure it throws to the caller (= if notify is requested but cannot be opened, startup should abort).
	/// Repeated calls are ignored. <see cref="Dispose"/> stops the loop.
	/// </summary>
	public void Start() {
		if (this.listenTask != null) {
			return;
		}
		// Establish LISTEN synchronously and emit the Information log on the calling thread
		// (so the log reliably appears before Api ctor → post-mount Console.SetOut(null)).
		var conn = new NpgsqlConnection(this.connectionString);
		conn.Open();
		conn.Notification += this.OnNotification;
		using (var cmd = conn.CreateCommand()) {
			cmd.CommandText = $"LISTEN {Pg.QuoteIdentifier(this.channelName)}";
			cmd.ExecuteNonQuery();
		}
		this.listenConn = conn;
		Logger.Information("NotifyChannel: LISTEN ", this.channelName, " (sender=", this.senderId, ")");

		// The notification wait is blocking, so move it to the background. It also handles reconnection (on transient failure).
		this.listenTask = Task.Run(() => this.RunWaitLoopAsync(this.cts.Token));
	}

	/// <summary>
	/// Sends a notification to other clients. Issues channel + payload via <c>SELECT pg_notify(@ch, @payload)</c>.
	/// This method is intended to be called outside a transaction (= after the operation completes, once committed).
	/// To issue it inside a transaction, the caller should add a separate <c>SELECT pg_notify(...)</c> to the tx
	/// (this method uses a short autocommit connection).
	/// <para>
	/// **It returns whether the notification was sent** (false = it was not). Exceptions are caught here, so the
	/// caller decides by the return value. Before, it returned nothing, and the caller's "put the obligation back
	/// if it could not be sent" catch **never ran** - a notification lost to a database blip or a payload over
	/// 8000 bytes **never** reached the other clients.
	/// </para>
	/// </summary>
	public bool Publish(NotifyMessage message) {
		if (this.disposed) {
			return false;
		}
		message.Sender = this.senderId;
		string payload;
		try {
			payload = JsonSerializer.Serialize(message);
		} catch (System.Exception ex) {
			Logger.Warning("NotifyChannel.Publish: payload serialize failed: ", ex.Message);
			return false;
		}
		try {
			Pg.Execute(
				this.connectionString,
				"SELECT pg_notify(@ch, @payload)",
				new { ch = this.channelName, payload }
			);
		} catch (System.Exception ex) {
			// A publish failure is not fatal (other clients just miss it). Leave it as a warning and tell the caller.
			Logger.Warning("NotifyChannel.Publish: ", ex.Message);
			return false;
		}
		return true;
	}

	/// <summary>
	/// **Raised when LISTEN has been re-established.** PostgreSQL does not deliver notifications to a session that
	/// is not listening, so the notifications sent before LISTEN came back are lost and the subscriber **has no
	/// way to learn about those changes**. The subscriber (<c>Api</c>) drops its clean caches on this.
	/// </summary>
	public event Action? Reconnected;

	public void Dispose() {
		if (this.disposed) {
			return;
		}
		this.disposed = true;
		try {
			this.cts.Cancel();
		} catch {
		}
		try {
			this.listenTask?.Wait(TimeSpan.FromSeconds(2));
		} catch {
		}
		if (this.listenConn != null) {
			this.listenConn.Notification -= this.OnNotification;
			try { this.listenConn.Dispose(); } catch { }
			this.listenConn = null;
		}
		this.cts.Dispose();
	}

	// ------------------------------------------------------------------
	// Internal: receive loop
	// ------------------------------------------------------------------

	/// <summary>
	/// Keeps running the notification wait on the <see cref="listenConn"/> opened by <see cref="Start"/>.
	/// On disconnect it reconnects and re-LISTENs.
	/// </summary>
	private async Task RunWaitLoopAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			try {
				var conn = this.listenConn;
				if (conn == null) {
					break;
				}
				while (!ct.IsCancellationRequested) {
					await conn.WaitAsync(ct);
				}
			} catch (OperationCanceledException) {
				break;
			} catch (System.Exception ex) {
				Logger.Warning("NotifyChannel: error in the wait loop (reconnecting): ", ex.Message);
				// reconnect if the connection died
				try {
					if (this.listenConn != null) {
						this.listenConn.Notification -= this.OnNotification;
						try { await this.listenConn.DisposeAsync(); } catch { }
						this.listenConn = null;
					}
					await Task.Delay(TimeSpan.FromSeconds(2), ct);
					if (ct.IsCancellationRequested) {
						break;
					}
					var newConn = new NpgsqlConnection(this.connectionString);
					await newConn.OpenAsync(ct);
					newConn.Notification += this.OnNotification;
					using (var cmd = newConn.CreateCommand()) {
						cmd.CommandText = $"LISTEN {Pg.QuoteIdentifier(this.channelName)}";
						await cmd.ExecuteNonQueryAsync(ct);
					}
					this.listenConn = newConn;
					Logger.Information("NotifyChannel: re-LISTEN ", this.channelName);
					// A failure in the subscriber must not stop the receive loop.
					try { this.Reconnected?.Invoke(); } catch (System.Exception ex3) { Logger.Warning("NotifyChannel: handling Reconnected failed (continuing): ", ex3.Message); }
				} catch (OperationCanceledException) {
					break;
				} catch (System.Exception ex2) {
					Logger.Warning("NotifyChannel: reconnect failed (retrying in 2s): ", ex2.Message);
				}
			}
		}
	}

	private void OnNotification(object sender, NpgsqlNotificationEventArgs e) {
		if (this.disposed) {
			return;
		}
		// Just in case, check that delivery for another channel is not mixed in (should not happen if only one LISTEN is set).
		if (!string.Equals(e.Channel, this.channelName, StringComparison.Ordinal)) {
			return;
		}
		NotifyMessage? msg;
		try {
			msg = JsonSerializer.Deserialize<NotifyMessage>(e.Payload);
		} catch (System.Exception ex) {
			Logger.Warning("NotifyChannel: failed to JSON-deserialize the payload (", ex.Message, "): ", e.Payload);
			return;
		}
		if (msg == null) {
			return;
		}
		if (string.Equals(msg.Sender, this.senderId, StringComparison.Ordinal)) {
			// A message we issued ourselves. Skip.
			return;
		}
		try {
			this.Received?.Invoke(msg);
		} catch (System.Exception ex) {
			Logger.Warning("NotifyChannel: exception in the Received handler: ", ex.Message);
		}
	}

	// ------------------------------------------------------------------
	// Internal: helpers
	// ------------------------------------------------------------------

	private static string BuildChannelName(string schemaName, string tablePrefix) {
		var schema = string.IsNullOrEmpty(schemaName) switch {
			true  => "public",
			false => schemaName,
		};
		var prefix = tablePrefix ?? "";
		return $"{schema}_{prefix}notify";
	}

	private static string GenerateSenderId() {
		// 8-char hex random (4 bytes = 32 bits is plenty by the birthday problem at ~1000 clients)
		System.Span<byte> buf = stackalloc byte[4];
		System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
		return System.Convert.ToHexString(buf).ToLowerInvariant();
	}
}

/// <summary>
/// The JSON shape of the NOTIFY payload.
/// <c>{"s":"<sender>","i":[<inode_ids>],"p":[<parent_ids>],"x":[<path_prefixes>],"d":[<data_ids>]}</c>.
/// </summary>
public sealed class NotifyMessage
{
	/// <summary>The sending sender id (8-char hex). For self-message detection.</summary>
	[System.Text.Json.Serialization.JsonPropertyName("s")]
	public string Sender { get; set; } = "";

	/// <summary>The list of changed/deleted inode IDs. The receiver <see cref="InodeCache.Invalidate"/>s these IDs.</summary>
	[System.Text.Json.Serialization.JsonPropertyName("i")]
	public List<long> InodeIds { get; set; } = new();

	/// <summary>The list of parent inode IDs whose child list changed. The receiver calls <see cref="InodeCache.InvalidateChildren"/>.</summary>
	[System.Text.Json.Serialization.JsonPropertyName("p")]
	public List<long> ParentIds { get; set; } = new();

	/// <summary>The list of deleted/renamed path prefixes. The receiver calls <see cref="InodeCache.InvalidatePrefix"/>.</summary>
	[System.Text.Json.Serialization.JsonPropertyName("x")]
	public List<string> PathPrefixes { get; set; } = new();

	/// <summary>The list of data_ids whose body changed (write/truncate). The receiving side calls <see cref="ContentCache.InvalidateData"/>.</summary>
	[System.Text.Json.Serialization.JsonPropertyName("d")]
	public List<long> DataIds { get; set; } = new();

	/// <summary>
	/// **The instruction to recover from a dropped notification**. When <c>true</c> the receiving side throws
	/// the whole cache away (because it does not know which ids changed). **Exactly one is sent when the
	/// sending queue overflows** (review M-2). When false it is not emitted in the payload.
	/// </summary>
	[System.Text.Json.Serialization.JsonPropertyName("r")]
	[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
	public bool Resync { get; set; }

	/// <summary>The kind of control message (<c>"reload"</c> / <c>"set"</c> / <c>"ping"</c>). null = an ordinary change notification. When null it is not emitted in the payload.</summary>
	[System.Text.Json.Serialization.JsonPropertyName("c")]
	[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
	public string? Control { get; set; }

	/// <summary>The target full-key of a control <c>"set"</c> (<c>scope.key</c>, for example <c>logging.level</c>). null for anything but set.</summary>
	[System.Text.Json.Serialization.JsonPropertyName("k")]
	[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
	public string? Key { get; set; }

	/// <summary>The raw value of a control <c>"set"</c> (a representation the Field can Parse). null for anything but set.</summary>
	[System.Text.Json.Serialization.JsonPropertyName("v")]
	[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
	public string? Value { get; set; }
}
