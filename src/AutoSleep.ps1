<#
.SYNOPSIS
    智能电源管理守护工具：多条件感知，任务完成后自动睡眠/休眠。

.DESCRIPTION
    AutoSleep 持续监控 CPU、GPU、磁盘、网络、用户输入和进程白名单，
    当所有条件满足并持续空闲达到设定时间后，自动触发睡眠或休眠。

    本脚本通过配置文件 C:\ProgramData\AutoSleep\settings.json 控制参数，
    无需命令行参数。

.EXAMPLE
    # 直接运行（通常由计划任务自动启动）
    .\AutoSleep.ps1

.LINK
    "https://github.com/Cesium-developer/AutoSleep"
#>

# Copyright (c) 2026 Cesium-developer. Licensed under the MIT License.

#Requires -RunAsAdministrator

Start-Transcript -Path "C:\ProgramData\AutoSleep\AutoSleep.log" -Append

$configPath = "C:\ProgramData\AutoSleep\settings.json"

if (Test-Path $configPath) {
    $config = Get-Content $configPath -Raw | ConvertFrom-Json
} else {
    $config = @{
        PowerAction          = "Hibernate"
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
    }
    $config | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
}

if ($null -eq $config.ClearLogOnNextRun) {
    $config | Add-Member -MemberType NoteProperty -Name "ClearLogOnNextRun" -Value $false -Force
}
if ($null -eq $config.EnableDiskCheck) {
    $config | Add-Member -MemberType NoteProperty -Name "EnableDiskCheck" -Value $true -Force
}
if ($null -eq $config.DiskThresholdKBps) {
    $config | Add-Member -MemberType NoteProperty -Name "DiskThresholdKBps" -Value 10240 -Force
}
if ($null -eq $config.EnableLogRotation) {
    $config | Add-Member -MemberType NoteProperty -Name "EnableLogRotation" -Value $false -Force
}
if ($null -eq $config.LogRetentionDays) {
    $config | Add-Member -MemberType NoteProperty -Name "LogRetentionDays" -Value 30 -Force
}
if ($null -eq $config.CustomLogicEnabled) {
    $config | Add-Member -MemberType NoteProperty -Name "CustomLogicEnabled" -Value $false -Force
}
if ($null -eq $config.CustomLogicTree) {
    $config | Add-Member -MemberType NoteProperty -Name "CustomLogicTree" -Value $null -Force
}

$powerAction          = $config.PowerAction
$durationMin          = $config.DurationMin
$cpuThreshold         = $config.CpuThreshold
$gpuThreshold         = $config.GpuThreshold
$enableGpu            = $config.EnableGpuCheck
$enableUser           = $config.EnableUserActivity
$enableNetwork        = $config.EnableNetworkCheck
$networkThresholdKBps = $config.NetworkThresholdKBps
$enableDisk           = $config.EnableDiskCheck
$diskThresholdKBps    = $config.DiskThresholdKBps
$enableProcess        = $config.EnableProcessCheck
$protectedProcesses   = $config.ProtectedProcesses
$enableTimeWindow     = $config.EnableTimeWindow
$timeWindowStart      = $config.TimeWindowStart
$timeWindowEnd        = $config.TimeWindowEnd
$enableLogRotation    = $config.EnableLogRotation
$logRetentionDays     = $config.LogRetentionDays
$cooldownUntil = (Get-Date).AddDays(-1)

$sleepSeconds  = [Math]::Max(1, $config.Interval - 2)
$interval      = $config.Interval

$elapsed = 0
$lastCheckTime = Get-Date
$lastLogTime = Get-Date
$lastRotationCheck = Get-Date
$lastTick = [Environment]::TickCount64   # 单调时钟基线：真实睡眠(S3/S4)时冻结、墙钟继续走，用于区分真唤醒与有意睡眠

Write-Host "Monitoring started. Idle for $durationMin minute(s) will trigger $powerAction."

if ($enableUser) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class UserInput {
    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    public struct LASTINPUTINFO {
        public uint cbSize;
        public uint dwTime;
    }
    public static uint GetIdleMilliseconds() {
        LASTINPUTINFO lii = new LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
        if (GetLastInputInfo(ref lii)) {
            uint lastInput = lii.dwTime;
            uint current = (uint)Environment.TickCount;
            return current - lastInput;
        }
        return 0;
    }
}
"@
}

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class PowerManager {
    [DllImport("powrprof.dll")]
    public static extern bool SetSuspendState(bool Hibernate, bool ForceCritical, bool DisableWakeEvent);
}
"@

