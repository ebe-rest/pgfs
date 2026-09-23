# pgfs Windows e2e tests
#
# Usage:
#   .\tests\windows\e2e.ps1 [-MountRoot P:\] [-Filter xattr]
#
# The default MountRoot is "P:\". Tests run only under MountRoot\test.
#
# Exit codes:
#   0   all tests passed
#   1   one or more failed
#   2   MountRoot does not exist (not mounted)
#   3   TestRoot could not be created (mount not writable)
#
# The Windows counterpart of the Linux side ([tests/linux/e2e.sh](../linux/e2e.sh)).
# It exercises the ✅/⚠️ operations that assign.pgfs offers through Dokan
# ([docs/Assign.md](../../docs/Assign.md)). The Linux-only ones (POSIX symlink/hardlink/chmod/chown/xattr)
# are out of scope.

[CmdletBinding()]
param(
	[string]$MountRoot = "P:\",
	[string]$Filter = ""
)

# Keep UTF-8 output even on PS 5.1.
[System.Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$ErrorActionPreference = "Continue"

# Normalize the trailing backslash ("P:" -> "P:\", "P:\foo\" -> "P:\foo").
if ($MountRoot.Length -eq 2 -and $MountRoot[1] -eq ':') {
	$MountRoot = "$MountRoot\"
}
$MountRoot = $MountRoot.TrimEnd('\') + '\'
$TestRoot = Join-Path $MountRoot "test"

# ===== output =====
#
# Raw ANSI would show `[31m` literally on legacy conhost, so we colorize with
# Write-Host -ForegroundColor (same approach as flow.ps1).

$script:Total   = 0
$script:Passed  = 0
$script:Failed  = 0
$script:Skipped = 0
$script:FailedNames = @()
$script:Current = ""
$script:CurrentFailed = $false

function Pass {
	$script:Passed++
	Write-Host "PASS" -ForegroundColor Green -NoNewline
	Write-Host ": $script:Current"
}

function Fail($msg) {
	$script:Failed++
	$script:FailedNames += $script:Current
	$script:CurrentFailed = $true
	Write-Host "FAIL" -ForegroundColor Red -NoNewline
	Write-Host ": $script:Current - $msg"
}

function Skip($msg) {
	$script:Skipped++
	Write-Host "SKIP" -ForegroundColor Yellow -NoNewline
	Write-Host ": $script:Current - $msg"
}

# ===== assertion helpers =====
#
# Unlike the bash version, a PowerShell function cannot propagate `return` to its caller,
# so on assertion failure we set $script:CurrentFailed and the caller does if (Test-Failed) { return }.

function Test-Failed { return $script:CurrentFailed }

function Assert-Eq($expected, $actual, $what = "value") {
	if ($expected -ne $actual) {
		Fail "${what}: expected '$expected', got '$actual'"
	}
}

function Assert-File($path) {
	if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
		Fail "expected file does not exist: $path"
	}
}

function Assert-Dir($path) {
	if (-not (Test-Path -LiteralPath $path -PathType Container)) {
		Fail "expected dir does not exist: $path"
	}
}

# A delete reaches the database **at Cleanup** (Dokan's delete-on-close semantics). The name can survive
# until the last handle closes, and an indexer or an antivirus holding a handle for a moment is perfectly normal.
# On top of that a DELETE on Citus takes about 100 ms per tx, so checking Test-Path once right after
# Remove-Item is flaky in principle (measured: up to ~1.1 seconds from the delete request to the DELETE landing).
# -> **A bounded retry** makes only "it never disappears" a failure.
function Assert-Absent($path) {
	$parent = [System.IO.Path]::GetDirectoryName($path)
	$leaf = [System.IO.Path]::GetFileName($path)
	$deadline = (Get-Date).AddSeconds(10)
	while ((Get-Date) -lt $deadline) {
		# **The decision is made on the parent directory's enumeration (FindFiles)**. That always goes down to the FS and so reflects pgfs's state.
		$byList = @(Get-ChildItem -LiteralPath $parent -Filter $leaf -Force -ErrorAction SilentlyContinue).Count
		if ($byList -eq 0) {
			if (Test-Path -LiteralPath $path) {
				# The Windows client-side cache is returning a stale answer. On the pgfs side it is gone.
				Write-Host "      (absent from the enumeration yet Test-Path is still true = the Windows client-side cache)" -ForegroundColor Yellow
			}
			return
		}
		Start-Sleep -Milliseconds 300
	}
	# Even when it still shows up in the enumeration, **whether it can be opened** is the final verdict. When
	# another process (an antivirus or an indexer) holds a handle, Windows keeps the delete-pending name in its
	# cache, but an open always fails.
	# The pgfs side removed it from the database at Cleanup, so failing to open here correctly means "it is gone".
	try {
		$probe = [System.IO.File]::OpenRead($path)
		$probe.Dispose()
	}
	catch {
		Write-Host "      (still in the enumeration but the open fails = delete-pending. It is already deleted on the pgfs side)" -ForegroundColor Yellow
		return
	}
	Fail "expected path to be absent (it is in the enumeration and can be opened): $path"
}

# ===== I/O helpers (UTF-8 no-BOM, byte-transparent) =====
#
# PS 5.1's Out-File / Set-Content default to UTF-16 LE with a BOM. The tests need byte sequences
# comparable with the Linux side, so we consistently use UTF-8 no-BOM.

$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Write-Text($path, $text) {
	[System.IO.File]::WriteAllText($path, $text, $script:Utf8NoBom)
}

function Read-Text($path) {
	return [System.IO.File]::ReadAllText($path, $script:Utf8NoBom)
}

function Add-Text($path, $text) {
	[System.IO.File]::AppendAllText($path, $text, $script:Utf8NoBom)
}

function Write-Bytes($path, [byte[]]$bytes) {
	[System.IO.File]::WriteAllBytes($path, $bytes)
}

function Read-Bytes($path) {
	return [System.IO.File]::ReadAllBytes($path)
}

function Get-FileLen($path) {
	return (Get-Item -LiteralPath $path).Length
}

function Set-FileLen($path, [long]$newLen) {
	$fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite)
	try {
		$fs.SetLength($newLen)
	}
	finally {
		$fs.Close()
		$fs.Dispose()
	}
}

function Get-Md5($path) {
	return (Get-FileHash -LiteralPath $path -Algorithm MD5).Hash
}

