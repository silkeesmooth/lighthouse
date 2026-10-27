@echo off
rem Double-click to install Lighthouse, or to update it after downloading a new version.
rem Windows asks once for admin rights. What runs as admin and why: docs\SECURITY.md
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0source\tools\install.ps1"
echo.
pause
