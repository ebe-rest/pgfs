namespace Pgfs.Lib.Utility;

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.Contracts;
using System.Text;

/// <summary>
/// Provides parsing, normalization, and reconstruction of path elements with a different separator.
/// </summary>
/// <remarks>
/// <see cref="NameList"/> is the array of names of each normalized path entry.
/// </remarks>
public class PathParser
{
	private static readonly string _defaultSeparator = System.IO.Path.DirectorySeparatorChar.ToString();
	private static readonly SearchValues<char> searchWildcard = SearchValues.Create('*', '?');

	private readonly Lazy<(string path, string separator)> _original;
	private readonly Lazy<(string name, bool has)> _drive;
	private readonly Lazy<(string path, bool absolute)> _root;
	private readonly Lazy<(string[] items, int firstWildcardAt)> _nameList;
	private readonly ConcurrentDictionary<string, string[]> _pathListDictionary = [];

	private class ConstructorParams
	{
		public required Lazy<(string path, string separator)> original;
		public required Lazy<(string name, bool has)> drive;
		public required Lazy<(string path, bool absolute)> root;
		public required Lazy<(string[] items, int firstWildcardAt)> nameList;
	}

	private PathParser(ConstructorParams args)
	{
		this._original = args.original;
		this._drive = args.drive;
		this._root = args.root;
		this._nameList = args.nameList;
		this.NameAt = new(this.GetNameAt);
		this.PathListWith = new(this.GetPathListWith);
		this.PathAtWith = new(this.GetPathAtWith);
		this.PathAt = new(this.GetPathAt);
		this.PathWith = new(this.GetPathWith);
	}

	/// <summary>
	/// Begins parsing with the given path and separator.
	/// </summary>
	/// <param name="path">The path string to parse.</param>
	/// <param name="separator">The path separator (e.g. "/", "\").</param>
	private static ConstructorParams FromPathInternal(string path, string separator)
	{
		var offset = 0;
		var original_ = new Lazy<( string path, string separator )>(() => (path, separator));
		var drive_ = new Lazy<( string name, bool has )>(ParseDrive);
		var root_ = new Lazy<( string path, bool absolute )>(ParseRoot);
		var nameList_ = new Lazy<( string[] items, int firstWildcardAt )>(ParseNameList);

		return new ConstructorParams
		{
			original = original_,
			drive = drive_,
			root = root_,
			nameList = nameList_
		};

		( string name, bool has ) ParseDrive()
		{
			var original = original_.Value;
			var remain = original.path.AsSpan();

			if (remain.Length < 2 || !char.IsLetter(remain[0]) || remain[1] != ':')
			{
				return (string.Empty, false);
			}

			offset = 2;
			return (remain[..2].ToString(), true);
		}

		( string path, bool isAbsolute ) ParseRoot()
		{
			var original = original_.Value;
			var drive = drive_.Value;
			var remain = original.path.AsSpan(offset);
			var separator = original.separator.AsSpan();

			if (!remain.StartsWith(separator))
			{
				return (drive.name, false);
			}

			offset += separator.Length;
			return (string.Concat((ReadOnlySpan<char>)drive.name, separator), true);
		}

		( string[] items, int firstWildcardAt ) ParseNameList()
		{
			var original = original_.Value;
			var root = root_.Value;
			var remain = original.path.AsSpan(offset);

			if (remain.IsEmpty)
			{
				return ([root.path], 0);
			}

			var separator = original.separator.AsSpan();
			var stack = new List<Range>(16);
			var count = 0;

			foreach (var r in remain.Split(separator))
			{
				var s = remain[r];

				if (s.IsEmpty || s is ".")
				{
					continue;
				}

				if (s is "..")
				{
					if (count > 0)
					{
						--count;
					}

					continue;
				}

				if (count >= stack.Count)
				{
					stack.Add(r);
				}
				else
				{
					stack[count] = r;
				}

				++count;
			}

			var items = new string [count + 1];
			var firstWildcardAt = count;

			items[0] = root.path;

			for (var i = 1; i <= count; i++)
			{
				var name = remain[stack[i - 1]];

				if (firstWildcardAt == count)
				{
					if (name.ContainsAny(searchWildcard))
					{
						firstWildcardAt = i;
					}
				}

				items[i] = name.ToString();
			}

			return (items, firstWildcardAt);
		}
	}

