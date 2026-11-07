#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
联想小新 16 IRL8 等机型：EC 风扇寄存器直接读写，实现真正无间隔连续满速。

=====================================================================
为什么需要它
---------------------------------------------------------------------
联想 EnergyDrv / AcpiVpc 接口只暴露「启动除尘」一条命令，EC 固件写死
「高速约 9 秒 -> 强制停 2 秒」的周期，任何软件都无法消除间隔。
本工具绕过该驱动，直接写 EC 的风扇目标转速寄存器，实现真正连续。

=====================================================================
原理
---------------------------------------------------------------------
直接访问 EC 端口 0x66(命令) / 0x62(数据)：
    命令 0x80 = 读 EC 寄存器
    命令 0x81 = 写 EC 寄存器
端口 I/O 由后端完成，优先使用 InpOutx64（DLL 自动加载 inpoutx64.sys），
也可回退到 RWEverything 的 RwDrv 驱动（IOCTL 0x222810/0x222814）。
小新系列 FANS 为 8-bit EC 寄存器（yogafan 数据库佐证，multiplier=100）。

=====================================================================
使用前
---------------------------------------------------------------------
方案 A（推荐，简单）：InpOutx64
1. 从 highrez.co.uk/downloads/inpout32/ 下载 InpOutBinaries。
2. 把 x64/inpoutx64.dll 放到本脚本同级的 inpout/x64/ 目录下（已自带）。
3. 以管理员身份运行本脚本；DLL 会自动安装并加载 inpoutx64.sys。

方案 B（备用）：RWEverything / RwDrv
1. 安装 RWEverything（rweverything.com），管理员运行一次以加载 RwDrv。
2. Win11：设置 -> Windows 安全中心 -> 设备安全性 -> 核心隔离 ->
   关闭「内存完整性」，重启（否则 RwDrv 被驱动阻止列表拦下）。

本脚本必须以管理员身份运行。

=====================================================================
命令
---------------------------------------------------------------------
  python ec_fan_lock.py read <offset>             读单个 EC 寄存器 (0x00-0xFF)
  python ec_fan_lock.py scan                      自动定位风扇寄存器(并保存基线)
  python ec_fan_lock.py once <offset> <value> [--force]   写一次(写前需确认)
  python ec_fan_lock.py lock <offset> <value> [--force]    持续钉满速(Ctrl+C 自动回滚基线)
  python ec_fan_lock.py restore <offset>          从基线把该寄存器写回正常态值

=====================================================================
内置安全网（降低风险）
---------------------------------------------------------------------
1. scan 自动保存正常态快照到 ec_baseline.json，供恢复使用。
2. lock / once 写前二次确认：打印 offset 与值，需输入 yes 才执行。
3. 满速区间守卫：拒绝写入 0x00（关闭风扇），拒绝低于 0x04 的低值
   （接近关闭风扇、限速过热风险）；确认该低值即满速时加 --force 强制。
4. lock 在 Ctrl+C / 异常退出时自动把风扇寄存器写回基线值，不留半残状态。
5. restore <offset> 可随时一键写回基线正常值（exit 后、异常后均可）。

=====================================================================
推荐工作流（三步）
---------------------------------------------------------------------
① 本机先运行 LenovoCoolBoost.exe 的「开始」，确认风扇能满速（有间隔也无妨）。
② python ec_fan_lock.py scan
   -> 按提示在采样窗口内触发一次满速，脚本输出「候选风扇寄存器 offset」并保存基线。
③ 用 RWEverything GUI（Embedded Controller 视图）或本工具 once 命令，
   对候选 offset 试写不同值（如 0x01 / 0xFF / 0x64），听风扇 / 看转速，
   确认哪个 offset + 哪个值 = 持续满速。
④ python ec_fan_lock.py lock <offset> <满速值>
   -> 真正无间隔连续满速。Ctrl+C 停止后自动恢复基线；也可随时
   python ec_fan_lock.py restore <offset> 手动恢复。

