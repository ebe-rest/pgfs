# The design for unifying the handle context (OpenFileContext)

> **Route**: [docs/README.md](../README.md) › **this document**
>
> **What this document is the source of truth for**: the design, the stages, the unresolved points and the
> division of labour for the handle context (`OpenFileContext` / `HandleTable`).
> **How stages A to D proceed is authoritative here.**
>
> **The neighbouring documents and what they cover**:
>
> | Document | What goes there |
> |---|---|
> | [windows-parity.md](windows-parity.md) | The design for taking things to Windows (its common-classes-and-responsibilities section is the origin of stage D) |
> | [metadata-write-back.md](metadata-write-back.md) | The synchronous-close mark (A-4 to A-7). The grounds for the decision **not to move it onto the handle** |
> | [fuse-binding.md](fuse-binding.md) | How `fuse_file_info` is handled |

**A design proposal. Stages A to C-3 are implemented** .
**Only stage D (the common operations class) has not been started.**
Some sections of the body **are left as the design proposal of the time**, so **for the as-built see each
stage's implementation-status / as-built section** (C-1 / C-2 / C-3 are in the latter half of this document).
**This opening line and the body disagreed with each other, and it was fixed** - the label on the
index side was fixed at the same time.
For the Windows-side as-built see
[windows-parity.md, the implementation status](windows-parity.md).

## Why do it (the problems, measured)

Today **an open file is identified primarily by path**, and all that is on the handle is "a resolved `Inode`
object". That causes the following (all either confirmed on real hardware by 2026-09-19 or established from the
code):

| # | The problem | The evidence |
|---|---|---|
| 1 | **If the name changes after the open, a different file is touched** | `Read` / `Write` look it up by path without using `fi.fh` (FUSE). Dokan's `Resolve` also starts from the path. If another client renames and then recreates the same name, an existing fd can point at a different inode |
| 2 | **A handle keeps stale attributes** | The `Inode` put on Dokan's `info.Context` is not re-fetched after a remote invalidation. A stale `Size` / `DataId` keeps being used |
| 3 | **There is only one place where the caller can be obtained** | Dokan's `GetRequestor` succeeds only inside `CreateFile` (measured: 3400 failures from the other callbacks in one e2e run). **There is no way to carry it around other than putting it on the handle** |
| 4 | **A per-handle sync policy cannot be held** | `FILE_FLAG_WRITE_THROUGH` is a handle attribute meaning "the writes of this handle are persisted at once". Today only the Dokan side holds it as `OpenFile.WriteThrough` and Core does not know about it |
| 5 | **The `FileIndex` does not agree between links pointing at the same data** | `inode.Id` is put in `ByHandleFileInformation.FileIndex`. Windows decides "is it the same file" with it |
| 6 | **There is no lifetime management for I/O after a delete** | Windows allows reads and writes until the last handle closes. pgfs removes it from the database at the first `Cleanup` |

**1 to 4 are already partly handled at the OS layer**, but Dokan and FUSE hold them separately so it is a
duplicate implementation. The Windows side's `FileSystem.OpenFile` (the inode plus the audit subject plus
WriteThrough) is effectively the advance implementation, and it is lifted into Core.

## The design

### `OpenFileContext` (Core, new)

**One handle = one instance.** The OS layer creates it at open time and throws it away at close.

| The element | The type | What it means | Notes |
|---|---|---|---|
| `InodeId` | `long` | **The stable identifier.** Settled at open time and looked up by from then on | The path is used only at open and for name operations |
| `Inode` | `Inode?` | A snapshot of the attributes | Made **re-fetchable** (thrown away and looked up again after a cache invalidation) |
| `Access` | enum | The request: read / write / delete and so on | Dokan's `FileAccess` and FUSE's `O_*` into a common vocabulary |
| `Durability` | enum | `Default` / `WriteThrough` | The receptacle for 4 |
| `Audit` | `AuditContext?` | **The subject settled at open time** | The receptacle for 3. Windows can only obtain it in `CreateFile` |
| `TruncatedByThisHandle` | bool | Whether this handle issued a truncate | **Diagnostics only.** The **safety of 1e's "synchronous-close mark" stays on the inode / data keys** (the section on not moving the synchronous-close mark onto the handle) |
| `DeletePending` | bool | A delete is reserved | The entry point for 6 |

**Handle ids are allocated from 1.** The `0` of `fi.fh` is used by the current code as **the sentinel for
"not set"** (the reverse lookup of `FSyncDir` decides on `fi.fh != 0`). **And the root inode is `id = 0`**, so
with the current implementation of "put the inode id in `fh`", **the `fh` of `OpenDir("/")` is 0 and slips
through the reverse lookup**.
**It is unreachable as things stand**, though: the reverse lookup is only needed when "another client's rename
makes the path unresolvable", and **the root cannot be renamed**, so there is no path by which `GetByPath("/")`
returns null (confirmed by the Linux side and recorded conditionally in its review notes).
**It disappears when the meaning of `fh` becomes a handle id starting at 1 in stage A**, so the allocation is
not shifted here first on its own (it would disagree with the convention in `Open` / `Create` and become the
next trap).

**What is not brought into Core**: the Windows token or SID itself, `DokanFileInfo`, `fuse_file_info`.
They are reduced to strings and ids (the same policy as the current `FileSystem.OpenFile`).

### The mapping at the OS layer

| | Windows (Dokan) | Linux (FUSE) |
|---|---|---|
| Where it lives | `DokanFileInfo.Context` (replacing the current `OpenFile`) | **The key into the handle table** goes in `fuse_file_info.fh` |
| Creation | `CreateFile` | `open` / `create` / `opendir` |
| Destruction | `CloseFile` | `release` / `releasedir` |
| Notes | `OpenFile` already exists, so it is just swapped | `fh` is a 64-bit integer, so **a handle table (id -> context) is needed on the Core side** |

FUSE cannot put an object directly in `fi.fh`, so **a handle table (id -> context) goes in Core** and `fh` holds
its key. Phase 0 (2) put the inode id in `fi.fh` for `FlushPath`, so this is a change of
**the key's meaning from "an inode id" to "a handle id"** (the reverse lookups of fsync and fsyncdir go through
the handle table). **Directories are covered too**: `OpenDir` puts the `fh` on and `FSyncDir` reverse-looks it
up.

#### `fh` is allocated **from 1** (decided, from the Linux side's finding)

**Handle ids do not use 0.** 0 is reserved as the sentinel for "not set".

The reason: today `fi.fh` **carries the inode id as it is**, and **the root inode is `id = 0`**, so "the root's
handle" cannot be told from "no `fh` was set". That is the real identity of the problem recorded in the review
notes that **the reverse lookup of `FSyncDir` is disabled at the root**.

Stage A changes the meaning of `fh` from "an inode id" to "a handle id", so **allocating from 1 makes the trap
disappear by itself**. **Allocating from 0 would keep the trap**, so this is fixed as a design decision.

### Cleaning up the handle table (not leaking it)

**`release` / `releasedir` are best-effort and are sometimes not called on an error path.** Left alone, an
`OpenFileContext` keeps holding an `Inode` and, like a pin, accumulates with the LRU unable to touch it. The
design includes the following:

