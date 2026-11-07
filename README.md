# Lenovo IdeaPad 风扇控制器

让联想小新 / IdeaPad 的风扇**持续保持最高转速**，即把联想电脑管家里那个「降温除尘」功能变成可一键开关、可脚本化的原生工具。

> 逆向与分析机型：**联想小新 16 IRL8**（Lenovo EM 机型，`AcpiVpc.sys` 驱动）

---

## 这是什么

联想电脑管家的「降温除尘」功能会让风扇全速运转约 9 秒、停顿约 2 秒，循环若干次后自动复位。本项目做了三件事：

1. **逆向出它底层的控制通道** —— 走 `Lenovo ACPI-Compliant Virtual Power Controller` 驱动创建的设备，用 `DeviceIoControl` 发除尘命令，不依赖任何第三方 DLL。
2. **提供可用的前端** —— 一个 C# 系统托盘程序（双击即生效）和一个等价的 Python 脚本。
3. **记录一条被证伪的错误路径** —— 网上流传的「写 EC 寄存器 `0xFE` 实现风扇满速」在本机**无效**，见下文。

---

## 实测结论（重要，请先读）

| 结论 | 说明 |
|---|---|
| ✅ **唯一可用通道是 EnergyDrv 设备命令** | 控制 IOCTL `0x831020C0`，入参 `{6, 1, mode}`，`mode=1` 最大档 / `0` 正常档 |
| ✅ 设备名需自动探测 | 不同机型/驱动版本暴露的名称不同：`\\.\EnergyDrv`、`\\.\Lenovo0`、`\\.\Lenovo1`、`\\.\AcpiVpc` |
| ✅ 状态读取用另一条 IOCTL | `0x831020C4`，入参 `{14}`；实测**空闲 ≈ `0x85`(133)，强制生效 ≈ `0x28`(40)**，故 `<100` 视为风扇在转 |
| ❌ **`EC[0xFE]` 不是风扇转速寄存器** | 早期误判。触发真实除尘（风扇必转）时扫描 EC，`0xFE` **始终为 `0x00`**；单独写 `0xFE=0x50` 时热区仅微降、听不到风扇转 |
| ❌ **9s 高速 + 2s 停顿无法消除** | 这是 **EC 固件写死的周期**，任何软件都去不掉，只能通过高频重触发让它尽量接近连续 |
| ❌ **WMI 没有可编程风扇 / 性能模式接口** | 无 `Lenovo_FanMode` / `ThermalMode` / `LENOVO_GAMEZONE` 类；`LENOVO_UTILITY_DATA` 只有 `GetIfSupportOrVersion`，无 setter |

想要**真正零间隙**的连续最大转速，唯一办法是手动开启联想的「性能模式」（`Fn+Q` 或电脑管家），那是固件层面的意图。

---

## 适用机型

- **已验证**：联想小新 16 IRL8
- **社区反馈可用**（同一 EnergyDrv 接口）：IdeaPad 3 / 330 / Y / Z / G 系列、小新 15IIL 2020 等老机型
- **可能不可用**：拯救者 / Yoga / ThinkBook 等新机型——这类通常改走 WMI（`LENOVO_GAMEZONE`），本工具会明确报错提示

---

## 前置条件

三者缺一不可：

1. 系统中有 `Lenovo ACPI-Compliant Virtual Power Controller` 驱动
2. **联想电脑管家 / 联想实用程序正在运行**（设备接口由它创建，关掉就 `CreateFile` 失败）
3. **管理员权限**

---

## 快速开始

### 方式一：C# 托盘程序（推荐）

```powershell
cd src\LenovoCoolBoost
dotnet build -c Release
# 产物: bin\Release\net8.0-windows\LenovoCoolBoost.exe
```

用**管理员身份**运行 `LenovoCoolBoost.exe`：

- 双击即自动开启满速，无需额外操作
- 右下角托盘图标：双击切换开关；右键菜单可「关闭并恢复默认」「开机自启」「退出」
- 睡眠/唤醒自动跟随（休眠前关闭，唤醒后按上次状态恢复）

自检与诊断：

```powershell
LenovoCoolBoost.exe --test   # 无界面自检：开启并采样 180 秒，保持率 ≥90% 算通过；结束后自动恢复
LenovoCoolBoost.exe --diag   # 观测模式：逐秒记录真实读数，长停转时尝试软重启恢复
```

