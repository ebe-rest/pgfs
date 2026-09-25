# register-logon-task.ps1 - register a task that calls pgfs-mount.ps1 at logon (no administrator rights needed; a task for your own user).
#
# Usage:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File register-logon-task.ps1 [-Drive P:] [-Script <pgfs-mount.ps1>] [-TaskName "pgfs assign P"]
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File register-logon-task.ps1 -Unregister [-TaskName ...]
#
# What the registered task contains:
#   - Trigger: at this user's logon
#   - Action: powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "<pgfs-mount.ps1>" -Drive <Drive>
#             (the working folder is %LOCALAPPDATA%\pgfs)
#   - No execution time limit (-ExecutionTimeLimit 0) - assign.pgfs runs for as long as it is mounted, so it must not be stopped at the default 72 hours
#   - Multiple instances: IgnoreNew - if it is triggered again while running, no new instance starts (the script has a guard too)
#   - Starts and keeps running regardless of power (battery)
#   - Normal privileges (Limited). If it runs elevated, operations through that mount bypass the permission decision (app.enforce_permissions)
# The procedure is in docs/Assign.md, "Keeping it resident from logon".
[CmdletBinding()]
param(
	[string]$Drive = 'P:',
	[string]$Script = '',
	[string]$TaskName = '',
	# Pass -SkipProcessGuard to pgfs-mount.ps1 (when the same user keeps another drive resident too).
	[switch]$SkipProcessGuard,
	[switch]$Unregister
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Drive = $Drive.TrimEnd('\')
# In Windows PowerShell 5.1, $PSScriptRoot is empty inside param default values, so fill it in here.
if ($Script -eq '') { $Script = Join-Path $PSScriptRoot 'pgfs-mount.ps1' }
if ($TaskName -eq '') { $TaskName = "pgfs assign $($Drive.TrimEnd(':'))" }

if ($Unregister) {
	Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
	Write-Host "unregistered: $TaskName"
	exit 0
}

$Script = (Resolve-Path $Script).Path
$workDir = Join-Path $env:LOCALAPPDATA 'pgfs'
$extra = ''
if ($SkipProcessGuard) { $extra = ' -SkipProcessGuard' }
$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
	-Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$Script`" -Drive $Drive$extra" `
	-WorkingDirectory $workDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew `
	-AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
Write-Host "registered: $TaskName -> $Script (drive $Drive)"
Write-Host "start now:  Start-ScheduledTask -TaskName '$TaskName'"
