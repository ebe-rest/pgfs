@echo off
setlocal
REM Run the pgfs Citus diagnostics against a live PG. See ./README.md for details.
REM
REM Overridable via environment variables:
REM   VERIFY_REMOTE   ssh target (default: pgsql_server)
if "%VERIFY_REMOTE%"=="" set VERIFY_REMOTE=pgsql_server
ssh -o LogLevel=ERROR %VERIFY_REMOTE% "sudo -u postgres -i bash -c 'psql -d pgfs'" < "%~dp0verify.sql"
