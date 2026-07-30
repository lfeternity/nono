using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace NoNoStandalone
{
    internal enum VoiceIntentType
    {
        Control = 0,
        PetAction = 1,
        ScreenRead = 2,
        ComputerAction = 3,
        Conversation = 4,
        Clarify = 5
    }

    internal enum VoiceControlCommand
    {
        None = 0,
        Stop = 1,
        Approve = 2,
        Reject = 3
    }

    internal enum VoiceLocalCommand
    {
        None = 0,
        Idle = 1,
        Wave = 2,
        Jump = 3,
        ShowDesktop = 4,
        ShowPanel = 5,
        ShowQuickLauncher = 6,
        OpenNotepad = 7,
        OpenCalculator = 8,
        OpenFileExplorer = 9,
        OpenWindowsSettings = 10
    }

    internal enum VoiceRouteSource
    {
        LocalFastPath = 0,
        LocalHeuristic = 1,
        SemanticRequired = 2
    }

    internal sealed class VoiceCommandRoute
    {
        public string OriginalText;
        public string NormalizedText;
        public string Goal;
        public string ResponseText;
        public string ContinuationPrefix;
        public VoiceIntentType Intent;
        public VoiceControlCommand Control;
        public VoiceLocalCommand LocalCommand;
        public VoiceRouteSource Source;
        public double Confidence;
        public bool RequiresScreen;
        public bool AllowActions;
        public bool RequiresSemanticIntent;

        public static VoiceCommandRoute Create(string original, string normalized)
        {
            VoiceCommandRoute route = new VoiceCommandRoute();
            route.OriginalText = original ?? "";
            route.NormalizedText = normalized ?? "";
            route.Goal = (original ?? "").Trim();
            route.Intent = VoiceIntentType.Conversation;
            route.Source = VoiceRouteSource.LocalHeuristic;
            route.Confidence = 0.5;
            return route;
        }
    }

    internal static class VoiceCommandRouter
    {
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

        private static readonly string[] NegativeSignals = new string[]
        {
            "不要", "别", "不许", "不用", "无需", "不能", "不需要", "禁止",
            "don't", "do not", "never", "not "
        };

        private static readonly string[] QuestionSignals = new string[]
        {
            "怎么", "如何", "为什么", "为何", "什么是", "是什么意思", "能否", "可以吗", "行不行",
            "怎么办", "?", "？", "how ", "why ", "what "
        };

        private static readonly string[] ReportedSpeechSignals = new string[]
        {
            "他说", "她说", "他们说", "有人说", "如果", "假如", "比如", "例如", "这句话", "命令是",
            "he said", "she said", "if ", "for example"
        };

        private static readonly string[] ScreenReferences = new string[]
        {
            "屏幕", "当前窗口", "这个窗口", "这个界面", "当前界面", "页面", "网页", "报错", "错误信息",
            "这里", "眼前", "桌面", "弹窗", "按钮", "输入框", "菜单", "screen", "window", "page", "error"
        };

        private static readonly string[] ReadVerbs = new string[]
        {
            "看一下", "看看", "查看", "读一下", "读取", "识别", "解释", "分析", "检查",
            "look at", "read", "inspect", "explain"
        };

        private static readonly string[] ActionVerbs = new string[]
        {
            "重新运行", "重新打开", "帮我点", "点击", "点一下", "打开", "关闭", "切换", "输入", "填写",
            "选择", "滚动", "重试", "运行", "提交", "发送", "删除", "保存", "安装", "操作", "执行",
            "查找", "寻找", "找到", "整理", "复制", "移动", "重命名", "播放", "暂停", "最小化", "恢复",
            "访问", "浏览", "搜索", "上网搜", "新建", "创建", "追加", "写入", "打开网址", "跳转",
            "锁定", "锁屏", "关机", "重启", "睡眠", "休眠",
            "click", "open", "close", "switch", "type", "fill", "select", "scroll", "retry", "run",
            "submit", "send", "delete", "save", "install", "visit", "browse", "search", "create", "append",
            "write", "lock", "shutdown", "restart", "sleep", "hibernate", "copy", "move", "rename", "play", "pause"
        };

        private static readonly string[] DeicticActionPrefixes = new string[]
        {
            "点那个", "点这个", "点刚才那个", "选择那个", "选择这个", "打开那个", "打开这个"
        };

        private static readonly string[] PolitePrefixes = new string[]
        {
            "麻烦你帮我", "麻烦帮我", "请你帮我", "能不能帮我", "可以帮我", "请帮我", "帮我", "麻烦你",
            "麻烦", "请你", "请", "给我"
        };

        private static readonly HashSet<string> StopCommands = Set(
            "停下", "停止", "停止操作", "取消操作", "别动了", "立即停止", "stop", "cancel");

        private static readonly HashSet<string> ApprovalCommands = Set(
            "确认", "确定", "继续", "执行", "可以", "是", "yes", "confirm");

        private static readonly HashSet<string> RejectionCommands = Set(
            "取消", "不要", "不执行", "否", "算了", "no", "reject");

        private static readonly Dictionary<string, VoiceLocalCommand> LocalCommands = CreateLocalCommands();

        public static VoiceCommandRoute Route(string text)
        {
            string original = (text ?? "").Trim();
            string normalized = Normalize(original);
            VoiceCommandRoute route = VoiceCommandRoute.Create(original, normalized);
            if (normalized.Length == 0)
            {
                return Clarify(route, "我没有听清楚，请再说一次。");
            }

            if (StopCommands.Contains(normalized))
            {
                return Control(route, VoiceControlCommand.Stop);
            }

            if (ApprovalCommands.Contains(normalized))
            {
                return Control(route, VoiceControlCommand.Approve);
            }

            if (RejectionCommands.Contains(normalized))
            {
                return Control(route, VoiceControlCommand.Reject);
            }

            string commandText = StripPolitePrefix(normalized);
            VoiceLocalCommand localCommand;
            if (LocalCommands.TryGetValue(commandText, out localCommand))
            {
                route.Intent = VoiceIntentType.PetAction;
                route.LocalCommand = localCommand;
                route.Source = VoiceRouteSource.LocalFastPath;
                route.Confidence = 1.0;
                return route;
            }

            if (CodexComputerPolicy.IsExplicitCodexGoal(commandText))
            {
                string codexGoal = CodexComputerPolicy.StripExplicitCodexPrefix(commandText);
                if (String.IsNullOrWhiteSpace(codexGoal))
                {
                    return Clarify(route, "你想让 Codex 操作什么？请把目标说完整。", "用 Codex ");
                }

                return Screen(route, VoiceIntentType.ComputerAction, true, false, 1.0);
            }

            bool hasAction = ContainsAny(commandText, ActionVerbs);
            bool hasRead = ContainsAny(commandText, ReadVerbs);
            bool hasScreenReference = ContainsAny(commandText, ScreenReferences);
            bool ambiguous = ContainsAny(commandText, NegativeSignals) ||
                ContainsAny(commandText, QuestionSignals) ||
                ContainsAny(commandText, ReportedSpeechSignals);

            string matchedAction;
            if (StartsWithAny(commandText, ActionVerbs, out matchedAction))
            {
                string target = commandText.Substring(matchedAction.Length).Trim(TrimCharacters);
                if (target.Length == 0 || IsOnlyActionFiller(target))
                {
                    return Clarify(route, "你想让我操作什么？请把目标说完整。", matchedAction);
                }

                if (!ambiguous)
                {
                    return Screen(route, VoiceIntentType.ComputerAction, true, false, 0.98);
                }
            }

            string matchedRead;
            if (StartsWithAny(commandText, ReadVerbs, out matchedRead) && hasScreenReference && !hasAction && !ambiguous)
            {
                return Screen(route, VoiceIntentType.ScreenRead, false, false, 0.98);
            }

            if (hasAction)
            {
                return Screen(route, VoiceIntentType.ComputerAction, true, true, 0.58);
            }

            if (StartsWithAny(commandText, DeicticActionPrefixes, out matchedAction))
            {
                return Screen(route, VoiceIntentType.ComputerAction, true, true, 0.72);
            }

            if (hasRead && hasScreenReference)
            {
                return Screen(route, VoiceIntentType.ScreenRead, false, ambiguous, ambiguous ? 0.62 : 0.92);
            }

            if (hasScreenReference)
            {
                return Screen(route, VoiceIntentType.ScreenRead, false, true, 0.55);
            }

            route.Intent = VoiceIntentType.Conversation;
            route.Source = VoiceRouteSource.LocalHeuristic;
            route.Confidence = 0.96;
            return route;
        }

        public static string Normalize(string text)
        {
            string value = (text ?? "").Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
            value = Whitespace.Replace(value, " ");
            value = StripWakePrefix(value);
            return value.Trim(TrimCharacters);
        }

        public static bool RunSelfTest()
        {
            return Expect("诺诺，挥挥手", VoiceIntentType.PetAction, VoiceLocalCommand.Wave, false) &&
                Expect("回到桌面", VoiceIntentType.PetAction, VoiceLocalCommand.ShowDesktop, false) &&
                Expect("打开计算器", VoiceIntentType.PetAction, VoiceLocalCommand.OpenCalculator, false) &&
                Expect("请你打开系统设置", VoiceIntentType.PetAction, VoiceLocalCommand.OpenWindowsSettings, false) &&
                Expect("停下", VoiceIntentType.Control, VoiceLocalCommand.None, false) &&
                Expect("打开微信", VoiceIntentType.ComputerAction, VoiceLocalCommand.None, false) &&
                Expect("能不能帮我打开微信", VoiceIntentType.ComputerAction, VoiceLocalCommand.None, false) &&
                Expect("点击登录按钮", VoiceIntentType.ComputerAction, VoiceLocalCommand.None, false) &&
                Expect("用 Codex 打开 ToDesk", VoiceIntentType.ComputerAction, VoiceLocalCommand.None, false) &&
                Expect("整理下载目录", VoiceIntentType.ComputerAction, VoiceLocalCommand.None, false) &&
                Expect("访问 example.com", VoiceIntentType.ComputerAction, VoiceLocalCommand.None, false) &&
                Expect("搜索 Windows API", VoiceIntentType.ComputerAction, VoiceLocalCommand.None, false) &&
                Expect("新建一个说明文件", VoiceIntentType.ComputerAction, VoiceLocalCommand.None, false) &&
                Expect("锁定电脑", VoiceIntentType.ComputerAction, VoiceLocalCommand.None, false) &&
                Expect("看看屏幕上的报错", VoiceIntentType.ScreenRead, VoiceLocalCommand.None, false) &&
                Expect("不要打开微信", VoiceIntentType.ComputerAction, VoiceLocalCommand.None, true) &&
                Expect("不要点击确定", VoiceIntentType.ComputerAction, VoiceLocalCommand.None, true) &&
                Expect("微信怎么打开", VoiceIntentType.ComputerAction, VoiceLocalCommand.None, true) &&
                Expect("如何关闭当前窗口", VoiceIntentType.ComputerAction, VoiceLocalCommand.None, true) &&
                Expect("他说让我点击确定", VoiceIntentType.ComputerAction, VoiceLocalCommand.None, true) &&
                Expect("比如打开微信", VoiceIntentType.ComputerAction, VoiceLocalCommand.None, true) &&
                Expect("点刚才那个按钮", VoiceIntentType.ComputerAction, VoiceLocalCommand.None, true) &&
                Expect("屏幕上有什么", VoiceIntentType.ScreenRead, VoiceLocalCommand.None, true) &&
                Expect("解释一下 async await", VoiceIntentType.Conversation, VoiceLocalCommand.None, false) &&
                Route("帮我打开").Intent == VoiceIntentType.Clarify && Route("帮我打开").ContinuationPrefix == "打开" &&
                Route("帮我点击").Intent == VoiceIntentType.Clarify;
        }

        private static bool Expect(
            string text,
            VoiceIntentType intent,
            VoiceLocalCommand localCommand,
            bool semanticRequired)
        {
            VoiceCommandRoute route = Route(text);
            return route.Intent == intent && route.LocalCommand == localCommand &&
                route.RequiresSemanticIntent == semanticRequired;
        }

        private static VoiceCommandRoute Control(VoiceCommandRoute route, VoiceControlCommand command)
        {
            route.Intent = VoiceIntentType.Control;
            route.Control = command;
            route.Source = VoiceRouteSource.LocalFastPath;
            route.Confidence = 1.0;
            return route;
        }

        private static VoiceCommandRoute Screen(
            VoiceCommandRoute route,
            VoiceIntentType intent,
            bool allowActions,
            bool semanticRequired,
            double confidence)
        {
            route.Intent = intent;
            route.RequiresScreen = true;
            route.AllowActions = allowActions;
            route.RequiresSemanticIntent = semanticRequired;
            route.Source = semanticRequired ? VoiceRouteSource.SemanticRequired : VoiceRouteSource.LocalFastPath;
            route.Confidence = confidence;
            return route;
        }

        private static VoiceCommandRoute Clarify(VoiceCommandRoute route, string response)
        {
            return Clarify(route, response, "");
        }

        private static VoiceCommandRoute Clarify(VoiceCommandRoute route, string response, string continuationPrefix)
        {
            route.Intent = VoiceIntentType.Clarify;
            route.ResponseText = response;
            route.ContinuationPrefix = continuationPrefix ?? "";
            route.Source = VoiceRouteSource.LocalFastPath;
            route.Confidence = 1.0;
            return route;
        }

        private static string StripWakePrefix(string value)
        {
            string[] prefixes = new string[] { "你好 nono", "你好nono", "你好诺诺", "nono", "诺诺" };
            for (int i = 0; i < prefixes.Length; i++)
            {
                if (value.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    return value.Substring(prefixes[i].Length).Trim(TrimCharacters);
                }
            }

            return value;
        }

        private static string StripPolitePrefix(string value)
        {
            string result = value;
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < PolitePrefixes.Length; i++)
                {
                    if (result.StartsWith(PolitePrefixes[i], StringComparison.OrdinalIgnoreCase))
                    {
                        result = result.Substring(PolitePrefixes[i].Length).Trim(TrimCharacters);
                        changed = true;
                        break;
                    }
                }
            }

            return result;
        }

        private static bool IsOnlyActionFiller(string value)
        {
            string normalized = value.Trim(TrimCharacters);
            return normalized == "一下" || normalized == "一下吧" || normalized == "吧" || normalized == "它" || normalized == "这个";
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

        private static bool StartsWithAny(string value, string[] words, out string matched)
        {
            for (int i = 0; i < words.Length; i++)
            {
                if (value.StartsWith(words[i], StringComparison.OrdinalIgnoreCase))
                {
                    matched = words[i];
                    return true;
                }
            }

            matched = "";
            return false;
        }

        private static HashSet<string> Set(params string[] values)
        {
            return new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, VoiceLocalCommand> CreateLocalCommands()
        {
            Dictionary<string, VoiceLocalCommand> commands = new Dictionary<string, VoiceLocalCommand>(StringComparer.OrdinalIgnoreCase);
            Add(commands, VoiceLocalCommand.Idle, "待机", "休息一下", "恢复待机");
            Add(commands, VoiceLocalCommand.Wave, "挥手", "挥挥手", "招手", "打个招呼");
            Add(commands, VoiceLocalCommand.Jump, "跳一下", "跳一跳", "蹦一下");
            Add(commands, VoiceLocalCommand.ShowDesktop, "回桌面", "回到桌面", "显示桌面");
            Add(commands, VoiceLocalCommand.ShowPanel, "打开面板", "显示面板", "打开你的面板");
            Add(commands, VoiceLocalCommand.ShowQuickLauncher, "打开快速直达", "显示快速直达", "打开快速启动");
            Add(commands, VoiceLocalCommand.OpenNotepad, "打开记事本", "启动记事本");
            Add(commands, VoiceLocalCommand.OpenCalculator, "打开计算器", "启动计算器");
            Add(commands, VoiceLocalCommand.OpenFileExplorer, "打开资源管理器", "打开文件资源管理器", "启动资源管理器");
            Add(commands, VoiceLocalCommand.OpenWindowsSettings, "打开系统设置", "打开 windows 设置", "打开windows设置");
            return commands;
        }

        private static void Add(Dictionary<string, VoiceLocalCommand> commands, VoiceLocalCommand command, params string[] phrases)
        {
            for (int i = 0; i < phrases.Length; i++)
            {
                commands[phrases[i]] = command;
            }
        }

        private static readonly char[] TrimCharacters = new char[]
        {
            ' ', '\t', '\r', '\n', '。', '！', '!', '？', '?', ',', '，', '、', ':', '：', ';', '；', '"', '\'', '“', '”'
        };
    }
}
