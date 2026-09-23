# pgfs Windows write-back tests
#
# Looks at the durability contract of assign.pgfs with `mount.write_back` (data write-back) **enabled**.
# The Windows counterpart of the Linux version ([tests/linux/writeback.sh](../linux/writeback.sh)).
#
#   pwsh -NoProfile -File tests\windows\writeback.ps1                     # data write-back on
#   pwsh -NoProfile -File tests\windows\writeback.ps1 -WriteBackMetadata  # metadata write-back on as well
#   pwsh -NoProfile -File tests\windows\writeback.ps1 -MountPoint S:
#
# Exit codes: 0 = everything PASSed / 1 = at least one FAIL / 2 = the mount could not be prepared
#
# **This script mounts and remounts on its own** (which is why it is separate from e2e).
# Durability can only be measured as "kill the process -> remount -> is the content still there".
#
# Prerequisites: bin\Publish\assign.pgfs.exe and pgfs.toml. The Dokan driver installed.
# Note: `Stop-Process -Force` is the equivalent of Linux's `kill -9`. Neither Cleanup nor Dispose runs, so
#       **a write that did not go through an explicit synchronization (FlushFileBuffers / WRITE_THROUGH) is allowed to be lost**.

[CmdletBinding()]
param(
	[string]$MountPoint = "P:",
	[string]$Filter = "",
	[switch]$WriteBackMetadata
)

