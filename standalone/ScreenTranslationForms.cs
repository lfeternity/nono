using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace NoNoStandalone
{
    internal sealed class RegionSelectionForm : Form
    {
        private const int WsExToolWindow = 0x00000080;
        private readonly Bitmap screenshot;
        private bool selecting;
        private Point startPoint;
        private Point currentPoint;

        public Rectangle SelectedRegion { get; private set; }

        public RegionSelectionForm(Bitmap screenshot, Rectangle screenBounds)
        {
            if (screenshot == null) throw new ArgumentNullException("screenshot");
            this.screenshot = screenshot;
            Text = "框选翻译区域";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = screenBounds;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;
            BackColor = Color.Black;
            SelectedRegion = Rectangle.Empty;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WsExToolWindow;
                return cp;
            }
        }

        public void CancelSelection()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(CancelSelection));
                return;
            }
            DialogResult = DialogResult.Cancel;
            Close();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            Focus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                CancelSelection();
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right)
            {
                CancelSelection();
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            selecting = true;
            startPoint = Clamp(e.Location);
            currentPoint = startPoint;
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!selecting) return;
            currentPoint = Clamp(e.Location);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!selecting || e.Button != MouseButtons.Left) return;
            selecting = false;
            Capture = false;
            currentPoint = Clamp(e.Location);
            Rectangle selected = Normalize(startPoint, currentPoint);
            if (selected.Width < 12 || selected.Height < 12)
            {
                SelectedRegion = Rectangle.Empty;
                Invalidate();
                return;
            }

            SelectedRegion = selected;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.CompositingMode = CompositingMode.SourceCopy;
            e.Graphics.DrawImage(screenshot, ClientRectangle, new Rectangle(Point.Empty, screenshot.Size), GraphicsUnit.Pixel);
            e.Graphics.CompositingMode = CompositingMode.SourceOver;

            Rectangle selection = selecting ? Normalize(startPoint, currentPoint) : SelectedRegion;
            using (Region dimmed = new Region(ClientRectangle))
            {
                if (!selection.IsEmpty) dimmed.Exclude(selection);
                using (Brush shade = new SolidBrush(Color.FromArgb(118, 0, 0, 0)))
                {
                    e.Graphics.FillRegion(shade, dimmed);
                }
            }

            if (!selection.IsEmpty)
            {
                using (Pen outer = new Pen(Color.FromArgb(240, 255, 255, 255), 2F))
                using (Pen inner = new Pen(Color.FromArgb(255, 30, 156, 214), 1F))
                {
                    Rectangle border = selection;
                    border.Width = Math.Max(1, border.Width - 1);
                    border.Height = Math.Max(1, border.Height - 1);
                    e.Graphics.DrawRectangle(outer, border);
                    border.Inflate(-2, -2);
                    if (border.Width > 0 && border.Height > 0) e.Graphics.DrawRectangle(inner, border);
                }

                string dimensions = selection.Width + " x " + selection.Height;
                Size labelSize = TextRenderer.MeasureText(dimensions, Font, Size.Empty, TextFormatFlags.NoPadding);
                Rectangle label = new Rectangle(
                    selection.Left,
                    selection.Top > labelSize.Height + 10 ? selection.Top - labelSize.Height - 8 : selection.Top + 6,
                    labelSize.Width + 12,
                    labelSize.Height + 6);
                using (Brush background = new SolidBrush(Color.FromArgb(225, 25, 28, 32)))
                {
                    e.Graphics.FillRectangle(background, label);
                }
                TextRenderer.DrawText(e.Graphics, dimensions, Font, label, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        private Point Clamp(Point point)
        {
            return new Point(
                Math.Max(0, Math.Min(ClientSize.Width, point.X)),
                Math.Max(0, Math.Min(ClientSize.Height, point.Y)));
        }

        internal static Rectangle Normalize(Point first, Point second)
        {
            int left = Math.Min(first.X, second.X);
            int top = Math.Min(first.Y, second.Y);
            int right = Math.Max(first.X, second.X);
            int bottom = Math.Max(first.Y, second.Y);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }
    }

    internal sealed class TranslationOverlaySession : IDisposable
    {
        private readonly TranslationOverlayForm overlay;
        private readonly TranslationToolbarForm toolbar;
        private readonly string translatedText;
        private bool disposed;
        private bool closedRaised;

        public event EventHandler Closed;

        public TranslationOverlaySession(
            Rectangle screenBounds,
            Rectangle absoluteSelection,
            Bitmap screenshot,
            IList<ScreenTextBlock> blocks)
        {
            overlay = new TranslationOverlayForm(screenBounds, absoluteSelection, screenshot, blocks);
            toolbar = new TranslationToolbarForm(absoluteSelection);
            translatedText = String.Join(Environment.NewLine, blocks
                .Where(x => !String.IsNullOrWhiteSpace(x.Translation))
                .OrderBy(x => x.Bounds.Top)
                .ThenBy(x => x.Bounds.Left)
                .Select(x => x.Translation));
            overlay.FormClosed += delegate { RaiseClosed(); };
            toolbar.CloseRequested += delegate { RaiseClosed(); };
            toolbar.CopyRequested += CopyTranslation;
        }

        public void Show()
        {
            overlay.Show();
            toolbar.Show();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (!toolbar.IsDisposed) toolbar.Close();
            if (!overlay.IsDisposed) overlay.Close();
            toolbar.Dispose();
            overlay.Dispose();
        }

        private void CopyTranslation(object sender, EventArgs e)
        {
            if (String.IsNullOrWhiteSpace(translatedText)) return;
            try
            {
                Clipboard.SetText(translatedText, TextDataFormat.UnicodeText);
            }
            catch (Exception ex)
            {
                MessageBox.Show(toolbar, ex.Message, "复制译文失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RaiseClosed()
        {
            if (closedRaised) return;
            closedRaised = true;
            EventHandler handler = Closed;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }

    internal sealed class TranslationOverlayForm : Form
    {
        private const int WsExToolWindow = 0x00000080;
        private const int WsExLayered = 0x00080000;
        private static readonly Color TransparentSurface = Color.FromArgb(1, 2, 3);
        private readonly List<OverlayTextLayout> layouts;
        private readonly ContextMenuStrip translationMenu;

        protected override bool ShowWithoutActivation { get { return true; } }

        public TranslationOverlayForm(
            Rectangle screenBounds,
            Rectangle absoluteSelection,
            Bitmap screenshot,
            IList<ScreenTextBlock> blocks)
        {
            Text = "区域翻译";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = screenBounds;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = TransparentSurface;
            TransparencyKey = TransparentSurface;
            DoubleBuffered = true;
            layouts = BuildLayouts(screenBounds, absoluteSelection, screenshot, blocks);
            translationMenu = CreateTranslationMenu();
            AddSelectableTranslationControls();
            ApplyInteractiveRegion();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WsExToolWindow | WsExLayered;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            for (int i = 0; i < layouts.Count; i++)
            {
                OverlayTextLayout item = layouts[i];
                using (Brush sourceMask = new SolidBrush(item.BackgroundColor))
                {
                    e.Graphics.FillRectangle(sourceMask, item.SourceBounds);
                }
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (translationMenu != null) translationMenu.Dispose();
                for (int i = 0; i < layouts.Count; i++) layouts[i].Font.Dispose();
            }
            base.Dispose(disposing);
        }

        private void AddSelectableTranslationControls()
        {
            for (int i = 0; i < layouts.Count; i++)
            {
                OverlayTextLayout item = layouts[i];
                RichTextBox text = new RichTextBox();
                text.ReadOnly = true;
                text.BorderStyle = BorderStyle.None;
                text.ScrollBars = RichTextBoxScrollBars.None;
                text.WordWrap = true;
                text.DetectUrls = false;
                text.ShortcutsEnabled = true;
                text.HideSelection = false;
                text.TabStop = true;
                text.Text = item.Text;
                text.Font = item.Font;
                text.BackColor = item.BackgroundColor;
                text.ForeColor = item.ForegroundColor;
                // The layout already accounts for the text control's padding. Keeping the
                // control in that exact rectangle prevents RichEdit from escaping a source box.
                text.Bounds = item.Bounds;
                text.ContextMenuStrip = translationMenu;
                Controls.Add(text);
            }
        }

        private void ApplyInteractiveRegion()
        {
            Region interactive = new Region();
            interactive.MakeEmpty();
            for (int i = 0; i < layouts.Count; i++)
            {
                interactive.Union(layouts[i].SourceBounds);
                interactive.Union(layouts[i].Bounds);
            }
            Region = interactive;
        }

        private static ContextMenuStrip CreateTranslationMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem copy = new ToolStripMenuItem("\u590d\u5236");
            ToolStripMenuItem selectAll = new ToolStripMenuItem("\u5168\u9009");
            copy.Click += delegate(object sender, EventArgs e)
            {
                RichTextBox source = menu.SourceControl as RichTextBox;
                if (source != null && source.SelectionLength > 0) source.Copy();
            };
            selectAll.Click += delegate(object sender, EventArgs e)
            {
                RichTextBox source = menu.SourceControl as RichTextBox;
                if (source != null) source.SelectAll();
            };
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                RichTextBox source = menu.SourceControl as RichTextBox;
                copy.Enabled = source != null && source.SelectionLength > 0;
                selectAll.Enabled = source != null && source.TextLength > 0;
            };
            menu.Items.Add(copy);
            menu.Items.Add(selectAll);
            return menu;
        }

        private static List<OverlayTextLayout> BuildLayouts(
            Rectangle screenBounds,
            Rectangle absoluteSelection,
            Bitmap screenshot,
            IList<ScreenTextBlock> blocks)
        {
            Rectangle selection = new Rectangle(
                absoluteSelection.Left - screenBounds.Left,
                absoluteSelection.Top - screenBounds.Top,
                absoluteSelection.Width,
                absoluteSelection.Height);
            List<OverlayTextLayout> result = new List<OverlayTextLayout>();
            foreach (ScreenTextBlock block in blocks
                .Where(x => !String.IsNullOrWhiteSpace(x.Translation))
                .OrderBy(x => x.Bounds.Top)
                .ThenBy(x => x.Bounds.Left))
            {
                Rectangle sourceBounds = Rectangle.Intersect(
                    selection,
                    new Rectangle(selection.Left + block.Bounds.Left, selection.Top + block.Bounds.Top,
                        Math.Max(1, block.Bounds.Width), Math.Max(1, block.Bounds.Height)));
                if (sourceBounds.Width < 3 || sourceBounds.Height < 3)
                {
                    continue;
                }

                string displayText = NormalizeTranslationForDisplay(block.Translation);
                if (displayText.Length == 0)
                {
                    continue;
                }

                Rectangle sourceMaskBounds = sourceBounds;
                sourceMaskBounds.Inflate(3, 3);
                sourceMaskBounds = Rectangle.Intersect(selection, sourceMaskBounds);
                // A translated paragraph must stay anchored to its source. The previous
                // collision search moved overlapping items to arbitrary free cells, which
                // made one sentence appear in several unrelated places. Fit the type in the
                // original source rectangle instead and let the control clip only at that edge.
                Rectangle box = sourceBounds;
                float preferredSize = Math.Max(9F, Math.Min(20F, sourceBounds.Height * 0.72F));
                Font font = CreateFittedFont(displayText, box, preferredSize);

                Color backgroundColor = SampleBackgroundColor(
                    screenshot,
                    sourceBounds,
                    selection);
                Color foregroundColor = ChooseForegroundColor(backgroundColor);

                result.Add(new OverlayTextLayout(
                    sourceMaskBounds,
                    box,
                    displayText,
                    font,
                    backgroundColor,
                    foregroundColor));
            }
            return result;
        }

        private static string NormalizeTranslationForDisplay(string value)
        {
            string text = (value ?? "").Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            StringBuilder compact = new StringBuilder(text.Length);
            bool previousWasSpace = false;
            for (int i = 0; i < text.Length; i++)
            {
                char character = text[i];
                if (Char.IsWhiteSpace(character))
                {
                    if (!previousWasSpace) compact.Append(' ');
                    previousWasSpace = true;
                    continue;
                }
                compact.Append(character);
                previousWasSpace = false;
            }
            return compact.ToString().Trim();
        }

        private static Font CreateFittedFont(string text, Rectangle bounds, float preferredSize)
        {
            float minimumSize = 5F;
            float maximumSize = Math.Max(minimumSize, preferredSize);
            for (float size = maximumSize; size >= minimumSize; size -= 0.5F)
            {
                Font candidate = new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Pixel);
                if (FitsText(text, candidate, bounds))
                {
                    return candidate;
                }
                candidate.Dispose();
            }

            return new Font("Microsoft YaHei UI", minimumSize, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private static bool FitsText(string text, Font font, Rectangle bounds)
        {
            int width = Math.Max(1, bounds.Width - 8);
            int height = Math.Max(1, bounds.Height - 4);
            Size measured = TextRenderer.MeasureText(
                text,
                font,
                new Size(width, Int32.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            return measured.Width <= width + 2 && measured.Height <= height + 2;
        }

        internal static bool RunLayoutSelfTest()
        {
            Rectangle selection = new Rectangle(10, 10, 520, 110);
            using (Bitmap screenshot = new Bitmap(560, 140))
            {
                using (Graphics graphics = Graphics.FromImage(screenshot)) graphics.Clear(Color.White);
                List<ScreenTextBlock> blocks = new List<ScreenTextBlock>
                {
                    new ScreenTextBlock
                    {
                        Translation = "现在毫无疑问：转换完全平坦（1025 个目录 / 353M，未变）并且 generateDebugBuildConfig 之后的一项操作占用了剩余的约 24 秒，且从未完成或建立检查点。",
                        Bounds = new Rectangle(24, 18, 480, 80)
                    }
                };
                List<OverlayTextLayout> layouts = BuildLayouts(
                    new Rectangle(0, 0, screenshot.Width, screenshot.Height),
                    selection,
                    screenshot,
                    blocks);
                try
                {
                    if (layouts.Count != 1 || !selection.Contains(layouts[0].Bounds)) return false;
                    if (!FitsText(layouts[0].Text, layouts[0].Font, layouts[0].Bounds)) return false;
                    return layouts[0].Bounds.Left == selection.Left + blocks[0].Bounds.Left &&
                        layouts[0].Bounds.Top == selection.Top + blocks[0].Bounds.Top;
                }
                finally
                {
                    for (int i = 0; i < layouts.Count; i++) layouts[i].Font.Dispose();
                }
            }
        }

        internal static Color SampleBackgroundColor(Bitmap screenshot, Rectangle textBounds, Rectangle selection)
        {
            Color fallback = Color.White;
            if (screenshot == null || screenshot.Width == 0 || screenshot.Height == 0)
            {
                return fallback;
            }

            Rectangle safeSelection = Rectangle.Intersect(
                new Rectangle(Point.Empty, screenshot.Size),
                selection);
            Rectangle target = Rectangle.Intersect(
                new Rectangle(Point.Empty, screenshot.Size),
                Rectangle.Intersect(safeSelection, textBounds));
            if (target.Width < 1 || target.Height < 1)
            {
                return SampleRegionMedian(screenshot, safeSelection, fallback);
            }

            int margin = Math.Max(2, Math.Min(8, Math.Max(target.Width, target.Height) / 3));
            List<Color> samples = new List<Color>();
            AddSampleStrip(screenshot, safeSelection, target, new Rectangle(
                target.Left - margin,
                target.Top - margin,
                target.Width + margin * 2,
                margin), samples);
            AddSampleStrip(screenshot, safeSelection, target, new Rectangle(
                target.Left - margin,
                target.Bottom,
                target.Width + margin * 2,
                margin), samples);
            AddSampleStrip(screenshot, safeSelection, target, new Rectangle(
                target.Left - margin,
                target.Top,
                margin,
                target.Height), samples);
            AddSampleStrip(screenshot, safeSelection, target, new Rectangle(
                target.Right,
                target.Top,
                margin,
                target.Height), samples);
            if (samples.Count < 8)
            {
                return SampleRegionMedian(screenshot, safeSelection, fallback);
            }

            samples.Sort(delegate(Color left, Color right)
            {
                int leftLuminance = left.R * 299 + left.G * 587 + left.B * 114;
                int rightLuminance = right.R * 299 + right.G * 587 + right.B * 114;
                return leftLuminance.CompareTo(rightLuminance);
            });
            return samples[samples.Count / 2];
        }

        private static void AddSampleStrip(
            Bitmap screenshot,
            Rectangle selection,
            Rectangle textBounds,
            Rectangle strip,
            List<Color> samples)
        {
            Rectangle safe = Rectangle.Intersect(selection, strip);
            safe = Rectangle.Intersect(safe, new Rectangle(Point.Empty, screenshot.Size));
            if (safe.Width < 1 || safe.Height < 1)
            {
                return;
            }

            int step = Math.Max(1, Math.Max(safe.Width, safe.Height) / 64);
            for (int y = safe.Top; y < safe.Bottom; y += step)
            {
                for (int x = safe.Left; x < safe.Right; x += step)
                {
                    if (textBounds.Contains(x, y))
                    {
                        continue;
                    }
                    samples.Add(screenshot.GetPixel(x, y));
                }
            }
        }

        private static Color SampleRegionMedian(Bitmap screenshot, Rectangle region, Color fallback)
        {
            Rectangle safe = Rectangle.Intersect(
                new Rectangle(Point.Empty, screenshot.Size),
                region);
            if (safe.Width < 1 || safe.Height < 1)
            {
                return fallback;
            }

            List<int> reds = new List<int>();
            List<int> greens = new List<int>();
            List<int> blues = new List<int>();
            int step = Math.Max(1, Math.Max(safe.Width, safe.Height) / 24);
            for (int y = safe.Top; y < safe.Bottom; y += step)
            {
                for (int x = safe.Left; x < safe.Right; x += step)
                {
                    Color sample = screenshot.GetPixel(x, y);
                    reds.Add(sample.R);
                    greens.Add(sample.G);
                    blues.Add(sample.B);
                }
            }
            if (reds.Count == 0)
            {
                return fallback;
            }

            reds.Sort();
            greens.Sort();
            blues.Sort();
            int middle = reds.Count / 2;
            return Color.FromArgb(reds[middle], greens[middle], blues[middle]);
        }

        private static Color ChooseForegroundColor(Color background)
        {
            double luminance = RelativeLuminance(background);
            double contrastWithBlack = (luminance + 0.05D) / 0.05D;
            double contrastWithWhite = 1.05D / (luminance + 0.05D);
            return contrastWithBlack >= contrastWithWhite ? Color.Black : Color.White;
        }

        private static double RelativeLuminance(Color color)
        {
            return 0.2126D * Linearize(color.R / 255D) +
                0.7152D * Linearize(color.G / 255D) +
                0.0722D * Linearize(color.B / 255D);
        }

        private static double Linearize(double channel)
        {
            return channel <= 0.03928D
                ? channel / 12.92D
                : Math.Pow((channel + 0.055D) / 1.055D, 2.4D);
        }

        private sealed class OverlayTextLayout
        {
            public readonly Rectangle SourceBounds;
            public readonly Rectangle Bounds;
            public readonly string Text;
            public readonly Font Font;
            public readonly Color BackgroundColor;
            public readonly Color ForegroundColor;

            public OverlayTextLayout(
                Rectangle sourceBounds,
                Rectangle bounds,
                string text,
                Font font,
                Color backgroundColor,
                Color foregroundColor)
            {
                SourceBounds = sourceBounds;
                Bounds = bounds;
                Text = text;
                Font = font;
                BackgroundColor = backgroundColor;
                ForegroundColor = foregroundColor;
            }
        }
    }

    internal sealed class TranslationToolbarForm : Form
    {
        private const int WsExToolWindow = 0x00000080;
        private readonly Button copyButton;
        private readonly Button closeButton;

        public event EventHandler CopyRequested;
        public event EventHandler CloseRequested;

        public TranslationToolbarForm(Rectangle selection)
        {
            Text = "区域翻译工具";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(76, 38);
            Rectangle workingArea = Screen.FromRectangle(selection).WorkingArea;
            int left = Math.Max(workingArea.Left, Math.Min(workingArea.Right - Width, selection.Right - Width));
            int preferredTop = selection.Top >= workingArea.Top + Height + 6 ? selection.Top - Height - 6 : selection.Top + 6;
            int top = Math.Max(workingArea.Top, Math.Min(workingArea.Bottom - Height, preferredTop));
            Location = new Point(left, top);
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(28, 31, 35);

            copyButton = CreateButton("⧉", 2);
            closeButton = CreateButton("×", 38);
            Controls.Add(copyButton);
            Controls.Add(closeButton);
            ToolTip tips = new ToolTip();
            tips.SetToolTip(copyButton, "复制全部译文");
            tips.SetToolTip(closeButton, "关闭区域翻译");
            copyButton.Click += delegate
            {
                EventHandler handler = CopyRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            };
            closeButton.Click += delegate
            {
                EventHandler handler = CloseRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WsExToolWindow;
                return cp;
            }
        }

        private Button CreateButton(string text, int left)
        {
            Button button = new Button();
            button.Text = text;
            button.Font = new Font("Segoe UI Symbol", 15F, FontStyle.Regular, GraphicsUnit.Pixel);
            button.Location = new Point(left, 2);
            button.Size = new Size(36, 34);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = Color.FromArgb(28, 31, 35);
            button.ForeColor = Color.White;
            button.TabStop = false;
            return button;
        }
    }

    internal sealed class ScreenTranslationSettingsDialog : Form
    {
        private readonly ComboBox targetLanguage;
        private readonly TextBox apiUrl;
        private readonly TextBox model;
        private readonly TextBox apiKey;
        private readonly NumericUpDown confidence;
        private readonly Button saveButton;

        private ScreenTranslationSettingsDialog(ScreenTranslationSettings settings, ScreenTranslationSecrets secrets)
        {
            Text = "区域翻译设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(540, 354);
            BackColor = Color.FromArgb(246, 248, 250);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            Label title = new Label();
            title.Text = "区域翻译";
            title.Font = new Font(Font.FontFamily, 15F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(28, 35, 42);
            title.Location = new Point(24, 20);
            title.AutoSize = true;
            Controls.Add(title);

            targetLanguage = AddCombo("目标语言", 70);
            targetLanguage.Items.AddRange(new object[] { "简体中文", "繁体中文", "English", "日本語", "한국어" });
            targetLanguage.SelectedItem = settings.TargetLanguage;
            if (targetLanguage.SelectedIndex < 0) targetLanguage.SelectedIndex = 0;

            apiUrl = AddTextField("API 地址", 120, settings.ApiBaseUrl,
                "https://fast.qianxing.pro/v1");
            model = AddTextField("模型", 170, settings.Model,
                "gemini-3.1-flash-lite");
            apiKey = AddTextField("API 密钥", 220, secrets.ApiKey, "本机服务可留空");
            apiKey.UseSystemPasswordChar = !IsPlaceholder(apiKey);
            apiKey.GotFocus += delegate { apiKey.UseSystemPasswordChar = true; };
            apiKey.LostFocus += delegate { apiKey.UseSystemPasswordChar = !IsPlaceholder(apiKey); };

            Label confidenceLabel = AddLabel("最低识别置信度", 270);
            confidence = new NumericUpDown();
            confidence.Location = new Point(166, confidenceLabel.Top - 4);
            confidence.Size = new Size(100, 28);
            confidence.Minimum = 35;
            confidence.Maximum = 95;
            confidence.Value = (decimal)Math.Round(Math.Max(0.35F, Math.Min(0.95F, settings.MinimumConfidence)) * 100F);
            confidence.Increment = 1;
            Controls.Add(confidence);
            Label percent = new Label();
            percent.Text = "%";
            percent.AutoSize = true;
            percent.Location = new Point(274, confidenceLabel.Top + 2);
            Controls.Add(percent);

            Button cancelButton = new Button();
            cancelButton.Text = "取消";
            cancelButton.DialogResult = DialogResult.Cancel;
            cancelButton.Location = new Point(350, 310);
            cancelButton.Size = new Size(78, 30);
            Controls.Add(cancelButton);

            saveButton = new Button();
            saveButton.Text = "保存";
            saveButton.Location = new Point(438, 310);
            saveButton.Size = new Size(78, 30);
            saveButton.BackColor = Color.FromArgb(32, 143, 190);
            saveButton.ForeColor = Color.White;
            saveButton.FlatStyle = FlatStyle.Flat;
            saveButton.FlatAppearance.BorderSize = 0;
            saveButton.Click += SaveSettings;
            Controls.Add(saveButton);

            AcceptButton = saveButton;
            CancelButton = cancelButton;
        }

        public static bool Edit(
            IWin32Window owner,
            ScreenTranslationSettings current,
            ScreenTranslationSecrets currentSecrets,
            out ScreenTranslationSettings result,
            out ScreenTranslationSecrets resultSecrets)
        {
            using (ScreenTranslationSettingsDialog dialog = new ScreenTranslationSettingsDialog(
                current.Clone(), currentSecrets.Clone()))
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK)
                {
                    result = current;
                    resultSecrets = currentSecrets;
                    return false;
                }

                result = new ScreenTranslationSettings();
                result.TargetLanguage = Convert.ToString(dialog.targetLanguage.SelectedItem);
                result.ApiBaseUrl = ValueOrSuggestedDefault(dialog.apiUrl);
                result.Model = ValueOrSuggestedDefault(dialog.model);
                result.MinimumConfidence = (float)dialog.confidence.Value / 100F;
                resultSecrets = new ScreenTranslationSecrets();
                resultSecrets.ApiKey = ValueOrPlaceholder(dialog.apiKey);
                return true;
            }
        }

        private void SaveSettings(object sender, EventArgs e)
        {
            ScreenTranslationSettings settings = new ScreenTranslationSettings();
            settings.TargetLanguage = Convert.ToString(targetLanguage.SelectedItem);
            settings.ApiBaseUrl = ValueOrSuggestedDefault(apiUrl);
            settings.Model = ValueOrSuggestedDefault(model);
            settings.MinimumConfidence = (float)confidence.Value / 100F;
            ScreenTranslationSecrets secrets = new ScreenTranslationSecrets();
            secrets.ApiKey = ValueOrPlaceholder(apiKey);
            string error = ScreenTranslationSettingsStore.Validate(settings, secrets);
            if (error.Length > 0)
            {
                MessageBox.Show(this, error, "区域翻译设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        private ComboBox AddCombo(string label, int top)
        {
            AddLabel(label, top);
            ComboBox box = new ComboBox();
            box.DropDownStyle = ComboBoxStyle.DropDownList;
            box.Location = new Point(166, top - 4);
            box.Size = new Size(350, 28);
            Controls.Add(box);
            return box;
        }

        private TextBox AddTextField(string label, int top, string value, string placeholder)
        {
            AddLabel(label, top);
            TextBox box = new TextBox();
            box.Location = new Point(166, top - 4);
            box.Size = new Size(350, 28);
            box.Text = String.IsNullOrWhiteSpace(value) ? placeholder : value;
            box.ForeColor = String.IsNullOrWhiteSpace(value) ? Color.FromArgb(118, 125, 132) : Color.FromArgb(28, 35, 42);
            box.Tag = placeholder;
            box.GotFocus += delegate
            {
                if (box.ForeColor == Color.FromArgb(118, 125, 132))
                {
                    box.Text = "";
                    box.ForeColor = Color.FromArgb(28, 35, 42);
                }
            };
            box.LostFocus += delegate
            {
                if (String.IsNullOrWhiteSpace(box.Text))
                {
                    box.Text = Convert.ToString(box.Tag);
                    box.ForeColor = Color.FromArgb(118, 125, 132);
                }
            };
            Controls.Add(box);
            return box;
        }

        private Label AddLabel(string text, int top)
        {
            Label label = new Label();
            label.Text = text;
            label.Location = new Point(24, top);
            label.Size = new Size(132, 24);
            label.ForeColor = Color.FromArgb(60, 69, 78);
            Controls.Add(label);
            return label;
        }

        private static string ValueOrPlaceholder(TextBox box)
        {
            return IsPlaceholder(box) ? "" : box.Text.Trim();
        }

        private static string ValueOrSuggestedDefault(TextBox box)
        {
            return IsPlaceholder(box) ? Convert.ToString(box.Tag).Trim() : box.Text.Trim();
        }

        private static bool IsPlaceholder(TextBox box)
        {
            return box.ForeColor == Color.FromArgb(118, 125, 132);
        }
    }
}
