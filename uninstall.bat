@echo off
del /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\GameGuard.vbs" >nul 2>&1
echo [OK] Boot auto-start removed.
echo      If the guard is still running, exit it from the tray menu
echo      (right-click the shield icon - Exit).
pause
