using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace LenovoCoolBoost
{
    /// <summary>
    /// 风扇控制器（后端 = Lenovo EnergyDrv 设备）。
    /// 说明：本机(小新16 IRL8)实测，EC 没有可被直接写入的"风扇转速寄存器"
    /// （早期误判的 0xFE 在触发除尘时仍为 0x00，属无效寄存器），因此改用联想
    /// 电脑管家底层使用的 EnergyDrv 设备 IOCTL 来控制风扇：
    ///   控制：DeviceIoControl(h, 0x831020C0, {6,1,mode}, ...)  mode=1 最大/0 正常
    ///   读状态：DeviceIoControl(h, 0x831020C4, {14}, out)      与基准值比较判断档位
    /// 注意：EnergyDrv「启动除尘」的 9s高速+2s暂停是 EC 固件写死的周期，软件无法消除；
    /// 本类用后台循环在掉速时重置计时并重新开启，使其尽量接近连续最大转速。
    /// 设备 \\.\EnergyDrv 仅在联想电脑管家/联想实用程序运行时存在，需管理员权限。
    /// </summary>
    public sealed class FanController : IDisposable
    {
        // ---------- Win32 ----------
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize,
            IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32")]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_RW = 0x00000003;
        private const uint OPEN_EXISTING = 0x00000003;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private static readonly IntPtr INVALID = new IntPtr(-1);

        private const uint IOCTL_CTRL = 0x831020C0;
        private const uint IOCTL_READ = 0x831020C4;

        // 保活节奏：循环间隔、false→true 边沿触发间隔、兜底强制重置周期(轮数)
        private const int LoopIntervalMs = 600;     // <2s：刷新命令频率高于固件 2s 暂停，消除 9+2 间隔
        private const int EdgeGapMs = 300;          // 边沿间隔加大，提高固件接受重触发的概率
        private const int ForceEdgeEveryLoops = 50; // ≈30s 无条件仿真"重启程序"一次，抢在固件会话结束前重排
        // 读数(索引14)判据：<100 = 强制生效(风扇在转)；>=100 = 空闲/停。
        // （实测：空闲≈133(0x85)，强制≈40(0x28)；早期误把 0x23/0x85 高低值方向判反。）
        private const int ArmIdleThreshold = 100;

        // 多候选设备名：不同机型/驱动版本暴露名不同，依次探测第一个能打开的
        private static readonly string[] Devices =
            { @"\\.\EnergyDrv", @"\\.\Lenovo0", @"\\.\Lenovo1", @"\\.\AcpiVpc" };

        private readonly object _lock = new object();
        private Thread _thread;
        private CancellationTokenSource _cts;
        private bool _enabled;
        private uint? _normalBase;   // 正常档读回值（动态校准，仅参考）
        private uint? _fastValue;     // 最大档读回值（动态校准，UI 判定是否满速的依据）
        public Action<string> Logger { get; set; } = _ => { };  // 看门狗日志钩子（默认无操作）

        public bool Enabled => _enabled;
        public bool DriverReady
        {
            get
            {
                IntPtr h = TryOpen(false);
                if (h != INVALID) { CloseHandle(h); return true; }
                return false;
            }
        }

        public enum FanMode { Normal, Full }

        // ------------------------------------------------------------------
        // 设备打开（自动探测）
        // ------------------------------------------------------------------
        private IntPtr TryOpen(bool write)
        {
            uint access = write ? GENERIC_WRITE : GENERIC_READ;
            foreach (var dev in Devices)
            {
                IntPtr h = CreateFileW(dev, access, FILE_SHARE_RW, IntPtr.Zero,
                    OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                if (h != INVALID && h != IntPtr.Zero)
                    return h;
                if (h != INVALID) CloseHandle(h);
            }
            return INVALID;
        }

        private void SetMode(bool fast)
        {
            IntPtr h = TryOpen(true);
            if (h == INVALID) throw new Win32Exception(0, "无法打开联想风扇设备（EnergyDrv 等）。请确认联想电脑管家在运行且以管理员身份运行。");
            try
            {
                var buf = new int[] { 6, 1, fast ? 1 : 0 };
                int size = buf.Length * 4;
                IntPtr p = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.Copy(buf, 0, p, buf.Length);
                    uint br = 0;
                    bool ok = DeviceIoControl(h, IOCTL_CTRL, p, (uint)size, IntPtr.Zero, 0, out br, IntPtr.Zero);
                    if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "设置风扇模式失败");
                }
                finally { Marshal.FreeHGlobal(p); }
            }
            finally { CloseHandle(h); }
        }

        private bool ReadFastRaw(out uint value)
        {
            IntPtr h = TryOpen(false);
            if (h == INVALID) { value = 0; return false; }
            try
            {
                IntPtr inP = Marshal.AllocHGlobal(4);
                IntPtr outP = Marshal.AllocHGlobal(4);
                try
                {
                    Marshal.WriteInt32(inP, 14);
                    uint br = 0;
                    bool ok = DeviceIoControl(h, IOCTL_READ, inP, 4, outP, 4, out br, IntPtr.Zero);
                    if (!ok) { value = 0; return false; }
                    value = (uint)Marshal.ReadInt32(outP);
                    return true;
                }
                finally { Marshal.FreeHGlobal(inP); Marshal.FreeHGlobal(outP); }
            }
            finally { CloseHandle(h); }
        }

        private bool IsFast()
        {
            // 以真实读数(索引14)判断：低值=强制生效中(转)，高值/0=空闲(停)。不再依赖易失真的基准。
            uint v = ReadStatusRaw();
            return v != 0 && v < ArmIdleThreshold;
        }

        // ---- 诊断/软重启辅助（公开）----
        /// <summary>直接读取设备状态原始值（索引14），0 表示读取失败。>=100=空闲，<100=强制生效。</summary>
        public uint ReadStatusRaw()
        {
            if (ReadFastRaw(out uint v)) return v;
            return 0;
        }

        /// <summary>真实"强制生效(风扇在转)"判定：读数存在且 <100。</summary>
        public bool IsActive() => IsFast();

        /// <summary>触发/刷新一次除尘最大档（等价于 SetMode(true)）。</summary>
        public void Trigger() => SetMode(true);

        /// <summary>边沿重置：先正常→等待→最大。等价于程序重启时的 Enable 序列核心。</summary>
        public void ReArm()
        {
            try { SetMode(false); } catch { }
            Thread.Sleep(EdgeGapMs);
            SetMode(true);
        }

        // ------------------------------------------------------------------
        // 开 / 关
        // ------------------------------------------------------------------
        public void Enable()
        {
            if (_enabled) return;
            // 校准基准：先正常（记录正常档值），再最大（记录最大档特征）
            try { SetMode(false); } catch { }
            if (ReadFastRaw(out uint nb)) _normalBase = nb;
            SetMode(true);
            if (ReadFastRaw(out uint fv)) _fastValue = fv;

            _cts = new CancellationTokenSource();
            _enabled = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "FanBoostLoop" };
            _thread.Start();
        }

        public void Disable()
        {
            if (!_enabled) { try { SetMode(false); } catch { } return; }
            _cts?.Cancel();
            _cts = null;
            _enabled = false;
            try { SetMode(false); } catch { }
        }

        // 后台循环：周期性"边沿触发"保持最大档，规避固件自动复位后忽略重复最大命令而停转。
        // 策略：检测到掉速即 false→true 边沿重置；每 ForceEdgeEveryLoops 轮无条件再触发一次兜底；
        //       设备短暂不可用时仅记录并继续重试，不退出循环（避免"需重启程序才有效"）。
        private void Loop()
        {
            var token = _cts.Token;
            int failStreak = 0;
            int sinceForce = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        bool active = IsFast();   // 真实读数 <100 = 风扇在转
                        sinceForce++;
                        if (!active || sinceForce >= ForceEdgeEveryLoops)
                        {
                            // 边沿重置(正常→最大)，大间隔提升固件接受重触发概率；
                            // 每 ForceEdgeEveryLoops 轮无条件做一次，仿真"重启程序"抢在固件会话结束前重排。
                            SetMode(false);
                            Thread.Sleep(EdgeGapMs);
                            SetMode(true);
                            sinceForce = 0;
                        }
                        else
                        {
                            SetMode(true); // 高速相刷新命令，防止固件因无新命令而复位
                        }
                        failStreak = 0;
                    }
                    catch (Exception ex)
                    {
                        failStreak++;
                        if (failStreak == 1 || failStreak % 20 == 0)
                            LogL("循环异常(已忽略并继续，避免线程死亡导致永久停转): " + ex.GetType().Name + ": " + ex.Message);
                    }
                    token.WaitHandle.WaitOne(LoopIntervalMs);
                }
            }
            finally
            {
                try { SetMode(false); } catch { }
            }
        }

        private void LogL(string s) => Logger?.Invoke("[Loop] " + s);

        public FanMode CurrentMode() => IsFast() ? FanMode.Full : FanMode.Normal;

        /// <summary>供 UI 显示：满速返回 0x50，正常返回 0x00（仅作状态指示，非 EC 寄存器）。</summary>
        public byte RawValue() => (byte)(IsFast() ? 0x50 : 0x00);

        public void Dispose() => Disable();
    }
}
