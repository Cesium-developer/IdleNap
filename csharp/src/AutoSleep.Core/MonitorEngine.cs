using System;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;

namespace AutoSleep.Core
{
    public class MonitorEngine
    {
        private readonly ConfigManager _config;
        private readonly HardwareMonitor _hardware;
        private readonly RuleEngine _rules;
        private readonly PowerManager _power;
        private readonly LogManager _log;
        private readonly ProcessMonitor _processMonitor;

        private double _elapsed;
        private DateTime _lastLogTime;
        private DateTime _lastRotationCheck;
        private DateTime _cooldownUntil;

        // 单调时钟（毫秒）：真实睡眠(S3/S4)时该计数器冻结、墙钟继续前进，用于区分"真唤醒"与"有意睡眠/慢迭代"
        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        public MonitorEngine(ConfigManager config)
        {
            _config = config;
            _hardware = new HardwareMonitor();
            _rules = new RuleEngine();
            _power = new PowerManager();
            _log = new LogManager();
            _processMonitor = new ProcessMonitor();
            _cooldownUntil = DateTime.Now.AddDays(-1);
            _lastLogTime = DateTime.Now;
            _lastRotationCheck = DateTime.Now;
        }

        public void Start()
        {
            // 对应原版 Write-Host "Monitoring started. Idle for X minute(s) will trigger Y."
            _log.Write(string.Format("Monitoring started. Idle for {0} minute(s) will trigger {1}.", _config.DurationMin, _config.PowerAction));

            DateTime lastCheckTime = DateTime.Now;
            // 原版减2是补偿 PowerShell 启动延迟，C# 不需要
            int sleepSeconds = _config.Interval;
            ulong lastTick = GetTickCount64();

            while (true)
            {
                DateTime now = DateTime.Now;
                double deltaSeconds = (now - lastCheckTime).TotalSeconds;
                lastCheckTime = now;

                ulong tickNow = GetTickCount64();
                double tickDeltaSeconds = (tickNow - lastTick) / 1000.0;
                lastTick = tickNow;

                // ---- 唤醒检测（永远优先） ----
                // 仅当墙钟前进量远大于单调时钟前进量时才判定为真实睡眠/挂起：
                // 真实睡眠(S3/S4)时 GetTickCount64 冻结而墙钟继续走（差≈睡眠时长）；
                // 时间窗口分支的 60s 有意睡眠两者同步增长（差≈0），不会误判。
                double suspendSeconds = deltaSeconds - tickDeltaSeconds;
                if (suspendSeconds > 30)
                {
                    _log.Write(string.Format("Wake from sleep, resetting timer. (wall={0:F0}s, tick={1:F0}s)", deltaSeconds, tickDeltaSeconds));
                    _elapsed = 0;
                    lastCheckTime = now;
                    Thread.Sleep(sleepSeconds * 1000);
                    continue;
                }

                // ---- 日志清空请求 ----
                if (_config.ClearLogOnNextRun)
                {
                    _log.Write("Clear log requested, resetting transcript...");
                    _log.Clear();
                    _config.ClearLogOnNextRun = false;
                    _config.LastRotationTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    _config.Save();
                    _log.Write("Log cleared and transcript restarted.");
                }

                // ---- 日志轮转（每1小时检查一次） ----
                if (_config.EnableLogRotation && (DateTime.Now - _lastRotationCheck).TotalHours >= 1)
                {
                    bool rotated = _log.RotateIfNeeded(_config.LogRetentionDays, _config.LastRotationTime);
                    if (rotated)
                    {
                        _config.LastRotationTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        _config.Save();
                    }
                    _lastRotationCheck = DateTime.Now;
                }

                // ============================================================
                // 采集所有原始数据（两条线路共用）
                // ============================================================

                HardwareData data = _hardware.Sample();

                string runningProc = null;
                if (_config.EnableProcessCheck && _config.ProtectedProcesses != null && _config.ProtectedProcesses.Count > 0)
                {
                    runningProc = _processMonitor.FindRunning(_config.ProtectedProcesses);
                }

                // ---- 时间窗口 ----
                // 始终按配置 Start/End 计算实际时段：EnableTimeWindow 总开关只控制硬编码分支（分支2），
                // 不影响自定义逻辑里 TimeWindow 节点对"当前是否在窗口内"的判定
                bool inWindow;
                int hourNow = now.Hour;
                int winStart = _config.TimeWindowStart;
                int winEnd = _config.TimeWindowEnd;
                if (winStart < winEnd)
                    inWindow = (hourNow >= winStart && hourNow < winEnd);
                else
                    inWindow = (hourNow >= winStart || hourNow < winEnd);

                // ---- 条件变量化 ----
                bool cpuIdle = data.CpuPercent < _config.CpuThreshold;
                bool gpuIdle = !_config.EnableGpuCheck || data.GpuPercent < _config.GpuThreshold;
                bool diskIdle = !_config.EnableDiskCheck || data.DiskKBps < _config.DiskThresholdKBps;
                bool networkIdle = !_config.EnableNetworkCheck || data.NetworkKBps < _config.NetworkThresholdKBps;
                bool userIdle = !_config.EnableUserActivity || data.IdleSeconds >= 3;
                bool processIdle = !_config.EnableProcessCheck || runningProc == null;
                bool timeWindowIdle = !_config.EnableTimeWindow || inWindow;

                // ---- 统一日志输出在决策之后（见下方"统一日志输出"段），旧状态块已并入 WriteStatusBlock ----


                // ============================================================
                // 决策（判定/计时/重置逻辑与源码一致；只删除各处独立日志行，改由统一输出层输出）
                // ============================================================
                bool decisionIdle;
                var failedReasons = new List<string>();

                // ---- 分支1：自定义逻辑（如果启用） ----
                if (_config.CustomLogicEnabled && _config.CustomLogicTree != null)
                {
                    var customValues = new Dictionary<string, bool>();
                    customValues["CPU"] = cpuIdle;
                    customValues["GPU"] = gpuIdle;
                    customValues["Disk"] = diskIdle;
                    customValues["Network"] = networkIdle;
                    customValues["User"] = userIdle;
                    customValues["Process"] = processIdle;
                    customValues["TimeWindow"] = inWindow;

                    // 原始指标（供自定义逻辑节点级阈值使用；无节点数值时引擎回退上面的布尔）
                    var customMetrics = new Dictionary<string, double>();
                    customMetrics["CPU"] = data.CpuPercent;
                    customMetrics["GPU"] = data.GpuPercent;
                    customMetrics["Disk"] = data.DiskKBps;
                    customMetrics["Network"] = data.NetworkKBps;
                    customMetrics["User"] = data.IdleSeconds;

                    RuleResult customResult = _rules.Evaluate(_config.CustomLogicTree, customValues, customMetrics);
                    decisionIdle = customResult.Idle;
                    string action = customResult.Action;
                    if (customResult.FailedConditions != null)
                        failedReasons.AddRange(customResult.FailedConditions);

                    if (decisionIdle)
                    {
                        if (action == "sleep")
                        {
                            _log.Write("Custom logic: immediate sleep triggered");
                            _elapsed = _config.DurationMin * 60;
                        }
                        else
                        {
                            _elapsed += deltaSeconds;
                        }
                    }
                    else
                    {
                        if (action == "reset_timer")
                            _elapsed = 0;
                        else if (action == "nothing")
                        {
                            // 什么都不做，保持 elapsed 不变
                        }
                        else
                        {
                            _elapsed = 0;
                        }
                    }
                }
                // ---- 分支2：原有硬编码逻辑（自定义未启用时执行） ----
                else
                {
                    // ---- 时间窗口 ----
                    if (_config.EnableTimeWindow && !inWindow)
                    {
                        AddFailed(failedReasons, "TimeWindow");
                        _elapsed = 0;
                        WriteStatusBlock(failedReasons, data, runningProc, inWindow);
                        Thread.Sleep(sleepSeconds * 1000);
                        continue;
                    }

                    // ---- 用户活动 ----
                    if (_config.EnableUserActivity && data.IdleSeconds < 3)
                    {
                        AddFailed(failedReasons, "User");
                        _elapsed = 0;
                        WriteStatusBlock(failedReasons, data, runningProc, inWindow);
                        Thread.Sleep(sleepSeconds * 1000);
                        continue;
                    }

                    // ---- 网络活动 ----
                    if (_config.EnableNetworkCheck && data.NetworkKBps > _config.NetworkThresholdKBps)
                    {
                        AddFailed(failedReasons, "Network");
                        _elapsed = 0;
                    }

                    // ---- 磁盘活动 ----
                    if (_config.EnableDiskCheck && data.DiskKBps > _config.DiskThresholdKBps)
                    {
                        AddFailed(failedReasons, "Disk");
                        _elapsed = 0;
                    }

                    // ---- 进程白名单 ----
                    if (_config.EnableProcessCheck && _config.ProtectedProcesses != null && _config.ProtectedProcesses.Count > 0)
                    {
                        if (runningProc != null)
                        {
                            AddFailed(failedReasons, "Process");
                            _elapsed = 0;
                        }
                    }

                    // ---- CPU / GPU 空闲判断（硬编码 AND）----
                    decisionIdle = cpuIdle && gpuIdle && diskIdle && networkIdle && userIdle && processIdle && timeWindowIdle;

                    if (decisionIdle)
                    {
                        _elapsed += deltaSeconds;
                    }
                    else
                    {
                        // CPU/GPU 无独立违反分支，在此补充失败原因（其余已在上面收集）
                        if (!cpuIdle) AddFailed(failedReasons, "CPU");
                        if (!gpuIdle) AddFailed(failedReasons, "GPU");
                        _elapsed = 0;
                    }
                }

                // ============================================================
                // 统一日志输出（节流 = 配置 Interval；两套格式：计时 / 非计时，字段集合一致）
                // ============================================================
                if (decisionIdle)
                    WriteIdleBlock(data, runningProc, inWindow);
                else
                    WriteStatusBlock(failedReasons, data, runningProc, inWindow);

                // ---- 触发 ----
                if (_elapsed >= (_config.DurationMin * 60))
                {
                    if (DateTime.Now < _cooldownUntil)
                    {
                        _log.Write(string.Format("In cooldown period (until {0:yyyy-MM-dd HH:mm:ss}), skipping sleep.", _cooldownUntil));
                        _elapsed = 0;
                        Thread.Sleep(sleepSeconds * 1000);
                        continue;
                    }

                    _log.Write("Condition met, showing countdown...");
                    bool canceled = ShowCountdownWindow(10, _config.PowerAction);
                    if (canceled)
                    {
                        _log.Write("User canceled sleep.");
                        _cooldownUntil = DateTime.Now.AddMinutes(10);
                        _log.Write(string.Format("Cooldown set until {0:yyyy-MM-dd HH:mm:ss}", _cooldownUntil));
                        _elapsed = 0;
                        Thread.Sleep(sleepSeconds * 1000);
                        continue;
                    }

                    _log.Write(string.Format("Executing {0} in 5 seconds...", _config.PowerAction));
                    Thread.Sleep(5000);

                    _power.Execute(_config.PowerAction);

                    _log.Write(string.Format("Resuming monitoring after {0}...", _config.PowerAction));
                    _elapsed = 0;
                    _lastLogTime = DateTime.Now;
                    Thread.Sleep(5000);
                }

                // 原版最后一行：Write-Host "Log Rotation checking time： X hour (Default checking is 1 hour.)"
                // 精度与日志其它数值行统一（F2，小时单位）
                _log.Write(string.Format("Log Rotation checking time: {0:F2} hour (Default checking is 1 hour.)",
                    (DateTime.Now - _lastRotationCheck).TotalHours));

                Thread.Sleep(sleepSeconds * 1000);
            }
        }

