namespace Pgfs.Lib.Utility;

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

/// <summary>
///
/// </summary>
public static class Json
{
#pragma warning disable IL3050 // Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.
#pragma warning disable IL2026 // Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code

	public static JsonSerializerOptions JsonSerializerOptions => new() {
		Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
	};

	public static string? ToJson<TValue>(TValue? o) {
		if (o == null) {
			return null;
		}
		return JsonSerializer.Serialize(o, JsonSerializerOptions);
	}

	public static string? ToJson(object? o, Type t) {
		if (o == null) {
			return null;
		}
		return JsonSerializer.Serialize(o, t, (JsonSerializerOptions?)JsonSerializerOptions);
	}

	public static TValue? FromJson<TValue>(string? s) {
		if (s == null) {
			return default;
		}
		return JsonSerializer.Deserialize<TValue>(s, JsonSerializerOptions);
	}

	public static object? FromJson(string? s, Type t) {
		if (s == null) {
			return null;
		}
		return JsonSerializer.Deserialize(s, t, (JsonSerializerOptions?)JsonSerializerOptions);
	}

#pragma warning restore IL2026 // Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code
#pragma warning restore IL3050 // Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.
}

/// <summary>
///
/// </summary>
[JsonSerializable(typeof(string[]))]
internal partial class StringArrayJsonSerializerContext : JsonSerializerContext;

/// <summary>
///
/// </summary>
[JsonSerializable(typeof(long))]
internal partial class IntegerJsonSerializerContext : JsonSerializerContext;
