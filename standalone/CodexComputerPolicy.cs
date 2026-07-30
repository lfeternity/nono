using System;
using System.Collections.Generic;
using System.Globalization;

namespace NoNoStandalone
{
    internal static class CodexComputerPolicy
    {
        private static readonly string[] ExplicitPrefixes = new string[]
        {
            "用codex", "使用codex", "让codex", "交给codex", "请codex",
            "用 codex", "使用 codex", "让 codex", "交给 codex", "请 codex"
        };

        private static readonly string[] NegativeSignals = new string[]
        {
            "不要", "别", "不许", "禁止", "无需", "不用", "不能", "不需要",
            "don't", "do not", "never", "not "
        };

        private static readonly string[] QuestionSignals = new string[]
        {
            "怎么", "如何", "为什么", "为何", "什么是", "是什么意思", "可以吗", "行不行",
            "是否", "怎么办", "?", "？", "how ", "why ", "what "
        };

        private static readonly string[] ReportedSignals = new string[]
        {
            "他说", "她说", "他们说", "有人说", "假如", "如果", "比如", "例如", "这句话", "命令是",
            "he said", "she said", "if ", "for example"
        };

        private static readonly HashSet<string> LowRiskTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "computer_capabilities",
            "computer_app_list", "computer_app_open", "computer_app_focus",
            "computer_window_list", "computer_window_minimize", "computer_window_restore",
            "computer_media_play", "computer_media_pause", "computer_media_next", "computer_media_previous",
            "computer_browser_open_url", "computer_browser_search",
            "computer_system_show_desktop", "computer_system_open_settings",
            "computer_file_list", "computer_file_find", "computer_file_info", "computer_file_open", "computer_folder_open",
            "computer_process_list", "computer_process_start",
            "computer_verify"
        };

