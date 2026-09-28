@echo off
setlocal
cd /d "%~dp0"
title Gonio Web - Keep this window open
if not exist "GonioWeb.exe" (
  echo Extract ALL files from the ZIP, then run Start-Gonio.cmd again.
  pause
  exit /b 1
)
"%~dp0GonioWeb.exe" --open
if errorlevel 1 (
  echo.
  echo Could not start. Check the error above. Port 4173 may already be in use.
  pause
)
endlocal
