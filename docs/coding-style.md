# Coding conventions

The conventions that apply to the C# code in the pgfs repository.

For mechanically enforceable items, [`.editorconfig`](../.editorconfig) is **authoritative**; this document supplements the **intent** and **examples** that cannot be read from it. ReSharper / Rider's `*.DotSettings` also reinforce `.editorconfig` and surface warnings in real time in the IDE.

---

## Basics

| Item | Value | Source |
|---|---|---|
| indentation | tab (width 4) | `.editorconfig` |
| line ending | LF | `.editorconfig` |
| encoding | UTF-8 (no BOM) | `.editorconfig` |
| trailing newline | required | `.editorconfig` |
| trailing whitespace | trimmed | `.editorconfig` |
| max line length | unlimited (overly long lines wrapped as appropriate via `chop_if_long`) | `.editorconfig` |

---

## Naming

| Target | Rule | Example |
|---|---|---|
| namespace | the `Pgfs.{Module}.{SubModule}` hierarchy | `Pgfs.Lib.Config`, `Pgfs.Lib.Models`, `Pgfs.Mount`, `Pgfs.Assign` |
| file name | matches the primary class name (1 file, 1 primary class) | `RootConfig.cs` ← `class RootConfig` |
| class / method | PascalCase | `InodeCache`, `GetByPath` |
| local variable / parameter | camelCase | `var inode`, `string path` |
| private field | camelCase (no underscore prefix) | `this.cache` |
| constant / enum value | PascalCase | `Mode.S_IFDIR`, `Level.Trace` |
| assembly name | lowercase + dot-separated (the csproj `<AssemblyName>`) | `lib.pgfs.dll`, `mkfs.pgfs.exe` |

### Explicit `this.`

Access to instance members **always uses an explicit `this.`**. The purpose is to make the distinction between global / local / instance members visually clear.

```csharp
this.cache.Initialize();      // OK
cache.Initialize();           // NG (this omitted)
```

---

## Modifier order

Follows `.editorconfig`'s `csharp_preferred_modifier_order`:

```text
private, public, abstract, protected, file, new, internal, static,
virtual, sealed, override, readonly, extern, unsafe, volatile, async, required
```

---

## Braces and control flow

### Braces required

`if` / `else` / `for` / `foreach` / `while` / `do` / `using` / `lock` / `fixed` **always get braces**. Content that fits in one statement may be folded onto the same line.

```csharp
// OK (single-line brace)
if (cond) { Foo(); }

// OK (multi-line brace)
if (cond) {
    Foo();
    Bar();
}

// NG (no brace)
if (cond) Foo();

// NG (a bare statement on a new line)
if (cond)
    Foo();
```

Enforcement in `.editorconfig`:

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

### Method body / accessors

- a method's open brace is on the **same line**
- a simple accessor (a one-line getter / a one-line expression-bodied method) is **one line**
- complex expressions wrap via `chop_if_long`

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

## Branching rules

The **core branching style** of this repository. To make the intent of flow control explicit, the following are enforced.

### Rules

1. An `if` body is one of these forms:
   - **tail flow-exit form**: the last statement is `return` / `break` / `continue` / `throw` / `goto`. Any statements (side effects, local-variable declarations, nested `if`, etc.) may precede it, **0 or more**.
   - **single statement only**: exactly 1 statement. Any kind (assignment, method call, `+=` etc., nested `if`, a `switch` statement are all fine).

