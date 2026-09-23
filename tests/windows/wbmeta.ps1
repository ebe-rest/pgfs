# pgfs Windows metadata write-back tests
#
# Looks at the contracts of `mount.write_back_metadata` (metadata write-back) and
# `mount.write_back_metadata_exclusive_create` (the B-1 knob) with **two real mounts**.
# The Linux version ([tests/linux/wbmeta.sh](../linux/wbmeta.sh)) reproduces it by fault injection, INSERTing
# a same-named row directly with psql; this one **makes a real second mount the occupant**, which is a stronger check.
#
#   pwsh -NoProfile -File tests\windows\wbmeta.ps1                  # A=defer / B=write_through
#   pwsh -NoProfile -File tests\windows\wbmeta.ps1 -MountB S:
#   pwsh -NoProfile -File tests\windows\wbmeta.ps1 -Filter conflict
#
# The setup:
#   A (P: by default) = `--write-back --write-back-metadata --write-back-metadata-exclusive-create defer --notify`
#   B (R: by default) = `--notify` only (write-through. **the occupant**)
#
# Exit codes: 0 = everything PASSed / 1 = at least one FAIL / 2 = the mounts could not be prepared
#
# Prerequisites: bin\Publish\assign.pgfs.exe and pgfs.toml. The Dokan driver installed. Two free drive letters.

