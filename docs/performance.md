# Performance improvement candidates

FUSE / DokanNet callbacks are a hot path called 100–10000 times per second. The following are investigated, promising, but not-yet-started improvements. In priority order:

1. **uid/gid cache in `UserResolver`** — [src/mount/src/UserResolver.cs](../src/mount/src/UserResolver.cs) may call libc's `getpwnam` / `getgrnam` on every `GetAttr`. Caching in a `Dictionary<string, uint>` makes the second-and-later calls zero-cost. Measurably effective for `ls -al` on a large directory. The Assign-side [WindowsUserResolver.cs](../src/assign/src/WindowsUserResolver.cs) is the same. **ROI: high / effort: low**
2. **`InodeCache`'s `lock(this)` → `ConcurrentDictionary`** — [src/lib/src/Api/InodeCache.cs](../src/lib/src/Api/InodeCache.cs) protects both the byId/byPath dictionaries with a single `lock(this)`, and in some cases runs a DB query inside the `lock`. Lock contention is inevitable under multi-threaded FUSE. Replace with `ConcurrentDictionary` and redesign so DB fetches run outside the lock. **ROI: high / effort: high (needs a Lazy pattern to suppress duplicate queries)**
3. **eager-ize the 5 `Lazy<T>` of `PathParser`** — [src/lib/src/Utility/PathParser.cs](../src/lib/src/Utility/PathParser.cs) creates 5 `Lazy<>` per `FromPath`. Add a hot-path-only eager constructor to reduce per-path allocation. **ROI: medium / effort: medium**
4. **replace Dapper with a raw `NpgsqlDataReader` (hot SELECTs only)** — hand-writing a reader only for hot queries such as the inode fetch in [src/lib/src/Api/InodeCache.cs](../src/lib/src/Api/InodeCache.cs) and `Api.ListChildren` would avoid reflection mapping. **ROI: high / effort: high (maintainability drop)**
5. **handle `Encoding.UTF8.GetString(path)` as a `Span<byte>`** — [src/mount/src/FileSystem.cs:75](../src/mount/src/FileSystem.cs#L75) `PathToString`. Making the `InodeCache.byPath` key a `byte[]` hash would skip the UTF-8 decode. Large design impact, so last. **ROI: medium / effort: very high**
6. **remove `Inode.Children` (`FirstList<Inode>`) or change it to `List<T>`** — it is effectively unreferenced, yet `new FirstList<Inode>()` (a 1377-line class) runs on every inode creation. May reduce the `Inode` constructor cost. **ROI: low / effort: low**
7. **cache the per-call deserialization of the `Setting<T>.Value` getter** — settings are read only at startup, so this is not a hot path. **ROI: low / effort: low**

When implementing without a benchmark, the order **1 → 6 → 3** is the lowest-risk. Do 2 and 4 only after measuring with BenchmarkDotNet if done seriously.
