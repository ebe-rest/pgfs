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

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0flow.ps1" %*
exit /b %errorlevel%
