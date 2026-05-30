namespace Pgfs.Lib.Collections;

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

public class RichDictionary<K, V> : IDictionary<K, V>, IDictionary, IReadOnlyDictionary<K, V>, ISerializable, IDeserializationCallback where K : notnull
{
	private readonly Dictionary<K, V> items;


	public RichDictionary() {
		this.items = new();
	}
	public RichDictionary(IDictionary<K, V> dictionary) {
		this.items = new(dictionary);
	}
	public RichDictionary(IDictionary<K, V> dictionary, IEqualityComparer<K>? comparer) {
		this.items = new(dictionary, comparer);
	}
	public RichDictionary(IEnumerable<KeyValuePair<K, V>> collection) {
		this.items = new(collection);
	}
	public RichDictionary(IEnumerable<KeyValuePair<K, V>> collection, IEqualityComparer<K>? comparer) {
		this.items = new(collection, comparer);
	}
	public RichDictionary(IEqualityComparer<K>? comparer) {
		this.items = new(comparer);
	}
	public RichDictionary(int capacity) {
		this.items = new(capacity);
	}
	public RichDictionary(int capacity, IEqualityComparer<K>? comparer) {
		this.items = new(capacity, comparer);
	}


	public class AddedEventArgs : EventArgs
	{
		public required K Key { get; init; }
		public required V Value { get; init; }
	}

	protected virtual void OnAdded(AddedEventArgs e) {
		if (this.Added != null) {
			this.Added(this, e);
		}
	}

	public event EventHandler<AddedEventArgs>? Added;


	public virtual Dictionary<K, V>.Enumerator GetEnumerator() {
		return this.items.GetEnumerator();
	}
	IEnumerator<KeyValuePair<K, V>> IEnumerable<KeyValuePair<K, V>>.GetEnumerator() {
		return ((IEnumerable<KeyValuePair<K, V>>)this.items).GetEnumerator();
	}
	IDictionaryEnumerator IDictionary.GetEnumerator() {
		return ((IDictionary)this.items).GetEnumerator();
	}
	IEnumerator IEnumerable.GetEnumerator() {
		return ((IEnumerable)this.items).GetEnumerator();
	}


	public virtual int Capacity {
		get {
			return this.items.Capacity;
		}
	}

	public virtual int EnsureCapacity(int capacity) => this.items.EnsureCapacity(capacity);

	public virtual void TrimExcess() { this.items.TrimExcess(); }
	public virtual void TrimExcess(int capacity) { this.items.TrimExcess(capacity); }

	public virtual int Count {
		get {
			return this.items.Count;
		}
	}


	public virtual bool ContainsKey(K key) => this.items.ContainsKey(key);
	public virtual bool ContainsValue(V value) => this.items.ContainsValue(value);
	bool ICollection<KeyValuePair<K, V>>.Contains(KeyValuePair<K, V> item) {
		return ((ICollection<KeyValuePair<K, V>>)this.items).Contains(item);
	}
	bool IDictionary.Contains(object key) {
		return ((IDictionary)this.items).Contains(key);
	}


	public V? this[K key] {
		get {
			return this.GetValueOrDefault(key);
		}
		set {
			this.AddOrReplaceOrRemove(key, value);
		}
	}
	V IReadOnlyDictionary<K, V>.this[K key] {
		get {
			return ((IReadOnlyDictionary<K, V>)this.items)[key];
		}
	}
	V IDictionary<K, V>.this[K key] {
		get {
			return ((IDictionary<K, V>)this.items)[key];
		}
		set {
			((IDictionary<K, V>)this.items)[key] = value;
		}
	}
	object? IDictionary.this[object key] {
		get {
			return ((IDictionary)this.items)[key];
		}
		set {
			((IDictionary)this.items)[key] = value;
		}
	}


	public virtual V? GetValueOrDefault(K key) {
		if (!this.items.TryGetValue(key, out V? value)) {
			return default;
		}
		return value;
	}
	public virtual V? GetValueOrDefault(K key, V? defaultValue) {
		if (!this.items.TryGetValue(key, out V? value)) {
			return defaultValue;
		}
		return value;
	}
	public virtual V? GetValueOrDefault(K key, Func<V?> defaultSupplier) {
		if (!this.items.TryGetValue(key, out V? value)) {
			return defaultSupplier();
		}
		return value;
	}

