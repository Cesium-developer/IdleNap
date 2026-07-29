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

$elapsed = 0
$lastCheckTime = Get-Date
$lastLogTime = Get-Date
$lastRotationCheck = Get-Date

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

# ---- 自定义逻辑求值 ----
function Evaluate-CustomLogic {
    param(
        [object]$Tree,
        [hashtable]$Values
    )
    if ($null -eq $Tree) { return @{ idle = $false; action = "none" } }

    switch ($Tree.type) {
        "program" {
            if (-not $Tree.actions) { return @{ idle = $false; action = "none" } }
            $result = @{ idle = $false; action = "none" }
            foreach ($action in $Tree.actions) {
                $result = Evaluate-CustomLogic -Tree $action -Values $Values
                # 如果某个 action 返回了非 "none" 的动作，可以提前返回（或者继续执行后续）
                # 但通常 program 按顺序执行，我们返回最后一个的结果
            }
            return $result
        }
        "condition" {
            $cond = $Tree.condition
            if ($Values.ContainsKey($cond)) {
                return @{ idle = [bool]$Values[$cond]; action = "none" }
            }
            return @{ idle = $false; action = "none" }
        }
        "logic" {
            $op = $Tree.operator
            $children = $Tree.children
            if ($op -eq "AND") {
                foreach ($child in $children) {
                    $childResult = Evaluate-CustomLogic -Tree $child -Values $Values
                    if (-not $childResult.idle) {
                        return @{ idle = $false; action = "none" }
                    }
                }
                return @{ idle = $true; action = "none" }
            } elseif ($op -eq "OR") {
                foreach ($child in $children) {
                    $childResult = Evaluate-CustomLogic -Tree $child -Values $Values
                    if ($childResult.idle) {
                        return @{ idle = $true; action = "none" }
                    }
                }
                return @{ idle = $false; action = "none" }
            } elseif ($op -eq "NOT") {
                $childResult = Evaluate-CustomLogic -Tree $children[0] -Values $Values
                return @{ idle = -not $childResult.idle; action = "none" }
            }
            return @{ idle = $false; action = "none" }
        }
        "control" {
            # if 分支
            if ($Tree.condition) {
                $condResult = Evaluate-CustomLogic -Tree $Tree.condition -Values $Values
                if ($condResult.idle) {
                    if ($Tree.then -ne $null) {
                        return Evaluate-CustomLogic -Tree $Tree.then -Values $Values
                    }
                    return @{ idle = $true; action = "none" }
                }
            }
            # elif 列表
            if ($Tree.elif -and $Tree.elif.Count -gt 0) {
                foreach ($elif in $Tree.elif) {
                    $elifCondResult = Evaluate-CustomLogic -Tree $elif.condition -Values $Values
                    if ($elifCondResult.idle) {
                        if ($elif.then -ne $null) {
                            return Evaluate-CustomLogic -Tree $elif.then -Values $Values
                        }
                        return @{ idle = $true; action = "none" }
                    }
                }
            }
            # else 分支
            if ($Tree.else -ne $null) {
                return Evaluate-CustomLogic -Tree $Tree.else -Values $Values
            }
            return @{ idle = $false; action = "none" }
        }
        "action" {
            # 根据动作类型返回不同的结果
            switch ($Tree.action) {
                "reset_timer" {
                    # 重置计时器：返回 idle=false，但需要主循环执行重置操作
                    return @{ idle = $false; action = "reset_timer" }
                }
                "continue_timer" {
                    # 继续计时：返回 idle=true，表示条件满足且应该累加
                    return @{ idle = $true; action = "continue_timer" }
                }
                "sleep" {
                    # 立即睡眠：返回 idle=true，但主循环应该特殊处理
                    return @{ idle = $true; action = "sleep" }
                }
                "nothing" {
                    # 什么都不做：不重置计时器，也不累加
                    return @{ idle = $false; action = "nothing" }
                }
                default {
                    # 未知动作，默认当作 continue_timer
                    return @{ idle = $true; action = "continue_timer" }
                }
            }
        }
        "sequence" {
            if (-not $Tree.actions) { return @{ idle = $false; action = "none" } }
            $result = @{ idle = $false; action = "none" }
            foreach ($action in $Tree.actions) {
                $result = Evaluate-CustomLogic -Tree $action -Values $Values
            }
            return $result
        }
        default {
            Write-Host "警告：未知节点类型 '$($Tree.type)'" -ForegroundColor Yellow
            return @{ idle = $false; action = "none" }
        }
    }
}

