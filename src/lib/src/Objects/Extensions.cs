using System.Runtime.CompilerServices;

namespace Pgfs.Lib.Objects;

public static class Extensions
{
	extension(object a)
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public T To<T>() {
			return (T)a;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public T? As<T>() {
			if (a is not T t) {
				return default;
			}
			return t;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool Is<T>() {
			return a is T;
		}
	}

	extension<T>(IEqualityComparer<T> a)
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool NotEquals(T x, T y) {
			return !a.Equals(x, y);
		}
	}
}
