@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
if errorlevel 1 (
  echo.
  echo Install failed. See install.log in this folder.
  pause
  exit /b 1
)
echo.
echo Install finished.
pause