# ---- 倒计时窗口函数 ----
function Show-CountdownDialog {
    param([int]$seconds = 10)
    Add-Type -AssemblyName System.Windows.Forms
    $form = New-Object System.Windows.Forms.Form
    $form.Text = "AutoSleep 提醒"
    $form.Size = New-Object System.Drawing.Size(350, 130)
    $form.StartPosition = "CenterScreen"
    $form.FormBorderStyle = "FixedDialog"
    $form.ControlBox = $false
    $form.TopMost = $true

    $label = New-Object System.Windows.Forms.Label
    $label.Text = "电脑将在 $seconds 秒后进入 $powerAction，点击取消可阻止。"
    $label.Location = New-Object System.Drawing.Point(15, 20)
    $label.Size = New-Object System.Drawing.Size(310, 30)
    $form.Controls.Add($label)

    $button = New-Object System.Windows.Forms.Button
    $button.Text = "取消"
    $button.Location = New-Object System.Drawing.Point(125, 60)
    $button.Size = New-Object System.Drawing.Size(80, 25)
    $form.Controls.Add($button)

    $script:canceled = $false
    $button.Add_Click({
        $script:canceled = $true
        $form.Close()
    })

    $form.Show()
    while ($seconds -gt 0 -and -not $script:canceled) {
        Start-Sleep -Seconds 1
        $seconds--
        $label.Text = "电脑将在 $seconds 秒后进入 $powerAction，点击取消可阻止。"
        [System.Windows.Forms.Application]::DoEvents()
    }

    if ($form.IsHandleCreated) {
        $form.Close()
        $form.Dispose()
    }

    return $script:canceled
}

