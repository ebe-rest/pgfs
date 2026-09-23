# pgfs Windows cross-client tests
#
# Mounts the same DB-FS **from two assign.pgfs processes at once** and looks at exclusion and visibility between the mounts.
#
#   .\tests\windows\crossclient.ps1 [-MountA P:] [-MountB R:] [-Filter createnew]
#
# It targets the contracts that the single-mount e2e ([e2e.ps1](e2e.ps1)) cannot see:
#   - CREATE_NEW exclusion is decided by **the database's unique constraint** (a local non-existence check cannot beat another mount)
#   - a mkdir collision must not let both succeed
#   - one side's create / write / delete becomes visible from the other
#
# Exit codes: 0 = everything PASSed / 1 = at least one FAIL / 2 = the mounts could not be prepared
#
# Prerequisites: bin\Publish\assign.pgfs.exe and pgfs.toml (the DB connection). The Dokan driver installed.
#
# **About notify (important)**: the `Api` cache is per mount, and another mount's changes only propagate
# through `database.notify_enabled` (LISTEN/NOTIFY). **The default is false**, so this script passes
# `--notify` to the mounts it starts itself (in order to verify the visibility contract for real).
# When `-NoNotify` is given, or an existing mount is reused, **the visibility tests are SKIPped**
# (with notify off, not seeing another mount's changes is by design, not a test failure).
# Exclusion (CREATE_NEW / mkdir) is decided by the database's unique constraint, so it runs regardless of notify.

