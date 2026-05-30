# pgfs Linux e2e: full flow
#
# rsync -> publish -> mount (background) -> e2e tests -> unmount
#
# Usage:
#   .\tests\linux\flow.ps1                         full flow
#   .\tests\linux\flow.ps1 xattr                   test-name filter (only names containing "xattr")
#   .\tests\linux\flow.ps1 -NoSync                 skip rsync
#   .\tests\linux\flow.ps1 -NoBuild                skip publish
#   .\tests\linux\flow.ps1 -NoMount                assume already mounted, run tests only
#   .\tests\linux\flow.ps1 -KeepMounted            do not unmount after the tests
#
# Prerequisites:
#   - bash (Git Bash or WSL) is on PATH
#   - passwordless SSH-key login to the target ($Remote, default linux_client)
#
# Defaults can be overridden by environment variables (CLI arg > env var > default):
#   REMOTE                SSH target                       (default linux_client / shared with run.cmd)
#   REMOTE_REPO           remote repository path           (default ~/project/pgfs_cs / shared with run.cmd)
#   MOUNT_POINT           mount point                      (default ~/mnt/pgfs / shared with run.cmd)
#   REMOTE_SETTING_FILE   remote pgfs.toml path            (default ~/pgfs.toml)
#   REMOTE_DOTNET         remote dotnet binary             (default ~/dotnet/10.0.300/dotnet)
#   REMOTE_MOUNT_BINARY   mount.pgfs binary path           (default ${REMOTE_REPO}/bin/Publish/mount.pgfs)

[CmdletBinding()]
param(
	[Parameter(Position = 0)]
	[string]$Filter = "",

	[string]$Remote      = $(if ($env:REMOTE)              { $env:REMOTE }              else { "linux_client" }),
	[string]$RemoteRepo  = $(if ($env:REMOTE_REPO)         { $env:REMOTE_REPO }         else { "~/project/pgfs_cs" }),
	[string]$MountBinary = $(if ($env:REMOTE_MOUNT_BINARY) { $env:REMOTE_MOUNT_BINARY } else { "" }),
	[string]$MountPoint  = $(if ($env:MOUNT_POINT)         { $env:MOUNT_POINT }         else { "~/mnt/pgfs" }),
	[string]$SettingFile = $(if ($env:REMOTE_SETTING_FILE) { $env:REMOTE_SETTING_FILE } else { "~/pgfs.toml" }),
	[string]$DotnetPath  = $(if ($env:REMOTE_DOTNET)       { $env:REMOTE_DOTNET }       else { "~/dotnet/10.0.300/dotnet" }),

	[switch]$NoSync,
	[switch]$NoBuild,
	[switch]$NoMount,
	[switch]$KeepMounted
)

# NOTE: in PowerShell 5.1, setting `$ErrorActionPreference = "Stop"` makes native commands
# (ssh / rsync etc.) throw a NativeCommandError for every stderr line, so even ssh's
# "Permanently added host" warning would abort the script. We therefore keep the default
# (Continue) and explicitly check `$LASTEXITCODE` right after each bash invocation.