# ---- 日志轮转函数 ----
function Invoke-LogRotation {
    $logFile = "C:\ProgramData\AutoSleep\AutoSleep.log"
    if (-not (Test-Path $logFile)) { return }

    $config = Get-Content $configPath -Raw | ConvertFrom-Json
    if ($null -eq $config.LastRotationTime) {
        $fileInfo = Get-Item $logFile
        $initTime = $fileInfo.CreationTime.ToString("yyyy-MM-dd HH:mm:ss")
        $config | Add-Member -MemberType NoteProperty -Name "LastRotationTime" -Value $initTime -Force
        $config | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
    }

    $lastRotationStr = $config.LastRotationTime
    if ($lastRotationStr -is [string]) {
        $lastRotation = [datetime]::ParseExact($lastRotationStr, "yyyy-MM-dd HH:mm:ss", $null)
    } else {
        $lastRotation = [datetime]$lastRotationStr
    }
    $ageDays = ((Get-Date) - $lastRotation).Days

    if ($ageDays -ge $logRetentionDays) {
        Write-Host "$(Get-Date -Format HH:mm:ss) Log rotation triggered, launching ClearLog.ps1..."

        # 直接启动独立的清空脚本文件（无转义问题）
        $clearScript = "C:\ProgramData\AutoSleep\ClearLog.ps1"
        Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$clearScript`"" -WindowStyle Hidden

        Write-Host "$(Get-Date -Format HH:mm:ss) ClearLog.ps1 launched."
    }
}

# ---- 自定义逻辑求值（对齐 C# RuleEngine：节点级数值 + FailedConditions 收集）----
function Evaluate-CustomLogic {
    param(
        [object]$Tree,
        [hashtable]$Values,
        [hashtable]$Metrics = $null,
        [System.Collections.ArrayList]$Failed = $null
    )
    if ($null -eq $Failed) { $Failed = New-Object System.Collections.ArrayList }
    if ($null -eq $Tree) { return @{ idle = $false; action = "none"; failed = $Failed } }

    switch ($Tree.type) {
        "program" {
            if (-not $Tree.actions) { return @{ idle = $false; action = "none"; failed = $Failed } }
            $result = @{ idle = $false; action = "none"; failed = $Failed }
            foreach ($action in $Tree.actions) {
                $result = Evaluate-CustomLogic -Tree $action -Values $Values -Metrics $Metrics -Failed $Failed
            }
            return $result
        }
        "condition" {
            $cond = $Tree.condition
            $idle = $false
            # 节点自带数值（编辑器保存阶段已补齐）：原始指标 vs 节点阈值
            if ($null -ne $Tree.value -and $null -ne $Metrics -and $Metrics.ContainsKey($cond)) {
                $threshold = 0.0
                if (-not [double]::TryParse($Tree.value, [ref]$threshold)) {
                    return @{ idle = $false; action = "none"; failed = $Failed }
                }
                $metric = [double]$Metrics[$cond]
                # User 是"无操作秒数 >= 阈值"才空闲；其余资源类都是"低于阈值"才空闲
                if ($cond -eq "User") { $idle = ($metric -ge $threshold) } else { $idle = ($metric -lt $threshold) }
            } elseif ($Values.ContainsKey($cond)) {
                $idle = [bool]$Values[$cond]
            } else {
                return @{ idle = $false; action = "none"; failed = $Failed }
            }
            # 收集失败叶子条件（仅供日志原因行；判定结果不受影响）
            if (-not $idle -and -not $Failed.Contains($cond)) { [void]$Failed.Add($cond) }
            return @{ idle = $idle; action = "none"; failed = $Failed }
        }
        "logic" {
            $op = $Tree.operator
            $children = $Tree.children
            if ($op -eq "AND") {
                $allIdle = $true
                foreach ($child in $children) {
                    $childResult = Evaluate-CustomLogic -Tree $child -Values $Values -Metrics $Metrics -Failed $Failed
                    if (-not $childResult.idle) { $allIdle = $false }
                }
                return @{ idle = $allIdle; action = "none"; failed = $Failed }
            } elseif ($op -eq "OR") {
                $anyIdle = $false
                foreach ($child in $children) {
                    $childResult = Evaluate-CustomLogic -Tree $child -Values $Values -Metrics $Metrics -Failed $Failed
                    if ($childResult.idle) { $anyIdle = $true }
                }
                return @{ idle = $anyIdle; action = "none"; failed = $Failed }
            } elseif ($op -eq "NOT") {
                $childResult = Evaluate-CustomLogic -Tree $children[0] -Values $Values -Metrics $Metrics -Failed $Failed
                return @{ idle = -not $childResult.idle; action = "none"; failed = $Failed }
            }
            return @{ idle = $false; action = "none"; failed = $Failed }
        }
        "control" {
            # if 分支
            if ($Tree.condition) {
                $condResult = Evaluate-CustomLogic -Tree $Tree.condition -Values $Values -Metrics $Metrics -Failed $Failed
                if ($condResult.idle) {
                    if ($null -ne $Tree.then) {
                        return Evaluate-CustomLogic -Tree $Tree.then -Values $Values -Metrics $Metrics -Failed $Failed
                    }
                    return @{ idle = $true; action = "none"; failed = $Failed }
                }
            }
            # elif 列表
            if ($Tree.elif -and $Tree.elif.Count -gt 0) {
                foreach ($elif in $Tree.elif) {
                    $elifCondResult = Evaluate-CustomLogic -Tree $elif.condition -Values $Values -Metrics $Metrics -Failed $Failed
                    if ($elifCondResult.idle) {
                        if ($null -ne $elif.then) {
                            return Evaluate-CustomLogic -Tree $elif.then -Values $Values -Metrics $Metrics -Failed $Failed
                        }
                        return @{ idle = $true; action = "none"; failed = $Failed }
                    }
                }
            }
            # else 分支
            if ($null -ne $Tree.else) {
                return Evaluate-CustomLogic -Tree $Tree.else -Values $Values -Metrics $Metrics -Failed $Failed
            }
            return @{ idle = $false; action = "none"; failed = $Failed }
        }
        "action" {
            switch ($Tree.action) {
                "reset_timer"    { return @{ idle = $false; action = "reset_timer"; failed = $Failed } }
                "continue_timer" { return @{ idle = $true;  action = "continue_timer"; failed = $Failed } }
                "sleep"          { return @{ idle = $true;  action = "sleep"; failed = $Failed } }
                "nothing"        { return @{ idle = $false; action = "nothing"; failed = $Failed } }
                default          { return @{ idle = $true;  action = "continue_timer"; failed = $Failed } }
            }
        }
        "sequence" {
            if (-not $Tree.actions) { return @{ idle = $false; action = "none"; failed = $Failed } }
            $result = @{ idle = $false; action = "none"; failed = $Failed }
            foreach ($action in $Tree.actions) {
                $result = Evaluate-CustomLogic -Tree $action -Values $Values -Metrics $Metrics -Failed $Failed
            }
            return $result
        }
        default {
            Write-Host "警告：未知节点类型 '$($Tree.type)'" -ForegroundColor Yellow
            return @{ idle = $false; action = "none"; failed = $Failed }
        }
    }
}

# ---- 统一日志输出层（节流 = 配置 Interval；Status/Idle 两套格式，字段集合一致）----
function Add-Failed {
    param([System.Collections.ArrayList]$List, [string]$Cond)
    if (-not $List.Contains($Cond)) { [void]$List.Add($Cond) }
}

# 非计时周期：Status 首行 + 决策者原因行（映射回源码原有行族）+ 全指标详情行
function Write-StatusBlock {
    param(
        [System.Collections.ArrayList]$Reasons,
        [double]$Cpu, [double]$Gpu, [double]$NetKBps, [double]$DiskKBps,
        [string]$RunningProc, [bool]$InWindow, [double]$IdleMs
    )
    if (($(Get-Date) - $script:lastLogTime).TotalSeconds -lt $script:interval) { return }
    $ts = Get-Date -Format HH:mm:ss
    Write-Host "$ts Status: Not idle (CPU: $($Cpu.ToString('F1'))%, GPU: $($Gpu.ToString('F1'))%)"

    $cpuGpuReported = $false
    $hasReason = $false
    foreach ($cond in $Reasons) {
        $hasReason = $true
        switch ($cond) {
            "User"       { Write-Host "$ts User activity detected, timer reset." }
            "Network"    { Write-Host "$ts Network activity detected ($($NetKBps.ToString('F1')) KB/s), timer reset." }
            "Disk"       { Write-Host "$ts Disk activity detected ($($DiskKBps.ToString('F1')) KB/s), timer reset." }
            "Process"    { if ($RunningProc) { Write-Host "$ts Protected process pattern '$RunningProc' is running, timer reset." } }
            "TimeWindow" { Write-Host "$ts Outside time window ($($script:timeWindowStart)-$($script:timeWindowEnd)), idle mode." }
            "CPU"        { if (-not $cpuGpuReported) { Write-Host "$ts Load recovered, timer reset (CPU: $($Cpu.ToString('F1'))%, GPU: $($Gpu.ToString('F1'))%)"; $cpuGpuReported = $true } }
            "GPU"        { if (-not $cpuGpuReported) { Write-Host "$ts Load recovered, timer reset (CPU: $($Cpu.ToString('F1'))%, GPU: $($Gpu.ToString('F1'))%)"; $cpuGpuReported = $true } }
            default      { Write-Host "$ts Timer reset by custom logic" }
        }
    }
    if (-not $hasReason) { Write-Host "$ts Timer reset by custom logic" }

    # 详情行（与计时块同源生成）
    if ($script:enableNetwork) { Write-Host "$ts Network: $($NetKBps.ToString('F1')) KB/s" }
    if ($script:enableDisk) { Write-Host "$ts Disk: $($DiskKBps.ToString('F1')) KB/s" }
    if ($script:enableProcess -and $script:protectedProcesses.Count -gt 0) { Write-Host "$ts Protected: $RunningProc" }
    if ($script:enableTimeWindow) { Write-Host "$ts TimeWindow: $InWindow" }
    Write-Host "$ts User: $([math]::Round($IdleMs / 1000))s"
    $script:lastLogTime = Get-Date
}

# 计时周期：Idle 首行 + 全指标详情行（含 User，字段与状态块一致）
function Write-IdleBlock {
    param(
        [double]$Elapsed, [double]$Cpu, [double]$Gpu, [double]$NetKBps, [double]$DiskKBps,
        [string]$RunningProc, [bool]$InWindow, [double]$IdleMs
    )
    if (($(Get-Date) - $script:lastLogTime).TotalSeconds -lt $script:interval) { return }
    $ts = Get-Date -Format HH:mm:ss
    Write-Host "$ts Idle: $($Elapsed.ToString('F1')) sec (CPU: $($Cpu.ToString('F1'))%, GPU: $($Gpu.ToString('F1'))%)"
    if ($script:enableNetwork) { Write-Host "$ts Network: $($NetKBps.ToString('F1')) KB/s" }
    if ($script:enableDisk) { Write-Host "$ts Disk: $($DiskKBps.ToString('F1')) KB/s" }
    if ($script:enableProcess -and $script:protectedProcesses.Count -gt 0) { Write-Host "$ts Protected: $RunningProc" }
    if ($script:enableTimeWindow) { Write-Host "$ts TimeWindow: $InWindow" }
    Write-Host "$ts User: $([math]::Round($IdleMs / 1000))s"
    $script:lastLogTime = Get-Date
}

while ($true) {
    $now = Get-Date
    $deltaSeconds = ($now - $lastCheckTime).TotalSeconds
    $lastCheckTime = $now

    $tickNow = [Environment]::TickCount64
    $tickDeltaSeconds = ($tickNow - $lastTick) / 1000.0
    $lastTick = $tickNow

    # ---- 唤醒检测（永远优先） ----
    # 仅当墙钟前进量远大于单调时钟前进量时才判定为真实睡眠/挂起：
    # 真实睡眠(S3/S4)时 tick 冻结而墙钟继续走（差≈睡眠时长）；
    # 时间窗口分支的 60s 有意睡眠两者同步增长（差≈0），不会误判。
    $suspendSeconds = $deltaSeconds - $tickDeltaSeconds
    if ($suspendSeconds -gt 30) {
        Write-Host "$(Get-Date -Format HH:mm:ss) Wake from sleep, resetting timer. (wall=$([math]::Round($deltaSeconds))s, tick=$([math]::Round($tickDeltaSeconds))s)"
        $elapsed = 0
        $lastCheckTime = $now
        Stop-Transcript -ErrorAction SilentlyContinue
        Start-Transcript -Path "C:\ProgramData\AutoSleep\AutoSleep.log" -Append
        Start-Sleep -Seconds $sleepSeconds
        continue
    }

    # ---- 日志清空请求（永远优先） ----
    if ($config.ClearLogOnNextRun -eq $true) {
        Write-Host "$(Get-Date -Format HH:mm:ss) Clear log requested, resetting transcript..."
        Stop-Transcript -ErrorAction SilentlyContinue
        Remove-Item -Path "C:\ProgramData\AutoSleep\AutoSleep.log" -Force -ErrorAction SilentlyContinue
        Start-Transcript -Path "C:\ProgramData\AutoSleep\AutoSleep.log" -Append
        $config.ClearLogOnNextRun = $false
        $config | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
        Write-Host "Log cleared and transcript restarted."
    }

    # ---- 日志轮转（每1小时检查一次） ----
    if ($enableLogRotation -and ((Get-Date) - $lastRotationCheck).TotalHours -ge 1) {
        Invoke-LogRotation
        $lastRotationCheck = Get-Date
    }

    # ============================================================
    # 采集所有原始数据（两条线路共用）
    # ============================================================

    # ---- 时间窗口（始终按配置 Start/End 计算；EnableTimeWindow 总开关只控制硬编码分支，不影响自定义逻辑里 TimeWindow 节点判定）----
    $hour = (Get-Date).Hour
    $start = $timeWindowStart
    $end = $timeWindowEnd
    if ($start -lt $end) {
        $inWindow = ($hour -ge $start -and $hour -lt $end)
    } else {
        $inWindow = ($hour -ge $start -or $hour -lt $end)
    }

    # ---- 用户活动 ----
    $idleMs = 999999
    if ($enableUser) {
        $idleMs = [UserInput]::GetIdleMilliseconds()
    }

    # ---- 网络活动 ----
    $netKBps = 0
    if ($enableNetwork) {
        try {
            $netSamples = (Get-Counter "\Network Interface(*)\Bytes Total/sec" -ErrorAction Stop).CounterSamples
            $netBytesPerSec = ($netSamples | Measure-Object -Property CookedValue -Sum).Sum
            $netKBps = $netBytesPerSec / 1024
        } catch {
            # 网络计数器不可用
        }
    }

    # ---- 磁盘活动 ----
    $diskKBps = 0
    if ($enableDisk) {
        try {
            $diskSamples = (Get-Counter "\PhysicalDisk(*)\Disk Bytes/sec" -ErrorAction Stop).CounterSamples
            $diskBytesPerSec = ($diskSamples | Measure-Object -Property CookedValue -Sum).Sum
            $diskKBps = $diskBytesPerSec / 1024
        } catch {
            # 磁盘计数器不可用
        }
    }

    # ---- 进程白名单 ----
    # enableProcess 控制硬编码分支（分支2）的进程判定；自定义逻辑启用时 Process 条件不受该开关钳制，需恒采集
    $runningProc = $null
    if (($enableProcess -or ($config.CustomLogicEnabled -and $config.CustomLogicTree)) -and $protectedProcesses.Count -gt 0) {
        $allProcesses = Get-Process | ForEach-Object { $_.ProcessName }
        foreach ($pattern in $protectedProcesses) {
            if ($allProcesses -match $pattern) {
                $runningProc = $pattern
                break
            }
        }
    }

    # ---- CPU / GPU ----
    try {
        $cpuSample = Get-Counter '\Processor Information(_Total)\% Processor Time' -ErrorAction Stop
        $cpu = [double]$cpuSample.CounterSamples.CookedValue
    } catch {
        Write-Host "$(Get-Date -Format HH:mm:ss) 警告：Processor Information 计数器不可用，降级到 Processor" -ForegroundColor Yellow
        try {
            $cpuSample = Get-Counter '\Processor(_Total)\% Processor Time' -ErrorAction Stop
            $cpu = [double]$cpuSample.CounterSamples.CookedValue
        } catch {
            Write-Host "$(Get-Date -Format HH:mm:ss) 警告：读取 CPU 计数器失败：$_" -ForegroundColor Yellow
            $cpu = 100.0
        }
    }

    if ($enableGpu) {
        try {
            $gpuSamples = (Get-Counter '\GPU Engine(*)\Utilization Percentage').CounterSamples
            $gpu = ($gpuSamples | Measure-Object -Property CookedValue -Maximum).Maximum
            if ($null -eq $gpu) { $gpu = 0 }
        } catch {
            $gpu = 0
        }
    } else {
        $gpu = 0
    }

    # ---- 条件变量化 ----
    $cpuIdle = $cpu -lt $cpuThreshold
    $gpuIdle = $enableGpu -eq $false -or $gpu -lt $gpuThreshold
    $diskIdle = $enableDisk -eq $false -or $diskKBps -lt $diskThresholdKBps
    $networkIdle = $enableNetwork -eq $false -or $netKBps -lt $networkThresholdKBps
    $userIdle = $enableUser -eq $false -or $idleMs -ge 3000
    $processIdle = $enableProcess -eq $false -or (-not $runningProc)
    $timeWindowIdle = $enableTimeWindow -eq $false -or $inWindow

    # ============================================================
    # 分支1：自定义逻辑（如果启用）
    # ============================================================
    if ($config.CustomLogicEnabled -and $config.CustomLogicTree) {
        $failedReasons = New-Object System.Collections.ArrayList
        # 自定义逻辑的值字典：一律使用未受启用开关门控的原始判定，
        # 开关只在硬编码分支（分支2）起作用；TimeWindow 沿用已修好的原始 $inWindow
        $customValues = @{
            "CPU"        = $cpu -lt $cpuThreshold
            "GPU"        = $gpu -lt $gpuThreshold
            "Disk"       = $diskKBps -lt $diskThresholdKBps
            "Network"    = $netKBps -lt $networkThresholdKBps
            "User"       = $idleMs -ge 3000
            "Process"    = -not $runningProc
            "TimeWindow" = $inWindow
        }
        # 原始指标（供自定义逻辑节点级阈值使用；无节点数值时引擎回退上面的布尔）
        $customMetrics = @{
            "CPU"     = [double]$cpu
            "GPU"     = [double]$gpu
            "Disk"    = [double]$diskKBps
            "Network" = [double]$netKBps
            "User"    = [double]($idleMs / 1000)
        }
        $customResult = Evaluate-CustomLogic -Tree $config.CustomLogicTree -Values $customValues -Metrics $customMetrics
        $idle = $customResult.idle
        $action = $customResult.action
        foreach ($fc in $customResult.failed) {
            Add-Failed -List $failedReasons -Cond $fc
        }

        if ($idle) {
            # 如果动作是 sleep，立即触发睡眠
            if ($action -eq "sleep") {
                Write-Host "$(Get-Date -Format HH:mm:ss) Custom logic: immediate sleep triggered"
                $elapsed = $durationMin * 60   # 强制满足触发条件
                # 直接跳到触发逻辑，不累加计时器
            } else {
                # 正常累加计时器（continue_timer 和其他动作）
                $elapsed += $deltaSeconds
            }
        } else {
            if ($action -eq "reset_timer") {
                $elapsed = 0
            } elseif ($action -eq "nothing") {
                # 什么都不做，保持 elapsed 不变
            } else {
                $elapsed = 0
            }
        }

        # ---- 统一日志输出（节流 = 配置 Interval；两套格式字段集合一致）----
        if ($idle) {
            Write-IdleBlock -Elapsed $elapsed -Cpu $cpu -Gpu $gpu -NetKBps $netKBps -DiskKBps $diskKBps -RunningProc $runningProc -InWindow $inWindow -IdleMs $idleMs
        } else {
            Write-StatusBlock -Reasons $failedReasons -Cpu $cpu -Gpu $gpu -NetKBps $netKBps -DiskKBps $diskKBps -RunningProc $runningProc -InWindow $inWindow -IdleMs $idleMs
        }

        # ---- 触发 ----
        if ($elapsed -ge ($durationMin * 60)) {
            # 检查冷却期
            if ((Get-Date) -lt $cooldownUntil) {
                Write-Host "$(Get-Date -Format HH:mm:ss) In cooldown period (until $cooldownUntil), skipping sleep."
                $elapsed = 0
                Start-Sleep -Seconds $sleepSeconds
                continue
            }

            Write-Host "$(Get-Date -Format HH:mm:ss) Condition met, showing countdown..."
            $canceled = Show-CountdownDialog -seconds 10
            if ($canceled) {
                Write-Host "$(Get-Date -Format HH:mm:ss) User canceled sleep."
                # 设置冷却期 10 分钟
                $cooldownUntil = (Get-Date).AddMinutes(10)
                Write-Host "$(Get-Date -Format HH:mm:ss) Cooldown set until $cooldownUntil"
                $elapsed = 0
                Start-Sleep -Seconds $sleepSeconds
                continue
            }

            Write-Host "Executing $powerAction in 5 seconds..."
            Start-Sleep -Seconds 5
            Stop-Transcript

            if ($powerAction -eq "Hibernate") {
                shutdown /h
            } elseif ($powerAction -eq "Sleep") {
                [PowerManager]::SetSuspendState($false, $true, $false)
            } else {
                Write-Host "Unknown PowerAction: $powerAction" -ForegroundColor Red
            }

            Write-Host "Resuming monitoring after $powerAction..."
            $elapsed = 0
            $lastLogTime = Get-Date
            Start-Sleep -Seconds 5
        }

        Write-Host "Log Rotation checking time: $(((Get-Date) - $lastRotationCheck).TotalHours.ToString('F2')) hour (Default checking is 1 hour.)"
        Start-Sleep -Seconds $sleepSeconds
        continue
    }

    # ============================================================
    # 分支2：原有硬编码逻辑（自定义未启用时执行）
    # ============================================================

    $failedReasons = New-Object System.Collections.ArrayList

    # ---- 时间窗口 ----
    if ($enableTimeWindow -and -not $inWindow) {
        Add-Failed -List $failedReasons -Cond "TimeWindow"
        $elapsed = 0
        Write-StatusBlock -Reasons $failedReasons -Cpu $cpu -Gpu $gpu -NetKBps $netKBps -DiskKBps $diskKBps -RunningProc $runningProc -InWindow $inWindow -IdleMs $idleMs
        Start-Sleep -Seconds $sleepSeconds
        continue
    }

    # ---- 用户活动 ----
    if ($enableUser -and $idleMs -lt 3000) {
        Add-Failed -List $failedReasons -Cond "User"
        $elapsed = 0
        Write-StatusBlock -Reasons $failedReasons -Cpu $cpu -Gpu $gpu -NetKBps $netKBps -DiskKBps $diskKBps -RunningProc $runningProc -InWindow $inWindow -IdleMs $idleMs
        Start-Sleep -Seconds $sleepSeconds
        continue
    }

    # ---- 网络活动 ----
    if ($enableNetwork -and $netKBps -gt $networkThresholdKBps) {
        Add-Failed -List $failedReasons -Cond "Network"
        $elapsed = 0
    }

    # ---- 磁盘活动 ----
    if ($enableDisk -and $diskKBps -gt $diskThresholdKBps) {
        Add-Failed -List $failedReasons -Cond "Disk"
        $elapsed = 0
    }

    # ---- 进程白名单 ----
    if ($enableProcess -and $protectedProcesses.Count -gt 0 -and $runningProc) {
        Add-Failed -List $failedReasons -Cond "Process"
        $elapsed = 0
    }

    # ---- CPU / GPU 空闲判断（硬编码 AND）----
    $idle = $cpuIdle -and $gpuIdle -and $diskIdle -and $networkIdle -and $userIdle -and $processIdle -and $timeWindowIdle

    if ($idle) {
        $elapsed += $deltaSeconds
    } else {
        # CPU/GPU 无独立违反分支，在此补充失败原因（其余已在上面收集）
        if (-not $cpuIdle) { Add-Failed -List $failedReasons -Cond "CPU" }
        if (-not $gpuIdle) { Add-Failed -List $failedReasons -Cond "GPU" }
        $elapsed = 0
    }

    # ---- 统一日志输出（节流 = 配置 Interval；两套格式字段集合一致）----
    if ($idle) {
        Write-IdleBlock -Elapsed $elapsed -Cpu $cpu -Gpu $gpu -NetKBps $netKBps -DiskKBps $diskKBps -RunningProc $runningProc -InWindow $inWindow -IdleMs $idleMs
    } else {
        Write-StatusBlock -Reasons $failedReasons -Cpu $cpu -Gpu $gpu -NetKBps $netKBps -DiskKBps $diskKBps -RunningProc $runningProc -InWindow $inWindow -IdleMs $idleMs
    }

    # ---- 触发 ----
    if ($elapsed -ge ($durationMin * 60)) {
        # 检查冷却期
        if ((Get-Date) -lt $cooldownUntil) {
            Write-Host "$(Get-Date -Format HH:mm:ss) In cooldown period (until $cooldownUntil), skipping sleep."
            $elapsed = 0
            Start-Sleep -Seconds $sleepSeconds
            continue
        }

        Write-Host "$(Get-Date -Format HH:mm:ss) Condition met, showing countdown..."
        $canceled = Show-CountdownDialog -seconds 10
        if ($canceled) {
            Write-Host "$(Get-Date -Format HH:mm:ss) User canceled sleep."
            # 设置冷却期 10 分钟
            $cooldownUntil = (Get-Date).AddMinutes(10)
            Write-Host "$(Get-Date -Format HH:mm:ss) Cooldown set until $cooldownUntil"
            $elapsed = 0
            Start-Sleep -Seconds $sleepSeconds
            continue
        }

        Write-Host "Executing $powerAction in 5 seconds..."
        Start-Sleep -Seconds 5
        Stop-Transcript

        if ($powerAction -eq "Hibernate") {
            shutdown /h
        } elseif ($powerAction -eq "Sleep") {
            [PowerManager]::SetSuspendState($false, $true, $false)
        } else {
            Write-Host "Unknown PowerAction: $powerAction" -ForegroundColor Red
        }

        Write-Host "Resuming monitoring after $powerAction..."
        $elapsed = 0
        $lastLogTime = Get-Date
        Start-Sleep -Seconds 5
    }

    Write-Host "Log Rotation checking time: $(((Get-Date) - $lastRotationCheck).TotalHours.ToString('F2')) hour (Default checking is 1 hour.)"
    Start-Sleep -Seconds $sleepSeconds
}