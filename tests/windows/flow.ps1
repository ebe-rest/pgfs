# pgfs Windows e2e: full flow
#
# (optional build) -> mount (background) -> e2e tests -> unmount
#
# Usage:
#   .\tests\windows\flow.ps1                       mount + test + unmount
#   .\tests\windows\flow.ps1 pattern               test-name filter (only names containing "pattern")
#   .\tests\windows\flow.ps1 -Build                dotnet publish first, then mount
#   .\tests\windows\flow.ps1 -NoMount              assume already mounted, run tests only
#   .\tests\windows\flow.ps1 -KeepMounted          do not unmount after the tests
#
# Unlike the Linux version ([tests/linux/flow.ps1](../linux/flow.ps1)), the build is *skipped* by
# default. On the development (Windows) side you build from the IDE / dotnet command, so there is
# no need to rebuild every time. Pass -Build to build explicitly.
#
# Prerequisites:
#   - Dokan 2.x is installed (C:\Program Files\Dokan\Dokan Library-*)
#   - bin\Publish\assign.pgfs.exe exists (if not, pass -Build or publish manually)
#   - pgfs.toml exists (holds the DB connection info)
#
# Defaults can be overridden by environment variables (CLI arg > env var > default):
#   MOUNT_ROOT            mount point                       (default P: / shared with run.cmd, "P:\" form also accepted)
#   ASSIGN_BINARY         assign.pgfs.exe full path         (default ${RepoRoot}\bin\Publish\assign.pgfs.exe)
#   ASSIGN_SETTING_FILE   pgfs.toml full path               (default ${RepoRoot}\pgfs.toml)

[CmdletBinding()]
param(
	[Parameter(Position = 0)]
	[string]$Filter = "",

	[string]$MountPoint   = $(if ($env:MOUNT_ROOT)          { $env:MOUNT_ROOT }          else { "P:" }),
	[string]$AssignBinary = $(if ($env:ASSIGN_BINARY)       { $env:ASSIGN_BINARY }       else { "" }),
	[string]$SettingFile  = $(if ($env:ASSIGN_SETTING_FILE) { $env:ASSIGN_SETTING_FILE } else { "" }),

	[switch]$Build,
	[switch]$NoMount,
	[switch]$KeepMounted
)

# Variables:
# - $RepoRoot               repository root
# - $AssignBinary           assign.pgfs.exe full path
# - $SettingFile            pgfs.toml full path
# - $MountPoint             e.g. "P:" (the form passed as a CLI argument)
# - $MountRoot              e.g. "P:\" (the form passed to the test script)
# - $DokanCtl               dokanctl.exe full path ($null if not found)

