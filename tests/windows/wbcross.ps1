# pgfs Windows write-back cross-client tests
#
# Looks at the contract for **another mount writing the same body while a write-back-enabled mount is still holding dirty data**.
# The source of truth is [docs/design/write-back.md, the cross-client contract](../../docs/design/write-back.md)
# and [docs/Mount.md](../../docs/Mount.md).
#
#   pwsh -NoProfile -File tests\windows\wbcross.ps1
#   pwsh -NoProfile -File tests\windows\wbcross.ps1 -MountB S: -Filter truncate
#
# The setup:
#   A (P: by default) = `--write-back --write-back-interval-ms 0 --notify`  <- the time trigger is off so dirty data is retained
#   B (R: by default) = `--notify` only (write-through)
#
# **`--write-back-interval-ms 0` is required.** Left at the default 1000 ms the background flush runs after a
# second, "still holding dirty data" cannot be produced and **even a pre-fix build goes green**.
#
# **The writes go through P/Invoke.** The default bufferSize of .NET `FileStream` is 4096, so a `Write`
# smaller than that **does not reach the FS until Dispose** - the details are in
# [README.md, the traps hit while writing the tests (Windows)](README.md).
# Each scenario confirms that **`WriteFileProxy` grew in the log** before going on, and Skips when it did not
# (it must not go green while nothing arrived).
#
# Exit codes: 0 = everything PASSed / 1 = at least one FAIL / 2 = the environment could not be prepared

[CmdletBinding()]
param(
	[string]$MountA = "P:",
	[string]$MountB = "R:",
	[string]$Filter = "",
	[string]$LogFile = $(if ($env:PGFS_LOG_FILE) { $env:PGFS_LOG_FILE } else { "" })
)

