# ============================================================
# Deploy-AutoSleep.ps1 - 完整部署脚本（集成卸载 exe）
# ============================================================

$LogPath = Join-Path $env:TEMP "AutoSleepDeploy.log"
if (Test-Path $LogPath) { Remove-Item $LogPath -Force }

function Write-Log {
    param([string]$Message, [string]$Color = "White")
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$timestamp] $Message"
    Add-Content -Path $LogPath -Value $line
    Write-Host $line -ForegroundColor $Color
}

Write-Log "===== AutoSleep 部署工具启动 =====" -Color "Cyan"

# ---- 提权 ----
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")
if (-not $isAdmin) {
    Write-Log "当前非管理员权限，正在尝试提权..." -Color "Yellow"
    $scriptPath = $MyInvocation.MyCommand.Path
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments
    exit
}

Write-Log "已获得管理员权限，继续执行部署..." -Color "Green"

# ---- 阶段1：环境检测 ----
Write-Log "----- 阶段1：环境检测 -----" -Color "Cyan"

$os = Get-CimInstance Win32_OperatingSystem
$version = [Version]$os.Version
$minVersion = [Version]"10.0.17134"
if ($version -lt $minVersion) {
    Write-Log "当前操作系统版本 $version 低于最低要求 (10.0.17134)，不支持该工具。" -Color "Red"
    Read-Host "按 Enter 退出"
    exit 1
}
Write-Log "✅ 操作系统版本: $version (符合要求)" -Color "Green"

$psVer = $PSVersionTable.PSVersion
if ($psVer.Major -lt 5 -or ($psVer.Major -eq 5 -and $psVer.Minor -lt 1)) {
    Write-Log "当前 PowerShell 版本 $psVer 低于 5.1，不支持。" -Color "Red"
    Read-Host "按 Enter 退出"
    exit 1
}
Write-Log "✅ PowerShell 版本: $psVer (符合要求)" -Color "Green"

$gpuAvailable = $true
try {
    $null = Get-Counter "\GPU Engine(*)\Utilization Percentage" -ErrorAction Stop
    Write-Log "✅ GPU 利用率计数器可用" -Color "Green"
} catch {
    $gpuAvailable = $false
    Write-Log "⚠️ GPU 利用率计数器不可用（将使用仅 CPU 模式）" -Color "Yellow"
}

$targetDir = "C:\ProgramData\AutoSleep"
$targetScript = Join-Path $targetDir "AutoSleep.ps1"
if (Test-Path $targetDir) {
    $testFile = Join-Path $targetDir "test.tmp"
    try {
        New-Item -ItemType File -Path $testFile -Force -ErrorAction Stop | Out-Null
        Remove-Item $testFile -Force
        Write-Log "✅ 目标目录 $targetDir 可写" -Color "Green"
    } catch {
        Write-Log "❌ 目标目录 $targetDir 不可写，请检查权限。" -Color "Red"
        Read-Host "按 Enter 退出"
        exit 1
    }
} else {
    Write-Log "ℹ️ 目标目录 $targetDir 不存在，将自动创建。" -Color "Cyan"
}

$taskExists = Get-ScheduledTask -TaskName "AutoSleep" -ErrorAction SilentlyContinue
if ($taskExists) {
    Write-Log "⚠️ 计划任务 'AutoSleep' 已存在。" -Color "Yellow"
    $choice = Read-Host "是否覆盖安装？(y/n)"
    if ($choice -ne 'y') {
        Write-Log "用户取消部署。" -Color "Red"
        exit 0
    }
    Unregister-ScheduledTask -TaskName "AutoSleep" -Confirm:$false
    Write-Log "已删除旧计划任务。" -Color "Cyan"
}

# ---- 阶段2：准备与配置 ----
Write-Log "----- 阶段2：准备与配置 -----" -Color "Cyan"

# ---- 检测是否为升级安装 ----
$isUpgrade = Test-Path "$targetDir\settings.json"
$oldConfig = $null
$logBackupPath = "$env:TEMP\AutoSleep.log.bak"

