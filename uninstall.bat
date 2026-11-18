@echo off
del /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\GameGuard.vbs" >nul 2>&1
schtasks /Delete /TN "GameGuardWatchdog" /F >nul 2>&1
del /q "%~dp0watchdog_off.flag" >nul 2>&1
echo [OK] Boot auto-start and watchdog removed.
echo      If the guard is still running, exit it from the tray menu
echo      (right-click the shield icon - Exit).
pause
