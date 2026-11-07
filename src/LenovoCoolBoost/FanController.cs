using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace LenovoCoolBoost
{
    /// <summary>风扇档位状态。</summary>
    public enum FanState
    {
        Unknown = 0,
        Idle,
        Spinning
    }

    /// <summary>一次状态快照，供 UI / 日志使用（避免多次读取产生不一致）。</summary>
    public sealed class FanStatus
    {
        public bool DeviceReady { get; set; }
        public bool ReadOk { get; set; }
        public uint Raw { get; set; }
        public FanState State { get; set; }
        public bool Calibrated { get; set; }
        public double Threshold { get; set; }

        public string Describe()
        {
            if (!DeviceReady) return "驱动未就绪（联想电脑管家是否在运行？）";
            if (!ReadOk) return "读数失败";
            string s;
            switch (State)
            {
                case FanState.Spinning: s = "满速(生效中)"; break;
                case FanState.Idle: s = "正常转速"; break;
                default: s = "未知"; break;
            }
            return string.Format("{0}  读数=0x{1:X2}({1}){2}", s, Raw, Calibrated ? " [已校准]" : " [默认阈值]");
        }
    }

    /// <summary>
    /// 风扇控制器（后端 = Lenovo EnergyDrv 设备）。
    /// 说明：本机(小新16 IRL8)实测，EC 没有可被直接写入的"风扇转速寄存器"
    /// （早期误判的 0xFE 在触发除尘时仍为 0x00，属无效寄存器），因此改用联想
    /// 电脑管家底层使用的 EnergyDrv 设备 IOCTL 来控制风扇：
    ///   控制：DeviceIoControl(h, 0x831020C0, {6,1,mode}, ...)  mode=1 最大/0 正常
    ///   读状态：DeviceIoControl(h, 0x831020C4, {14}, out)      与阈值比较判断档位
    /// 注意：EnergyDrv「启动除尘」的 9s高速+2s暂停是 EC 固件写死的周期，软件无法消除；
    /// 本类用后台循环在掉速时重置计时并重新开启，使其尽量接近连续最大转速。
    /// 设备 \\.\EnergyDrv 仅在联想电脑管家/联想实用程序运行时存在，需管理员权限。
    ///
    /// v1.1 改进：
    ///   * 设备句柄复用（原实现每次读写都 CreateFile/CloseHandle，600ms 一次）；
    ///   * Disable() 会等待后台线程真正退出，修掉"点了关闭风扇又自己转起来"的竞态；
    ///   * 阈值自适应校准：开机时采样"正常档/最大档"两个读数取中点，
    ///     替代原先写死的 100。读数方向也自动判定，兼容高低方向相反的机型；
    ///   * 去掉合成的假读数，全部使用设备真实返回值。
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
        private const int STATUS_INDEX = 14;

        // 保活节奏：循环间隔、边沿触发间隔、兜底强制重置周期(轮数)
        private const int DefaultLoopIntervalMs = 600;     // <2s：刷新频率高于固件 2s 暂停
        private const int EdgeGapDefaultMs = 300;          // false→true 边沿间隔
        private const int ForceEdgeEveryLoops = 50;        // ≈30s 无条件重排一次
        private const int CalibrationSettleMs = 250;       // 校准时等 EC 稳定的最短时间
        private const int CalibrationTimeoutMs = 1800;     // 单次采样最长等待
        private const int PollSliceMs = 80;                // 采样轮询间隔

        // 未校准时使用的兜底值：本机实测空闲≈0x85(133)、强制≈0x28(40)，取中点 100 以下判为在转。
        private const double FallbackThreshold = 100.0;

        // 多候选设备名：不同机型/驱动版本暴露名不同，依次探测第一个能打开的
        private static readonly string[] Devices =
            { @"\\.\EnergyDrv", @"\\.\Lenovo0", @"\\.\Lenovo1", @"\\.\AcpiVpc" };

        private readonly object _ioLock = new object();
        private readonly object _lifecycleLock = new object();

        private IntPtr _handle = INVALID;
        private bool _canWrite;
        private string _deviceName;
        private IntPtr _inBuf = IntPtr.Zero;   // 12 字节：控制命令入参
        private IntPtr _rdBuf = IntPtr.Zero;   // 4 字节：读状态入参
        private IntPtr _outBuf = IntPtr.Zero;  // 4 字节：读状态出参

        private Thread _thread;
        private CancellationTokenSource _cts;
        private volatile bool _enabled;

        // 校准结果
        private bool _calibrated;
        private bool _fastIsLower = true;      // 最大档读数是否小于正常档
        private double _threshold = FallbackThreshold;

        /// <summary>看门狗日志钩子（默认无操作）。</summary>
        public Action<string> Logger { get; set; } = _ => { };

        /// <summary>保活循环间隔（毫秒），默认 600。</summary>
        public int LoopIntervalMs { get; set; } = DefaultLoopIntervalMs;

        /// <summary>边沿重置时"正常→最大"的间隔（毫秒），默认 300。</summary>
        public int EdgeGapMs { get; set; } = EdgeGapDefaultMs;

        /// <summary>状态变化时触发（仅在档位真正改变时回调）。</summary>
        public event Action<FanState> StateChanged;

        public bool Enabled { get { return _enabled; } }

        /// <summary>探测到的设备名，未打开时为 null。</summary>
        public string DeviceName { get { return _deviceName; } }

        public FanController()
        {
            _inBuf = Marshal.AllocHGlobal(12);
            _rdBuf = Marshal.AllocHGlobal(4);
            _outBuf = Marshal.AllocHGlobal(4);
        }

        // ------------------------------------------------------------------
        // 设备句柄管理（复用 + 失联自动重开）
        // ------------------------------------------------------------------
        private IntPtr EnsureHandle(bool forWrite)
        {
            lock (_ioLock)
            {
                if (_handle != INVALID && (!forWrite || _canWrite)) return _handle;
                if (_handle != INVALID) { CloseHandle(_handle); _handle = INVALID; }

                foreach (var dev in Devices)
                {
                    // 优先读写权限
                    IntPtr h = CreateFileW(dev, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                        IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                    if (h != INVALID && h != IntPtr.Zero)
                    {
                        _handle = h; _canWrite = true; _deviceName = dev; return h;
                    }
                    // 退回只读（只能查询状态，不能开关）
                    h = CreateFileW(dev, GENERIC_READ, FILE_SHARE_RW,
                        IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                    if (h != INVALID && h != IntPtr.Zero)
                    {
                        _deviceName = dev;
                        if (!forWrite) { _handle = h; _canWrite = false; return h; }
                        CloseHandle(h);   // 需要写权限，这个句柄没用
                    }
                }
                _deviceName = null;
                return INVALID;
            }
        }

        private void DropHandle()
        {
            lock (_ioLock)
            {
                if (_handle != INVALID) { CloseHandle(_handle); _handle = INVALID; }
            }
        }

        /// <summary>驱动是否可用（能打开设备即视为可用）。</summary>
        public bool DriverReady
        {
            get { return EnsureHandle(false) != INVALID; }
        }

        // ------------------------------------------------------------------
        // 底层 IOCTL
        // ------------------------------------------------------------------
        private bool TryWriteMode(bool fast, out string error)
        {
            lock (_ioLock)
            {
                IntPtr h = EnsureHandle(true);
                if (h == INVALID)
                {
                    error = "无法打开联想风扇设备（已尝试 EnergyDrv/Lenovo0/Lenovo1/AcpiVpc）。"
                          + "请确认联想电脑管家正在运行，且本程序以管理员身份运行。";
                    return false;
                }
                try
                {
                    Marshal.WriteInt32(_inBuf, 0, 6);
                    Marshal.WriteInt32(_inBuf, 4, 1);
                    Marshal.WriteInt32(_inBuf, 8, fast ? 1 : 0);
                    uint br;
                    if (!DeviceIoControl(h, IOCTL_CTRL, _inBuf, 12, IntPtr.Zero, 0, out br, IntPtr.Zero))
                    {
                        int code = Marshal.GetLastWin32Error();
                        DropHandle();   // 句柄可能已失效（管家重启），下次重开
                        error = "设置风扇模式失败，Win32 错误码 " + code;
                        return false;
                    }
                    error = null;
                    return true;
                }
                catch (Exception ex)
                {
                    DropHandle();
                    error = ex.Message;
                    return false;
                }
            }
        }

        private void WriteMode(bool fast)
        {
            string err;
            if (!TryWriteMode(fast, out err)) throw new Win32Exception(0, err);
            SetState(fast ? FanState.Spinning : FanState.Idle);
        }

        private bool TryRead(out uint value)
        {
            lock (_ioLock)
            {
                value = 0;
                IntPtr h = EnsureHandle(false);
                if (h == INVALID) return false;
                try
                {
                    Marshal.WriteInt32(_rdBuf, 0, STATUS_INDEX);
                    uint br;
                    if (!DeviceIoControl(h, IOCTL_READ, _rdBuf, 4, _outBuf, 4, out br, IntPtr.Zero))
                    {
                        DropHandle();
                        return false;
                    }
                    value = (uint)Marshal.ReadInt32(_outBuf);
                    return true;
                }
                catch
                {
                    DropHandle();
                    return false;
                }
            }
        }

        /// <summary>读取设备状态原始值（索引 14）。读取失败返回 0。</summary>
        public uint ReadStatusRaw()
        {
            uint v;
            return TryRead(out v) ? v : 0u;
        }

        // ------------------------------------------------------------------
        // 档位判定（带校准）
        // ------------------------------------------------------------------
        private FanState Classify(uint raw)
        {
            bool fast = _fastIsLower ? (raw < _threshold) : (raw > _threshold);
            return fast ? FanState.Spinning : FanState.Idle;
        }

        private FanState _lastState = FanState.Unknown;

        private void SetState(FanState s)
        {
            if (_lastState == s) return;
            _lastState = s;
            try { var h = StateChanged; if (h != null) h(s); } catch { }
        }

        /// <summary>
        /// 轮询读数直到连续两次相同（视为 EC 已稳定），或超时返回最后一次读数。
        ///
        /// 为什么必须这样做：切档后 EC 不会立刻反映新转速，实测 250ms 时可能读到过渡值
        /// （例如刚下发最大档时读到 0x87(135)，而真实稳定值是 0x23(35)）。
        /// 只 sleep 固定时间就采样，校准出来的阈值会是错的，进而把判据方向搞反。
        /// </summary>
        private uint? WaitStable(int timeoutMs)
        {
            uint prev = 0;
            int same = 0;
            bool first = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(PollSliceMs);
                uint v;
                if (!TryRead(out v)) continue;
                if (!first && v == prev)
                {
                    same++;
                    if (same >= 2) return v;
                }
                else
                {
                    same = 0;
                    prev = v;
                    first = false;
                }
            }
            uint last;
            return TryRead(out last) ? (uint?)last : null;
        }

        /// <summary>采集"正常档/最大档"两个读数并据此确定阈值与方向。会重试一次。</summary>
        private void Calibrate()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (TryCalibrateOnce(attempt)) return;
            }
            // 两次都失败：回退到本机实测的兜底值
            _calibrated = false;
            _fastIsLower = true;
            _threshold = FallbackThreshold;
            LogL(string.Format("校准失败，回退默认阈值 {0}（低值=在转）", FallbackThreshold));
        }

        private bool TryCalibrateOnce(int attempt)
        {
            _calibrated = false;
            _fastIsLower = true;
            _threshold = FallbackThreshold;

            string err;
            uint? idle = null, fast = null;

            if (TryWriteMode(false, out err))
            {
                Thread.Sleep(CalibrationSettleMs);
                idle = WaitStable(CalibrationTimeoutMs);
            }
            // 最大档探针必须走"边沿重置"：实测空闲期直接下发最大档有时根本不生效，
            // 必须 正常 → 间隔 → 最大 才可靠（后台保活循环用的也是这个手法）。
            if (TryWriteMode(false, out err))
            {
                Thread.Sleep(EdgeGapMs);
            }
            if (TryWriteMode(true, out err))
            {
                Thread.Sleep(CalibrationSettleMs);
                fast = WaitStable(CalibrationTimeoutMs);
            }

            if (!idle.HasValue || !fast.HasValue)
            {
                LogL(string.Format("校准第 {0} 次：采样失败(正常={1} 最大={2})",
                    attempt + 1, Show(idle), Show(fast)));
                return false;
            }

            // 有效性校验：两档读数必须拉开足够差距，否则说明采到了同一个状态
            double diff = Math.Abs((double)idle.Value - fast.Value);
            double margin = Math.Max(8.0, idle.Value * 0.10);
            if (diff < margin)
            {
                LogL(string.Format("校准第 {0} 次：两档读数差异过小(正常=0x{1:X2} 最大=0x{2:X2} 差={3:F0} 需≥{4:F0})，判为无效",
                    attempt + 1, idle.Value, fast.Value, diff, margin));
                return false;
            }

            _threshold = (idle.Value + fast.Value) / 2.0;
            _fastIsLower = fast.Value < idle.Value;
            _calibrated = true;
            LogL(string.Format("校准完成：正常=0x{0:X2}({0}) 最大=0x{1:X2}({1}) 阈值={2:F1} {3}",
                idle.Value, fast.Value, _threshold, _fastIsLower ? "低值=在转" : "高值=在转"));
            return true;
        }

        private static string Show(uint? v)
        {
            return v.HasValue ? string.Format("0x{0:X2}", v.Value) : "null";
        }

        /// <summary>当前是否处于最大档（风扇在转）。</summary>
        public bool IsActive()
        {
            uint raw;
            if (!TryRead(out raw)) return false;
            return Classify(raw) == FanState.Spinning;
        }

        /// <summary>当前档位。</summary>
        public FanState CurrentState()
        {
            uint raw;
            if (!TryRead(out raw)) return FanState.Unknown;
            return Classify(raw);
        }

        /// <summary>兼容旧接口：满速返回 Full，否则 Normal。</summary>
        public FanMode CurrentMode()
        {
            return CurrentState() == FanState.Spinning ? FanMode.Full : FanMode.Normal;
        }

        public enum FanMode { Normal, Full }

        /// <summary>取一次完整状态快照。</summary>
        public FanStatus GetStatus()
        {
            var st = new FanStatus
            {
                Calibrated = _calibrated,
                Threshold = _threshold
            };
            uint raw;
            st.DeviceReady = TryRead(out raw);
            st.ReadOk = st.DeviceReady;
            st.Raw = raw;
            st.State = st.ReadOk ? Classify(raw) : FanState.Unknown;
            return st;
        }

        // ------------------------------------------------------------------
        // 手动操作
        // ------------------------------------------------------------------
        /// <summary>触发一次除尘最大档（不启动保活循环）。</summary>
        public void Trigger() { WriteMode(true); }

        /// <summary>切回正常档。</summary>
        public void Restore() { WriteMode(false); }

        /// <summary>边沿重置：正常 → 等待 → 最大。</summary>
        public void ReArm()
        {
            string err;
            TryWriteMode(false, out err);
            Thread.Sleep(EdgeGapMs);
            WriteMode(true);
        }

        // ------------------------------------------------------------------
        // 开 / 关（含后台保活循环）
        // ------------------------------------------------------------------
        public void Enable()
        {
            lock (_lifecycleLock)
            {
                if (_enabled) return;

                Calibrate();

                string err;
                if (!TryWriteMode(true, out err))
                    throw new Win32Exception(0, err);
                SetState(FanState.Spinning);

                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _thread = new Thread(() => Loop(token))
                {
                    IsBackground = true,
                    Name = "FanBoostLoop"
                };
                _enabled = true;
                _thread.Start();

                // 开启后的自检：如果立刻读到"没在转"，大概率是校准方向搞反或设备没响应，
                // 这条日志是排查此类问题的第一现场。
                uint check;
                if (TryRead(out check))
                {
                    LogL(string.Format("Enable 成功，保活循环已启动（开启后读数=0x{0:X2}({0})，判定={1}）",
                        check, Classify(check) == FanState.Spinning ? "在转" : "未在转"));
                }
                else
                {
                    LogL("Enable 成功，保活循环已启动（开启后读数失败）");
                }
            }
        }

        public void Disable()
        {
            Thread t = null;
            lock (_lifecycleLock)
            {
                if (!_enabled)
                {
                    // 未开启时不做任何设备写入：避免 --status 之类的只读查询
                    // 顺手把风扇切回正常档（那会产生用户没要求的副作用）。
                    return;
                }
                var cts = _cts;
                _cts = null;
                if (cts != null) cts.Cancel();
                t = _thread;
                _thread = null;
                _enabled = false;
            }

            // 关键修复：必须等后台线程真正退出，否则它可能在下面 SetMode(false)
            // 之后又发一次 SetMode(true)，表现为"关闭后风扇自己转起来"。
            if (t != null && t.IsAlive && t != Thread.CurrentThread)
                t.Join(3000);

            string err;
            TryWriteMode(false, out err);
            SetState(FanState.Idle);
            LogL("Disable 完成，已恢复默认转速");
        }

        private void Loop(CancellationToken token)
        {
            int failStreak = 0;
            int sinceForce = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        uint raw;
                        bool ok = TryRead(out raw);
                        bool active = ok && Classify(raw) == FanState.Spinning;
                        sinceForce++;

                        // 关键：只有校准通过时才依据读数做控制决策。
                        // 未校准时读数不可信（实测会读到 0x87 这类过渡值，与是否在转无关），
                        // 若照旧"读数非满速就边沿重置"，会每 600ms 把风扇切回正常档，
                        // 反而把满速打断（实测出现 2/8 的极差保持率）。
                        bool needEdge = (sinceForce >= ForceEdgeEveryLoops)
                                        || (_calibrated && !active);

                        string err;
                        if (needEdge)
                        {
                            // 掉速或到兜底周期：做一次边沿重置，抢在固件 2s 暂停前重排
                            TryWriteMode(false, out err);
                            Thread.Sleep(EdgeGapMs);
                            if (token.IsCancellationRequested) break;
                            if (TryWriteMode(true, out err))
                            {
                                SetState(FanState.Spinning);
                                sinceForce = 0;
                            }
                        }
                        else
                        {
                            // 高速相持续刷新，防止固件因无新命令复位
                            if (TryWriteMode(true, out err)) SetState(FanState.Spinning);
                        }

                        if (failStreak > 0 && failStreak % 20 == 0)
                            LogL("循环已恢复（此前连续失败 " + failStreak + " 次）");
                        failStreak = 0;
                    }
                    catch (Exception ex)
                    {
                        failStreak++;
                        if (failStreak == 1 || failStreak % 20 == 0)
                            LogL("循环异常(已忽略并继续，避免线程死亡导致永久停转): " + ex.GetType().Name + ": " + ex.Message);
                    }

                    if (token.WaitHandle.WaitOne(LoopIntervalMs)) break;
                }
            }
            finally
            {
                string err;
                TryWriteMode(false, out err);
                SetState(FanState.Idle);
            }
        }

        private void LogL(string s)
        {
            var l = Logger;
            if (l != null) l("[FanController] " + s);
        }

        // ------------------------------------------------------------------
        // 释放
        // ------------------------------------------------------------------
        public void Dispose()
        {
            try { Disable(); } catch { }
            lock (_ioLock)
            {
                if (_handle != INVALID) { CloseHandle(_handle); _handle = INVALID; }
                if (_inBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_inBuf); _inBuf = IntPtr.Zero; }
                if (_rdBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_rdBuf); _rdBuf = IntPtr.Zero; }
                if (_outBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_outBuf); _outBuf = IntPtr.Zero; }
            }
        }
    }
}