	/// <summary>
	/// Begins parsing with the given path and separator.
	/// </summary>
	/// <param name="path">The path string to parse.</param>
	/// <param name="separator">The path separator (e.g. "/", "\").</param>
	public static PathParser FromPath(string path, string separator)
		=> new(FromPathInternal(path, separator));

	/// <summary>
	/// Begins parsing with the given path, using the OS default path separator.
	/// </summary>
	/// <param name="path">The path string to parse.</param>
	public static PathParser FromPath(string path)
		=> new(FromPathInternal(path, _defaultSeparator));

	/// <summary>
	/// Begins parsing with the given path and separator.
	/// </summary>
	/// <param name="path">The path string to parse.</param>
	/// <param name="separator">The path separator (e.g. "/", "\").</param>
	public PathParser(string path, string separator)
		: this(FromPathInternal(path, separator))
	{
	}

	/// <summary>
	/// Begins parsing with the given path, using the OS default path separator.
	/// </summary>
	/// <param name="path">The path string to parse.</param>
	public PathParser(string path)
		: this(FromPathInternal(path, _defaultSeparator))
	{
	}

	/// <summary>
	/// Builds a PathParser from a NameList-form array.
	/// </summary>
	/// <param name="nameList">A normalized NameList array.</param>
	/// <param name="separator">The path separator.</param>
	/// <remarks>
	/// <ul>
	/// <li>Drive letter + path separator: an absolute path with a drive.</li>
	/// <li>Drive letter only: a relative path with a drive.</li>
	/// <li>Path separator only: an absolute path.</li>
	/// <li>Empty string: a relative path.</li>
	/// <li>No other combinations exist.</li>
	/// </ul>
	/// </remarks>
	public static PathParser FromNameList(IReadOnlyList<string> nameList, string separator)
	{
		var parsed = new Lazy<(string driveName, bool hasDrive, string rootPath, bool absolute, string[] nameList, int firstWildcardAt)>(ParseNameList);
		return new PathParser(
			new ConstructorParams
			{
				original = new(ParseOriginal),
				drive = new(() => (parsed.Value.driveName, parsed.Value.hasDrive)),
				root = new(() => (parsed.Value.rootPath, parsed.Value.absolute)),
				nameList = new(() => (parsed.Value.nameList, parsed.Value.firstWildcardAt))
			}
		);

		(string path, string separator) ParseOriginal()
		{
			if (nameList.Count == 0)
			{
				throw new ArgumentException("nameList must not be empty");
			}
			if (nameList.Count == 1)
			{
				return (nameList[0], separator);
			}
			return (string.Join(separator, nameList), separator);
		}

		(string driveName, bool hasDrive, string rootPath, bool absolute, string[] items, int firstWildcardAt) ParseNameList()
		{
			if (nameList.Count == 0)
			{
				return (string.Empty, false, string.Empty, false, Array.Empty<string>(), int.MaxValue);
			}

			var cursor = nameList[0].AsSpan();

			var rootPath = new StringBuilder(cursor.Length);
			var driveName = string.Empty;
			var hasDrive = false;
			if (cursor.Length >= 2 && char.IsLetter(cursor[0]) && cursor[1] == ':')
			{
				rootPath.Append(driveName);
				driveName = cursor[..2].ToString();
				hasDrive = true;
				cursor = cursor[..2];
			}

			var absolute = false;
			if (cursor.Length >= separator.Length && cursor.StartsWith(separator))
			{
				rootPath.Append(separator);
				absolute = true;
				cursor = cursor[..separator.Length];
			}

			var stack = new Stack<string>(nameList.Count);
			stack.Push(rootPath.ToString());
			for (var index = 0;;)
			{
				while (cursor.IsEmpty)
				{
					++index;
					if (index >= nameList.Count)
					{
						goto next;
					}

					cursor = nameList[index].AsSpan();
				}

				ReadOnlySpan<char> current;
				var offset = cursor.IndexOf(separator);
				if (offset != -1)
				{
					current = cursor[..offset].ToString();
					cursor = cursor[(offset + separator.Length) .. ^1];
				}
				else
				{
					current = cursor;
					cursor = ReadOnlySpan<char>.Empty;
				}

				if (current.IsEmpty || current is ".")
				{
					continue;
				}
				if (current is "..")
				{
					stack.Pop();
					continue;
				}

				stack.Push(cursor.ToString());
			}

		next:
			int firstWildcardAt = (from e in stack.Index() where e.Item.ContainsAny(searchWildcard) select e.Index).FirstOrDefault(nameList.Count);
			return (driveName, hasDrive, rootPath.ToString(), absolute, stack.ToArray(), firstWildcardAt);
		}
	}

