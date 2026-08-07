using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local;

namespace NoNoStandalone
{
    internal enum ScreenTranslationState
    {
        Idle,
        Capturing,
        Selecting,
        Recognizing,
        Translating,
        Visible,
        Failed
    }

    internal sealed class ScreenTranslationSettings
    {
        public string TargetLanguage;
        public string ApiBaseUrl;
        public string Model;
        public float MinimumConfidence;

        public ScreenTranslationSettings Clone()
        {
            return (ScreenTranslationSettings)MemberwiseClone();
        }
    }

    internal sealed class ScreenTranslationSecrets
    {
        public string ApiKey;

        public ScreenTranslationSecrets Clone()
        {
            return (ScreenTranslationSecrets)MemberwiseClone();
        }
    }

    internal static class ScreenTranslationSettingsStore
    {
        internal const string DefaultApiBaseUrl = "https://fast.qianxing.pro/v1";
        internal const string DefaultModel = "gemini-3.1-flash-lite";
        private const string TargetLanguageKey = "screen-translation-target-language";
        private const string ApiBaseUrlKey = "screen-translation-api-url";
        private const string ModelKey = "screen-translation-model";
        private const string MinimumConfidenceKey = "screen-translation-minimum-confidence";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NoNo.ScreenTranslation.Secrets.v1");
        private static readonly string SecretsFile = Path.Combine(PanelStorage.Root, "screen-translation-secrets.dat");

        public static ScreenTranslationSettings Load()
        {
            ScreenTranslationSettings settings = new ScreenTranslationSettings();
            settings.TargetLanguage = DefaultIfEmpty(PanelStorage.LoadPreference(TargetLanguageKey), "简体中文");
            settings.ApiBaseUrl = DefaultIfEmpty(PanelStorage.LoadPreference(ApiBaseUrlKey), DefaultApiBaseUrl);
            settings.Model = DefaultIfEmpty(PanelStorage.LoadPreference(ModelKey), DefaultModel);
            settings.MinimumConfidence = ParseConfidence(PanelStorage.LoadPreference(MinimumConfidenceKey));
            return settings;
        }

        public static ScreenTranslationSecrets LoadSecrets()
        {
            ScreenTranslationSecrets secrets = new ScreenTranslationSecrets();
            secrets.ApiKey = Environment.GetEnvironmentVariable("NONO_TRANSLATION_API_KEY") ?? "";
            try
            {
                if (!File.Exists(SecretsFile))
                {
                    return secrets;
                }

                byte[] encrypted = File.ReadAllBytes(SecretsFile);
                byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                secrets.ApiKey = Encoding.UTF8.GetString(plain);
                Array.Clear(plain, 0, plain.Length);
            }
            catch
            {
            }

            return secrets;
        }

        public static void Save(ScreenTranslationSettings settings, ScreenTranslationSecrets secrets)
        {
            if (settings == null || secrets == null)
            {
                return;
            }

            PanelStorage.SavePreference(TargetLanguageKey, DefaultIfEmpty(settings.TargetLanguage, "简体中文"));
            PanelStorage.SavePreference(ApiBaseUrlKey, (settings.ApiBaseUrl ?? "").Trim());
            PanelStorage.SavePreference(ModelKey, (settings.Model ?? "").Trim());
            PanelStorage.SavePreference(
                MinimumConfidenceKey,
                Math.Max(0.35F, Math.Min(0.95F, settings.MinimumConfidence)).ToString("0.00", CultureInfo.InvariantCulture));

            PanelStorage.EnsureRoot();
            byte[] plain = Encoding.UTF8.GetBytes(secrets.ApiKey ?? "");
            try
            {
                byte[] encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(SecretsFile, encrypted);
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }

        public static bool IsConfigured(ScreenTranslationSettings settings, ScreenTranslationSecrets secrets)
        {
            if (settings == null || String.IsNullOrWhiteSpace(settings.ApiBaseUrl) || String.IsNullOrWhiteSpace(settings.Model))
            {
                return false;
            }

            Uri uri;
            if (!Uri.TryCreate(BuildChatCompletionsUrl(settings.ApiBaseUrl), UriKind.Absolute, out uri))
            {
                return false;
            }

            return IsLoopback(uri) || !String.IsNullOrWhiteSpace(secrets == null ? "" : secrets.ApiKey);
        }

        public static string Validate(ScreenTranslationSettings settings, ScreenTranslationSecrets secrets)
        {
            if (settings == null || String.IsNullOrWhiteSpace(settings.ApiBaseUrl))
            {
                return "请填写翻译 API 地址。";
            }

            if (String.IsNullOrWhiteSpace(settings.Model))
            {
                return "请填写翻译模型名称。";
            }

            Uri uri;
            if (!Uri.TryCreate(BuildChatCompletionsUrl(settings.ApiBaseUrl), UriKind.Absolute, out uri))
            {
                return "翻译 API 地址无效。";
            }

            bool isHttp = String.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
            bool isHttps = String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            if (!isHttps && !(isHttp && IsLoopback(uri)))
            {
                return "远程翻译 API 必须使用 HTTPS；HTTP 只允许本机回环地址。";
            }

            if (!IsLoopback(uri) && String.IsNullOrWhiteSpace(secrets == null ? "" : secrets.ApiKey))
            {
                return "远程翻译 API 需要密钥。";
            }

            return "";
        }

        public static string BuildChatCompletionsUrl(string baseUrl)
        {
            string value = (baseUrl ?? "").Trim().TrimEnd('/');
            if (value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                return value + "/chat/completions";
            }

            return value + "/v1/chat/completions";
        }

        public static bool IsLoopback(Uri uri)
        {
            if (uri == null)
            {
                return false;
            }

            return uri.IsLoopback ||
                String.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);
        }

        private static float ParseConfidence(string value)
        {
            float parsed;
            return Single.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                ? Math.Max(0.35F, Math.Min(0.95F, parsed))
                : 0.58F;
        }

        private static string DefaultIfEmpty(string value, string fallback)
        {
            return String.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
    }

    internal sealed class ScreenTextBlock
    {
        public string Id;
        public string Text;
        public string Translation;
        public string Source;
        public float Confidence;
        public Rectangle Bounds;
        public PointF[] Polygon;

        public ScreenTextBlock Clone()
        {
            ScreenTextBlock copy = (ScreenTextBlock)MemberwiseClone();
            copy.Polygon = Polygon == null ? null : (PointF[])Polygon.Clone();
            return copy;
        }
    }

    internal sealed class RegionCaptureSnapshot : IDisposable
    {
        public Bitmap Screenshot;
        public Rectangle ScreenBounds;
        public IntPtr TargetWindow;

        public void Dispose()
        {
            if (Screenshot != null)
            {
                Screenshot.Dispose();
                Screenshot = null;
            }
        }
    }

    internal sealed class ExternalForegroundWindowTracker : IDisposable
    {
        private readonly int ownerProcessId;
        private readonly System.Windows.Forms.Timer timer;
        private bool disposed;
        private IntPtr lastWindow;

        public ExternalForegroundWindowTracker()
        {
            ownerProcessId = Process.GetCurrentProcess().Id;
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 200;
            timer.Tick += delegate { Remember(); };
            Remember();
            timer.Start();
        }

        public IntPtr LastWindow
        {
            get
            {
                Remember();
                return lastWindow;
            }
        }

        public void Dispose()
        {
            disposed = true;
            timer.Stop();
            timer.Dispose();
        }

        private void Remember()
        {
            if (disposed)
            {
                return;
            }

            IntPtr window = DesktopNative.GetForegroundWindow();
            if (window == IntPtr.Zero || !DesktopNative.IsWindow(window) || !DesktopNative.IsWindowVisible(window))
            {
                return;
            }

            window = DesktopNative.GetAncestor(window, DesktopNative.GaRoot);
            int processId;
            DesktopNative.GetWindowThreadProcessId(window, out processId);
            if (processId != ownerProcessId)
            {
                lastWindow = window;
            }
        }
    }

    internal static class RegionCaptureService
    {
        public static async Task<RegionCaptureSnapshot> CaptureAsync(
            Form owner,
            IntPtr targetWindow,
            CancellationToken cancellationToken)
        {
            if (owner == null)
            {
                throw new ArgumentNullException("owner");
            }

            Screen targetScreen = Screen.FromControl(owner);
            Rectangle bounds = targetScreen.Bounds;
            bool wasVisible = owner.Visible;
            try
            {
                if (wasVisible)
                {
                    owner.Hide();
                }

                await Task.Delay(45, cancellationToken).ConfigureAwait(true);
                Bitmap screenshot = await Task.Run(delegate
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
                    try
                    {
                        using (Graphics graphics = Graphics.FromImage(bitmap))
                        {
                            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                        }
                        return bitmap;
                    }
                    catch
                    {
                        bitmap.Dispose();
                        throw;
                    }
                }, cancellationToken).ConfigureAwait(true);

                RegionCaptureSnapshot result = new RegionCaptureSnapshot();
                result.Screenshot = screenshot;
                result.ScreenBounds = bounds;
                result.TargetWindow = targetWindow;
                return result;
            }
            finally
            {
                if (wasVisible && !owner.IsDisposed)
                {
                    owner.Show();
                }
            }
        }

        public static Bitmap Crop(Bitmap source, Rectangle selectedBounds)
        {
            Rectangle safe = Rectangle.Intersect(new Rectangle(Point.Empty, source.Size), selectedBounds);
            if (safe.Width < 12 || safe.Height < 12)
            {
                throw new InvalidOperationException("框选区域太小。" );
            }

            return source.Clone(safe, PixelFormat.Format32bppPArgb);
        }
    }

