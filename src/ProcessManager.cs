using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DshBotManager
{
    internal sealed class ProcessManager
    {
        private readonly AppConfig _config;
        private readonly Logger _log;
        private readonly AstrBotSetup _astrbot;
        private readonly DshSetup _dsh;

        internal ProcessManager(AppConfig config, Logger log, AstrBotSetup astrbot, DshSetup dsh)
        {
            _config = config;
            _log = log;
            _astrbot = astrbot;
            _dsh = dsh;
        }

        internal bool IsAstrBotRunning()
        {
            return IsPortListening(_config.AstrBotPort) || IsPortListening(6185);
        }

        internal bool IsSnowLumaRunning()
        {
            return IsPortListening(_config.SnowLumaPort) || IsPortListening(3000) || IsPortListening(3001);
        }

        internal bool IsDshRunning()
        {
            return IsPortListening(13391) || _dsh.IsRunning();
        }

        internal bool IsPortListening(int port)
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

        internal void StartAll()
        {
            _astrbot.EnsureInstance();
            bool controlChanged = _astrbot.InstallControlPlugin();
            _astrbot.WriteRuntime(null);

            try
            {
                _dsh.StartDsh();
            }
            catch (Exception ex)
            {
                _log.Log("启动 DSH 失败，继续启动 AstrBot/SnowLuma", ex);
            }

            if (IsAstrBotRunning() && (controlChanged || !_astrbot.IsControlPluginLoaded()))
            {
                _log.Log("AstrBot 已在运行，但控制插件未加载或刚更新；重启 AstrBot 以加载控制插件。");
                RestartAstrBot();
            }
            else
            {
                StartAstrBot();
            }

            StartSnowLuma();
            _log.Log("一键启动完成。");
        }

        internal void StartAstrBot()
        {
            _astrbot.EnsureInstance();
            _astrbot.InstallControlPlugin();
            _astrbot.WriteRuntime(null);

            if (IsAstrBotRunning())
            {
                _log.Log("AstrBot 已在运行，采用现有进程。");
                return;
            }

            string python = _astrbot.PythonPath;
            if (string.IsNullOrEmpty(python))
                throw new InvalidOperationException("未找到 AstrBot 的 python.exe：" + _astrbot.InstanceDir);

            string main = Path.Combine(_astrbot.CoreDir, "main.py");
            if (!File.Exists(main))
                throw new InvalidOperationException("未找到 AstrBot main.py：" + main);

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = python;
            psi.Arguments = "main.py";
            psi.WorkingDirectory = _astrbot.CoreDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.EnvironmentVariables["ASTRBOT_ROOT"] = _astrbot.CoreDir;
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

            StartHidden(psi, "AstrBot");
            _log.Log("已隐藏启动 AstrBot，等待 " + _config.AstrBotPort + " …");
            if (!WaitForPort(_config.AstrBotPort, 90))
                throw new InvalidOperationException("AstrBot 在 90 秒内未监听 " + _config.AstrBotPort + "，请查看日志。");
            _log.Log("AstrBot 已就绪。");
        }

        internal void StartSnowLuma()
        {
            if (IsSnowLumaRunning())
            {
                // 旧实例可能是由旧版管理器启动的，没有 SNOWLUMA_HOOK_AUTOLOAD。
                // 一键启动时重启它，确保能注入当前已登录的 QQ 进程。
                _log.Log("检测到已有 SnowLuma，重启以启用 QQ 自动注入。");
                StopSnowLuma();
                WaitForPortClosed(_config.SnowLumaPort, 8);
            }

            string node = Path.Combine(_config.SnowLumaDir, "node.exe");
            string index = Path.Combine(_config.SnowLumaDir, "index.mjs");
            if (!File.Exists(node))
                throw new InvalidOperationException("未找到 SnowLuma node.exe：" + node);
            if (!File.Exists(index))
                throw new InvalidOperationException("未找到 SnowLuma index.mjs：" + index);

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = node;
            psi.Arguments = "index.mjs";
            psi.WorkingDirectory = _config.SnowLumaDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            // 重要：SnowLuma 默认 hookAutoLoad=false，只会接管“已经注入过 Hook”的 QQ。
            // 管理器启动时强制开启自动注入，才能接上当前已登录的 QQ 进程。
            psi.EnvironmentVariables["SNOWLUMA_HOOK_AUTOLOAD"] = "1";

            StartHidden(psi, "SnowLuma");
            _log.Log("已隐藏启动 SnowLuma，等待 " + _config.SnowLumaPort + " …");
            if (!WaitForPort(_config.SnowLumaPort, 60))
                throw new InvalidOperationException("SnowLuma 在 60 秒内未监听 " + _config.SnowLumaPort + "，请查看日志。");
            _log.Log("SnowLuma 已就绪。");
        }

        internal void StartDsh()
        {
            _dsh.StartDsh();
        }

        internal void StopAll()
        {
            try { StopSnowLuma(); }
            catch (Exception ex) { _log.Log("停止 SnowLuma 失败", ex); }
            try { StopAstrBot(); }
            catch (Exception ex) { _log.Log("停止 AstrBot 失败", ex); }
        }

        internal void StopAstrBot()
        {
            List<int> pids = new List<int>();
            pids.AddRange(GetListenerPids(_config.AstrBotPort));
            pids.AddRange(GetListenerPids(6185));
            pids.AddRange(FindPidsByPathPart("astrbot_launcher"));
            pids.AddRange(FindPidsByPathPart("astrbot-launcher"));
            KillUnique(pids, "AstrBot");
            WaitForPortClosed(_config.AstrBotPort, 8);
            WaitForPortClosed(6185, 8);
            _log.Log("AstrBot 停止流程完成。");
        }

        internal void StopSnowLuma()
        {
            List<int> pids = new List<int>();
            pids.AddRange(GetListenerPids(_config.SnowLumaPort));
            pids.AddRange(GetListenerPids(3000));
            pids.AddRange(GetListenerPids(3001));
            pids.AddRange(FindPidsByPathPart("snowluma"));
            KillUnique(pids, "SnowLuma");
            WaitForPortClosed(_config.SnowLumaPort, 8);
            WaitForPortClosed(3000, 8);
            WaitForPortClosed(3001, 8);
            _log.Log("SnowLuma 停止流程完成。");
        }

        internal void RestartAstrBot()
        {
            _log.Log("重启 AstrBot：先停止，再隐藏启动。");
            StopAstrBot();
            Thread.Sleep(1000);
            StartAstrBot();
        }

        internal void RestartDsh()
        {
            _log.Log("重启 DSH：将结束所有 DeepSeek Harness 进程。");
            Process[] list = Process.GetProcessesByName("DeepSeek Harness");
            foreach (Process p in list)
            {
                try
                {
                    _log.Log("结束 DSH PID " + p.Id);
                    p.Kill();
                }
                catch (Exception ex)
                {
                    _log.Log("结束 DSH PID " + p.Id + " 失败", ex);
                }
                finally
                {
                    p.Dispose();
                }
            }
            Thread.Sleep(2000);
            _dsh.StartDsh();
        }

        private void StartHidden(ProcessStartInfo psi, string name)
        {
            Process p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null) _log.Log("[" + name + "] " + e.Data);
            };
            p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null) _log.Log("[" + name + "] " + e.Data);
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _log.Log("[" + name + "] PID " + p.Id + " 已启动（隐藏）。");
        }

        private void KillUnique(List<int> pids, string label)
        {
            HashSet<int> seen = new HashSet<int>();
            foreach (int pid in pids)
            {
                if (pid <= 0 || pid == Process.GetCurrentProcess().Id) continue;
                if (!seen.Add(pid)) continue;
                KillPidTree(pid, label);
            }
        }

        private void KillPidTree(int pid, string label)
        {
            try
            {
                _log.Log("结束 " + label + " PID " + pid);
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "taskkill.exe";
                psi.Arguments = "/PID " + pid + " /T /F";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    string error = p.StandardError.ReadToEnd();
                    p.WaitForExit(8000);
                    if (!string.IsNullOrWhiteSpace(output)) _log.Log("taskkill: " + output.Trim());
                    if (!string.IsNullOrWhiteSpace(error)) _log.Log("taskkill: " + error.Trim());
                }
            }
            catch (Exception ex)
            {
                _log.Log("结束 PID " + pid + " 失败", ex);
            }
        }

        private List<int> GetListenerPids(int port)
        {
            List<int> result = new List<int>();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "netstat.exe";
                psi.Arguments = "-ano -p tcp";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.StandardOutputEncoding = Encoding.Default;
                using (Process p = Process.Start(psi))
                {
                    string text = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(8000);
                    string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string raw in lines)
                    {
                        string line = raw.Trim();
                        if (line.IndexOf("LISTEN", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string[] parts = Regex.Split(line, "\\s+");
                        if (parts.Length < 5) continue;
                        if (!parts[1].EndsWith(":" + port, StringComparison.OrdinalIgnoreCase)) continue;
                        int pid;
                        if (int.TryParse(parts[parts.Length - 1], out pid) && pid > 0)
                        {
                            if (!result.Contains(pid)) result.Add(pid);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Log("netstat 查询端口 " + port + " 失败", ex);
            }
            return result;
        }

        private List<int> FindPidsByPathPart(string part)
        {
            List<int> result = new List<int>();
            Process[] list;
            try { list = Process.GetProcesses(); }
            catch { return result; }
            foreach (Process p in list)
            {
                try
                {
                    string path = p.MainModule.FileName;
                    if (path != null && path.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!result.Contains(p.Id)) result.Add(p.Id);
                    }
                }
                catch
                {
                }
                finally
                {
                    p.Dispose();
                }
            }
            return result;
        }

        private bool WaitForPort(int port, int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                if (IsPortListening(port)) return true;
                Thread.Sleep(500);
            }
            return IsPortListening(port);
        }

        private void WaitForPortClosed(int port, int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                if (!IsPortListening(port)) return;
                Thread.Sleep(300);
            }
        }
    }
}
