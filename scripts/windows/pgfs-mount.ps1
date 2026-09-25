# pgfs-mount.ps1 - start assign.pgfs in the background and wait until the drive appears (shared by the logon task and manual starts).
#
# Usage:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File pgfs-mount.ps1 [-Drive P:] [-Exe <assign.pgfs.exe>] [-ConfigDir <dir>]
#
# Default locations:
#   -Exe       %LOCALAPPDATA%\Programs\pgfs\assign.pgfs.exe
#   -ConfigDir %LOCALAPPDATA%\pgfs   (put pgfs.toml here; assign.pgfs is started with this as its working folder)
#   -Drive     P:                    (keep it the same as [mount] mount_point in pgfs.toml)
#
# Guards against running twice (3 stages; if any of them hits, do nothing and exit 0):
#   1. A named Mutex - limits concurrent runs of this script itself to one (if the task and a double-click overlap, one of them exits at once)
#   2. The drive is already mounted
#   3. assign.pgfs is already running (assign itself also refuses when the mount point is in use, but this stops it earlier)
#      **Assumes one mount per user**. To keep another drive resident as well, pass -SkipProcessGuard.
#
# To stop it: "C:\Program Files\Dokan\Dokan Library-2.x.x\dokanctl.exe" /u P:   (assign.pgfs exits by itself)
# The procedure is in docs/Assign.md, "Keeping it resident from logon".
[CmdletBinding()]
param(
	[string]$Drive = 'P:',
	[string]$Exe = (Join-Path $env:LOCALAPPDATA 'Programs\pgfs\assign.pgfs.exe'),
	[string]$ConfigDir = (Join-Path $env:LOCALAPPDATA 'pgfs'),
	[int]$TimeoutSec = 30,
	# Pass this when the same user keeps another drive resident too (removes guard 3; guard 1 is a Mutex per -Drive and guard 2 looks at the drive, so they stay).
	[switch]$SkipProcessGuard
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Drive = $Drive.TrimEnd('\')
$mutex = New-Object System.Threading.Mutex($false, "Local\pgfs-mount-$($Drive.TrimEnd(':'))")
if (-not $mutex.WaitOne(0)) {
	Write-Host 'pgfs-mount: another instance of this script is running; nothing to do'
	exit 0
}
try {
	if (Test-Path "$Drive\") {
		Write-Host "pgfs-mount: $Drive is already mounted; nothing to do"
		exit 0
	}
	if (-not $SkipProcessGuard -and @(Get-Process -Name 'assign.pgfs' -ErrorAction SilentlyContinue).Count -gt 0) {
		Write-Host 'pgfs-mount: assign.pgfs is already running; nothing to do'
		exit 0
	}
	if (-not (Test-Path $Exe)) { throw "pgfs-mount: not found: $Exe" }
	if (-not (Test-Path (Join-Path $ConfigDir 'pgfs.toml'))) { throw "pgfs-mount: not found: $ConfigDir\pgfs.toml" }

	$p = Start-Process -FilePath $Exe -WorkingDirectory $ConfigDir -WindowStyle Hidden -PassThru
	for ($i = 0; $i -lt $TimeoutSec * 2; $i++) {
		Start-Sleep -Milliseconds 500
		if ($p.HasExited) { throw "pgfs-mount: assign.pgfs exited with code $($p.ExitCode) (see the log configured in pgfs.toml)" }
		if (Test-Path "$Drive\") {
			Write-Host "pgfs-mount: mounted $Drive (assign.pgfs pid $($p.Id))"
			exit 0
		}
	}
	throw "pgfs-mount: $Drive did not appear within ${TimeoutSec}s (assign.pgfs pid $($p.Id) still running)"
} finally {
	$mutex.ReleaseMutex() | Out-Null
	$mutex.Dispose()
}
