using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NoNoStandalone
{
    internal static class CodexComputerTools
    {
        private const int MaximumListedItems = 100;
        private const int MaximumReadCharacters = 32768;
        private static readonly HashSet<string> SupportedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "computer_capabilities",
            "computer_app_list", "computer_app_open", "computer_app_focus",
            "computer_window_list", "computer_window_minimize", "computer_window_restore", "computer_window_close",
            "computer_media_play", "computer_media_pause", "computer_media_next", "computer_media_previous",
            "computer_browser_open_url", "computer_browser_search",
            "computer_clipboard_read", "computer_clipboard_write",
            "computer_system_show_desktop", "computer_system_open_settings", "computer_system_power", "computer_system_cancel_power",
            "computer_file_list", "computer_file_find", "computer_file_info", "computer_file_read",
            "computer_file_create_text", "computer_file_append_text", "computer_directory_create",
            "computer_file_copy", "computer_file_move", "computer_file_rename", "computer_file_open", "computer_folder_open",
            "computer_process_list", "computer_process_start", "computer_process_stop", "computer_verify"
        };

        private static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".json", ".csv", ".log", ".xml", ".yaml", ".yml", ".ini", ".config",
            ".cs", ".vb", ".js", ".ts", ".tsx", ".jsx", ".html", ".htm", ".css", ".scss", ".sql",
            ".py", ".java", ".c", ".cpp", ".h", ".hpp", ".go", ".rs", ".php", ".rb", ".sh",
            ".ps1", ".toml", ".properties", ".srt", ".vtt"
        };

        private static readonly HashSet<string> UnsafeOpenExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse",
            ".wsf", ".wsh", ".scr", ".msi", ".msp", ".reg", ".lnk", ".url", ".hta", ".cpl",
            ".jar", ".chm", ".iso", ".docm", ".xlsm", ".pptm"
        };

        private static readonly string[] SensitiveFileSignals = new string[]
        {
            ".env", "credential", "password", "passwd", "secret", "token", "apikey", "api-key", "private-key",
            "凭据", "密码", "口令", "密钥", "令牌"
        };

        private static readonly HashSet<string> ProtectedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system", "registry", "smss", "csrss", "wininit", "services", "lsass", "winlogon",
            "fontdrvhost", "dwm", "securityhealthservice", "msmpeng", "memory compression"
        };

        public static bool IsSupported(string tool)
        {
            return SupportedTools.Contains((tool ?? "").Trim());
        }

        public static object[] BuildDynamicToolSpecs()
        {
            Dictionary<string, object> app = StringProperty("app", "应用名称，必须来自本机应用目录。", null);
            Dictionary<string, object> optionalApp = StringProperty("app", "可选的媒体应用名称。", null);
            Dictionary<string, object> browser = StringProperty(
                "browser",
                "浏览器；default 使用 Windows 默认浏览器。",
                new string[] { "default", "chrome", "edge" });
            Dictionary<string, object> setting = StringProperty(
                "setting",
                "Windows 设置页。",
                new string[]
                {
                    "设置", "系统", "显示", "声音", "蓝牙", "网络", "Wi-Fi", "VPN", "存储", "电源", "通知",
                    "应用", "默认应用", "启动应用", "隐私", "位置", "摄像头", "麦克风", "更新", "时间", "语言",
                    "个性化", "账户", "辅助功能", "剪贴板", "关于"
                });
            Dictionary<string, object> root = StringProperty(
                "root",
                "授权的用户目录。",
                new string[] { "desktop", "downloads", "documents", "pictures", "music", "videos" });
            Dictionary<string, object> path = StringProperty("path", "相对于授权目录的路径，不能是绝对路径。", null);
            Dictionary<string, object> pattern = StringProperty("pattern", "文件名匹配模式，例如 *.pdf。", null);

            List<object> tools = new List<object>();
            tools.Add(Tool("computer_capabilities", "列出宠物当前允许和明确禁止的电脑能力。", Properties(), null));
            tools.Add(Tool("computer_app_list", "列出 Windows 开始菜单中真实存在的应用。", Properties(), null));
            tools.Add(Tool("computer_app_open", "打开已安装应用并验证窗口出现。", Properties(app), new string[] { "app" }));
            tools.Add(Tool("computer_app_focus", "把正在运行的应用窗口切换到前台。", Properties(app), new string[] { "app" }));
            tools.Add(Tool("computer_window_list", "列出非敏感的可见顶层窗口。", Properties(), null));
            tools.Add(Tool("computer_window_minimize", "最小化指定应用窗口。", Properties(app), new string[] { "app" }));
            tools.Add(Tool("computer_window_restore", "恢复并显示指定应用窗口。", Properties(app), new string[] { "app" }));
            tools.Add(Tool("computer_window_close", "请求应用正常关闭窗口，不强制结束进程。", Properties(app), new string[] { "app" }));
            tools.Add(Tool("computer_media_play", "通过 Windows 媒体会话开始播放。", Properties(optionalApp), null));
            tools.Add(Tool("computer_media_pause", "通过 Windows 媒体会话暂停播放。", Properties(optionalApp), null));
            tools.Add(Tool("computer_media_next", "通过 Windows 媒体会话切换到下一首。", Properties(optionalApp), null));
            tools.Add(Tool("computer_media_previous", "通过 Windows 媒体会话切换到上一首。", Properties(optionalApp), null));
            tools.Add(Tool(
                "computer_browser_open_url",
                "让默认浏览器、Chrome 或 Edge 直接打开 HTTP/HTTPS 网址，不操作地址栏。支付与资金操作网址被禁止。",
                Properties(StringProperty("url", "要打开的 HTTP 或 HTTPS 网址。", null), browser),
                new string[] { "url" }));
            tools.Add(Tool(
                "computer_browser_search",
                "在浏览器中打开 Bing 搜索结果，不模拟键盘或鼠标。支付与资金操作搜索被禁止。",
                Properties(StringProperty("query", "搜索内容。", null), browser),
                new string[] { "query" }));
            tools.Add(Tool("computer_clipboard_read", "读取非凭据的文本剪贴板。内容会发送给 Codex。", Properties(), null));
            tools.Add(Tool(
                "computer_clipboard_write",
                "把指定文本写入 Windows 剪贴板。",
                Properties(StringProperty("text", "要写入剪贴板的文本。", null)),
                new string[] { "text" }));
            tools.Add(Tool("computer_system_show_desktop", "使用 Windows 系统命令显示桌面。", Properties(), null));
            tools.Add(Tool("computer_system_open_settings", "打开允许的 Windows 设置页。", Properties(setting), new string[] { "setting" }));
            tools.Add(Tool(
                "computer_system_power",
                "执行锁定、睡眠、休眠，或在 30 秒后关机/重启。该工具需要用户逐次确认。",
                Properties(StringProperty("action", "电源动作。", new string[] { "lock", "sleep", "hibernate", "shutdown", "restart" })),
                new string[] { "action" }));
            tools.Add(Tool("computer_system_cancel_power", "取消已安排的关机或重启。", Properties(), null));
            tools.Add(Tool("computer_file_list", "列出授权用户目录中的文件和文件夹。", Properties(root, path), new string[] { "root" }));
            tools.Add(Tool("computer_file_find", "在授权用户目录中按文件名查找文件。", Properties(root, pattern), new string[] { "root", "pattern" }));
            tools.Add(Tool("computer_file_info", "读取授权目录内文件或文件夹的大小、时间和属性。", Properties(root, path), new string[] { "root", "path" }));
            tools.Add(Tool("computer_file_read", "读取小型非敏感文本文件。内容会发送给 Codex。", Properties(root, path), new string[] { "root", "path" }));
            tools.Add(Tool(
                "computer_file_create_text",
                "在授权目录中新建 UTF-8 文本文件，不覆盖现有文件。",
                Properties(root, path, StringProperty("text", "要写入的新文件内容，最多 32768 个字符。", null)),
                new string[] { "root", "path", "text" }));
            tools.Add(Tool(
                "computer_file_append_text",
                "向授权目录内的现有文本文件追加 UTF-8 内容，不替换原内容。",
                Properties(root, path, StringProperty("text", "要追加的内容，最多 32768 个字符。", null)),
                new string[] { "root", "path", "text" }));
            tools.Add(Tool(
                "computer_directory_create",
                "在授权用户目录中新建文件夹。",
                Properties(root, path),
                new string[] { "root", "path" }));
            tools.Add(Tool(
                "computer_file_copy",
                "在授权用户目录之间复制单个文件，不覆盖现有文件。",
                Properties(
                    root,
                    StringProperty("source", "源文件相对路径。", null),
                    StringProperty("destinationRoot", "可选目标用户目录；省略时与 root 相同。", new string[] { "desktop", "downloads", "documents", "pictures", "music", "videos" }),
                    StringProperty("destination", "目标文件相对路径。", null)),
                new string[] { "root", "source", "destination" }));
            tools.Add(Tool(
                "computer_file_move",
                "在授权用户目录之间移动单个文件，不覆盖或删除文件内容。",
                Properties(
                    root,
                    StringProperty("source", "源文件相对路径。", null),
                    StringProperty("destinationRoot", "可选目标用户目录；省略时与 root 相同。", new string[] { "desktop", "downloads", "documents", "pictures", "music", "videos" }),
                    StringProperty("destination", "目标文件相对路径。", null)),
                new string[] { "root", "source", "destination" }));
            tools.Add(Tool(
                "computer_file_rename",
                "重命名授权目录内的单个文件，不覆盖现有文件。",
                Properties(root, path, StringProperty("newName", "不含目录分隔符的新文件名。", null)),
                new string[] { "root", "path", "newName" }));
            tools.Add(Tool("computer_file_open", "使用默认应用打开授权目录内的非可执行文件。", Properties(root, path), new string[] { "root", "path" }));
            tools.Add(Tool("computer_folder_open", "使用文件资源管理器打开授权目录内的文件夹。", Properties(root, path), new string[] { "root" }));
            tools.Add(Tool("computer_process_list", "列出非敏感进程及其内存占用。", Properties(), null));
            tools.Add(Tool("computer_process_start", "通过已安装应用目录启动程序。", Properties(app), new string[] { "app" }));
            tools.Add(Tool(
                "computer_process_stop",
                "请求有窗口的普通进程正常退出，不强制终止系统进程。",
                Properties(StringProperty("process", "进程名称，不含路径。", null)),
                new string[] { "process" }));
            tools.Add(Tool(
                "computer_verify",
                "验证应用窗口、文件或文件夹是否存在。",
                Properties(
                    StringProperty("kind", "验证类型。", new string[] { "app_running", "file_exists", "directory_exists" }),
                    StringProperty("target", "应用名称或相对文件路径。", null),
                    root),
                new string[] { "kind", "target" }));
            return tools.ToArray();
        }

        public static async Task<CodexComputerToolResult> ExecuteAsync(
            CodexComputerToolCall call,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string tool = call == null ? "" : (call.Tool ?? "").Trim().ToLowerInvariant();
                switch (tool)
                {
                    case "computer_capabilities":
                        return CodexComputerToolResult.Ok(GetCapabilitiesSummary());
                    case "computer_app_list":
                        return CodexComputerToolResult.Ok(ComputerApplicationCatalog.GetApplicationSummary(120));
                    case "computer_app_open":
                        return await ExecuteActionAsync("app_open", Require(call, "app"), "", cancellationToken).ConfigureAwait(false);
                    case "computer_app_focus":
                        return await ExecuteActionAsync("app_focus", Require(call, "app"), "", cancellationToken).ConfigureAwait(false);
                    case "computer_window_list":
                        return CodexComputerToolResult.Ok(ComputerWindowLocator.GetVisibleWindowSummary(50));
                    case "computer_window_minimize":
                        return await ExecuteActionAsync("window_minimize", Require(call, "app"), "", cancellationToken).ConfigureAwait(false);
                    case "computer_window_restore":
                        return await ExecuteActionAsync("window_restore", Require(call, "app"), "", cancellationToken).ConfigureAwait(false);
                    case "computer_window_close":
                        return await ExecuteActionAsync("window_close", Require(call, "app"), "", cancellationToken).ConfigureAwait(false);
                    case "computer_media_play":
                        return await ExecuteActionAsync("media_play", Optional(call, "app"), "", cancellationToken).ConfigureAwait(false);
                    case "computer_media_pause":
                        return await ExecuteActionAsync("media_pause", Optional(call, "app"), "", cancellationToken).ConfigureAwait(false);
                    case "computer_media_next":
                        return await ExecuteActionAsync("media_next", Optional(call, "app"), "", cancellationToken).ConfigureAwait(false);
                    case "computer_media_previous":
                        return await ExecuteActionAsync("media_previous", Optional(call, "app"), "", cancellationToken).ConfigureAwait(false);
                    case "computer_browser_open_url":
                        return CodexComputerToolResult.Ok(ComputerBrowserController.OpenUrl(Require(call, "url"), Optional(call, "browser")));
                    case "computer_browser_search":
                        return CodexComputerToolResult.Ok(ComputerBrowserController.Search(Require(call, "query"), Optional(call, "browser")));
                    case "computer_clipboard_read":
                        return CodexComputerToolResult.Ok(ComputerClipboardController.ReadText());
                    case "computer_clipboard_write":
                        return CodexComputerToolResult.Ok(ComputerClipboardController.WriteText(RequireRaw(call, "text")));
                    case "computer_system_show_desktop":
                        return await ExecuteActionAsync("system_show_desktop", "", "", cancellationToken).ConfigureAwait(false);
                    case "computer_system_open_settings":
                        return await ExecuteActionAsync("system_open_settings", "", Require(call, "setting"), cancellationToken).ConfigureAwait(false);
                    case "computer_system_power":
                        return CodexComputerToolResult.Ok(ComputerPowerController.Execute(Require(call, "action")));
                    case "computer_system_cancel_power":
                        return CodexComputerToolResult.Ok(ComputerPowerController.CancelScheduledShutdown());
                    case "computer_file_list":
                        return CodexComputerToolResult.Ok(ListFiles(Require(call, "root"), Optional(call, "path")));
                    case "computer_file_find":
                        return CodexComputerToolResult.Ok(FindFiles(Require(call, "root"), Require(call, "pattern")));
                    case "computer_file_info":
                        return CodexComputerToolResult.Ok(GetFileInfo(Require(call, "root"), Require(call, "path")));
                    case "computer_file_read":
                        return CodexComputerToolResult.Ok(ReadTextFile(Require(call, "root"), Require(call, "path")));
                    case "computer_file_create_text":
                        return CodexComputerToolResult.Ok(CreateTextFile(Require(call, "root"), Require(call, "path"), RequireRaw(call, "text")));
                    case "computer_file_append_text":
                        return CodexComputerToolResult.Ok(AppendTextFile(Require(call, "root"), Require(call, "path"), RequireRaw(call, "text")));
                    case "computer_directory_create":
                        return CodexComputerToolResult.Ok(CreateDirectory(Require(call, "root"), Require(call, "path")));
                    case "computer_file_copy":
                        return CodexComputerToolResult.Ok(CopyFile(
                            Require(call, "root"), Require(call, "source"), Optional(call, "destinationRoot"), Require(call, "destination")));
                    case "computer_file_move":
                        return CodexComputerToolResult.Ok(MoveFile(
                            Require(call, "root"), Require(call, "source"), Optional(call, "destinationRoot"), Require(call, "destination")));
                    case "computer_file_rename":
                        return CodexComputerToolResult.Ok(RenameFile(Require(call, "root"), Require(call, "path"), Require(call, "newName")));
                    case "computer_file_open":
                        return CodexComputerToolResult.Ok(OpenFile(Require(call, "root"), Require(call, "path")));
                    case "computer_folder_open":
                        return CodexComputerToolResult.Ok(OpenFolder(Require(call, "root"), Optional(call, "path")));
                    case "computer_process_list":
                        return CodexComputerToolResult.Ok(ListProcesses());
                    case "computer_process_start":
                        return await ExecuteActionAsync("app_open", Require(call, "app"), "", cancellationToken).ConfigureAwait(false);
                    case "computer_process_stop":
                        return await StopProcessAsync(Require(call, "process"), cancellationToken).ConfigureAwait(false);
                    case "computer_verify":
                        return CodexComputerToolResult.Ok(Verify(call));
                    default:
                        return CodexComputerToolResult.Fail("不支持的 Codex 电脑工具：“" + tool + "”。");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return CodexComputerToolResult.Fail(ex.Message);
            }
        }

        public static bool RunSelfTest()
        {
            string root;
            try
            {
                root = ResolveRoot("downloads");
            }
            catch
            {
                return false;
            }

            return IsSupported("computer_app_open") &&
                IsSupported("computer_browser_open_url") &&
                IsSupported("computer_file_create_text") &&
                IsSupported("computer_clipboard_write") &&
                !IsSupported("computer_file_trash") &&
                !IsSupported("computer_shell") &&
                !CodexComputerSafety.IsForbiddenDeletionTool("computer_file_move") &&
                Path.IsPathRooted(root) &&
                ComputerBrowserController.RunSelfTest() &&
                BuildDynamicToolSpecs().Length >= 35;
        }

        private static string GetCapabilitiesSummary()
        {
            return "允许：应用与窗口、媒体、HTTP/HTTPS 浏览器直达和搜索、剪贴板、Windows 设置与电源、" +
                "桌面/下载/文档/图片/音乐/视频目录中的查询、读取、创建、追加、复制、移动、重命名和打开、普通进程管理。\n" +
                "禁止：支付、付款、购买、下单、转账等资金操作；删除文件或移入回收站；任意 Shell、脚本执行、" +
                "模拟鼠标、键盘注入、凭据读取和安全策略绕过。";
        }

        private static async Task<CodexComputerToolResult> ExecuteActionAsync(
            string type,
            string app,
            string setting,
            CancellationToken cancellationToken)
        {
            DesktopAction action = new DesktopAction();
            action.Type = type;
            action.App = app;
            action.Setting = setting;
            DesktopActionResult result = await ComputerCommandExecutor.ExecuteAsync(action, cancellationToken).ConfigureAwait(false);
            return result.Success
                ? CodexComputerToolResult.Ok(result.Message)
                : CodexComputerToolResult.Fail(result.Message);
        }

        private static string ListFiles(string rootAlias, string relativePath)
        {
            string root = ResolveRoot(rootAlias);
            string directory = ResolvePath(root, relativePath, true);
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException("目录不存在：“" + relativePath + "”。");
            }

            List<string> entries = new List<string>();
            string[] directories = Directory.GetDirectories(directory);
            Array.Sort(directories, StringComparer.CurrentCultureIgnoreCase);
            for (int i = 0; i < directories.Length && entries.Count < MaximumListedItems; i++)
            {
                DirectoryInfo info = new DirectoryInfo(directories[i]);
                if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    entries.Add("目录\t" + Relative(root, info.FullName) + "\t" + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                }
            }

            string[] files = Directory.GetFiles(directory);
            Array.Sort(files, StringComparer.CurrentCultureIgnoreCase);
            for (int i = 0; i < files.Length && entries.Count < MaximumListedItems; i++)
            {
                FileInfo info = new FileInfo(files[i]);
                entries.Add("文件\t" + Relative(root, info.FullName) + "\t" + info.Length.ToString(CultureInfo.InvariantCulture) + " 字节\t" +
                    info.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            }

            return entries.Count == 0 ? "目录为空。" : String.Join("\n", entries.ToArray());
        }

        private static string FindFiles(string rootAlias, string pattern)
        {
            string root = ResolveRoot(rootAlias);
            string safePattern = ValidatePattern(pattern);
            Queue<Tuple<string, int>> queue = new Queue<Tuple<string, int>>();
            queue.Enqueue(Tuple.Create(root, 0));
            List<string> matches = new List<string>();
            while (queue.Count > 0 && matches.Count < MaximumListedItems)
            {
                Tuple<string, int> current = queue.Dequeue();
                try
                {
                    string[] files = Directory.GetFiles(current.Item1, safePattern, System.IO.SearchOption.TopDirectoryOnly);
                    for (int i = 0; i < files.Length && matches.Count < MaximumListedItems; i++)
                    {
                        FileInfo info = new FileInfo(files[i]);
                        matches.Add(Relative(root, info.FullName) + "\t" + info.Length.ToString(CultureInfo.InvariantCulture) + " 字节\t" +
                            info.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                    }

                    if (current.Item2 >= 6)
                    {
                        continue;
                    }

                    string[] directories = Directory.GetDirectories(current.Item1);
                    for (int i = 0; i < directories.Length; i++)
                    {
                        DirectoryInfo info = new DirectoryInfo(directories[i]);
                        if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                        {
                            queue.Enqueue(Tuple.Create(info.FullName, current.Item2 + 1));
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
            }

            return matches.Count == 0 ? "没有找到匹配文件。" : String.Join("\n", matches.ToArray());
        }

        private static string GetFileInfo(string rootAlias, string relativePath)
        {
            string root = ResolveRoot(rootAlias);
            string path = ResolvePath(root, relativePath, false);
            if (File.Exists(path))
            {
                FileInfo info = new FileInfo(path);
                return "类型：文件\n路径：" + Relative(root, path) +
                    "\n大小：" + info.Length.ToString(CultureInfo.InvariantCulture) + " 字节" +
                    "\n修改时间：" + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    "\n只读：" + info.IsReadOnly.ToString();
            }

            if (Directory.Exists(path))
            {
                DirectoryInfo info = new DirectoryInfo(path);
                return "类型：文件夹\n路径：" + Relative(root, path) +
                    "\n修改时间：" + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    "\n属性：" + info.Attributes.ToString();
            }

            throw new FileNotFoundException("文件或文件夹不存在：“" + relativePath + "”。");
        }

        private static string ReadTextFile(string rootAlias, string relativePath)
        {
            string root = ResolveRoot(rootAlias);
            string path = ResolvePath(root, relativePath, false);
            EnsureReadableTextFile(path);
            using (StreamReader reader = new StreamReader(path, Encoding.UTF8, true))
            {
                char[] buffer = new char[MaximumReadCharacters + 1];
                int count = reader.ReadBlock(buffer, 0, buffer.Length);
                if (count > MaximumReadCharacters || !reader.EndOfStream)
                {
                    throw new InvalidOperationException("文件内容超过 32768 个字符，拒绝发送给 Codex。");
                }

                return "文件：" + Relative(root, path) + "\n---\n" + new string(buffer, 0, count);
            }
        }

        private static string CreateTextFile(string rootAlias, string relativePath, string content)
        {
            ValidateWritableText(content);
            string root = ResolveRoot(rootAlias);
            string path = ResolvePath(root, relativePath, false);
            EnsureSafeTextPath(path);
            EnsureDestinationAvailable(path);
            string parent = Path.GetDirectoryName(path);
            if (String.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            {
                throw new DirectoryNotFoundException("目标文件夹不存在。请先使用 computer_directory_create 创建文件夹。");
            }

            File.WriteAllText(path, content, new UTF8Encoding(false));
            if (!File.Exists(path) || File.ReadAllText(path, Encoding.UTF8) != content)
            {
                throw new IOException("新建文件后的内容验证失败。");
            }

            return "已新建文本文件“" + Relative(root, path) + "”，共 " + content.Length.ToString(CultureInfo.InvariantCulture) + " 个字符。";
        }

        private static string AppendTextFile(string rootAlias, string relativePath, string content)
        {
            ValidateWritableText(content);
            string root = ResolveRoot(rootAlias);
            string path = ResolveExistingFile(root, relativePath);
            EnsureSafeTextPath(path);
            long previousLength = new FileInfo(path).Length;
            File.AppendAllText(path, content, new UTF8Encoding(false));
            long currentLength = new FileInfo(path).Length;
            if (currentLength <= previousLength)
            {
                throw new IOException("追加文件后的大小验证失败。");
            }

            return "已向“" + Relative(root, path) + "”追加 " + content.Length.ToString(CultureInfo.InvariantCulture) + " 个字符。";
        }

        private static string CreateDirectory(string rootAlias, string relativePath)
        {
            string root = ResolveRoot(rootAlias);
            string path = ResolvePath(root, relativePath, false);
            EnsureDestinationAvailable(path);
            Directory.CreateDirectory(path);
            if (!Directory.Exists(path))
            {
                throw new IOException("新建文件夹后验证失败。");
            }

            return "已新建文件夹“" + Relative(root, path) + "”。";
        }

        private static string CopyFile(string rootAlias, string sourcePath, string destinationRootAlias, string destinationPath)
        {
            string sourceRoot = ResolveRoot(rootAlias);
            string destinationRoot = ResolveRoot(String.IsNullOrWhiteSpace(destinationRootAlias) ? rootAlias : destinationRootAlias);
            string source = ResolveExistingFile(sourceRoot, sourcePath);
            string destination = ResolvePath(destinationRoot, destinationPath, false);
            EnsureDestinationAvailable(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(source, destination, false);
            if (!File.Exists(destination))
            {
                throw new IOException("复制后未检测到目标文件。");
            }

            return "已复制到“" + RootLabel(destinationRootAlias, rootAlias) + "/" + Relative(destinationRoot, destination) + "”。";
        }

        private static string MoveFile(string rootAlias, string sourcePath, string destinationRootAlias, string destinationPath)
        {
            string sourceRoot = ResolveRoot(rootAlias);
            string destinationRoot = ResolveRoot(String.IsNullOrWhiteSpace(destinationRootAlias) ? rootAlias : destinationRootAlias);
            string source = ResolveExistingFile(sourceRoot, sourcePath);
            string destination = ResolvePath(destinationRoot, destinationPath, false);
            EnsureDestinationAvailable(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Move(source, destination);
            if (File.Exists(source) || !File.Exists(destination))
            {
                throw new IOException("移动后的文件状态验证失败。");
            }

            return "已移动到“" + RootLabel(destinationRootAlias, rootAlias) + "/" + Relative(destinationRoot, destination) + "”。";
        }

        private static string RenameFile(string rootAlias, string relativePath, string newName)
        {
            string cleanName = (newName ?? "").Trim();
            if (cleanName.Length == 0 || cleanName == "." || cleanName == ".." ||
                cleanName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                cleanName.IndexOf(Path.DirectorySeparatorChar) >= 0 || cleanName.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                throw new InvalidOperationException("新文件名无效。");
            }

            string root = ResolveRoot(rootAlias);
            string source = ResolveExistingFile(root, relativePath);
            string destination = ResolvePath(root, Path.Combine(Path.GetDirectoryName(relativePath) ?? "", cleanName), false);
            EnsureDestinationAvailable(destination);
            File.Move(source, destination);
            if (File.Exists(source) || !File.Exists(destination))
            {
                throw new IOException("重命名后的文件状态验证失败。");
            }

            return "已重命名为“" + Relative(root, destination) + "”。";
        }

        private static string OpenFile(string rootAlias, string relativePath)
        {
            string root = ResolveRoot(rootAlias);
            string path = ResolveExistingFile(root, relativePath);
            string extension = Path.GetExtension(path);
            if (UnsafeOpenExtensions.Contains(extension))
            {
                throw new InvalidOperationException("不允许通过文件工具打开可执行、脚本、快捷方式或宏文件。");
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return "已使用默认应用打开“" + Relative(root, path) + "”。";
        }

        private static string OpenFolder(string rootAlias, string relativePath)
        {
            string root = ResolveRoot(rootAlias);
            string path = ResolvePath(root, relativePath, true);
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException("文件夹不存在：“" + relativePath + "”。");
            }

            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            return "已在文件资源管理器中打开“" + (String.IsNullOrWhiteSpace(relativePath) ? rootAlias : Relative(root, path)) + "”。";
        }

        private static string ListProcesses()
        {
            List<ProcessSummary> items = new List<ProcessSummary>();
            Process[] processes = Process.GetProcesses();
            for (int i = 0; i < processes.Length; i++)
            {
                using (Process process = processes[i])
                {
                    try
                    {
                        if (PrivacyRedactor.IsBlockedProcess(process.ProcessName))
                        {
                            continue;
                        }

                        items.Add(new ProcessSummary
                        {
                            Name = process.ProcessName,
                            Id = process.Id,
                            WorkingSet = process.WorkingSet64
                        });
                    }
                    catch
                    {
                    }
                }
            }

            items.Sort(delegate(ProcessSummary left, ProcessSummary right)
            {
                int memory = right.WorkingSet.CompareTo(left.WorkingSet);
                return memory != 0 ? memory : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            });
            StringBuilder output = new StringBuilder();
            int count = Math.Min(40, items.Count);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) output.AppendLine();
                output.Append(items[i].Name).Append("\tPID ").Append(items[i].Id.ToString(CultureInfo.InvariantCulture))
                    .Append("\t").Append((items[i].WorkingSet / 1024 / 1024).ToString(CultureInfo.InvariantCulture)).Append(" MB");
            }

            return output.Length == 0 ? "没有可显示的普通进程。" : output.ToString();
        }

        private static async Task<CodexComputerToolResult> StopProcessAsync(string processName, CancellationToken cancellationToken)
        {
            string name = Path.GetFileNameWithoutExtension((processName ?? "").Trim());
            if (name.Length == 0 || name.IndexOfAny(new char[] { '\\', '/', ':', '"', '\r', '\n' }) >= 0)
            {
                return CodexComputerToolResult.Fail("进程名称无效。");
            }

            if (ProtectedProcesses.Contains(name) || String.Equals(name, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return CodexComputerToolResult.Fail("不允许关闭系统进程或宠物自身进程。");
            }

            Process[] processes = Process.GetProcessesByName(name);
            if (processes.Length == 0)
            {
                return CodexComputerToolResult.Fail("没有找到正在运行的进程“" + name + "”。");
            }

            int requested = 0;
            try
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    if (PrivacyRedactor.IsBlockedProcess(processes[i].ProcessName))
                    {
                        continue;
                    }

                    try
                    {
                        if (processes[i].MainWindowHandle != IntPtr.Zero && processes[i].CloseMainWindow())
                        {
                            requested++;
                        }
                    }
                    catch
                    {
                    }
                }

                if (requested == 0)
                {
                    return CodexComputerToolResult.Fail("该进程没有可正常关闭的窗口；宠物不会强制终止它。");
                }

                DateTime limit = DateTime.UtcNow.AddSeconds(4);
                while (DateTime.UtcNow < limit)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool anyRunning = false;
                    for (int i = 0; i < processes.Length; i++)
                    {
                        try
                        {
                            processes[i].Refresh();
                            if (!processes[i].HasExited && processes[i].MainWindowHandle != IntPtr.Zero)
                            {
                                anyRunning = true;
                            }
                        }
                        catch
                        {
                        }
                    }

                    if (!anyRunning)
                    {
                        return CodexComputerToolResult.Ok("进程“" + name + "”的窗口已经正常关闭。");
                    }

                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }

                return CodexComputerToolResult.Fail("进程没有在超时时间内正常退出；宠物没有强制终止它。");
            }
            finally
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    processes[i].Dispose();
                }
            }
        }

        private static string Verify(CodexComputerToolCall call)
        {
            string kind = Require(call, "kind").ToLowerInvariant();
            string target = Require(call, "target");
            if (kind == "app_running")
            {
                ComputerApplication app = ComputerApplicationCatalog.Resolve(target);
                bool running = ComputerWindowLocator.FindBest(app) != IntPtr.Zero;
                return running ? "验证成功：“" + app.LaunchDescription + "”窗口正在运行。" : "验证失败：没有检测到“" + app.LaunchDescription + "”窗口。";
            }

            if (kind == "file_exists")
            {
                string root = ResolveRoot(Require(call, "root"));
                string path = ResolvePath(root, target, false);
                return File.Exists(path) ? "验证成功：文件存在。" : "验证失败：文件不存在。";
            }

            if (kind == "directory_exists")
            {
                string root = ResolveRoot(Require(call, "root"));
                string path = ResolvePath(root, target, true);
                return Directory.Exists(path) ? "验证成功：文件夹存在。" : "验证失败：文件夹不存在。";
            }

            throw new InvalidOperationException("不支持的验证类型：“" + kind + "”。");
        }

        private static string ResolveRoot(string alias)
        {
            string normalized = (alias ?? "").Trim().ToLowerInvariant();
            string path;
            switch (normalized)
            {
                case "desktop":
                case "桌面":
                    path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    break;
                case "downloads":
                case "download":
                case "下载":
                case "下载目录":
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    break;
                case "documents":
                case "document":
                case "文档":
                case "我的文档":
                    path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    break;
                case "pictures":
                case "picture":
                case "图片":
                case "照片":
                    path = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                    break;
                case "music":
                case "音乐":
                    path = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                    break;
                case "videos":
                case "video":
                case "视频":
                    path = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                    break;
                default:
                    throw new InvalidOperationException("未授权的目录。只允许 desktop、downloads、documents、pictures、music 或 videos。");
            }

            if (String.IsNullOrWhiteSpace(path))
            {
                throw new DirectoryNotFoundException("Windows 没有返回授权目录路径。");
            }

            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string ResolvePath(string root, string relativePath, bool allowRoot)
        {
            string relative = (relativePath ?? "").Trim();
            if (relative.Length == 0)
            {
                if (allowRoot) return root;
                throw new InvalidOperationException("文件路径不能为空。");
            }

            if (Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0 || relative.IndexOf('\0') >= 0)
            {
                throw new InvalidOperationException("只能使用授权目录内的相对路径。");
            }

            string full = Path.GetFullPath(Path.Combine(root, relative));
            string prefix = root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !String.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("路径超出了授权目录。");
            }

            EnsureNoReparseEscape(root, full);
            return full;
        }

        private static void EnsureNoReparseEscape(string root, string path)
        {
            string current = File.Exists(path) ? Path.GetDirectoryName(path) : path;
            while (!String.IsNullOrWhiteSpace(current) &&
                current.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(current))
                {
                    FileAttributes attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException("授权目录中的链接或重解析点不能用于文件操作。");
                    }
                }

                if (String.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = Path.GetDirectoryName(current);
            }
        }

        private static string ResolveExistingFile(string root, string relativePath)
        {
            string path = ResolvePath(root, relativePath, false);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("文件不存在：“" + relativePath + "”。");
            }

            return path;
        }

        private static void EnsureDestinationAvailable(string destination)
        {
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new IOException("目标已经存在，宠物不会覆盖：“" + Path.GetFileName(destination) + "”。");
            }
        }

        private static void ValidateWritableText(string content)
        {
            if (String.IsNullOrEmpty(content) || content.Length > MaximumReadCharacters || content.IndexOf('\0') >= 0)
            {
                throw new InvalidOperationException("文本内容为空、超过 32768 个字符或包含无效字符。");
            }
        }

        private static void EnsureSafeTextPath(string path)
        {
            string name = Path.GetFileName(path).ToLowerInvariant();
            for (int i = 0; i < SensitiveFileSignals.Length; i++)
            {
                if (name.IndexOf(SensitiveFileSignals[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new InvalidOperationException("该文件名可能用于保存凭据，拒绝写入。");
                }
            }

            if (!TextExtensions.Contains(Path.GetExtension(path)))
            {
                throw new InvalidOperationException("只允许创建或追加已知文本格式的文件。");
            }
        }

        private static void EnsureReadableTextFile(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("文件不存在。", path);
            }

            string name = Path.GetFileName(path).ToLowerInvariant();
            for (int i = 0; i < SensitiveFileSignals.Length; i++)
            {
                if (name.IndexOf(SensitiveFileSignals[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new InvalidOperationException("该文件名可能包含凭据或敏感信息，拒绝读取。");
                }
            }

            if (!TextExtensions.Contains(Path.GetExtension(path)))
            {
                throw new InvalidOperationException("只允许读取已知的小型文本文件。");
            }

            FileInfo info = new FileInfo(path);
            if (info.Length > 131072)
            {
                throw new InvalidOperationException("文件过大，拒绝发送给 Codex。");
            }
        }

        private static string Relative(string root, string path)
        {
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(prefix.Length) : Path.GetFileName(path);
        }

        private static string RootLabel(string preferred, string fallback)
        {
            string value = String.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
            return (value ?? "").Trim().ToLowerInvariant();
        }

        private static string ValidatePattern(string pattern)
        {
            string value = (pattern ?? "").Trim();
            if (value.Length == 0 || value.Length > 128 || value.IndexOfAny(new char[] { '\\', '/', ':', '\0' }) >= 0 || value.Contains(".."))
            {
                throw new InvalidOperationException("文件匹配模式无效。");
            }

            return value;
        }

        private static string Require(CodexComputerToolCall call, string key)
        {
            string value = Optional(call, key);
            if (value.Length == 0)
            {
                throw new InvalidOperationException("电脑工具缺少参数：“" + key + "”。");
            }

            return value;
        }

        private static string Optional(CodexComputerToolCall call, string key)
        {
            object value;
            return call != null && call.Arguments != null && call.Arguments.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture).Trim()
                : "";
        }

        private static string RequireRaw(CodexComputerToolCall call, string key)
        {
            object value;
            string text = call != null && call.Arguments != null && call.Arguments.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : "";
            if (String.IsNullOrEmpty(text))
            {
                throw new InvalidOperationException("电脑工具缺少参数：“" + key + "”。");
            }

            return text;
        }

        private static Dictionary<string, object> StringProperty(string name, string description, string[] values)
        {
            Dictionary<string, object> schema = new Dictionary<string, object>();
            schema["type"] = "string";
            schema["description"] = description;
            if (values != null)
            {
                schema["enum"] = values;
            }

            Dictionary<string, object> property = new Dictionary<string, object>();
            property[name] = schema;
            return property;
        }

        private static Dictionary<string, object> Properties(params Dictionary<string, object>[] sources)
        {
            Dictionary<string, object> properties = new Dictionary<string, object>();
            for (int i = 0; i < sources.Length; i++)
            {
                foreach (KeyValuePair<string, object> item in sources[i])
                {
                    properties[item.Key] = item.Value;
                }
            }

            return properties;
        }

        private static Dictionary<string, object> Tool(
            string name,
            string description,
            Dictionary<string, object> properties,
            string[] required)
        {
            Dictionary<string, object> inputSchema = new Dictionary<string, object>();
            inputSchema["type"] = "object";
            inputSchema["properties"] = properties;
            inputSchema["additionalProperties"] = false;
            if (required != null && required.Length > 0)
            {
                inputSchema["required"] = required;
            }

            Dictionary<string, object> tool = new Dictionary<string, object>();
            tool["type"] = "function";
            tool["name"] = name;
            tool["description"] = description;
            tool["inputSchema"] = inputSchema;
            return tool;
        }

        private sealed class ProcessSummary
        {
            public string Name;
            public int Id;
            public long WorkingSet;
        }
    }
}
