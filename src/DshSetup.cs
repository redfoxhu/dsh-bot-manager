using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;

namespace DshBotManager
{
    internal sealed class DshSetup
    {
        private readonly AppConfig _config;
        private readonly Logger _log;

        internal DshSetup(AppConfig config, Logger log)
        {
            _config = config;
            _log = log;
        }

        internal string ProfileDir
        {
            get { return Path.Combine(_config.DshHome, "profiles", _config.DshProfile); }
        }

        internal string PackageJson
        {
            get { return Path.Combine(ProfileDir, "package.json"); }
        }

        internal string DshCmd
        {
            get { return Path.Combine(_config.DshDir, "resources", "runtime", "cli", "bin", "dsh.cmd"); }
        }

        internal string DshExe
        {
            get { return Path.Combine(_config.DshDir, "DeepSeek Harness.exe"); }
        }

        internal string BeaconPath
        {
            get { return Path.Combine(_config.DshHome, "astrbot-ingress.json"); }
        }

        internal bool IsPackageInstalled()
        {
            if (!File.Exists(PackageJson)) return false;
            try
            {
                Dictionary<string, object> root = JsonUtil.DeserializeObject(File.ReadAllText(PackageJson, Encoding.UTF8));
                Dictionary<string, object> deps = GetDict(root, "dependencies");
                if (deps == null || !deps.ContainsKey("dsh-astrbot-ingress")) return false;
                return Directory.Exists(Path.Combine(ProfileDir, "node_modules", "dsh-astrbot-ingress"));
            }
            catch
            {
                return false;
            }
        }

        internal bool IsBundled()
        {
            if (!File.Exists(PackageJson)) return false;
            try
            {
                Dictionary<string, object> root = JsonUtil.DeserializeObject(File.ReadAllText(PackageJson, Encoding.UTF8));
                Dictionary<string, object> dsh = GetDict(root, "dsh");
                if (dsh == null) return false;
                Dictionary<string, object> profile = GetDict(dsh, "profile");
                if (profile == null) return false;
                object[] bundles = GetArray(profile, "bundles");
                if (bundles == null) return false;
                foreach (object item in bundles)
                {
                    if (string.Equals(Convert.ToString(item), "dsh-astrbot-ingress", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch
            {
            }
            return false;
        }

        internal bool IsBeaconFresh()
        {
            if (!File.Exists(BeaconPath)) return false;
            try
            {
                return (DateTime.UtcNow - File.GetLastWriteTimeUtc(BeaconPath)).TotalSeconds <= 180;
            }
            catch
            {
                return false;
            }
        }

        internal bool IsRunning()
        {
            Process[] list = Process.GetProcessesByName("DeepSeek Harness");
            try { return list.Length > 0; }
            finally
            {
                foreach (Process p in list) p.Dispose();
            }
        }

        internal void StartDsh()
        {
            if (!File.Exists(DshExe))
                throw new InvalidOperationException("未找到 DSH：" + DshExe);
            if (IsRunning() || IsPortListening(13391))
            {
                _log.Log("DSH 已在运行，采用现有进程。");
                return;
            }

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = DshExe;
            psi.WorkingDirectory = _config.DshDir;
            psi.UseShellExecute = true;
            psi.WindowStyle = ProcessWindowStyle.Normal;
            Process.Start(psi);
            _log.Log("已启动 DSH，等待 13391 …");
            if (!WaitForPort(13391, 30))
                throw new InvalidOperationException("DSH 在 30 秒内未监听 13391，请查看 DSH 窗口/日志。");
            _log.Log("DSH 已就绪。");
        }

        internal void InstallOrRepair()
        {
            if (!File.Exists(DshCmd))
                throw new InvalidOperationException("未找到 dsh.cmd：" + DshCmd);
            if (!Directory.Exists(ProfileDir))
                throw new InvalidOperationException("未找到 DSH profile 目录：" + ProfileDir);
            if (!File.Exists(PackageJson))
                throw new InvalidOperationException("未找到 profile 的 package.json：" + PackageJson);

            BackupPackageJson();

            if (!IsPackageInstalled())
            {
                _log.Log("安装 npm 包 dsh-astrbot-ingress …");
                RunPluginAdd();
            }
            else
            {
                _log.Log("dsh-astrbot-ingress 已安装。");
            }

            if (!IsBundled())
            {
                AddBundle();
                _log.Log("已把 dsh-astrbot-ingress 加入 dsh.profile.bundles。");
            }
            else
            {
                _log.Log("dsh.profile.bundles 已包含 dsh-astrbot-ingress。");
            }

            bool ready = WaitBeacon(15);
            if (!ready)
                _log.Log("警告：15 秒内未检测到 DSH ingress 信标。若 DSH 正在运行，可能需要重启 DSH 才能加载桥。");
            else
                _log.Log("DSH ingress 信标已就绪：" + BeaconPath);
        }

        private void BackupPackageJson()
        {
            string backup = PackageJson + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            int n = 0;
            while (File.Exists(backup))
            {
                n++;
                backup = PackageJson + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + n;
            }
            File.Copy(PackageJson, backup, false);
            _log.Log("已备份 package.json：" + backup);
        }

        private void RunPluginAdd()
        {
            string args = "/c \"\"" + DshCmd + "\" plugin --profile " + _config.DshProfile +
                          " add dsh-astrbot-ingress\"";
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "cmd.exe";
            psi.Arguments = args;
            psi.WorkingDirectory = _config.DshDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.EnvironmentVariables["DSH_HOME"] = _config.DshHome;

            StringBuilder output = new StringBuilder();
            object gate = new object();
            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (gate) { output.AppendLine(e.Data); }
                };
                p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (gate) { output.AppendLine(e.Data); }
                };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                bool exited = p.WaitForExit(180000);
                if (!exited)
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException("dsh plugin add 超过 180 秒。");
                }
                p.WaitForExit();
                string text;
                lock (gate) { text = output.ToString(); }
                _log.Log("dsh plugin add 输出：" + Environment.NewLine + text);
                if (p.ExitCode != 0)
                    throw new InvalidOperationException("dsh plugin add 失败，退出码 " + p.ExitCode + "。");
            }
        }

