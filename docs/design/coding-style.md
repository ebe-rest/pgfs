# The coding conventions

> **Route**: [docs/README.md](../README.md) › **this document**
>
> **What this document is the source of truth for**: of the conventions applied to pgfs's C# code,
> **the intent and the examples that cannot be read out of `.editorconfig`**. The naming and how the namespaces
> are cut, where an enum lives and its behaviour, being explicit with `this.`, the order of the modifiers, the
> braces, the conditional rules (no `else`, no ternary, and turning a multi-way branch into a `switch`), the
> rhythm of pushing a guard into the callee, the comment policy and the logger guard on a hot path all belong
> here.
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [../../.editorconfig](../../.editorconfig) | The settings that can be enforced mechanically (the indentation, the line endings, the encoding). If a value disagrees, that one wins |
> | [../architecture.md](../architecture.md) | The project structure, the module split, the namespace assignment and the build procedure |
>
> The upstream reference was the
> [.NET Runtime Coding Style](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md),
> and some of it is deliberately departed from (tabs against spaces and so on).

The conventions applied to the C# code of the pgfs repository.

[`.editorconfig`](../../.editorconfig) is **the source of truth** for anything that can be enforced
mechanically, and this document supplements it with **the intent** and **the examples** that cannot be read out
of it. ReSharper / Rider's `*.DotSettings` reinforce the `.editorconfig` too, and warnings appear in real time
in the IDE.

---

## The basics

| The item | The value | Where it comes from |
|---|---|---|
| The indentation | Tabs (a width of 4) | `.editorconfig` |
| The line ending | LF | `.editorconfig` |
| The encoding | UTF-8 (no BOM) | `.editorconfig` |
| A trailing newline | Mandatory | `.editorconfig` |
| Trailing whitespace | Removed | `.editorconfig` |
| The maximum line length | Unlimited (an over-long line is wrapped as appropriate with `chop_if_long`) | `.editorconfig` |

---

## The naming

