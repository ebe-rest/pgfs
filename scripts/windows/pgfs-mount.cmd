@echo off
REM pgfs-mount.cmd - double-click launcher. The logic lives in pgfs-mount.ps1 next to this file.
REM Append -Drive / -Exe / -ConfigDir to the line below if you do not use the defaults.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0pgfs-mount.ps1"
pause
