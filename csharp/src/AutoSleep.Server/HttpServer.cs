using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace AutoSleep.Server
{
    public class HttpServer
    {
        private HttpListener _listener;
        private const string ConfigPath = @"C:\ProgramData\AutoSleep\settings.json";
        private const string EditorPath = @"C:\ProgramData\AutoSleep\editor.html";
        private const string LogPath = @"C:\ProgramData\AutoSleep\http_server.log";

        static void Main()
        {
            new HttpServer().Start();
        }

        public void Start()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://localhost:56790/");
            _listener.Start();

            WriteLog("Server started on http://localhost:56790/");

            while (_listener.IsListening)
            {
                try
                {
                    var context = _listener.GetContext();
                    ProcessRequest(context);
                }
                catch { break; }
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            string path = context.Request.Url.AbsolutePath;
            WriteLog("Request: " + path);

            if (path == "/favicon.ico")
            {
                Respond(context, 204, "");
                return;
            }

            if (path == "/shutdown")
            {
                Respond(context, 200, "{\"status\":\"shutting down\"}");
                _listener.Stop();
                return;
            }

            if (path == "/config.js")
            {
                ServeConfigJs(context);
                return;
            }

            if (path == "/" || path == "/editor.html")
            {
                ServeEditor(context);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/save")
            {
                HandleSave(context);
                return;
            }

            Respond(context, 404, "{\"status\":\"error\",\"message\":\"Not found\"}");
        }

        private void ServeConfigJs(HttpListenerContext context)
        {
            WriteLog("  -> Generating config.js");
            try
            {
                string configJson = File.ReadAllText(ConfigPath, Encoding.UTF8);
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                var config = serializer.Deserialize<Dictionary<string, object>>(configJson);

                // 提取各个字段，与原版 PowerShell 逻辑一致
                bool enableGpuCheck = GetBool(config, "EnableGpuCheck", true);
                bool enableNetworkCheck = GetBool(config, "EnableNetworkCheck", true);
                bool enableDiskCheck = GetBool(config, "EnableDiskCheck", true);
                bool enableUserActivity = GetBool(config, "EnableUserActivity", true);
                bool enableProcessCheck = GetBool(config, "EnableProcessCheck", false);
                bool enableTimeWindow = GetBool(config, "EnableTimeWindow", false);
                bool customLogicEnabled = GetBool(config, "CustomLogicEnabled", false);
                int cpuThreshold = GetInt(config, "CpuThreshold", 30);
                int gpuThreshold = GetInt(config, "GpuThreshold", 30);
                int diskThresholdKBps = GetInt(config, "DiskThresholdKBps", 10240);
                int networkThresholdKBps = GetInt(config, "NetworkThresholdKBps", 1024);
                int timeWindowStart = GetInt(config, "TimeWindowStart", 2);
                int timeWindowEnd = GetInt(config, "TimeWindowEnd", 7);

                // 序列化 CustomLogicTree（原版用 ConvertTo-Json -Compress -Depth 10）
                string treeJson = "null";
                if (config.ContainsKey("CustomLogicTree") && config["CustomLogicTree"] != null)
                {
                    treeJson = serializer.Serialize(config["CustomLogicTree"]);
                }

                string jsContent = "window.__AUTOSLEEP_CONFIG = {\n"
                    + "    EnableGpuCheck: " + BoolStr(enableGpuCheck) + ",\n"
                    + "    EnableNetworkCheck: " + BoolStr(enableNetworkCheck) + ",\n"
                    + "    EnableDiskCheck: " + BoolStr(enableDiskCheck) + ",\n"
                    + "    EnableUserActivity: " + BoolStr(enableUserActivity) + ",\n"
                    + "    EnableProcessCheck: " + BoolStr(enableProcessCheck) + ",\n"
                    + "    EnableTimeWindow: " + BoolStr(enableTimeWindow) + ",\n"
                    + "    CustomLogicEnabled: " + BoolStr(customLogicEnabled) + ",\n"
                    + "    CustomLogicTree: " + treeJson + ",\n"
                    + "    CpuThreshold: " + cpuThreshold + ",\n"
                    + "    GpuThreshold: " + gpuThreshold + ",\n"
                    + "    DiskThresholdKBps: " + diskThresholdKBps + ",\n"
                    + "    NetworkThresholdKBps: " + networkThresholdKBps + ",\n"
                    + "    TimeWindowStart: " + timeWindowStart + ",\n"
                    + "    TimeWindowEnd: " + timeWindowEnd + "\n"
                    + "};\n";

                Respond(context, 200, jsContent, "application/javascript; charset=utf-8");
                WriteLog("  -> config.js served");
            }
            catch (Exception ex)
            {
                WriteLog("  -> ERROR: " + ex.Message);
                string fallback = "window.__AUTOSLEEP_CONFIG = { EnableGpuCheck: true, EnableNetworkCheck: true, EnableDiskCheck: true, EnableUserActivity: true, EnableProcessCheck: true, EnableTimeWindow: true, CustomLogicEnabled: false, CustomLogicTree: null, CpuThreshold: 30, GpuThreshold: 30, DiskThresholdKBps: 10240, NetworkThresholdKBps: 1024, TimeWindowStart: 2, TimeWindowEnd: 7 };";
                Respond(context, 200, fallback, "application/javascript; charset=utf-8");
                WriteLog("  -> Fallback config.js served");
            }
        }

        private string BoolStr(bool val) { return val ? "true" : "false"; }

        private bool GetBool(Dictionary<string, object> dict, string key, bool def)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return def;
            try { return Convert.ToBoolean(dict[key]); }
            catch { return def; }
        }

        private int GetInt(Dictionary<string, object> dict, string key, int def)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return def;
            try { return Convert.ToInt32(dict[key]); }
            catch { return def; }
        }

        private void ServeEditor(HttpListenerContext context)
        {
            WriteLog("  -> Serving editor.html");
            if (File.Exists(EditorPath))
            {
                try
                {
                    string html = File.ReadAllText(EditorPath, Encoding.UTF8);
                    // 确保 editor.html 里的 config.js 引用指向正确的路径
                    html = html.Replace("src=\"config.js\"", "src=\"/config.js\"");
                    Respond(context, 200, html, "text/html; charset=utf-8");
                    WriteLog("  -> editor.html served");
                }
                catch (Exception ex)
                {
                    WriteLog("  -> Error reading editor.html: " + ex.Message);
                    Respond(context, 500, "{\"status\":\"error\",\"message\":\"read error\"}");
                }
            }
            else
            {
                Respond(context, 404, "{\"status\":\"error\",\"message\":\"editor.html not found\"}");
                WriteLog("  -> editor.html not found");
            }
        }

        private void HandleSave(HttpListenerContext context)
        {
            WriteLog("  -> POST /save received");
            try
            {
                string json;
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    json = reader.ReadToEnd();
                }
                WriteLog("  -> JSON length: " + json.Length);

                // 解析 POST 过来的 JSON（Blockly 生成的逻辑树）
                var parsed = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(json);
                WriteLog("  -> JSON parsed successfully");

                // 读取现有配置
                string configJson = File.ReadAllText(ConfigPath, Encoding.UTF8);
                var config = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(configJson);
                WriteLog("  -> Config loaded");

                // 更新 CustomLogicTree 和 CustomLogicEnabled
                config["CustomLogicTree"] = parsed;
                config["CustomLogicEnabled"] = true;
                WriteLog("  -> Assigned CustomLogicTree");

                // 写回文件
                string outputJson = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .Serialize(config);
                File.WriteAllText(ConfigPath, outputJson, Encoding.UTF8);
                WriteLog("  -> Config written");

                // 验证
                string verifyJson = File.ReadAllText(ConfigPath, Encoding.UTF8);
                var verify = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(verifyJson);
                if (!verify.ContainsKey("CustomLogicTree") || verify["CustomLogicTree"] == null)
                {
                    throw new Exception("CustomLogicTree is null after write");
                }
                WriteLog("  -> Verification passed");

                Respond(context, 200, "{\"status\":\"success\"}");
                WriteLog("  -> Success");
            }
            catch (Exception ex)
            {
                WriteLog("  -> ERROR: " + ex.Message);
                var errorObj = new Dictionary<string, object>();
                errorObj["status"] = "error";
                errorObj["message"] = ex.Message;
                string errorJson = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(errorObj);
                Respond(context, 500, errorJson);
            }
        }

        private void Respond(HttpListenerContext context, int statusCode, string body, string contentType = "application/json")
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = contentType;
            byte[] buffer = Encoding.UTF8.GetBytes(body);
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.Close();
        }

        private void WriteLog(string msg)
        {
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg;
            Console.WriteLine(line);
            try
            {
                string dir = Path.GetDirectoryName(LogPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch { }
        }
    }
}
