using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Details;
using Sdcb.PaddleOCR.Models.Local;

namespace NoNoStandalone
{
    internal sealed class ClipboardOcrResult
    {
        public string Text = "";
        public string Engine = "";
        public string Version = ClipboardImageOcrService.CurrentVersion;
        public string Error = "";
        public int BlockCount;
        public float AverageConfidence;

        public bool Success
        {
            get { return !String.IsNullOrWhiteSpace(Text); }
        }
    }

    internal sealed class ClipboardOcrBlock
    {
        public string Text;
        public float Confidence;
        public Rectangle Bounds;

        public ClipboardOcrBlock Clone()
        {
            return (ClipboardOcrBlock)MemberwiseClone();
        }
    }

    internal static class ClipboardImageOcrService
    {
        internal const string CurrentVersion = "ppocrv5-server-english-ensemble-v2";

        private const int TileSize = 1600;
        private const int TileOverlap = 160;
        private const float MinimumConfidence = 0.58F;
        private const float RetryConfidence = 0.86F;
        private const int MaxRegionRetries = 24;
        private static readonly object Sync = new object();
        private static PaddleOcrAll serverEngine;
        private static PaddleOcrAll mobileEngine;
        private static PaddleOcrRecognizer technicalRecognizer;
        private static Exception serverInitializationError;
        private static readonly StringBuilder technicalDiagnostics = new StringBuilder();
        private static bool disposed;

        internal static string LastTechnicalDiagnostics
        {
            get { return technicalDiagnostics.ToString(); }
        }

        public static ClipboardOcrResult Recognize(Image image)
        {
            if (image == null)
            {
                return Failed("图片不可用。");
            }

            try
            {
                using (Bitmap normalized = NormalizeImage(image))
                {
                    return RecognizeNormalized(normalized);
                }
            }
            catch (Exception ex)
            {
                ProgramLog.Write("Clipboard OCR", ex);
                return Failed(SafeError(ex));
            }
        }

        public static ClipboardOcrResult RecognizeFile(string imagePath)
        {
            if (String.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                return Failed("图片文件不可用。");
            }

            try
            {
                using (Image image = Image.FromFile(imagePath))
                {
                    return Recognize(image);
                }
            }
            catch (Exception ex)
            {
                ProgramLog.Write("Clipboard OCR file", ex);
                return Failed(SafeError(ex));
            }
        }

        public static Task WarmUpAsync()
        {
            return Task.Run(delegate
            {
                lock (Sync)
                {
                    EnsureServerEngine();
                }
            });
        }

        public static void Dispose()
        {
            lock (Sync)
            {
                disposed = true;
                if (serverEngine != null)
                {
                    serverEngine.Dispose();
                    serverEngine = null;
                }
                if (mobileEngine != null)
                {
                    mobileEngine.Dispose();
                    mobileEngine = null;
                }
                if (technicalRecognizer != null)
                {
                    technicalRecognizer.Dispose();
                    technicalRecognizer = null;
                }
            }
        }