        private static readonly HashSet<string> MediumRiskTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "computer_file_read", "computer_clipboard_read", "computer_clipboard_write",
            "computer_file_create_text", "computer_file_append_text", "computer_directory_create",
            "computer_system_cancel_power"
        };

        private static readonly HashSet<string> HighRiskTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "computer_window_close", "computer_file_copy", "computer_file_move",
            "computer_file_rename", "computer_process_stop", "computer_system_power"
        };

        private static readonly HashSet<string> ReadOnlyTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "computer_capabilities", "computer_app_list", "computer_window_list",
            "computer_file_list", "computer_file_find", "computer_file_info", "computer_file_read",
            "computer_clipboard_read", "computer_process_list", "computer_verify"
        };

        private static readonly HashSet<string> MediumStateChangingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "computer_clipboard_write", "computer_file_create_text", "computer_file_append_text",
            "computer_directory_create", "computer_system_cancel_power"
        };

        public static bool IsExplicitCodexGoal(string goal)
        {
            string value = Normalize(goal);
            for (int i = 0; i < ExplicitPrefixes.Length; i++)
            {
                if (value.StartsWith(ExplicitPrefixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static string StripExplicitCodexPrefix(string goal)
        {
            string original = (goal ?? "").Trim();
            for (int i = 0; i < ExplicitPrefixes.Length; i++)
            {
                string prefix = ExplicitPrefixes[i];
                if (original.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return original.Substring(prefix.Length).Trim(' ', '，', ',', '：', ':', '。');
                }
            }

            return original;
        }

        public static CodexComputerPolicyResult Evaluate(CodexComputerToolCall call)
        {
            string tool = call == null ? "" : (call.Tool ?? "").Trim();
            CodexComputerPolicyResult result = new CodexComputerPolicyResult();
            result.Description = Describe(call);
            result.Allowed = true;
            result.Risk = DesktopActionRisk.Low;
            result.Reason = "只读取状态或执行可逆的电脑操作";

            if (CodexComputerSafety.IsForbiddenDeletionTool(tool))
            {
                return Block(result, "删除文件和移入回收站工具被永久禁用");
            }

            if (!CodexComputerTools.IsSupported(tool))
            {
                return Block(result, "工具不在宠物允许列表中");
            }

            string goal = call == null ? "" : call.Goal;
            string forbiddenReason = CodexComputerSafety.GetForbiddenGoalReason(goal);
            if (!String.IsNullOrWhiteSpace(forbiddenReason))
            {
                return Block(result, forbiddenReason);
            }

            string arguments = ArgumentsText(call);
            if ((String.Equals(tool, "computer_browser_open_url", StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(tool, "computer_browser_search", StringComparison.OrdinalIgnoreCase)) &&
                CodexComputerSafety.ContainsPaymentIntent(arguments))
            {
                return Block(result, "浏览器工具不允许打开或搜索支付、购买、下单、转账等资金操作");
            }

            if ((String.Equals(tool, "computer_file_read", StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(tool, "computer_clipboard_read", StringComparison.OrdinalIgnoreCase)) &&
                (CodexComputerSafety.ContainsCredentialSignal(goal) || CodexComputerSafety.ContainsCredentialSignal(arguments)))
            {
                return Block(result, "凭据、密码、验证码、令牌和密钥不发送给 Codex");
            }

            if (HighRiskTools.Contains(tool))
            {
                result.ChangesState = true;
                result.Risk = DesktopActionRisk.High;
                result.Reason = String.Equals(tool, "computer_system_power", StringComparison.OrdinalIgnoreCase)
                    ? "该操作会改变 Windows 电源状态，可能中断未保存的工作"
                    : "该操作会关闭程序，或移动、复制、重命名文件";
            }
            else if (MediumRiskTools.Contains(tool))
            {
                result.Risk = DesktopActionRisk.Medium;
                result.ChangesState = MediumStateChangingTools.Contains(tool);
                result.Reason = result.ChangesState
                    ? "该操作会写入剪贴板、创建或追加用户文件，需要确认"
                    : "文件或剪贴板内容将发送给 Codex，需要确认";
            }
            else if (LowRiskTools.Contains(tool))
            {
                result.ChangesState = !ReadOnlyTools.Contains(tool);
            }

            if (result.ChangesState && LooksLikeNonCommand(call == null ? "" : call.Goal))
            {
                return Block(result, "原始指令包含否定、疑问、条件或转述，不能执行会改变电脑状态的操作");
            }

            return result;
        }

        public static bool RunSelfTest()
        {
            CodexComputerToolCall open = Call("打开 ToDesk", "computer_app_open", "app", "ToDesk");
            CodexComputerToolCall question = Call("ToDesk 怎么打开？", "computer_app_open", "app", "ToDesk");
            CodexComputerToolCall remove = Call("删除下载目录的 test.tmp", "computer_file_trash", "path", "test.tmp");
            CodexComputerToolCall secret = Call("读取密码", "computer_file_read", "path", "password.txt");
            CodexComputerToolCall browser = Call("用 Chrome 打开 example.com", "computer_browser_open_url", "url", "https://example.com");
            CodexComputerToolCall payment = Call("打开付款页面", "computer_browser_open_url", "url", "https://example.com/checkout");
            CodexComputerToolCall create = Call("新建说明文件", "computer_file_create_text", "path", "notes.txt");
            return IsExplicitCodexGoal("用 Codex 打开 ToDesk") &&
                StripExplicitCodexPrefix("用 Codex：打开 ToDesk") == "打开 ToDesk" &&
                Evaluate(open).Allowed &&
                !Evaluate(question).Allowed &&
                !Evaluate(remove).Allowed &&
                !String.IsNullOrWhiteSpace(CodexComputerSafety.GetForbiddenGoalReason("删除下载目录的 test.tmp")) &&
                !Evaluate(secret).Allowed &&
                Evaluate(browser).Allowed &&
                !Evaluate(payment).Allowed &&
                Evaluate(create).Allowed && Evaluate(create).ChangesState &&
                Evaluate(create).Risk == DesktopActionRisk.Medium;
        }

        private static CodexComputerToolCall Call(string goal, string tool, string key, string value)
        {
            CodexComputerToolCall call = new CodexComputerToolCall();
            call.Goal = goal;
            call.Tool = tool;
            call.Arguments = new Dictionary<string, object>();
            call.Arguments[key] = value;
            return call;
        }

        private static CodexComputerPolicyResult Block(CodexComputerPolicyResult result, string reason)
        {
            result.Allowed = false;
            result.Risk = DesktopActionRisk.Blocked;
            result.Reason = reason;
            return result;
        }

        private static bool LooksLikeNonCommand(string goal)
        {
            string value = Normalize(goal);
            return ContainsAny(value, NegativeSignals) ||
                ContainsAny(value, QuestionSignals) ||
                ContainsAny(value, ReportedSignals);
        }

        private static string ArgumentsText(CodexComputerToolCall call)
        {
            if (call == null || call.Arguments == null)
            {
                return "";
            }

            List<string> values = new List<string>();
            foreach (KeyValuePair<string, object> item in call.Arguments)
            {
                values.Add(Convert.ToString(item.Value, CultureInfo.InvariantCulture) ?? "");
            }

            return String.Join(" ", values.ToArray());
        }

        private static string Describe(CodexComputerToolCall call)
        {
            string tool = call == null ? "" : call.Tool ?? "";
            string app = Read(call, "app");
            string path = Read(call, "path");
            string source = Read(call, "source");
            string destination = Read(call, "destination");
            string process = Read(call, "process");
            string url = Read(call, "url");
            string query = Read(call, "query");
            string action = Read(call, "action");
            switch (tool)
            {
                case "computer_capabilities": return "读取宠物电脑能力清单";
                case "computer_app_list": return "读取本机应用列表";
                case "computer_app_open": return "打开“" + app + "”";
                case "computer_app_focus": return "切换到“" + app + "”";
                case "computer_window_list": return "读取可见窗口列表";
                case "computer_window_minimize": return "最小化“" + app + "”";
                case "computer_window_restore": return "恢复“" + app + "”窗口";
                case "computer_window_close": return "关闭“" + app + "”窗口";
                case "computer_media_play": return "开始播放媒体";
                case "computer_media_pause": return "暂停媒体";
                case "computer_media_next": return "切换到下一首";
                case "computer_media_previous": return "切换到上一首";
                case "computer_browser_open_url": return "在浏览器中打开“" + url + "”";
                case "computer_browser_search": return "在浏览器中搜索“" + query + "”";
                case "computer_clipboard_read": return "读取文本剪贴板并交给 Codex";
                case "computer_clipboard_write": return "写入文本剪贴板";
                case "computer_system_show_desktop": return "显示 Windows 桌面";
                case "computer_system_open_settings": return "打开 Windows 设置";
                case "computer_system_power": return "执行 Windows 电源操作“" + action + "”";
                case "computer_system_cancel_power": return "取消计划中的关机或重启";
                case "computer_file_list": return "列出授权目录中的文件";
                case "computer_file_find": return "在授权目录中查找文件";
                case "computer_file_info": return "读取“" + path + "”的文件信息";
                case "computer_file_read": return "读取文件“" + path + "”并交给 Codex";
                case "computer_file_create_text": return "新建文本文件“" + path + "”";
                case "computer_file_append_text": return "向文本文件“" + path + "”追加内容";
                case "computer_directory_create": return "新建文件夹“" + path + "”";
                case "computer_file_copy": return "复制文件“" + source + "”到“" + destination + "”";
                case "computer_file_move": return "移动文件“" + source + "”到“" + destination + "”";
                case "computer_file_rename": return "重命名文件“" + path + "”";
                case "computer_file_open": return "打开文件“" + path + "”";
                case "computer_folder_open": return "打开文件夹“" + path + "”";
                case "computer_process_list": return "读取进程列表";
                case "computer_process_start": return "启动“" + app + "”";
                case "computer_process_stop": return "请求关闭进程“" + process + "”";
                case "computer_verify": return "验证电脑状态";
                default: return "执行 Codex 电脑工具“" + tool + "”";
            }
        }

        private static string Read(CodexComputerToolCall call, string key)
        {
            object value;
            return call != null && call.Arguments != null && call.Arguments.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : "";
        }

        private static bool ContainsAny(string value, string[] words)
        {
            string source = value ?? "";
            for (int i = 0; i < words.Length; i++)
            {
                if (source.IndexOf(words[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string Normalize(string value)
        {
            return (value ?? "").Trim().ToLowerInvariant().Replace(" ", "");
        }
    }
}