	/// <summary>
	/// The original path string passed to the constructor.
	/// </summary>
	public string Original
		=> this._original.Value.path;

	/// <summary>
	/// The original separator used for parsing the path.
	/// </summary>
	public string Separator
		=> this._original.Value.separator;

	/// <summary>
	/// The drive-letter part of the path. Returns an empty string if there is no drive.
	/// e.g. "C:", "D:", ""
	/// </summary>
	public string Drive
		=> this._drive.Value.name;

	/// <summary>
	/// Indicates whether the path contains a drive letter.
	/// </summary>
	public bool HasDrive
		=> this._drive.Value.has;

	/// <summary>
	/// The root part when it is an absolute path (may include the drive letter).
	/// e.g. "/" (Linux/UNIX), "C:\" (Windows)
	/// </summary>
	public string Root
		=> this._root.Value.path;

	/// <summary>
	/// Indicates whether the path starts as an absolute path.
	/// </summary>
	public bool Absolute
		=> this._root.Value.absolute;

	/// <summary>
	/// The list of each entry of the normalized path. The first element contains the root part.
	/// </summary>
	public string[] NameList
		=> this._nameList.Value.items;

	/// <summary>
	/// Gets the path entry at the given index.
	/// Index 0 is the root part.
	/// </summary>
	/// <param name="index">The index of the path entry to get.</param>
	/// <seealso cref="NameAt"/>
	/// <seealso cref="this"/>
	private string GetNameAt(int index)
		=> this._nameList.Value.items[index];

	/// <summary>
	/// Gets the path entry at the given index.
	/// Index 0 is the root part.
	/// </summary>
	/// <param name="index">The index of the path entry to get.</param>
	/// <seealso cref="GetNameAt"/>
	public string this[int index]
		=> this.GetNameAt(index);

	/// <summary>
	/// Gets the path entry at the given index.
	/// Index 0 is the root part.
	/// </summary>
	/// <seealso cref="GetNameAt"/>
	/// <seealso cref="this"/>
	public readonly ReadOnlyIndexer<int /*index*/, string /*path*/> NameAt;

	/// <summary>
	/// The index of the first position where a wildcard ('*' or '?') appears among the path entries.
	/// Returns int.MaxValue if no wildcard is present.
	/// </summary>
	public int FirstWildcardAt
		=> this._nameList.Value.firstWildcardAt;

	/// <summary>
	/// The number of path entries (including the root part).
	/// </summary>
	public int Count
		=> this._nameList.Value.items.Length;

