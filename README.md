# 诺诺（NoNo）

> 一只会陪你写代码、跟随 Codex 工作状态的 Windows 桌面电子宠物。

[下载最新版本](https://github.com/lfeternity/nono/releases/latest) · [查看 v1.0.2](https://github.com/lfeternity/nono/releases/tag/v1.0.2) · [安全策略](https://github.com/lfeternity/nono/security/policy)

![诺诺动作总览](run-nono/qa/contact-sheet.png)

诺诺是一只为 Codex Desktop 和 Windows 开发环境设计的轻量桌面伙伴。它拥有白色圆润机身、黑色屏幕脸、青蓝色发光眼睛和兔耳式能量天线，会根据空闲、运行、等待、审阅和失败等状态切换动画。

除了作为 Codex 自定义宠物使用，项目还提供独立 Windows 桌宠、Codex 状态联动、本地语音对话，以及带安全边界的电脑助手功能。

## 主要功能

- **九种动画状态**：覆盖空闲、移动、招手、跳跃、失败、等待、运行和审阅。
- **独立 Windows 桌宠**：支持拖动、右键菜单、动作切换、设置面板和单实例运行。
- **Codex 状态联动**：读取状态桥接文件，并在没有状态文件时进行保守的进程状态判断。
- **本地语音助手**：使用 Qwen3-ASR、Silero VAD、Kokoro TTS 和可选的本地 Ollama 对话模型。
- **电脑助手**：结合 Windows UI Automation、受控系统工具和 Codex App Server 完成明确授权的电脑任务。
- **隐私与安全控制**：敏感字段脱敏、危险操作拦截、确认机制、紧急停止和本地凭据加密。

## 预览

| 空闲 | 处理中 | 等待输入 |
| --- | --- | --- |
| ![空闲](run-nono/qa/previews/idle.gif) | ![处理中](run-nono/qa/previews/running.gif) | ![等待输入](run-nono/qa/previews/waiting.gif) |

| 招手 | 跳跃 | 失败 |
| --- | --- | --- |
| ![招手](run-nono/qa/previews/waving.gif) | ![跳跃](run-nono/qa/previews/jumping.gif) | ![失败](run-nono/qa/previews/failed.gif) |

完整动作画廊位于 [`run-nono/qa/action-gallery.html`](run-nono/qa/action-gallery.html)。

## 下载与安装

### 使用 Windows 安装包

推荐普通用户直接安装最新的 MSI：

- [NoNo v1.0.2 Windows x64 安装包](https://github.com/lfeternity/nono/releases/download/v1.0.2/NoNo-Desktop-Pet-1.0.2-x64.msi)
- 文件大小：约 2.71 MB
- SHA-256：`7646949FE48E71E29878EC718E3E001104C8F4374E9E930A14552FDA15D1B38B`

安装要求：

- Windows 10 或 Windows 11，x64 系统
- .NET Framework 4.8 或更高版本

安装完成后，从开始菜单或桌面快捷方式启动 `NoNo Desktop Pet`。安装程序为当前用户安装，不要求写入系统级程序目录。

### 作为 Codex 自定义宠物安装

仓库中的 [`nono`](nono) 目录包含可直接使用的宠物资源：

```text
nono/
  avatar.json
  pet.json
  spritesheet.webp
```

当前 Codex Desktop 通常读取：

```text
%USERPROFILE%\.codex\avatars\nono\
  avatar.json
  spritesheet.webp
```

旧版或兼容模式可能读取：

```text
%USERPROFILE%\.codex\pets\nono\
  pet.json
  spritesheet.webp
```

复制完成后，重启 Codex Desktop，或在设置中刷新自定义宠物列表。

## 动画状态

精灵图尺寸为 `1536 x 1872`，使用 `192 x 208` 的单元格布局。

| 行 | 状态 | 用途 |
| --- | --- | --- |
| 0 | `idle` | 默认待机和轻微漂浮 |
| 1 | `running-right` | 向右移动或被向右拖动 |
| 2 | `running-left` | 向左移动或被向左拖动 |
| 3 | `waving` | 招手和问候 |
| 4 | `jumping` | 跳跃和积极反馈 |
| 5 | `failed` | 失败或错误反馈 |
| 6 | `waiting` | 等待用户输入 |
| 7 | `running` | Codex 正在处理任务 |
| 8 | `review` | 检查、审阅或提交前确认 |

最终精灵图位于 [`run-nono/final/spritesheet.webp`](run-nono/final/spritesheet.webp)，验证结果位于 [`run-nono/final/validation.json`](run-nono/final/validation.json)。

## Codex 状态联动

在桌宠右键菜单中打开 `Codex 状态`，或在设置面板中启用 `自动跟随`。程序会优先读取以下状态文件：

```text
%APPDATA%\NoNoStandalone\codex-status.txt
%APPDATA%\NoNoStandalone\codex-status.json
%USERPROFILE%\.codex\codex-status.txt
%USERPROFILE%\.codex\codex-status.json
当前目录\.codex\status.txt
当前目录\.codex\status.json
```

文本文件可以直接写入状态名：

```text
running
```

JSON 文件支持 `state`、`status`、`phase`、`activity` 或 `codexState` 字段：

```json
{
  "state": "waiting"
}
```

联动状态支持 `idle`、`running`、`waiting`、`review` 和 `failed`。没有状态文件时，程序只根据本机 Codex 相关进程和前台窗口进行保守判断。

## 本地语音助手

语音功能默认关闭。启用后可以说 `nono`、`诺诺` 或 `你好 nono` 唤醒，也可以从右键菜单点击 `现在说话`。

首次配置需要：

- Python 3.13 和 Windows Python Launcher（`py.exe`）
- 可用的麦克风权限
- 下载模型所需的网络连接和磁盘空间

在项目根目录运行：

```powershell
.\voice\setup.ps1 -InstallOllama -PullChatModel
```

脚本会在 `voice` 目录内创建独立虚拟环境，并下载：

- Qwen3-ASR 0.6B 语音识别模型，约 1.9 GB
- Kokoro v1.1 语音合成模型，约 348 MB
- 可选的 Ollama 和 `qwen3:4b-instruct-2507-q4_K_M` 对话模型

运行时数据保存在以下本地目录，这些目录不会提交到 Git：

```text
voice/.venv/
voice/cache/
voice/models/
voice/ollama/
```

语音音频只在内存中处理，不保存原始录音。未配置云端屏幕模型时，普通语音对话可以完全使用本地模型。

## 电脑助手

电脑助手用于执行范围明确、可验证的 Windows 操作。复杂任务可以交给本机 Codex 规划，再通过诺诺提供的类型化工具执行。

主要能力包括：

- 启动和聚焦应用、管理普通窗口
- 媒体控制、浏览器打开网址和搜索
- 读取受支持目录中的文件信息
- 在确认后执行剪贴板、文件写入、复制、移动或重命名
- 结合截图和 UI Automation 查看桌面状态
- 使用 `Ctrl+Alt+Escape` 立即停止当前任务

安全边界包括：

- 支付、转账、购买和下单操作会被阻止
- 不提供删除文件或清空回收站工具
- 不读取密码、令牌、私钥等凭据
- 不修改安全软件、管理员设置或系统安全策略
- 敏感输入框和凭据相关界面会被脱敏或拒绝处理
- 状态变更根据设置进入建议或确认流程

电脑助手需要本机已安装并登录 Codex CLI。若启用云端屏幕分析，桌面截图和可见界面信息会发送给用户自行配置的模型服务商；启用前请确认其隐私政策和数据处理方式。

相关实现可查看：

- [`standalone/CodexComputerSafety.cs`](standalone/CodexComputerSafety.cs)
- [`standalone/CodexComputerPolicy.cs`](standalone/CodexComputerPolicy.cs)
- [`standalone/CodexComputerTools.cs`](standalone/CodexComputerTools.cs)

## 从源码构建

构建环境：

- Windows x64
- .NET SDK
- .NET Framework 4.8 参考程序集
- PowerShell

构建独立桌宠：

```powershell
dotnet build .\standalone\NoNoStandalone.csproj -c Release
```

输出文件：

```text
standalone\bin\Release\net48\NoNo-Standalone.exe
```

构建完整 MSI 安装包：

```powershell
.\installer\build.ps1
```

脚本会构建桌宠和 WiX 安装器，并将安装包输出到 `release` 目录。WiX SDK 和 .NET Framework 参考程序集会通过 NuGet 自动还原。

## 自检

桌宠构建完成后，可以运行：

```powershell
.\standalone\bin\Release\net48\NoNo-Standalone.exe --self-test
.\standalone\bin\Release\net48\NoNo-Standalone.exe --codex-computer-self-test
```

语音协议自检：

```powershell
.\voice\.venv\Scripts\python.exe .\voice\voice_service.py --self-test
```

精灵图已经通过项目验证：

- WebP 尺寸为 `1536 x 1872`
- 单元格尺寸为 `192 x 208`
- 未使用单元格保持透明
- 九种动作均有对应帧和 GIF 预览

## 项目结构

```text
.
├─ .github/              GitHub 安全策略
├─ installer/            WiX 安装器项目和构建脚本
├─ nono/                 Codex 自定义宠物发布资源
├─ patch-inspect/        Windows/WSL 自定义宠物路径补丁参考
├─ run-nono/             精灵图、动作帧、预览和验证结果
├─ standalone/           Windows 桌宠和电脑助手源码
├─ tools/                资源同步工具
└─ voice/                本地语音服务和安装脚本
```

`dist`、`release`、编译输出、语音模型、虚拟环境、缓存、日志和本机配置均由 `.gitignore` 排除。

## Windows 与 WSL

部分 Codex Desktop 版本在 Windows 与 WSL 混合路径环境下可能无法发现自定义宠物。遇到问题时：

1. 确认宠物文件位于当前 Codex 版本实际读取的 `avatars` 或 `pets` 目录。
2. 重启 Codex Desktop，并刷新自定义宠物列表。
3. 不要直接修改 WindowsApps 中的原始 Codex 安装目录。
4. 如需排查路径兼容问题，参考 [`patch-inspect`](patch-inspect) 中的说明和脚本。

## 隐私与安全

- API 密钥不会写入仓库或普通偏好设置文件。
- 本地保存的云端模型 API 密钥使用 Windows DPAPI `CurrentUser` 加密。
- 麦克风音频默认不保存、不上传。
- 审计日志不记录文件正文、剪贴板正文或凭据内容。
- 原始参考素材、生成任务记录、本机路径和模型缓存不会上传到仓库。

发现安全问题时，请不要创建公开 Issue。请阅读[安全策略](https://github.com/lfeternity/nono/security/policy)，并使用 GitHub 私密漏洞报告。

## 参与项目

普通缺陷和功能建议可以通过 [GitHub Issues](https://github.com/lfeternity/nono/issues) 提交。提交前请移除日志、截图和配置文件中的个人信息、API 密钥与本机路径。

## 许可证

当前仓库尚未附带开源许可证。除非获得维护者明确授权，请不要默认拥有复制、修改或再分发本项目的权利。
