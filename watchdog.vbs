' GameGuard 看门狗：由计划任务每分钟调用
' 若 watchdog_off.flag 存在（用户正常退出时写入）则不复活
' 若 GameGuard.exe 未在运行则拉起
Set fso = CreateObject("Scripting.FileSystemObject")
dir = fso.GetParentFolderName(WScript.ScriptFullName)

If fso.FileExists(dir & "\watchdog_off.flag") Then WScript.Quit

Set wmi = GetObject("winmgmts:\\.\root\cimv2")
Set procs = wmi.InstancesOf("Win32_Process WHERE Name='GameGuard.exe'")
If procs.Count = 0 Then
  CreateObject("WScript.Shell").Run """" & dir & "\GameGuard.exe""", 1, False
End If
