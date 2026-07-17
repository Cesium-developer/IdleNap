OutFile "Uninstall.exe"
SilentInstall silent
RequestExecutionLevel admin

Section
    SetOutPath "$TEMP"
    File "..\src\Uninstall-AutoSleep.ps1"
    ExecWait '"powershell.exe" -ExecutionPolicy Bypass -File "$TEMP\Uninstall-AutoSleep.ps1"'
    Delete "$TEMP\Uninstall-AutoSleep.ps1"
SectionEnd