using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace DshBotManager
{
    public sealed class ControlStatus
    {
        public string plugin { get; set; }
        public bool bridge_loaded { get; set; }
        public bool bridge_enabled { get; set; }
        public bool ingress_beacon { get; set; }
        public string ingress_url { get; set; }
        public long updated_at { get; set; }
        public string last_error { get; set; }
    }

    internal sealed class IngressInfo
    {
        public string url { get; set; }
        public string kind { get; set; }
        public long updatedAt { get; set; }
        public DateTime fileTimeUtc { get; set; }
    }

    internal sealed class AstrBotSetup
    {
        private const string ControlResourceMain = "DshBotManager.Payload.main.py";
        private const string ControlResourceMeta = "DshBotManager.Payload.metadata.yaml";
        private const string ControlResourceSchema = "DshBotManager.Payload._conf_schema.json";

        private readonly AppConfig _config;
        private readonly Logger _log;

        internal AstrBotSetup(AppConfig config, Logger log)
        {
            _config = config;
            _log = log;
        }

        internal string InstanceDir
        {
            get { return _config.AstrBotInstance; }
        }

        internal string CoreDir
        {
            get { return Path.Combine(InstanceDir, "core"); }
        }

        internal string DataDir
        {
            get { return Path.Combine(CoreDir, "data"); }
        }

        internal string ConfigDir
        {
            get { return Path.Combine(DataDir, "config"); }
        }

        internal string PluginDir
        {
            get { return Path.Combine(DataDir, "plugins", "astrbot_plugin_dsh_control"); }
        }

        internal string BridgePluginDir
        {
            get { return Path.Combine(DataDir, "plugins", "astrbot_plugin_dsh"); }
        }

        internal string BridgeConfigPath
        {
            get { return Path.Combine(ConfigDir, "astrbot_plugin_dsh_config.json"); }
        }

        internal string RuntimePath
        {
            get { return Path.Combine(ConfigDir, "dsh_control_runtime.json"); }
        }

        internal string StatusPath
        {
            get { return Path.Combine(ConfigDir, "dsh_control_status.json"); }
        }

        internal string IngressBeaconPath
        {
            get { return Path.Combine(_config.DshHome, "astrbot-ingress.json"); }
        }

        internal string DashboardUrl
        {
            get { return "http://127.0.0.1:6185"; }
        }

        internal void EnsureInstance()
        {
            string envInstance = Environment.GetEnvironmentVariable("DSHBOTMANAGER_ASTRBOT_INSTANCE");
            if (!string.IsNullOrWhiteSpace(envInstance))
            {
                string envMain = Path.Combine(envInstance, "core", "main.py");
                if (File.Exists(envMain))
                {
                    _config.AstrBotInstance = envInstance;
                    return;
                }
            }
            if (!string.IsNullOrWhiteSpace(_config.AstrBotInstance))
            {
                string main = Path.Combine(_config.AstrBotInstance, "core", "main.py");
                if (File.Exists(main)) return;
            }
            string found = DiscoverInstance();
            if (string.IsNullOrEmpty(found))
                throw new InvalidOperationException("未找到 AstrBot 实例。请在设置中指定实例目录。");
            _config.AstrBotInstance = found;
            _config.Save();
            _log.Log("已选择 AstrBot 实例：" + found);
        }

        internal string DiscoverInstance()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".astrbot_launcher", "instances");
            if (!Directory.Exists(root)) return null;

            string best = null;
            DateTime bestTime = DateTime.MinValue;
            int bestScore = -1;
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch { return null; }

            foreach (string dir in dirs)
            {
                string main = Path.Combine(dir, "core", "main.py");
                if (!File.Exists(main)) continue;
                int score = 0;
                string bridge = Path.Combine(dir, "core", "data", "config", "astrbot_plugin_dsh_config.json");
                if (File.Exists(bridge)) score += 100;
                string cmd = Path.Combine(dir, "core", "data", "cmd_config.json");
                DateTime time = DateTime.MinValue;
                if (File.Exists(cmd))
                {
                    try { time = File.GetLastWriteTimeUtc(cmd); }
                    catch { }
                }
                if (score > bestScore || (score == bestScore && time > bestTime))
                {
                    best = dir;
                    bestScore = score;
                    bestTime = time;
                }
            }
            return best;
        }

        internal string PythonPath
        {
            get
            {
                string p = Path.Combine(InstanceDir, "venv", "Scripts", "python.exe");
                if (File.Exists(p)) return p;
                p = Path.Combine(InstanceDir, "core", "venv", "Scripts", "python.exe");
                if (File.Exists(p)) return p;
                return null;
            }
        }

        internal bool IsBridgeInstalled()
        {
            return File.Exists(BridgeConfigPath) && Directory.Exists(BridgePluginDir);
        }

        internal bool ReadBridgeEnabled()
        {
            if (!File.Exists(BridgeConfigPath)) return true;
            try
            {
                Dictionary<string, object> obj = JsonUtil.DeserializeObject(File.ReadAllText(BridgeConfigPath, Encoding.UTF8));
                return JsonUtil.GetBool(obj, "enabled", true);
            }
            catch (Exception ex)
            {
                _log.Log("读取 DSH 桥配置失败，按已连接处理", ex);
                return true;
            }
        }

        internal bool InstallControlPlugin()
        {
            if (string.IsNullOrWhiteSpace(InstanceDir))
                throw new InvalidOperationException("AstrBot 实例目录为空。");

            EnsureWritableConfig();
            Directory.CreateDirectory(PluginDir);
            bool changed = false;
            changed |= WriteResource(ControlResourceMain, Path.Combine(PluginDir, "main.py"));
            changed |= WriteResource(ControlResourceMeta, Path.Combine(PluginDir, "metadata.yaml"));
            changed |= WriteResource(ControlResourceSchema, Path.Combine(PluginDir, "_conf_schema.json"));

            if (changed)
                _log.Log("控制插件文件已更新：" + PluginDir);
            else
                _log.Log("控制插件文件已是最新：" + PluginDir);
            return changed;
        }

        private bool WriteResource(string resourceName, string target)
        {
            Assembly asm = typeof(AstrBotSetup).Assembly;
            using (Stream stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("缺少嵌入资源：" + resourceName);
                byte[] data = new byte[stream.Length];
                int offset = 0;
                while (offset < data.Length)
                {
                    int read = stream.Read(data, offset, data.Length - offset);
                    if (read <= 0) break;
                    offset += read;
                }
                if (offset != data.Length)
                    throw new IOException("读取嵌入资源不完整：" + resourceName);

                if (File.Exists(target))
                {
                    byte[] old = File.ReadAllBytes(target);
                    if (BytesEqual(old, data)) return false;
                }
                File.WriteAllBytes(target, data);
                return true;
            }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        internal void EnsureWritableConfig()
        {
            Directory.CreateDirectory(ConfigDir);
            string probe = Path.Combine(ConfigDir, ".dshbotmanager-write-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (FileStream fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.WriteByte(0);
                }
                File.Delete(probe);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException(
                    "无法写入 AstrBot 配置目录：\r\n" + ConfigDir + "\r\n\r\n" +
                    "当前程序很可能是在 DSH 沙箱内启动的（低完整性进程），" +
                    "即使 NTFS 权限允许，也会被拒绝写入工作区外路径。\r\n" +
                    "请从 Windows 资源管理器/桌面直接双击安装版的 DshBotManager.exe，" +
                    "不要从 DSH 的文件卡片、终端或聊天附件里打开，也不要运行工作区 dist 里的副本。\r\n\r\n" +
                    "如果这是开机自启动弹出的，请从资源管理器运行 install.cmd 修复自启动路径。", ex);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    "无法写入 AstrBot 配置目录：\r\n" + ConfigDir + "\r\n\r\n" + ex.Message, ex);
            }
            finally
            {
                try { if (File.Exists(probe)) File.Delete(probe); }
                catch { }
            }
        }

        internal void WriteRuntime(bool? enabledOverride)
        {
            EnsureWritableConfig();
            Directory.CreateDirectory(ConfigDir);
            Dictionary<string, object> obj = new Dictionary<string, object>();
            if (File.Exists(RuntimePath))
            {
                try
                {
                    Dictionary<string, object> existing = JsonUtil.DeserializeObject(File.ReadAllText(RuntimePath, Encoding.UTF8));
                    if (existing != null) obj = existing;
                }
                catch (Exception ex)
                {
                    _log.Log("读取 runtime 失败，将重新生成", ex);
                }
            }

            bool enabled = enabledOverride.HasValue
                ? enabledOverride.Value
                : JsonUtil.GetBool(obj, "enabled", ReadBridgeEnabled());

            long revision = JsonUtil.GetLong(obj, "revision", 0) + 1;
            obj["connect_cmd"] = _config.ConnectCmd;
            obj["disconnect_cmd"] = _config.DisconnectCmd;
            obj["status_cmd"] = _config.StatusCmd;
            obj["enabled"] = enabled;
            obj["admin_ids"] = _config.AdminIds.ToArray();
            obj["revision"] = revision;
            obj["updated_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            AtomicWrite(RuntimePath, JsonUtil.Serialize(obj));
            _log.Log("已写入控制 runtime：enabled=" + enabled + "，revision=" + revision);
        }

        internal ControlStatus ReadStatus()
        {
            if (!File.Exists(StatusPath)) return null;
            try
            {
                return JsonUtil.Deserialize<ControlStatus>(File.ReadAllText(StatusPath, Encoding.UTF8));
            }
            catch
            {
                return null;
            }
        }

        internal bool IsControlPluginLoaded()
        {
            ControlStatus status = ReadStatus();
            if (status == null) return false;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return status.plugin == "running" && status.updated_at > 0 && now - status.updated_at <= 6;
        }

        internal IngressInfo ReadIngressBeacon()
        {
            if (!File.Exists(IngressBeaconPath)) return null;
            try
            {
                DateTime fileTime = File.GetLastWriteTimeUtc(IngressBeaconPath);
                if ((DateTime.UtcNow - fileTime).TotalSeconds > 180) return null;
                Dictionary<string, object> obj = JsonUtil.DeserializeObject(File.ReadAllText(IngressBeaconPath, Encoding.UTF8));
                IngressInfo info = new IngressInfo();
                info.url = JsonUtil.GetString(obj, "url", string.Empty);
                info.kind = JsonUtil.GetString(obj, "kind", string.Empty);
                info.updatedAt = JsonUtil.GetLong(obj, "updatedAt", 0);
                info.fileTimeUtc = fileTime;
                if (string.IsNullOrWhiteSpace(info.url)) return null;
                return info;
            }
            catch
            {
                return null;
            }
        }

        internal void AtomicWrite(string path, string content)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try { File.Replace(temp, path, null); }
                catch
                {
                    File.Copy(temp, path, true);
                    File.Delete(temp);
                }
            }
            else
            {
                File.Move(temp, path);
            }
        }
    }
}