日志位置：`%TEMP%\LenovoCoolBoost.log`；自检写 `%LOCALAPPDATA%\LenovoCoolBoost_test.log`。

> 后台循环以 **600ms** 间隔检测读数，一旦发现掉速立刻做一次 `正常 → 300ms → 最大` 的边沿重置，以此抢在固件 2 秒停顿前重排；另外每约 30 秒无条件强制重置一次作为兜底。

### 方式二：Python 脚本

```powershell
python python\lenovo_cool_boost.py status   # 查看当前档位
python python\lenovo_cool_boost.py once     # 触发一次官方除尘（约 2 分钟后自动停）
python python\lenovo_cool_boost.py start    # 持续激活，Ctrl+C 停止
```

只依赖标准库，不需要第三方包。`start --tray` 需要额外 `pip install pystray pillow`。

---

## 仓库结构

```
src/LenovoCoolBoost/      C# 托盘程序（主交付物）
  FanController.cs          EnergyDrv 设备封装：探测/开关/读状态/保活循环
  Program.cs                托盘 UI、自检(--test)、观测(--diag)
src/lhm_spike/            为确认「读不到真实转速」而做的最小验证程序（LibreHardwareMonitor 探针）
python/lenovo_cool_boost.py   Python 版同后端实现
docs/FanControl_Setup_Guide.md 备选方案：FanControl + Lenovo 插件（GUI，无需写代码）
docs/RESEARCH_NOTES.md    完整逆向过程、被证伪的路径与踩坑记录
tools/                    逆向期的探针脚本（见下方说明）
```

### 关于 `tools/`

这些是逆向过程中用的诊断脚本，**保留仅供复盘，不是推荐使用路径**：

| 脚本 | 用途 |
|---|---|
| `ec_scan_fan.py` | 对比空闲态与强制除尘态的 EC 全部 256 个寄存器 + EnergyDrv 读索引 0~31，找真实转速反馈通道 |
| `ed_probe.py` | 只用 EnergyDrv 读索引，探测哪个索引随风扇启停变化 |
| `ec_fan_lock.py` | 早期「直写 EC 寄存器」方案，**已被证伪**，见 RESEARCH_NOTES |
| `gen_baseline.py` / `ec_baseline.json` | 生成 EC 基线快照 |

> ⚠️ `ec_fan_lock.py` / `ec_scan_fan.py` 依赖 **InpOut32/InpOutx64** 的端口 I/O 驱动（`inpoutx64.dll` + `.sys`）。为保持仓库干净，**本仓库不附带这些二进制文件**，需要时请自行从 <https://www.highrez.co.uk/downloads/inpout32/> 获取，并放到 `tools/inpout/x64/` 下。请注意 InpOut 是第三方驱动，使用时自行确认其许可证与安全风险。

---

## 备选方案：FanControl + 插件

如果你不想用命令行、也不想编译，可以走 GUI 方案：用 [FanControl](https://github.com/Rem0o/FanControl.Releases) 搭配 [FanControl.LenovoPlugin](https://github.com/jiarandiana0307/FanControl.LenovoPlugin/releases)，把风扇固定到 ≥80% 即对应最大档。

完整步骤见 [`docs/FanControl_Setup_Guide.md`](docs/FanControl_Setup_Guide.md)。

---

## 安全与免责声明

- **持续最高转速会显著增加噪音、耗电与风扇磨损**，建议插电时短时使用，用完即关。
- 本工具只把风扇往「更快」的方向设，**不会主动降速**造成过热。
- 涉及内核驱动与设备 IOCTL，**风险自担**；在非目标机型上运行可能无效果或行为异常。
- 与联想电脑管家可能冲突，遇到问题可先退出管家再试。

本项目基于对本人自有设备的逆向分析，与联想公司无关，未使用任何联想官方代码或签名文件。

---

## 参考

- [FanControl](https://github.com/Rem0o/FanControl.Releases) — 通用风扇控制 GUI
- [FanControl.LenovoPlugin](https://github.com/jiarandiana0307/FanControl.LenovoPlugin) — 通过 `\\.\EnergyDrv` 控制联想风扇
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) — 用于验证第三方工具能否读到真实转速

---

[![](https://img.shields.io/badge/powered_by-dsh-4D6BFE?style=flat-square&logo=deepseek&logoColor=white)](https://github.com/deepseek-ai/deepseek-harness)
