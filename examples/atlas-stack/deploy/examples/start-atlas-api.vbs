Option Explicit
Dim shell, files, launcher
Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")
launcher = files.BuildPath(files.GetParentFolderName(WScript.ScriptFullName), "start-atlas-api.ps1")
shell.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & launcher & """", 0, False