        // ---- 统一日志输出层（节流 = 配置 Interval；仅此两处 + 事件行写日志） ----
        private static void AddFailed(List<string> list, string cond)
        {
            if (!list.Contains(cond))
                list.Add(cond);
        }

        // 非计时周期：Status 首行 + 决策者原因行（映射回源码原有行族）+ 全指标详情行
        private void WriteStatusBlock(List<string> reasons, HardwareData data, string runningProc, bool inWindow)
        {
            if ((DateTime.Now - _lastLogTime).TotalSeconds < _config.Interval)
                return;

            _log.Write(string.Format("Status: Not idle (CPU: {0:F1}%, GPU: {1:F1}%)", data.CpuPercent, data.GpuPercent));

            bool cpuGpuReported = false;
            bool hasReason = false;
            foreach (var cond in reasons)
            {
                hasReason = true;
                switch (cond)
                {
                    case "User":
                        _log.Write("User activity detected, timer reset.");
                        break;
                    case "Network":
                        _log.Write(string.Format("Network activity detected ({0:F1} KB/s), timer reset.", data.NetworkKBps));
                        break;
                    case "Disk":
                        _log.Write(string.Format("Disk activity detected ({0:F1} KB/s), timer reset.", data.DiskKBps));
                        break;
                    case "Process":
                        if (runningProc != null)
                            _log.Write(string.Format("Protected process pattern '{0}' is running, timer reset.", runningProc));
                        break;
                    case "TimeWindow":
                        _log.Write(string.Format("Outside time window ({0}-{1}), idle mode.", _config.TimeWindowStart, _config.TimeWindowEnd));
                        break;
                    case "CPU":
                    case "GPU":
                        // 源码中 CPU/GPU 超阈值对应的行族是 Load recovered（带 CPU/GPU 数值）
                        if (!cpuGpuReported)
                        {
                            _log.Write(string.Format("Load recovered, timer reset (CPU: {0:F1}%, GPU: {1:F1}%)", data.CpuPercent, data.GpuPercent));
                            cpuGpuReported = true;
                        }
                        break;
                    default:
                        _log.Write("Timer reset by custom logic");
                        break;
                }
            }
            if (!hasReason)
                _log.Write("Timer reset by custom logic");

            // 详情行（与计时块同源生成）
            if (_config.EnableNetworkCheck)
                _log.Write(string.Format("Network: {0:F1} KB/s", data.NetworkKBps));
            if (_config.EnableDiskCheck)
                _log.Write(string.Format("Disk: {0:F1} KB/s", data.DiskKBps));
            if (_config.EnableProcessCheck && _config.ProtectedProcesses != null && _config.ProtectedProcesses.Count > 0)
                _log.Write(string.Format("Protected: {0}", runningProc ?? ""));
            if (_config.EnableTimeWindow)
                _log.Write(string.Format("TimeWindow: {0}", inWindow));
            _log.Write(string.Format("User: {0:F0}s", data.IdleSeconds));
            _lastLogTime = DateTime.Now;
        }

