using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoSleep.Deploy
{
    static class Deployer
    {
        private const string InstallDir = @"C:\ProgramData\AutoSleep";
        private const string TaskName = "AutoSleep";
        private const string ShortcutName = "AutoSleep 设置";
        private const string RegUninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AutoSleep";
        private const string ConfigFile = InstallDir + @"\settings.json";
        private const string LogFile = InstallDir + @"\AutoSleep.log";

        private static string _logPath;

        [STAThread]
        static void Main(string[] args)
        {
            _logPath = Path.Combine(Path.GetTempPath(), "AutoSleepDeploy.log");
            if (File.Exists(_logPath)) File.Delete(_logPath);

            WriteLog("===== AutoSleep 部署工具启动 =====");

            // ---- 提权检查 ----
            if (!IsAdministrator())
            {
                WriteLog("当前非管理员权限，正在尝试提权...");
                var proc = new ProcessStartInfo
                {
                    FileName = Process.GetCurrentProcess().MainModule.FileName,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                try { Process.Start(proc); }
                catch { WriteLog("用户拒绝提权，退出。"); }
                return;
            }
            WriteLog("已获得管理员权限。");

            // ---- 环境检测 ----
            WriteLog("----- 环境检测 -----");
            WriteLog(string.Format("操作系统版本: {0}", Environment.OSVersion.Version));

            // GPU 计数器检测
            try
            {
                var gpuCategory = new System.Diagnostics.PerformanceCounterCategory("GPU Engine");
                string[] instances = gpuCategory.GetInstanceNames();
                bool gpuOk = false;
                foreach (string inst in instances)
                {
                    if (inst.Contains("engtype_3D") || inst.Contains("engtype_Compute"))
                    {
                        gpuOk = true;
                        break;
                    }
                }
                WriteLog(gpuOk ? "GPU 利用率计数器可用" : "GPU 利用率计数器不可用（将使用仅 CPU 模式）");
            }
            catch
            {
                WriteLog("GPU 利用率计数器不可用（将使用仅 CPU 模式）");
            }

            // ---- 目录可写检查 ----
            WriteLog("----- 目录检查 -----");
            if (!Directory.Exists(InstallDir))
                Directory.CreateDirectory(InstallDir);
            string testFile = Path.Combine(InstallDir, "test.tmp");
            try
            {
                File.WriteAllText(testFile, "");
                File.Delete(testFile);
                WriteLog("目录 " + InstallDir + " 可写");
            }
            catch
            {
                WriteLog("目录 " + InstallDir + " 不可写，请检查权限。");
                MessageBox.Show("安装目录不可写，请以管理员身份运行。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // ---- 检测升级 ----
            bool isUpgrade = File.Exists(ConfigFile);
            string logBackupPath = Path.Combine(Path.GetTempPath(), "AutoSleep.log.bak");
            Dictionary<string, object> oldConfig = null;

            if (isUpgrade)
            {
                WriteLog("检测到升级安装，执行前置备份...");

                if (File.Exists(LogFile))
                {
                    File.Copy(LogFile, logBackupPath, true);
                    WriteLog("日志已备份到 " + logBackupPath);
                }

                try
                {
                    string json = File.ReadAllText(ConfigFile);
                    var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                    oldConfig = serializer.Deserialize<Dictionary<string, object>>(json);
                    WriteLog("旧配置已提取");
                }
                catch
                {
                    WriteLog("无法读取旧配置，将使用默认配置");
                }

                string uninstallExe = Path.Combine(InstallDir, "Uninstall.exe");
                if (File.Exists(uninstallExe))
                {
                    WriteLog("正在执行卸载程序...");
                    var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = uninstallExe,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    if (p != null) p.WaitForExit();
                    System.Threading.Thread.Sleep(2000);
                    WriteLog("卸载完成");
                }
            }

            // 重新创建目录
            if (!Directory.Exists(InstallDir))
                Directory.CreateDirectory(InstallDir);

            // ---- 复制文件 ----
            WriteLog("----- 复制文件 -----");
            string sourceDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            string[] filesToCopy = {
                "AutoSleep.exe", "AutoSleepSettings.exe", "AutoSleepServer.exe",
                "Uninstall.exe", "README.txt", "editor.html"
            };
            foreach (string file in filesToCopy)
            {
                string src = Path.Combine(sourceDir, file);
                string dst = Path.Combine(InstallDir, file);
                if (File.Exists(src))
                {
                    File.Copy(src, dst, true);
                    WriteLog("已复制: " + file);
                }
                else
                {
                    WriteLog("未找到: " + file + "，跳过");
                }
            }

            // Win7 需要 curl.exe（自带 TLS 栈，不走 Schannel），Win10+ 不需要
            if (IsWindows7())
            {
                string curlSrc = Path.Combine(sourceDir, "curl.exe");
                string curlDst = Path.Combine(InstallDir, "curl.exe");
                if (File.Exists(curlSrc))
                {
                    File.Copy(curlSrc, curlDst, true);
                    WriteLog("已复制: curl.exe（Win7 专用）");
                }
                else
                {
                    WriteLog("未找到: curl.exe，跳过（Win7 检查更新将不可用）");
                }
            }
            else
            {
                WriteLog("跳过 curl.exe（非 Win7 系统，不需要）");
            }

            // ---- 休眠检测与配置 ----
            WriteLog("----- 休眠配置 -----");
            bool hibernateOn = GetHibernateStatus();
            string defaultPowerAction = "Hibernate";

            if (!isUpgrade)
            {
                if (!hibernateOn)
                {
                    long hiberFileSize = 0;
                    try
                    {
                        var fi = new FileInfo(@"C:\hiberfil.sys");
                        if (fi.Exists) hiberFileSize = fi.Length;
                    }
                    catch { }

                    string sizeHint;
                    if (hiberFileSize > 0)
                        sizeHint = "约 " + Math.Round(hiberFileSize / 1024.0 / 1024.0 / 1024.0, 1) + " GB";
                    else
                        sizeHint = "约 " + (GetTotalMemoryGB() * 0.5).ToString("F1") + " GB（通常为物理内存的 40%~60%）";

                    string msg = "休眠功能可让电脑在完全断电后恢复状态，但会在 C 盘占用空间。\n\n"
                              + sizeHint + "\n\n是否开启休眠功能？\n"
                              + "（选择「否」将使用睡眠模式，不占用额外磁盘空间）";
                    var result = MessageBox.Show(msg, "AutoSleep - 休眠功能设置", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (result == DialogResult.Yes)
                    {
                        hibernateOn = true;
                        defaultPowerAction = "Hibernate";
                    }
                    else
                    {
                        hibernateOn = false;
                        defaultPowerAction = "Sleep";
                    }
                }
            }
            else
            {
                if (oldConfig != null && oldConfig.ContainsKey("PowerAction"))
                    defaultPowerAction = oldConfig["PowerAction"].ToString();
            }

            var powerProc = Process.Start("powercfg.exe", hibernateOn ? "-h on" : "-h off");
            if (powerProc != null) powerProc.WaitForExit();
            WriteLog(hibernateOn ? "休眠功能已开启" : "休眠功能已关闭");

            // ---- 生成 settings.json ----
            WriteLog("----- 生成配置 -----");
            var defaults = new Dictionary<string, object>
            {
                { "PowerAction", defaultPowerAction },
                { "DurationMin", 15 },
                { "CpuThreshold", 30 },
                { "GpuThreshold", 30 },
                { "Interval", 5 },
                { "EnableGpuCheck", true },
                { "EnableUserActivity", true },
                { "EnableNetworkCheck", true },
                { "NetworkThresholdKBps", 1024 },
                { "EnableDiskCheck", true },
                { "DiskThresholdKBps", 10240 },
                { "EnableProcessCheck", false },
                { "ProtectedProcesses", new List<object>() },
                { "EnableTimeWindow", false },
                { "TimeWindowStart", 2 },
                { "TimeWindowEnd", 7 },
                { "ClearLogOnNextRun", false },
                { "EnableLogRotation", false },
                { "LogRetentionDays", 30 },
                { "LastRotationTime", null },
                { "CustomLogicEnabled", false },
                { "CustomLogicTree", null }
            };

            if (oldConfig != null)
            {
                foreach (var key in new List<string>(defaults.Keys))
                {
                    if (oldConfig.ContainsKey(key))
                        defaults[key] = oldConfig[key];
                }
                WriteLog("已恢复旧配置");
            }

            var serializer2 = new System.Web.Script.Serialization.JavaScriptSerializer();
            string configJson = serializer2.Serialize(defaults);
            File.WriteAllText(ConfigFile, configJson);
            WriteLog("配置文件已生成");

            // ---- 恢复日志 ----
            if (isUpgrade && File.Exists(logBackupPath))
            {
                try
                {
                    File.Copy(logBackupPath, LogFile, true);
                    File.Delete(logBackupPath);
                    WriteLog("日志已恢复");
                }
                catch { }
            }

            // ---- 创建桌面快捷方式 ----
            WriteLog("----- 创建快捷方式 -----");
            try
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string shortcutPath = Path.Combine(desktopPath, ShortcutName + ".lnk");
                string psCmd = string.Format(
                    "$ws = New-Object -ComObject WScript.Shell; " +
                    "$sc = $ws.CreateShortcut('{0}'); " +
                    "$sc.TargetPath = '{1}'; " +
                    "$sc.WorkingDirectory = '{2}'; " +
                    "$sc.Save();",
                    shortcutPath.Replace("'", "''"),
                    Path.Combine(InstallDir, "AutoSleepSettings.exe").Replace("'", "''"),
                    InstallDir.Replace("'", "''")
                );
                var proc = Process.Start("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + psCmd + "\"");
                if (proc != null) proc.WaitForExit();
                WriteLog("桌面快捷方式已创建");
            }
            catch (Exception ex)
            {
                WriteLog("创建快捷方式失败: " + ex.Message);
            }

            // ---- 创建计划任务（用 schtasks.exe + XML，兼容 Win7）----
            WriteLog("----- 创建计划任务 -----");
            try
            {
                string exePath = Path.Combine(InstallDir, "AutoSleep.exe");
                string userName = Environment.UserName;
                string xmlContent = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n"
                    + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n"
                    + "  <RegistrationInfo>\r\n"
                    + "    <Date>" + DateTime.Now.ToString("yyyy-MM-ddT00:00:00") + "</Date>\r\n"
                    + "    <Author>AutoSleep</Author>\r\n"
                    + "  </RegistrationInfo>\r\n"
                    + "  <Triggers>\r\n"
                    + "    <BootTrigger>\r\n"
                    + "      <Enabled>true</Enabled>\r\n"
                    + "    </BootTrigger>\r\n"
                    + "    <LogonTrigger>\r\n"
                    + "      <UserId>" + userName + "</UserId>\r\n"
                    + "      <Enabled>true</Enabled>\r\n"
                    + "    </LogonTrigger>\r\n"
                    + "  </Triggers>\r\n"
                    + "  <Principals>\r\n"
                    + "    <Principal id=\"Author\">\r\n"
                    + "      <UserId>" + userName + "</UserId>\r\n"
                    + "      <LogonType>InteractiveToken</LogonType>\r\n"
                    + "      <RunLevel>HighestAvailable</RunLevel>\r\n"
                    + "    </Principal>\r\n"
                    + "  </Principals>\r\n"
                    + "  <Settings>\r\n"
                    + "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n"
                    + "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n"
                    + "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n"
                    + "  </Settings>\r\n"
                    + "  <Actions Context=\"Author\">\r\n"
                    + "    <Exec>\r\n"
                    + "      <Command>" + exePath + "</Command>\r\n"
                    + "    </Exec>\r\n"
                    + "  </Actions>\r\n"
                    + "</Task>\r\n";

                string xmlPath = Path.Combine(Path.GetTempPath(), "AutoSleepTask.xml");
                File.WriteAllText(xmlPath, xmlContent, System.Text.Encoding.Unicode);

                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = "/create /tn \"AutoSleep\" /xml \"" + xmlPath + "\" /f",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                if (p != null) p.WaitForExit();

                try { File.Delete(xmlPath); } catch { }

                WriteLog("计划任务 'AutoSleep' 已创建（开机 + 登录双触发器）");
            }
            catch (Exception ex)
            {
                WriteLog("创建计划任务失败: " + ex.Message);
            }

            // ---- 写入注册表卸载项 ----
            WriteLog("----- 写入注册表 -----");
            try
            {
                using (var key = Registry.LocalMachine.CreateSubKey(RegUninstallPath))
                {
                    if (key != null)
                    {
                        key.SetValue("DisplayName", "AutoSleep 智能休眠工具");
                        key.SetValue("DisplayVersion", "1.0.11");
                        key.SetValue("Publisher", "Cesium-developer");
                        key.SetValue("InstallLocation", InstallDir);
                        key.SetValue("DisplayIcon", Path.Combine(InstallDir, "AutoSleepSettings.exe"));
                        key.SetValue("UninstallString", Path.Combine(InstallDir, "Uninstall.exe"));
                        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    }
                }
                WriteLog("注册表卸载项已写入");
            }
            catch (Exception ex)
            {
                WriteLog("写入注册表失败: " + ex.Message);
            }

            // ---- 启动服务 ----
            WriteLog("----- 启动服务 -----");
            try
            {
                Process.Start(Path.Combine(InstallDir, "AutoSleep.exe"));
                WriteLog("AutoSleep 已启动");
            }
            catch (Exception ex)
            {
                WriteLog("启动失败: " + ex.Message);
            }

            // ---- 验证 ----
            System.Threading.Thread.Sleep(5000);
            if (File.Exists(LogFile))
            {
                WriteLog("验证成功：日志文件已生成");
            }
            else
            {
                WriteLog("警告：未检测到日志文件");
            }

            WriteLog("===== 部署完成 =====");

            Console.WriteLine("按 Enter 退出...");
            Console.ReadLine();
        }

        static bool IsAdministrator()
        {
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        static bool GetHibernateStatus()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power"))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("HibernateEnabled");
                        if (val != null && val.ToString() == "1")
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        static int GetTotalMemoryGB()
        {
            try
            {
                var searcher = new System.Management.ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (var obj in searcher.Get())
                {
                    ulong bytes = Convert.ToUInt64(obj["TotalPhysicalMemory"]);
                    return (int)(bytes / (1024L * 1024L * 1024L));
                }
            }
            catch { }
            return 8;
        }

        static bool IsWindows7()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key == null) return false;
                    var majorVer = key.GetValue("CurrentMajorVersionNumber");
                    if (majorVer != null) return false;
                    var name = key.GetValue("ProductName") as string;
                    return name != null && name.IndexOf("Windows 7", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        static void WriteLog(string msg)
        {
            string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}", DateTime.Now, msg);
            Console.WriteLine(line);
            try { File.AppendAllText(_logPath, line + Environment.NewLine); }
            catch { }
        }
    }
}
