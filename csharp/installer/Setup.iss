; AutoSleep Setup Script (C# version) - Inno Setup 6
; 编译：ISCC.exe Setup.iss  （需要 Inno Setup 6）
; 设计要点：
;   1) 安装 = 标准 Inno 向导；文件解包到 {tmp}\AutoSleepInstall，由 AutoSleepDeploy.exe 部署
;      （与旧 NSIS 同构，Deployer 从自身所在临时目录复制到 C:\ProgramData\AutoSleep）
;   2) Deployer 以 /silent 静默运行，退出码传给 Inno，失败时提示并中止安装
;   3) 卸载 = Inno 标准卸载器 unins000.exe；带"保留配置"询问；静默模式跳过询问默认保留
;   4) 升级路径：Deployer 检测新旧卸载器（Uninstall.exe = 旧版直接跑；unins000.exe = 新版
;      静默调用），注册表卸载键为 {AppId}_is1（与 Deployer.cs 硬编码一致）
#define MyAppName "AutoSleep 智能休眠工具"
#define MyAppVersion "1.0.15"
#define MyAppPublisher "Cesium-developer"
#define MyAppId "{8F2C1D4E-5A6B-4C7D-8E9F-0A1B2C3D4E5F}"

[Setup]
AppId={{#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName=C:\ProgramData\AutoSleep
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=admin
; 64 位系统以 64 位模式安装：卸载注册表写入 64 位视图（HKLM\SOFTWARE\...），
; 与 64 位 Deployer 的注册表读取一致；32 位系统（含 Win7 x86）不受影响
ArchitecturesInstallIn64BitMode=x64
OutputDir=.
OutputBaseFilename=AutoSleep_Setup_Win7_Net40
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\AutoSleepSettings.exe
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
SetupIconFile=..\src\AutoSleep.Settings\AutoSleep.ico
CloseApplications=no

[Files]
; 全部解包到安装临时目录，由 Deployer 部署（与旧 NSIS 同构）
Source: "..\src\AutoSleep.Core\bin\Release\AutoSleep.exe"; DestDir: "{tmp}\AutoSleepInstall"
Source: "..\src\AutoSleep.Settings\bin\Release\AutoSleepSettings.exe"; DestDir: "{tmp}\AutoSleepInstall"
Source: "..\src\AutoSleep.Server\bin\Release\AutoSleepServer.exe"; DestDir: "{tmp}\AutoSleepInstall"
Source: "..\src\AutoSleep.Deploy\bin\Release\AutoSleepDeploy.exe"; DestDir: "{tmp}\AutoSleepInstall"
Source: "..\docs\README.txt"; DestDir: "{tmp}\AutoSleepInstall"
Source: "..\src\editor.html"; DestDir: "{tmp}\AutoSleepInstall"
Source: "..\src\AutoSleep.Settings\AutoSleep.ico"; DestDir: "{tmp}\AutoSleepInstall"
; Win7 需要自带 curl.exe（TLS 栈绕过 Schannel），Win10+ 不需要
Source: "..\curl.exe"; DestDir: "{tmp}\AutoSleepInstall"; Check: IsWindows7

[Code]
var
  DeploySucceeded: Boolean;
  KeepConfig: Boolean;

// Inno 没有内置 IsSilent/CmdLineParamExists，用 GetCmdTail 检测静默参数
function IsSilent(): Boolean;
var
  CmdTail: String;
begin
  CmdTail := GetCmdTail();
  Result := (Pos('/VERYSILENT', CmdTail) > 0) or (Pos('/SILENT', CmdTail) > 0);
end;

function BoolStr(v: Boolean): String;
begin
  if v then Result := 'True' else Result := 'False';
end;

// 安装模式诊断（/LOG 时可见；正常安装无副作用）
function InitializeSetup(): Boolean;
begin
  Log('SetupDiag IsWin64=' + BoolStr(IsWin64));
  Log('SetupDiag IsX64Compatible=' + BoolStr(IsX64Compatible));
  Log('SetupDiag Is64BitInstallMode=' + BoolStr(Is64BitInstallMode));
  Result := True;
end;

// ---------- 安装：运行 Deployer ----------
procedure RunDeployer();
var
  DeployerPath: String;
  ResultCode: Integer;
  ErrMsg: String;
begin
  DeployerPath := ExpandConstant('{tmp}\AutoSleepInstall\AutoSleepDeploy.exe');
  DeploySucceeded := False;

  if not FileExists(DeployerPath) then
  begin
    ErrMsg := '部署程序缺失：' + DeployerPath;
    Log(ErrMsg);
    if not IsSilent() then
      MsgBox(ErrMsg, mbError, MB_OK);
    Abort();
  end;

  // /silent：Deployer 跳过"按 Enter 退出"等待，便于 Inno 静默调用
  if not Exec(DeployerPath, '/silent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    ErrMsg := '部署程序启动失败，安装中止。';
    Log(ErrMsg);
    if not IsSilent() then
      MsgBox(ErrMsg, mbError, MB_OK);
    Abort();
  end;

  if ResultCode <> 0 then
  begin
    ErrMsg := '部署程序执行失败（退出码 ' + IntToStr(ResultCode) + '），安装中止。'#13#10 +
              '详情请查看 %TEMP%\AutoSleepDeploy.log';
    Log(ErrMsg);
    if not IsSilent() then
      MsgBox(ErrMsg, mbError, MB_OK);
    Abort();
  end;

  DeploySucceeded := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RunDeployer();
end;

// ---------- 卸载 ----------
function InitializeUninstall(): Boolean;
begin
  KeepConfig := True;
  // 静默模式（Deployer 升级时调用 /VERYSILENT）不询问，默认保留配置
  if not IsSilent() then
  begin
    if MsgBox('是否保留配置文件和日志？'#13#10#13#10 +
              '选择「是」将保留 settings.json 和 AutoSleep.log，'#13#10 +
              '重装后可继续使用原有设置；'#13#10 +
              '选择「否」将删除全部数据。',
              mbConfirmation, MB_YESNO) = IDNO then
      KeepConfig := False;
  end;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDir, LnkFile, DelBat, DelContent: String;
  ResultCode: Integer;
  FindRec: TFindRec;
begin
  if CurUninstallStep = usUninstall then
  begin
    // 1) 杀进程（计划任务/桌面启动的实例，确保文件可删除）
    Exec('taskkill.exe', '/F /IM AutoSleep.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('taskkill.exe', '/F /IM AutoSleepSettings.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('taskkill.exe', '/F /IM AutoSleepServer.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // 2) 删除计划任务（开机 + 登录双触发器）
    Exec('schtasks.exe', '/delete /tn "AutoSleep" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // 3) 删除桌面快捷方式（Deployer 创建）
    LnkFile := ExpandConstant('{userdesktop}\AutoSleep 设置.lnk');
    if FileExists(LnkFile) then DeleteFile(LnkFile);
    LnkFile := ExpandConstant('{commondesktop}\AutoSleep 设置.lnk');
    if FileExists(LnkFile) then DeleteFile(LnkFile);
    // 3.1) 删除任务栏/开始菜单固定快捷方式（用户手动固定产生；旧版卸载器未覆盖，GeekUninstaller 会报残留）
    LnkFile := ExpandConstant('{userappdata}\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\AutoSleep 设置.lnk');
    if FileExists(LnkFile) then DeleteFile(LnkFile);
    LnkFile := ExpandConstant('{userappdata}\Microsoft\Internet Explorer\Quick Launch\User Pinned\StartMenu\AutoSleep 设置.lnk');
    if FileExists(LnkFile) then DeleteFile(LnkFile);
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    // 4) 清理安装目录：Deployer 部署的文件不在 Inno 清单里，须手动删除；
    //    保留 unins000.exe/.dat 由 Inno 卸载器自删；
    //    用户选择保留配置时跳过 settings.json / AutoSleep.log
    AppDir := ExpandConstant('{app}');
    if FindFirst(AppDir + '\*', FindRec) then
    begin
      try
        repeat
          if (FindRec.Name <> '.') and (FindRec.Name <> '..') and
             (CompareText(FindRec.Name, 'unins000.exe') <> 0) and
             (CompareText(FindRec.Name, 'unins000.dat') <> 0) then
          begin
            if KeepConfig and
               ((CompareText(FindRec.Name, 'settings.json') = 0) or
                (CompareText(FindRec.Name, 'AutoSleep.log') = 0)) then
              Continue;
            if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
              DelTree(AppDir + '\' + FindRec.Name, True, True, True)
            else
              DeleteFile(AppDir + '\' + FindRec.Name);
          end;
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;

    // 5) 不保留配置时删除整个目录（含卸载器自身文件）。
    //    Inno 的 {app} 不在其卸载清单（[Files] 解包到 {tmp}），卸载器不会删除该目录；
    //    且卸载器自删 unins000.exe/.dat 发生在本步骤之后、进程退出前，此时该文件仍被
    //    锁定，rmdir 必失败。故：先尽力删除卸载器文件 → RemoveDir 立即尝试 →
    //    仍失败则生成延迟批处理，循环重试直到锁释放。
    if not KeepConfig then
    begin
      DeleteFile(AppDir + '\unins000.exe');
      DeleteFile(AppDir + '\unins000.dat');
      if not RemoveDir(AppDir) then
      begin
        DelBat := AddBackslash(GetTempDir()) + 'AutoSleepRemoveDir.bat';
        DelContent := '@echo off' + #13#10
          + 'cd /d "C:\"' + #13#10
          + 'set /a n=0' + #13#10
          + 'timeout /t 3 /nobreak > nul' + #13#10
          + ':retry' + #13#10
          + 'rmdir /s /q "' + AppDir + '" 2>nul' + #13#10
          + 'if not exist "' + AppDir + '" goto done' + #13#10
          + 'set /a n+=1' + #13#10
          + 'if %n% geq 10 goto done' + #13#10
          + 'timeout /t 2 /nobreak > nul' + #13#10
          + 'goto retry' + #13#10
          + ':done' + #13#10
          + 'del /f /q "%~f0" 2>nul' + #13#10;
        if SaveStringToFile(DelBat, DelContent, False) then
          Exec('cmd.exe', '/c ""' + DelBat + '""', 'C:\', SW_HIDE, ewNoWait, ResultCode);
      end;
    end;
  end;
end;

function IsWindows7(): Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := (Version.Major = 6) and (Version.Minor = 1);
end;
