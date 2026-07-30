using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NoNoStandalone
{
    internal sealed class CodexComputerBridge : IDisposable
    {
        private const int RequestTimeoutMilliseconds = 30000;
        private const int TurnTimeoutMilliseconds = 180000;
        private const string BaseInstructions =
            "你是诺诺桌面宠物的电脑任务代理。只能使用宿主提供的 computer_* 动态工具操作电脑。" +
            "禁止支付、付款、购买、下单、转账等资金操作；禁止删除文件或把文件移入回收站。" +
            "禁止运行 shell、PowerShell、CMD 或脚本，禁止调用内置文件补丁；禁止使用鼠标坐标、模拟点击、键盘注入或屏幕 GUI 自动化。" +
            "不要调用 Codex 内置的命令执行、文件修改、网页搜索、MCP、技能或插件。" +
            "应用、窗口、媒体、浏览器、剪贴板、系统、文件和进程操作必须通过 computer_* 工具完成。" +
            "浏览器只能直接打开网址或搜索结果，不得填写或提交网页表单。文件写入只能使用宿主提供的限定目录工具。" +
            "工具返回失败时说明失败原因，不得绕过限制。否定句、疑问句、条件句、举例和转述不是执行授权。" +
            "涉及文件或剪贴板内容、关闭程序、写入、移动、重命名或电源状态时，宿主会独立审批。" +
            "完成后用简洁中文说明实际完成的操作和未完成部分，不要声称未验证的结果。";

        private readonly Func<CodexComputerToolCall, CancellationToken, Task<CodexComputerToolResult>> toolHandler;
        private readonly Action<string, string> statusHandler;
        private readonly SemaphoreSlim lifecycleGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim taskGate = new SemaphoreSlim(1, 1);
        private readonly object writeGate = new object();
        private readonly object stateGate = new object();
        private readonly Dictionary<string, TaskCompletionSource<Dictionary<string, object>>> pendingRequests =
            new Dictionary<string, TaskCompletionSource<Dictionary<string, object>>>(StringComparer.Ordinal);

        private Process process;
        private StreamWriter input;
        private Task readerTask;
        private Task errorReaderTask;
        private int nextRequestId;
        private string threadId;
        private string currentTurnId;
        private string currentGoal;
        private bool currentSpeak;
        private string currentFinalMessage;
        private string currentError;
        private string lastStandardError;
        private CancellationToken currentTurnToken;
        private TaskCompletionSource<CodexComputerTaskResult> currentTurnCompletion;
        private bool disposed;

        public CodexComputerBridge(
            Func<CodexComputerToolCall, CancellationToken, Task<CodexComputerToolResult>> toolHandler,
            Action<string, string> statusHandler)
        {
            if (toolHandler == null) throw new ArgumentNullException("toolHandler");
            this.toolHandler = toolHandler;
            this.statusHandler = statusHandler;
        }

        public bool IsRunning
        {
            get
            {
                lock (stateGate)
                {
                    return process != null && !process.HasExited && !String.IsNullOrWhiteSpace(threadId);
                }
            }
        }

        public async Task<CodexComputerTaskResult> RunTaskAsync(
            string goal,
            bool speak,
            CancellationToken cancellationToken)
        {
            string cleanGoal = (goal ?? "").Trim();
            if (cleanGoal.Length == 0)
            {
                throw new InvalidOperationException("Codex 电脑任务不能为空。");
            }

            await taskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                TaskCompletionSource<CodexComputerTaskResult> completion = new TaskCompletionSource<CodexComputerTaskResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                lock (stateGate)
                {
                    currentGoal = cleanGoal;
                    currentSpeak = speak;
                    currentFinalMessage = "";
                    currentError = "";
                    currentTurnId = "";
                    currentTurnToken = cancellationToken;
                    currentTurnCompletion = completion;
                }

                ReportStatus("planning", "Codex 正在理解电脑任务");
                Dictionary<string, object> textInput = new Dictionary<string, object>();
                textInput["type"] = "text";
                textInput["text"] =
                    "用户当前电脑任务：" + cleanGoal + "\n" +
                    "只允许使用 computer_* 动态工具。先查询真实状态，再执行必要操作，最后依据工具结果回答。";
                Dictionary<string, object> parameters = new Dictionary<string, object>();
                parameters["threadId"] = GetThreadId();
                parameters["input"] = new object[] { textInput };
                Dictionary<string, object> response = await SendRequestAsync(
                    "turn/start",
                    parameters,
                    RequestTimeoutMilliseconds,
                    cancellationToken).ConfigureAwait(false);
                Dictionary<string, object> result = ReadDictionary(response, "result");
                Dictionary<string, object> turn = ReadDictionary(result, "turn");
                string startedTurnId = ReadString(turn, "id");
                if (startedTurnId.Length == 0)
                {
                    throw new InvalidOperationException("Codex 没有返回 turn ID。");
                }

                lock (stateGate)
                {
                    currentTurnId = startedTurnId;
                }

                using (cancellationToken.Register(delegate { Observe(InterruptCurrentTurnAsync()); }))
                {
                    try
                    {
                        return await AwaitWithTimeoutAsync(
                            completion.Task,
                            TurnTimeoutMilliseconds,
                            cancellationToken,
                            "Codex 电脑任务执行超时。").ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        await InterruptCurrentTurnAsync().ConfigureAwait(false);
                        throw;
                    }
                }
            }
            finally
            {
                lock (stateGate)
                {
                    currentGoal = "";
                    currentSpeak = false;
                    currentTurnId = "";
                    currentFinalMessage = "";
                    currentError = "";
                    currentTurnCompletion = null;
                    currentTurnToken = CancellationToken.None;
                }

                taskGate.Release();
            }
        }

        public async Task InterruptCurrentTurnAsync()
        {
            string activeThread;
            string activeTurn;
            lock (stateGate)
            {
                activeThread = threadId;
                activeTurn = currentTurnId;
            }

            if (activeThread.Length == 0 || activeTurn.Length == 0)
            {
                return;
            }

            try
            {
                Dictionary<string, object> parameters = new Dictionary<string, object>();
                parameters["threadId"] = activeThread;
                parameters["turnId"] = activeTurn;
                await SendRequestAsync("turn/interrupt", parameters, 5000, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task EnsureStartedAsync(CancellationToken cancellationToken)
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                lock (stateGate)
                {
                    if (process != null && !process.HasExited && !String.IsNullOrWhiteSpace(threadId))
                    {
                        return;
                    }
                }

                StopProcess();
                ReportStatus("planning", "正在连接本机 Codex");
                ProcessStartInfo startInfo = new ProcessStartInfo("cmd.exe");
                startInfo.Arguments = "/d /s /c \"codex app-server --stdio\"";
                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardInput = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.CreateNoWindow = true;
                startInfo.StandardOutputEncoding = new UTF8Encoding(false);
                startInfo.StandardErrorEncoding = new UTF8Encoding(false);

                Process started = new Process();
                started.StartInfo = startInfo;
                started.EnableRaisingEvents = true;
                if (!started.Start())
                {
                    throw new InvalidOperationException("无法启动 Codex CLI。");
                }

                lock (stateGate)
                {
                    process = started;
                    input = new StreamWriter(started.StandardInput.BaseStream, new UTF8Encoding(false));
                    input.AutoFlush = true;
                    threadId = "";
                    lastStandardError = "";
                }

                readerTask = Task.Run(ReadLoopAsync);
                errorReaderTask = Task.Run(ReadErrorLoopAsync);

                Dictionary<string, object> clientInfo = new Dictionary<string, object>();
                clientInfo["name"] = "nono";
                clientInfo["title"] = "NoNo Computer Assistant";
                clientInfo["version"] = "1.0";
                Dictionary<string, object> capabilities = new Dictionary<string, object>();
                capabilities["experimentalApi"] = true;
                Dictionary<string, object> initialize = new Dictionary<string, object>();
                initialize["clientInfo"] = clientInfo;
                initialize["capabilities"] = capabilities;
                await SendRequestAsync("initialize", initialize, RequestTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);

                string workingDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
                Dictionary<string, object> start = new Dictionary<string, object>();
                start["cwd"] = workingDirectory;
                start["runtimeWorkspaceRoots"] = new string[] { workingDirectory };
                start["ephemeral"] = true;
                start["sandbox"] = "read-only";
                start["approvalPolicy"] = "never";
                start["approvalsReviewer"] = "user";
                start["baseInstructions"] = BaseInstructions;
                start["dynamicTools"] = CodexComputerTools.BuildDynamicToolSpecs();
                Dictionary<string, object> response = await SendRequestAsync(
                    "thread/start",
                    start,
                    RequestTimeoutMilliseconds,
                    cancellationToken).ConfigureAwait(false);
                Dictionary<string, object> result = ReadDictionary(response, "result");
                Dictionary<string, object> thread = ReadDictionary(result, "thread");
                string newThreadId = ReadString(thread, "id");
                if (newThreadId.Length == 0)
                {
                    throw new InvalidOperationException("Codex App Server 没有返回 thread ID。");
                }

                lock (stateGate)
                {
                    threadId = newThreadId;
                }
            }
            catch (Exception ex)
            {
                string stderr;
                lock (stateGate) { stderr = lastStandardError; }
                StopProcess();
                if (!String.IsNullOrWhiteSpace(stderr))
                {
                    throw new InvalidOperationException("Codex 启动失败：" + Shorten(stderr, 300), ex);
                }

                throw new InvalidOperationException(
                    "Codex 启动失败。请确认已经安装并登录 Codex CLI。" +
                    (String.IsNullOrWhiteSpace(ex.Message) ? "" : " " + ex.Message),
                    ex);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        private async Task<Dictionary<string, object>> SendRequestAsync(
            string method,
            Dictionary<string, object> parameters,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            int id = Interlocked.Increment(ref nextRequestId);
            string key = id.ToString(CultureInfo.InvariantCulture);
            TaskCompletionSource<Dictionary<string, object>> completion =
                new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (stateGate)
            {
                pendingRequests[key] = completion;
            }

            Dictionary<string, object> request = new Dictionary<string, object>();
            request["id"] = id;
            request["method"] = method;
            request["params"] = parameters;
            try
            {
                WriteJson(request);
                Dictionary<string, object> response = await AwaitWithTimeoutAsync(
                    completion.Task,
                    timeoutMilliseconds,
                    cancellationToken,
                    "Codex 请求超时：" + method).ConfigureAwait(false);
                Dictionary<string, object> error = ReadOptionalDictionary(response, "error");
                if (error != null)
                {
                    throw new InvalidOperationException(DefaultMessage(ReadString(error, "message"), "Codex 请求失败：" + method));
                }

                return response;
            }
            finally
            {
                lock (stateGate)
                {
                    pendingRequests.Remove(key);
                }
            }
        }

        private void WriteJson(Dictionary<string, object> message)
        {
            string json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(message);
            lock (writeGate)
            {
                StreamWriter writer;
                Process activeProcess;
                lock (stateGate)
                {
                    writer = input;
                    activeProcess = process;
                }

                if (writer == null || activeProcess == null || activeProcess.HasExited)
                {
                    throw new InvalidOperationException("Codex App Server 未运行。");
                }

                writer.WriteLine(json);
                writer.Flush();
            }
        }

        private async Task ReadLoopAsync()
        {
            Exception failure = null;
            try
            {
                Process active;
                lock (stateGate) { active = process; }
                while (active != null)
                {
                    string line = await active.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                    ProcessMessage(line);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                FailPending(failure ?? new InvalidOperationException("Codex App Server 已退出。"));
            }
        }

        private async Task ReadErrorLoopAsync()
        {
            try
            {
                Process active;
                lock (stateGate) { active = process; }
                while (active != null)
                {
                    string line = await active.StandardError.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    if (String.IsNullOrWhiteSpace(line)) continue;
                    lock (stateGate)
                    {
                        lastStandardError = Shorten(line.Trim(), 600);
                    }
                }
            }
            catch
            {
            }
        }

        private void ProcessMessage(string line)
        {
            Dictionary<string, object> message;
            try
            {
                message = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(line) as Dictionary<string, object>;
            }
            catch
            {
                return;
            }

            if (message == null) return;
            string method = ReadString(message, "method");
            object idValue;
            bool hasId = message.TryGetValue("id", out idValue) && idValue != null;
            if (method.Length == 0 && hasId)
            {
                string id = Convert.ToString(idValue, CultureInfo.InvariantCulture);
                TaskCompletionSource<Dictionary<string, object>> completion = null;
                lock (stateGate)
                {
                    pendingRequests.TryGetValue(id, out completion);
                }

                if (completion != null)
                {
                    completion.TrySetResult(message);
                }

                return;
            }

            if (hasId && method.Length > 0)
            {
                Observe(HandleServerRequestAsync(message, method, idValue));
                return;
            }

            HandleNotification(method, ReadOptionalDictionary(message, "params"));
        }

        private async Task HandleServerRequestAsync(Dictionary<string, object> message, string method, object id)
        {
            if (String.Equals(method, "item/tool/call", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDynamicToolCallAsync(id, ReadDictionary(message, "params")).ConfigureAwait(false);
                return;
            }

            if (String.Equals(method, "item/permissions/requestApproval", StringComparison.OrdinalIgnoreCase))
            {
                WriteResponse(id, null, "诺诺不授予 Codex 额外系统权限。");
                return;
            }

            if (method.IndexOf("requestApproval", StringComparison.OrdinalIgnoreCase) >= 0 ||
                String.Equals(method, "applyPatchApproval", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(method, "execCommandApproval", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> denied = new Dictionary<string, object>();
                denied["decision"] = "decline";
                WriteResponse(id, denied, null);
                return;
            }

            if (String.Equals(method, "item/tool/requestUserInput", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> result = new Dictionary<string, object>();
                result["answers"] = new Dictionary<string, object>();
                WriteResponse(id, result, null);
                return;
            }

            WriteResponse(id, null, "诺诺不允许 Codex 调用该宿主能力：" + method);
        }

        private async Task HandleDynamicToolCallAsync(object id, Dictionary<string, object> parameters)
        {
            CodexComputerToolResult result;
            try
            {
                CodexComputerToolCall call = new CodexComputerToolCall();
                call.Namespace = ReadString(parameters, "namespace");
                call.Tool = ReadString(parameters, "tool");
                call.Arguments = ReadOptionalDictionary(parameters, "arguments") ?? new Dictionary<string, object>();
                lock (stateGate)
                {
                    call.Goal = currentGoal;
                    call.Speak = currentSpeak;
                }

                result = await toolHandler(call, currentTurnToken).ConfigureAwait(false);
                if (result == null)
                {
                    result = CodexComputerToolResult.Fail("宠物电脑工具没有返回结果。");
                }
            }
            catch (OperationCanceledException)
            {
                result = CodexComputerToolResult.Fail("电脑任务已取消。");
            }
            catch (Exception ex)
            {
                result = CodexComputerToolResult.Fail(ex.Message);
            }

            Dictionary<string, object> content = new Dictionary<string, object>();
            content["type"] = "inputText";
            content["text"] = DefaultMessage(result.Message, result.Success ? "操作已完成。" : "操作失败。");
            Dictionary<string, object> response = new Dictionary<string, object>();
            response["success"] = result.Success;
            response["contentItems"] = new object[] { content };
            WriteResponse(id, response, null);
        }

        private void WriteResponse(object id, Dictionary<string, object> result, string errorMessage)
        {
            Dictionary<string, object> response = new Dictionary<string, object>();
            response["id"] = id;
            if (errorMessage == null)
            {
                response["result"] = result;
            }
            else
            {
                Dictionary<string, object> error = new Dictionary<string, object>();
                error["code"] = -32601;
                error["message"] = errorMessage;
                response["error"] = error;
            }

            try { WriteJson(response); }
            catch { }
        }

        private void HandleNotification(string method, Dictionary<string, object> parameters)
        {
            if (String.IsNullOrWhiteSpace(method) || parameters == null) return;
            if (String.Equals(method, "item/started", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> item = ReadOptionalDictionary(parameters, "item");
                string type = ReadString(item, "type");
                if (String.Equals(type, "dynamicToolCall", StringComparison.OrdinalIgnoreCase))
                {
                    ReportStatus("acting", "Codex 正在调用电脑工具");
                }
                else if (String.Equals(type, "commandExecution", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(type, "fileChange", StringComparison.OrdinalIgnoreCase))
                {
                    lock (stateGate)
                    {
                        currentError = "Codex 尝试使用未授权的命令或文件修改能力，任务已停止。";
                    }
                    Observe(InterruptCurrentTurnAsync());
                }
                else if (String.Equals(type, "reasoning", StringComparison.OrdinalIgnoreCase))
                {
                    ReportStatus("planning", "Codex 正在规划电脑操作");
                }

                return;
            }

            if (String.Equals(method, "item/completed", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> item = ReadOptionalDictionary(parameters, "item");
                if (String.Equals(ReadString(item, "type"), "agentMessage", StringComparison.OrdinalIgnoreCase))
                {
                    string text = ReadString(item, "text");
                    string phase = ReadString(item, "phase");
                    if (text.Length > 0 && (phase.Length == 0 || String.Equals(phase, "final_answer", StringComparison.OrdinalIgnoreCase)))
                    {
                        lock (stateGate) { currentFinalMessage = text; }
                    }
                }

                return;
            }

            if (String.Equals(method, "error", StringComparison.OrdinalIgnoreCase))
            {
                string error = FirstNonEmpty(ReadString(parameters, "message"), ReadString(parameters, "error"));
                if (error.Length > 0)
                {
                    lock (stateGate) { currentError = error; }
                }

                return;
            }

            if (String.Equals(method, "turn/completed", StringComparison.OrdinalIgnoreCase))
            {
                CompleteCurrentTurn(ReadOptionalDictionary(parameters, "turn"));
            }
        }

        private void CompleteCurrentTurn(Dictionary<string, object> turn)
        {
            TaskCompletionSource<CodexComputerTaskResult> completion;
            string finalMessage;
            string error;
            string activeThread;
            string activeTurn;
            lock (stateGate)
            {
                completion = currentTurnCompletion;
                finalMessage = currentFinalMessage;
                error = currentError;
                activeThread = threadId;
                activeTurn = currentTurnId;
            }

            if (completion == null) return;
            string status = ReadString(turn, "status");
            Dictionary<string, object> turnError = ReadOptionalDictionary(turn, "error");
            if (turnError != null)
            {
                error = FirstNonEmpty(ReadString(turnError, "message"), error);
            }

            bool cancelled = String.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase) || currentTurnToken.IsCancellationRequested;
            bool success = !cancelled && String.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) && String.IsNullOrWhiteSpace(error);
            CodexComputerTaskResult result = new CodexComputerTaskResult();
            result.Success = success;
            result.Cancelled = cancelled;
            result.ThreadId = activeThread;
            result.TurnId = activeTurn;
            result.Message = cancelled
                ? "Codex 电脑任务已取消。"
                : success
                    ? DefaultMessage(finalMessage, "Codex 电脑任务已经完成。")
                    : DefaultMessage(error, DefaultMessage(finalMessage, "Codex 电脑任务失败。"));
            completion.TrySetResult(result);
        }

        private void FailPending(Exception error)
        {
            List<TaskCompletionSource<Dictionary<string, object>>> requests;
            TaskCompletionSource<CodexComputerTaskResult> turn;
            lock (stateGate)
            {
                requests = new List<TaskCompletionSource<Dictionary<string, object>>>(pendingRequests.Values);
                pendingRequests.Clear();
                turn = currentTurnCompletion;
            }

            for (int i = 0; i < requests.Count; i++)
            {
                requests[i].TrySetException(error);
            }

            if (turn != null)
            {
                turn.TrySetResult(new CodexComputerTaskResult
                {
                    Success = false,
                    Message = error.Message,
                    ThreadId = GetThreadId(),
                    TurnId = currentTurnId
                });
            }
        }

        private void StopProcess()
        {
            Process active;
            StreamWriter writer;
            lock (stateGate)
            {
                active = process;
                writer = input;
                process = null;
                input = null;
                threadId = "";
            }

            if (writer != null)
            {
                try { writer.Close(); }
                catch { }
            }

            if (active != null)
            {
                try
                {
                    if (!active.HasExited && !active.WaitForExit(1200))
                    {
                        active.Kill();
                    }
                }
                catch
                {
                }
                finally
                {
                    active.Dispose();
                }
            }
        }

        private string GetThreadId()
        {
            lock (stateGate) { return threadId ?? ""; }
        }

        private void ReportStatus(string state, string message)
        {
            Action<string, string> handler = statusHandler;
            if (handler != null)
            {
                try { handler(state, message); }
                catch { }
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("CodexComputerBridge");
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { Observe(InterruptCurrentTurnAsync()); }
            catch { }
            StopProcess();
            FailPending(new ObjectDisposedException("CodexComputerBridge"));
            lifecycleGate.Dispose();
            taskGate.Dispose();
        }

        private static async Task<T> AwaitWithTimeoutAsync<T>(
            Task<T> task,
            int timeoutMilliseconds,
            CancellationToken cancellationToken,
            string timeoutMessage)
        {
            Task delay = Task.Delay(timeoutMilliseconds, cancellationToken);
            Task completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
            if (ReferenceEquals(completed, task))
            {
                return await task.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(timeoutMessage);
        }

        private static Dictionary<string, object> ReadDictionary(Dictionary<string, object> source, string key)
        {
            Dictionary<string, object> value = ReadOptionalDictionary(source, key);
            if (value == null)
            {
                throw new InvalidOperationException("Codex 响应缺少对象字段：“" + key + "”。");
            }

            return value;
        }

        private static Dictionary<string, object> ReadOptionalDictionary(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static string ReadString(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
                : "";
        }

        private static string FirstNonEmpty(string first, string second)
        {
            return String.IsNullOrWhiteSpace(first) ? (second ?? "") : first;
        }

        private static string DefaultMessage(string value, string fallback)
        {
            return String.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string Shorten(string value, int maximum)
        {
            string text = (value ?? "").Trim();
            return text.Length <= maximum ? text : text.Substring(0, Math.Max(1, maximum - 1)) + "…";
        }

        private static void Observe(Task task)
        {
            if (task == null) return;
            task.ContinueWith(delegate(Task ignored) { }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    internal static class CodexComputerIntegrationSelfTest
    {
        public static async Task<bool> RunAsync()
        {
            bool toolInvoked = false;
            bool catalogContainsToDesk = false;
            using (CodexComputerBridge bridge = new CodexComputerBridge(
                async delegate(CodexComputerToolCall call, CancellationToken token)
                {
                    token.ThrowIfCancellationRequested();
                    if (String.Equals(call.Tool, "computer_app_list", StringComparison.OrdinalIgnoreCase))
                    {
                        toolInvoked = true;
                        CodexComputerToolResult toolResult = await CodexComputerTools.ExecuteAsync(call, token).ConfigureAwait(false);
                        catalogContainsToDesk = toolResult.Success &&
                            toolResult.Message.IndexOf("ToDesk", StringComparison.OrdinalIgnoreCase) >= 0;
                        return toolResult;
                    }

                    return CodexComputerToolResult.Fail("集成自测只允许读取应用列表。");
                },
                null))
            using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
            {
                CodexComputerTaskResult result = await bridge.RunTaskAsync(
                    "请调用 computer_app_list 工具读取应用列表，然后简短说明结果。",
                    false,
                    timeout.Token).ConfigureAwait(false);
                return result.Success && toolInvoked && catalogContainsToDesk && !String.IsNullOrWhiteSpace(result.Message);
            }
        }
    }
}
