using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace LenovoCoolBoost
{
    /// <summary>
    /// 系统托盘程序：一键开关「联想降温除尘」风扇最大转速。
    /// 后端 = Lenovo EnergyDrv 设备 IOCTL（即联想电脑管家底层用的风扇控制）。
    /// 注意：EnergyDrv「启动除尘」的 9s高速+2s暂停是 EC 固件写死的周期，软件无法消除；
    /// 本程序用后台循环在掉速时重置计时并重开，尽量接近连续最大转速。
    ///
    /// 命令行：
    ///   (无参数)      启动托盘，双击即自动开启满速
    ///   --status      打印当前风扇状态后退出（只读，不会改变风扇状态）
    ///   --off         一次性恢复默认转速后退出（可用于善后，不影响托盘实例）
    ///   --test        无界面自检：开启并采样 180 秒，结束自动恢复
    ///   --diag        观测模式：逐秒记录真实读数，长停转时尝试软重启
    ///   --no-elevate  以普通权限运行，不尝试 UAC 提权
    ///
    /// 诊断日志：%TEMP%\LenovoCoolBoost.log
    /// </summary>
    public class Program : Form
    {
        private readonly FanController _fan = new FanController();
        private NotifyIcon _tray;
        private ToolStripMenuItem _startItem;
        private ToolStripMenuItem _stopItem;
        private ToolStripMenuItem _statusItem;
        private Microsoft.Win32.PowerModeChangedEventHandler _powerHandler;
        private bool _autoResume;
        private bool _elevatedAtStart;
        private bool _exiting;

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "LenovoCoolBoost";
        private static readonly string LogPath =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LenovoCoolBoost.log");
        private static Mutex _mutex;

        [DllImport("kernel32")]
        private static extern bool AttachConsole(int dwProcessId);

        private static void Log(string s)
        {
            try
            {
                System.IO.File.AppendAllText(LogPath,
                    DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\n");
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
            return args != null && Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// WinExe 是 GUI 子系统，默认没有控制台；命令行模式下挂到父进程控制台才能看到输出。
        /// 这里只做 AttachConsole，不碰 Console.Out / Console.OutputEncoding：
        /// .NET 会自然跟随当前控制台代码页（中文 Windows 为 GBK、UTF-8 终端为 65001），
        /// 两种情况都正确。自己包一层 StreamWriter 反而会在重定向场景下把编码固定成 UTF-8，
        /// 与终端不一致而产生乱码。
        /// </summary>
        private static void AttachParentConsole()
        {
            try { AttachConsole(-1); } catch { }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            Log("=== 启动 === 参数=[" + string.Join(" ", args ?? new string[0]) + "]");

            if (HasArg(args, "--status")) { AttachParentConsole(); CmdStatus(); return; }
            if (HasArg(args, "--off")) { AttachParentConsole(); CmdRestore(); return; }
            if (HasArg(args, "--test")) { SelfTest(); return; }
            if (HasArg(args, "--diag")) { Diag(); return; }

            // 自动提权：EnergyDrv 需要管理员权限才能打开，否则用户只会看到"启动失败"
            if (!IsAdmin() && !HasArg(args, "--no-elevate"))
            {
                if (TryRelaunchElevated(args))
                {
                    Log("已请求 UAC 提权，当前实例退出。");
                    return;
                }
                Log("UAC 提权被取消或失败，以普通权限继续运行。");
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

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Program());
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
        // --status：只读查询
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

        // ------------------------------------------------------------------
        // --off：一次性恢复默认转速（不影响正在运行的托盘实例）
        // ------------------------------------------------------------------
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
        // 无界面自检（供自动化验证）
        // ------------------------------------------------------------------
        private static void SelfTest()
        {
            string logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LenovoCoolBoost_test.log");
            void LogT(string s)
            {
                try
                {
                    System.IO.File.AppendAllText(logPath,
                        DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\n");
                }
                catch { }
            }
            LogT("=== LenovoCoolBoost 自检模式 (--test) ===");
            var fan = new FanController();
            LogT("驱动就绪: " + fan.DriverReady + "  设备: " + (fan.DeviceName ?? "(无)"));
            if (!fan.DriverReady)
            {
                LogT("FAIL: 无法打开 Lenovo EnergyDrv 风扇设备。请确认：1) 联想电脑管家/联想实用程序正在运行；2) 本程序以管理员身份运行。");
                Environment.Exit(2);
            }
            try
            {
                var before = fan.GetStatus();
                LogT("开启前: " + before.Describe());

                fan.Enable();
                LogT("已启用，开始 180 秒持续采样...");
                int ok = 0, bad = 0;
                const int total = 360;            // 360 * 500ms ≈ 180s
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
                var after = fan.GetStatus();
                LogT("关闭后: " + after.Describe());

                bool pass = ok >= total * 0.9;   // 保持率 ≥90% 视为解决"中途会停"
                LogT(pass
                    ? "PASS: 风扇在 180s 内稳定维持最大转速（保持率≥90%），中途停转问题已解决。"
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
        // 观测模式（--diag）
        // ------------------------------------------------------------------
        private static void Diag()
        {
            string logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LenovoCoolBoost_diag.log");
            void LogT(string s)
            {
                try
                {
                    System.IO.File.AppendAllText(logPath,
                        DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\n");
                }
                catch { }
            }
            LogT("=== LenovoCoolBoost 观测模式 (--diag) ===");
            var fan = new FanController();
            LogT("驱动就绪: " + fan.DriverReady + "  设备: " + (fan.DeviceName ?? "(无)"));
            if (!fan.DriverReady)
            {
                LogT("FAIL: 无法打开 Lenovo EnergyDrv 风扇设备。请确认联想电脑管家在运行且以管理员身份运行。");
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

                // 持续停转≥15s 且本轮尚未尝试软重启 → 做一次 Disable+Enable 软重启
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
        // 托盘 UI
        // ------------------------------------------------------------------
        public Program()
        {
            _elevatedAtStart = IsAdmin();

            Text = "LenovoCoolBoost";
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Load += (s, e) => { Visible = false; Hide(); };

            _fan.Logger = s => Log(s);

            _tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "联想降温除尘(EnergyDrv)",
                Visible = true,
                ContextMenuStrip = BuildMenu()
            };
            _tray.DoubleClick += (s, e) => Toggle();

            _powerHandler = (s, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Suspend) Stop();
                else if (e.Mode == Microsoft.Win32.PowerModes.Resume && _autoResume) Start();
            };
            Microsoft.Win32.SystemEvents.PowerModeChanged += _powerHandler;

            UpdateMenuState();
            RefreshStatus();

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
                    "后端: EnergyDrv 除尘命令 · 风扇最大转速 · 注：除尘有 9+2s 固件间隔，软件无法消除",
                    ToolTipIcon.Info);
                Log("Enable 成功");
            }
            catch (Exception ex)
            {
                Log("Enable 失败: " + ex.Message);
                string hint = _elevatedAtStart
                    ? ex.Message
                    : ex.Message + "\n\n提示：本程序当前没有管理员权限，右键选择「以管理员身份运行」后重试。";
                _tray.ShowBalloonTip(5000, "启动失败", hint, ToolTipIcon.Error);
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
                var st = _fan.GetStatus();
                _statusItem.Text = "状态：" + st.Describe();
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
            CleanupAndExit();
        }

        private void CleanupAndExit()
        {
            // 防止 OnFormClosing 里调用 Application.Exit() 再次触发 FormClosing 造成递归
            if (_exiting) return;
            _exiting = true;

            try { Microsoft.Win32.SystemEvents.PowerModeChanged -= _powerHandler; } catch { }
            try { _fan.Dispose(); } catch { }
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            try
            {
                if (_mutex != null)
                {
                    _mutex.ReleaseMutex();
                    _mutex.Dispose();
                    _mutex = null;
                }
            }
            catch { }
            Application.Exit();
        }

        private static bool IsAutoStart()
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
            {
                return key != null && key.GetValue(AppName) != null;
            }
        }

        private static void SetAutoStart(bool enable)
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null) return;
                if (enable)
                {
                    // 关键修复：路径必须加引号，否则装在 "Program Files" 这类含空格目录下会启动失败
                    key.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                }
                else
                {
                    key.DeleteValue(AppName, false);
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            CleanupAndExit();
            base.OnFormClosing(e);
        }
    }
}
