# Lenovo IdeaPad 风扇控制器

让联想小新 / IdeaPad 的风扇**持续保持最高转速**，即把联想电脑管家里那个「降温除尘」功能变成可一键开关、可脚本化的原生工具。

> 逆向与分析机型：**联想小新 16 IRL8**（Lenovo EM 机型，`AcpiVpc.sys` 驱动）
>
> 当前版本 **v1.2.1** —— 变更明细见 [CHANGELOG.md](CHANGELOG.md)

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
| ✅ 状态读取用另一条 IOCTL | `0x831020C4`，入参 `{14}`；本机实测**空闲 ≈ `0x85`(133)，强制生效 ≈ `0x23`~`0x28`(35~40)**。阈值由启动时校准得出，而非写死 |
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

### ⚠️ 先看这条：不要从受管目录运行

如果把这个 exe 放在某些**受管/沙箱目录**（例如自动化工具的工作区目录）里运行，
进程会受到限制 —— 表现为**托盘图标不出现、日志文件也写不出来**，而窗口看起来又是正常的。

实测验证：同一个 exe 放到 `%TEMP%` 或 `%LOCALAPPDATA%` 下运行，图标与日志全部正常。

**推荐安装位置：`%LOCALAPPDATA%\LenovoCoolBoost\`**（本项目已装在此处，并建了桌面快捷方式）。

### 方式一：C# 桌面程序（推荐）

```powershell
cd src\LenovoCoolBoost
dotnet build -c Release

# 发布成单文件，并装到工作区之外的位置
dotnet publish -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:DebugType=none `
  -o "$env:LOCALAPPDATA\LenovoCoolBoost"
```

启动时若未以管理员身份运行，程序会**自动通过 UAC 请求提权**（加 `--no-elevate` 可跳过）。

打开后是一个控制窗口：

- 顶部实时显示状态（满速运行中 / 正常转速）、设备名与当前读数
- **「开启满速(降温除尘)」** 与 **「停止并恢复默认转速」** 两个按钮
- 开机自启、隐藏到托盘、退出
- 双击运行时自动开启满速；睡眠/唤醒自动跟随

> 点窗口右上角 X **不会退出**，而是缩到托盘并弹提示（防止你以为已停止、风扇却还在转）。
> 要真正停止，用「停止并恢复默认转速」；要退出，用「退出」按钮 —— 退出时会先恢复默认转速。

托盘图标也提供同样的菜单。如果系统把托盘图标折叠进了「^」溢出区，点 ^ 找到它后
拖动到任务栏即可固定；也可以直接用窗口操作，不必依赖图标。

命令行：

```powershell
LenovoCoolBoost.exe --tray    # 启动后只驻留托盘，不显示窗口
LenovoCoolBoost.exe --status  # 只读打印设备名/状态/阈值/权限，不改风扇状态
LenovoCoolBoost.exe --off     # 一次性恢复默认转速（善后，不影响正在运行的实例）
LenovoCoolBoost.exe --test    # 无界面自检：开启并采样 180 秒，保持率 ≥90% 算通过；结束后自动恢复
LenovoCoolBoost.exe --diag    # 观测模式：逐秒记录真实读数，长停转时尝试软重启恢复
```

> `--status` 的提示：程序是 GUI 子系统（WinExe），从 PowerShell 直接调用不会等待它结束。
> 想稳定拿到输出请用：
> ```powershell
> Start-Process .\LenovoCoolBoost.exe -ArgumentList '--status' -NoNewWindow -Wait
> ```

日志位置：`%TEMP%\LenovoCoolBoost.log`；自检写 `%LOCALAPPDATA%\LenovoCoolBoost_test.log`。

> **阈值是校准出来的，不是写死的。** 开启时会先采样「正常档」和「最大档」两个读数并取中点作阈值，
> 同时自动判定「低值=在转」还是「高值=在转」，所以 `--status` 会显示 `[默认阈值]` 还是 `[已校准]`。
> 本机校准结果：正常 `0x85`(133)、最大 `0x23`(35)、阈值 `84.0`。
>
> 校准采用「轮询到读数稳定 + 两档差异校验」：切档后 EC 不会立刻反映新转速，固定等一小会儿就读
> 很容易采到 `0x87` 这类过渡值从而把判据方向算反。校准失败时会回退默认阈值，此时程序**不会**
> 依据读数判断，只做周期性刷新与重排，避免误判导致反复切档。
>
> 后台循环以 **600ms** 间隔刷新最大档命令，一旦发现掉速立刻做一次 `正常 → 300ms → 最大` 的边沿重置，以此抢在固件 2 秒停顿前重排；另外每约 30 秒无条件强制重置一次作为兜底。

### 方式二：Python 脚本

```powershell
python python\lenovo_cool_boost.py status   # 查看当前档位（只读，不会改变风扇状态）
python python\lenovo_cool_boost.py once     # 触发一次官方除尘（约 2 分钟后自动停）
python python\lenovo_cool_boost.py start    # 持续激活（先校准再保活），Ctrl+C 停止
```