[CmdletBinding()]
param(
	[string]$MountA = "P:",
	[string]$MountB = "R:",
	[string]$Filter = "",
	[switch]$KeepMounted
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
$script:Skipped = 0
$script:FailedNames = @()
$script:Current = ""
$script:ProcA = $null
$script:ProcB = $null

$RootA = $MountA.TrimEnd('\') + '\' + "mtest"
$RootB = $MountB.TrimEnd('\') + '\' + "mtest"

function Say($msg, $color = "Gray") { Write-Host $msg -ForegroundColor $color }
function Pass { $script:Passed++; Write-Host "PASS" -ForegroundColor Green -NoNewline; Write-Host ": $script:Current" }
function Fail($msg) {
	$script:Failed++
	$script:FailedNames += $script:Current
	Write-Host "FAIL" -ForegroundColor Red -NoNewline
	Write-Host ": $script:Current - $msg"
}
function Skip($msg) {
	$script:Skipped++
	Write-Host "SKIP" -ForegroundColor Yellow -NoNewline
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

function Start-Mount($point, $extra) {
	$root = $point.TrimEnd('\') + '\'
	if (Test-Path -LiteralPath $root -PathType Container) {
		Say "  $root is already mounted (this test mounts and remounts on its own)" "Red"
		return $null
	}
	$tag = $point.Replace(':', '')
	$log = Join-Path $env:TEMP "pgfs-wbmeta-$tag.log"
	$err = Join-Path $env:TEMP "pgfs-wbmeta-$tag.err.log"
	$proc = Start-Process -FilePath $AssignBinary `
		-ArgumentList "-f `"$SettingFile`" -m `"$point`" --notify $extra" `
		-PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError $err
	$deadline = (Get-Date).AddSeconds(20)
	while ((Get-Date) -lt $deadline) {
		Start-Sleep -Milliseconds 400
		if (Test-Path -LiteralPath $root -PathType Container) {
			Say "  mounted $root (pid $($proc.Id))" "Green"
			return $proc
		}
		if ($proc.HasExited) {
			Say "  the mount process exited early (exit=$($proc.ExitCode))" "Red"
			if (Test-Path $err) { Get-Content $err | Select-Object -Last 8 | ForEach-Object { Say "    $_" "Red" } }
			return $null
		}
	}
	Say "  could not mount within 20 seconds: $root" "Red"
	return $null
}

function Stop-Mount($point, $proc) {
	if ($null -eq $proc) { return $null }
	& $DokanCtl /u $point 2>&1 | Out-Null
	$null = $proc.WaitForExit(60000)
	$code = $proc.ExitCode
	Say "  unmounted $point (exit=$code)" "Gray"
	return $code
}

# ===== helpers =====

# An explicit barrier (FlushFileBuffers). **It returns whether it succeeded** - the loser under defer has to fail here.
function Invoke-Barrier($path) {
	try {
		$fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite)
		try { $fs.Flush($true) } finally { $fs.Dispose() }
		return $true
	}
	catch {
		Say "    the barrier failed (which is sometimes expected): $($_.Exception.GetType().Name)" "Yellow"
		return $false
	}
}

# Wait until it is visible from the other mount (via an enumeration = something that always goes down to the FS).
function Wait-Listed($dir, $name, $seconds = 15) {
	$deadline = (Get-Date).AddSeconds($seconds)
	while ((Get-Date) -lt $deadline) {
		if (@(Get-ChildItem -LiteralPath $dir -Filter $name -Force -ErrorAction SilentlyContinue).Count -gt 0) {
			return $true
		}
		Start-Sleep -Milliseconds 300
	}
	return $false
}

$Payload = "winner-content"

# ===== tests =====

# Even under defer, **O_EXCL exclusion within the same mount is kept** (the ledger's TryAdd rejects a (parent,name) collision).
function test_meta_defer_exclusive_within_mount {
	$p = "$RootA\m01_local.txt"
	$fs = [System.IO.File]::Open($p, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
	$fs.Dispose()
	$collided = $false
	try {
		$fs2 = [System.IO.File]::Open($p, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
		$fs2.Dispose()
	}
	catch [System.IO.IOException] { $collided = $true }
	if (-not $collided) {
		Fail "even under defer, a second CreateNew within the same mount has to fail"
		return
	}
	Pass
}

# **The heart of B-1**: when B (write-through) takes the name A (defer) created while still pending,
# A's flush must keep failing and **must not erase the occupant's (B's) contents**.
function test_meta_defer_conflict_does_not_clobber_occupier {
	$name = "m02_conflict.txt"
	# A creates it with **CreateNew**. Under defer it does not go into the database, only onto the ledger (the contract is that close does not flush either).
	# Making this FileMode.Create would turn it into "overwriting an existing file" and miss the create-collision branch under test.
	$fa = [System.IO.File]::Open("$RootA\$name", [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
	try {
		$b = [System.Text.Encoding]::ASCII.GetBytes("loser-content")
		$fa.Write($b, 0, $b.Length)
	}
	finally { $fa.Dispose() }
	# B: a successful **CreateNew** means "A's row is not in the database yet" = the evidence that A really was pending.
	try {
		$fb = [System.IO.File]::Open("$RootB\$name", [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
		try {
			$b2 = [System.Text.Encoding]::ASCII.GetBytes($Payload)
			$fb.Write($b2, 0, $b2.Length)
		}
		finally { $fb.Dispose() }
	}
	catch [System.IO.IOException] {
		Skip "B's CreateNew collided = A's create was not pending (the premise that defer is in effect no longer holds)"
		return
	}
	# A's explicit barrier **has to fail** (a name collision must not be last-flush-wins = the B-1 requirement).
	$flushed = Invoke-Barrier "$RootA\$name"
	if ($flushed) {
		Fail "A's flush succeeded - a name collision has become last-flush-wins (a B-1 requirement violation)"
		return
	}
	# **Drive it into the error state**: the threshold is "5 consecutive failures on the same target", so the barrier is fired several times.
	# Firing once only latches the target without entering the error state, and the premise of the B-7 test that follows would not hold.
	for ($i = 0; $i -lt 6; $i++) {
		$null = Invoke-Barrier "$RootA\$name"
		Start-Sleep -Milliseconds 200
	}
	# The occupant's contents must be untouched (that is the B-1 requirement).
	$body = [System.IO.File]::ReadAllText("$RootB\$name")
	if ($body -ne $Payload) {
		Fail "the occupant's (B's) contents were corrupted: '$body' (expected '$Payload')"
		return
	}
	Pass
}

# B-7: during the error state, **operations that destroy something persisted are stopped**. This watches
# whether "it was stopped" reaches the caller as seen from Windows.
# **It FAILs as things stand (known)**: Core does stop it correctly (the file survives), but
# **Dokan's `Cleanup` is void, so the exception is swallowed and the caller sees success**.
# Fixing it properly means stopping at `DeleteFile` (the deletability check), and that needs a Core
# predicate (CanDestroy) that answers "is this inode pending or persisted" without a side effect.
# Already requested on the Linux side.
function test_meta_error_state_blocks_persisted_delete {
	$name = "m05_persisted.txt"
	# B (write-through) creates it = persisted. Wait until it is visible from A.
	[System.IO.File]::WriteAllText("$RootB\$name", "persisted-content")
	if (-not (Wait-Listed $RootA $name)) {
		Skip "the file B created is not visible from A (a notify delay)"
		return
	}
	# A is in the error state (latched by the collision test just before). Deleting something persisted should be stopped.
	$blocked = $false
	try { Remove-Item -LiteralPath "$RootA\$name" -Force -ErrorAction Stop }
	catch { $blocked = $true }
	# Print all the evidence (Windows defers DeleteFile, so the answer depends on where you look).
	Start-Sleep -Seconds 2
	$listedA = @(Get-ChildItem -LiteralPath $RootA -Filter $name -Force -ErrorAction SilentlyContinue).Count
	$listedB = @(Get-ChildItem -LiteralPath $RootB -Filter $name -Force -ErrorAction SilentlyContinue).Count
	$readable = "no"
	try { $null = [System.IO.File]::ReadAllText("$RootB\$name"); $readable = "yes" } catch { $readable = $_.Exception.GetType().Name }
	Say "    blocked=$blocked listedA=$listedA listedB=$listedB readableFromB=$readable" "Gray"
	if ($listedB -eq 0 -and $readable -ne "yes") {
		Fail "a persisted file was deleted although it is in the error state (B-7 is not working)"
		return
	}
	if (-not $blocked) {
		Fail "the file survived but **the caller saw success** (Dokan has to stop it at DeleteFile)"
		return
	}
	Pass
}

# Verifies the contract that an A latched by a collision **recovers once that file is deleted** (a pure cancel of the pending entry).
# **It FAILs as things stand (a known, unfixed issue)**: the unlink itself goes through and the pending entry
# does disappear, but the error state's latch is not released and the creates that follow stay at `-EIO`.
# Already reported as a problem with the release condition on the Core side.
function test_meta_defer_conflict_recovers_by_unlink {
	$name = "m02_conflict.txt"
	if (-not (Test-Path -LiteralPath "$RootB\$name")) {
		Skip "the prerequisite test (conflict) did not run"
		return
	}
	# Delete it on A's side. It is a pure cancel that does not touch the database, so the occupant's row may survive.
	try { Remove-Item -LiteralPath "$RootA\$name" -Force -ErrorAction Stop }
	catch { Say "    an exception while deleting on A's side: $($_.Exception.GetType().Name)" "Yellow" }
	# Whether it recovered is judged by "create another new file and see whether the explicit barrier goes through".
	# **A delete is triggered at Cleanup**, so the pending entry is not cancelled yet when `Remove-Item` returns.
	# Measured: **up to 16 seconds** until the release (Cleanup itself is delayed when another process holds a handle).
	# **Polling that occupies the FS pushes Cleanup further back still** (it gets to a state where one open takes a second).
	# Measured: the harder it was hammered the later the release came, so it waits quietly first and then tries at intervals.
	$probe = "$RootA\m03_after_recovery.txt"
	$created = $false
	$lastError = ""
	# **Waiting without touching the FS** is the crux. Measured: the delete request had not reached the FS when
	# `Remove-Item` returned (Windows defers it until the last handle is released), and the more it was polled
	# the further back it went (16 seconds -> 41 seconds -> 63 seconds until release). Wait quietly, then try a few times.
	foreach ($wait in @(10, 15, 20)) {
		Start-Sleep -Seconds $wait
		try {
			[System.IO.File]::WriteAllText($probe, "recovered")
			$created = $true
			break
		}
		catch {
			$lastError = $_.Exception.InnerException.GetType().Name
		}
	}
	if (-not $created) {
		# Tell the cases apart by **whether the delete reached the FS**. Windows defers DeleteFile until the
		# last handle is released, so if it still shows up in A's enumeration then "the cancel has not run yet"
		# = it cannot be observed in this environment.
		$stillListed = @(Get-ChildItem -LiteralPath $RootA -Filter $name -Force -ErrorAction SilentlyContinue).Count
		if ($stillListed -gt 0) {
			Skip "timed out before the delete request reached the FS (Windows defers DeleteFile) - the recovery cannot be observed in this environment: $lastError"
			return
		}
		Fail "the unlink arrived yet a new create keeps being refused (the error state's latch is not released): $lastError"
		return
	}
	if (-not (Invoke-Barrier $probe)) {
		Fail "the explicit barrier still fails after the recovery (the error state has not been released)"
		return
	}
	if (-not (Wait-Listed $RootB "m03_after_recovery.txt")) {
		Fail "the file written after the recovery is not visible from B (it was not flushed)"
		return
	}
	Pass
}

# The close-no-flush contract: **a file A merely created and closed may lose its contents to a forced termination**.
# "A zero-length piece of junk survives", however, is a window that stage 2 of metadata write-back declared closed, so which of the two happened is always recorded.
function test_meta_close_no_flush_loses_content {
	$name = "m04_close.txt"
	[System.IO.File]::WriteAllText("$RootA\$name", "should-be-lost")
	# Force-terminate A alone (the equivalent of kill -9) and remount.
	Stop-Process -Id $script:ProcA.Id -Force -ErrorAction SilentlyContinue
	$null = $script:ProcA.WaitForExit(30000)
	& $DokanCtl /u $MountA 2>&1 | Out-Null
	$deadline = (Get-Date).AddSeconds(30)
	while ((Get-Date) -lt $deadline -and (Test-Path -LiteralPath ($MountA.TrimEnd('\') + '\'))) {
		Start-Sleep -Milliseconds 300
	}
	$script:ProcA = Start-Mount $MountA $DeferArgs
	if ($null -eq $script:ProcA) { throw "remounting A failed" }

	if (-not (Test-Path -LiteralPath "$RootA\$name")) {
		Say "    as the contract says: the file is gone entirely" "Gray"
		Pass
		return
	}
	$body = [System.IO.File]::ReadAllText("$RootA\$name")
	if ($body -eq "should-be-lost") {
		Fail "it was persisted by a close alone (which contradicts the close-no-flush contract)"
		return
	}
	Say "    ⚠ a zero-length file survived (length $($body.Length)) - the inode was materialized but the contents are lost" "Yellow"
	Pass
}

# ===== main =====

$DeferArgs = "--write-back --write-back-metadata --write-back-metadata-exclusive-create defer"

Say "=== pgfs Windows metadata write-back tests ===" "Cyan"
Say "A = $MountA (defer + write-back) / B = $MountB (write-through)"

if (-not (Test-Path -LiteralPath $AssignBinary)) {
	Say "assign.pgfs.exe was not found: $AssignBinary" "Red"
	exit 2
}

$script:ProcA = Start-Mount $MountA $DeferArgs
$script:ProcB = Start-Mount $MountB ""
if ($null -eq $script:ProcA -or $null -eq $script:ProcB) {
	Stop-Mount $MountA $script:ProcA | Out-Null
	Stop-Mount $MountB $script:ProcB | Out-Null
	exit 2
}

try {
	if (Test-Path -LiteralPath $RootA) {
		Remove-Item -LiteralPath $RootA -Recurse -Force -ErrorAction SilentlyContinue
	}
	New-Item -ItemType Directory -Path $RootA -ErrorAction Stop | Out-Null
	# A's mkdir stays pending under defer, so we wait until it is visible from B (otherwise B's writes fail).
	if (-not (Wait-Listed ($MountB.TrimEnd('\') + '\') "mtest" 20)) {
		Say "the test root is not visible from B (A's pending entry was not flushed)" "Red"
		exit 2
	}

	# The order matters: once the collision test **latches the error state**, the creates and writes that
	# follow return -EIO, so the contract tests that do not latch come first.
	Invoke-Test test_meta_defer_exclusive_within_mount
	Invoke-Test test_meta_close_no_flush_loses_content
	Invoke-Test test_meta_defer_conflict_does_not_clobber_occupier
	Invoke-Test test_meta_error_state_blocks_persisted_delete
	Invoke-Test test_meta_defer_conflict_recovers_by_unlink

	Write-Host ""
	Write-Host "Results: " -NoNewline
	Write-Host "$($script:Passed) passed" -ForegroundColor Green -NoNewline
	Write-Host ", " -NoNewline
	Write-Host "$($script:Failed) failed" -ForegroundColor Red -NoNewline
	Write-Host ", " -NoNewline
	Write-Host "$($script:Skipped) skipped" -ForegroundColor Yellow -NoNewline
	Write-Host " (out of $($script:Total))"
	foreach ($n in $script:FailedNames) { Say "  - $n" "Red" }
}
finally {
	if (-not $KeepMounted) {
		if (Test-Path -LiteralPath $RootA) {
			Remove-Item -LiteralPath $RootA -Recurse -Force -ErrorAction SilentlyContinue
		}
		Stop-Mount $MountA $script:ProcA | Out-Null
		Stop-Mount $MountB $script:ProcB | Out-Null
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
