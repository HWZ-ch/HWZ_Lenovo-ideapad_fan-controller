#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""
Lenovo Cool Boost - 持续激活联想电脑管家「降温除尘」(风扇最大转速)
=====================================================================

原理
----
联想电脑管家的「降温除尘」本质是通过 `Lenovo ACPI-Compliant Virtual Power
Controller` 驱动把风扇拉到最高转速。该驱动在系统中创建设备（不同机型/驱动版本
暴露的用户态设备名可能为 `\\.\EnergyDrv` / `\\.\Lenovo0` / `\\.\Lenovo1` /
`\\.\AcpiVpc`），本工具**自动依次探测**第一个能打开的设备，无需手动指定：
  * 控制风扇：DeviceIoControl(h, 0x831020C0, {6, 1, mode}, ...)  mode=1 最大 / 0 正常
  * 读取状态：DeviceIoControl(h, 0x831020C4, {14}, out)         与阈值比较判断当前档位

驱动自身的"除尘"行为是：最高转速约 9 秒 -> 停 2 秒 -> 循环，约 2 分钟后复位。
本工具用"开启 -> 持续监测，一旦风扇掉速立即切回正常重置计时 -> 马上再开"的循环，
跳过官方 2 秒停顿，使风扇几乎持续保持最高转速。

阈值自适应（v1.1）
------------------
旧版把"在转/停转"的判据写死为 `读数 < 100`，那是单台机器的实测值，换机型就可能失效。
现在改为**启动时校准**：先切正常档读一次、再切最大档读一次，取两者中点作为阈值，
并自动判定"低值=在转"还是"高值=在转"。校准失败才回退默认值 100。

安全提示
--------
  * 持续最高转速会增加风扇磨损与耗电，建议在插电时使用；此为用户自主行为，风险自担。
  * 本工具只把风扇设到"最高"（降温方向），不会降速导致过热。
  * 仅适用于装有 `Lenovo ACPI-Compliant Virtual Power Controller` 驱动的联想机型。

使用（需 Windows + 管理员权限 + 联想电脑管家在运行）
----------------------------------------------------
  python lenovo_cool_boost.py status          # 查看当前风扇档位（只读，不会改变风扇状态）
  python lenovo_cool_boost.py once            # 触发一次官方除尘（约 2 分钟自动停止）
  python lenovo_cool_boost.py start           # 持续激活（前台运行，Ctrl+C 停止）
  python lenovo_cool_boost.py start --tray    # 若已安装 pystray，则显示系统托盘
  pythonw lenovo_cool_boost.py start          # 无控制台后台运行