[System.Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Step($msg) { Write-Host ""; Write-Host "=== $msg ===" -ForegroundColor Cyan }
function Info($msg) { Write-Host "  $msg" -ForegroundColor Gray }
function Ok($msg)   { Write-Host "  $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "  $msg" -ForegroundColor Yellow }
function Err($msg)  { Write-Host "  $msg" -ForegroundColor Red }

$RepoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
Push-Location $RepoRoot
try {

if ([string]::IsNullOrEmpty($AssignBinary)) {
	$AssignBinary = Join-Path $RepoRoot "bin\Publish\assign.pgfs.exe"
}
if ([string]::IsNullOrEmpty($SettingFile)) {
	$SettingFile = Join-Path $RepoRoot "pgfs.toml"
}

# Normalize MountPoint: drop the trailing \ (the form passed as an assign argument).
$MountPoint = $MountPoint.TrimEnd('\')
# MountRoot is the drive-root form ("P:" -> "P:\").
if ($MountPoint.Length -eq 2 -and $MountPoint[1] -eq ':') {
	$MountRoot = "$MountPoint\"
}
else {
	$MountRoot = $MountPoint.TrimEnd('\') + '\'
}

# ===== 1. (optional) build =====

if ($Build) {
	Step "dotnet publish"
	Info "dotnet publish pgfs.sln -c Release"
	dotnet publish (Join-Path $RepoRoot "pgfs.sln") -c Release
	if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }
	Ok "build complete"
}
else {
	Info "skip: build (pass -Build to run dotnet publish)"
}

# ===== check binary / settings file =====

if (-not (Test-Path -LiteralPath $AssignBinary)) {
	Err "assign.pgfs.exe not found: $AssignBinary"
	Err "  pass -Build, or run 'dotnet publish pgfs.sln -c Release' first"
	exit 11
}

if (-not (Test-Path -LiteralPath $SettingFile)) {
	Warn "pgfs.toml not found: $SettingFile"
	Warn "  copy pgfs.toml.example and edit it (continuing anyway -- assign uses its defaults)"
}

# ===== locate dokanctl =====

$DokanCtl = $null
$ctl = Get-Command dokanctl -ErrorAction SilentlyContinue
if ($ctl) {
	$DokanCtl = $ctl.Source
}
else {
	# The Dokan install has dokanctl.exe in both the root (x64) and the x86\ subfolder.
	# The x86 build fails /u with "Admin rights required" without administrator privileges,
	# so prefer the one with the shorter path (the root).
	$cand = Get-ChildItem -Path "C:\Program Files\Dokan" -Filter "dokanctl.exe" -Recurse -ErrorAction SilentlyContinue |
		Sort-Object { $_.FullName.Length } |
		Select-Object -First 1
	if ($cand) {
		$DokanCtl = $cand.FullName
	}
}

# ===== 2. mount =====

$mountProc = $null
$mountStartedHere = $false
$mountLogPath = Join-Path $RepoRoot "tests\windows\mount.log"
$mountErrPath = Join-Path $RepoRoot "tests\windows\mount.err.log"

if ($NoMount) {
	Info "skip: mount (-NoMount, assuming already mounted)"
}
else {
	Step "pgfs.assign"

	if (Test-Path -LiteralPath $MountRoot -PathType Container) {
		Warn "$MountRoot already exists (assuming already mounted, reusing)"
	}
	else {
		# Note: PS 5.1's Start-Process -ArgumentList @(...) does not auto-quote spaces inside array
		# elements, so if the SettingFile path contains spaces the arguments split apart on the
		# assign side. We pass a single command-line string with explicit quotes instead.
		$mountCmd = "-f `"$SettingFile`" -m `"$MountPoint`""
		Info "starting: $AssignBinary $mountCmd"
		$mountProc = Start-Process -FilePath $AssignBinary `
			-ArgumentList $mountCmd `
			-PassThru -NoNewWindow `
			-RedirectStandardOutput $mountLogPath `
			-RedirectStandardError $mountErrPath
		$mountStartedHere = $true
		Info "mount pid=$($mountProc.Id)"

		# Wait up to 15 seconds for P:\ to appear.
		$deadline = (Get-Date).AddSeconds(15)
		$up = $false
		while ((Get-Date) -lt $deadline) {
			Start-Sleep -Milliseconds 500
			if (Test-Path -LiteralPath $MountRoot -PathType Container) {
				$up = $true
				break
			}
			if ($mountProc.HasExited) {
				Err "mount process exited early (exit=$($mountProc.ExitCode))"
				if (Test-Path -LiteralPath $mountErrPath) {
					Write-Host "--- stderr ---" -ForegroundColor Yellow
					Get-Content -LiteralPath $mountErrPath | ForEach-Object { Write-Host $_ }
				}
				if (Test-Path -LiteralPath $mountLogPath) {
					Write-Host "--- stdout ---" -ForegroundColor Yellow
					Get-Content -LiteralPath $mountLogPath | ForEach-Object { Write-Host $_ }
				}
				throw "mount failed to start"
			}
		}
		if (-not $up) {
			throw "mount did not come up within 15s"
		}
		Ok "mounted at $MountRoot"
	}
}

# ===== 3. tests =====

$testExit = 99
try {
	Step "e2e tests"
	& (Join-Path $PSScriptRoot "e2e.ps1") -MountRoot $MountRoot -Filter $Filter
	$testExit = $LASTEXITCODE
}
finally {

	# ===== 4. unmount =====

	if ($mountStartedHere -and -not $KeepMounted) {
		Step "unmount"

		$unmounted = $false
		if ($null -ne $DokanCtl) {
			Info "$DokanCtl /u $MountPoint"
			& $DokanCtl /u $MountPoint | Out-Null
			# dokanctl /u is asynchronous: wait for the process to exit.
			if ($null -ne $mountProc) {
				if (-not $mountProc.WaitForExit(10000)) {
					Warn "mount process did not exit within 10s; Stop-Process"
					try { $mountProc.Kill() } catch {}
				}
				else {
					$unmounted = $true
				}
			}
			else {
				$unmounted = $true
			}
		}

		if (-not $unmounted) {
			Warn "dokanctl was unavailable; terminating with Stop-Process"
			if ($null -ne $mountProc -and -not $mountProc.HasExited) {
				try { $mountProc.Kill() } catch {}
				$mountProc.WaitForExit(5000) | Out-Null
			}
		}

		Ok "unmounted"

		# Show only the mount log size (details stay in mount.log).
		if (Test-Path -LiteralPath $mountLogPath) {
			$lines = (Get-Content -LiteralPath $mountLogPath | Measure-Object).Count
			Info "mount log: $mountLogPath ($lines lines)"
		}
	}
	elseif ($KeepMounted -and $mountStartedHere) {
		Warn "keeping mount up (-KeepMounted). Unmount manually:"
		if ($null -ne $DokanCtl) {
			Warn "  & '$DokanCtl' /u $MountPoint"
		}
		else {
			Warn "  Stop-Process -Id $($mountProc.Id) (dokanctl not found)"
		}
	}
}

# ===== exit =====

Write-Host ""
if ($testExit -ne 0) {
	Err "FAILED (exit $testExit)"
	exit $testExit
}
Ok "ALL PASSED"
exit 0

}
finally {
	Pop-Location
}
