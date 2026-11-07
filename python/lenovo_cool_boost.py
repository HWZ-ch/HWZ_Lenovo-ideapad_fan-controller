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
  * 读取状态：DeviceIoControl(h, 0x831020C4, {14}, out)         与基准值比较判断当前档位
驱动自身的"除尘"行为是：最高转速约 9 秒 -> 停 2 秒 -> 循环，约 2 分钟后复位。
本工具用"开启 -> 在 EC 除尘窗口(~9s)内、安全间隔(默认 7s)处切回正常重置计时
-> 立即再开"的循环，并在维持期持续监测掉速，一旦风扇掉速即刻重置，从而跳过官方
2 秒停顿，使风扇几乎持续保持最高转速，即"持续激活降温除尘"。

使用（需 Windows + 管理员权限 + 已安装上述联想驱动）
----------------------------------------------------
  python lenovo_cool_boost.py status          # 查看当前风扇档位
  python lenovo_cool_boost.py once            # 触发一次官方除尘（约 2 分钟自动停止）
  python lenovo_cool_boost.py start           # 持续激活（前台运行，Ctrl+C 停止）
  python lenovo_cool_boost.py start --tray    # 若已安装 pystray，则显示系统托盘
  pythonw lenovo_cool_boost.py start          # 无控制台后台运行（停止：再运行 once/或任务管理器结束）

安全提示
--------
  * 持续最高转速会增加风扇磨损与耗电，建议在插电时使用；此为用户自主行为，风险自担。
  * 本工具只把风扇设到"最高"（降温方向），不会降速导致过热；请勿用第三方工具反向降速。
  * 仅适用于装有 `Lenovo ACPI-Compliant Virtual Power Controller` 驱动的联想机型。
    若提示无法打开设备（已尝试 EnergyDrv/Lenovo0/1/AcpiVpc），说明驱动不存在/未运行
    或设备接口未创建，需先运行联想电脑管家/联想实用程序，或适配 WMI 接口
    （Legion / newer 机型）。

兼容机型（社区验证）：IdeaPad 3/330/Y/Z/G 系列、小新 15IIL 2020 等老机型。
新机型（拯救者/ Yoga / ThinkBook）可能走 WMI(LENOVO_GAMEZONE) 接口，本工具会自动
报错并提示。
"""

import argparse
import sys
import time
import threading

# 当前生效的控制设备名（自动探测后写入，供排错时打印）
ACTIVE_DEVICE = None

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
else:
    kernel32 = None


class FanError(Exception):
    pass


class LenovoFan:
    r"""封装联想风扇控制设备（自动探测 \\.\EnergyDrv / \\.\Lenovo0/1 / \\.\AcpiVpc）的风扇控制与状态读取。"""

    def __init__(self):
        if sys.platform != "win32" or kernel32 is None:
            raise FanError("本工具仅支持 Windows 系统。")
        self._normal_expected = None  # 首次读取时校准的"正常档"基准值
        self._device = None           # 探测到的实际设备名

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

    def is_fast(self):
        """返回当前是否为最高转速档。首次调用会先校准基准值。"""
        if self._normal_expected is None:
            self.set_mode(False)  # 先切到正常档以获得基准读数
        h = self._open(False)
        try:
            inb = (ctypes.c_uint32 * 1)(14)
            outb = ctypes.c_uint32()
            br = ctypes.c_uint32()
            ok = kernel32.DeviceIoControl(
                h, IOCTL_READ,
                ctypes.byref(inb), ctypes.sizeof(inb),
                ctypes.byref(outb), ctypes.sizeof(outb),
                ctypes.byref(br), None,
            )
            if not ok:
                raise FanError("读取风扇状态失败: %s" % ctypes.WinError(ctypes.GetLastError()))
            value = outb.value
            if self._normal_expected is None:
                self._normal_expected = value
            # 与 C# 版一致：读数偏离"正常档基准值"即视为最高转速档
            return value != self._normal_expected
        finally:
            kernel32.CloseHandle(h)

    def keep_running(self, interval_ms=7000, poll_ms=200, stop_event=None):
        """
        持续保持最高转速（可靠版）。
        循环逻辑：确保最高档 -> 在 interval_ms 内持续监测，一旦风扇掉速(EC 进入
        约 2 秒停顿)立即切回正常重置计时并马上再开 -> 跳过官方停顿，几乎无间断。
        interval_ms 设为安全小于 EC 除尘窗口(~9s)，避免等待过久而撞上停顿。
        """
        try:
            while not (stop_event and stop_event.is_set()):
                # 确保处于最高档
                while not self.is_fast():
                    if stop_event and stop_event.is_set():
                        return
                    self.set_mode(True)
                    time.sleep(0.01)
                # 维持最高档并监测掉速
                waited = 0
                while waited < interval_ms:
                    if stop_event and stop_event.wait(poll_ms / 1000.0):
                        return
                    waited += poll_ms
                    if not self.is_fast():
                        break  # 风扇掉速，立即重置
                # 切回正常以重置 EC 内置计时，下次循环马上再开
                self.set_mode(False)
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
    fan = LenovoFan()
    print("当前风扇档位: %s" % ("最高转速 (降温除尘生效中)" if fan.is_fast() else "正常转速"))
    if ACTIVE_DEVICE:
        print("控制设备: %s" % ACTIVE_DEVICE)


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
    """可选：pystray 系统托盘（需 pip install pystray）。"""
    import pystray
    from PIL import Image

    icon_img = Image.new("RGB", (64, 64), (200, 60, 60))

    def on_start(icon, item):
        if not stop_event.is_set():
            return
        stop_event.clear()
        threading.Thread(target=fan.keep_running,
                         kwargs={"interval_ms": 9000, "stop_event": stop_event},
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

    sub.add_parser("status", help="查看当前风扇档位").set_defaults(func=cmd_status)
    sub.add_parser("once", help="触发一次官方除尘(约2分钟自动停)").set_defaults(func=cmd_once)

    p_start = sub.add_parser("start", help="持续激活(前台运行，Ctrl+C 停止)")
    p_start.add_argument("--interval", type=int, default=7000,
                         help="最高档维持毫秒数后重置计时，需小于 EC 除尘窗口(~9000)，默认 7000")
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
