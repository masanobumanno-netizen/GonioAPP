@echo off
setlocal
cd /d "%~dp0"
"%~dp0launcher\GonioLauncher.exe" --install
if errorlevel 1 pause
endlocal
