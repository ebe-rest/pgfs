# pgfs Windows permission tests (app.enforce_permissions)
#
# Checks that on Windows (Dokan) **the POSIX permissions (mode + canonical ACL) are enforced** (since v0.2.1).
# The design reference is [docs/design/permission-interop.md, the section on the Windows decision](../../docs/design/permission-interop.md).
#
#   pwsh -NoProfile -File tests\windows\permissions.ps1
#   pwsh -NoProfile -File tests\windows\permissions.ps1 -StickyDir P:\      # also run the sticky check (see below)
#   pwsh -NoProfile -File tests\windows\permissions.ps1 -Filter group
#
# Exit codes: 0 = all PASS / 1 = one or more FAIL / 2 = the prerequisites are not met (not elevated / cannot mount)
#
# **Making "someone else's files" without creating users**:
#   - The setup runs in an **elevated shell** (Administrators enabled = a principal that bypasses the decision); Set-Acl changes the owner
#     to Administrator (= pgfs `root`) and the group to Administrators (= `root`) or Users (= `users`),
#     and the DACL builds the mode (the reverse projection of SetFileSecurity).
#   - The operations being checked run with a **restricted token that has Administrators disabled** (CreateRestrictedToken).
#     The principal becomes your ordinary self (neither the owner nor in the root group), so the other / group / named decisions become visible.
#
# **sticky (`01000`) cannot be set from Windows**, so it is checked only when `-StickyDir` is given a **root-owned `1777` directory**
# (e.g. the FS root after `chmod 1777` from Linux). Without it the test is SKIPped.
#
# **It rewrites settings, so it always puts them back before exiting**.

[CmdletBinding()]
param(
	[string]$MountPoint = "P:",
	[string]$StickyDir = "",
	# To run against a different build of assign.pgfs.exe (e.g. to confirm that the denial cases fail on a pre-fix build).
	[string]$AssignBinary = "",
	[string]$Filter = "",
	[int]$ReflectTimeoutSec = 20
)

