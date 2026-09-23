# The namespace policy - deciding together the three cases where what is visible and what exists diverge

> **Route**: [docs/README.md](../README.md) › **this document**
>
> **What this document is the source of truth for**: **the policy on the cases where the namespace's
> appearance and its contents diverge**. Which names can be created / how what was created appears / whether
> what appears can be reached.
> **It handles the three cases together (hiding `.fuse_hidden*` / the Windows reserved names / trailing
> spaces and dots).**
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [handle-context.md](handle-context.md) | **Why hiding `.fuse_hidden*` was chosen** (the alternative H). How `hard_remove` came not to be set |
> | [windows-parity.md](windows-parity.md) | The Windows-specific measurements (the namespace pre-tests) and Dokan's as-built status |
> | [../Assign.md](../Assign.md) / [../Mount.md](../Mount.md) | The behaviour and the contract as the user sees them |
> | [../Pgfsctl.md](../Pgfsctl.md) | What `pgfsctl prune` cleans up |

## Why this document exists

**Deciding the three separately does not hold together.** Each of them has the shape "**what is visible and
what exists diverge**", and each has the same structure: **opening a hole for the convenience of one
operating system makes things inexplicable from the other**.
Fixing them one at a time leads to **closing one side opening the other** (the worked example is option H-2 of M-1).

## Where things stand (all of it measured on real Windows hardware)

| # | The case | What is visible | What exists | Who opened the hole |
|---|---|---|---|---|
| **M-1** | `.fuse_hidden<16 hex>` is kept out of the enumeration | Not in `ls` | A row in the database | **pgfs itself** (it hides them deliberately) |
| **N** | The Windows reserved names (`CON`/`PRN`/`NUL`/`AUX`/`COM*`/`LPT*`) can be created | In the enumeration | It exists | **The Win32 layer** (it resolves them as device names) |
| **T** | Names with a trailing space or dot can be created | In the enumeration | It exists | **The Win32 layer** (its path normalization drops them) |

### M-1 - not in the enumeration yet not deletable

**This is not an oversight; it is a price consciously accepted in
[handle-context.md, the alternative H](handle-context.md).** What was written at the time:

> **The price**: `rmdir` can come back `ENOTEMPTY` **although nothing shows in the enumeration** (a parent
> with a hidden file left in it). **It looks empty yet cannot be deleted**, so when in doubt, run `prune`.

**The measurements on Windows are heavier than that description of the price**:

| Operation | Result |
|---|---|
| `Get-ChildItem -Force` | **Empty** (= as the price says) |
| `Test-Path` / `ReadAllText` (naming it directly) | **Visible and readable** |
| `Remove-Item` (naming it directly) | **Fails with "cannot find the specified file"** <- **not in the document** |
| `[System.IO.File]::Delete` (Win32 `DeleteFile` directly) | **Deletes it** |
| `Remove-Item <parent>` (rmdir) | **Fails with "the directory is not empty"** (= as the price says) |

**`Remove-Item` presumably does not work because PowerShell's provider enumerates the parent before deleting**
(inferred from the behaviour, not confirmed). **Explorer is enumeration-based too, so it should be the same.**

So as a Windows user sees it:

- **It is invisible / it cannot be deleted by ordinary means / the directory cannot be deleted either**
- **It can be deleted if you know the name character for character and have a way to call the Win32 API directly**

**"There is a way to delete it and nobody can find it" is more awkward in operation than "it cannot be
deleted".** **Running `prune` removes it, but nothing leads you to notice that something invisible is left.**

### N - the reserved names

**`CON` / `PRN` / `NUL` / `AUX` / `COM1` / `LPT1` can all be created, and `CON` / `COM1` can even be read
through a Win32 path** (they are under a drive, so the DOS device resolution does not apply).
**Only `NUL` turns into the NUL device through Win32 and reads back empty.**

