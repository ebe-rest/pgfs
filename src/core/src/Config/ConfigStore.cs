namespace Pgfs.Core.Config;

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Pgfs.Core.Utility;

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
			Pgfs.Core.Logging.Logger.Warning(
				"ConfigStore.LoadAll: the load failed (using the Default): ", ex.Message);
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
		this.SaveJson(field.Scope, field.Key, field.FormatJson(value));
	}

	/// <summary>
	/// The non-generic version that parses a raw representation (the form CLI/TOML/DB accept), turns it into a
	/// JSON literal and UPSERTs it. Used by <c>config set</c> (pgfsctl) to write from a non-generic
	/// <see cref="Field"/>.
	/// If the value cannot be parsed, <see cref="Field{T}.Parse"/> throws (the caller is expected to have validated it).
	/// </summary>
	public void SaveRaw(Field field, string raw) {
		this.SaveJson(field.Scope, field.Key, field.FormatJsonFromRaw(raw));
	}

	/// <summary>The shared body that UPSERTs one <c>(scope, key, jsonValue)</c> as a flat row.</summary>
	private void SaveJson(string scope, string key, string jsonValue) {
		var user = System.Environment.UserName.IsNotNullAndNotWhiteSpace()
			? System.Environment.UserName
			: "pgfs";
		// `updated_at` is generated on the client side and referenced through EXCLUDED.
		// Because pgfs_settings is registered in the metadata through citus_add_local_table_to_metadata in a Citus
		// environment, putting a function that is not IMMUTABLE (current_timestamp / now() and the like) into an
		// expression of the ON CONFLICT DO UPDATE SET clause is rejected with "functions used in the DO UPDATE SET
		// clause of INSERTs on distributed tables must be marked IMMUTABLE". EXCLUDED.* is a constant coming from
		// VALUES, so it gets around that.
		// The same SQL works as-is on a single PG, so no branching is needed.
		var now = Pg.UtcNow;
		var sql = $"""
			INSERT INTO {this.qualifiedTable} (scope, key, value, created_at, created_by, updated_at, updated_by)
			VALUES (@scope, @key, @value::jsonb, @now, @user, @now, @user)
			ON CONFLICT (scope, key) DO UPDATE SET
				value = EXCLUDED.value,
				updated_at = EXCLUDED.updated_at,
				updated_by = EXCLUDED.updated_by
			""";
		Pg.Execute(this.connectionString, sql, new {
			scope,
			key,
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