        private void AddBundle()
        {
            Dictionary<string, object> root = JsonUtil.DeserializeObject(File.ReadAllText(PackageJson, Encoding.UTF8));
            if (root == null) throw new InvalidOperationException("package.json 解析为空。");

            Dictionary<string, object> dsh = GetDict(root, "dsh");
            if (dsh == null)
            {
                dsh = new Dictionary<string, object>();
                root["dsh"] = dsh;
            }
            Dictionary<string, object> profile = GetDict(dsh, "profile");
            if (profile == null)
            {
                profile = new Dictionary<string, object>();
                dsh["profile"] = profile;
            }

            object[] bundles = GetArray(profile, "bundles");
            List<object> list = new List<object>();
            if (bundles != null)
            {
                foreach (object item in bundles) list.Add(item);
            }
            bool found = false;
            foreach (object item in list)
            {
                if (string.Equals(Convert.ToString(item), "dsh-astrbot-ingress", StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
            if (!found) list.Add("dsh-astrbot-ingress");
            profile["bundles"] = list.ToArray();

            string json = JsonUtil.Serialize(root);
            string temp = PackageJson + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(PackageJson))
            {
                try { File.Replace(temp, PackageJson, null); }
                catch
                {
                    File.Copy(temp, PackageJson, true);
                    File.Delete(temp);
                }
            }
            else
            {
                File.Move(temp, PackageJson);
            }
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key) || obj[key] == null) return null;
            return obj[key] as Dictionary<string, object>;
        }

        private static object[] GetArray(Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key) || obj[key] == null) return null;
            return obj[key] as object[];
        }

        private bool WaitBeacon(int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                if (IsBeaconFresh() && IsPortListening(_config.IngressPort)) return true;
                Thread.Sleep(500);
            }
            return IsBeaconFresh() && IsPortListening(_config.IngressPort);
        }

        private static bool IsPortListening(int port)
        {
            try
            {
                IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
                IPEndPoint[] endpoints = properties.GetActiveTcpListeners();
                foreach (IPEndPoint ep in endpoints)
                {
                    if (ep.Port == port) return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool WaitForPort(int port, int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                if (IsPortListening(port)) return true;
                Thread.Sleep(500);
            }
            return IsPortListening(port);
        }
    }
}