[System.Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = "Continue"

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ($AssignBinary -eq "") { $AssignBinary = Join-Path $RepoRoot "bin\Publish\assign.pgfs.exe" }
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
$TestRoot = $MountRoot + "permtest"

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

# ===== Operating with the restricted token (C#) =====
#
# Running a PowerShell script block while impersonating can move it to another thread, so **each operation lives in C#**.
# The return value is "ok" / "denied" / "<exception type>: <message>".

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

public static class PgfsAs
{
	[StructLayout(LayoutKind.Sequential)]
	private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool CreateRestrictedToken(IntPtr existing, uint flags, uint disableCount, SidAndAttributes[] disable,
		uint deleteCount, IntPtr delete, uint restrictCount, IntPtr restrict, out IntPtr newToken);

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetCurrentProcess();

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(IntPtr handle);

	private static SafeAccessTokenHandle token;

	/// <summary>Create a restricted token with Administrators set to deny-only and the privileges dropped (treated the same as the UAC restricted token).</summary>
	public static void Init() {
		IntPtr own;
		// TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_IMPERSONATE | TOKEN_QUERY
		if (!OpenProcessToken(GetCurrentProcess(), 0x000F, out own)) { throw new System.ComponentModel.Win32Exception(); }
		var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
		var bytes = new byte[admins.BinaryLength];
		admins.GetBinaryForm(bytes, 0);
		var sid = Marshal.AllocHGlobal(bytes.Length);
		Marshal.Copy(bytes, 0, sid, bytes.Length);
		var disable = new[] { new SidAndAttributes { Sid = sid, Attributes = 0 } };
		IntPtr restricted;
		// DISABLE_MAX_PRIVILEGE
		var ok = CreateRestrictedToken(own, 0x1, 1, disable, 0, IntPtr.Zero, 0, IntPtr.Zero, out restricted);
		CloseHandle(own);
		Marshal.FreeHGlobal(sid);
		if (!ok) { throw new System.ComponentModel.Win32Exception(); }
		token = new SafeAccessTokenHandle(restricted);
	}

	/// <summary>Confirm that the restricted token is not judged as Administrators (a premise of the tests).</summary>
	public static bool IsAdmin() {
		return WindowsIdentity.RunImpersonated(token, () => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));
	}

	private static string Run(Action action) {
		try {
			WindowsIdentity.RunImpersonated(token, action);
			return "ok";
		} catch (UnauthorizedAccessException) {
			return "denied";
		} catch (Exception e) {
			return e.GetType().Name + ": " + e.Message;
		}
	}

	public static string Read(string path) {
		return Run(() => { using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { fs.ReadByte(); } });
	}

	public static string Write(string path) {
		return Run(() => { using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { fs.WriteByte(65); fs.Flush(); } });
	}

	public static string Create(string path) {
		return Run(() => { using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { fs.WriteByte(65); } });
	}

	public static string Mkdir(string path) {
		return Run(() => { Directory.CreateDirectory(path); });
	}

	public static string Delete(string path) {
		return Run(() => { File.Delete(path); });
	}

	public static string Move(string from, string to) {
		return Run(() => { File.Move(from, to); });
	}

	/// <summary>Change only the DACL (the equivalent of chmod). Drops the Everyone ACE.</summary>
	public static string DropEveryone(string path) {
		return Run(() => {
			var fi = new FileInfo(path);
			var sec = fi.GetAccessControl(AccessControlSections.Access);
			sec.PurgeAccessRules(new SecurityIdentifier(WellKnownSidType.WorldSid, null));
			fi.SetAccessControl(sec);
		});
	}

	/// <summary>Change the owner (the equivalent of chown).</summary>
	public static string SetOwner(string path, string sid) {
		return Run(() => {
			var fi = new FileInfo(path);
			var sec = fi.GetAccessControl(AccessControlSections.Owner);
			sec.SetOwner(new SecurityIdentifier(sid));
			fi.SetAccessControl(sec);
		});
	}
}
'@

# ===== Setup (in the elevated shell) =====

$MeSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
# pgfs `root` (the user) = Administrator. `root` (the group) = Administrators. `users` = Users.
$RootSid = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::AccountAdministratorSid, $MeSid.AccountDomainSid)
$RootGroupSid = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
$UsersSid = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinUsersSid, $null)
$EveryoneSid = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::WorldSid, $null)

# Turn an "rwx" string into the bits the reverse projection of SetFileSecurity reads (ReadData / WriteData / ExecuteFile).
function Rights($rwx) {
	$r = [System.Security.AccessControl.FileSystemRights]0
	if ($rwx.Contains('r')) { $r = $r -bor [System.Security.AccessControl.FileSystemRights]::ReadData }
	if ($rwx.Contains('w')) { $r = $r -bor [System.Security.AccessControl.FileSystemRights]::WriteData }
	if ($rwx.Contains('x')) { $r = $r -bor [System.Security.AccessControl.FileSystemRights]::ExecuteFile }
	return $r
}

# Set the owner / group / mode (+ named). $named has the form @{ <SID> = "rw" }.
function Set-Owned($path, $owner, $group, $ownerRwx, $groupRwx, $otherRwx, $named = @{}) {
	$sec = Get-Acl -LiteralPath $path
	$sec.SetAccessRuleProtection($true, $false)
	foreach ($rule in @($sec.Access)) { [void]$sec.RemoveAccessRule($rule) }
	$sec.SetOwner($owner)
	$sec.SetGroup($group)
	$pairs = @(@($owner, $ownerRwx), @($group, $groupRwx), @($EveryoneSid, $otherRwx))
	foreach ($k in $named.Keys) { $pairs += , @((New-Object System.Security.Principal.SecurityIdentifier($k)), $named[$k]) }
	foreach ($p in $pairs) {
		if ($p[1] -eq "") { continue }
		$sec.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($p[0], (Rights $p[1]), 'Allow')))
	}
	Set-Acl -LiteralPath $path -AclObject $sec -ErrorAction Stop
}

