using System.Collections;

namespace Pgfs.Lib.Collections;

public class ComparerToEqualityComparer<T> : IEqualityComparer<T>
{
	private readonly IComparer<T> comparer;

	public ComparerToEqualityComparer() : this(Comparer<T>.Default) { }

	public ComparerToEqualityComparer(IComparer<T> comparer) {
		this.comparer = comparer;
	}

	public bool Equals(T? x, T? y) {
		return this.comparer.Compare(x, y) == 0;
	}

	public int GetHashCode(T obj) {
		if (obj == null) {
			return 0;
		}
		return obj.GetHashCode();
	}
}

public class ComparerToEqualityComparer : ComparerToEqualityComparer<object>, IEqualityComparer
{
	public static ComparerToEqualityComparer<T> Create<T>() {
		return new ComparerToEqualityComparer<T>();
	}

	public static ComparerToEqualityComparer<T> Create<T>(IComparer<T> comparer) {
		return new ComparerToEqualityComparer<T>(comparer);
	}

	private readonly IComparer comparer;

	public ComparerToEqualityComparer() : this(Comparer.Default) { }

	public ComparerToEqualityComparer(IComparer comparer) {
		this.comparer = comparer;
	}
}
