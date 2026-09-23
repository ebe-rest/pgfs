@echo off
setlocal

REM tests\windows\flow.cmd
REM Full e2e flow wrapper: (optional build) -> mount -> tests -> unmount.
REM
REM Usage:
REM   tests\windows\flow.cmd                 mount + test + unmount
REM   tests\windows\flow.cmd pattern         only tests containing "pattern"
REM   tests\windows\flow.cmd -Build          dotnet publish first, then run
REM   tests\windows\flow.cmd -NoMount        assume already mounted, run tests only
REM   tests\windows\flow.cmd -KeepMounted    do not unmount after the tests
REM
REM Arguments are passed straight through to flow.ps1.

REM Use PowerShell 7 (pwsh) when it is available. Why:
REM   - on some machines Windows PowerShell 5.1 fails to load Microsoft.PowerShell.Security / Utility
REM     (duplicate TypeData), leaving Get-Acl / Get-FileHash unusable and **failing the tests for reasons
REM     that have nothing to do with pgfs**.
REM   - 7 also behaves better about Write-Host reaching the harness's stdout during a long run.
set "PS_EXE=powershell"
where pwsh >nul 2>&1 && set "PS_EXE=pwsh"

"%PS_EXE%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0flow.ps1" %*
exit /b %errorlevel%