    internal sealed class PaddleScreenOcrEngine : IDisposable
    {
        private readonly object sync = new object();
        private PaddleOcrAll engine;
        private Exception initializationError;
        private bool disposed;

        public Task WarmUpAsync()
        {
            return Task.Run(delegate
            {
                try
                {
                    using (Bitmap sample = new Bitmap(320, 80, PixelFormat.Format24bppRgb))
                    using (Graphics graphics = Graphics.FromImage(sample))
                    using (Font font = new Font("Microsoft YaHei UI", 20F, FontStyle.Regular, GraphicsUnit.Pixel))
                    {
                        graphics.Clear(Color.White);
                        graphics.DrawString("NoNo OCR 你好", font, Brushes.Black, 8F, 18F);
                        RecognizePaddle(sample, CancellationToken.None);
                    }
                }
                catch
                {
                }
            });
        }

        public List<ScreenTextBlock> Recognize(Bitmap image, CancellationToken cancellationToken)
        {
            try
            {
                return RecognizePaddle(image, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return WindowsOcrPowerShellFallback.Recognize(image, cancellationToken);
            }
        }

        internal List<ScreenTextBlock> RecognizePaddleOnlyForSelfTest(Bitmap image, CancellationToken cancellationToken)
        {
            return RecognizePaddle(image, cancellationToken);
        }

        public void Dispose()
        {
            lock (sync)
            {
                disposed = true;
                if (engine != null)
                {
                    engine.Dispose();
                    engine = null;
                }
            }
        }

        private List<ScreenTextBlock> RecognizePaddle(Bitmap image, CancellationToken cancellationToken)
        {
            lock (sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PaddleOcrAll current = EnsureEngine();
                byte[] encoded;
                using (MemoryStream stream = new MemoryStream())
                {
                    image.Save(stream, ImageFormat.Png);
                    encoded = stream.ToArray();
                }

                using (OpenCvSharp.Mat source = OpenCvSharp.Cv2.ImDecode(encoded, OpenCvSharp.ImreadModes.Color))
                {
                    PaddleOcrResult result = current.Run(source, 8);
                    List<ScreenTextBlock> blocks = new List<ScreenTextBlock>();
                    for (int i = 0; i < result.Regions.Length; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        PaddleOcrResultRegion region = result.Regions[i];
                        string text = (region.Text ?? "").Trim();
                        if (text.Length == 0)
                        {
                            continue;
                        }

                        OpenCvSharp.Rect raw = region.Rect.BoundingRect();
                        Rectangle bounds = Rectangle.Intersect(
                            new Rectangle(0, 0, image.Width, image.Height),
                            new Rectangle(raw.X, raw.Y, raw.Width, raw.Height));
                        if (bounds.Width < 2 || bounds.Height < 2)
                        {
                            continue;
                        }

                        OpenCvSharp.Point2f[] points = region.Rect.Points();
                        ScreenTextBlock block = new ScreenTextBlock();
                        block.Id = "ocr_" + blocks.Count.ToString(CultureInfo.InvariantCulture);
                        block.Text = text;
                        block.Source = "ocr";
                        block.Confidence = region.Score;
                        block.Bounds = bounds;
                        block.Polygon = points.Select(p => new PointF(p.X, p.Y)).ToArray();
                        blocks.Add(block);
                    }
                    return blocks;
                }
            }
        }

        private PaddleOcrAll EnsureEngine()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("PaddleScreenOcrEngine");
            }

            if (engine != null)
            {
                return engine;
            }

            if (initializationError != null)
            {
                throw new InvalidOperationException("PP-OCRv5 初始化失败。", initializationError);
            }