`goto` is accepted as a flow-exit **only for a jump to a label in the same scope**. This permits the structured idiom of jumping to a cleanup label at the end of a loop (a real example exists in [RemoveRange](../src/lib/src/Collections/FirstList.cs)). Using `goto` across function boundaries remains forbidden (C# disallows it anyway).
2. **Log output is not counted (free)**. Calls to `Console.Write*` / `Console.Error.Write*` / `Logger.*` / `Debug.Write*` / `Trace.Write*` are not counted as "side effects" — any number of them does not count against the rule. Diagnostic output is orthogonal to logic.
3. Prefer **negation (fail-fast)** for flow-exit. The affirmative (fast-path) is fine if it is clearly more readable.
4. **`else` forbidden**.
5. Multi-way branching / 2-or-more logic side effects (= non-log side effects) are written with a **`switch`**.
6. **Ternary operator `?:` forbidden** (value selection is also a `switch` expression).
7. Recommended: an `if` body is roughly **within 5 statements** (excluding logs). If it grows longer, consider extracting a method.

### OK examples

#### Flow-exit (recommended pattern)

```csharp
public Inode? Load(long id) {
    if (id < 0) { return null; }                                  // negation: invalid value
    if (!this.connection.IsOpen) { throw new InvalidOperationException(); }
    if (this.cache.TryGetValue(id, out var hit)) { return hit; }  // affirmative: fast-path
    // ... main work ...
}

foreach (var inode in this.ListChildren(parentId)) {
    if (inode.Name == ".") { continue; }                          // loop skip
    if (inode.IsCorrupt) { break; }                               // loop exit
    Process(inode);
}
```

#### Tail flow-exit + N side-effect statements (message-style)

```csharp
// 1 side-effect statement + exit
if (settings.Help.Value == true) {
    ShowHelp();
    return 0;
}

// 1 side-effect statement (log) + exit — logs are not counted, so 0 logic side effects + return
if (OperatingSystem.IsWindows()) {
    Console.Error.WriteLine("mount.pgfs is not available on Windows. Use pgfs.assign (the DokanNet version).");
    return 1;
}

// multiple log lines + exit — logs do not count, no matter how many
if (!Tmds.Fuse.Fuse.CheckDependencies()) {
    Console.Error.WriteLine("FUSE dependencies not found:");
    Console.Error.WriteLine(Tmds.Fuse.Fuse.InstallationInstructions);
    return 1;
}

// log + logic side effect + exit
if (input is null) {
    Logger.Warning("Input is null, using default");
    input = DefaultValue;
    return Process(input);
}
```

#### Single side-effect statement only (allowed pattern)

```csharp
if (config.UseCache) { this.cache.Initialize(); }            // one-sided branch
if (this.cache is null) { this.cache = new Cache(); }        // lazy init (??= also fine)
if (input is null) { input = DefaultValue; }                 // fill an argument default
if (Logger.IsTraceEnabled) { Logger.Trace("...", arg); }     // logger guard (with free logging, the body is effectively empty)
if (this.dirty) { this.flushCount += 1; }                    // counter increment
```

#### Multi-way is `switch`

```csharp
// returns a value: switch expression
var label = priority switch {
    > 8 => "urgent",
    > 4 => "high",
    _ => "normal"
};

// produces side effects: switch statement
switch (priority) {
    case > 8: SendAlert(); break;
    case > 4: NotifyTeam(); break;
    default: break;
}
```

### NG examples

```csharp
// NG: else
if (a) { X(); } else { Y(); }
//                ^^^^^^^^^^

// NG: else-if chain
if (n > 100) { Huge(); } else if (n > 50) { Big(); } else { Normal(); }

// NG: ternary
var label = isAdmin ? "admin" : "user";
//                  ^^^^^^^^^^^^^^^^^^^

// NG: 2 logic side effects, no exit (the "effective" side effects excluding logs are 2)
if (cond) {
    Foo();
    Bar();      // <- 2nd logic side effect, and no exit -> switch or extract a method
}

// NG: abusing free logging is still not allowed
if (cond) {
    Logger.Info("doing A");
    Foo();
    Logger.Info("doing B");
    Bar();      // <- 2 logs + 2 logic side effects, the last is not an exit -> NG
}
```

### Definition of "log"

Any of these calls are exempt from the side-effect count as "log output":

- `Console.Write*` / `Console.WriteLine` / `Console.Error.Write*`
- `Logger.*` (this repository's in-house logger [`Pgfs.Lib.Logging`](../src/lib/src/Logging/))
- `System.Diagnostics.Debug.Write*` / `Debug.WriteLine`
- `System.Diagnostics.Trace.Write*` / `Trace.WriteLine`
- calls equivalent to the above whose "only side effect is an output message"

Criterion: anything for which **"removing the call has no effect on external state (DB / file / network / shared memory / process control)"** is treated as a log. Anything that mutates data is counted normally as a logic side effect.

### Allowed language features

All `?`-family operators other than `?:` are **allowed**:

| Operator | Name | Example |
|---|---|---|
| `??` | null coalescing | `name ?? "Guest"` |
| `??=` | null-coalescing assignment | `cache ??= new Cache()` |
| `?.` | null-conditional access | `user?.Name` |
| `?[]` | null-conditional indexer | `list?[0]` |
| `?? throw ...` | throw expression | `x ?? throw new ArgumentNullException()` |
| `is` / `is not` | pattern matching | `x is null`, `x is Foo f` |
| `as` | safe cast | `x as Foo` |
| `switch` expression / statement | multi-way | `x switch { ... }` |

### Enforcement

| Rule | Auto-enforced |
|---|---|
| 1. if body is a single statement | △ (possible with a Roslyn analyzer) |
| 2. kind of the single statement | △ (same) |
| 3. prefer negation | ✗ (human judgment at review time) |
| 4. `else` forbidden | △ (possible with an analyzer) |
| 5. use `switch` | △ (else-if chain detection is possible) |
| 6. ternary forbidden | ✅ (suppressible via `.editorconfig`) |

Mechanical enforcement currently works only for **6 (ternary forbidden)**. The rest are upheld in review. If needed in the future, a small project like `Pgfs.Analyzers` could enforce them with Roslyn analyzers.

### Why these rules

- seeing an `if`, you can be sure "exactly one thing happens"
- the nesting depth ceiling is effectively 1 (the else / else-if pyramid disappears)
- multi-way branching becomes an explicit `switch`, where exhaustiveness checks (`_ =>` / `default:`) work
- nested ternaries are eliminated
- it leans toward leveraging C# 9+ switch expressions / pattern matching

---

## Rhythm — push guards inward from the facade (recommended)

**A soft recommendation, not a Must**. In "facade"-style methods that line up several `await`s / sequential calls, **do not scatter `if`s on the caller side; put the guard at the top of the called function**. The facade then reads as an "unconditional sequence of calls" and the eye does not stop ("rhythm" emerges).

### Before (scattering ifs on the caller side)

```csharp
public async Task InitializeAsync() {
    await this.EnsureUserAsync();
    await this.EnsureTablespaceAsync();
    if (this.config.Clean) {
        await this.DropDatabaseAsync();
    }
    await this.EnsureDatabaseAsync();
    // ... create tables ...
    if (this.config.Database.Citus) {
        await this.SetupCitusDistributionAsync(prefix);
    }
    await this.InsertRootInodeAsync(prefix);
}
```

### After (guard at the top of the called function)

```csharp
public async Task InitializeAsync() {
    await this.EnsureUserAsync();
    await this.EnsureTablespaceAsync();
    await this.DropDatabaseAsync();      // <- internally if (!Clean) return
    await this.EnsureDatabaseAsync();
    // ... create tables ...
    await this.SetupCitusDistributionAsync(prefix);  // <- internally if (!Citus) return
    await this.InsertRootInodeAsync(prefix);
}

private async Task DropDatabaseAsync() {
    if (!this.config.Clean) { return; }   // <- moved here
    // ... body ...
}

private async Task SetupCitusDistributionAsync(string prefix) {
    if (!this.config.Database.Citus) { return; }   // <- moved here
    // ... body ...
}
```

### Why

- the facade side can be taken in at a glance as the ordered story **"(1) ensure user → (2) drop DB → (3) ensure DB → (4) create tables → (5) Citus-ize → (6) insert root inode"**
- "this call runs conditionally" is the responsibility of the called function — the caller need not know each time
- it aligns with the branching rule "an `if` body is a single statement + tail exit" (a 1-line guard ending in `return`)
- adding calls does not add ifs → attention does not fragment when a method is added
- writing a single line in the XML doc comment, "does nothing if `--xxx` is not specified", lets the caller's reviewer understand "you only need to follow the facade's sequence"

### Cases where it is **OK to break** this rhythm

- **the guard itself branches on "which function to call"**: when the target changes, as in `if (cond) { A(); } else { B(); }`, an `if` on the caller side is natural (pushing it down would need duplicate code or extra arguments)
- **the guard depends on a caller-local variable**: if it branches on information the called side has no way to access, it has to stay in the facade
- **early fail is needed (e.g. throw on validation failure)**: if `if (!valid) throw` reads more clearly on the caller side, do not force it inward

### Related

- runs alongside the branching rule "**prefer negation (fail-fast) for flow-exit**". A guard body is written as the 1-line tail-exit form `if (!cond) { return; }`.
- the hot-path logger guard (`if (Logger.IsTraceEnabled) { Logger.Trace(...) }`) **cannot be pushed inward** (the guard itself is meaningful and should be visible on the caller side)

---

## Comment policy

### Comments to keep

- **intent / Why**: why the code is needed (background, trade-offs, past bugs, etc.)
- **non-obvious constraints**: hard-won knowledge like "setting this to X dies at Y"
- **TODO / FIXME**: explicitly left incomplete parts. Note the owner or ticket number if any

### Comments to remove

- explanations of "what it does" only, obvious from the function name or the code
- aimless commented-out remnants of old code

### Handling commented-out code

A **large block** commented out with `// ...` is **often intentionally left from a past attempt**. **Do not delete it on your own.**

When you do delete (when it is clear, e.g. the whole file is commented out), state "why it was deleted" in the commit message.

### XML doc comments

- attach a `<summary>` to public APIs wherever possible
- internal / private as needed
- use `<remarks>` to supplement implementation notes (performance, transaction requirements, platform dependence, etc.)

---

## Logger usage

When calling `Logger.Trace(...)` / `Logger.Debug(...)` on a hot path, **always add a guard clause**:

```csharp
if (Logger.IsTraceEnabled) { Logger.Trace("...", arg1, arg2); }
```

Reason: `Logger.Trace(...)` is `params object?[]`, which allocates an array and boxes. Inside FUSE / Dokan callbacks called 1000+/sec it is a measurable cost. Avoid it with an early `IsTraceEnabled` / `IsDebugEnabled` test.

---

## References

- [`.editorconfig`](../.editorconfig) — the authoritative source of all mechanically enforceable settings
- [.NET Runtime Coding Style](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md) — the upstream we referenced (with some differences: tabs vs spaces, etc.)
