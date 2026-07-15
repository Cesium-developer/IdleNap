# ============================================================
# Settings.ps1 - AutoSleep 设置程序（管理员权限版）
# ============================================================

# ---- 隐藏控制台窗口 ----
Add-Type -Name Window -Namespace Console -MemberDefinition '
[DllImport("kernel32.dll")]
public static extern IntPtr GetConsoleWindow();

[DllImport("user32.dll")]
public static extern bool ShowWindow(IntPtr hWnd, Int32 nCmdShow);
'
$consoleHandle = [Console.Window]::GetConsoleWindow()
if ($consoleHandle -ne [IntPtr]::Zero) {
    [Console.Window]::ShowWindow($consoleHandle, 0)
}

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $scriptPath = $MyInvocation.MyCommand.Path
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments
    exit
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName Microsoft.VisualBasic

$configPath = "C:\ProgramData\AutoSleep\settings.json"

$defaultConfig = @{
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
    ClearLogOnNextRun    = $false
    EnableDiskCheck      = $true
    DiskThresholdKBps    = 10240
}

if (Test-Path $configPath) {
    $config = Get-Content $configPath -Raw | ConvertFrom-Json
    foreach ($key in $defaultConfig.Keys) {
        if ($null -eq $config.$key) {
            $config | Add-Member -MemberType NoteProperty -Name $key -Value $defaultConfig[$key] -Force
        }
    }
} else {
    $config = $defaultConfig
}

$config.DurationMin          = [int]$config.DurationMin
$config.CpuThreshold         = [int]$config.CpuThreshold
$config.GpuThreshold         = [int]$config.GpuThreshold
$config.Interval             = [int]$config.Interval
$config.NetworkThresholdKBps = [int]$config.NetworkThresholdKBps
$config.TimeWindowStart      = [int]$config.TimeWindowStart
$config.TimeWindowEnd        = [int]$config.TimeWindowEnd
$config.DiskThresholdKBps    = [int]$config.DiskThresholdKBps

$form = New-Object System.Windows.Forms.Form
$form.Text = "AutoSleep 设置"
$form.Size = New-Object System.Drawing.Size(480, 920)
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = "FixedDialog"
$form.MaximizeBox = $false
$form.MinimizeBox = $false

[int]$top = 20
[int]$labelWidth = 160
[int]$controlWidth = 200
[int]$leftLabel = 20
[int]$leftControl = 190
[int]$rowHeight = 40

function New-Point([int]$x, [int]$y) {
    return New-Object System.Drawing.Point($x, $y)
}