while ($true) {
    $now = Get-Date
    $deltaSeconds = ($now - $lastCheckTime).TotalSeconds
    $lastCheckTime = $now

    # ---- 唤醒检测（永远优先） ----
    if ($deltaSeconds -gt ($sleepSeconds * 5)) {
        Write-Host "$(Get-Date -Format HH:mm:ss) Wake from sleep, resetting timer."
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

    # ---- 时间窗口 ----
    $inWindow = $true
    if ($enableTimeWindow) {
        $hour = (Get-Date).Hour
        $start = $timeWindowStart
        $end = $timeWindowEnd
        if ($start -lt $end) {
            $inWindow = ($hour -ge $start -and $hour -lt $end)
        } else {
            $inWindow = ($hour -ge $start -or $hour -lt $end)
        }
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
    $runningProc = $null
    if ($enableProcess -and $protectedProcesses.Count -gt 0) {
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
        $customResult = Evaluate-CustomLogic -Tree $config.CustomLogicTree -Values @{
            "CPU"        = $cpuIdle
            "GPU"        = $gpuIdle
            "Disk"       = $diskIdle
            "Network"    = $networkIdle
            "User"       = $userIdle
            "Process"    = $processIdle
            "TimeWindow" = $timeWindowIdle
        }
        $idle = $customResult.idle
        $action = $customResult.action

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

            if (($now - $lastLogTime).TotalSeconds -ge 5) {
                Write-Host "$(Get-Date -Format HH:mm:ss) Idle: $([math]::Round($elapsed, 1)) sec (CPU: $($cpu.ToString('F1'))%, GPU: $($gpu.ToString('F1'))%)"
                if ($enableNetwork) {
                    Write-Host "$(Get-Date -Format HH:mm:ss) Network: $([math]::Round($netKBps, 1)) KB/s"
                }
                if ($enableDisk) {
                    Write-Host "$(Get-Date -Format HH:mm:ss) Disk: $([math]::Round($diskKBps, 1)) KB/s"
                }
                if ($enableProcess -and $protectedProcesses.Count -gt 0) {
                    Write-Host "$(Get-Date -Format HH:mm:ss) Protected: $($runningProc)"
                }
                if ($enableTimeWindow) {
                    Write-Host "$(Get-Date -Format HH:mm:ss) TimeWindow: $inWindow"
                }
                $lastLogTime = $now
            }
        } else {
            if ($action -eq "reset_timer") {
                Write-Host "$(Get-Date -Format HH:mm:ss) Timer reset by custom logic"
                $elapsed = 0
            } elseif ($action -eq "nothing") {
                # 什么都不做，保持 elapsed 不变
            } else {
                if ($elapsed -gt 0) {
                    Write-Host "$(Get-Date -Format HH:mm:ss) Load recovered, timer reset (CPU: $($cpu.ToString('F1'))%, GPU: $($gpu.ToString('F1'))%)"
                }
                $elapsed = 0
            }
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

        Write-Host "Log Rotation checking time： $(((Get-Date) - $lastRotationCheck).TotalHours) hour (Default checking is 1 hour.)"
        Start-Sleep -Seconds $sleepSeconds
        continue
    }

    # ============================================================
    # 分支2：原有硬编码逻辑（自定义未启用时执行）
    # ============================================================

    # ---- 时间窗口 ----
    if ($enableTimeWindow) {
        if (-not $inWindow) {
            if ($elapsed -gt 0) {
                Write-Host "$(Get-Date -Format HH:mm:ss) Outside time window ($start-$end), idle mode."
            }
            $elapsed = 0
            Start-Sleep -Seconds 60
            continue
        }
    }

    # ---- 用户活动 ----
    if ($enableUser) {
        if ($idleMs -lt 3000) {
            if ($elapsed -gt 0) {
                Write-Host "$(Get-Date -Format HH:mm:ss) User activity detected, timer reset."
            }
            $elapsed = 0
            Start-Sleep -Seconds $sleepSeconds
            continue
        }
    }

    # ---- 网络活动 ----
    if ($enableNetwork) {
        if ($netKBps -gt $networkThresholdKBps) {
            if ($elapsed -gt 0) {
                Write-Host "$(Get-Date -Format HH:mm:ss) Network activity detected ($([math]::Round($netKBps, 1)) KB/s), timer reset."
            }
            $elapsed = 0
        }
    }

    # ---- 磁盘活动 ----
    if ($enableDisk) {
        if ($diskKBps -gt $diskThresholdKBps) {
            if ($elapsed -gt 0) {
                Write-Host "$(Get-Date -Format HH:mm:ss) Disk activity detected ($([math]::Round($diskKBps, 1)) KB/s), timer reset."
            }
            $elapsed = 0
        }
    }

    # ---- 进程白名单 ----
    if ($enableProcess -and $protectedProcesses.Count -gt 0) {
        if ($runningProc) {
            if ($elapsed -gt 0) {
                Write-Host "$(Get-Date -Format HH:mm:ss) Protected process pattern '$runningProc' is running, timer reset."
            }
            $elapsed = 0
        }
    }

    # ---- CPU / GPU 空闲判断（硬编码 AND） ----
    $idle = $cpuIdle -and $gpuIdle -and $diskIdle -and $networkIdle -and $userIdle -and $processIdle -and $timeWindowIdle

    if ($idle) {
        $elapsed += $deltaSeconds
        if (($now - $lastLogTime).TotalSeconds -ge 5) {
            Write-Host "$(Get-Date -Format HH:mm:ss) Idle: $([math]::Round($elapsed, 1)) sec (CPU: $($cpu.ToString('F1'))%, GPU: $($gpu.ToString('F1'))%)"
            if ($enableNetwork) {
                Write-Host "$(Get-Date -Format HH:mm:ss) Network: $([math]::Round($netKBps, 1)) KB/s"
            }
            if ($enableDisk) {
                Write-Host "$(Get-Date -Format HH:mm:ss) Disk: $([math]::Round($diskKBps, 1)) KB/s"
            }
            if ($enableProcess -and $protectedProcesses.Count -gt 0) {
                Write-Host "$(Get-Date -Format HH:mm:ss) Protected: $($runningProc)"
            }
            if ($enableTimeWindow) {
                Write-Host "$(Get-Date -Format HH:mm:ss) TimeWindow: $inWindow"
            }
            $lastLogTime = $now
        }
    } else {
        if ($elapsed -gt 0) {
            Write-Host "$(Get-Date -Format HH:mm:ss) Load recovered, timer reset (CPU: $($cpu.ToString('F1'))%, GPU: $($gpu.ToString('F1'))%)"
        }
        $elapsed = 0
        $lastLogTime = $now
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

    Write-Host "Log Rotation checking time： $(((Get-Date) - $lastRotationCheck).TotalHours) hour (Default checking is 1 hour.)"
    Start-Sleep -Seconds $sleepSeconds
}