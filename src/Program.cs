using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DshBotManager
{
    internal static class Program
    {
        private const string MutexName = "DshBotManager.SingleInstance";
        private const string WindowTitle = "DSH 机器人管家";

        internal static string InstalledExePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "DshBotManager", "DshBotManager.exe");
            }
        }

        internal static string PreferredExePath
        {
            get
            {
                try
                {
                    string installed = InstalledExePath;
                    if (File.Exists(installed)) return installed;
                }
                catch
                {
                }
                return Application.ExecutablePath;
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            NativeMethods.SetProcessDPIAware();
            Trace("DshBotManager args=" + string.Join(",", args));

            if (RedirectToInstalledCopy(args)) return;

            if (HasArg(args, "--install-control-plugin"))
            {
                RunInstallControlPlugin();
                return;
            }
            if (HasArg(args, "--install-dsh-bridge"))
            {
                RunInstallDshBridge();
                return;
            }
            if (HasArg(args, "--install-all"))
            {
                int a = RunInstallControlPlugin();
                int b = RunInstallDshBridge();
                Environment.ExitCode = (a == 0 && b == 0) ? 0 : 1;
                return;
            }

            bool created;
            using (Mutex mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    IntPtr existing = NativeMethods.FindWindow(null, WindowTitle);
                    if (existing != IntPtr.Zero)
                    {
                        NativeMethods.ShowWindow(existing, 9);
                        NativeMethods.SetForegroundWindow(existing);
                    }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                AppConfig config;
                Logger logger;
                try
                {
                    config = AppConfig.Load();
                    logger = new Logger(Path.Combine(AppConfig.LogDir, "manager.log"));
                }
                catch (Exception ex)
                {
                    TryLogCrash("startup", ex);
                    MessageBox.Show("管理器启动失败：" + ex.Message, "DSH 机器人管家",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                bool autostart = HasArg(args, "--autostart");
                if (config.AutoStart)
                {
                    try { AutoStart.Apply(true); }
                    catch (Exception ex) { logger.Log("修复开机自启动项失败", ex); }
                }
                logger.Log("管理器启动。autostart=" + autostart + "，exe=" + Application.ExecutablePath);
                try
                {
                    Application.Run(new MainForm(config, logger, autostart));
                }
                catch (Exception ex)
                {
                    TryLogCrash("run", ex);
                    MessageBox.Show("管理器运行失败：" + ex.Message, "DSH 机器人管家",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                GC.KeepAlive(mutex);
            }
        }

        private static bool RedirectToInstalledCopy(string[] args)
        {
            if (HasArg(args, "--install-control-plugin") ||
                HasArg(args, "--install-dsh-bridge") ||
                HasArg(args, "--install-all"))
            {
                return false;
            }

            string installed = InstalledExePath;
            bool runningInstalled;
            try
            {
                runningInstalled = string.Equals(
                    Path.GetFullPath(Application.ExecutablePath),
                    Path.GetFullPath(installed),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                runningInstalled = false;
            }
            if (runningInstalled) return false;

            bool autostart = HasArg(args, "--autostart");
            if (!File.Exists(installed))
            {
                if (autostart) return false;
                MessageBox.Show(
                    "当前运行的是工作区里的开发副本，而且没有找到安装版。\r\n\r\n" +
                    "请从 Windows 资源管理器打开：\r\n" +
                    "E:\\dsh\\app\\bot自启动\\install.cmd\r\n\r\n" +
                    "先安装，再运行桌面上的“DSH机器人管家”。",
                    WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return true;
            }

            if (autostart)
            {
                try { Process.Start(installed, "--autostart"); }
                catch { }
                return true;
            }

            DialogResult result = MessageBox.Show(
                "你正在运行工作区里的开发副本：\r\n" + Application.ExecutablePath + "\r\n\r\n" +
                "这个位置会被 DSH 标记为低完整性，程序无法写入 AstrBot 配置。\r\n\r\n" +
                "请运行安装版：\r\n" + installed + "\r\n\r\n" +
                "是否现在启动安装版？",
                WindowTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result == DialogResult.Yes)
            {
                try
                {
                    Process.Start(installed);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("启动安装版失败：" + ex.Message, WindowTitle,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            return true;
        }

        private static int RunInstallControlPlugin()
        {
            try
            {
                AppConfig config = AppConfig.Load();
                Logger logger = new Logger(Path.Combine(AppConfig.LogDir, "manager.log"));
                AstrBotSetup setup = new AstrBotSetup(config, logger);
                Trace("instance=" + config.AstrBotInstance);
                setup.EnsureInstance();
                Trace("pluginDir=" + setup.PluginDir);
                setup.InstallControlPlugin();
                setup.WriteRuntime(setup.ReadBridgeEnabled());
                Trace("installed=" + setup.PluginDir);
                logger.Log("控制插件安装完成：" + setup.PluginDir);
                return 0;
            }
            catch (Exception ex)
            {
                Trace("install error: " + ex);
                TryLogCrash("install-control-plugin", ex);
                return 1;
            }
        }

        private static int RunInstallDshBridge()
        {
            try
            {
                AppConfig config = AppConfig.Load();
                Logger logger = new Logger(Path.Combine(AppConfig.LogDir, "manager.log"));
                DshSetup setup = new DshSetup(config, logger);
                setup.InstallOrRepair();
                logger.Log("DSH 桥安装/修复流程完成。");
                return 0;
            }
            catch (Exception ex)
            {
                TryLogCrash("install-dsh-bridge", ex);
                return 1;
            }
        }

        private static void TryLogCrash(string area, Exception ex)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "DshBotManager-crash.log");
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + area + "] " +
                    ex + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static void Trace(string message)
        {
            try
            {
                Console.WriteLine(message);
            }
            catch
            {
            }
        }

        private static bool HasArg(string[] args, string name)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool SetProcessDPIAware();

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);
    }

    internal static class JsonUtil
    {
        internal static string Serialize(object value)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 100;
            return serializer.Serialize(value);
        }

        internal static T Deserialize<T>(string json) where T : class
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 100;
            return serializer.Deserialize<T>(json);
        }

        internal static Dictionary<string, object> DeserializeObject(string json)
        {
            return Deserialize<Dictionary<string, object>>(json);
        }

        internal static string GetString(Dictionary<string, object> obj, string key, string fallback)
        {
            if (obj == null || !obj.ContainsKey(key) || obj[key] == null) return fallback;
            return Convert.ToString(obj[key]);
        }

        internal static bool GetBool(Dictionary<string, object> obj, string key, bool fallback)
        {
            if (obj == null || !obj.ContainsKey(key) || obj[key] == null) return fallback;
            try { return Convert.ToBoolean(obj[key]); }
            catch { return fallback; }
        }

        internal static long GetLong(Dictionary<string, object> obj, string key, long fallback)
        {
            if (obj == null || !obj.ContainsKey(key) || obj[key] == null) return fallback;
            try { return Convert.ToInt64(obj[key]); }
            catch { return fallback; }
        }
    }

    internal sealed class Logger
    {
        private readonly object _lock = new object();
        private readonly string _path;

        internal event Action<string> Line;

        internal Logger(string path)
        {
            _path = path;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        internal void Log(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + (message ?? string.Empty);
            lock (_lock)
            {
                try
                {
                    RotateIfNeeded();
                    File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch
                {
                }
            }
            Action<string> handler = Line;
            if (handler != null)
            {
                try { handler(line); }
                catch { }
            }
        }

        internal void Log(string message, Exception ex)
        {
            if (ex == null)
            {
                Log(message);
                return;
            }
            Log(message + "：" + ex.Message);
            Log(ex.ToString());
        }

        internal string ReadTail(int maxChars)
        {
            try
            {
                if (!File.Exists(_path)) return string.Empty;
                string text = File.ReadAllText(_path, Encoding.UTF8);
                if (text.Length <= maxChars) return text;
                return text.Substring(text.Length - maxChars);
            }
            catch (Exception ex)
            {
                return "读取日志失败：" + ex.Message;
            }
        }

        private void RotateIfNeeded()
        {
            if (!File.Exists(_path)) return;
            FileInfo info = new FileInfo(_path);
            if (info.Length < 2 * 1024 * 1024) return;
            string old2 = _path + ".2";
            string old1 = _path + ".1";
            try
            {
                if (File.Exists(old2)) File.Delete(old2);
                if (File.Exists(old1)) File.Move(old1, old2);
                File.Move(_path, old1);
            }
            catch
            {
            }
        }
    }

    public sealed class AppConfig
    {
        public string AstrBotInstance { get; set; }
        public string SnowLumaDir { get; set; }
        public string DshDir { get; set; }
        public string DshHome { get; set; }
        public string DshProfile { get; set; }
        public string ConnectCmd { get; set; }
        public string DisconnectCmd { get; set; }
        public string StatusCmd { get; set; }
        public List<string> AdminIds { get; set; }
        public string CloseAction { get; set; }
        public bool AutoStart { get; set; }
        public bool StartServicesOnLaunch { get; set; }
        public bool StartMinimized { get; set; }
        public int AstrBotPort { get; set; }
        public int SnowLumaPort { get; set; }
        public int IngressPort { get; set; }

        internal static string ConfigDir
        {
            get
            {
                string home = Environment.GetEnvironmentVariable("DSHBOTMANAGER_HOME");
                if (!string.IsNullOrWhiteSpace(home)) return home;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshBotManager");
            }
        }

        internal static string LogDir
        {
            get { return Path.Combine(ConfigDir, "logs"); }
        }

        internal static string ConfigPath
        {
            get { return Path.Combine(ConfigDir, "config.json"); }
        }

        public AppConfig()
        {
            AstrBotInstance = string.Empty;
            SnowLumaDir = "E:\\SnowLuma";
            DshDir = "E:\\dsh";
            DshHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            DshProfile = "desktop";
            ConnectCmd = "连接dsh";
            DisconnectCmd = "断开dsh";
            StatusCmd = "dsh状态";
            AdminIds = new List<string>();
            AdminIds.Add("1031998556");
            CloseAction = "hide";
            AutoStart = false;
            StartServicesOnLaunch = true;
            StartMinimized = false;
            AstrBotPort = 6199;
            SnowLumaPort = 5099;
            IngressPort = 3188;
        }

        internal static AppConfig Load()
        {
            Directory.CreateDirectory(ConfigDir);
            if (!File.Exists(ConfigPath)) return new AppConfig();
            try
            {
                AppConfig config = JsonUtil.Deserialize<AppConfig>(File.ReadAllText(ConfigPath, Encoding.UTF8));
                if (config == null) config = new AppConfig();
                config.Normalize();
                return config;
            }
            catch (Exception)
            {
                try
                {
                    File.Copy(ConfigPath, ConfigPath + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                }
                catch
                {
                }
                AppConfig config = new AppConfig();
                config.Normalize();
                return config;
            }
        }

        internal void Normalize()
        {
            if (SnowLumaDir == null) SnowLumaDir = "E:\\SnowLuma";
            if (DshDir == null) DshDir = "E:\\dsh";
            if (string.IsNullOrWhiteSpace(DshHome))
                DshHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            if (string.IsNullOrWhiteSpace(DshProfile)) DshProfile = "desktop";
            if (ConnectCmd == null) ConnectCmd = "连接dsh";
            if (DisconnectCmd == null) DisconnectCmd = "断开dsh";
            if (StatusCmd == null) StatusCmd = "dsh状态";
            ConnectCmd = ConnectCmd.Trim();
            DisconnectCmd = DisconnectCmd.Trim();
            StatusCmd = StatusCmd.Trim();
            if (AdminIds == null) AdminIds = new List<string>();
            List<string> admins = new List<string>();
            foreach (string id in AdminIds)
            {
                if (!string.IsNullOrWhiteSpace(id)) admins.Add(id.Trim());
            }
            if (admins.Count == 0) admins.Add("1031998556");
            AdminIds = admins;
            if (CloseAction == null) CloseAction = "hide";
            CloseAction = CloseAction.Trim().ToLowerInvariant();
            if (CloseAction != "exit") CloseAction = "hide";
            if (AstrBotPort <= 0 || AstrBotPort > 65535) AstrBotPort = 6199;
            if (SnowLumaPort <= 0 || SnowLumaPort > 65535) SnowLumaPort = 5099;
            if (IngressPort <= 0 || IngressPort > 65535) IngressPort = 3188;
        }

        internal void Save()
        {
            Directory.CreateDirectory(ConfigDir);
            string temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, JsonUtil.Serialize(this), new UTF8Encoding(false));
            if (File.Exists(ConfigPath))
            {
                string backup = ConfigPath + ".bak";
                try
                {
                    File.Replace(temp, ConfigPath, backup);
                }
                catch
                {
                    File.Copy(temp, ConfigPath, true);
                    File.Delete(temp);
                }
            }
            else
            {
                File.Move(temp, ConfigPath);
            }
        }
    }

    internal static class AutoStart
    {
        private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string ValueName = "DshBotManager";

        internal static void Apply(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key == null) throw new InvalidOperationException("无法打开自启动注册表项。");
                if (enabled)
                {
                    string value = "\"" + Program.PreferredExePath + "\" --autostart";
                    key.SetValue(ValueName, value, RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }
    }
}
