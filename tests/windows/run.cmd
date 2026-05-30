@echo off
setlocal

REM pgfs Windows e2e test runner
REM
REM Usage:
REM   tests\windows\run.cmd            ... all tests
REM   tests\windows\run.cmd pattern    ... only tests whose name contains "pattern"
REM
REM Prerequisites:
REM   - assign.pgfs.exe is mounted at %MOUNT_ROOT% (default P:\)
REM
REM Overridable via environment variables:
REM   MOUNT_ROOT  mount root (default: P:\)

if "%MOUNT_ROOT%"=="" set MOUNT_ROOT=P:\

set FILTER=%1

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0e2e.ps1" -MountRoot "%MOUNT_ROOT%" -Filter "%FILTER%"
exit /b %errorlevel%