        private static ClipboardOcrResult RecognizeNormalized(Bitmap image)
        {
            lock (Sync)
            {
                technicalDiagnostics.Clear();
                if (disposed)
                {
                    return Failed("OCR 服务已经停止。");
                }

                Exception serverFailure = null;
                try
                {
                    PaddleOcrAll engine = EnsureServerEngine();
                    List<ClipboardOcrBlock> blocks = RunHighAccuracy(engine, image);
                    if (blocks.Count > 0)
                    {
                        return BuildResult(blocks, "PP-OCRv5 Server");
                    }
                }
                catch (Exception ex)
                {
                    serverFailure = ex;
                    ProgramLog.Write("Clipboard OCR server model", ex);
                }

                try
                {
                    PaddleOcrAll engine = EnsureMobileEngine();
                    List<ClipboardOcrBlock> blocks = RunHighAccuracy(engine, image);
                    if (blocks.Count > 0)
                    {
                        return BuildResult(blocks, "PP-OCRv5 Mobile fallback");
                    }
                }
                catch (Exception ex)
                {
                    ProgramLog.Write("Clipboard OCR mobile fallback", ex);
                }

                try
                {
                    List<ScreenTextBlock> windowsBlocks = WindowsOcrPowerShellFallback.Recognize(
                        image, System.Threading.CancellationToken.None);
                    List<ClipboardOcrBlock> blocks = windowsBlocks.Select(delegate(ScreenTextBlock block)
                    {
                        return new ClipboardOcrBlock
                        {
                            Text = block.Text,
                            Confidence = block.Confidence,
                            Bounds = block.Bounds
                        };
                    }).ToList();
                    if (blocks.Count > 0)
                    {
                        return BuildResult(blocks, "Windows OCR fallback");
                    }
                }
                catch (Exception ex)
                {
                    ProgramLog.Write("Clipboard OCR Windows fallback", ex);
                }

                return Failed(serverFailure == null ? "未识别到文字。" : SafeError(serverFailure));
            }
        }

        private static PaddleOcrAll EnsureServerEngine()
        {
            if (serverEngine != null)
            {
                return serverEngine;
            }
            if (serverInitializationError != null)
            {
                throw new InvalidOperationException("PP-OCRv5 Server 初始化失败。", serverInitializationError);
            }

            try
            {
                string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr-models");
                string detectionPath = Path.Combine(root, "PP-OCRv5_server_det_infer");
                string recognitionPath = Path.Combine(root, "PP-OCRv5_server_rec_infer");
                string dictionaryPath = Path.Combine(recognitionPath, "ppocrv5_server_dict.txt");
                EnsureModelFile(Path.Combine(detectionPath, "inference.json"));
                EnsureModelFile(Path.Combine(detectionPath, "inference.pdiparams"));
                EnsureModelFile(Path.Combine(recognitionPath, "inference.json"));
                EnsureModelFile(Path.Combine(recognitionPath, "inference.pdiparams"));
                EnsureModelFile(dictionaryPath);

                DetectionModel detection = new FileDetectionModel(detectionPath, ModelVersion.V5);
                RecognizationModel recognition = new FileRecognizationModel(
                    recognitionPath, dictionaryPath, ModelVersion.V5);
                FullOcrModel model = new FullOcrModel(
                    detection, LocalClassificationModel.ChineseMobileV2, recognition);
                serverEngine = CreateEngine(model, PaddleDevice.OneDnn());
                return serverEngine;
            }
            catch (Exception ex)
            {
                serverInitializationError = ex;
                throw;
            }
        }

        private static PaddleOcrAll EnsureMobileEngine()
        {
            if (mobileEngine == null)
            {
                mobileEngine = CreateEngine(LocalFullModels.ChineseV5, PaddleDevice.Onnx());
            }
            return mobileEngine;
        }

        private static PaddleOcrRecognizer EnsureTechnicalRecognizer()
        {
            if (technicalRecognizer == null)
            {
                string modelPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "ocr-models",
                    "en_PP-OCRv5_mobile_rec_infer");
                string dictionaryPath = Path.Combine(modelPath, "en_ppocrv5_dict.txt");
                EnsureModelFile(Path.Combine(modelPath, "inference.json"));
                EnsureModelFile(Path.Combine(modelPath, "inference.pdiparams"));
                EnsureModelFile(dictionaryPath);
                RecognizationModel model = new FileRecognizationModel(
                    modelPath, dictionaryPath, ModelVersion.V5);
                technicalRecognizer = new PaddleOcrRecognizer(model, PaddleDevice.OneDnn());
            }
            return technicalRecognizer;
        }

        private static PaddleOcrAll CreateEngine(
            FullOcrModel model, Action<PaddleConfig> device)
        {
            PaddleOcrAll engine = new PaddleOcrAll(model, device)
            {
                AllowRotateDetection = true,
                Enable180Classification = false
            };
            engine.Detector.MaxSize = null;
            return engine;
        }

