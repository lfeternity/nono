# 贡献指南

感谢你愿意参与诺诺（NoNo）项目。可以通过 Issue 报告普通缺陷或提出功能建议，也可以提交 Pull Request 改进代码、动画资源、安装流程和文档。

## 开始之前

- 请先搜索现有 Issue 和 Pull Request，避免重复工作。
- 安全漏洞不要公开提交，请按照[安全策略](.github/SECURITY.md)使用 GitHub 私密漏洞报告。
- 提交日志、截图或配置前，请移除个人信息、API 密钥、本机绝对路径和其他凭据。
- 不要提交虚拟环境、模型文件、缓存、日志、编译输出或本机 Codex 配置。

## 开发环境

桌宠和安装器的主要开发环境：

- Windows x64
- .NET SDK
- .NET Framework 4.8 参考程序集
- PowerShell

语音功能还需要 Python 3.13 和 Windows Python Launcher（`py.exe`）。

## 提交流程

1. Fork 仓库并从 `main` 创建功能分支。
2. 保持改动聚焦，避免在同一个 Pull Request 中混入无关重构。
3. 使用清晰的提交信息，推荐 Conventional Commits 格式，例如 `fix: correct Codex status detection`。
4. 根据改动范围运行相应构建和自检。
5. 在 Pull Request 中说明改动目的、验证方式、用户可见变化和已知限制。

## 构建与自检

构建独立桌宠：

```powershell
dotnet build .\standalone\NoNoStandalone.csproj -c Release
```

构建 MSI 安装包：

```powershell
.\installer\build.ps1
```

运行桌宠自检：

```powershell
.\standalone\bin\Release\net48\NoNo-Standalone.exe --self-test
```

运行语音协议自检：

```powershell
.\voice\.venv\Scripts\python.exe .\voice\voice_service.py --self-test
```

如果改动涉及电脑助手，还应运行：

```powershell
.\standalone\bin\Release\net48\NoNo-Standalone.exe --codex-computer-self-test
```

## 代码与资源要求

- 保持现有项目结构和 Windows 原生交互习惯。
- 新增电脑操作必须有明确边界、风险判断和可验证结果。
- 不得绕过安全策略、确认流程、凭据保护或紧急停止机制。
- 精灵图资源应保持 `1536 x 1872` 总尺寸和 `192 x 208` 单元格布局。
- 新增第三方代码、字体、模型或图片时，必须确认许可证允许再分发，并保留必要的版权声明。

## 许可证

提交贡献即表示你同意按照项目的 [MIT License](LICENSE) 授权你的贡献。
