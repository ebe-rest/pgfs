namespace Pgfs.Lib.Config;

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Pgfs.Lib.Utility;

/// <summary>
/// A Store that abstracts the DB read/write of <c>pgfs_settings</c>. <see cref="ConfigLoader"/> uses it to read the
/// <see cref="SaveTarget.Db"/> <see cref="Field{T}"/>s, and at mkfs time it writes the initial values via <see cref="Save{T}"/>.
///
/// <para>
/// **DB schema (the flat form)**: <c>{prefix}settings(scope TEXT, key TEXT, value JSONB, ...)</c>
/// with PK <c>(scope, key)</c>. It has no `id` / `parent_id`. <see cref="LoadAll"/> reads via a simple
/// <c>SELECT scope, key, value</c>, and <see cref="Save{T}"/> writes via <c>INSERT ... ON CONFLICT
/// (scope, key) DO UPDATE</c>.
/// </para>
/// </summary>
public sealed class ConfigStore
{
	private readonly string connectionString;
	private readonly string qualifiedTable;

	public ConfigStore(string connectionString, string schemaName, string tablePrefix) {
		this.connectionString = connectionString;
		var qSchema = Pg.QuoteIdentifier(schemaName);
		var qTable = Pg.QuoteIdentifier(tablePrefix + "settings");
		this.qualifiedTable = $"{qSchema}.{qTable}";
	}

	/// <summary>
	/// Fetches all the <see cref="SaveTarget.Db"/> entries among <paramref name="fields"/> in a single SELECT, and returns the
	/// <c>scope.key</c> full-key plus the unwrapped raw representation. The value is converted from JSONB back to a raw
	/// representation via <see cref="UnwrapJsonValue"/> (the inner contents for a StringField; the numeric / bool text for an
	/// IntField/BoolField) so that <see cref="Field{T}.Parse"/> can take it directly.
	/// </summary>
	public IEnumerable<KeyValuePair<string, string>> LoadAll(IReadOnlyList<Field> fields) {
		var allowedFullKeys = new HashSet<string>(
			fields.Where(f => f.SaveTo == SaveTarget.Db).Select(f => f.FullKey)
		);
		if (allowedFullKeys.Count == 0) {
			yield break;
		}
		var sql = $"SELECT scope, key, value::text AS value FROM {this.qualifiedTable}";
		List<dynamic> rows;
		try {
			rows = Pg.Query<dynamic>(this.connectionString, sql).ToList();
		} catch (System.Exception ex) {
			Pgfs.Lib.Logging.Logger.Warning(
				"ConfigStore.LoadAll: load failed (using Default): ", ex.Message);
			yield break;
		}
		foreach (var row in rows) {
			var scope = (string)row.scope;
			var key = (string)row.key;
			var fullKey = $"{scope}.{key}";
			if (!allowedFullKeys.Contains(fullKey)) {
				continue;
			}
			var unwrapped = UnwrapJsonValue((string)row.value);
			if (unwrapped == null) {
				continue;
			}
			yield return new KeyValuePair<string, string>(fullKey, unwrapped);
		}
	}

	/// <summary>
	/// UPSERTs a single <see cref="Field{T}"/>'s value as a flat <c>(scope, key)</c> row.
	/// It is turned into a JSONB literal via <see cref="Field{T}.FormatJson"/> before writing.
	/// If a row already exists, <c>value</c> / <c>updated_at</c> / <c>updated_by</c> are updated.
	/// </summary>
	public void Save<T>(Field<T> field, T value) {
		var jsonValue = field.FormatJson(value);
		var user = System.Environment.UserName.IsNotNullAndNotWhiteSpace()
			? System.Environment.UserName
			: "pgfs";
		// `updated_at` is generated client-side and referenced via EXCLUDED.
		// Because pgfs_settings is registered into metadata via citus_add_local_table_to_metadata on Citus, putting a
		// non-IMMUTABLE function (current_timestamp / now(), etc.) in the DO UPDATE SET expression is rejected with
		// "functions used in the DO UPDATE SET clause of INSERTs on distributed tables must be marked IMMUTABLE".
		// EXCLUDED.* is a constant from VALUES, so it avoids that. The same SQL runs as-is on a single PG too, so no branch is needed.
		var now = System.DateTime.UtcNow;
		var sql = $"""
			INSERT INTO {this.qualifiedTable} (scope, key, value, created_at, created_by, updated_at, updated_by)
			VALUES (@scope, @key, @value::jsonb, @now, @user, @now, @user)
			ON CONFLICT (scope, key) DO UPDATE SET
				value = EXCLUDED.value,
				updated_at = EXCLUDED.updated_at,
				updated_by = EXCLUDED.updated_by
			""";
		Pg.Execute(this.connectionString, sql, new {
			scope = field.Scope,
			key = field.Key,
			value = jsonValue,
			user,
			now,
		});
	}

	/// <summary>
	/// Unwraps the JSONB text representation into a raw value representation.
	/// string → inner contents, number → as-is (`42`), bool → `true`/`false`, null → null (signals "use Default").
	/// </summary>
	private static string? UnwrapJsonValue(string rawJson) {
		try {
			using var doc = JsonDocument.Parse(rawJson);
			switch (doc.RootElement.ValueKind) {
				case JsonValueKind.String:
					return doc.RootElement.GetString() ?? "";
				case JsonValueKind.Number:
					return rawJson;
				case JsonValueKind.True:
					return "true";
				case JsonValueKind.False:
					return "false";
				case JsonValueKind.Null:
				case JsonValueKind.Undefined:
					return null;
			}
			return rawJson;
		} catch {
			return rawJson;
		}
	}
}
