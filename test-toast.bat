@echo off
cd /d "%~dp0"
echo 正在发送一条测试通知...
GameGuard.exe --toast
echo 已发送（等 2-3 秒看屏幕右下角和通知中心 Win+N）
pause
