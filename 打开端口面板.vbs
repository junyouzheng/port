Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")
dir = fso.GetParentFolderName(WScript.ScriptFullName) & "\dist"
exe = dir & "\port.exe"
If Not fso.FileExists(exe) Then
  MsgBox "还没有 port.exe。请先在这个目录执行 dotnet publish -c Release -o dist", vbExclamation, "本机端口与进程面板"
  WScript.Quit 1
End If
sh.CurrentDirectory = dir
sh.Run """" & exe & """", 1, False