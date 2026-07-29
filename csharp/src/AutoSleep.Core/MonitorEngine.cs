using System;
using System.Collections.Generic;
using System.Threading;

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

            while (true)
            {
                DateTime now = DateTime.Now;
                double deltaSeconds = (now - lastCheckTime).TotalSeconds;
                lastCheckTime = now;

                // ---- 唤醒检测（永远优先） ----
                if (deltaSeconds > (sleepSeconds * 5))
                {
                    _log.Write("Wake from sleep, resetting timer.");
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
                bool inWindow = true;
                if (_config.EnableTimeWindow)
                {
                    int hour = now.Hour;
                    int start = _config.TimeWindowStart;
                    int end = _config.TimeWindowEnd;
                    if (start < end)
                        inWindow = (hour >= start && hour < end);
                    else
                        inWindow = (hour >= start || hour < end);
                }

                // ---- 条件变量化 ----
                bool cpuIdle = data.CpuPercent < _config.CpuThreshold;
                bool gpuIdle = !_config.EnableGpuCheck || data.GpuPercent < _config.GpuThreshold;
                bool diskIdle = !_config.EnableDiskCheck || data.DiskKBps < _config.DiskThresholdKBps;
                bool networkIdle = !_config.EnableNetworkCheck || data.NetworkKBps < _config.NetworkThresholdKBps;
                bool userIdle = !_config.EnableUserActivity || data.IdleSeconds >= 3;
                bool processIdle = !_config.EnableProcessCheck || runningProc == null;
                bool timeWindowIdle = !_config.EnableTimeWindow || inWindow;

                // ============================================================
                // 分支1：自定义逻辑（如果启用）
                // ============================================================
                if (_config.CustomLogicEnabled && _config.CustomLogicTree != null)
                {
                    var customValues = new Dictionary<string, bool>();
                    customValues["CPU"] = cpuIdle;
                    customValues["GPU"] = gpuIdle;
                    customValues["Disk"] = diskIdle;
                    customValues["Network"] = networkIdle;
                    customValues["User"] = userIdle;
                    customValues["Process"] = processIdle;
                    customValues["TimeWindow"] = timeWindowIdle;

                    RuleResult customResult = _rules.Evaluate(_config.CustomLogicTree, customValues);
                    bool idle = customResult.Idle;
                    string action = customResult.Action;

                    if (idle)
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

                        // 日志输出（对照原版逐行）
                        if ((now - _lastLogTime).TotalSeconds >= 5)
                        {
                            _log.Write(string.Format("Idle: {0:F1} sec (CPU: {1:F1}%, GPU: {2:F1}%)", _elapsed, data.CpuPercent, data.GpuPercent));
                            if (_config.EnableNetworkCheck)
                                _log.Write(string.Format("Network: {0:F1} KB/s", data.NetworkKBps));
                            if (_config.EnableDiskCheck)
                                _log.Write(string.Format("Disk: {0:F1} KB/s", data.DiskKBps));
                            if (_config.EnableProcessCheck && _config.ProtectedProcesses != null && _config.ProtectedProcesses.Count > 0)
                                _log.Write(string.Format("Protected: {0}", runningProc ?? ""));
                            if (_config.EnableTimeWindow)
                                _log.Write(string.Format("TimeWindow: {0}", inWindow));
                            _lastLogTime = now;
                        }
                    }
                    else
                    {
                        if (action == "reset_timer")
                        {
                            _log.Write("Timer reset by custom logic");
                            _elapsed = 0;
                        }
                        else if (action == "nothing")
                        {
                            // 什么都不做，保持 elapsed 不变
                        }
                        else
                        {
                            if (_elapsed > 0)
                                _log.Write(string.Format("Load recovered, timer reset (CPU: {0:F1}%, GPU: {1:F1}%)", data.CpuPercent, data.GpuPercent));
                            _elapsed = 0;
                            // 原版此处不更新 _lastLogTime，保持一致
                        }
                    }
                }
                // ============================================================
                // 分支2：原有硬编码逻辑（自定义未启用时执行）
                // ============================================================
                else
                {
                    // ---- 时间窗口 ----
                    if (_config.EnableTimeWindow && !inWindow)
                    {
                        if (_elapsed > 0)
                            _log.Write(string.Format("Outside time window ({0}-{1}), idle mode.", _config.TimeWindowStart, _config.TimeWindowEnd));
                        _elapsed = 0;
                        Thread.Sleep(60000);
                        continue;
                    }

                    // ---- 用户活动 ----
                    if (_config.EnableUserActivity && data.IdleSeconds < 3)
                    {
                        if (_elapsed > 0)
                            _log.Write("User activity detected, timer reset.");
                        _elapsed = 0;
                        Thread.Sleep(sleepSeconds * 1000);
                        continue;
                    }

                    // ---- 网络活动 ----
                    if (_config.EnableNetworkCheck && data.NetworkKBps > _config.NetworkThresholdKBps)
                    {
                        if (_elapsed > 0)
                            _log.Write(string.Format("Network activity detected ({0:F1} KB/s), timer reset.", data.NetworkKBps));
                        _elapsed = 0;
                    }

                    // ---- 磁盘活动 ----
                    if (_config.EnableDiskCheck && data.DiskKBps > _config.DiskThresholdKBps)
                    {
                        if (_elapsed > 0)
                            _log.Write(string.Format("Disk activity detected ({0:F1} KB/s), timer reset.", data.DiskKBps));
                        _elapsed = 0;
                    }

                    // ---- 进程白名单 ----
                    if (_config.EnableProcessCheck && _config.ProtectedProcesses != null && _config.ProtectedProcesses.Count > 0)
                    {
                        if (runningProc != null)
                        {
                            if (_elapsed > 0)
                                _log.Write(string.Format("Protected process pattern '{0}' is running, timer reset.", runningProc));
                            _elapsed = 0;
                        }
                    }

                    // ---- CPU / GPU 空闲判断（硬编码 AND）----
                    bool idle = cpuIdle && gpuIdle && diskIdle && networkIdle && userIdle && processIdle && timeWindowIdle;

                    if (idle)
                    {
                        _elapsed += deltaSeconds;
                        if ((now - _lastLogTime).TotalSeconds >= 5)
                        {
                            _log.Write(string.Format("Idle: {0:F1} sec (CPU: {1:F1}%, GPU: {2:F1}%)", _elapsed, data.CpuPercent, data.GpuPercent));
                            if (_config.EnableNetworkCheck)
                                _log.Write(string.Format("Network: {0:F1} KB/s", data.NetworkKBps));
                            if (_config.EnableDiskCheck)
                                _log.Write(string.Format("Disk: {0:F1} KB/s", data.DiskKBps));
                            if (_config.EnableProcessCheck && _config.ProtectedProcesses != null && _config.ProtectedProcesses.Count > 0)
                                _log.Write(string.Format("Protected: {0}", runningProc ?? ""));
                            if (_config.EnableTimeWindow)
                                _log.Write(string.Format("TimeWindow: {0}", inWindow));
                            _lastLogTime = now;
                        }
                    }
                    else
                    {
                        if (_elapsed > 0)
                            _log.Write(string.Format("Load recovered, timer reset (CPU: {0:F1}%, GPU: {1:F1}%)", data.CpuPercent, data.GpuPercent));
                        _elapsed = 0;
                        _lastLogTime = now;
                    }
                }

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
                _log.Write(string.Format("Log Rotation checking time: {0} hour (Default checking is 1 hour.)",
                    (DateTime.Now - _lastRotationCheck).TotalHours));

                Thread.Sleep(sleepSeconds * 1000);
            }
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