# ---- 辅助函数：检测休眠状态 ----
function Get-HibernateStatus {
    try {
        $regValue = (Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Power" -Name "HibernateEnabled" -ErrorAction Stop).HibernateEnabled
        return ($regValue -eq 1)
    } catch {
        return $false
    }
}

function Get-MemorySize {
    try {
        $totalBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
        return [math]::Round($totalBytes / 1GB, 1)
    } catch {
        return $null
    }
}

# ---- 1. 模式 ----
$lblMode = New-Object System.Windows.Forms.Label
$lblMode.Text = "休眠/睡眠模式："
$lblMode.Location = New-Point $leftLabel $top
$lblMode.Size = New-Object System.Drawing.Size($labelWidth, 25)
$form.Controls.Add($lblMode)

$comboMode = New-Object System.Windows.Forms.ComboBox
$comboMode.Location = New-Point $leftControl $top
$comboMode.Size = New-Object System.Drawing.Size($controlWidth, 25)
$comboMode.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$comboMode.Items.AddRange(@("Hibernate", "Sleep"))

# 根据休眠状态决定默认选中项并调整下拉选项
$hibernateOn = Get-HibernateStatus
if (-not $hibernateOn) {
    # 休眠关闭时，从下拉框中移除 Hibernate 选项
    $comboMode.Items.Remove("Hibernate")
    if ($comboMode.Items.Count -eq 1 -and $comboMode.Items[0] -eq "Sleep") {
        $comboMode.SelectedIndex = 0
    }
    # 如果配置中是 Hibernate 但系统不支持，自动切换到 Sleep
    if ($config.PowerAction -eq "Hibernate") {
        $config.PowerAction = "Sleep"
        $config | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
    }
    $comboMode.SelectedItem = "Sleep"
} else {
    # 休眠开启时，正常显示
    $comboMode.SelectedItem = $config.PowerAction
}
$form.Controls.Add($comboMode)
$top += $rowHeight

# 休眠状态提示（在模式选择下方显示）
$lblHibernateStatus = New-Object System.Windows.Forms.Label
$lblHibernateStatus.Text = if ($hibernateOn) { "✅ 休眠功能已开启" } else { "⚠️ 休眠功能未开启，仅可使用睡眠模式" }
$lblHibernateStatus.Location = New-Point $leftControl $top
$lblHibernateStatus.Size = New-Object System.Drawing.Size($controlWidth, 25)
$lblHibernateStatus.ForeColor = if ($hibernateOn) { [System.Drawing.Color]::LightGreen } else { [System.Drawing.Color]::Yellow }
$form.Controls.Add($lblHibernateStatus)
$top += $rowHeight

# ---- 2. 空闲时间 ----
$lblDuration = New-Object System.Windows.Forms.Label
$lblDuration.Text = "空闲等待时间（分钟）："
$lblDuration.Location = New-Point $leftLabel $top
$lblDuration.Size = New-Object System.Drawing.Size($labelWidth, 25)
$form.Controls.Add($lblDuration)

$txtDuration = New-Object System.Windows.Forms.TextBox
$txtDuration.Location = New-Point $leftControl $top
$txtDuration.Size = New-Object System.Drawing.Size($controlWidth, 25)
$txtDuration.Text = $config.DurationMin.ToString()
$form.Controls.Add($txtDuration)
$top += $rowHeight

# ---- 3. CPU ----
$lblCpu = New-Object System.Windows.Forms.Label
$lblCpu.Text = "CPU 阈值（%）："
$lblCpu.Location = New-Point $leftLabel $top
$lblCpu.Size = New-Object System.Drawing.Size($labelWidth, 25)
$form.Controls.Add($lblCpu)

$txtCpu = New-Object System.Windows.Forms.TextBox
$txtCpu.Location = New-Point $leftControl $top
$txtCpu.Size = New-Object System.Drawing.Size($controlWidth, 25)
$txtCpu.Text = $config.CpuThreshold.ToString()
$form.Controls.Add($txtCpu)
$top += $rowHeight

# ---- 4. GPU ----
$lblGpu = New-Object System.Windows.Forms.Label
$lblGpu.Text = "GPU 阈值（%）："
$lblGpu.Location = New-Point $leftLabel $top
$lblGpu.Size = New-Object System.Drawing.Size($labelWidth, 25)
$form.Controls.Add($lblGpu)

$txtGpu = New-Object System.Windows.Forms.TextBox
$txtGpu.Location = New-Point $leftControl $top
$txtGpu.Size = New-Object System.Drawing.Size($controlWidth, 25)
$txtGpu.Text = $config.GpuThreshold.ToString()
$form.Controls.Add($txtGpu)
$top += $rowHeight

# ---- 5. 检测间隔 ----
$lblInterval = New-Object System.Windows.Forms.Label
$lblInterval.Text = "检测间隔（秒，最小3）："
$lblInterval.Location = New-Point $leftLabel $top
$lblInterval.Size = New-Object System.Drawing.Size($labelWidth, 25)
$form.Controls.Add($lblInterval)

$txtInterval = New-Object System.Windows.Forms.TextBox
$txtInterval.Location = New-Point $leftControl $top
$txtInterval.Size = New-Object System.Drawing.Size($controlWidth, 25)
$txtInterval.Text = $config.Interval.ToString()
$form.Controls.Add($txtInterval)
$top += $rowHeight

# ---- 6. GPU检测 ----
$chkGpu = New-Object System.Windows.Forms.CheckBox
$chkGpu.Text = "启用 GPU 检测"
$chkGpu.Location = New-Point $leftControl $top
$chkGpu.Size = New-Object System.Drawing.Size($controlWidth, 25)
$chkGpu.Checked = [bool]$config.EnableGpuCheck
$form.Controls.Add($chkGpu)
$top += $rowHeight

# ---- 7. 用户活动 ----
$chkUser = New-Object System.Windows.Forms.CheckBox
$chkUser.Text = "启用用户活动检测"
$chkUser.Location = New-Point $leftControl $top
$chkUser.Size = New-Object System.Drawing.Size($controlWidth, 25)
$chkUser.Checked = [bool]$config.EnableUserActivity
$form.Controls.Add($chkUser)
$top += $rowHeight

# ---- 8. 网络检测 ----
$chkNetwork = New-Object System.Windows.Forms.CheckBox
$chkNetwork.Text = "启用网络活动检测"
$chkNetwork.Location = New-Point $leftLabel $top
$chkNetwork.Size = New-Object System.Drawing.Size(160, 25)
$chkNetwork.Checked = [bool]$config.EnableNetworkCheck
$form.Controls.Add($chkNetwork)

$txtNetworkThreshold = New-Object System.Windows.Forms.TextBox
$txtNetworkThreshold.Location = New-Point $leftControl $top
$txtNetworkThreshold.Size = New-Object System.Drawing.Size(80, 25)
$txtNetworkThreshold.Text = $config.NetworkThresholdKBps.ToString()

$lblNetworkUnit = New-Object System.Windows.Forms.Label
$lblNetworkUnit.Text = "KB/s"
$lblNetworkUnit.Location = New-Point ($leftControl + 90) ($top + 3)
$lblNetworkUnit.Size = New-Object System.Drawing.Size(40, 20)
$form.Controls.Add($txtNetworkThreshold)
$form.Controls.Add($lblNetworkUnit)
$top += $rowHeight

# ---- 9. 磁盘检测 ----
$chkDisk = New-Object System.Windows.Forms.CheckBox
$chkDisk.Text = "启用磁盘活动检测"
$chkDisk.Location = New-Point $leftLabel $top
$chkDisk.Size = New-Object System.Drawing.Size(160, 25)
$chkDisk.Checked = [bool]$config.EnableDiskCheck
$form.Controls.Add($chkDisk)

$txtDiskThreshold = New-Object System.Windows.Forms.TextBox
$txtDiskThreshold.Location = New-Point $leftControl $top
$txtDiskThreshold.Size = New-Object System.Drawing.Size(80, 25)
$txtDiskThreshold.Text = $config.DiskThresholdKBps.ToString()

$lblDiskUnit = New-Object System.Windows.Forms.Label
$lblDiskUnit.Text = "KB/s"
$lblDiskUnit.Location = New-Point ($leftControl + 90) ($top + 3)
$lblDiskUnit.Size = New-Object System.Drawing.Size(40, 20)
$form.Controls.Add($txtDiskThreshold)
$form.Controls.Add($lblDiskUnit)
$top += $rowHeight

# ---- 10. 进程白名单 ----
$chkProcess = New-Object System.Windows.Forms.CheckBox
$chkProcess.Text = "启用进程白名单（包含匹配）"
$chkProcess.Location = New-Point ($leftLabel - 10) $top
$chkProcess.Size = New-Object System.Drawing.Size(220, 25)
$chkProcess.Checked = [bool]$config.EnableProcessCheck
$form.Controls.Add($chkProcess)

$lstProcessList = New-Object System.Windows.Forms.ListBox
$lstProcessList.Location = New-Point $leftControl ($top + 25)
$lstProcessList.Size = New-Object System.Drawing.Size(140, 80)
$lstProcessList.SelectionMode = [System.Windows.Forms.SelectionMode]::One
if ($config.ProtectedProcesses -and $config.ProtectedProcesses.Count -gt 0) {
    foreach ($item in $config.ProtectedProcesses) {
        $lstProcessList.Items.Add($item)
    }
}
$form.Controls.Add($lstProcessList)

$btnAdd = New-Object System.Windows.Forms.Button
$btnAdd.Text = "添加"
$btnAdd.Location = New-Point ($leftControl + 145) ($top + 25)
$btnAdd.Size = New-Object System.Drawing.Size(50, 25)
$btnAdd.Add_Click({
    $newProc = [Microsoft.VisualBasic.Interaction]::InputBox("输入进程名（不包含 .exe）：", "添加进程", "")
    if ($newProc -and $newProc.Trim() -ne '') {
        $lstProcessList.Items.Add($newProc.Trim())
    }
})
$form.Controls.Add($btnAdd)

$btnRemove = New-Object System.Windows.Forms.Button
$btnRemove.Text = "删除"
$btnRemove.Location = New-Point ($leftControl + 145) ($top + 25 + 30)
$btnRemove.Size = New-Object System.Drawing.Size(50, 25)
$btnRemove.Add_Click({
    if ($lstProcessList.SelectedItem -ne $null) {
        $lstProcessList.Items.Remove($lstProcessList.SelectedItem)
    }
})
$form.Controls.Add($btnRemove)

$lblProcessHint = New-Object System.Windows.Forms.Label
$lblProcessHint.Text = "（点击添加/删除管理进程，匹配不区分大小写）"
$lblProcessHint.Location = New-Point $leftControl ($top + 25 + 85)
$lblProcessHint.AutoSize = $true
$lblProcessHint.ForeColor = [System.Drawing.Color]::Gray
$form.Controls.Add($lblProcessHint)

$top += $rowHeight + 100 + 25

# ---- 11. 时间窗口 ----
$chkTimeWindow = New-Object System.Windows.Forms.CheckBox
$chkTimeWindow.Text = "启用时间窗口"
$chkTimeWindow.Location = New-Point $leftLabel $top
$chkTimeWindow.Size = New-Object System.Drawing.Size(160, 25)
$chkTimeWindow.Checked = [bool]$config.EnableTimeWindow
$form.Controls.Add($chkTimeWindow)

$lblTimeStart = New-Object System.Windows.Forms.Label
$lblTimeStart.Text = "开始小时（0-23）："
$lblTimeStart.Location = New-Point $leftControl $top
$lblTimeStart.Size = New-Object System.Drawing.Size(120, 25)
$form.Controls.Add($lblTimeStart)

$txtTimeStart = New-Object System.Windows.Forms.TextBox
$txtTimeStart.Location = New-Point ($leftControl + 120) $top
$txtTimeStart.Size = New-Object System.Drawing.Size(40, 25)
$txtTimeStart.Text = $config.TimeWindowStart.ToString()
$form.Controls.Add($txtTimeStart)

$lblTimeEnd = New-Object System.Windows.Forms.Label
$lblTimeEnd.Text = "结束小时："
$lblTimeEnd.Location = New-Point ($leftControl + 170) $top
$lblTimeEnd.Size = New-Object System.Drawing.Size(60, 25)
$form.Controls.Add($lblTimeEnd)

$txtTimeEnd = New-Object System.Windows.Forms.TextBox
$txtTimeEnd.Location = New-Point ($leftControl + 230) $top
$txtTimeEnd.Size = New-Object System.Drawing.Size(40, 25)
$txtTimeEnd.Text = $config.TimeWindowEnd.ToString()
$form.Controls.Add($txtTimeEnd)
$top += $rowHeight + 10

# ---- 按钮行1 ----
$btnSave = New-Object System.Windows.Forms.Button
$btnSave.Text = "保存"
$btnSave.Location = New-Point 20 $top
$btnSave.Size = New-Object System.Drawing.Size(80, 30)
$btnSave.Add_Click({
    function Get-Int($text) {
        $trimmed = $text.Trim()
        if ($trimmed -eq '') { return $null }
        try { return [int]$trimmed } catch { return $null }
    }

    $duration = Get-Int $txtDuration.Text
    $cpu = Get-Int $txtCpu.Text
    $gpu = Get-Int $txtGpu.Text
    $interval = Get-Int $txtInterval.Text
    $netThreshold = Get-Int $txtNetworkThreshold.Text
    $diskThreshold = Get-Int $txtDiskThreshold.Text
    $timeStart = Get-Int $txtTimeStart.Text
    $timeEnd = Get-Int $txtTimeEnd.Text

    if ($null -eq $duration -or $null -eq $cpu -or $null -eq $gpu -or $null -eq $interval -or $null -eq $netThreshold -or $null -eq $diskThreshold -or $null -eq $timeStart -or $null -eq $timeEnd) {
        [System.Windows.Forms.MessageBox]::Show("请确保所有数字字段已正确填写。", "输入错误", "OK", "Error")
        return
    }
    if ($duration -le 0 -or $cpu -lt 0 -or $gpu -lt 0 -or $interval -lt 3 -or $netThreshold -lt 1 -or $diskThreshold -lt 1 -or $timeStart -lt 0 -or $timeStart -gt 23 -or $timeEnd -lt 0 -or $timeEnd -gt 23) {
        [System.Windows.Forms.MessageBox]::Show("数值超出合理范围，请检查。", "输入错误", "OK", "Error")
        return
    }

    # 检查休眠状态与模式选择的匹配
    $hibernateOn = Get-HibernateStatus
    if (-not $hibernateOn -and $comboMode.SelectedItem -eq "Hibernate") {
        [System.Windows.Forms.MessageBox]::Show("休眠功能未开启，无法选择休眠模式。`n请先开启休眠功能，或选择睡眠模式。", "模式不可用", "OK", "Error")
        return
    }

    $processList = @()
    foreach ($item in $lstProcessList.Items) {
        $processList += $item.ToString()
    }

    try {
        $newConfig = @{
            PowerAction          = $comboMode.SelectedItem
            DurationMin          = $duration
            CpuThreshold         = $cpu
            GpuThreshold         = $gpu
            Interval             = $interval
            EnableGpuCheck       = [bool]$chkGpu.Checked
            EnableUserActivity   = [bool]$chkUser.Checked
            EnableNetworkCheck   = [bool]$chkNetwork.Checked
            NetworkThresholdKBps = $netThreshold
            EnableDiskCheck      = [bool]$chkDisk.Checked
            DiskThresholdKBps    = $diskThreshold
            EnableProcessCheck   = [bool]$chkProcess.Checked
            ProtectedProcesses   = $processList
            EnableTimeWindow     = [bool]$chkTimeWindow.Checked
            TimeWindowStart      = $timeStart
            TimeWindowEnd        = $timeEnd
        }
        $newConfig | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
        [System.Windows.Forms.MessageBox]::Show("设置已保存！", "成功", "OK", "Information")

        Get-CimInstance -ClassName Win32_Process | Where-Object { $_.CommandLine -like "*AutoSleep.ps1*" -and $_.ProcessId -ne $PID } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        schtasks /run /tn "AutoSleep"

        $form.Close()
    } catch {
        [System.Windows.Forms.MessageBox]::Show("保存失败：`n`n$_", "错误", "OK", "Error")
    }
})
$form.Controls.Add($btnSave)

$btnHelp = New-Object System.Windows.Forms.Button
$btnHelp.Text = "帮助"
$btnHelp.Location = New-Point 110 $top
$btnHelp.Size = New-Object System.Drawing.Size(80, 30)
$btnHelp.Add_Click({
    $readmePath = "C:\ProgramData\AutoSleep\README.txt"
    if (Test-Path $readmePath) {
        Start-Process "notepad.exe" $readmePath
    } else {
        [System.Windows.Forms.MessageBox]::Show(
            "帮助文件未找到，请确认安装完整。`n路径：$readmePath",
            "错误",
            "OK",
            "Error"
        )
    }
})
$form.Controls.Add($btnHelp)

$btnCancel = New-Object System.Windows.Forms.Button
$btnCancel.Text = "取消"
$btnCancel.Location = New-Point 200 $top
$btnCancel.Size = New-Object System.Drawing.Size(80, 30)
$btnCancel.Add_Click({ $form.Close() })
$form.Controls.Add($btnCancel)

$btnClearLog = New-Object System.Windows.Forms.Button
$btnClearLog.Text = "清空日志"
$btnClearLog.Location = New-Point 290 $top
$btnClearLog.Size = New-Object System.Drawing.Size(80, 30)
$btnClearLog.Add_Click({
    $logPath = "C:\ProgramData\AutoSleep\AutoSleep.log"
    try {
        Get-CimInstance -ClassName Win32_Process | Where-Object {
            $_.CommandLine -like "*AutoSleep.ps1*" -and $_.ProcessId -ne $PID
        } | ForEach-Object {
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 200

        if (Test-Path $logPath) {
            Remove-Item -Path $logPath -Force
            [System.Windows.Forms.MessageBox]::Show("日志已清空。", "成功", "OK", "Information")
        } else {
            [System.Windows.Forms.MessageBox]::Show("日志文件不存在，无需清空。", "提示", "OK", "Information")
        }

        schtasks /run /tn "AutoSleep" 2>&1 | Out-Null
    } catch {
        [System.Windows.Forms.MessageBox]::Show(
            "清空失败：`n$($_.Exception.Message)",
            "错误",
            "OK",
            "Error"
        )
    }
})
$form.Controls.Add($btnClearLog)

$btnShowLog = New-Object System.Windows.Forms.Button
$btnShowLog.Text = "显示日志"
$btnShowLog.Location = New-Point 380 $top
$btnShowLog.Size = New-Object System.Drawing.Size(80, 30)
$btnShowLog.Add_Click({
    $logFile = "C:\ProgramData\AutoSleep\AutoSleep.log"
    if (Test-Path $logFile) {
        $cmd = @"
`$Host.UI.RawUI.BackgroundColor = 'Black'
`$Host.UI.RawUI.ForegroundColor = 'White'
Clear-Host
`$Host.UI.RawUI.WindowTitle = 'AutoSleep 日志监控'
Get-Content 'C:\ProgramData\AutoSleep\AutoSleep.log' -Wait
"@
        Start-Process powershell -ArgumentList @(
            '-NoProfile',
            '-NoExit',
            '-Command',
            $cmd
        )
    } else {
        [System.Windows.Forms.MessageBox]::Show(
            "日志文件尚未生成，请先运行 AutoSleep 主程序。`n路径：$logFile",
            "提示",
            "OK",
            "Information"
        )
    }
})
$form.Controls.Add($btnShowLog)

# ---- 按钮行2：休眠开关 ----
$top += $rowHeight + 10

$btnToggleHibernate = New-Object System.Windows.Forms.Button
# 动态文本在刷新函数中设置
$btnToggleHibernate.Location = New-Point 150 $top
$btnToggleHibernate.Size = New-Object System.Drawing.Size(160, 30)

# 刷新休眠开关按钮状态和界面
function Refresh-HibernateUI {
    $hibernateOn = Get-HibernateStatus
    if ($hibernateOn) {
        $btnToggleHibernate.Text = "关闭休眠"
    } else {
        $btnToggleHibernate.Text = "开启休眠"
    }
    # 更新状态标签
    $lblHibernateStatus.Text = if ($hibernateOn) { "✅ 休眠功能已开启" } else { "⚠️ 休眠功能未开启，仅可使用睡眠模式" }
    $lblHibernateStatus.ForeColor = if ($hibernateOn) { [System.Drawing.Color]::LightGreen } else { [System.Drawing.Color]::Goldenrod }

    # 更新下拉框：休眠关闭时移除 Hibernate 选项
    $currentSelected = $comboMode.SelectedItem
    $comboMode.Items.Clear()
    if ($hibernateOn) {
        $comboMode.Items.AddRange(@("Hibernate", "Sleep"))
        if ($currentSelected -in @("Hibernate", "Sleep")) {
            $comboMode.SelectedItem = $currentSelected
        } else {
            $comboMode.SelectedItem = "Sleep"
        }
    } else {
        $comboMode.Items.Add("Sleep")
        $comboMode.SelectedItem = "Sleep"
        # 如果配置中是 Hibernate，自动切换为 Sleep 并保存
        $config = Get-Content $configPath -Raw | ConvertFrom-Json
        if ($config.PowerAction -eq "Hibernate") {
            $config.PowerAction = "Sleep"
            $config | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
        }
    }
}

$btnToggleHibernate.Add_Click({
    $hibernateOn = Get-HibernateStatus
    if ($hibernateOn) {
        $hiberFile = Get-ChildItem -Path "C:\" -Filter "hiberfil.sys" -Force -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hiberFile) {
            $hibernateSizeGB = [math]::Round($hiberFile.Length / 1GB, 1)
            $sizeHint = "约 $hibernateSizeGB GB"
        } else {
            $totalMemoryGB = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
            $hibernateSizeGB = [math]::Round($totalMemoryGB * 0.5, 1)
            $sizeHint = "约 $hibernateSizeGB GB（通常为物理内存的 40%~60%）"
        }
        $confirmMsg = "关闭休眠功能将释放 C 盘约 $sizeHint GB 空间，但会失去休眠模式。`n`n确定要关闭休眠功能吗？"
        $title = "确认关闭休眠"
    } else {
        $hiberFile = Get-ChildItem -Path "C:\" -Filter "hiberfil.sys" -Force -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hiberFile) {
            $hibernateSizeGB = [math]::Round($hiberFile.Length / 1GB, 1)
            $sizeHint = "约 $hibernateSizeGB GB"
        } else {
            $totalMemoryGB = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
            $hibernateSizeGB = [math]::Round($totalMemoryGB * 0.5, 1)
            $sizeHint = "约 $hibernateSizeGB GB（通常为物理内存的 40%~60%）"
        }
        $confirmMsg = "开启休眠功能将在 C 盘占用约 $sizeHint GB 空间，但可提供完整的休眠模式。`n`n确定要开启休眠功能吗？"
        $title = "确认开启休眠"
    }

    $result = [System.Windows.Forms.MessageBox]::Show($confirmMsg, $title, "YesNo", "Question")
    if ($result -eq "Yes") {
        if ($hibernateOn) {
            powercfg -h off
        } else {
            powercfg -h on
        }
        # 刷新界面
        Refresh-HibernateUI
        [System.Windows.Forms.MessageBox]::Show(
            "休眠功能已" + $(if ($hibernateOn) { "关闭" } else { "开启" }) + "。",
            "操作完成",
            "OK",
            "Information"
        )
    }
})
$form.Controls.Add($btnToggleHibernate)

# ---- 按钮行2：标签说明 ----
$lblHibernateHint = New-Object System.Windows.Forms.Label
$lblHibernateHint.Text = "点击按钮可在系统级开关休眠功能"
$lblHibernateHint.Location = New-Point 20 ($top + 5)
$lblHibernateHint.Size = New-Object System.Drawing.Size(130, 30)
$lblHibernateHint.ForeColor = [System.Drawing.Color]::Gray
$lblHibernateHint.Font = New-Object System.Drawing.Font("微软雅黑", 8)
$form.Controls.Add($lblHibernateHint)

# 初始化界面
Refresh-HibernateUI

$form.ShowDialog()