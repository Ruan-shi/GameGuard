@echo off
if not exist "%~dp0paused.flag" echo %date% %time%> "%~dp0paused.flag"
echo.
echo Guard paused. Run the resume script to turn it back on.
timeout /t 3 >nul
