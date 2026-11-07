@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -NoShortcut
if errorlevel 1 (
  echo.
  echo Install failed. See the error above.
  pause
  exit /b 1
)
echo.
echo Installed. Start DSH Bot Manager from the desktop shortcut.
pause
