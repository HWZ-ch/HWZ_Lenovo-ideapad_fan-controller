# 联想「降温除尘」连续狂吹 — FanControl + Lenovo 插件 配置指南

> 目标：让风扇持续保持最高转速，尽可能消除官方「9 秒高速 + 2 秒停顿」除尘周期带来的间隔。  
> 适用：联想小新 / IdeaPad 等老款（已安装 `Lenovo ACPI-Compliant Virtual Power Controller` 驱动，即系统存在 `\\.\EnergyDrv`）。  
> 权限：FanControl 与插件均需**管理员身份**运行。

---

## 一、下载

1. **FanControl（解压即用）**  
   <https://github.com/Rem0o/FanControl.Releases>  
   下载最新 `FanControl.zip`，解压到任意目录（如 `D:\Tools\FanControl`）。
2. **Lenovo 插件 DLL**  
   <https://github.com/jiarandiana0307/FanControl.LenovoPlugin/releases>  
   下载 `FanControl.LenovoPlugin.dll`。

---

## 二、安装插件

1. 右键 `FanControl.exe` → **以管理员身份运行**。
2. 首次启动会扫描硬件，进入主界面后点左上角菜单 → **Settings**。
3. 在 Settings 中找到 **Install plugin…** 按钮，选择下载的 `FanControl.LenovoPlugin.dll`。
4. **重启 FanControl**（关闭后再次以管理员运行）。
5. 重启后若出现弹窗 `Lenovo Fan Control failed to open \\.\EnergyDrv.`  
   → 你的机型不被该插件支持，**直接走方案 B（EC 直写）**，请联系我并提供具体型号。

---

## 三、配置为持续最大转速

该插件只有三档（依据 FanControl 设定的百分比映射）：

| FanControl 设定 | 插件行为             |
| ------------- | ---------------- |
| ≤ 20%         | 最小转速             |
| 20% ~ 80%     | 正常转速             |
| **≥ 80%**     | **最大转速（即除尘高速档）** |

操作：

1. 主界面找到风扇控制卡片（来源显示为 Lenovo）。
2. 将该风扇的曲线 / 固定值设为 **100%**（至少 ≥ 80%）。
   - 推荐建一条 **Constant 100%** 曲线，避免温度联动导致降速。
3. 应用后，风扇应进入最大转速。

---

## 四、验证是否「真正连续」

观察 **1~2 分钟**：

- ✅ **风扇持续拉满、无周期性掉速** → 成功，本机型被插件持续重发覆盖，问题解决。
- ❌ **仍出现每 ~~9~~10 秒一次的短暂停顿（约 2 秒）** → 说明该机型 `EnergyDrv` 接口仍有 EC 物理周期限制，方案 A 无效。  
  → **此时回到对话，提供你的具体联想型号，我为你做方案 B（EC 寄存器直写，真正无间隔连续）。**

---

## 五、回退与注意事项

- 退出 FanControl 后，风扇控制权交还系统，恢复正常自动调速。
- 持续最高转速会增加风扇磨损与耗电，**建议插电使用，风险自担**。
- 若与联想电脑管家冲突，可暂时退出管家后再试。
- 若插件识别不到风扇或仍有间隔，备选为**方案 B：EC 风扇 PWM 寄存器直写**（需提供具体型号，由我逆向寄存器地址后交付单文件工具）。
