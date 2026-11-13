@echo off
rem Installs SQLFinish into SQL Server Management Studio 22.10 or later. Close SSMS first.
rem "Install.cmd -Uninstall" removes it again.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-SQLFinish.ps1" %*
echo.
pause