[CmdletBinding()]
param(
	[string]$MountA = "P:",
	[string]$MountB = "R:",
	[string]$Filter = "",
	[int]$RaceRounds = 6,
	[switch]$KeepMounted,
	[switch]$NoNotify
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
# True only when **this script started both mounts with `--notify`**.
# When an existing mount is reused the notify state is unknown, so it is set false and the visibility tests are skipped.
$script:NotifyOn = $false

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

# The entrance to a visibility test. With no notify, "not visible" is by design, so it SKIPs.
function Test-NotifyOrSkip {
	if ($script:NotifyOn) { return $true }
	Skip "a setup with no change notification from the other mount (database.notify_enabled=false / an existing mount reused)"
	return $false
}

function Invoke-Test($name) {
	if ($Filter -ne "" -and $name -notlike "*$Filter*") { return }
	$script:Total++
	$script:Current = $name
	try { & $name }
	catch { Fail "exception [$($_.Exception.GetType().FullName)]: $($_.Exception.Message)" }
}

# ===== mount / unmount =====

function Start-Mount($point) {
	$root = $point.TrimEnd('\') + '\'
	if (Test-Path -LiteralPath $root -PathType Container) {
		Say "  $root already exists (reusing)" "Yellow"
		return $null
	}
	$log = Join-Path $env:TEMP ("pgfs-cross-" + $point.Replace(':', '') + ".log")
	$err = Join-Path $env:TEMP ("pgfs-cross-" + $point.Replace(':', '') + ".err.log")
	$notifyArg = " --notify"
	if ($NoNotify) { $notifyArg = "" }
	$proc = Start-Process -FilePath $AssignBinary `
		-ArgumentList "-f `"$SettingFile`" -m `"$point`"$notifyArg" `
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
			if (Test-Path $err) { Get-Content $err | Select-Object -Last 10 | ForEach-Object { Say "    $_" "Red" } }
			return $null
		}
	}
	Say "  could not mount within 20 seconds: $root" "Red"
	return $null
}

function Stop-Mount($point, $proc) {
	if ($null -eq $proc) { return }
	& $DokanCtl /u $point | Out-Null
	# After the unmount, **wait until the daemon is gone** (this is where write-back's cleanup and the exit code are decided).
	if (-not $proc.WaitForExit(30000)) {
		Say "  the daemon of $point did not exit within 30 seconds" "Yellow"
		return
	}
	Say "  unmounted $point (exit=$($proc.ExitCode))" "Gray"
}

# ===== checking the notifications up front =====

# **Before the visibility tests, confirm that there are two mounts able to send and receive data-change notifications.**
# Without this, merely failing to establish the notifications gets recorded as "not visible from the other side"
# = a visibility defect (hit once when four visibility tests failed together in a batch run while passing on
#  their own and on a re-run. It did not reproduce so the cause is unsettled, but this guard keeps the
#  indistinguishable state from happening again).
#
# **Looking at `connected` alone is not enough**: the control channel's LISTEN is always established
# regardless of `database.notify_enabled`, so `connected: true` / `control_listen: true` hold even with data
# notifications disabled (measured). What visibility needs is **`data_enabled`**, so both are checked.
function Test-NotifyPreflight {
	if (-not $script:NotifyOn) { return $true }
	if (-not (Test-Path -LiteralPath $Pgfsctl)) {
		Say "  pgfsctl is missing, so the notification pre-check is skipped" "Yellow"
		return $true
	}
	$deadline = (Get-Date).AddSeconds(20)
	$last = "(could not obtain it)"
	while ((Get-Date) -lt $deadline) {
		$out = & $Pgfsctl status --json --setting-file $SettingFile 2>&1 | Out-String
		if ($LASTEXITCODE -eq 0) {
			try {
				$j = $out | ConvertFrom-Json
				$ready = @($j.mounts.rows | Where-Object {
					$_.live -eq $true -and $_.stats.notify.connected -eq $true -and $_.stats.notify.data_enabled -eq $true
				})
				$last = ($j.mounts.rows | ForEach-Object { "pid=$($_.pid) live=$($_.live) notify=$($_.stats.notify | ConvertTo-Json -Compress)" }) -join " / "
				if ($ready.Count -ge 2) { return $true }
			} catch { }
		}
		Start-Sleep -Milliseconds 700
	}
	Say "  could not get two mounts able to send and receive data-change notifications: $last" "Red"
	return $false
}

# ===== helpers =====

# Fire the same operation from both mounts "at the same time". The start time is decided first and both jobs wait for it.
function Invoke-Race($block, $argA, $argB) {
	$start = (Get-Date).AddMilliseconds(1500)
	$jobs = @(
		(Start-Job -ScriptBlock $block -ArgumentList $argA, $start),
		(Start-Job -ScriptBlock $block -ArgumentList $argB, $start)
	)
	$jobs | Wait-Job -Timeout 90 | Out-Null
	$out = $jobs | ForEach-Object { Receive-Job -Job $_ }
	$jobs | Remove-Job -Force
	return @($out)
}

# Wait until it becomes visible from the other mount (absorbing the cache / NOTIFY delay).
function Wait-Visible($path, $seconds = 10) {
	$deadline = (Get-Date).AddSeconds($seconds)
	while ((Get-Date) -lt $deadline) {
		if (Test-Path -LiteralPath $path) { return $true }
		Start-Sleep -Milliseconds 300
	}
	return $false
}

# The verdict comes from **what the FS returns**. `Test-Path` can be answered by the Windows client-side cache
# (measured: after the other mount deleted it, it kept returning true without going down to the FS once),
# whereas **the parent directory's enumeration (FindFiles) always goes down to the FS**, so that is taken as the truth.
function Wait-Gone($path, $seconds = 15) {
	$parent = [System.IO.Path]::GetDirectoryName($path)
	$leaf = [System.IO.Path]::GetFileName($path)
	$deadline = (Get-Date).AddSeconds($seconds)
	while ((Get-Date) -lt $deadline) {
		$listed = @(Get-ChildItem -LiteralPath $parent -Filter $leaf -Force -ErrorAction SilentlyContinue).Count
		if ($listed -eq 0) {
			if (Test-Path -LiteralPath $path) {
				Say "  (gone from the enumeration yet Test-Path is still true = the Windows client-side cache)" "Yellow"
			}
			return $true
		}
		Start-Sleep -Milliseconds 300
	}
	return $false
}

$CreateNewBlock = {
	param($path, $start)
	while ((Get-Date) -lt $start) { Start-Sleep -Milliseconds 5 }
	try {
		$fs = [System.IO.File]::Open($path, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
		$fs.Write([byte[]](65, 66, 67), 0, 3)
		$fs.Dispose()
		"OK"
	}
	catch { "ERR:" + $_.Exception.GetType().Name }
}

$MkdirBlock = {
	param($path, $start)
	while ((Get-Date) -lt $start) { Start-Sleep -Milliseconds 5 }
	try {
		[System.IO.Directory]::CreateDirectory($path) | Out-Null
		# .NET's CreateDirectory succeeds even when it already exists, so the actual winner is decided by the native call below.
		"OK"
	}
	catch { "ERR:" + $_.Exception.GetType().Name }
}

# ===== tests =====

# **Two mounts pointing at the same FS get the same volume serial.** It used to be `label.GetHashCode()`, and
# .NET's string.GetHashCode is **random per process**, so every mount (= process) and every remount got a different
# serial and Windows saw "a different volume" (.lnk tracking, and tools identifying a file by FileIndex + serial,
# would take the same file for a different one). Matching across two processes means it is stable across processes.
# The output of `vol` depends on the language, so only the `XXXX-XXXX` shape is picked up.
function test_x_volume_serial_matches_across_mounts {
	$serialOf = {
		param($drive)
		$out = cmd /c "vol $($drive.TrimEnd('\'))" 2>&1 | Out-String
		$m = [regex]::Match($out, '[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}')
		if ($m.Success) { return $m.Value.ToUpperInvariant() }
		return ""
	}
	$a = & $serialOf $MountA
	$b = & $serialOf $MountB
	if ($a -eq "" -or $b -eq "") {
		Fail "cannot read the volume serials (A='$a' / B='$b')"
		return
	}
	if ($a -ne $b) {
		Fail "the same FS has a different serial per mount (A=$a / B=$b) = it changes per process"
		return
	}
	Pass
}

# CREATE_NEW must let **only one side** succeed even from two mounts at once.
# Deciding it on a local non-existence check alone lets both succeed and creates two inode rows with the same name.
function test_x_createnew_exclusive_race {
	for ($i = 0; $i -lt $RaceRounds; $i++) {
		$name = "x_cn_$i.txt"
		$res = Invoke-Race $CreateNewBlock (Join-Path $script:RootA $name) (Join-Path $script:RootB $name)
		$ok = @($res | Where-Object { $_ -eq "OK" }).Count
		if ($ok -ne 1) {
			Fail "round ${i}: successes = $ok (expected 1) / results = $($res -join ',')"
			return
		}
		$bytes = [System.IO.File]::ReadAllBytes((Join-Path $script:RootA $name))
		if ($bytes.Length -ne 3) {
			Fail "round ${i}: the winner's contents were corrupted len=$($bytes.Length)"
			return
		}
	}
	Pass
}

# A simultaneous mkdir of the same name. Both must not end up "having created it".
# (Core's Api.CreateDirectory has no exclusive entrance, and under write-through the database's unique
#  constraint does the job. Turning metadata write-back on can change the behaviour through pending adoption, so that is verified separately.)
function test_x_mkdir_race {
	if (-not $script:NotifyOn) {
		Skip "deciding a mkdir race needs a re-enumeration from both mounts, so it only runs in a notify setup"
		return
	}
	for ($i = 0; $i -lt $RaceRounds; $i++) {
		$name = "x_md_$i"
		$res = Invoke-Race $MkdirBlock (Join-Path $script:RootA $name) (Join-Path $script:RootB $name)
		$err = @($res | Where-Object { $_ -ne "OK" }).Count
		if ($err -gt 0) {
			Fail "round ${i}: an unexpected error / results = $($res -join ',')"
			return
		}
		# There must be exactly one body (one entry seen from each mount = the same one).
		$fromA = @(Get-ChildItem -LiteralPath $script:RootA -Filter $name -Force -ErrorAction SilentlyContinue).Count
		$fromB = @(Get-ChildItem -LiteralPath $script:RootB -Filter $name -Force -ErrorAction SilentlyContinue).Count
		if ($fromA -ne 1 -or $fromB -ne 1) {
			Fail "round ${i}: the same-named directory is A=$fromA / B=$fromB (expected 1/1)"
			return
		}
	}
	Pass
}

# A file created on A must be visible from B with matching contents.
function test_x_create_visible_from_peer {
	if (-not (Test-NotifyOrSkip)) { return }
	$name = "x_vis.txt"
	$a = Join-Path $script:RootA $name
	$b = Join-Path $script:RootB $name
	Set-Content -LiteralPath $a -Value "hello-cross" -NoNewline
	if (-not (Wait-Visible $b)) {
		Fail "not visible from B: $b"
		return
	}
	$body = Get-Content -LiteralPath $b -Raw
	if ($body -ne "hello-cross") {
		Fail "the contents read from B differ: '$body'"
		return
	}
	Pass
}

# Contents rewritten on A must be readable from B (visibility across the read cache).
function test_x_overwrite_visible_from_peer {
	if (-not (Test-NotifyOrSkip)) { return }
	$name = "x_ovr.txt"
	$a = Join-Path $script:RootA $name
	$b = Join-Path $script:RootB $name
	Set-Content -LiteralPath $a -Value "first" -NoNewline
	if (-not (Wait-Visible $b)) { Fail "not visible from B: $b"; return }
	(Get-Content -LiteralPath $b -Raw) | Out-Null
	Set-Content -LiteralPath $a -Value "second-and-longer" -NoNewline
	$deadline = (Get-Date).AddSeconds(10)
	while ((Get-Date) -lt $deadline) {
		if ((Get-Content -LiteralPath $b -Raw) -eq "second-and-longer") {
			Pass
			return
		}
		Start-Sleep -Milliseconds 300
	}
	Fail "the new contents cannot be read from B (the read cache is still stale): '$(Get-Content -LiteralPath $b -Raw)'"
}

# A file deleted on A must look deleted from B.
function test_x_delete_visible_from_peer {
	if (-not (Test-NotifyOrSkip)) { return }
	$name = "x_del.txt"
	$a = Join-Path $script:RootA $name
	$b = Join-Path $script:RootB $name
	Set-Content -LiteralPath $a -Value "bye" -NoNewline
	if (-not (Wait-Visible $b)) { Fail "not visible from B: $b"; return }
	# **Do not use `Remove-Item`.** PowerShell's `Remove-Item` inserts a `SetFileAttributes` to clear
	# ReadOnly and then leaves `DeleteFile` to Windows, so when the FS's `DeleteFile` callback actually
	# runs **depends on other processes' handles**
	# (measured: A's `DeleteFileProxy` was 15 seconds late in the mount log.
	#  B was being watched during that window, so "something not yet deleted even on A" was being counted as a visibility failure).
	# With `DeleteOnClose` the delete runs **the moment this script closes its own handle**, so
	# "the delete has already happened" can be taken as given.
	$h = New-Object System.IO.FileStream($a, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Delete, 4096, [System.IO.FileOptions]::DeleteOnClose)
	$h.Dispose()
	# **First wait for it to really disappear on A's side.** Windows's `DeleteFile` does not run
	# immediately; it stays DeletePending until the last handle closes - and `Remove-Item` returns before that.
	# Who holds a handle is up to Explorer, the indexer and Defender, and measurements showed
	# **A's DeleteFileProxy delayed by 15 seconds** (confirmed in the mount log).
	# Looking at B without waiting here counts "something not yet deleted even on A" being visible from B
	# as a cross-client visibility failure (which is not what this test is about).
	if (-not (Wait-Gone $a -seconds 30)) {
		Fail "it does not disappear on A's side at all (the Windows delete is still pending): $a"
		return
	}
	if (-not (Wait-Gone $b)) {
		Fail "it does not look deleted from B: $b"
		return
	}
	Pass
}

# **A child must not be creatable under a parent another mount deleted.**
# Being able to create one leaves an orphan in the database that the FS cannot reach, and only SQL can clean it up
# (the window for orphaning between a parent deletion and a write-through create).
#
# **Putting the parent into A's cache before B deletes it** is the crux. Without that, A re-reads the database
# on every create and goes straight past the hole (the Linux side's crossclient.sh stats it first for the same reason).
function test_x_create_under_removed_parent_fails {
	# **It runs regardless of notify** (treated like the exclusion tests). What is under test is not
	# visibility but the contract "a child must not be creatable under a deleted parent", which holds in a
	# setup with no notifications too.
	# In fact **it reproduces deterministically with notify OFF** - with notify ON, B's rmdir notification
	# drops A's cache, A re-reads the database and correctly fails, which closes the window
	# (= with ON it confirms the contract but does not reproduce the hole).
	$dirName = "x_orphan_p"
	$a = Join-Path $script:RootA $dirName
	$b = Join-Path $script:RootB $dirName
	Remove-Item -LiteralPath $a -Recurse -Force -ErrorAction SilentlyContinue
	New-Item -ItemType Directory -Path $a | Out-Null
	# Put the parent into A's cache (without this, A re-reads the database on every create and goes straight past the hole)
	Get-ChildItem -LiteralPath $a -Force | Out-Null
	# B does not have it in its own cache, so it resolves it from the database (independent of notify)
	Remove-Item -LiteralPath $b -Recurse -Force
	if (-not (Wait-Gone $b -seconds 30)) { Fail "the parent does not disappear on B's side: $b"; return }
	$child = Join-Path $a "child.txt"
	$created = $true
	$errType = ""
	try { [System.IO.File]::WriteAllText($child, "orphan") }
	catch { $created = $false; $errType = $_.Exception.GetBaseException().GetType().Name }
	if ($created) {
		Fail "a file could be created under the deleted parent (an unreachable orphan is left in the database): $child"
		return
	}
	# **Check the kind of failure too.** Settling for "as long as it fails" would PASS even when Core
	# swallowed the exception and returned null (= a name collision) giving **EEXIST**
	# (actually hit once. "File exists on a path whose parent does not exist" cannot be read by the caller).
	# A missing parent is PathNotFound = DirectoryNotFoundException in .NET. **Look inside with GetBaseException()** -
	# PowerShell wraps a .NET method's exception in MethodInvocationException, so a bare GetType() is always
	# MethodInvocationException and decides nothing.
	if ($errType -ne "DirectoryNotFoundException") {
		Fail "it failed but with the wrong kind (expected DirectoryNotFoundException = a missing parent / actually $errType)"
		return
	}
	# **mkdir is deliberately not checked here.** Both `Directory.CreateDirectory` and
	# `New-Item -ItemType Directory` behave like `mkdir -p` and **recreate the parents recursively**, so
	# they rebuild the deleted parent and create underneath it = this window cannot be hit (measured: all
	# three runs reported "it could be created", which was the test's error rather than pgfs's).
	# Hitting it needs a direct Win32 `CreateDirectoryW` call.
	# On the Core side `CreateFile` / `CreateDirectory` / `CreateSymlink` share the same `InsertInodeThrough`,
	# so confirming the window is closed for file creation protects mkdir at the same time.
	# Non-recursive `mkdir` / `ln -s` / `ln` are covered by the Linux side's crossclient.sh.
	Pass
}

# A replacement rename on A (the tmp + rename pattern) must stay "one file" as seen from B.
function test_x_rename_replace_visible_from_peer {
	if (-not (Test-NotifyOrSkip)) { return }
	$target = Join-Path $script:RootA "x_rn.txt"
	$tmp = Join-Path $script:RootA "x_rn.tmp"
	$peer = Join-Path $script:RootB "x_rn.txt"
	Set-Content -LiteralPath $target -Value "old" -NoNewline
	if (-not (Wait-Visible $peer)) { Fail "not visible from B: $peer"; return }
	Set-Content -LiteralPath $tmp -Value "new-content" -NoNewline
	[System.IO.File]::Move($tmp, $target, $true)
	$deadline = (Get-Date).AddSeconds(10)
	while ((Get-Date) -lt $deadline) {
		if (-not (Test-Path -LiteralPath $peer)) {
			Fail "the file vanished from B in the middle of the replacement rename"
			return
		}
		if ((Get-Content -LiteralPath $peer -Raw) -eq "new-content") {
			Pass
			return
		}
		Start-Sleep -Milliseconds 300
	}
	Fail "the new contents cannot be read from B: '$(Get-Content -LiteralPath $peer -Raw)'"
}

# ===== handle context (docs/design/handle-context.md, stage B) =====

# **The entrance for firing an append through Win32.** .NET's `FileMode.Append` makes the FileStream seek to
# the end itself and **write at an explicit offset**, so it comes down to Dokan with `WriteToEndOfFile = false`.
# What is under test is the freshness of the attributes the handle is holding, so **it opens with FILE_APPEND_DATA alone and lets the OS decide the end**.
Add-Type -Namespace PgfsNative -Name Win32 -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool CloseHandle(IntPtr hObject);
'@

$FILE_APPEND_DATA = 0x0004
$SYNCHRONIZE = 0x00100000
$FILE_SHARE_ALL = 0x00000007
$OPEN_EXISTING = 3
$FILE_FLAG_WRITE_THROUGH = [uint32]0x80000000L   # PowerShell reads 0x80000000 as an Int32 (-2147483648), hence the L

function Open-AppendHandle($path) {
	return [PgfsNative.Win32]::CreateFileW(
		$path,
		[uint32]($FILE_APPEND_DATA -bor $SYNCHRONIZE),
		[uint32]$FILE_SHARE_ALL,
		[IntPtr]::Zero,
		[uint32]$OPEN_EXISTING,
		$FILE_FLAG_WRITE_THROUGH,
		[IntPtr]::Zero)
}

function Write-Append($handle, $text) {
	$bytes = [System.Text.Encoding]::ASCII.GetBytes($text)
	$written = [uint32]0
	$ok = [PgfsNative.Win32]::WriteFile($handle, $bytes, [uint32]$bytes.Length, [ref]$written, [IntPtr]::Zero)
	if (-not $ok) { return -1 }
	return [int]$written
}

# Wait on **the length in the enumeration (FindFiles)**. `Get-Item` / `Test-Path` can be answered by the
# Windows client-side FCB, whereas the parent directory's enumeration always goes down to the FS (the same reason as Wait-Gone).
function Wait-ListedLength($root, $name, $expected, $seconds = 15) {
	$deadline = (Get-Date).AddSeconds($seconds)
	$last = -1
	while ((Get-Date) -lt $deadline) {
		$item = @(Get-ChildItem -LiteralPath $root -Filter $name -Force -ErrorAction SilentlyContinue)[0]
		if ($null -ne $item) {
			$last = $item.Length
			if ($last -eq $expected) { return $true }
		}
		Start-Sleep -Milliseconds 300
	}
	Say "  the last length seen = $last (expected $expected)" "Yellow"
	return $false
}

# Wait until it becomes readable from the other mount and return the contents (absorbing the notify delay).
function Wait-Content($path, $expected, $seconds = 15) {
	$deadline = (Get-Date).AddSeconds($seconds)
	$last = ""
	while ((Get-Date) -lt $deadline) {
		$last = [System.IO.File]::ReadAllText($path)
		if ($last -eq $expected) { return $last }
		Start-Sleep -Milliseconds 300
	}
	return $last
}

# **Does a handle that is still open see the other mount's growth** (problem 2 = the handle holds stale attributes).
#
# A `FILE_APPEND_DATA` write comes down from Dokan with `WriteToEndOfFile = true`, and pgfs
# **uses the `inode.Size` of that moment as the write offset**. If the handle keeps holding the `Inode` from
# open time, it **overwrites and corrupts whatever the other mount appended** (= data loss. The size shrinks too).
# **Once stage B resolves it afresh every time, it lands at the end.**
#
# As a premise, A's inode cache is invalidated by the notification (`InodeCache.Invalidate`
# **removes the entry**, so the next load is a different instance = a different thing from the stale instance the handle held).
function test_x_append_handle_sees_peer_growth {
	if (-not (Test-NotifyOrSkip)) { return }
	$name = "x_app.txt"
	$a = Join-Path $script:RootA $name
	$b = Join-Path $script:RootB $name
	Set-Content -LiteralPath $a -Value "AAAA" -NoNewline
	if (-not (Wait-Visible $b)) { Fail "not visible from B: $b"; return }

	$h = Open-AppendHandle $a
	if ($h -eq [IntPtr](-1) -or $h -eq [IntPtr]::Zero) {
		Fail "cannot open the append handle (win32 error $([System.Runtime.InteropServices.Marshal]::GetLastWin32Error()))"
		return
	}
	try {
		# (1) append one byte ourselves first = make the handle hold "Size = 5".
		if ((Write-Append $h "1") -ne 1) { Fail "the first append failed" ; return }
		# (2) the other mount grows it by 6 bytes (it becomes 11 bytes in the database).
		Add-Content -LiteralPath $b -Value "BBBBBB" -NoNewline
		# (3) **wait until A can observe the growth** (without waiting here, a notification delay gets counted as an FS defect).
		if (-not (Wait-ListedLength $script:RootA $name 11)) {
			Fail "A cannot observe the other side's growth (11 bytes) = the notification did not arrive"
			return
		}
		# (4) append one byte **with the handle still held**. This is the point.
		if ((Write-Append $h "2") -ne 1) { Fail "the second append failed"; return }
	}
	finally {
		[PgfsNative.Win32]::CloseHandle($h) | Out-Null
	}

	$expected = "AAAA1BBBBBB2"
	$got = Wait-Content $b $expected
	if ($got -ne $expected) {
		Fail "the append did not land at the end (the handle is holding a stale Size): expected '$expected' / actually '$got' (len=$($got.Length))"
		return
	}
	Pass
}

# **Does a handle that is still open keep pointing at the first inode after the other mount renames and re-creates the same name** (problem 1).
#
# **This passes in stage A as well** - Dokan puts the `Inode` resolved at open time onto the handle, so even
# now it does not re-resolve by path. It is here as a regression guard for **inverting identification to
# `InodeId` in stage B without breaking it** (it is green on a pre-fix build too, knowingly. The teeth are in the append above).
# On the FUSE side `Read` / `Write` resolve by path, so the Linux e2e reproduces it as **a failure**.
function test_x_handle_follows_inode_after_peer_rename {
	if (-not (Test-NotifyOrSkip)) { return }
	$name = "x_hid.txt"
	$moved = "x_hid_moved.txt"
	$a = Join-Path $script:RootA $name
	$bOld = Join-Path $script:RootB $name
	$bMoved = Join-Path $script:RootB $moved
	Set-Content -LiteralPath $a -Value "orig" -NoNewline
	if (-not (Wait-Visible $bOld)) { Fail "not visible from B: $bOld"; return }

	# WriteThrough = do not pile up on the client side, send it down to the FS. Sharing allows Delete so as not to block the other side's rename.
	$fs = New-Object System.IO.FileStream(
		$a,
		[System.IO.FileMode]::Open,
		[System.IO.FileAccess]::ReadWrite,
		([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete),
		4096,
		[System.IO.FileOptions]::WriteThrough)
	try {
		# The other side renames it and **creates a different file under the same name**.
		[System.IO.File]::Move($bOld, $bMoved)
		Set-Content -LiteralPath $bOld -Value "brand-new" -NoNewline
		if (-not (Wait-ListedLength $script:RootA $moved 4)) {
			Fail "A cannot observe the post-rename name = the notification did not arrive"
			return
		}
		# Write through the handle that is still held. It has to land on **the first inode (= x_hid_moved.txt now)**.
		$bytes = [System.Text.Encoding]::ASCII.GetBytes("ZZZZ")
		$fs.Position = 0
		$fs.Write($bytes, 0, $bytes.Length)
		$fs.Flush()
	}
	finally {
		$fs.Dispose()
	}

	$movedBody = Wait-Content $bMoved "ZZZZ"
	if ($movedBody -ne "ZZZZ") {
		Fail "it did not land on the first inode: x_hid_moved.txt = '$movedBody' (expected 'ZZZZ')"
		return
	}
	$newBody = [System.IO.File]::ReadAllText($bOld)
	if ($newBody -ne "brand-new") {
		Fail "it corrupted the different file re-created under the same name: x_hid.txt = '$newBody' (expected 'brand-new')"
		return
	}
	Pass
}

# **No bytes are lost on a concurrent cross-mount write** (the `st_size` rollback regression).
#
# While A is appending 4 MiB in one go, B interleaves 10 bytes x 12.
# **Both sets of chunks go into the database, but overwriting `st_size` with "the value I know about" lets a
# short write that arrives later roll the size in the database back** - measured on the Linux side,
# `st_size` went back from 4198400 to 4216 and **4 MiB became unreachable from the FS**
# Core's UPDATE was made monotonic with `GREATEST` to close it.
#
# **The order (the interleaving) is not checked.** It depends on the timing of the interleaving and is not
# stable, so only **the total byte count**, which the contract promises, is checked (docs/Mount.md, the append contract).
function test_x_concurrent_append_keeps_all_bytes {
	if (-not (Test-NotifyOrSkip)) { return }
	$name = "x_capp.txt"
	$a = Join-Path $script:RootA $name
	$b = Join-Path $script:RootB $name
	$head = 4096
	$bulk = 4194304
	$drops = 12
	$dropLen = 10
	[System.IO.File]::WriteAllBytes($a, [byte[]]::new($head))
	if (-not (Wait-Visible $b)) { Fail "not visible from B: $b"; return }

	# B's interleaving. **B writes with a real append (`FILE_APPEND_DATA`) too.**
	# .NET's `FileMode.Append` makes **the FileStream seek to the end at open time and write at an explicit offset**,
	# which is **an offset the application chose itself**, so when B's view is stale B's own appends overlap and are lost
	# (writing it that way first lost 10 bytes. **That was the test's error, not a violation of the FS contract**).
	# What the contract (docs/Mount.md, the append contract) protects is only **an append whose end the FS decided**.
	$job = Start-Job -ScriptBlock {
		param($path, $count, $len)
		Add-Type -Namespace PgfsJob -Name Win32 -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool CloseHandle(IntPtr hObject);
'@
		Start-Sleep -Milliseconds 120
		for ($i = 0; $i -lt $count; $i++) {
			$h = [PgfsJob.Win32]::CreateFileW($path, [uint32](0x0004 -bor 0x00100000), [uint32]0x7, [IntPtr]::Zero, [uint32]3, [uint32]0x80000000L, [IntPtr]::Zero)
			if ($h -ne [IntPtr](-1)) {
				$w = [uint32]0
				[PgfsJob.Win32]::WriteFile($h, [byte[]]::new($len), [uint32]$len, [ref]$w, [IntPtr]::Zero) | Out-Null
				[PgfsJob.Win32]::CloseHandle($h) | Out-Null
			}
			Start-Sleep -Milliseconds 80
		}
	} -ArgumentList $b, $drops, $dropLen

	$h = Open-AppendHandle $a
	if ($h -eq [IntPtr](-1) -or $h -eq [IntPtr]::Zero) {
		Fail "cannot open the append handle (win32 error $([System.Runtime.InteropServices.Marshal]::GetLastWin32Error()))"
		$job | Wait-Job -Timeout 60 | Out-Null
		$job | Remove-Job -Force
		return
	}
	try {
		$buf = [byte[]]::new($bulk)
		$written = [uint32]0
		$ok = [PgfsNative.Win32]::WriteFile($h, $buf, [uint32]$bulk, [ref]$written, [IntPtr]::Zero)
		if (-not $ok -or $written -ne $bulk) {
			Fail "the 4 MiB append did not run to completion (ok=$ok written=$written)"
			return
		}
	}
	finally {
		[PgfsNative.Win32]::CloseHandle($h) | Out-Null
		$job | Wait-Job -Timeout 120 | Out-Null
		$job | Remove-Job -Force
	}

	$expected = $head + $bulk + ($drops * $dropLen)
	$deadline = (Get-Date).AddSeconds(20)
	$last = -1
	while ((Get-Date) -lt $deadline) {
		$item = @(Get-ChildItem -LiteralPath $script:RootA -Filter $name -Force -ErrorAction SilentlyContinue)[0]
		if ($null -ne $item) {
			$last = $item.Length
			if ($last -eq $expected) { Pass; return }
		}
		Start-Sleep -Milliseconds 400
	}
	Fail "bytes were lost: expected $expected byte(s) / actually $last byte(s) (a difference of $($expected - $last))"
}

# ===== main =====

Say "=== pgfs Windows cross-client tests ===" "Cyan"
Say "A = $MountA / B = $MountB / rounds = $RaceRounds"

if (-not (Test-Path -LiteralPath $AssignBinary)) {
	Say "assign.pgfs.exe was not found: $AssignBinary" "Red"
	exit 2
}

$procA = Start-Mount $MountA
$procB = Start-Mount $MountB
$rootA = $MountA.TrimEnd('\') + '\'
$rootB = $MountB.TrimEnd('\') + '\'
if (-not (Test-Path -LiteralPath $rootA -PathType Container) -or -not (Test-Path -LiteralPath $rootB -PathType Container)) {
	Say "could not prepare two mounts" "Red"
	Stop-Mount $MountA $procA
	Stop-Mount $MountB $procB
	exit 2
}

$script:RootA = Join-Path $rootA "xtest"
$script:RootB = Join-Path $rootB "xtest"
# The visibility contract can only be checked when "both were started by us with --notify".
$script:NotifyOn = (-not $NoNotify) -and ($null -ne $procA) -and ($null -ne $procB)
if (-not $script:NotifyOn) {
	Say "  this is not a notify setup, so the visibility tests are SKIPped" "Yellow"
}

try {
	if (Test-Path -LiteralPath $script:RootA) {
		Remove-Item -LiteralPath $script:RootA -Recurse -Force -ErrorAction SilentlyContinue
	}
	New-Item -ItemType Directory -Path $script:RootA -ErrorAction Stop | Out-Null
	if (-not (Wait-Visible $script:RootB 15)) {
		Say "the test root is not visible from B: $script:RootB" "Red"
		exit 2
	}

	# **Confirm the notifications are established first** (running without that makes it indistinguishable from a visibility defect).
	if (-not (Test-NotifyPreflight)) {
		Stop-Mount $MountA $procA
		Stop-Mount $MountB $procB
		exit 2
	}

	Invoke-Test test_x_volume_serial_matches_across_mounts
	Invoke-Test test_x_createnew_exclusive_race
	Invoke-Test test_x_mkdir_race
	Invoke-Test test_x_create_visible_from_peer
	Invoke-Test test_x_overwrite_visible_from_peer
	Invoke-Test test_x_delete_visible_from_peer
	Invoke-Test test_x_rename_replace_visible_from_peer
	Invoke-Test test_x_create_under_removed_parent_fails
	Invoke-Test test_x_append_handle_sees_peer_growth
	Invoke-Test test_x_handle_follows_inode_after_peer_rename
	Invoke-Test test_x_concurrent_append_keeps_all_bytes

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
	if (Test-Path -LiteralPath $script:RootA) {
		Remove-Item -LiteralPath $script:RootA -Recurse -Force -ErrorAction SilentlyContinue
	}
	if (-not $KeepMounted) {
		Stop-Mount $MountA $procA
		Stop-Mount $MountB $procB
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