# Pin the console to UTF-8 so UTF-8 output coming from the Linux side (mount.pgfs logs etc.)
# is not misinterpreted as Shift-JIS and garbled.
[System.Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if ([string]::IsNullOrEmpty($MountBinary)) {
	$MountBinary = "${RemoteRepo}/bin/Publish/mount.pgfs"
}

# ssh's "Permanently added 'host' to the list of known hosts" warning makes PowerShell emit a
# NativeCommandError, so pass `-o LogLevel=ERROR` on every ssh call to suppress it.
$SshOpt = "-o LogLevel=ERROR"

# ===== output helpers =====

function Step($msg) { Write-Host ""; Write-Host "=== $msg ===" -ForegroundColor Cyan }
function Info($msg) { Write-Host "  $msg" -ForegroundColor Gray }
function Ok($msg)   { Write-Host "  $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "  $msg" -ForegroundColor Yellow }
function Err($msg)  { Write-Host "  $msg" -ForegroundColor Red }

# ===== check that bash exists =====

if (-not (Get-Command bash -ErrorAction SilentlyContinue)) {
	Err "bash not found. Install Git Bash or WSL."
	exit 10
}

# ===== move to the repository root =====

$RepoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
Push-Location $RepoRoot
try {

# ===== 1. rsync =====

if (-not $NoSync) {
	Step "rsync"
	Info "${RepoRoot}\ -> ${Remote}:${RemoteRepo}/"
	# Exclude the Windows-built bin/ obj/ when syncing to the Linux side, otherwise dotnet publish
	# fails with 'already exists'. .git/ is not needed, so excluding it also reduces transfer size.
	bash -c "rsync -e 'ssh $SshOpt' -avz --delete --exclude='bin/' --exclude='obj/' --exclude='.git/' ./ ${Remote}:${RemoteRepo}/"
	if ($LASTEXITCODE -ne 0) { throw "rsync failed (exit $LASTEXITCODE)" }
	Ok "synced"
}
else {
	Info "skip: rsync (-NoSync)"
}

# ===== 2. build =====

if (-not $NoBuild) {
	Step "dotnet publish"
	# Clean leftover obj/ rsynced from another platform (it remains on the first run even with --exclude).
	Info "clean stale src/*/bin src/*/obj"
	bash -c "ssh $SshOpt $Remote rm -rf ${RemoteRepo}/src/lib/bin ${RemoteRepo}/src/lib/obj ${RemoteRepo}/src/mkfs/bin ${RemoteRepo}/src/mkfs/obj ${RemoteRepo}/src/mount/bin ${RemoteRepo}/src/mount/obj ${RemoteRepo}/src/assign/bin ${RemoteRepo}/src/assign/obj"
	# `-p:RestoreDisableParallel=true` works around a symlink race on the remote: when the remote
	# repo path is reached through a symlink, parallel restore tries to write the same
	# obj/*.nuget.g.props from two paths and fails.
	Info "$DotnetPath publish ${RemoteRepo}/pgfs.sln -c Release -p:RestoreDisableParallel=true"
	bash -c "ssh $SshOpt $Remote $DotnetPath publish ${RemoteRepo}/pgfs.sln -c Release -p:RestoreDisableParallel=true"
	if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }
	Ok "build complete"
}
else {
	Info "skip: build (-NoBuild)"
}

# ===== 3. mount =====
#
# mount.pgfs has to keep running in the foreground over ssh, so we background it in a separate
# runspace with PowerShell's Start-Job.
# (With Start-Process, the quoting of -ArgumentList @("-c", "ssh ...") collapses when the command
#  line is generated, splitting bash's -c so ssh exits immediately with a usage message. The call
#  operator passes it correctly, which is why we use Start-Job.)

$mountJob = $null
$mountStartedHere = $false

if ($NoMount) {
	Info "skip: mount (-NoMount, assuming already mounted)"
}
else {
	Step "mount.pgfs"

	# Reuse the mount if it is already up.
	bash -c "ssh $SshOpt $Remote mountpoint -q $MountPoint" 2>$null
	if ($LASTEXITCODE -eq 0) {
		Warn "$MountPoint is already mounted; reusing"
	}
	else {
		# Pass `-f` to keep mount.pgfs in the foreground (i.e. do not daemonize). In the default
		# fstab-compatible mode mount.pgfs detaches the parent right after the mount completes, so
		# Start-Job would exit immediately and no trace SQL would be left in mount.log. For tests/CI
		# we keep it foreground so the log can be captured.
		# `--log-level trace` keeps the SQL trace in mount.log (for diagnosing e2e failures).
		# For CI/manual runs the default (Information) is sufficient.
		$mountCmd = "ssh $SshOpt $Remote $MountBinary --setting-file $SettingFile --mount-point $MountPoint --log-level trace -f"
		Info "starting: $mountCmd"
		$mountJob = Start-Job -ScriptBlock {
			param($cmd)
			bash -c $cmd 2>&1
		} -ArgumentList $mountCmd
		$mountStartedHere = $true
		Info "mount job id=$($mountJob.Id) state=$($mountJob.State)"

		# Wait up to 15 seconds for the mount point to appear.
		$deadline = (Get-Date).AddSeconds(15)
		$startTime = Get-Date
		$up = $false
		while ((Get-Date) -lt $deadline) {
			Start-Sleep -Milliseconds 500

			bash -c "ssh $SshOpt $Remote mountpoint -q $MountPoint" 2>$null
			if ($LASTEXITCODE -eq 0) {
				$up = $true
				break
			}

			# If still not mounted after 2 seconds, suspect the job has died.
			$elapsed = ((Get-Date) - $startTime).TotalSeconds
			if ($elapsed -gt 2 -and $mountJob.State -ne 'Running') {
				Err "mount job exited early (state=$($mountJob.State), elapsed ${elapsed}s)"
				$out = Receive-Job -Job $mountJob -Keep 2>&1
				if ($out) {
					Write-Host "--- mount output ---" -ForegroundColor Yellow
					$out | ForEach-Object { Write-Host $_ }
				}
				throw "mount failed to start"
			}
		}
		if (-not $up) {
			throw "mount did not come up within 15s"
		}
		Ok "mounted at $MountPoint"
	}
}

# ===== 4. tests =====

$testExit = 99
try {
	Step "e2e tests"
	# Errors that the e2e's ln / setfattr etc. write to stderr would be picked up by PowerShell as
	# NativeCommandError, so merge the ssh output into stdout with 2>&1 before passing it to local bash.
	if ([string]::IsNullOrEmpty($Filter)) {
		bash -c "ssh $SshOpt $Remote bash ${RemoteRepo}/tests/linux/e2e.sh $MountPoint 2>&1"
	}
	else {
		Info "filter: $Filter"
		bash -c "ssh $SshOpt $Remote TEST_FILTER=$Filter bash ${RemoteRepo}/tests/linux/e2e.sh $MountPoint 2>&1"
	}
	$testExit = $LASTEXITCODE
}
finally {

	# ===== 5. unmount =====

	if ($mountStartedHere -and -not $KeepMounted) {
		Step "unmount"
		bash -c "ssh $SshOpt $Remote fusermount3 -u $MountPoint"
		if ($LASTEXITCODE -ne 0) {
			Warn "fusermount3 -u returned $LASTEXITCODE"
		}
		else {
			Ok "unmounted"
		}

		# Wait for the mount job to finish.
		if ($mountJob) {
			$mountJob | Wait-Job -Timeout 5 | Out-Null
			if ($mountJob.State -eq 'Running') {
				Warn "mount job did not finish; Stop-Job"
				$mountJob | Stop-Job
			}

			# Save the mount-side output (trace SQL log etc.) to a file.
			# Do not print it to the console so it does not bury the test Pass/Fail results.
			$out = Receive-Job -Job $mountJob 2>&1
			if ($out) {
				$logPath = Join-Path $RepoRoot "tests/linux/mount.log"
				$out | Out-File -FilePath $logPath -Encoding UTF8
				Info "mount log: $logPath ($(($out | Measure-Object).Count) lines)"
			}
			Remove-Job -Job $mountJob -Force
		}
	}
	elseif ($KeepMounted -and $mountStartedHere) {
		Warn "keeping mount up (-KeepMounted). Unmount manually later:"
		Warn "  bash -c `"ssh $Remote fusermount3 -u $MountPoint`""
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
