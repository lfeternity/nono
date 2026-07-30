using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NoNoStandalone
{
    internal static class ComputerBrowserController
    {
        private const int MaximumUrlLength = 2048;
        private const int MaximumSearchLength = 512;

        public static string OpenUrl(string value, string browser)
        {
            string url = NormalizeHttpUrl(value);
            CodexComputerSafety.EnsureNoPaymentIntent(url);
            string normalizedBrowser = NormalizeBrowser(browser);
            ProcessStartInfo startInfo;
            if (normalizedBrowser == "default")
            {
                startInfo = new ProcessStartInfo(url) { UseShellExecute = true };
            }
            else
            {
                string executable = FindBrowserExecutable(normalizedBrowser);
                startInfo = new ProcessStartInfo(executable, QuoteArgument(url)) { UseShellExecute = true };
            }

            Process.Start(startInfo);
            return "已让" + BrowserLabel(normalizedBrowser) + "打开“" + DisplayUrl(url) + "”。";
        }

        public static string Search(string query, string browser)
        {
            string text = (query ?? "").Trim();
            if (text.Length == 0 || text.Length > MaximumSearchLength || ContainsControlCharacter(text))
            {
                throw new InvalidOperationException("搜索内容为空、过长或包含无效字符。");
            }

            CodexComputerSafety.EnsureNoPaymentIntent(text);
            return OpenUrl("https://www.bing.com/search?q=" + Uri.EscapeDataString(text), browser);
        }

        public static string NormalizeHttpUrl(string value)
        {
            string text = (value ?? "").Trim();
            if (text.Length == 0 || text.Length > MaximumUrlLength || ContainsControlCharacter(text))
            {
                throw new InvalidOperationException("网址为空、过长或包含无效字符。");
            }

            if (text.IndexOf("://", StringComparison.Ordinal) < 0)
            {
                text = "https://" + text;
            }

            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri) ||
                !(String.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                  String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
                String.IsNullOrWhiteSpace(uri.Host) || !String.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidOperationException("只允许不含账号密码的 HTTP 或 HTTPS 网址。");
            }

            CodexComputerSafety.EnsureNoPaymentIntent(uri.AbsoluteUri);
            return uri.AbsoluteUri;
        }

        public static bool RunSelfTest()
        {
            try
            {
                return NormalizeHttpUrl("example.com/path") == "https://example.com/path" &&
                    NormalizeBrowser("Chrome") == "chrome" &&
                    RejectsUrl("javascript:alert(1)") &&
                    RejectsUrl("https://example.com/checkout");
            }
            catch
            {
                return false;
            }
        }

        private static bool RejectsUrl(string value)
        {
            try
            {
                NormalizeHttpUrl(value);
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        private static string NormalizeBrowser(string browser)
        {
            string value = (browser ?? "").Trim().ToLowerInvariant();
            if (value.Length == 0 || value == "default" || value == "默认" || value == "默认浏览器")
            {
                return "default";
            }

            if (value == "chrome" || value == "google chrome" || value == "谷歌" || value == "谷歌浏览器")
            {
                return "chrome";
            }

            if (value == "edge" || value == "microsoft edge" || value == "微软edge")
            {
                return "edge";
            }

            throw new InvalidOperationException("浏览器只支持 default、chrome 或 edge。");
        }

        private static string FindBrowserExecutable(string browser)
        {
            string fileName = browser == "chrome" ? "chrome.exe" : "msedge.exe";
            string[] registryKeys = new string[]
            {
                @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + fileName,
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + fileName,
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" + fileName
            };
            for (int i = 0; i < registryKeys.Length; i++)
            {
                string value = Convert.ToString(Registry.GetValue(registryKeys[i], "", null));
                if (!String.IsNullOrWhiteSpace(value) && File.Exists(value.Trim('"')))
                {
                    return Path.GetFullPath(value.Trim('"'));
                }
            }

            List<string> candidates = new List<string>();
            AddBrowserCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), browser);
            AddBrowserCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), browser);
            AddBrowserCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), browser);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (File.Exists(candidates[i]))
                {
                    return candidates[i];
                }
            }

            throw new InvalidOperationException("没有找到已安装的" + BrowserLabel(browser) + "。可以改用 default 打开默认浏览器。");
        }

        private static void AddBrowserCandidates(List<string> candidates, string root, string browser)
        {
            if (String.IsNullOrWhiteSpace(root)) return;
            candidates.Add(Path.Combine(root, browser == "chrome"
                ? @"Google\Chrome\Application\chrome.exe"
                : @"Microsoft\Edge\Application\msedge.exe"));
        }

        private static string BrowserLabel(string browser)
        {
            if (browser == "chrome") return " Chrome";
            if (browser == "edge") return " Edge";
            return "默认浏览器";
        }

        private static string DisplayUrl(string url)
        {
            return url.Length <= 180 ? url : url.Substring(0, 179) + "…";
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "%22") + "\"";
        }

        private static bool ContainsControlCharacter(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (Char.IsControl(value[i])) return true;
            }

            return false;
        }
    }

    internal static class ComputerClipboardController
    {
        private const int MaximumClipboardCharacters = 32768;

        public static string ReadText()
        {
            string text = InvokeSta(delegate
            {
                return Clipboard.ContainsText(TextDataFormat.UnicodeText)
                    ? Clipboard.GetText(TextDataFormat.UnicodeText)
                    : "";
            });
            if (String.IsNullOrEmpty(text))
            {
                return "剪贴板中没有文本。";
            }

            if (text.Length > MaximumClipboardCharacters)
            {
                throw new InvalidOperationException("剪贴板文本超过 32768 个字符，拒绝发送给 Codex。");
            }

            if (CodexComputerSafety.ContainsCredentialSignal(text))
            {
                throw new InvalidOperationException("剪贴板文本可能包含凭据，拒绝发送给 Codex。");
            }

            return "剪贴板文本：\n---\n" + text;
        }

        public static string WriteText(string value)
        {
            string text = value ?? "";
            if (text.Length == 0 || text.Length > MaximumClipboardCharacters || text.IndexOf('\0') >= 0)
            {
                throw new InvalidOperationException("剪贴板文本为空、过长或包含无效字符。");
            }

            InvokeSta(delegate
            {
                Clipboard.SetText(text, TextDataFormat.UnicodeText);
                string actual = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (!String.Equals(actual, text, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("剪贴板写入后验证失败。");
                }

                return true;
            });
            return "已写入剪贴板，共 " + text.Length.ToString() + " 个字符。";
        }

        private static T InvokeSta<T>(Func<T> action)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                return action();
            }

            T result = default(T);
            Exception error = null;
            using (ManualResetEvent completed = new ManualResetEvent(false))
            {
                Thread thread = new Thread(new ThreadStart(delegate
                {
                    try
                    {
                        result = action();
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    finally
                    {
                        completed.Set();
                    }
                }));
                thread.IsBackground = true;
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                if (!completed.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("剪贴板操作超时。");
                }
            }

            if (error != null)
            {
                throw new InvalidOperationException("剪贴板操作失败：" + error.Message, error);
            }

            return result;
        }
    }

    internal static class ComputerPowerController
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LockWorkStation();

        public static string Execute(string action)
        {
            string value = (action ?? "").Trim().ToLowerInvariant();
            switch (value)
            {
                case "lock":
                    if (!LockWorkStation())
                    {
                        throw new InvalidOperationException("Windows 未能锁定工作站，错误码：" + Marshal.GetLastWin32Error().ToString() + "。");
                    }
                    return "Windows 已锁定。";
                case "sleep":
                    if (!Application.SetSuspendState(PowerState.Suspend, false, false))
                    {
                        throw new InvalidOperationException("Windows 未能进入睡眠状态。");
                    }
                    return "Windows 已进入睡眠状态。";
                case "hibernate":
                    if (!Application.SetSuspendState(PowerState.Hibernate, false, false))
                    {
                        throw new InvalidOperationException("Windows 未能进入休眠状态。");
                    }
                    return "Windows 已进入休眠状态。";
                case "shutdown":
                    StartShutdown("/s /t 30 /d p:0:0");
                    return "Windows 已安排在 30 秒后关机。";
                case "restart":
                    StartShutdown("/r /t 30 /d p:0:0");
                    return "Windows 已安排在 30 秒后重启。";
                default:
                    throw new InvalidOperationException("电源操作只支持 lock、sleep、hibernate、shutdown 或 restart。");
            }
        }

        public static string CancelScheduledShutdown()
        {
            Process process = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/a")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process != null)
            {
                process.WaitForExit(5000);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("没有可取消的关机或重启计划。");
                }
                process.Dispose();
            }

            return "已取消计划中的关机或重启。";
        }

        private static void StartShutdown(string arguments)
        {
            Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "shutdown.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
    }
}
