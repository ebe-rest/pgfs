# コーディング規約

pgfs リポジトリの C# コードに適用される規約です。

機械的に強制可能な項目は [`.editorconfig`](../.editorconfig) が**正**であり、本ドキュメントはそこから読み取れない**意図**と**例**を補足します。ReSharper / Rider の `*.DotSettings` も `.editorconfig` を補強しており、IDE 上でリアルタイムに警告が出ます。

---

## 基本

| 項目 | 値 | 出典 |
|---|---|---|
| インデント | タブ (幅 4) | `.editorconfig` |
| 改行コード | LF | `.editorconfig` |
| 文字コード | UTF-8 (BOM 無し) | `.editorconfig` |
| 末尾改行 | 必須 | `.editorconfig` |
| 末尾空白 | 削除 | `.editorconfig` |
| 最大行長 | 無制限 (長すぎる行は `chop_if_long` で適宜折る) | `.editorconfig` |

---

## 命名

| 対象 | 規則 | 例 |
|---|---|---|
| namespace | `Pgfs.{Module}.{SubModule}` の階層 | `Pgfs.Lib.Config`, `Pgfs.Lib.Models`, `Pgfs.Mount`, `Pgfs.Assign` |
| ファイル名 | 主要クラス名と一致 (1 ファイル 1 主要クラス) | `RootConfig.cs` ← `class RootConfig` |
| クラス / メソッド | PascalCase | `InodeCache`, `GetByPath` |
| ローカル変数 / パラメータ | camelCase | `var inode`, `string path` |
| private フィールド | camelCase (アンダースコア接頭辞は使わない) | `this.cache` |
| 定数 / enum 値 | PascalCase | `Mode.S_IFDIR`, `Level.Trace` |
| アセンブリ名 | 小文字 + ドット区切り (csproj の `<AssemblyName>`) | `lib.pgfs.dll`, `mkfs.pgfs.exe` |

### `this.` の明示

インスタンスメンバへのアクセスは **常に `this.` を明示**する。グローバル / ローカル / インスタンスメンバの区別を視覚的にする目的。

```csharp
this.cache.Initialize();      // OK
cache.Initialize();           // NG (this 省略)
```

---

## 修飾子の順序

`.editorconfig` の `csharp_preferred_modifier_order` に従う:

```text
private, public, abstract, protected, file, new, internal, static,
virtual, sealed, override, readonly, extern, unsafe, volatile, async, required
```

---

## ブレースと制御構文

### ブレース必須

`if` / `else` / `for` / `foreach` / `while` / `do` / `using` / `lock` / `fixed` には**常にブレースを付ける**。1 文で済む内容は同じ行に畳んでよい。

```csharp
// OK (単行ブレース)
if (cond) { Foo(); }

// OK (複数行ブレース)
if (cond) {
    Foo();
    Bar();
}

// NG (ブレース無し)
if (cond) Foo();

// NG (新規行に裸の文)
if (cond)
    Foo();
```

`.editorconfig` での強制:

```ini
csharp_prefer_braces = true:warning
csharp_preserve_single_line_blocks = true
resharper_braces_for_dowhile = required
resharper_braces_for_fixed = required
resharper_braces_for_for = required
resharper_braces_for_foreach = required
resharper_braces_for_ifelse = required
resharper_braces_for_lock = required
resharper_braces_for_using = required
resharper_braces_for_while = required
resharper_keep_existing_embedded_block_arrangement = true
```

### メソッド本体・アクセサ

- メソッドのオープン中括弧は **同じ行**
- シンプルなアクセサ (1 行で済む getter / 1 行で済む式本体メソッド) は **1 行**
- 複雑な式は `chop_if_long` で折り返し

```csharp
// OK
public Inode GetRoot() => this.inodeCache.GetRoot();

public bool IsOpen {
    get { return this.connection != null && this.connection.State == ConnectionState.Open; }
}

public async Task<int> ProcessAsync(int id) {
    // ...
}
```

---

## 条件分岐ルール

このリポジトリの **核となる条件分岐スタイル**。フロー制御の意図を明示するため、以下を強制します。

### 規約