if ($isUpgrade) {
    Write-Log "检测到升级安装，执行前置备份..." -Color "Cyan"

    # 1. 备份日志（如果存在） - 使用不同变量名避免覆盖部署日志路径
    $sourceLogFile = "$targetDir\AutoSleep.log"
    if (Test-Path $sourceLogFile) {
        Copy-Item -Path $sourceLogFile -Destination $logBackupPath -Force -ErrorAction SilentlyContinue
        Write-Log "✅ 日志已备份到 $logBackupPath" -Color "Green"
    }else {
        Write-Log "ℹ️ 日志文件不存在，跳过备份" -Color "Yellow"
    }

    # 2. 提取旧配置（所有键值对）
    try {
        $oldConfig = Get-Content "$targetDir\settings.json" -Raw | ConvertFrom-Json
        Write-Log "✅ 旧配置已提取（共 $($oldConfig.PSObject.Properties.Count) 个字段）" -Color "Green"
    } catch {
        Write-Log "⚠️ 无法读取旧配置，将使用默认配置" -Color "Yellow"
        $oldConfig = $null
    }

    # 3. 执行现有 Uninstall.exe（完整卸载）
    $uninstallExe = "$targetDir\Uninstall.exe"
    if (Test-Path $uninstallExe) {
        Write-Log "正在执行卸载程序 $uninstallExe ..." -Color "Cyan"
        $proc = Start-Process -FilePath $uninstallExe -Wait -PassThru -WindowStyle Hidden
        if ($proc.ExitCode -eq 0) {
            Write-Log "✅ 卸载完成（退出码 0）" -Color "Green"
        } else {
            Write-Log "⚠️ 卸载退出码：$($proc.ExitCode)，继续安装..." -Color "Yellow"
        }
        # 等待文件系统同步
        Start-Sleep -Seconds 2
    } else {
        Write-Log "⚠️ 未找到 Uninstall.exe，跳过卸载步骤" -Color "Yellow"
    }
}

# 重新创建目标目录
if (-not (Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    Write-Log "✅ 已重新创建目录 $targetDir" -Color "Green"
}

# 复制主脚本
$sourceScript = Join-Path $PSScriptRoot "AutoSleep.ps1"
if (-not (Test-Path $sourceScript)) {
    Write-Log "❌ 未找到 AutoSleep.ps1，请确保它与部署脚本在同一目录。" -Color "Red"
    Read-Host "按 Enter 退出"
    exit 1
}
Copy-Item -Path $sourceScript -Destination $targetScript -Force
Write-Log "✅ 已复制 AutoSleep.ps1 到 $targetScript" -Color "Green"

# ---- 检测休眠状态并询问用户（仅在首次安装时） ----
$enableHibernate = $true
$defaultPowerAction = "Hibernate"

if (-not $isUpgrade) {
    Write-Log "检测到首次安装，检查休眠功能状态..." -Color "Cyan"

    $hibernateReg = Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Power" -Name "HibernateEnabled" -ErrorAction SilentlyContinue
    $hibernateCurrentlyOn = ($hibernateReg -and $hibernateReg.HibernateEnabled -eq 1)

    if ($hibernateCurrentlyOn) {
        Write-Log "✅ 休眠功能已开启，直接使用默认配置。" -Color "Green"
        $enableHibernate = $true
        $defaultPowerAction = "Hibernate"
    } else {
        $hiberFile = Get-ChildItem -Path "C:\" -Filter "hiberfil.sys" -Force -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hiberFile) {
            $hibernateSizeGB = [math]::Round($hiberFile.Length / 1GB, 1)
            $sizeHint = "当前休眠文件约 $hibernateSizeGB GB"
        } else {
            $totalMemoryGB = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
            $hibernateSizeGB = [math]::Round($totalMemoryGB * 0.5, 1)
            $sizeHint = "预计占用约 $hibernateSizeGB GB（通常为物理内存的 40%~60%）"
        }

        Write-Log "⚠️ 休眠功能未开启（$sizeHint）" -Color "Yellow"
        Write-Log "正在询问用户是否开启..." -Color "Cyan"

        Add-Type -AssemblyName System.Windows.Forms

        $msg = '休眠功能可让电脑在完全断电后恢复状态，但会在 C 盘占用空间。' + "`n`n" + $sizeHint + "`n`n" + '是否开启休眠功能？' + "`n" + '（选择“否”将使用睡眠模式，不占用额外磁盘空间）'
        $result = [System.Windows.Forms.MessageBox]::Show($msg, "AutoSleep - 休眠功能设置", "YesNo", "Question")

        if ($result -eq "Yes") {
            Write-Log "用户选择开启休眠功能。" -Color "Green"
            $enableHibernate = $true
            $defaultPowerAction = "Hibernate"
        } else {
            Write-Log "用户选择不开启休眠功能，将使用睡眠模式。" -Color "Yellow"
            $enableHibernate = $false
            $defaultPowerAction = "Sleep"
        }
    }
} else {
    # 升级安装：检测当前休眠状态，但不询问
    $hibernateReg = Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Power" -Name "HibernateEnabled" -ErrorAction SilentlyContinue
    $hibernateCurrentlyOn = ($hibernateReg -and $hibernateReg.HibernateEnabled -eq 1)
    $enableHibernate = $hibernateCurrentlyOn

    # 从现有配置读取 PowerAction（如果有旧配置备份，则使用旧值）
    if ($oldConfig -and $oldConfig.PowerAction) {
        $defaultPowerAction = $oldConfig.PowerAction
        Write-Log "✅ 升级安装，从旧配置恢复 PowerAction = $defaultPowerAction" -Color "Green"
    } else {
        # 没有旧配置或无法读取，使用默认
        $defaultPowerAction = "Hibernate"
        Write-Log "⚠️ 无法从旧配置读取 PowerAction，使用默认值" -Color "Yellow"
    }
}

