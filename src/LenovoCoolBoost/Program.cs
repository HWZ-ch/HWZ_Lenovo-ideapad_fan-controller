using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace LenovoCoolBoost
{
    /// <summary>
    /// 联想降温除尘控制程序。
    /// 后端 = Lenovo EnergyDrv 设备 IOCTL（即联想电脑管家底层用的风扇控制）。
    /// 注意：EnergyDrv「启动除尘」的 9s高速+2s暂停是 EC 固件写死的周期，软件无法消除；
    /// 本程序用后台循环在掉速时重置计时并重开，尽量接近连续最大转速。
    ///
    /// v1.2 变化：新增可见的控制窗口。此前只有托盘图标，而 Windows 11 默认把新应用的
    /// 托盘图标折叠进「^」溢出区，用户找不到图标就无法停止风扇。现在窗口上有明确的
    /// 「停止并恢复默认」按钮，托盘只作为次要入口。
    ///
    /// 命令行：
    ///   (无参数)      打开控制窗口，并自动开启满速
    ///   --tray        启动后只驻留托盘（不显示窗口）
    ///   --status      打印当前风扇状态后退出（只读，不会改变风扇状态）
    ///   --off         一次性恢复默认转速后退出
    ///   --test        无界面自检：开启并采样 180 秒，结束自动恢复
    ///   --diag        观测模式：逐秒记录真实读数，长停转时尝试软重启
    ///   --no-elevate  以普通权限运行，不尝试 UAC 提权
    ///
    /// 诊断日志：%TEMP%\LenovoCoolBoost.log
    /// </summary>
    public class Program : Form
    {
        private readonly FanController _fan = new FanController();

        // 控件
        private Label _statusLabel;
        private Label _detailLabel;
        private Button _startButton;
        private Button _stopButton;
        private Button _hideButton;
        private Button _exitButton;
        private CheckBox _autoStartCheck;
        private System.Windows.Forms.Timer _refreshTimer;

        private NotifyIcon _tray;
        private ToolStripMenuItem _trayStatusItem;
        private ToolStripMenuItem _trayStartItem;
        private ToolStripMenuItem _trayStopItem;
        private ToolStripMenuItem _trayShowItem;
        private Microsoft.Win32.PowerModeChangedEventHandler _powerHandler;

        private bool _autoResume;
        private bool _elevatedAtStart;
        private bool _exiting;
        private bool _allowClose;

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "LenovoCoolBoost";
        private static Mutex _mutex;

        [DllImport("kernel32")]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("user32", CharSet = CharSet.Auto)]
        private static extern int RegisterWindowMessage(string lpString);

        /// <summary>资源管理器重启后会广播这条消息，收到就必须重建托盘图标，否则图标永久消失。</summary>
        private static readonly int WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");

        private static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "LenovoCoolBoost.log");

        // 图标需要在进程生命周期内保持有效，否则托盘可能显示空白
        private static Icon _iconSmall;
        private static Icon _iconLarge;
        private static readonly System.Collections.Generic.List<MemoryStream> _iconStreams =
            new System.Collections.Generic.List<MemoryStream>();

        // ------------------------------------------------------------------
        // 基础工具
        // ------------------------------------------------------------------
        private static void Log(string s)
        {
            try
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\n");
            }
            catch { }
        }

        private static bool IsAdmin()
        {
            try
            {
                return new WindowsPrincipal(WindowsIdentity.GetCurrent())
                    .IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static bool HasArg(string[] args, string name)
        {
            return args != null && Array.Exists(args,
                a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// WinExe 是 GUI 子系统，默认没有控制台；命令行模式下挂到父进程控制台才能看到输出。
        /// 不碰 Console.OutputEncoding，让它跟随当前控制台代码页。
        /// </summary>
        private static void AttachParentConsole()
        {
            try { AttachConsole(-1); } catch { }
        }

        /// <summary>从嵌入资源加载图标；失败则退回 exe 图标，再失败退回系统图标。</summary>
        private static Icon LoadIcon(int size, ref Icon cache)
        {
            if (cache != null) return cache;
            try
            {
                var asm = typeof(Program).Assembly;
                using (var s = asm.GetManifestResourceStream("LenovoCoolBoost.app.ico"))
                {
                    if (s != null)
                    {
                        var ms = new MemoryStream();
                        s.CopyTo(ms);
                        ms.Position = 0;
                        _iconStreams.Add(ms);   // 保住流，避免 Icon 内部引用被回收
                        cache = new Icon(ms, new Size(size, size));
                        return cache;
                    }
                }
            }
            catch (Exception ex) { Log("加载嵌入图标失败: " + ex.Message); }

            try
            {
                cache = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (cache != null) return cache;
            }
            catch { }

            cache = SystemIcons.Application;
            return cache;
        }

        // ------------------------------------------------------------------
        // 入口
        // ------------------------------------------------------------------
        [STAThread]
        public static void Main(string[] args)
        {
            Log("=== 启动 === 参数=[" + string.Join(" ", args ?? new string[0]) + "]");

            if (HasArg(args, "--status")) { AttachParentConsole(); CmdStatus(); return; }
            if (HasArg(args, "--off")) { AttachParentConsole(); CmdRestore(); return; }
            if (HasArg(args, "--test")) { SelfTest(); return; }
            if (HasArg(args, "--diag")) { Diag(); return; }

            // 自动提权：EnergyDrv 需要管理员权限才能打开
            if (!IsAdmin() && !HasArg(args, "--no-elevate"))
            {
                if (TryRelaunchElevated(args))
                {
                    Log("已请求 UAC 提权，当前实例退出。");
                    return;
                }
                Log("UAC 提权被取消或失败，以普通权限继续运行。");
            }

            // 单实例：多开会让多个保活循环互相抢设备
            _mutex = new Mutex(true, @"Global\LenovoCoolBoost_SingleInstance", out bool created);
            if (!created)
            {
                Log("已有实例在运行，提示用户。");
                MessageBox.Show("LenovoCoolBoost 已在运行。请使用它的窗口或托盘图标操作。",
                    "已在运行", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try { Application.SetHighDpiMode(HighDpiMode.SystemAware); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Program(HasArg(args, "--tray")));
        }

        private static bool TryRelaunchElevated(string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                if (args != null)
                {
                    foreach (var a in args)
                    {
                        if (string.Equals(a, "--no-elevate", StringComparison.OrdinalIgnoreCase)) continue;
                        psi.ArgumentList.Add(a);
                    }
                }
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                Log("提权失败: " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 命令行子命令
        // ------------------------------------------------------------------
        private static void CmdStatus()
        {
            var fan = new FanController();
            try
            {
                var st = fan.GetStatus();
                Console.WriteLine("LenovoCoolBoost 状态查询");
                Console.WriteLine("  设备    : " + (fan.DeviceName ?? "(未打开)"));
                Console.WriteLine("  状态    : " + st.Describe());
                Console.WriteLine("  阈值    : " + (st.Calibrated
                    ? string.Format("{0:F1}（本次启动已校准）", st.Threshold)
                    : string.Format("{0:F1}（默认值，未校准）", st.Threshold)));
                Console.WriteLine("  管理员  : " + (IsAdmin() ? "是" : "否（可能无法控制风扇）"));
            }
            finally { fan.Dispose(); }
        }

        private static void CmdRestore()
        {
            var fan = new FanController();
            try
            {
                fan.Restore();
                Thread.Sleep(400);
                Console.WriteLine("已恢复默认转速: " + fan.GetStatus().Describe());
            }
            catch (Exception ex)
            {
                Console.WriteLine("恢复失败: " + ex.Message);
                Environment.Exit(1);
            }
            finally { fan.Dispose(); }
        }

        // ------------------------------------------------------------------
        // 无界面自检
        // ------------------------------------------------------------------
        private static void SelfTest()
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LenovoCoolBoost_test.log");
            void LogT(string s)
            {
                try
                {
                    File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\n");
                }
                catch { }
            }
            LogT("=== LenovoCoolBoost 自检模式 (--test) ===");
            var fan = new FanController();
            LogT("驱动就绪: " + fan.DriverReady + "  设备: " + (fan.DeviceName ?? "(无)"));
            if (!fan.DriverReady)
            {
                LogT("FAIL: 无法打开 Lenovo EnergyDrv 风扇设备。请确认：1) 联想电脑管家在运行；2) 以管理员身份运行。");
                Environment.Exit(2);
            }
            try
            {
                var before = fan.GetStatus();
                LogT("开启前: " + before.Describe());

                fan.Enable();
                LogT("已启用，开始 180 秒持续采样...");
                int ok = 0, bad = 0;
                const int total = 360;
                for (int i = 0; i < total; i++)
                {
                    Thread.Sleep(500);
                    var st = fan.GetStatus();
                    if (st.State == FanState.Spinning) ok++; else bad++;
                    if ((i + 1) % 20 == 0)
                        LogT(string.Format("  采样 {0}/{1}: {2}", i + 1, total, st.Describe()));
                }
                LogT(string.Format("采样结果: 在转={0} 次, 偏离={1} 次, 保持率={2:F1}%",
                    ok, bad, ok * 100.0 / total));

                fan.Disable();
                Thread.Sleep(3000);
                LogT("关闭后: " + fan.GetStatus().Describe());

                bool pass = ok >= total * 0.9;
                LogT(pass
                    ? "PASS: 风扇在 180s 内稳定维持最大转速（保持率≥90%）。"
                    : "WARN: 开启期间仍有明显偏离，请检查设备/管家状态。");
                Environment.Exit(pass ? 0 : 1);
            }
            catch (Exception ex)
            {
                LogT("FAIL: " + ex.Message);
                try { fan.Dispose(); } catch { }
                Environment.Exit(3);
            }
        }

        // ------------------------------------------------------------------
        // 观测模式
        // ------------------------------------------------------------------
        private static void Diag()
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LenovoCoolBoost_diag.log");
            void LogT(string s)
            {
                try
                {
                    File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\n");
                }
                catch { }
            }
            LogT("=== LenovoCoolBoost 观测模式 (--diag) ===");
            var fan = new FanController();
            LogT("驱动就绪: " + fan.DriverReady + "  设备: " + (fan.DeviceName ?? "(无)"));
            if (!fan.DriverReady)
            {
                LogT("FAIL: 无法打开 Lenovo EnergyDrv 风扇设备。");
                Environment.Exit(2);
            }

            fan.Enable();
            LogT("已 Enable(后台循环连续重触发)，开始观测（最长 5 分钟）...");

            int total = 300;
            int consecutiveStop = 0;
            int recoveryAttempts = 0, recoveryOk = 0;
            var dist = new System.Collections.Generic.Dictionary<uint, int>();
            int distWindow = 0;

            for (int t = 1; t <= total; t++)
            {
                Thread.Sleep(1000);
                var st = fan.GetStatus();
                uint s = st.Raw;
                bool spinning = st.State == FanState.Spinning;
                if (spinning) consecutiveStop = 0; else consecutiveStop++;

                if (!dist.ContainsKey(s)) dist[s] = 0;
                dist[s]++;
                if (++distWindow >= 30)
                {
                    distWindow = 0;
                    var pairs = System.Linq.Enumerable.ToList(dist);
                    var ordered = System.Linq.Enumerable.Take(
                        System.Linq.Enumerable.OrderByDescending(pairs, kv => kv.Value), 5);
                    var top = string.Join(",", System.Linq.Enumerable.Select(ordered,
                        kv => string.Format("0x{0:X2}x{1}", kv.Key, kv.Value)));
                    LogT("  [分布@t=" + t + "] " + top);
                    dist.Clear();
                }

                LogT(string.Format("t={0}s status=0x{1:X2} {2}", t, s, spinning ? "生效(转)" : "停转"));

                if (consecutiveStop == 15 && recoveryAttempts < 3)
                {
                    recoveryAttempts++;
                    LogT(string.Format("  [软重启] 持续停转{0}s，执行 Disable()+Enable()...", consecutiveStop));
                    try { fan.Disable(); Thread.Sleep(800); fan.Enable(); }
                    catch (Exception ex) { LogT("  软重启异常: " + ex.Message); }
                    Thread.Sleep(3000);
                    var st2 = fan.GetStatus();
                    if (st2.State == FanState.Spinning)
                    {
                        recoveryOk++; consecutiveStop = 0;
                        LogT("  [软重启结果] 恢复生效 (" + st2.Describe() + ")");
                    }
                    else
                    {
                        LogT("  [软重启结果] 仍未恢复 (" + st2.Describe() + ")");
                    }
                }
            }

            LogT(string.Format("DONE 软重启次数={0} 软重启恢复={1}", recoveryAttempts, recoveryOk));
            try { fan.Dispose(); } catch { }
            Environment.Exit(0);
        }

        // ------------------------------------------------------------------
        // 窗口
        // ------------------------------------------------------------------
        public Program(bool startHidden)
        {
            _elevatedAtStart = IsAdmin();
            _fan.Logger = s => Log(s);

            BuildWindow();
            BuildTray();

            _powerHandler = (s, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Suspend) StopFan(false);
                else if (e.Mode == Microsoft.Win32.PowerModes.Resume && _autoResume) StartFan(false);
            };
            Microsoft.Win32.SystemEvents.PowerModeChanged += _powerHandler;

            _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _refreshTimer.Tick += (s, e) => RefreshStatus();
            _refreshTimer.Start();

            Log("窗口已创建，自动开启满速...");
            StartFan(false);

            if (startHidden)
            {
                // 不显示窗口，只留托盘
                Load += (s, e) => Hide();
            }
        }

        private void BuildWindow()
        {
            Text = "联想降温除尘";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(420, 250);
            Icon = LoadIcon(32, ref _iconLarge);

            var font = new Font("Microsoft YaHei UI", 9f);

            var title = new Label
            {
                Text = "联想降温除尘 · 风扇最大转速",
                Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
                Location = new Point(18, 14),
                AutoSize = true
            };

            _statusLabel = new Label
            {
                Text = "状态：查询中…",
                Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold),
                Location = new Point(18, 48),
                Size = new Size(384, 30)
            };

            _detailLabel = new Label
            {
                Text = "",
                ForeColor = Color.DimGray,
                Location = new Point(18, 80),
                Size = new Size(384, 22)
            };

            _startButton = new Button
            {
                Text = "开启满速(降温除尘)",
                Location = new Point(18, 112),
                Size = new Size(186, 40),
                Font = font
            };
            _startButton.Click += (s, e) => StartFan(true);

            _stopButton = new Button
            {
                Text = "停止并恢复默认转速",
                Location = new Point(216, 112),
                Size = new Size(186, 40),
                Font = font
            };
            _stopButton.Click += (s, e) => StopFan(true);

            _autoStartCheck = new CheckBox
            {
                Text = "开机自启",
                Location = new Point(18, 166),
                Size = new Size(110, 24),
                Checked = IsAutoStart()
            };
            _autoStartCheck.CheckedChanged += (s, e) => SetAutoStart(_autoStartCheck.Checked);

            _hideButton = new Button
            {
                Text = "隐藏到托盘",
                Location = new Point(196, 162),
                Size = new Size(96, 30),
                Font = font
            };
            _hideButton.Click += (s, e) => HideToTray(true);

            _exitButton = new Button
            {
                Text = "退出",
                Location = new Point(300, 162),
                Size = new Size(102, 30),
                Font = font
            };
            _exitButton.Click += (s, e) => ExitApp();

            Controls.AddRange(new Control[]
            {
                title, _statusLabel, _detailLabel, _startButton, _stopButton,
                _autoStartCheck, _hideButton, _exitButton
            });

            RefreshStatus();
        }

        private void BuildTray()
        {
            _trayShowItem = new ToolStripMenuItem("显示窗口", null, (s, e) => ShowWindow());
            _trayStartItem = new ToolStripMenuItem("开启满速", null, (s, e) => StartFan(true));
            _trayStopItem = new ToolStripMenuItem("停止并恢复默认转速", null, (s, e) => StopFan(true));
            _trayStatusItem = new ToolStripMenuItem("状态：查询中…", null, (s, e) => RefreshStatus());
            var trayAutoItem = new ToolStripMenuItem("开机自启", null, (s, e) =>
            {
                var item = (ToolStripMenuItem)s;
                item.Checked = !item.Checked;
                SetAutoStart(item.Checked);
                if (_autoStartCheck != null) _autoStartCheck.Checked = item.Checked;
            }) { Checked = IsAutoStart() };
            var trayExitItem = new ToolStripMenuItem("退出(并恢复默认)", null, (s, e) => ExitApp());

            var menu = new ContextMenuStrip();
            menu.Items.Add(_trayShowItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_trayStatusItem);
            menu.Items.Add(_trayStartItem);
            menu.Items.Add(_trayStopItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(trayAutoItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(trayExitItem);

            _tray = new NotifyIcon
            {
                Icon = LoadIcon(16, ref _iconSmall),
                Text = "联想降温除尘",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (s, e) => ShowWindow();
            _tray.BalloonTipClicked += (s, e) => ShowWindow();
            _tray.Tag = trayAutoItem;
        }

        /// <summary>资源管理器重启后托盘图标会被清掉，必须重建。</summary>
        private void RecreateTray()
        {
            try
            {
                var autoItem = _tray != null ? _tray.Tag as ToolStripMenuItem : null;
                var menu = _tray != null ? _tray.ContextMenuStrip : null;
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
                _tray = new NotifyIcon
                {
                    Icon = LoadIcon(16, ref _iconSmall),
                    Text = "联想降温除尘",
                    Visible = true,
                    ContextMenuStrip = menu
                };
                _tray.DoubleClick += (s, e) => ShowWindow();
                _tray.BalloonTipClicked += (s, e) => ShowWindow();
                _tray.Tag = autoItem;
                Log("资源管理器重启，托盘图标已重建");
            }
            catch (Exception ex)
            {
                Log("重建托盘图标失败: " + ex.Message);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_TASKBARCREATED) RecreateTray();
            base.WndProc(ref m);
        }

        // ------------------------------------------------------------------
        // 行为
        // ------------------------------------------------------------------
        private void ShowWindow()
        {
            try
            {
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
                BringToFront();
            }
            catch (Exception ex) { Log("显示窗口失败: " + ex.Message); }
        }

        private void HideToTray(bool notify)
        {
            try
            {
                Hide();
                if (notify && _tray != null)
                {
                    string extra = _fan.Enabled
                        ? "风扇仍在满速运行。双击托盘图标可再次打开窗口。"
                        : "双击托盘图标可再次打开窗口。";
                    _tray.ShowBalloonTip(4000, "已最小化到托盘", extra, ToolTipIcon.Info);
                }
            }
            catch (Exception ex) { Log("隐藏失败: " + ex.Message); }
        }

        private void StartFan(bool showBalloon)
        {
            if (_fan.Enabled) { RefreshStatus(); return; }
            try
            {
                _fan.Enable();
                _autoResume = true;
                Log("Enable 成功");
                if (showBalloon && _tray != null)
                {
                    _tray.ShowBalloonTip(3000, "降温除尘已满速",
                        "注：除尘有 9+2s 固件间隔，软件无法消除", ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                Log("Enable 失败: " + ex.Message);
                string hint = _elevatedAtStart
                    ? ex.Message
                    : ex.Message + "\n\n提示：当前没有管理员权限，请右键「以管理员身份运行」后重试。";
                if (showBalloon)
                {
                    MessageBox.Show(this, hint, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else if (_tray != null)
                {
                    _tray.ShowBalloonTip(5000, "启动失败", hint, ToolTipIcon.Error);
                }
            }
            RefreshStatus();
        }

        private void StopFan(bool notify)
        {
            if (!_fan.Enabled)
            {
                // 即使程序自认为没开，也向设备发一次"正常档"，避免状态不一致时停不下来
                try { _fan.Restore(); } catch { }
                if (notify)
                {
                    MessageBox.Show(this, "当前已是默认转速。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                RefreshStatus();
                return;
            }
            try
            {
                _fan.Disable();
                _autoResume = false;
                Log("Disable 成功，已恢复默认转速");
                if (notify && _tray != null)
                {
                    _tray.ShowBalloonTip(2500, "已恢复默认转速",
                        "EnergyDrv 已切回正常模式", ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                Log("Disable 失败: " + ex.Message);
                if (notify)
                {
                    MessageBox.Show(this, "恢复默认转速失败: " + ex.Message, "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            try
            {
                var st = _fan.GetStatus();
                string state = st.State == FanState.Spinning ? "满速运行中"
                             : st.State == FanState.Idle ? "正常转速"
                             : "未知";
                if (_statusLabel != null)
                {
                    _statusLabel.Text = "状态：" + state;
                    _statusLabel.ForeColor = st.State == FanState.Spinning
                        ? Color.FromArgb(200, 60, 30)
                        : Color.FromArgb(30, 120, 60);
                }
                if (_detailLabel != null)
                {
                    _detailLabel.Text = st.ReadOk
                        ? string.Format("读数=0x{0:X2}({0})   设备={1}{2}",
                            st.Raw, _fan.DeviceName ?? "-", st.Calibrated ? "   已校准" : "")
                        : "无法读取设备状态（联想电脑管家是否在运行？）";
                }

                bool running = _fan.Enabled;
                if (_startButton != null) _startButton.Enabled = !running;
                if (_stopButton != null) _stopButton.Enabled = true;   // 始终可点，保证一定能停
                if (_trayStartItem != null) _trayStartItem.Enabled = !running;
                if (_trayStatusItem != null) _trayStatusItem.Text = "状态：" + state;
                if (_tray != null)
                {
                    _tray.Text = running ? "联想降温除尘(满速中)" : "联想降温除尘(已停止)";
                }
            }
            catch (Exception ex)
            {
                Log("刷新状态失败: " + ex.Message);
                if (_statusLabel != null) _statusLabel.Text = "状态：读取错误";
            }
        }

        private void ExitApp()
        {
            _allowClose = true;
            StopFan(false);
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose)
            {
                // 点 X 不退出，缩到托盘，避免用户以为已停止而风扇仍在转
                e.Cancel = true;
                HideToTray(true);
                return;
            }

            if (_exiting) { base.OnFormClosing(e); return; }
            _exiting = true;

            try { _refreshTimer.Stop(); _refreshTimer.Dispose(); } catch { }
            try { Microsoft.Win32.SystemEvents.PowerModeChanged -= _powerHandler; } catch { }
            try { _fan.Dispose(); } catch { }          // 内部会 Disable，恢复默认转速
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            try
            {
                if (_mutex != null) { _mutex.ReleaseMutex(); _mutex.Dispose(); _mutex = null; }
            }
            catch { }
            Log("退出，已恢复默认转速");

            base.OnFormClosing(e);
        }

        // ------------------------------------------------------------------
        // 开机自启
        // ------------------------------------------------------------------
        private static bool IsAutoStart()
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
            {
                return key != null && key.GetValue(AppName) != null;
            }
        }

        private static void SetAutoStart(bool enable)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    if (enable)
                    {
                        // 路径必须加引号，否则装在含空格目录下会启动失败
                        key.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                    }
                    else
                    {
                        key.DeleteValue(AppName, false);
                    }
                }
            }
            catch (Exception ex) { Log("设置开机自启失败: " + ex.Message); }
        }
    }
}