	/// <summary>
	/// An indexer that gets the array of hierarchical paths rejoined with the given separator.
	/// Index 0 is the root only; index N is the path joined up to N levels.
	/// </summary>
	/// <param name="separator">The separator used for rejoining.</param>
	/// <returns>The array of joined paths per level.</returns>
	/// <seealso cref="PathListWith"/>
	private string[] GetPathListWith(string separator)
	{
		return this._pathListDictionary.GetOrAdd(separator, ParsePathListWith);

		string[] ParsePathListWith(string separator)
		{
			var nameList = this._nameList.Value.items;
			var length = nameList.Length;
			var path = new StringBuilder(this.Original.Length + (this.Separator.Length - separator.Length) * length);
			path.Append(this._drive.Value.name);

			// If it is an absolute path, always prepend the root separator.
			// (The old code conditioned on `separator != this.Separator`, but that meant e.g. on Linux with
			//  this.Separator="/", pathList[0] became "" and pathList[1] became "dir1" instead of "/dir1",
			//  so it no longer hit InodeCache's path cache.)
			if (this._root.Value.absolute)
			{
				path.Append(separator);
			}

			var pathList = new string[length];
			pathList[0] = path.ToString();

			if (length >= 2)
			{
				pathList[1] = path.Append(nameList[1]).ToString();

				for (var i = 2; i < length; i++)
				{
					pathList[i] = path.Append(separator).Append(nameList[i]).ToString();
				}
			}

			this._pathListDictionary[separator] = pathList;
			return pathList;
		}
	}

	/// <summary>
	/// The array of hierarchical paths rejoined with the original separator (<see cref="Separator"/>).
	/// </summary>
	public string[] PathList
		=> this.GetPathListWith(this.Separator);

	/// <summary>
	/// Calls <see cref="GetPathListWith"/>.
	/// </summary>
	/// <example>
	/// pathList = paser.PathListWith[separator]
	/// </example>
	public readonly ReadOnlyIndexer<string /*separator*/, string[] /*pathList*/> PathListWith;

	/// <summary>
	/// An indexer that gets the path rejoined with the given index and separator.
	/// </summary>
	/// <param name="index">The hierarchy index to get.</param>
	/// <param name="separator">The separator used for rejoining.</param>
	private string GetPathAtWith(int index, string separator)
		=> this.GetPathListWith(separator)[index];

	/// <summary>
	/// Calls <see cref="GetPathAtWith"/>.
	/// </summary>
	/// <example>
	/// path = paser.PathAtWith[index, separator]
	/// </example>
	public readonly ReadOnlyIndexer<int /*index*/, string /*separator*/, string /*path*/> PathAtWith;

	/// <summary>
	/// An indexer that, using the original separator (<see cref="Separator"/>), gets the path joined up to the given index.
	/// </summary>
	/// <param name="index">The hierarchy index to get.</param>
	private string GetPathAt(int index)
		=> this.GetPathListWith(this.Separator)[index];

	/// <summary>
	/// Calls <see cref="GetPathAt"/>.
	/// </summary>
	/// <example>
	/// path = paser.PathAt[index]
	/// </example>
	public readonly ReadOnlyIndexer<int /*index*/, string /*path*/> PathAt;

	/// <summary>
	/// An indexer that gets the entire final path fully rejoined with the given separator.
	/// </summary>
	/// <param name="separator">The separator used for rejoining.</param>
	private string GetPathWith(string separator)
		=> this.GetPathListWith(separator)[^1];

	/// <summary>
	/// Calls <see cref="GetPathWith"/>.
	/// </summary>
	/// <example>
	/// path = paser.PathWith[separator]
	/// </example>
	public readonly ReadOnlyIndexer<string /*separator*/, string /*path*/> PathWith;

	/// <summary>
	/// The entire final path fully rejoined with the original separator (<see cref="Separator"/>).
	/// </summary>
	public string Path
		=> this.GetPathListWith(this.Separator)[^1];

	// ---

	/// <summary>
	/// Inserts another <see cref="PathParser"/>'s contents at the given position and returns a new instance.
	/// </summary>
	/// <param name="position">The index to insert at.</param>
	/// <param name="parser">The <see cref="PathParser"/> to insert.</param>
	/// <returns>A new <see cref="PathParser"/> instance.</returns>
	/// <remarks>
	/// When position is 0, <paramref name="parser"/>'s contents become the new root and the rest of the current path is joined onto it.
	/// Otherwise, <paramref name="parser"/>'s contents (excluding the root) are inserted at the given position.
	/// </remarks>
	[Pure]
	public PathParser Insert(int position, PathParser parser)
	{
		if (position < 0)
		{
			position = 0;
		}

		var nameList = this.NameList;
		if (position > nameList.Length)
		{
			position = nameList.Length;
		}

		var insertNames = parser.NameList;
		switch (position)
		{
			case 0:
				return FromNameList(insertNames[..position].Concat(nameList[1..]).ToArray(), this.Separator);
			default:
				return FromNameList(nameList[.. position].Concat(insertNames[1..]).Concat(nameList[position..]).ToArray(), this.Separator);
		}
	}