[System.Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = "Continue"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class PgfsNative {
	[DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
	public static extern IntPtr CreateFileW(string p, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
	[DllImport("kernel32.dll", SetLastError=true)]
	public static extern bool WriteFile(IntPtr h, byte[] buf, uint n, out uint written, IntPtr ov);
	[DllImport("kernel32.dll", SetLastError=true)]
	public static extern bool SetFilePointerEx(IntPtr h, long dist, IntPtr newPtr, uint method);
	[DllImport("kernel32.dll", SetLastError=true)]
	public static extern bool CloseHandle(IntPtr h);
}
"@

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$AssignBinary = Join-Path $RepoRoot "bin\Publish\assign.pgfs.exe"
$SettingFile = Join-Path $RepoRoot "pgfs.toml"
$DokanCtl = "C:\Program Files\Dokan\Dokan Library-2.3.1\dokanctl.exe"
$CHUNK = 1048576

if ([string]::IsNullOrEmpty($LogFile)) {
	$LogFile = Join-Path $env:USERPROFILE ("pgfs\log\pgfs-{0}.log" -f (Get-Date -Format 'yyyyMMdd'))
}

$GENERIC_RW    = [uint32]3221225472   # GENERIC_READ | GENERIC_WRITE
$SHARE_ALL     = [uint32]7            # READ | WRITE | DELETE
$OPEN_EXISTING = [uint32]3
$INVALID       = [IntPtr]::new(-1)

$script:Total = 0
$script:Passed = 0
$script:Failed = 0
$script:Skipped = 0
$script:FailedNames = @()
$script:Current = ""
$script:ProcA = $null
$script:ProcB = $null

function Say($msg, $color = "Gray") { Write-Host $msg -ForegroundColor $color }
function Pass { $script:Passed++; Write-Host "PASS" -ForegroundColor Green -NoNewline; Write-Host ": $script:Current" }
function Skip($msg) { $script:Skipped++; Write-Host "SKIP" -ForegroundColor Yellow -NoNewline; Write-Host ": $script:Current - $msg" }
function Fail($msg) {
	$script:Failed++
	$script:FailedNames += $script:Current
	Write-Host "FAIL" -ForegroundColor Red -NoNewline
	Write-Host ": $script:Current - $msg"
}

function Invoke-Test($name) {
	if ($Filter -ne "" -and $name -notlike "*$Filter*") { return }
	$script:Total++
	$script:Current = $name
	try { & $name }
	catch { Fail "exception [$($_.Exception.GetBaseException().GetType().FullName)]: $($_.Exception.Message)" }
}

function Start-Mount($point, $extra) {
	$root = $point.TrimEnd('\') + '\'
	if (Test-Path -LiteralPath $root -PathType Container) { Say "  $root is already mounted" "Red"; return $null }
	$tag = $point.Replace(':', '')
	$proc = Start-Process -FilePath $AssignBinary `
		-ArgumentList "-f `"$SettingFile`" -m `"$point`" --notify $extra" `
		-PassThru -NoNewWindow `
		-RedirectStandardOutput (Join-Path $env:TEMP "pgfs-wbcross-$tag.log") `
		-RedirectStandardError  (Join-Path $env:TEMP "pgfs-wbcross-$tag.err.log")
	$deadline = (Get-Date).AddSeconds(25)
	while ((Get-Date) -lt $deadline) {
		Start-Sleep -Milliseconds 400
		if (Test-Path -LiteralPath $root -PathType Container) { return $proc }
		if ($proc.HasExited) { return $null }
	}
	return $null
}

function Stop-Mount($point, $proc) {
	if ($null -eq $proc) { return }
	& $DokanCtl /u $point 2>&1 | Out-Null
	$proc.WaitForExit(30000) | Out-Null
	Start-Sleep -Seconds 2
}

# The mount that **holds dirty data while the other side moves**. **The options are baked in here rather than left to the caller.**
#
# Forgetting `--write-back-interval-ms 0` lets the default 1000 ms background flush write the dirty data out
# first, and **it goes green without the conflict under test ever happening**. The same omission was made
# **four times** (the three H-2 scenarios and the exit-4 measurement). **Every one of them looked like the
# false conclusion "it does not reproduce"**, so it is prevented by construction rather than by care.
# The Linux side bakes it into `mount_a_writeback()` in `crossclient.sh` for the same reason.
function Start-MountHoldingDirty($point) {
	return Start-Mount $point "--write-back --write-back-interval-ms 0"
}

# The number of `WriteFileProxy` entries. -1 when the log cannot be read (the caller then Skips).
function Get-WriteCallbackCount($name) {
	if (-not (Test-Path -LiteralPath $LogFile)) { return -1 }
	return @(Select-String -Path $LogFile -Pattern ([regex]::Escape("WriteFileProxy : \$name Return")) -ErrorAction SilentlyContinue).Count
}

# A writes at offset and returns **while keeping the handle**. It also returns whether the write arrived.
function Open-AndWriteKeepOpen($path, $name, $offset, $text) {
	$before = Get-WriteCallbackCount $name
	$h = [PgfsNative]::CreateFileW($path, $GENERIC_RW, $SHARE_ALL, [IntPtr]::Zero, $OPEN_EXISTING, 0, [IntPtr]::Zero)
	if ($h -eq $INVALID) { return @{ H = $INVALID; Reached = $false; Err = [Runtime.InteropServices.Marshal]::GetLastWin32Error() } }
	[void][PgfsNative]::SetFilePointerEx($h, [long]$offset, [IntPtr]::Zero, 0)
	$w = 0
	$bytes = [System.Text.Encoding]::ASCII.GetBytes($text)
	[void][PgfsNative]::WriteFile($h, $bytes, [uint32]$bytes.Length, [ref]$w, [IntPtr]::Zero)

	# **Wait until it arrives (up to 15 seconds). Looking once and giving up misjudges "a delay" as "it never arrived".**
	# Back when it looked once after a fixed 2 seconds, **the same test arrived when run on its own and did
	# not arrive at the end of a full run**. The log was merely being updated late.
	# **Waiting is what tells the two apart**, and if it still does not come it really did not arrive.
	$after = $before
	$deadline = (Get-Date).AddSeconds(15)
	while ((Get-Date) -lt $deadline) {
		Start-Sleep -Milliseconds 500
		$after = Get-WriteCallbackCount $name
		if ($before -lt 0 -or $after -lt 0) { break }
		if ($after -gt $before) { break }
	}
	# If the log cannot be read, do not conclude "it arrived" (return -1 and let the caller Skip)
	if ($before -lt 0 -or $after -lt 0) { return @{ H = $h; Reached = $null; Written = $w } }
	return @{ H = $h; Reached = ($after -gt $before); Written = $w; Before = $before; After = $after }
}

function Write-ThroughAt($path, $offset, $text) {
	$share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
	$fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, $share)
	try {
		$fs.Seek($offset, 'Begin') | Out-Null
		$b = [System.Text.Encoding]::ASCII.GetBytes($text)
		$fs.Write($b, 0, $b.Length)
		$fs.Flush($true)
	}
	finally { $fs.Dispose() }
}

function Read-At($path, $offset, $len) {
	$fs = [System.IO.File]::OpenRead($path)
	try {
		$fs.Seek($offset, 'Begin') | Out-Null
		$buf = New-Object byte[] $len
		[void]$fs.Read($buf, 0, $len)
		return [System.Text.Encoding]::ASCII.GetString($buf)
	}
	finally { $fs.Dispose() }
}

# ===== tests =====

# **A flush must not roll back another mount's truncate** (already fixed on the Core side).
#
# The dirty `Size` is "the value a stat returns while dirty", so `Math.Max(inode.Size, ...)` is fine there,
# but **what a flush hands to `GREATEST(st_size, @size)` has to be "the furthest end actually written"**.
# Mixing in the cached value makes **the old size come back** after another mount has shrunk it.
#
# On a pre-fix build it fails with `st_size = 32` (although A only wrote 4 bytes).
function test_wbx_flush_does_not_undo_remote_truncate {
	$name = "wbx_trunc.bin"
	$pathA = $script:RootA + $name
	$pathB = $script:RootB + $name

	# **Delete the previous body before creating it** (the same reason as the chunkwide side below; the premise is rebuilt every time)
	Remove-Item -LiteralPath $pathB -Force -ErrorAction SilentlyContinue
	Start-Sleep -Seconds 1

	[System.IO.File]::WriteAllText($pathB, ("A" * 32))
	Start-Sleep -Seconds 2
	[void][System.IO.File]::ReadAllText($pathA)   # put 32 into A's cache

	$w = Open-AndWriteKeepOpen $pathA $name 0 "BBBB"
	if ($w.H -eq $INVALID) { Fail "cannot open from A (err=$($w.Err))"; return }
	try {
		if ($null -eq $w.Reached) { Skip "the log ($LogFile) cannot be read, so whether the write reached the FS cannot be confirmed"; return }
		# **Do not Skip.** If it has not come after 15 seconds it is not a delay. A Skip would give
		# "1 passed / 1 skipped" and exit 0, burying **a green that measured nothing** (it nearly got buried once).
		if (-not $w.Reached) { Fail "A's write did not reach the FS within 15 seconds ($($w.Before) -> $($w.After)). No dirty data could be produced, so this test does not hold as a measurement"; return }

		$fsB = [System.IO.File]::Open($pathB, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite,
			([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
		$fsB.SetLength(4)
		$fsB.Flush($true)
		$fsB.Dispose()
		Start-Sleep -Seconds 3
	}
	finally {
		[void][PgfsNative]::CloseHandle($w.H)
	}
	Start-Sleep -Seconds 3

	# **Judge by the authoritative value in the database.** B's stat depends on how the notify arrives, so it is not trusted.
	$len = $script:Verify.Invoke($name)
	if ($len -ne 4) {
		Fail "another mount's truncate was rolled back (st_size = $len / expected 4)"
		return
	}
	Pass
}

# **⚠ This test pins down "what happens today" and is not the desirable behaviour.**
#
# The dirty buffer is **a complete image of the chunk** (step (3) of `Api.LoadChunkBase`), so a flush writes
# that whole image back. As a result, **bytes another mount wrote into a chunk A has read are erased by A's flush**.
# What is erased is **whole chunks rather than individual bytes**, and a write across a boundary takes **both chunks entirely**.
#
# The contract of record is [write-back.md, the cross-client contract](../../docs/design/write-back.md).
# **Closing this hole makes this test fail. When that happens, rewrite the test** (not "revert the implementation because it failed").
function test_wbx_flush_clobbers_remote_write_chunkwide {
	# **Use a unique name every time.** Deleting and recreating the same name has a path where
	# **the create runs before the delete reaches the FS** (a delete on Windows does not go down until the
	# last handle closes), so the old body survives and it becomes an "overwrite". That in turn means
	# **the value A is about to write is already there and Windows never sends the write down**, leaving
	# `WriteFileProxy` unchanged through the whole 15-second wait.
	# **Adding `Remove-Item` still reproduced it at the end of a full run**, so it was changed to
	# **not sharing the premise** rather than rebuilding it.
	$name = "wbx_chunk_$([System.Guid]::NewGuid().ToString('N').Substring(0, 8)).bin"
	$pathA = $script:RootA + $name
	$pathB = $script:RootB + $name
	$script:ChunkName = $name

	$buf = New-Object byte[] (2 * $CHUNK)
	for ($i = 0; $i -lt $buf.Length; $i++) { $buf[$i] = 0x41 }
	[System.IO.File]::WriteAllBytes($pathB, $buf)
	Start-Sleep -Seconds 2

	# Put a two-chunk image into A
	$fr = [System.IO.File]::OpenRead($pathA)
	$tmp = New-Object byte[] (2 * $CHUNK)
	[void]$fr.Read($tmp, 0, $tmp.Length)
	$fr.Dispose()

	# **Record the state before writing.** If it differs from the expectation, the reason nothing arrives
	# is on the "the premise is broken" side (kept so that the next failure can be told apart).
	$beforeEdge = Read-At $pathA ($CHUNK - 2) 4
	Say "    (premise: len=$((Get-Item $pathA).Length) / offset $($CHUNK - 2) = '$beforeEdge' / name=$name)" "DarkGray"

	# Write 4 bytes across the chunk boundary (the last 2B of chunk 0 plus the first 2B of chunk 1)
	$w = Open-AndWriteKeepOpen $pathA $name ($CHUNK - 2) "BBBB"
	if ($w.H -eq $INVALID) { Fail "cannot open from A (err=$($w.Err))"; return }
	try {
		if ($null -eq $w.Reached) { Skip "the log ($LogFile) cannot be read, so whether the write reached the FS cannot be confirmed"; return }
		# **Do not Skip.** If it has not come after 15 seconds it is not a delay. A Skip would give
		# "1 passed / 1 skipped" and exit 0, burying **a green that measured nothing** (it nearly got buried once).
		if (-not $w.Reached) { Fail "A's write did not reach the FS within 15 seconds ($($w.Before) -> $($w.After)). No dirty data could be produced, so this test does not hold as a measurement"; return }

		Write-ThroughAt $pathB 0 "CCCC"
		Write-ThroughAt $pathB ($CHUNK + 4) "DDDD"
		Start-Sleep -Seconds 3
	}
	finally {
		[void][PgfsNative]::CloseHandle($w.H)
	}
	Start-Sleep -Seconds 3

	$script:VerifyBytes.Invoke($name)
	$at0 = $script:V0
	$atEdge = $script:VEdge
	$at1 = $script:V1

	# A's 4 bytes survive. **Both** of B's are erased under the current contract.
	if ($atEdge -ne "BBBB") { Fail "A's own write did not survive ($atEdge)"; return }
	if ($at0 -eq "CCCC" -and $at1 -eq "DDDD") {
		Fail "both of B's writes survived. **The trampling may have been fixed** - if it has, rewrite this test (and update the cross-client contract in write-back.md at the same time)"
		return
	}
	if ($at0 -ne "AAAA" -or $at1 -ne "AAAA") {
		Fail "an unexpected state: offset 0 = '$at0' / $($CHUNK + 4) = '$at1' (both were expected to be 'AAAA' = overwritten by A's image)"
		return
	}
	Pass
}

# ===== main =====

Say "=== pgfs Windows write-back cross-client tests ===" "Cyan"
if (-not (Test-Path -LiteralPath $AssignBinary)) { Say "assign.pgfs.exe was not found: $AssignBinary" "Red"; exit 2 }
Say "  A = $MountA (write-back, interval 0) / B = $MountB (write-through)"
Say "  log = $LogFile"

$script:RootA = $MountA.TrimEnd('\') + '\'
$script:RootB = $MountB.TrimEnd('\') + '\'

# The decision is made **after bringing every mount down and reading again** = the authoritative value in the database. Windows has no psql, hence this shape.
$script:Verify = {
	param($name)
	Stop-Mount $MountA $script:ProcA; $script:ProcA = $null
	Stop-Mount $MountB $script:ProcB; $script:ProcB = $null
	$p = Start-Mount $MountA ""
	if ($null -eq $p) { throw "cannot remount to check" }
	try { return (Get-Item ($MountA.TrimEnd('\') + '\' + $name)).Length }
	finally { Stop-Mount $MountA $p }
}

$script:VerifyBytes = {
	param($name)
	Stop-Mount $MountA $script:ProcA; $script:ProcA = $null
	Stop-Mount $MountB $script:ProcB; $script:ProcB = $null
	$p = Start-Mount $MountA ""
	if ($null -eq $p) { throw "cannot remount to check" }
	try {
		$path = $MountA.TrimEnd('\') + '\' + $name
		$script:V0 = Read-At $path 0 4
		$script:VEdge = Read-At $path ($CHUNK - 2) 4
		$script:V1 = Read-At $path ($CHUNK + 4) 4
		# **Cleanup**: the names are unique, so without deleting them 2MB piles up per run.
		# This is the only window where anything is mounted, so they are deleted once they have been read.
		Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
	}
	finally { Stop-Mount $MountA $p }
}

try {
	foreach ($t in @("test_wbx_flush_does_not_undo_remote_truncate", "test_wbx_flush_clobbers_remote_write_chunkwide")) {
		if ($Filter -ne "" -and $t -notlike "*$Filter*") { continue }
		$script:ProcA = Start-MountHoldingDirty $MountA
		if ($null -eq $script:ProcA) { Say "cannot mount A" "Red"; exit 2 }
		$script:ProcB = Start-Mount $MountB ""
		if ($null -eq $script:ProcB) { Stop-Mount $MountA $script:ProcA; Say "cannot mount B" "Red"; exit 2 }
		Invoke-Test $t
		Stop-Mount $MountA $script:ProcA; $script:ProcA = $null
		Stop-Mount $MountB $script:ProcB; $script:ProcB = $null
	}

	Write-Host ""
	Write-Host "Results: " -NoNewline
	Write-Host "$($script:Passed) passed" -ForegroundColor Green -NoNewline
	Write-Host ", " -NoNewline
	Write-Host "$($script:Failed) failed" -ForegroundColor Red -NoNewline
	Write-Host ", $($script:Skipped) skipped (out of $($script:Total))"
	foreach ($n in $script:FailedNames) { Say "  - $n" "Red" }
}
finally {
	Stop-Mount $MountA $script:ProcA
	Stop-Mount $MountB $script:ProcB
}

# **Fail when not a single test ran, or not a single one PASSed** (kept in line with the other suites).
if ($script:Total -eq 0) {
	Say "not a single test ran (does Filter '$Filter' match nothing?)" "Red"
	exit 1
}
if ($script:Passed -eq 0) {
	Say "not a single test passed (everything skipped = a missing environment, not a passing test)" "Red"
	exit 1
}
if ($script:Failed -gt 0) { exit 1 }
exit 0