只依赖标准库，不需要第三方包。`start --tray` 需要额外 `pip install pystray pillow`。

---

## 仓库结构

```
src/LenovoCoolBoost/      C# 桌面程序（主交付物）
  FanController.cs          EnergyDrv 设备封装：探测/校准/开关/读状态/保活循环
  Program.cs                控制窗口、托盘、--status/--off、自检(--test)、观测(--diag)
  app.ico                   应用图标（16~256 多尺寸，由 tools/gen_icon.py 生成）
  LenovoCoolBoost-signing.cer  代码签名证书公钥（自签名，见「代码签名」一节）
src/lhm_spike/            为确认「读不到真实转速」而做的最小验证程序（LibreHardwareMonitor 探针）
python/lenovo_cool_boost.py   Python 版同后端实现
docs/FanControl_Setup_Guide.md 备选方案：FanControl + Lenovo 插件（GUI，无需写代码）
docs/RESEARCH_NOTES.md    完整逆向过程、被证伪的路径与踩坑记录
CHANGELOG.md              版本变更记录
tools/                    逆向期的探针脚本 + 图标生成（见下方说明）
```

### 关于 `tools/`

| 脚本 | 用途 |
|---|---|
| `gen_icon.py` | 生成 `app.ico`（雪花图标，多尺寸）。改图标后重跑此脚本再构建 |
| `ec_scan_fan.py` | 对比空闲态与强制除尘态的 EC 全部 256 个寄存器 + EnergyDrv 读索引 0~31，找真实转速反馈通道 |
| `ed_probe.py` | 只用 EnergyDrv 读索引，探测哪个索引随风扇启停变化 |
| `ec_fan_lock.py` | 早期「直写 EC 寄存器」方案，**已被证伪**，见 RESEARCH_NOTES |
| `gen_baseline.py` / `ec_baseline.json` | 生成 EC 基线快照 |

> 除 `gen_icon.py` 外都是逆向期的诊断脚本，**保留仅供复盘，不是推荐使用路径**。

> ⚠️ `ec_fan_lock.py` / `ec_scan_fan.py` 依赖 **InpOut32/InpOutx64** 的端口 I/O 驱动（`inpoutx64.dll` + `.sys`）。为保持仓库干净，**本仓库不附带这些二进制文件**，需要时请自行从 <https://www.highrez.co.uk/downloads/inpout32/> 获取，并放到 `tools/inpout/x64/` 下。请注意 InpOut 是第三方驱动，使用时自行确认其许可证与安全风险。

---

## 托盘图标

Windows 11 默认把新应用的托盘图标折叠进任务栏的「^」溢出区。要让图标直接显示在任务栏上：

```powershell
# 1) 先运行一次程序，让系统登记该 exe 的托盘记录
# 2) 找到对应项并把 IsPromoted 设为 1
$base = 'HKCU:\Control Panel\NotifyIconSettings'
$exe  = "$env:LOCALAPPDATA\LenovoCoolBoost\LenovoCoolBoost.exe"
Get-ChildItem $base | ForEach-Object {
    $p = Get-ItemProperty $_.PSPath
    if ($p.ExecutablePath -eq $exe) {
        New-ItemProperty -Path $_.PSPath -Name IsPromoted -Value 1 -PropertyType DWord -Force
    }
}
# 3) 退出程序再启动，图标即出现在任务栏托盘区
```

也可以不等：点任务栏的「^」，把雪花图标**拖到任务栏**即可固定（Windows 会记住）。

如果图标怎么都不出现，先确认是不是从受管目录运行的（见上方「先看这条」）。

---

## 代码签名

发布版 exe 已用自签名证书签名，并带 DigiCert 的 RFC3161 时间戳。

```powershell
# 查看签名状态
Get-AuthenticodeSignature .\LenovoCoolBoost.exe

# 用 SDK 的 signtool 详细验证
signtool verify /pa /v .\LenovoCoolBoost.exe
```

**签名者**：`CN=HWZ Lenovo Cool Boost, O=HWZ, C=CN`，RSA 3072 / SHA-256，有效期至 2031-11-07。

### ⚠️ 自签名 ≠ 全球可信

自签名证书只在**安装了该证书的机器**上被视为可信。在别人的电脑上运行，Windows 仍会
提示「未知发布者」，因为它的根证书不在系统信任列表里。要在其他机器上消除警告，需要：

```powershell
# 以管理员身份，导入随仓库提供的公钥证书
certutil -addstore -f Root .\src\LenovoCoolBoost\LenovoCoolBoost-signing.cer
certutil -addstore -f TrustedPublisher .\src\LenovoCoolBoost\LenovoCoolBoost-signing.cer
```

> 导入自签名根证书意味着**这台机器将信任该证书签名的任何程序**。只在你确认来源可信时这样做。

要彻底消除所有机器上的警告，只能购买 CA（DigiCert / Sectigo / GlobalSign 等）签发的
代码签名证书，通常需要企业资质审核，并可能要求硬件令牌 —— 这不是本项目能提供的。

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
