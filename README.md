# AutoSleep

![Build Status](https://github.com/Cesium-developer/AutoSleep/actions/workflows/build.yml/badge.svg)

![设置界面](docs/images/settings.png)

> 🤖 **AI 生成声明**：本项目代码由 AI（DeepSeek）辅助生成，最终由作者完成测试、调试和打包。所有源码以明文形式提供，欢迎审计。

Windows 智能电源管理守护工具：多条件感知，任务完成后自动睡眠/休眠。

**PowerShell 版（原版）**：适用于 Windows 10 1803+ / Windows 11。
**C# 版**：适用于 Windows 7（需安装 .NET Framework 4.0），位于 `csharp/` 目录，功能与 PowerShell 版一致。

---

## 项目结构

| 文件                        | 用途                                   |
| ------------------------- | ------------------------------------ |
| `AutoSleep.ps1`           | 主监控脚本（条件检测 + 触发睡眠/休眠）                |
| `Deploy-AutoSleep.ps1`    | 部署脚本（复制文件、创建计划任务、写注册表）               |
| `Settings.ps1`            | 图形化设置界面（WinForms）                    |
| `Uninstall-AutoSleep.ps1` | 卸载逻辑（删除快捷方式、计划任务、注册表、自身）             |
| `ClearLog.ps1`            | 日志轮转（重置日志）                           |
| `http_server.ps1`         | 自定义规则设置（通过图形化编程）                     |
| `Setup.nsi`               | NSIS 安装包脚本（生成 `AutoSleep_Setup.exe`） |
| `Uninstall.nsi`           | NSIS 卸载包脚本（生成 `Uninstall.exe`）       |
| `README.txt`              | 用户手册（打包进安装包）                         |

### C# 版（`csharp/`）

|||
|---|---|
| `src/AutoSleep.Core/`    | 主监控程序（`AutoSleep.exe`）                 |
| `src/AutoSleep.Settings/` | 设置界面（`AutoSleepSettings.exe`）           |
| `src/AutoSleep.Server/`  | 自定义规则 API 服务（`AutoSleepServer.exe`）  |
| `src/AutoSleep.Deploy/`  | 安装部署程序（`AutoSleepDeploy.exe`）         |
| `src/AutoSleep.Uninstall/` | 卸载程序（`Uninstall.exe`）                 |
| `installer/Setup.nsi`    | NSIS 安装包脚本（生成 `AutoSleep_Setup_Win7_Net40.exe`） |
| `build.bat`              | 本地编译脚本                                   |

---

## 系统要求

- Windows 10 1803+ / Windows 11
- PowerShell 5.1+
- 管理员权限（部署和运行需要）

### C# 版

- Windows 7 SP1 及以上
- .NET Framework 4.0（Win7 SP1 自带）
- 管理员权限（部署和运行需要）

---

## 从源码构建安装包

### 前置条件

1. 安装 [NSIS](https://nsis.sourceforge.io/Download)（Nullsoft Scriptable Install System）
2. 确保 NSIS 的 `makensis.exe` 在系统 PATH 中，或右键 `.nsi` 文件有 “Compile NSIS Script” 菜单

### 构建步骤

1. **准备源码目录**  
- 将以下所有文件放在同一个文件夹中：
- AutoSleep.ps1
- Deploy-AutoSleep.ps1
- Settings.ps1
- Uninstall-AutoSleep.ps1
- ClearLog.ps1
- http_server.ps1
- README.txt
- Setup.nsi
- Uninstall.nsi
2. **编译 Uninstall.exe（卸载程序）**  
- 右键 `Uninstall.nsi` → 选择 “Compile NSIS Script”  
- 或在命令行执行：
- makensis Uninstall.nsi
- 生成 `Uninstall.exe`
3. **编译 AutoSleep_Setup.exe（安装包）**  
- 确保 `Uninstall.exe` 已经在源码目录中  
- 右键 `Setup.nsi` → 选择 “Compile NSIS Script”  
- 或在命令行执行：
- makensis Setup.nsi
- 生成 `AutoSleep_Setup.exe`

### 最终产物

- `AutoSleep_Setup.exe`：用户安装包（双击即可部署）
- `Uninstall.exe`：卸载程序（在安装过程中由部署脚本复制到目标目录）

### C# 版

#### 前置条件

- .NET Framework 4.0 SDK 或 Visual Studio 2010+（MSBuild）
- NSIS 3.x

#### 构建步骤

```cmd
cd csharp
build.bat
```

或手动执行：

```cmd
cd csharp
C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe src\AutoSleep.Core\AutoSleep.Core.csproj /p:Configuration=Release
C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe src\AutoSleep.Settings\AutoSleep.Settings.csproj /p:Configuration=Release
C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe src\AutoSleep.Server\AutoSleep.Server.csproj /p:Configuration=Release
C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe src\AutoSleep.Uninstall\AutoSleep.Uninstall.csproj /p:Configuration=Release
C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe src\AutoSleep.Deploy\AutoSleep.Deploy.csproj /p:Configuration=Release
```

然后编译安装包：

```cmd
cd csharp\installer
makensis Setup.nsi
```

#### 最终产物

- `AutoSleep_Setup_Win7_Net40.exe`：Win7 用户安装包（双击即可部署）

---

## 部署原理

`AutoSleep_Setup.exe` 的本质是 NSIS 自解压包，它会：

1. 解压所有文件到 `%TEMP%\AutoSleepInstall`
2. 以管理员权限执行 `Deploy-AutoSleep.ps1`
3. `Deploy-AutoSleep.ps1` 完成：
- 复制 `AutoSleep.ps1`、`Settings.ps1`、`README.txt`、`Uninstall.exe`、`ClearLog.ps1` 到 `C:\ProgramData\AutoSleep`
- 生成默认 `settings.json`
- 创建桌面快捷方式 “AutoSleep 设置”
- 创建计划任务 `AutoSleep`（开机 + 登录启动）
- 写入 `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\AutoSleep` 注册表项
- 生成备用卸载脚本 `Uninstall-AutoSleep.ps1`
- 验证部署（检查日志是否生成）
4. NSIS 删除临时目录 `%TEMP%\AutoSleepInstall`

---

## 卸载原理

`Uninstall.exe`（由 `Uninstall.nsi` 生成）会：

1. 解压 `Uninstall-AutoSleep.ps1` 到 `%TEMP%`
2. 以管理员权限执行它
3. `Uninstall-AutoSleep.ps1` 完成：
- 强制结束所有 AutoSleep 后台进程
- 删除桌面快捷方式
- 删除计划任务 `AutoSleep`
- 删除注册表卸载项（64 位和 32 位视图，`/reg:64` 和 `/reg:32`）
- 删除安装目录下除 `Uninstall.exe` 自身外的所有文件
- 启动后台批处理，删除 `Uninstall.exe` 自身和空目录
4. 删除临时脚本 `Uninstall-AutoSleep.ps1`

---

## 技术亮点

- **绿色卸载**：无残留
- **唤醒恢复**：唤醒后自动重置计时器，不需要手动重启程序
- **多条件组合**：CPU / GPU / 网络 / 磁盘 / 用户活动 / 进程白名单 六重判断
- **时间窗口**：支持指定时间段内才触发
- **倒计时取消**：触发前弹出倒计时窗口，用户可取消本次睡眠
- **可选休眠开关**：避免不必要的磁盘占用

---

## 用户手册

### 这是什么？

AutoSleep 是一个 Windows 后台守护工具，它通过监控 CPU、GPU、磁盘、网络、用户输入和进程白名单，智能判断电脑是否真正空闲，当所有条件持续满足你设定的时间后，自动执行睡眠或休眠。

### 适用场景

- 下载机 / NAS 备份机：下载任务完成后自动睡眠，不用守着关机
- 渲染节点：Blender、FFmpeg 等渲染完成后自动断电，不空转耗电
- 游戏挂机：游戏掉线或进程退出后，自动休眠止损
- 编译/解压任务：大型项目编译完成后自动进入睡眠
- 任何需要“任务结束就断电”的自动化场景

### 核心功能

**多维度空闲检测**

AutoSleep 同时监控 6 个维度，全部满足且持续达到设定时间后才会触发：

| 检测项     | 默认值          | 说明                             |
| ------- | ------------ | ------------------------------ |
| CPU 使用率 | 低于 30%       | 通过性能计数器读取，可靠稳定                 |
| GPU 使用率 | 低于 30%       | 支持 NVIDIA/AMD/Intel，不可用时自动降级忽略 |
| 磁盘读写速度  | 低于 10 MB/s   | 全磁盘总和，包括所有物理硬盘和 SSD            |
| 网络流量    | 低于 1024 KB/s | 所有网卡的总流量（含无线/有线）               |
| 用户活动    | 鼠标键盘静止超过 3 秒 | 通过系统 API 精确检测                  |
| 进程白名单   | 默认关闭         | 指定进程存在时永不触发，支持模糊匹配             |

**时间窗口（可选）**

你可以指定一个时间段，让 AutoSleep 只在这个时间段内允许触发（例如凌晨 2:00 到 7:00）。超出时间窗口时，计时器会自动重置，电脑不会进入睡眠。

**睡眠/休眠模式切换**

- 睡眠（Sleep）：内存供电，硬盘停转，唤醒极快（1~3 秒），适合短时间离开
- 休眠（Hibernate）：内存写入硬盘，完全断电，唤醒稍慢（20~30 秒），适合长时间离开或笔记本

**自定义规则**

- 通过图形化编程，达到你希望的功能

### 安装方法

1. 根据系统选择安装包：
   - **Windows 10 1803+ / Windows 11**：双击 `AutoSleep_Setup.exe`
   - **Windows 7（需 .NET Framework 4.0）**：双击 `AutoSleep_Setup_Win7_Net40.exe`
2. 安装程序会自动完成：
- 复制所有文件到 `C:\ProgramData\AutoSleep`
- 创建计划任务（开机 + 登录自动启动）
- 桌面生成 “AutoSleep 设置” 快捷方式
- 注册到 Windows “已安装的应用” 列表（可正常卸载）
3. 安装完成后，桌面会出现 “AutoSleep 设置” 图标，双击即可调整参数

### 设置界面说明

打开桌面快捷方式 “AutoSleep 设置” 后，你会看到以下选项：

**基本设置**

- 休眠/睡眠模式：选择 `Sleep` 或 `Hibernate`
- 空闲等待时间（分钟）：所有条件满足后持续多久才触发，默认 15 分钟
- 检测间隔（秒）：扫描频率，默认 5 秒，最小 3 秒

**阈值设置**

- CPU 阈值（%）：CPU 使用率低于此值视为空闲
- GPU 阈值（%）：GPU 使用率低于此值视为空闲。如果系统不支持 GPU 计数器，可关闭此检测

**功能开关**

- 启用 GPU 检测：取消勾选后，GPU 不再参与空闲判断（适合虚拟机或老旧显卡）
- 启用用户活动检测：取消勾选后，鼠标键盘不再阻止睡眠（不推荐）
- 启用网络活动检测：勾选后，当网速超过阈值（默认 1024 KB/s）视为忙碌
- 启用磁盘活动检测：勾选后，当磁盘读写速度超过阈值（默认 10 MB/s）视为忙碌

**高级功能**

- 进程白名单：输入进程名（不含 `.exe`），只要该进程存在，永不休眠。支持模糊匹配（如输入 `qbit` 可匹配 `qBittorrent.exe`）
- 时间窗口：只在指定时间段内允许触发，例如 02:00 到 07:00
- 自定义规则：允许用户自定义条件组合和嵌套等等

### 常见问题

**Q：安装后怎么验证它在运行？**

打开任务管理器，查看是否有 `powershell.exe` 进程，其命令行中包含 `AutoSleep.ps1`。或者查看日志文件 `C:\ProgramData\AutoSleep\AutoSleep.log`，如果有内容输出，说明正在运行。

**Q：唤醒后脚本还在吗？**

在。计划任务保证了脚本在开机、登录时启动。同时脚本内置了唤醒检测机制，睡眠唤醒后会自动重置计时器，确保不会误触发。

**Q：日志文件会撑爆硬盘吗？**

不会。每天约 1~2 MB，一个月约 30~60 MB，可忽略。你可以在设置界面点击 “清空日志” 按钮手动清理。

**Q：为什么我设了 15 分钟，但感觉时间不准？**

本工具检测的是“连续空闲”，任何瞬时波动（CPU 峰值、网络跳变、磁盘写入）都会重置计时器。这是设计行为——确保电脑真正“没事干”才触发。

**Q：我选了 Sleep，但实际上表现为休眠？**

这是正常现象，原因分两种情况：

- 情况一（常见）：某些笔记本（尤其是 2020 年后的新款）仅支持 S0 现代待机，不支持传统 S3 睡眠。Windows 会将 Sleep 请求映射为休眠（S4），导致唤醒时出现恢复界面。
- 情况二：系统电源设置中启用了 “混合睡眠”，导致睡眠一定时间后自动转为休眠。

建议：如果你的笔记本是 2020 年以后出厂的，建议直接使用 Hibernate 模式，因为你的硬件根本不能进入真正意义上的 S3 睡眠。

**Q：怎么判断我的笔记本支持 S3 还是 S0？**

以管理员身份运行 CMD，输入 `powercfg -a`。如果显示 “待机 (S3) 可用”，说明支持传统睡眠；如果只显示 “待机 (S0 低电量待机)”，说明仅支持现代待机，Sleep 会被系统映射为休眠。

**Q：如何卸载？**

两种方式：

- 方式一：Windows 设置 → 应用 → 已安装的应用 → 找到 “AutoSleep 智能休眠工具” → 卸载
- 方式二：运行 `C:\ProgramData\AutoSleep\Uninstall.exe`

卸载后，所有文件、计划任务、注册表项都会被删除，无残留。

**Q：它会在后台产生网络流量吗？**

不会。本工具只读取网卡计数器，不发送任何数据。

### 已知限制

- PowerShell 版：不支持 Windows 7（PowerShell 版本过低）
- C# 版：支持 Windows 7（需 .NET Framework 4.0，可从https://dotnet.microsoft.com/zh-cn/download/dotnet-framework/thank-you/net40-offline-installer下载离线版并安装）
- Windows 10 1809 以下版本：GPU 计数器可能不可用，请关闭 GPU 检测
- 仅支持 S0 现代待机的笔记本：Sleep 模式会被系统映射为休眠（S4），这是微软的设计限制，非工具 bug
- 虚拟机环境：GPU 和磁盘计数器可能不可用，请关闭相应检测
- 物理合盖动作：本工具不拦截合盖，请自行设置电源选项
- 系统电源设置：本工具不拦截睡眠/休眠，请自行调整系统设置中的电源选项（多少时间睡眠/休眠）

### 安全性与透明

- 所有逻辑公开透明，可以自行审计源码，并且release中附带sha256文件用于校验
- 无加密、无混淆、无隐藏行为
- 不依赖外部服务器：无需联网，完全本地运行
- 卸载后无残留

### 高级用户：手动编辑配置

你可以直接编辑 `C:\ProgramData\AutoSleep\settings.json`，调整以下参数：

```json
{
"PowerAction": "Hibernate",
"DurationMin": 15,
"CpuThreshold": 30,
"GpuThreshold": 30,
"Interval": 5,
"EnableGpuCheck": true,
"EnableUserActivity": true,
"EnableNetworkCheck": true,
"NetworkThresholdKBps": 1024,
"EnableDiskCheck": true,
"DiskThresholdKBps": 10240,
"EnableProcessCheck": false,
"ProtectedProcesses": ["qbit", "ffmpeg"],
"EnableTimeWindow": false,
"TimeWindowStart": 2,
"TimeWindowEnd": 7,
"ClearLogOnNextRun": false,
"EnableLogRotation": true,
"LogRotationDays": 30,
"CustomLogicEnabled": false,
"CustomLogicTree": null
}
```

修改后需要重启脚本，因此更推荐通过桌面快捷方式（AutoSleep设置）进行修改并且保存，可以自动进行重启的同时，其中已经涵盖了绝大多数参数的设置。

### 许可证

本项目为个人开源工具，使用 MIT License，源码公开，欢迎 fork 和修改。

### 致谢

感谢所有测试和反馈的用户。AutoSleep 不是一个复杂的软件，但它在后台默默守护着你的电脑——只在你真正需要休息的时候，才让它也休息一下。

如果它帮到了你，欢迎告诉你的朋友。
