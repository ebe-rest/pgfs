namespace Pgfs.Ctl;

using System.Collections.Generic;
using System.Text.Json;
using Core.Config;

/// <summary>
/// The CLI front end of the <c>config</c> subcommand. A thin layer that delegates <c>list</c> / <c>get</c> /
/// <c>set</c> to <see cref="ConfigAdmin"/> (Core) and prints them as text or with <c>--json</c>.
///
/// <para>
/// **The argument grammar**: the positionals are pinned right after the verb (= so they cannot mix with the
/// values of the connection options).
/// <c>config list [opts]</c> / <c>config get &lt;key&gt; [opts]</c> / <c>config set &lt;key&gt; &lt;value&gt; [opts]</c>.
/// The connection options are resolved from CLI/TOML with <see cref="ConfigLoader"/> (store=null), as in mount.pgfs.
/// </para>
/// </summary>
public static class ConfigCommand
{
	public static int Run(string[] rest) {
		if (rest.Length == 0) {
			Console.Error.WriteLine("config requires a verb: list | get | set");
			return 1;
		}
		var verb = rest[0];
		switch (verb) {
			case "list":
				return RunList(rest[1..]);
			case "get": {
				if (rest.Length < 2) {
					Console.Error.WriteLine("config get requires <scope.key>");
					return 1;
				}
				return RunGet(rest[1], rest[2..]);
			}
			case "set": {
				if (rest.Length < 3) {
					Console.Error.WriteLine("config set requires <scope.key> <value>");
					return 1;
				}
				return RunSet(rest[1], rest[2], rest[3..]);
			}
		}
		Console.Error.WriteLine($"unknown config verb '{verb}' (expected list | get | set)");
		return 1;
	}

	private static int RunList(string[] opts) {
		var (json, admin, loader) = Build(opts);
		var items = admin.List(loader);
		if (json) {
			WriteJsonArray(items);
			return 0;
		}
		foreach (var item in items) {
			Console.WriteLine(FormatLine(item));
		}
		return 0;
	}

	private static int RunGet(string key, string[] opts) {
		var (json, admin, loader) = Build(opts);
		var item = admin.Get(key, loader);
		if (item == null) {
			Console.Error.WriteLine($"unknown key '{key}'");
			return 1;
		}
		if (json) {
			Console.WriteLine(JsonSerializer.Serialize(ToJsonObject(item), JsonOpts));
			return 0;
		}
		Console.WriteLine(FormatLine(item));
		return 0;
	}

	private static int RunSet(string key, string value, string[] opts) {
		var (_, admin, _) = Build(opts);
		var result = admin.Set(key, value);
		if (result.Ok) {
			Console.WriteLine($"{key} = {value}: {result.Message}");
			return 0;
		}
		Console.Error.WriteLine($"{key}: {result.Message}");
		return 1;
	}

	/// <summary>Builds a <see cref="ConfigAdmin"/> from the connection options (resolving the connection is delegated to <see cref="CliUtil.Resolve"/>).</summary>
	private static (bool Json, ConfigAdmin Admin, ConfigLoader Loader) Build(string[] opts) {
		var (json, loader, conn, schema, prefix) = CliUtil.Resolve(opts);
		return (json, new ConfigAdmin(conn, schema, prefix), loader);
	}

	private static string FormatLine(ConfigItem item) {
		return $"{item.FullKey} = {item.Value}  [{item.Source}, save={item.SaveTo}, reload={item.Reload}]";
	}

	private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

	private static void WriteJsonArray(IReadOnlyList<ConfigItem> items) {
		var arr = new List<Dictionary<string, string>>();
		foreach (var item in items) {
			arr.Add(ToJsonObject(item));
		}
		Console.WriteLine(JsonSerializer.Serialize(arr, JsonOpts));
	}

	private static Dictionary<string, string> ToJsonObject(ConfigItem item) {
		return new Dictionary<string, string> {
			["key"] = item.FullKey,
			["value"] = item.Value,
			["source"] = item.Source,
			["save"] = item.SaveTo.ToString(),
			["reload"] = item.Reload.ToString(),
			["comment"] = item.Comment,
		};
	}
}