        private static void EnsureModelFile(string path)
        {
            FileInfo file = new FileInfo(path);
            if (!file.Exists || file.Length == 0)
            {
                throw new FileNotFoundException("OCR 模型文件缺失。", path);
            }
        }

        private static List<ClipboardOcrBlock> RunHighAccuracy(PaddleOcrAll engine, Bitmap image)
        {
            byte[] encoded;
            using (MemoryStream stream = new MemoryStream())
            {
                image.Save(stream, ImageFormat.Png);
                encoded = stream.ToArray();
            }

            using (Mat source = Cv2.ImDecode(encoded, ImreadModes.Color))
            {
                List<ClipboardOcrBlock> blocks = new List<ClipboardOcrBlock>();
                List<Rectangle> tiles = BuildTiles(source.Width, source.Height);
                int retriesRemaining = MaxRegionRetries;
                for (int i = 0; i < tiles.Count; i++)
                {
                    Rectangle tileBounds = tiles[i];
                    using (Mat tile = new Mat(source, ToCvRect(tileBounds)).Clone())
                    {
                        double scale = ChooseScale(tile.Width, tile.Height);
                        using (Mat prepared = Resize(tile, scale))
                        {
                            List<ClipboardOcrBlock> tileBlocks = RunPass(
                                engine, prepared, tileBounds.Location, scale, true, ref retriesRemaining);
                            blocks.AddRange(tileBlocks);

                            float average = tileBlocks.Count == 0
                                ? 0F
                                : tileBlocks.Average(x => x.Confidence);
                            if (tileBlocks.Count == 0 || average < 0.80F)
                            {
                                using (Mat enhanced = Enhance(prepared))
                                {
                                    blocks.AddRange(RunPass(
                                        engine, enhanced, tileBounds.Location, scale, false, ref retriesRemaining));
                                }
                            }
                        }
                    }
                }

                return Deduplicate(blocks)
                    .Where(x => x.Confidence >= MinimumConfidence && IsUsableText(x.Text))
                    .ToList();
            }
        }

        private static List<ClipboardOcrBlock> RunPass(
            PaddleOcrAll engine,
            Mat image,
            System.Drawing.Point tileOffset,
            double scale,
            bool retryWeakRegions,
            ref int retriesRemaining)
        {
            PaddleOcrResult result = engine.Run(image, 8);
            List<ClipboardOcrBlock> blocks = new List<ClipboardOcrBlock>();
            for (int i = 0; i < result.Regions.Length; i++)
            {
                PaddleOcrResultRegion region = result.Regions[i];
                string text = NormalizeText(region.Text);
                float confidence = region.Score;
                OpenCvSharp.Rect raw = region.Rect.BoundingRect();
                bool technicalText = IsTechnicalText(text);

                if (retryWeakRegions && retriesRemaining > 0 &&
                    (confidence < RetryConfidence || raw.Height < 24 * scale || technicalText))
                {
                    PaddleOcrRecognizerResult retry = RetryRegion(
                        engine, image, region.Rect, technicalText);
                    retriesRemaining--;
                    if (IsBetterRecognition(
                        text, confidence, retry.Text, retry.Score, technicalText))
                    {
                        text = NormalizeText(retry.Text);
                        confidence = retry.Score;
                    }
                }

                Rectangle bounds = new Rectangle(
                    tileOffset.X + (int)Math.Floor(raw.X / scale),
                    tileOffset.Y + (int)Math.Floor(raw.Y / scale),
                    Math.Max(1, (int)Math.Ceiling(raw.Width / scale)),
                    Math.Max(1, (int)Math.Ceiling(raw.Height / scale)));
                if (IsUsableText(text))
                {
                    blocks.Add(new ClipboardOcrBlock
                    {
                        Text = text,
                        Confidence = confidence,
                        Bounds = bounds
                    });
                }
            }
            return blocks;
        }

