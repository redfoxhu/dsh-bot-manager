using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace DshBotManager
{
    internal sealed class MainForm : Form
    {
        private readonly AppConfig _config;
        private readonly Logger _log;
        private readonly AstrBotSetup _astrbot;
        private readonly DshSetup _dsh;
        private readonly ProcessManager _pm;
        private readonly bool _autostart;

        private Timer _statusTimer;
        private Timer _startTimer;
        private NotifyIcon _tray;
        private ToolStripMenuItem _menuToggle;
        private ToolStripMenuItem _menuStartAll;
        private bool _busy;
        private bool _exiting;

        private Label _lblAstrBot;
        private Label _lblSnowLuma;
        private Label _lblDsh;
        private Label _lblBridge;
        private Label _lblIngress;
        private Label _lblControl;
        private ToolStripStatusLabel _lblStatus;
        private Button _btnStartAll;
        private Button _btnStopAll;
        private Button _btnToggle;
        private Button _btnRestartAstrBot;
        private Button _btnInstallDsh;
        private Button _btnRestartDsh;
        private TextBox _txtLog;
        private TextBox _txtAstrBot;
        private TextBox _txtSnowLuma;
        private TextBox _txtDsh;
        private TextBox _txtDshProfile;
        private TextBox _txtConnect;
        private TextBox _txtDisconnect;
        private TextBox _txtStatusCmd;
        private TextBox _txtAdmins;
        private TextBox _txtAstrBotPort;
        private TextBox _txtSnowPort;
        private TextBox _txtIngressPort;
        private CheckBox _chkAutoStart;
        private CheckBox _chkStartServices;
        private CheckBox _chkStartMinimized;
        private ComboBox _cmbCloseAction;

        internal MainForm(AppConfig config, Logger log, bool autostart)
        {
            _config = config;
            _log = log;
            _autostart = autostart;
            _astrbot = new AstrBotSetup(config, log);
            _dsh = new DshSetup(config, log);
            _pm = new ProcessManager(config, log, _astrbot, _dsh);

            Text = "DSH 机器人管家";
            Width = 1000;
            Height = 760;
            MinimumSize = new Size(820, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = SystemIcons.Application;
            Font = new Font("Microsoft YaHei UI", 9F);

            BuildUi();
            BuildTray();
            LoadSettingsToUi();
            _log.Line += OnLogLine;
            RefreshStatus();

            _statusTimer = new Timer();
            _statusTimer.Interval = 2000;
            _statusTimer.Tick += delegate { RefreshStatus(); };
            _statusTimer.Start();

            Shown += OnShown;
            FormClosing += OnFormClosing;
            FormClosed += delegate
            {
                _log.Line -= OnLogLine;
                if (_statusTimer != null) _statusTimer.Stop();
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
            };
        }

        private void BuildUi()
        {
            SuspendLayout();

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;

            TabPage statusPage = new TabPage("状态");
            statusPage.Padding = new Padding(12);
            statusPage.AutoScroll = true;
            BuildStatusPage(statusPage);

            TabPage settingsPage = new TabPage("设置");
            settingsPage.Padding = new Padding(12);
            settingsPage.AutoScroll = true;
            BuildSettingsPage(settingsPage);

            TabPage logsPage = new TabPage("日志");
            logsPage.Padding = new Padding(8);
            BuildLogsPage(logsPage);

            TabPage aboutPage = new TabPage("关于");
            aboutPage.Padding = new Padding(16);
            BuildAboutPage(aboutPage);

            tabs.TabPages.Add(statusPage);
            tabs.TabPages.Add(settingsPage);
            tabs.TabPages.Add(logsPage);
            tabs.TabPages.Add(aboutPage);
            Controls.Add(tabs);

            StatusStrip strip = new StatusStrip();
            _lblStatus = new ToolStripStatusLabel("就绪");
            strip.Items.Add(_lblStatus);
            Controls.Add(strip);

            ResumeLayout(false);
        }

        private void BuildStatusPage(TabPage page)
        {
            TableLayoutPanel table = new TableLayoutPanel();
            table.Dock = DockStyle.Top;
            table.AutoSize = true;
            table.ColumnCount = 2;
            table.RowCount = 6;
            table.Padding = new Padding(0, 0, 0, 8);
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _lblAstrBot = AddStatusRow(table, 0, "AstrBot", "检查中…");
            _lblSnowLuma = AddStatusRow(table, 1, "SnowLuma", "检查中…");
            _lblDsh = AddStatusRow(table, 2, "DSH", "检查中…");
            _lblBridge = AddStatusRow(table, 3, "DSH 桥", "检查中…");
            _lblIngress = AddStatusRow(table, 4, "Ingress 信标", "检查中…");
            _lblControl = AddStatusRow(table, 5, "控制插件", "检查中…");
            page.Controls.Add(table);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Top;
            buttons.AutoSize = true;
            buttons.WrapContents = true;
            buttons.Padding = new Padding(0, 8, 0, 8);

            _btnStartAll = MakeButton("一键启动");
            _btnStartAll.Click += delegate { RunTask("一键启动", delegate { _pm.StartAll(); }, null); };
            buttons.Controls.Add(_btnStartAll);

            _btnStopAll = MakeButton("停止 AstrBot/SnowLuma");
            _btnStopAll.Click += delegate
            {
                if (!Confirm("确定停止 AstrBot 和 SnowLuma？DSH 不会被停止。")) return;
                RunTask("停止 AstrBot/SnowLuma", delegate { _pm.StopAll(); }, null);
            };
            buttons.Controls.Add(_btnStopAll);

            _btnToggle = MakeButton("连接 DSH");
            _btnToggle.Click += delegate { ToggleDsh(); };
            buttons.Controls.Add(_btnToggle);

            _btnRestartAstrBot = MakeButton("重启 AstrBot");
            _btnRestartAstrBot.Click += delegate
            {
                if (!Confirm("重启 AstrBot 会短暂断开 QQ，然后隐藏启动。继续？")) return;
                RunTask("重启 AstrBot", delegate { _pm.RestartAstrBot(); }, null);
            };
            buttons.Controls.Add(_btnRestartAstrBot);

            _btnInstallDsh = MakeButton("安装/修复 DSH 桥");
            _btnInstallDsh.Click += delegate
            {
                if (!Confirm("将安装 dsh-astrbot-ingress 并修改 DSH desktop profile 的 package.json。不会自动重启 DSH。继续？")) return;
                RunTask("安装/修复 DSH 桥", delegate { _dsh.InstallOrRepair(); }, delegate
                {
                    RefreshStatus();
                    MessageBox.Show(this,
                        "安装流程完成。\r\n\r\n如果仍没有 Ingress 信标，请手动重启 DSH 以加载桥。\r\n本程序不会自动重启 DSH。",
                        "DSH 桥", MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
            };
            buttons.Controls.Add(_btnInstallDsh);

            _btnRestartDsh = MakeButton("重启 DSH");
            _btnRestartDsh.Click += delegate
            {
                if (!Confirm("警告：将结束所有 DeepSeek Harness 进程，当前 DSH 会话会中断。确定重启 DSH？")) return;
                RunTask("重启 DSH", delegate { _pm.RestartDsh(); }, delegate
                {
                    RefreshStatus();
                    MessageBox.Show(this, "DSH 重启流程完成。", "DSH", MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
            };
            buttons.Controls.Add(_btnRestartDsh);

            page.Controls.Add(buttons);

            FlowLayoutPanel open = new FlowLayoutPanel();
            open.Dock = DockStyle.Top;
            open.AutoSize = true;
            open.WrapContents = true;
            open.Padding = new Padding(0, 4, 0, 4);

            Button b1 = MakeButton("AstrBot 面板");
            b1.Click += delegate { OpenUrl("http://127.0.0.1:6185"); };
            open.Controls.Add(b1);

            Button b2 = MakeButton("SnowLuma WebUI");
            b2.Click += delegate { OpenUrl("http://127.0.0.1:" + _config.SnowLumaPort); };
            open.Controls.Add(b2);

            Button b3 = MakeButton("DSH");
            b3.Click += delegate { OpenUrl("http://127.0.0.1:13391"); };
            open.Controls.Add(b3);
            page.Controls.Add(open);

            Label tip = new Label();
            tip.AutoSize = true;
            tip.MaximumSize = new Size(900, 0);
            tip.ForeColor = Color.DimGray;
            tip.Text = "提示：AstrBot/SnowLuma 由本程序启动时隐藏窗口；若它们原本由启动器打开，会先采用现有进程。\r\n" +
                       "关闭行为在“设置”中选择；退出时只停止 AstrBot/SnowLuma，不停止 DSH。";
            tip.Padding = new Padding(0, 10, 0, 0);
            page.Controls.Add(tip);

            // Dock=Top 的控件按添加顺序从下往上排，重新调整顺序让状态卡在最上面。
            page.Controls.SetChildIndex(tip, 3);
            page.Controls.SetChildIndex(open, 2);
            page.Controls.SetChildIndex(buttons, 1);
            page.Controls.SetChildIndex(table, 0);
        }

        private Label AddStatusRow(TableLayoutPanel table, int row, string title, string value)
        {
            Label name = new Label();
            name.Text = title;
            name.AutoSize = true;
            name.Font = new Font(Font, FontStyle.Bold);
            name.Margin = new Padding(3, 6, 3, 6);
            table.Controls.Add(name, 0, row);

            Label val = new Label();
            val.Text = value;
            val.AutoSize = true;
            val.Margin = new Padding(3, 6, 3, 6);
            table.Controls.Add(val, 1, row);
            return val;
        }

        private void BuildSettingsPage(TabPage page)
        {
            TableLayoutPanel table = new TableLayoutPanel();
            table.Dock = DockStyle.Top;
            table.AutoSize = true;
            table.ColumnCount = 2;
            table.Padding = new Padding(0, 0, 0, 8);
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            int row = 0;
            _txtAstrBot = AddPathRow(table, row++, "AstrBot 实例目录", true);
            _txtSnowLuma = AddPathRow(table, row++, "SnowLuma 目录", true);
            _txtDsh = AddPathRow(table, row++, "DSH 目录", true);
            _txtDshProfile = AddTextRow(table, row++, "DSH profile", "desktop");
            _txtConnect = AddTextRow(table, row++, "连接命令", "连接dsh");
            _txtDisconnect = AddTextRow(table, row++, "断开命令", "断开dsh");
            _txtStatusCmd = AddTextRow(table, row++, "状态命令（可留空）", "dsh状态");
            _txtAdmins = AddTextRow(table, row++, "管理员 QQ（逗号分隔）", "1031998556");

            _cmbCloseAction = new ComboBox();
            _cmbCloseAction.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbCloseAction.Items.Add("隐藏到托盘");
            _cmbCloseAction.Items.Add("退出");
            _cmbCloseAction.Width = 220;
            AddRow(table, row++, "关闭窗口时", _cmbCloseAction);

            _chkAutoStart = new CheckBox();
            _chkAutoStart.Text = "开机自动启动（当前用户）";
            _chkAutoStart.AutoSize = true;
            AddRow(table, row++, "开机自启", _chkAutoStart);

            _chkStartServices = new CheckBox();
            _chkStartServices.Text = "启动程序时自动启动 AstrBot/SnowLuma";
            _chkStartServices.AutoSize = true;
            AddRow(table, row++, "启动行为", _chkStartServices);

            _chkStartMinimized = new CheckBox();
            _chkStartMinimized.Text = "普通启动时最小化到托盘";
            _chkStartMinimized.AutoSize = true;
            AddRow(table, row++, "启动窗口", _chkStartMinimized);

            _txtAstrBotPort = AddTextRow(table, row++, "AstrBot 端口", "6199");
            _txtSnowPort = AddTextRow(table, row++, "SnowLuma 端口", "5099");
            _txtIngressPort = AddTextRow(table, row++, "DSH ingress 端口", "3188");

            page.Controls.Add(table);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Top;
            actions.AutoSize = true;
            actions.Padding = new Padding(0, 10, 0, 0);

            Button save = MakeButton("保存设置");
            save.Click += delegate { SaveSettings(); };
            actions.Controls.Add(save);

            Button reload = MakeButton("重新载入");
            reload.Click += delegate
            {
                _config.Normalize();
                LoadSettingsToUi();
                RefreshStatus();
            };
            actions.Controls.Add(reload);

            Button folder = MakeButton("打开日志目录");
            folder.Click += delegate { OpenFolder(AppConfig.LogDir); };
            actions.Controls.Add(folder);
            page.Controls.Add(actions);
            page.Controls.SetChildIndex(actions, 0);
            page.Controls.SetChildIndex(table, 1);
        }

        private TextBox AddTextRow(TableLayoutPanel table, int row, string label, string value)
        {
            TextBox box = new TextBox();
            box.Text = value;
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(3, 5, 3, 5);
            AddRow(table, row, label, box);
            return box;
        }

        private TextBox AddPathRow(TableLayoutPanel table, int row, string label, bool folder)
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.Height = 30;
            panel.Margin = new Padding(3, 3, 3, 3);

            TextBox box = new TextBox();
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(0);
            panel.Controls.Add(box);

            Button browse = new Button();
            browse.Text = "浏览";
            browse.Dock = DockStyle.Right;
            browse.Width = 72;
            browse.Click += delegate
            {
                FolderBrowserDialog dialog = new FolderBrowserDialog();
                dialog.Description = "选择目录";
                if (Directory.Exists(box.Text)) dialog.SelectedPath = box.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
            };
            panel.Controls.Add(browse);
            AddRow(table, row, label, panel);
            return box;
        }

        private void AddRow(TableLayoutPanel table, int row, string label, Control control)
        {
            Label l = new Label();
            l.Text = label;
            l.AutoSize = true;
            l.Margin = new Padding(3, 7, 3, 5);
            table.Controls.Add(l, 0, row);
            control.Margin = new Padding(3, 4, 3, 4);
            table.Controls.Add(control, 1, row);
        }

        private void BuildLogsPage(TabPage page)
        {
            _txtLog = new TextBox();
            _txtLog.Dock = DockStyle.Fill;
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Both;
            _txtLog.WordWrap = false;
            _txtLog.Font = new Font("Consolas", 9F);
            _txtLog.BackColor = Color.White;
            page.Controls.Add(_txtLog);

            FlowLayoutPanel top = new FlowLayoutPanel();
            top.Dock = DockStyle.Top;
            top.AutoSize = true;
            top.Padding = new Padding(0, 0, 0, 6);
            Button open = MakeButton("打开日志目录");
            open.Click += delegate { OpenFolder(AppConfig.LogDir); };
            top.Controls.Add(open);
            Button clear = MakeButton("清空显示");
            clear.Click += delegate { _txtLog.Clear(); };
            top.Controls.Add(clear);
            page.Controls.Add(top);
            page.Controls.SetChildIndex(top, 0);
            page.Controls.SetChildIndex(_txtLog, 1);
        }

        private void BuildAboutPage(TabPage page)
        {
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.TopDown;
            panel.WrapContents = false;
            panel.AutoScroll = true;
            panel.Padding = new Padding(0);

            Label title = new Label();
            title.Text = "DSH 机器人管家";
            title.Font = new Font(Font.FontFamily, 16F, FontStyle.Bold);
            title.AutoSize = true;
            panel.Controls.Add(title);

            Label text = new Label();
            text.AutoSize = true;
            text.MaximumSize = new Size(860, 0);
            text.Padding = new Padding(0, 12, 0, 0);
            text.Text =
                "功能：隐藏启动 AstrBot/SnowLuma，管理员 QQ 命令连接/断开 DSH 桥，开机自启，关闭隐藏/退出。\r\n\r\n" +
                "聊天命令默认：连接dsh / 断开dsh / dsh状态。命令和关闭行为可在“设置”中修改。\r\n" +
                "DSH 桥安装会修改 DSH desktop profile 的 package.json，但不会自动重启 DSH。\r\n" +
                "退出程序只停止 AstrBot/SnowLuma，不停止 DSH。\r\n\r\n" +
                "AstrBot 数据目录：" + _astrbot.DataDir + "\r\n" +
                "管理器配置：" + AppConfig.ConfigPath;
            panel.Controls.Add(text);

            LinkLabel link = new LinkLabel();
            link.Text = "打开 DSH";
            link.AutoSize = true;
            link.Padding = new Padding(0, 12, 0, 0);
            link.Click += delegate { OpenUrl("http://127.0.0.1:13391"); };
            panel.Controls.Add(link);

            page.Controls.Add(panel);
        }

        private static Button MakeButton(string text)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.Padding = new Padding(8, 3, 8, 3);
            b.Margin = new Padding(3, 3, 6, 3);
            return b;
        }

        private void BuildTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Text = "DSH 机器人管家";
            _tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem show = new ToolStripMenuItem("显示主界面");
            show.Click += delegate { ShowFromTray(); };
            menu.Items.Add(show);

            _menuStartAll = new ToolStripMenuItem("一键启动");
            _menuStartAll.Click += delegate { RunTask("一键启动", delegate { _pm.StartAll(); }, null); };
            menu.Items.Add(_menuStartAll);

            _menuToggle = new ToolStripMenuItem("连接 DSH");
            _menuToggle.Click += delegate { ToggleDsh(); };
            menu.Items.Add(_menuToggle);

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { RequestExit(true); };
            menu.Items.Add(exit);

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ShowFromTray(); };
        }

        private void OnShown(object sender, EventArgs e)
        {
            if (_autostart)
            {
                Hide();
                _log.Log("开机自启模式：已隐藏到托盘。");
            }
            else if (_config.StartMinimized)
            {
                Hide();
                _log.Log("按设置启动后隐藏到托盘。");
            }

            if (_config.StartServicesOnLaunch)
            {
                _startTimer = new Timer();
                _startTimer.Interval = 3000;
                _startTimer.Tick += delegate
                {
                    _startTimer.Stop();
                    _startTimer.Dispose();
                    _startTimer = null;
                    RunTask("自动启动服务", delegate { _pm.StartAll(); }, null);
                };
                _startTimer.Start();
            }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_exiting) return;
            if (_config.CloseAction == "hide")
            {
                e.Cancel = true;
                Hide();
                ShowBalloon("已隐藏到托盘", "程序仍在后台运行；从托盘菜单可退出。");
                return;
            }
            e.Cancel = true;
            RequestExit(true);
        }

        private void RequestExit(bool confirm)
        {
            if (_exiting) return;
            if (_busy)
            {
                MessageBox.Show(this, "当前有操作正在进行，请稍后再退出。", "DSH 机器人管家",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (confirm)
            {
                DialogResult result = MessageBox.Show(this,
                    "退出会停止 AstrBot 和 SnowLuma（DSH 不会退出）。确定退出？",
                    "DSH 机器人管家", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result != DialogResult.Yes) return;
            }

            _exiting = true;
            if (_tray != null) _tray.Visible = false;
            RunTask("退出并停止服务", delegate { _pm.StopAll(); }, delegate { Close(); });
        }

        private void ToggleDsh()
        {
            if (_busy) return;
            bool current = GetBridgeEnabled();
            bool target = !current;
            try
            {
                _astrbot.EnsureInstance();
                _astrbot.WriteRuntime(target);
                if (_astrbot.IsControlPluginLoaded())
                    _log.Log("已通过界面下发 DSH 连接状态：" + (target ? "连接" : "断开"));
                else
                    _log.Log("控制插件未加载，已写入 runtime；重启 AstrBot 后生效。");
                RefreshStatus();
                if (!_astrbot.IsControlPluginLoaded())
                {
                    MessageBox.Show(this,
                        "已写入连接设置，但控制插件当前未加载或状态过期。\r\n请用“重启 AstrBot”加载控制插件后再生效。",
                        "DSH 连接", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                _log.Log("切换 DSH 连接失败", ex);
                MessageBox.Show(this, ex.Message, "切换失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private bool GetBridgeEnabled()
        {
            if (_astrbot.IsControlPluginLoaded())
            {
                ControlStatus status = _astrbot.ReadStatus();
                if (status != null) return status.bridge_enabled;
            }
            return _astrbot.ReadBridgeEnabled();
        }

        private void SaveSettings()
        {
            try
            {
                string astrbot = _txtAstrBot.Text.Trim();
                if (astrbot.Length == 0)
                {
                    astrbot = _astrbot.DiscoverInstance();
                    if (astrbot == null) throw new InvalidOperationException("未找到 AstrBot 实例，请手动选择目录。");
                }
                if (!File.Exists(Path.Combine(astrbot, "core", "main.py")))
                    throw new InvalidOperationException("AstrBot 实例目录无效（缺少 core\\main.py）。");

                string snow = _txtSnowLuma.Text.Trim();
                if (snow.Length == 0) throw new InvalidOperationException("SnowLuma 目录不能为空。");
                if (!File.Exists(Path.Combine(snow, "node.exe")))
                    throw new InvalidOperationException("SnowLuma 目录无效（缺少 node.exe）。");

                string dsh = _txtDsh.Text.Trim();
                if (dsh.Length == 0) throw new InvalidOperationException("DSH 目录不能为空。");
                if (!File.Exists(Path.Combine(dsh, "DeepSeek Harness.exe")))
                    throw new InvalidOperationException("DSH 目录无效（缺少 DeepSeek Harness.exe）。");

                string connect = NormalizeCommand(_txtConnect.Text);
                string disconnect = NormalizeCommand(_txtDisconnect.Text);
                string status = NormalizeCommand(_txtStatusCmd.Text);
                if (connect.Length == 0 || disconnect.Length == 0)
                    throw new InvalidOperationException("连接/断开命令不能为空。");
                if (string.Equals(connect, disconnect, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("连接命令和断开命令不能相同。");
                if (status.Length > 0 &&
                    (string.Equals(status, connect, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(status, disconnect, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("状态命令不能与连接/断开命令重复。");

                List<string> admins = ParseAdmins(_txtAdmins.Text);
                if (admins.Count == 0) throw new InvalidOperationException("至少需要一个管理员 QQ。");

                int astrbotPort = ParsePort(_txtAstrBotPort.Text, "AstrBot 端口");
                int snowPort = ParsePort(_txtSnowPort.Text, "SnowLuma 端口");
                int ingressPort = ParsePort(_txtIngressPort.Text, "DSH ingress 端口");

                _config.AstrBotInstance = astrbot;
                _config.SnowLumaDir = snow;
                _config.DshDir = dsh;
                _config.DshProfile = string.IsNullOrWhiteSpace(_txtDshProfile.Text) ? "desktop" : _txtDshProfile.Text.Trim();
                _config.ConnectCmd = connect;
                _config.DisconnectCmd = disconnect;
                _config.StatusCmd = status;
                _config.AdminIds = admins;
                _config.AstrBotPort = astrbotPort;
                _config.SnowLumaPort = snowPort;
                _config.IngressPort = ingressPort;
                _config.CloseAction = (_cmbCloseAction.SelectedIndex == 1) ? "exit" : "hide";
                _config.AutoStart = _chkAutoStart.Checked;
                _config.StartServicesOnLaunch = _chkStartServices.Checked;
                _config.StartMinimized = _chkStartMinimized.Checked;
                _config.Normalize();
                _config.Save();

                AutoStart.Apply(_config.AutoStart);
                _astrbot.WriteRuntime(null);
                _log.Log("设置已保存。");
                LoadSettingsToUi();
                RefreshStatus();
                MessageBox.Show(this, "设置已保存。", "DSH 机器人管家",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _log.Log("保存设置失败", ex);
                MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string NormalizeCommand(string text)
        {
            if (text == null) return string.Empty;
            string s = text.Trim();
            if (s.StartsWith("/", StringComparison.Ordinal)) s = s.Substring(1).Trim();
            return s;
        }

        private static List<string> ParseAdmins(string text)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            string[] parts = text.Split(new char[] { ',', '，', ';', '；', ' ', '\r', '\n', '\t' },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                string s = part.Trim();
                if (s.Length == 0) continue;
                bool digits = true;
                foreach (char c in s)
                {
                    if (!char.IsDigit(c)) { digits = false; break; }
                }
                if (!digits) throw new InvalidOperationException("管理员 QQ 必须是数字：" + s);
                if (!result.Contains(s)) result.Add(s);
            }
            return result;
        }

        private static int ParsePort(string text, string label)
        {
            int port;
            if (!int.TryParse((text ?? string.Empty).Trim(), out port) || port < 1 || port > 65535)
                throw new InvalidOperationException(label + "无效。");
            return port;
        }

        private void LoadSettingsToUi()
        {
            _txtAstrBot.Text = _config.AstrBotInstance;
            _txtSnowLuma.Text = _config.SnowLumaDir;
            _txtDsh.Text = _config.DshDir;
            _txtDshProfile.Text = _config.DshProfile;
            _txtConnect.Text = _config.ConnectCmd;
            _txtDisconnect.Text = _config.DisconnectCmd;
            _txtStatusCmd.Text = _config.StatusCmd;
            _txtAdmins.Text = string.Join(", ", _config.AdminIds.ToArray());
            _txtAstrBotPort.Text = _config.AstrBotPort.ToString();
            _txtSnowPort.Text = _config.SnowLumaPort.ToString();
            _txtIngressPort.Text = _config.IngressPort.ToString();
            _cmbCloseAction.SelectedIndex = (_config.CloseAction == "exit") ? 1 : 0;
            _chkAutoStart.Checked = _config.AutoStart;
            _chkStartServices.Checked = _config.StartServicesOnLaunch;
            _chkStartMinimized.Checked = _config.StartMinimized;
        }

        private void RefreshStatus()
        {
            try
            {
                bool astrbot = _pm.IsAstrBotRunning();
                bool snow = _pm.IsSnowLumaRunning();
                bool dsh = _pm.IsDshRunning();
                bool bridge = !string.IsNullOrWhiteSpace(_config.AstrBotInstance) && _astrbot.IsBridgeInstalled();
                bool control = _astrbot.IsControlPluginLoaded();
                IngressInfo ingress = _astrbot.ReadIngressBeacon();
                bool enabled = GetBridgeEnabled();

                SetStatusLabel(_lblAstrBot, astrbot ? "运行中（" + _config.AstrBotPort + "）" : "已停止", astrbot);
                SetStatusLabel(_lblSnowLuma, snow ? "运行中（" + _config.SnowLumaPort + "）" : "已停止", snow);
                SetStatusLabel(_lblDsh, dsh ? "运行中（13391）" : "已停止", dsh);
                SetStatusLabel(_lblBridge, (bridge ? "插件已安装" : "插件未安装") +
                    "；" + (enabled ? "已连接" : "已断开"), bridge);
                SetStatusLabel(_lblIngress, ingress != null ? "正常 " + ingress.url : "未发现", ingress != null);
                SetStatusLabel(_lblControl, control ? "运行中" : "未加载/已过期", control);

                if (_btnToggle != null) _btnToggle.Text = enabled ? "断开 DSH" : "连接 DSH";
                if (_menuToggle != null) _menuToggle.Text = enabled ? "断开 DSH" : "连接 DSH";
                if (_lblStatus != null)
                {
                    _lblStatus.Text = "AstrBot " + (astrbot ? "运行" : "停止") +
                        "  |  SnowLuma " + (snow ? "运行" : "停止") +
                        "  |  DSH " + (dsh ? "运行" : "停止") +
                        "  |  桥 " + (enabled ? "连接" : "断开") +
                        "  |  控制插件 " + (control ? "正常" : "未加载");
                }
            }
            catch (Exception ex)
            {
                _log.Log("刷新状态失败", ex);
            }
        }

        private static void SetStatusLabel(Label label, string text, bool ok)
        {
            if (label == null) return;
            label.Text = text;
            label.ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
        }

        private void RunTask(string title, Action action, Action onDone)
        {
            if (_busy)
            {
                MessageBox.Show(this, "当前有操作正在进行。", "DSH 机器人管家",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _busy = true;
            SetBusy(true);
            _log.Log("开始：" + title);

            string error = null;
            Task.Run(delegate
            {
                try
                {
                    action();
                    _log.Log("完成：" + title);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    _log.Log(title + "失败", ex);
                }
                finally
                {
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            _busy = false;
                            SetBusy(false);
                            if (onDone != null) onDone();
                            else RefreshStatus();
                            if (error != null && !IsDisposed)
                            {
                                MessageBox.Show(this, error, title + "失败",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }));
                    }
                    catch
                    {
                    }
                }
            });
        }

        private void SetBusy(bool busy)
        {
            UseWaitCursor = busy;
            Button[] buttons = new Button[]
            {
                _btnStartAll, _btnStopAll, _btnToggle,
                _btnRestartAstrBot, _btnInstallDsh, _btnRestartDsh
            };
            foreach (Button b in buttons)
            {
                if (b != null) b.Enabled = !busy;
            }
            if (_menuStartAll != null) _menuStartAll.Enabled = !busy;
            if (_menuToggle != null) _menuToggle.Enabled = !busy;
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void ShowBalloon(string title, string text)
        {
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.ShowBalloonTip(2500);
            }
            catch
            {
            }
        }

        private void OnLogLine(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(OnLogLine), line); }
                catch { }
                return;
            }
            if (_txtLog == null) return;
            try
            {
                _txtLog.AppendText(line + Environment.NewLine);
                if (_txtLog.TextLength > 300000)
                    _txtLog.Text = _txtLog.Text.Substring(_txtLog.TextLength - 200000);
                _txtLog.SelectionStart = _txtLog.TextLength;
                _txtLog.ScrollToCaret();
            }
            catch
            {
            }
        }

        private bool Confirm(string text)
        {
            return MessageBox.Show(this, text, "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        private void OpenUrl(string url)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = url;
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                _log.Log("打开链接失败：" + url, ex);
                MessageBox.Show(this, ex.Message, "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "explorer.exe";
                psi.Arguments = "\"" + path + "\"";
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                _log.Log("打开目录失败：" + path, ex);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_statusTimer != null) _statusTimer.Dispose();
                if (_startTimer != null) _startTimer.Dispose();
                if (_tray != null) _tray.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
