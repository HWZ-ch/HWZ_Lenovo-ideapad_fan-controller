#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
定位「真实风扇转速」反馈通道：在空闲态与强制除尘态对比
  (A) EC 全部 256 寄存器
  (B) EnergyDrv 读索引 0..31
找出随风扇启停显著变化、且数值像 RPM 的寄存器/索引。

用法（管理员）:
  python ec_scan_fan.py
"""
import os, sys, time, ctypes
import ctypes.wintypes as wintypes

kernel32 = ctypes.windll.kernel32

GENERIC_READ  = 0x80000000
GENERIC_WRITE = 0x40000000
FILE_SHARE_RW = 0x00000003
OPEN_EXISTING = 0x00000003
FILE_ATTR     = 0x00000080

# ---------- EC 端口 I/O (InpOutx64) ----------
EC_CMD = 0x66
EC_DATA = 0x62
EC_RD = 0x80
EC_WR = 0x81

class EcError(Exception):
    pass

def load_inpout():
    cands = [
        os.path.join(os.path.dirname(os.path.abspath(__file__)), "inpout", "x64", "inpoutx64.dll"),
        os.path.join(os.path.dirname(os.path.abspath(__file__)), "inpoutx64.dll"),
        "inpoutx64.dll",
    ]
    for p in cands:
        if os.path.exists(p):
            dll = ctypes.WinDLL(p)
            dll.Inp32.argtypes = [ctypes.c_short]; dll.Inp32.restype = ctypes.c_short
            dll.Out32.argtypes = [ctypes.c_short, ctypes.c_short]; dll.Out32.restype = None
            dll.Inp32(0x80)  # 触发驱动加载
            return dll
    raise EcError("未找到 inpoutx64.dll")

_dll = load_inpout()
def rp(port): return _dll.Inp32(port & 0xFFFF) & 0xFF
def wp(port, v): _dll.Out32(port & 0xFFFF, v & 0xFF)

def ec_wait_ibf():
    for _ in range(5000):
        if not (rp(EC_CMD) & 0x02): return
        time.sleep(0.00001)
    raise EcError("IBF timeout")

def ec_wait_obf():
    for _ in range(5000):
        if rp(EC_CMD) & 0x01: return
        time.sleep(0.00001)
    raise EcError("OBF timeout")

def ec_read(off, tries=3):
    for _ in range(tries):
        try:
            ec_wait_ibf(); wp(EC_CMD, EC_RD)
            ec_wait_ibf(); wp(EC_DATA, off)
            ec_wait_obf(); v = rp(EC_DATA)
            time.sleep(0.004)
            return v
        except EcError:
            time.sleep(0.01)
    raise EcError("ec_read 0x%02X" % off)

# ---------- EnergyDrv ----------
ED_IOCTL_CTRL = 0x831020C0
ED_IOCTL_READ = 0x831020C4
DEVICES = [r"\\.\EnergyDrv", r"\\.\Lenovo0", r"\\.\Lenovo1", r"\\.\AcpiVpc"]

def ed_open(write):
    acc = GENERIC_WRITE if write else GENERIC_READ
    for dev in DEVICES:
        h = kernel32.CreateFileW(dev, acc, FILE_SHARE_RW, None, OPEN_EXISTING, FILE_ATTR, None)
        if h != wintypes.HANDLE(-1).value and h != 0:
            return h
        if h != wintypes.HANDLE(-1).value:
            kernel32.CloseHandle(h)
    return wintypes.HANDLE(-1).value

def ed_force_once():
    """发送一次除尘(最大)命令，模拟 SetMode(true)。"""
    h = ed_open(True)
    if h == wintypes.HANDLE(-1).value:
        return False
    try:
        buf = (ctypes.c_int * 3)(6, 1, 1)
        br = wintypes.DWORD()
        ok = kernel32.DeviceIoControl(h, ED_IOCTL_CTRL, buf, 12, None, 0, ctypes.byref(br), None)
        return bool(ok)
    finally:
        kernel32.CloseHandle(h)

def ed_read_idx(idx):
    h = ed_open(False)
    if h == wintypes.HANDLE(-1).value:
        return None
    try:
        inp = ctypes.create_string_buffer(4)
        ctypes.memmove(inp, ctypes.byref(ctypes.c_int(idx)), 4)
        outp = ctypes.create_string_buffer(4)
        br = wintypes.DWORD()
        ok = kernel32.DeviceIoControl(h, ED_IOCTL_READ, inp, 4, outp, 4, ctypes.byref(br), None)
        if not ok:
            return None
        return ctypes.cast(outp, ctypes.POINTER(ctypes.c_int))[0] & 0xFFFFFFFF
    finally:
        kernel32.CloseHandle(h)

# ---------- 主流程 ----------
def ec_scan_all():
    return [ec_read(i) for i in range(256)]

def main():
    print("=== 风扇转速反馈通道扫描 ===")
    # A. 空闲基线
    print("[1/4] 采集空闲基线(EC 256 + EnergyDrv 0..31) ...")
    base_ec = ec_scan_all()
    base_ed = [ed_read_idx(i) for i in range(32)]
    print("    空闲 EC 示例: " + ", ".join("0x%02X" % v for v in base_ec[:16]))
    print("    空闲 EnergyDrv: " + ", ".join(_fmt(base_ed)))

    # B. 强制风扇态
    print("[2/4] 强制除尘 25s，持续采样(EC max + EnergyDrv) ...")
    max_ec = list(base_ec)
    forc_ed_samples = []
    t0 = time.time()
    while time.time() - t0 < 25:
        ed_force_once()
        # EnergyDrv 探针（每轮采一次）
        forc_ed_samples.append([ed_read_idx(i) for i in range(32)])
        # EC 全扫（取 max）
        snap = ec_scan_all()
        for i in range(256):
            if snap[i] > max_ec[i]:
                max_ec[i] = snap[i]
        time.sleep(0.8)
    print("    强制采样完成")

    # C. 差分分析
    print("[3/4] 分析 EC 差分(强制max - 空闲基线) ...")
    ec_diff = []
    for i in range(256):
        d = max_ec[i] - base_ec[i]
        if d >= 4 or max_ec[i] >= 200:   # 明显变化或像 RPM 的大值
            ec_diff.append((i, base_ec[i], max_ec[i], d))
    ec_diff.sort(key=lambda x: -abs(x[3]))
    for off, b, m, d in ec_diff[:25]:
        print("    EC[0x%02X] 空闲=0x%02X 强制max=0x%02X 差=%+d" % (off, b, m, d))

    print("[4/4] 分析 EnergyDrv 索引差分 ...")
    if forc_ed_samples:
        avg_ed = []
        for i in range(32):
            vals = [s[i] for s in forc_ed_samples if s[i] is not None]
            avg_ed.append(round(sum(vals)/len(vals), 1) if vals else None)
        for i in range(32):
            b = base_ed[i]; f = avg_ed[i]
            if b is None or f is None:
                continue
            d = (f if isinstance(f, (int, float)) else 0) - (b if isinstance(b, (int, float)) else 0)
            if abs(d) >= 1 or (f is not None and f >= 200):
                print("    EDidx[%d] 空闲=%s 强制avg=%s 差=%+s" % (i, _fmt1(b), _fmt1(f), _fmt1(d)))

    print("=== 扫描结束 ===")

def _fmt(vals):
    out = []
    for v in vals:
        out.append("None" if v is None else ("0x%X" % v))
    return ", ".join(out)

def _fmt1(v):
    if v is None: return "None"
    if isinstance(v, float): return "%.1f" % v
    return "0x%X" % v

if __name__ == "__main__":
    try:
        main()
    except EcError as e:
        print("EC 错误: %s" % e)
        sys.exit(1)