	public virtual bool TryGetValue(K key, [MaybeNullWhen(false)] out V value) {
		return this.items.TryGetValue(key, out value);
	}
	bool IReadOnlyDictionary<K, V>.TryGetValue(K key, [MaybeNullWhen(false)] out V value) {
		return ((IReadOnlyDictionary<K, V>)this.items).TryGetValue(key, out value);
	}

	void IDictionary<K, V>.Add(K key, V value) {
		((IDictionary<K, V>)this.items).Add(key, value);
		this.OnAdded(new AddedEventArgs { Key = key, Value = value });
	}
	void ICollection<KeyValuePair<K, V>>.Add(KeyValuePair<K, V> item) {
		((ICollection<KeyValuePair<K, V>>)this.items).Add(item);
		this.OnAdded(new AddedEventArgs { Key = item.Key, Value = item.Value });
	}
	void IDictionary.Add(object key, object? value) {
		if (key is not K k || value is not V v) {
			throw new ArrayTypeMismatchException();
		}
		((IDictionary)this.items).Add(k, v);
		this.OnAdded(new AddedEventArgs { Key = k, Value = v });
	}

	public virtual bool TryAdd(K key, V value) {
		if (!this.items.TryAdd(key, value)) {
			return false;
		}
		this.OnAdded(new AddedEventArgs { Key = key, Value = value });
		return true;
	}

	public virtual ref V? GetValueRefOrAddDefault(K key) {
		// the Add event cannot be raised here
		return ref CollectionsMarshal.GetValueRefOrAddDefault(this.items, key, out _);
	}
	public virtual ref V? GetValueRefOrAddDefault(K key, out bool exists) {
		// the Add event cannot be raised here
		return ref CollectionsMarshal.GetValueRefOrAddDefault(this.items, key, out exists);
	}
	public virtual void AddOrReplaceOrRemove(K key, V? value) {
		if (value == null) {
			this.items.Remove(key);
			return;
		}
		ref var mine = ref CollectionsMarshal.GetValueRefOrAddDefault(this.items, key, out bool exists);
		mine = value;
		if (exists) {
			return;
		}
		this.OnAdded(new AddedEventArgs { Key = key, Value = value });
	}

	public virtual V GetOrAdd(K key, V value) {
		ref var mine = ref this.GetValueRefOrAddDefault(key, out bool exists);
		if (exists) {
			if (mine != null) {
				return mine;
			}
			mine = value;
			return mine;
		}
		mine = value;
		this.OnAdded(new AddedEventArgs { Key = key, Value = mine });
		return mine;
	}
	public virtual V GetOrAdd(K key, Func<V> valueFactory) {
		ref var mine = ref CollectionsMarshal.GetValueRefOrAddDefault(this.items, key, out bool exists);
		if (exists) {
			if (mine != null) {
				return mine;
			}
			mine = valueFactory();
			return mine;
		}
		mine = valueFactory();
		this.OnAdded(new AddedEventArgs { Key = key, Value = mine });
		return mine;
	}
	public virtual V GetOrAdd(K key, Func<K, V> valueFactory) {
		ref var mine = ref CollectionsMarshal.GetValueRefOrAddDefault(this.items, key, out bool exists);
		if (exists) {
			if (mine != null) {
				return mine;
			}
			mine = valueFactory(key);
			return mine;
		}
		mine = valueFactory(key);
		this.OnAdded(new AddedEventArgs { Key = key, Value = mine });
		return mine;
	}
	public virtual W GetOrAdd<W>(K key, W value) where W : V {
		ref var mine = ref this.GetValueRefOrAddDefault(key, out bool exists);
		if (exists) {
			if (mine is W m) {
				return m;
			}
			mine = value;
			return value;
		}
		mine = value;
		this.OnAdded(new AddedEventArgs { Key = key, Value = mine });
		return value;
	}
	public virtual W GetOrAdd<W>(K key, Func<W> valueFactory) where W : V {
		ref var mine = ref CollectionsMarshal.GetValueRefOrAddDefault(this.items, key, out bool exists);
		if (exists) {
			if (mine is W m) {
				return m;
			}
			var v = valueFactory();
			mine = v;
			return v;
		}
		var va = valueFactory();
		mine = va;
		this.OnAdded(new AddedEventArgs { Key = key, Value = mine });
		return va;
	}
	public virtual W GetOrAdd<W>(K key, Func<K, W> valueFactory) where W : V {
		ref var mine = ref CollectionsMarshal.GetValueRefOrAddDefault(this.items, key, out bool exists);
		if (exists) {
			if (mine is W m) {
				return m;
			}
			var v = valueFactory(key);
			mine = v;
			return v;
		}
		var va = valueFactory(key);
		mine = va;
		this.OnAdded(new AddedEventArgs { Key = key, Value = mine });
		return va;
	}


