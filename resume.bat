@echo off
if exist "%~dp0paused.flag" del /q "%~dp0paused.flag"
echo.
echo Guard resumed.
timeout /t 3 >nul