# ---- 执行休眠开启/关闭 ----
if ($enableHibernate) {
    try {
        powercfg -h on
        Write-Log "✅ 休眠功能已开启。" -Color "Green"
    } catch {
        Write-Log "❌ 开启休眠失败，请手动执行 powercfg -h on。" -Color "Red"
    }
} else {
    try {
        powercfg -h off
        Write-Log "✅ 休眠功能已关闭。" -Color "Yellow"
    } catch {
        Write-Log "⚠️ 关闭休眠失败，可能已被其他程序占用。" -Color "Yellow"
    }
}

# ---- 生成默认 settings.json（含所有字段） ----
$defaultConfig = @{
    PowerAction          = $defaultPowerAction
    DurationMin          = 15
    CpuThreshold         = 30
    GpuThreshold         = 30
    Interval             = 5
    EnableGpuCheck       = $true
    EnableUserActivity   = $true
    EnableNetworkCheck   = $true
    NetworkThresholdKBps = 1024
    EnableProcessCheck   = $false
    ProtectedProcesses   = @()
    EnableTimeWindow     = $false
    TimeWindowStart      = 2
    TimeWindowEnd        = 7
    ClearLogOnNextRun    = $false
    EnableDiskCheck      = $true
    DiskThresholdKBps    = 10240
    EnableLogRotation    = $false
    LogRetentionDays     = 30
    LastRotationTime     = $null
}

$configPath = "$targetDir\settings.json"

# ---- 生成配置：优先使用旧配置，补充默认值 ----
if ($oldConfig) {
    # 先构建新配置：默认值 + 旧配置覆盖
    $newConfig = @{}
    foreach ($key in $defaultConfig.Keys) {
        $newConfig[$key] = $defaultConfig[$key]  # 先放默认值
    }
    foreach ($key in $oldConfig.PSObject.Properties.Name) {
        if ($newConfig.ContainsKey($key)) {
            $newConfig[$key] = $oldConfig.$key
            Write-Log "  恢复配置: $key = $($oldConfig.$key)" -Color "Gray"
        } else {
            # 旧配置中有新版本没有的字段（可能已废弃），忽略
            Write-Log "  忽略旧字段: $key（新版本已移除）" -Color "Gray"
        }
    }
    $newConfig | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
    Write-Log "✅ 已生成配置文件（恢复用户旧配置，补充新字段默认值）" -Color "Green"
} else {
    # 首次安装或无法读取旧配置，直接使用默认值
    $defaultConfig | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
    Write-Log "✅ 已生成默认配置文件 settings.json（使用默认值）" -Color "Green"
}

# ---- 恢复日志 ----
if ($isUpgrade -and (Test-Path $logBackupPath)) {
    if (Test-Path $sourceLogFile) {
        Write-Log "✅ 日志已恢复" -Color "Green"
    } else {
        Write-Log "⚠️ 恢复日志时捕获到异常：" -Color "Yellow"
    }
    Remove-Item $logBackupPath -Force -ErrorAction SilentlyContinue
}

