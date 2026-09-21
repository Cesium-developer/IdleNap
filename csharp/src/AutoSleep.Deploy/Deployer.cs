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
        // Inno 卸载键（必须与 Setup.iss 的 AppId 一致）：64 位视图由 Inno 在安装阶段创建；
        // 升级时 Deployer 静默调用的卸载器会把它删除，须备份-恢复；32 位视图键为早期
        // 32 位模式安装遗留，必须清理，否则控制面板/GeekUninstaller 出现 32 位残留条目。
        private const string RegInnoUninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8F2C1D4E-5A6B-4C7D-8E9F-0A1B2C3D4E5F}_is1";
        private const string RegInnoUninstall32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{8F2C1D4E-5A6B-4C7D-8E9F-0A1B2C3D4E5F}_is1";
        private const string ConfigFile = InstallDir + @"\settings.json";
        private const string LogFile = InstallDir + @"\AutoSleep.log";

        private static string _logPath;

        [STAThread]
        static int Main(string[] args)
        {
            bool silent = false;
            foreach (string a in args)
            {
                if (a.Equals("/silent", StringComparison.OrdinalIgnoreCase)) silent = true;
            }

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
                return 1;
            }
            WriteLog("已获得管理员权限。");

            // ---- 暂存源文件 ----
            // Deployer 从 Inno 临时目录 {tmp}\AutoSleepInstall 运行；本流程调用的卸载器
            // （unins000.exe /VERYSILENT）会清理 Inno 安装会话的临时目录，导致源文件丢失。
            // 先把源文件复制到安全暂存目录，后续复制一律从暂存目录取。
            string stagingDir = Path.Combine(Path.GetTempPath(), "AutoSleepDeploySrc");
            string deploySourceDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            WriteLog("暂存源文件: " + deploySourceDir + " -> " + stagingDir);
            try
            {
                if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
                Directory.CreateDirectory(stagingDir);
                foreach (string f in Directory.GetFiles(deploySourceDir))
                {
                    File.Copy(f, Path.Combine(stagingDir, Path.GetFileName(f)), true);
                }
                WriteLog("源文件暂存完成（" + Directory.GetFiles(stagingDir).Length + " 个文件）");
            }
            catch (Exception ex)
            {
                WriteLog("源文件暂存失败: " + ex.Message);
            }

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
                if (!silent)
                    MessageBox.Show("安装目录不可写，请以管理员身份运行。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            // ---- 检测升级 ----
            bool isUpgrade = File.Exists(ConfigFile);
            string logBackupPath = Path.Combine(Path.GetTempPath(), "AutoSleep.log.bak");
            Dictionary<string, object> oldConfig = null;
            // Inno 卸载器备份（方法级作用域，供升级卸载后恢复）
            string uninsBackupExe = Path.Combine(Path.GetTempPath(), "AutoSleep.unins000.exe.bak");
            string uninsBackupDat = Path.Combine(Path.GetTempPath(), "AutoSleep.unins000.dat.bak");
            bool uninsBackedUp = false;
            // Inno 卸载键（64 位视图）备份：静默调用的卸载器会删除该键，须卸载前备份、部署后恢复
            Dictionary<string, object> innoUninstallValues = null;

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

                // 备份 Inno 卸载器（若存在）：Inno 在运行 Deployer 前已把 unins000.exe 释放到安装目录，
                // 旧版 Uninstall.exe 清目录会把它一并删除，须在卸载前备份、重建后恢复
                try
                {
                    string uninsExe = Path.Combine(InstallDir, "unins000.exe");
                    string uninsDat = Path.Combine(InstallDir, "unins000.dat");
                    if (File.Exists(uninsExe))
                    {
                        File.Copy(uninsExe, uninsBackupExe, true);
                        if (File.Exists(uninsDat)) File.Copy(uninsDat, uninsBackupDat, true);
                        uninsBackedUp = true;
                        WriteLog("已备份 Inno 卸载器");
                    }
                }
                catch { }

                // 备份 Inno 卸载键（64 位视图）：卸载器卸载时会删除此键，须在卸载前备份、部署后恢复。
                // Deployer 为 64 位进程，访问 HKLM\SOFTWARE 即 64 位视图，无需 RegistryView 参数
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(RegInnoUninstallPath))
                    {
                        if (key != null)
                        {
                            innoUninstallValues = new Dictionary<string, object>();
                            foreach (string name in key.GetValueNames())
                                innoUninstallValues[name] = key.GetValue(name);
                            WriteLog("已备份 Inno 卸载键（" + innoUninstallValues.Count + " 个值）");
                        }
                    }
                }
                catch (Exception ex)
                {
                    WriteLog("备份 Inno 卸载键失败: " + ex.Message);
                }

                // ---- 旧版卸载器检测 ----
                // 兼容旧版：1.0.13 及更早（NSIS）安装的卸载器是 Uninstall.exe，负责杀进程、
                // 删快捷方式/计划任务/注册表并清空目录，升级时必须调用它才能干净卸载旧版。
                // 注意：Inno 在运行 Deployer 前已把本次安装的 unins000.exe 与 {AppId}_is1
                // 卸载键写入安装目录。不能以 {AppId}_is1 为准去调用 unins000：它是本次安装
                // 自带的卸载器，不认识旧版文件、不杀进程，且其卸载记录包含 {tmp}\AutoSleepInstall
                // （Deployer 正运行其中的目录），调用会导致卸载器删除被占用文件而挂起
                // （1.0.13 检查更新升级异常即为此因）。因此只检测旧版卸载器；找不到时由
                // Deployer 自行清理（Inno 版升级同样适用：旧版 unins000 与 {AppId}_is1 键
                // 会被本次安装覆盖/重写，无需先卸载）。
                string uninstallCmd = null;
                bool innoMarker = false;

                // 1) 目录中的旧版卸载器（最可靠标志：1.0.13 及更早版本存在 Uninstall.exe）
                string legacyUninstaller = Path.Combine(InstallDir, "Uninstall.exe");
                if (File.Exists(legacyUninstaller))
                {
                    uninstallCmd = legacyUninstaller;
                    WriteLog("检测到旧版卸载器（Uninstall.exe），将调用以兼容旧版");
                }
                else
                {
                    // 2) 旧注册表键（1.0.13/NSIS 安装时写入）
                    try
                    {
                        using (var key = Registry.LocalMachine.OpenSubKey(RegUninstallPath))
                        {
                            if (key != null)
                                uninstallCmd = key.GetValue("UninstallString") as string;
                        }
                    }
                    catch { }
                }

                string[] cmdParts = string.IsNullOrEmpty(uninstallCmd) ? new string[0] : SplitCommandLine(uninstallCmd);
                string uninstallerPath = cmdParts.Length > 0 ? cmdParts[0] : null;
                string uninstallerArgs = cmdParts.Length > 1 ? string.Join(" ", cmdParts, 1, cmdParts.Length - 1) : "";

                if (!string.IsNullOrEmpty(uninstallerPath) && File.Exists(uninstallerPath))
                {
                    string fileName = Path.GetFileName(uninstallerPath);
                    bool isNewUninstaller = innoMarker || fileName.Equals("unins000.exe", StringComparison.OrdinalIgnoreCase);
                    if (isNewUninstaller)
                    {
                        uninstallerArgs = (uninstallerArgs + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART").Trim();
                        WriteLog("检测到新版卸载器（Inno Setup），静默调用: " + uninstallerPath + " " + uninstallerArgs);
                    }
                    else
                    {
                        WriteLog("正在执行旧版卸载程序: " + uninstallerPath + " " + uninstallerArgs);
                    }
                    var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = uninstallerPath,
                        Arguments = uninstallerArgs,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    if (p != null) p.WaitForExit();
                    System.Threading.Thread.Sleep(2000);
                    WriteLog("卸载完成");
                }
                else if (string.IsNullOrEmpty(uninstallerPath) && File.Exists(Path.Combine(InstallDir, "Uninstall.exe")))
                {
                    // 回退：注册表读不到但安装目录存在旧版卸载器
                    WriteLog("正在执行旧版卸载程序（目录回退）...");
                    var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = Path.Combine(InstallDir, "Uninstall.exe"),
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    if (p != null) p.WaitForExit();
                    System.Threading.Thread.Sleep(2000);
                    WriteLog("卸载完成");
                }
                else
                {
                    // 检测不到任何卸载器（注册表无键、目录无 Uninstall.exe）：
                    // 由 Deployer 自行清理旧版残留（杀进程/删计划任务/删快捷方式/清目录），
                    // 保证升级路径完全不依赖外部卸载程序
                    WriteLog("未检测到旧版卸载器，由 Deployer 自行清理旧版残留...");
                    try
                    {
                        foreach (var proc in Process.GetProcessesByName("AutoSleep")) proc.Kill();
                        foreach (var proc in Process.GetProcessesByName("AutoSleepSettings")) proc.Kill();
                        foreach (var proc in Process.GetProcessesByName("AutoSleepServer")) proc.Kill();
                        WriteLog("已终止 AutoSleep 相关进程");
                    }
                    catch { }
                    try
                    {
                        var p = Process.Start(new ProcessStartInfo
                        {
                            FileName = "schtasks.exe",
                            Arguments = "/delete /tn \"AutoSleep\" /f",
                            WindowStyle = ProcessWindowStyle.Hidden,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                        if (p != null) p.WaitForExit();
                        WriteLog("已删除计划任务 'AutoSleep'");
                    }
                    catch { }
                    try
                    {
                        string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), ShortcutName + ".lnk");
                        if (File.Exists(lnk)) File.Delete(lnk);
                        WriteLog("已删除桌面快捷方式");
                    }
                    catch { }
                    try
                    {
                        if (Directory.Exists(InstallDir))
                        {
                            foreach (string f in Directory.GetFiles(InstallDir))
                            {
                                try { File.Delete(f); } catch { }
                            }
                            foreach (string d in Directory.GetDirectories(InstallDir))
                            {
                                try { Directory.Delete(d, true); } catch { }
                            }
                            WriteLog("已清空安装目录");
                        }
                    }
                    catch { }
                    System.Threading.Thread.Sleep(2000);
                    WriteLog("旧版清理完成");
                }
            }

            // 重新创建目录
            if (!Directory.Exists(InstallDir))
                Directory.CreateDirectory(InstallDir);

            // ---- 确保旧版进程退出 ----
            // 旧版 Uninstall.exe 卸载时会终止 AutoSleep 三进程，但升级时静默调用的 Inno
            // 卸载器（unins000.exe）不会终止它们。若 AutoSleepSettings.exe（设置窗口，
            // 检查更新场景下必然在运行）或 AutoSleep.exe（计划任务监控）仍占用 exe 文件，
            // 后续 File.Copy 覆盖会因文件被占用而失败，导致安装中止（1.0.13 检查更新升级
            // 异常即为该原因）。因此复制前统一终止三进程，与旧版卸载器行为一致，不依赖
            // 卸载器类型；仅精确匹配 AutoSleep 进程名，不影响日志监控等其他进程。
            WriteLog("----- 确保旧版进程退出 -----");
            try
            {
                foreach (var proc in Process.GetProcessesByName("AutoSleep")) { try { proc.Kill(); } catch { } }
                foreach (var proc in Process.GetProcessesByName("AutoSleepSettings")) { try { proc.Kill(); } catch { } }
                foreach (var proc in Process.GetProcessesByName("AutoSleepServer")) { try { proc.Kill(); } catch { } }
                System.Threading.Thread.Sleep(1000);
                WriteLog("已终止 AutoSleep 相关进程");
            }
            catch { }

            // ---- 复制文件 ----
            WriteLog("----- 复制文件 -----");
            string sourceDir = stagingDir;   // 从安全暂存目录取源文件
            string[] filesToCopy = {
                "AutoSleep.exe", "AutoSleepSettings.exe", "AutoSleepServer.exe",
                "README.txt", "editor.html",
                "AutoSleep.ico"
            };
            foreach (string file in filesToCopy)
            {
                string src = Path.Combine(sourceDir, file);
                string dst = Path.Combine(InstallDir, file);
                if (!File.Exists(src))
                {
                    WriteLog("未找到: " + file + "，跳过");
                    continue;
                }
                if (!CopyFileWithRetry(src, dst, file))
                {
                    WriteLog("复制失败: " + file + "，安装中止");
                    return 1;
                }
            }

            // Win7 需要 curl.exe（自带 TLS 栈，不走 Schannel），Win10+ 不需要
            if (IsWindows7())
            {
                string curlSrc = Path.Combine(sourceDir, "curl.exe");
                string curlDst = Path.Combine(InstallDir, "curl.exe");
                if (File.Exists(curlSrc))
                {
                    if (!CopyFileWithRetry(curlSrc, curlDst, "curl.exe（Win7 专用）"))
                    {
                        WriteLog("复制失败: curl.exe，安装中止");
                        return 1;
                    }
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

            // ---- 恢复 Inno 卸载器备份 ----
            // 新的 unins000.exe 由 Inno 安装结束时写入覆盖，这里仅保证升级期间不被旧卸载器删掉
            if (uninsBackedUp)
            {
                try
                {
                    if (File.Exists(uninsBackupExe))
                    {
                        File.Copy(uninsBackupExe, Path.Combine(InstallDir, "unins000.exe"), true);
                        if (File.Exists(uninsBackupDat))
                            File.Copy(uninsBackupDat, Path.Combine(InstallDir, "unins000.dat"), true);
                        WriteLog("Inno 卸载器备份已恢复");
                    }
                }
                catch { }
                try { File.Delete(uninsBackupExe); File.Delete(uninsBackupDat); } catch { }
            }

            // ---- 恢复 Inno 卸载键 + 清理 32 位残留 ----
            // 升级时静默调用的卸载器删除了 64 位视图卸载键，恢复之，保证控制面板/GeekUninstaller
            // 有 64 位条目（UninstallString 指向的路径不变，值依然有效）；
            // 同时删除早期 32 位模式安装遗留的 WOW6432Node 视图键，避免出现 32 位残留条目。
            // 该清理对所有安装场景执行（升级/全新安装）。
            if (innoUninstallValues != null && innoUninstallValues.Count > 0)
            {
                try
                {
                    using (var key = Registry.LocalMachine.CreateSubKey(RegInnoUninstallPath))
                    {
                        foreach (var kv in innoUninstallValues)
                            key.SetValue(kv.Key, kv.Value);
                    }
                    WriteLog("Inno 卸载键已恢复（64 位视图）");
                }
                catch (Exception ex)
                {
                    WriteLog("恢复 Inno 卸载键失败: " + ex.Message);
                }
            }
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(RegInnoUninstall32Path, false);
                WriteLog("已清理 32 位视图残留卸载键");
            }
            catch { }

            // ---- 创建桌面快捷方式 ----
            WriteLog("----- 创建快捷方式 -----");
            try
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string shortcutPath = Path.Combine(desktopPath, ShortcutName + ".lnk");
                string settingsExe = Path.Combine(InstallDir, "AutoSleepSettings.exe").Replace("'", "''");
                string psCmd = string.Format(
                    "$ws = New-Object -ComObject WScript.Shell; " +
                    "$sc = $ws.CreateShortcut('{0}'); " +
                    "$sc.TargetPath = '{1}'; " +
                    "$sc.IconLocation = '{1}, 0'; " +
                    "$sc.WorkingDirectory = '{2}'; " +
                    "$sc.Save();",
                    shortcutPath.Replace("'", "''"),
                    settingsExe,
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

            // ---- 注册表卸载项 ----
            // 卸载项由 Inno Setup 统一管理（标准卸载器 unins000.exe、卸载键 {AppId}_is1），
            // Deployer 不再写入，避免控制面板出现重复卸载条目。
            WriteLog("----- 注册表 -----");
            WriteLog("卸载项由 Inno Setup 管理（跳过）");

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

            if (!silent)
            {
                Console.WriteLine("按 Enter 退出...");
                Console.ReadLine();
            }
            return 0;
        }

        // 简单命令行拆分：支持引号包裹的路径（Inno 的 UninstallString 带引号）
        static string[] SplitCommandLine(string cmd)
        {
            var parts = new List<string>();
            bool inQuote = false;
            string cur = "";
            foreach (char c in cmd)
            {
                if (c == '"') { inQuote = !inQuote; }
                else if (c == ' ' && !inQuote)
                {
                    if (cur.Length > 0) { parts.Add(cur); cur = ""; }
                }
                else cur += c;
            }
            if (cur.Length > 0) parts.Add(cur);
            return parts.ToArray();
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

        // 带重试的文件复制：升级时旧进程刚终止，文件句柄可能尚未完全释放，首两次失败
        // 属正常；重试后仍失败则明确返回 false，由调用方中止安装（Inno 报错提示用户），
        // 不再以未捕获异常崩溃导致静默失败。
        static bool CopyFileWithRetry(string src, string dst, string name)
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    File.Copy(src, dst, true);
                    WriteLog("已复制: " + name);
                    return true;
                }
                catch (Exception ex)
                {
                    if (attempt < 3)
                    {
                        WriteLog("复制 " + name + " 失败（第 " + attempt + " 次），重试: " + ex.Message);
                        System.Threading.Thread.Sleep(1000);
                    }
                    else
                    {
                        WriteLog("复制 " + name + " 失败（已重试 3 次）: " + ex.Message);
                    }
                }
            }
            return false;
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
