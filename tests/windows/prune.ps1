# pgfs Windows prune tests
#
# Checks end to end **whether C-2's body retention really produces leftovers and whether `pgfsctl prune` can clean them up**.
#
#   .\tests\windows\prune.ps1 [-MountPoint P:] [-Filter orphan]
#
# **Only Windows can produce "the real thing" in orphan data** (measured on the Linux side):
# Linux's libfuse **turns an `unlink` of an open file into a rename** with `hard_remove = 0`, so
# `Api.DeleteInode` is only called **when nobody has it open**. On Windows
# **the replacement of a rename** removes the name while the open handle stays, which is where C-2's
# retention kicks in, and **if the daemon dies in the middle, orphan data is left behind**. The Linux side's
# [prune.sh](../linux/prune.sh) stands the leftovers in with psql, so **this is the only place it can be seen end to end**.
#
# **The database is never looked at** - the decision comes from what `pgfsctl prune --json` reports alone.
# Windows has no psql, and **whether what prune says it found really disappears** is the contract under test.
#
# Exit codes: 0 = everything PASSed / 1 = at least one FAIL / 2 = the environment could not be prepared

[CmdletBinding()]
param(
	[string]$MountPoint = "P:",
	[string]$Filter = ""
)

[System.Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = "Continue"

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$AssignBinary = Join-Path $RepoRoot "bin\Publish\assign.pgfs.exe"
$Pgfsctl = Join-Path $RepoRoot "bin\Publish\pgfsctl.exe"
$SettingFile = Join-Path $RepoRoot "pgfs.toml"
$DokanCtl = "C:\Program Files\Dokan\Dokan Library-2.3.1\dokanctl.exe"
$MountRoot = $MountPoint.TrimEnd('\') + '\'
# **`Join-Path` falls over on a drive that does not exist** (before the mount there is no P: yet). Build it by plain concatenation.
$TestRoot = $MountRoot + "prunetest"

$script:Total = 0
$script:Passed = 0
$script:Failed = 0
$script:FailedNames = @()
$script:Current = ""
$script:Proc = $null

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
	catch { Fail "exception [$($_.Exception.GetBaseException().GetType().FullName)]: $($_.Exception.Message)" }
}

function Start-Mount {
	$log = Join-Path $env:TEMP "pgfs-prune.log"
	$err = Join-Path $env:TEMP "pgfs-prune.err.log"
	$proc = Start-Process -FilePath $AssignBinary -ArgumentList "-f `"$SettingFile`" -m `"$MountPoint`"" `
		-PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError $err
	$deadline = (Get-Date).AddSeconds(25)
	while ((Get-Date) -lt $deadline) {
		Start-Sleep -Milliseconds 400
		if (Test-Path -LiteralPath $MountRoot -PathType Container) {
			$script:Proc = $proc
			return $true
		}
		if ($proc.HasExited) { return $false }
	}
	return $false
}

# The equivalent of kill -9. **Neither Cleanup nor CloseFile runs**, so the body C-2 retained is never dropped and stays.
function Kill-Mount {
	if ($null -eq $script:Proc) { return }
	Stop-Process -Id $script:Proc.Id -Force -ErrorAction SilentlyContinue
	$script:Proc.WaitForExit(20000) | Out-Null
	$script:Proc = $null
	# Bring the driver-side mount point down too (the registration survives the process dying).
	& $DokanCtl /u $MountPoint 2>&1 | Out-Null
	Start-Sleep -Seconds 2
}

# **A clean unmount.** Unlike `Kill-Mount`, Cleanup / CloseFile run, so **no orphan is produced**.
# The side that checks "a user's files survive" needs live to be 0 without producing leftovers
# (prune **skips the data-deleting side whenever even one live mount is around**, so measuring while still
# live deletes nothing and **even a pre-fix build goes green** = a test with zero detection power).
function Stop-Mount {
	if ($null -eq $script:Proc) { return }
	& $DokanCtl /u $MountPoint 2>&1 | Out-Null
	$script:Proc.WaitForExit(30000) | Out-Null
	$script:Proc = $null
	Start-Sleep -Seconds 2
}

function Get-PruneJson {
	$out = & $Pgfsctl prune --json --setting-file $SettingFile 2>&1 | Out-String
	try { return $out | ConvertFrom-Json } catch { return $null }
}

function Invoke-PruneApply {
	$out = & $Pgfsctl prune --apply --json --setting-file $SettingFile 2>&1 | Out-String
	try { return $out | ConvertFrom-Json } catch { return $null }
}

# ===== tests =====

# **A body C-2 retained is left as an orphan when the daemon dies, and prune can clean it up.**
#
# The steps: overwrite the victim by rename while it is still open -> **C-2 deletes only the inode row and
# keeps the body** -> **kill without closing** -> nobody is left to drop the body -> prune picks it up.
#
# Without the retention (before C-2) the body goes at the moment of the rename, so **no orphan is produced** =
# this test cannot detect "it grew". It therefore watches C-2's retention and prune's detection **as a pair**.
function test_prune_finds_orphan_from_killed_mount {
	$before = Get-PruneJson
	if ($null -eq $before) { Fail "cannot read prune --json"; return }
	$baseline = [int]$before.orphan_data.rows

	if (-not (Start-Mount)) { Fail "cannot mount"; return }
	New-Item -ItemType Directory -Path $TestRoot -ErrorAction SilentlyContinue | Out-Null
	$victim = $TestRoot + "\victim.txt"
	$src = $TestRoot + "\src.txt"
	Set-Content -LiteralPath $victim -Value "VICTIM-BODY" -NoNewline
	Set-Content -LiteralPath $src -Value "NEW-BODY" -NoNewline
	$share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
	$h = [System.IO.File]::Open($victim, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, $share)
	try {
		[System.IO.File]::Move($src, $victim, $true)
		Start-Sleep -Milliseconds 800
	}
	finally {
		# **Do not Dispose.** Closing makes Core drop the body and no orphan is produced.
		Kill-Mount
	}

	$after = Get-PruneJson
	if ($null -eq $after) { Fail "cannot read prune --json after the kill"; return }
	$found = [int]$after.orphan_data.rows
	if ($found -le $baseline) {
		Fail "the orphan data did not grow ($baseline -> $found). Either C-2's retention is not working or it is dropped on close"
		return
	}
	Pass
}

# **An orphan that was found really disappears with `--apply`** (with no live mount around).
function test_prune_apply_removes_orphan {
	$before = Get-PruneJson
	if ($null -eq $before) { Fail "cannot read prune --json"; return }
	if ([int]$before.mounts.live -ne 0) {
		Fail "a live mount is still around ($($before.mounts.live)). The previous test's cleanup did not work"
		return
	}
	if ([int]$before.orphan_data.rows -eq 0) {
		Fail "there is no orphan data to delete (the previous test should have created some)"
		return
	}
	$result = Invoke-PruneApply
	if ($null -eq $result) { Fail "cannot read prune --apply --json"; return }
	if ([int]$result.applied.orphan_data_deleted -eq 0) {
		Fail "not a single orphan data row was deleted (skipped_because_live = $($result.applied.skipped_because_live))"
		return
	}
	$after = Get-PruneJson
	if ([int]$after.orphan_data.rows -ne 0) {
		Fail "orphan data survived --apply ($($after.orphan_data.rows) row(s))"
		return
	}
	Pass
}

# ===== prune must not delete a user's files =====
#
# **When the picking side and the hiding side disagree, a file visible in `ls` is deleted, contents and all, by prune.**
# `PruneAdmin.ScanFuseHidden` picks by **the prefix match `name LIKE '.fuse_hidden%'` alone**, whereas
# `Api.IsLibfuseHidden`, the side that hides them from the enumeration, checks **the prefix plus 16 hex digits** strictly.
# It is inverted - **the enumeration is strict and the deletion is loose** - so a name a user chose matches only on the deleting side.
# The `_` of a LIKE also matches **any single character** (unescaped), which picks up things whose prefix differs.
#
# The two created here are **both in a format libfuse never produces** = user files that must not be deleted:
#   A `.fuse_hidden_notes.txt`       the same prefix but not 16 hex digits  -> exercises the looseness of the prefix match
#   B `.fuseXhiddenAAAABBBBCCCCDDDD` the hex part follows the format but the prefix differs -> exercises the unescaped `_` of the LIKE
#
# **They are split into two so that it is clear which one a fix addressed** (tightening the format alone makes
# A pass while B survives; escaping the `_` alone makes B pass while A survives).

$script:UserHiddenA = ".fuse_hidden_notes.txt"
$script:UserHiddenB = ".fuseXhiddenAAAABBBBCCCCDDDD"
$script:UserHiddenBody = "USER-DATA-MUST-SURVIVE"
$script:HiddenBaseline = -1

# **It must not even be picked up at the scan stage.** `--apply` is contractually "delete only what was
# shown", so if it is not listed here there is no way for it to be deleted = it stops short of real harm.
function test_prune_scan_ignores_user_files_named_like_fuse_hidden {
	$before = Get-PruneJson
	if ($null -eq $before) { Fail "cannot read prune --json"; return }
	$script:HiddenBaseline = [int]$before.fuse_hidden

	if (-not (Start-Mount)) { Fail "cannot mount"; return }
	try {
		New-Item -ItemType Directory -Path $TestRoot -ErrorAction SilentlyContinue | Out-Null
		Set-Content -LiteralPath ($TestRoot + "\" + $script:UserHiddenA) -Value $script:UserHiddenBody -NoNewline
		Set-Content -LiteralPath ($TestRoot + "\" + $script:UserHiddenB) -Value $script:UserHiddenBody -NoNewline

		# **Confirm first that they show up in the enumeration.** If they are invisible here, the hiding
		# side is picking them up too, and the very premise this regression protects collapses ("it disappears and the user cannot notice").
		$names = @((Get-ChildItem -LiteralPath $TestRoot -Force -ErrorAction SilentlyContinue).Name)
		if ($names -notcontains $script:UserHiddenA) { Fail "$($script:UserHiddenA) does not show up in the enumeration (the hiding side is loose as well)"; return }
		if ($names -notcontains $script:UserHiddenB) { Fail "$($script:UserHiddenB) does not show up in the enumeration (the hiding side is loose as well)"; return }
	}
	finally { Stop-Mount }

	$after = Get-PruneJson
	if ($null -eq $after) { Fail "cannot read prune --json after the unmount"; return }
	$found = [int]$after.fuse_hidden
	if ($found -ne $script:HiddenBaseline) {
		Fail "a user's files are being picked up as libfuse leftovers ($($script:HiddenBaseline) -> $found)"
		return
	}
	Pass
}

# **They must survive `--apply` with their contents intact.** If the scan does not pick them up they survive
# as a matter of course, but this is the only thing that fails if **the deleting side stops using the scan's result and issues its own SQL**.
function test_prune_apply_keeps_user_files_named_like_fuse_hidden {
	if ($script:HiddenBaseline -lt 0) { Fail "the prerequisite test (the scan side) did not run"; return }

	$result = Invoke-PruneApply
	if ($null -eq $result) { Fail "cannot read prune --apply --json"; return }
	# **With something live around, prune skips the data-deleting side entirely.** Going green on that
	# would count "it never went to delete" as a PASS rather than "it did not delete".
	if ([bool]$result.applied.skipped_because_live) {
		Fail "a live mount was still around and prune skipped the data side (this regression cannot be detected in that state)"
		return
	}

	if (-not (Start-Mount)) { Fail "cannot remount to check"; return }
	try {
		foreach ($name in @($script:UserHiddenA, $script:UserHiddenB)) {
			$path = $TestRoot + "\" + $name
			if (-not (Test-Path -LiteralPath $path)) {
				Fail "$name was deleted by prune --apply (fuse_hidden_deleted = $($result.applied.fuse_hidden_deleted))"
				return
			}
			$body = Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
			if ($body -ne $script:UserHiddenBody) {
				Fail "the contents of $name changed ('$body')"
				return
			}
		}
		Remove-Item -LiteralPath ($TestRoot + "\" + $script:UserHiddenA) -Force -ErrorAction SilentlyContinue
		Remove-Item -LiteralPath ($TestRoot + "\" + $script:UserHiddenB) -Force -ErrorAction SilentlyContinue
	}
	finally { Stop-Mount }
	Pass
}

# ===== main =====

Say "=== pgfs Windows prune tests ===" "Cyan"
if (-not (Test-Path -LiteralPath $AssignBinary)) { Say "assign.pgfs.exe was not found" "Red"; exit 2 }
if (-not (Test-Path -LiteralPath $Pgfsctl)) { Say "pgfsctl.exe was not found" "Red"; exit 2 }

try {
	Invoke-Test test_prune_finds_orphan_from_killed_mount
	Invoke-Test test_prune_apply_removes_orphan
	Invoke-Test test_prune_scan_ignores_user_files_named_like_fuse_hidden
	Invoke-Test test_prune_apply_keeps_user_files_named_like_fuse_hidden

	Write-Host ""
	Write-Host "Results: " -NoNewline
	Write-Host "$($script:Passed) passed" -ForegroundColor Green -NoNewline
	Write-Host ", " -NoNewline
	Write-Host "$($script:Failed) failed" -ForegroundColor Red -NoNewline
	Write-Host " (out of $($script:Total))"
	foreach ($n in $script:FailedNames) { Say "  - $n" "Red" }
}
finally {
	if ($null -ne $script:Proc) { Kill-Mount }
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