# 复制设置程序
$sourceSettings = Join-Path $PSScriptRoot "Settings.ps1"
if (Test-Path $sourceSettings) {
    Copy-Item -Path $sourceSettings -Destination "$targetDir\Settings.ps1" -Force
    Write-Log "✅ 已复制设置程序 Settings.ps1" -Color "Green"

    $desktopPath = [Environment]::GetFolderPath("Desktop")
    $shortcutPath = Join-Path $desktopPath "AutoSleep 设置.lnk"
    $ws = New-Object -ComObject WScript.Shell
    $sc = $ws.CreateShortcut($shortcutPath)
    $sc.TargetPath = "powershell.exe"
    $sc.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$targetDir\Settings.ps1`""
    $sc.WorkingDirectory = $targetDir
    $sc.Save()
    Write-Log "✅ 已创建桌面快捷方式：$shortcutPath" -Color "Green"
} else {
    Write-Log "⚠️ 未找到 Settings.ps1，跳过设置程序复制。" -Color "Yellow"
}

# 复制帮助文档
$sourceReadme = Join-Path $PSScriptRoot "README.txt"
if (Test-Path $sourceReadme) {
    Copy-Item -Path $sourceReadme -Destination "$targetDir\README.txt" -Force
    Write-Log "✅ 已复制帮助文档 README.txt" -Color "Green"
} else {
    Write-Log "⚠️ 未找到 README.txt，跳过。" -Color "Yellow"
}

# 复制卸载 exe
$sourceUninstallExe = Join-Path $PSScriptRoot "Uninstall.exe"
if (Test-Path $sourceUninstallExe) {
    Copy-Item -Path $sourceUninstallExe -Destination "$targetDir\Uninstall.exe" -Force
    Write-Log "✅ 已复制卸载程序 Uninstall.exe" -Color "Green"
} else {
    Write-Log "⚠️ 未找到 Uninstall.exe，将只生成脚本卸载方式。" -Color "Yellow"
}

# 复制清空日志脚本
$sourceClearLog = Join-Path $PSScriptRoot "ClearLog.ps1"
if (Test-Path $sourceClearLog) {
    Copy-Item -Path $sourceClearLog -Destination "$targetDir\ClearLog.ps1" -Force
    Write-Log "✅ 已复制清空日志脚本 ClearLog.ps1" -Color "Green"
} else {
    Write-Log "⚠️ 未找到 ClearLog.ps1，跳过。" -Color "Yellow"
}

# 修改主脚本的日志路径
$scriptContent = Get-Content $targetScript -Raw
$newLogPath = "C:\ProgramData\AutoSleep\AutoSleep.log"
$pattern = 'Start-Transcript -Path\s+"[^"]*"'
$replacement = 'Start-Transcript -Path "' + $newLogPath + '"'
$scriptContent = $scriptContent -replace $pattern, $replacement
if ($scriptContent -notmatch 'Start-Transcript') {
    $scriptContent = $scriptContent -replace '(#Requires -RunAsAdministrator)', '$1' + "`nStart-Transcript -Path `"$newLogPath`" -Append"
}
Set-Content -Path $targetScript -Value $scriptContent -Encoding UTF8
Write-Log "✅ 已修改日志路径为 $newLogPath" -Color "Green"

Unblock-File -Path $targetScript -ErrorAction SilentlyContinue
Write-Log "✅ 已解除脚本阻止标记" -Color "Green"

# 创建计划任务
$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$targetScript`""
$trigger1 = New-ScheduledTaskTrigger -AtStartup
$trigger2 = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$triggers = @($trigger1, $trigger2)
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName "AutoSleep" -Action $action -Trigger $triggers -Settings $settings -User $env:USERNAME -RunLevel Highest -ErrorAction Stop | Out-Null
Write-Log "✅ 已创建计划任务 'AutoSleep'（开机 + 登录启动）" -Color "Green"

# ---- 生成备用卸载脚本 ----
$uninstallScriptPath = Join-Path $targetDir "Uninstall-AutoSleep.ps1"
$uninstallContent = @'
$installDir = "C:\ProgramData\AutoSleep"
$taskName = "AutoSleep"
$uninstallRegKey = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AutoSleep"

Write-Host "正在卸载 AutoSleep..." -ForegroundColor Cyan

$desktopPath = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktopPath "AutoSleep 设置.lnk"
if (Test-Path $shortcutPath) {
    Remove-Item $shortcutPath -Force
    Write-Host "已删除桌面快捷方式" -ForegroundColor Green
} else {
    Write-Host "桌面快捷方式不存在，跳过。" -ForegroundColor Yellow
}

$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($task) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Host "已删除计划任务" -ForegroundColor Green
} else {
    Write-Host "未找到计划任务，跳过。" -ForegroundColor Yellow
}

if (Test-Path $uninstallRegKey) {
    Remove-Item -Path $uninstallRegKey -Recurse -Force
    Write-Host "已删除注册表卸载信息" -ForegroundColor Green
} else {
    Write-Host "未找到注册表卸载信息，跳过。" -ForegroundColor Yellow
}

if (Test-Path $installDir) {
    Write-Host "正在清理安装目录..." -ForegroundColor Yellow
    Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile -Command `"Start-Sleep -Seconds 2; Remove-Item -Path '$installDir' -Recurse -Force -ErrorAction SilentlyContinue`"" -WindowStyle Hidden
    Write-Host "安装目录将在几秒后删除。" -ForegroundColor Green
} else {
    Write-Host "安装目录不存在，跳过。" -ForegroundColor Yellow
}