兼容机型（社区验证）：IdeaPad 3/330/Y/Z/G 系列、小新 15IIL 2020、小新 16 IRL8 等。
新机型（拯救者 / Yoga / ThinkBook）可能走 WMI(LENOVO_GAMEZONE) 接口，本工具会报错提示。
"""

import argparse
import sys
import time
import threading

# 当前生效的控制设备名（自动探测后写入，供排错时打印）
ACTIVE_DEVICE = None

# 未校准时的兜底阈值：本机实测空闲≈0x85(133)、强制≈0x23~0x28(35~40)，取 100 以下判为在转。
FALLBACK_THRESHOLD = 100.0

# 校准参数
CALIBRATE_SETTLE = 0.25     # 切档后等 EC 稳定的最短时间
CALIBRATE_TIMEOUT = 1.8     # 单次采样最长等待
POLL_SLICE = 0.08           # 采样轮询间隔
CALIBRATE_ATTEMPTS = 3      # 校准最多尝试次数

# 保活循环参数
LOOP_INTERVAL_MS = 600      # 循环间隔：必须 < 2000 才能压过固件 2 秒停顿
EDGE_GAP = 0.3              # 边沿重置：正常 → 间隔 → 最大
FORCE_EDGE_LOOPS = 50       # 每约 30 秒无条件重排一次

# ---------------------------------------------------------------------------
# Windows-only Win32 绑定（在 Linux/macOS 上仅占位，确保可导入与语法检查）
# ---------------------------------------------------------------------------
if sys.platform == "win32":
    import ctypes

    kernel32 = ctypes.windll.kernel32

    GENERIC_READ = 0x80000000
    GENERIC_WRITE = 0x40000000
    FILE_SHARE_READ = 0x00000001
    FILE_SHARE_WRITE = 0x00000002
    OPEN_EXISTING = 0x00000003
    FILE_ATTRIBUTE_NORMAL = 0x00000080
    INVALID_HANDLE = ctypes.c_void_p(-1).value  # 0xFFFFFFFFFFFFFFFF

    kernel32.CreateFileW.argtypes = [
        ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32,
        ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p,
    ]
    kernel32.CreateFileW.restype = ctypes.c_void_p

    kernel32.DeviceIoControl.argtypes = [
        ctypes.c_void_p, ctypes.c_uint32,
        ctypes.c_void_p, ctypes.c_uint32,
        ctypes.c_void_p, ctypes.c_uint32,
        ctypes.POINTER(ctypes.c_uint32), ctypes.c_void_p,
    ]
    kernel32.DeviceIoControl.restype = ctypes.c_int  # BOOL

    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
    kernel32.CloseHandle.restype = ctypes.c_int

    # 多候选设备名：不同联想机型/驱动版本暴露的用户态设备名不同，
    # 依次尝试，第一个能打开的即为本机实际设备（自动探测，跨机型兼容）。
    DEVICE_CANDIDATES = [
        r"\\.\EnergyDrv",   # 老款 Energy Management 驱动
        r"\\.\Lenovo0",     # 新版 EM 驱动 (AcpiVpc.sys) 常见名
        r"\\.\Lenovo1",
        r"\\.\AcpiVpc",     # 驱动名直写（少数机型）
    ]
    IOCTL_CONTROL = 0x831020C0
    IOCTL_READ = 0x831020C4
    STATUS_INDEX = 14
else:
    kernel32 = None
    DEVICE_CANDIDATES = []
    IOCTL_CONTROL = 0
    IOCTL_READ = 0
    STATUS_INDEX = 14


class FanError(Exception):
    pass


class LenovoFan:
    r"""封装联想风扇控制设备（自动探测 \\.\EnergyDrv / \\.\Lenovo0/1 / \\.\AcpiVpc）。"""

    def __init__(self):
        if sys.platform != "win32" or kernel32 is None:
            raise FanError("本工具仅支持 Windows 系统。")
        self._device = None
        # 校准结果
        self._calibrated = False
        self._fast_is_lower = True
        self._threshold = FALLBACK_THRESHOLD
        self._idle_sample = None
        self._fast_sample = None

    # ---------------- 设备 ----------------
    def _open(self, write):
        access = GENERIC_WRITE if write else GENERIC_READ
        errors = []
        for dev in DEVICE_CANDIDATES:
            h = kernel32.CreateFileW(
                dev, access,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                None, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, None,
            )
            if h != INVALID_HANDLE and h is not None:
                self._device = dev
                global ACTIVE_DEVICE
                ACTIVE_DEVICE = dev
                return h
            errors.append("%s: %s" % (dev, ctypes.WinError(ctypes.GetLastError())))
        raise FanError(
            "无法打开联想风扇控制设备（已依次尝试：%s）。\n"
            "请确认：(1) 已安装 Lenovo ACPI-Compliant Virtual Power Controller 驱动；"
            "(2) 联想电脑管家/联想实用程序正在运行（设备接口由其创建）；"
            "(3) 以管理员身份运行。\n详细：%s"
            % (", ".join(DEVICE_CANDIDATES), " | ".join(errors))
        )

    # ---------------- 底层操作 ----------------
    def set_mode(self, fast):
        """fast=True 设最高转速(降温除尘)，False 设正常转速。"""
        h = self._open(True)
        try:
            buf = (ctypes.c_uint32 * 3)(6, 1, 1 if fast else 0)
            br = ctypes.c_uint32()
            ok = kernel32.DeviceIoControl(
                h, IOCTL_CONTROL,
                ctypes.byref(buf), ctypes.sizeof(buf),
                None, 0, ctypes.byref(br), None,
            )
            if not ok:
                raise FanError("设置风扇模式失败: %s" % ctypes.WinError(ctypes.GetLastError()))
        finally:
            kernel32.CloseHandle(h)

    def read_status(self):
        """只读当前状态原始值（索引 14）。读取失败返回 None。

        注意：本方法**不会**改变风扇状态，可安全用于 status 查询。
        旧版 is_fast() 会先 set_mode(False) 校准，导致 `status` 把风扇切回正常档。
        """
        h = self._open(False)
        try:
            inb = (ctypes.c_uint32 * 1)(STATUS_INDEX)
            outb = ctypes.c_uint32()
            br = ctypes.c_uint32()
            ok = kernel32.DeviceIoControl(
                h, IOCTL_READ,
                ctypes.byref(inb), ctypes.sizeof(inb),
                ctypes.byref(outb), ctypes.sizeof(outb),
                ctypes.byref(br), None,
            )
            if not ok:
                return None
            return outb.value
        finally:
            kernel32.CloseHandle(h)

    # ---------------- 校准与判定 ----------------
    def _wait_stable(self, timeout):
        """轮询直到连续两次读数相同（视为 EC 已稳定），超时返回最后一次读数。

        切档后 EC 不会立刻反映新转速：实测 250ms 时可能读到过渡值（刚下发最大档时
        读到 0x87(135)，而真实稳定值是 0x23(35)）。固定 sleep 后采样会得到错误阈值，
        进而把判据方向搞反。
        """
        prev = None
        same = 0
        deadline = time.time() + timeout
        while time.time() < deadline:
            time.sleep(POLL_SLICE)
            v = self.read_status()
            if v is None:
                continue
            if prev is not None and v == prev:
                same += 1
                if same >= 2:
                    return v
            else:
                same = 0
                prev = v
        return self.read_status()

    def _try_calibrate_once(self, attempt):
        self._calibrated = False
        self._fast_is_lower = True
        self._threshold = FALLBACK_THRESHOLD

        try:
            self.set_mode(False)
            time.sleep(CALIBRATE_SETTLE)
            idle = self._wait_stable(CALIBRATE_TIMEOUT)

            # 最大档探针必须走"边沿重置"：实测空闲期直接下发最大档有时根本不生效，
            # 必须 正常 → 间隔 → 最大 才可靠（保活循环用的也是这个手法）。
            self.set_mode(False)
            time.sleep(EDGE_GAP)
            self.set_mode(True)
            time.sleep(CALIBRATE_SETTLE)
            fast = self._wait_stable(CALIBRATE_TIMEOUT)
        except FanError:
            return False

        if idle is None or fast is None:
            return False

        # 有效性校验：两档读数必须拉开足够差距，否则说明采到了同一个状态
        diff = abs(idle - fast)
        margin = max(8.0, idle * 0.10)
        if diff < margin:
            return False

        self._idle_sample = idle
        self._fast_sample = fast
        self._threshold = (idle + fast) / 2.0
        self._fast_is_lower = fast < idle
        self._calibrated = True
        return True

    def calibrate(self):
        """采样正常档/最大档读数，确定阈值与方向。返回是否校准成功。"""
        for attempt in range(CALIBRATE_ATTEMPTS):
            if self._try_calibrate_once(attempt):
                return True
        # 全部失败：回退到本机实测的兜底值
        self._calibrated = False
        self._fast_is_lower = True
        self._threshold = FALLBACK_THRESHOLD
        return False

    def classify(self, value):
        """按校准阈值判定，返回 'fast' / 'idle' / None。"""
        if value is None:
            return None
        fast = (value < self._threshold) if self._fast_is_lower else (value > self._threshold)
        return "fast" if fast else "idle"

    def describe(self, value):
        if value is None:
            return "读取失败"
        kind = self.classify(value)
        label = "满速(生效中)" if kind == "fast" else "正常转速"
        tag = "已校准" if self._calibrated else "默认阈值"
        return "%s  读数=0x%02X(%d) [%s]" % (label, value, value, tag)

    def is_fast(self):
        """当前是否最大档（只读，不会改变风扇状态）。"""
        return self.classify(self.read_status()) == "fast"

    # ---------------- 保活循环 ----------------
    def keep_running(self, interval_ms=LOOP_INTERVAL_MS, poll_ms=200, stop_event=None):
        """
        持续保持最高转速。

        与 C# 版同一套策略：每 interval_ms 刷新一次最大档命令；到 FORCE_EDGE_LOOPS 轮
        无条件做一次"正常 → 间隔 → 最大"的边沿重置，抢在固件 2 秒暂停前重排。

        关键：**只有校准通过时才依据读数做决策**。未校准时读数不可信（实测会读到 0x87
        这类过渡值，与是否在转无关），若照旧"读数非满速就边沿重置"，会每轮把风扇切回
        正常档，反而把满速打断。
        """
        def stopped():
            return stop_event is not None and stop_event.is_set()

        def wait(seconds):
            if stop_event is not None:
                return stop_event.wait(seconds)
            time.sleep(seconds)
            return False

        try:
            self.set_mode(False)
            wait(EDGE_GAP)
            self.set_mode(True)

            since_force = 0
            while not stopped():
                since_force += 1
                need_edge = (since_force >= FORCE_EDGE_LOOPS) or \
                            (self._calibrated and not self.is_fast())

                if need_edge:
                    self.set_mode(False)
                    wait(EDGE_GAP)
                    if stopped():
                        break
                    self.set_mode(True)
                    since_force = 0
                else:
                    self.set_mode(True)   # 刷新命令，防止固件因无新命令而复位

                if wait(interval_ms / 1000.0):
                    break
        finally:
            # 退出前恢复到系统自动控制（正常档）
            try:
                self.set_mode(False)
            except FanError:
                pass


def is_admin():
    try:
        return ctypes.windll.shell32.IsUserAnAdmin() != 0
    except Exception:
        return False


def cmd_status(args):
    """只读查询：不会改变风扇状态。"""
    fan = LenovoFan()
    value = fan.read_status()
    print("当前风扇档位: %s" % fan.describe(value))
    if ACTIVE_DEVICE:
        print("控制设备: %s" % ACTIVE_DEVICE)
    if not fan._calibrated:
        print("提示: 本次未校准（阈值 %g 为默认值）。如需精确判定请用 start，它会先校准。"
              % FALLBACK_THRESHOLD)


def cmd_once(args):
    fan = LenovoFan()
    fan.set_mode(True)
    print("已触发一次「降温除尘」(官方行为：最高转速约 9s/停 2s 循环，约 2 分钟后自动复位)。")


def cmd_start(args):
    if not is_admin():
        print("[警告] 未以管理员身份运行，可能无法打开联想风扇控制设备。"
              "请右键\"以管理员身份运行\"。")
    fan = LenovoFan()
    stop_event = threading.Event()

    if args.tray:
        try:
            run_tray(fan, stop_event)
            return
        except ImportError:
            print("[提示] 未安装 pystray，退回前台模式（Ctrl+C 停止）。")

    if fan.calibrate():
        print("校准完成: 正常=0x%02X(%d) 最大=0x%02X(%d) 阈值=%.1f %s"
              % (fan._idle_sample, fan._idle_sample, fan._fast_sample, fan._fast_sample,
                 fan._threshold, "低值=在转" if fan._fast_is_lower else "高值=在转"))
    else:
        print("校准未取得有效样本，回退默认阈值 %g。" % FALLBACK_THRESHOLD)

    print("持续激活降温除尘中……（按 Ctrl+C 停止，停止后会恢复系统自动控制）")
    if ACTIVE_DEVICE:
        print("控制设备: %s" % ACTIVE_DEVICE)
    try:
        fan.keep_running(interval_ms=args.interval, stop_event=stop_event)
    except FanError as e:
        print("错误: %s" % e)
        sys.exit(1)
    except KeyboardInterrupt:
        pass
    print("\n已停止，风扇恢复系统自动控制。")


def run_tray(fan, stop_event):
    """可选：pystray 系统托盘（需 pip install pystray pillow）。"""
    import pystray
    from PIL import Image

    icon_img = Image.new("RGB", (64, 64), (200, 60, 60))

    def on_start(icon, item):
        if not stop_event.is_set():
            return
        stop_event.clear()
        threading.Thread(target=fan.keep_running,
                         kwargs={"interval_ms": LOOP_INTERVAL_MS, "stop_event": stop_event},
                         daemon=True).start()

    def on_stop(icon, item):
        stop_event.set()

    def on_quit(icon, item):
        stop_event.set()
        icon.stop()

    menu = pystray.Menu(
        pystray.MenuItem("开始持续降温除尘", on_start),
        pystray.MenuItem("停止", on_stop),
        pystray.MenuItem("退出", on_quit),
    )
    icon = pystray.Icon("LenovoCoolBoost", icon_img, "Lenovo 降温除尘", menu)
    icon.run()


def main():
    parser = argparse.ArgumentParser(
        description="持续激活联想电脑管家「降温除尘」(风扇最大转速)")
    sub = parser.add_subparsers(dest="cmd")

    sub.add_parser("status", help="查看当前风扇档位（只读，不改变状态）").set_defaults(func=cmd_status)
    sub.add_parser("once", help="触发一次官方除尘(约2分钟自动停)").set_defaults(func=cmd_once)

    p_start = sub.add_parser("start", help="持续激活(前台运行，Ctrl+C 停止)")
    p_start.add_argument("--interval", type=int, default=LOOP_INTERVAL_MS,
                         help="循环刷新间隔毫秒，默认 600；必须小于 2000 才能压过固件 2 秒停顿")
    p_start.add_argument("--tray", action="store_true",
                         help="使用系统托盘(需 pystray)")
    p_start.set_defaults(func=cmd_start)

    args = parser.parse_args()
    if not getattr(args, "func", None):
        parser.print_help()
        sys.exit(1)
    args.func(args)


if __name__ == "__main__":
    main()
