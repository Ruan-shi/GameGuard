@echo off
chcp 65001 >nul
cd /d "%~dp0"
> _run_marker.log echo batch started %DATE% %TIME%

set VERSION=1.5.0
set DIST=dist\GameGuard-%VERSION%

echo === 1/4 Compiling release exe ===
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" (
  set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
) else (
  set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
if exist "%DIST%" rmdir /s /q "%DIST%"
mkdir "%DIST%"

"%CSC%" /nologo /target:winexe /win32icon:guard.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
  /out:"%DIST%\GameGuard.exe" GameGuardApp.cs > build.log 2>&1
set CSERR=%errorlevel%
type build.log

if not "%CSERR%"=="0" (
  echo.
  echo COMPILE FAILED - details saved to build.log
  pause
  exit /b 1
)

echo === 2/4 Copying release files ===
copy /y *.txt "%DIST%\" >nul
copy /y "docs\config.example.json" "%DIST%\" >nul
copy /y "GameGuard.vbs" "%DIST%\" >nul
copy /y "watchdog.vbs" "%DIST%\" >nul
copy /y "install.bat" "%DIST%\" >nul
copy /y "uninstall.bat" "%DIST%\" >nul

echo === 3/4 Writing version file ===
> "%DIST%\VERSION.txt" echo GameGuard v%VERSION% - %DATE%

echo === 4/4 Creating zip ===
if exist "dist\GameGuard-%VERSION%.zip" del /q "dist\GameGuard-%VERSION%.zip" >nul
powershell -NoProfile -Command "Compress-Archive -Path '%DIST%\*' -DestinationPath 'dist\GameGuard-%VERSION%.zip' -Force"
if errorlevel 1 (
  echo ZIP FAILED
  pause
  exit /b 1
)
del /q build.log >nul 2>&1

echo.
echo Done. Release package: dist\GameGuard-%VERSION%.zip
echo Upload this zip to GitHub Releases (see PUBLISHING.md step 3).
pause
