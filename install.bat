@echo off
cd /d "%~dp0"
if not exist GameGuard.exe (
  echo [!] GameGuard.exe not found.
  echo     Compile first: double-click build.bat
  pause
  exit /b 1
)
copy /y GameGuard.vbs "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\GameGuard.vbs" >nul
echo [OK] Boot auto-start enabled.
echo      The guard will start automatically next time Windows boots.
echo Tip: to remove it later, run uninstall.bat
pause
