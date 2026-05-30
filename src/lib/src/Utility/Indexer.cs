namespace Pgfs.Lib.Utility;

public readonly struct ReadOnlyIndexer<TKey, TValue>(Func<TKey, TValue> getter)
{
	public TValue this[TKey key] => getter(key);
}

public readonly struct ReadOnlyIndexer<TKey1, TKey2, TValue>(Func<TKey1, TKey2, TValue> getter)
{
	public TValue this[TKey1 key1, TKey2 key2] => getter(key1, key2);
}

public readonly struct Indexer<TKey, TValue>(Func<TKey, TValue> getter, Action<TKey, TValue> setter)
{
	public TValue this[TKey index] {
		get => getter(index);
		set => setter(index, value);
	}
}
