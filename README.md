# AnbyPet 安比桌宠

AnbyPet 是一个 Windows x64 桌面宠物与监测面板，使用 C#、WinForms 和原生 Windows API。将单文件 `AnbyPet.exe` 放到有写入权限的目录即可运行，托盘常驻时仍可按需打开监测面板和独立安比窗口。

[下载 v1.0.0](https://github.com/SaiBoPangHu/anby/releases/tag/v1.0.0)

## 开始使用

1. 下载 `AnbyPet.exe`，或解压 `AnbyPet-v1.0.0-windows-x64.zip`。可将程序放在 `D:\Apps\AnbyPet\` 等目录。
2. 双击 EXE。左键点击托盘图标打开监测面板，右键可显示或隐藏安比、管理应用、切换「登录时启动」或退出。重复运行同一路径的 EXE 会唤出已有实例的监测面板。
3. 首次手动运行默认启用当前 Windows 用户登录后启动。需要移除程序时，先在托盘菜单取消「登录时启动」，再退出程序并移除 EXE 和所需清理的同名数据目录。

## 当前功能

| 功能 | 当前实现 |
| --- | --- |
| 系统监测 | 显示 CPU 占用、第一块硬件 GPU 的占用，以及 `HH:mm:ss` 格式的本地时间。 |
| 游戏帧率 | 内置 PresentMon，通过 ETW 采集前台进程的呈现事件并估算实际显示帧率。没有有效事件或采集不可用时，帧率卡片显示小安比。 |
| 双屏避让 | 主屏出现全屏窗口时，主屏上的监测面板会移到副屏左下角，独立安比也会避让。两者同时显示时尝试错开，退出全屏后自动移动过的窗口回到原位。跨屏放置按显示工作区与缩放比例计算。 |
| AI 额度 | 自动检测 Codex 与 Antigravity 的运行状态。桌宠可独立运行，每个客户端只在检测到它运行时才查询对应额度。卡片展示剩余比例、重置时间和数据来源。 |
| 应用管理 | 可从监测面板或托盘启动与退出已安装的 Codex、Antigravity。正常退出后如有残留进程，程序会询问是否强制结束。退出 Codex 可能同时关闭本机的 ChatGPT 桌面端，操作前会提示。 |
| 采样节奏 | 面板展开时按约 1 秒刷新系统监测，收起时降低到约 5 秒。全屏检查只在面板或独立安比可见时运行。 |

## AI 额度配置

- **Codex** 使用已登录的本机 Codex 客户端读取账号额度，正常轮询间隔约 60 秒。账号查询不可用时会尝试读取本机已有的会话额度快照，并显示来源和采集时间。快照时间对应历史记录的时间。
- **Antigravity** 需要本机安装并登录客户端。要启用 CLI 查询，将自行获取的 Windows 版 `agy.exe` 放在 EXE 旁的 `AnbyPet\agy\bin\agy.exe`。监测面板打开时通常每 60 秒通过 `/usage` 查询一次，托盘状态约每 5 分钟查询一次。右键托盘选择「设置 Antigravity 同步」可复制可选的 `statusLine` 配置，用于已在终端使用 `agy` 的环境。

## 文件与构建

```text
src/                         C# 源码与 Windows manifest
build.ps1                    Windows PowerShell 构建脚本
anby-desktop-pet-v1.png       内嵌的透明安比素材
third_party/PresentMon/      固定版本的帧率采集工具与许可证
```

在 Windows x64 上从仓库根目录构建。脚本使用本机 .NET Framework 的 64 位 C# 编译器，核对 PresentMon 的 SHA-256，生成托盘图标，再将图标、安比素材、PresentMon 和许可证嵌进 `AnbyPet.exe`。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

程序生成的设置、额度快照、查询临时文件和帧率工具落在 EXE 旁的 `AnbyPet\` 目录。登录启动使用 Windows 当前用户的启动项，Codex 与 Antigravity 的原有账号和会话目录由各自客户端管理。发布包包含 EXE、说明和第三方许可证，运行数据在首次使用时生成。

v1.0.0 已在 Windows 10 x64 上构建。随包 EXE 与本机双屏实测使用的版本一致，验证场景为 Edge 在主屏进入与退出全屏、监测面板移到副屏并回位。帧率采集和额度获取仍取决于系统权限及相应客户端版本。

[第三方组件和素材说明](THIRD_PARTY_NOTICES.md)