            try
            {
                engine = new PaddleOcrAll(LocalFullModels.ChineseV5, PaddleDevice.Onnx())
                {
                    AllowRotateDetection = true,
                    Enable180Classification = false
                };
                return engine;
            }
            catch (Exception ex)
            {
                initializationError = ex;
                throw;
            }
        }
    }

    internal static class WindowsOcrPowerShellFallback
    {
        public static List<ScreenTextBlock> Recognize(Bitmap image, CancellationToken cancellationToken)
        {
            string imagePath = Path.Combine(Path.GetTempPath(), "nono-region-ocr-" + Guid.NewGuid().ToString("N") + ".png");
            string scriptPath = Path.Combine(Path.GetTempPath(), "nono-region-ocr-" + Guid.NewGuid().ToString("N") + ".ps1");
            try
            {
                image.Save(imagePath, ImageFormat.Png);
                File.WriteAllText(scriptPath, Script, new UTF8Encoding(false));
                ProcessStartInfo info = new ProcessStartInfo("powershell.exe");
                info.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" \"" + imagePath + "\"";
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                using (Process process = Process.Start(info))
                {
                    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                    while (!process.WaitForExit(50))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            try { process.Kill(); } catch { }
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }

                    string output = outputTask.GetAwaiter().GetResult().Trim();
                    return Parse(output);
                }
            }
            finally
            {
                TryDelete(imagePath);
                TryDelete(scriptPath);
            }
        }

        private static List<ScreenTextBlock> Parse(string json)
        {
            List<ScreenTextBlock> blocks = new List<ScreenTextBlock>();
            if (String.IsNullOrWhiteSpace(json))
            {
                return blocks;
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            object value = serializer.DeserializeObject(json);
            object[] items = value as object[];
            if (items == null)
            {
                Dictionary<string, object> single = value as Dictionary<string, object>;
                items = single == null ? new object[0] : new object[] { single };
            }

            for (int i = 0; i < items.Length; i++)
            {
                Dictionary<string, object> item = items[i] as Dictionary<string, object>;
                if (item == null)
                {
                    continue;
                }

                string text = ReadString(item, "text").Trim();
                Rectangle bounds = new Rectangle(
                    ReadInt(item, "x"), ReadInt(item, "y"),
                    ReadInt(item, "width"), ReadInt(item, "height"));
                if (text.Length == 0 || bounds.Width < 2 || bounds.Height < 2)
                {
                    continue;
                }

                ScreenTextBlock block = new ScreenTextBlock();
                block.Id = "winocr_" + blocks.Count.ToString(CultureInfo.InvariantCulture);
                block.Text = text;
                block.Source = "windows-ocr";
                block.Confidence = 0.72F;
                block.Bounds = bounds;
                blocks.Add(block);
            }
            return blocks;
        }

        private static string ReadString(Dictionary<string, object> value, string key)
        {
            object result;
            return value.TryGetValue(key, out result) && result != null ? Convert.ToString(result, CultureInfo.InvariantCulture) : "";
        }

        private static int ReadInt(Dictionary<string, object> value, string key)
        {
            object result;
            int parsed;
            return value.TryGetValue(key, out result) && Int32.TryParse(Convert.ToString(result, CultureInfo.InvariantCulture), out parsed)
                ? parsed
                : 0;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private const string Script = @"param([string]$Path)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
[Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime] | Out-Null
[Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Storage.Streams, ContentType=WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType=WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType=WindowsRuntime] | Out-Null
[Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime] | Out-Null
[Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType=WindowsRuntime] | Out-Null
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetGenericArguments().Length -eq 1 -and $_.GetParameters().Length -eq 1 } | Select-Object -First 1
function Await($op, $type) { $task = $asTask.MakeGenericMethod($type).Invoke($null, @($op)); $task.Wait(); $task.Result }
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Path)) ([Windows.Storage.StorageFile])
$stream = Await ($file.OpenReadAsync()) ([Windows.Storage.Streams.IRandomAccessStreamWithContentType])
$decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
if ($engine -eq $null) { '[]'; exit 0 }
$result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
$items = @()
foreach ($line in $result.Lines) {
  if ($line.Words.Count -eq 0) { continue }
  $left = ($line.Words | ForEach-Object { $_.BoundingRect.X } | Measure-Object -Minimum).Minimum
  $top = ($line.Words | ForEach-Object { $_.BoundingRect.Y } | Measure-Object -Minimum).Minimum
  $right = ($line.Words | ForEach-Object { $_.BoundingRect.X + $_.BoundingRect.Width } | Measure-Object -Maximum).Maximum
  $bottom = ($line.Words | ForEach-Object { $_.BoundingRect.Y + $_.BoundingRect.Height } | Measure-Object -Maximum).Maximum
  $items += [PSCustomObject]@{ text=$line.Text; x=[int]$left; y=[int]$top; width=[int]($right-$left); height=[int]($bottom-$top) }
}
ConvertTo-Json -InputObject @($items) -Compress";
    }

    internal static class ScreenUiTextExtractor
    {
        public static List<ScreenTextBlock> Extract(
            IntPtr window,
            Rectangle absoluteSelection,
            CancellationToken cancellationToken)
        {
            List<ScreenTextBlock> blocks = new List<ScreenTextBlock>();
            if (window == IntPtr.Zero || !DesktopNative.IsWindow(window))
            {
                return blocks;
            }

            int processId;
            DesktopNative.GetWindowThreadProcessId(window, out processId);
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    if (PrivacyRedactor.IsBlockedProcess(process.ProcessName))
                    {
                        return blocks;
                    }
                }
            }
            catch
            {
            }

            try
            {
                AutomationElement root = AutomationElement.FromHandle(window);
                if (root == null)
                {
                    return blocks;
                }

                AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                Stopwatch budget = Stopwatch.StartNew();
                int count = Math.Min(elements.Count, 700);
                for (int i = 0; i < count && budget.ElapsedMilliseconds < 450; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AutomationElement element = elements[i];
                    try
                    {
                        if (element.Current.IsOffscreen || element.Current.IsPassword)
                        {
                            continue;
                        }

                        string text = (element.Current.Name ?? "").Trim();
                        System.Windows.Rect raw = element.Current.BoundingRectangle;
                        Rectangle absolute = Rectangle.FromLTRB(
                            (int)Math.Floor(raw.Left), (int)Math.Floor(raw.Top),
                            (int)Math.Ceiling(raw.Right), (int)Math.Ceiling(raw.Bottom));
                        Rectangle intersection = Rectangle.Intersect(absolute, absoluteSelection);
                        if (text.Length == 0 || text.Length > 320 || intersection.Width < 2 || intersection.Height < 2)
                        {
                            continue;
                        }

                        ScreenTextBlock block = new ScreenTextBlock();
                        block.Id = "uia_" + blocks.Count.ToString(CultureInfo.InvariantCulture);
                        block.Text = text;
                        block.Source = "uia";
                        block.Confidence = 1F;
                        block.Bounds = new Rectangle(
                            intersection.Left - absoluteSelection.Left,
                            intersection.Top - absoluteSelection.Top,
                            intersection.Width,
                            intersection.Height);
                        blocks.Add(block);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return blocks;
        }
    }

    internal static class ScreenTextMerger
    {
        public static List<ScreenTextBlock> Merge(
            List<ScreenTextBlock> ocr,
            List<ScreenTextBlock> uia,
            Rectangle selection,
            float minimumConfidence)
        {
            List<ScreenTextBlock> result = new List<ScreenTextBlock>();
            for (int i = 0; i < ocr.Count; i++)
            {
                if (ocr[i].Confidence >= minimumConfidence && IsSafeText(ocr[i].Text))
                {
                    result.Add(ocr[i].Clone());
                }
            }
            result = RemoveDuplicateBlocks(result);

            for (int i = 0; i < uia.Count; i++)
            {
                ScreenTextBlock candidate = uia[i];
                if (!IsSafeText(candidate.Text) ||
                    candidate.Bounds.Width * candidate.Bounds.Height > selection.Width * selection.Height * 0.8)
                {
                    continue;
                }

                List<int> matches = new List<int>();
                for (int p = 0; p < result.Count; p++)
                {
                    double overlap = Overlap(candidate.Bounds, result[p].Bounds);
                    if (overlap >= 0.55)
                    {
                        matches.Add(p);
                    }
                }

                if (matches.Count > 1)
                {
                    string combined = String.Join(" ", matches
                        .Select(index => result[index])
                        .OrderBy(x => x.Bounds.Top)
                        .ThenBy(x => x.Bounds.Left)
                        .Select(x => x.Text));
                    bool isSameContent = TextSimilarity(candidate.Text, combined) >= 0.40;
                    bool hasWeakOcr = matches.Any(index => result[index].Confidence < 0.78F);
                    if (isSameContent || hasWeakOcr)
                    {
                        for (int index = matches.Count - 1; index >= 0; index--)
                        {
                            result.RemoveAt(matches[index]);
                        }
                        candidate.Source = "uia+ocr";
                        result.Add(candidate.Clone());
                    }
                }
                else if (matches.Count == 1)
                {
                    int match = matches[0];
                    if (TextSimilarity(candidate.Text, result[match].Text) >= 0.45 || result[match].Confidence < 0.78F)
                    {
                        result[match].Text = candidate.Text;
                        result[match].Confidence = 1F;
                        result[match].Source = "uia+ocr";
                    }
                }
                else if (candidate.Bounds.Width < selection.Width * 0.95 && candidate.Bounds.Height < selection.Height * 0.5)
                {
                    result.Add(candidate.Clone());
                }
            }

            result = RemoveDuplicateBlocks(result);
            result = result
                .OrderBy(x => x.Bounds.Top)
                .ThenBy(x => x.Bounds.Left)
                .ToList();
            result = MergeVisualLines(result);
            result = MergeParagraphLines(result);
            for (int i = 0; i < result.Count; i++)
            {
                result[i].Id = "seg_" + (i + 1).ToString(CultureInfo.InvariantCulture);
            }
            return result;
        }

        private static List<ScreenTextBlock> MergeVisualLines(List<ScreenTextBlock> blocks)
        {
            List<ScreenTextBlock> lines = new List<ScreenTextBlock>();
            for (int i = 0; i < blocks.Count; i++)
            {
                ScreenTextBlock current = blocks[i].Clone();
                int bestLine = -1;
                double bestScore = 0D;
                for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                {
                    ScreenTextBlock line = lines[lineIndex];
                    int minHeight = Math.Max(1, Math.Min(line.Bounds.Height, current.Bounds.Height));
                    Rectangle verticalIntersection = Rectangle.Intersect(
                        new Rectangle(0, line.Bounds.Top, 1, line.Bounds.Height),
                        new Rectangle(0, current.Bounds.Top, 1, current.Bounds.Height));
                    double verticalOverlap = verticalIntersection.Height / (double)minHeight;
                    double centerDistance = Math.Abs(
                        (line.Bounds.Top + line.Bounds.Bottom) * 0.5D -
                        (current.Bounds.Top + current.Bounds.Bottom) * 0.5D);
                    double centerLimit = Math.Max(3D, Math.Max(line.Bounds.Height, current.Bounds.Height) * 0.42D);
                    if (verticalOverlap < 0.45D && centerDistance > centerLimit)
                    {
                        continue;
                    }
                    double score = verticalOverlap - centerDistance / Math.Max(1D, centerLimit * 4D);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestLine = lineIndex;
                    }
                }

                if (bestLine < 0)
                {
                    lines.Add(current);
                    continue;
                }

                ScreenTextBlock existing = lines[bestLine];
                int gap = current.Bounds.Left - existing.Bounds.Right;
                int height = Math.Max(1, Math.Max(existing.Bounds.Height, current.Bounds.Height));
                bool horizontallyRelated = gap <= Math.Max(32, height * 3) ||
                    Rectangle.Intersect(existing.Bounds, current.Bounds).Width > 0;
                if (!horizontallyRelated)
                {
                    lines.Add(current);
                    continue;
                }

                if (current.Bounds.Left < existing.Bounds.Left)
                {
                    ScreenTextBlock left = current;
                    current = existing;
                    existing = left;
                }
                existing.Text = JoinFragments(existing.Text, current.Text, existing.Bounds, current.Bounds);
                existing.Bounds = Rectangle.Union(existing.Bounds, current.Bounds);
                existing.Confidence = Math.Min(existing.Confidence, current.Confidence);
                existing.Source = "line";
                existing.Polygon = null;
                lines[bestLine] = existing;
            }

            return lines
                .OrderBy(x => x.Bounds.Top)
                .ThenBy(x => x.Bounds.Left)
                .ToList();
        }

        private static List<ScreenTextBlock> MergeParagraphLines(List<ScreenTextBlock> blocks)
        {
            List<ScreenTextBlock> merged = new List<ScreenTextBlock>();
            ScreenTextBlock previousLine = null;
            for (int i = 0; i < blocks.Count; i++)
            {
                ScreenTextBlock current = blocks[i].Clone();
                if (merged.Count == 0 || !CanJoinParagraphLine(previousLine, current))
                {
                    merged.Add(current);
                }
                else
                {
                    ScreenTextBlock paragraph = merged[merged.Count - 1];
                    paragraph.Text = JoinParagraphText(paragraph.Text, current.Text);
                    paragraph.Bounds = Rectangle.Union(paragraph.Bounds, current.Bounds);
                    paragraph.Confidence = Math.Min(paragraph.Confidence, current.Confidence);
                    paragraph.Source = "paragraph";
                    paragraph.Polygon = null;
                }
                previousLine = current;
            }
            return merged;
        }

        private static bool CanJoinParagraphLine(ScreenTextBlock previous, ScreenTextBlock current)
        {
            if (previous == null || current == null || String.IsNullOrWhiteSpace(previous.Text) || String.IsNullOrWhiteSpace(current.Text))
            {
                return false;
            }
            if (previous.Text.Length + current.Text.Length > 1600 || !CompatibleLanguageFamilies(previous.Text, current.Text))
            {
                return false;
            }

            int previousHeight = Math.Max(1, previous.Bounds.Height);
            int currentHeight = Math.Max(1, current.Bounds.Height);
            int smallerHeight = Math.Min(previousHeight, currentHeight);
            int largerHeight = Math.Max(previousHeight, currentHeight);
            if (largerHeight > smallerHeight * 2)
            {
                return false;
            }

            int verticalGap = current.Bounds.Top - previous.Bounds.Bottom;
            if (verticalGap < -Math.Max(2, smallerHeight / 3) ||
                verticalGap > Math.Max(12, largerHeight * 1.45))
            {
                return false;
            }

            int horizontalOverlap = Rectangle.Intersect(previous.Bounds, current.Bounds).Width;
            int alignmentTolerance = Math.Max(24, largerHeight * 2);
            bool alignedLeft = Math.Abs(previous.Bounds.Left - current.Bounds.Left) <= alignmentTolerance;
            bool overlaps = horizontalOverlap >= Math.Min(previous.Bounds.Width, current.Bounds.Width) * 0.25;
            return alignedLeft || overlaps;
        }

        private static string JoinFragments(string left, string right, Rectangle leftBounds, Rectangle rightBounds)
        {
            string first = (left ?? "").Trim();
            string second = (right ?? "").Trim();
            if (first.Length == 0) return second;
            if (second.Length == 0) return first;

            string normalizedFirst = Normalize(first);
            string normalizedSecond = Normalize(second);
            if (normalizedFirst.Length >= normalizedSecond.Length && normalizedFirst.Contains(normalizedSecond) &&
                Overlap(leftBounds, rightBounds) >= 0.45)
            {
                return first;
            }
            if (normalizedSecond.Length >= normalizedFirst.Length && normalizedSecond.Contains(normalizedFirst) &&
                Overlap(leftBounds, rightBounds) >= 0.45)
            {
                return second;
            }

            int overlap = LongestSuffixPrefixOverlap(first, second);
            if (overlap >= 2)
            {
                second = second.Substring(overlap).TrimStart();
            }
            if (second.Length == 0) return first;
            if (NeedsSeparator(first, second)) return first + " " + second;
            return first + second;
        }

        private static string JoinParagraphText(string left, string right)
        {
            string first = (left ?? "").Trim();
            string second = (right ?? "").Trim();
            if (first.Length == 0) return second;
            if (second.Length == 0) return first;
            if (first.EndsWith("-", StringComparison.Ordinal) && second.Length > 0 && Char.IsLetter(second[0]))
            {
                return first.Substring(0, first.Length - 1) + second;
            }
            return NeedsSeparator(first, second) ? first + " " + second : first + second;
        }

        private static bool NeedsSeparator(string left, string right)
        {
            if (left.Length == 0 || right.Length == 0) return false;
            char last = left[left.Length - 1];
            char first = right[0];
            if (Char.IsWhiteSpace(last) || Char.IsWhiteSpace(first)) return false;
            if (IsCjk(last) && IsCjk(first)) return false;
            if ("([{\u201c\u300c".IndexOf(last) >= 0 || ",.;:!?)]}\u201d\u300d".IndexOf(first) >= 0) return false;
            return true;
        }

        private static int LongestSuffixPrefixOverlap(string left, string right)
        {
            int limit = Math.Min(left.Length, right.Length);
            for (int length = limit; length >= 2; length--)
            {
                if (String.Equals(
                    left.Substring(left.Length - length, length),
                    right.Substring(0, length),
                    StringComparison.OrdinalIgnoreCase))
                {
                    return length;
                }
            }
            return 0;
        }

        private static bool IsCjk(char character)
        {
            return (character >= '\u3400' && character <= '\u9fff') ||
                (character >= '\uf900' && character <= '\ufaff');
        }

        private static bool CompatibleLanguageFamilies(string left, string right)
        {
            int first = LanguageFamily(left);
            int second = LanguageFamily(right);
            return first == second || first == 0 || second == 0 || first == 3 || second == 3;
        }

        private static List<ScreenTextBlock> RemoveDuplicateBlocks(List<ScreenTextBlock> blocks)
        {
            List<ScreenTextBlock> unique = new List<ScreenTextBlock>();
            IEnumerable<ScreenTextBlock> ordered = blocks
                .Where(x => x != null && IsSafeText(x.Text))
                .OrderByDescending(x => SourcePriority(x.Source))
                .ThenByDescending(x => x.Confidence)
                .ThenByDescending(x => Normalize(x.Text).Length)
                .ThenByDescending(x => x.Bounds.Width * x.Bounds.Height);
            foreach (ScreenTextBlock candidate in ordered)
            {
                int duplicate = -1;
                for (int i = 0; i < unique.Count; i++)
                {
                    if (AreDuplicateDetections(unique[i], candidate))
                    {
                        duplicate = i;
                        break;
                    }
                }
                if (duplicate < 0)
                {
                    unique.Add(candidate.Clone());
                    continue;
                }

                if (IsPreferred(candidate, unique[duplicate]))
                {
                    unique[duplicate] = candidate.Clone();
                }
            }
            return unique
                .OrderBy(x => x.Bounds.Top)
                .ThenBy(x => x.Bounds.Left)
                .ToList();
        }

        private static bool AreDuplicateDetections(ScreenTextBlock left, ScreenTextBlock right)
        {
            double overlap = Overlap(left.Bounds, right.Bounds);
            if (overlap < 0.68) return false;
            double similarity = TextSimilarity(left.Text, right.Text);
            if (similarity >= 0.40) return true;

            Rectangle intersection = Rectangle.Intersect(left.Bounds, right.Bounds);
            double verticalOverlap = intersection.Height / (double)Math.Max(1, Math.Min(left.Bounds.Height, right.Bounds.Height));
            double horizontalOverlap = intersection.Width / (double)Math.Max(1, Math.Min(left.Bounds.Width, right.Bounds.Width));
            return verticalOverlap >= 0.82 && horizontalOverlap >= 0.72 &&
                CompatibleLanguageFamilies(left.Text, right.Text);
        }

        private static bool IsPreferred(ScreenTextBlock candidate, ScreenTextBlock current)
        {
            int candidateSource = SourcePriority(candidate.Source);
            int currentSource = SourcePriority(current.Source);
            if (candidateSource != currentSource) return candidateSource > currentSource;
            if (Math.Abs(candidate.Confidence - current.Confidence) > 0.04F)
            {
                return candidate.Confidence > current.Confidence;
            }
            int candidateLength = Normalize(candidate.Text).Length;
            int currentLength = Normalize(current.Text).Length;
            if (candidateLength != currentLength) return candidateLength > currentLength;
            return candidate.Bounds.Width * candidate.Bounds.Height > current.Bounds.Width * current.Bounds.Height;
        }

        private static int SourcePriority(string source)
        {
            if (String.Equals(source, "uia+ocr", StringComparison.Ordinal)) return 3;
            if (String.Equals(source, "uia", StringComparison.Ordinal)) return 2;
            return 1;
        }

        private static int LanguageFamily(string text)
        {
            int cjk = 0;
            int latin = 0;
            foreach (char character in text ?? "")
            {
                if (!Char.IsLetter(character)) continue;
                if ((character >= '\u3400' && character <= '\u9fff') || (character >= '\uf900' && character <= '\ufaff')) cjk++;
                else if ((character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z')) latin++;
            }
            if (cjk > 0 && latin > 0) return 3;
            if (cjk > 0) return 1;
            if (latin > 0) return 2;
            return 0;
        }

        public static bool ShouldTranslate(string text, string targetLanguage)
        {
            string value = (text ?? "").Trim();
            if (value.Length < 2 || !value.Any(Char.IsLetter))
            {
                return false;
            }

            if (Uri.IsWellFormedUriString(value, UriKind.Absolute) ||
                Regex.IsMatch(value, @"^[A-Za-z]:\\") ||
                Regex.IsMatch(value, @"^[A-Fa-f0-9]{24,}$"))
            {
                return false;
            }

            int letters = 0;
            int cjk = 0;
            int latin = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (!Char.IsLetter(ch)) continue;
                letters++;
                if ((ch >= '\u3400' && ch <= '\u9fff') || (ch >= '\uf900' && ch <= '\ufaff')) cjk++;
                else if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')) latin++;
            }

            if (letters == 0) return false;
            if ((targetLanguage ?? "").IndexOf("中文", StringComparison.OrdinalIgnoreCase) >= 0 && cjk == letters) return false;
            if (String.Equals(targetLanguage, "English", StringComparison.OrdinalIgnoreCase) && latin == letters) return false;
            return true;
        }

        private static bool IsSafeText(string text)
        {
            if (String.IsNullOrWhiteSpace(text) || CodexComputerSafety.ContainsCredentialSignal(text))
            {
                return false;
            }

            return !Regex.IsMatch(text, @"(?i)(bearer\s+[a-z0-9._-]{12,}|api[_-]?key\s*[:=]|-----BEGIN [A-Z ]*PRIVATE KEY-----)");
        }

        private static double Overlap(Rectangle left, Rectangle right)
        {
            Rectangle intersection = Rectangle.Intersect(left, right);
            if (intersection.Width <= 0 || intersection.Height <= 0)
            {
                return 0;
            }

            double intersectionArea = intersection.Width * intersection.Height;
            double minimumArea = Math.Max(1, Math.Min(left.Width * left.Height, right.Width * right.Height));
            return intersectionArea / minimumArea;
        }

        private static double TextSimilarity(string left, string right)
        {
            string a = Normalize(left);
            string b = Normalize(right);
            if (a.Length == 0 || b.Length == 0) return 0;
            if (a.Contains(b) || b.Contains(a)) return (double)Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length);
            int same = 0;
            int limit = Math.Min(a.Length, b.Length);
            for (int i = 0; i < limit; i++) if (a[i] == b[i]) same++;
            return (double)same / Math.Max(a.Length, b.Length);
        }

        private static string Normalize(string value)
        {
            return new string((value ?? "").Where(Char.IsLetterOrDigit).Select(Char.ToLowerInvariant).ToArray());
        }
    }

    internal sealed class OpenAiScreenTranslationProvider
    {
        private enum ResponseFormatMode
        {
            JsonSchema,
            JsonObject,
            None
        }

        private static readonly HttpClient Client = CreateClient();
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

        public async Task TranslateAsync(
            IList<ScreenTextBlock> blocks,
            ScreenTranslationSettings settings,
            ScreenTranslationSecrets secrets,
            CancellationToken cancellationToken)
        {
            List<ScreenTextBlock> pending = blocks
                .Where(x => ScreenTextMerger.ShouldTranslate(x.Text, settings.TargetLanguage))
                .ToList();
            if (pending.Count == 0)
            {
                return;
            }

            int offset = 0;
            while (offset < pending.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<ScreenTextBlock> batch = new List<ScreenTextBlock>();
                int characters = 0;
                while (offset < pending.Count && batch.Count < 40)
                {
                    ScreenTextBlock next = pending[offset];
                    if (batch.Count > 0 && characters + next.Text.Length > 3500)
                    {
                        break;
                    }
                    batch.Add(next);
                    characters += next.Text.Length;
                    offset++;
                }

                Dictionary<string, string> translations = await TranslateBatchAsync(batch, settings, secrets, cancellationToken).ConfigureAwait(true);
                for (int i = 0; i < batch.Count; i++)
                {
                    string translated;
                    if (translations.TryGetValue(batch[i].Id, out translated) && !String.IsNullOrWhiteSpace(translated))
                    {
                        batch[i].Translation = translated.Trim();
                    }
                }
            }
        }

        private async Task<Dictionary<string, string>> TranslateBatchAsync(
            IList<ScreenTextBlock> batch,
            ScreenTranslationSettings settings,
            ScreenTranslationSecrets secrets,
            CancellationToken cancellationToken)
        {
            List<object> segments = new List<object>();
            for (int i = 0; i < batch.Count; i++)
            {
                segments.Add(new Dictionary<string, object>
                {
                    { "id", batch[i].Id },
                    { "text", batch[i].Text }
                });
            }

            string systemPrompt =
                "You are a professional English-Chinese translator. Translate every provided segment into " + settings.TargetLanguage + ". " +
                "Translate ordinary words and sentences; preserve only real product names, code, URLs, numbers and placeholders. " +
                "Do not leave ordinary English words untranslated and do not explain your choices. Return one compact JSON object only in the form " +
                "{\"translations\":[{\"id\":\"seg_1\",\"text\":\"...\"}]}. " +
                "Return exactly one item for every input id and never merge, omit, reorder or invent ids. " +
                "Replace source line breaks with a single space in translated text. Escape quotes, backslashes and all control characters according to JSON. " +
                "never place literal carriage returns, line feeds or tabs inside a quoted JSON string.";
            string userPrompt = serializer.Serialize(new Dictionary<string, object> { { "segments", segments.ToArray() } });
            Exception firstPayloadError = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string attemptPrompt = attempt == 0
                    ? systemPrompt
                    : systemPrompt + " This is a repair attempt after an invalid response. Output JSON only, with no Markdown or explanation.";
                using (HttpResponseMessage response = await SendWithFormatFallbackAsync(
                    attemptPrompt,
                    userPrompt,
                    batch,
                    settings,
                    secrets,
                    attempt > 0,
                    cancellationToken).ConfigureAwait(true))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException("翻译服务请求失败（HTTP " + (int)response.StatusCode + "）：" + Shorten(body, 240));
                    }

                    try
                    {
                        string content = ReadAssistantContent(body);
                        Dictionary<string, string> result = ParseTranslations(content);
                        ValidateTranslations(result, batch);
                        return result;
                    }
                    catch (Exception ex)
                    {
                        if (attempt == 0 && IsPayloadError(ex))
                        {
                            firstPayloadError = ex;
                            continue;
                        }

                        if (firstPayloadError != null && IsPayloadError(ex))
                        {
                            throw new InvalidOperationException(
                                "翻译服务连续返回无效 JSON。NoNo 已自动修复并重试一次，请缩小框选区域或更换支持结构化输出的模型。",
                                new AggregateException(firstPayloadError, ex));
                        }
                        throw;
                    }
                }
            }

            throw new InvalidOperationException("翻译服务未返回可用结果。");
        }

        private async Task<HttpResponseMessage> SendWithFormatFallbackAsync(
            string systemPrompt,
            string userPrompt,
            IList<ScreenTextBlock> batch,
            ScreenTranslationSettings settings,
            ScreenTranslationSecrets secrets,
            bool repairAttempt,
            CancellationToken cancellationToken)
        {
            ResponseFormatMode[] modes =
            {
                ResponseFormatMode.JsonSchema,
                ResponseFormatMode.JsonObject,
                ResponseFormatMode.None
            };
            for (int i = 0; i < modes.Length; i++)
            {
                HttpResponseMessage response = await SendAsync(
                    systemPrompt,
                    userPrompt,
                    batch,
                    settings,
                    secrets,
                    modes[i],
                    repairAttempt,
                    cancellationToken).ConfigureAwait(true);
                bool unsupported = ((int)response.StatusCode == 400 || (int)response.StatusCode == 422) &&
                    i < modes.Length - 1;
                if (!unsupported)
                {
                    return response;
                }
                response.Dispose();
            }

            throw new InvalidOperationException("翻译服务不支持可用的响应格式。");
        }

        private async Task<HttpResponseMessage> SendAsync(
            string systemPrompt,
            string userPrompt,
            IList<ScreenTextBlock> batch,
            ScreenTranslationSettings settings,
            ScreenTranslationSecrets secrets,
            ResponseFormatMode responseFormat,
            bool repairAttempt,
            CancellationToken cancellationToken)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["model"] = settings.Model;
            body["temperature"] = 0.0;
            body["max_tokens"] = 4096;
            body["stream"] = false;
            body["messages"] = new object[]
            {
                Message("system", systemPrompt),
                Message("user", userPrompt)
            };
            Dictionary<string, object> format = BuildResponseFormat(responseFormat, batch);
            if (format != null)
            {
                body["response_format"] = format;
            }

            HttpRequestMessage request = new HttpRequestMessage(
                HttpMethod.Post,
                ScreenTranslationSettingsStore.BuildChatCompletionsUrl(settings.ApiBaseUrl));
            if (!String.IsNullOrWhiteSpace(secrets.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.ApiKey.Trim());
            }
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(serializer.Serialize(body), Encoding.UTF8, "application/json");
            try
            {
                return await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(true);
            }
            catch (TaskCanceledException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new InvalidOperationException(
                    "翻译服务请求超时。请确认服务已启动，或缩小框选区域后重试。",
                    ex);
            }
            catch (HttpRequestException ex)
            {
                string endpoint = ScreenTranslationSettingsStore.BuildChatCompletionsUrl(settings.ApiBaseUrl);
                Uri endpointUri;
                bool loopback = Uri.TryCreate(endpoint, UriKind.Absolute, out endpointUri) &&
                    ScreenTranslationSettingsStore.IsLoopback(endpointUri);
                string message = loopback
                    ? "无法连接本地翻译服务（" + endpoint + "）。请先启动 Ollama（ollama serve），并确认已下载模型“" + settings.Model + "”。"
                    : "无法连接翻译服务（" + endpoint + "）。请检查 API 地址、网络连接和 HTTPS 设置。";
                throw new InvalidOperationException(message, ex);
            }
            finally
            {
                request.Dispose();
            }
        }

        private static Dictionary<string, object> BuildResponseFormat(
            ResponseFormatMode mode,
            IList<ScreenTextBlock> batch)
        {
            if (mode == ResponseFormatMode.None)
            {
                return null;
            }
            if (mode == ResponseFormatMode.JsonObject)
            {
                return new Dictionary<string, object> { { "type", "json_object" } };
            }

            object[] allowedIds = batch.Select(x => (object)x.Id).ToArray();
            Dictionary<string, object> itemSchema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new object[] { "id", "text" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "id", new Dictionary<string, object>
                            {
                                { "type", "string" },
                                { "enum", allowedIds }
                            }
                        },
                        { "text", new Dictionary<string, object> { { "type", "string" } } }
                    }
                }
            };
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new object[] { "translations" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "translations", new Dictionary<string, object>
                            {
                                { "type", "array" },
                                { "minItems", batch.Count },
                                { "maxItems", batch.Count },
                                { "items", itemSchema }
                            }
                        }
                    }
                }
            };
            return new Dictionary<string, object>
            {
                { "type", "json_schema" },
                { "json_schema", new Dictionary<string, object>
                    {
                        { "name", "screen_translations" },
                        { "strict", true },
                        { "schema", schema }
                    }
                }
            };
        }

        private string ReadAssistantContent(string body)
        {
            Dictionary<string, object> root = serializer.DeserializeObject(body) as Dictionary<string, object>;
            object choicesValue = null;
            object[] choices = root != null && root.TryGetValue("choices", out choicesValue) ? choicesValue as object[] : null;
            if (choices == null && choicesValue is ArrayList) choices = ((ArrayList)choicesValue).ToArray();
            if (choices == null || choices.Length == 0) throw new InvalidOperationException("翻译服务返回了空响应。" );
            Dictionary<string, object> choice = choices[0] as Dictionary<string, object>;
            object messageValue;
            Dictionary<string, object> message = choice != null && choice.TryGetValue("message", out messageValue)
                ? messageValue as Dictionary<string, object>
                : null;
            object contentValue;
            if (message == null || !message.TryGetValue("content", out contentValue) || contentValue == null)
            {
                throw new InvalidOperationException("翻译服务响应中没有文本内容。" );
            }
            return Convert.ToString(contentValue, CultureInfo.InvariantCulture);
        }

        private Dictionary<string, string> ParseTranslations(string content)
        {
            string value = RepairJson(ExtractJsonCandidate(content));
            object rootValue;
            try
            {
                rootValue = serializer.DeserializeObject(value);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("翻译服务返回的 JSON 无法解析。", ex);
            }

            Dictionary<string, object> root = rootValue as Dictionary<string, object>;
            object translationsValue = null;
            if (root != null)
            {
                root.TryGetValue("translations", out translationsValue);
            }
            else
            {
                translationsValue = rootValue;
            }

            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            object[] translations = ToObjectArray(translationsValue);
            if (translations != null)
            {
                for (int i = 0; i < translations.Length; i++)
                {
                    Dictionary<string, object> item = translations[i] as Dictionary<string, object>;
                    object idValue;
                    object textValue;
                    if (item == null || !item.TryGetValue("id", out idValue))
                    {
                        continue;
                    }
                    if (!item.TryGetValue("text", out textValue))
                    {
                        item.TryGetValue("translation", out textValue);
                    }
                    AddTranslation(result, idValue, textValue);
                }
                return result;
            }

            Dictionary<string, object> translationMap = translationsValue as Dictionary<string, object>;
            if (translationMap == null && root != null && !root.ContainsKey("translations"))
            {
                translationMap = root;
            }
            if (translationMap != null)
            {
                foreach (KeyValuePair<string, object> entry in translationMap)
                {
                    AddTranslation(result, entry.Key, entry.Value);
                }
                return result;
            }

            throw new InvalidOperationException("翻译服务返回的 JSON 格式无效。");
        }

        internal Dictionary<string, string> ParseTranslationsForSelfTest(string content)
        {
            return ParseTranslations(content);
        }

        private static void ValidateTranslations(
            Dictionary<string, string> translations,
            IList<ScreenTextBlock> batch)
        {
            HashSet<string> expected = new HashSet<string>(batch.Select(x => x.Id), StringComparer.Ordinal);
            if (translations == null || translations.Count != expected.Count)
            {
                throw new InvalidOperationException("翻译服务返回的段落数量不正确。");
            }
            foreach (string id in expected)
            {
                string text;
                if (!translations.TryGetValue(id, out text) || String.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidOperationException("翻译服务返回的段落 ID 不完整。");
                }
            }
            if (translations.Keys.Any(id => !expected.Contains(id)))
            {
                throw new InvalidOperationException("翻译服务返回了未知的段落 ID。");
            }
        }

        private static bool IsPayloadError(Exception exception)
        {
            return !(exception is OperationCanceledException) &&
                (exception is InvalidOperationException || exception is ArgumentException);
        }

        private static object[] ToObjectArray(object value)
        {
            object[] array = value as object[];
            if (array != null)
            {
                return array;
            }
            ArrayList list = value as ArrayList;
            return list == null ? null : list.ToArray();
        }

        private static void AddTranslation(
            Dictionary<string, string> result,
            object idValue,
            object textValue)
        {
            if (idValue == null || textValue == null)
            {
                return;
            }
            string id = Convert.ToString(idValue, CultureInfo.InvariantCulture).Trim();
            if (id.Length == 0)
            {
                return;
            }
            result[id] = Convert.ToString(textValue, CultureInfo.InvariantCulture);
        }

        private static string ExtractJsonCandidate(string content)
        {
            string value = (content ?? "").Trim().TrimStart('\uFEFF');
            int objectStart = value.IndexOf('{');
            int arrayStart = value.IndexOf('[');
            int start;
            if (objectStart < 0)
            {
                start = arrayStart;
            }
            else if (arrayStart < 0)
            {
                start = objectStart;
            }
            else
            {
                start = Math.Min(objectStart, arrayStart);
            }
            if (start < 0)
            {
                throw new InvalidOperationException("翻译服务响应中没有 JSON 对象。");
            }

            bool inString = false;
            bool escaped = false;
            int depth = 0;
            for (int i = start; i < value.Length; i++)
            {
                char character = value[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (character == '"')
                {
                    inString = true;
                }
                else if (character == '{' || character == '[')
                {
                    depth++;
                }
                else if (character == '}' || character == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return value.Substring(start, i - start + 1);
                    }
                }
            }

            int lastObject = value.LastIndexOf('}');
            int lastArray = value.LastIndexOf(']');
            int end = Math.Max(lastObject, lastArray);
            if (end >= start)
            {
                return value.Substring(start, end - start + 1);
            }
            throw new InvalidOperationException("翻译服务返回的 JSON 不完整。");
        }

        private static string RepairJson(string value)
        {
            return RemoveTrailingCommas(EscapeInvalidStringCharacters(value));
        }

        private static string EscapeInvalidStringCharacters(string value)
        {
            StringBuilder repaired = new StringBuilder(value.Length + 16);
            bool inString = false;
            bool escaped = false;
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (!inString)
                {
                    repaired.Append(character);
                    if (character == '"')
                    {
                        inString = true;
                    }
                    continue;
                }

                if (character < 0x20)
                {
                    string escape;
                    switch (character)
                    {
                        case '\b': escape = "b"; break;
                        case '\t': escape = "t"; break;
                        case '\n': escape = "n"; break;
                        case '\f': escape = "f"; break;
                        case '\r': escape = "r"; break;
                        default: escape = "u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture); break;
                    }
                    if (!escaped)
                    {
                        repaired.Append('\\');
                    }
                    repaired.Append(escape);
                    escaped = false;
                    continue;
                }

                repaired.Append(character);
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }
            }
            return repaired.ToString();
        }

        private static string RemoveTrailingCommas(string value)
        {
            StringBuilder repaired = new StringBuilder(value.Length);
            bool inString = false;
            bool escaped = false;
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (inString)
                {
                    repaired.Append(character);
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (character == '"')
                {
                    inString = true;
                    repaired.Append(character);
                    continue;
                }
                if (character == ',')
                {
                    int next = i + 1;
                    while (next < value.Length && Char.IsWhiteSpace(value[next]))
                    {
                        next++;
                    }
                    if (next < value.Length && (value[next] == '}' || value[next] == ']'))
                    {
                        continue;
                    }
                }
                repaired.Append(character);
            }
            return repaired.ToString();
        }

        private static Dictionary<string, object> Message(string role, string content)
        {
            return new Dictionary<string, object> { { "role", role }, { "content", content } };
        }

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpClientHandler handler = new HttpClientHandler();
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        }

        private static string Shorten(string value, int max)
        {
            string text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }
    }

    internal sealed class ScreenTranslationCoordinator : IDisposable
    {
        private readonly Form owner;
        private readonly Action<string, bool> playAnimation;
        private readonly ExternalForegroundWindowTracker foregroundTracker;
        private readonly PaddleScreenOcrEngine ocrEngine;
        private readonly OpenAiScreenTranslationProvider translationProvider;
        private CancellationTokenSource cancellation;
        private RegionSelectionForm selectionForm;
        private TranslationOverlaySession overlay;
        private bool disposed;

        public event EventHandler StateChanged;
        public ScreenTranslationState State { get; private set; }
        public string StatusText { get; private set; }

        public bool IsBusy
        {
            get { return State != ScreenTranslationState.Idle && State != ScreenTranslationState.Visible; }
        }

        public bool HasOverlay
        {
            get { return overlay != null; }
        }

        public ScreenTranslationCoordinator(Form owner, Action<string, bool> playAnimation)
        {
            this.owner = owner;
            this.playAnimation = playAnimation;
            foregroundTracker = new ExternalForegroundWindowTracker();
            ocrEngine = new PaddleScreenOcrEngine();
            translationProvider = new OpenAiScreenTranslationProvider();
            State = ScreenTranslationState.Idle;
            StatusText = "空闲";
        }

        public Task WarmUpAsync()
        {
            return ocrEngine.WarmUpAsync();
        }

        public bool EditSettings(IWin32Window dialogOwner)
        {
            try
            {
                ScreenTranslationSettings settings = ScreenTranslationSettingsStore.Load();
                ScreenTranslationSecrets secrets = ScreenTranslationSettingsStore.LoadSecrets();
                ScreenTranslationSettings updated;
                ScreenTranslationSecrets updatedSecrets;
                if (!ScreenTranslationSettingsDialog.Edit(dialogOwner, settings, secrets, out updated, out updatedSecrets))
                {
                    return false;
                }

                ScreenTranslationSettingsStore.Save(updated, updatedSecrets);
                return true;
            }
            catch (Exception ex)
            {
                // Settings is opened from a synchronous WinForms menu event. Keep
                // persistence and dialog teardown failures from escaping that event.
                ProgramLog.Write("Screen translation settings", ex);
                ShowError("区域翻译设置失败", "设置未保存：" + ex.Message);
                return false;
            }
        }

        public async Task BeginAsync()
        {
            if (disposed || IsBusy)
            {
                return;
            }

            CloseOverlay();
            ScreenTranslationSettings settings;
            ScreenTranslationSecrets secrets;
            try
            {
                settings = ScreenTranslationSettingsStore.Load();
                secrets = ScreenTranslationSettingsStore.LoadSecrets();
                if (!ScreenTranslationSettingsStore.IsConfigured(settings, secrets))
                {
                    if (!EditSettings(owner))
                    {
                        return;
                    }
                    settings = ScreenTranslationSettingsStore.Load();
                    secrets = ScreenTranslationSettingsStore.LoadSecrets();
                }

                string validation = ScreenTranslationSettingsStore.Validate(settings, secrets);
                if (validation.Length > 0)
                {
                    MessageBox.Show(owner, validation, "区域翻译设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
            catch (Exception ex)
            {
                SetState(ScreenTranslationState.Failed, ex.Message, "failed");
                ProgramLog.Write("Screen translation setup", ex);
                ShowError("区域翻译设置失败", ex.Message);
                SetState(ScreenTranslationState.Idle, "空闲", "idle");
                return;
            }

            CancellationTokenSource operationCancellation = new CancellationTokenSource();
            cancellation = operationCancellation;
            CancellationToken token = operationCancellation.Token;
            RegionCaptureSnapshot snapshot = null;
            Bitmap selectedImage = null;
            try
            {
                SetState(ScreenTranslationState.Capturing, "正在截取屏幕", "review");
                IntPtr targetWindow = foregroundTracker.LastWindow;
                snapshot = await RegionCaptureService.CaptureAsync(owner, targetWindow, token).ConfigureAwait(true);

                SetState(ScreenTranslationState.Selecting, "拖动鼠标框选区域", "review");
                using (selectionForm = new RegionSelectionForm(snapshot.Screenshot, snapshot.ScreenBounds))
                {
                    DialogResult selectionResult = selectionForm.ShowDialog();
                    if (selectionResult != DialogResult.OK)
                    {
                        SetState(ScreenTranslationState.Idle, "已取消", "idle");
                        return;
                    }

                    Rectangle localSelection = selectionForm.SelectedRegion;
                    Rectangle absoluteSelection = new Rectangle(
                        snapshot.ScreenBounds.Left + localSelection.Left,
                        snapshot.ScreenBounds.Top + localSelection.Top,
                        localSelection.Width,
                        localSelection.Height);
                    selectedImage = RegionCaptureService.Crop(snapshot.Screenshot, localSelection);
                    selectionForm = null;

                    SetState(ScreenTranslationState.Recognizing, "正在识别文字", "running");
                    Task<List<ScreenTextBlock>> ocrTask = Task.Run(
                        delegate { return ocrEngine.Recognize(selectedImage, token); }, token);
                    Task<List<ScreenTextBlock>> uiaTask = Task.Run(
                        delegate { return ScreenUiTextExtractor.Extract(snapshot.TargetWindow, absoluteSelection, token); }, token);
                    await Task.WhenAll(ocrTask, uiaTask).ConfigureAwait(true);
                    List<ScreenTextBlock> blocks = ScreenTextMerger.Merge(
                        ocrTask.Result, uiaTask.Result,
                        new Rectangle(Point.Empty, localSelection.Size),
                        settings.MinimumConfidence);
                    blocks = blocks.Where(x => ScreenTextMerger.ShouldTranslate(x.Text, settings.TargetLanguage)).ToList();
                    if (blocks.Count == 0)
                    {
                        throw new InvalidOperationException("框选区域中没有需要翻译的文字。" );
                    }

                    SetState(ScreenTranslationState.Translating, "正在翻译 " + blocks.Count + " 个文字块", "running");
                    await translationProvider.TranslateAsync(blocks, settings, secrets, token).ConfigureAwait(true);
                    int untranslated = blocks.Count(x => String.IsNullOrWhiteSpace(x.Translation));
                    if (untranslated > 0)
                    {
                        throw new InvalidOperationException("翻译服务没有返回可显示的译文。" );
                    }

                    overlay = new TranslationOverlaySession(
                        snapshot.ScreenBounds,
                        absoluteSelection,
                        snapshot.Screenshot,
                        blocks);
                    overlay.Closed += OnOverlayClosed;
                    overlay.Show();
                    SetState(ScreenTranslationState.Visible, "译文已显示", "waving");
                }
            }
            catch (OperationCanceledException)
            {
                SetState(ScreenTranslationState.Idle, "已停止", "idle");
            }
            catch (Exception ex)
            {
                SetState(ScreenTranslationState.Failed, ex.Message, "failed");
                ProgramLog.Write("Screen translation", ex);
                ShowError("区域翻译失败", ex.Message);
                SetState(ScreenTranslationState.Idle, "空闲", "idle");
            }
            finally
            {
                selectionForm = null;
                if (selectedImage != null) selectedImage.Dispose();
                if (snapshot != null) snapshot.Dispose();
                operationCancellation.Dispose();
                if (Object.ReferenceEquals(cancellation, operationCancellation))
                {
                    cancellation = null;
                }
            }
        }

        public void Stop()
        {
            CancellationTokenSource activeOperation = cancellation;
            if (activeOperation != null)
            {
                activeOperation.Cancel();
            }
            if (selectionForm != null && !selectionForm.IsDisposed)
            {
                selectionForm.CancelSelection();
            }
            CloseOverlay();
            if (!disposed && activeOperation == null)
            {
                SetState(ScreenTranslationState.Idle, "已停止", "idle");
            }
        }

        public void Dispose()
        {
            disposed = true;
            if (cancellation != null) cancellation.Cancel();
            CloseOverlay();
            foregroundTracker.Dispose();
            ocrEngine.Dispose();
            if (cancellation != null) cancellation.Dispose();
            cancellation = null;
        }

        private void CloseOverlay()
        {
            TranslationOverlaySession current = overlay;
            overlay = null;
            if (current != null)
            {
                current.Closed -= OnOverlayClosed;
                current.Dispose();
            }
        }

        private void OnOverlayClosed(object sender, EventArgs e)
        {
            TranslationOverlaySession current = overlay;
            overlay = null;
            if (current != null)
            {
                current.Closed -= OnOverlayClosed;
                current.Dispose();
            }
            if (!disposed) SetState(ScreenTranslationState.Idle, "空闲", "idle");
        }

        private void SetState(ScreenTranslationState state, string status, string animation)
        {
            State = state;
            StatusText = status ?? "";
            if (playAnimation != null && !String.IsNullOrEmpty(animation))
            {
                playAnimation(animation, animation == "failed" || animation == "waving");
            }
            EventHandler handler = StateChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void ShowError(string title, string message)
        {
            try
            {
                if (!owner.IsDisposed && owner.IsHandleCreated)
                {
                    MessageBox.Show(owner, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                ProgramLog.Write("Screen translation error dialog", ex);
            }
        }
    }

    internal static class ProgramLog
    {
        public static void Write(string source, Exception exception)
        {
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NoNoStandalone");
                Directory.CreateDirectory(root);
                File.AppendAllText(
                    Path.Combine(root, "crash.log"),
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " [" + source + "]\r\n" +
                    (exception == null ? "<unknown exception>" : exception.ToString()) + "\r\n\r\n",
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }

    internal static class ScreenTranslationSelfTest
    {
        public static bool RunLightweight()
        {
            ScreenTranslationSettings loopback = new ScreenTranslationSettings
            {
                ApiBaseUrl = "http://127.0.0.1:11434/v1",
                Model = "test",
                TargetLanguage = "简体中文",
                MinimumConfidence = 0.58F
            };
            Dictionary<string, string> repaired;
            try
            {
                string malformed =
                    "Model output:\n```json\n{\"translations\":[{\"id\":\"seg_1\",\"text\":\"下载 Ollama\nWindows\",},],}\n```";
                repaired = new OpenAiScreenTranslationProvider().ParseTranslationsForSelfTest(malformed);
            }
            catch
            {
                return false;
            }
            List<ScreenTextBlock> paragraphLines = new List<ScreenTextBlock>
            {
                new ScreenTextBlock { Text = "The final revision keeps", Bounds = new Rectangle(20, 20, 220, 22), Confidence = 0.9F },
                new ScreenTextBlock { Text = "the reveal for chapter six.", Bounds = new Rectangle(20, 46, 240, 22), Confidence = 0.9F }
            };
            List<ScreenTextBlock> mergedParagraphs = ScreenTextMerger.Merge(
                paragraphLines,
                new List<ScreenTextBlock>(),
                new Rectangle(0, 0, 500, 200),
                0.58F);
            List<ScreenTextBlock> fragmentedLine = ScreenTextMerger.Merge(
                new List<ScreenTextBlock>
                {
                    new ScreenTextBlock
                    {
                        Text = "generateDebugBuildConfig",
                        Bounds = new Rectangle(20, 20, 210, 22),
                        Confidence = 0.92F
                    },
                    new ScreenTextBlock
                    {
                        Text = "DebugBuildConfig 之后的一项操作占用了剩余",
                        Bounds = new Rectangle(190, 20, 280, 22),
                        Confidence = 0.91F
                    },
                    new ScreenTextBlock
                    {
                        Text = "的约 24 秒，且从未完成或建立检查点。",
                        Bounds = new Rectangle(20, 47, 380, 22),
                        Confidence = 0.91F
                    }
                },
                new List<ScreenTextBlock>(),
                new Rectangle(0, 0, 520, 120),
                0.58F);
            return ScreenTranslationSettingsStore.IsLoopback(new Uri("http://127.0.0.1:11434")) &&
                ScreenTranslationSettingsStore.BuildChatCompletionsUrl(loopback.ApiBaseUrl) == "http://127.0.0.1:11434/v1/chat/completions" &&
                ScreenTextMerger.ShouldTranslate("Hello world", "简体中文") &&
                !ScreenTextMerger.ShouldTranslate("你好世界", "简体中文") &&
                !ScreenTextMerger.ShouldTranslate("https://example.com", "简体中文") &&
                mergedParagraphs.Count == 1 &&
                mergedParagraphs[0].Text == "The final revision keeps the reveal for chapter six." &&
                fragmentedLine.Count == 1 &&
                fragmentedLine[0].Text.IndexOf("generateDebugBuildConfig 之后", StringComparison.Ordinal) >= 0 &&
                fragmentedLine[0].Text.IndexOf("约 24 秒", StringComparison.Ordinal) >= 0 &&
                repaired.Count == 1 &&
                repaired["seg_1"] == "下载 Ollama\nWindows" &&
                TranslationOverlayForm.RunLayoutSelfTest();
        }

        public static async Task<ScreenTranslationSelfTestResult> RunDeepAsync()
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("NoNo screen translation self-test");
            report.AppendLine("UTC: " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            try
            {
                if (!RunLightweight())
                {
                    throw new InvalidOperationException("轻量规则自检失败。");
                }

                List<ScreenTextBlock> recognized;
                List<ScreenTextBlock> warmRecognized;
                long warmOcrElapsedMs;
                Stopwatch ocrTimer = Stopwatch.StartNew();
                using (PaddleScreenOcrEngine engine = new PaddleScreenOcrEngine())
                {
                    using (Bitmap sample = CreateOcrSample())
                    {
                        recognized = engine.RecognizePaddleOnlyForSelfTest(sample, CancellationToken.None);
                    }
                    ocrTimer.Stop();
                    Stopwatch warmOcrTimer = Stopwatch.StartNew();
                    using (Bitmap sample = CreateOcrSample())
                    {
                        warmRecognized = engine.RecognizePaddleOnlyForSelfTest(sample, CancellationToken.None);
                    }
                    warmOcrTimer.Stop();
                    warmOcrElapsedMs = warmOcrTimer.ElapsedMilliseconds;
                }

                string recognizedText = String.Join(" | ", recognized.Select(x => x.Text));
                report.AppendLine("OCR backend: PP-OCRv5 mobile");
                report.AppendLine("OCR cold elapsed ms: " + ocrTimer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                report.AppendLine("OCR warm elapsed ms: " + warmOcrElapsedMs.ToString(CultureInfo.InvariantCulture));
                report.AppendLine("OCR blocks: " + recognized.Count.ToString(CultureInfo.InvariantCulture));
                report.AppendLine("OCR text: " + recognizedText);
                if (recognized.Count == 0 ||
                    !recognized.Any(x => x.Bounds.Width > 2 && x.Bounds.Height > 2) ||
                    warmRecognized.Count == 0 ||
                    recognizedText.IndexOf("HELLO", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    throw new InvalidOperationException("PP-OCRv5 未能识别固定测试图中的 HELLO。");
                }

                List<ScreenTextBlock> windowsRecognized;
                Stopwatch windowsOcrTimer = Stopwatch.StartNew();
                using (Bitmap sample = CreateOcrSample())
                {
                    windowsRecognized = WindowsOcrPowerShellFallback.Recognize(sample, CancellationToken.None);
                }
                windowsOcrTimer.Stop();
                string windowsText = String.Join(" | ", windowsRecognized.Select(x => x.Text));
                report.AppendLine("Windows OCR elapsed ms: " + windowsOcrTimer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                report.AppendLine("Windows OCR blocks: " + windowsRecognized.Count.ToString(CultureInfo.InvariantCulture));
                if (windowsRecognized.Count == 0 ||
                    !windowsRecognized.Any(x => x.Bounds.Width > 2 && x.Bounds.Height > 2) ||
                    windowsText.IndexOf("HELLO", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    throw new InvalidOperationException("Windows OCR 回退未能识别固定测试图中的 HELLO。");
                }

                ScreenTextBlock block = new ScreenTextBlock
                {
                    Id = "seg_1",
                    Text = "Hello world",
                    Bounds = new Rectangle(0, 0, 200, 40),
                    Confidence = 1F,
                    Source = "self-test"
                };
                using (FakeOpenAiTranslationServer server = new FakeOpenAiTranslationServer())
                {
                    Task serverTask = server.ServeAsync();
                    ScreenTranslationSettings settings = new ScreenTranslationSettings
                    {
                        ApiBaseUrl = server.ApiBaseUrl,
                        Model = "self-test-model",
                        TargetLanguage = "简体中文",
                        MinimumConfidence = 0.58F
                    };
                    Stopwatch translationTimer = Stopwatch.StartNew();
                    await new OpenAiScreenTranslationProvider().TranslateAsync(
                        new List<ScreenTextBlock> { block },
                        settings,
                        new ScreenTranslationSecrets(),
                        CancellationToken.None).ConfigureAwait(false);
                    await serverTask.ConfigureAwait(false);
                    translationTimer.Stop();
                    report.AppendLine("Translation elapsed ms: " + translationTimer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                    report.AppendLine("Translation requests: " + server.RequestCount.ToString(CultureInfo.InvariantCulture));
                    if (!String.Equals(block.Translation, "你好，\n世界", StringComparison.Ordinal) || server.RequestCount != 2)
                    {
                        throw new InvalidOperationException("OpenAI 兼容翻译响应解析或 response_format 降级失败。");
                    }
                }

                ScreenTextBlock retryBlock = new ScreenTextBlock
                {
                    Id = "seg_1",
                    Text = "Hello world",
                    Bounds = new Rectangle(0, 0, 200, 40),
                    Confidence = 1F,
                    Source = "self-test-retry"
                };
                using (FakeOpenAiTranslationServer retryServer = new FakeOpenAiTranslationServer(false, true))
                {
                    Task serverTask = retryServer.ServeAsync();
                    ScreenTranslationSettings settings = new ScreenTranslationSettings
                    {
                        ApiBaseUrl = retryServer.ApiBaseUrl,
                        Model = "self-test-model",
                        TargetLanguage = "简体中文",
                        MinimumConfidence = 0.58F
                    };
                    await new OpenAiScreenTranslationProvider().TranslateAsync(
                        new List<ScreenTextBlock> { retryBlock },
                        settings,
                        new ScreenTranslationSecrets(),
                        CancellationToken.None).ConfigureAwait(false);
                    await serverTask.ConfigureAwait(false);
                    report.AppendLine("Translation repair requests: " + retryServer.RequestCount.ToString(CultureInfo.InvariantCulture));
                    if (!String.Equals(retryBlock.Translation, "你好，\n世界", StringComparison.Ordinal) || retryServer.RequestCount != 2)
                    {
                        throw new InvalidOperationException("翻译响应自动修复重试失败。");
                    }
                }

                report.AppendLine("Result: PASS");
                return new ScreenTranslationSelfTestResult(true, report.ToString());
            }
            catch (Exception ex)
            {
                report.AppendLine("Result: FAIL");
                report.AppendLine(ex.ToString());
                return new ScreenTranslationSelfTestResult(false, report.ToString());
            }
        }

        private static Bitmap CreateOcrSample()
        {
            Bitmap sample = new Bitmap(1000, 180, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(sample))
            using (Font font = new Font("Arial", 58F, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.White);
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                graphics.DrawString("HELLO SCREEN TRANSLATION 2026", font, Brushes.Black, 24F, 45F);
            }
            return sample;
        }
    }

    internal sealed class ScreenTranslationSelfTestResult
    {
        public readonly bool Success;
        public readonly string Report;

        public ScreenTranslationSelfTestResult(bool success, string report)
        {
            Success = success;
            Report = report ?? "";
        }
    }

    internal sealed class FakeOpenAiTranslationServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly bool rejectFirstResponseFormat;
        private readonly bool invalidFirstPayload;
        private bool disposed;

        public string ApiBaseUrl { get; private set; }
        public int RequestCount { get; private set; }

        public FakeOpenAiTranslationServer()
            : this(true, false)
        {
        }

        public FakeOpenAiTranslationServer(bool rejectFirstResponseFormat, bool invalidFirstPayload)
        {
            this.rejectFirstResponseFormat = rejectFirstResponseFormat;
            this.invalidFirstPayload = invalidFirstPayload;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            ApiBaseUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/v1";
        }

        public async Task ServeAsync()
        {
            while (RequestCount < 2)
            {
                using (TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                using (NetworkStream stream = client.GetStream())
                {
                    string request = await ReadRequestAsync(stream).ConfigureAwait(false);
                    RequestCount++;
                    bool rejectResponseFormat = rejectFirstResponseFormat && RequestCount == 1 &&
                        request.IndexOf("response_format", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool invalidPayload = invalidFirstPayload && RequestCount == 1 && !rejectResponseFormat;
                    await WriteResponseAsync(stream, rejectResponseFormat, invalidPayload).ConfigureAwait(false);
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            listener.Stop();
        }

        private static async Task<string> ReadRequestAsync(NetworkStream stream)
        {
            List<byte> bytes = new List<byte>();
            byte[] one = new byte[1];
            while (bytes.Count < 65536)
            {
                int read = await stream.ReadAsync(one, 0, 1).ConfigureAwait(false);
                if (read == 0) break;
                bytes.Add(one[0]);
                int count = bytes.Count;
                if (count >= 4 && bytes[count - 4] == 13 && bytes[count - 3] == 10 && bytes[count - 2] == 13 && bytes[count - 1] == 10)
                {
                    break;
                }
            }

            string headers = Encoding.ASCII.GetString(bytes.ToArray());
            int contentLength = 0;
            Match match = Regex.Match(headers, @"(?im)^Content-Length:\s*(\d+)\s*$");
            if (match.Success)
            {
                Int32.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength);
            }

            byte[] body = new byte[Math.Max(0, contentLength)];
            int offset = 0;
            while (offset < body.Length)
            {
                int read = await stream.ReadAsync(body, offset, body.Length - offset).ConfigureAwait(false);
                if (read == 0) break;
                offset += read;
            }
            return headers + Encoding.UTF8.GetString(body, 0, offset);
        }

        private static async Task WriteResponseAsync(
            NetworkStream stream,
            bool rejectResponseFormat,
            bool invalidPayload)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            int statusCode;
            string statusText;
            string payload;
            if (rejectResponseFormat)
            {
                statusCode = 400;
                statusText = "Bad Request";
                payload = serializer.Serialize(new Dictionary<string, object> { { "error", "response_format unsupported" } });
            }
            else
            {
                statusCode = 200;
                statusText = "OK";
                string content = invalidPayload
                    ? "not valid json"
                    : "{\"translations\":[{\"id\":\"seg_1\",\"text\":\"你好，\n世界\",},],}";
                payload = serializer.Serialize(new Dictionary<string, object>
                {
                    { "choices", new object[] { new Dictionary<string, object>
                        {
                            { "message", new Dictionary<string, object> { { "content", content } } }
                        }
                    } }
                });
            }

            byte[] body = Encoding.UTF8.GetBytes(payload);
            string headers = "HTTP/1.1 " + statusCode.ToString(CultureInfo.InvariantCulture) + " " + statusText + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "Connection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
    }
}
