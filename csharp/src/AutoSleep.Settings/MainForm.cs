using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoSleep.Settings
{
    public class MainForm : Form
    {
        private const string ConfigPath = @"C:\ProgramData\AutoSleep\settings.json";
        private const string InstallDir = @"C:\ProgramData\AutoSleep";
        private const string LogFile = @"C:\ProgramData\AutoSleep\AutoSleep.log";
        private const string ReadmeFile = @"C:\ProgramData\AutoSleep\README.txt";
        private const string ServerScript = @"C:\ProgramData\AutoSleep\AutoSleepServer.exe";
        private const string RegUninstallPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AutoSleep";

        private ComboBox _comboMode;
        private Label _lblHibernateStatus;
        private TextBox _txtDuration, _txtCpu, _txtGpu, _txtInterval;
        private TextBox _txtNetworkThreshold, _txtDiskThreshold;
        private TextBox _txtTimeStart, _txtTimeEnd, _txtRetentionDays;
        private CheckBox _chkGpu, _chkUser, _chkNetwork, _chkDisk, _chkProcess, _chkTimeWindow, _chkLogRotation, _chkCustomLogic;
        private ListBox _lstProcessList;
        private Button _btnToggleHibernate;

        private Dictionary<string, object> _config;

        public MainForm()
        {
            LoadConfig();
            BuildForm();
        }

        private void LoadConfig()
        {
            var defaults = new Dictionary<string, object>
            {
                { "PowerAction", "Hibernate" }, { "DurationMin", 15 }, { "CpuThreshold", 30 },
                { "GpuThreshold", 30 }, { "Interval", 5 }, { "EnableGpuCheck", true },
                { "EnableUserActivity", true }, { "EnableNetworkCheck", true }, { "NetworkThresholdKBps", 1024 },
                { "EnableProcessCheck", false }, { "ProtectedProcesses", new List<object>() },
                { "EnableTimeWindow", false }, { "TimeWindowStart", 2 }, { "TimeWindowEnd", 7 },
                { "ClearLogOnNextRun", false }, { "EnableDiskCheck", true }, { "DiskThresholdKBps", 10240 },
                { "EnableLogRotation", false }, { "LogRetentionDays", 30 },
                { "LastRotationTime", null },
                { "CustomLogicEnabled", false }, { "CustomLogicTree", null }
            };
            _config = new Dictionary<string, object>(defaults);
            if (File.Exists(ConfigPath))
            {
                try
                {
                    string json = File.ReadAllText(ConfigPath);
                    var loaded = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                    if (loaded != null)
                        foreach (var kv in loaded)
                            _config[kv.Key] = kv.Value;
                }
                catch { }
            }
        }

        private void SaveConfig()
        {
            // ---- 输入验证（对照原版 Settings.ps1）----
            string powerAction = _comboMode.SelectedItem == null ? "Sleep" : _comboMode.SelectedItem.ToString();
            int duration = ParseInt(_txtDuration.Text, -1);
            int cpu = ParseInt(_txtCpu.Text, -1);
            int gpu = ParseInt(_txtGpu.Text, -1);
            int interval = ParseInt(_txtInterval.Text, -1);
            int netThreshold = ParseInt(_txtNetworkThreshold.Text, -1);
            int diskThreshold = ParseInt(_txtDiskThreshold.Text, -1);
            int retentionDays = ParseInt(_txtRetentionDays.Text, -1);
            int timeStart = ParseInt(_txtTimeStart.Text, -1);
            int timeEnd = ParseInt(_txtTimeEnd.Text, -1);

            if (duration < 1) { MessageBox.Show("空闲等待时间必须大于 0。", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (cpu < 0) { MessageBox.Show("CPU 阈值必须大于等于 0。", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (gpu < 0) { MessageBox.Show("GPU 阈值必须大于等于 0。", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (interval < 3) { MessageBox.Show("检测间隔必须大于等于 3 秒。", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (netThreshold < 1) { MessageBox.Show("网络阈值必须大于等于 1。", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (diskThreshold < 1) { MessageBox.Show("磁盘阈值必须大于等于 1。", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (retentionDays < 1) { MessageBox.Show("保留天数必须大于等于 1。", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (timeStart < 0 || timeStart > 23) { MessageBox.Show("开始小时必须在 0-23 之间。", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (timeEnd < 0 || timeEnd > 23) { MessageBox.Show("结束小时必须在 0-23 之间。", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            // 检查休眠模式
            if (powerAction == "Hibernate" && !GetHibernateStatus())
            {
                MessageBox.Show("系统休眠功能未开启，无法使用休眠模式。\n请先点击「休眠开关」按钮开启休眠功能。", "休眠未开启", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _config["PowerAction"] = powerAction;
            _config["DurationMin"] = duration;
            _config["CpuThreshold"] = cpu;
            _config["GpuThreshold"] = gpu;
            _config["Interval"] = interval;
            _config["EnableGpuCheck"] = _chkGpu.Checked;
            _config["EnableUserActivity"] = _chkUser.Checked;
            _config["EnableNetworkCheck"] = _chkNetwork.Checked;
            _config["NetworkThresholdKBps"] = netThreshold;
            _config["EnableDiskCheck"] = _chkDisk.Checked;
            _config["DiskThresholdKBps"] = diskThreshold;
            _config["EnableProcessCheck"] = _chkProcess.Checked;
            _config["EnableTimeWindow"] = _chkTimeWindow.Checked;
            _config["TimeWindowStart"] = timeStart;
            _config["TimeWindowEnd"] = timeEnd;
            _config["EnableLogRotation"] = _chkLogRotation.Checked;
            _config["LogRetentionDays"] = retentionDays;
            _config["CustomLogicEnabled"] = _chkCustomLogic.Checked;
            var procList = new List<object>();
            foreach (var item in _lstProcessList.Items) procList.Add(item.ToString());
            _config["ProtectedProcesses"] = procList;
            try
            {
                string existingJson = File.ReadAllText(ConfigPath);
                var existingConfig = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(existingJson);
                if (existingConfig != null)
                {
                    // 总是从文件读 LastRotationTime（外部可能已更新）
                    if (existingConfig.ContainsKey("LastRotationTime"))
                        _config["LastRotationTime"] = existingConfig["LastRotationTime"];
                    // 补充 UI 不管理的其他字段
                    if (existingConfig.ContainsKey("CustomLogicTree"))
                        _config["CustomLogicTree"] = existingConfig["CustomLogicTree"];
                }
            }
            catch { }
            try
            {
                string json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(_config);
                File.WriteAllText(ConfigPath, json);
                MessageBox.Show("设置已保存！", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                KillAutoSleepProcesses();
                RunScheduledTask("AutoSleep");
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败：\n\n" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BuildForm()
        {
            Text = "AutoSleep 设置";
            var screen = Screen.PrimaryScreen.WorkingArea;
            int formHeight = Math.Min(920, (int)(screen.Height * 0.9));
            Size = new Size(480, formHeight);
            AutoScroll = true;
            AutoScrollMinSize = new Size(0, 920);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            int top = 20, labelWidth = 160, controlWidth = 200, leftLabel = 20, leftControl = 190, rowHeight = 40;

            // 1. 模式
            AddLabel("休眠/睡眠模式：", leftLabel, top, labelWidth, 25);
            _comboMode = new ComboBox { Location = NewPoint(leftControl, top), Size = new Size(controlWidth, 25), DropDownStyle = ComboBoxStyle.DropDownList };
            _comboMode.Items.AddRange(new[] { "Hibernate", "Sleep" });
            bool hibernateOn = GetHibernateStatus();
            if (!hibernateOn)
            {
                _comboMode.Items.Remove("Hibernate");
                if (_comboMode.Items.Count == 1 && _comboMode.Items[0].ToString() == "Sleep") _comboMode.SelectedIndex = 0;
                if (GetConfigStr("PowerAction") == "Hibernate") { _config["PowerAction"] = "Sleep"; SaveConfig(); }
                _comboMode.SelectedItem = "Sleep";
            }
            else { _comboMode.SelectedItem = GetConfigStr("PowerAction"); }
            Controls.Add(_comboMode);
            _lblHibernateStatus = new Label { Text = hibernateOn ? "✅ 休眠功能已开启" : "⚠️ 休眠功能未开启，仅可使用睡眠模式", Location = NewPoint(leftControl, top += rowHeight), Size = new Size(controlWidth + 60, 25), ForeColor = hibernateOn ? Color.LightGreen : Color.Yellow };
            Controls.Add(_lblHibernateStatus);
            top += rowHeight;

            // 2. 空闲等待时间
            top += rowHeight - 40;
            AddLabel("空闲等待时间（分钟）：", leftLabel, top, labelWidth, 25);
            _txtDuration = AddTextBox(leftControl, top, controlWidth, GetConfigInt("DurationMin").ToString());
            top += rowHeight;

            // 3-5. CPU、GPU、间隔
            AddLabel("CPU 阈值（%）：", leftLabel, top, labelWidth, 25); _txtCpu = AddTextBox(leftControl, top, controlWidth, GetConfigInt("CpuThreshold").ToString()); top += rowHeight;
            AddLabel("GPU 阈值（%）：", leftLabel, top, labelWidth, 25); _txtGpu = AddTextBox(leftControl, top, controlWidth, GetConfigInt("GpuThreshold").ToString()); top += rowHeight;
            AddLabel("检测间隔（秒，最小3）：", leftLabel, top, labelWidth, 25); _txtInterval = AddTextBox(leftControl, top, controlWidth, GetConfigInt("Interval").ToString()); top += rowHeight;

            // 6-7. 开关
            _chkGpu = AddCheckBox("启用 GPU 检测", leftControl, top, controlWidth, GetConfigBool("EnableGpuCheck")); top += rowHeight;
            _chkUser = AddCheckBox("启用用户活动检测", leftControl, top, controlWidth, GetConfigBool("EnableUserActivity")); top += rowHeight;

            // 8. 网络
            _chkNetwork = AddCheckBox("启用网络活动检测", leftLabel, top, 160, GetConfigBool("EnableNetworkCheck"));
            _txtNetworkThreshold = AddTextBox(leftControl, top, 80, GetConfigInt("NetworkThresholdKBps").ToString());
            AddLabel("KB/s", leftControl + 90, top + 3, 40, 20); top += rowHeight;

            // 9. 磁盘
            _chkDisk = AddCheckBox("启用磁盘活动检测", leftLabel, top, 160, GetConfigBool("EnableDiskCheck"));
            _txtDiskThreshold = AddTextBox(leftControl, top, 80, GetConfigInt("DiskThresholdKBps").ToString());
            AddLabel("KB/s", leftControl + 90, top + 3, 40, 20); top += rowHeight;

            // 10. 进程白名单
            _chkProcess = new CheckBox { Text = "启用进程白名单（包含匹配）", Location = NewPoint(leftLabel - 10, top), Size = new Size(220, 25), Checked = GetConfigBool("EnableProcessCheck") };
            Controls.Add(_chkProcess);
            _lstProcessList = new ListBox { Location = NewPoint(leftControl, top + 25), Size = new Size(140, 80), SelectionMode = SelectionMode.One };
            foreach (var p in GetConfigList("ProtectedProcesses")) _lstProcessList.Items.Add(p);
            Controls.Add(_lstProcessList);
            var btnAdd = new Button { Text = "添加", Location = NewPoint(leftControl + 145, top + 25), Size = new Size(50, 25) };
            btnAdd.Click += (s, e) => { string input = Microsoft.VisualBasic.Interaction.InputBox("输入进程名（不包含 .exe）：", "添加进程", ""); if (!string.IsNullOrWhiteSpace(input)) _lstProcessList.Items.Add(input.Trim()); };
            Controls.Add(btnAdd);
            var btnRemove = new Button { Text = "删除", Location = NewPoint(leftControl + 145, top + 25 + 30), Size = new Size(50, 25) };
            btnRemove.Click += (s, e) => { if (_lstProcessList.SelectedItem != null) _lstProcessList.Items.Remove(_lstProcessList.SelectedItem); };
            Controls.Add(btnRemove);
            var lblProcessHint = new Label { Text = "（点击添加/删除管理进程，匹配不区分大小写）", Location = NewPoint(leftControl, top + 25 + 85), AutoSize = true, ForeColor = Color.Gray };
            Controls.Add(lblProcessHint);
            top += rowHeight + 100 + 25;

            // 11. 时间窗口
            _chkTimeWindow = AddCheckBox("启用时间窗口", leftLabel, top, 160, GetConfigBool("EnableTimeWindow"));
            AddLabel("开始小时（0-23）：", leftControl, top, 120, 25); _txtTimeStart = AddTextBox(leftControl + 120, top, 40, GetConfigInt("TimeWindowStart").ToString());
            AddLabel("结束小时：", leftControl + 170, top, 60, 25); _txtTimeEnd = AddTextBox(leftControl + 230, top, 40, GetConfigInt("TimeWindowEnd").ToString());
            top += rowHeight + 10;

            // 12. 日志轮转
            _chkLogRotation = AddCheckBox("启用日志轮转", leftLabel, top, 160, GetConfigBool("EnableLogRotation"));
            AddLabel("保留天数：", leftControl, top, 80, 25); _txtRetentionDays = AddTextBox(leftControl + 80, top, 50, GetConfigInt("LogRetentionDays").ToString());
            AddLabel("天", leftControl + 135, top + 3, 30, 20); top += rowHeight;

            // 按钮行1
            var btnSave = new Button { Text = "保存", Location = NewPoint(20, top), Size = new Size(80, 30) };
            btnSave.Click += (s, e) => SaveConfig(); Controls.Add(btnSave);
            var btnHelp = new Button { Text = "帮助", Location = NewPoint(110, top), Size = new Size(80, 30) };
            btnHelp.Click += (s, e) => { if (File.Exists(ReadmeFile)) Process.Start("notepad.exe", ReadmeFile); else MessageBox.Show(string.Format("帮助文件未找到，请确认安装完整。\n路径：{0}", ReadmeFile), "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); };
            Controls.Add(btnHelp);
            var btnCancel = new Button { Text = "取消", Location = NewPoint(200, top), Size = new Size(80, 30) };
            btnCancel.Click += (s, e) => Close(); Controls.Add(btnCancel);
            var btnClearLog = new Button { Text = "清空日志", Location = NewPoint(290, top), Size = new Size(80, 30) };
            btnClearLog.Click += (s, e) => { KillAutoSleepProcesses(); System.Threading.Thread.Sleep(200); if (File.Exists(LogFile)) { File.Delete(LogFile); try { string json = File.ReadAllText(ConfigPath); var cfg = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); if (cfg != null) { cfg["LastRotationTime"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); File.WriteAllText(ConfigPath, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(cfg)); } } catch { } MessageBox.Show("日志已清空。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information); } else { MessageBox.Show("日志文件不存在，无需清空。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); } RunScheduledTask("AutoSleep"); };
            Controls.Add(btnClearLog);
            var btnShowLog = new Button { Text = "显示日志", Location = NewPoint(380, top), Size = new Size(80, 30) };
            btnShowLog.Click += (s, e) => { if (File.Exists(LogFile)) { string cmd = "$Host.UI.RawUI.BackgroundColor = 'Black'; $Host.UI.RawUI.ForegroundColor = 'White'; Clear-Host; $Host.UI.RawUI.WindowTitle = 'AutoSleep 日志监控'; Get-Content '" + LogFile + "' -Wait"; Process.Start("powershell.exe", "-NoProfile -NoExit -Command \"" + cmd + "\""); } else { MessageBox.Show(string.Format("日志文件尚未生成，请先运行主程序。\n路径：{0}", LogFile), "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); } };
            Controls.Add(btnShowLog);

            // 按钮行2
            top += rowHeight + 10;
            _chkCustomLogic = new CheckBox { Text = "启用自定义逻辑", Location = NewPoint(150, top + rowHeight + 10), Size = new Size(160, 30), Checked = GetConfigBool("CustomLogicEnabled") };
            Controls.Add(_chkCustomLogic);
            var btnCustomLogic = new Button { Text = "🧩 自定义逻辑", Location = NewPoint(20, top + rowHeight + 10), Size = new Size(120, 30) };
            btnCustomLogic.Click += (s, e) =>
            {
                try { var req = (HttpWebRequest)WebRequest.Create("http://localhost:56790/shutdown"); req.Timeout = 2000; try { req.GetResponse().Close(); } catch { } } catch { }
                foreach (var proc in Process.GetProcessesByName("AutoSleepServer")) { try { proc.Kill(); } catch { } }
                System.Threading.Thread.Sleep(500);
                if (!File.Exists(ServerScript)) { MessageBox.Show(string.Format("服务器程序未找到，请确认安装完整。\n路径：{0}", ServerScript), "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                Process.Start(ServerScript);
                System.Threading.Thread.Sleep(800);
                Process.Start("http://localhost:56790/editor.html");
            };
            Controls.Add(btnCustomLogic);
            var lblCustomInfo = new Label { Text = "启用后，空闲判断将完全由逻辑树控制。阈值沿用当前设置，独立开关仅对默认模式生效。", Location = NewPoint(20, top + rowHeight + 45), Size = new Size(420, 35), ForeColor = Color.Gray, Font = new Font("Microsoft YaHei", 8) };
            Controls.Add(lblCustomInfo);

            // 休眠开关
            top += rowHeight + 80;
            _btnToggleHibernate = new Button { Location = NewPoint(150, top), Size = new Size(160, 30) };
            _btnToggleHibernate.Click += (s, e) => ToggleHibernate();
            Controls.Add(_btnToggleHibernate);
            var lblHibernateHint = new Label { Text = "点击按钮可在系统级开关休眠功能", Location = NewPoint(20, top + 5), Size = new Size(130, 30), ForeColor = Color.Gray, Font = new Font("Microsoft YaHei", 8) };
            Controls.Add(lblHibernateHint);
            RefreshHibernateUI();

            // 按钮行3
            top += rowHeight + 80;
            var btnUpdate = new Button { Text = "检查更新", Location = NewPoint(20, top), Size = new Size(80, 30) };
            btnUpdate.Click += (s, e) => CheckUpdate();
            Controls.Add(btnUpdate);
        }

        private Point NewPoint(int x, int y) { return new Point(x, y); }
        private Label AddLabel(string text, int x, int y, int w, int h) { var lbl = new Label { Text = text, Location = NewPoint(x, y), Size = new Size(w, h) }; Controls.Add(lbl); return lbl; }
        private TextBox AddTextBox(int x, int y, int w, string text) { var tb = new TextBox { Location = NewPoint(x, y), Size = new Size(w, 25), Text = text }; Controls.Add(tb); return tb; }
        private CheckBox AddCheckBox(string text, int x, int y, int w, bool c) { var cb = new CheckBox { Text = text, Location = NewPoint(x, y), Size = new Size(w, 25), Checked = c }; Controls.Add(cb); return cb; }
        private int ParseInt(string text, int def) { text = (text == null ? null : text.Trim()); if (string.IsNullOrEmpty(text)) return def; int result; int.TryParse(text, out result); return result; }
        private string GetConfigStr(string key) { if (_config.ContainsKey(key) && _config[key] != null) return _config[key].ToString(); return ""; }
        private int GetConfigInt(string key) { try { return Convert.ToInt32(_config.ContainsKey(key) ? _config[key] : 0); } catch { return 0; } }
        private bool GetConfigBool(string key) { try { return _config.ContainsKey(key) && Convert.ToBoolean(_config[key]); } catch { return false; } }
        private List<string> GetConfigList(string key) { if (!_config.ContainsKey(key)) return new List<string>(); var list = _config[key] as List<object>; if (list == null) return new List<string>(); return list.ConvertAll(x => x == null ? "" : x.ToString()); }
        private string GetRegistryString(string regPath, string valueName, string defaultValue) { try { object val = Registry.GetValue(regPath, valueName, defaultValue); if (val != null) return val.ToString(); return defaultValue; } catch { return defaultValue; } }
        private bool GetHibernateStatus() { try { object val = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", 0); return val != null && Convert.ToInt32(val) == 1; } catch { return false; } }

        private void ToggleHibernate()
        {
            bool hibernateOn = GetHibernateStatus();
            var hiberFile = new FileInfo(@"C:\hiberfil.sys");
            string sizeHint = hiberFile.Exists ? "约 " + Math.Round(hiberFile.Length / 1024.0 / 1024.0 / 1024.0, 1) + " GB" : "约 " + (GetTotalMemoryGB() * 0.5).ToString("F1") + " GB（通常为物理内存的 40%~60%）";
            string msg = hibernateOn ? "关闭休眠功能将释放 C 盘 " + sizeHint + " 空间，但会失去休眠模式。\n\n确定要关闭休眠功能吗？" : "开启休眠功能将在 C 盘占用 " + sizeHint + " 空间，但可提供完整的休眠模式。\n\n确定要开启休眠功能吗？";
            string title = hibernateOn ? "确认关闭休眠" : "确认开启休眠";
            if (MessageBox.Show(msg, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                var p = Process.Start("powercfg.exe", hibernateOn ? "-h off" : "-h on");
                if (p != null) p.WaitForExit();
                RefreshHibernateUI();
                MessageBox.Show("休眠功能已" + (hibernateOn ? "关闭" : "开启") + "。", "操作完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void RefreshHibernateUI()
        {
            bool hibernateOn = GetHibernateStatus();
            _btnToggleHibernate.Text = hibernateOn ? "关闭休眠" : "开启休眠";
            _lblHibernateStatus.Text = hibernateOn ? "✅ 休眠功能已开启" : "⚠️ 休眠功能未开启，仅可使用睡眠模式";
            _lblHibernateStatus.ForeColor = hibernateOn ? Color.LightGreen : Color.Goldenrod;
            string current = _comboMode.SelectedItem == null ? null : _comboMode.SelectedItem.ToString();
            _comboMode.Items.Clear();
            if (hibernateOn) { _comboMode.Items.AddRange(new[] { "Hibernate", "Sleep" }); _comboMode.SelectedItem = (current == "Hibernate" || current == "Sleep") ? current : "Sleep"; }
            else { _comboMode.Items.Add("Sleep"); _comboMode.SelectedItem = "Sleep"; }
        }

        private double GetTotalMemoryGB() { try { var mos = new System.Management.ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"); foreach (var o in mos.Get()) return Convert.ToDouble(o["TotalPhysicalMemory"]) / 1024 / 1024 / 1024; } catch { } return 8; }

        private void KillAutoSleepProcesses()
        {
            foreach (var proc in Process.GetProcessesByName("AutoSleep")) { try { proc.Kill(); } catch { } }
            foreach (var proc in Process.GetProcessesByName("powershell")) { try { if (proc.MainWindowTitle.Contains("AutoSleep")) proc.Kill(); } catch { } }
        }

        private void RunScheduledTask(string taskName) { try { var p = Process.Start("schtasks.exe", "/run /tn \"" + taskName + "\""); if (p != null) p.WaitForExit(); } catch { } try { if (Process.GetProcessesByName("AutoSleep").Length == 0) Process.Start(Path.Combine(InstallDir, "AutoSleep.exe")); } catch { } }

        private static bool IsWindows7()
        {
            try
            {
                var reg = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (reg == null) return false;
                // Win10+ 有 CurrentMajorVersionNumber，Win7 没有
                var majorVer = reg.GetValue("CurrentMajorVersionNumber");
                if (majorVer != null) return false;
                var name = reg.GetValue("ProductName") as string;
                return name != null && name.IndexOf("Windows 7", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private string FetchApiWithCurl(string url)
        {
            string curlPath = Path.Combine(InstallDir, "curl.exe");
            if (!File.Exists(curlPath)) return null;
            try
            {
                var psi = new ProcessStartInfo(curlPath, "-sk --connect-timeout 10 -H \"User-Agent: AutoSleep\" \"" + url + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return null;
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                    return p.ExitCode == 0 && !string.IsNullOrEmpty(output) ? output : null;
                }
            }
            catch { return null; }
        }

        private bool DownloadWithCurl(string url, string outputPath)
        {
            string curlPath = Path.Combine(InstallDir, "curl.exe");
            if (!File.Exists(curlPath)) return false;
            try
            {
                var psi = new ProcessStartInfo(curlPath, "-skL --connect-timeout 15 --max-time 120 -o \"" + outputPath + "\" \"" + url + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    p.WaitForExit(120000);
                    return p.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
                }
            }
            catch { return false; }
        }

        private void CheckUpdate()
        {
            bool isWin7 = IsWindows7();

            string currentVersion = "0.0.0";
            try { currentVersion = GetRegistryString(RegUninstallPath, "DisplayVersion", "0.0.0"); } catch { }
            string[] mirrors = new string[] { "https://api.github.com/repos/Cesium-developer/IdleNap/releases/latest", "https://ghproxy.net/https://api.github.com/repos/Cesium-developer/IdleNap/releases/latest" };
            string latestVersion = null, releaseJson = null;

            if (isWin7)
            {
                // Win7：curl 自带 LibreSSL TLS 栈，不走 Schannel
                foreach (string url in mirrors)
                {
                    releaseJson = FetchApiWithCurl(url);
                    if (releaseJson != null) break;
                }
            }
            else
            {
                // Win10+：正常用 HttpWebRequest
                try { System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072; } catch { }
                foreach (string url in mirrors)
                {
                    try { var req = (HttpWebRequest)WebRequest.Create(url); req.Timeout = 5000; req.UserAgent = "AutoSleep"; req.Accept = "application/json"; using (var resp = req.GetResponse()) using (var reader = new StreamReader(resp.GetResponseStream())) { releaseJson = reader.ReadToEnd(); break; } }
                    catch { continue; }
                }
            }

            if (releaseJson == null) { MessageBox.Show("无法连接服务器，请检查网络后重试。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            try { var serializer = new System.Web.Script.Serialization.JavaScriptSerializer(); var data = serializer.Deserialize<Dictionary<string, object>>(releaseJson); string tag = (data != null && data.ContainsKey("tag_name") && data["tag_name"] != null) ? data["tag_name"].ToString() : ""; latestVersion = tag == null ? null : tag.TrimStart('v'); } catch { }
            if (string.IsNullOrEmpty(latestVersion)) { MessageBox.Show("无法获取最新版本信息。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            try { var latest = Version.Parse(latestVersion); var current = Version.Parse(currentVersion); if (latest <= current) { MessageBox.Show(string.Format("已是最新版本（当前 v{0}）。", currentVersion), "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; } } catch { }
            if (MessageBox.Show(string.Format("发现新版本 v{0}（当前 v{1}）。\n\n是否下载并安装？", latestVersion, currentVersion), "更新可用", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string downloadUrl = null;
            try { var serializer = new System.Web.Script.Serialization.JavaScriptSerializer(); var data = serializer.Deserialize<Dictionary<string, object>>(releaseJson); var assets = data != null ? data["assets"] as List<object> : null; if (assets != null) { foreach (var a in assets) { var asset = a as Dictionary<string, object>; string name = (asset != null && asset.ContainsKey("name") && asset["name"] != null) ? asset["name"].ToString() : ""; if (name != null && name.IndexOf("AutoSleep_Setup_Win7", StringComparison.OrdinalIgnoreCase) >= 0) { downloadUrl = (asset != null && asset.ContainsKey("browser_download_url") && asset["browser_download_url"] != null) ? asset["browser_download_url"].ToString() : null; break; } } } } catch { }
            if (string.IsNullOrEmpty(downloadUrl)) { MessageBox.Show("未找到 C# 版安装包，请手动从 GitHub Releases 下载。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            string tempInstaller = Path.Combine(Path.GetTempPath(), "AutoSleep_Setup_Win7_Net40.exe");
            bool downloadOk = false;
            if (isWin7)
                downloadOk = DownloadWithCurl(downloadUrl, tempInstaller);
            else
            {
                try { using (var wc = new WebClient()) wc.DownloadFile(downloadUrl, tempInstaller); downloadOk = true; } catch { }
            }
            if (!downloadOk) { MessageBox.Show("下载失败，请检查网络后重试。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            var p = Process.Start(tempInstaller); if (p != null) p.WaitForExit();
            try { File.Delete(tempInstaller); } catch { }
            MessageBox.Show("更新安装完成。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
    }
}
