# 性能改善候補

FUSE / DokanNet コールバックは秒間 100〜10000 回呼ばれるホットパス。以下は調査済みで効果が見込めるが未着手の改善案。優先度順:

1. **`UserResolver` の uid/gid キャッシュ** — [src/mount/src/UserResolver.cs](../src/mount/src/UserResolver.cs) は `GetAttr` のたびに libc の `getpwnam` / `getgrnam` を呼んでいる可能性。`Dictionary<string, uint>` でキャッシュすれば 2 回目以降はゼロコスト。`ls -al` 大ディレクトリで実測効果大。Assign 側の [WindowsUserResolver.cs](../src/assign/src/WindowsUserResolver.cs) も同様。**ROI: 高 / 工数: 低**
2. **`InodeCache` の `lock(this)` → `ConcurrentDictionary` 化** — [src/lib/src/Api/InodeCache.cs](../src/lib/src/Api/InodeCache.cs) は単一 `lock(this)` で byId/byPath 両辞書を保護、しかも `lock` 内で DB クエリを実行しているケースがある。FUSE マルチスレッドで lock contention 必至。`ConcurrentDictionary` に置換し、DB 取得はロック外で実行する設計に変える。**ROI: 高 / 工数: 高（Lazy パターンで重複クエリ抑制が必要）**
3. **`PathParser` の `Lazy<T>` 5 連発を eager 化** — [src/lib/src/Utility/PathParser.cs](../src/lib/src/Utility/PathParser.cs) で `FromPath` ごとに `Lazy<>` を 5 つ生成。ホットパス専用 eager コンストラクタを追加してパスあたりのアロケーションを削減。**ROI: 中 / 工数: 中**
4. **Dapper を生 `NpgsqlDataReader` に置換（ホット SELECT のみ）** — [src/lib/src/Api/InodeCache.cs](../src/lib/src/Api/InodeCache.cs) の inode 取得や `Api.ListChildren` などのホットクエリだけ手書きリーダにすると、リフレクションマッピングを回避できる。**ROI: 高 / 工数: 高（保守性低下）**
5. **`Encoding.UTF8.GetString(path)` を `Span<byte>` のまま扱う** — [src/mount/src/FileSystem.cs:75](../src/mount/src/FileSystem.cs#L75) `PathToString`。`InodeCache.byPath` のキーを `byte[]` ハッシュにすれば UTF-8 デコードを省略可。設計影響が大きいので最後。**ROI: 中 / 工数: 極高**
6. **`Inode.Children` (`FirstList<Inode>`) を削除または `List<T>` 化** — 実質未参照なのに毎 inode 生成時に `new FirstList<Inode>()` (1377 行クラス) が走る。`Inode` のコンストラクタコスト削減になる可能性。**ROI: 低 / 工数: 低**
7. **`Setting<T>.Value` getter の毎回デシリアライズキャッシュ** — 設定読み取りは起動時のみなのでホットパスではない。**ROI: 低 / 工数: 低**

ベンチマークなしで実装する場合は **1 → 6 → 3** の順がリスクが低い。2 と 4 は本格的にやるなら BenchmarkDotNet で計測した上で。