[System.Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = "Continue"

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$AssignBinary = Join-Path $RepoRoot "bin\Publish\assign.pgfs.exe"
$SettingFile = Join-Path $RepoRoot "pgfs.toml"
$DokanCtl = "C:\Program Files\Dokan\Dokan Library-2.3.1\dokanctl.exe"

$script:Total = 0
$script:Passed = 0
$script:Failed = 0
$script:FailedNames = @()
$script:Current = ""
$script:Proc = $null

$MountRoot = $MountPoint.TrimEnd('\') + '\'
# Note: Join-Path errors on a drive that does not exist (it is evaluated before the mount, so the path is built by string concatenation)
$TestRoot = $MountRoot + "wbtest"

function Say($msg, $color = "Gray") { Write-Host $msg -ForegroundColor $color }
function Pass { $script:Passed++; Write-Host "PASS" -ForegroundColor Green -NoNewline; Write-Host ": $script:Current" }
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
	catch { Fail "exception [$($_.Exception.GetType().FullName)]: $($_.Exception.Message)" }
}

# ===== mount / unmount =====

function Start-Mount {
	# interval 0 = no time trigger for the background flush. Without it, the negative control that watches
	# "a write with no barrier is lost" turns into a race against a one-second wait and the test loses its teeth.
	$argLine = "-f `"$SettingFile`" -m `"$MountPoint`" --write-back --write-back-interval-ms 0"
	if ($WriteBackMetadata) { $argLine += " --write-back-metadata" }
	$log = Join-Path $env:TEMP "pgfs-wb.log"
	$err = Join-Path $env:TEMP "pgfs-wb.err.log"
	$proc = Start-Process -FilePath $AssignBinary -ArgumentList $argLine `
		-PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError $err
	$deadline = (Get-Date).AddSeconds(20)
	while ((Get-Date) -lt $deadline) {
		Start-Sleep -Milliseconds 400
		if (Test-Path -LiteralPath $MountRoot -PathType Container) {
			$script:Proc = $proc
			return $true
		}
		if ($proc.HasExited) {
			Say "  the mount process exited early (exit=$($proc.ExitCode))" "Red"
			if (Test-Path $err) { Get-Content $err | Select-Object -Last 8 | ForEach-Object { Say "    $_" "Red" } }
			return $false
		}
	}
	Say "  could not mount within 20 seconds" "Red"
	return $false
}

# A clean unmount (waiting until the daemon is gone = this is where write-back's cleanup and the exit code are decided).
function Stop-MountGracefully {
	if ($null -eq $script:Proc) { return $null }
	& $DokanCtl /u $MountPoint | Out-Null
	$exited = $script:Proc.WaitForExit(60000)
	$code = $null
	if ($exited) { $code = $script:Proc.ExitCode }
	$script:Proc = $null
	$null = Wait-MountGone
	return $code
}

# The equivalent of kill -9. Neither Cleanup nor Dispose runs.
function Stop-MountHard {
	if ($null -eq $script:Proc) { return }
	Stop-Process -Id $script:Proc.Id -Force -ErrorAction SilentlyContinue
	$script:Proc.WaitForExit(30000) | Out-Null
	$script:Proc = $null
	# The mount can be left behind on the driver side, so it is brought down first just in case, then we wait for it to disappear.
	& $DokanCtl /u $MountPoint 2>&1 | Out-Null
	$null = Wait-MountGone
}

function Wait-MountGone {
	$deadline = (Get-Date).AddSeconds(30)
	while ((Get-Date) -lt $deadline) {
		if (-not (Test-Path -LiteralPath $MountRoot -PathType Container)) { return $true }
		Start-Sleep -Milliseconds 300
	}
	Say "  the mount did not disappear within 30 seconds: $MountRoot" "Yellow"
	return $false
}

function Reset-TestRoot {
	if (Test-Path -LiteralPath $TestRoot) {
		Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
	}
	New-Item -ItemType Directory -Path $TestRoot -ErrorAction Stop | Out-Null
}

# ===== helpers =====

# Write one file the given way, then force-kill the process -> remount and return the contents.
function Invoke-CrashCycle($name, $writer) {
	$path = "$TestRoot\$name"
	& $writer $path
	Stop-MountHard
	if (-not (Start-Mount)) {
		throw "the remount failed"
	}
	if (-not (Test-Path -LiteralPath $path)) { return $null }
	return [System.IO.File]::ReadAllText($path)
}

$Payload = "pgfs-write-back-durability-check"

# ===== tests =====

# After an explicit synchronization (FlushFileBuffers) it survives a kill.
# .NET's FileStream.Flush($true) comes down to FlushFileBuffers.
function test_wb_flush_survives_kill {
	$body = Invoke-CrashCycle "t01_flush.txt" {
		param($p)
		$fs = [System.IO.File]::Create($p)
		try {
			$bytes = [System.Text.Encoding]::ASCII.GetBytes($Payload)
			$fs.Write($bytes, 0, $bytes.Length)
			$fs.Flush($true)   # FlushFileBuffers
		}
		finally { $fs.Dispose() }
	}
	if ($body -ne $Payload) {
		Fail "the contents were lost although the kill came after FlushFileBuffers: '$body'"
		return
	}
	Pass
}

# After the handle is closed (Cleanup -> CloseInode) it survives a kill.
# **The contract changes when metadata write-back is on** (it is close-no-flush, so it may disappear).
function test_wb_close_survives_kill {
	if ($WriteBackMetadata) {
		Say "  (with metadata on, close-no-flush is the contract, so this test means something different - running it anyway)" "Yellow"
	}
	$body = Invoke-CrashCycle "t02_close.txt" {
		param($p)
		[System.IO.File]::WriteAllText($p, $Payload)
	}
	if ($WriteBackMetadata) {
		# Under the metadata-on contract, a close alone does not necessarily persist it. Neither surviving nor being lost is a FAIL.
		Say "  metadata on: the contents after the close = '$body' (either is allowed by the contract)" "Yellow"
		Pass
		return
	}
	if ($body -ne $Payload) {
		Fail "the contents were lost although the kill came after the close: '$body'"
		return
	}
	Pass
}

# A WRITE_THROUGH handle survives a kill **without a flush or a close**.
# (Wired up so that a full barrier is raised on every WriteFile even with write-back enabled.)
function test_wb_writethrough_survives_kill {
	$body = Invoke-CrashCycle "t03_wt.txt" {
		param($p)
		$opts = [System.IO.FileStreamOptions]::new()
		$opts.Mode = [System.IO.FileMode]::Create
		$opts.Access = [System.IO.FileAccess]::Write
		$opts.Options = [System.IO.FileOptions]::WriteThrough
		$fs = [System.IO.FileStream]::new($p, $opts)
		try {
			$bytes = [System.Text.Encoding]::ASCII.GetBytes($Payload)
			$fs.Write($bytes, 0, $bytes.Length)
			$fs.Flush()   # push out .NET's user buffer only (FlushFileBuffers is not called)
		}
		finally {
			# We want to kill before Dispose's Cleanup runs, so we leave with the handle still open.
			# (Leaving it to PowerShell's GC would be non-deterministic, so nothing is done explicitly here.)
		}
	}
	if ($body -ne $Payload) {
		Fail "the contents were lost to the kill although it is WRITE_THROUGH: '$body'"
		return
	}
	Pass
}

# A clean unmount writes out whatever is unflushed and ends with exit 0.
function test_wb_graceful_unmount_persists {
	$path = "$TestRoot\t04_graceful.txt"
	[System.IO.File]::WriteAllText($path, $Payload)
	$code = Stop-MountGracefully
	if ($code -ne 0) {
		Fail "the exit code of the clean unmount is not 0: $code"
		if (-not (Start-Mount)) { throw "the remount failed" }
		return
	}
	if (-not (Start-Mount)) { throw "the remount failed" }
	if (-not (Test-Path -LiteralPath $path)) {
		Fail "the contents disappeared after the clean unmount: $path"
		return
	}
	$body = [System.IO.File]::ReadAllText($path)
	if ($body -ne $Payload) {
		Fail "the contents after the clean unmount differ: '$body'"
		return
	}
	Pass
}

# A negative control: a write that **did not go through a barrier** is lost to the kill (if it is not lost,
# the three tests above were merely "persisted by chance" and the contract has not been verified).
function test_wb_unflushed_is_lost_without_barrier {
	$body = Invoke-CrashCycle "t06_nobarrier.txt" {
		param($p)
		$fs = [System.IO.File]::Create($p)
		$bytes = [System.Text.Encoding]::ASCII.GetBytes($Payload)
		$fs.Write($bytes, 0, $bytes.Length)
		$fs.Flush()   # .NET's user buffer only. FlushFileBuffers is not called and it is not closed
	}
	if ($body -eq $Payload) {
		Fail "a write with no barrier was persisted (suspect this suite cannot verify the contract)"
		return
	}
	Pass
}

# A larger write (spanning chunks) must match exactly after the flush.
function test_wb_large_write_flush_roundtrip {
	$path = "$TestRoot\t05_large.bin"
	$size = 3 * 1024 * 1024
	$data = [byte[]]::new($size)
	[System.Random]::new(42).NextBytes($data)
	$fs = [System.IO.File]::Create($path)
	try {
		$fs.Write($data, 0, $data.Length)
		$fs.Flush($true)
	}
	finally { $fs.Dispose() }
	$expected = [System.Security.Cryptography.SHA256]::HashData($data)
	$actual = [System.Security.Cryptography.SHA256]::HashData([System.IO.File]::ReadAllBytes($path))
	if ([System.Convert]::ToHexString($expected) -ne [System.Convert]::ToHexString($actual)) {
		Fail "the hash of the 3 MiB read-back does not match"
		return
	}
	Pass
}

# ===== main =====

Say "=== pgfs Windows write-back tests ===" "Cyan"
Say ("mount = {0} / write_back = on / write_back_metadata = {1}" -f $MountPoint, $(if ($WriteBackMetadata) { "on" } else { "off" }))

if (-not (Test-Path -LiteralPath $AssignBinary)) {
	Say "assign.pgfs.exe was not found: $AssignBinary" "Red"
	exit 2
}
if (Test-Path -LiteralPath $MountRoot -PathType Container) {
	Say "$MountRoot is already mounted. Unmount it first (this test mounts and remounts on its own)" "Red"
	exit 2
}
if (-not (Start-Mount)) { exit 2 }

try {
	Reset-TestRoot

	Invoke-Test test_wb_flush_survives_kill
	Invoke-Test test_wb_close_survives_kill
	Invoke-Test test_wb_writethrough_survives_kill
	Invoke-Test test_wb_unflushed_is_lost_without_barrier
	Invoke-Test test_wb_graceful_unmount_persists
	Invoke-Test test_wb_large_write_flush_roundtrip

	Write-Host ""
	Write-Host "Results: " -NoNewline
	Write-Host "$($script:Passed) passed" -ForegroundColor Green -NoNewline
	Write-Host ", " -NoNewline
	Write-Host "$($script:Failed) failed" -ForegroundColor Red -NoNewline
	Write-Host " (out of $($script:Total))"
	foreach ($n in $script:FailedNames) { Say "  - $n" "Red" }
}
finally {
	if ($null -ne $script:Proc) {
		if (Test-Path -LiteralPath $TestRoot) {
			Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
		}
		$code = Stop-MountGracefully
		Say "  unmounted (exit=$code)" "Gray"
	}
}

# **Fail when not a single test ran.** When a typo in `-Filter` or a missing prerequisite leaves
# **everything skipped and exit 0**, it **looks like a green pass while nothing was checked**.
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