function New-OwnedFile($name, $owner, $group, $ownerRwx, $groupRwx, $otherRwx, $named = @{}) {
	$path = Join-Path $TestRoot $name
	[System.IO.File]::WriteAllText($path, "seed")
	Set-Owned $path $owner $group $ownerRwx $groupRwx $otherRwx $named
	return $path
}

function New-OwnedDir($name, $owner, $group, $ownerRwx, $groupRwx, $otherRwx) {
	$path = Join-Path $TestRoot $name
	New-Item -ItemType Directory -Path $path -ErrorAction Stop | Out-Null
	Set-Owned $path $owner $group $ownerRwx $groupRwx $otherRwx
	return $path
}

function Expect($what, $expected, $actual) {
	if ($actual -eq $expected) { return $true }
	Fail "${what}: expected '$expected', got '$actual'"
	return $false
}

# Remove the test area. **A file nobody can write (every w dropped) looks "read-only" on Windows and cannot be deleted**
# (Windows refuses to delete a read-only file). So first give the ownership back to yourself, make the mode writable, then delete.
# (Before a fix in v0.2.1, someone else's `0644` also looked read-only and could not be deleted.)
function Clear-TestRoot {
	if (-not (Test-Path -LiteralPath $TestRoot)) { return }
	$items = @(Get-ChildItem -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue | Sort-Object { $_.FullName.Length } -Descending)
	foreach ($item in $items + @(Get-Item -LiteralPath $TestRoot)) {
		try { Set-Owned $item.FullName $MeSid $UsersSid "rwx" "rx" "rx" } catch { Say "  could not restore the owner: $($item.FullName) $($_.Exception.Message)" "Yellow" }
	}
	Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
	if (Test-Path -LiteralPath $TestRoot) { Say "  could not remove $TestRoot completely" "Yellow" }
}

# ===== pgfsctl / mount =====

function Invoke-Ctl {
	param([string[]]$CtlArgs)
	$out = & $Pgfsctl @CtlArgs --setting-file $SettingFile 2>&1 | Out-String
	return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out }
}

function Set-Setting($key, $value) {
	if (-not $script:Restore.ContainsKey($key)) {
		$r = Invoke-Ctl @('config', 'get', $key)
		if ($r.ExitCode -eq 0) {
			$line = ($r.Output -split "`n" | Where-Object { $_ -match "^\s*$([regex]::Escape($key))\s*=" } | Select-Object -First 1)
			if ($line) { $script:Restore[$key] = (($line -split '=', 2)[1] -split '\[')[0].Trim() }
		}
	}
	return (Invoke-Ctl @('config', 'set', $key, $value))
}

# Applying to a running mount is asynchronous through control messages, so wait **until the behavior changes**.
function Wait-Result($probe, $expected, $seconds = $ReflectTimeoutSec) {
	$deadline = (Get-Date).AddSeconds($seconds)
	$last = ""
	while ((Get-Date) -lt $deadline) {
		$last = & $probe
		if ($last -eq $expected) { return $last }
		Start-Sleep -Milliseconds 500
	}
	return $last
}

