; AutoSleep 安装程序（等效于原 WinRAR 自解压）
OutFile "AutoSleep_Setup.exe"
RequestExecutionLevel admin
SilentInstall normal   ; 正常显示解压进度，但不会阻挡后续窗口

Section
    SetOutPath "$TEMP\AutoSleepInstall"
    File "..\src\AutoSleep.ps1"
    File "..\src\Deploy-AutoSleep.ps1"
    File "..\src\Settings.ps1"
    File "..\docs\README.txt"
    File "..\src\Uninstall.exe"
    File "..\src\ClearLog.ps1"
    ExecWait 'powershell.exe -ExecutionPolicy Bypass -File "$TEMP\AutoSleepInstall\Deploy-AutoSleep.ps1"'
    RMDir /r "$TEMP\AutoSleepInstall"
SectionEnd