        private static PaddleOcrRecognizerResult RetryRegion(
            PaddleOcrAll engine, Mat source, RotatedRect rectangle, bool technicalText)
        {
            RotatedRect cropRectangle = technicalText
                ? ExpandRecognitionRectangle(rectangle, source.Width, source.Height)
                : rectangle;
            using (Mat crop = PaddleOcrAll.GetRotateCropImage(source, cropRectangle))
            {
                if (engine.Enable180Classification &&
                    engine.Classifier != null && engine.Classifier.ShouldRotate180(crop))
                {
                    Cv2.Rotate(crop, crop, RotateFlags.Rotate180);
                }

                double scale = crop.Height < 32 ? 3D : 2D;
                using (Mat enlarged = Resize(crop, scale))
                using (Mat enhanced = Enhance(crop))
                using (Mat enlargedEnhanced = Resize(enhanced, scale))
                {
                    PaddleOcrRecognizerResult best = engine.Recognizer.Run(enlarged);
                    PaddleOcrRecognizerResult enhancedResult = engine.Recognizer.Run(enlargedEnhanced);
                    if (technicalText)
                    {
                        AppendTechnicalDiagnostic("server raw", best);
                        AppendTechnicalDiagnostic("server enhanced", enhancedResult);
                    }
                    if (IsBetterRecognition(
                        best.Text, best.Score, enhancedResult.Text, enhancedResult.Score, technicalText))
                    {
                        best = enhancedResult;
                    }
                    if (technicalText && ReferenceEquals(engine, serverEngine))
                    {
                        PaddleOcrRecognizerResult specialized =
                            EnsureTechnicalRecognizer().Run(enlarged);
                        AppendTechnicalDiagnostic("english raw", specialized);
                        if (IsBetterRecognition(
                            best.Text, best.Score, specialized.Text, specialized.Score, true))
                        {
                            best = specialized;
                        }
                        PaddleOcrRecognizerResult specializedEnhanced =
                            EnsureTechnicalRecognizer().Run(enlargedEnhanced);
                        AppendTechnicalDiagnostic("english enhanced", specializedEnhanced);
                        if (IsBetterRecognition(
                            best.Text, best.Score,
                            specializedEnhanced.Text, specializedEnhanced.Score, true))
                        {
                            best = specializedEnhanced;
                        }
                    }
                    return best;
                }
            }
        }

        private static void AppendTechnicalDiagnostic(
            string label, PaddleOcrRecognizerResult result)
        {
            technicalDiagnostics.Append(label);
            technicalDiagnostics.Append(": ");
            technicalDiagnostics.Append(NormalizeText(result.Text));
            technicalDiagnostics.Append(" (");
            technicalDiagnostics.Append(result.Score.ToString("0.000", CultureInfo.InvariantCulture));
            technicalDiagnostics.AppendLine(")");
        }

        private static RotatedRect ExpandRecognitionRectangle(
            RotatedRect rectangle, int imageWidth, int imageHeight)
        {
            double radians = rectangle.Angle * Math.PI / 180D;
            bool widthIsShortSide = rectangle.Size.Width <= rectangle.Size.Height;
            float shortSide = widthIsShortSide ? rectangle.Size.Width : rectangle.Size.Height;
            float width = rectangle.Size.Width * (widthIsShortSide ? 2.20F : 1.04F);
            float height = rectangle.Size.Height * (widthIsShortSide ? 1.04F : 2.20F);
            width = Math.Min(imageWidth, Math.Max(1F, width));
            height = Math.Min(imageHeight, Math.Max(1F, height));

            float axisX = widthIsShortSide
                ? (float)Math.Cos(radians)
                : -(float)Math.Sin(radians);
            float axisY = widthIsShortSide
                ? (float)Math.Sin(radians)
                : (float)Math.Cos(radians);
            if (axisY < 0F)
            {
                axisX = -axisX;
                axisY = -axisY;
            }
            float offset = shortSide * 0.30F;
            Point2f center = new Point2f(
                rectangle.Center.X + axisX * offset,
                rectangle.Center.Y + axisY * offset);
            return new RotatedRect(center, new Size2f(width, height), rectangle.Angle);
        }

