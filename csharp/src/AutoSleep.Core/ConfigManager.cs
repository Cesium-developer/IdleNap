using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace AutoSleep.Core
{
    /// <summary>
    /// 读取/写入 C:\ProgramData\AutoSleep\settings.json
    /// 格式与 PowerShell 版完全兼容。
    /// </summary>
    public class ConfigManager
    {
        private const string ConfigDir = @"C:\ProgramData\AutoSleep";
        private const string ConfigFile = ConfigDir + @"\settings.json";

        // ---- 全部配置字段（与 PowerShell 版一致）----
        public string PowerAction { get; set; }
        public int DurationMin { get; set; }
        public int CpuThreshold { get; set; }
        public int GpuThreshold { get; set; }
        public int Interval { get; set; }
        public bool EnableGpuCheck { get; set; }
        public bool EnableUserActivity { get; set; }
        public bool EnableNetworkCheck { get; set; }
        public int NetworkThresholdKBps { get; set; }
        public bool EnableDiskCheck { get; set; }
        public int DiskThresholdKBps { get; set; }
        public bool EnableProcessCheck { get; set; }
        public List<string> ProtectedProcesses { get; set; }
        public bool EnableTimeWindow { get; set; }
        public int TimeWindowStart { get; set; }
        public int TimeWindowEnd { get; set; }
        public bool ClearLogOnNextRun { get; set; }
        public bool EnableLogRotation { get; set; }
        public int LogRetentionDays { get; set; }
        public string LastRotationTime { get; set; }
        public bool CustomLogicEnabled { get; set; }
        public object CustomLogicTree { get; set; }

        public ConfigManager()
        {
            PowerAction = "Hibernate";
            DurationMin = 15;
            CpuThreshold = 30;
            GpuThreshold = 30;
            Interval = 5;
            EnableGpuCheck = true;
            EnableUserActivity = true;
            EnableNetworkCheck = true;
            NetworkThresholdKBps = 1024;
            EnableDiskCheck = true;
            DiskThresholdKBps = 10240;
            EnableProcessCheck = false;
            ProtectedProcesses = new List<string>();
            EnableTimeWindow = false;
            TimeWindowStart = 2;
            TimeWindowEnd = 7;
            ClearLogOnNextRun = false;
            EnableLogRotation = true;
            LogRetentionDays = 30;
            LastRotationTime = null;
            CustomLogicEnabled = false;
            CustomLogicTree = null;
        }

        public void Load()
        {
            if (!File.Exists(ConfigFile))
            {
                Save(); // 生成默认配置
                return;
            }

            try
            {
                string json = File.ReadAllText(ConfigFile);
                var serializer = new JavaScriptSerializer();
                var dict = serializer.Deserialize<Dictionary<string, object>>(json);

                if (dict == null) return;

                // 逐个字段读取，缺失的用默认值
                PowerAction = GetString(dict, "PowerAction") ?? "Hibernate";
                DurationMin = GetInt(dict, "DurationMin", 15);
                CpuThreshold = GetInt(dict, "CpuThreshold", 30);
                GpuThreshold = GetInt(dict, "GpuThreshold", 30);
                Interval = GetInt(dict, "Interval", 5);
                EnableGpuCheck = GetBool(dict, "EnableGpuCheck", true);
                EnableUserActivity = GetBool(dict, "EnableUserActivity", true);
                EnableNetworkCheck = GetBool(dict, "EnableNetworkCheck", true);
                NetworkThresholdKBps = GetInt(dict, "NetworkThresholdKBps", 1024);
                EnableDiskCheck = GetBool(dict, "EnableDiskCheck", true);
                DiskThresholdKBps = GetInt(dict, "DiskThresholdKBps", 10240);
                EnableProcessCheck = GetBool(dict, "EnableProcessCheck", false);
                ProtectedProcesses = GetStringList(dict, "ProtectedProcesses");
                EnableTimeWindow = GetBool(dict, "EnableTimeWindow", false);
                TimeWindowStart = GetInt(dict, "TimeWindowStart", 2);
                TimeWindowEnd = GetInt(dict, "TimeWindowEnd", 7);
                ClearLogOnNextRun = GetBool(dict, "ClearLogOnNextRun", false);
                EnableLogRotation = GetBool(dict, "EnableLogRotation", true);
                LogRetentionDays = GetInt(dict, "LogRetentionDays", 30);
                LastRotationTime = GetString(dict, "LastRotationTime");
                CustomLogicEnabled = GetBool(dict, "CustomLogicEnabled", false);

                if (dict.ContainsKey("CustomLogicTree"))
                    CustomLogicTree = dict["CustomLogicTree"];
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format("Config load error: {0}", ex.Message));
            }
        }

        public void Save()
        {
            if (!Directory.Exists(ConfigDir))
                Directory.CreateDirectory(ConfigDir);

            var dict = new Dictionary<string, object>
            {
                { "PowerAction", PowerAction },
                { "DurationMin", DurationMin },
                { "CpuThreshold", CpuThreshold },
                { "GpuThreshold", GpuThreshold },
                { "Interval", Interval },
                { "EnableGpuCheck", EnableGpuCheck },
                { "EnableUserActivity", EnableUserActivity },
                { "EnableNetworkCheck", EnableNetworkCheck },
                { "NetworkThresholdKBps", NetworkThresholdKBps },
                { "EnableDiskCheck", EnableDiskCheck },
                { "DiskThresholdKBps", DiskThresholdKBps },
                { "EnableProcessCheck", EnableProcessCheck },
                { "ProtectedProcesses", ProtectedProcesses },
                { "EnableTimeWindow", EnableTimeWindow },
                { "TimeWindowStart", TimeWindowStart },
                { "TimeWindowEnd", TimeWindowEnd },
                { "ClearLogOnNextRun", ClearLogOnNextRun },
                { "EnableLogRotation", EnableLogRotation },
                { "LogRetentionDays", LogRetentionDays },
                { "LastRotationTime", LastRotationTime },
                { "CustomLogicEnabled", CustomLogicEnabled },
                { "CustomLogicTree", CustomLogicTree }
            };

            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(dict);
            File.WriteAllText(ConfigFile, json);
        }

        // ---- 辅助读取 ----
        private string GetString(Dictionary<string, object> dict, string key)
        {
            return dict.ContainsKey(key) ? dict[key] as string : null;
        }

        private int GetInt(Dictionary<string, object> dict, string key, int def)
        {
            if (!dict.ContainsKey(key)) return def;
            try { return Convert.ToInt32(dict[key]); }
            catch { return def; }
        }

        private bool GetBool(Dictionary<string, object> dict, string key, bool def)
        {
            if (!dict.ContainsKey(key)) return def;
            try { return Convert.ToBoolean(dict[key]); }
            catch { return def; }
        }

        private List<string> GetStringList(Dictionary<string, object> dict, string key)
        {
            if (!dict.ContainsKey(key)) return new List<string>();
            var raw = dict[key] as System.Collections.ArrayList;
            if (raw == null) return new List<string>();
            var result = new List<string>();
            foreach (var x in raw) result.Add(x == null ? "" : x.ToString());
            return result;
        }
    }
}