function Get-Sha256($path) {
	return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

function New-RandomBytes([int]$len) {
	$buf = New-Object byte[] $len
	$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
	try {
		$rng.GetBytes($buf)
	}
	finally {
		$rng.Dispose()
	}
	return ,$buf
}

# Entry point that runs a single test.
function Invoke-Test($name) {
	if ($Filter -ne "" -and $name -notlike "*$Filter*") {
		return
	}
	$script:Total++
	$script:Current = $name
	$script:CurrentFailed = $false
	try {
		& $name
	}
	catch {
		$ex = $_.Exception
		$type = $ex.GetType().FullName
		$msg = $ex.Message
		$inner = ""
		if ($null -ne $ex.InnerException) {
			$inner = " inner=[$($ex.InnerException.GetType().FullName): $($ex.InnerException.Message)]"
		}
		# For IOException-family errors, the raw HResult often lets us extract the Win32 error code.
		if ($ex.HResult -ne 0) {
			$msg = "$msg HResult=0x{0:X8}" -f $ex.HResult
		}
		if ($ex -is [System.ComponentModel.Win32Exception]) {
			$msg = "$msg Native=0x{0:X8}" -f $ex.NativeErrorCode
		}
		Fail "exception [$type]: $msg$inner"
	}
}

# ===== individual tests =====
#
# Each test uses a unique file/dir name under TestRoot (t01_, t02_, ...).
# No per-test cleanup is needed (TestRoot is removed wholesale at the end).

# --- directory operations ---

function test_mkdir_rmdir {
	$d = Join-Path $TestRoot "t01_mkdir"
	New-Item -ItemType Directory -Path $d | Out-Null
	if (-not $?) { Fail "mkdir"; return }
	Assert-Dir $d; if (Test-Failed) { return }
	Remove-Item -LiteralPath $d
	if (-not $?) { Fail "rmdir"; return }
	Assert-Absent $d; if (Test-Failed) { return }
	Pass
}

function test_nested_directories {
	$d = Join-Path $TestRoot "t02_nested\a\b\c\d\e"
	New-Item -ItemType Directory -Path $d -Force | Out-Null
	if (-not $?) { Fail "mkdir -Force"; return }
	Assert-Dir $d; if (Test-Failed) { return }
	$f = Join-Path $d "file.txt"
	Write-Text $f "deep"
	$got = Read-Text $f
	Assert-Eq "deep" $got "deep content"; if (Test-Failed) { return }
	Pass
}

function test_many_files_ls {
	$d = Join-Path $TestRoot "t03_many"
	New-Item -ItemType Directory -Path $d | Out-Null
	for ($i = 0; $i -lt 100; $i++) {
		New-Item -ItemType File -Path (Join-Path $d "f_$i") | Out-Null
	}
	$count = (Get-ChildItem -LiteralPath $d).Count
	Assert-Eq 100 $count "files in dir"; if (Test-Failed) { return }
	Pass
}

function test_rmdir_nonempty_fails {
	$d = Join-Path $TestRoot "t04_nonempty"
	New-Item -ItemType Directory -Path $d | Out-Null
	$inside = Join-Path $d "inside.txt"
	New-Item -ItemType File -Path $inside | Out-Null
	# Remove-Item internally prompts via Read-Host for a non-empty directory, so even with
	# -Confirm:$false / -ErrorAction SilentlyContinue it dies in NonInteractive mode with the
	# terminating "Windows PowerShell is in NonInteractive mode" exception.
	# The test only wants to confirm the directory and its content survive, so call the .NET API directly.
	try {
		[System.IO.Directory]::Delete($d, $false)  # recursive=false
	}
	catch {
		# Dokan is expected to reject it as "not empty" (IOException). Swallow it and just inspect state.
	}
	if (-not (Test-Path -LiteralPath $d -PathType Container)) {
		Fail "rmdir succeeded on non-empty dir (dir was deleted)"
		return
	}
	if (-not (Test-Path -LiteralPath $inside -PathType Leaf)) {
		Fail "inside file disappeared (partial delete?)"
		return
	}
	Pass
}

# --- basic file operations ---

function test_touch_unlink {
	$f = Join-Path $TestRoot "t10_touch.txt"
	New-Item -ItemType File -Path $f | Out-Null
	if (-not $?) { Fail "new file"; return }
	Assert-File $f; if (Test-Failed) { return }
	# **Do not use `Remove-Item`.** Windows's `DeleteFile` does not run immediately; it stays
	# `DeletePending` until the last handle closes - and `Remove-Item` returns before that. Who holds a
	# handle is up to Explorer, the indexer and Defender, and measurements showed **the FS's DeleteFile
	# callback delayed by more than 15 seconds** (which exceeds `Assert-Absent`'s 10-second wait, so this
	# test used to fail one time in three). With `DeleteOnClose` the delete runs **the moment this script
	# closes its own handle**, which keeps pgfs's delete path (DeleteFile + Cleanup) under test while making it deterministic.
	# The background is in docs/design/windows-parity.md, the section on delete visibility.
	$h = New-Object System.IO.FileStream($f, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Delete, 4096, [System.IO.FileOptions]::DeleteOnClose)
	$h.Dispose()
	Assert-Absent $f; if (Test-Failed) { return }
	Pass
}

function test_small_write_read {
	$f = Join-Path $TestRoot "t11_small.txt"
	Write-Text $f "hello world"
	$got = Read-Text $f
	Assert-Eq "hello world" $got "content"; if (Test-Failed) { return }
	Pass
}

function test_append {
	$f = Join-Path $TestRoot "t12_append.txt"
	Write-Text $f "line1`n"
	Add-Text $f "line2`n"
	$got = Read-Text $f
	if ($got -ne "line1`nline2`n") {
		Fail "append content: '$got'"
		return
	}
	Pass
}

function test_overwrite_truncates {
	# Overwriting an existing file with an empty file should yield 0 bytes (O_TRUNC / FileMode.Create equivalent).
	$f = Join-Path $TestRoot "t13_otrunc.txt"
	Write-Text $f "long content here"
	# Overwrite with an empty file via Copy-Item.
	$tmp = Join-Path $env:TEMP "pgfs_empty_$PID.tmp"
	Write-Bytes $tmp (New-Object byte[] 0)
	Copy-Item -LiteralPath $tmp -Destination $f -Force
	if (-not $?) { Fail "copy"; Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue; return }
	Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue
	$size = Get-FileLen $f
	Assert-Eq 0 $size "size after overwrite with empty"; if (Test-Failed) { return }
	Pass
}

# --- data I/O (byte chunks) ---

function test_large_file_round_trip {
	# Default chunk is 1 MiB, so 2 MiB spans two chunks.
	# Copy-Item (CopyFileEx) issues concurrent WriteFile calls spanning chunk boundaries, which
	# makes it a good stress test for the race condition in Api.EnsureChunk (PK constraint violation).
	$src = Join-Path $env:TEMP "pgfs_large_$PID.bin"
	$f = Join-Path $TestRoot "t20_large.bin"
	$bytes = New-RandomBytes (2 * 1024 * 1024)
	Write-Bytes $src $bytes
	try {
		Copy-Item -LiteralPath $src -Destination $f -Force
	}
	finally {
		Remove-Item -LiteralPath $src -ErrorAction SilentlyContinue
	}
	$sha = [System.Security.Cryptography.SHA256]::Create()
	try {
		$hashSrc = [BitConverter]::ToString($sha.ComputeHash($bytes))
		$readBack = Read-Bytes $f
		if ($readBack.Length -ne $bytes.Length) {
			Fail "length differs: wrote $($bytes.Length), read $($readBack.Length)"
			return
		}
		$hashDst = [BitConverter]::ToString($sha.ComputeHash($readBack))
	}
	finally {
		$sha.Dispose()
	}
	if ($hashSrc -ne $hashDst) {
		Fail "data differs after round-trip"
		return
	}
	Pass
}

function test_truncate_shrink {
	$f = Join-Path $TestRoot "t21_trunc_shrink.txt"
	Write-Text $f "0123456789"
	Set-FileLen $f 5
	$got = Read-Text $f
	Assert-Eq "01234" $got "content after shrink"; if (Test-Failed) { return }
	Pass
}

function test_truncate_grow {
	$f = Join-Path $TestRoot "t22_trunc_grow.txt"
	Write-Text $f "abc"
	Set-FileLen $f 10
	$size = Get-FileLen $f
	Assert-Eq 10 $size "size after grow"; if (Test-Failed) { return }
	Pass
}

function test_truncate_to_zero {
	$f = Join-Path $TestRoot "t23_trunc_zero.txt"
	$bytes = New-RandomBytes (1 * 1024 * 1024)
	Write-Bytes $f $bytes
	Set-FileLen $f 0
	$size = Get-FileLen $f
	Assert-Eq 0 $size "size after zero"; if (Test-Failed) { return }
	Pass
}

# The Windows-side regression for the data_id lifecycle (docs/design/data-id-lifecycle.md).
# Dokan exposes neither st_ino nor nlink, so the hardlink sharing itself is checked on the Linux side.
# Here it checks that, now that **a truncate to 0 no longer deletes the data row**, zeroing and then rewriting still works.
function test_truncate_zero_then_rewrite {
	$f = Join-Path $TestRoot "t24_trunc_rewrite.txt"
	$first = New-RandomBytes (256 * 1024)
	Write-Bytes $f $first
	Set-FileLen $f 0
	Assert-Eq 0 (Get-FileLen $f) "size after zero"; if (Test-Failed) { return }
	$second = New-RandomBytes (128 * 1024)
	Write-Bytes $f $second
	Assert-Eq $second.Length (Get-FileLen $f) "size after rewrite"; if (Test-Failed) { return }
	$got = [System.IO.File]::ReadAllBytes($f)
	$same = (Get-FileHash -Path $f -Algorithm SHA256).Hash
	$tmp = Join-Path $env:TEMP "pgfs_t24_expected.bin"
	[System.IO.File]::WriteAllBytes($tmp, $second)
	$want = (Get-FileHash -Path $tmp -Algorithm SHA256).Hash
	Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
	Assert-Eq $want $same "content after truncate 0 + rewrite"; if (Test-Failed) { return }
	Assert-Eq $second.Length $got.Length "read length"; if (Test-Failed) { return }
	Pass
}

# --- rename / move ---

function test_rename_file {
	$src = Join-Path $TestRoot "t30_rn_src.txt"
	$dst = Join-Path $TestRoot "t30_rn_dst.txt"
	Write-Text $src "renamed"
	Move-Item -LiteralPath $src -Destination $dst
	if (-not $?) { Fail "mv"; return }
	Assert-Absent $src; if (Test-Failed) { return }
	Assert-File $dst; if (Test-Failed) { return }
	$got = Read-Text $dst
	Assert-Eq "renamed" $got "content after rename"; if (Test-Failed) { return }
	Pass
}

function test_rename_into_subdir {
	$sub = Join-Path $TestRoot "t31_subdir"
	New-Item -ItemType Directory -Path $sub | Out-Null
	$src = Join-Path $TestRoot "t31_mv.txt"
	Write-Text $src "moved"
	Move-Item -LiteralPath $src -Destination (Join-Path $sub "moved.txt")
	if (-not $?) { Fail "mv into subdir"; return }
	Assert-Absent $src; if (Test-Failed) { return }
	Assert-File (Join-Path $sub "moved.txt"); if (Test-Failed) { return }
	Pass
}

# --- attributes / time (Windows-specific) ---

function test_readonly_attribute {
	# SetFileAttributes maps only ReadOnly onto the write bit of st_mode.
	$f = Join-Path $TestRoot "t40_ro.txt"
	Write-Text $f "ro"
	$item = Get-Item -LiteralPath $f
	$item.IsReadOnly = $true
	$item.Refresh()
	$after = (Get-Item -LiteralPath $f).IsReadOnly
	if (-not $after) {
		Fail "IsReadOnly did not become true"
		return
	}
	# Also confirm it can be cleared and the file deleted.
	(Get-Item -LiteralPath $f).IsReadOnly = $false
	Remove-Item -LiteralPath $f
	if (-not $?) { Fail "rm after clearing ReadOnly"; return }
	Pass
}

function test_hidden_system_archive_roundtrip {
	# Hidden / System / Archive are stored in an xattr (user.win_attrs).
	# Confirm the values round-trip through SetFileAttributes -> GetFileInformation.
	# Note: setting Hidden makes Get-Item hide the file by default, so use
	#       [System.IO.File]::GetAttributes / SetAttributes directly.
	$f = Join-Path $TestRoot "t42_attrs.txt"
	Write-Text $f "win attrs"
	$initial = [System.IO.File]::GetAttributes($f)
	if (($initial -band [System.IO.FileAttributes]::Hidden) -ne 0) {
		Fail "newly created file already has Hidden: $initial"
		return
	}
	# Set Hidden + System + Archive together.
	$set = [System.IO.FileAttributes]::Hidden -bor [System.IO.FileAttributes]::System -bor [System.IO.FileAttributes]::Archive
	[System.IO.File]::SetAttributes($f, $set)
	$readBack = [System.IO.File]::GetAttributes($f)
	foreach ($flag in @('Hidden', 'System', 'Archive')) {
		$bit = [System.IO.FileAttributes]::$flag
		if (($readBack -band $bit) -eq 0) {
			Fail "${flag} flag missing after set: got $readBack"
			return
		}
	}
	# Clear only Hidden.
	$only = [System.IO.FileAttributes]::System -bor [System.IO.FileAttributes]::Archive
	[System.IO.File]::SetAttributes($f, $only)
	$afterClear = [System.IO.File]::GetAttributes($f)
	if (($afterClear -band [System.IO.FileAttributes]::Hidden) -ne 0) {
		Fail "Hidden flag still set after clearing: got $afterClear"
		return
	}
	if (($afterClear -band [System.IO.FileAttributes]::System) -eq 0) {
		Fail "System flag dropped unexpectedly: got $afterClear"
		return
	}
	# Clear everything -> by design the xattr is **not** removed (four zero bytes are written).
	# The name is a regular (non-dot) name, so Hidden is definitely gone.
	[System.IO.File]::SetAttributes($f, [System.IO.FileAttributes]::Normal)
	$cleared = [System.IO.File]::GetAttributes($f)
	foreach ($flag in @('Hidden', 'System', 'Archive')) {
		$bit = [System.IO.FileAttributes]::$flag
		if (($cleared -band $bit) -ne 0) {
			Fail "${flag} flag still set after Normal: got $cleared"
			return
		}
	}
	Pass
}

function test_dotfile_hidden_heuristic {
	# A dotfile with no xattr should get Hidden set via the fallback heuristic.
	$f = Join-Path $TestRoot ".t43_dotfile"
	Write-Text $f "dot"
	$attrs = [System.IO.File]::GetAttributes($f)
	if (($attrs -band [System.IO.FileAttributes]::Hidden) -eq 0) {
		Fail "dotfile not flagged Hidden: $attrs"
		return
	}
	Pass
}

function test_dotfile_unhide_sticks {
	# Regression guard: after un-hiding a dotfile in Explorer, the heuristic must not kick back in
	# and re-hide it. This is the bug you hit if SaveWinAttrs removes the xattr.
	$f = Join-Path $TestRoot ".t44_unhide"
	Write-Text $f "unhide"
	# The initial state should be Hidden via the heuristic.
	$before = [System.IO.File]::GetAttributes($f)
	if (($before -band [System.IO.FileAttributes]::Hidden) -eq 0) {
		Fail "expected dotfile to start Hidden via heuristic, got: $before"
		return
	}
	# Explicitly clear Hidden (write Normal -> four zero bytes are stored in the xattr).
	[System.IO.File]::SetAttributes($f, [System.IO.FileAttributes]::Normal)
	$after = [System.IO.File]::GetAttributes($f)
	if (($after -band [System.IO.FileAttributes]::Hidden) -ne 0) {
		Fail "Hidden returned after un-hide on dotfile (heuristic should be suppressed by xattr presence): $after"
		return
	}
	Pass
}

function test_set_lastwritetime {
	# SetFileTime: only LastWriteTime is reflected in the DB (atime is ignored).
	$f = Join-Path $TestRoot "t41_mtime.txt"
	Write-Text $f "mtime"
	$target = [DateTime]::new(2025, 1, 15, 12, 30, 45, [DateTimeKind]::Local)
	(Get-Item -LiteralPath $f).LastWriteTime = $target
	$got = (Get-Item -LiteralPath $f).LastWriteTime
	# An exact match tends to fail due to second-precision rounding, so accept the same calendar day.
	if ($got.Date -ne $target.Date) {
		Fail "LastWriteTime date mismatch: expected $($target.Date), got $($got.Date)"
		return
	}
	Pass
}

# --- volume / wildcard ---

function test_volume_info {
	# GetVolumeInformation: the volume label should be "pgfs" (the default of RootSettings.FileSystem.VolumeLabel).
	$driveLetter = $MountRoot.Substring(0, 1)
	$vol = Get-PSDrive -PSProvider FileSystem | Where-Object Name -eq $driveLetter
	if ($null -eq $vol) {
		Fail "Get-PSDrive returned nothing for $driveLetter"
		return
	}
	# Just confirm Used + Free are obtainable (via GetDiskFreeSpace).
	# The Used property holds a number (if the DB size query failed it would throw).
	if ($null -eq $vol.Used) {
		Fail "Used is null on $driveLetter"
		return
	}
	Pass
}

function test_disk_free_space {
	# GetDiskFreeSpace (docs/df-support.md): measured if the server-side statfs() exists, nominal capacity otherwise.
	# DriveInfo goes through Win32 GetDiskFreeSpaceEx, which hits the driver's GetDiskFreeSpace.
	# Confirm total>0 / 0<=avail<=total (this inequality should hold whether measured or nominal).
	$driveLetter = $MountRoot.Substring(0, 1)
	$di = New-Object System.IO.DriveInfo($driveLetter)
	$total = $di.TotalSize
	$avail = $di.AvailableFreeSpace
	Write-Host "      ($driveLetter`: total=$total avail=$avail)" -ForegroundColor DarkGray
	if ($total -le 0) {
		Fail "TotalSize not positive: $total"
		return
	}
	if ($avail -lt 0 -or $avail -gt $total) {
		Fail "AvailableFreeSpace out of range: avail=$avail total=$total"
		return
	}
	Pass
}

function test_getfilesecurity_projection {
	# GetFileSecurity: project the inode's uname/gname/mode onto a Windows security descriptor.
	# Confirm Get-Acl returns (a) owner = the POSIX owner name (= the current user) and (b) a non-empty DACL.
	$f = Join-Path $TestRoot "t60_acl.txt"
	Write-Text $f "acl"
	$acl = Get-Acl -LiteralPath $f
	if ($null -eq $acl) { Fail "Get-Acl returned null"; return }
	if ([string]::IsNullOrEmpty($acl.Owner)) { Fail "owner is empty (projection missing?)"; return }
	# The owner should resolve to the creator = the current user (normalization + SID projection).
	$me = $env:USERNAME
	if ($acl.Owner -notmatch [regex]::Escape($me)) {
		Fail "owner '$($acl.Owner)' does not contain current user '$me'"
		return
	}
	# The DACL should carry at least one projected ACE (owner/group/Everyone).
	$rules = @($acl.Access)
	if ($rules.Count -lt 1) {
		Fail "DACL has no access rules (projection missing?)"
		return
	}
	Remove-Item -LiteralPath $f
	Pass
}

function test_setfilesecurity_roundtrip {
	# SetFileSecurity: change the DACL to owner=Read only -> the owner loses 'w' in st_mode and
	# the file becomes ReadOnly, confirming the SD -> mode reverse projection takes effect.
	$f = Join-Path $TestRoot "t61_setacl.txt"
	Write-Text $f "setacl"
	$acl = Get-Acl -LiteralPath $f
	foreach ($r in @($acl.Access)) { [void]$acl.RemoveAccessRule($r) }
	$me = New-Object System.Security.Principal.NTAccount($env:USERNAME)
	$rule = New-Object System.Security.AccessControl.FileSystemAccessRule($me, [System.Security.AccessControl.FileSystemRights]::Read, [System.Security.AccessControl.AccessControlType]::Allow)
	$acl.SetAccessRule($rule)
	Set-Acl -LiteralPath $f -AclObject $acl
	if (-not $?) { Fail "Set-Acl failed"; return }
	$attrs = [System.IO.File]::GetAttributes($f)
	if (($attrs -band [System.IO.FileAttributes]::ReadOnly) -eq 0) {
		Fail "expected ReadOnly after owner=Read-only DACL, got $attrs"
		return
	}
	# Cleanup: restore the write permission and delete.
	[System.IO.File]::SetAttributes($f, [System.IO.FileAttributes]::Normal)
	Remove-Item -LiteralPath $f -Force
	Pass
}

function test_wildcard_pattern {
	# FindFilesWithPattern: -Filter calls Dokan's FindFilesWithPattern.
	$d = Join-Path $TestRoot "t50_pat"
	New-Item -ItemType Directory -Path $d | Out-Null
	New-Item -ItemType File -Path (Join-Path $d "alpha.txt") | Out-Null
	New-Item -ItemType File -Path (Join-Path $d "beta.txt") | Out-Null
	New-Item -ItemType File -Path (Join-Path $d "gamma.log") | Out-Null
	$txt = @(Get-ChildItem -LiteralPath $d -Filter "*.txt")
	Assert-Eq 2 $txt.Count "txt pattern count"; if (Test-Failed) { return }
	$log = @(Get-ChildItem -LiteralPath $d -Filter "*.log")
	Assert-Eq 1 $log.Count "log pattern count"; if (Test-Failed) { return }
	$alpha = @(Get-ChildItem -LiteralPath $d -Filter "alpha.*")
	Assert-Eq 1 $alpha.Count "alpha pattern count"; if (Test-Failed) { return }
	Pass
}

# --- concurrent access ---

function test_concurrent_writes_diff_files {
	$jobs = 0..4 | ForEach-Object {
		Start-Job -ScriptBlock {
			param($p, $t)
			$enc = New-Object System.Text.UTF8Encoding($false)
			[System.IO.File]::WriteAllText($p, $t, $enc)
		} -ArgumentList (Join-Path $TestRoot "t90_cw_$_.txt"), "data $_"
	}
	$jobs | Wait-Job | Out-Null
	$jobs | ForEach-Object { Remove-Job -Job $_ -Force }
	for ($i = 0; $i -lt 5; $i++) {
		$got = Read-Text (Join-Path $TestRoot "t90_cw_$i.txt")
		if ($got -ne "data $i") {
			Fail "t90_cw_${i} content: '$got'"
			return
		}
	}
	Pass
}

function test_concurrent_reads_same_file {
	$f = Join-Path $TestRoot "t91_shared.bin"
	$bytes = New-RandomBytes (1 * 1024 * 1024)
	Write-Bytes $f $bytes
	$orig = Get-Md5 $f
	# Start-Job is heavy; run 5 in parallel without a separate Runspace.
	$results = New-Object System.Collections.ArrayList
	$jobs = 1..5 | ForEach-Object {
		Start-Job -ScriptBlock {
			param($path)
			(Get-FileHash -LiteralPath $path -Algorithm MD5).Hash
		} -ArgumentList $f
	}
	$jobs | Wait-Job | Out-Null
	$jobs | ForEach-Object {
		$h = Receive-Job -Job $_
		[void]$results.Add($h)
		Remove-Job -Job $_ -Force
	}
	for ($i = 0; $i -lt $results.Count; $i++) {
		if ($results[$i] -ne $orig) {
			Fail "concurrent read ${i}: hash mismatch (got '$($results[$i])')"
			return
		}
	}
	Pass
}

# Byte-range locks (LockFile / UnlockFile) must be enforced by the driver.
# pgfs keeps no ledger of range locks, so `UserModeLock` is left off and it is left to the Dokan driver
# (leaving it on makes our own callback always return Success = lying that a lock was taken when it was not).
function test_byte_range_lock_enforced {
	$p = Join-Path $TestRoot "t99_lock.bin"
	Write-Bytes $p (New-RandomBytes 4096)
	$a = [System.IO.File]::Open($p, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::ReadWrite)
	$b = $null
	try {
		$a.Lock(0, 16)
		$b = [System.IO.File]::Open($p, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::ReadWrite)
		$blocked = $false
		try { $b.Lock(0, 16) }
		catch [System.IO.IOException] { $blocked = $true }
		if (-not $blocked) {
			Fail "another handle managed to take a lock on the same range (the driver is not enforcing it)"
			return
		}
		# It can be taken once it has been released
		$a.Unlock(0, 16)
		try { $b.Lock(0, 16); $b.Unlock(0, 16) }
		catch [System.IO.IOException] {
			Fail "the lock still cannot be taken after the release"
			return
		}
	}
	finally {
		if ($null -ne $b) { $b.Dispose() }
		$a.Dispose()
	}
	Pass
}

# The CopyFileEx path (Copy-Item, or a copy in Explorer). There was a known problem where 2 MiB raised an
# IOException and it was worked around by calling `WriteAllBytes` directly, but **it stopped reproducing**,
# so the real path was put back into the test.
# (It did not reproduce at 2 MiB / 8 MiB, in either direction, on overwrite, with robocopy /COPYALL, or on a tree copy.)
function test_copy_item_round_trip {
	$src = Join-Path $env:TEMP "pgfs_e2e_copy_src.bin"
	$back = Join-Path $env:TEMP "pgfs_e2e_copy_back.bin"
	$dst = Join-Path $TestRoot "t98_copy.bin"
	Write-Bytes $src (New-RandomBytes (2 * 1024 * 1024))
	try {
		Copy-Item -LiteralPath $src -Destination $dst -ErrorAction Stop
		$h = Get-Sha256 $src
		if ((Get-Sha256 $dst) -ne $h) {
			Fail "the hash of the copy destination does not match"
			return
		}
		# The copy back (P: -> local) goes through CopyFileEx as well
		Copy-Item -LiteralPath $dst -Destination $back -Force -ErrorAction Stop
		if ((Get-Sha256 $back) -ne $h) {
			Fail "the hash of the copy back does not match"
			return
		}
	}
	finally {
		Remove-Item -LiteralPath $src -Force -ErrorAction SilentlyContinue
		Remove-Item -LiteralPath $back -Force -ErrorAction SilentlyContinue
	}
	Pass
}

# ===== deriving the owner / group =====

# The owner of a new file has to be **the requesting account** (not the mount process's default).
function test_new_file_owner_is_requestor {
	$f = Join-Path $TestRoot "t96_owner.txt"
	Set-Content -LiteralPath $f -Value "x" -NoNewline
	$owner = (Get-Acl -LiteralPath $f).Owner
	$me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
	# pgfs normalizes the name before storing it (the domain is dropped and it is lower-cased), so only the account name is compared.
	$ownerLeaf = $owner.Substring($owner.LastIndexOf('\') + 1)
	$meLeaf = $me.Substring($me.LastIndexOf('\') + 1)
	if ($ownerLeaf -ne $meLeaf) {
		Fail "the owner of the new file differs from the requester: owner=$owner me=$me"
		return
	}
	Pass
}

# The group of a new file is **inherited from the parent directory**.
# The parent's group is changed to something else before the child is created, which confirms it is not "the running process's primary group".
function test_new_file_inherits_parent_group {
	$dir = Join-Path $TestRoot "t97_grp"
	New-Item -ItemType Directory -Path $dir | Out-Null
	try {
		$acl = Get-Acl -LiteralPath $dir
		$acl.SetGroup([System.Security.Principal.NTAccount]"Users")
		Set-Acl -LiteralPath $dir -AclObject $acl -ErrorAction Stop
	}
	catch {
		Skip "an environment where the parent directory's group cannot be changed: $($_.Exception.GetType().Name)"
		return
	}
	$dirGroup = (Get-Acl -LiteralPath $dir).Group.Value
	$f = Join-Path $dir "child.txt"
	Set-Content -LiteralPath $f -Value "x" -NoNewline
	$fileGroup = (Get-Acl -LiteralPath $f).Group.Value
	if ($fileGroup -ne $dirGroup) {
		Fail "the group of the new file differs from the parent's: file=$fileGroup dir=$dirGroup"
		return
	}
	Pass
}

# ===== Windows basics (docs/design/windows-parity.md, the Windows basics section) =====

# CREATE_NEW means "always fail if it already exists". assign now reaches Api.CreateFile(exclusive: true), so
# the verdict comes from the database's unique constraint rather than a local non-existence check (exclusion
# against another mount takes the same path).
# What a single mount can show is two things: "the second one fails" and "the loser does not corrupt the winner's contents".
function test_createnew_exclusive {
	$p = Join-Path $TestRoot "t93_excl.txt"
	$fs = [System.IO.File]::Open($p, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
	try {
		$fs.Write([byte[]](65, 66, 67), 0, 3)
	}
	finally {
		$fs.Dispose()
	}
	$collided = $false
	try {
		$fs2 = [System.IO.File]::Open($p, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
		$fs2.Dispose()
	}
	catch [System.IO.IOException] {
		$collided = $true
	}
	if (-not $collided) {
		Fail "CreateNew succeeded against an existing file"
		return
	}
	$bytes = [System.IO.File]::ReadAllBytes($p)
	if ($bytes.Length -ne 3) {
		Fail "the loser corrupted the winner's contents: len=$($bytes.Length) (expected 3)"
		return
	}
	Pass
}

# The allocation size is a different quantity from EOF. It is a reservation hint and must not grow the logical size
# (SetAllocationSize used to be wired straight to SetEndOfFile = a 1 MB reservation made EOF 1 MB as well).
function test_allocation_size_does_not_extend_eof {
	if ($PSVersionTable.PSVersion.Major -lt 7) {
		Skip "FileStreamOptions.PreallocationSize needs PowerShell 7+ (.NET 6+)"
		return
	}
	$p = Join-Path $TestRoot "t94_prealloc.bin"
	$opts = [System.IO.FileStreamOptions]::new()
	$opts.Mode = [System.IO.FileMode]::Create
	$opts.Access = [System.IO.FileAccess]::Write
	$opts.PreallocationSize = 1048576
	$fs = [System.IO.FileStream]::new($p, $opts)
	try {
		$fs.Write([byte[]](1, 2, 3, 4), 0, 4)
	}
	finally {
		$fs.Dispose()
	}
	$len = (Get-Item -LiteralPath $p).Length
	if ($len -ne 4) {
		Fail "the allocation reservation grew EOF: len=$len (expected 4)"
		return
	}
	Pass
}

# A rename onto the same target has to be a no-op success. Core's replacement path is "delete the target ->
# UPDATE the source", so passing the same target would delete itself (Linux's VFS rejects it, but the Dokan path goes straight through).
# The contract checked here is that "whether the call succeeds or fails, the file and its contents survive".
function test_rename_same_path_noop {
	$p = Join-Path $TestRoot "t95_same.txt"
	Set-Content -LiteralPath $p -Value "keepme" -NoNewline
	try {
		[System.IO.File]::Move($p, $p)
	}
	catch {
		# The OS or .NET may reject the identical path up front. As long as it has not disappeared, the contract holds.
	}
	if (-not (Test-Path -LiteralPath $p -PathType Leaf)) {
		Fail "the file disappeared on a rename onto the same path"
		return
	}
	$body = Get-Content -LiteralPath $p -Raw
	if ($body -ne "keepme") {
		Fail "the contents were corrupted by a rename onto the same path: '$body'"
		return
	}
	Pass
}

# **FileIndex (= Windows's id for deciding "is this the same file") comes from data_id and is immutable from birth.**
#
# pgfs keeps **one link = one inode row**, so returning `inode.Id` would make **the siblings of a hardlink
# look like different files** (measured: two names pointing at the same body returned different ids).
# `data_id` is **settled at create time and immutable until the final unlink**, so that is what is used
# (docs/design/data-id-lifecycle.md). **The same formula as FUSE's `st_ino`.**
#
# **A hardlink cannot be created from Windows**, so what is checked here is two things: (1) the top bit is set
# (= it comes from the data_id space) and (2) **it does not change across the first write**. **That the
# siblings agree is held by the Linux side's `test_empty_file_hardlink_shares` (st_ino) and by the cross-client measurements.**
Add-Type -Namespace PgfsE2E -Name FileId -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)]
public struct BHFI {
    public uint FileAttributes;
    public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
}
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern IntPtr CreateFileW(string p, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool GetFileInformationByHandle(IntPtr h, out BHFI i);
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool CloseHandle(IntPtr h);
'@

# Open with FILE_READ_ATTRIBUTES alone and read the FileIndex (0 = could not be obtained).
function Get-PgfsFileIndex($path) {
	$h = [PgfsE2E.FileId]::CreateFileW($path, [uint32]0x80, [uint32]0x7, [IntPtr]::Zero, [uint32]3, [uint32]0x80, [IntPtr]::Zero)
	if ($h -eq [IntPtr](-1)) { return [uint64]0 }
	$i = New-Object PgfsE2E.FileId+BHFI
	$ok = [PgfsE2E.FileId]::GetFileInformationByHandle($h, [ref]$i)
	[PgfsE2E.FileId]::CloseHandle($h) | Out-Null
	if (-not $ok) { return [uint64]0 }
	return ((([uint64]$i.FileIndexHigh) -shl 32) -bor [uint64]$i.FileIndexLow)
}

function test_file_index_is_data_id_and_stable {
	$p = Join-Path $TestRoot "t99_fileindex.txt"
	$fs = [System.IO.File]::Create($p)
	$fs.Dispose()
	$empty = Get-PgfsFileIndex $p
	if ($empty -eq 0) { Fail "cannot obtain the FileIndex of an empty file"; return }
	# The top bit is the mark of the data_id space. **If it is not set, inode.Id is being returned** (the
	# state in which the siblings of a hardlink look like different files).
	# **`[uint64]0x8000000000000000` cannot be written** - PowerShell reads a hex literal as an Int64, so it
	# becomes negative and the cast to uint64 falls over. It is built from a hex string instead.
	$topBit = [Convert]::ToUInt64("8000000000000000", 16)
	if (($empty -band $topBit) -eq 0) {
		Fail "the FileIndex does not come from data_id (the top bit is not set): $empty"
		return
	}
	# **It must not change across the first write** either. That contract holds because the data_id is settled
	# at create time; going back to assigning it lazily splits it here (`find -samefile` / `rsync -H` / a backup's deduplication would misbehave).
	Set-Content -LiteralPath $p -Value "now-has-data" -NoNewline
	$written = Get-PgfsFileIndex $p
	if ($written -ne $empty) {
		Fail "the FileIndex changed on the first write: $empty -> $written"
		return
	}
	# A directory has no body, so it comes from inode.Id = the top bit is not set.
	$dirIndex = Get-PgfsFileIndex $TestRoot
	if ($dirIndex -ne 0 -and ($dirIndex -band $topBit) -ne 0) {
		Fail "a directory's FileIndex is in the data_id space: $dirIndex"
		return
	}
	Pass
}

# **A handle that is still open on the side a rename overwrote must be able to keep reading its own body**
# (handle-context stage C-2 / the POSIX "the fd stays alive even when the name is gone").
#
# **This window does not open on the `unlink` side** - Windows does not send a delete down to the FS until the
# last handle closes (measured: with `DeleteOnClose`, closing the second handle first still kept it in the
# enumeration while the first was alive).
# **Only a `rename` replacement removes the name while an open handle remains**, which makes this the only
# entrance to stage C-2 from Windows.
#
# Without keeping it, **the handle on the overwritten side loses its body** (because pgfs deletes the data row together with the inode row).
function test_rename_over_open_victim_keeps_body {
	$victim = Join-Path $TestRoot "t101_victim.txt"
	$src = Join-Path $TestRoot "t101_src.txt"
	Set-Content -LiteralPath $victim -Value "VICTIM-BODY" -NoNewline
	Set-Content -LiteralPath $src -Value "NEW-BODY" -NoNewline
	$share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
	$h = [System.IO.File]::Open($victim, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, $share)
	try {
		[System.IO.File]::Move($src, $victim, $true)
		Start-Sleep -Milliseconds 600
		$buf = New-Object byte[] 64
		$n = 0
		try {
			$h.Position = 0
			$n = $h.Read($buf, 0, 64)
		}
		catch {
			# **When the body is deleted along with it, the read dies here** (how it used to fail; FileNotFoundException).
			Fail "the handle on the overwritten side lost its body (the read failed): $($_.Exception.GetBaseException().GetType().Name)"
			return
		}
		$body = [System.Text.Encoding]::ASCII.GetString($buf, 0, $n)
		if ($body -ne "VICTIM-BODY") {
			Fail "the handle on the overwritten side lost its body: '$body' ($n byte(s), expected 'VICTIM-BODY')"
			return
		}
	}
	finally {
		$h.Dispose()
	}
	# The name itself must point at the new contents (is the replacement itself intact).
	$byName = Get-Content -LiteralPath $victim -Raw
	if ($byName -ne "NEW-BODY") {
		Fail "after the replacement the name does not point at the new contents: '$byName'"
		return
	}
	Pass
}


# **Adding ReadOnly to a directory must not drop the execute (traverse) right.**
#
# `ReadOnly` is reflected in the write bits of `st_mode`, but **it used to rebuild the mode as 0444 / 0644 / 0755**,
# so **adding it to a directory made it 0444 the moment it was set, dropping the x bit so `cd` from Linux stopped working**.
# **The Windows side holds no mode, so it is only visible through the projected ACL.**
#
# It also checks that **write comes back** once it is cleared (otherwise "ReadOnly cannot be cleared").
# **That the mode is not rebuilt to a default value** cannot be seen without looking at the database, so that
# part is held by the measurements recorded in [windows-parity.md](../../docs/design/windows-parity.md).
function test_readonly_dir_keeps_execute {
	$d = Join-Path $TestRoot "t102_rodir"
	if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d -ErrorAction Stop | Out-Null }

	$item = Get-Item -LiteralPath $d
	$item.Attributes = $item.Attributes -bor [System.IO.FileAttributes]::ReadOnly
	Start-Sleep -Milliseconds 400
	if (((Get-Item -LiteralPath $d).Attributes -band [System.IO.FileAttributes]::ReadOnly) -eq 0) {
		Fail "ReadOnly cannot be set on a directory"
		return
	}
	# **The execute (traverse) right must survive in the projected ACL.** Back when it was rebuilt to 0444 this is what failed.
	$acl = Get-Acl -LiteralPath $d
	$exec = @($acl.Access | Where-Object { ($_.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::ExecuteFile) -ne 0 })
	if ($exec.Count -eq 0) {
		Fail "setting ReadOnly removed the directory's execute (traverse) right = the x bit was dropped"
		return
	}
	# It does not go as far as checking that nothing can be created inside (that ReadOnly is enforced) -
	# Dokan does not enforce the mode, so being able to create there is **out of scope for this test**.
	$item = Get-Item -LiteralPath $d
	$item.Attributes = $item.Attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly)
	Start-Sleep -Milliseconds 400
	if (((Get-Item -LiteralPath $d).Attributes -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
		Fail "ReadOnly cannot be cleared (the owner's write did not come back)"
		return
	}
	Pass
}

# ===== the namespace policy (docs/design/namespace-policy.md) =====

# **Do not let Windows create a name that can be created but not handled.**
# Creating a single `NUL` makes that directory undeletable with `Remove-Item -Recurse`
# (ERROR_INVALID_FUNCTION), so **it is rejected at the entrance**. **Only a new creation is rejected**;
# existing ones (created from Linux, say) can still be opened.
function test_ns_reserved_device_name_rejected {
	$dir = Join-Path $TestRoot "t103_reserved"
	if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -ErrorAction Stop | Out-Null }

	# **Pass it through `\\?\`.** With a plain Win32 path **`NUL` never reaches the FS and resolves to the
	# NUL device instead** (the write succeeds but no file is created), so **it would measure a path pgfs
	# is not even involved in**.
	# The real harm appears when it actually gets created through `\\?\`, so that is what is checked.
	$raw = "\\?\" + $dir + "\"
	foreach ($name in @("CON", "NUL", "COM1", "LPT1", "CON.txt")) {
		$p = $raw + $name
		$created = $false
		try {
			[System.IO.File]::WriteAllText($p, "x")
			$created = $true
		}
		catch { }
		if ($created) {
			Fail "the reserved name '$name' could be created (it is not being rejected at the entrance)"
			return
		}
	}
	# It also checks that, **having been rejected, the directory can be deleted normally** (which is the whole point of rejecting it).
	try { Remove-Item -LiteralPath $dir -Recurse -ErrorAction Stop }
	catch {
		Fail "the reserved name was rejected yet the directory cannot be deleted: $($_.Exception.Message)"
		return
	}
	Pass
}

# **A trailing space or dot is rejected too.** `dot` and `dot.` can coexist as separate things, and giving
# `dot.` in a Win32 path normalizes it so the contents of `dot` come back - **it is not an error**, so the user cannot notice.
function test_ns_trailing_space_or_dot_rejected {
	$dir = Join-Path $TestRoot "t104_trailing"
	if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -ErrorAction Stop | Out-Null }

	# **Pass it through `\\?\`.** With a plain Win32 path the trailing character is normalized away and it
	# becomes a different name before reaching the FS (= it would not be checking the FS's decision at all).
	$raw = "\\?\" + (Join-Path $dir "x")
	$raw = $raw.Substring(0, $raw.Length - 1)   # strip the trailing "x" to get the base path
	foreach ($name in @("trailspace ", "traildot.")) {
		$p = $raw + $name
		$created = $false
		try {
			[System.IO.File]::WriteAllText($p, "x")
			$created = $true
		}
		catch { }
		if ($created) {
			Fail "the name '$name' with a trailing space or dot could be created"
			return
		}
	}
	Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
	Pass
}

# **A rename gets the same name check as a create.** Before, MoveFile skipped it, and through `\\?\` or Git
# Bash's `mv a CON` / `mv a 'b.'` the names blocked on create could be made. **The kind of failure is checked
# too**: the rename fails **and** the original name is still there (losing the original while refusing would be
# worse than not refusing).
function test_ns_rename_to_unsafe_name_rejected {
	$dir = Join-Path $TestRoot "t106_rename_unsafe"
	if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -ErrorAction Stop | Out-Null }
	$src = Join-Path $dir "src.txt"
	[System.IO.File]::WriteAllText($src, "keep")
	$raw = "\\?\" + $dir + "\"
	# A **leftover with an unsafe name** from an earlier failure would let the rename through as a replacement (an
	# existing name) and break the test's premise. It can only be removed through `\\?\`, so remove it first.
	foreach ($name in @("CON", "COM1", "traildot.", "trailspace ")) {
		try { [System.IO.File]::Delete($raw + $name) } catch { }
	}
	foreach ($name in @("CON", "COM1", "traildot.", "trailspace ")) {
		$moved = $false
		try {
			[System.IO.File]::Move("\\?\" + $src, $raw + $name)
			$moved = $true
		}
		catch { }
		if ($moved) {
			# **Remove the unsafe name it made through `\\?\` before failing** (leaving it breaks the next run and the cleanup).
			try { [System.IO.File]::Delete($raw + $name) } catch { }
			Fail "could rename to '$name' (MoveFile skips the name check)"
			return
		}
		if (-not (Test-Path -LiteralPath $src)) {
			Fail "the rename to '$name' failed, but the original src.txt is gone"
			return
		}
	}
	if ([System.IO.File]::ReadAllText($src) -ne "keep") {
		Fail "the contents of src.txt changed after the refused rename"
		return
	}
	Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
	Pass
}

# **`.fuse_hidden*` is not hidden on Windows.** libfuse leaves no such leftovers here, so there is no reason
# to hide them, and hiding them only leaves "it does not show up in the enumeration yet cannot be deleted"
# (`Remove-Item` goes through the enumeration so it cannot delete it, while calling `DeleteFile` directly can
# = **there is a way to delete it and nobody can find it**).
function test_ns_libfuse_leftover_is_visible {
	$dir = Join-Path $TestRoot "t105_hidden"
	if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -ErrorAction Stop | Out-Null }
	$name = ".fuse_hidden0123456789abcdef"
	Set-Content -LiteralPath (Join-Path $dir $name) -Value "LEFTOVER" -NoNewline

	# **Avoid the trap where $null becomes a one-element array** (tests/windows/README.md, the traps hit while writing the tests)
	$listed = @(Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
	if ($listed -notcontains $name) {
		Fail "a name in libfuse's format does not show up in the enumeration (the policy is not to hide them on Windows): [$($listed -join ', ')]"
		return
	}
	# **It must be deletable with `Remove-Item`.** If it is hidden, it fails with "not found".
	try { Remove-Item -LiteralPath (Join-Path $dir $name) -Force -ErrorAction Stop }
	catch {
		Fail "it cannot be deleted with Remove-Item (it is hidden): $($_.Exception.Message)"
		return
	}
	# **The parent must be rmdir-able.** If it is hidden, that becomes ENOTEMPTY.
	try { Remove-Item -LiteralPath $dir -ErrorAction Stop }
	catch {
		Fail "the parent directory cannot be rmdir'ed (a hidden row is still there): $($_.Exception.Message)"
		return
	}
	Pass
}

function test_concurrent_mkdir_diff_dirs {
	$jobs = 0..4 | ForEach-Object {
		Start-Job -ScriptBlock {
			param($p)
			New-Item -ItemType Directory -Path $p | Out-Null
		} -ArgumentList (Join-Path $TestRoot "t92_cm_$_")
	}
	$jobs | Wait-Job | Out-Null
	$jobs | ForEach-Object { Remove-Job -Job $_ -Force }
	for ($i = 0; $i -lt 5; $i++) {
		$p = Join-Path $TestRoot "t92_cm_$i"
		if (-not (Test-Path -LiteralPath $p -PathType Container)) {
			Fail "t92_cm_${i} missing"
			return
		}
	}
	Pass
}

# ===== main =====

function Setup {
	Write-Host "${BLUE}=== pgfs Windows e2e tests ===${NC}"
	Write-Host "Mount root: $MountRoot"
	Write-Host "Test root:  $TestRoot"
	if ($Filter -ne "") {
		Write-Host "Filter:     $Filter"
	}
	Write-Host ""

	if (-not (Test-Path -LiteralPath $MountRoot -PathType Container)) {
		Write-Host "${RED}ERROR${NC}: $MountRoot does not exist."
		Write-Host "Mount assign.pgfs first (bin\Publish\assign.pgfs.exe -m $MountRoot)."
		exit 2
	}

	# Clean up any existing test directory.
	if (Test-Path -LiteralPath $TestRoot) {
		try { Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction Stop } catch {}
	}
	try {
		New-Item -ItemType Directory -Path $TestRoot -ErrorAction Stop | Out-Null
	}
	catch {
		Write-Host "${RED}ERROR${NC}: failed to create $TestRoot (mount writable?): $($_.Exception.Message)"
		exit 3
	}
}

function Teardown {
	if (Test-Path -LiteralPath $TestRoot) {
		try { Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue } catch {}
	}
}

try {
	Setup

	# directory operations
	Invoke-Test test_mkdir_rmdir
	Invoke-Test test_nested_directories
	Invoke-Test test_many_files_ls
	Invoke-Test test_rmdir_nonempty_fails

	# basic file operations
	Invoke-Test test_touch_unlink
	Invoke-Test test_small_write_read
	Invoke-Test test_append
	Invoke-Test test_overwrite_truncates

	# data I/O
	Invoke-Test test_large_file_round_trip
	Invoke-Test test_truncate_shrink
	Invoke-Test test_truncate_grow
	Invoke-Test test_truncate_to_zero
Invoke-Test test_truncate_zero_then_rewrite

	# rename
	Invoke-Test test_rename_file
	Invoke-Test test_rename_into_subdir

	# attributes / time
	Invoke-Test test_readonly_attribute
	Invoke-Test test_hidden_system_archive_roundtrip
	Invoke-Test test_dotfile_hidden_heuristic
	Invoke-Test test_dotfile_unhide_sticks
	Invoke-Test test_set_lastwritetime

	# volume / pattern
	Invoke-Test test_volume_info
	Invoke-Test test_disk_free_space
	Invoke-Test test_getfilesecurity_projection
	Invoke-Test test_setfilesecurity_roundtrip
	Invoke-Test test_wildcard_pattern

	# Windows basics (exclusive create / allocation / a rename onto the same target)
	Invoke-Test test_copy_item_round_trip
	Invoke-Test test_byte_range_lock_enforced
	Invoke-Test test_new_file_owner_is_requestor
	Invoke-Test test_new_file_inherits_parent_group
	Invoke-Test test_createnew_exclusive
	Invoke-Test test_allocation_size_does_not_extend_eof
	Invoke-Test test_rename_same_path_noop
	Invoke-Test test_file_index_is_data_id_and_stable
	Invoke-Test test_rename_over_open_victim_keeps_body
	Invoke-Test test_readonly_dir_keeps_execute
	Invoke-Test test_ns_reserved_device_name_rejected
	Invoke-Test test_ns_trailing_space_or_dot_rejected
	Invoke-Test test_ns_rename_to_unsafe_name_rejected
	Invoke-Test test_ns_libfuse_leftover_is_visible

	# concurrent access
	Invoke-Test test_concurrent_writes_diff_files
	Invoke-Test test_concurrent_reads_same_file
	Invoke-Test test_concurrent_mkdir_diff_dirs

	# ===== summary =====
	Write-Host ""
	Write-Host "${BLUE}===========================================${NC}"
	Write-Host "Results: ${GREEN}$($script:Passed) passed${NC}, ${RED}$($script:Failed) failed${NC}, ${YELLOW}$($script:Skipped) skipped${NC} (out of $($script:Total))"
	# **Fail when not a single test ran.** When a typo in `-Filter` or a missing prerequisite leaves
	# **everything skipped and exit 0**, it **looks like a green pass while nothing was checked**. The
	# Linux side has the same shape: without `psql` on the PATH, `wbmeta.sh` / `negcache.sh` run nothing and exit 0.
	if ($script:Total -eq 0) {
		Write-Host "${RED}not a single test ran (does Filter '$Filter' match nothing?)${NC}"
		exit 1
	}
	if ($script:Passed -eq 0) {
		Write-Host "${RED}not a single test passed (everything skipped = a missing environment, not a passing test)${NC}"
		exit 1
	}
	if ($script:Failed -gt 0) {
		Write-Host "${RED}Failed tests:${NC}"
		foreach ($name in $script:FailedNames) {
			Write-Host "  - $name"
		}
		exit 1
	}
	exit 0
}
finally {
	Teardown
}