| The item | The policy |
|---|---|
| Who removes it | The OS layer's close callbacks (`CloseFile` / `release` / `releasedir`) first. **The table also has a ceiling** |
| The ceiling | A new knob (provisionally `mount.max_open_handles`) caps the count. Going over it **refuses a new open** (it does not silently throw away the old ones) |
| Visibility | **"The number of open handles" is published in `pgfsctl status` Layer 3** (the same slot as 1e's pending count) |
| Degradation | Reaching the ceiling is a sign of a leak, so it goes into **a Warning log plus the status**. Nothing is reclaimed automatically |

## The stages (in an order that does not change the behaviour)

| The stage | What it is | The acceptance criteria |
|---|---|---|
| **A. The extraction** | Create `OpenFileContext` in Core and replace Dokan's `OpenFile`. FUSE builds a handle table and swaps what `fh` holds (**both files and directories**). **The behaviour does not change** | Every existing suite on both operating systems green. **The counts keep growing, so [tests.md](../tests.md) is the source of truth** |
| **B. Identification onto the handle** | `Read` / `Write` / `Flush` / `SetEndOfFile` start from **`InodeId`** (the path resolution goes). The attributes are **looked up again every time** | (1) Reproducing problem 1: "A keeps it open while B renames and recreates the name -> A's read/write/fsync go to the first inode" (Windows in `crossclient.ps1`, Linux in the e2e suite). (2) **Reproducing problem 2: a write across a flush re-pointing the `data_id` (a materialize or A-1's sibling re-pointing) reaches the data after the re-pointing.** (3) **Measure the performance** (below) |
| **C. Lifetime management** | Keep the body until the last handle is released (`DeletePending` plus a reference count). Align I/O after a delete with the Windows semantics | "Delete while open -> it can still be read through an existing handle -> it goes at the last close". Windows is made consistent with the measured delete-pending behaviour (it appears in the enumeration but an open fails while another handle is held) |
| **D. The common operations class** | Put the `FileSystemOperationsBase` plus the OS-derived classes of [windows-parity.md, the common classes and responsibilities](windows-parity.md) on top of the context assembled through C | The existing tests green plus the duplicated code (the procedures of create / flush / close / rename) gathers in one place |

**A is worth it on its own**: 3 (the audit subject) and 4 (WriteThrough) become part of Core's vocabulary, so
**"who, through which handle's request" can be carried into the background flush of write-back** (the leftover
of 1e's audit).

**Stage B is decided after measuring the performance** - so it was written at first, but **the substance of the
concern was refuted by a static check**. The purpose of the measurement changed from "deciding
whether to do B" to **"showing that B has no regression"**. The grounds are in
**the section on stage B's acceptance criteria and API surface, point (4)** below.

## Stage B's acceptance criteria and API surface

What the both sides agreed right after stage A landed. **(1) and (2) are agreed by both sides**, and
**(3) to (5) are the Dokan side's (the Core owner's) answers** awaiting the Linux side's confirmation.
Stage B **changes the behaviour, unlike A**, so what may change and what must not is fixed first.

### (1) What must change (detected by new tests)

| # | The contract | Linux | Windows |
|---|---|---|---|
| 1 | A keeps it open while B renames and recreates the name -> **A's read / write / fsync land on the first inode** (problem 1) | Added to the e2e suite | Added to [crossclient.ps1](../../tests/windows/crossclient.ps1). **A separate mount = a separate process**, so it reproduces without being blocked by Windows's share locks |
| 2 | A write across a flush re-pointing the `data_id` (a materialize or A-1's sibling re-pointing) **reaches the data after the re-pointing** (problem 2) | The e2e suite | crossclient. On Windows the same problem takes the form of `FileSystem.Resolve` **continuing to return a cached `Inode`** |

**A new test is landed only after being applied to the pre-fix build and confirmed to fail, with the failure
message pointing at the cause** ([next.md, the conventions being handed on](../next.md)).

### (2) What must not change (the existing suites stay green)

- **1e's synchronization discipline A-4 to A-7 is not moved.** In particular, the window of
  `truncate -s 0 f; cmd >> f` (the truncate and the append on different fds) must not come back. The mark stays
  on **both the inode and the data keys**, and `TruncatedByThisHandle` is not added to `OpenFileContext`
  (the section on not moving the synchronous-close mark onto the handle).
- **POSIX's "the fd survives an unlink" is not realized in stage B.** If the handle fails to resolve, it returns
  **an error as it does today** (a path resolution already fails with `ENOENT` today, so it is not a
  regression). The lifetime management is stage C.
- [tests.md](../tests.md) is the source of truth for the counts. At the point of starting, it was
  **7 Linux suites** (e2e off 47+1 skip / e2e on 47+1 skip / writeback 9 / wbmeta 27 / negcache 7 /
  crossclient 8 / startup 5) and **5 Windows suites, 59 cases** (e2e 35 / crossclient 7 / writeback 6 /
  wbmeta 5 = 4+1 skip / control-plane 6).

### (3) Core's API surface (frozen first)

A **stable identifier** is added to `OpenFileContext`, and **versions that take a handle context** are added to
6 methods of `Api`.

```csharp
// OpenFileContext (added in stage B)
public long InodeId { get; }        // settled at open time and immutable; this is the primary basis of the decision
public Inode? Inode { get; set; }   // the snapshot; refilled on every resolution

// Api (the existing Inode versions stay; ctx versions are added)
public int  ReadData      (OpenFileContext h, long offset, Span<byte> destination);
public int  WriteData     (OpenFileContext h, long offset, ReadOnlySpan<byte> source);
public bool TruncateData  (OpenFileContext h, long newLength);
public void FlushInode    (OpenFileContext h);
public void CloseInode    (OpenFileContext h);
public void FlushDirectory(OpenFileContext h);
```

| What was decided | The reason |
|---|---|
| **What is passed is an `OpenFileContext`, not a handle id** | Dokan does not go through the table and puts the object on `DokanFileInfo.Context`. An id version would make **only Windows look up the handle table every time**. FUSE does a `Handles.Get(fi.fh)` at the head of each callback and converts to a ctx |
| **The existing `Inode` versions go through the same core as a "disposable context"** | So that identification is not implemented twice. From stage B on, any path that has an `fh` goes to the ctx version |
| **The resolution (`InodeId` -> `Inode`) is closed inside `Api`** | The internal policy (a `GetById` every time / a generation-marked snapshot) can be changed later **with no change on the adapter side**. That is also the premise of (4) |
| **`HandleTable` stays FUSE-only** | Dokan's `FileSystem.Resolve` has paths that **build a ctx ad hoc** for attribute queries and the like, and putting those in the table would leak them unclosed. "The number of open handles" in `pgfsctl status` Layer 3 is therefore **the FUSE-side value** for now (n/a on Windows) |

#### The implementation status of (3) (as-built)

**Core has landed** ([Api.Handle.cs](../../src/core/src/Api/Api.Handle.cs), new, plus
[OpenFileContext.cs](../../src/core/src/Api/OpenFileContext.cs)). **Nothing has changed behaviourally yet** -
until the adapters start calling the ctx versions, the existing `Inode` versions are used.

- The constructor of `OpenFileContext` was made to **take a non-null `Inode`**. `InodeId` is a value settled at
  open time, so **allowing null would make it possible to build "a handle with no identifier"**. The existing
  callers (7 places in Dokan, 3 in FUSE) all passed a resolved inode, so there was no impact.
- **The resolution was split in two.** It was meant to be one at design time, but **the close path means
  something different**:

  | | Where it is used | When the inode is gone |
  |---|---|---|
  | `ResolveHandle` | `ReadData` / `WriteData` / `TruncateData` | **`Api.StaleHandleException`** (dropped to the equivalent of ESTALE at the OS layer) |
  | `TryResolveHandle` | `FlushInode` / `CloseInode` / `FlushDirectory` | **A no-op** (it just returns `null`) |

  The reason: **a flush to an inode that is gone is "there is nothing to write", not an error** (the dirty data
  was discarded on the delete side). FUSE's `FlushPath` up to stage A also operated on "return 0 if it really is
  gone". Aligning this with `ResolveHandle` would make **the `close` of an unlinked file return `-EIO` every
  time**.
- `TryResolveHandle` puts **`null`** into `handle.Inode` when the resolution fails (so that a stale snapshot is
  not left behind).
- **The verification**: `dotnet build pgfs.sln -c Release` gives 0 errors on Windows (with no new warnings).
  **The suites on real hardware were not run** - the behaviour changes only from the next move, flipping the
  adapters, and every suite on both operating systems is run there.

### (4) The performance gate is "measure first" and may run alongside the implementation

The measurement can only be done on real Linux hardware, so **the FUSE side runs it following the
conventions of [performance.md](performance.md)** (wait for the daemon to disappear / check the integrity with
md5 every time).

**But the substance of the gate changed (from the Linux side's static check).** The original concern
that "starting from the id means a `GetById` every time, and **a pending inode is always hit because it is
pinned but a persisted one can fall out of the LRU**, so the database round trips can grow depending on the
conditions" **does not hold as far as [InodeCache](../../src/core/src/Api/InodeCache.cs) can be read**:

- **`byId` is the authority.** After the LRU eviction, `EvictIfOverCapacity` **cleans up the `byPath` and
  `childrenByParent` entries pointing at ids that are not in `byId`, keeping them consistent**.
  Therefore **anything that hits `byPath` is certain to hit `byId`** - **it is structurally impossible for
  starting from the id to miss more often than starting from the path** (the reverse does happen: an inode
  loaded by id goes only into `byId`). The only asymmetry is the path where `Invalidate(id, null)` leaves
  `byPath`, but **both miss then** (`get(path)` goes `byPath` -> `byId[id]` = null and falls to a miss), so the
  ordering does not change.
- **The cost on a hit actually drops.** `get(path)` is **two dictionary lookups**, `byPath` then `byId`, while
  `get(id)` is **one**, `byId`. On top of that the `PathToString` at the head of `Read` / `Write`
  (= `Encoding.UTF8.GetString` plus a string allocation) **becomes entirely unnecessary**.
  That also means improvement candidate 5 of [performance.md](performance.md) (medium ROI, **very high**
  effort) **gets done as a by-product of stage B, at least for those two hot paths**.
- **A miss does not grow either.** An id miss is **one SELECT**, `selectInodeByIdQuery`, and a path miss is
  **the path-chain walk** of `load(parser, ...)`.

-> **The gate was passed (2026-09-20).** Re-taken after flipping the FUSE adapter, **tx/file = 32.1 with no
change** (= no extra database round trips), and the times were all within the spread for every workload
([performance.md, the results: stage B (after flipping the FUSE adapter)](performance.md)).
**No improvement in time is claimed** - W1 came down from 2.12 to 1.95 s, but that is inside a spread of 36% and
indistinguishable from noise.

What follows is how the judgement was reached. **The stage A baseline was taken**
([performance.md, the measurements: the baseline of handle-context stage B](performance.md)) - on a single PG,
W1 rsync 4 KB x 300 = 2.12 s / W3 write 64 MiB = 0.88 s / W4 cold read 64 MiB = 0.23 s, with
**W1's `xact_commit` = 32.1 tx/file**.

**The judgement rests on tx/file.** The spread in time is 4 to 36% while tx/file's is **0.3%**, and it directly
measures "did the database round trips grow". What is removed is **one dictionary lookup plus one UTF-8 decode**
per callback, which is orders of magnitude below chunk I/O (in ms), so **"it got faster" is not expected to show
in the time**.

**The signatures of (3) can be frozen without waiting for the measurement**, though - the resolution is closed
inside `Api`, so if the result is bad, only the internals are swapped and the FUSE / Dokan adapters need no
rewriting. **The measurement and the Core implementation can therefore run in parallel**, and the result is
looked at **as a gate before landing**.

### (5) The contract for calls that arrive with no handle

- On FUSE, `fi` can be null in **the 5 callbacks `GetAttr` / `ChMod` / `Chown` / `Truncate` /
  `UpdateTimestamps`** (the ones that take a `FuseFileInfoRef`). **`Read` / `Write` always get an `fi` from
  libfuse**, so they are out of scope.
- **Read-ahead has no path today because 1c is unimplemented.** Only the contract is settled in advance.
- The contract: **with an `fh`, start from the ctx's `InodeId`; without one, look the path up to an id once and
  then go through the same core**. The only thing that can differ in what they see is
  **the single case of "a rename plus a recreation of the name"**, and **differing there is correct**
  (the fd points at the inode from open time and the path points at the new inode). Writing this down lets
  "what is seen differs with and without an `fh`" be stated as a specification.

### The implementation status - the FUSE adapter (the FUSE side)

**Flipped.** [src/fuse/src/FileSystem.cs](../../src/fuse/src/FileSystem.cs):

| The callback | The identification in stage B |
|---|---|
| `Read` / `Write` | **The `fh` only** (libfuse always passes an `fi`). Only when the table cannot resolve it does it fall back to `ReadByPath` / `WriteByPath`, and **it emits a Warning** (falling back means the stage A window is open) |
| `Flush` / `FSync` (`FlushPath`) / `FSyncDir` | Flipped to **the `fh` primary and the path secondary**. The ctx version is `TryResolveHandle`, so **if it is gone it is a no-op returning 0** (keeping stage A's "return 0 if it really is gone") |
| `Truncate` | With an `fh` it is the ctx version (`ResolveHandle` = `-ESTALE` if it is gone); without one it is the path |
| `GetAttr` / `ChMod` / `Chown` / `UpdateTimestamps` | `ResolveByHandleOrPath`, following the contract of (5) |

`Api.StaleHandleException` is dropped to **`-ESTALE`** (not `-EIO`).

**`PathToString` is gone from the hot path** - `Read` / `Write` only stringify the path on an error.
Improvement candidate 5 of [performance.md](performance.md) (**very high** effort) got done as a by-product of
the flip, at least for those two.

**The tests** (2 added to [tests/linux/crossclient.sh](../../tests/linux/crossclient.sh), 8 -> 10):

- `test_xc_open_fd_sticks_to_inode` - **the reproduction of problem 1**. **Confirmed to fail against the stage A
  build** (`a write through the still-open fd did not reach the first inode ... = 'ORIGINAL'` = the write was
  landing on the file created later under the same name). **It does not reproduce with a rename inside the same
  mount** - the kernel's dentry moves with it, so even a path-based lookup finds the right inode.
  **When B does the rename, A's kernel does not know the name has changed**, and only then does it split.
- `test_xc_open_fd_follows_data_repoint` - **a guard for problem 2 (not a reproduction)**.
  **It is green on the stage A build too**: on Linux, even in stage A, `Read` / `Write` looked it up again with
  `GetByPath` every time. Where problem 2 bares its teeth is **Windows's `FileSystem.Resolve`, which holds on to
  the resolution result**, and this is placed as a guard against "changing `ResolveHandle` to reuse a snapshot".

**The regression**: all 7 Linux suites green - e2e off 47+1 skip / e2e on 47+1 skip / writeback 9 / wbmeta 27 /
negcache 7 / **crossclient 10** / startup 5.

**The Linux side of (6) (the leak regression test) has landed** - see the leak regression of (6) below.
~~**Stage C (the lifetime management) has not been started**~~ -> **stage C is implemented as C-1 / C-2 / C-3**
. This line is as it was written when stage B landed. **The opening line is the source of truth for
where things stand.**

### The implementation status - the Dokan adapter (the Dokan side)

**Flipped.** The change in [src/dokan/src/FileSystem.cs](../../src/dokan/src/FileSystem.cs) is
**only 3 places** - Dokan concentrates the identification in `Resolve`, so making that start from the id flips
every callback.

| The place | The identification in stage B |
|---|---|
| `Resolve` | **If there is a handle, it looks it up again every time with `Api.TryResolveHandle`** (up to stage A it returned the `Inode` resolved at open time as it was). **The path is used only on paths with no handle** (attribute queries and so on) |
| The delete in `Cleanup` (`DeletePending`) | What is deleted is looked up again from the id too. **If it is already gone it is skipped** |
| `FlushOnCleanup` | To the ctx version `Api.CloseInode(open)` (a no-op if it is gone) |

`ReadFile` / `WriteFile` / `FlushFileBuffers` / `GetFileInformation` / `SetEndOfFile` / `SetAllocationSize` /
`SetFileAttributes` / `SetFileTime` / `GetFileSecurity` / `SetFileSecurity` **all go through `Resolve`**, so not
one line changed on the caller side.

**The only ctx version of the 6 that is used is `CloseInode`.** Calling a ctx version on a path that needs the
inode for an advance decision such as `IsDirectory` or `WriteToEndOfFile` would **resolve twice in one call**
(once in `Resolve` and once inside the ctx version). The identification implementation stays the single
`TryResolveHandle` in Core, so (3)'s "do not implement identification twice" is satisfied.

**The treatment of stale**: Windows returns **`FileNotFound`** as before (not `STATUS_FILE_INVALID`). Up to
stage A "cannot resolve = `FileNotFound`", so **the error code does not change**.

**The tests** (2 added to [tests/windows/crossclient.ps1](../../tests/windows/crossclient.ps1), 7 -> 9):

- `test_x_append_handle_sees_peer_growth` - **the reproduction of problem 2** (the one that cannot be written on
  the Linux side). A handle opened with `FILE_APPEND_DATA` comes down from Dokan with
  `WriteToEndOfFile = true`, and pgfs **uses the `inode.Size` of that moment as the write offset**.
  **Confirmed to fail against the stage A build**, failing with
  `expected 'AAAA1BBBBBB2' / actual 'AAAA12' (len=6)` = **it overwrote and erased the 6 bytes the peer mount had
  added, and even shrank the size** (data loss itself).
  - **It does not reproduce with .NET's `FileMode.Append`** - the FileStream seeks to the end itself and
    **writes at an explicit offset**, so `WriteToEndOfFile` is not set. It has to be opened with Win32
    `CreateFileW` with **`FILE_APPEND_DATA` alone** (the test does a P/Invoke).
  - **The growth has to come from another mount.** Within the same mount, **the same `Inode` instance** in the
    `InodeCache` is updated so the handle's copy becomes current with it and the discriminating power is zero
    (`InodeCache.Invalidate` **removes the entry**, so through a notify it becomes a different instance on the
    next load).
- `test_x_handle_follows_inode_after_peer_rename` - **a guard for problem 1 (not a reproduction)**.
  **It is green on the stage A build too**: as of stage A, Dokan already put the resolved `Inode` on the handle
  and did not look it up again by path. It is placed to protect against breaking it when flipping to the id.
  The real reproduction of problem 1 is **on the FUSE side**
  ([tests/linux/crossclient.sh](../../tests/linux/crossclient.sh)).

**The regression**: all 5 Windows suites green - e2e 35/35 / **cross-client 9/9** / write-back 6/6 /
metadata write-back 4 passed + 1 skip / control-plane 6/6 (2026-09-20, the Windows machine plus Dokan 2.3.1
against the server's `pgfs` schema). The skip is the known
`test_meta_defer_conflict_recovers_by_unlink` (Windows defers the `DeleteFile` until the last handle, so the
recovery cannot be observed in this environment; the contract is verified on the Linux side).

### (6) Publishing `HandleTable.Count`

As a by-product of stage B, **"the number of open handles" is published in `pgfsctl status` Layer 3**
(Core plus the status are the Dokan side). Once it is there, **a regression test for a handle leak can be
written from the Linux e2e suite** (the FUSE side writes it). The meaning of the value is, as in (3),
**the number of handles on the FUSE side**.


#### The implementation status of (6) (as-built)

**It went in** ([HandleTable.cs](../../src/core/src/Api/HandleTable.cs) /
`BuildStatsJson` in [Api.cs](../../src/core/src/Api/Api.cs) /
[StatusCommand.cs](../../src/ctl/src/StatusCommand.cs)).

- **`handles: { open, peak }`** was added to the heartbeat snapshot of `{prefix}mounts.stats`.
  The text output of `pgfsctl status` is `handles      : N open / peak M`.
- The reason **`peak` is published too**: with `open` alone, only "how many are open **now**" can be known, and
  **whether a mount that has already gone quiet was leaking in the past** cannot be asked. It is updated
  best-effort on every `Rent` (strict simultaneity is not needed, so no lock is taken).
- **The count is held in an `Interlocked` counter rather than `ConcurrentDictionary.Count`.** The latter
  **takes every lock**, which would stop the open / close hot paths for the sake of a diagnostic. The ids are
  unique per issue and `Return` decrements only when one existed, so it always agrees with the dictionary's
  count.
- **Windows is always `0 open / peak 0`** - Dokan does not go through the table and puts the object on
  `DokanFileInfo.Context`. That it is **"not counted"** rather than "nothing open" was written into
  [Pgfsctl.md](../Pgfsctl.md).
  If counting on Windows becomes desirable, the first step is making the Dokan adapter go through `Rent` /
  `Return` (**the disposable contexts `FileSystem.Resolve` creates are never closed**, so renting them as they
  are would leak).
- **The test**: **a key-existence check** was added to `test_cp_status_json_shape` in
  [control_plane.ps1](../../tests/windows/control_plane.ps1) (the count stays 6). **It does not verify the
  value** - it is always 0 on Windows, so counting only means something on the Linux side. What is being
  protected here is that **the key does not disappear and silently render the Linux leak regression
  meaningless**, and being a null check it is not the kind of test to apply to a pre-fix build.
- **The FUSE side writes the leak regression** (the FUSE side is what actually uses `HandleTable`).

## Who decides where an append goes

**A separate hole that came out as a by-product of stage B.** Even with the handle identification fixed,
**a path remained where the OS side decides "where to write"**.

| | Who decided the end | The symptom |
|---|---|---|
| Linux | **The kernel** (`generic_write_checks` doing an `i_size_read`) | An offset that knows nothing of another mount's growth comes down. Measured, **it overwrote and erased 6 bytes of B's** |
| Windows | **pgfs** (Dokan only says `WriteToEndOfFile`) | Up to stage A it used the stale `Inode.Size` the handle held. Measured, **a loss of 11 -> 6 bytes** |

**That "who decides the end" differs between the operating systems and that neither was looking at another
mount's growth** is the substance of it.

**The decision (proposal (2))**: **put `Api.AppendData(OpenFileContext, source)` in Core and have
Core resolve and decide the end itself.** FUSE routes here on `fi.flags & O_APPEND` and Dokan on
`WriteToEndOfFile`. **The implementation that decides where an append goes becomes one place**, so raising it to
indivisibility (proposal (3)) later means fixing one place.

- **While write-back is on, the local dirty size is the authority** (`AppendOffsetOf`). Looking only at
  `Inode.Size` writes into the middle of the pre-flush dirty region when **that inode has fallen out of the LRU
  and been read back from the database**.
- **[Mount.md, the append contract](../Mount.md) is the source of truth for the contract.** Indivisibility is
  guaranteed only when **(1) it is write-through and (2) the write fits in one callback**.
- **Even under (2) the change from "a loss" to "interleaving" happens**, so **the contract text lands in the
  same commit as the implementation** (the Linux side's finding: do not create a state where the behaviour
  changed and the contract has not caught up).

~~**Proposal (3) is designed together with stage C**~~ -> **it went in on its own without waiting for stage C**
. The "lock on the inode row" that had been estimated **was not needed** - `LockData` already
serializes the writers to the same body, so **one plain SELECT after taking the lock is enough**.
The consideration of Citus's shard-touch order was therefore unnecessary either. See the implementation status
of proposal (3) below.

**The measured grounds** (the FUSE side):

- `max_write` is **a negotiated value** (**1 MiB** on the measured host, not 128 KiB). A 200 KiB append was not
  split, and 4 MiB broke into 4 x 1 MiB.
- **The kernel decides the offsets of every callback at once from the first `i_size`** - interleaving 12 writes
  of 10 bytes from the peer during a 4 MiB append left A's offsets strictly cumulative, and
  **the peer's 120 bytes were erased entirely**.
- Therefore **"not losing anything" is the limit for an append larger than `max_write`**, and the same
  indivisibility as a local FS (holding `i_rwsem` for the duration of one `write(2)`) cannot be built from a
  FUSE callback.

### The implementation status of proposal (3) (as-built)

**It went in.** **The end is settled inside the write transaction.**

```csharp
// WriteDataThroughOnce
this.LockData(conn, tx, dataId);
if (appendAtEnd) { offset = this.ReadSizeInTx(conn, tx, inode.Id) ?? inode.Size; }
```

- **The lock on the inode row was not needed.** It had been estimated as "lock the inode row to settle the end =
  it breaks Citus's shard-touch order so a deadlock retry is a precondition", but **`LockData` already
  serializes the writers to the same body**, so **one plain SELECT after taking the lock is enough**.
  **A plain SELECT creates no deadlock edge**, so it does not break the shard-touch convention either
  (the inode is touched once at the end of the tx). **Thanks to that oversight, it could go in on its own
  without waiting for stage C.**
- **That the value seen after the wait is current was confirmed on real Citus hardware** (rf=2). In the probe's
  log, the `st_size` read after the lock on the second and later rounds was **always ahead of the local cache by
  exactly the peer's commits**.
- **A negative offset is not used as the signal.** The entry to `WriteData` returns 0 for `offset < 0`, which
  **from FUSE's point of view is a short write = a failed write (`-EIO`)** (the Linux side hit it in a probe).
  A separate entry point `WriteDataAppend` was created, passing an `appendAtEnd` flag to `WriteDataThrough`.
- **It is out of scope while write-back is enabled.** There is no tx, so there is nothing to settle inside one,
  and as long as the peer's bytes are not in the database it cannot be made indivisible in principle.
  **The local dirty size stays the authority** (`AppendOffsetOf`).
- **Concurrent appends from the same handle close the same way** (everyone is serialized by `LockData` before
  reading the end). The 4 split callbacks were **serial** in the measurement, but **that is not proof that they
  are not concurrent**, so the design stays on the premise of concurrency.

**The measurements** (two mounts appending at once, A 4 MiB and B 10 bytes x 12):

| | The difference |
|---|---|
| Before anything | **-4,194,304** |
| Only making `st_size` monotonic | Linux **-40** / Windows **-10** |
| **Settling the end inside the tx (current)** | **0** (green on 3 runs each on Linux and Windows) |

**[Mount.md, the append contract](../Mount.md) is the source of truth for the contract.** The part that was
going to say "they interleave (the order mixes)" **became unnecessary** - each callback writes on from the
previous commit, so what mixes is **"another mount's append slipping into the gaps between the splits"**, and
the bytes of one `write(2)` are neither lost nor reordered.

## The unresolved points (to be decided before the implementation)

1. ~~**Who holds it**~~ -> **Decided**: **a separate class `HandleTable` is created and `Api`
   holds it as a `private readonly` field.** `Api` already holds
   `InodeCache` / `ContentCache` / `DirtySet` / `DirtyNamespace` / `IdReservation` - **five of them in exactly
   the same shape** - so following the precedent satisfies both "do not fatten `Api`" and "it can be reached
   from the flush paths (`CloseInode` / `FlushInode`)". No new idiom is introduced.
2. ~~**Whether the synchronous-close mark moves onto the handle**~~ -> **Decided not to move it**
   (the Linux side's review). See the section on not moving the synchronous-close mark onto the
   handle.
3. **The contract for I/O after a delete**: Linux (POSIX) has "the fd survives an unlink" and Windows has
   "the name too survives until the last handle". The intent is not "which one to align with" but a division
   where **Core holds only the reference count and the OS layer decides how the name appears**.
4. **The locks**: a lock-free handle table (a ConcurrentDictionary) is enough, but C's reference count needs
   per-inode exclusion. It must not break the Citus shard-touch convention
   ([support_for_citus.md](support_for_citus.md)).

#### The leak regression of (6) (as-built)

**[tests/linux/handles.sh](../../tests/linux/handles.sh) was newly created (6 cases).**
It is a dedicated suite rather than part of the e2e suite because **psql is needed to fire the snapshot with a
ping control NOTIFY** ([e2e.sh](../../tests/linux/e2e.sh) is kept standalone).

| The test | What it watches |
|---|---|
| `test_handles_idle_is_zero` | A mount doing nothing is `0 open` |
| `test_handles_file_open_close_balances` | 3 fds give 3 open -> closing gives 0 |
| **`test_handles_dup_fd_survives_first_close`** | **Duplicating an fd and closing only one leaves the other alive** |
| `test_handles_readdir_balances` | 20 `ls`es come back to 0 (OpenDir / ReleaseDir) |
| `test_handles_zero_after_mixed_workload` | 0 after a mixed workload |
| `test_handles_peak_records_concurrent_opens` | `peak` does not go down on a close (monotonic) |

**The discriminating power was confirmed with two ways of breaking it** (the probes were removed):

- **Moving the `Return` from `Release` to `Flush`** -> **3 cases failed.**
  It is a way of breaking it that can actually happen - **bash's `exec 9< file` closes an intermediate fd after
  opening**, so one `Flush` comes down and **the handle leaves the table although the fd is still alive**.
  That is the measurement behind "the place to return it must be `Release`" (stage A).
- **Removing the `Return` from `ReleaseDir`** -> `test_handles_readdir_balances` failed with
  **`open did not come back to 0 after 20 ls runs (got '20')`** (with the number matching too).

## The synchronous-close mark is not moved onto the handle (decided)

It was first written that "holding it on the handle would naturally close the window of 'truncate on one fd ->
append on another'", but it was **the other way round** (the Linux side's review):

- What A-7 closed happens **when the truncate and the append are on different fds**. Holding the mark on the
  handle means **it disappears with the close of the fd that truncated**, so the following append's fd has no
  mark and **it goes back to the same zero-length garbage as before the A-7 fix**.
- The reason A-5 puts the mark on **both the inode and the data keys** is to make it effective for
  **another handle that opened a hardlink sibling inode**. That cannot be expressed per handle.

-> **The safety guarantee stays on the inode / data keys.** What the handle holds is only
**the diagnostic information (`TruncatedByThisHandle`)** that "this handle issued a truncate". 1e's discipline
(A-4 to A-7) is not moved.

## The files touched (an estimate)

- New: `src/core/src/Api/OpenFileContext.cs` (plus the handle table)
- `src/dokan/src/FileSystem.cs`: replace `OpenFile` (**the current 3 fields move over as they are**)
- `src/fuse/src/FileSystem.cs`: swap what `fh` holds in `open` / `create` / `release`, and take `Read` /
  `Write` / `FlushPath` to the id
- `src/core/src/Api/Api.cs`: make `CloseInode` / `FlushInode` able to take a handle context (stage B onwards)
- The tests: add reproductions of "rename or delete while open" and "a write across a `data_id` re-pointing" to
  both operating systems in stages B and C

## How it was implemented

> **as-built**: stages A to C were implemented in the order A -> B -> C (**stage D is not started**). Core's
> handle table, the FUSE adapter and the Dokan adapter were built as separate pieces of work on top of the
> same Core design.

- **Stage A is behaviour-preserving**, and its acceptance criterion was "every existing suite on both operating
  systems green".
- **Stage A alone does not close problem 1 (an open fd landing on a different file after a rename).**
  Stage B closes it. A's value is that 3 (the audit subject) and 4 (WriteThrough) become part of Core's
  vocabulary.
- Point 3 (the contract for I/O after a delete) and point 4 (the locks) belong to stage C, and the performance
  measurement was stage B's gate.

## The Core design of stage C (the Dokan side; implemented)

> **as-built**: the proposals of this section were agreed, and **C-1 / C-2 / C-3 are all implemented**.
> **The C-1 / C-2 / C-3 sections are the source of truth for what actually went in**, and what follows is the
> description as proposed.
> **In particular, `hard_remove = 1` dropped out of C-2's acceptance criteria** (see the section on why
> `hard_remove` is not raised).

The Core-side design proposal against the Linux side's three requirements (a reference count / `DeletePending` /
removing the body at the final release).

### The crux is "where to put an inode with no name"

To realize POSIX's "unlinked but the fd is alive" in the FS itself, **somewhere to put a body whose name is
gone** is needed. In pgfs **one row of `{prefix}inode` is "a name plus the attributes"** and
**removing the name = removing the row**, so it cannot be expressed as it is. libfuse getting by with a rename
to `.fuse_hidden` is exactly filling that hole from outside.

**There are three proposals.**

| | How it works | Citus | The schema | How the remnants appear |
|---|---|---|---|---|
| **O-1 An orphan parent** | Move `parent_id` to a reserved value, taking it out of the tree | ❌ **Breaks.** `parent_id` is **the distribution key** and Citus does not allow an `UPDATE` of a distribution key (a DELETE plus an INSERT is possible, but the consistency of the audit and the data_id makes it heavy) | Unchanged | They stay under the reserved parent |
| **O-2 Add a column** | Raise an `unlinked_at` and rewrite the `name` to a unique internal name. `lookup` / `readdir` exclude them with `unlinked_at IS NULL` | ⭕ It does not touch the distribution key | **A column is added** (idempotently by mkfs; `{prefix}mounts` is the precedent) | **They stay as rows** (visible if a filter is missed) |
| **O-3 Keep only the body (recommended)** | The inode row is **removed as it is today**. **`{prefix}data` and the chunks are not removed until the reference count reaches 0.** An open handle keeps reading and writing through the `OpenFileContext` snapshot plus the `DataId` | ⭕ It touches nothing | **Unchanged** | **They do not appear in an enumeration** (there is no inode row, so no trace is left in the namespace) |

**O-3 is recommended.** The reasons:

1. **pgfs already separates the data into `data` / `data_chunk`**, so POSIX's "the inode is alive but has no
   name" can be expressed as **"the data is alive but there is no inode row"**. **That separation is an existing
   asset** and no new concept is added.
2. **What stages A and B built works as it is.** The handle already holds the `InodeId` and an attribute
   snapshot, and it holds the `DataId` too. **Reads and writes start from the data_id**, so they can continue
   with no inode row.
3. **Neither the schema nor Citus's distribution key is touched.** O-2's "a forgotten filter becomes a new hole"
   is avoided too.
4. **The remnants do not appear in the namespace.** The present real harm is `.fuse_hidden*` appearing in
   another mount's `ls` (measured), and O-1 / O-2 **only change where it is put, leaving the possibility of it
   being visible**. An O-3 remnant is "a data row referenced by no inode", which **counts towards `df` but does
   not appear in an enumeration**.

### The specifics of O-3

| | What happens |
|---|---|
| An `unlink` (no references) | The same as today. The inode row plus the data plus the chunks are removed |
| An `unlink` (**with an open handle**) | **Only the inode row is removed.** The `data` row and the chunks are kept and `DeletePending` is raised on the handle |
| A `read` / `write` on an open handle | Even if `ResolveHandle` cannot look up the inode row, **it returns the ctx's snapshot if it is `DeletePending`** (it does not throw a `StaleHandleException`) |
| An `fstat` | Returned from the ctx's snapshot (there is nowhere to write back to in the database, so the size and so on **advance only in memory**) |
| The final release (`Release` / `CloseFile`) | When the reference count reaches 0, **the `data` plus the chunks are removed in one tx** |
| There are hardlink siblings | **Nothing is removed.** While a sibling with `st_nlink > 0` remains it is as before (the existing nlink management) |

### Where the reference count lives and how it is entered

**It counts per inode.** **It is separate from `HandleTable`** - there can be several handles but only one body,
and **Dokan does not go through the handle table** (the implementation status of the Dokan adapter).

- `Api` holds it as a `private readonly` sixth companion (the same shape as `InodeCache` / `ContentCache` /
  `DirtySet` / `DirtyNamespace` / `IdReservation` / `HandleTable`).
- **The entry points are explicit calls shared by both adapters**: `Api.OpenHandle(ctx)` /
  `Api.CloseHandle(ctx)`. They do not ride on `HandleTable.Rent` / `Return` - that would **count even the
  disposable contexts Dokan's `Resolve` creates**, which are never closed, so **the count would not come back**.
- **A by-product**: `handles.open` becomes a meaningful value on Windows too (it is always 0 today).

### Citus's shard-touch order (unlike proposal (3), it is needed here)

Proposal (3) getting away with a plain SELECT was **the exception**. Stage C's final release is
**a tx that removes the inode, the data and the chunks**, and it has to ride **the same lock order as the
existing delete path**. The order `LockInode` -> `LockData` must not be broken
([support_for_citus.md](support_for_citus.md)). **The reference count itself is in memory so it adds no database
lock.**

### The cross-mount trade-off (**written into the contract**)

**The reference count is per-mount memory.** Therefore:

- **An open-then-unlink within the same mount is POSIX-correct** (it can be read and written while the fd is
  alive).
- **If another mount unlinks it, this fd dies** (`-ESTALE`). The count in the peer's memory cannot be seen from
  here, and **the peer has no way of knowing that someone has it open**.
- Counting it in the database would **add one tx per open and per close**. The cost on the hot path is far too
  high, so it is not taken.
- **NFS handles the same problem with a silly rename**, so as a trade-off it is straightforward.

### Cleaning up what an abnormal exit left behind (**gathering `.fuse_hidden*` / orphan data / `{prefix}mounts` into one**)

All three have the same shape of **"an abnormally terminated mount left something behind and nobody cleans it
up"**.

| The remnant | The state today | The condition for cleaning it |
|---|---|---|
| `.fuse_hidden*` (Linux) | A `kill -9` leaves it **forever, contents and all**. It appears in another mount's `ls` | **After stage C nobody creates them**, so it is a one-off. **It cannot be removed while a mount currently has it open** |
| Orphan data rows (the remnants of O-3) | They can be born in stage C | **Referenced by no inode** and **the owning mount is not alive** |
| The remnants in `{prefix}mounts` | They accumulate with every `kill -9` (11 rows accumulated during testing). **The harm is only cosmetic** | **The heartbeat has stopped** and **it is not a loss gravestone (B-2)** |

**There is one common danger**: **removing something a living mount is using breaks it.**
All of them therefore have to be **cross-checked against the liveness in `{prefix}mounts`**.

**The design proposal**: add **one subcommand** to `pgfsctl` (provisionally `pgfsctl prune`).

- **The default is a dry run.** It only lists what would be removed. `--apply` carries it out.
- **If even one live mount exists, the orphan data and the `.fuse_hidden*` are not touched by default**
  (only the `{prefix}mounts` remnants can be removed, because the stopped heartbeat can be judged).
  `--force` is written in the documents as something for use after stopping every mount.
- **A loss gravestone (`unflushedLoss > 0`) is not removed.** B-2 uses it for the warning at the next mount.
- **It is not run automatically at `mount` startup.** Even if "there is no live mount other than me" can be
  judged at startup, there is a window for missing **a peer that is starting at the same moment**. It is made
  **something to fire explicitly**.

### The stages and their acceptance criteria (proposed)

| | What it is | The acceptance criteria |
|---|---|---|
| **C-1** | The reference count plus `Api.OpenHandle` / `CloseHandle` called from both adapters. **The behaviour does not change** | Every suite on both operating systems green / `handles.open` works on Windows too |
| **C-2** | `DeletePending` plus O-3's retention plus the removal at the final release. ~~FUSE raises `hard_remove = 1`~~ -> **not taken** (the section on why `hard_remove` is not raised). Instead, **`.fuse_hidden*` is removed from the enumeration** (alternative H) | **`test_open_unlink_read_still_works` stays green** (libfuse gets it through today; pgfs gets it through after C-2) / **`.fuse_hidden*` is invisible to another mount** (in the same commit as stage C) / the Windows delete-pending semantics do not change |
| **C-3** | `pgfsctl prune` (the unified cleanup) | The dry run does not sweep up a live mount / it does not remove a gravestone / the remnants really are removed |

### The implementation status of C-1 (as-built)

**Core and the Dokan side went in. The behaviour is unchanged** (it only counts). **The FUSE-side wiring is the
Linux session.**

- The new [OpenInodes.cs](../../src/core/src/Api/OpenInodes.cs) - **a per-inode reference count**
  (`Acquire` / `Release` / `CountOf` / `Count`). `Release` returns **the remaining reference count** and
  **drops a key that reaches 0 from the table** (remembering every inode that was ever opened would grow without
  bound on a long-running mount).
- `Api.OpenHandle(ctx)` / `Api.CloseHandle(ctx)` were exposed as **the entry points shared by both adapters**.
  **They do not ride on `HandleTable.Rent` / `Return`** - Dokan does not go through the table, and
  `FileSystem.Resolve` has a path that creates **a disposable context**.
- `OpenFileContext.Counted` was added. It is the mark that stops **closing an uncounted context from
  decrementing the count**, and **it absorbs a double open and a double close with the same mark**.
  `Api` manages it and **the adapters do not touch it**.
- The Dokan side was **concentrated in the single `AttachHandle`**. `CreateFile` has many branches (opening an
  existing one / opening to overwrite / creating a new one / a directory), and **missing one leaves the count
  never coming back**. The release is **only in `CloseFile`** - `Cleanup` is the trigger for a delete, not a
  close (the same story as putting it on `Flush` breaking things on FUSE).
- **`inodes`** (the number of bodies that are open) was added to `handles` in `pgfsctl status`.
  **The text output is `handles      : N open / peak M / K inodes`.** **A snapshot from before C-1 has no
  `inodes`**, so when it is absent the item is not printed at all (`NodeLong` returns -1 for a missing value, so
  printing it as it is **looks like a value of -1**).
  **`open` / `peak` are still the values of FUSE's handle table** and stay 0 on Windows.
  **Once both operating systems call `OpenHandle`, moving `open` onto this count** would be natural, but
  **changing it now would turn the Linux side's `handles.sh` red before the FUSE wiring**, so it is decided
  after C-1 completes.

**The test**: `test_cp_open_inodes_counted` was added to
[control_plane.ps1](../../tests/windows/control_plane.ps1) (6 -> **7 cases**). 0 before opening -> 3 with 3 open
-> 0 after closing.
**Confirmed to fail against a build with the `OpenHandle` calls removed**, failing with
`3 were opened but inodes did not reach 3 (a missed count in a CreateFile branch?)`, which points at the cause.

**The regression**: all 5 Windows suites green - e2e 35/35 / cross-client 10/10 / write-back 6/6 /
metadata write-back 4 passed + 1 skip / **control-plane 7/7**.

### The implementation status of C-2 (Core, as-built)

**The Core side went in. The FUSE side is owned by the FUSE side.**
~~`hard_remove = 1` and the rest~~ -> **it was settled that `hard_remove` is not taken** (the section on why
`hard_remove` is not raised). **What went in on the FUSE side is alternative H = removing `.fuse_hidden*` from
the enumeration.**

**What went in** (as in proposal O-3: the inode row is removed and `{prefix}data` plus the chunks are kept):

| The place | What it does |
|---|---|
| `ReleaseOrRelinkDataInTx` | When the last reference is removed, **the body is kept if any handle still has that inode open** (`OpenInodes.MarkOrphan`) |
| `Api.CloseHandle` | **The body is dropped once the reference count reaches 0** (`DropOrphanData` = discard the dirty data and remove the chunks and the data row in one tx) |
| `Api.TryResolveHandle` | Even when the inode row cannot be looked up, **it returns the ctx's snapshot if it is an orphan** (it does not throw a `StaleHandleException`) |

**The decision was put inside `DeleteInodeInTx`, so it works on all three delete paths** - `unlink` / `rmdir` /
**a `rename` replacement** (both write-through and pending). Review finding (1), "the rename replacement is
missing", is closed structurally by this placement.

**`DeletePending` is held by Core keyed on the inode rather than on `OpenFileContext`** (a change from the design
proposal). When **several handles have the same body open**, per-ctx leaves "which handle holds the mark"
undecided. The liveness of a body is a per-inode fact, so it went inside `OpenInodes`.

**Before the final release it checks "is it really gone"** (`DropOrphanIfGone`). The mark is put on inside the
delete tx, so **if that tx rolls back or the same name is recreated**, the mark alone can remain. Without the
check it **removes the body of a living file**.

#### ⚠ Only a `rename` replacement can poke at it from Windows (measured 2026-09-21)

**This window does not open on the `unlink` side.** Windows **does not push a delete down to the FS until the
last handle closes**, so closing the second of two `DeleteOnClose` handles first **leaves it in the enumeration**
while the first is alive (= pgfs has not received the delete request). **It was first made an e2e test, but it
was green on the pre-fix build too**, so it was thrown away (no test with zero discriminating power is kept).

**Only a `rename` replacement removes a name while leaving an open handle.**
`test_rename_over_open_victim_keeps_body` in [e2e.ps1](../../tests/windows/e2e.ps1) (36 -> **37 cases**) pokes at
that. **Confirmed to fail against the pre-fix build**, failing with
`the overwritten side's handle lost its body (the read failed): FileNotFoundException`.

**The regression**: all 5 Windows suites green - e2e 37/37 / cross-client 10/10 / write-back 6/6 /
metadata write-back 4 passed + 1 skip / control-plane 7/7.

#### To the FUSE side (the FUSE side)

- **`Unlink` / `Rmdir` / `Rename` can stay as they are** - `Api.DeleteInode` decides inside.
- **Call `Api.CloseHandle` in `Release`** (wired in C-1). **Core drops the body the moment 0 is returned.**
- ~~**Raising `hard_remove = 1` comes after this.** The moment it is raised, both `unlink` and rename-over ride
  Core's retention path.~~ -> **It is not raised.** **It was taken as far as (1) and came back** (the section on
  why `hard_remove` is not raised). **Raising it stops `read` reaching the FUSE side and breaks rename-over
  too.** **What went in is alternative H** = removing `.fuse_hidden*` from the enumeration.
- **A caution**: if **the flush runs first inside `Release`** (`FlushPath` -> `CloseInode`), it becomes
  **a flush against an orphaned inode**. The chunks are still alive so there should be no real harm, but
  **please confirm that it does not return `-EIO` there** (`FinishWriteInodeInTx` returns early when the parent
  cannot be looked up).


### Why `hard_remove` is not raised (the record of going as far as (1) and coming back)

**The conclusion: `hard_remove = 1` does not go with libfuse's high-level API.** It is not raised.
Instead, **`.fuse_hidden*` is removed from the enumeration** (alternative H below) plus **the remnants are
cleaned up with `pgfsctl prune`**.

**Somebody will go down the same road next** (seeing `.fuse_hidden` and thinking "surely pgfs can remove this
itself" is natural), so **how far it went and what turned it back** is recorded.

| The step | What was done | The result |
|---|---|---|
| 1 | Raise `hard_remove = 1` | **The `read` does not reach us** (0 arrivals). The high-level API **requires a path for every operation**, so an unlinked node becomes `-ENOENT` at `get_path` |
| 2 | Raise `fuse_config.nullpath_ok` (offset **96**, computed from the upstream header of 3.10.2 and verified by writing it back and measuring) | **The `read` gets through** (arriving with `path=(null)`). `dd` / `head` / `wc` / `od` can read after the unlink |
| 3 | But **`cat` fails** | `strace` shows `fstat(0, ...) = -1 ENOENT`. Under the `nullpath_ok` contract, **the path of a `getattr` is NULL only when `fi != NULL`**, but **the kernel does not put a file handle on the `GETATTR` of an `fstat(2)`** (it goes through `vfs_getattr(&file->f_path, ...)`, so `FUSE_GETATTR_FH` is not set) |
| 4 | **(1): remember the orphan's path in memory and answer the `fstat`** | **The removed name came back to life in `stat`** (`[ -e held.txt ]` was true and `stat` returned 1 byte). It does not appear in `ls`, so it is the half-state of **"it is not in the listing but it can be stat'd"**, which is worse than `.fuse_hidden` |

**Why (4) is a dead end**: with the high-level API,

- `fuse_lib_lookup` -> `lookup_path` -> `fuse_fs_getattr(path, ..., fi = NULL)`
- `fuse_lib_getattr` (= `fstat`) -> `fuse_fs_getattr(path, ..., fi = NULL)`

**both come down with "the same path and a NULL `fi`"**. They are **completely identical** from our side, so
**"answer the `fstat` but not the `lookup`" cannot be written**.

-> **libfuse renaming rather than remembering is exactly to create that separation.** Doing the same in the FS
means **actually changing the name**, which is **design O-1 (moving `parent_id` to an orphan parent)**.
And **O-1 breaks on Citus's distribution-key restriction** (an `UPDATE` of a distribution key is not allowed).
**It goes round and comes back.**

**Moving to the low-level API (`fuse_lowlevel_ops`) would be a different story**, but **it is not worth going
that far now**.

#### Alternative H - removing `.fuse_hidden*` from the enumeration (taken)

- **The decision is in [Api.IsLibfuseHidden](../../src/core/src/Api/Api.cs) and they are removed from
  `ListChildren`.** **At the time it was put in Core and made effective on both operating systems** - the real
  harm is **appearing in another mount's `ls`**, and those other mounts include Windows.

  > **⚠ It changed. It now hides them only on Linux (FUSE).** `Api.HideLibfuseLeftovers`
  > (`true` by default) is set to `false` by Dokan. **On Windows libfuse does not create `.fuse_hidden*`**, so
  > there is no reason to hide them, and hiding them **leaves only "it is not in the listing and it cannot be
  > removed"** - and on Windows **it cannot be removed through a path that goes via the enumeration, such as
  > `Remove-Item` or Explorer** (`[System.IO.File]::Delete` = the Win32 `DeleteFile` directly can remove it).
  > The judgement is that **"there is a way to remove it but nobody can find it" is worse than "it cannot be
  > removed"**.
  > **[namespace-policy.md](namespace-policy.md) is the source of truth for the decision and the comparison of
  > the alternatives.**
- **They are not removed from the path resolution (`GetByPath`).** Removing them would **stop the very mount
  keeping that fd alive from looking up its own hidden file** (libfuse fires `getattr` / `release` at the hidden
  name).
- **The decision matches libfuse's format strictly** (`.fuse_hidden` plus **16 hex digits**). Rejecting on the
  prefix alone would **also remove a name of the user's own such as `.fuse_hidden...`**.
- **The permanent remnants of a `kill -9` are handled by [`pgfsctl prune`](../Pgfsctl.md)** (which went in with
  C-3).

**The price (written into the documents)** - **after the change it remains only on Linux**:

| | What it is |
|---|---|
| Both operating systems | **The row stays in the database**, so it counts towards the usage in `df` / `du`. The cleanup is `prune` |
| **Linux only** | **An `rmdir` can give `ENOTEMPTY` although nothing is in the enumeration** (a parent with a hidden file left in it). **It looks empty but cannot be removed**, so when in doubt, fire `prune` |
| **Windows** | **They are not hidden, so this price does not apply** - they appear in the enumeration, `Remove-Item` removes them and `rmdir` gets through (the change of) |
| Both operating systems | **The POSIX semantics are carried by libfuse as before**, so neither `cat` nor `fstat` breaks |

> **A place where the estimate of the time turned out wrong is recorded**: when this price was written, the
> premise was that **"when in doubt, fire `prune`" is possible in any environment**. In reality
> **Windows has no psql**, and `prune` touches the data side so **every mount has to be stopped**.
> Furthermore it was written on the premise that **`.fuse_hidden*` is something libfuse creates**, but
> **a user giving a name in the same format produces the same state** (the decision looks only at the format and
> does not distinguish who created it). **Those two are the reasons for deciding not to hide them
> on the Dokan side.**

### The implementation status of C-3 (as-built)

**It went in.** [PruneAdmin.cs](../../src/core/src/Api/PruneAdmin.cs) (Core) plus `pgfsctl prune`
([PruneCommand.cs](../../src/ctl/src/PruneCommand.cs)). **[Pgfsctl.md](../Pgfsctl.md) is the source of truth for
the CLI specification.**

**The three remnants were gathered into one** - the old rows of `{prefix}mounts`, the orphan data rows and the
`.fuse_hidden*` files. **Splitting the liveness decision per kind** (review finding (4)) was implemented as
stated too:

- A `mounts` row is removed if its heartbeat is older than `--mounts-older-than` (3600 seconds by default).
- **The data-removing side is skipped if even one mount is alive** (overridable with `--force`).
- **A gravestone (`unflushedLoss > 0`) is not removed.**

**An anti-join cannot be used on Citus**, so the orphan data is detected by **fetching the id list of `data` and
the DISTINCT of `inode.data_id` separately and taking the difference locally**. The same restriction was hit in
practice (even a join between inodes gives `the query contains a join that requires repartitioning`). prune is an
administrative command, so two round trips are fine.

**`Apply` rescans the data side after removing the inodes of the `.fuse_hidden*` files.** Those bodies become
orphans the moment they are removed, so **not catching them in the same run would mean having to fire it twice**.
The inodes go first so that being interrupted does not leave a row of "there is a name but no body"
(the same shape as the defect [data-id-lifecycle.md](data-id-lifecycle.md) fixed).

**Confirmed on real hardware (the server's `pgfs` schema)**:

| | The result |
|---|---|
| The dry run | Correctly displayed old `mounts` **104 rows** / a gravestone **1 (protected)** / orphan data 0 / `.fuse_hidden` 0 |
| A dry run after making artificial remnants | Detected **1 row** of orphan data and **1** `.fuse_hidden0000000900000001` |
| **`--apply` with a live mount present** | **Removed only the 104 `mounts` rows. The data side was skipped** (with a message) |
| `--apply` after stopping the mount | Removed **1** `.fuse_hidden` plus **2 rows** of orphan data (including the rescanned one) |
| Verified in the database | 0 data rows / 0 chunks / 0 `.fuse_hidden` / **the gravestone survives** |

**There is no automated test yet.** Making the remnants **needs psql** (a `.fuse_hidden` is born naturally only
on Linux), so **putting it in a Linux-side suite is the natural thing**. psql cannot be driven from the Windows
e2e or control-plane suites.

### The review folded in (settled on the Linux side's findings)

**It proceeds with O-3** (the Linux side agreed). On top of that, **four points of the design were corrected**.
The findings themselves are in the Linux side's review section below.

**(1) A `rename` replacement fires O-3's retention too (it was missing)**

What `hard_remove = 0` protects is not only `unlink`. **When the side being overwritten by a rename (the victim)
is open, libfuse escapes it to `.fuse_hidden` too** (measured by the Linux side).
**The moment `hard_remove = 1` is raised, fixing `unlink` still breaks rename-over.**

-> **O-3's retention fires from both `Unlink` / `Rmdir` and the replacement path of `Rename`.**
Under POSIX both are the same "the name goes but the body is alive".
**The existing `test_rename_replace_existing` cannot detect this hole** - it only looks at the result of the
replacement and **does not open an fd**. **The Linux side adds a test in the same commit as C-2.**

**(2) The cross-mount trade-off "is already the case" (the wording changes)**

The same thing is written into the contract, but as **"it is already the case" rather than "it becomes so in
stage C"**. Otherwise **it reads as a degradation in stage C**. Measured (the Linux side):

```
A keeps it open while B does an rm -> read from A's fd -> fails (No such file or directory)
```

The reason is structural: **it is the daemon that unlinked which escapes it to `.fuse_hidden`**, and
**B has no way of knowing that A has it open**, so B does a plain unlink.
What changes in stage C is only **that within one mount it goes from relying on libfuse to pgfs doing it
itself**.

**(3) The logical size after an unlink does not stay in the database (written into the contract)**

O-3 removes the inode row, so **there is nowhere for the `st_size` to live** and what stays in the database is
`data.total_size` = **the occupied bytes** only. **For a sparse file that does not agree with the logical size.**
The ctx's snapshot is enough while it is open, but **if the daemon dies, the logical size of the orphan data
becomes unknown**. prune only removes things so the real harm is small, but **a line saying "the size after an
unlink is only in memory" goes into the contract**.

**(4) prune's liveness decision splits per kind of remnant**

Making the condition "the heartbeat has stopped" alone would **remove the row of a living mount whose heartbeat
went down because of a database failure**. **Removing the row makes that mount invisible as live from the next
prune, which cascades into removing "orphan data that is in use".**

| The remnant | The liveness decision |
|---|---|
| A `{prefix}mounts` row (**cosmetic harm only**) | The heartbeat has stopped plus it is not a gravestone. **A generous grace period** |
| Orphan data / `.fuse_hidden*` (**it removes data**) | The condition is **that no `{prefix}mounts` row exists** (not judged on how old the heartbeat is). On the same host, **the liveness of the pid** is looked at too |

**Do not damage the evidence the data-removing side relies on, for the sake of cleaning up `mounts`, whose harm
is cosmetic only.**

**(5) If the trade-off goes into the contract, a test goes with it**

If the fd dying across mounts is made a contract, **a net is needed to notice if it silently changes**.
Today it is only "that is how it happens to be". **C-2 makes it pgfs's own path = the behaviour becomes easier
to change**, so the Linux side adds it then.

**Additions to the acceptance criteria** (C-2):

- **Replace a file while its victim is open -> the victim's fd can still read** ((1))
- **Across mounts, the fd dies when another mount unlinks** - pinning down that it is as contracted ((5))

### The points that were open, and how they were settled

1. ~~**Is O-3 acceptable**~~ -> **Agreed.** But the review folding-in (1) (the rename replacement) is to be
   taken in.
2. ~~**May the cross-mount trade-off go into the contract**~~ -> **Agreed.** But it is written as
   **"it is already the case"** as in (2), and the test of (5) is added.
3. ~~**prune's defaults**~~ -> **Agreed** (a dry run plus `--apply`, with `--force` too). But the
   **liveness decision splits per kind of remnant** as in (4).

## The review of stage C's Core design from the FUSE side

A review of **the Core design of stage C** above. **Only what was backed by measurement** is written as
evidence.

### Points of agreement

- **Agreed on O-3 (keep only the body).** It needs neither a new column nor an update of a distribution key, and
  **the remnants not appearing in the namespace** is the decider.
  `.fuse_hidden*` appearing in another mount's `ls` is the present real harm, so closing that structurally is an
  advantage O-1 and O-2 do not have.
- **Agreed that `Release` is the only release point and that it does not ride on `Rent` / `Return`.**
  That **counting in `Flush` breaks with a duplicated fd** has been measured (the leak regression of (6):
  moving the `Return` to `Flush` means **bash's `exec 9< file` closes an intermediate fd and sends one `Flush`
  down**, so the count comes back while the fd is still alive).
- **Agreed that prune defaults to a dry run and is not run automatically at startup.** "The window for missing a
  peer that is starting at the same moment" is real.

### ⚠ Finding 1 (heavy): **the side being overwritten by a `rename` is missing from the design**

**What `hard_remove = 0` protects is not only `unlink`.** libfuse **escapes the side being overwritten by a
rename to `.fuse_hidden` too, when it is open**.

Measured (the same mount, `mv src.txt dst.txt` while `dst.txt` is open):

```
today (hard_remove=0):  read from the victim's fd -> 'VICTIM' was read
                        .fuse_hidden0000000400000001 appears in the directory and goes after the close
hard_remove=1:          read from the victim's fd -> fails (No such file or directory)
```

**The moment `hard_remove = 1` is raised in C-2, fixing `unlink` still breaks rename-over.**
Under POSIX both are the same "the fd survives the name going", so **O-3's retention has to fire from the
replacement path of `Rename` too**.

- **Please add to the acceptance criteria**: "**an existing fd can still read after an open file is overwritten
  by a rename**".
- **There is no such test on the Linux side today.** `test_rename_replace_existing` **only looks at the result
  of the replacement and does not open an fd**, so it cannot detect this hole. **It is added in the same commit
  as C-2** (adding it now would be a red test).

### Finding 2: the cross-mount trade-off is **not a regression** (measured)

**"If another mount unlinks it, this fd dies" is already the case today.**

```
A keeps it open while B does an rm -> read from A's fd -> fails (No such file or directory)
```

The reason is structural - **it is the daemon that unlinked which escapes it to `.fuse_hidden`**, and
**B has no way of knowing that A has it open**, so B does a plain unlink.

-> **Agreed on writing the trade-off into the contract.** But **please write it as "it is already the case",
not "it becomes so in stage C"**. Otherwise **it reads as a degradation in stage C**. In reality
**only the same-mount case goes from relying on libfuse to pgfs doing it itself**, and the cross-mount
behaviour **does not change**.

### Finding 3: with O-3 **the logical size after an unlink does not stay in the database**

Removing the inode row leaves nowhere for the `st_size`. What remains is `data.total_size`, but that is
**the occupied bytes** and **for a sparse file it does not agree with the logical size**
(the doc comment on `AddOccupiedBytes` says so).

- While it is open on a single mount, **the ctx's snapshot is enough**, so **there is no practical problem**.
- But **if the daemon dies, the logical size of the orphan data row becomes unknown**. prune only removes
  things, so the real harm is small, but **writing a line in the contract saying "the size after an unlink is
  only in memory" is the honest thing**.

### Finding 4: **a stopped heartbeat is not a dead process**

If prune's condition is only "the heartbeat in `{prefix}mounts` has stopped", it **removes the row of a living
mount whose heartbeat went down because of a database failure**. **Once the row is removed, that mount is no
longer visible as live to prune**, so **the next prune can remove "orphan data that is in use"** in a cascade.

- **Please split the liveness decision** between removing a `{prefix}mounts` row and removing orphan data /
  `.fuse_hidden*`.
- At the very least **take a generous grace period** (tens of times the heartbeat interval), or
  **look at the liveness of the pid on the same host**.
  Do not **damage the evidence the data-removing side relies on** for the sake of cleaning up `{prefix}mounts`,
  whose harm is cosmetic only.

### Finding 5: if the trade-off goes into the contract, **a test to pin that behaviour is needed too**

If the fd dying across mounts is made a contract, **a net is needed to notice if it silently changes**.
Today it is only "that is how it happens to be", with no test. **Adding it in C-2 is natural**
(by then it will be pgfs's own path, so **the behaviour becomes easier to change**).

### The answers to the three unresolved points

| The unresolved point | The Linux side's answer |
|---|---|
| **Is O-3 acceptable** | **Yes.** But finding 1 (the rename replacement) is to be taken into the design |
| **May the cross-mount trade-off go into the contract** | **Yes.** But **write it as "it is already the case"** (finding 2) plus **add a test** (finding 5) |
| **prune's defaults** | **A dry run plus `--apply` is fine.** `--force` may be provided too, but **split the liveness decision per kind of remnant** (finding 4) |

### The FUSE side of C-1 (as-built)

**Wired** ([FileSystem.cs](../../src/fuse/src/FileSystem.cs)). **The behaviour is unchanged** (it only counts).

| The callback | What it calls |
|---|---|
| `Open` / `Create` / `OpenDir` | Build an `OpenFileContext` and **`api.OpenHandle(context)`**, then `Handles.Rent(context)` |
| `Release` / `ReleaseDir` | **`api.CloseHandle(context)`** on the context `Handles.Return(fh)` gave back |

**They do not ride on `Rent` / `Return`** (as per the Core-side policy; it is two lines in the same place but
they mean different things).
**They are not called in `Flush`** - `Flush` comes down for every duplicated fd, so **the count would come back
the moment `exec 9< file` closes its intermediate fd** (measured in the leak regression of (6)).

**4 tests were added to [handles.sh](../../tests/linux/handles.sh) (6 -> 10)**:

| The test | What it watches |
|---|---|
| `test_handles_inodes_counted` | 3 separate files give `inodes` = 3 -> closing gives 0 |
| **`test_handles_inodes_dedupe_same_file`** | **Opening the same file 3 times gives `open` 3 but `inodes` 1** |
| `test_handles_inodes_dup_fd_is_one` | A dup still gives 1. **That closing the first does not decrement** is watched too |
| `test_handles_inodes_readdir_balances` | **Opening a directory's fd gives 1**, and closing gives 0. 20 `ls`es also give 0 |

**The discriminating power was confirmed with two ways of breaking it** (the probes were removed):

- **Removing `OpenHandle` from the 3 places** -> **all 4 failed** (`inodes did not reach 3 (got '0')` and so on).
- **Removing `CloseHandle` from `ReleaseDir`** -> the `readdir` one failed with `got '1'`
  (it is the same directory, so it stops at 1 rather than 20 = consistent with the meaning of a body count).

> ⚠ **The `readdir` case was at first only "20 `ls`es come back to 0"**, but that
> **cannot detect a missed count in `OpenHandle`** (if nothing is counted it is always 0).
> It was changed to **also watch a directory's fd being opened and the count going up**
> (on Linux a directory can be opened O_RDONLY with `open(2)`, so `OpenDir` runs).

**`inodes` appears in the text output too** (at first it was only in the JSON).
The body-count tests read from the **JSON** of `{prefix}mounts.stats`, and
**the text rendering is watched by the single `test_handles_status_text_shows_inodes`** - looking only at the
JSON **would not notice the rendering breaking**. **A snapshot from before C-1 has no `inodes`, and not printing
the item there is correct** (rendering a missing value gives `-1 inodes`). A row with the `inodes` artificially
dropped was injected and **the item disappearing** was confirmed.

> ⚠ **`MOUNT_PID` is looked up from the registry as "the newest row for this mount point".**
> With `pgrep -x mount.pgfs | head -1` it grabs **a daemon running on a different mount point**
> (crossclient's B side, or one that was not cleaned up), and **every later status read is somebody else's row**.
> It was actually hit and **6 cases FAILed mysteriously**. It was confirmed to be green even with another mount
> deliberately left running.

## Stage C on the Linux side - the measurements and the design (the FUSE side)

**Stage C is not the work of adding POSIX semantics to Linux. They already work.**
What stage C does is **move that realization from libfuse's workaround into pgfs itself and remove the side
effects**.

### Measured: libfuse gets by with a rename to `.fuse_hidden`

`fuse_config.hard_remove` is **not set by pgfs** (0 by default).
[FuseMount.Init](../../src/fuse/src/FuseMount.cs) touches only `use_ino`.
With the default of 0, libfuse **substitutes a rename within the same directory for the `unlink` of a file that
is open**.

Measured (two mounts, `pgfs_test`):

```
$ exec 9< f.txt ; rm f.txt
$ ls -a                      # A (the side that unlinked)
.  ..  .fuse_hidden0000000300000001
$ ls -a                      # B (the other mount)
.  ..  .fuse_hidden0000000300000001
$ cat <&9
KEEPME                       # <- readable. The POSIX semantics are satisfied
$ exec 9<&- ; ls -a          # after the close it is gone on both A and B
.  ..
```

### So what is the problem (the three side effects, measured)

| # | The side effect | Measured |
|---|---|---|
| 1 | **It is visible from other mounts** | `.fuse_hidden0000000300000001` appears in B's `ls`. **It is a real file in the database**, so of course it does |
| 2 | **It stays forever if the daemon dies** | After a `kill -9`, `.fuse_hidden0000000400000002` **survived with its contents `ORPHAN`**. There is nowhere for a cleaner to live (libfuse's cleanup is in the memory of the dead process) |
| 3 | **Every unlink becomes a rename plus an unlink at release** | One deletion becomes two namespace operations. With write-back, a re-pointing of the pending entry is folded in too |

**2 is the heaviest.** With every crash, **rubbish nobody can remove keeps accumulating in the database, is
visible to every client and counts towards the usage in `df`**. Today there is no path to clean it up in `mkfs`,
in `mount` or in `pgfsctl` (a `grep -r fuse_hidden` finds only the one declaration in `fuse_config`).

**No name collision happened**: two mounts doing an unlink-while-open in the same directory at once gave
different names, `...00000001` and `...00000002` (the same with notify off). It looks like **it finds the
existing name through the shared database lookup and advances the number**, but **libfuse's mechanism was not
followed up**. Raising `hard_remove` removes the whole path, so it was not pursued.

### The ordering constraint - **`hard_remove = 1` must not be raised before stage C** (measured)

It was raised temporarily to check (the probe was removed):

```
$ exec 9< f.txt ; rm f.txt
$ ls -a                      # no hidden file is created
.  ..
$ cat <&9
cat: -: No such file or directory     # <- not readable
```

**Raising it now breaks open-then-unlink.** Stage B took `Read` / `Write` to the `InodeId`, so once the inode row
is gone, [ResolveHandle](../../src/core/src/Api/Api.Handle.cs) throws a `StaleHandleException`
((2)'s "it stays an error in stage B" working exactly as stated).
**`hard_remove = 1` can only be raised as a set with stage C's lifetime management.**

### The Linux side's design

**What is required of Core (= what the Dokan side builds)**:

| The requirement | Why the Linux side cannot hold it |
|---|---|
| **A per-inode reference count** (++ on open, -- on release) | Unless both operating systems count the same way, the moment of deletion differs between Dokan and FUSE |
| **The `DeletePending` mark** (the state where the name is gone but the body is alive) | It changes the condition under which `TryResolveHandle` says "it is gone", so it is a matter inside `Api` |
| **Removing the body at the final release** (the count is 0 and it is `DeletePending`) | A deletion is a matter of a tx, and it involves the audit rows, the `data_chunk` and the handling of hardlink siblings |

**On the FUSE adapter side (= what we hold)**:

1. Raise `fuse_config.hard_remove = 1` (**after Core satisfies the three above**).
2. Change `Unlink` / `Rmdir` to "remove the name and raise `DeletePending`".
3. Decrement the reference count in `Release`. **`Release` is called only at the last close**, so as confirmed
   in stage A **it is the only release point** (`Flush` is called for every duplicated fd, so it is not
   possible).
4. Split the treatment of a failed resolution. **A handle alive under `DeletePending` succeeds**, and
   **a handle that really is gone gets `-ESTALE`**. Today only the latter exists.

**How the name appears is decided by the OS layer** (as per the division in unresolved point 3). Linux can be
**"the name goes the moment it is unlinked and only the open fd sees the body"** = POSIX as it is.
It **may differ** from Windows's delete-pending (it appears in the enumeration but an open fails while another
handle is held).

### To be decided first (unresolved)

- **What to do about the existing `.fuse_hidden*` remnants.** Even with stage C in, **what leaked in the past
  remains**. Whether the cleanup happens at `mount` startup, whether a subcommand is added to `pgfsctl`, or
  whether a manual SQL statement just goes in the documents.
  **After `hard_remove` is raised nobody creates them**, so a one-off cleanup is enough.
  ⚠ **They must not be removed mechanically** - removing **one another mount currently has open** breaks that
    mount's fd. Either cross-check against the liveness in `{prefix}mounts`, or make it operationally
    **something run only when every mount is down**; one of the two has to be decided.
- **`DeletePending` while write-back is enabled.** How the ledgers (`DirtySet` / `DirtyNamespace`) are folded
  when a pending inode becomes `DeletePending`. It touches the same place as A-1 (the re-pointing to a hardlink
  sibling), so it must be kept consistent with the [metadata-write-back.md](metadata-write-back.md) side.

### Where the regression tests go

**Before entering stage C, tests that pin the present behaviour should be in place** (there is not one today).
It breaks the moment `hard_remove` is raised, so **a net to notice the breakage is needed first**.

- `tests/linux/e2e.sh`: that **an unlink while open can still be read from the fd** (a single mount is enough).
  -> **Landed as `test_open_unlink_read_still_works`**.
  It was confirmed to fail against a `hard_remove = 1` build with **`expected 'STILL_HERE', got ''`**.
  **The counterpart, "the `.fuse_hidden*` is gone after the close", was not made a test** - the cleanup runs not
  at the close but **when the kernel FORGETs the inode**, and the kernel decides when that is
  (measured, there were runs where it went in 1 second and runs where it was still there after 5).
  **Waiting on something with no bound and asserting counts a merely slow run as broken.**
- `tests/linux/crossclient.sh`: that **`.fuse_hidden*` is invisible to another mount** -
  this **only goes green once stage C is in**, so **it goes in the same commit as stage C**.
  Adding it now would mean placing a red test.
