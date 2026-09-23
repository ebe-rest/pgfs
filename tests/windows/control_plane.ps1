# pgfs Windows control-plane tests (pgfsctl config / status)
#
# The Windows acceptance test for the runtime control plane
# ([docs/design/runtime-control-plane.md](../../docs/design/runtime-control-plane.md)).
# It checks that **`pgfsctl config set` takes effect live against a running assign.pgfs** and that
# **`pgfsctl status` reports the effective values**. The counterpart of the Linux side's [tests/docker/control_plane.sh](../docker/control_plane.sh).
#
#   pwsh -NoProfile -File tests\windows\control_plane.ps1
#   pwsh -NoProfile -File tests\windows\control_plane.ps1 -Filter reload
#
# Exit codes: 0 = everything PASSed / 1 = at least one FAIL / 2 = the mount could not be prepared
#
# **It changes settings, so it always puts them back before exiting** (the FS this repository's `pgfs.toml` points at is used for operational testing).
# The application goes through the heartbeat snapshot, so **it may take up to one period (30 seconds by default)**.
#
# Note: the control channel's LISTEN is always ON, so **`config set` reaches a mount with
# `database.notify_enabled = false` too**. This script mounts without notify and checks that path at the same time.

[CmdletBinding()]
param(
	[string]$MountPoint = "P:",
	[string]$Filter = "",
	[int]$ReflectTimeoutSec = 75
)

