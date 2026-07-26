; AutoSleep Setup Script (C# version)
; Packs compiled exes and resources, runs Deployer on install

OutFile "AutoSleep_Setup_Win7_Net40.exe"
RequestExecutionLevel admin
SilentInstall normal

Section
    SetOutPath "$TEMP\AutoSleepInstall"

    ; Compiled exes
    File "..\src\AutoSleep.Core\bin\Release\AutoSleep.exe"
    File "..\src\AutoSleep.Settings\bin\Release\AutoSleepSettings.exe"
    File "..\src\AutoSleep.Server\bin\Release\AutoSleepServer.exe"
    File "..\src\AutoSleep.Deploy\bin\Release\AutoSleepDeploy.exe"
    File "..\src\AutoSleep.Uninstall\bin\Release\Uninstall.exe"

    ; Resource files
    File "..\docs\README.txt"
    File "..\src\editor.html"

    ; Win7 curl (bundled TLS stack, bypasses Schannel)
    File "..\curl.exe"

    ; Run deployer
    ExecWait '"$TEMP\AutoSleepInstall\AutoSleepDeploy.exe"'

    RMDir /r "$TEMP\AutoSleepInstall"
SectionEnd
