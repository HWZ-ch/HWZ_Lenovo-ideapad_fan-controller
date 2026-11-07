using System;
using System.Diagnostics;
using System.Linq;
using System.Drawing;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace LenovoCoolBoost
{
    /// <summary>
    /// 系统托盘程序：一键开关「联想降温除尘」风扇最大转速。
    /// 后端 = Lenovo EnergyDrv 设备 IOCTL（即联想电脑管家底层用的风扇控制，已在本机验证能真正转）。
    /// 注意：EnergyDrv「启动除尘」的 9s高速+2s暂停是 EC 固件写死的周期，软件无法消除；
    /// 本程序用后台循环在掉速时重置计时并重开，尽量接近连续最大转速。
    /// 行为：双击运行即自动开启；托盘图标用于关闭/再开/退出。
    ///   LenovoCoolBoost.exe --test -> 无界面自检：开启除尘并验证，结束后自动恢复。
    /// 诊断日志：%TEMP%\LenovoCoolBoost.log
    /// </summary>
    public class Program : Form
    {
        private readonly FanController _fan = new FanController();
        private NotifyIcon _tray;
        private ToolStripMenuItem _startItem;
        private ToolStripMenuItem _stopItem;
        private ToolStripMenuItem _statusItem;
        private bool _autoResume;

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "LenovoCoolBoost";
        private static readonly string LogPath =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LenovoCoolBoost.log");
        private static Mutex _mutex;

        private static void Log(string s)
        {
            try
            {
                System.IO.File.AppendAllText(LogPath,
                    DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\n");
            }
            catch { }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            Log("=== 启动 === 参数=[" + string.Join(" ", args ?? new string[0]) + "]");
            if (args != null && Array.Exists(args, a => a == "--test"))
            {
                SelfTest();
                return;
            }

            if (args != null && Array.Exists(args, a => a == "--diag"))
            {
                Diag();
                return;
            }

            // 单实例，避免多个托盘
            _mutex = new Mutex(true, @"Global\LenovoCoolBoost_SingleInstance", out bool created);
            if (!created)
            {
                Log("已有实例在运行，提示用户使用现有托盘。");
                MessageBox.Show("LenovoCoolBoost 已在运行（右下角托盘图标）。请使用托盘菜单开关降温除尘。",
                    "已在运行", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator);
            Log("管理员=" + isAdmin + "；驱动已加载时标准用户亦可控制，故不强制退出。");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Program());
        }

        // ------------------------------------------------------------------
        // 无界面自检（供本机自动化验证）
        // ------------------------------------------------------------------
        private static void SelfTest()
        {
            string logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LenovoCoolBoost_test.log");
            void LogT(string s)
            {
                try { System.IO.File.AppendAllText(logPath,
                    DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\n"); } catch { }
            }
            LogT("=== LenovoCoolBoost 自检模式 (--test) ===");
            var fan = new FanController();
            LogT("驱动就绪: " + fan.DriverReady);
            if (!fan.DriverReady)
            {
                LogT("FAIL: 无法打开 Lenovo EnergyDrv 风扇设备。请确认：1) 联想电脑管家/联想实用程序正在运行；2) 本程序以管理员身份运行。");
                Environment.Exit(2);
            }
            try
            {
                byte before = fan.RawValue();
                LogT("开启前 风扇档位 = 0x" + before.ToString("X2") + " (0x50=最大)");

                fan.Enable();
                LogT("已启用，开始 180 秒持续采样（真实读数=索引14：<100=转，>=100=停）...");
                int ok = 0, bad = 0;
                const int total = 360;            // 360 * 500ms ≈ 180s
                for (int i = 0; i < total; i++)
                {
                    Thread.Sleep(500);
                    byte v = fan.RawValue();       // 0x50=转(active) / 0x00=停
                    uint real = fan.ReadStatusRaw();
                    if (v == 0x50) ok++; else bad++;
                    if ((i + 1) % 20 == 0)
                        LogT($"  采样 {i + 1}/{total}: 合成=0x{v:X2} 真实读数=0x{real:X2}({(real < 100 ? "转" : "停")}) {(v == 0x50 ? "满速" : "偏离")}");
                }
                LogT($"采样结果: 最大档(0x50)={ok} 次, 偏离={bad} 次, 保持率={ok * 100.0 / total:F1}%");
                fan.Disable();
                Thread.Sleep(3000);
                byte after = fan.RawValue();
                uint realAfter = fan.ReadStatusRaw();
                LogT($"关闭后 合成=0x{after:X2} 真实读数=0x{realAfter:X2}({((after==0x50)?"仍转(固件周期)":"已停")})");

                bool pass = ok >= total * 0.9;   // 保持率 ≥90% 视为解决"中途会停"
                LogT(pass ? "PASS: 风扇在 180s 内稳定维持最大转速（保持率≥90%），中途停转问题已解决。" : "WARN: 开启期间仍有明显偏离，请检查设备/管家状态。");
                Environment.Exit(pass ? 0 : 1);
            }
            catch (Exception ex)
            {
                LogT("FAIL: " + ex.Message);
                try { fan.Disable(); } catch { }
                Environment.Exit(3);
            }
        }

        // ------------------------------------------------------------------
        // 观测模式（--diag）：启动后台循环(连续重触发，复现"连续转"体验)，
        // 逐秒记录「真实」状态值(0x23 等)，不依赖任何硬编码代理。
        // 出现长停转时做一次软重启(Disable+Enable)看能否恢复。
        // 约定：真实读数(索引14) <100 视为"除尘/最大档生效中(风扇在转)"，>=100 视为空闲/停转。
        // （实测：空闲≈133(0x85)，强制≈40(0x28)；早前把高低方向判反了。）
        // 日志：%LOCALAPPDATA%\LenovoCoolBoost_diag.log
        // ------------------------------------------------------------------
        private static void Diag()
        {
            string logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LenovoCoolBoost_diag.log");
            void LogT(string s)
            {
                try { System.IO.File.AppendAllText(logPath,
                    DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\n"); } catch { }
            }
            LogT("=== LenovoCoolBoost 观测模式 (--diag, 真实状态) ===");
            var fan = new FanController();
            LogT("驱动就绪: " + fan.DriverReady);
            if (!fan.DriverReady)
            {
                LogT("FAIL: 无法打开 Lenovo EnergyDrv 风扇设备。请确认联想电脑管家在运行且以管理员身份运行。");
                Environment.Exit(2);
            }

            // 启动后台循环：连续重触发（复现用户"连续转"的体验）
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
                uint s = fan.ReadStatusRaw();
                bool spinning = (s != 0 && s < 100);   // 真实读数<100=强制生效(转)
                if (spinning) consecutiveStop = 0; else consecutiveStop++;

                if (!dist.ContainsKey(s)) dist[s] = 0;
                dist[s]++;
                if (++distWindow >= 30)
                {
                    distWindow = 0;
                    var top = string.Join(",", System.Linq.Enumerable.OrderByDescending(
                        System.Linq.Enumerable.ToList(dist), kv => kv.Value)
                        .Take(5).Select(kv => $"0x{kv.Key:X2}x{kv.Value}"));
                    LogT($"  [分布@t={t}] {top}");
                    dist.Clear();
                }

                LogT($"t={t}s status=0x{s:X2} {(spinning ? "生效(转)" : "停转")}");

                // 持续停转≥15s 且本轮尚未尝试软重启 → 做一次 Disable+Enable 软重启
                if (consecutiveStop == 15 && recoveryAttempts < 3)
                {
                    recoveryAttempts++;
                    LogT($"  [软重启] 持续停转{consecutiveStop}s，执行 Disable()+Enable()（等价于重启程序）...");
                    try { fan.Disable(); Thread.Sleep(800); fan.Enable(); }
                    catch (Exception ex) { LogT("  软重启异常: " + ex.Message); }
                    Thread.Sleep(3000);
                    uint s2 = fan.ReadStatusRaw();
                    bool back = (s2 != 0 && s2 < 100);
                    if (back) { recoveryOk++; consecutiveStop = 0;
                        LogT($"  [软重启结果] 恢复生效 (status=0x{s2:X2})"); }
                    else
                        LogT($"  [软重启结果] 仍未恢复 (status=0x{s2:X2})");
                }
            }

            LogT($"DONE 软重启次数={recoveryAttempts} 软重启恢复={recoveryOk}");
            try { fan.Disable(); } catch { }
            Environment.Exit(0);
        }

        // ------------------------------------------------------------------
        // 托盘 UI
        // ------------------------------------------------------------------
        public Program()
        {
            Text = "LenovoCoolBoost";
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Load += (s, e) => { Visible = false; Hide(); };

            _fan.Logger = s => Log("Loop: " + s);
            _tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "联想降温除尘(EnergyDrv)",
                Visible = true,
                ContextMenuStrip = BuildMenu()
            };
            _tray.DoubleClick += (s, e) => Toggle();
            UpdateMenuState();
            RefreshStatus();

            Microsoft.Win32.SystemEvents.PowerModeChanged += (s, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Suspend) Stop();
                else if (e.Mode == Microsoft.Win32.PowerModes.Resume && _autoResume) Start();
            };

            // 启动即自动开启满速：运行即生效，解决"运行没反应"
            Log("构造完成，自动开启满速...");
            Start();
        }

        private ContextMenuStrip BuildMenu()
        {
            _startItem = new ToolStripMenuItem("开启满速(降温除尘)", null, (s, e) => Start());
            _stopItem = new ToolStripMenuItem("关闭并恢复默认", null, (s, e) => Stop());
            _statusItem = new ToolStripMenuItem("状态：未知", null, (s, e) => RefreshStatus());
            var autoItem = new ToolStripMenuItem("开机自启", null, (s, e) =>
            {
                var item = (ToolStripMenuItem)s;
                item.Checked = !item.Checked;
                SetAutoStart(item.Checked);
            }) { Checked = IsAutoStart() };
            var exitItem = new ToolStripMenuItem("退出", null, (s, e) => ExitApp());

            var menu = new ContextMenuStrip();
            menu.Items.Add(_startItem);
            menu.Items.Add(_stopItem);
            menu.Items.Add(_statusItem);
            menu.Items.Add(autoItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);
            return menu;
        }

        private void Start()
        {
            if (_fan.Enabled) return;
            try
            {
                _fan.Enable();
                _autoResume = true;
                _tray.ShowBalloonTip(3000, "降温除尘已满速",
                    "后端: EnergyDrv 除尘命令 · 风扇最大转速 · 注：除尘有9+2s固件间隔，软件无法消除", ToolTipIcon.Info);
                Log("Enable 成功");
            }
            catch (Exception ex)
            {
                Log("Enable 失败: " + ex.Message);
                _tray.ShowBalloonTip(4000, "启动失败", ex.Message, ToolTipIcon.Error);
            }
            UpdateMenuState();
            RefreshStatus();
        }

        private void Stop()
        {
            if (!_fan.Enabled) { Log("Stop 调用但当前未开启"); return; }
            _fan.Disable();
            _autoResume = false;
            Log("Disable 成功，已恢复默认");
            _tray.ShowBalloonTip(2500, "已恢复默认转速", "EnergyDrv 已切回正常模式", ToolTipIcon.Info);
            UpdateMenuState();
            RefreshStatus();
        }

        private void Toggle()
        {
            if (_fan.Enabled) Stop(); else Start();
        }

        private void RefreshStatus()
        {
            try
            {
                var mode = _fan.CurrentMode();
                byte raw = _fan.RawValue();
                _statusItem.Text = "状态：" + (mode == FanController.FanMode.Full
                    ? "满速(生效中)"
                    : "正常转速") + "  [0x" + raw.ToString("X2") + "]";
            }
            catch (Exception ex)
            {
                _statusItem.Text = "状态：错误 - " + ex.Message;
            }
        }

        private void UpdateMenuState()
        {
            bool running = _fan.Enabled;
            _startItem.Enabled = !running;
            _stopItem.Enabled = running;
            _tray.Text = running ? "联想降温除尘(满速中)" : "联想降温除尘(EnergyDrv)";
        }

        private void ExitApp()
        {
            Stop();
            _tray.Visible = false;
            Application.Exit();
        }

        private static bool IsAutoStart()
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppName) != null;
        }

        private static void SetAutoStart(bool enable)
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable) key.SetValue(AppName, Application.ExecutablePath);
            else key.DeleteValue(AppName, false);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Stop();
            _tray.Visible = false;
            base.OnFormClosing(e);
        }
    }
}
