using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Pgfs.Lib.Collections;

/// <remarks>
/// <see cref="System.Collections.IEnumerable"/>
/// <see cref="System.Collections.Generic.IEnumerable{T}"/>
/// <see cref="System.Collections.ICollection"/>
/// <see cref="System.Collections.Generic.ICollection{T}"/>
/// <see cref="System.Collections.IList"/>
/// <see cref="System.Collections.Generic.IList{T}"/>
/// <see cref="System.Collections.Generic.HashSet{T}"/>
/// <see cref="System.Collections.Generic.List{T}"/>
/// </remarks>
public class FirstList<T> :
	IEnumerable<T>, IEnumerable,
	IReadOnlyCollection<T>, ICollection<T>, ICollection,
	IReadOnlyList<T>, IList<T>, IList
{
	internal protected readonly IEqualityComparer<T> equalityComparer;
	internal protected readonly IComparer<T> comparer;
	internal protected readonly LinkedList<Memory<T>> memories;
	internal protected int count;
	internal protected (LinkedListNode<Memory<T>>? node, int nodeStart) cacheForFindSegmentNode;

	#region constructors

	public FirstList() {
		this.comparer = Comparer<T>.Default;
		this.equalityComparer = EqualityComparer<T>.Default;
		this.memories = new();
		this.count = 0;
		this.cacheForFindSegmentNode = default;
	}

	public FirstList(params IEnumerable<T> items) {
		this.comparer = Comparer<T>.Default;
		this.equalityComparer = EqualityComparer<T>.Default;
		this.memories = MakeSegments(items);
		this.count = CountMemories(this.memories);
		this.cacheForFindSegmentNode = default;
	}

	public FirstList(IEnumerable<T> items, IComparer<T> comparer) {
		this.comparer = comparer;
		this.equalityComparer = ComparerToEqualityComparer.Create(comparer);
		this.memories = MakeSegments(items);
		this.count = CountMemories(this.memories);
		this.cacheForFindSegmentNode = default;
	}

	public FirstList(IEnumerable<T> items, IComparer<T> comparer, IEqualityComparer<T> equalityComparer) {
		this.comparer = comparer;
		this.equalityComparer = equalityComparer;
		this.memories = MakeSegments(items);
		this.count = CountMemories(this.memories);
		this.cacheForFindSegmentNode = default;
	}

	// ---

	public static FirstList<T> Flat(params IEnumerable<IEnumerable<T>> segments) {
		return new FirstList<T>(segments);
	}

	protected FirstList(params IEnumerable<IEnumerable<T>> segments) {
		this.comparer = Comparer<T>.Default;
		this.equalityComparer = EqualityComparer<T>.Default;
		this.memories = MakeSegments(segments);
		this.count = CountMemories(this.memories);
		this.cacheForFindSegmentNode = default;
	}

	public static FirstList<T> Flat(IEnumerable<IEnumerable<T>> segments, IComparer<T> comparer) {
		return new FirstList<T>(segments, comparer);
	}

	protected FirstList(IEnumerable<IEnumerable<T>> segments, IComparer<T> comparer) {
		this.comparer = comparer;
		this.equalityComparer = ComparerToEqualityComparer.Create(comparer);
		this.memories = MakeSegments(segments);
		this.count = CountMemories(this.memories);
		this.cacheForFindSegmentNode = default;
	}

	public static FirstList<T> Flat(IEnumerable<IEnumerable<T>> segments, IComparer<T> comparer, IEqualityComparer<T> equalityComparer) {
		return new FirstList<T>(segments, comparer, equalityComparer);
	}

	protected FirstList(IEnumerable<IEnumerable<T>> segments, IComparer<T> comparer, IEqualityComparer<T> equalityComparer) {
		this.comparer = comparer;
		this.equalityComparer = equalityComparer;
		this.memories = MakeSegments(segments);
		this.count = CountMemories(this.memories);
		this.cacheForFindSegmentNode = default;
	}

	// ---

	public FirstList(IEnumerable items) {
		this.comparer = Comparer<T>.Default;
		this.equalityComparer = EqualityComparer<T>.Default;
		this.memories = MakeSegments(items);
		this.count = CountMemories(this.memories);
		this.cacheForFindSegmentNode = default;
	}

	public FirstList(IEnumerable items, IComparer<T> comparer) {
		this.comparer = comparer;
		this.equalityComparer = ComparerToEqualityComparer.Create(comparer);
		this.memories = MakeSegments(items);
		this.count = CountMemories(this.memories);
		this.cacheForFindSegmentNode = default;
	}

	public FirstList(IEnumerable items, IComparer<T> comparer, IEqualityComparer<T> equalityComparer) {
		this.comparer = comparer;
		this.equalityComparer = equalityComparer;
		this.memories = MakeSegments(items);
		this.count = CountMemories(this.memories);
		this.cacheForFindSegmentNode = default;
	}

	// ---

	protected static LinkedList<Memory<T>> MakeSegments(object arg) {
		switch (arg) {
		case LinkedList<Memory<T>> s:
			return s;
		case IEnumerable<Memory<T>> s:
			return new LinkedList<Memory<T>>(s);
		case IEnumerable<IEnumerable<T>> s:
			return new LinkedList<Memory<T>>(s.Select(t => t.ToArray().AsMemory()));
		case Memory<T> s:
			return new LinkedList<Memory<T>>([s]);
		case IEnumerable<T> s:
			return new LinkedList<Memory<T>>([s.ToArray().AsMemory()]);
		case T s:
			return new LinkedList<Memory<T>>([new T[] { s }.AsMemory()]);
		case IEnumerable s:
			var r = new LinkedList<Memory<T>>();
			foreach (var t in s) {
				switch (t) {
				case Memory<T> u:
					r.AddLast(u);
					continue;
				case IEnumerable<T> u:
					r.AddLast(u.ToArray().AsMemory());
					continue;
				case T u:
					r.AddLast(new T[] { u }.AsMemory());
					continue;
				case IEnumerable u:
					foreach (var v in u) {
						switch (v) {
						case T w:
							r.AddLast(new T[] { w }.AsMemory());
							continue;
						default:
							if (v == null) {
								throw new ArgumentException("invalid type (null)");
							}
							throw new ArgumentException($"invalid type ({v.GetType().FullName})");
						}
					}
					continue;
				default:
					if (t == null) {
						throw new ArgumentException("invalid type (null)");
					}
					throw new ArgumentException($"invalid type ({t.GetType().FullName})");
				}
			}
			return r;
		default:
			if (arg == null) {
				throw new ArgumentException("invalid type (null)");
			}
			throw new ArgumentException($"invalid type ({arg.GetType().FullName})");
		}
	}

	protected static int CountMemories(LinkedList<Memory<T>> memories) {
		return memories.Sum(memory => memory.Length);
	}

	#endregion constructors

	#region Count

	/// <see cref="System.Collections.Generic.ICollection{T}.Count"/>
	/// <see cref="System.Collections.ICollection.Count"/>
	public int Count {
		get {
			return this.count;
		}
	}

	public bool IsEmpty() {
		return this.count == 0;
	}

	public bool IsNotEmpty() {
		return this.count != 0;
	}

	#endregion

	#region GetEnumerator

	/// <see cref="System.Collections.IEnumerable.GetEnumerator()"/>
	public IEnumerator<T> GetEnumerator() {
		return new Enumerator(this);
	}

	/// <summary>
	/// <see cref="Pgfs.Lib.Collections.FirstList{T}.Enumerator"/> is an <see cref="System.Collections.Generic.IEnumerable{T}"/> for fast traversal of <see cref="System.Memory{T}"/> segments.
	/// By temporarily taking a <see cref="System.Span{T}"/> inside <see cref="Pgfs.Lib.Collections.FirstList{T}.Enumerator.MoveNext"/>, it works around the <c>ref struct</c> restriction while staying fast.
	/// </summary>
	protected class Enumerator : IEnumerator<T>
	{
		protected readonly FirstList<T> parent;
		protected LinkedListNode<Memory<T>>? currentNode;
		protected int memoryIndex;
		protected T? current;

		public Enumerator(FirstList<T> parent) {
			this.parent = parent;
			this.currentNode = parent.memories.First;
			this.memoryIndex = -1;
			this.current = default;
		}

		public bool MoveNext() {
			if (this.currentNode == null) {
				return false;
			}

			++this.memoryIndex;

			var span = this.currentNode.Value.Span;
			if (this.memoryIndex < span.Length) {
				this.current = span[this.memoryIndex];
				return true;
			}

			this.currentNode = this.currentNode.Next;
			this.memoryIndex = -1;
			if (this.currentNode != null) {
				return true;
			}

			return false;
		}

		public T Current {
			get {
				return this.current!;
			}
		}

		object? IEnumerator.Current {
			get {
				return Current;
			}
		}

		public void Reset() {
			this.currentNode = this.parent.memories.First;
			this.memoryIndex = -1;
			this.current = default;
		}

		public void Dispose() { }
	}

	/// <see cref="System.Collections.Generic.IEnumerable{T}.GetEnumerator()"/>
	IEnumerator IEnumerable.GetEnumerator() {
		return this.GetEnumerator();
	}

	#endregion

	#region IndexOf, LastIndexOf, Contains

	/// <see cref="System.Collections.Generic.IList{T}.IndexOf"/>
	public int IndexOf(T item) {
		int baseIndex = 0;
		foreach (var m in this.memories) {
			var span = m.Span;
			var length = span.Length;
			for (int i = 0; i < length; ++i) {
				if (this.equalityComparer.Equals(span[i], item)) {
					return baseIndex + i;
				}
			}
			baseIndex += span.Length;
		}
		return -1;
	}

	public int IndexOf(Predicate<T> match) {
		if (this.count == 0) {
			return -1;
		}

		var baseIndex = 0;
		foreach (var m in this.memories) {
			var span = m.Span;
			var length = span.Length;
			for (int i = 0; i < length; ++i) {
				if (match(span[i])) {
					return baseIndex + i;
				}
			}
			baseIndex += span.Length;
		}

		return -1;
	}

	/// <see cref="System.Collections.IList.IndexOf"/>
	int IList.IndexOf(object? value) {
		if (value == null) {
			if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) == null) {
				return -1;
			}

			return this.IndexOf(default(T)!);
		}

		if (value is not T item) {
			return -1;
		}

		return this.IndexOf(item);
	}

	// ---

	public int LastIndexOf(T item) {
		var offset = this.count;
		for (var m = this.memories.Last; m != null; m = m.Previous) {
			var span = m.Value.Span;
			offset -= span.Length;
			var index = span.LastIndexOf(item, this.equalityComparer);
			if (index < 0) {
				continue;
			}
			return offset + index;
		}
		return -1;
	}

	public int LastIndexOf(Predicate<T> match) {
		var offset = this.count;
		for (var m = this.memories.Last; m != null; m = m.Previous) {
			var span = m.Value.Span;
			var length = span.Length;
			offset -= length;
			for (int index = length - 1; index >= 0; --index) {
				if (match(span[index])) {
					return offset + index;
				}
			}
		}
		return -1;
	}

	// ---

	/// <see cref="System.Collections.Generic.ICollection{T}.Contains"/>
	public bool Contains(T item) {
		return this.IndexOf(item) != -1;
	}

	public bool Contains(Predicate<T> match) {
		return this.IndexOf(match) != -1;
	}

	/// <see cref="System.Collections.IList.Contains"/>
	bool IList.Contains(object? value) {
		return ((IList)this).IndexOf(value) != -1;
	}

	#endregion IndexOf, Contains

	#region this

	/// <see cref="System.Collections.Generic.IList{T}.this"/>
	public T this[int index] {
		get {
			var (node, offset) = FindSegmentNode(index);
			return node.Value.Span[offset];
		}
		set {
			var (node, offset) = FindSegmentNode(index);
			node.Value.Span[offset] = value;
		}
	}

	/// <see cref="System.Collections.IList.this"/>
	object? IList.this[int index] {
		get {
			return this[index];
		}
		set {
			this[index] = (T)value!;
		}
	}

	// ---

	protected (LinkedListNode<Memory<T>> node, int subIndex) FindSegmentNode(int index) {
		if (index < 0 || index >= this.count) {
			throw new IndexOutOfRangeException();
		}

		var (node, nodeStart) = this.cacheForFindSegmentNode;

		var hasCache = node != null;
		if (hasCache && index >= nodeStart && index < nodeStart + node!.Value.Length) {
			return (node, index - nodeStart);
		}

		var distanceFromEnd = (this.count - 1) - index;
		var minDistance = hasCache switch {
			true  => Math.Min(index, Math.Min(distanceFromEnd, Math.Abs(index - nodeStart))),
			false => Math.Min(index, distanceFromEnd),
		};

		// minDistance == index           : advance from the head
		// minDistance == distanceFromEnd : step back from the tail
		// otherwise                      : move from the cached position toward index
		(node, nodeStart, var forward) = (minDistance == index, minDistance == distanceFromEnd) switch {
			(true, _) => (this.memories.First, 0, true),
			(_, true) => (this.memories.Last, this.count, false),
			_         => (node, nodeStart, index >= nodeStart),
		};

		return forward switch {
			true  => this.WalkForward(node, nodeStart, index),
			false => this.WalkBackward(node, nodeStart, index),
		};
	}

	private (LinkedListNode<Memory<T>> node, int subIndex) WalkForward(
		LinkedListNode<Memory<T>>? node, int nodeStart, int index
	) {
		while (node != null) {
			var length = node.Value.Length;
			if (index < nodeStart + length) {
				this.cacheForFindSegmentNode = (node, nodeStart);
				return (node, index - nodeStart);
			}
			nodeStart += length;
			node = node.Next;
		}
		throw new IndexOutOfRangeException();
	}

	private (LinkedListNode<Memory<T>> node, int subIndex) WalkBackward(
		LinkedListNode<Memory<T>>? node, int nodeStart, int index
	) {
		while (node != null) {
			nodeStart -= node.Value.Length;
			if (index >= nodeStart) {
				this.cacheForFindSegmentNode = (node, nodeStart);
				return (node, index - nodeStart);
			}
			node = node.Previous;
		}
		throw new IndexOutOfRangeException();
	}

	#endregion

	#region Insert, Add

	public void InsertRange(int index, Memory<T> items) {
		if (index < 0 || index > count) {
			throw new ArgumentOutOfRangeException(nameof(index));
		}

		var itemsLength = items.Length;
		if (itemsLength == 0) {
			return;
		}

		if (index == 0) {
			InsertNodeFirst(null, this.memories.First);
			return;
		}

		if (index == this.count) {
			InsertNodeLast(this.memories.Last, null);
			return;
		}

		var nodeStart = 0;
		var node = this.memories.First;
		while (node != null) {
			var memory = node.Value;
			var nodeLength = memory.Length;
			var nextNodeStart = nodeStart + nodeLength;
			try {
				if (index < nodeStart || index > nextNodeStart) {
					continue;
				}

				if (index == nodeStart) {
					InsertNodeFirst(node.Previous, node);
					return;
				}

				if (index == nextNodeStart) {
					InsertNodeLast(node, node.Next);
					return;
				}

				var subIndex = index - nodeStart;
				var left = memory[..subIndex];
				var right = memory[subIndex..];
				this.memories.AddBefore(node, left);
				this.memories.AddAfter(node, right);
				node.Value = items;
				this.count += itemsLength;
				this.cacheForFindSegmentNode = default;
				return;
			} finally {
				node = node.Next;
				nodeStart = nextNodeStart;
			}
		}
		return;

		void InsertNodeFirst(LinkedListNode<Memory<T>>? prev, LinkedListNode<Memory<T>>? node) {
			if (TryPrependInPlace(node)) { return; }

			if (prev != null) {
				this.memories.AddAfter(prev, items);
				this.count += itemsLength;
				this.cacheForFindSegmentNode = default;
				return;
			}

			this.memories.AddFirst(items);
			this.count += itemsLength;
			this.cacheForFindSegmentNode = default;
		}

		bool TryPrependInPlace(LinkedListNode<Memory<T>>? node) {
			if (node == null) { return false; }
			var memory = node.Value;
			if (!MemoryMarshal.TryGetArray<T>(memory, out var segment)) { return false; }

			var array = segment.Array!;
			var arrayOffset = segment.Offset;
			var arrayCount = segment.Count;
			var newArrayFirst = arrayOffset - itemsLength;
			if (newArrayFirst < 0) { return false; }

			memory = array.AsMemory(newArrayFirst, arrayCount + itemsLength);
			items.Span.CopyTo(memory.Span[..itemsLength]);
			node.Value = memory;
			this.count += itemsLength;
			this.cacheForFindSegmentNode = default;
			return true;
		}

		void InsertNodeLast(LinkedListNode<Memory<T>>? node, LinkedListNode<Memory<T>>? next) {
			if (TryAppendInPlace(node)) { return; }

			if (next != null) {
				this.memories.AddBefore(next, items);
				this.count += itemsLength;
				this.cacheForFindSegmentNode = default;
				return;
			}

			this.memories.AddLast(items);
			this.count += itemsLength;
			this.cacheForFindSegmentNode = default;
		}

		bool TryAppendInPlace(LinkedListNode<Memory<T>>? node) {
			if (node == null) { return false; }
			var memory = node.Value;
			if (!MemoryMarshal.TryGetArray<T>(memory, out var segment)) { return false; }

			var array = segment.Array!;
			var arrayOffset = segment.Offset;
			var arrayCount = segment.Count;
			var arrayLength = array.Length;
			var newArrayLast = arrayOffset + arrayCount + itemsLength;
			if (newArrayLast > arrayLength) { return false; }

			memory = array.AsMemory(arrayOffset, arrayCount + itemsLength);
			items.Span.CopyTo(memory.Span[arrayCount..newArrayLast]);
			node.Value = memory;
			this.count += itemsLength;
			this.cacheForFindSegmentNode = default;
			return true;
		}
	}

	public void InsertRange(int index, ReadOnlyMemory<T> items) {
		this.InsertRange(index, items.ToArray().AsMemory());
	}

	public void InsertRange(int index, params T[] items) {
		this.InsertRange(index, items.AsMemory());
	}

	public void InsertRange(int index, ArraySegment<T> items) {
		this.InsertRange(index, items.AsMemory());
	}

	public void InsertRange(int index, IEnumerable<T> items) {
		this.InsertRange(index, items.ToArray().AsMemory());
	}

	public void InsertRange(int index, IEnumerable items) {
		this.InsertRange(index, items.Cast<T>().ToArray().AsMemory());
	}

	/// <see cref="System.Collections.Generic.IList{T}.Insert"/>
	public void Insert(int index, T item) {
		this.InsertRange(index, new T[] { item }.AsMemory());
	}

	/// <see cref="System.Collections.IList.Insert"/>
	void IList.Insert(int index, object? value) {
		if (value == null) {
			if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) == null) {
				throw new ArgumentException("The value null is not of type T and cannot be used in this collection.");
			}
			this.Insert(index, default(T)!);
			return;
		}

		if (value is not T item) {
			throw new ArgumentException($"The value \"{value}\" is not of type T.");
		}
		this.Insert(index, item);
	}

	// ---

	public void AddRange(Memory<T> items) {
		this.InsertRange(this.count, items);
	}

	public void AddRange(ReadOnlyMemory<T> items) {
		this.InsertRange(this.count, items);
	}

	public void AddRange(params T[] items) {
		this.InsertRange(this.count, items);
	}

	public void AddRange(ArraySegment<T> items) {
		this.InsertRange(this.count, items);
	}

	public void AddRange(IEnumerable<T> items) {
		this.InsertRange(this.count, items);
	}

	public void AddRange(IEnumerable items) {
		this.InsertRange(this.count, items);
	}

	/// <see cref="System.Collections.Generic.ICollection{T}.Add"/>
	public void Add(T item) {
		this.Insert(this.count, item);
	}

	/// <see cref="System.Collections.IList.Add"/>
	int IList.Add(object? value) {
		((IList)this).Insert(this.count, value);
		return this.count - 1;
	}

	#endregion Insert, Add

	#region RemoveRange, RemoveAt, Remove, RemoveAll

	public void RemoveRange(Range range) {
		var (start, count) = range.GetOffsetAndLength(this.count);
		if (count == 0) {
			return;
		}

		var end = start + count;
		var node = this.memories.First;
		var nodeStart = 0;
		while (node != null) {
			if (nodeStart >= end) {
				break;
			}

			var nextNode = node.Next;
			var memory = node.Value;
			var nodeEnd = nodeStart + memory.Length;

			if (nodeEnd <= start) {
				node = nextNode;
				nodeStart = nodeEnd;
				continue;
			}

			var overlapStart = Math.Max(nodeStart, start);
			var overlapEnd = Math.Min(nodeEnd, end);
			var overlapLength = overlapEnd - overlapStart;
			var prefix = overlapStart - nodeStart;
			var suffix = nodeEnd - overlapEnd;

			if (MemoryMarshal.TryGetArray<T>(memory, out _)) {
				if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
					memory.Span.Slice(prefix, overlapLength).Clear();
				}

				if (prefix > 0 && suffix > 0) {
					node.Value = memory[..prefix];
					this.memories.AddAfter(node, memory[^suffix..]);
					goto next;
				}

				if (prefix > 0) {
					node.Value = memory[..prefix];
					goto next;
				}

				node.Value = memory[overlapLength..];
				goto next;
			}

			if (prefix > 0 && suffix > 0) {
				node.Value = memory[..prefix].ToArray();
				this.memories.AddAfter(node, memory[^suffix..].ToArray());
				goto next;
			}

			if (prefix > 0) {
				node.Value = memory[..prefix].ToArray();
				goto next;
			}

			node.Value = memory[overlapLength..].ToArray();
			// goto next;

		next:
			this.count -= overlapLength;
			this.cacheForFindSegmentNode = default;
			nodeStart = nodeEnd;
			node = nextNode;
		}
	}

	public void RemoveRange(int offset, int count) {
		this.RemoveRange(offset.. (offset + count));
	}

	/// <see cref="System.Collections.Generic.IList{T}.RemoveAt"/>
	/// <see cref="System.Collections.IList.RemoveAt"/>
	public void RemoveAt(int index) {
		this.RemoveRange(index.. (index + 1));
	}

	/// <see cref="System.Collections.Generic.ICollection{T}.Remove"/>
	public bool Remove(T item) {
		var index = this.IndexOf(item);
		if (index < 0) {
			return false;
		}

		this.RemoveAt(index);
		return true;
	}

	/// <see cref="System.Collections.IList.Remove"/>
	void IList.Remove(object? value) {
		if (value == null) {
			if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) == null) {
				return;
			}

			this.Remove(default(T)!);
			return;
		}

		if (value is not T item) {
			return;
		}

		this.Remove(item);
	}

	// ---

	public void RemoveAll(params HashSet<T> items) {
		if (this.count == 0 || items.Count == 0) {
			return;
		}

		var indicesToRemove = new List<int>();
		var currentIndex = 0;
		foreach (var element in this) {
			if (items.Contains(element)) {
				indicesToRemove.Add(currentIndex);
			}
			++currentIndex;
		}

		this.RemoveByIndices(indicesToRemove);
	}

	public void RemoveAll(IEnumerable<T> items) {
		var set = items as HashSet<T>;
		if (set == null) {
			set = new HashSet<T>(items);
		}
		this.RemoveAll(set);
	}

	public void RemoveAll(T[] items) {
		RemoveAll((IEnumerable<T>)items);
	}

	public void RemoveAll(Memory<T> items) {
		RemoveAll(items.ToArray());
	}

	public void RemoveAll(ReadOnlyMemory<T> items) {
		RemoveAll(items.ToArray());
	}

	public void RemoveAll(ArraySegment<T> items) {
		RemoveAll((IEnumerable<T>)items);
	}

	public void RemoveAll(IEnumerable items) {
		this.RemoveAll(items.Cast<T>());
	}

	public void RemoveAll(object? value) {
		switch (value) {
		case null:
			if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) == null) {
				return;
			}
			this.RemoveAll(default(T)!);
			return;
		case T a:
			this.RemoveAll(a);
			return;
		case IEnumerable a:
			this.RemoveAll(a.Cast<T>());
			return;
		default:
			return;
		}
	}

	protected void RemoveByIndices(List<int> indicesToRemove) {
		var indicesCount = indicesToRemove.Count;
		if (indicesCount == 0) {
			return;
		}

		var ranges = new List<Range>();
		var start = indicesToRemove[0];
		var last = start + 1;
		var ix = 1;
		for (; ix < indicesCount; ++ix) {
			var indexToRemove = indicesToRemove[ix];
			if (indexToRemove != last) {
				ranges.Add(start..last);
				start = indexToRemove;
			}
			last = indexToRemove + 1;
		}
		ranges.Add(start..last);

		for (int i = ranges.Count - 1; i >= 0; i--) {
			this.RemoveRange(ranges[i]);
		}
	}

	// ---

	public void RemoveAll(Func<T, bool> match) {
		if (this.count == 0) {
			return;
		}

		var indicesToRemove = new List<int>();
		var currentIndex = 0;
		foreach (var element in this) {
			if (match(element)) {
				indicesToRemove.Add(currentIndex);
			}
			++currentIndex;
		}

		this.RemoveByIndices(indicesToRemove);
	}

	public void RemoveAll(Func<int, T, bool> match) {
		if (this.count == 0) {
			return;
		}

		var indicesToRemove = new List<int>();
		var currentIndex = 0;
		foreach (var element in this) {
			if (match(currentIndex, element)) {
				indicesToRemove.Add(currentIndex);
			}
			++currentIndex;
		}

		this.RemoveByIndices(indicesToRemove);
	}

	public void RemoveAll(Func<T, (bool remove, bool next)> match) {
		if (this.count == 0) {
			return;
		}

		var indicesToRemove = new List<int>();
		var currentIndex = 0;
		foreach (var element in this) {
			var (remove, next) = match(element);
			if (remove) {
				indicesToRemove.Add(currentIndex);
			}
			if (next) {
				++currentIndex;
				continue;
			}
			return;
		}

		this.RemoveByIndices(indicesToRemove);
	}

	public void RemoveAll(Func<int, T, (bool remove, bool next)> match) {
		if (this.count == 0) {
			return;
		}

		var indicesToRemove = new List<int>();
		var currentIndex = 0;
		foreach (var element in this) {
			var (remove, next) = match(currentIndex, element);
			if (remove) {
				indicesToRemove.Add(currentIndex);
			}
			if (next) {
				++currentIndex;
				continue;
			}
			return;
		}

		this.RemoveByIndices(indicesToRemove);
	}

	#endregion RemoveAt, Remove, RemoveAll

	#region Clear

	/// <see cref="System.Collections.Generic.ICollection{T}.Add"/>
	/// <see cref="System.Collections.IList.Clear"/>
	public void Clear() {
		var node = this.memories.First;
		while (node != null) {
			var nextNode = node.Next;
			var memory = node.Value;
			if (MemoryMarshal.TryGetArray<T>(memory, out _)) {
				if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
					memory.Span.Clear();
				}

				node.Value = memory[..0];
				node = nextNode;
				continue;
			}

			this.memories.Remove(node);
			node = nextNode;
		}
		this.count = 0;
		this.cacheForFindSegmentNode = default;
	}

	#endregion

	#region Conversion

	public void CopyTo(Span<T> dest) {
		if (dest.Length < this.count) {
			throw new ArgumentOutOfRangeException(nameof(dest));
		}

		this.CopyToInternal(dest);
	}

	protected void CopyToInternal(Span<T> dest) {
		if (this.memories.Count <= 0) {
			return;
		}

		int destStart = 0;
		foreach (var segment in this.memories) {
			var length = segment.Length;
			if (length == 0) {
				continue;
			}

			segment.Span.CopyTo(dest.Slice(destStart, length));
			destStart += length;
		}
	}

	public void CopyTo(Range srcRange, Span<T> dest) {
		var (srcStart, srcCount) = srcRange.GetOffsetAndLength(this.count);
		this.CopyTo(srcStart, srcCount, dest);
	}

	public void CopyTo(int srcStart, int srcCount, Span<T> dest) {
		if (srcStart < 0 || srcStart > this.count) {
			throw new ArgumentOutOfRangeException(nameof(srcStart));
		}
		if (srcCount < 0 || srcStart + srcCount > this.count) {
			throw new ArgumentOutOfRangeException(nameof(srcCount));
		}
		this.CopyToInternal(srcStart, srcCount, dest);
	}

	protected void CopyToInternal(int srcStart, int srcCount, Span<T> dest) {
		if (srcCount <= 0) {
			return;
		}

		var srcEnd = srcStart + srcCount;
		int nextIndex = 0;
		int copiedCount = 0;
		foreach (var segment in this.memories) {
			var length = segment.Length;
			var nodeStart = nextIndex;
			var nodeEnd = nextIndex + length;
			nextIndex += length;
			if (nodeEnd <= srcStart || nodeStart >= srcEnd) {
				continue;
			}

			var overlapStart = Math.Max(nodeStart, srcStart);
			var overlapLength = Math.Min(nodeEnd, srcEnd) - overlapStart;
			segment.Span.Slice(overlapStart - nodeStart, overlapLength)
				.CopyTo(dest.Slice(copiedCount, overlapLength));

			copiedCount += overlapLength;
			if (copiedCount >= srcCount) {
				break;
			}
		}
	}

	/// <see cref="System.Collections.Generic.ICollection{T}.CopyTo"/>
	public void CopyTo(T[] array, int arrayIndex) {
		this.CopyTo(array.AsSpan(arrayIndex));
	}

	/// <see cref="System.Collections.ICollection.CopyTo"/>
	void ICollection.CopyTo(Array array, int index) {
		CopyTo(((T[])array).AsSpan(index));
	}

	// ---

	public T[] ToArray() {
		T[] array = new T[this.count];
		this.CopyToInternal(array);
		return array;
	}

	public T[] ToArray(Range range) {
		var (srcStart, srcCount) = range.GetOffsetAndLength(this.count);
		return this.ToArray(srcStart, srcCount);
	}

	public T[] ToArray(int srcStart, int srcCount) {
		if (srcStart < 0 || srcStart > this.count) {
			throw new ArgumentOutOfRangeException(nameof(srcStart));
		}
		if (srcCount < 0 || srcStart + srcCount > this.count) {
			throw new ArgumentOutOfRangeException(nameof(srcCount));
		}

		if (srcCount == 0) {
			return Array.Empty<T>();
		}

		T[] array = new T[srcCount];
		this.CopyToInternal(srcStart, srcCount, array);
		return array;
	}

	// ---

	public List<T> ToList() {
		var list = new List<T>(this.count);
		foreach (var memory in this.memories) {
			list.AddRange(memory.Span);
		}
		return list;
	}

	// ---

	public HashSet<T> ToHashSet(IEqualityComparer<T>? comparer = null) {
		var set = new HashSet<T>(this.count, comparer ?? this.equalityComparer);
		foreach (var memory in this.memories) {
			foreach (var item in memory.Span) {
				set.Add(item);
			}
		}
		return set;
	}

	// ---

	public Dictionary<int, T> ToDictionary(IEqualityComparer<int>? comparer = null) {
		var dict = new Dictionary<int, T>(this.count, comparer);
		var index = 0;
		foreach (var memory in this.memories) {
			foreach (var item in memory.Span) {
				dict.Add(index++, item);
			}
		}
		return dict;
	}

	public Dictionary<K, T> ToDictionary<K>(Func<T, K> keySelector, IEqualityComparer<K>? comparer = null) where K : notnull {
		var dict = new Dictionary<K, T>(this.count, comparer);
		foreach (var memory in this.memories) {
			foreach (var item in memory.Span) {
				dict.Add(keySelector(item), item);
			}
		}
		return dict;
	}

	public Dictionary<K, V> ToDictionary<K, V>(Func<T, K> keySelector, Func<T, V> valueSelector, IEqualityComparer<K>? comparer = null) where K : notnull {
		var dict = new Dictionary<K, V>(this.count, comparer);
		foreach (var memory in this.memories) {
			foreach (var item in memory.Span) {
				dict.Add(keySelector(item), valueSelector(item));
			}
		}
		return dict;
	}

	public Dictionary<K, V> ToDictionary<K, V>(Func<T, (K key, V value)> selector, IEqualityComparer<K>? comparer = null) where K : notnull {
		var dict = new Dictionary<K, V>(this.count, comparer);
		foreach (var memory in this.memories) {
			foreach (var item in memory.Span) {
				var (key, value) = selector(item);
				dict.Add(key, value);
			}
		}
		return dict;
	}

	public Dictionary<K, T> ToDictionary<K>(Func<int, T, K> keySelector, IEqualityComparer<K>? comparer = null) where K : notnull {
		var dict = new Dictionary<K, T>(this.count, comparer);
		var index = 0;
		foreach (var memory in this.memories) {
			foreach (var item in memory.Span) {
				dict.Add(keySelector(index++, item), item);
			}
		}
		return dict;
	}

	public Dictionary<K, V> ToDictionary<K, V>(Func<int, T, K> keySelector, Func<int, T, V> valueSelector, IEqualityComparer<K>? comparer = null) where K : notnull {
		var dict = new Dictionary<K, V>(this.count, comparer);
		var index = 0;
		foreach (var memory in this.memories) {
			foreach (var item in memory.Span) {
				dict.Add(keySelector(index, item), valueSelector(index, item));
				++index;
			}
		}
		return dict;
	}

	public Dictionary<K, V> ToDictionary<K, V>(Func<int, T, (K key, V value)> selector, IEqualityComparer<K>? comparer = null) where K : notnull {
		var dict = new Dictionary<K, V>(this.count, comparer);
		var index = 0;
		foreach (var memory in this.memories) {
			foreach (var item in memory.Span) {
				var (key, value) = selector(index++, item);
				dict.Add(key, value);
			}
		}
		return dict;
	}

	// ---

	public ImmutableArray<T> ToImmutableArray() {
		return ImmutableArray.Create(this.ToArray());
	}

	public ImmutableList<T> ToImmutableList() {
		return ImmutableList.Create(this.ToArray());
	}

	public ImmutableHashSet<T> ToImmutableHashSet() {
		return ImmutableHashSet.Create(this.equalityComparer, this.ToArray());
	}

	// ---

	public void Unify() {
		var first = this.memories.First;
		if (first == null) {
			this.memories.Clear();
			this.count = 0;
			this.cacheForFindSegmentNode = default;
			return;
		}

		var next = first.Next;
		if (next == null) {
			if (MemoryMarshal.TryGetArray<T>(first.Value, out var segment)) {
				if (segment.Offset == 0 && segment.Count == segment.Array!.Length) {
					this.cacheForFindSegmentNode = (first, 0);
					return;
				}
			}
		}

		var array = this.ToArray();
		this.memories.Clear();
		var node = this.memories.AddFirst(array);
		this.count = array.Length;
		this.cacheForFindSegmentNode = (node, 0);
	}

	// ---

	public ArraySegment<T> AsArraySegment() {
		this.Unify();

		var first = this.memories.First;
		if (first == null) {
			return ArraySegment<T>.Empty;
		}

		if (!MemoryMarshal.TryGetArray<T>(first.Value, out var segment)) {
			throw new InvalidOperationException();
		}

		return segment;
	}

	public Memory<T> AsMemory() {
		this.Unify();

		var first = this.memories.First;
		if (first == null) {
			return Memory<T>.Empty;
		}

		return first.Value;
	}

	public ReadOnlyMemory<T> AsReadOnlyMemory() {
		return this.AsMemory();
	}

	public Span<T> AsSpan() {
		return this.AsMemory().Span;
	}

	public ReadOnlySpan<T> AsReadOnlySpan() {
		return this.AsReadOnlyMemory().Span;
	}

	// ---

	public IList<T> AsList() {
		return this;
	}

	public IReadOnlyList<T> AsReadOnlyList() {
		return this;
	}

	public ICollection<T> AsCollection() {
		return this;
	}

	public IReadOnlyCollection<T> AsReadOnlyCollection() {
		return this;
	}

	public IEnumerable<T> AsEnumerable() {
		return this;
	}

	#endregion

	#region SyncRoot

	/// <see cref="System.Collections.ICollection.SyncRoot"/>
	object ICollection.SyncRoot {
		get {
			return this;
		}
	}

	#endregion

	#region IsFixedSize

	/// <see cref="System.Collections.IList.IsFixedSize"/>
	bool IList.IsFixedSize {
		get {
			return false;
		}
	}

	#endregion

	#region IsReadOnly

	/// <see cref="System.Collections.Generic.ICollection{T}.IsReadOnly"/>
	bool ICollection<T>.IsReadOnly {
		get {
			return false;
		}
	}

	/// <see cref="System.Collections.IList.IsReadOnly"/>
	bool IList.IsReadOnly {
		get {
			return false;
		}
	}

	#endregion

	#region IsSynchronized

	/// <see cref="System.Collections.ICollection.IsSynchronized"/>
	bool ICollection.IsSynchronized {
		get {
			return false;
		}
	}

	#endregion
}

public static class FirstList
{
	public static Dictionary<T, V> ToDictionary<T, V>(this FirstList<T> list, Func<T, V> valueSelector, IEqualityComparer<T>? comparer = null) where T : notnull {
		var dict = new Dictionary<T, V>(list.count, comparer ?? list.equalityComparer);
		foreach (var memory in list.memories) {
			foreach (var item in memory.Span) {
				dict.Add(item, valueSelector(item));
			}
		}
		return dict;
	}
}
