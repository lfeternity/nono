using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NoNoStandalone
{
    internal sealed class VoiceAssistantEventArgs : EventArgs
    {
        public string Type { get; private set; }
        public string State { get; private set; }
        public string Message { get; private set; }
        public string Text { get; private set; }
        public string Phrase { get; private set; }
        public string RequestId { get; private set; }
        public bool Fatal { get; private set; }

        public VoiceAssistantEventArgs(
            string type,
            string state,
            string message,
            string text,
            string phrase,
            string requestId,
            bool fatal)
        {
            Type = type ?? "";
            State = state ?? "";
            Message = message ?? "";
            Text = text ?? "";
            Phrase = phrase ?? "";
            RequestId = requestId ?? "";
            Fatal = fatal;
        }
    }

    internal sealed class VoiceAssistantSettings
    {
        public bool Enabled;
        public string PythonPath;
        public string ChatModel;
        public string OllamaUrl;
        public int FollowUpSeconds;
        public int TtsVoice;
        public int TtsRate;
        public int CaptionSeconds;
        public bool CommandRoutingEnabled;
        public bool CommandCaptionEnabled;

        public VoiceAssistantSettings Clone()
        {
            return (VoiceAssistantSettings)MemberwiseClone();
        }
    }

    internal sealed class TtsVoiceOption
    {
        public int Id { get; private set; }
        public string Code { get; private set; }
        public string DisplayName { get; private set; }

        public TtsVoiceOption(int id, string code, string displayName)
        {
            Id = id;
            Code = code ?? "";
            DisplayName = displayName ?? Code;
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    internal static class VoiceSettingsStore
    {
        private const string DefaultChatModel = "qwen3:4b-instruct-2507-q4_K_M";
        private const string LegacyThinkingModel = "qwen3:4b";
        private const int DefaultTtsVoice = 3;
        private const int DefaultTtsRate = 2;
        private const string TtsRateVersion = "2";
        private const string EnabledKey = "voice-enabled";
        private const string PythonPathKey = "voice-python-path";
        private const string ChatModelKey = "voice-chat-model";
        private const string OllamaUrlKey = "voice-ollama-url";
        private const string FollowUpSecondsKey = "voice-follow-up-seconds";
        private const string TtsVoiceKey = "voice-tts-voice";
        private const string TtsRateKey = "voice-tts-rate";
        private const string TtsRateVersionKey = "voice-tts-rate-version";
        private const string CaptionSecondsKey = "voice-caption-seconds";
        private const string CommandRoutingEnabledKey = "voice-command-routing-enabled";
        private const string CommandCaptionEnabledKey = "voice-command-caption-enabled";
        private static readonly TtsVoiceOption[] TtsVoiceOptions = CreateTtsVoiceOptions();

        public static readonly string ConfigFile = Path.Combine(PanelStorage.Root, "voice-config.json");

        public static VoiceAssistantSettings Load()
        {
            VoiceAssistantSettings settings = new VoiceAssistantSettings();
            settings.Enabled = String.Equals(LoadPreference(EnabledKey), "1", StringComparison.Ordinal);
            settings.PythonPath = LoadPreference(PythonPathKey);
            settings.ChatModel = MigrateChatModel(LoadPreference(ChatModelKey));
            settings.OllamaUrl = DefaultIfEmpty(LoadPreference(OllamaUrlKey), "http://127.0.0.1:11434/api/chat");
            settings.FollowUpSeconds = ParseRange(LoadPreference(FollowUpSecondsKey), 30, 5, 120);
            settings.TtsVoice = ParseRange(
                LoadPreference(TtsVoiceKey),
                DefaultTtsVoice,
                0,
                TtsVoiceOptions.Length - 1);
            settings.TtsRate = MigrateTtsRate(
                LoadPreference(TtsRateKey),
                LoadPreference(TtsRateVersionKey));
            settings.CaptionSeconds = ParseRange(LoadPreference(CaptionSecondsKey), 12, 3, 60);
            settings.CommandRoutingEnabled = LoadDefaultTrue(CommandRoutingEnabledKey);
            settings.CommandCaptionEnabled = LoadDefaultTrue(CommandCaptionEnabledKey);
            return settings;
        }

        public static void Save(VoiceAssistantSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            PanelStorage.SavePreference(EnabledKey, settings.Enabled ? "1" : "0");
            PanelStorage.SavePreference(PythonPathKey, settings.PythonPath ?? "");
            PanelStorage.SavePreference(ChatModelKey, DefaultIfEmpty(settings.ChatModel, DefaultChatModel));
            PanelStorage.SavePreference(OllamaUrlKey, DefaultIfEmpty(settings.OllamaUrl, "http://127.0.0.1:11434/api/chat"));
            PanelStorage.SavePreference(FollowUpSecondsKey, settings.FollowUpSeconds.ToString(CultureInfo.InvariantCulture));
            PanelStorage.SavePreference(TtsVoiceKey, settings.TtsVoice.ToString(CultureInfo.InvariantCulture));
            PanelStorage.SavePreference(TtsRateKey, settings.TtsRate.ToString(CultureInfo.InvariantCulture));
            PanelStorage.SavePreference(TtsRateVersionKey, TtsRateVersion);
            PanelStorage.SavePreference(CaptionSecondsKey, settings.CaptionSeconds.ToString(CultureInfo.InvariantCulture));
            PanelStorage.SavePreference(CommandRoutingEnabledKey, settings.CommandRoutingEnabled ? "1" : "0");
            PanelStorage.SavePreference(CommandCaptionEnabledKey, settings.CommandCaptionEnabled ? "1" : "0");
        }

        public static void WriteRuntimeConfig(VoiceAssistantSettings settings)
        {
            PanelStorage.EnsureRoot();
            Dictionary<string, object> config = new Dictionary<string, object>();
            config["asr_model"] = "Qwen/Qwen3-ASR-0.6B";
            config["device"] = "cuda:0";
            config["wake_phrases"] = new string[] { "nono", "诺诺", "你好 nono" };
            config["vad_threshold"] = 0.55;
            config["vad_release_threshold"] = 0.35;
            config["end_silence_ms"] = 600;
            config["wake_end_silence_ms"] = 620;
            config["command_end_silence_ms"] = 480;
            config["long_end_silence_ms"] = 680;
            config["max_utterance_seconds"] = 15;
            config["follow_up_seconds"] = settings.FollowUpSeconds;
            config["tts_model_dir"] = "models/tts/kokoro-multi-lang-v1_1";
            config["tts_voice"] = settings.TtsVoice;
            config["tts_threads"] = 4;
            config["ollama_url"] = settings.OllamaUrl;
            config["ollama_model"] = settings.ChatModel;
            config["external_conversation"] = settings.CommandRoutingEnabled;
            config["system_prompt"] = "你是诺诺，一只安静、准确、简洁的桌面 AI 宠物。默认使用中文回答，除非用户要求其他语言。回答适合直接朗读，避免 Markdown 表格和冗长列表。";

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            File.WriteAllText(ConfigFile, serializer.Serialize(config), new UTF8Encoding(false));
        }

        public static IList<TtsVoiceOption> GetTtsVoiceOptions()
        {
            return Array.AsReadOnly(TtsVoiceOptions);
        }

        private static TtsVoiceOption[] CreateTtsVoiceOptions()
        {
            List<TtsVoiceOption> options = new List<TtsVoiceOption>();
            options.Add(new TtsVoiceOption(0, "af_maple", "英文女声 Maple（af_maple）"));
            options.Add(new TtsVoiceOption(1, "af_sol", "英文女声 Sol（af_sol）"));
            options.Add(new TtsVoiceOption(2, "bf_vale", "英式女声 Vale（bf_vale）"));

            int[] femaleVoices = new int[]
            {
                1, 2, 3, 4, 5, 6, 7, 8, 17, 18, 19, 21, 22, 23, 24, 26, 27, 28,
                32, 36, 38, 39, 40, 42, 43, 44, 46, 47, 48, 49, 51, 59, 60, 67, 70,
                71, 72, 73, 74, 75, 76, 77, 78, 79, 83, 84, 85, 86, 87, 88, 90, 92,
                93, 94, 99
            };
            foreach (int number in femaleVoices)
            {
                string code = "zf_" + number.ToString("000", CultureInfo.InvariantCulture);
                string label = "中文女声 " + number.ToString("000", CultureInfo.InvariantCulture) + "（" + code;
                label += number == 1 ? "，默认）" : "）";
                options.Add(new TtsVoiceOption(options.Count, code, label));
            }

            int[] maleVoices = new int[]
            {
                9, 10, 11, 12, 13, 14, 15, 16, 20, 25, 29, 30, 31, 33, 34, 35, 37,
                41, 45, 50, 52, 53, 54, 55, 56, 57, 58, 61, 62, 63, 64, 65, 66, 68,
                69, 80, 81, 82, 89, 91, 95, 96, 97, 98, 100
            };
            foreach (int number in maleVoices)
            {
                string code = "zm_" + number.ToString("000", CultureInfo.InvariantCulture);
                string label = "中文男声 " + number.ToString("000", CultureInfo.InvariantCulture) + "（" + code + "）";
                options.Add(new TtsVoiceOption(options.Count, code, label));
            }

            return options.ToArray();
        }

        private static string LoadPreference(string key)
        {
            try
            {
                return PanelStorage.LoadPreference(key);
            }
            catch
            {
                return "";
            }
        }

        private static bool LoadDefaultTrue(string key)
        {
            string value = LoadPreference(key);
            return !String.Equals(value, "0", StringComparison.Ordinal);
        }

        private static string DefaultIfEmpty(string value, string fallback)
        {
            return String.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string MigrateChatModel(string value)
        {
            string model = DefaultIfEmpty(value, DefaultChatModel);
            return String.Equals(model, LegacyThinkingModel, StringComparison.OrdinalIgnoreCase)
                ? DefaultChatModel
                : model;
        }

        private static int MigrateTtsRate(string value, string version)
        {
            int rate = ParseRange(value, DefaultTtsRate, -10, 10);
            if (!String.Equals(version, TtsRateVersion, StringComparison.Ordinal) &&
                (String.IsNullOrWhiteSpace(value) || rate == 0))
            {
                return DefaultTtsRate;
            }

            return rate;
        }

        private static int ParseRange(string value, int fallback, int minimum, int maximum)
        {
            int parsed;
            if (!Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return fallback;
            }

            return Math.Max(minimum, Math.Min(maximum, parsed));
        }
    }

    internal sealed class VoiceRuntimeLocation
    {
        public readonly string PythonFile;
        public readonly string PythonArguments;
        public readonly string ServiceFile;
        public readonly string VoiceDirectory;

        public VoiceRuntimeLocation(string pythonFile, string pythonArguments, string serviceFile, string voiceDirectory)
        {
            PythonFile = pythonFile;
            PythonArguments = pythonArguments;
            ServiceFile = serviceFile;
            VoiceDirectory = voiceDirectory;
        }
    }

    internal static class VoiceRuntimeLocator
    {
        public static VoiceRuntimeLocation Resolve(VoiceAssistantSettings settings)
        {
            string voiceDirectory = FindVoiceDirectory();
            if (String.IsNullOrEmpty(voiceDirectory))
            {
                throw new FileNotFoundException("未找到 voice\\voice_service.py。请确认语音运行目录与程序一起发布。", "voice_service.py");
            }

            string serviceFile = Path.Combine(voiceDirectory, "voice_service.py");
            string configured = settings == null ? "" : settings.PythonPath;
            if (!String.IsNullOrWhiteSpace(configured))
            {
                string expanded = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
                if (!File.Exists(expanded))
                {
                    throw new FileNotFoundException("设置的 Python 路径不存在。", expanded);
                }

                return new VoiceRuntimeLocation(expanded, "", serviceFile, voiceDirectory);
            }

            string venvPython = Path.Combine(voiceDirectory, ".venv", "Scripts", "python.exe");
            if (File.Exists(venvPython))
            {
                return new VoiceRuntimeLocation(venvPython, "", serviceFile, voiceDirectory);
            }

            string pyLauncher = FindOnPath("py.exe");
            if (!String.IsNullOrEmpty(pyLauncher))
            {
                return new VoiceRuntimeLocation(pyLauncher, "-3.13", serviceFile, voiceDirectory);
            }

            string python = FindOnPath("python.exe");
            if (!String.IsNullOrEmpty(python))
            {
                return new VoiceRuntimeLocation(python, "", serviceFile, voiceDirectory);
            }

            throw new FileNotFoundException("未找到 Python 3.13。请先运行 voice\\setup.ps1。", "python.exe");
        }

        public static string FindVoiceDirectory()
        {
            List<string> candidates = new List<string>();
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string currentDirectory = Environment.CurrentDirectory;
            AddVoiceCandidates(candidates, baseDirectory);
            AddVoiceCandidates(candidates, currentDirectory);

            string fallback = "";
            for (int i = 0; i < candidates.Count; i++)
            {
                string candidate = Path.GetFullPath(candidates[i]);
                if (File.Exists(Path.Combine(candidate, "voice_service.py")))
                {
                    if (File.Exists(Path.Combine(candidate, ".venv", "Scripts", "python.exe")))
                    {
                        return candidate;
                    }

                    if (fallback.Length == 0)
                    {
                        fallback = candidate;
                    }
                }
            }

            return fallback;
        }

        private static void AddVoiceCandidates(List<string> candidates, string start)
        {
            if (String.IsNullOrWhiteSpace(start))
            {
                return;
            }

            DirectoryInfo directory;
            try
            {
                directory = new DirectoryInfo(Path.GetFullPath(start));
            }
            catch
            {
                return;
            }

            for (int depth = 0; directory != null && depth < 5; depth++)
            {
                candidates.Add(Path.Combine(directory.FullName, "voice"));
                directory = directory.Parent;
            }
        }

        private static string FindOnPath(string fileName)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            string[] directories = path.Split(Path.PathSeparator);
            for (int i = 0; i < directories.Length; i++)
            {
                string directory = directories[i].Trim().Trim('"');
                if (directory.Length == 0)
                {
                    continue;
                }

                try
                {
                    string candidate = Path.Combine(directory, fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            return "";
        }
    }

    internal sealed class VoiceAssistantController : IDisposable
    {
        private readonly Control owner;
        private readonly JavaScriptSerializer serializer;
        private readonly object processLock;
        private Process process;
        private Process ollamaProcess;
        private StreamWriter commandWriter;
        private VoiceAssistantSettings settings;
        private string lastErrorLine;
        private string lastServiceError;
        private bool stopping;
        private bool disposed;

        public event EventHandler<VoiceAssistantEventArgs> EventReceived;

        public VoiceAssistantController(Control owner)
        {
            this.owner = owner;
            serializer = new JavaScriptSerializer();
            processLock = new object();
            settings = VoiceSettingsStore.Load();
            StatusText = settings.Enabled ? "等待启动" : "已关闭";
        }

        public bool IsEnabled
        {
            get { return settings.Enabled; }
        }

        public bool IsRunning
        {
            get
            {
                lock (processLock)
                {
                    return process != null && !process.HasExited;
                }
            }
        }

        public bool IsAnimationActive
        {
            get { return IsEnabled; }
        }

        public string StatusText { get; private set; }

        public VoiceAssistantSettings Settings
        {
            get { return settings.Clone(); }
        }

        public void StartIfEnabled()
        {
            if (settings.Enabled)
            {
                Start();
            }
        }

        public void SetEnabled(bool enabled)
        {
            settings.Enabled = enabled;
            VoiceSettingsStore.Save(settings);
            if (enabled)
            {
                Start();
            }
            else
            {
                StopProcess();
                SetLocalStatus("已关闭", "stopped", "语音助手已关闭", false);
            }
        }

        public void ApplySettings(VoiceAssistantSettings newSettings)
        {
            if (newSettings == null)
            {
                return;
            }

            bool wasEnabled = settings.Enabled;
            newSettings.Enabled = wasEnabled;
            settings = newSettings.Clone();
            VoiceSettingsStore.Save(settings);
            settings = VoiceSettingsStore.Load();
            settings.Enabled = wasEnabled;
            if (wasEnabled)
            {
                Restart();
            }
        }

        public void Restart()
        {
            if (!settings.Enabled)
            {
                return;
            }

            StopProcess();
            Start();
        }

        public void StartCapture()
        {
            SendCommand("start_capture");
        }

        public void ClearHistory()
        {
            SendCommand("clear_history");
        }

        public void NotifySpeechStarted()
        {
            SendCommand("speech_started");
        }

        public void NotifySpeechDone()
        {
            SendCommand("speech_done");
        }

        public bool Speak(string text, int rate)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["text"] = text ?? "";
            payload["speed"] = Math.Max(0.7, Math.Min(1.4, 1.0 + (rate * 0.04)));
            return SendCommand("speak", payload);
        }

        public bool AskLocalConversation(string text, string requestId)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["text"] = text ?? "";
            payload["request_id"] = requestId ?? "";
            return SendCommand("ask_local", payload);
        }

        public string GetVoiceDirectory()
        {
            return VoiceRuntimeLocator.FindVoiceDirectory();
        }

        private void Start()
        {
            if (disposed || IsRunning)
            {
                return;
            }

            try
            {
                VoiceRuntimeLocation runtime = VoiceRuntimeLocator.Resolve(settings);
                VoiceSettingsStore.WriteRuntimeConfig(settings);
                if (!DesktopAgentSettingsStore.Load().Enabled || !DesktopAgentSettingsStore.HasPrimaryCredential())
                {
                    EnsureBundledOllamaStarted(runtime.VoiceDirectory);
                }
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = runtime.PythonFile;
                startInfo.Arguments = JoinArguments(
                    runtime.PythonArguments,
                    Quote(runtime.ServiceFile),
                    "--config",
                    Quote(VoiceSettingsStore.ConfigFile),
                    "--parent-pid",
                    Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                startInfo.WorkingDirectory = runtime.VoiceDirectory;
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardInput = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.StandardOutputEncoding = Encoding.UTF8;
                startInfo.StandardErrorEncoding = Encoding.UTF8;
                startInfo.EnvironmentVariables["PYTHONUTF8"] = "1";
                startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
                ConfigureLocalModelEnvironment(startInfo, runtime.VoiceDirectory);

                Process started = new Process();
                started.StartInfo = startInfo;
                started.EnableRaisingEvents = true;
                started.OutputDataReceived += OnOutputDataReceived;
                started.ErrorDataReceived += OnErrorDataReceived;
                started.Exited += OnProcessExited;
                lastErrorLine = "";
                lastServiceError = "";
                stopping = false;
                if (!started.Start())
                {
                    throw new InvalidOperationException("本地语音服务未能启动。");
                }

                lock (processLock)
                {
                    process = started;
                    commandWriter = new StreamWriter(
                        started.StandardInput.BaseStream,
                        new UTF8Encoding(false));
                    commandWriter.AutoFlush = true;
                }

                started.BeginOutputReadLine();
                started.BeginErrorReadLine();
                SetLocalStatus("正在启动", "loading", "正在启动本地语音服务", false);
            }
            catch (Exception ex)
            {
                StopBundledOllama();
                SetLocalStatus("启动失败", "error", ex.Message, true);
            }
        }

        private void StopProcess()
        {
            Process current;
            lock (processLock)
            {
                current = process;
                stopping = true;
            }

            if (current == null)
            {
                StopBundledOllama();
                return;
            }

            try
            {
                SendCommand("shutdown");
                if (!current.WaitForExit(1200))
                {
                    KillProcessTree(current);
                    current.WaitForExit(800);
                }
            }
            catch
            {
            }
            finally
            {
                lock (processLock)
                {
                    if (ReferenceEquals(process, current))
                    {
                        commandWriter = null;
                        process = null;
                    }
                }

                current.Dispose();
                StopBundledOllama();
            }
        }

        private void EnsureBundledOllamaStarted(string voiceDirectory)
        {
            string executable = Path.Combine(voiceDirectory, "ollama", "ollama.exe");
            if (!File.Exists(executable) || (ollamaProcess != null && !ollamaProcess.HasExited))
            {
                return;
            }

            string modelDirectory = Path.Combine(voiceDirectory, "models", "ollama");
            Directory.CreateDirectory(modelDirectory);
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = executable;
            startInfo.Arguments = "serve";
            startInfo.WorkingDirectory = Path.GetDirectoryName(executable);
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.EnvironmentVariables["OLLAMA_MODELS"] = modelDirectory;
            startInfo.EnvironmentVariables["OLLAMA_HOST"] = "127.0.0.1:11434";
            ollamaProcess = Process.Start(startInfo);
        }

        private static void ConfigureLocalModelEnvironment(ProcessStartInfo startInfo, string voiceDirectory)
        {
            string modelDirectory = Path.Combine(voiceDirectory, "models");
            string huggingFaceDirectory = Path.Combine(modelDirectory, "huggingface");
            string torchDirectory = Path.Combine(modelDirectory, "torch");
            string ollamaDirectory = Path.Combine(modelDirectory, "ollama");
            Directory.CreateDirectory(huggingFaceDirectory);
            Directory.CreateDirectory(torchDirectory);
            Directory.CreateDirectory(ollamaDirectory);
            startInfo.EnvironmentVariables["HF_HOME"] = huggingFaceDirectory;
            startInfo.EnvironmentVariables["TORCH_HOME"] = torchDirectory;
            startInfo.EnvironmentVariables["OLLAMA_MODELS"] = ollamaDirectory;
            startInfo.EnvironmentVariables["HF_HUB_OFFLINE"] = "1";
            startInfo.EnvironmentVariables["TRANSFORMERS_OFFLINE"] = "1";
            startInfo.EnvironmentVariables["TRANSFORMERS_VERBOSITY"] = "error";
            startInfo.EnvironmentVariables["TOKENIZERS_PARALLELISM"] = "false";
        }

        private void StopBundledOllama()
        {
            Process current = ollamaProcess;
            ollamaProcess = null;
            if (current == null)
            {
                return;
            }

            try
            {
                if (!current.HasExited)
                {
                    KillProcessTree(current);
                    current.WaitForExit(800);
                }
            }
            catch
            {
            }
            finally
            {
                current.Dispose();
            }
        }

        private static void KillProcessTree(Process root)
        {
            if (root == null)
            {
                return;
            }

            try
            {
                if (root.HasExited)
                {
                    return;
                }

                string taskKill = Path.Combine(Environment.SystemDirectory, "taskkill.exe");
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = taskKill;
                startInfo.Arguments = "/PID " + root.Id.ToString(CultureInfo.InvariantCulture) + " /T /F";
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                using (Process killer = Process.Start(startInfo))
                {
                    if (killer != null)
                    {
                        killer.WaitForExit(1500);
                    }
                }
            }
            catch
            {
                try
                {
                    if (!root.HasExited)
                    {
                        root.Kill();
                    }
                }
                catch
                {
                }
            }
        }

        private bool SendCommand(string type)
        {
            return SendCommand(type, null);
        }

        private bool SendCommand(string type, Dictionary<string, object> payload)
        {
            StreamWriter writer;
            lock (processLock)
            {
                writer = commandWriter;
                if (writer == null || process == null || process.HasExited)
                {
                    return false;
                }

                try
                {
                    Dictionary<string, object> command = new Dictionary<string, object>();
                    command["type"] = type;
                    if (payload != null)
                    {
                        foreach (KeyValuePair<string, object> item in payload)
                        {
                            command[item.Key] = item.Value;
                        }
                    }
                    writer.WriteLine(serializer.Serialize(command));
                    writer.Flush();
                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (String.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            try
            {
                Dictionary<string, object> payload = serializer.DeserializeObject(e.Data) as Dictionary<string, object>;
                if (payload == null)
                {
                    return;
                }

                VoiceAssistantEventArgs parsed = new VoiceAssistantEventArgs(
                    ReadString(payload, "type"),
                    ReadString(payload, "state"),
                    ReadString(payload, "message"),
                    ReadString(payload, "text"),
                    ReadString(payload, "phrase"),
                    ReadString(payload, "request_id"),
                    ReadBoolean(payload, "fatal"));
                if (String.Equals(parsed.Type, "state", StringComparison.OrdinalIgnoreCase))
                {
                    StatusText = String.IsNullOrWhiteSpace(parsed.Message) ? StateLabel(parsed.State) : parsed.Message;
                }
                else if (String.Equals(parsed.Type, "ready", StringComparison.OrdinalIgnoreCase))
                {
                    StatusText = "等待唤醒";
                }
                else if (String.Equals(parsed.Type, "error", StringComparison.OrdinalIgnoreCase))
                {
                    StatusText = parsed.Message;
                    if (parsed.Fatal)
                    {
                        lastServiceError = parsed.Message;
                    }
                }

                RaiseEvent(parsed);
            }
            catch (Exception ex)
            {
                lastErrorLine = "无法解析语音服务消息: " + ex.Message;
            }
        }

        private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!String.IsNullOrWhiteSpace(e.Data) && !IsBenignDiagnostic(e.Data))
            {
                lastErrorLine = e.Data.Trim();
            }
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            Process exited = sender as Process;
            bool expected;
            lock (processLock)
            {
                if (!ReferenceEquals(process, exited))
                {
                    return;
                }

                expected = stopping || !settings.Enabled || disposed;
                commandWriter = null;
                process = null;
            }

            if (!expected)
            {
                StopBundledOllama();
                int exitCode = -1;
                try
                {
                    exitCode = exited == null ? -1 : exited.ExitCode;
                }
                catch
                {
                }

                string reason = !String.IsNullOrWhiteSpace(lastServiceError)
                    ? lastServiceError
                    : lastErrorLine;
                string voiceDirectory = VoiceRuntimeLocator.FindVoiceDirectory();
                string logFile = String.IsNullOrWhiteSpace(voiceDirectory)
                    ? "voice\\cache\\voice-service.log"
                    : Path.Combine(voiceDirectory, "cache", "voice-service.log");
                string detail = String.IsNullOrWhiteSpace(reason)
                    ? "本地语音服务已意外退出，退出码 " + exitCode.ToString(CultureInfo.InvariantCulture) + "。"
                    : "本地语音服务已退出：" + reason;
                detail += " 诊断日志：" + logFile;
                SetLocalStatus("服务已退出", "error", detail, true);
            }
        }

        private static bool IsBenignDiagnostic(string message)
        {
            string value = message ?? "";
            return value.IndexOf("generation flags are not valid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("TRANSFORMERS_VERBOSITY", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("Setting `pad_token_id`", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("lexicon.cc:ConvertTokensToIds", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void SetLocalStatus(string status, string state, string message, bool fatal)
        {
            StatusText = status;
            RaiseEvent(new VoiceAssistantEventArgs(fatal ? "error" : "state", state, message, "", "", "", fatal));
        }

        private void RaiseEvent(VoiceAssistantEventArgs e)
        {
            if (disposed || owner == null || owner.IsDisposed)
            {
                return;
            }

            MethodInvoker callback = delegate
            {
                EventHandler<VoiceAssistantEventArgs> handler = EventReceived;
                if (!disposed && handler != null)
                {
                    handler(this, e);
                }
            };

            try
            {
                if (owner.IsHandleCreated && owner.InvokeRequired)
                {
                    owner.BeginInvoke(callback);
                }
                else
                {
                    callback();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static string ReadString(Dictionary<string, object> payload, string key)
        {
            object value;
            return payload.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }

        private static bool ReadBoolean(Dictionary<string, object> payload, string key)
        {
            object value;
            if (!payload.TryGetValue(key, out value) || value == null)
            {
                return false;
            }

            bool parsed;
            return value is bool ? (bool)value : Boolean.TryParse(value.ToString(), out parsed) && parsed;
        }

        private static string StateLabel(string state)
        {
            switch (state)
            {
                case "loading": return "正在加载";
                case "listening_wake": return "等待唤醒";
                case "listening_command": return "正在聆听";
                case "listening_followup": return "等待追问";
                case "transcribing": return "正在识别";
                case "thinking": return "正在思考";
                case "speaking": return "正在回答";
                case "stopped": return "已关闭";
                default: return state;
            }
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static string JoinArguments(params string[] values)
        {
            List<string> parts = new List<string>();
            for (int i = 0; i < values.Length; i++)
            {
                if (!String.IsNullOrWhiteSpace(values[i]))
                {
                    parts.Add(values[i]);
                }
            }

            return String.Join(" ", parts.ToArray());
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            StopProcess();
        }
    }

    internal sealed class LocalSpeechPlayer : IDisposable
    {
        private readonly SpeechSynthesizer synthesizer;
        private Action<Exception> completion;
        private bool disposed;

        public LocalSpeechPlayer()
        {
            synthesizer = new SpeechSynthesizer();
            synthesizer.SpeakCompleted += OnSpeakCompleted;
            TrySelectChineseVoice();
        }

        public void Speak(string text, int rate, Action<Exception> completed)
        {
            if (disposed)
            {
                if (completed != null)
                {
                    completed(new ObjectDisposedException("LocalSpeechPlayer"));
                }
                return;
            }

            synthesizer.SpeakAsyncCancelAll();
            completion = completed;
            synthesizer.Rate = Math.Max(-10, Math.Min(10, rate));
            try
            {
                synthesizer.SpeakAsync(text ?? "");
            }
            catch (Exception ex)
            {
                Complete(ex);
            }
        }

        private void TrySelectChineseVoice()
        {
            try
            {
                foreach (InstalledVoice voice in synthesizer.GetInstalledVoices())
                {
                    if (voice.Enabled && voice.VoiceInfo.Culture != null &&
                        String.Equals(voice.VoiceInfo.Culture.Name, "zh-CN", StringComparison.OrdinalIgnoreCase))
                    {
                        synthesizer.SelectVoice(voice.VoiceInfo.Name);
                        return;
                    }
                }
            }
            catch
            {
            }
        }

        private void OnSpeakCompleted(object sender, SpeakCompletedEventArgs e)
        {
            Complete(e.Error);
        }

        private void Complete(Exception error)
        {
            Action<Exception> callback = completion;
            completion = null;
            if (callback != null)
            {
                callback(error);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            synthesizer.SpeakCompleted -= OnSpeakCompleted;
            synthesizer.SpeakAsyncCancelAll();
            synthesizer.Dispose();
            completion = null;
        }
    }

    internal sealed class VoiceCaptionForm : Form
    {
        private const int WmMouseWheel = 0x020A;
        private const int CaptionScrollBarWidth = 8;
        private const int CaptionScrollBarGap = 3;
        private static readonly Color CaptionBackgroundColor = Color.FromArgb(242, 246, 248);
        private static readonly Color CaptionTextColor = Color.FromArgb(27, 39, 48);
        private static readonly Color CaptionErrorTextColor = Color.FromArgb(165, 44, 55);
        private static readonly Color CaptionScrollTrackColor = Color.FromArgb(202, 216, 222);
        private static readonly Color CaptionScrollThumbColor = Color.FromArgb(45, 130, 145);
        private static readonly Color CaptionScrollThumbHoverColor = Color.FromArgb(24, 111, 130);
        private static readonly Color CaptionScrollThumbActiveColor = Color.FromArgb(13, 90, 108);
        private readonly CaptionTextBox caption;
        private readonly CaptionScrollBar captionScrollBar;
        private readonly Timer hideTimer;
        private Form anchor;
        private int hideIntervalMs;

        public VoiceCaptionForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = CaptionBackgroundColor;
            ForeColor = CaptionTextColor;
            Padding = new Padding(14, 11, 14, 11);
            Font = new Font("Microsoft YaHei UI", 9.25F, FontStyle.Regular, GraphicsUnit.Point);
            MinimumSize = new Size(176, 48);
            MaximumSize = new Size(380, 190);

            caption = new CaptionTextBox();
            caption.BackColor = BackColor;
            caption.BorderStyle = BorderStyle.None;
            caption.Cursor = Cursors.IBeam;
            caption.DetectUrls = false;
            caption.ForeColor = ForeColor;
            caption.Font = Font;
            caption.HideSelection = false;
            caption.Location = new Point(Padding.Left, Padding.Top);
            caption.ReadOnly = true;
            caption.ScrollBars = RichTextBoxScrollBars.None;
            caption.ShortcutsEnabled = true;
            caption.WordWrap = true;
            Controls.Add(caption);

            captionScrollBar = new CaptionScrollBar(caption);
            captionScrollBar.BackColor = BackColor;
            caption.MouseWheelScrollRequested += captionScrollBar.ScrollByWheelDelta;
            Controls.Add(captionScrollBar);
            captionScrollBar.BringToFront();

            hideTimer = new Timer();
            hideTimer.Tick += delegate
            {
                hideTimer.Stop();
                Hide();
            };
            Click += delegate { Hide(); };
            caption.MouseEnter += delegate { hideTimer.Stop(); };
            caption.MouseLeave += delegate { RestartHideTimer(); };
            captionScrollBar.MouseEnter += delegate { hideTimer.Stop(); };
            captionScrollBar.MouseLeave += delegate { RestartHideTimer(); };
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WsExNoActivate = 0x08000000;
                const int WsExToolWindow = 0x00000080;
                CreateParams cp = base.CreateParams;
                cp.ExStyle &= ~WsExNoActivate;
                cp.ExStyle |= WsExToolWindow;
                return cp;
            }
        }

        public void ShowCaption(Form anchorForm, string text, int seconds, bool isError)
        {
            if (anchorForm == null || anchorForm.IsDisposed || String.IsNullOrWhiteSpace(text))
            {
                return;
            }

            anchor = anchorForm;
            caption.ForeColor = isError ? CaptionErrorTextColor : CaptionTextColor;
            caption.Text = text.Trim();
            caption.Select(0, 0);
            caption.ScrollToCaret();
            captionScrollBar.ResetWheelDelta();
            Size proposed = TextRenderer.MeasureText(
                caption.Text,
                caption.Font,
                new Size(341, 164),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            int width = Math.Max(176, Math.Min(380, proposed.Width + Padding.Horizontal + CaptionScrollBarWidth + CaptionScrollBarGap));
            int height = Math.Max(48, Math.Min(190, proposed.Height + Padding.Vertical + 4));
            Size = new Size(width, height);
            LayoutCaption();
            UpdateRegion();
            Reposition();
            if (!Visible)
            {
                Show(anchorForm);
            }
            else
            {
                BringToFront();
            }
            captionScrollBar.RefreshScrollState();

            int effectiveSeconds = Math.Max(seconds, Math.Min(45, Math.Max(3, caption.Text.Length / 4)));
            hideIntervalMs = effectiveSeconds * 1000;
            hideTimer.Stop();
            RestartHideTimer();
        }

        private void RestartHideTimer()
        {
            if (hideIntervalMs <= 0 || !Visible)
            {
                return;
            }

            hideTimer.Stop();
            hideTimer.Interval = hideIntervalMs;
            hideTimer.Start();
        }

        public void Reposition()
        {
            if (anchor == null || anchor.IsDisposed)
            {
                return;
            }

            Rectangle area = Screen.FromControl(anchor).WorkingArea;
            int x = anchor.Left + (anchor.Width - Width) / 2;
            int y = anchor.Top - Height - 8;
            if (y < area.Top + 8)
            {
                y = anchor.Bottom + 8;
            }
            x = Math.Max(area.Left + 8, Math.Min(area.Right - Width - 8, x));
            y = Math.Max(area.Top + 8, Math.Min(area.Bottom - Height - 8, y));
            Location = new Point(x, y);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            LayoutCaption();
            UpdateRegion();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmMouseWheel && captionScrollBar != null)
            {
                captionScrollBar.ScrollByWheelDelta(GetMouseWheelDelta(m.WParam));
                return;
            }

            base.WndProc(ref m);
        }

        private void LayoutCaption()
        {
            if (caption == null || captionScrollBar == null)
            {
                return;
            }

            int contentHeight = Math.Max(1, ClientSize.Height - Padding.Vertical);
            int contentWidth = Math.Max(1, ClientSize.Width - Padding.Horizontal - CaptionScrollBarWidth - CaptionScrollBarGap);
            caption.Bounds = new Rectangle(Padding.Left, Padding.Top, contentWidth, contentHeight);
            captionScrollBar.Bounds = new Rectangle(
                caption.Right + CaptionScrollBarGap,
                Padding.Top,
                CaptionScrollBarWidth,
                contentHeight);
            captionScrollBar.BringToFront();
            captionScrollBar.RefreshScrollState();
        }

        private void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0)
            {
                return;
            }

            using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, Width, Height), 8))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null)
                {
                    old.Dispose();
                }
            }
        }

        private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static int GetMouseWheelDelta(IntPtr wParam)
        {
            return unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
        }

        private sealed class CaptionTextBox : RichTextBox
        {
            public event Action<int> MouseWheelScrollRequested;

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WmMouseWheel)
                {
                    Action<int> handler = MouseWheelScrollRequested;
                    if (handler != null)
                    {
                        handler(GetMouseWheelDelta(m.WParam));
                        return;
                    }
                }

                base.WndProc(ref m);
            }
        }

        private sealed class CaptionScrollBar : Control
        {
            private const int EmLineScroll = 0x00B6;
            private const int EmGetScrollPos = 0x04DD;
            private const int EmSetScrollPos = 0x04DE;
            private const int MinimumThumbHeight = 24;
            private readonly CaptionTextBox target;
            private Rectangle thumbBounds;
            private int currentScrollY;
            private int maximumScrollY;
            private int wheelDeltaRemainder;
            private bool dragging;
            private bool pointerOverThumb;
            private int dragOffsetY;

            public CaptionScrollBar(CaptionTextBox targetControl)
            {
                target = targetControl;
                SetStyle(
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.ResizeRedraw |
                    ControlStyles.UserPaint,
                    true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;

                target.TextChanged += OnTargetChanged;
                target.Resize += OnTargetChanged;
                target.VScroll += OnTargetChanged;
            }

            public void ResetWheelDelta()
            {
                wheelDeltaRemainder = 0;
            }

            public void ScrollByWheelDelta(int delta)
            {
                if (delta == 0 || !CalculateScrollMetrics())
                {
                    return;
                }

                wheelDeltaRemainder += delta;
                int steps = wheelDeltaRemainder / SystemInformation.MouseWheelScrollDelta;
                if (steps == 0)
                {
                    return;
                }

                wheelDeltaRemainder -= steps * SystemInformation.MouseWheelScrollDelta;
                int configuredLines = SystemInformation.MouseWheelScrollLines;
                if (configuredLines < 0)
                {
                    SetScrollPosition(currentScrollY - (steps * target.ClientSize.Height));
                }
                else if (configuredLines > 0)
                {
                    ScrollByLines(-steps * configuredLines);
                }
            }

            public void RefreshScrollState()
            {
                if (!CalculateScrollMetrics())
                {
                    Visible = false;
                    return;
                }

                Visible = true;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.Clear(BackColor);
                if (!CalculateScrollMetrics())
                {
                    return;
                }

                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle trackBounds = new Rectangle((Width - 2) / 2, 2, 2, Math.Max(1, Height - 4));
                using (SolidBrush trackBrush = new SolidBrush(CaptionScrollTrackColor))
                using (GraphicsPath trackPath = RoundedPath(trackBounds, 1))
                {
                    e.Graphics.FillPath(trackBrush, trackPath);
                }

                Color thumbColor = dragging
                    ? CaptionScrollThumbActiveColor
                    : pointerOverThumb
                        ? CaptionScrollThumbHoverColor
                        : CaptionScrollThumbColor;
                using (SolidBrush thumbBrush = new SolidBrush(thumbColor))
                using (GraphicsPath thumbPath = RoundedPath(thumbBounds, Math.Max(1, thumbBounds.Width / 2)))
                {
                    e.Graphics.FillPath(thumbBrush, thumbPath);
                }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || !CalculateScrollMetrics())
                {
                    return;
                }

                if (thumbBounds.Contains(e.Location))
                {
                    dragging = true;
                    dragOffsetY = e.Y - thumbBounds.Top;
                    Capture = true;
                    Invalidate();
                    return;
                }

                int page = Math.Max(1, target.ClientSize.Height - target.Font.Height);
                SetScrollPosition(e.Y < thumbBounds.Top ? currentScrollY - page : currentScrollY + page);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (dragging)
                {
                    int travel = Math.Max(1, Height - 4 - thumbBounds.Height);
                    int requestedTop = Math.Max(2, Math.Min(2 + travel, e.Y - dragOffsetY));
                    double ratio = (requestedTop - 2) / (double)travel;
                    SetScrollPosition((int)Math.Round(ratio * maximumScrollY));
                    return;
                }

                bool overThumb = thumbBounds.Contains(e.Location);
                if (overThumb != pointerOverThumb)
                {
                    pointerOverThumb = overThumb;
                    Invalidate();
                }
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (!dragging && pointerOverThumb)
                {
                    pointerOverThumb = false;
                    Invalidate();
                }
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (!dragging)
                {
                    return;
                }

                dragging = false;
                Capture = false;
                pointerOverThumb = thumbBounds.Contains(e.Location);
                Invalidate();
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WmMouseWheel)
                {
                    ScrollByWheelDelta(GetMouseWheelDelta(m.WParam));
                    return;
                }

                base.WndProc(ref m);
            }

            private void OnTargetChanged(object sender, EventArgs e)
            {
                RefreshScrollState();
            }

            private bool CalculateScrollMetrics()
            {
                if (target == null || target.IsDisposed || !target.IsHandleCreated || target.TextLength == 0 || Height <= 4)
                {
                    thumbBounds = Rectangle.Empty;
                    currentScrollY = 0;
                    maximumScrollY = 0;
                    return false;
                }

                NativePoint scrollPosition = GetScrollPosition();
                Point lastCharacter = target.GetPositionFromCharIndex(target.TextLength - 1);
                int contentHeight = scrollPosition.Y + lastCharacter.Y + target.Font.Height;
                maximumScrollY = Math.Max(0, contentHeight - target.ClientSize.Height + 2);
                currentScrollY = Math.Max(0, Math.Min(maximumScrollY, scrollPosition.Y));
                if (maximumScrollY <= 2)
                {
                    thumbBounds = Rectangle.Empty;
                    return false;
                }

                int trackHeight = Height - 4;
                int fullContentHeight = target.ClientSize.Height + maximumScrollY;
                int thumbHeight = Math.Max(
                    Math.Min(MinimumThumbHeight, trackHeight),
                    (int)Math.Round(trackHeight * (target.ClientSize.Height / (double)fullContentHeight)));
                thumbHeight = Math.Min(trackHeight, thumbHeight);
                int travel = Math.Max(0, trackHeight - thumbHeight);
                int thumbTop = 2 + (maximumScrollY == 0
                    ? 0
                    : (int)Math.Round(travel * (currentScrollY / (double)maximumScrollY)));
                thumbBounds = new Rectangle(1, thumbTop, Math.Max(1, Width - 2), thumbHeight);
                return true;
            }

            private NativePoint GetScrollPosition()
            {
                NativePoint point = new NativePoint();
                SendMessagePoint(target.Handle, EmGetScrollPos, IntPtr.Zero, ref point);
                return point;
            }

            private void SetScrollPosition(int value)
            {
                NativePoint point = GetScrollPosition();
                point.Y = Math.Max(0, Math.Min(maximumScrollY, value));
                SendMessagePoint(target.Handle, EmSetScrollPos, IntPtr.Zero, ref point);
                target.Invalidate();
                RefreshScrollState();
            }

            private void ScrollByLines(int lineCount)
            {
                if (lineCount == 0)
                {
                    return;
                }

                SendMessage(target.Handle, EmLineScroll, IntPtr.Zero, new IntPtr(lineCount));
                target.Invalidate();
                RefreshScrollState();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && target != null)
                {
                    target.TextChanged -= OnTargetChanged;
                    target.Resize -= OnTargetChanged;
                    target.VScroll -= OnTargetChanged;
                    target.MouseWheelScrollRequested -= ScrollByWheelDelta;
                }

                base.Dispose(disposing);
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct NativePoint
            {
                public int X;
                public int Y;
            }

            [DllImport("user32.dll", EntryPoint = "SendMessage", CharSet = CharSet.Auto)]
            private static extern IntPtr SendMessage(
                IntPtr window,
                int message,
                IntPtr wParam,
                IntPtr lParam);

            [DllImport("user32.dll", EntryPoint = "SendMessage", CharSet = CharSet.Auto)]
            private static extern IntPtr SendMessagePoint(
                IntPtr window,
                int message,
                IntPtr wParam,
                ref NativePoint lParam);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                hideTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class VoiceSettingsDialog : Form
    {
        private readonly TextBox pythonPath;
        private readonly TextBox chatModel;
        private readonly TextBox ollamaUrl;
        private readonly NumericUpDown followUpSeconds;
        private readonly ComboBox ttsVoice;
        private readonly NumericUpDown ttsRate;
        private readonly NumericUpDown captionSeconds;
        private readonly CheckBox commandRoutingEnabled;
        private readonly CheckBox commandCaptionEnabled;

        private VoiceSettingsDialog(VoiceAssistantSettings settings)
        {
            Text = "语音助手设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 480);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(245, 247, 249);

            Label privacy = new Label();
            privacy.Text = "本地模式：音频仅在内存中处理，不保存、不上传。唤醒词：nono、诺诺、你好 nono";
            privacy.AutoSize = false;
            privacy.Location = new Point(20, 18);
            privacy.Size = new Size(480, 42);
            privacy.ForeColor = Color.FromArgb(58, 70, 82);
            Controls.Add(privacy);

            pythonPath = AddTextField("Python（留空自动查找）", settings.PythonPath, 72);
            chatModel = AddTextField("Ollama 对话模型", settings.ChatModel, 124);
            ollamaUrl = AddTextField("Ollama API 地址", settings.OllamaUrl, 176);
            followUpSeconds = AddNumberField("连续追问窗口（秒）", settings.FollowUpSeconds, 5, 120, 228);
            ttsVoice = AddVoiceField("朗读音色", settings.TtsVoice, 264);
            ttsRate = AddNumberField("朗读速度（-10 到 10）", settings.TtsRate, -10, 10, 300);
            captionSeconds = AddNumberField("字幕最短显示（秒）", settings.CaptionSeconds, 3, 60, 336);

            commandRoutingEnabled = new CheckBox();
            commandRoutingEnabled.Text = "语音指令联动";
            commandRoutingEnabled.Checked = settings.CommandRoutingEnabled;
            commandRoutingEnabled.Location = new Point(20, 374);
            commandRoutingEnabled.Size = new Size(180, 26);
            Controls.Add(commandRoutingEnabled);

            commandCaptionEnabled = new CheckBox();
            commandCaptionEnabled.Text = "执行前显示识别到的指令";
            commandCaptionEnabled.Checked = settings.CommandCaptionEnabled;
            commandCaptionEnabled.Location = new Point(202, 374);
            commandCaptionEnabled.Size = new Size(260, 26);
            Controls.Add(commandCaptionEnabled);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Size = new Size(84, 30);
            cancel.Location = new Point(416, 432);
            Controls.Add(cancel);

            Button save = new Button();
            save.Text = "保存";
            save.DialogResult = DialogResult.OK;
            save.Size = new Size(84, 30);
            save.Location = new Point(322, 432);
            Controls.Add(save);
            AcceptButton = save;
            CancelButton = cancel;
        }

        public static bool Edit(IWin32Window owner, VoiceAssistantSettings current, out VoiceAssistantSettings result)
        {
            using (VoiceSettingsDialog dialog = new VoiceSettingsDialog(current))
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK)
                {
                    result = current;
                    return false;
                }

                result = current.Clone();
                result.PythonPath = dialog.pythonPath.Text.Trim();
                result.ChatModel = dialog.chatModel.Text.Trim();
                result.OllamaUrl = dialog.ollamaUrl.Text.Trim();
                result.FollowUpSeconds = Decimal.ToInt32(dialog.followUpSeconds.Value);
                TtsVoiceOption selectedVoice = dialog.ttsVoice.SelectedItem as TtsVoiceOption;
                result.TtsVoice = selectedVoice == null ? current.TtsVoice : selectedVoice.Id;
                result.TtsRate = Decimal.ToInt32(dialog.ttsRate.Value);
                result.CaptionSeconds = Decimal.ToInt32(dialog.captionSeconds.Value);
                result.CommandRoutingEnabled = dialog.commandRoutingEnabled.Checked;
                result.CommandCaptionEnabled = dialog.commandCaptionEnabled.Checked;
                return true;
            }
        }

        private TextBox AddTextField(string label, string value, int top)
        {
            Label fieldLabel = new Label();
            fieldLabel.Text = label;
            fieldLabel.Location = new Point(20, top);
            fieldLabel.Size = new Size(176, 24);
            fieldLabel.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(fieldLabel);

            TextBox box = new TextBox();
            box.Text = value ?? "";
            box.Location = new Point(202, top);
            box.Size = new Size(298, 26);
            Controls.Add(box);
            return box;
        }

        private ComboBox AddVoiceField(string label, int selectedId, int top)
        {
            Label fieldLabel = new Label();
            fieldLabel.Text = label;
            fieldLabel.Location = new Point(20, top);
            fieldLabel.Size = new Size(176, 24);
            fieldLabel.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(fieldLabel);

            ComboBox input = new ComboBox();
            input.DropDownStyle = ComboBoxStyle.DropDownList;
            input.IntegralHeight = false;
            input.DropDownHeight = 300;
            input.Location = new Point(202, top);
            input.Size = new Size(298, 26);
            foreach (TtsVoiceOption option in VoiceSettingsStore.GetTtsVoiceOptions())
            {
                input.Items.Add(option);
                if (option.Id == selectedId)
                {
                    input.SelectedItem = option;
                }
            }
            if (input.SelectedIndex < 0 && input.Items.Count > 0)
            {
                input.SelectedIndex = 0;
            }
            Controls.Add(input);
            return input;
        }

        private NumericUpDown AddNumberField(string label, int value, int minimum, int maximum, int top)
        {
            Label fieldLabel = new Label();
            fieldLabel.Text = label;
            fieldLabel.Location = new Point(20, top);
            fieldLabel.Size = new Size(176, 24);
            fieldLabel.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(fieldLabel);

            NumericUpDown input = new NumericUpDown();
            input.Minimum = minimum;
            input.Maximum = maximum;
            input.Value = Math.Max(minimum, Math.Min(maximum, value));
            input.Location = new Point(202, top);
            input.Size = new Size(94, 26);
            Controls.Add(input);
            return input;
        }
    }
}