function Start-Mount {
	if (Test-Path -LiteralPath $MountRoot -PathType Container) {
		Say "  $MountRoot is already mounted (this test mounts it itself)" "Red"
		return $false
	}
	$proc = Start-Process -FilePath $AssignBinary -ArgumentList "-f `"$SettingFile`" -m `"$MountPoint`"" `
		-PassThru -NoNewWindow -RedirectStandardOutput "$env:TEMP\pgfs-perm-out.log" -RedirectStandardError "$env:TEMP\pgfs-perm-err.log"
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

# Premise: the restricted token is not judged as Administrators (if it were, everything would pass through and nothing would be checked).
function test_perm_restricted_token_is_not_admin {
	if ([PgfsAs]::IsAdmin()) { Fail "the restricted token is still Administrators (the results after this are meaningless)"; return }
	Pass
}

# Only r for other: can read, cannot write.
function test_perm_other_read_only {
	$f = New-OwnedFile "other644.txt" $RootSid $RootGroupSid "rw" "r" "r"
	if (-not (Expect "read" "ok" ([PgfsAs]::Read($f)))) { return }
	if (-not (Expect "write" "denied" ([PgfsAs]::Write($f)))) { return }
	Pass
}

# Nothing for other: cannot read.
function test_perm_other_no_read {
	$f = New-OwnedFile "other600.txt" $RootSid $RootGroupSid "rw" "" ""
	if (-not (Expect "read" "denied" ([PgfsAs]::Read($f)))) { return }
	Pass
}

# With a named user (rw for yourself), you can write even though other has nothing.
function test_perm_named_user_grants_write {
	$f = New-OwnedFile "named600.txt" $RootSid $RootGroupSid "rw" "" "" @{ "$MeSid" = "rw" }
	if (-not (Expect "write" "ok" ([PgfsAs]::Write($f)))) { return }
	Pass
}

# The owning group's (users) rw allows writing. You are a member of Users.
function test_perm_group_grants_write {
	$f = New-OwnedFile "group664.txt" $RootSid $UsersSid "rw" "rw" "r"
	if (-not (Expect "write" "ok" ([PgfsAs]::Write($f)))) { return }
	Pass
}

# **POSIX order**: once the owner matches, only the owner's bits decide (you cannot write even if the group has rw).
# With Windows ACLs (the union of the ACEs) it would be writable.
function test_perm_owner_narrower_than_group {
	$f = New-OwnedFile "owner460.txt" $MeSid $UsersSid "r" "rw" ""
	if (-not (Expect "read" "ok" ([PgfsAs]::Read($f)))) { return }
	if (-not (Expect "write" "denied" ([PgfsAs]::Write($f)))) { return }
	Pass
}

# Create / mkdir need w on the parent.
function test_perm_create_needs_parent_write {
	$d = New-OwnedDir "dir755" $RootSid $RootGroupSid "rwx" "rx" "rx"
	if (-not (Expect "create" "denied" ([PgfsAs]::Create((Join-Path $d "mine.txt"))))) { return }
	if (-not (Expect "mkdir" "denied" ([PgfsAs]::Mkdir((Join-Path $d "sub"))))) { return }
	Pass
}

# In a directory anyone can write, you can create and delete. What you create is owned by you; as the owner you can chmod it but not chown it.
function test_perm_own_file_in_open_dir {
	$d = New-OwnedDir "dir777" $RootSid $RootGroupSid "rwx" "rwx" "rwx"
	$f = Join-Path $d "mine.txt"
	if (-not (Expect "create" "ok" ([PgfsAs]::Create($f)))) { return }
	if (-not (Expect "write" "ok" ([PgfsAs]::Write($f)))) { return }
	if (-not (Expect "chmod (DACL)" "ok" ([PgfsAs]::DropEveryone($f)))) { return }
	$chown = [PgfsAs]::SetOwner($f, "$RootSid")
	if ($chown -eq "ok") { Fail "chown went through (a principal that is not Administrators should not be able to change the owner)"; return }
	if (-not (Expect "delete" "ok" ([PgfsAs]::Delete($f)))) { return }
	Pass
}

# A rename also needs w on the destination parent (checked in MoveFile). The source parent is checked by the open with Delete.
function test_perm_rename_into_readonly_dir_denied {
	$open = New-OwnedDir "mv777" $RootSid $RootGroupSid "rwx" "rwx" "rwx"
	$closed = New-OwnedDir "mv755" $RootSid $RootGroupSid "rwx" "rx" "rx"
	$f = Join-Path $open "mine.txt"
	if (-not (Expect "create" "ok" ([PgfsAs]::Create($f)))) { return }
	if (-not (Expect "rename into 0755" "denied" ([PgfsAs]::Move($f, (Join-Path $closed "mine.txt"))))) { return }
	if (-not (Expect "rename within 0777" "ok" ([PgfsAs]::Move($f, (Join-Path $open "renamed.txt"))))) { return }
	# Someone else's file cannot be moved out of someone else's directory (no w on the source parent)
	$theirs = Join-Path $closed "theirs.txt"
	[System.IO.File]::WriteAllText($theirs, "seed")
	Set-Owned $theirs $RootSid $RootGroupSid "rw" "rw" "rw"
	if (-not (Expect "rename out of 0755" "denied" ([PgfsAs]::Move($theirs, (Join-Path $open "stolen.txt"))))) { return }
	Pass
}

# sticky: even if anyone can write, someone else's file cannot be deleted / renamed. Your own can be deleted.
function test_perm_sticky_protects_others_files {
	if ($StickyDir -eq "") { Skip "-StickyDir (a root-owned 1777 directory) is not given"; return }
	$tag = [guid]::NewGuid().ToString("N").Substring(0, 8)
	$theirs = Join-Path $StickyDir "perm-sticky-theirs-$tag.txt"
	$mine = Join-Path $StickyDir "perm-sticky-mine-$tag.txt"
	try {
		[System.IO.File]::WriteAllText($theirs, "seed")
		Set-Owned $theirs $RootSid $RootGroupSid "rw" "rw" "rw"
		if (-not (Expect "create mine" "ok" ([PgfsAs]::Create($mine)))) { return }
		if (-not (Expect "delete theirs" "denied" ([PgfsAs]::Delete($theirs)))) { return }
		if (-not (Expect "rename theirs" "denied" ([PgfsAs]::Move($theirs, "$theirs.moved")))) { return }
		if (-not (Expect "delete mine" "ok" ([PgfsAs]::Delete($mine)))) { return }
		Pass
	}
	finally {
		Remove-Item -LiteralPath $theirs, "$theirs.moved", $mine -Force -ErrorAction SilentlyContinue
	}
}

# **Even someone else's file can be deleted if the parent has w** (same as POSIX). Previously the "read-only" attribute was decided by whether
# the mounting user could write, so someone else's `0644` looked read-only and Windows refused to delete it (not even an elevated shell could).
function test_perm_others_file_deletable_in_open_dir {
	$d = New-OwnedDir "del777" $RootSid $RootGroupSid "rwx" "rwx" "rwx"
	$f = Join-Path $d "theirs.txt"
	[System.IO.File]::WriteAllText($f, "seed")
	Set-Owned $f $RootSid $RootGroupSid "rw" "r" "r"
	if (-not (Expect "delete theirs (0644 in 0777)" "ok" ([PgfsAs]::Delete($f)))) { return }
	if (Test-Path -LiteralPath $f) { Fail "Delete returned ok but the file is still there"; return }
	Pass
}

# The "read-only" attribute is set **only when nobody can write (every w dropped)**. Who can write is decided by the permission check.
function test_perm_readonly_attr_only_when_nobody_can_write {
	$theirs = New-OwnedFile "ro644.txt" $RootSid $RootGroupSid "rw" "r" "r"
	$nobody = New-OwnedFile "ro444.txt" $MeSid $UsersSid "r" "r" "r"
	$a1 = [System.IO.File]::GetAttributes($theirs)
	$a2 = [System.IO.File]::GetAttributes($nobody)
	if (($a1 -band [System.IO.FileAttributes]::ReadOnly) -ne 0) { Fail "someone else's 0644 looks read-only ($a1)"; return }
	if (($a2 -band [System.IO.FileAttributes]::ReadOnly) -eq 0) { Fail "0444 does not look read-only ($a2)"; return }
	Pass
}

# An elevated shell (Administrators enabled) passes through = the same as root on Linux.
function test_perm_admin_bypasses {
	$f = New-OwnedFile "admin600.txt" $RootSid $RootGroupSid "" "" ""
	try {
		[System.IO.File]::AppendAllText($f, "x")
	}
	catch {
		Fail "the elevated shell cannot write: $($_.Exception.Message)"
		return
	}
	Pass
}

# With app.enforce_permissions=false it stops deciding (same as v0.2.0), and setting it back to true denies again. **It switches while running**.
function test_perm_enforce_toggle_live {
	$f = New-OwnedFile "toggle644.txt" $RootSid $RootGroupSid "rw" "r" "r"
	if (-not (Expect "write (on)" "denied" ([PgfsAs]::Write($f)))) { return }
	$r = Set-Setting "app.enforce_permissions" "false"
	if ($r.ExitCode -ne 0) { Fail "config set false failed: $($r.Output)"; return }
	if (-not (Expect "write (off)" "ok" (Wait-Result { [PgfsAs]::Write($f) } "ok"))) { return }
	$r = Set-Setting "app.enforce_permissions" "true"
	if ($r.ExitCode -ne 0) { Fail "config set true failed: $($r.Output)"; return }
	if (-not (Expect "write (on again)" "denied" (Wait-Result { [PgfsAs]::Write($f) } "denied"))) { return }
	Pass
}

# ===== main =====

Say "=== pgfs Windows permission tests ===" "Cyan"

$principal = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
	Say "Run this in an elevated shell (Administrators is needed to set up files owned by someone else)" "Red"
	exit 2
}
if (-not (Test-Path -LiteralPath $Pgfsctl)) { Say "pgfsctl.exe not found: $Pgfsctl" "Red"; exit 2 }
[PgfsAs]::Init()
if (-not (Start-Mount)) { exit 2 }