**The real harm**: **creating a single file named `NUL` makes that directory undeletable with
`Remove-Item -Recurse`** (`ERROR_INVALID_FUNCTION`). Recovering it needs an individual delete through `\\?\`.

### T - trailing spaces and dots

**`dot` and `dot.` can coexist as separate things, and giving `dot.` in a Win32 path normalizes it so the
contents of `dot` come back.**

**The real harm**: **it is not an error.** The user **has no way of noticing they read the wrong data.**
**The quietest and the most dangerous of the three.**

## The options and their trade-offs

### M-1 (hiding `.fuse_hidden*`)

| Option | What it is | What it gains | What it loses |
|---|---|---|---|
| **H-1** | **Leave it as it is** and add the Windows harm to the document | No change. **The price stays as already accepted** | Windows users still have **nothing leading them to notice** |
| **H-2** | **Align `IsDirectoryEmpty` with the hiding side** (do not count hidden rows) | **`rmdir` works on both operating systems** | **A hidden inode goes with the parent** = orphan data grows and **nobody can see that it went**. **prune can recover it, but no record that it happened survives** |
| **H-3** | Keep hiding, but **explain in the log why `rmdir` failed** | The user can find their way to `prune` | **The error code does not change**, so from a GUI or from Explorer the reason is still invisible |
| **H-4** | **Do not hide on the Dokan path** (put an OS condition into Core's decision) | **Every bit of the Windows harm disappears** (visible, deletable, and `rmdir` works). **On Windows libfuse leaves no leftovers, so there is no reason to hide in the first place** | **Mounting the same FS from both operating systems shows different things.** The price on the Linux side remains. **What was judged "the real harm" at the time - leftovers showing up in another mount's `ls` - comes back on the Windows side** (see the section on the original decision) |

**H-2 is the worked example of "closing one side opens the other"** - `rmdir` starts working, but
**it quietly deletes the very bodies C-2 (keeping a body alive while it is open even after its name is gone)
set out to protect**.

### N (the reserved names) / T (trailing spaces and dots)

**The two have the same shape, so the options are the same.**

| Option | What it is | What it gains | What it loses |
|---|---|---|---|
| **P-1** | **Leave it as it is** (allow them) | The design goal of **an identical namespace with Linux** is kept exactly | From Windows you can create **things that cannot be deleted, and things that silently open something else** |
| **P-2** | **Reject only creation from Windows** (make `CreateFile` return `STATUS_OBJECT_NAME_INVALID` in the Dokan layer) | **Nothing new and inexplicable can be created from Windows.** **What Linux created stays visible and readable**, so the shared namespace is preserved | It becomes **asymmetric** (creatable from Linux, not from Windows). Although **the asymmetry already exists on the Win32 side** |
| **P-3** | **Reject across the whole FS** (refuse at creation time in Core) | **The same namespace from either operating system**, a stronger guarantee | **Linux becomes inconvenient** (names that are perfectly valid under POSIX are refused). **A migration problem appears** for names that already exist on an existing FS |

**P-3 collides head-on with the goal of "an identical namespace with Linux"** - lining them up would mean
**bringing both down to Windows's constraints**.

## The decision

**M-1 = H-4 / N = P-2 / T = P-2 are adopted.** The words of the decision were
**"it is a Windows thing, so go with the recommendation"** - the framing being that **all three holes come
from the Windows side, so they are closed on the Windows side**.

**What follows is the recommendation as it was written when proposed, and it doubles as the reasoning for the decision.**

**The reason is one and the same for all three**:

> **Close the hole where whoever opened it is.**

- **The M-1 hole is opened by pgfs itself** (it hides them). **On Windows there is no reason to hide**
  (libfuse does not create `.fuse_hidden*` there), so **stopping the hiding there makes the harm disappear**.
  **The price on the Linux side remains**, but that comes from **libfuse's constraint that `hard_remove`
  cannot be set**, so **it is not a hole pgfs can close**
  ([handle-context.md, on why `hard_remove` is not set](handle-context.md)).
  **This option is recommended in full knowledge that it overturns the earlier judgement** - at the time,
  "leftovers showing up in another mount's `ls`" was seen as the real harm and it was applied to both
  operating systems. **After the measurements, "being invisible" looks like the real harm**, but **that may
  simply be taking "leftovers in `ls`" too lightly**. **This is the weakest part of the recommendation**, so
  **whoever decides should look at the comparison table in the section on the original decision.**
- **The N / T holes are opened by the Win32 layer.** **Win32's constraints exist only on Windows**, so
  **rejecting at the Windows entrance** is the natural move. **There is no reason to carry it into Linux.**

**Under this policy, "a reserved name created from Linux cannot be deleted from Windows" remains.** That is
**left deliberately** - **rejecting it would only move the problem**, not remove it. **If it stays, it gets
written down**, which is this document's job.

### The size of the implementation (an estimate)

| | What is touched | Size |
|---|---|---|
| **H-4** | Pass an OS condition into `Api.ListChildren`'s hiding decision (Core). The callers are Fuse and Dokan | **Small.** The decision is already concentrated in `IsLibfuseHidden` |
| **P-2 (N)** | Check the name in Dokan's `CreateFile` (`src/dokan`) | **Small.** The list of reserved names is fixed |
| **P-2 (T)** | The same (check for trailing spaces and dots) | **Small** |

**All of it stays within the Windows side's scope** (`Api.cs` / `src/dokan`).
**Only H-4 touches Core's `ListChildren`**, so **the Linux side confirms that the Fuse callers are unaffected.**

### The acceptance criteria

| Option | What to confirm |
|---|---|
| **H-4** | Windows: `.fuse_hidden<16 hex>` **appears in the enumeration / can be deleted with `Remove-Item` / the parent can be `rmdir`ed**. Linux: **still hidden as before** (no regression) |
| **P-2 (N)** | Windows: creating `CON` and friends **is refused**. **A same-named one created from Linux is visible and readable on Windows** |
| **P-2 (T)** | Windows: creating `trail ` / `trail.` **is refused**. **Existing ones are visible** |

**Each of them is landed only after confirming it fails against the pre-fix build**
([tests/windows/README.md, the traps hit while writing the tests](../../tests/windows/README.md)).

## The original decision, and what has happened to its premises

**Making M-1 apply to both operating systems was deliberate.** The reasoning in
[handle-context.md, the alternative H](handle-context.md):

> **It is in Core, so it applies to both operating systems** - the real harm is "**it shows up in another
> mount's `ls`**", and those other mounts include Windows.

**That reasoning still holds.** What changed is **what it is compared against**:

| | The judgement at the time | After the measurements |
|---|---|---|
| The harm of **hiding** | "`rmdir` can come back `ENOTEMPTY`. When in doubt, `prune`" | **plus, on Windows, not even a single file can be deleted by ordinary means, and nothing says what is in the way** |
| The harm of **not hiding** | **Leftovers show up in another mount's `ls` (Windows included)** | The same |

**At the time, "being visible" was judged the real harm. After the measurements, "being invisible" looks like
the real harm** - but **that may simply be taking "leftovers showing in `ls`" too lightly**, so both are set
out side by side. **Taking H-4 (not hiding on Dokan) brings back, on the Windows side, what was called the
real harm at the time.**

**Two of the premises were not part of the original consideration** (confirmed by the FUSE side):

1. **How heavy "when in doubt, run `prune`" really is.** `pgfsctl prune` **does work on Windows** (confirmed
   by running it many times), so it is not that it cannot be run. **But prune skips the whole data-deleting
   side whenever even one mount is live**, so **every mount has to be stopped first**. **It is not the light
   operation that "run it when in doubt" suggests.**
2. **The premise was that `.fuse_hidden*` is something libfuse creates.**
   **If a user chooses a name in the same format themselves (`.fuse_hidden` plus 16 hex digits), the same
   state arises on Windows too.**
   `IsLibfuseHidden` **decides on the format alone and does not distinguish who created it.** **That shape
   was not evaluated at the time.**

## The lines that must not be crossed (earlier decisions; stepping over them breaks things)

- **Do not hide it from `GetByPath` (path resolution).** Removing it there makes **the very mount keeping
  that fd alive unable to resolve its own hidden file** - libfuse **fires `getattr` / `release` against the
  hidden name**. **Lining it up as "if we hide, hide everywhere" breaks it.** What is hidden is **the
  enumeration (`ListChildren`) only**.
- **`hard_remove = 1` is not taken.** The idea of never creating `.fuse_hidden*` in the first place was
  already killed here ([handle-context.md, on why `hard_remove` is not set](handle-context.md)). **Read the
  reasoning from that time before proposing it again.**
- **If `IsDirectoryEmpty` is aligned with the hiding side, something has to keep C-2's protected bodies from
  being deleted** (H-2 above).

## The options that were rejected

- **Delete `.fuse_hidden*` from the database, or prevent it being created** - **libfuse creates them, so pgfs
  cannot stop it.** Setting `hard_remove = 1` would stop them being created, but **setting it breaks reads
  after an `unlink` and the rename-over** (there is a record in [handle-context.md](handle-context.md) of
  going as far as that and coming back).
- **Automatically rename a reserved name** (`CON` -> `CON_` and so on) - it goes against the existing policy of
  **not rewriting a name in the database automatically** ([windows-parity.md](windows-parity.md)).
  **A name changing by itself as seen from Linux** breaks the shared namespace.