        // 计时周期：Idle 首行 + 全指标详情行（含 User，字段与状态块一致）
        private void WriteIdleBlock(HardwareData data, string runningProc, bool inWindow)
        {
            if ((DateTime.Now - _lastLogTime).TotalSeconds < _config.Interval)
                return;

            _log.Write(string.Format("Idle: {0:F1} sec (CPU: {1:F1}%, GPU: {2:F1}%)", _elapsed, data.CpuPercent, data.GpuPercent));
            if (_config.EnableNetworkCheck)
                _log.Write(string.Format("Network: {0:F1} KB/s", data.NetworkKBps));
            if (_config.EnableDiskCheck)
                _log.Write(string.Format("Disk: {0:F1} KB/s", data.DiskKBps));
            if (_config.EnableProcessCheck && _config.ProtectedProcesses != null && _config.ProtectedProcesses.Count > 0)
                _log.Write(string.Format("Protected: {0}", runningProc ?? ""));
            if (_config.EnableTimeWindow)
                _log.Write(string.Format("TimeWindow: {0}", inWindow));
            _log.Write(string.Format("User: {0:F0}s", data.IdleSeconds));
            _lastLogTime = DateTime.Now;
        }

        private bool ShowCountdownWindow(int seconds, string powerAction)
        {
            bool canceled = false;
            AutoResetEvent waitHandle = new AutoResetEvent(false);

            Thread formThread = new Thread(() =>
            {
                System.Windows.Forms.Form form = new System.Windows.Forms.Form();
                form.Text = "AutoSleep \u63d0\u9192";
                form.Size = new System.Drawing.Size(350, 130);
                form.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
                form.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
                form.ControlBox = false;
                form.TopMost = true;

                System.Windows.Forms.Label label = new System.Windows.Forms.Label();
                label.Text = string.Format("\u7535\u8111\u5c06\u5728 {0} \u79d2\u540e\u8fdb\u5165 {1}\uff0c\u70b9\u51fb\u53d6\u6d88\u53ef\u963b\u6b62\u3002", seconds, powerAction);
                label.Location = new System.Drawing.Point(15, 20);
                label.Size = new System.Drawing.Size(310, 30);
                form.Controls.Add(label);

                System.Windows.Forms.Button button = new System.Windows.Forms.Button();
                button.Text = "\u53d6\u6d88";
                button.Location = new System.Drawing.Point(125, 60);
                button.Size = new System.Drawing.Size(80, 25);
                form.Controls.Add(button);

                // 倒计时更新和超时关闭
                System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
                timer.Interval = 1000;
                int remaining = seconds;
                timer.Tick += (s, e) =>
                {
                    remaining--;
                    if (remaining <= 0)
                    {
                        timer.Stop();
                        form.Close();
                    }
                    else
                    {
                        label.Text = string.Format("\u7535\u8111\u5c06\u5728 {0} \u79d2\u540e\u8fdb\u5165 {1}\uff0c\u70b9\u51fb\u53d6\u6d88\u53ef\u963b\u6b62\u3002", remaining, powerAction);
                    }
                };
                timer.Start();

                button.Click += (sender, args) =>
                {
                    canceled = true;
                    timer.Stop();
                    form.Close();
                };

                form.FormClosed += (sender, args) =>
                {
                    waitHandle.Set();
                };

                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.Run(form);
            });

            formThread.SetApartmentState(ApartmentState.STA);
            formThread.Start();

            // 等待窗口自然关闭（按钮取消或计时结束），不超时、不 Abort
            waitHandle.WaitOne();

            return canceled;
        }
    }
}
