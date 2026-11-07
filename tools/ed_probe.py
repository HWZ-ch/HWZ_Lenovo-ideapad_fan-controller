#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""仅用 EnergyDrv 读索引探测：空闲 vs 强制风扇，找随风扇变化/像 RPM 的索引。"""
import os, sys, time, ctypes
import ctypes.wintypes as wintypes

kernel32 = ctypes.windll.kernel32
GENERIC_READ=0x80000000; GENERIC_WRITE=0x40000000
FILE_SHARE_RW=0x00000003; OPEN_EXISTING=0x00000003; FILE_ATTR=0x00000080
ED_IOCTL_CTRL=0x831020C0; ED_IOCTL_READ=0x831020C4
DEVICES=[r"\\.\EnergyDrv", r"\\.\Lenovo0", r"\\.\Lenovo1", r"\\.\AcpiVpc"]

def ed_open(write):
    acc=GENERIC_WRITE if write else GENERIC_READ
    for dev in DEVICES:
        h=kernel32.CreateFileW(dev, acc, FILE_SHARE_RW, None, OPEN_EXISTING, FILE_ATTR, None)
        if h!=wintypes.HANDLE(-1).value and h!=0: return h
        if h!=wintypes.HANDLE(-1).value: kernel32.CloseHandle(h)
    return wintypes.HANDLE(-1).value

def ed_force_once():
    h=ed_open(True)
    if h==wintypes.HANDLE(-1).value: return False
    try:
        buf=(ctypes.c_int*3)(6,1,1); br=wintypes.DWORD()
        return bool(kernel32.DeviceIoControl(h, ED_IOCTL_CTRL, buf, 12, None, 0, ctypes.byref(br), None))
    finally: kernel32.CloseHandle(h)

def ed_read_idx(idx):
    h=ed_open(False)
    if h==wintypes.HANDLE(-1).value: return None
    try:
        inp=ctypes.create_string_buffer(4); ctypes.memmove(inp, ctypes.byref(ctypes.c_int(idx)),4)
        outp=ctypes.create_string_buffer(4); br=wintypes.DWORD()
        if not kernel32.DeviceIoControl(h, ED_IOCTL_READ, inp, 4, outp, 4, ctypes.byref(br), None): return None
        return ctypes.cast(outp, ctypes.POINTER(ctypes.c_int))[0] & 0xFFFFFFFF
    finally: kernel32.CloseHandle(h)

def probe(idle_samples=8, force_sec=20, idx_max=64):
    print("空闲采样 %d 次..." % idle_samples)
    idle=[]
    for _ in range(idle_samples):
        idle.append([ed_read_idx(i) for i in range(idx_max)])
        time.sleep(0.3)
    base=[]
    for i in range(idx_max):
        vals=[s[i] for s in idle if s[i] is not None]
        base.append(round(sum(vals)/len(vals),1) if vals else None)
    print("空闲基线(前16):", [_fmt(base[:16])])
    print("强制风扇 %ds..." % force_sec)
    forc=[]
    t0=time.time()
    while time.time()-t0<force_sec:
        ed_force_once()
        forc.append([ed_read_idx(i) for i in range(idx_max)])
        time.sleep(0.5)
    # 统计：每个索引的强制态 min/max/avg
    print("差异索引(强制 与 空闲 不同):")
    for i in range(idx_max):
        fv=[s[i] for s in forc if s[i] is not None]
        if not fv: continue
        favg=round(sum(fv)/len(fv),1); fmax=max(fv); fmin=min(fv)
        b=base[i]
        if b is None: 
            if any(v!=0 for v in fv): print("  idx[%d] 空闲=None 强制avg=%.1f min=%d max=%d"%(i,favg,fmin,fmax))
            continue
        if abs(favg-b)>=1 or (isinstance(favg,(int,float)) and favg>=200):
            print("  idx[%d] 空闲=%s 强制avg=%.1f min=%d max=%d" % (i, _fmt1(b), favg, fmin, fmax))

def _fmt(vals):
    return ", ".join(_fmt1(v) for v in vals)
def _fmt1(v):
    if v is None: return "None"
    if isinstance(v,float): return "%.1f"%v
    return "0x%X"%v

if __name__=="__main__":
    probe()
    print("done")