[System.Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = "Continue"

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$AssignBinary = Join-Path $RepoRoot "bin\Publish\assign.pgfs.exe"
$Pgfsctl = Join-Path $RepoRoot "bin\Publish\pgfsctl.exe"
$SettingFile = Join-Path $RepoRoot "pgfs.toml"
$DokanCtl = "C:\Program Files\Dokan\Dokan Library-2.3.1\dokanctl.exe"

$script:Total = 0
$script:Passed = 0
$script:Failed = 0
$script:Skipped = 0
$script:FailedNames = @()
$script:Current = ""
$script:Proc = $null
$script:Restore = @{}

$MountRoot = $MountPoint.TrimEnd('\') + '\'
$TestRoot = $MountRoot + "cptest"

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

# ===== the pgfsctl wrapper =====

function Invoke-Ctl {
	param([string[]]$CtlArgs)
	$out = & $Pgfsctl @CtlArgs --setting-file $SettingFile 2>&1 | Out-String
	return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out }
}

function Get-StatusJson {
	$r = Invoke-Ctl @('status', '--json')
	if ($r.ExitCode -ne 0) { return $null }
	try { return $r.Output | ConvertFrom-Json } catch { return $null }
}

# The row of the mount we started (live and with a matching pid). Null when it is not found.
function Get-MyRow {
	$j = Get-StatusJson
	if ($null -eq $j -or $null -eq $j.mounts.rows) { return $null }
	return $j.mounts.rows | Where-Object { $_.live -eq $true -and $_.pid -eq $script:Proc.Id } | Select-Object -First 1
}

function Get-EffectiveValue($key) {
	$row = Get-MyRow
	if ($null -eq $row -or $null -eq $row.config) { return $null }
	return $row.config.$key
}

# Wait until the effective value becomes what is expected. **The application goes through the heartbeat snapshot**, so it takes one period (30 seconds by default).
function Wait-EffectiveValue($key, $expected, $seconds = $ReflectTimeoutSec) {
	$deadline = (Get-Date).AddSeconds($seconds)
	while ((Get-Date) -lt $deadline) {
		$v = Get-EffectiveValue $key
		if ("$v" -eq "$expected") { return $true }
		Start-Sleep -Seconds 3
	}
	return $false
}

# Note the current value down before setting it, so that it can be put back later.
function Set-Setting($key, $value) {
	if (-not $script:Restore.ContainsKey($key)) {
		$r = Invoke-Ctl @('config', 'get', $key)
		if ($r.ExitCode -eq 0) {
			# Take just the value out of the first line, which is in the form "scope.key = value  [source, ...]"
			$line = ($r.Output -split "`n" | Where-Object { $_ -match "^\s*$([regex]::Escape($key))\s*=" } | Select-Object -First 1)
			if ($line) { $script:Restore[$key] = (($line -split '=', 2)[1] -split '\[')[0].Trim() }
		}
	}
	return (Invoke-Ctl @('config', 'set', $key, $value))
}

# ===== mount =====

function Start-Mount {
	if (Test-Path -LiteralPath $MountRoot -PathType Container) {
		Say "  $MountRoot is already mounted (this test mounts and remounts on its own)" "Red"
		return $false
	}
	# **notify is deliberately not passed** - the control channel's LISTEN is always ON, so config set still reaches it.
	$proc = Start-Process -FilePath $AssignBinary -ArgumentList "-f `"$SettingFile`" -m `"$MountPoint`"" `
		-PassThru -NoNewWindow -RedirectStandardOutput "$env:TEMP\pgfs-cp-out.log" -RedirectStandardError "$env:TEMP\pgfs-cp-err.log"
	$deadline = (Get-Date).AddSeconds(20)
	while ((Get-Date) -lt $deadline) {
		Start-Sleep -Milliseconds 400
		if (Test-Path -LiteralPath $MountRoot -PathType Container) {
			$script:Proc = $proc
			Say "  mounted $MountRoot (pid $($proc.Id))" "Green"
			return $true
		}
		if ($proc.HasExited) {
			Say "  the mount process exited early (exit=$($proc.ExitCode))" "Red"
			return $false
		}
	}
	Say "  could not mount within 20 seconds" "Red"
	return $false
}

function Stop-Mount {
	if ($null -eq $script:Proc) { return }
	& $DokanCtl /u $MountPoint 2>&1 | Out-Null
	$null = $script:Proc.WaitForExit(60000)
	Say "  unmounted $MountPoint (exit=$($script:Proc.ExitCode))" "Gray"
	$script:Proc = $null
}

# ===== tests =====

# `config list` / `config get` work and return the known keys.
function test_cp_config_list_and_get {
	$list = Invoke-Ctl @('config', 'list')
	if ($list.ExitCode -ne 0) { Fail "config list failed: exit=$($list.ExitCode)"; return }
	foreach ($key in @('mount.write_back', 'audit.enabled', 'mount.negative_cache_ttl_ms')) {
		if ($list.Output -notmatch [regex]::Escape($key)) {
			Fail "$key does not appear in config list"
			return
		}
	}
	$get = Invoke-Ctl @('config', 'get', 'audit.enabled')
	if ($get.ExitCode -ne 0 -or $get.Output -notmatch 'audit\.enabled') {
		Fail "config get audit.enabled failed: exit=$($get.ExitCode)"
		return
	}
	Pass
}

# `status --json` parses, and **our own mount appears as a live row carrying the effective settings**.
function test_cp_status_json_shape {
	$row = $null
	$deadline = (Get-Date).AddSeconds(60)
	while ((Get-Date) -lt $deadline -and $null -eq $row) {
		$row = Get-MyRow
		if ($null -eq $row) { Start-Sleep -Seconds 3 }
	}
	if ($null -eq $row) { Fail "no live row for our own mount (pid $($script:Proc.Id)) in status --json"; return }
	if ($null -eq $row.config) { Fail "the live row carries no effective settings (config)"; return }
	if ($row.mode -ne 'dokan') { Fail "mode is not dokan: $($row.mode)"; return }
	foreach ($key in @('mount.write_back', 'mount.write_back_metadata_exclusive_create')) {
		if ($null -eq $row.config.$key) {
			Fail "$key is missing from the effective settings (BuildConfigJson is written by hand, so one can be forgotten)"
			return
		}
	}
	# The stats B-9 added. Without **the effective mode and the intake-closed flag** being visible, the middle of a flip cannot be observed from operations.
	$wbm = $row.stats.writeBackMetadata
	if ($null -eq $wbm) { Fail "stats carries no writeBackMetadata"; return }
	foreach ($key in @('intakeClosed', 'effective', 'pendingInodes')) {
		if ($null -eq $wbm.$key) {
			Fail "stats.writeBackMetadata is missing $key (B-9)"
			return
		}
	}
	# handle-context ⑥: the number of open handles. **Dokan does not use the table so the value is always 0**,
	# but **if the key disappears the Linux side's leak regression silently becomes meaningless**, so its presence is guarded here.
	$handles = $row.stats.handles
	if ($null -eq $handles) { Fail "stats carries no handles (handle-context ⑥)"; return }
	foreach ($key in @("open", "peak", "inodes")) {
		if ($null -eq $handles.$key) {
			Fail "stats.handles is missing $key (handle-context ⑥)"
			return
		}
	}
	Pass
}

# A `config set` of a Live item **takes effect on the running mount and shows up in status as the effective value**.
# The mount was started without notify, so this checks the path **through the control channel (always LISTENing)**.
function test_cp_live_reload_reflected {
	$key = 'mount.negative_cache_ttl_ms'
	$before = Get-EffectiveValue $key
	$target = if ("$before" -eq "1500") { "2500" } else { "1500" }
	$r = Set-Setting $key $target
	if ($r.ExitCode -ne 0) { Fail "config set failed: exit=$($r.ExitCode) / $($r.Output)"; return }
	if (-not (Wait-EffectiveValue $key $target)) {
		Fail "it does not reach the effective value within $ReflectTimeoutSec seconds (expected $target / currently $(Get-EffectiveValue $key))"
		return
	}
	Pass
}

# Verifying EnumField: a value outside the permitted set is **rejected at set time rather than at start-up** (B-1).
function test_cp_invalid_enum_rejected {
	$key = 'mount.write_back_metadata_exclusive_create'
	$before = Get-EffectiveValue $key
	$r = Invoke-Ctl @('config', 'set', $key, 'bogus')
	if ($r.ExitCode -eq 0) { Fail "a value outside the permitted set was accepted"; return }
	if ($r.Output -notmatch 'write_through' -or $r.Output -notmatch 'defer') {
		Say "    message: $($r.Output.Trim())" "Yellow"
		Fail "the error message does not list the permitted values"
		return
	}
	$after = Get-EffectiveValue $key
	if ("$after" -ne "$before") { Fail "the effective value changed although it was rejected: $before -> $after"; return }
	Pass
}

# Nothing written is lost across data write-back's **live on -> off (the two-phase flip)**.
function test_cp_write_back_live_flip {
	$r = Set-Setting 'mount.write_back' 'true'
	if ($r.ExitCode -ne 0) { Fail "the set of write_back on failed: $($r.Output)"; return }
	if (-not (Wait-EffectiveValue 'mount.write_back' 'True')) {
		Fail "write_back on does not reach the effective value"
		return
	}
	$f = "$TestRoot\flip.bin"
	$payload = [byte[]](1..200)
	[System.IO.File]::WriteAllBytes($f, $payload)
	# From on to off. **It is a two-phase flip, so the contract is that it drains before dropping.**
	$r2 = Set-Setting 'mount.write_back' 'false'
	if ($r2.ExitCode -ne 0) { Fail "the set of write_back off failed: $($r2.Output)"; return }
	if (-not (Wait-EffectiveValue 'mount.write_back' 'False')) {
		Fail "write_back off does not reach the effective value"
		return
	}
	$got = [System.IO.File]::ReadAllBytes($f)
	if ($got.Length -ne $payload.Length) {
		Fail "the length changed across the flip: $($got.Length) (expected $($payload.Length))"
		return
	}
	if ([System.Convert]::ToHexString($got) -ne [System.Convert]::ToHexString($payload)) {
		Fail "the contents changed across the flip"
		return
	}
	Pass
}

# Metadata write-back's **live off (the two-phase flip)** runs to completion.
# The steps follow the Linux side's practice: (1) stop the background flush's time trigger (2) build up a few
# hundred pending entries (3) issue `config set` (4) poll `status --json` at short intervals.
# **Whether the first phase (intake closed) can be caught is timing-dependent**, so it is only recorded when
# observed, and what is asserted is **completion (enabled=false and effective=false and intake reopened and pending 0)**.
# "A pending entry can be created after the flip completes" (B-9 (1)) cannot be forced deterministically from a
# shell, so **no test is written for it** (atomicity is upheld by Core inside the ledger lock. The Linux side made the same call).
function test_cp_metadata_flip_completes {
	$null = Set-Setting 'mount.write_back_interval_ms' '0'
	$null = Set-Setting 'mount.write_back' 'true'
	$r = Set-Setting 'mount.write_back_metadata' 'true'
	if ($r.ExitCode -ne 0) { Fail "the set of metadata on failed: $($r.Output)"; return }
	if (-not (Wait-EffectiveValue 'mount.write_back_metadata' 'True')) {
		Fail "metadata write-back on does not reach the effective value"
		return
	}
	# Build up pending entries (the background flush's time trigger has been stopped).
	$count = 300
	for ($i = 0; $i -lt $count; $i++) {
		[System.IO.File]::WriteAllText("$TestRoot\flip_$i.txt", "x")
	}
	# Issue the flip. `config set` returns without waiting for an ack, so polling can start right after.
	$r2 = Set-Setting 'mount.write_back_metadata' 'false'
	if ($r2.ExitCode -ne 0) { Fail "the set of metadata off failed: $($r2.Output)"; return }
	$sawIntakeClosed = $false
	$done = $false
	$deadline = (Get-Date).AddSeconds(120)
	while ((Get-Date) -lt $deadline) {
		$row = Get-MyRow
		$wbm = if ($null -ne $row) { $row.stats.writeBackMetadata } else { $null }
		if ($null -ne $wbm) {
			if ($wbm.intakeClosed -eq $true) { $sawIntakeClosed = $true }
			if ($wbm.enabled -eq $false -and $wbm.effective -eq $false -and $wbm.intakeClosed -eq $false) {
				$done = $true
				break
			}
		}
		Start-Sleep -Milliseconds 500
	}
	if (-not $done) { Fail "the flip does not complete within 120 seconds (still intake-closed, or the effective value never drops)"; return }
	$row = Get-MyRow
	if ($row.stats.writeBackMetadata.pendingInodes -ne 0) {
		Fail "pending entries survive the completed flip: $($row.stats.writeBackMetadata.pendingInodes)"
		return
	}
	$actual = @(Get-ChildItem -LiteralPath $TestRoot -Filter 'flip_*.txt' -Force).Count
	if ($actual -ne $count) { Fail "files were lost across the flip: $actual (expected $count)"; return }
	Say "    was the first phase (intake closed) observed: $sawIntakeClosed" "Gray"
	Pass
}

# **The number of open bodies appears in status** (handle-context stage C-1).
#
# C-1 **does not change any behaviour**, so this counter is the only thing that can be observed.
# **`CreateFile` has many branches** (open an existing one / open with overwrite / create a new one / a
# directory), so **missing even one leaves the count never coming back, or never going up**. This is that net.
#
# The application goes through the heartbeat snapshot, so **one period (30 seconds by default) is waited out**.
function Get-OpenInodes {
	$row = Get-MyRow
	if ($null -eq $row -or $null -eq $row.stats.handles) { return $null }
	return [int]$row.stats.handles.inodes
}

function Wait-OpenInodes($expected, $seconds = $ReflectTimeoutSec) {
	$deadline = (Get-Date).AddSeconds($seconds)
	$last = $null
	while ((Get-Date) -lt $deadline) {
		$last = Get-OpenInodes
		if ($null -ne $last -and $last -eq $expected) { return $true }
		Start-Sleep -Seconds 3
	}
	Say "  the last inodes seen = $last (expected $expected)" "Yellow"
	return $false
}

function test_cp_open_inodes_counted {
	$dir = Join-Path ($MountPoint.TrimEnd('\') + '\') "cptest"
	if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -ErrorAction Stop | Out-Null }
	# **Judge by the delta from a baseline.** Assuming an absolute 0 makes it fail merely because
	# **handles held by an earlier test or another process are still in the snapshot** (the application goes
	# through the heartbeat, so a value up to one period old can be seen). What matters is **that it goes up and comes back**, and a delta is enough for that.
	$base = $null
	$deadline = (Get-Date).AddSeconds($ReflectTimeoutSec)
	while ((Get-Date) -lt $deadline -and $null -eq $base) {
		$base = Get-OpenInodes
		if ($null -eq $base) { Start-Sleep -Seconds 3 }
	}
	if ($null -eq $base) { Fail "handles.inodes does not appear in status (is our own live row not visible?)"; return }

	$streams = @()
	try {
		foreach ($i in 1..3) {
			$p = Join-Path $dir "oi_$i.txt"
			$streams += [System.IO.File]::Open($p, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::ReadWrite)
		}
		if (-not (Wait-OpenInodes ($base + 3))) { Fail "inodes did not reach $($base + 3) although three are open (a missing count in a CreateFile branch?)"; return }
	}
	finally {
		foreach ($s in $streams) { $s.Dispose() }
	}
	# **It must come back when they are closed.** If it does not, it is being counted somewhere other than CloseFile, or counted twice.
	if (-not (Wait-OpenInodes $base)) { Fail "inodes did not come back to $base although they were closed (is it not being dropped in CloseFile?)"; return }
	Pass
}

# ===== main =====

Say "=== pgfs Windows control-plane tests ===" "Cyan"
Say "mount = $MountPoint / waiting for the application = up to $ReflectTimeoutSec seconds (through the heartbeat snapshot)"

if (-not (Test-Path -LiteralPath $Pgfsctl)) { Say "pgfsctl.exe was not found: $Pgfsctl" "Red"; exit 2 }
if (-not (Start-Mount)) { exit 2 }

try {
	if (Test-Path -LiteralPath $TestRoot) {
		Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
	}
	New-Item -ItemType Directory -Path $TestRoot -ErrorAction Stop | Out-Null

	Invoke-Test test_cp_config_list_and_get
	Invoke-Test test_cp_status_json_shape
	Invoke-Test test_cp_open_inodes_counted
	Invoke-Test test_cp_live_reload_reflected
	Invoke-Test test_cp_invalid_enum_rejected
	Invoke-Test test_cp_write_back_live_flip
	Invoke-Test test_cp_metadata_flip_completes

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
	# **Always put the settings back** (this FS is used for operational testing, and leaving audit off would stop the auditing).
	foreach ($key in $script:Restore.Keys) {
		$v = $script:Restore[$key]
		Say "  restore: $key = $v" "Gray"
		$null = Invoke-Ctl @('config', 'set', $key, $v)
	}
	if (Test-Path -LiteralPath $TestRoot) {
		Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
	}
	Stop-Mount
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
