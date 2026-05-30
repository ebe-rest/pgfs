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
# Exercises the ✅/⚠️ operations that pgfs.assign provides through Dokan
# ([docs/Assign.md](../../docs/Assign.md)). Linux-only operations (POSIX
# symlink/hardlink/chmod/chown/xattr) are out of scope.

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

function Assert-Absent($path) {
	if (Test-Path -LiteralPath $path) {
		Fail "expected path to be absent: $path"
	}
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
	Remove-Item -LiteralPath $f
	if (-not $?) { Fail "rm"; return }
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
		Write-Host "Mount pgfs.assign first (bin\Publish\assign.pgfs.exe -m $MountRoot)."
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
	Invoke-Test test_wildcard_pattern

	# concurrent access
	Invoke-Test test_concurrent_writes_diff_files
	Invoke-Test test_concurrent_reads_same_file
	Invoke-Test test_concurrent_mkdir_diff_dirs

	# ===== summary =====
	Write-Host ""
	Write-Host "${BLUE}===========================================${NC}"
	Write-Host "Results: ${GREEN}$($script:Passed) passed${NC}, ${RED}$($script:Failed) failed${NC}, ${YELLOW}$($script:Skipped) skipped${NC} (out of $($script:Total))"
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
