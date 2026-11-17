' GameGuard silent launcher
' Starts GameGuard.exe - standalone tray app.
Option Explicit

Dim sh, dir
Set sh = CreateObject("WScript.Shell")
dir = Left(WScript.ScriptFullName, InStrRev(WScript.ScriptFullName, "\"))

sh.CurrentDirectory = dir
sh.Run """" & dir & "GameGuard.exe""", 0, False
