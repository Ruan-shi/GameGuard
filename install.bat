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
schtasks /Create /F /TN "GameGuardWatchdog" /TR "wscript.exe \"%~dp0watchdog.vbs\"" /SC MINUTE /MO 1 >nul 2>&1
if errorlevel 1 (
  echo [!] Watchdog task creation failed. Try right-click install.bat and "Run as administrator".
  echo     The guard still works without the watchdog.
) else (
  echo [OK] Watchdog enabled: every minute it revives GameGuard.exe if it is not running.
  echo      Exiting GameGuard normally pauses the watchdog automatically.
)
echo Tip: to remove it later, run uninstall.bat
pause