=====================================================================
警告
---------------------------------------------------------------------
写错 EC 寄存器可能导致风扇失控、EC 异常甚至需断电恢复。
务必先用 scan + RWEverything GUI 确认目标寄存器与满速值，再使用 lock。
若 lock 异常退出且自动恢复失败：用 RWEverything GUI 手动写回基线值，
或重启 / 拔电长按电源键 30 秒清 EC。风险自担；建议插电使用。
"""
import sys
import os
import json
import time
import struct
import ctypes
from ctypes import wintypes

# ---------- Windows / RWEverything 驱动 ----------
kernel32 = ctypes.windll.kernel32
kernel32.CreateFileW.restype = wintypes.HANDLE
kernel32.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                 wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
kernel32.DeviceIoControl.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPVOID,
                                     wintypes.DWORD, wintypes.LPVOID, wintypes.DWORD,
                                     ctypes.POINTER(wintypes.DWORD), wintypes.LPVOID]
kernel32.DeviceIoControl.restype = wintypes.BOOL
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.CloseHandle.restype = wintypes.BOOL

GENERIC_READ_WRITE = 0xC0000000
OPEN_EXISTING = 3
REQ_SIZE = 0x100
RW_IOCTL_READ_PORT = 0x222810
RW_IOCTL_WRITE_PORT = 0x222814

EC_CMD_PORT = 0x66
EC_DATA_PORT = 0x62
EC_RD = 0x80
EC_WR = 0x81
EC_SPACE = 256

# ---------- 安全网配置 ----------
BASELINE_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ec_baseline.json")
# 低于此值的写入默认拒绝（接近关闭风扇，存在限速过热风险）；如需写入请加 --force
MIN_SAFE_VALUE = 0x04

# InpOutx64 DLL 候选路径（优先自带，其次本目录，最后系统 PATH）
_INPOUT_DLL_CANDIDATES = [
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "inpout", "x64", "inpoutx64.dll"),
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "inpoutx64.dll"),
    "inpoutx64.dll",
]


class EcError(Exception):
    pass


class PortIOBackend:
    """端口 I/O 抽象后端（InpOutx64 或 RWEverything/RwDrv）。"""
    def read_port(self, port):
        raise NotImplementedError
    def write_port(self, port, value):
        raise NotImplementedError
    def close(self):
        pass


class RwDrvBackend(PortIOBackend):
    """通过 RWEverything 的 RwDrv 驱动做端口 I/O。"""
    def __init__(self):
        self._h = kernel32.CreateFileW(r"\\.\RWEverything", GENERIC_READ_WRITE, 0, None,
                                       OPEN_EXISTING, 0, None)
        if self._h == wintypes.HANDLE(-1).value:
            raise EcError("无法打开 \\\\.\\RWEverything：请先安装并管理员运行 RWEverything 以加载 RwDrv 驱动")

    def read_port(self, port):
        req = ctypes.create_string_buffer(REQ_SIZE)
        struct.pack_into('<H', req, 0, port & 0xFFFF)
        ioctl = RW_IOCTL_READ_PORT
        ret = wintypes.DWORD()
        ok = kernel32.DeviceIoControl(self._h, ioctl, req, REQ_SIZE, req, REQ_SIZE,
                                      ctypes.byref(ret), None)
        if not ok:
            raise EcError("DeviceIoControl 失败 (IOCTL=0x%X): %s" % (ioctl, ctypes.WinError()))
        return struct.unpack_from('<Q', req, 4)[0] & 0xFF

    def write_port(self, port, value):
        req = ctypes.create_string_buffer(REQ_SIZE)
        struct.pack_into('<H', req, 0, port & 0xFFFF)
        struct.pack_into('<Q', req, 4, value & 0xFFFFFFFFFFFFFFFF)
        ioctl = RW_IOCTL_WRITE_PORT
        ret = wintypes.DWORD()
        ok = kernel32.DeviceIoControl(self._h, ioctl, req, REQ_SIZE, req, REQ_SIZE,
                                      ctypes.byref(ret), None)
        if not ok:
            raise EcError("DeviceIoControl 失败 (IOCTL=0x%X): %s" % (ioctl, ctypes.WinError()))

    def close(self):
        if self._h:
            kernel32.CloseHandle(self._h)
            self._h = None


class InpOutBackend(PortIOBackend):
    """通过 InpOutx64 的 Inp32/Out32 做端口 I/O（DLL 自动加载 inpoutx64.sys）。"""
    def __init__(self, dll_path):
        self._dll = ctypes.WinDLL(dll_path)
        self._dll.Inp32.argtypes = [ctypes.c_short]
        self._dll.Inp32.restype = ctypes.c_short
        self._dll.Out32.argtypes = [ctypes.c_short, ctypes.c_short]
        self._dll.Out32.restype = None
        # 触发 DLL 自动安装/加载驱动
        self._dll.Inp32(0x80)
        # 检查驱动是否打开（若 DLL 导出该函数）
        if hasattr(self._dll, "IsInpOutDriverOpen"):
            self._dll.IsInpOutDriverOpen.argtypes = []
            self._dll.IsInpOutDriverOpen.restype = ctypes.c_ubyte
            if not self._dll.IsInpOutDriverOpen():
                raise EcError("InpOutx64 驱动未成功打开")

    def read_port(self, port):
        return self._dll.Inp32(port & 0xFFFF) & 0xFF

    def write_port(self, port, value):
        self._dll.Out32(port & 0xFFFF, value & 0xFF)


def _find_inpout_dll():
    for p in _INPOUT_DLL_CANDIDATES:
        if os.path.exists(p):
            return p
    return None


def open_portio():
    """依次尝试 InpOutx64、RwDrv，返回可用的端口 I/O 后端。"""
    dll = _find_inpout_dll()
    if dll:
        try:
            return InpOutBackend(dll)
        except Exception as e:
            print("InpOutx64 后端尝试失败: %s" % e)
    try:
        return RwDrvBackend()
    except EcError:
        raise EcError("无法初始化端口 I/O：InpOutx64 DLL 不可用且 RwDrv 驱动未加载。"
                      "请把 InpOutx64 的 DLL 放在本脚本同级目录的 inpout/x64/ 下，"
                      "或管理员运行 RWEverything 加载 RwDrv 驱动。")


def ec_wait_ibf(h):
    for _ in range(5000):
        if not (h.read_port(EC_CMD_PORT) & 0x02):
            return
        time.sleep(0.00001)
    raise EcError("EC IBF 等待超时")


def ec_wait_obf(h):
    for _ in range(5000):
        if h.read_port(EC_CMD_PORT) & 0x01:
            return
        time.sleep(0.00001)
    raise EcError("EC OBF 等待超时")


def ec_read(h, offset, tries=3):
    last = None
    for _ in range(tries):
        try:
            ec_wait_ibf(h)
            h.write_port(EC_CMD_PORT, EC_RD)
            ec_wait_ibf(h)
            h.write_port(EC_DATA_PORT, offset)
            ec_wait_obf(h)
            v = h.read_port(EC_DATA_PORT)
            time.sleep(0.005)   # 给 EC 一点喘息时间，防止连续访问超时
            return v
        except EcError as e:
            last = e
            time.sleep(0.01)
    raise last or EcError("EC 读取失败 (EC[0x%02X])" % offset)


def ec_write(h, offset, value, tries=3):
    last = None
    for _ in range(tries):
        try:
            ec_wait_ibf(h)
            h.write_port(EC_CMD_PORT, EC_WR)
            ec_wait_ibf(h)
            h.write_port(EC_DATA_PORT, offset)
            ec_wait_ibf(h)
            h.write_port(EC_DATA_PORT, value)
            time.sleep(0.005)   # 给 EC 一点喘息时间
            return
        except EcError as e:
            last = e
            time.sleep(0.01)
    raise last or EcError("EC 写入失败 (EC[0x%02X]=0x%02X)" % (offset, value))


def is_admin():
    try:
        return ctypes.windll.shell32.IsUserAnAdmin()
    except Exception:
        return False


def save_baseline(base):
    """将正常态 EC 快照写入本地基线文件，供 restore / 自动回滚使用。"""
    try:
        with open(BASELINE_FILE, "w") as f:
            json.dump({"base": base}, f)
        print("基线已保存 -> %s" % BASELINE_FILE)
    except Exception as e:
        print("警告：基线保存失败: %s" % e)


def load_baseline():
    """读取基线文件，返回 256 个 EC 寄存器正常态值；不存在则返回 None。"""
    try:
        with open(BASELINE_FILE) as f:
            return json.load(f).get("base")
    except Exception:
        return None


def prepare_write(offset, value, force, interactive=True):
    """写前安全校验：范围 / 关闭风扇拦截 / 低值拦截 / 二次确认。返回是否允许写入。"""
    if not (0 <= offset <= 0xFF):
        print("✗ offset 超出范围 0x00-0xFF")
        return False
    if value == 0x00:
        print("✗ 拒绝写入 0x00：等于关闭风扇，存在过热风险。")
        return False
    if value < MIN_SAFE_VALUE and not force:
        print("✗ 拒绝写入 0x%02X：低于安全阈值 0x%02X（接近关闭风扇，限速过热风险）。"
              % (value, MIN_SAFE_VALUE))
        print("  如果经 RWEverything GUI 实测确认该低值确为满速，请加 --force 强制写入。")
        return False
    if interactive:
        print("即将写入 EC[0x%02X] = 0x%02X" % (offset, value))
        if value < MIN_SAFE_VALUE:
            print("  （注意：该值低于安全阈值，已用 --force 强制）")
        ans = input("确认写入？(输入 yes 继续，其他任意键取消): ").strip().lower()
        return ans == "yes"
    return True


def cmd_read(h, offset):
    print("EC[0x%02X] = 0x%02X" % (offset, ec_read(h, offset)))


def cmd_scan(h):
    print("[1/3] 读取正常态 EC 快照 (0x00-0xFF) ...")
    base = [ec_read(h, i) for i in range(EC_SPACE)]
    save_baseline(base)
    print("[2/3] 现在请运行 LenovoCoolBoost.exe 的『开始』(或联想电脑管家除尘)")
    print("       使风扇进入满速，本脚本将在接下来 8 秒内高频采样 EC ...")
    diff = {}
    for _ in range(40):                       # 40 * 0.2s = 8s
        time.sleep(0.2)
        for i in range(EC_SPACE):
            try:
                v = ec_read(h, i)
            except EcError:
                continue
            if v != base[i]:
                diff.setdefault(i, set()).add(v)
    print("[3/3] 候选风扇寄存器 (offset: 正常态值 -> 满速态出现的值):")
    if not diff:
        print("  未检测到变化。请确认除尘已触发且风扇确实满速；或调大 EC_SPACE。")
        return
    for i in sorted(diff):
        vals = sorted(x for x in diff[i] if x != base[i])
        print("  0x%02X : 正常=0x%02X  满速出现=%s" % (i, base[i], [hex(x) for x in vals]))


def cmd_once(h, offset, value, force):
    if not prepare_write(offset, value, force, interactive=True):
        print("已取消。")
        return
    ec_write(h, offset, value)
    print("已写入 EC[0x%02X] = 0x%02X" % (offset, value))


def cmd_restore(h, offset):
    """从基线文件把指定 offset 写回正常态值（一键恢复）。"""
    base = load_baseline()
    if base is None:
        print("未找到基线文件 %s；请先运行 scan。" % BASELINE_FILE)
        return
    if not (0 <= offset <= 0xFF):
        print("✗ offset 超出范围 0x00-0xFF")
        return
    v = base[offset]
    print("恢复 EC[0x%02X] -> 基线值 0x%02X" % (offset, v))
    ec_write(h, offset, v)
    print("已恢复。")


STOP_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "lock.stop")


def cmd_lock(h, offset, value, force, yes=False):
    if not prepare_write(offset, value, force, interactive=not yes):
        print("已取消。")
        return
    base = load_baseline()
    restore_val = base[offset] if (base and 0 <= offset < len(base)) else None
    print("持续钉 EC[0x%02X] = 0x%02X (创建 lock.stop 文件或 Ctrl+C 停止并自动恢复基线)" % (offset, value))
    try:
        while True:
            if os.path.exists(STOP_FILE):
                print("\n检测到停止标记 %s，准备退出并恢复..." % STOP_FILE)
                break
            ec_write(h, offset, value)
            time.sleep(0.08)
    except KeyboardInterrupt:
        print("\n捕获 Ctrl+C，正在停止 ...")
    except EcError as e:
        print("\n写入异常: %s" % e)
    finally:
        try:
            if os.path.exists(STOP_FILE):
                os.remove(STOP_FILE)
        except Exception:
            pass
        if restore_val is not None:
            try:
                ec_write(h, offset, restore_val)
                print("已自动写回基线值 EC[0x%02X] = 0x%02X" % (offset, restore_val))
            except EcError as e:
                print("自动恢复失败: %s；请手动 restore 或重启恢复。" % e)
        else:
            print("未找到基线（请先运行 scan），请手动将 0x%02X 恢复，或重启恢复 EC 默认。" % offset)


def main():
    if not is_admin():
        print("错误：请以管理员身份运行。")
        return
    if len(sys.argv) < 2:
        print(__doc__)
        return
    cmd = sys.argv[1].lower()
    try:
        h = open_portio()
    except EcError as e:
        print("错误：", e)
        return
    try:
        if cmd == "read":
            if len(sys.argv) < 3:
                print("用法: read <offset>"); return
            cmd_read(h, int(sys.argv[2], 0))
        elif cmd == "scan":
            cmd_scan(h)
        elif cmd == "once":
            if len(sys.argv) < 4:
                print("用法: once <offset> <value> [--force]"); return
            force = "--force" in sys.argv
            cmd_once(h, int(sys.argv[2], 0), int(sys.argv[3], 0), force)
        elif cmd == "restore":
            if len(sys.argv) < 3:
                print("用法: restore <offset>"); return
            cmd_restore(h, int(sys.argv[2], 0))
        elif cmd == "lock":
            if len(sys.argv) < 4:
                print("用法: lock <offset> <value> [--force] [--yes]"); return
            force = "--force" in sys.argv
            yes = "--yes" in sys.argv
            cmd_lock(h, int(sys.argv[2], 0), int(sys.argv[3], 0), force, yes)
        else:
            print(__doc__)
    finally:
        h.close()


if __name__ == "__main__":
    main()