1. `if` 本体は以下のいずれかの形:
   - **末尾フロー脱出形**: 最後の文が `return` / `break` / `continue` / `throw` / `goto`。手前に任意の文 (副作用・ローカル変数宣言・ネスト `if` 等) を **0 個以上**置いて良い
   - **単一文のみ**: 1 文だけ。種類は問わない (代入・メソッド呼び出し・`+=` 等、ネスト `if`、`switch` 文も可)

`goto` は **同一スコープ内のラベルへのジャンプ**に限り flow-exit として認める。ループ末尾の cleanup ラベルに飛ばす構造化イディオム ([RemoveRange](../src/lib/src/Collections/FirstList.cs) で実例あり) を許容するため。`goto` を関数境界を越えて使うのは引き続き禁止 (C# はそもそも不可)。
2. **ログ出力はカウントしない (自由)**。`Console.Write*` / `Console.Error.Write*` / `Logger.*` / `Debug.Write*` / `Trace.Write*` の呼び出しは「副作用」として数えず、何文あっても規約に対してカウントしない。診断出力はロジックと直交するため
3. フロー脱出系は **否定 (fail-fast) を優先**。肯定 (fast-path) のほうが明らかに読みやすければ可
4. **`else` 禁止**
5. 多分岐 / 2 文以上のロジック副作用 (= 非ログ副作用) は **`switch`** で書く
6. **三項演算子 `?:` 禁止** (値選択も `switch` 式)
7. 推奨: `if` 本体は概ね **5 文以内** (ログを除く)。長くなる場合はメソッド抽出を検討する

### OK 例

#### フロー脱出 (推奨パターン)

```csharp
public Inode? Load(long id) {
    if (id < 0) { return null; }                                  // 否定: 無効値
    if (!this.connection.IsOpen) { throw new InvalidOperationException(); }
    if (this.cache.TryGetValue(id, out var hit)) { return hit; }  // 肯定: fast-path
    // ... メイン処理 ...
}

foreach (var inode in this.ListChildren(parentId)) {
    if (inode.Name == ".") { continue; }                          // ループスキップ
    if (inode.IsCorrupt) { break; }                               // ループ脱出
    Process(inode);
}
```

#### 末尾フロー脱出 + 副作用 N 文 (メッセージ系)

```csharp
// 副作用 1 文 + 脱出
if (settings.Help.Value == true) {
    ShowHelp();
    return 0;
}

// 副作用 1 文 (ログ) + 脱出 — ログはノーカウントなのでロジック副作用 0 + return
if (OperatingSystem.IsWindows()) {
    Console.Error.WriteLine("mount.pgfs is not available on Windows. Use pgfs.assign (the DokanNet version).");
    return 1;
}

// ログ複数行 + 脱出 — ログはいくつあってもカウント外
if (!Tmds.Fuse.Fuse.CheckDependencies()) {
    Console.Error.WriteLine("FUSE dependencies not found:");
    Console.Error.WriteLine(Tmds.Fuse.Fuse.InstallationInstructions);
    return 1;
}

// ログ + ロジック副作用 + 脱出
if (input is null) {
    Logger.Warning("Input is null, using default");
    input = DefaultValue;
    return Process(input);
}
```

#### 副作用 1 文のみ (許容パターン)

```csharp
if (config.UseCache) { this.cache.Initialize(); }            // 単側分岐
if (this.cache is null) { this.cache = new Cache(); }        // 遅延初期化 (??= も可)
if (input is null) { input = DefaultValue; }                 // 引数デフォルト埋め
if (Logger.IsTraceEnabled) { Logger.Trace("...", arg); }     // ロガーガード (ログ自由化により本体は実質空)
if (this.dirty) { this.flushCount += 1; }                    // カウンタ増加
```

#### 多分岐は `switch`

```csharp
// 値を返す: switch 式
var label = priority switch {
    > 8 => "urgent",
    > 4 => "high",
    _ => "normal"
};

// 副作用を起こす: switch 文
switch (priority) {
    case > 8: SendAlert(); break;
    case > 4: NotifyTeam(); break;
    default: break;
}
```

### NG 例

```csharp
// NG: else
if (a) { X(); } else { Y(); }
//                ^^^^^^^^^^

// NG: else-if chain
if (n > 100) { Huge(); } else if (n > 50) { Big(); } else { Normal(); }

// NG: 三項演算子
var label = isAdmin ? "admin" : "user";
//                  ^^^^^^^^^^^^^^^^^^^

// NG: ロジック副作用 2 文、脱出なし (ログを除いた "実効" 副作用が 2)
if (cond) {
    Foo();
    Bar();      // ← 2 つ目のロジック副作用、しかも脱出なし → switch かメソッド抽出
}

// NG: ログ自由化を悪用してもダメな例
if (cond) {
    Logger.Info("doing A");
    Foo();
    Logger.Info("doing B");
    Bar();      // ← ログ 2 + ロジック副作用 2、最後が脱出ではない → NG
}
```

### 「ログ」の定義

下記いずれかの呼び出しは「ログ出力」として副作用カウントの対象外:

- `Console.Write*` / `Console.WriteLine` / `Console.Error.Write*`
- `Logger.*` (本リポジトリ自作のロガー [`Pgfs.Lib.Logging`](../src/lib/src/Logging/))
- `System.Diagnostics.Debug.Write*` / `Debug.WriteLine`
- `System.Diagnostics.Trace.Write*` / `Trace.WriteLine`
- 上記と同等の「副作用が出力先メッセージのみ」である呼び出し

判定基準: **「呼び出しを削除しても外部状態 (DB / ファイル / ネットワーク / 共有メモリ / プロセス制御) に影響しない」**ものはログ扱い。データ書き換えを伴うものはロジック副作用として通常カウントする。

### 言語機能の許容範囲

`?:` 以外の `?` 系演算子は**全て許可**:

| 演算子 | 名称 | 例 |
|---|---|---|
| `??` | null 合体 | `name ?? "Guest"` |
| `??=` | null 合体代入 | `cache ??= new Cache()` |
| `?.` | null 条件アクセス | `user?.Name` |
| `?[]` | null 条件インデクサ | `list?[0]` |
| `?? throw ...` | throw 式 | `x ?? throw new ArgumentNullException()` |
| `is` / `is not` | パターンマッチ | `x is null`, `x is Foo f` |
| `as` | 安全キャスト | `x as Foo` |
| `switch` 式 / 文 | 多分岐 | `x switch { ... }` |

### 強制方法

| ルール | 自動強制 |
|---|---|
| 1. if 本体が単一文 | △ (Roslyn アナライザを書けば可) |
| 2. 単一文の内容種別 | △ (同上) |
| 3. 否定優先 | ✗ (レビュー時の人間判断) |
| 4. `else` 禁止 | △ (アナライザで可) |
| 5. `switch` 使用 | △ (else-if chain の検出は可) |
| 6. ternary 禁止 | ✅ (`.editorconfig` で抑制可能) |

機械強制が効くのは現状 **6 (ternary 禁止)** のみ。残りはレビューで担保する。将来必要になれば `Pgfs.Analyzers` 的な小さなプロジェクトを追加して Roslyn アナライザで強制する手がある。

### なぜこのルールか

- `if` を見たら必ず「単一の何かが起きる」と確信できる
- ネスト深度の上限が事実上 1 (else / else-if のピラミッドが消える)
- 多分岐は明示的に `switch` になり、網羅性チェック (`_ =>` / `default:`) が効く
- nested ternary が排除される
- C# 9+ の switch expression / pattern matching を活用する方向に寄る

---

## リズム — ファサードからガードを内側に押し込む (推奨)

**Must ではなくソフトな推奨**。複数の `await` / 順次呼び出しを並べる「ファサード」的なメソッドでは、**呼び出し側に `if` を散らさず、ガードは呼ばれる側の関数の先頭に置く**と、ファサードが「無条件の連続呼び出し」として読めて目線が止まらなくなる ("リズム" が出る)。

### Before (呼び出し側に if を散らす)

```csharp
public async Task InitializeAsync() {
    await this.EnsureUserAsync();
    await this.EnsureTablespaceAsync();
    if (this.config.Clean) {
        await this.DropDatabaseAsync();
    }
    await this.EnsureDatabaseAsync();
    // ... テーブル作成 ...
    if (this.config.Database.Citus) {
        await this.SetupCitusDistributionAsync(prefix);
    }
    await this.InsertRootInodeAsync(prefix);
}
```

### After (呼ばれる側の先頭でガード)

```csharp
public async Task InitializeAsync() {
    await this.EnsureUserAsync();
    await this.EnsureTablespaceAsync();
    await this.DropDatabaseAsync();      // ← 内部で if (!Clean) return
    await this.EnsureDatabaseAsync();
    // ... テーブル作成 ...
    await this.SetupCitusDistributionAsync(prefix);  // ← 内部で if (!Citus) return
    await this.InsertRootInodeAsync(prefix);
}

private async Task DropDatabaseAsync() {
    if (!this.config.Clean) { return; }   // ← ここに移動
    // ... 実体 ...
}

private async Task SetupCitusDistributionAsync(string prefix) {
    if (!this.config.Database.Citus) { return; }   // ← ここに移動
    // ... 実体 ...
}
```

### なぜ

- ファサード側が **「(1) ユーザ確保 → (2) DB drop → (3) DB ensure → (4) テーブル作成 → (5) Citus 化 → (6) root inode 投入」** の順序ストーリーとして一望できる
- 「この呼び出しは条件付きで動く」というのは呼ばれる側の責務 — 呼び出し側がいちいち知る必要が無い
- 条件分岐ルールの「`if` 本体は単一文 + 末尾脱出」とも整合する (ガード 1 行 + `return` で終わる)
- 呼び出しを増やしても if が増えない → メソッド追加時に意識が分散しない
- XML doc コメントに「`--xxx` 未指定なら何もしない」を 1 行書くだけで、呼び出し側のレビュアーは「ファサードの並びだけ追えば良い」と理解できる

### このリズムを **崩していい** ケース

- **ガードが「どの関数を呼ぶか」自体を分岐する**: `if (cond) { A(); } else { B(); }` のように対象が変わる場合は呼び出し側の if が自然 (押し下げると重複コードか追加引数が必要)
- **ガードが呼び出し側ローカル変数に依存する**: 呼ばれる側からはアクセス手段が無い情報で分岐するなら、ファサードに残すしかない
- **早期 fail が必要 (例: バリデーション失敗で例外を投げる)**: 呼び出し側で `if (!valid) throw` が読みやすいなら無理に押し込まない

### 関連

- 条件分岐ルールの「**フロー脱出系は否定 (fail-fast) を優先**」とセット運用。ガード本体は `if (!cond) { return; }` の 1 行末尾脱出形で書く
- ホットパスのロガーガード (`if (Logger.IsTraceEnabled) { Logger.Trace(...) }`) は **押し込めない** (ガード自体に意味があり、呼び出し側で見えていてほしい)

---

## コメント方針

### 残すべきコメント

- **意図 / Why**: なぜそのコードが必要か (背景・トレードオフ・過去のバグ等)
- **非自明な制約**: 「ここを X にすると Y で死ぬ」のような hard-won knowledge
- **TODO / FIXME**: 明示的に残された未完了部分。担当者やチケット番号があれば併記

### 削除すべきコメント

- 関数名やコードから自明な「何をしているか」だけの説明
- 古いコードの**意図のないコメントアウト残骸**

### コメントアウトされたコードの扱い

`// ...` でコメントアウトされている**大ブロック**は、過去の試行で**意図的に残されたものが多い**。**勝手に削除しない**。

削除する場合は、コミットメッセージに「なぜ消したか」を明記する。

### XML Doc コメント

- public API には可能な限り `<summary>` を付ける
- internal / private は必要に応じて
- `<remarks>` で実装上の注意点 (パフォーマンス、トランザクション要件、プラットフォーム依存等) を補足

---

## ロガー使用

ホットパスで `Logger.Trace(...)` / `Logger.Debug(...)` を呼ぶときは、**必ずガード句**を入れる:

```csharp
if (Logger.IsTraceEnabled) { Logger.Trace("...", arg1, arg2); }
```

理由: `Logger.Trace(...)` は `params object?[]` で配列確保とボクシングが発生する。秒間 1000+ 回呼ばれる FUSE / Dokan コールバック内では計測可能なコスト。`IsTraceEnabled` / `IsDebugEnabled` の早期判定で回避する。

---

## 参考

- [`.editorconfig`](../.editorconfig) — 機械強制可能な全設定の正
- [.NET Runtime Coding Style](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md) — 参考にした上流 (一部相違あり: タブ vs スペース等)
