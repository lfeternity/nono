using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NoNoStandalone
{
    internal sealed class ComputerApplication
    {
        public string Name;
        public string AppId;
        public string DirectTarget;
        public string[] Aliases;
        public string[] ProcessHints;

        public string LaunchDescription
        {
            get { return String.IsNullOrWhiteSpace(Name) ? AppId : Name; }
        }
    }

    internal static class ComputerCommandExecutor
    {
        private static readonly HashSet<string> SupportedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "app_open", "app_focus", "window_minimize", "window_restore", "window_close",
            "media_play", "media_pause", "media_next", "media_previous",
            "system_show_desktop", "system_open_settings"
        };

        public static bool IsSupported(string type)
        {
            return SupportedActions.Contains((type ?? "").Trim());
        }

        public static bool RunSelfTest()
        {
            string uri;
            List<DesktopAction> planned;
            List<DesktopAction> denied;
            return IsSupported("app_open") &&
                IsSupported("window_close") &&
                IsSupported("media_play") &&
                !IsSupported("click_point") &&
                !IsSupported("shell") &&
                ComputerSettingsCatalog.TryResolve("声音", out uri) &&
                String.Equals(uri, "ms-settings:sound", StringComparison.Ordinal) &&
                ComputerCommandPlanner.TryPlan("打开电脑的酷狗并播放音乐", out planned) &&
                planned.Count == 2 &&
                planned[0].Type == "app_open" &&
                planned[1].Type == "media_play" &&
                !ComputerCommandPlanner.TryPlan("不要打开酷狗", out denied) &&
                !ComputerCommandPlanner.TryPlan("酷狗怎么打开", out denied);
        }

        public static string GetModelContext(string goal)
        {
            StringBuilder context = new StringBuilder();
            context.AppendLine("电脑操作使用本地受控能力，不使用鼠标坐标或任意命令行。");
            context.AppendLine("动作参数：app_open/app_focus/window_minimize/window_restore 使用 app；媒体动作使用可选 app；system_open_settings 使用 setting。");
            string matches = ComputerApplicationCatalog.GetGoalMatches(goal, 12);
            if (!String.IsNullOrWhiteSpace(matches))
            {
                context.AppendLine("用户目标明确命中的本机应用：" + matches + "。");
                context.AppendLine("命中应用已经由本地目录确认存在，涉及打开、聚焦或窗口操作时应直接使用该名称。");
            }

            string applications = ComputerApplicationCatalog.GetApplicationSummary(80);
            if (!String.IsNullOrWhiteSpace(applications))
            {
                context.AppendLine("本机开始菜单应用（只能选择真实存在的名称）：");
                context.AppendLine(applications);
            }

            return context.ToString();
        }

        public static async Task<DesktopActionResult> ExecuteAsync(DesktopAction action, CancellationToken cancellationToken)
        {
            DesktopActionResult result = new DesktopActionResult();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string type = (action == null ? "" : action.Type ?? "").Trim().ToLowerInvariant();
                switch (type)
                {
                    case "app_open":
                        result.Message = await OpenApplicationAsync(RequireApp(action), cancellationToken).ConfigureAwait(false);
                        break;
                    case "app_focus":
                        result.Message = await FocusApplicationAsync(RequireApp(action), cancellationToken).ConfigureAwait(false);
                        break;
                    case "window_minimize":
                        result.Message = await SetWindowStateAsync(RequireApp(action), true, cancellationToken).ConfigureAwait(false);
                        break;
                    case "window_restore":
                        result.Message = await SetWindowStateAsync(RequireApp(action), false, cancellationToken).ConfigureAwait(false);
                        break;
                    case "window_close":
                        result.Message = await CloseApplicationWindowAsync(RequireApp(action), cancellationToken).ConfigureAwait(false);
                        break;
                    case "media_play":
                    case "media_pause":
                    case "media_next":
                    case "media_previous":
                        result.Message = await ComputerMediaController.ControlAsync(
                            type.Substring("media_".Length),
                            action == null ? "" : action.App,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case "system_show_desktop":
                        NativeMethods.ShowDesktop();
                        result.Message = "Windows 已执行显示桌面命令";
                        break;
                    case "system_open_settings":
                        result.Message = await OpenSettingsAsync(action == null ? "" : action.Setting, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidOperationException("不支持的电脑能力：" + type);
                }

                result.Success = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex.Message;
            }

            return result;
        }

        private static string RequireApp(DesktopAction action)
        {
            string app = action == null ? "" : (action.App ?? "").Trim();
            if (app.Length == 0)
            {
                throw new InvalidOperationException("电脑操作缺少应用名称。");
            }

            return app;
        }

        private static async Task<string> OpenApplicationAsync(string query, CancellationToken cancellationToken)
        {
            ComputerApplication application = ComputerApplicationCatalog.Resolve(query);
            IntPtr existing = ComputerWindowLocator.FindBest(application);
            if (existing != IntPtr.Zero)
            {
                await ComputerWindowLocator.RestoreAndFocusAsync(existing, false, cancellationToken).ConfigureAwait(false);
                return "已检测并显示“" + application.LaunchDescription + "”窗口";
            }

            ProcessStartInfo startInfo;
            if (!String.IsNullOrWhiteSpace(application.DirectTarget))
            {
                startInfo = new ProcessStartInfo(application.DirectTarget);
                startInfo.UseShellExecute = true;
            }
            else
            {
                ValidateAppId(application.AppId);
                startInfo = new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + application.AppId);
                startInfo.UseShellExecute = true;
            }

            Process.Start(startInfo);
            IntPtr window = await ComputerWindowLocator.WaitForWindowAsync(application, 6000, cancellationToken).ConfigureAwait(false);
            if (window == IntPtr.Zero)
            {
                throw new InvalidOperationException("Windows 已收到启动请求，但没有检测到“" + application.LaunchDescription + "”窗口。");
            }

            await ComputerWindowLocator.RestoreAndFocusAsync(window, false, cancellationToken).ConfigureAwait(false);
            return "已启动并检测到“" + application.LaunchDescription + "”窗口";
        }

        private static async Task<string> FocusApplicationAsync(string query, CancellationToken cancellationToken)
        {
            ComputerApplication application = ComputerApplicationCatalog.Resolve(query);
            IntPtr window = ComputerWindowLocator.FindBest(application);
            if (window == IntPtr.Zero)
            {
                throw new InvalidOperationException("没有找到正在运行的“" + application.LaunchDescription + "”窗口。");
            }

            await ComputerWindowLocator.RestoreAndFocusAsync(window, true, cancellationToken).ConfigureAwait(false);
            return "已将“" + application.LaunchDescription + "”切换到前台";
        }

        private static async Task<string> SetWindowStateAsync(string query, bool minimize, CancellationToken cancellationToken)
        {
            ComputerApplication application = ComputerApplicationCatalog.Resolve(query);
            IntPtr window = ComputerWindowLocator.FindBest(application);
            if (window == IntPtr.Zero)
            {
                throw new InvalidOperationException("没有找到正在运行的“" + application.LaunchDescription + "”窗口。");
            }

            if (minimize)
            {
                ComputerWindowLocator.Minimize(window);
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                if (!ComputerWindowLocator.IsMinimized(window))
                {
                    throw new InvalidOperationException("窗口没有进入最小化状态。");
                }

                return "已最小化“" + application.LaunchDescription + "”";
            }

            await ComputerWindowLocator.RestoreAndFocusAsync(window, false, cancellationToken).ConfigureAwait(false);
            if (ComputerWindowLocator.IsMinimized(window))
            {
                throw new InvalidOperationException("窗口没有恢复显示。");
            }

            return "已恢复“" + application.LaunchDescription + "”窗口";
        }

        private static async Task<string> CloseApplicationWindowAsync(string query, CancellationToken cancellationToken)
        {
            ComputerApplication application = ComputerApplicationCatalog.Resolve(query);
            IntPtr window = ComputerWindowLocator.FindBest(application);
            if (window == IntPtr.Zero)
            {
                throw new InvalidOperationException("没有找到正在运行的“" + application.LaunchDescription + "”窗口。");
            }

            await ComputerWindowLocator.CloseWindowAsync(window, cancellationToken).ConfigureAwait(false);
            if (ComputerWindowLocator.IsWindowHandle(window))
            {
                throw new InvalidOperationException("应用没有在超时时间内正常关闭窗口；宠物没有强制终止进程。");
            }

            return "已正常关闭“" + application.LaunchDescription + "”窗口";
        }

        private static async Task<string> OpenSettingsAsync(string setting, CancellationToken cancellationToken)
        {
            string uri;
            if (!ComputerSettingsCatalog.TryResolve(setting, out uri))
            {
                throw new InvalidOperationException("不支持的 Windows 设置页：“" + (setting ?? "") + "”。");
            }

            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            ComputerApplication settings = ComputerApplicationCatalog.SystemSettingsApplication;
            IntPtr window = await ComputerWindowLocator.WaitForWindowAsync(settings, 5000, cancellationToken).ConfigureAwait(false);
            if (window == IntPtr.Zero)
            {
                throw new InvalidOperationException("Windows 已收到设置页请求，但没有检测到设置窗口。");
            }

            return "已打开 Windows 设置页";
        }

        private static void ValidateAppId(string appId)
        {
            string value = (appId ?? "").Trim();
            if (value.Length == 0 || value.Length > 512 || value.IndexOfAny(new char[] { '\r', '\n', '"' }) >= 0)
            {
                throw new InvalidOperationException("应用 ID 无效。");
            }
        }
    }

    internal static class ComputerApplicationCatalog
    {
        private static readonly object Gate = new object();
        private static List<ComputerApplication> cached;
        private static DateTime cacheExpiresUtc;

        public static readonly ComputerApplication SystemSettingsApplication = new ComputerApplication
        {
            Name = "Windows 设置",
            DirectTarget = "ms-settings:",
            Aliases = new string[] { "设置", "系统设置", "windows设置" },
            ProcessHints = new string[] { "systemsettings" }
        };

        private static readonly ComputerApplication[] Known = new ComputerApplication[]
        {
            new ComputerApplication { Name = "酷狗音乐", AppId = "kugou", Aliases = new string[] { "酷狗", "kugou" }, ProcessHints = new string[] { "kugou" } },
            new ComputerApplication { Name = "记事本", DirectTarget = "notepad.exe", Aliases = new string[] { "notepad" }, ProcessHints = new string[] { "notepad" } },
            new ComputerApplication { Name = "计算器", DirectTarget = "calc.exe", Aliases = new string[] { "calculator", "calc" }, ProcessHints = new string[] { "calculatorapp", "calc" } },
            new ComputerApplication { Name = "文件资源管理器", DirectTarget = "explorer.exe", Aliases = new string[] { "资源管理器", "explorer" }, ProcessHints = new string[] { "explorer" } },
            SystemSettingsApplication
        };

        public static ComputerApplication Resolve(string query)
        {
            string normalized = Normalize(query);
            if (normalized.Length == 0)
            {
                throw new InvalidOperationException("应用名称为空。");
            }

            for (int i = 0; i < Known.Length; i++)
            {
                if (Score(Known[i], normalized) >= 1000)
                {
                    return Known[i];
                }
            }

            List<ComputerApplication> applications = GetApplications();
            ComputerApplication best = null;
            int bestScore = 0;
            bool tied = false;
            for (int i = 0; i < applications.Count; i++)
            {
                int score = Score(applications[i], normalized);
                if (score > bestScore)
                {
                    best = applications[i];
                    bestScore = score;
                    tied = false;
                }
                else if (score > 0 && score == bestScore && best != null &&
                    !String.Equals(best.AppId, applications[i].AppId, StringComparison.OrdinalIgnoreCase))
                {
                    tied = true;
                }
            }

            if (best == null || bestScore < 300)
            {
                throw new InvalidOperationException("没有在本机开始菜单中找到应用“" + query.Trim() + "”。");
            }

            if (tied && bestScore < 900)
            {
                throw new InvalidOperationException("应用名称“" + query.Trim() + "”不够明确，请使用完整名称。");
            }

            return best;
        }

        public static string GetApplicationSummary(int maximum)
        {
            try
            {
                List<ComputerApplication> applications = GetApplications();
                StringBuilder summary = new StringBuilder();
                int count = Math.Min(Math.Max(1, maximum), applications.Count);
                for (int i = 0; i < count; i++)
                {
                    if (i > 0)
                    {
                        summary.Append("、");
                    }

                    summary.Append(applications[i].Name);
                }

                return summary.ToString();
            }
            catch
            {
                return "酷狗音乐、记事本、计算器、文件资源管理器、Windows 设置";
            }
        }

        public static string GetGoalMatches(string goal, int maximum)
        {
            string normalizedGoal = Normalize(goal);
            if (normalizedGoal.Length == 0)
            {
                return "";
            }

            try
            {
                List<ComputerApplication> applications = GetApplications();
                List<string> matches = new List<string>();
                for (int i = 0; i < applications.Count && matches.Count < Math.Max(1, maximum); i++)
                {
                    ComputerApplication application = applications[i];
                    string name = Normalize(application.Name);
                    bool matched = name.Length >= 2 && normalizedGoal.Contains(name);
                    string[] aliases = application.Aliases ?? new string[0];
                    for (int aliasIndex = 0; !matched && aliasIndex < aliases.Length; aliasIndex++)
                    {
                        string alias = Normalize(aliases[aliasIndex]);
                        matched = alias.Length >= 2 && normalizedGoal.Contains(alias);
                    }

                    if (matched && !matches.Contains(application.Name))
                    {
                        matches.Add(application.Name);
                    }
                }

                return String.Join("、", matches.ToArray());
            }
            catch
            {
                return "";
            }
        }

        private static List<ComputerApplication> GetApplications()
        {
            lock (Gate)
            {
                if (cached != null && DateTime.UtcNow < cacheExpiresUtc)
                {
                    return cached;
                }

                List<ComputerApplication> loaded = LoadStartApplications();
                for (int i = 0; i < Known.Length; i++)
                {
                    if (!ContainsEquivalent(loaded, Known[i]))
                    {
                        loaded.Add(Known[i]);
                    }
                }

                loaded.Sort(delegate(ComputerApplication left, ComputerApplication right)
                {
                    return StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
                });
                cached = loaded;
                cacheExpiresUtc = DateTime.UtcNow.AddMinutes(10);
                return cached;
            }
        }

        private static List<ComputerApplication> LoadStartApplications()
        {
            const string script =
                "$OutputEncoding=[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false);" +
                "Get-StartApps | ForEach-Object {" +
                "$n=([string]$_.Name).Replace([char]9,' ').Replace([char]13,' ').Replace([char]10,' ');" +
                "$i=([string]$_.AppID).Replace([char]9,' ').Replace([char]13,' ').Replace([char]10,' ');" +
                "[Console]::WriteLine($n+[char]9+$i)}";
            FixedPowerShellResult result = FixedPowerShellRunner.Run(script, null, 5000);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException("无法读取 Windows 开始菜单应用目录：" + result.Error);
            }

            List<ComputerApplication> applications = new List<ComputerApplication>();
            string[] lines = result.Output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                int separator = lines[i].IndexOf('\t');
                if (separator <= 0 || separator >= lines[i].Length - 1)
                {
                    continue;
                }

                string name = lines[i].Substring(0, separator).Trim();
                string appId = lines[i].Substring(separator + 1).Trim();
                if (name.Length == 0 || appId.Length == 0)
                {
                    continue;
                }

                string normalizedName = Normalize(name);
                string fileName = "";
                try { fileName = Path.GetFileName(appId); }
                catch { }
                if (normalizedName.StartsWith("卸载", StringComparison.Ordinal) ||
                    normalizedName.Contains("uninstall") ||
                    fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool trustedExecutable = false;
                try
                {
                    trustedExecutable = Path.IsPathRooted(appId) &&
                        String.Equals(Path.GetExtension(appId), ".exe", StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(appId);
                }
                catch
                {
                    trustedExecutable = false;
                }

                applications.Add(new ComputerApplication
                {
                    Name = name,
                    AppId = trustedExecutable ? "" : appId,
                    DirectTarget = trustedExecutable ? Path.GetFullPath(appId) : "",
                    Aliases = new string[0],
                    ProcessHints = trustedExecutable
                        ? new string[] { Normalize(Path.GetFileNameWithoutExtension(appId)) }
                        : BuildProcessHints(appId)
                });
            }

            return applications;
        }

        private static bool ContainsEquivalent(List<ComputerApplication> applications, ComputerApplication candidate)
        {
            for (int i = 0; i < applications.Count; i++)
            {
                if ((!String.IsNullOrWhiteSpace(candidate.AppId) && String.Equals(applications[i].AppId, candidate.AppId, StringComparison.OrdinalIgnoreCase)) ||
                    String.Equals(Normalize(applications[i].Name), Normalize(candidate.Name), StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static int Score(ComputerApplication application, string query)
        {
            string name = Normalize(application.Name);
            string appId = Normalize(application.AppId);
            if (name == query || appId == query)
            {
                return 1000;
            }

            string[] aliases = application.Aliases ?? new string[0];
            for (int i = 0; i < aliases.Length; i++)
            {
                if (Normalize(aliases[i]) == query)
                {
                    return 1000;
                }
            }

            if (name.StartsWith(query, StringComparison.Ordinal) || query.StartsWith(name, StringComparison.Ordinal))
            {
                return 800;
            }

            if (name.Contains(query) || query.Contains(name))
            {
                return 600;
            }

            if (appId.Contains(query))
            {
                return 400;
            }

            return 0;
        }

        private static string[] BuildProcessHints(string appId)
        {
            string[] parts = (appId ?? "").Split(new char[] { '!', '_', '.', '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> hints = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                string normalized = Normalize(parts[i]);
                if (normalized.Length >= 3 && !hints.Contains(normalized))
                {
                    hints.Add(normalized);
                }
            }

            return hints.ToArray();
        }

        internal static string Normalize(string value)
        {
            StringBuilder normalized = new StringBuilder();
            string source = (value ?? "").Trim().ToLowerInvariant();
            for (int i = 0; i < source.Length; i++)
            {
                if (Char.IsLetterOrDigit(source[i]))
                {
                    normalized.Append(source[i]);
                }
            }

            return normalized.ToString();
        }
    }

    internal static class ComputerSettingsCatalog
    {
        private static readonly Dictionary<string, string> Settings = BuildSettings();

        public static bool TryResolve(string value, out string uri)
        {
            string key = ComputerApplicationCatalog.Normalize(value);
            if (key.Length == 0)
            {
                key = "设置";
            }

            return Settings.TryGetValue(key, out uri);
        }

        private static Dictionary<string, string> BuildSettings()
        {
            Dictionary<string, string> settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Add(settings, "ms-settings:", "设置", "系统设置", "windows设置", "home");
            Add(settings, "ms-settings:system", "系统", "系统主页", "system");
            Add(settings, "ms-settings:display", "显示", "显示设置", "屏幕", "display");
            Add(settings, "ms-settings:sound", "声音", "声音设置", "音频", "sound", "audio");
            Add(settings, "ms-settings:bluetooth", "蓝牙", "蓝牙设置", "bluetooth");
            Add(settings, "ms-settings:network", "网络", "网络设置", "network");
            Add(settings, "ms-settings:network-wifi", "wifi", "wi-fi", "无线网络");
            Add(settings, "ms-settings:network-vpn", "vpn", "虚拟专用网络");
            Add(settings, "ms-settings:storagesense", "存储", "存储设置", "storage");
            Add(settings, "ms-settings:powersleep", "电源", "电源和睡眠", "睡眠设置", "power");
            Add(settings, "ms-settings:notifications", "通知", "通知设置", "notifications");
            Add(settings, "ms-settings:appsfeatures", "应用", "应用设置", "应用和功能", "apps");
            Add(settings, "ms-settings:defaultapps", "默认应用", "默认程序", "defaultapps");
            Add(settings, "ms-settings:startupapps", "启动应用", "开机启动应用", "startupapps");
            Add(settings, "ms-settings:privacy", "隐私", "隐私设置", "privacy");
            Add(settings, "ms-settings:privacy-location", "位置", "位置隐私", "location");
            Add(settings, "ms-settings:privacy-webcam", "摄像头", "相机隐私", "camera", "webcam");
            Add(settings, "ms-settings:privacy-microphone", "麦克风", "麦克风隐私", "microphone");
            Add(settings, "ms-settings:windowsupdate", "更新", "windows更新", "系统更新", "update");
            Add(settings, "ms-settings:dateandtime", "时间", "日期和时间", "dateandtime");
            Add(settings, "ms-settings:regionlanguage", "语言", "区域和语言", "language");
            Add(settings, "ms-settings:personalization", "个性化", "个性化设置", "personalization");
            Add(settings, "ms-settings:yourinfo", "账户", "账号", "用户信息", "account");
            Add(settings, "ms-settings:easeofaccess", "辅助功能", "无障碍", "accessibility");
            Add(settings, "ms-settings:clipboard", "剪贴板", "剪贴板设置", "clipboard");
            Add(settings, "ms-settings:about", "关于", "系统信息", "about");
            return settings;
        }

        private static void Add(Dictionary<string, string> settings, string uri, params string[] aliases)
        {
            for (int i = 0; i < aliases.Length; i++)
            {
                settings[ComputerApplicationCatalog.Normalize(aliases[i])] = uri;
            }
        }
    }

    internal static class ComputerCommandPlanner
    {
        private static readonly string[] NonCommands = new string[]
        {
            "不要", "别", "不许", "怎么", "如何", "能不能", "可不可以", "是否", "假如", "如果", "比如", "例如", "他说", "她说", "它说"
        };

        private static readonly string[] PolitePrefixes = new string[]
        {
            "麻烦你帮我", "麻烦帮我", "请你帮我", "请帮我", "帮我", "麻烦你", "麻烦", "请你", "请", "给我"
        };

        private static readonly string[] DevicePrefixes = new string[]
        {
            "电脑里的", "电脑里", "电脑上的", "电脑上", "电脑的", "本机里的", "本机的", "本机"
        };

        public static bool TryPlan(string goal, out List<DesktopAction> actions)
        {
            actions = new List<DesktopAction>();
            string command = Clean(goal);
            if (command.Length == 0 || ContainsAny(command, NonCommands))
            {
                return false;
            }

            command = TrimPrefix(command, PolitePrefixes);
            string verb = "";
            if (command.StartsWith("打开", StringComparison.Ordinal))
            {
                verb = "打开";
            }
            else if (command.StartsWith("启动", StringComparison.Ordinal))
            {
                verb = "启动";
            }

            if (verb.Length == 0)
            {
                return false;
            }

            string target = command.Substring(verb.Length).Trim();
            target = TrimPrefix(target, DevicePrefixes);
            bool playAfterOpen = false;
            string[] playConnectors = new string[] { "并开始播放", "然后开始播放", "并播放音乐", "然后播放音乐", "并播放", "然后播放" };
            for (int i = 0; i < playConnectors.Length; i++)
            {
                int connector = target.IndexOf(playConnectors[i], StringComparison.Ordinal);
                if (connector > 0)
                {
                    target = target.Substring(0, connector).Trim();
                    playAfterOpen = true;
                    break;
                }
            }

            target = TrimSuffix(target, new string[] { "这个软件", "软件", "这个应用", "应用", "程序" });
            if (target.Length == 0)
            {
                return false;
            }

            ComputerApplication application;
            try
            {
                application = ComputerApplicationCatalog.Resolve(target);
            }
            catch
            {
                return false;
            }

            DesktopAction open = new DesktopAction();
            open.Type = "app_open";
            open.App = application.LaunchDescription;
            open.Summary = "打开“" + application.LaunchDescription + "”";
            actions.Add(open);
            if (playAfterOpen)
            {
                DesktopAction play = new DesktopAction();
                play.Type = "media_play";
                play.App = application.LaunchDescription;
                play.Summary = "在“" + application.LaunchDescription + "”中开始播放";
                actions.Add(play);
            }

            return true;
        }

        private static string Clean(string value)
        {
            return (value ?? "").Trim().Trim('。', '！', '!', '？', '?', '，', ',', ' ');
        }

        private static string TrimPrefix(string value, string[] prefixes)
        {
            string result = value ?? "";
            for (int i = 0; i < prefixes.Length; i++)
            {
                if (result.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    return result.Substring(prefixes[i].Length).Trim();
                }
            }

            return result;
        }

        private static string TrimSuffix(string value, string[] suffixes)
        {
            string result = value ?? "";
            for (int i = 0; i < suffixes.Length; i++)
            {
                if (result.EndsWith(suffixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    return result.Substring(0, result.Length - suffixes[i].Length).Trim();
                }
            }

            return result;
        }

        private static bool ContainsAny(string value, string[] terms)
        {
            for (int i = 0; i < terms.Length; i++)
            {
                if (value.IndexOf(terms[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal static class ComputerWindowLocator
    {
        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        private const int SwMinimize = 6;
        private const int SwRestore = 9;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        public static IntPtr FindBest(ComputerApplication application)
        {
            IntPtr best = IntPtr.Zero;
            int bestScore = 0;
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                if (!IsWindowVisible(window))
                {
                    return true;
                }

                StringBuilder titleBuilder = new StringBuilder(512);
                GetWindowText(window, titleBuilder, titleBuilder.Capacity);
                string title = titleBuilder.ToString();
                uint processId;
                GetWindowThreadProcessId(window, out processId);
                string processName;
                try
                {
                    using (Process process = Process.GetProcessById((int)processId))
                    {
                        processName = process.ProcessName;
                    }
                }
                catch
                {
                    return true;
                }

                int score = ScoreWindow(application, processName, title);
                if (score > bestScore)
                {
                    best = window;
                    bestScore = score;
                }

                return true;
            }, IntPtr.Zero);
            return bestScore >= 100 ? best : IntPtr.Zero;
        }

        public static async Task<IntPtr> WaitForWindowAsync(
            ComputerApplication application,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeoutMilliseconds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntPtr window = FindBest(application);
                if (window != IntPtr.Zero)
                {
                    return window;
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            return IntPtr.Zero;
        }

        public static async Task RestoreAndFocusAsync(IntPtr window, bool requireForeground, CancellationToken cancellationToken)
        {
            ShowWindow(window, SwRestore);
            BringWindowToTop(window);
            SetForegroundWindow(window);
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            if (IsIconic(window))
            {
                throw new InvalidOperationException("目标窗口仍处于最小化状态。");
            }

            if (requireForeground && GetForegroundWindow() != window)
            {
                throw new InvalidOperationException("Windows 阻止了目标窗口切换到前台。");
            }
        }

        public static void Minimize(IntPtr window)
        {
            ShowWindow(window, SwMinimize);
        }

        public static bool IsMinimized(IntPtr window)
        {
            return IsIconic(window);
        }

        public static bool IsWindowHandle(IntPtr window)
        {
            return window != IntPtr.Zero && IsWindow(window);
        }

        public static async Task CloseWindowAsync(IntPtr window, CancellationToken cancellationToken)
        {
            const uint WmClose = 0x0010;
            if (!IsWindowHandle(window) || !PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero))
            {
                throw new InvalidOperationException("Windows 拒绝了正常关闭窗口请求。");
            }

            DateTime limit = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsWindowHandle(window))
                {
                    return;
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        public static string GetVisibleWindowSummary(int maximum)
        {
            int limit = Math.Max(1, maximum);
            List<string> windows = new List<string>();
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                if (windows.Count >= limit || !IsWindowVisible(window))
                {
                    return windows.Count < limit;
                }

                StringBuilder titleBuilder = new StringBuilder(512);
                GetWindowText(window, titleBuilder, titleBuilder.Capacity);
                string title = titleBuilder.ToString().Trim();
                if (title.Length == 0)
                {
                    return true;
                }

                uint processId;
                GetWindowThreadProcessId(window, out processId);
                string processName;
                try
                {
                    using (Process process = Process.GetProcessById((int)processId))
                    {
                        processName = process.ProcessName;
                    }
                }
                catch
                {
                    return true;
                }

                if (PrivacyRedactor.IsBlockedProcess(processName))
                {
                    return true;
                }

                if (title.Length > 120)
                {
                    title = title.Substring(0, 119) + "…";
                }

                windows.Add(processName + "\t" + title + (IsIconic(window) ? "\t已最小化" : "\t可见"));
                return true;
            }, IntPtr.Zero);
            return windows.Count == 0 ? "没有可显示的普通窗口。" : String.Join("\n", windows.ToArray());
        }

        private static int ScoreWindow(ComputerApplication application, string processName, string title)
        {
            string process = ComputerApplicationCatalog.Normalize(processName);
            string normalizedTitle = ComputerApplicationCatalog.Normalize(title);
            int score = 0;
            string[] hints = application.ProcessHints ?? new string[0];
            for (int i = 0; i < hints.Length; i++)
            {
                string hint = ComputerApplicationCatalog.Normalize(hints[i]);
                if (hint.Length > 0 && process == hint)
                {
                    score = Math.Max(score, 300);
                }
                else if (hint.Length >= 4 && process.Contains(hint))
                {
                    score = Math.Max(score, 220);
                }
            }

            string name = ComputerApplicationCatalog.Normalize(application.Name);
            if (name.Length > 0 && normalizedTitle.Contains(name))
            {
                score = Math.Max(score, 240);
            }

            string[] aliases = application.Aliases ?? new string[0];
            for (int i = 0; i < aliases.Length; i++)
            {
                string alias = ComputerApplicationCatalog.Normalize(aliases[i]);
                if (alias.Length >= 2 && normalizedTitle.Contains(alias))
                {
                    score = Math.Max(score, 180);
                }
            }

            return score;
        }
    }

    internal static class ComputerMediaController
    {
        private const string MediaScript = @"
$ErrorActionPreference='Stop'
$OutputEncoding=[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
function Await($async,[Type]$resultType) {
  $method=[System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetGenericArguments().Count -eq 1 -and $_.GetParameters().Count -eq 1
  } | Select-Object -First 1
  $task=$method.MakeGenericMethod($resultType).Invoke($null,@($async))
  $task.Wait()
  return $task.Result
}
try {
  $managerType=[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager,Windows.Media.Control,ContentType=WindowsRuntime]
  $manager=Await ($managerType::RequestAsync()) $managerType
  $target=([string]$env:NONO_MEDIA_TARGET).ToLowerInvariant()
  $action=([string]$env:NONO_MEDIA_ACTION).ToLowerInvariant()
  $session=$null
  $limit=[DateTime]::UtcNow.AddSeconds(5)
  do {
    $sessions=@($manager.GetSessions())
    if ([string]::IsNullOrWhiteSpace($target)) {
      $session=$manager.GetCurrentSession()
    } else {
      $session=$sessions | Where-Object {
        $source=([string]$_.SourceAppUserModelId).ToLowerInvariant()
        $source -eq $target -or $source.Contains($target) -or $target.Contains($source)
      } | Select-Object -First 1
    }
    if ($null -eq $session) { Start-Sleep -Milliseconds 120 }
  } while ($null -eq $session -and [DateTime]::UtcNow -lt $limit)
  if ($null -eq $session) { throw '没有找到目标应用的 Windows 媒体会话。' }
  switch ($action) {
    'play' { $accepted=Await ($session.TryPlayAsync()) ([bool]) }
    'pause' { $accepted=Await ($session.TryPauseAsync()) ([bool]) }
    'next' { $accepted=Await ($session.TrySkipNextAsync()) ([bool]) }
    'previous' { $accepted=Await ($session.TrySkipPreviousAsync()) ([bool]) }
    default { throw '不支持的媒体动作。' }
  }
  if (-not $accepted) { throw '媒体应用拒绝了控制请求。' }
  Start-Sleep -Milliseconds 180
  $status=[string]$session.GetPlaybackInfo().PlaybackStatus
  if ($action -eq 'play' -and $status -ne 'Playing') { throw ('播放状态验证失败，当前状态：'+$status) }
  if ($action -eq 'pause' -and $status -eq 'Playing') { throw ('暂停状态验证失败，当前状态：'+$status) }
  [Console]::WriteLine('OK'+[char]9+[string]$session.SourceAppUserModelId+[char]9+$status)
} catch {
  [Console]::WriteLine('ERROR'+[char]9+$_.Exception.Message)
  exit 2
}";

        public static async Task<string> ControlAsync(string action, string app, CancellationToken cancellationToken)
        {
            string target = (app ?? "").Trim();
            string display = target;
            if (target.Length > 0)
            {
                try
                {
                    ComputerApplication application = ComputerApplicationCatalog.Resolve(target);
                    display = application.LaunchDescription;
                    target = String.IsNullOrWhiteSpace(application.AppId) ? target : application.AppId;
                }
                catch (InvalidOperationException)
                {
                    // A media-session source can be valid without a Start Apps entry.
                }
            }

            Dictionary<string, string> environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            environment["NONO_MEDIA_ACTION"] = action;
            environment["NONO_MEDIA_TARGET"] = target;
            FixedPowerShellResult result = await FixedPowerShellRunner.RunAsync(
                MediaScript,
                environment,
                8000,
                cancellationToken).ConfigureAwait(false);
            string[] fields = (result.Output ?? "").Trim().Split('\t');
            if (result.ExitCode != 0 || fields.Length == 0 || !String.Equals(fields[0], "OK", StringComparison.Ordinal))
            {
                string message = fields.Length > 1 ? fields[1] : result.Error;
                throw new InvalidOperationException(String.IsNullOrWhiteSpace(message) ? "Windows 媒体控制失败。" : message.Trim());
            }

            string status = fields.Length > 2 ? fields[2] : "已确认";
            string targetText = String.IsNullOrWhiteSpace(display) ? "当前媒体应用" : "“" + display + "”";
            switch (action)
            {
                case "play": return targetText + "已开始播放，系统状态为 " + status;
                case "pause": return targetText + "已暂停，系统状态为 " + status;
                case "next": return targetText + "已切换到下一首";
                case "previous": return targetText + "已切换到上一首";
                default: return targetText + "媒体操作已完成";
            }
        }
    }

    internal sealed class FixedPowerShellResult
    {
        public int ExitCode;
        public string Output;
        public string Error;
    }

    internal static class FixedPowerShellRunner
    {
        public static FixedPowerShellResult Run(string script, IDictionary<string, string> environment, int timeoutMilliseconds)
        {
            return RunAsync(script, environment, timeoutMilliseconds, CancellationToken.None).GetAwaiter().GetResult();
        }

        public static async Task<FixedPowerShellResult> RunAsync(
            string script,
            IDictionary<string, string> environment,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script ?? ""));
            ProcessStartInfo startInfo = new ProcessStartInfo("powershell.exe");
            startInfo.Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.CreateNoWindow = true;
            startInfo.StandardOutputEncoding = new UTF8Encoding(false);
            startInfo.StandardErrorEncoding = new UTF8Encoding(false);
            if (environment != null)
            {
                foreach (KeyValuePair<string, string> item in environment)
                {
                    startInfo.EnvironmentVariables[item.Key] = item.Value ?? "";
                }
            }

            using (Process process = new Process())
            {
                process.StartInfo = startInfo;
                process.Start();
                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();
                Stopwatch timer = Stopwatch.StartNew();
                try
                {
                    while (!process.HasExited)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (timer.ElapsedMilliseconds >= timeoutMilliseconds)
                        {
                            try { process.Kill(); }
                            catch { }
                            throw new TimeoutException("固定系统任务执行超时。");
                        }

                        await Task.Delay(40, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(); }
                        catch { }
                    }

                    throw;
                }

                process.WaitForExit();
                return new FixedPowerShellResult
                {
                    ExitCode = process.ExitCode,
                    Output = await outputTask.ConfigureAwait(false),
                    Error = await errorTask.ConfigureAwait(false)
                };
            }
        }
    }
}
