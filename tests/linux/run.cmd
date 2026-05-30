@echo off
setlocal

REM pgfs Linux e2e test runner (Windows cmd side)
REM
REM Usage:
REM   tests\linux\run.cmd            ... all tests
REM   tests\linux\run.cmd xattr      ... only tests whose name contains "xattr"
REM
REM Prerequisites:
REM   - the repository is already rsynced to %REMOTE_REPO%
REM   - mount.pgfs is mounted at %MOUNT_POINT% (in another terminal)
REM
REM Overridable via environment variables:
REM   REMOTE       SSH target (default: linux_client)
REM   REMOTE_REPO  remote repository path (default: ~/project/pgfs_cs)
REM   MOUNT_POINT  mount point (default: ~/mnt/pgfs)

if "%REMOTE%"=="" set REMOTE=linux_client
if "%REMOTE_REPO%"=="" set REMOTE_REPO=~/project/pgfs_cs
if "%MOUNT_POINT%"=="" set MOUNT_POINT=~/mnt/pgfs

set FILTER=%1

if "%FILTER%"=="" (
	bash -c "ssh %REMOTE% bash %REMOTE_REPO%/tests/linux/e2e.sh %MOUNT_POINT%"
) else (
	bash -c "ssh %REMOTE% TEST_FILTER=%FILTER% bash %REMOTE_REPO%/tests/linux/e2e.sh %MOUNT_POINT%"
)

exit /b %errorlevel%
