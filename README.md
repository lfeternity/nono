# 诺诺（NoNo / nono）中文说明

诺诺（英文名 NoNo，也可以叫 nono）是一只为 Codex Desktop 制作的自定义电子宠物。它是一个漂浮在编辑器旁边的小型 AI 编程伙伴：白色圆润机身、黑色屏幕脸、青蓝色发光眼睛、兔耳式能量天线，以及轻量、安静、带一点保护感的陪伴气质。

它的目标不是做一个吵闹的吉祥物，而是在写代码、等待 Codex、检查结果、遇到失败时，用小幅度动画给出清晰的状态反馈。

## 下载

- [最新版本](https://github.com/lfeternity/nono/releases/latest)
- [NoNo v1.0.2 Windows x64 安装包](https://github.com/lfeternity/nono/releases/download/v1.0.2/NoNo-Desktop-Pet-1.0.2-x64.msi)

## 预览

### 动作总览

![NoNo contact sheet](run-nono/qa/contact-sheet.png)

### 代表动作

| 空闲 | 处理中 | 等待输入 |
| --- | --- | --- |
| ![idle](run-nono/qa/previews/idle.gif) | ![running](run-nono/qa/previews/running.gif) | ![waiting](run-nono/qa/previews/waiting.gif) |

| 招手 | 跳跃 | 失败 |
| --- | --- | --- |
| ![waving](run-nono/qa/previews/waving.gif) | ![jumping](run-nono/qa/previews/jumping.gif) | ![failed](run-nono/qa/previews/failed.gif) |

如果想一次性查看所有动作，可以直接打开：

```text
run-nono/qa/action-gallery.html
```

## Codex 状态联动

`NoNo-Standalone.exe` 现在可以自动查看 Codex 的执行状态并切换动作。右键桌宠打开 `Codex 状态`，或在面板的 `设置` 页中开启/关闭 `自动跟随`。

- 优先读取状态桥接文件：`%APPDATA%\NoNoStandalone\codex-status.txt`、`%APPDATA%\NoNoStandalone\codex-status.json`、`%USERPROFILE%\.codex\codex-status.txt/json`，以及当前目录 `.codex\status.txt/json`。
- 状态值支持 `idle`、`running`、`waiting`、`review`、`failed`；JSON 可使用 `state`、`status`、`phase`、`activity` 或 `codexState` 字段。
- 没有状态文件时，会根据本机 Codex 相关进程做保守推断：`codex-command-runner` 表示 `running`，Codex 窗口在前台表示 `waiting`，仅检测到 Codex 打开时保持 `idle`。
- 右键 `动作` 菜单和设置页的宠物动作按钮覆盖 hatch-pet 的 9 个标准状态：`idle`、`running-right`、`running-left`、`waving`、`jumping`、`failed`、`waiting`、`running`、`review`。

## 本地语音对话

独立桌宠支持全本地语音对话。右键宠物打开 `语音助手`，启用后可以说 `nono`、`诺诺` 或 `你好 nono`，也可以点击 `现在说话`。

```powershell
.\voice\setup.ps1 -InstallOllama -PullChatModel
dotnet build .\standalone\NoNoStandalone.csproj -c Release
```

- 语音识别：本地 `Qwen3-ASR-0.6B`。
- 语音活动检测：本地 Silero VAD。
- 对话：未配置云端屏幕模型时使用项目内 Ollama；屏幕问答使用配置的云端模型，复杂电脑操作使用本机 Codex。
- 指令联动：明确的停止、确认、宠物动作和简单系统指令走本地快速路径；复杂电脑任务或“用 Codex”指令进入 Codex App Server，再由宠物的受控工具执行。
- 准确性：否定句、疑问句、转述句和缺少目标的指令不会仅凭关键词执行；低置信度结果会追问。
- 速度：短语音使用 480ms 结束窗口，唤醒语句使用 620ms，长句自动延长到 680ms；截图和 UI Automation 并行采集。
- 朗读：CPU 本地运行的 Kokoro v1.1 自然人声，可在语音助手设置中切换 103 个中英文音色，默认 `zf_001`；Windows TTS 仅作故障回退。
- 隐私：默认关闭；音频仅在内存中处理，不保存、不上传。
- 安装位置：Python、Ollama、模型和缓存均位于 `voice` 目录。

首次安装时 Qwen3-ASR 会下载约 1.9GB 权重，Kokoro TTS 约 348MB。右键菜单中的 `Windows 麦克风隐私设置` 可处理系统权限，`设置` 可调整语音指令联动、指令字幕、Ollama 模型、朗读音色、连续追问窗口、朗读速度和字幕时长。

## 电脑助手与 Codex

右键宠物打开 `电脑助手`。简单操作使用本地快速路径；复杂任务或明确包含“用 Codex”的指令由本机 Codex 规划，再调用宠物提供的类型化电脑工具：

- `查看整个屏幕`：仍使用配置的云端屏幕模型分析多显示器截图和 UI Automation 元素；
- `操作电脑`：应用启动/聚焦、窗口管理、媒体控制、浏览器、剪贴板、系统设置和电源动作使用可验证的 Windows 能力；复杂任务进入常驻的 `codex app-server --stdio`；
- 浏览器能力：默认浏览器、Chrome 或 Edge 可以直接打开 HTTP/HTTPS URL，也可以打开 Bing 搜索结果；不操作地址栏，不填写或提交网页表单；
- 文件能力：只允许 `desktop`、`downloads`、`documents`、`pictures`、`music`、`videos` 六个用户目录，使用相对路径；支持查找、列出、读取和查看信息、新建/追加文本、创建目录、复制、移动、重命名以及打开文件或文件夹；没有删除或回收站工具；
- 进程能力：可以列出普通进程、通过应用目录启动程序，以及请求有窗口的普通进程正常退出；不会强制结束系统进程；
- `立即停止`：取消 Codex turn 和后续工具调用；全局快捷键为 `Ctrl+Alt+Escape`；
- `设置`：控制只读、建议或经确认执行模式，以及低风险操作是否也需要确认。

使用 Codex 电脑任务前需要在本机安装并登录 Codex CLI。宠物不读取或保存 Codex 的 API Key。App Server 按需启动并保持常驻，当前版本仍属于实验性接口；协议不兼容时会明确报错，不会降级为任意 shell。

Codex 始终运行在 `read-only` 沙箱，审批策略为 `never`，额外权限请求会被宿主拒绝。所有电脑状态变更只能通过 `computer_*` 动态工具完成；命令执行或文件补丁事件一旦出现，宠物会中止任务。全流程不使用鼠标坐标、模拟鼠标点击或任意键盘输入。

读取文件或剪贴板内容、写入剪贴板、新建或追加文件、关闭程序、复制、移动、重命名和电源动作需要审批。支付、付款、购买、下单、转账、删除文件、读取凭据、安全软件操作、覆盖文件、重解析点路径和授权目录外路径会被阻止。审计日志只记录工具名、风险、成功状态和消息长度，不记录文件正文、剪贴板正文或凭据。

例如可以说“用 Codex 在 Chrome 打开 `https://example.com`”“搜索 Windows API”“在文档目录新建说明文件”或“把这段文字写入剪贴板”。支付和删除文件类指令会在进入 Codex 前直接拒绝。

构建后可以运行两类自测：

```powershell
NoNo-Standalone.exe --self-test
NoNo-Standalone.exe --codex-computer-self-test
```

第二项会真实启动 Codex App Server，让 Codex 调用只读应用列表工具，并确认 Windows 应用目录中可以发现 ToDesk；不会执行任何状态变更。

安全策略与电脑操作边界可查看 [`standalone/CodexComputerSafety.cs`](standalone/CodexComputerSafety.cs) 和 [`standalone/CodexComputerPolicy.cs`](standalone/CodexComputerPolicy.cs)。

## 角色设定

诺诺是一只 Codex 本机电子宠物，定位是“会陪你写代码的小型智能助手”。

- 形象：圆润白色机器人，黑色屏幕脸，青蓝色眼睛
- 气质：聪明、安静、可靠、轻微调皮
- 场景：写代码、调试、等待 Codex、测试通过或失败、提交前检查
- 风格：简洁、未来感、桌面悬浮宠物，不遮挡工作区

## 动作状态

| 行 | 状态 | 含义 |
| --- | --- | --- |
| 0 | `idle` | 默认待机，轻微漂浮 |
| 1 | `running-right` | 向右移动或被拖动 |
| 2 | `running-left` | 向左移动或被拖动 |
| 3 | `waving` | 打招呼 |
| 4 | `jumping` | 轻微跳跃 |
| 5 | `failed` | 失败、报错或不顺利 |
| 6 | `waiting` | 等待用户输入 |
| 7 | `running` | Codex 正在处理任务 |
| 8 | `review` | 检查、审阅、提交前确认 |

最终精灵图尺寸为 `1536 x 1872`，每格为 `192 x 208`。

## 安装

当前 Codex Desktop 版本通常读取 `avatars` 目录：

```text
%USERPROFILE%\.codex\avatars\nono\
  avatar.json
  spritesheet.webp
```

旧版或兼容场景可能读取 `pets` 目录：

```text
%USERPROFILE%\.codex\pets\nono\
  pet.json
  spritesheet.webp
```

清单文件示例：

```json
{
  "id": "nono",
  "displayName": "诺诺",
  "description": "A compact floating AI coding companion robot with a white rounded body, black glossy screen face, cyan glowing eyes, rabbit-ear energy antennae, small blue light wings, and calm protective coding-pet behavior.",
  "spritesheetPath": "spritesheet.webp"
}
```

复制完成后，重启 Codex Desktop，或在设置页刷新宠物/头像列表。

## Windows / WSL 说明

如果你在 Windows 上使用 Codex Desktop，并且 app-server 运行在 WSL 相关模式下，自定义宠物可能因为路径混用而无法被发现。典型情况是文件已经放进 `.codex/avatars` 或 `.codex/pets`，但设置页依旧不显示。

本项目保留了补丁排查目录 `patch-inspect/`，用于处理这类 Windows/WSL 路径问题。不要直接修改 WindowsApps 里的原始 Codex 安装包；更稳妥的做法是复制出一个本地 patched Codex，再从 patched 快捷方式启动。

## 文件结构

```text
.
+-- README.md
+-- run-nono/
|   +-- final/
|   |   +-- spritesheet.webp
|   |   +-- spritesheet.png
|   |   +-- validation.json
|   +-- qa/
|       +-- action-gallery.html
|       +-- contact-sheet.png
|       +-- previews/
|       |   +-- idle.gif
|       |   +-- running-right.gif
|       |   +-- running-left.gif
|       |   +-- waving.gif
|       |   +-- jumping.gif
|       |   +-- failed.gif
|       |   +-- waiting.gif
|       |   +-- running.gif
|       |   +-- review.gif
+-- patch-inspect/
```

## 验证

- 已生成最终 WebP 精灵图
- 精灵图尺寸为 `1536 x 1872`
- 使用 `192 x 208` 单元格布局
- 未使用格子保持透明
- 9 个动作状态均生成了 GIF 预览

---

# NoNo

NoNo is a custom animated pet for Codex Desktop: a compact floating AI coding companion with a rounded white body, glossy screen face, cyan eyes, rabbit-ear energy antennae, and a calm sci-fi assistant personality.

It is designed as a lightweight desktop coding companion that reacts to Codex workflow states such as idle, running, waiting, review, and failure.

## Download

- [Latest release](https://github.com/lfeternity/nono/releases/latest)
- [NoNo v1.0.2 Windows x64 installer](https://github.com/lfeternity/nono/releases/download/v1.0.2/NoNo-Desktop-Pet-1.0.2-x64.msi)

## Preview

The generated pet atlas and QA previews are included under `run-nono/`.

- Contact sheet: `run-nono/qa/contact-sheet.png`
- Action gallery: `run-nono/qa/action-gallery.html`
- Final spritesheet: `run-nono/final/spritesheet.webp`

Open `run-nono/qa/action-gallery.html` in a browser to preview all animations at once.

## Codex Status Following

`NoNo-Standalone.exe` can now inspect Codex activity and switch animations automatically. Use the pet's right-click `Codex Status` menu, or the panel `Settings` page, to toggle automatic following.

- NoNo is single-instance per Windows desktop session. Launching copies from multiple installation directories keeps the first pet running and exits later copies immediately.
- Status bridge files are checked first: `%APPDATA%\NoNoStandalone\codex-status.txt`, `%APPDATA%\NoNoStandalone\codex-status.json`, `%USERPROFILE%\.codex\codex-status.txt/json`, and the current directory's `.codex\status.txt/json`.
- Supported status values are `idle`, `running`, `waiting`, `review`, and `failed`; JSON may use `state`, `status`, `phase`, `activity`, or `codexState`.
- Without a status file, the standalone app uses conservative process inference: `codex-command-runner` maps to `running`, a foreground Codex window maps to `waiting`, and an open but inactive Codex app stays `idle`.
- The right-click `Actions` menu and the panel pet-action buttons cover all 9 hatch-pet rows: `idle`, `running-right`, `running-left`, `waving`, `jumping`, `failed`, `waiting`, `running`, and `review`.

## Cloud Desktop Agent

The standalone pet can inspect and operate the full virtual desktop from the `Screen Assistant` context menu. It combines a desktop screenshot with a time-bounded Windows UI Automation tree, prefers short batches of stable semantic element actions, falls back to coordinates only when necessary, and re-observes after every state-changing action.

- Primary model: `gemini-3.6-flash` through an OpenAI-compatible endpoint.
- Optional fallback: `gpt-5.5`, started as a delayed hedge when the primary model is slow and preferred after repeated verification failures.
- Emergency stop: `Ctrl+Alt+Escape`, the context-menu stop command, or the voice phrase “停下”.
- Secrets: Windows DPAPI `CurrentUser`; API keys are never stored in the repository or the normal preferences file.
- Safety: password fields are redacted, sensitive applications can be refused, and payment, credential, administrator, and security-setting actions are blocked locally.

## Character

NoNo is a small floating assistant robot built for a developer workflow:

- white rounded capsule body
- black glossy screen face
- cyan glowing symbolic eyes
- blue-and-white rabbit-ear energy antennae
- small side light-wings
- subtle futuristic body lines
- soft, protective, slightly playful coding-pet behavior

The design is original, while keeping the broad feeling of a friendly sci-fi helper pet.

## Animation States

The spritesheet follows the Codex pet atlas layout with 9 animation rows:

| Row | State | Meaning |
| --- | --- | --- |
| 0 | `idle` | Calm floating default state |
| 1 | `running-right` | Moving or being dragged to the right |
| 2 | `running-left` | Moving or being dragged to the left |
| 3 | `waving` | Friendly greeting |
| 4 | `jumping` | Small happy jump |
| 5 | `failed` | Failure or error feedback |
| 6 | `waiting` | Waiting for user input |
| 7 | `running` | Codex is working or thinking |
| 8 | `review` | Reviewing, checking, or inspecting |

The final atlas is `1536 x 1872`, using `192 x 208` cells.

## Installation

Copy the final pet files into your Codex custom pet/avatar directory.

The standalone Windows MSI includes an installation-directory page. Keep the default per-user location or use Browse to choose another folder before installation.

For current Codex Desktop builds that read custom avatars:

```text
%USERPROFILE%\.codex\avatars\nono\
  avatar.json
  spritesheet.webp
```

For older pet-based builds:

```text
%USERPROFILE%\.codex\pets\nono\
  pet.json
  spritesheet.webp
```

Example manifest:

```json
{
  "id": "nono",
  "displayName": "诺诺",
  "description": "A compact floating AI coding companion robot with a white rounded body, black glossy screen face, cyan glowing eyes, rabbit-ear energy antennae, small blue light wings, and calm protective coding-pet behavior.",
  "spritesheetPath": "spritesheet.webp"
}
```

After copying the files, restart Codex Desktop or refresh the pet/avatar list in Settings.

## WSL Note

Some Codex Desktop builds on Windows with WSL app-server mode may not discover custom pets because the app constructs a mixed Windows/POSIX path.

If the custom pet does not appear even though the files are in the correct directory, a local patched Codex copy may be required. This repository keeps the downloaded patch inspection files under `patch-inspect/`, but the original Codex installation should not be modified directly.

## Repository Layout

```text
.
+-- README.md
+-- run-nono/
|   +-- final/
|   |   +-- spritesheet.webp
|   |   +-- spritesheet.png
|   |   +-- validation.json
|   +-- qa/
|       +-- action-gallery.html
|       +-- contact-sheet.png
|       +-- previews/
|       |   +-- idle.gif
|       |   +-- running-right.gif
|       |   +-- running-left.gif
|       |   +-- waving.gif
|       |   +-- jumping.gif
|       |   +-- failed.gif
|       |   +-- waiting.gif
|       |   +-- running.gif
|       |   +-- review.gif
+-- patch-inspect/
```

## Validation

The generated atlas has been validated by the hatch-pet pipeline:

- final WebP atlas exists
- atlas size is `1536 x 1872`
- frame cells follow the `192 x 208` layout
- unused cells are transparent
- preview GIFs are generated for all 9 rows

## License

Add your preferred license before publishing. If you do not have a specific requirement, MIT is a simple default for sharing the pet files and documentation.