	/// <summary>
	/// Inserts a path string at the given position and returns a new instance.
	/// </summary>
	/// <param name="position">The index to insert at.</param>
	/// <param name="path">The path string to insert. Its separator must be <see cref="Separator"/>.</param>
	/// <returns>A new <see cref="PathParser"/> instance.</returns>
	[Pure]
	public PathParser Insert(int position, string path)
	{
		if (position < 0)
		{
			position = 0;
		}

		var nameList = this.NameList;
		if (position > nameList.Length)
		{
			position = nameList.Length;
		}

		return FromNameList(nameList[..position].Append(path).Concat(nameList[position..]).ToArray(), this.Separator);
	}

	/// <summary>
	/// Appends another <see cref="PathParser"/>'s contents at the end and returns a new instance.
	/// </summary>
	/// <param name="parser">The <see cref="PathParser"/> to append.</param>
	/// <returns>A new <see cref="PathParser"/> instance.</returns>
	[Pure]
	public PathParser Append(PathParser parser)
		=> this.Insert(this.Count, parser);

	/// <summary>
	/// Appends a path string at the end and returns a new instance.
	/// </summary>
	/// <param name="path">The path string to insert. Its separator must be <see cref="Separator"/>.</param>
	/// <returns>A new <see cref="PathParser"/> instance.</returns>
	[Pure]
	public PathParser Append(string path)
		=> this.Insert(this.Count, path);

	/// <summary>
	/// Inserts a <see cref="PathParser"/> at the front, replacing the root too, and returns a new instance.
	/// </summary>
	/// <param name="parser">The <see cref="PathParser"/> to insert.</param>
	/// <returns>A new <see cref="PathParser"/> instance.</returns>
	[Pure]
	public PathParser PrependRoot(PathParser parser)
		=> this.Insert(0, parser);

	/// <summary>
	/// Inserts a path string at the front, replacing the root too, and returns a new instance.
	/// </summary>
	/// <param name="path">The path string to insert. Its separator must be <see cref="Separator"/>.</param>
	/// <returns>A new <see cref="PathParser"/> instance.</returns>
	[Pure]
	public PathParser PrependRoot(string path)
		=> this.Insert(0, path);

	/// <summary>
	/// Inserts a <see cref="PathParser"/> at the front and returns a new instance. The root is preserved.
	/// </summary>
	/// <param name="parser">The <see cref="PathParser"/> to insert.</param>
	/// <returns>A new <see cref="PathParser"/> instance.</returns>
	[Pure]
	public PathParser Prepend(PathParser parser)
		=> this.Insert(1, parser);

	/// <summary>
	/// Inserts a path string at the front and returns a new instance. The root is preserved.
	/// </summary>
	/// <param name="path">The path string to insert. Its separator must be <see cref="Separator"/>.</param>
	/// <returns>A new <see cref="PathParser"/> instance.</returns>
	[Pure]
	public PathParser Prepend(string path)
		=> this.Insert(1, path);
}

/* Below, defined elsewhere

public static class Statics
{
	public static string Join(this IEnumerable<string> items, string? separator) {
		return string.Join(separator, items);
	}
}

public class ReadOnlyIndexer<TKey, TValue>(Func<TKey, TValue> getter)
{
	public TValue this[TKey key] => getter(key);
}

public class ReadOnlyIndexer<TKey1, TKey2, TValue>(Func<TKey1, TKey2, TValue> getter)
{
	public TValue this[TKey1 key1, TKey2 key2] => getter(key1, key2);
}
*/
