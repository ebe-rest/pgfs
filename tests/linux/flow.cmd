@echo off
setlocal

REM tests\linux\flow.cmd
REM Full e2e flow wrapper: rsync -> publish -> mount -> tests -> unmount.
REM
REM Usage:
REM   tests\linux\flow.cmd                 full flow
REM   tests\linux\flow.cmd xattr           only tests containing "xattr"
REM   tests\linux\flow.cmd -NoBuild        skip the build (rsync + mount + test + unmount)
REM   tests\linux\flow.cmd -NoMount        assume already mounted, run tests only
REM   tests\linux\flow.cmd -KeepMounted    do not unmount after the tests
REM
REM Arguments are passed straight through to flow.ps1.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0flow.ps1" %*
exit /b %errorlevel%