Write-Host "卸载完成。" -ForegroundColor Green
Read-Host "按 Enter 退出"
'@
Set-Content -Path $uninstallScriptPath -Value $uninstallContent -Encoding UTF8
Write-Log "✅ 已生成备用卸载脚本 $uninstallScriptPath" -Color "Green"

# ---- 注册到 Windows 已安装应用列表 ----
$regPath = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AutoSleep"
if (Test-Path $regPath) {
    Remove-Item -Path $regPath -Recurse -Force -ErrorAction SilentlyContinue
    Write-Log "已清理旧注册表项" -Color "Cyan"
}
New-Item -Path $regPath -Force | Out-Null
Set-ItemProperty -Path $regPath -Name "DisplayName" -Value "AutoSleep 智能休眠工具"
Set-ItemProperty -Path $regPath -Name "DisplayVersion" -Value "1.0.4"
Set-ItemProperty -Path $regPath -Name "Publisher" -Value "Cesium-developer"
Set-ItemProperty -Path $regPath -Name "InstallLocation" -Value $targetDir
Set-ItemProperty -Path $regPath -Name "UninstallString" -Value "$targetDir\Uninstall.exe"
Set-ItemProperty -Path $regPath -Name "NoModify" -Value 1
Set-ItemProperty -Path $regPath -Name "NoRepair" -Value 1
Write-Log "✅ 已注册到 Windows 已安装应用列表（卸载程序：Uninstall.exe）" -Color "Green"

# ---- 阶段3：验证部署 ----
Write-Log "----- 阶段3：验证部署 -----" -Color "Cyan"

Get-CimInstance -ClassName Win32_Process | Where-Object { $_.CommandLine -like "*AutoSleep.ps1*" -and $_.ProcessId -ne $PID } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Write-Log "已清理旧进程（排除当前部署脚本）" -Color "Cyan"

schtasks /run /tn "AutoSleep"
Write-Log "已通过 schtasks 启动 AutoSleep 任务" -Color "Cyan"
Start-Sleep -Seconds 5

$logFile = "C:\ProgramData\AutoSleep\AutoSleep.log"
if (Test-Path $logFile) {
    Write-Log "✅ 验证成功：日志文件已生成" -Color "Green"
    Get-Content $logFile -Tail 3 | ForEach-Object { Write-Log "  $_" -Color "Gray" }
    Write-Log "✅ 部署验证通过！" -Color "Green"
} else {
    Write-Log "❌ 验证失败：未检测到日志文件" -Color "Red"
}

Write-Log "----- 阶段4：部署完成 -----" -Color "Cyan"
Write-Log "部署详情：" -Color "White"
Write-Log "  主脚本位置：$targetScript" -Color "White"
Write-Log "  日志文件位置：$newLogPath" -Color "White"
Write-Log "  卸载程序位置：$targetDir\Uninstall.exe" -Color "White"
Write-Log "  运行模式：$(if (-not $gpuAvailable) { '仅 CPU' } else { 'CPU + GPU' })" -Color "White"
Write-Log "  休眠状态：$(if ($enableHibernate) { '已开启' } else { '已关闭' })" -Color "White"
Write-Log "✅ 已注册到 Windows 设置（开始菜单 → 设置 → 应用 → 已安装的应用）" -Color "Green"
Write-Log "部署日志已保存至：$LogPath" -Color "Cyan"
Write-Host "按 Enter 退出..." -ForegroundColor Cyan
Read-Host