        private static bool IsBetterRecognition(
            string currentText,
            float currentScore,
            string candidateText,
            float candidateScore,
            bool technicalText)
        {
            candidateText = NormalizeText(candidateText);
            if (!IsUsableText(candidateText))
            {
                return false;
            }
            if (!IsUsableText(currentText))
            {
                return true;
            }
            if (technicalText &&
                String.Equals(
                    TechnicalSkeleton(candidateText),
                    TechnicalSkeleton(currentText),
                    StringComparison.OrdinalIgnoreCase) &&
                candidateScore >= currentScore - 0.12F &&
                TechnicalPunctuationScore(candidateText) > TechnicalPunctuationScore(currentText))
            {
                return true;
            }
            return candidateScore >= currentScore + 0.015F;
        }

        private static string TechnicalSkeleton(string text)
        {
            if (String.IsNullOrEmpty(text))
            {
                return "";
            }
            StringBuilder result = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (IsLatinOrDigit(text[i]))
                {
                    result.Append(text[i]);
                }
            }
            return result.ToString();
        }

        private static bool IsTechnicalText(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            int ascii = 0;
            int lettersOrDigits = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char value = text[i];
                if (value >= 32 && value <= 126) ascii++;
                if (IsLatinOrDigit(value)) lettersOrDigits++;
            }
            return lettersOrDigits > 0 && ascii >= Math.Ceiling(text.Length * 0.80D) &&
                (text.Any(Char.IsDigit) || text.Any(Char.IsWhiteSpace) || TechnicalPunctuationScore(text) > 0);
        }

        private static int TechnicalPunctuationScore(string text)
        {
            if (String.IsNullOrEmpty(text))
            {
                return 0;
            }
            int score = 0;
            const string punctuation = "_-/:.\\@#=+%&?";
            for (int i = 0; i < text.Length; i++)
            {
                if (punctuation.IndexOf(text[i]) >= 0)
                {
                    score++;
                }
            }
            return score;
        }

        private static Mat Enhance(Mat source)
        {
            Mat gray = new Mat();
            Mat equalized = new Mat();
            Mat result = new Mat();
            try
            {
                Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
                using (CLAHE clahe = Cv2.CreateCLAHE(2.0, new OpenCvSharp.Size(8, 8)))
                {
                    clahe.Apply(gray, equalized);
                }
                Cv2.CvtColor(equalized, result, ColorConversionCodes.GRAY2BGR);
                return result;
            }
            finally
            {
                gray.Dispose();
                equalized.Dispose();
            }
        }

        private static Mat Resize(Mat source, double scale)
        {
            if (Math.Abs(scale - 1D) < 0.01D)
            {
                return source.Clone();
            }
            Mat result = new Mat();
            Cv2.Resize(source, result, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Cubic);
            return result;
        }

        private static double ChooseScale(int width, int height)
        {
            int longest = Math.Max(width, height);
            int shortest = Math.Min(width, height);
            if (longest <= 900)
            {
                return 2D;
            }
            if (longest <= 1400 && shortest <= 500)
            {
                return 1.5D;
            }
            return 1D;
        }

        private static List<Rectangle> BuildTiles(int width, int height)
        {
            List<int> x = BuildTileStarts(width);
            List<int> y = BuildTileStarts(height);
            List<Rectangle> result = new List<Rectangle>();
            for (int row = 0; row < y.Count; row++)
            {
                for (int column = 0; column < x.Count; column++)
                {
                    result.Add(new Rectangle(
                        x[column], y[row],
                        Math.Min(TileSize, width - x[column]),
                        Math.Min(TileSize, height - y[row])));
                }
            }
            return result;
        }

        private static List<int> BuildTileStarts(int length)
        {
            if (length <= TileSize)
            {
                return new List<int> { 0 };
            }

            List<int> starts = new List<int>();
            int step = TileSize - TileOverlap;
            for (int value = 0; value < length; value += step)
            {
                int start = Math.Min(value, length - TileSize);
                if (starts.Count == 0 || starts[starts.Count - 1] != start)
                {
                    starts.Add(start);
                }
                if (start + TileSize >= length)
                {
                    break;
                }
            }
            return starts;
        }

        private static List<ClipboardOcrBlock> Deduplicate(List<ClipboardOcrBlock> blocks)
        {
            List<ClipboardOcrBlock> accepted = new List<ClipboardOcrBlock>();
            foreach (ClipboardOcrBlock candidate in blocks.OrderByDescending(x => x.Confidence))
            {
                int duplicate = -1;
                for (int i = 0; i < accepted.Count; i++)
                {
                    if (OverlapOverSmaller(candidate.Bounds, accepted[i].Bounds) >= 0.62D)
                    {
                        duplicate = i;
                        break;
                    }
                }
                if (duplicate < 0)
                {
                    accepted.Add(candidate.Clone());
                }
            }
            return accepted;
        }

        private static double OverlapOverSmaller(Rectangle left, Rectangle right)
        {
            Rectangle intersection = Rectangle.Intersect(left, right);
            if (intersection.IsEmpty)
            {
                return 0D;
            }
            double smaller = Math.Min((double)left.Width * left.Height, (double)right.Width * right.Height);
            return smaller <= 0D ? 0D : intersection.Width * intersection.Height / smaller;
        }

        private static ClipboardOcrResult BuildResult(List<ClipboardOcrBlock> blocks, string engine)
        {
            List<ClipboardOcrBlock> ordered = OrderForReading(blocks);
            return new ClipboardOcrResult
            {
                Text = RebuildText(ordered),
                Engine = engine,
                Version = CurrentVersion,
                BlockCount = ordered.Count,
                AverageConfidence = ordered.Count == 0 ? 0F : ordered.Average(x => x.Confidence)
            };
        }

        private static List<ClipboardOcrBlock> OrderForReading(List<ClipboardOcrBlock> blocks)
        {
            List<List<ClipboardOcrBlock>> lines = new List<List<ClipboardOcrBlock>>();
            foreach (ClipboardOcrBlock block in blocks.OrderBy(x => x.Bounds.Top).ThenBy(x => x.Bounds.Left))
            {
                List<ClipboardOcrBlock> best = null;
                double bestScore = 0D;
                for (int i = 0; i < lines.Count; i++)
                {
                    Rectangle lineBounds = Union(lines[i].Select(x => x.Bounds));
                    int overlap = Math.Max(0, Math.Min(lineBounds.Bottom, block.Bounds.Bottom) -
                        Math.Max(lineBounds.Top, block.Bounds.Top));
                    double score = overlap / (double)Math.Max(1, Math.Min(lineBounds.Height, block.Bounds.Height));
                    if (score > bestScore && score >= 0.45D)
                    {
                        best = lines[i];
                        bestScore = score;
                    }
                }
                if (best == null)
                {
                    best = new List<ClipboardOcrBlock>();
                    lines.Add(best);
                }
                best.Add(block);
            }

            return lines
                .OrderBy(line => line.Min(x => x.Bounds.Top))
                .SelectMany(line => line.OrderBy(x => x.Bounds.Left))
                .ToList();
        }

        private static string RebuildText(List<ClipboardOcrBlock> ordered)
        {
            if (ordered.Count == 0)
            {
                return "";
            }

            StringBuilder result = new StringBuilder();
            ClipboardOcrBlock previous = null;
            for (int i = 0; i < ordered.Count; i++)
            {
                ClipboardOcrBlock current = ordered[i];
                if (previous != null)
                {
                    int overlap = Math.Max(0, Math.Min(previous.Bounds.Bottom, current.Bounds.Bottom) -
                        Math.Max(previous.Bounds.Top, current.Bounds.Top));
                    double verticalOverlap = overlap /
                        (double)Math.Max(1, Math.Min(previous.Bounds.Height, current.Bounds.Height));
                    if (verticalOverlap >= 0.45D)
                    {
                        int gap = current.Bounds.Left - previous.Bounds.Right;
                        if (gap > Math.Max(previous.Bounds.Height, current.Bounds.Height) * 2)
                        {
                            result.Append('\t');
                        }
                        else if (NeedsSpace(previous.Text, current.Text))
                        {
                            result.Append(' ');
                        }
                    }
                    else
                    {
                        result.AppendLine();
                    }
                }
                result.Append(current.Text);
                previous = current;
            }
            return result.ToString().Trim();
        }

        private static bool NeedsSpace(string left, string right)
        {
            if (String.IsNullOrEmpty(left) || String.IsNullOrEmpty(right))
            {
                return false;
            }
            char last = left[left.Length - 1];
            char first = right[0];
            return IsLatinOrDigit(last) && IsLatinOrDigit(first);
        }

        private static bool IsLatinOrDigit(char value)
        {
            return (value >= '0' && value <= '9') ||
                (value >= 'A' && value <= 'Z') ||
                (value >= 'a' && value <= 'z');
        }

        private static Rectangle Union(IEnumerable<Rectangle> rectangles)
        {
            bool first = true;
            Rectangle result = Rectangle.Empty;
            foreach (Rectangle rectangle in rectangles)
            {
                result = first ? rectangle : Rectangle.Union(result, rectangle);
                first = false;
            }
            return result;
        }

        private static bool IsUsableText(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            for (int i = 0; i < text.Length; i++)
            {
                if (Char.IsControl(text[i]) && text[i] != '\t')
                {
                    return false;
                }
            }
            return true;
        }

        private static string NormalizeText(string text)
        {
            return String.IsNullOrWhiteSpace(text)
                ? ""
                : text.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static Bitmap NormalizeImage(Image source)
        {
            using (Bitmap oriented = new Bitmap(source))
            {
                ApplyExifOrientation(oriented, source);
                Color background = ChooseAlphaBackground(oriented);
                Bitmap result = new Bitmap(oriented.Width, oriented.Height, PixelFormat.Format24bppRgb);
                result.SetResolution(
                    Math.Max(1F, oriented.HorizontalResolution),
                    Math.Max(1F, oriented.VerticalResolution));
                using (Graphics graphics = Graphics.FromImage(result))
                {
                    graphics.Clear(background);
                    graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(oriented, new Rectangle(0, 0, result.Width, result.Height));
                }
                return result;
            }
        }

        private static void ApplyExifOrientation(Bitmap image, Image source)
        {
            try
            {
                const int OrientationId = 0x0112;
                if (!source.PropertyIdList.Contains(OrientationId))
                {
                    return;
                }
                byte orientation = source.GetPropertyItem(OrientationId).Value[0];
                RotateFlipType transform = RotateFlipType.RotateNoneFlipNone;
                if (orientation == 2) transform = RotateFlipType.RotateNoneFlipX;
                else if (orientation == 3) transform = RotateFlipType.Rotate180FlipNone;
                else if (orientation == 4) transform = RotateFlipType.Rotate180FlipX;
                else if (orientation == 5) transform = RotateFlipType.Rotate90FlipX;
                else if (orientation == 6) transform = RotateFlipType.Rotate90FlipNone;
                else if (orientation == 7) transform = RotateFlipType.Rotate270FlipX;
                else if (orientation == 8) transform = RotateFlipType.Rotate270FlipNone;
                image.RotateFlip(transform);
            }
            catch
            {
            }
        }

        private static Color ChooseAlphaBackground(Bitmap image)
        {
            long luminance = 0;
            int translucent = 0;
            int opaqueSamples = 0;
            int stepX = Math.Max(1, image.Width / 64);
            int stepY = Math.Max(1, image.Height / 64);
            for (int y = 0; y < image.Height; y += stepY)
            {
                for (int x = 0; x < image.Width; x += stepX)
                {
                    Color pixel = image.GetPixel(x, y);
                    if (pixel.A < 245)
                    {
                        translucent++;
                    }
                    if (pixel.A > 32)
                    {
                        luminance += pixel.R * 299L + pixel.G * 587L + pixel.B * 114L;
                        opaqueSamples++;
                    }
                }
            }
            if (translucent == 0 || opaqueSamples == 0)
            {
                return Color.White;
            }
            double average = luminance / (opaqueSamples * 1000D);
            return average >= 160D ? Color.Black : Color.White;
        }

        private static OpenCvSharp.Rect ToCvRect(Rectangle rectangle)
        {
            return new OpenCvSharp.Rect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
        }

        private static ClipboardOcrResult Failed(string error)
        {
            return new ClipboardOcrResult
            {
                Version = CurrentVersion,
                Error = String.IsNullOrWhiteSpace(error) ? "未识别到文字。" : error.Trim()
            };
        }

        private static string SafeError(Exception exception)
        {
            Exception current = exception;
            while (current.InnerException != null)
            {
                current = current.InnerException;
            }
            string message = current.Message;
            return String.IsNullOrWhiteSpace(message) ? "OCR 不可用。" : message.Trim();
        }
    }

    internal static class ClipboardOcrSelfTest
    {
        public static int Run(string outputPath)
        {
            Stopwatch coldStopwatch = Stopwatch.StartNew();
            Stopwatch warmStopwatch;
            ClipboardOcrResult coldResult;
            ClipboardOcrResult result;
            using (Bitmap sample = new Bitmap(1080, 260, PixelFormat.Format24bppRgb))
            using (Graphics graphics = Graphics.FromImage(sample))
            using (Font title = new Font("Microsoft YaHei UI", 36F, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Font body = new Font("Consolas", 30F, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.White);
                graphics.DrawString("剪贴板高精度文字识别", title, Brushes.Black, 28F, 34F);
                graphics.DrawString("NoNo OCR 2026  API_KEY=AB12-90", body, Brushes.Black, 28F, 130F);
                coldResult = ClipboardImageOcrService.Recognize(sample);
                coldStopwatch.Stop();
                warmStopwatch = Stopwatch.StartNew();
                result = ClipboardImageOcrService.Recognize(sample);
                warmStopwatch.Stop();
            }

            bool success = coldResult.Success && result.Success &&
                coldResult.Engine == "PP-OCRv5 Server" &&
                result.Engine == "PP-OCRv5 Server" &&
                result.Text.IndexOf("剪贴板", StringComparison.Ordinal) >= 0 &&
                result.Text.IndexOf("NoNo", StringComparison.OrdinalIgnoreCase) >= 0 &&
                result.Text.IndexOf("2026", StringComparison.Ordinal) >= 0 &&
                result.Text.IndexOf("API_KEY=AB12-90", StringComparison.Ordinal) >= 0;
            StringBuilder report = new StringBuilder();
            report.AppendLine("Clipboard OCR self-test: " + (success ? "PASS" : "FAIL"));
            report.AppendLine("Engine: " + result.Engine);
            report.AppendLine("Version: " + result.Version);
            report.AppendLine("Blocks: " + result.BlockCount.ToString(CultureInfo.InvariantCulture));
            report.AppendLine("Average confidence: " + result.AverageConfidence.ToString("0.000", CultureInfo.InvariantCulture));
            report.AppendLine("Cold elapsed ms: " + coldStopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
            report.AppendLine("Warm elapsed ms: " + warmStopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
            report.AppendLine("Text: " + result.Text.Replace("\r", " ").Replace("\n", " | "));
            if (!String.IsNullOrWhiteSpace(ClipboardImageOcrService.LastTechnicalDiagnostics))
            {
                report.AppendLine("Technical candidates:");
                report.Append(ClipboardImageOcrService.LastTechnicalDiagnostics);
            }
            if (!String.IsNullOrWhiteSpace(result.Error))
            {
                report.AppendLine("Error: " + result.Error);
            }
            File.WriteAllText(outputPath, report.ToString(), new UTF8Encoding(false));
            return success ? 0 : 11;
        }
    }
}
