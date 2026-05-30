namespace Pgfs.Lib.Utility;

using System.Collections;
using System.Diagnostics.CodeAnalysis;

public static class Fn
{
	public readonly struct InResult<T>(int index, T value, bool ok)
	{
		public int Index { get; init; } = index;

		public T? Value { get; init; } = value;

		[MemberNotNullWhen(true, nameof(InResult<T>.Value))]
		public bool Ok { get; init; } = ok;
	}

	public static InResult<T?> In<T>(this T a, params IEnumerable<T> b) {
		using var c = b.GetEnumerator();
		for (var index = 0; c.MoveNext(); ++index) {
			var value = c.Current;
			if (Equals(a, value)) {
				return new(index, value, true);
			}
		}
		return new(-1, default, false);
	}

	public static bool IsEmpty(this IEnumerable? items) {
		if (items == null) {
			return true;
		}
		foreach (var _ in items) {
			return false;
		}
		return true;
	}

	public static bool IsEmpty(this ICollection? items) {
		if (items == null) {
			return true;
		}
		return items.Count == 0;
	}
	public static bool IsNotEmpty(this ICollection? items) {
		return !items.IsEmpty();
	}

	public static (TValue? value, IEnumerable<TValue> enumerable, bool ok) Pop<TValue>(this IEnumerable<TValue>? enumerable) {
		if (enumerable == null) {
			return (default, [], false);
		}

		using var enumerator = enumerable.GetEnumerator();
		if (!enumerator.MoveNext()) {
			return (default, [], false);
		}

		return (enumerator.Current, enumerator.Collect(), true);
	}

	public static IEnumerable<TValue> Collect<TValue>(this IEnumerator<TValue>? enumerator) {
		if (enumerator == null) {
			yield break;
		}
		while (enumerator.MoveNext()) {
			yield return enumerator.Current;
		}
	}

	public static bool NotContains<TValue>(this IEnumerable<TValue>? enumerator, TValue value) {
		if (enumerator == null) {
			return true;
		}
		return !enumerator.Contains(value);
	}

	public static T? Get<T>(this T[]? array, int index) => array != null && index > 0 && index <= array.Length ? array[index] : default;
	public static T Get<T>(this T[]? array, int index, T @default) => array != null && index > 0 && index <= array.Length ? array[index] : @default;

	public static T? First<T>(this T[]? array) => array != null && array.Length != 0 ? array[0] : default;
	public static T First<T>(this T[]? array, T @default) => array != null && array.Length != 0 ? array[0] : @default;

	public static T? Last<T>(this T[]? array) => array != null && array.Length != 0 ? array[^1] : default;
	public static T Last<T>(this T[]? array, T @default) => array != null && array.Length != 0 ? array[^1] : @default;
}