try {
	# The decision should be on by default, but set it on explicitly so it can be checked even if a previous run died with it off (restored at the end).
	$null = Set-Setting "app.enforce_permissions" "true"
	Clear-TestRoot
	New-Item -ItemType Directory -Path $TestRoot -ErrorAction Stop | Out-Null
	# Let anyone enter and read the test area (each test decides its own ownership and mode).
	Set-Owned $TestRoot $RootSid $RootGroupSid "rwx" "rwx" "rx"

	Invoke-Test test_perm_restricted_token_is_not_admin
	Invoke-Test test_perm_other_read_only
	Invoke-Test test_perm_other_no_read
	Invoke-Test test_perm_named_user_grants_write
	Invoke-Test test_perm_group_grants_write
	Invoke-Test test_perm_owner_narrower_than_group
	Invoke-Test test_perm_create_needs_parent_write
	Invoke-Test test_perm_own_file_in_open_dir
	Invoke-Test test_perm_rename_into_readonly_dir_denied
	Invoke-Test test_perm_sticky_protects_others_files
	Invoke-Test test_perm_others_file_deletable_in_open_dir
	Invoke-Test test_perm_readonly_attr_only_when_nobody_can_write
	Invoke-Test test_perm_admin_bypasses
	Invoke-Test test_perm_enforce_toggle_live

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
	foreach ($key in $script:Restore.Keys) {
		$v = $script:Restore[$key]
		Say "  restore: $key = $v" "Gray"
		$null = Invoke-Ctl @('config', 'set', $key, $v)
	}
	Clear-TestRoot
	Stop-Mount
}

# **Fail if nothing ran / nothing PASSed** (prevents looking green while having checked nothing).
if ($script:Total -eq 0) {
	Say "nothing was run (does Filter '$Filter' match nothing?)" "Red"
	exit 1
}
if ($script:Passed -eq 0) {
	Say "nothing PASSed (all skipped = a missing environment, not a test success)" "Red"
	exit 1
}
if ($script:Failed -gt 0) { exit 1 }
exit 0