	ICollection IDictionary.Keys {
		get {
			return ((IDictionary)this.items).Keys;
		}
	}
	ICollection<K> IDictionary<K, V>.Keys {
		get {
			return ((IDictionary<K, V>)this.items).Keys;
		}
	}
	IEnumerable<K> IReadOnlyDictionary<K, V>.Keys {
		get {
			return ((IReadOnlyDictionary<K, V>)this.items).Keys;
		}
	}
	public virtual Dictionary<K, V>.KeyCollection Keys {
		get {
			return this.items.Keys;
		}
	}

	ICollection IDictionary.Values { get { return ((IDictionary)this.items).Values; } }
	ICollection<V> IDictionary<K, V>.Values { get { return ((IDictionary<K, V>)this.items).Values; } }
	IEnumerable<V> IReadOnlyDictionary<K, V>.Values { get { return ((IReadOnlyDictionary<K, V>)this.items).Values; } }
	public virtual Dictionary<K, V>.ValueCollection Values { get { return this.items.Values; } }


	public virtual bool Remove(K key) {
		return this.items.Remove(key);
	}
	public virtual bool Remove(K key, [MaybeNullWhen(false)] out V value) {
		return this.items.Remove(key, out value);
	}
	bool ICollection<KeyValuePair<K, V>>.Remove(KeyValuePair<K, V> item) {
		return ((ICollection<KeyValuePair<K, V>>)this.items).Remove(item);
	}
	void IDictionary.Remove(object key) {
		((IDictionary)this.items).Remove(key);
	}

	public virtual void Clear() {
		this.items.Clear();
	}


	public virtual Dictionary<K, V>.AlternateLookup<TAlternateKey> GetAlternateLookup<TAlternateKey>() where TAlternateKey : notnull, allows ref struct => this.items.GetAlternateLookup<TAlternateKey>();

	public virtual bool TryGetAlternateLookup<TAlternateKey>(out Dictionary<K, V>.AlternateLookup<TAlternateKey> lookup) where TAlternateKey : notnull, allows ref struct => this.items.TryGetAlternateLookup(out lookup);


	public virtual void OnDeserialization(object? sender) { this.items.OnDeserialization(sender); }


	public virtual IEqualityComparer<K> Comparer {
		get {
			return this.items.Comparer;
		}
	}


	void ICollection.CopyTo(Array array, int index) {
		((ICollection)this.items).CopyTo(array, index);
	}
	void ICollection<KeyValuePair<K, V>>.CopyTo(KeyValuePair<K, V>[] array, int arrayIndex) {
		((ICollection<KeyValuePair<K, V>>)this.items).CopyTo(array, arrayIndex);
	}
	bool ICollection.IsSynchronized {
		get { return ((ICollection)this.items).IsSynchronized; }
	}
	object ICollection.SyncRoot {
		get { return ((ICollection)this.items).SyncRoot; }
	}
	bool IDictionary.IsFixedSize {
		get {
			return ((IDictionary)this.items).IsFixedSize;
		}
	}
	bool IDictionary.IsReadOnly {
		get {
			return ((IDictionary)this.items).IsReadOnly;
		}
	}
	bool ICollection<KeyValuePair<K, V>>.IsReadOnly {
		get {
			return ((ICollection<KeyValuePair<K, V>>)this.items).IsReadOnly;
		}
	}
	[Obsolete("Obsolete")]
	void ISerializable.GetObjectData(SerializationInfo info, StreamingContext context) {
		((ISerializable)this.items).GetObjectData(info, context);
	}
}
