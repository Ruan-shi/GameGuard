@echo off
chcp 65001 >nul
cd /d "%~dp0"
> _run_marker.log echo batch started %DATE% %TIME%

echo === 1/3 Compiling GameGuard.exe (standalone C# app) ===
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" (
  set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
) else (
  set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

taskkill /f /im GameGuard.exe >nul 2>&1
timeout /t 1 /nobreak >nul
if exist GameGuard.exe.bak del /q GameGuard.exe.bak >nul
if exist GameGuard.exe copy /y GameGuard.exe GameGuard.exe.bak >nul

"%CSC%" /nologo /target:winexe /win32icon:guard.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
  /out:GameGuard.exe GameGuardApp.cs > build.log 2>&1
set CSERR=%errorlevel%
type build.log

if not "%CSERR%"=="0" (
  echo.
  echo COMPILE FAILED - details saved to build.log
  if exist GameGuard.exe.bak copy /y GameGuard.exe.bak GameGuard.exe >nul
  echo Old exe restored. Send build.log content to WorkBuddy.
  pause
  exit /b 1
)
if exist GameGuard.exe.bak del /q GameGuard.exe.bak >nul

echo === 2/3 Updating startup entry ===
copy /y GameGuard.vbs "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\GameGuard.vbs" >nul

echo === 3/3 Cleaning up ===
del /q build.log >nul 2>&1

echo.
echo Done. GameGuard.exe is now a standalone app (no PowerShell needed).
echo Double-click GameGuard.exe to start, or just reboot.
pause