| The subject | The rule | An example |
|---|---|---|
| A namespace | The hierarchy `Pgfs.{Module}.{SubModule}` | `Pgfs.Core.Config`, `Pgfs.Core.Models`, `Pgfs.Mount`, `Pgfs.Assign` |
| A file name | Matching the main class name (one main class per file) | `RootConfig.cs` <- `class RootConfig` |
| A class or method | PascalCase | `InodeCache`, `GetByPath` |
| A local variable or parameter | camelCase | `var inode`, `string path` |
| A private field | camelCase (no underscore prefix) | `this.cache` |
| A constant or enum value | PascalCase | `Mode.S_IFDIR`, `Level.Trace` |
| An assembly name | Lowercase with dots (the csproj's `<AssemblyName>`) | `core.pgfs.dll`, `mkfs.pgfs.exe` |

### Where an enum lives and its behaviour

> **The convention**
> 1. **An enum that has a string-to-value conversion (parse / format) keeps that conversion in the same file as
>    the enum.**
> 2. **Whether it is wrapped in a class does not matter.** Either a `static class Xxx { enum Enum }` or a bare
>    `enum Xxx` plus a `static class XxxText` in the same file is fine. **The wrapping is outside the
>    convention.**
> 3. **An enum with no behaviour is declared bare.**

**"Wrapping it gives the behaviour somewhere to live" does not hold**, which is the conclusion drawn from the
real thing. Counting the 10 enums in `src/core` plus `src/fuse` (2 of which come from libfuse's P/Invoke and are
out of scope):

| The category | The real thing | What it actually is |
|---|---|---|
| Wrapped, with behaviour | [`Level.Enum`](../../src/core/src/Logging/Level.cs) | **Just one.** It has `Parse(string)` / `Parse(int)` |
| Wrapped, with **no** behaviour | [`SettingLoggingCycle.Enum`](../../src/core/src/Models/SettingLoggingCycle.cs) (12 lines) / [`SettingLoggingKind.Enum`](../../src/core/src/Models/SettingLoggingKind.cs) (14 lines) | **Merely wrapped in an empty class.** The contents are the enum declaration only |
| Bare | `ReloadPolicy` / `SaveTarget` / `Tool` / `CoalesceResult` / `PendingState` / `PendingAddResult` | No behaviour |

**The decisive one is `SettingLoggingCycle`**: **it is wrapped and yet its parse and format are in
[`Field.cs`](../../src/core/src/Config/Field.cs)** (`"hourly" => ...Enum.Hourly` and
`...Enum.Hourly => "hourly"` line up in two separate switches). Even with a wrapper, what is not put there
scatters. Conversely, `Level` is not scattered not because it is wrapped but **because its `Parse` was put in
one place**.

**The wrapping has a double cost**:

1. The name gets one level deeper. `Field.cs` has 15 or more lines of
   `Pgfs.Core.Models.SettingLoggingCycle.Enum.Hourly` (4 levels including the namespace).
2. **Aliases get written to hide it.** `Level` has **8** of
   `public const Enum Trace = Enum.Trace;` - nobody wants to write `Level.Enum.Trace`, so `Level.Trace` is
   rebuilt. **With a bare `Level` enum it would have been `Level.Trace` from the start.**

What has to be unified is therefore **not the syntax but the ownership of the behaviour**.

**A caution on counting**: do not measure the scatter by "how many files reference that enum". **The
declarations and the decisions get mixed together.** `SaveTarget` appears in 13 files, but most of them are
**declarations** in `Schema.cs` and the `*Config.cs` files, which is not scatter. **Count only the places that
look at the value and branch on it.**

### Being explicit with `this.`

Access to an instance member is **always explicit with `this.`**, to make the distinction between a global, a
local and an instance member visible.

```csharp
this.cache.Initialize();      // OK
cache.Initialize();           // NG (this omitted)
```

---

## The order of the modifiers

Follow the `.editorconfig`'s `csharp_preferred_modifier_order`:

```text
private, public, abstract, protected, file, new, internal, static,
virtual, sealed, override, readonly, extern, unsafe, volatile, async, required
```

---

## The braces and the control statements

### Braces are mandatory

`if` / `else` / `for` / `foreach` / `while` / `do` / `using` / `lock` / `fixed` **always take braces**. Content
that fits in one statement may be folded onto the same line.

```csharp
// OK (a single-line brace)
if (cond) { Foo(); }

// OK (a multi-line brace)
if (cond) {
    Foo();
    Bar();
}

// NG (no braces)
if (cond) Foo();

// NG (a bare statement on a new line)
if (cond)
    Foo();
```

Enforcement through `.editorconfig`:

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

### Method bodies and accessors

- A method's opening brace goes **on the same line**
- A simple accessor (a one-line getter or a one-line expression-bodied method) goes **on one line**
- A complex expression is wrapped with `chop_if_long`

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

## The conditional rules

**The core conditional style** of this repository. To make the intent of the flow control explicit, the
following is enforced.

### The convention

1. The body of an `if` takes one of these shapes:
   - **A trailing flow exit**: the last statement is a `return` / `break` / `continue` / `throw` / `goto`.
     **Zero or more** arbitrary statements (side effects, local variable declarations, a nested `if` and so on)
     may come before it
   - **A single statement only**: exactly one statement. Its kind does not matter (an assignment, a method call,
     a `+=` and so on; a nested `if` and a `switch` statement are fine too)

A `goto` is accepted as a flow exit **only for a jump to a label in the same scope**, to allow the structured
idiom of jumping to a cleanup label at the end of a loop (there is a real example in
[RemoveRange](../../src/core/src/Collections/FirstList.cs)). Using a `goto` across a function boundary stays
forbidden (C# does not allow it anyway).
2. **Log output is not counted (it is free).** Calls to `Console.Write*` / `Console.Error.Write*` / `Logger.*` /
   `Debug.Write*` / `Trace.Write*` are not counted as "side effects", and however many there are they do not
   count against the convention. Diagnostic output is orthogonal to the logic
3. A flow exit **prefers the negation (fail-fast)**. The affirmative (the fast path) is fine when it is clearly
   easier to read
4. **`else` is forbidden**
5. A multi-way branch, or 2 or more logic side effects (= non-log side effects), is written with a **`switch`**
6. **The ternary operator `?:` is forbidden** (a value choice is a `switch` expression too)
7. Recommended: the body of an `if` is roughly **within 5 statements** (excluding the logs). When it gets longer,
   consider extracting a method

### The OK examples

#### A flow exit (the recommended pattern)

```csharp
public Inode? Load(long id) {
    if (id < 0) { return null; }                                  // the negation: an invalid value
    if (!this.connection.IsOpen) { throw new InvalidOperationException(); }
    if (this.cache.TryGetValue(id, out var hit)) { return hit; }  // the affirmative: a fast path
    // ... the main processing ...
}

foreach (var inode in this.ListChildren(parentId)) {
    if (inode.Name == ".") { continue; }                          // skipping in a loop
    if (inode.IsCorrupt) { break; }                               // breaking out of a loop
    Process(inode);
}
```

#### A trailing flow exit plus N side effects (the message cases)

```csharp
// one side effect plus the exit
if (settings.Help.Value == true) {
    ShowHelp();
    return 0;
}

// one side effect (a log) plus the exit - a log is not counted, so it is 0 logic side effects plus a return
if (OperatingSystem.IsWindows()) {
    Console.Error.WriteLine("mount.pgfs cannot be used on Windows. Use assign.pgfs (the DokanNet build).");
    return 1;
}

// several log lines plus the exit - however many logs there are they are not counted
if (!Pgfs.Fuse.Fuse.CheckDependencies()) {
    Console.Error.WriteLine("The FUSE dependency was not found:");
    Console.Error.WriteLine(Pgfs.Fuse.Fuse.InstallationInstructions);
    return 1;
}

// a log plus a logic side effect plus the exit
if (input is null) {
    Logger.Warning("Input is null, using default");
    input = DefaultValue;
    return Process(input);
}
```

#### A single side effect only (the accepted pattern)

```csharp
if (config.UseCache) { this.cache.Initialize(); }            // a one-sided branch
if (this.cache is null) { this.cache = new Cache(); }        // lazy initialization (??= is fine too)
if (input is null) { input = DefaultValue; }                 // filling in an argument default
if (Logger.IsTraceEnabled) { Logger.Trace("...", arg); }     // a logger guard (the body is effectively empty, logs being free)
if (this.dirty) { this.flushCount += 1; }                    // incrementing a counter
```

#### A multi-way branch is a `switch`

```csharp
// returning a value: a switch expression
var label = priority switch {
    > 8 => "urgent",
    > 4 => "high",
    _ => "normal"
};

// causing a side effect: a switch statement
switch (priority) {
    case > 8: SendAlert(); break;
    case > 4: NotifyTeam(); break;
    default: break;
}
```

### The NG examples

```csharp
// NG: an else
if (a) { X(); } else { Y(); }
//                ^^^^^^^^^^

// NG: an else-if chain
if (n > 100) { Huge(); } else if (n > 50) { Big(); } else { Normal(); }

// NG: a ternary operator
var label = isAdmin ? "admin" : "user";
//                  ^^^^^^^^^^^^^^^^^^^

// NG: 2 logic side effects with no exit (the "effective" side effects excluding the logs are 2)
if (cond) {
    Foo();
    Bar();      // <- the second logic side effect, and no exit -> a switch or an extracted method
}

// NG: an example of abusing the freedom of logs
if (cond) {
    Logger.Info("doing A");
    Foo();
    Logger.Info("doing B");
    Bar();      // <- 2 logs plus 2 logic side effects, and the last one is not an exit -> NG
}
```

### The definition of "a log"

Any of the following calls is treated as "log output" and is exempt from the side-effect count:

- `Console.Write*` / `Console.WriteLine` / `Console.Error.Write*`
- `Logger.*` (this repository's own logger, [`Pgfs.Core.Logging`](../../src/core/src/Logging/))
- `System.Diagnostics.Debug.Write*` / `Debug.WriteLine`
- `System.Diagnostics.Trace.Write*` / `Trace.WriteLine`
- Any call equivalent to the above whose "only side effect is the message at the destination"

The criterion: anything for which **"removing the call does not affect the external state (the database, files,
the network, shared memory, process control)"** is treated as a log. Anything that rewrites data counts as a
logic side effect as usual.

### What the language allows

Every `?` operator other than `?:` is **permitted**:

| The operator | Its name | An example |
|---|---|---|
| `??` | Null coalescing | `name ?? "Guest"` |
| `??=` | Null coalescing assignment | `cache ??= new Cache()` |
| `?.` | Null conditional access | `user?.Name` |
| `?[]` | Null conditional indexer | `list?[0]` |
| `?? throw ...` | A throw expression | `x ?? throw new ArgumentNullException()` |
| `is` / `is not` | Pattern matching | `x is null`, `x is Foo f` |
| `as` | A safe cast | `x as Foo` |
| A `switch` expression or statement | A multi-way branch | `x switch { ... }` |

### How they are enforced

| The rule | Automatic enforcement |
|---|---|
| 1. The body of an if is a single statement | △ (possible if a Roslyn analyzer is written) |
| 2. The kind of that single statement | △ (the same) |
| 3. Preferring the negation | ✗ (a human judgement at review time) |
| 4. No `else` | △ (possible with an analyzer) |
| 5. Using a `switch` | △ (detecting an else-if chain is possible) |
| 6. No ternary | △ (**a tool can be stopped from writing them**; detecting one that has been written is not possible) |

**Not one of those 6 is enforced mechanically.** That was confirmed by measurement on 2026-09-21.
**They are secured in review.**
(**Outside the conditional rules, the mandatory braces do take effect in the build** - see the note below.)
If it ever becomes necessary, a small project along the lines of `Pgfs.Analyzers` could be added to enforce them
with a Roslyn analyzer.

> **About 6 (corrected by measurement on 2026-09-21)**: this table once said 6 was
> "✅ (it can be suppressed with `.editorconfig`)", but **that was wrong**. The `.editorconfig`'s
> `dotnet_style_prefer_conditional_expression_over_assignment` / `_over_return` control
> **IDE0045 / IDE0046 = the rule pointing the other way, "fold this `if` into a ternary"**, and
> **turning them `false` merely stops the tool suggesting a ternary**.
> **Not one hand-written ternary is reported** (measured with `EnforceCodeStyleInBuild=true`: 0 reports.
> **Turning the same settings `true` does make IDE0046 appear on `if` statements**, so it is not that the
> analyzer is not running).
> **Those two lines are in the `.editorconfig`** - their only effect is that a ternary does not come back from
> the tool side.
>
> **Conversely, there is one thing that is enforced mechanically**: **the mandatory braces
> (`csharp_prefer_braces = true:warning`) have been checked in the build since 2026-09-21**, because
> **`EnforceCodeStyleInBuild = true`** went into `src/Directory.Build.props` (until then it was configured but
> **did not take effect in the build**).
> **A violation gives an `IDE0011` warning.** When it was enabled, the 18 violations in `src/fuse`
> (12 in `FuseFileInfo.cs`, 6 in `FuseMount.cs`) were cleaned up.
> **Removing one brace was confirmed to produce the warning**, so **"0 reports" is not a failed measurement.**

### Why this rule

- Seeing an `if`, one can be sure that "exactly one thing happens"
- The nesting depth is effectively capped at 1 (the pyramid of else and else-if disappears)
- A multi-way branch becomes an explicit `switch`, where the exhaustiveness check (`_ =>` / `default:`) takes
  effect
- Nested ternaries are eliminated
- It leans towards making use of C# 9+'s switch expressions and pattern matching

---

## The rhythm - pushing the guards from the facade into the inside (recommended)

**A soft recommendation rather than a must.** In a "facade" method that lines up several `await`s or sequential
calls, **not scattering `if`s at the call site but putting the guard at the head of the callee** makes the facade
read as "an unconditional run of calls" and stops the eye from catching (a "rhythm" appears).

### Before (scattering ifs at the call site)

```csharp
public async Task InitializeAsync() {
    await this.EnsureUserAsync();
    await this.EnsureTablespaceAsync();
    if (this.config.Clean) {
        await this.DropDatabaseAsync();
    }
    await this.EnsureDatabaseAsync();
    // ... creating the tables ...
    if (this.config.Database.Citus) {
        await this.SetupCitusDistributionAsync(prefix);
    }
    await this.InsertRootInodeAsync(prefix);
}
```

### After (a guard at the head of the callee)

```csharp
public async Task InitializeAsync() {
    await this.EnsureUserAsync();
    await this.EnsureTablespaceAsync();
    await this.DropDatabaseAsync();      // <- if (!Clean) return, inside
    await this.EnsureDatabaseAsync();
    // ... creating the tables ...
    await this.SetupCitusDistributionAsync(prefix);  // <- if (!Citus) return, inside
    await this.InsertRootInodeAsync(prefix);
}

private async Task DropDatabaseAsync() {
    if (!this.config.Clean) { return; }   // <- moved here
    // ... the substance ...
}

private async Task SetupCitusDistributionAsync(string prefix) {
    if (!this.config.Database.Citus) { return; }   // <- moved here
    // ... the substance ...
}
```

### Why

- The facade side can be taken in at a glance as the ordered story
  **"(1) ensure the user -> (2) drop the database -> (3) ensure the database -> (4) create the tables ->
  (5) make it Citus -> (6) insert the root inode"**
- "This call runs conditionally" is the callee's responsibility - the caller has no need to know each time
- It is consistent with the conditional rules' "the body of an `if` is a single statement plus a trailing exit"
  (a one-line guard ending in a `return`)
- Adding calls does not add ifs -> attention does not scatter when a method is added
- One line in the XML doc comment saying "it does nothing unless `--xxx` is given" lets a reviewer of the caller
  understand that "following the facade's sequence is enough"

### The cases where this rhythm **may be broken**

- **The guard branches on which function to call**: when the target changes, as in
  `if (cond) { A(); } else { B(); }`, an if at the call site is natural (pushing it down would need duplicated
  code or an extra parameter)
- **The guard depends on a local variable of the caller**: if it branches on information the callee has no way
  to reach, it has to stay in the facade
- **An early fail is needed (throwing on a validation failure, say)**: if `if (!valid) throw` at the call site
  reads better, do not force it inward

### Related

- It is operated as a set with the conditional rules' **"a flow exit prefers the negation (fail-fast)"**. The
  body of a guard is written as the one-line trailing exit `if (!cond) { return; }`
- The logger guard on a hot path (`if (Logger.IsTraceEnabled) { Logger.Trace(...) }`) **cannot be pushed in**
  (the guard itself is the point and it should be visible at the call site)

---

## The comment policy

### The comments to keep

- **The intent / the why**: why that code is needed (the background, the trade-offs, a past bug and so on)
- **A non-obvious constraint**: hard-won knowledge of the form "making this X kills it through Y"
- **TODO / FIXME**: explicitly left unfinished parts. With the owner or the ticket number if there is one

### The comments to remove

- An explanation of only "what it is doing" that is obvious from the function name or the code
- **Purposeless commented-out remnants** of old code

### How commented-out code is treated

**A large block** commented out with `// ...` is **often left deliberately** from a past attempt.
**Do not remove it on your own initiative.**

Past examples of tidying (the decision to delete is made when it is clear, such as a whole file being commented
out):
- `Collections/extentsions.cs` / `tests.cs` - removed

When removing one, state "why it was removed" in the commit message.

### XML doc comments

- Put a `<summary>` on a public API wherever possible
- On internal and private members as needed
- Use `<remarks>` to add the implementation caveats (the performance, the transaction requirements, the platform
  dependence and so on)

---

## Using the logger

When calling `Logger.Trace(...)` / `Logger.Debug(...)` on a hot path, **always put in a guard clause**:

```csharp
if (Logger.IsTraceEnabled) { Logger.Trace("...", arg1, arg2); }
```

The reason: `Logger.Trace(...)` takes `params object?[]`, which allocates an array and boxes. Inside a FUSE or
Dokan callback that is called 1000+ times a second that is a measurable cost. It is avoided with an early
decision on `IsTraceEnabled` / `IsDebugEnabled`.
