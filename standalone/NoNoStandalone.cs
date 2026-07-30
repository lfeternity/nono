using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NoNoStandalone
{
    internal static class Program
    {
        private const string SingleInstanceMutexName = @"Local\NoNoStandalone-2d9cf640-7d73-42fb-9fc1-3daeb36ca78e";

        [STAThread]
        private static int Main(string[] args)
        {
            if (HasFlag(args, "--self-test"))
            {
                return SelfTest.Run();
            }

            if (HasFlag(args, "--codex-computer-self-test"))
            {
                try
                {
                    return CodexComputerIntegrationSelfTest.RunAsync().GetAwaiter().GetResult() ? 0 : 7;
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.WriteAllText(
                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NoNo-CodexComputer.selftest.log"),
                            ex.ToString(),
                            new UTF8Encoding(false));
                    }
                    catch
                    {
                    }
                    return 8;
                }
            }

            bool isFirstInstance;
            using (System.Threading.Mutex singleInstanceMutex = new System.Threading.Mutex(true, SingleInstanceMutexName, out isFirstInstance))
            {
                if (!isFirstInstance)
                {
                    return 0;
                }

                try
                {
                    DesktopNative.EnablePerMonitorDpiAwareness();
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new PetForm());
                    return 0;
                }
                finally
                {
                    singleInstanceMutex.ReleaseMutex();
                }
            }
        }

        private static bool HasFlag(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (String.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal sealed class AnimationState
    {
        public readonly string Id;
        public readonly string Label;
        public readonly int Row;
        public readonly int FrameCount;
        public readonly int FrameMs;
        public readonly bool OneShot;

        public AnimationState(string id, string label, int row, int frameCount, int frameMs, bool oneShot)
        {
            Id = id;
            Label = label;
            Row = row;
            FrameCount = frameCount;
            FrameMs = frameMs;
            OneShot = oneShot;
        }
    }

    internal sealed class PetAtlas : IDisposable
    {
        public const int CellWidth = 192;
        public const int CellHeight = 208;
        public const int Columns = 8;
        public const int Rows = 9;

        public static readonly AnimationState[] States = new AnimationState[]
        {
            new AnimationState("idle", "待机", 0, 6, 180, false),
            new AnimationState("running-right", "向右移动", 1, 8, 90, false),
            new AnimationState("running-left", "向左移动", 2, 8, 90, false),
            new AnimationState("waving", "挥手", 3, 4, 150, true),
            new AnimationState("jumping", "跳跃", 4, 5, 120, true),
            new AnimationState("failed", "失败", 5, 8, 240, false),
            new AnimationState("waiting", "等待", 6, 6, 180, false),
            new AnimationState("running", "运行中", 7, 6, 220, false),
            new AnimationState("review", "检查", 8, 6, 180, false),
        };

        private Bitmap atlas;
        private readonly Dictionary<string, AnimationState> statesById;
        private readonly Dictionary<string, Bitmap[]> framesByStateId;
        private readonly string validationFailure;
        private bool disposed;

        public PetAtlas()
        {
            using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream("spritesheet.png"))
            {
                if (input == null)
                {
                    throw new InvalidOperationException("Missing embedded spritesheet.png resource.");
                }

                using (Bitmap loaded = new Bitmap(input))
                {
                    atlas = new Bitmap(loaded.Width, loaded.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(atlas))
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.DrawImage(loaded, new Rectangle(0, 0, loaded.Width, loaded.Height));
                    }
                }
            }

            if (atlas.Width != CellWidth * Columns || atlas.Height != CellHeight * Rows)
            {
                throw new InvalidOperationException("Unexpected spritesheet size.");
            }

            statesById = new Dictionary<string, AnimationState>(StringComparer.OrdinalIgnoreCase);
            framesByStateId = new Dictionary<string, Bitmap[]>(StringComparer.OrdinalIgnoreCase);
            foreach (AnimationState state in States)
            {
                statesById[state.Id] = state;
                framesByStateId[state.Id] = ExtractFrames(state);
            }

            validationFailure = FindValidationFailure();
            atlas.Dispose();
            atlas = null;
        }

        public AnimationState GetState(string id)
        {
            return statesById[id];
        }

        public Bitmap GetFrame(AnimationState state, int frameIndex)
        {
            return (Bitmap)GetFrameReference(state, frameIndex).Clone();
        }

        public Bitmap GetFrameReference(AnimationState state, int frameIndex)
        {
            if (disposed)
            {
                throw new ObjectDisposedException("PetAtlas");
            }

            Bitmap[] frames = framesByStateId[state.Id];
            return frames[frameIndex % state.FrameCount];
        }

        private Bitmap[] ExtractFrames(AnimationState state)
        {
            Bitmap[] frames = new Bitmap[state.FrameCount];
            for (int i = 0; i < frames.Length; i++)
            {
                frames[i] = ExtractFrame(state, i);
            }

            return frames;
        }

        private Bitmap ExtractFrame(AnimationState state, int frameIndex)
        {
            Rectangle src = new Rectangle(frameIndex * CellWidth, state.Row * CellHeight, CellWidth, CellHeight);
            Bitmap frame = new Bitmap(CellWidth, CellHeight, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(frame))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(atlas, new Rectangle(0, 0, CellWidth, CellHeight), src, GraphicsUnit.Pixel);
            }

            return frame;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (Bitmap[] frames in framesByStateId.Values)
            {
                for (int i = 0; i < frames.Length; i++)
                {
                    if (frames[i] != null)
                    {
                        frames[i].Dispose();
                    }
                }
            }

            if (atlas != null)
            {
                atlas.Dispose();
                atlas = null;
            }
        }

        public bool Validate(out string failure)
        {
            failure = validationFailure;
            return failure == null;
        }

        private string FindValidationFailure()
        {
            foreach (AnimationState state in States)
            {
                for (int column = 0; column < Columns; column++)
                {
                    bool used = column < state.FrameCount;
                    bool hasPixels = CellHasPixels(column, state.Row);
                    if (used != hasPixels)
                    {
                        return state.Id + "[" + column + "] used=" + used + " hasPixels=" + hasPixels;
                    }
                }
            }

            return null;
        }

        private bool CellHasPixels(int column, int row)
        {
            Rectangle rect = new Rectangle(column * CellWidth, row * CellHeight, CellWidth, CellHeight);
            BitmapData data = atlas.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = rect.Width * 4;
                byte[] buffer = new byte[rowBytes];
                for (int y = 0; y < rect.Height; y++)
                {
                    IntPtr rowPointer = new IntPtr(data.Scan0.ToInt64() + y * data.Stride);
                    Marshal.Copy(rowPointer, buffer, 0, rowBytes);
                    for (int x = 0; x < rect.Width; x++)
                    {
                        int alphaIndex = x * 4 + 3;
                        if (buffer[alphaIndex] != 0)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            finally
            {
                atlas.UnlockBits(data);
            }
        }
    }

    internal static class PetWindowIcon
    {
        private const int IconSize = 256;
        private const int IconPadding = 18;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static Icon Create()
        {
            using (Bitmap frame = LoadIdleFrame())
            using (Bitmap iconBitmap = DrawIconBitmap(frame))
            {
                IntPtr handle = iconBitmap.GetHicon();
                try
                {
                    using (Icon icon = Icon.FromHandle(handle))
                    {
                        return (Icon)icon.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        internal static Bitmap LoadIdleFrame()
        {
            using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream("spritesheet.png"))
            {
                if (input == null)
                {
                    throw new InvalidOperationException("Missing embedded spritesheet.png resource.");
                }

                using (Bitmap atlas = new Bitmap(input))
                {
                    Rectangle source = new Rectangle(0, 0, PetAtlas.CellWidth, PetAtlas.CellHeight);
                    Bitmap frame = new Bitmap(PetAtlas.CellWidth, PetAtlas.CellHeight, PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(frame))
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.DrawImage(atlas, new Rectangle(0, 0, frame.Width, frame.Height), source, GraphicsUnit.Pixel);
                    }

                    return frame;
                }
            }
        }

        private static Bitmap DrawIconBitmap(Bitmap frame)
        {
            Rectangle content = FindContentBounds(frame);
            Bitmap iconBitmap = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(iconBitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceOver;

                int maxSize = IconSize - IconPadding * 2;
                float scale = Math.Min((float)maxSize / content.Width, (float)maxSize / content.Height);
                int width = Math.Max(1, (int)Math.Round(content.Width * scale));
                int height = Math.Max(1, (int)Math.Round(content.Height * scale));
                Rectangle destination = new Rectangle(
                    (IconSize - width) / 2,
                    (IconSize - height) / 2,
                    width,
                    height);

                g.DrawImage(frame, destination, content, GraphicsUnit.Pixel);
            }

            return iconBitmap;
        }

        private static Rectangle FindContentBounds(Bitmap bitmap)
        {
            int left = bitmap.Width;
            int top = bitmap.Height;
            int right = -1;
            int bottom = -1;

            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).A <= 12)
                    {
                        continue;
                    }

                    if (x < left)
                    {
                        left = x;
                    }

                    if (y < top)
                    {
                        top = y;
                    }

                    if (x > right)
                    {
                        right = x;
                    }

                    if (y > bottom)
                    {
                        bottom = y;
                    }
                }
            }

            if (right < left || bottom < top)
            {
                return new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            }

            return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }
    }

    internal sealed class PetMenuRenderer : ToolStripRenderer
    {
        public static readonly PetMenuRenderer Instance = new PetMenuRenderer();
        public static readonly Color SurfaceColor = Color.FromArgb(250, 253, 255);
        public static readonly Color TextColor = Color.FromArgb(31, 45, 56);

        private static readonly Color BorderColor = Color.FromArgb(173, 210, 221);
        private static readonly Color GutterColor = Color.FromArgb(237, 248, 251);
        private static readonly Color SeparatorColor = Color.FromArgb(211, 230, 236);
        private static readonly Color SelectedTopColor = Color.FromArgb(222, 248, 254);
        private static readonly Color SelectedBottomColor = Color.FromArgb(200, 239, 248);
        private static readonly Color AccentColor = Color.FromArgb(23, 151, 184);
        private static readonly Color MutedTextColor = Color.FromArgb(104, 122, 135);

        private PetMenuRenderer()
        {
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            Rectangle bounds = new Rectangle(Point.Empty, e.ToolStrip.Size);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush brush = new SolidBrush(SurfaceColor))
            {
                e.Graphics.FillRectangle(brush, bounds);
            }

            Rectangle inner = new Rectangle(1, 1, Math.Max(0, bounds.Width - 3), Math.Max(0, bounds.Height - 3));
            using (GraphicsPath path = RoundedPath(inner, 8))
            using (SolidBrush brush = new SolidBrush(SurfaceColor))
            {
                e.Graphics.FillPath(brush, path);
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            Rectangle border = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedPath(border, 8))
            using (Pen pen = new Pen(BorderColor))
            {
                e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            Rectangle bounds = e.AffectedBounds;
            if (bounds.Width <= 0)
            {
                bounds = new Rectangle(6, 7, 26, e.ToolStrip.Height - 14);
            }

            using (SolidBrush brush = new SolidBrush(GutterColor))
            {
                e.Graphics.FillRectangle(brush, bounds);
            }

            using (Pen pen = new Pen(Color.FromArgb(220, 237, 242)))
            {
                e.Graphics.DrawLine(pen, bounds.Right - 1, bounds.Top + 6, bounds.Right - 1, bounds.Bottom - 6);
            }
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            ToolStripMenuItem menuItem = e.Item as ToolStripMenuItem;
            if (menuItem == null || !menuItem.Selected || !menuItem.Enabled)
            {
                return;
            }

            Rectangle bounds = new Rectangle(5, 2, e.Item.Width - 10, e.Item.Height - 4);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedPath(bounds, 6))
            using (LinearGradientBrush brush = new LinearGradientBrush(bounds, SelectedTopColor, SelectedBottomColor, LinearGradientMode.Vertical))
            {
                e.Graphics.FillPath(brush, path);
            }

            using (GraphicsPath path = RoundedPath(bounds, 6))
            using (Pen pen = new Pen(Color.FromArgb(139, 218, 235)))
            {
                e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Color textColor;
            if (!e.Item.Enabled)
            {
                textColor = Color.FromArgb(145, 158, 168);
            }
            else if (e.Item.Selected)
            {
                textColor = Color.FromArgb(13, 72, 91);
            }
            else if (e.Item is ToolStripMenuItem && ((ToolStripMenuItem)e.Item).Checked)
            {
                textColor = AccentColor;
            }
            else
            {
                textColor = TextColor;
            }

            Rectangle textBounds = new Rectangle(
                e.TextRectangle.Left,
                0,
                e.TextRectangle.Width,
                e.Item.Height);
            TextRenderer.DrawText(
                e.Graphics,
                e.Text,
                e.TextFont,
                textBounds,
                textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            Rectangle bounds = new Rectangle(Point.Empty, e.Item.Size);
            int y = bounds.Height / 2;
            int left = 38;
            int right = bounds.Right - 8;
            using (Pen pen = new Pen(SeparatorColor))
            {
                e.Graphics.DrawLine(pen, left, y, right, y);
            }
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle itemBounds = new Rectangle(Point.Empty, e.Item.Size);
            Rectangle checkBounds = new Rectangle(10, itemBounds.Top + (itemBounds.Height - 18) / 2, 18, 18);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedPath(checkBounds, 4))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(219, 248, 254)))
            {
                e.Graphics.FillPath(brush, path);
            }

            using (GraphicsPath path = RoundedPath(checkBounds, 4))
            using (Pen pen = new Pen(Color.FromArgb(104, 207, 229)))
            {
                e.Graphics.DrawPath(pen, path);
            }

            Point[] tick = new Point[]
            {
                new Point(checkBounds.Left + 5, checkBounds.Top + 9),
                new Point(checkBounds.Left + 8, checkBounds.Top + 12),
                new Point(checkBounds.Left + 13, checkBounds.Top + 6)
            };
            using (Pen pen = new Pen(AccentColor, 2.0f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                e.Graphics.DrawLines(pen, tick);
            }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            Rectangle bounds = e.ArrowRectangle;
            Point center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            Point[] arrow;
            if (e.Direction == ArrowDirection.Right)
            {
                arrow = new Point[]
                {
                    new Point(center.X - 2, center.Y - 5),
                    new Point(center.X - 2, center.Y + 5),
                    new Point(center.X + 4, center.Y)
                };
            }
            else
            {
                base.OnRenderArrow(e);
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush brush = new SolidBrush(e.Item.Selected ? AccentColor : MutedTextColor))
            {
                e.Graphics.FillPolygon(brush, arrow);
            }
        }

        private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            Rectangle arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class LayeredFrame : IDisposable
    {
        public readonly IntPtr BitmapHandle;
        public readonly Size Size;
        private bool disposed;

        public LayeredFrame(Bitmap bitmap)
        {
            if (bitmap == null)
            {
                throw new ArgumentNullException("bitmap");
            }

            Size = bitmap.Size;
            BitmapHandle = NativeMethods.CreateLayeredBitmapHandle(bitmap);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            NativeMethods.DeleteLayeredBitmapHandle(BitmapHandle);
        }
    }

    internal sealed class PetForm : Form
    {
        private readonly PetAtlas atlas;
        private readonly Timer timer;
        private readonly Timer clipboardMonitorTimer;
        private readonly ContextMenuStrip menu;
        private readonly ContextMenuStrip trayMenu;
        private readonly Icon appIcon;
        private readonly NotifyIcon trayIcon;
        private Form panelForm;
        private QuickLauncherForm launcherForm;
        private readonly CodexActivityMonitor codexMonitor;
        private ToolStripMenuItem topMostMenuItem;
        private ToolStripMenuItem startupMenuItem;
        private ToolStripMenuItem desktopIconsMenuItem;
        private ToolStripMenuItem taskbarMenuItem;
        private ToolStripMenuItem mousePassthroughMenuItem;
        private ToolStripMenuItem trayTopMostMenuItem;
        private ToolStripMenuItem trayStartupMenuItem;
        private ToolStripMenuItem trayMousePassthroughMenuItem;
        private ToolStripMenuItem codexFollowMenuItem;
        private ToolStripMenuItem codexStateMenuItem;
        private ToolStripMenuItem petAppearanceMenuItem;
        private ToolStripMenuItem voiceEnableMenuItem;
        private ToolStripMenuItem voiceManualTalkMenuItem;
        private ToolStripMenuItem voiceStateMenuItem;
        private ToolStripMenuItem desktopAgentEnableMenuItem;
        private ToolStripMenuItem desktopAgentViewMenuItem;
        private ToolStripMenuItem desktopAgentOperateMenuItem;
        private ToolStripMenuItem desktopAgentStopMenuItem;
        private ToolStripMenuItem desktopAgentStateMenuItem;
        private ToolStripMenuItem emptyRecycleBinMenuItem;
        private readonly VoiceAssistantController voiceAssistant;
        private readonly DesktopAgentCoordinator desktopAgent;
        private LocalSpeechPlayer voiceSpeechPlayer;
        private readonly VoiceCaptionForm voiceCaption;
        private static readonly int[] JumpFrameTopOffsets = new int[] { 17, 7, 0, 9, 18 };
        private const int AnimationTimerMinMs = 45;
        private const int AnimationTimerMaxMs = 250;
        private const int BackgroundClipboardMonitorIntervalMs = 5000;
        private const int JumpTargetWidth = 180;
        private const int JumpTargetHeight = 190;
        private readonly Dictionary<string, LayeredFrame> layeredFrameCache;

        private AnimationState state;
        private int frameIndex;
        private ulong lastFrameTickMs;
        private float scale;
        private PetAppearance petAppearance;
        private bool dragging;
        private bool suppressDragState;
        private bool playOnceThenIdle;
        private bool mousePassthrough;
        private bool followCodexStatus;
        private string lastVoiceRequestKey;
        private DateTime lastVoiceRequestAtUtc;
        private string pendingVoiceClarificationPrefix;
        private DateTime pendingVoiceClarificationExpiresAtUtc;
        private Point dragCursor;
        private Point dragWindow;
        private Task emptyRecycleBinTask;
        private int codexPollInProgress;
        private bool pendingForcedCodexPoll;

        public PetForm()
        {
            atlas = new PetAtlas();
            codexMonitor = new CodexActivityMonitor();
            layeredFrameCache = new Dictionary<string, LayeredFrame>(StringComparer.OrdinalIgnoreCase);
            state = atlas.GetState("idle");
            frameIndex = 0;
            lastFrameTickMs = NativeMethods.GetTickCount64();
            scale = PetScaleSettingsStore.Load();
            petAppearance = PetAppearanceStore.Load();
            followCodexStatus = CodexMonitorSettingsStore.LoadEnabled();

            Text = "诺诺";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = false;
            MinimumSize = new Size(1, 1);
            Size = ScaledSize;

            menu = BuildMenu();
            ContextMenuStrip = menu;
            trayMenu = BuildTrayMenu();

            appIcon = PetWindowIcon.Create();
            Icon = appIcon;
            trayIcon = new NotifyIcon();
            trayIcon.Icon = appIcon;
            trayIcon.Text = "诺诺";
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate { ShowPanel(); };

            voiceAssistant = new VoiceAssistantController(this);
            voiceAssistant.EventReceived += OnVoiceAssistantEvent;
            desktopAgent = new DesktopAgentCoordinator(this);
            desktopAgent.EventReceived += OnDesktopAgentEvent;
            voiceCaption = new VoiceCaptionForm();

            timer = new Timer();
            timer.Interval = AnimationTimerInterval(state);
            timer.Tick += delegate
            {
                TickAnimation();
                TickCodexMonitor(false);
            };

            ClipboardSessionHistory.EnsureLoaded();
            clipboardMonitorTimer = new Timer();
            clipboardMonitorTimer.Interval = BackgroundClipboardMonitorIntervalMs;
            clipboardMonitorTimer.Tick += delegate { CaptureClipboardInBackground(); };
            SystemEvents.SessionEnding += OnSystemSessionEnding;

            MouseDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseUp += OnMouseUp;
            LocationChanged += delegate
            {
                if (voiceCaption != null && voiceCaption.Visible)
                {
                    voiceCaption.Reposition();
                }
            };
            MouseDoubleClick += delegate
            {
                if (voiceAssistant.IsEnabled)
                {
                    StartManualVoiceCapture();
                }
                else
                {
                    Play("waving");
                }
            };
            HotkeySettingsStore.SettingsChanged += delegate { RegisterConfiguredHotkeys(); };
            Shown += delegate
            {
                Location = InitialLocation();
                Render();
                timer.Start();
                clipboardMonitorTimer.Start();
                voiceAssistant.StartIfEnabled();
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.SessionEnding -= OnSystemSessionEnding;
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.ContextMenuStrip = null;
                    trayIcon.Dispose();
                }

                if (trayMenu != null)
                {
                    trayMenu.Dispose();
                }

                if (clipboardMonitorTimer != null)
                {
                    clipboardMonitorTimer.Dispose();
                }

                if (timer != null)
                {
                    timer.Dispose();
                }

                if (voiceAssistant != null)
                {
                    voiceAssistant.EventReceived -= OnVoiceAssistantEvent;
                    voiceAssistant.Dispose();
                }

                if (desktopAgent != null)
                {
                    desktopAgent.EventReceived -= OnDesktopAgentEvent;
                    desktopAgent.Dispose();
                }

                if (voiceSpeechPlayer != null)
                {
                    voiceSpeechPlayer.Dispose();
                }

                if (voiceCaption != null)
                {
                    voiceCaption.Dispose();
                }

                ClearLayeredFrameCache();
                atlas.Dispose();
                Icon = null;
                if (appIcon != null)
                {
                    appIcon.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        private void OnSystemSessionEnding(object sender, SessionEndingEventArgs e)
        {
            ClipboardSessionHistory.ClearCurrentSession();
        }

        private async void CaptureClipboardInBackground()
        {
            try
            {
                await ClipboardSessionHistory.CaptureCurrentAsync();
            }
            catch
            {
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == GlobalHotkeyManager.WmHotkey)
            {
                HandleHotkey(m.WParam.ToInt32());
                return;
            }

            if (mousePassthrough && m.Msg == NativeMethods.WM_NCHITTEST)
            {
                m.Result = NativeMethods.IsRightButtonDown()
                    ? new IntPtr(NativeMethods.HTCLIENT)
                    : new IntPtr(NativeMethods.HTTRANSPARENT);
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterConfiguredHotkeys();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            GlobalHotkeyManager.UnregisterAll(Handle);
            base.OnHandleDestroyed(e);
        }

        private Size ScaledSize
        {
            get
            {
                return new Size((int)Math.Round(PetAtlas.CellWidth * scale), (int)Math.Round(PetAtlas.CellHeight * scale));
            }
        }

        private Point InitialLocation()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Size size = ScaledSize;
            return new Point(area.Right - size.Width - 48, area.Bottom - size.Height - 48);
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip context = new ContextMenuStrip();
            ConfigurePetMenu(context);
            context.Opening += delegate
            {
                UpdateSystemMenuChecks();
                UpdateDesktopAgentMenuChecks();
            };
            ToolStripMenuItem panel = StyledMenuItem("面板");
            panel.Font = new Font(context.Font, FontStyle.Bold);
            panel.Click += delegate { ShowPanel(); };
            context.Items.Add(panel);
            ToolStripMenuItem launcher = StyledMenuItem("快速直达");
            launcher.Click += delegate { ShowQuickLauncher(); };
            context.Items.Add(launcher);

            ToolStripMenuItem weather = StyledMenuItem("查看天气");
            weather.Click += delegate { ShowWeather(); };
            context.Items.Add(weather);

            ToolStripMenuItem showDesktop = StyledMenuItem("回桌面");
            showDesktop.Click += delegate { ShowDesktop(); };
            context.Items.Add(showDesktop);
            context.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem actionMenu = StyledMenuItem("动作");
            ConfigurePetMenu(actionMenu.DropDown);
            actionMenu.DropDownItems.Add(MenuItem("待机", "idle"));
            actionMenu.DropDownItems.Add(MenuItem("向右移动", "running-right"));
            actionMenu.DropDownItems.Add(MenuItem("向左移动", "running-left"));
            actionMenu.DropDownItems.Add(MenuItem("挥手一次", "waving"));
            actionMenu.DropDownItems.Add(MenuItem("跳跃一次", "jumping"));
            actionMenu.DropDownItems.Add(MenuItem("失败", "failed"));
            actionMenu.DropDownItems.Add(MenuItem("等待", "waiting"));
            actionMenu.DropDownItems.Add(MenuItem("运行中", "running"));
            actionMenu.DropDownItems.Add(MenuItem("检查", "review"));
            context.Items.Add(actionMenu);

            petAppearanceMenuItem = StyledMenuItem("宠物外观");
            ConfigurePetMenu(petAppearanceMenuItem.DropDown);
            PetAppearance[] appearances = PetAppearanceStore.All;
            for (int i = 0; i < appearances.Length; i++)
            {
                petAppearanceMenuItem.DropDownItems.Add(PetAppearanceItem(appearances[i]));
            }
            context.Items.Add(petAppearanceMenuItem);

            ToolStripMenuItem codexMenu = StyledMenuItem("Codex 状态");
            ConfigurePetMenu(codexMenu.DropDown);
            codexFollowMenuItem = StyledMenuItem("跟随 Codex 状态");
            codexFollowMenuItem.Checked = followCodexStatus;
            codexFollowMenuItem.Click += ToggleCodexFollow;
            codexMenu.DropDownItems.Add(codexFollowMenuItem);
            ToolStripMenuItem checkCodex = StyledMenuItem("立即检查");
            checkCodex.Click += delegate { TickCodexMonitor(true); };
            codexMenu.DropDownItems.Add(checkCodex);
            codexMenu.DropDownItems.Add(new ToolStripSeparator());
            codexStateMenuItem = StyledMenuItem("当前: " + codexMonitor.Current.Label);
            codexStateMenuItem.Enabled = false;
            codexMenu.DropDownItems.Add(codexStateMenuItem);
            context.Items.Add(codexMenu);

            ToolStripMenuItem voiceMenu = StyledMenuItem("语音助手");
            ConfigurePetMenu(voiceMenu.DropDown);
            voiceEnableMenuItem = StyledMenuItem("启用本地语音唤醒");
            voiceEnableMenuItem.Checked = VoiceSettingsStore.Load().Enabled;
            voiceEnableMenuItem.Click += ToggleVoiceAssistant;
            voiceMenu.DropDownItems.Add(voiceEnableMenuItem);
            voiceManualTalkMenuItem = StyledMenuItem("现在说话");
            voiceManualTalkMenuItem.Click += delegate { StartManualVoiceCapture(); };
            voiceMenu.DropDownItems.Add(voiceManualTalkMenuItem);
            ToolStripMenuItem clearVoiceHistory = StyledMenuItem("清空对话上下文");
            clearVoiceHistory.Click += delegate
            {
                if (voiceAssistant != null)
                {
                    voiceAssistant.ClearHistory();
                }
            };
            voiceMenu.DropDownItems.Add(clearVoiceHistory);
            ToolStripMenuItem restartVoice = StyledMenuItem("重启本地语音服务");
            restartVoice.Click += delegate
            {
                if (voiceAssistant != null)
                {
                    voiceAssistant.Restart();
                }
            };
            voiceMenu.DropDownItems.Add(restartVoice);
            voiceMenu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem voiceSettings = StyledMenuItem("设置");
            voiceSettings.Click += delegate { ShowVoiceSettings(); };
            voiceMenu.DropDownItems.Add(voiceSettings);
            ToolStripMenuItem microphonePrivacy = StyledMenuItem("Windows 麦克风隐私设置");
            microphonePrivacy.Click += delegate { OpenMicrophonePrivacySettings(); };
            voiceMenu.DropDownItems.Add(microphonePrivacy);
            voiceMenu.DropDownItems.Add(new ToolStripSeparator());
            voiceStateMenuItem = StyledMenuItem("当前: 已关闭");
            voiceStateMenuItem.Enabled = false;
            voiceMenu.DropDownItems.Add(voiceStateMenuItem);
            context.Items.Add(voiceMenu);

            ToolStripMenuItem desktopAgentMenu = StyledMenuItem("电脑助手");
            ConfigurePetMenu(desktopAgentMenu.DropDown);
            DesktopAgentSettings initialAgentSettings = DesktopAgentSettingsStore.Load();
            desktopAgentEnableMenuItem = StyledMenuItem("启用电脑助手");
            desktopAgentEnableMenuItem.Checked = initialAgentSettings.Enabled;
            desktopAgentEnableMenuItem.Click += ToggleDesktopAgent;
            desktopAgentMenu.DropDownItems.Add(desktopAgentEnableMenuItem);
            desktopAgentViewMenuItem = StyledMenuItem("查看整个屏幕");
            desktopAgentViewMenuItem.Click += async delegate { await StartDesktopAgentViewAsync(); };
            desktopAgentMenu.DropDownItems.Add(desktopAgentViewMenuItem);
            desktopAgentOperateMenuItem = StyledMenuItem("操作电脑");
            desktopAgentOperateMenuItem.Click += async delegate { await StartDesktopAgentTaskAsync(); };
            desktopAgentMenu.DropDownItems.Add(desktopAgentOperateMenuItem);
            desktopAgentStopMenuItem = StyledMenuItem("立即停止");
            desktopAgentStopMenuItem.Click += delegate
            {
                if (desktopAgent != null)
                {
                    desktopAgent.Stop();
                }
            };
            desktopAgentMenu.DropDownItems.Add(desktopAgentStopMenuItem);
            desktopAgentMenu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem desktopAgentSettings = StyledMenuItem("设置");
            desktopAgentSettings.Click += delegate { ShowDesktopAgentSettings(); };
            desktopAgentMenu.DropDownItems.Add(desktopAgentSettings);
            desktopAgentMenu.DropDownItems.Add(new ToolStripSeparator());
            desktopAgentStateMenuItem = StyledMenuItem("当前: 已关闭");
            desktopAgentStateMenuItem.Enabled = false;
            desktopAgentMenu.DropDownItems.Add(desktopAgentStateMenuItem);
            context.Items.Add(desktopAgentMenu);
            context.Items.Add(new ToolStripSeparator());

            topMostMenuItem = ToggleItem("置顶", TopMost, ToggleTopMost);
            context.Items.Add(topMostMenuItem);

            startupMenuItem = ToggleItem("开机自启动", StartupManager.IsEnabled(), ToggleStartup);
            context.Items.Add(startupMenuItem);

            desktopIconsMenuItem = ToggleItem("隐藏桌面图标", !NativeMethods.AreDesktopIconsVisible(), ToggleDesktopIcons);
            context.Items.Add(desktopIconsMenuItem);

            taskbarMenuItem = ToggleItem("隐藏任务栏", !NativeMethods.AreTaskbarsVisible(), ToggleTaskbar);
            context.Items.Add(taskbarMenuItem);

            mousePassthroughMenuItem = ToggleItem("鼠标穿透", mousePassthrough, ToggleMousePassthrough);
            context.Items.Add(mousePassthroughMenuItem);

            emptyRecycleBinMenuItem = StyledMenuItem("清空回收站");
            emptyRecycleBinMenuItem.Click += async delegate { await EmptyRecycleBinAsync(); };
            context.Items.Add(emptyRecycleBinMenuItem);

            ToolStripMenuItem scaleMenu = StyledMenuItem("缩放");
            ConfigurePetMenu(scaleMenu.DropDown);
            scaleMenu.DropDownItems.Add(ScaleItem("50%", 0.5f));
            scaleMenu.DropDownItems.Add(ScaleItem("70%", 0.7f));
            scaleMenu.DropDownItems.Add(ScaleItem("80%", 0.8f));
            scaleMenu.DropDownItems.Add(ScaleItem("90%", 0.9f));
            scaleMenu.DropDownItems.Add(ScaleItem("100%", 1.0f));
            scaleMenu.DropDownItems.Add(ScaleItem("125%", 1.25f));
            scaleMenu.DropDownItems.Add(ScaleItem("150%", 1.5f));
            scaleMenu.DropDownItems.Add(ScaleItem("200%", 2.0f));
            context.Items.Add(scaleMenu);

            context.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem exit = StyledMenuItem("退出");
            exit.Click += delegate { Close(); };
            context.Items.Add(exit);
            return context;
        }

        private ContextMenuStrip BuildTrayMenu()
        {
            ContextMenuStrip context = new ContextMenuStrip();
            ConfigurePetMenu(context);
            context.Opening += delegate { UpdateSystemMenuChecks(); };

            trayTopMostMenuItem = ToggleItem("置顶", TopMost, ToggleTopMost);
            context.Items.Add(trayTopMostMenuItem);

            trayStartupMenuItem = ToggleItem("开机自启动", StartupManager.IsEnabled(), ToggleStartup);
            context.Items.Add(trayStartupMenuItem);

            trayMousePassthroughMenuItem = ToggleItem("鼠标穿透", mousePassthrough, ToggleMousePassthrough);
            context.Items.Add(trayMousePassthroughMenuItem);

            context.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem exit = StyledMenuItem("退出");
            exit.Click += delegate { Close(); };
            context.Items.Add(exit);
            return context;
        }

        private static void ConfigurePetMenu(ToolStrip menuStrip)
        {
            menuStrip.Renderer = PetMenuRenderer.Instance;
            menuStrip.BackColor = PetMenuRenderer.SurfaceColor;
            menuStrip.ForeColor = PetMenuRenderer.TextColor;
            menuStrip.Padding = new Padding(6, 7, 6, 7);
            menuStrip.Font = new Font("Microsoft YaHei UI", 9.25f, FontStyle.Regular, GraphicsUnit.Point);

            ToolStripDropDownMenu dropDown = menuStrip as ToolStripDropDownMenu;
            if (dropDown != null)
            {
                dropDown.ShowCheckMargin = true;
                dropDown.ShowImageMargin = false;
            }
        }

        private static ToolStripMenuItem StyledMenuItem(string label)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label);
            item.AutoSize = true;
            item.ForeColor = PetMenuRenderer.TextColor;
            item.Padding = new Padding(7, 5, 14, 5);
            return item;
        }

        private void ShowPanel()
        {
            if (panelForm == null || panelForm.IsDisposed)
            {
                panelForm = new FunctionalNoNoPanelForm(
                    delegate(string stateId, bool onceThenIdle) { Play(stateId, onceThenIdle); },
                    delegate { return codexMonitor.ForcePoll(); },
                    delegate { return followCodexStatus; },
                    delegate(bool enabled) { SetCodexFollow(enabled, true); },
                    delegate { return petAppearance; },
                    delegate(string appearanceId) { SetPetAppearance(PetAppearanceStore.Find(appearanceId), true); });
                panelForm.FormClosed += delegate { panelForm = null; };
            }

            if (!panelForm.Visible)
            {
                panelForm.StartPosition = FormStartPosition.Manual;
                panelForm.Location = PanelLocation(panelForm.Size);
                panelForm.Show();
            }

            if (panelForm.WindowState == FormWindowState.Minimized)
            {
                panelForm.WindowState = FormWindowState.Normal;
            }

            panelForm.TopMost = false;
            KeepFormInsideWorkingArea(panelForm, 12);
            panelForm.Activate();
            Play("review", true);
        }

        private static void KeepFormInsideWorkingArea(Form form, int margin)
        {
            if (form == null || form.IsDisposed || form.WindowState != FormWindowState.Normal)
            {
                return;
            }

            Rectangle area = Screen.FromControl(form).WorkingArea;
            int maxWidth = Math.Max(form.MinimumSize.Width, area.Width - margin * 2);
            int maxHeight = Math.Max(form.MinimumSize.Height, area.Height - margin * 2);
            int width = Math.Min(form.Width, maxWidth);
            int height = Math.Min(form.Height, maxHeight);
            int minX = area.Left + margin;
            int minY = area.Top + margin;
            int maxX = Math.Max(minX, area.Right - width - margin);
            int maxY = Math.Max(minY, area.Bottom - height - margin);
            int x = Math.Min(Math.Max(form.Left, minX), maxX);
            int y = Math.Min(Math.Max(form.Top, minY), maxY);

            form.Bounds = new Rectangle(x, y, width, height);
        }

        private void RegisterConfiguredHotkeys()
        {
            if (!IsHandleCreated)
            {
                return;
            }

            GlobalHotkeyManager.RegisterAll(Handle);
        }

        private void HandleHotkey(int id)
        {
            string action = GlobalHotkeyManager.GetAction(id);
            if (String.Equals(action, "show-panel", StringComparison.OrdinalIgnoreCase))
            {
                ShowPanel();
            }
            else if (String.Equals(action, "quick-launch", StringComparison.OrdinalIgnoreCase))
            {
                ShowQuickLauncher();
            }
            else if (String.Equals(action, "show-desktop", StringComparison.OrdinalIgnoreCase))
            {
                ShowDesktop();
            }
            else if (String.Equals(action, "toggle-passthrough", StringComparison.OrdinalIgnoreCase))
            {
                mousePassthrough = !mousePassthrough;
                UpdateSystemMenuChecks();
            }
            else if (String.Equals(action, "idle", StringComparison.OrdinalIgnoreCase))
            {
                Play("idle");
            }
            else if (String.Equals(action, "running", StringComparison.OrdinalIgnoreCase))
            {
                Play("running");
            }
            else if (String.Equals(action, "waiting", StringComparison.OrdinalIgnoreCase))
            {
                Play("waiting");
            }
            else if (String.Equals(action, "review", StringComparison.OrdinalIgnoreCase))
            {
                Play("review");
            }
            else if (String.Equals(action, "failed", StringComparison.OrdinalIgnoreCase))
            {
                Play("failed");
            }
            else if (String.Equals(action, "stop-agent", StringComparison.OrdinalIgnoreCase))
            {
                if (desktopAgent != null)
                {
                    desktopAgent.Stop();
                }
            }
        }

        private void ShowQuickLauncher()
        {
            if (launcherForm == null || launcherForm.IsDisposed)
            {
                launcherForm = new QuickLauncherForm();
                launcherForm.FormClosed += delegate { launcherForm = null; };
            }

            launcherForm.TopMost = TopMost;
            launcherForm.ShowLauncher(Screen.FromControl(this).WorkingArea);
            Play("waiting", true);
        }

        private void ShowWeather()
        {
            try
            {
                Play("waiting", true);
                WeatherDialog.ShowWeather(this);
            }
            catch (Exception ex)
            {
                ShowActionError("查看天气失败", ex);
            }
        }

        private void ShowDesktop()
        {
            try
            {
                NativeMethods.ShowDesktop();
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed)
                    {
                        return;
                    }

                    if (WindowState == FormWindowState.Minimized)
                    {
                        WindowState = FormWindowState.Normal;
                    }

                    Show();
                    Render();
                });
            }
            catch (Exception ex)
            {
                ShowActionError("回桌面失败", ex);
            }
        }

        private Point PanelLocation(Size panelSize)
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int x = Left - panelSize.Width - 18;
            if (x < area.Left + 12)
            {
                x = Right + 18;
            }

            if (x + panelSize.Width > area.Right - 12)
            {
                x = area.Right - panelSize.Width - 12;
            }
            if (x < area.Left + 12)
            {
                x = area.Left + 12;
            }

            int y = Top + (Height - panelSize.Height) / 2;
            if (y < area.Top + 12)
            {
                y = area.Top + 12;
            }

            if (y + panelSize.Height > area.Bottom - 12)
            {
                y = area.Bottom - panelSize.Height - 12;
            }
            if (y < area.Top + 12)
            {
                y = area.Top + 12;
            }

            return new Point(x, y);
        }

        private ToolStripMenuItem MenuItem(string label, string stateId)
        {
            ToolStripMenuItem item = StyledMenuItem(label);
            item.Click += delegate { Play(stateId, !String.Equals(stateId, "idle", StringComparison.OrdinalIgnoreCase)); };
            return item;
        }

        private ToolStripMenuItem ToggleItem(string label, bool isChecked, EventHandler onClick)
        {
            ToolStripMenuItem item = StyledMenuItem(label);
            item.Checked = isChecked;
            item.Click += onClick;
            return item;
        }

        private ToolStripMenuItem ScaleItem(string label, float value)
        {
            ToolStripMenuItem item = StyledMenuItem(label);
            item.Checked = Math.Abs(scale - value) < 0.01f;
            item.Click += delegate
            {
                scale = value;
                ClearLayeredFrameCache();
                foreach (ToolStripItem sibling in item.Owner.Items)
                {
                    ToolStripMenuItem siblingItem = sibling as ToolStripMenuItem;
                    if (siblingItem != null)
                    {
                        siblingItem.Checked = false;
                    }
                }

                item.Checked = true;
                Size = ScaledSize;
                PetScaleSettingsStore.Save(scale);
                Render();
            };
            return item;
        }

        private ToolStripMenuItem PetAppearanceItem(PetAppearance appearance)
        {
            ToolStripMenuItem item = StyledMenuItem(appearance.Name);
            item.Tag = appearance.Id;
            item.Checked = String.Equals(petAppearance.Id, appearance.Id, StringComparison.OrdinalIgnoreCase);
            item.Click += delegate
            {
                SetPetAppearance(PetAppearanceStore.Find((string)item.Tag), true);
            };
            return item;
        }

        private void SetPetAppearance(PetAppearance appearance, bool save)
        {
            if (appearance == null)
            {
                return;
            }

            petAppearance = appearance;
            ClearLayeredFrameCache();
            if (save)
            {
                PetAppearanceStore.Save(petAppearance);
            }

            UpdatePetAppearanceMenuChecks();
            Render();
        }

        private void UpdatePetAppearanceMenuChecks()
        {
            if (petAppearanceMenuItem == null)
            {
                return;
            }

            foreach (ToolStripItem item in petAppearanceMenuItem.DropDownItems)
            {
                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem == null || menuItem.Tag == null)
                {
                    continue;
                }

                menuItem.Checked = String.Equals((string)menuItem.Tag, petAppearance.Id, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void ToggleVoiceAssistant(object sender, EventArgs e)
        {
            if (voiceAssistant == null)
            {
                return;
            }

            bool enable = !voiceAssistant.IsEnabled;
            voiceAssistant.SetEnabled(enable);
            UpdateVoiceMenuChecks();
            if (!enable)
            {
                ResumeAnimationAfterVoice();
            }
        }

        private void StartManualVoiceCapture()
        {
            if (voiceAssistant == null)
            {
                return;
            }

            if (!voiceAssistant.IsEnabled)
            {
                voiceAssistant.SetEnabled(true);
            }

            voiceAssistant.StartCapture();
            UpdateVoiceMenuChecks();
        }

        private void ShowVoiceSettings()
        {
            if (voiceAssistant == null)
            {
                return;
            }

            VoiceAssistantSettings updated;
            if (VoiceSettingsDialog.Edit(this, voiceAssistant.Settings, out updated))
            {
                voiceAssistant.ApplySettings(updated);
                UpdateVoiceMenuChecks();
            }
        }

        private void ToggleDesktopAgent(object sender, EventArgs e)
        {
            if (desktopAgent == null)
            {
                return;
            }

            DesktopAgentSettings updated = desktopAgent.Settings;
            updated.Enabled = !updated.Enabled;
            desktopAgent.ApplySettings(updated, desktopAgent.Secrets);
            RestartVoiceForAgentRouting();
            UpdateDesktopAgentMenuChecks();
        }

        private void ShowDesktopAgentSettings()
        {
            if (desktopAgent == null)
            {
                return;
            }

            DesktopAgentSettings updated;
            DesktopAgentSecrets updatedSecrets;
            if (DesktopAgentSettingsDialog.Edit(
                this,
                desktopAgent.Settings,
                desktopAgent.Secrets,
                out updated,
                out updatedSecrets))
            {
                desktopAgent.ApplySettings(updated, updatedSecrets);
                RestartVoiceForAgentRouting();
                UpdateDesktopAgentMenuChecks();
            }
        }

        private async System.Threading.Tasks.Task StartDesktopAgentViewAsync()
        {
            if (!EnsureDesktopAgentReady())
            {
                return;
            }

            string question = DesktopAgentPromptDialog.Show(this, "查看整个屏幕", "你想让诺诺查看或解释屏幕上的什么内容？");
            if (question != null)
            {
                await desktopAgent.ReadScreenAsync(question, false);
            }
        }

        private async System.Threading.Tasks.Task StartDesktopAgentTaskAsync()
        {
            if (!EnsureComputerAgentReady())
            {
                return;
            }

            string goal = DesktopAgentPromptDialog.Show(this, "操作电脑", "描述要完成的任务；简单操作会直接执行，复杂任务由本机 Codex 调用受控电脑工具。");
            if (goal != null)
            {
                await desktopAgent.OperateComputerAsync(goal, false);
            }
        }

        private bool EnsureDesktopAgentReady()
        {
            if (desktopAgent == null)
            {
                return false;
            }

            if (!desktopAgent.IsEnabled || String.IsNullOrWhiteSpace(desktopAgent.Secrets.PrimaryApiKey))
            {
                ShowDesktopAgentSettings();
            }

            return desktopAgent.IsEnabled && !String.IsNullOrWhiteSpace(desktopAgent.Secrets.PrimaryApiKey);
        }

        private bool EnsureComputerAgentReady()
        {
            if (desktopAgent == null)
            {
                return false;
            }

            if (!desktopAgent.IsEnabled)
            {
                DesktopAgentSettings updated = desktopAgent.Settings;
                updated.Enabled = true;
                desktopAgent.ApplySettings(updated, desktopAgent.Secrets);
                UpdateDesktopAgentMenuChecks();
            }

            return desktopAgent.IsEnabled;
        }

        private void RestartVoiceForAgentRouting()
        {
            if (voiceAssistant != null && voiceAssistant.IsEnabled)
            {
                voiceAssistant.Restart();
            }
        }

        private void UpdateDesktopAgentMenuChecks()
        {
            bool enabled = desktopAgent != null
                ? desktopAgent.IsEnabled
                : DesktopAgentSettingsStore.Load().Enabled;
            bool busy = desktopAgent != null && desktopAgent.IsBusy;
            if (desktopAgentEnableMenuItem != null)
            {
                desktopAgentEnableMenuItem.Checked = enabled;
            }

            if (desktopAgentViewMenuItem != null)
            {
                desktopAgentViewMenuItem.Enabled = enabled && !busy;
            }

            if (desktopAgentOperateMenuItem != null)
            {
                desktopAgentOperateMenuItem.Enabled = enabled && !busy;
            }

            if (desktopAgentStopMenuItem != null)
            {
                desktopAgentStopMenuItem.Enabled = busy;
            }

            if (desktopAgentStateMenuItem != null)
            {
                string status = desktopAgent == null ? (enabled ? "就绪" : "已关闭") : desktopAgent.StatusText;
                desktopAgentStateMenuItem.Text = "当前: " + status;
            }
        }

        private void OnDesktopAgentEvent(object sender, DesktopAgentEventArgs e)
        {
            UpdateDesktopAgentMenuChecks();
            if (String.Equals(e.Type, "state", StringComparison.OrdinalIgnoreCase))
            {
                Play(DesktopAgentAnimationState(e.State));
                return;
            }

            if (String.Equals(e.Type, "answer", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(e.Type, "approval", StringComparison.OrdinalIgnoreCase))
            {
                if (e.Speak)
                {
                    SpeakVoiceAnswer(e.Message);
                }
                else
                {
                    Play(String.Equals(e.Type, "approval", StringComparison.OrdinalIgnoreCase) ? "review" : "jumping", true);
                    ShowVoiceCaption(e.Message, false);
                }

                return;
            }

            if (String.Equals(e.Type, "error", StringComparison.OrdinalIgnoreCase))
            {
                Play("failed");
                ShowVoiceCaption(e.Message, true);
                if (e.Speak)
                {
                    SpeakVoiceAnswer(e.Message);
                }
            }
        }

        private static string DesktopAgentAnimationState(string state)
        {
            switch (state)
            {
                case "observing":
                case "planning":
                case "acting":
                    return "running";
                case "verifying":
                case "approval":
                    return "review";
                case "error":
                    return "failed";
                case "complete":
                    return "jumping";
                default:
                    return "idle";
            }
        }

        private void OpenMicrophonePrivacySettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowActionError("打开麦克风隐私设置失败", ex);
            }
        }

        private void UpdateVoiceMenuChecks()
        {
            bool enabled = voiceAssistant != null
                ? voiceAssistant.IsEnabled
                : VoiceSettingsStore.Load().Enabled;
            if (voiceEnableMenuItem != null)
            {
                voiceEnableMenuItem.Checked = enabled;
            }

            if (voiceManualTalkMenuItem != null)
            {
                voiceManualTalkMenuItem.Enabled = enabled;
            }

            if (voiceStateMenuItem != null)
            {
                string status = voiceAssistant == null ? (enabled ? "等待启动" : "已关闭") : voiceAssistant.StatusText;
                voiceStateMenuItem.Text = "当前: " + status;
            }
        }

        private void OnVoiceAssistantEvent(object sender, VoiceAssistantEventArgs e)
        {
            UpdateVoiceMenuChecks();
            if (String.Equals(e.Type, "state", StringComparison.OrdinalIgnoreCase))
            {
                Play(VoiceAnimationState(e.State));
                return;
            }

            if (String.Equals(e.Type, "ready", StringComparison.OrdinalIgnoreCase))
            {
                Play("waiting");
                return;
            }

            if (String.Equals(e.Type, "wake", StringComparison.OrdinalIgnoreCase))
            {
                ShowVoiceCaption("我在听。", false);
                Play("waiting");
                return;
            }

            if (String.Equals(e.Type, "question", StringComparison.OrdinalIgnoreCase))
            {
                VoiceAssistantSettings voiceSettings = voiceAssistant.Settings;
                if (voiceSettings.CommandCaptionEnabled)
                {
                    ShowVoiceCaption("你：" + e.Text, false);
                }

                VoiceCommandRoute quickRoute = VoiceCommandRouter.Route(e.Text);
                if (voiceSettings.CommandRoutingEnabled || quickRoute.Intent == VoiceIntentType.Control)
                {
                    Play("running");
                    HandleVoiceCommand(e);
                }

                return;
            }

            if (String.Equals(e.Type, "answer", StringComparison.OrdinalIgnoreCase))
            {
                SpeakVoiceAnswer(e.Text);
                return;
            }

            if (String.Equals(e.Type, "tts_error", StringComparison.OrdinalIgnoreCase))
            {
                SpeakVoiceFallback(e.Text);
                return;
            }

            if (String.Equals(e.Type, "speech_finished", StringComparison.OrdinalIgnoreCase))
            {
                Play("waiting");
                return;
            }

            if (String.Equals(e.Type, "warning", StringComparison.OrdinalIgnoreCase))
            {
                ShowVoiceCaption(e.Message, true);
                return;
            }

            if (String.Equals(e.Type, "history_cleared", StringComparison.OrdinalIgnoreCase))
            {
                ShowVoiceCaption("对话上下文已清空。", false);
                return;
            }

            if (String.Equals(e.Type, "error", StringComparison.OrdinalIgnoreCase))
            {
                Play("failed");
                ShowVoiceCaption(e.Message, true);
            }
        }

        private async void HandleVoiceCommand(VoiceAssistantEventArgs e)
        {
            try
            {
                string requestKey = String.IsNullOrWhiteSpace(e.RequestId)
                    ? VoiceCommandRouter.Normalize(e.Text)
                    : e.RequestId.Trim();
                DateTime now = DateTime.UtcNow;
                if (requestKey.Length > 0 && String.Equals(lastVoiceRequestKey, requestKey, StringComparison.Ordinal) &&
                    (now - lastVoiceRequestAtUtc).TotalSeconds < 2)
                {
                    return;
                }

                lastVoiceRequestKey = requestKey;
                lastVoiceRequestAtUtc = now;

                Stopwatch routingStopwatch = Stopwatch.StartNew();
                VoiceCommandRoute route = VoiceCommandRouter.Route(e.Text);
                if (!String.IsNullOrWhiteSpace(pendingVoiceClarificationPrefix) &&
                    now > pendingVoiceClarificationExpiresAtUtc)
                {
                    pendingVoiceClarificationPrefix = "";
                    pendingVoiceClarificationExpiresAtUtc = DateTime.MinValue;
                }

                if (!String.IsNullOrWhiteSpace(pendingVoiceClarificationPrefix) &&
                    now <= pendingVoiceClarificationExpiresAtUtc)
                {
                    if (route.Intent == VoiceIntentType.Control &&
                        (route.Control == VoiceControlCommand.Reject || route.Control == VoiceControlCommand.Stop))
                    {
                        pendingVoiceClarificationPrefix = "";
                        pendingVoiceClarificationExpiresAtUtc = DateTime.MinValue;
                        if (route.Control == VoiceControlCommand.Reject)
                        {
                            SpeakVoiceAnswer("好的，已取消。" );
                            return;
                        }
                    }
                    else if (route.Intent == VoiceIntentType.Conversation && route.NormalizedText.Length <= 40)
                    {
                        string continuedGoal = pendingVoiceClarificationPrefix + route.NormalizedText;
                        pendingVoiceClarificationPrefix = "";
                        pendingVoiceClarificationExpiresAtUtc = DateTime.MinValue;
                        route = VoiceCommandRouter.Route(continuedGoal);
                        route.Goal = continuedGoal;
                    }
                    else if (route.Intent != VoiceIntentType.Clarify)
                    {
                        pendingVoiceClarificationPrefix = "";
                        pendingVoiceClarificationExpiresAtUtc = DateTime.MinValue;
                    }
                }

                AgentAuditLog.Write(
                    "voice-route",
                    "ms=" + routingStopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                    "; intent=" + route.Intent +
                    "; source=" + route.Source +
                    "; confidence=" + route.Confidence.ToString("0.00", CultureInfo.InvariantCulture));
                if (route.Intent == VoiceIntentType.PetAction)
                {
                    string answer = ExecuteLocalVoiceCommand(route.LocalCommand);
                    SpeakVoiceAnswer(answer);
                    return;
                }

                if (route.Intent == VoiceIntentType.Clarify)
                {
                    if (!String.IsNullOrWhiteSpace(route.ContinuationPrefix))
                    {
                        pendingVoiceClarificationPrefix = route.ContinuationPrefix;
                        pendingVoiceClarificationExpiresAtUtc = DateTime.UtcNow.AddSeconds(20);
                    }

                    SpeakVoiceAnswer(String.IsNullOrWhiteSpace(route.ResponseText)
                        ? "请把指令说得更具体一些。"
                        : route.ResponseText);
                    return;
                }

                if (route.Intent == VoiceIntentType.Conversation &&
                    (desktopAgent == null || !desktopAgent.CanHandleVoice))
                {
                    if (!voiceAssistant.AskLocalConversation(route.Goal, e.RequestId))
                    {
                        SpeakVoiceAnswer("本地对话服务暂时不可用，请稍后重试。");
                    }

                    return;
                }

                if (route.Intent == VoiceIntentType.ScreenRead &&
                    (desktopAgent == null || !desktopAgent.CanHandleVoice))
                {
                    SpeakVoiceAnswer("屏幕助手尚未启用，请先配置云端模型。");
                    return;
                }

                if (route.Intent == VoiceIntentType.ComputerAction &&
                    (desktopAgent == null || !desktopAgent.CanHandleComputerVoice))
                {
                    SpeakVoiceAnswer("电脑助手尚未启用。");
                    return;
                }

                if (desktopAgent != null)
                {
                    await desktopAgent.HandleVoiceRouteAsync(route);
                }
            }
            catch (Exception ex)
            {
                Play("failed");
                ShowVoiceCaption("电脑助手处理失败：" + ex.Message, true);
                SpeakVoiceAnswer("电脑助手处理失败：" + ex.Message);
            }
        }

        private string ExecuteLocalVoiceCommand(VoiceLocalCommand command)
        {
            switch (command)
            {
                case VoiceLocalCommand.Idle:
                    Play("idle");
                    return "好的，我休息一下。";
                case VoiceLocalCommand.Wave:
                    Play("waving", true);
                    return "你好。";
                case VoiceLocalCommand.Jump:
                    Play("jumping", true);
                    return "好。";
                case VoiceLocalCommand.ShowDesktop:
                    ShowDesktop();
                    return "已经回到桌面。";
                case VoiceLocalCommand.ShowPanel:
                    ShowPanel();
                    return "面板已经打开。";
                case VoiceLocalCommand.ShowQuickLauncher:
                    ShowQuickLauncher();
                    return "快速直达已经打开。";
                case VoiceLocalCommand.OpenNotepad:
                    OpenTrustedLocalTarget("notepad.exe");
                    return "记事本已经打开。";
                case VoiceLocalCommand.OpenCalculator:
                    OpenTrustedLocalTarget("calc.exe");
                    return "计算器已经打开。";
                case VoiceLocalCommand.OpenFileExplorer:
                    OpenTrustedLocalTarget("explorer.exe");
                    return "文件资源管理器已经打开。";
                case VoiceLocalCommand.OpenWindowsSettings:
                    OpenTrustedLocalTarget("ms-settings:");
                    return "系统设置已经打开。";
                default:
                    throw new InvalidOperationException("不支持的本地语音指令。");
            }
        }

        private static void OpenTrustedLocalTarget(string target)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = target;
            startInfo.UseShellExecute = true;
            Process.Start(startInfo);
        }

        private void SpeakVoiceAnswer(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                if (voiceAssistant != null)
                {
                    voiceAssistant.NotifySpeechDone();
                }
                return;
            }

            VoiceAssistantSettings settings = voiceAssistant.Settings;
            Play("review");
            ShowVoiceCaption(text, false);
            if (!voiceAssistant.Speak(text, settings.TtsRate))
            {
                SpeakVoiceFallback(text);
            }
        }

        private void SpeakVoiceFallback(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                if (voiceAssistant != null)
                {
                    voiceAssistant.NotifySpeechDone();
                }
                return;
            }

            VoiceAssistantSettings settings = voiceAssistant.Settings;
            voiceAssistant.NotifySpeechStarted();
            if (voiceSpeechPlayer == null)
            {
                try
                {
                    voiceSpeechPlayer = new LocalSpeechPlayer();
                }
                catch (Exception ex)
                {
                    voiceAssistant.NotifySpeechDone();
                    ShowVoiceCaption(ex.Message, true);
                    Play("failed");
                    return;
                }
            }

            voiceSpeechPlayer.Speak(text, settings.TtsRate, delegate(Exception error)
            {
                if (voiceAssistant != null)
                {
                    voiceAssistant.NotifySpeechDone();
                }

                if (error != null && !IsDisposed && IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (!IsDisposed)
                            {
                                ShowVoiceCaption("系统语音朗读失败：" + error.Message, true);
                                Play("failed");
                            }
                        });
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            });
        }

        private void ShowVoiceCaption(string text, bool error)
        {
            if (voiceCaption == null || voiceCaption.IsDisposed || String.IsNullOrWhiteSpace(text))
            {
                return;
            }

            int seconds = voiceAssistant == null ? 12 : voiceAssistant.Settings.CaptionSeconds;
            voiceCaption.ShowCaption(this, text, seconds, error);
        }

        private void ResumeAnimationAfterVoice()
        {
            if (followCodexStatus)
            {
                TickCodexMonitor(true);
            }
            else
            {
                Play("idle");
            }
        }

        private static string VoiceAnimationState(string voiceState)
        {
            switch (voiceState)
            {
                case "loading":
                case "transcribing":
                case "thinking":
                    return "running";
                case "speaking":
                    return "review";
                case "error":
                    return "failed";
                case "stopped":
                    return "idle";
                case "listening_wake":
                case "listening_command":
                case "listening_followup":
                default:
                    return "waiting";
            }
        }

        private void ToggleCodexFollow(object sender, EventArgs e)
        {
            ToolStripMenuItem item = (ToolStripMenuItem)sender;
            SetCodexFollow(!item.Checked, true);
        }

        private void SetCodexFollow(bool enabled, bool forcePoll)
        {
            followCodexStatus = enabled;
            CodexMonitorSettingsStore.SaveEnabled(enabled);
            UpdateCodexStatusMenu(codexMonitor.Current);
            if (forcePoll)
            {
                TickCodexMonitor(true);
            }
        }

        private void TickCodexMonitor(bool force)
        {
            if (System.Threading.Interlocked.CompareExchange(ref codexPollInProgress, 1, 0) != 0)
            {
                pendingForcedCodexPoll |= force;
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (!force && !codexMonitor.IsPollDue(now))
            {
                System.Threading.Interlocked.Exchange(ref codexPollInProgress, 0);
                return;
            }

            Task.Run<CodexActivitySnapshot>(delegate
            {
                return force ? codexMonitor.ForcePoll() : codexMonitor.Poll(now);
            }).ContinueWith(delegate(Task<CodexActivitySnapshot> completed)
            {
                CodexActivitySnapshot snapshot = completed.Status == TaskStatus.RanToCompletion
                    ? completed.Result
                    : codexMonitor.Current;

                try
                {
                    if (!IsDisposed && !Disposing && IsHandleCreated)
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            System.Threading.Interlocked.Exchange(ref codexPollInProgress, 0);
                            ApplyCodexSnapshot(snapshot);
                            if (pendingForcedCodexPoll)
                            {
                                pendingForcedCodexPoll = false;
                                TickCodexMonitor(true);
                            }
                        });
                        return;
                    }
                }
                catch
                {
                }

                System.Threading.Interlocked.Exchange(ref codexPollInProgress, 0);
            }, TaskScheduler.Default);
        }

        private void ApplyCodexSnapshot(CodexActivitySnapshot snapshot)
        {
            UpdateCodexStatusMenu(snapshot);
            if (!followCodexStatus)
            {
                return;
            }

            if (voiceAssistant != null && voiceAssistant.IsAnimationActive)
            {
                return;
            }

            if (dragging || playOnceThenIdle || state.OneShot)
            {
                return;
            }

            if (snapshot == null || String.IsNullOrEmpty(snapshot.StateId))
            {
                return;
            }

            if (String.Equals(state.Id, snapshot.StateId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Play(snapshot.StateId);
        }

        private void UpdateCodexStatusMenu(CodexActivitySnapshot snapshot)
        {
            if (codexFollowMenuItem != null)
            {
                codexFollowMenuItem.Checked = followCodexStatus;
            }

            if (codexStateMenuItem != null)
            {
                string label = snapshot == null ? "未知" : snapshot.Label;
                string detail = snapshot == null ? "" : snapshot.Detail;
                codexStateMenuItem.Text = String.IsNullOrWhiteSpace(detail)
                    ? "当前: " + label
                    : "当前: " + label + " · " + detail;
            }
        }

        private void UpdateSystemMenuChecks()
        {
            if (topMostMenuItem != null)
            {
                topMostMenuItem.Checked = TopMost;
            }

            if (trayTopMostMenuItem != null)
            {
                trayTopMostMenuItem.Checked = TopMost;
            }

            if (startupMenuItem != null)
            {
                startupMenuItem.Checked = StartupManager.IsEnabled();
            }

            if (trayStartupMenuItem != null)
            {
                trayStartupMenuItem.Checked = StartupManager.IsEnabled();
            }

            if (desktopIconsMenuItem != null)
            {
                desktopIconsMenuItem.Checked = !NativeMethods.AreDesktopIconsVisible();
            }

            if (taskbarMenuItem != null)
            {
                taskbarMenuItem.Checked = !NativeMethods.AreTaskbarsVisible();
            }

            if (mousePassthroughMenuItem != null)
            {
                mousePassthroughMenuItem.Checked = mousePassthrough;
            }

            if (trayMousePassthroughMenuItem != null)
            {
                trayMousePassthroughMenuItem.Checked = mousePassthrough;
            }

            UpdateVoiceMenuChecks();

            UpdatePetAppearanceMenuChecks();
            UpdateCodexStatusMenu(codexMonitor.Current);
        }

        private void ToggleTopMost(object sender, EventArgs e)
        {
            ToolStripMenuItem item = (ToolStripMenuItem)sender;
            TopMost = !item.Checked;
            UpdateSystemMenuChecks();
        }

        private void ToggleStartup(object sender, EventArgs e)
        {
            ToolStripMenuItem item = (ToolStripMenuItem)sender;
            bool enabled = !item.Checked;
            try
            {
                StartupManager.SetEnabled(enabled);
                UpdateSystemMenuChecks();
            }
            catch (Exception ex)
            {
                UpdateSystemMenuChecks();
                ShowActionError("更新开机自启动失败", ex);
            }
        }

        private void ToggleDesktopIcons(object sender, EventArgs e)
        {
            ToolStripMenuItem item = (ToolStripMenuItem)sender;
            bool hide = !item.Checked;
            try
            {
                if (!NativeMethods.SetDesktopIconsVisible(!hide))
                {
                    throw new InvalidOperationException("未找到桌面图标窗口。");
                }

                item.Checked = hide;
            }
            catch (Exception ex)
            {
                item.Checked = !NativeMethods.AreDesktopIconsVisible();
                ShowActionError("切换桌面图标失败", ex);
            }
        }

        private void ToggleTaskbar(object sender, EventArgs e)
        {
            ToolStripMenuItem item = (ToolStripMenuItem)sender;
            bool hide = !item.Checked;
            try
            {
                if (!NativeMethods.SetTaskbarsVisible(!hide))
                {
                    throw new InvalidOperationException("未找到任务栏窗口。");
                }

                item.Checked = hide;
            }
            catch (Exception ex)
            {
                item.Checked = !NativeMethods.AreTaskbarsVisible();
                ShowActionError("切换任务栏失败", ex);
            }
        }

        private void ToggleMousePassthrough(object sender, EventArgs e)
        {
            ToolStripMenuItem item = (ToolStripMenuItem)sender;
            mousePassthrough = !item.Checked;
            UpdateSystemMenuChecks();
        }

        private async Task EmptyRecycleBinAsync()
        {
            if (emptyRecycleBinTask != null)
            {
                return;
            }

            DialogResult result = MessageBox.Show(
                this,
                "确定要清空回收站吗？",
                "诺诺",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (result != DialogResult.Yes)
            {
                return;
            }

            emptyRecycleBinMenuItem.Enabled = false;
            emptyRecycleBinMenuItem.Text = "正在清空回收站...";

            Task operation = Task.Factory.StartNew(
                delegate { NativeMethods.EmptyRecycleBin(); },
                System.Threading.CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            emptyRecycleBinTask = operation;

            try
            {
                await operation;
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                {
                    ShowActionError("清空回收站失败", ex);
                }
            }
            finally
            {
                emptyRecycleBinTask = null;
                if (!IsDisposed &&
                    !Disposing &&
                    emptyRecycleBinMenuItem != null &&
                    !emptyRecycleBinMenuItem.IsDisposed)
                {
                    emptyRecycleBinMenuItem.Text = "清空回收站";
                    emptyRecycleBinMenuItem.Enabled = true;
                }
            }
        }

        private void ShowActionError(string title, Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                title,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        private void Play(string stateId)
        {
            Play(stateId, false);
        }

        private void Play(string stateId, bool onceThenIdle)
        {
            state = atlas.GetState(stateId);
            frameIndex = 0;
            lastFrameTickMs = NativeMethods.GetTickCount64();
            playOnceThenIdle = onceThenIdle;
            UpdateAnimationTimerInterval();
            Render();
        }

        private static int AnimationTimerInterval(AnimationState animationState)
        {
            if (animationState == null)
            {
                return 100;
            }

            int interval = animationState.FrameMs;
            if (interval < AnimationTimerMinMs)
            {
                return AnimationTimerMinMs;
            }

            if (interval > AnimationTimerMaxMs)
            {
                return AnimationTimerMaxMs;
            }

            return interval;
        }

        private void UpdateAnimationTimerInterval()
        {
            if (timer == null)
            {
                return;
            }

            int interval = AnimationTimerInterval(state);
            if (timer.Interval != interval)
            {
                timer.Interval = interval;
            }
        }

        private void TickAnimation()
        {
            ulong now = NativeMethods.GetTickCount64();
            ulong elapsed = now - lastFrameTickMs;
            if (elapsed < (ulong)state.FrameMs)
            {
                return;
            }

            ulong elapsedFrames = elapsed / (ulong)state.FrameMs;
            lastFrameTickMs += elapsedFrames * (ulong)state.FrameMs;
            int previousFrame = frameIndex;
            AnimationState previousState = state;
            if ((state.OneShot || playOnceThenIdle) && elapsedFrames >= (ulong)(state.FrameCount - frameIndex))
            {
                state = atlas.GetState("idle");
                frameIndex = 0;
                playOnceThenIdle = false;
                lastFrameTickMs = now;
                UpdateAnimationTimerInterval();
            }
            else
            {
                int frameAdvance = (int)(elapsedFrames % (ulong)state.FrameCount);
                frameIndex = (frameIndex + frameAdvance) % state.FrameCount;
            }

            if (!ReferenceEquals(previousState, state) || previousFrame != frameIndex)
            {
                Render();
            }
        }

        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            dragging = true;
            suppressDragState = false;
            playOnceThenIdle = false;
            dragCursor = Cursor.Position;
            dragWindow = Location;
            Capture = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!dragging)
            {
                return;
            }

            Point cursor = Cursor.Position;
            int dx = cursor.X - dragCursor.X;
            int dy = cursor.Y - dragCursor.Y;
            Location = new Point(dragWindow.X + dx, dragWindow.Y + dy);

            if (!suppressDragState)
            {
                if (Math.Abs(dx) > 6)
                {
                    AnimationState next = atlas.GetState(dx > 0 ? "running-right" : "running-left");
                    if (!ReferenceEquals(next, state))
                    {
                        state = next;
                        frameIndex = 0;
                        playOnceThenIdle = false;
                        lastFrameTickMs = NativeMethods.GetTickCount64();
                        UpdateAnimationTimerInterval();
                        Render();
                    }
                }
            }

        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            dragging = false;
            suppressDragState = true;
            Capture = false;
            Play("idle");
        }

        private void Render()
        {
            Size size = ScaledSize;
            LayeredFrame layeredFrame = GetLayeredFrame(state, frameIndex, size);
            NativeMethods.UpdateLayeredBitmap(Handle, layeredFrame.BitmapHandle, layeredFrame.Size, Location);
        }

        private LayeredFrame GetLayeredFrame(AnimationState animationState, int index, Size size)
        {
            int normalizedIndex = index % animationState.FrameCount;
            string appearanceId = petAppearance == null ? "classic" : petAppearance.Id;
            string key = animationState.Id + ":" + normalizedIndex.ToString(CultureInfo.InvariantCulture) + ":" +
                appearanceId + ":" + size.Width.ToString(CultureInfo.InvariantCulture) + "x" + size.Height.ToString(CultureInfo.InvariantCulture);
            LayeredFrame cached;
            if (layeredFrameCache.TryGetValue(key, out cached))
            {
                return cached;
            }

            bool ownsPrepared;
            Bitmap prepared = PrepareDisplayFrame(animationState, normalizedIndex, out ownsPrepared);
            Bitmap finalBitmap = prepared;
            bool ownsFinal = ownsPrepared;
            try
            {
                if (prepared.Size != size)
                {
                    finalBitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                    ownsFinal = true;
                    using (Graphics graphics = Graphics.FromImage(finalBitmap))
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.Clear(Color.Transparent);
                        graphics.CompositingMode = CompositingMode.SourceOver;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.DrawImage(prepared, new Rectangle(Point.Empty, size));
                    }

                    if (ownsPrepared)
                    {
                        prepared.Dispose();
                        ownsPrepared = false;
                    }
                }

                LayeredFrame layeredFrame = new LayeredFrame(finalBitmap);
                layeredFrameCache[key] = layeredFrame;
                return layeredFrame;
            }
            finally
            {
                if (ownsFinal)
                {
                    finalBitmap.Dispose();
                }
                else if (ownsPrepared)
                {
                    prepared.Dispose();
                }
            }
        }

        private Bitmap PrepareDisplayFrame(AnimationState animationState, int index, out bool ownsBitmap)
        {
            Bitmap prepared = atlas.GetFrameReference(animationState, index);
            ownsBitmap = false;
            if (ShouldNormalizeJumpFrame(animationState))
            {
                prepared = NormalizeJumpFrame(prepared, index);
                ownsBitmap = true;
            }

            string appearanceId = petAppearance == null ? "classic" : petAppearance.Id;
            if (!String.Equals(appearanceId, "classic", StringComparison.OrdinalIgnoreCase))
            {
                Bitmap transformed = PetAppearanceRenderer.Apply(prepared, petAppearance);
                if (ownsBitmap)
                {
                    prepared.Dispose();
                }

                prepared = transformed;
                ownsBitmap = true;
            }

            return prepared;
        }

        private void ClearLayeredFrameCache()
        {
            if (layeredFrameCache == null)
            {
                return;
            }

            foreach (LayeredFrame frame in layeredFrameCache.Values)
            {
                frame.Dispose();
            }

            layeredFrameCache.Clear();
        }

        private static bool ShouldNormalizeJumpFrame(AnimationState animationState)
        {
            return String.Equals(animationState.Id, "jumping", StringComparison.OrdinalIgnoreCase);
        }

        private static Bitmap NormalizeJumpFrame(Bitmap source, int index)
        {
            Rectangle contentBounds;
            if (!TryGetOpaqueBounds(source, out contentBounds))
            {
                return (Bitmap)source.Clone();
            }

            Bitmap output = new Bitmap(PetAtlas.CellWidth, PetAtlas.CellHeight, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(output))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;

                int left = (PetAtlas.CellWidth - JumpTargetWidth) / 2;
                int top = JumpFrameTopOffsets[Math.Min(index, JumpFrameTopOffsets.Length - 1)];
                Rectangle targetBounds = new Rectangle(left, top, JumpTargetWidth, JumpTargetHeight);
                graphics.DrawImage(source, targetBounds, contentBounds, GraphicsUnit.Pixel);
            }

            return output;
        }

        private static bool TryGetOpaqueBounds(Bitmap image, out Rectangle bounds)
        {
            int left = image.Width;
            int top = image.Height;
            int right = -1;
            int bottom = -1;
            Rectangle full = new Rectangle(0, 0, image.Width, image.Height);
            BitmapData data = image.LockBits(full, ImageLockMode.ReadOnly, image.PixelFormat);
            try
            {
                int rowBytes = image.Width * 4;
                byte[] buffer = new byte[rowBytes];
                for (int y = 0; y < image.Height; y++)
                {
                    IntPtr rowPointer = new IntPtr(data.Scan0.ToInt64() + y * data.Stride);
                    Marshal.Copy(rowPointer, buffer, 0, rowBytes);
                    for (int x = 0; x < image.Width; x++)
                    {
                        int alphaIndex = x * 4 + 3;
                        if (buffer[alphaIndex] == 0)
                        {
                            continue;
                        }

                        if (x < left)
                        {
                            left = x;
                        }

                        if (x > right)
                        {
                            right = x;
                        }

                        if (y < top)
                        {
                            top = y;
                        }

                        if (y > bottom)
                        {
                            bottom = y;
                        }
                    }
                }
            }
            finally
            {
                image.UnlockBits(data);
            }

            if (right < left || bottom < top)
            {
                bounds = Rectangle.Empty;
                return false;
            }

            bounds = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            return true;
        }
    }

    internal sealed class FunctionalNoNoPanelForm : Form
    {
        private readonly List<PanelFeature> features;
        private readonly List<Button> navButtons;
        private readonly Panel contentPanel;
        private readonly Timer systemTimer;
        private readonly Timer clipboardTimer;
        private readonly Action<string, bool> petAction;
        private readonly Func<CodexActivitySnapshot> codexStatusProvider;
        private readonly Func<bool> codexFollowGetter;
        private readonly Action<bool> codexFollowSetter;
        private readonly Func<PetAppearance> petAppearanceGetter;
        private readonly Action<string> petAppearanceSetter;
        private PanelTheme currentTheme;
        private Panel navPanel;
        private Label brandLabel;
        private Label panelSubtitleLabel;
        private PerformanceCounter cpuCounter;
        private int selectedIndex;
        private Label cpuLabel;
        private Label memoryLabel;
        private Label diskLabel;
        private Label codexStateLabel;
        private CheckBox codexFollowCheck;
        private Label colorLabel;
        private Panel colorSwatch;
        private ListView targetList;
        private TextBox targetQueryBox;
        private ListView textClipboardList;
        private ListView imageClipboardList;
        private Label clipboardTypeLabel;
        private Label clipboardLengthLabel;
        private Label clipboardTimeLabel;
        private TextBox clipboardContentBox;
        private TextBox clipboardOcrBox;
        private PictureBox clipboardPictureBox;
        private Button copyClipboardButton;
        private Button saveClipboardImageButton;
        private Button copyOcrButton;
        private ClipboardHistoryItem selectedClipboardItem;
        private TreeView notesTree;
        private TextBox noteContentBox;
        private Label noteDetailsLabel;
        private string selectedNotePath;
        private readonly Icon windowIcon;

        public FunctionalNoNoPanelForm()
            : this(null, null, null, null, null, null)
        {
        }

        public FunctionalNoNoPanelForm(Action<string, bool> petAction)
            : this(petAction, null, null, null, null, null)
        {
        }

        public FunctionalNoNoPanelForm(Action<string, bool> petAction, Func<CodexActivitySnapshot> codexStatusProvider, Func<bool> codexFollowGetter, Action<bool> codexFollowSetter)
            : this(petAction, codexStatusProvider, codexFollowGetter, codexFollowSetter, null, null)
        {
        }

        public FunctionalNoNoPanelForm(Action<string, bool> petAction, Func<CodexActivitySnapshot> codexStatusProvider, Func<bool> codexFollowGetter, Action<bool> codexFollowSetter, Func<PetAppearance> petAppearanceGetter, Action<string> petAppearanceSetter)
        {
            this.petAction = petAction;
            this.codexStatusProvider = codexStatusProvider;
            this.codexFollowGetter = codexFollowGetter;
            this.codexFollowSetter = codexFollowSetter;
            this.petAppearanceGetter = petAppearanceGetter;
            this.petAppearanceSetter = petAppearanceSetter;
            features = new List<PanelFeature>();
            navButtons = new List<Button>();
            ClipboardSessionHistory.EnsureLoaded();
            ClipboardSessionHistory.Changed += OnClipboardSessionHistoryChanged;
            windowIcon = PetWindowIcon.Create();
            currentTheme = PanelThemeStore.Load();

            Text = "诺诺面板";
            Icon = windowIcon;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = true;
            MaximizeBox = true;
            ShowInTaskbar = true;
            TopMost = false;
            MinimumSize = new Size(940, 620);
            Size = new Size(1080, 720);
            BackColor = currentTheme.WindowBack;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            features.Add(new PanelFeature("网址直达", "保存关键词与网址", PanelGlyph.Link, "保存常用网页入口。"));
            features.Add(new PanelFeature("应用启动", "保存关键词与目标", PanelGlyph.App, "保存程序、路径、命令或网址。"));
            features.Add(new PanelFeature("工具箱", "系统、取色、端口进程", PanelGlyph.Tool, "查看系统信息并处理端口或进程。"));
            features.Add(new PanelFeature("剪贴板", "本次开机文本与图片", PanelGlyph.Clipboard, "显示本次开机期间复制的内容。"));
            features.Add(new PanelFeature("便签", "目录树与内容", PanelGlyph.Note, "按目录管理便签。"));
            features.Add(new PanelFeature("外观", "主题套装与配色", PanelGlyph.Appearance, "切换面板的整体外观套装。"));
            features.Add(new PanelFeature("设置", "快捷键与行为", PanelGlyph.Settings, "控制全局快捷键设置。"));

            TableLayoutPanel shell = new TableLayoutPanel();
            shell.Dock = DockStyle.Fill;
            shell.ColumnCount = 2;
            shell.RowCount = 1;
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220F));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            shell.BackColor = BackColor;
            Controls.Add(shell);

            navPanel = new Panel();
            navPanel.Dock = DockStyle.Fill;
            navPanel.Padding = new Padding(14, 16, 14, 16);
            navPanel.BackColor = currentTheme.NavBack;
            shell.Controls.Add(navPanel, 0, 0);

            brandLabel = new Label();
            brandLabel.Text = "诺诺";
            brandLabel.ForeColor = currentTheme.HeaderText;
            brandLabel.Font = new Font(Font.FontFamily, 13F, FontStyle.Bold);
            brandLabel.Dock = DockStyle.Top;
            brandLabel.Height = 28;
            navPanel.Controls.Add(brandLabel);

            panelSubtitleLabel = new Label();
            panelSubtitleLabel.Text = "桌面控制面板";
            panelSubtitleLabel.ForeColor = currentTheme.MutedText;
            panelSubtitleLabel.Dock = DockStyle.Top;
            panelSubtitleLabel.Height = 24;
            navPanel.Controls.Add(panelSubtitleLabel);

            FlowLayoutPanel navList = new FlowLayoutPanel();
            navList.Dock = DockStyle.Fill;
            navList.Top = 62;
            navList.FlowDirection = FlowDirection.TopDown;
            navList.WrapContents = false;
            navList.AutoScroll = true;
            navList.Padding = new Padding(0, 18, 0, 0);
            navPanel.Controls.Add(navList);
            navList.BringToFront();

            for (int i = 0; i < features.Count; i++)
            {
                Button button = CreateNavButton(features[i], i);
                navButtons.Add(button);
                navList.Controls.Add(button);
            }

            contentPanel = new Panel();
            contentPanel.Dock = DockStyle.Fill;
            contentPanel.Padding = new Padding(18);
            contentPanel.BackColor = currentTheme.ContentBack;
            shell.Controls.Add(contentPanel, 1, 0);

            systemTimer = new Timer();
            systemTimer.Interval = 2000;
            systemTimer.Tick += delegate { RefreshSystemInfo(); };

            clipboardTimer = new Timer();
            clipboardTimer.Interval = 900;
            clipboardTimer.Tick += delegate { CaptureClipboardSnapshot(); };

            SelectFeature(0);
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);

            if (cpuCounter != null)
            {
                return;
            }

            PerformanceCounter initializedCounter = await Task.Run<PerformanceCounter>(delegate
            {
                try
                {
                    PerformanceCounter counter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                    counter.NextValue();
                    return counter;
                }
                catch
                {
                    return null;
                }
            });

            if (IsDisposed)
            {
                if (initializedCounter != null)
                {
                    initializedCounter.Dispose();
                }

                return;
            }

            cpuCounter = initializedCounter;
            if (selectedIndex == 2)
            {
                RefreshSystemInfo();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                systemTimer.Dispose();
                clipboardTimer.Dispose();
                ClipboardSessionHistory.Changed -= OnClipboardSessionHistoryChanged;
                if (cpuCounter != null)
                {
                    cpuCounter.Dispose();
                }

                if (windowIcon != null)
                {
                    windowIcon.Dispose();
                }

            }

            base.Dispose(disposing);
        }

        private void OnClipboardSessionHistoryChanged(object sender, EventArgs e)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate { OnClipboardSessionHistoryChanged(sender, e); });
                }
                catch
                {
                }

                return;
            }

            if (selectedIndex == 3)
            {
                PopulateClipboardLists();
            }
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            KeepInsideWorkingArea(12);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                TopMost = false;
                KeepInsideWorkingArea(12);
            }
        }

        private void KeepInsideWorkingArea(int margin)
        {
            if (WindowState != FormWindowState.Normal)
            {
                return;
            }

            Rectangle area = Screen.FromControl(this).WorkingArea;
            int maxWidth = Math.Max(MinimumSize.Width, area.Width - margin * 2);
            int maxHeight = Math.Max(MinimumSize.Height, area.Height - margin * 2);
            int width = Math.Min(Width, maxWidth);
            int height = Math.Min(Height, maxHeight);
            int minX = area.Left + margin;
            int minY = area.Top + margin;
            int maxX = Math.Max(minX, area.Right - width - margin);
            int maxY = Math.Max(minY, area.Bottom - height - margin);
            int x = Math.Min(Math.Max(Left, minX), maxX);
            int y = Math.Min(Math.Max(Top, minY), maxY);

            Bounds = new Rectangle(x, y, width, height);
        }

        private Button CreateNavButton(PanelFeature feature, int index)
        {
            SidebarNavButton button = new SidebarNavButton(feature);
            button.SetTheme(currentTheme);
            button.Tag = index;
            button.Width = 186;
            button.Height = 58;
            button.Margin = new Padding(0, 0, 0, 9);
            button.Click += delegate(object sender, EventArgs e)
            {
                SelectFeature((int)((Button)sender).Tag);
            };
            return button;
        }

        private void SelectFeature(int index)
        {
            selectedIndex = index;
            for (int i = 0; i < navButtons.Count; i++)
            {
                bool selected = i == selectedIndex;
                SidebarNavButton sidebarButton = navButtons[i] as SidebarNavButton;
                if (sidebarButton != null)
                {
                    sidebarButton.Selected = selected;
                }
            }

            systemTimer.Enabled = index == 2;
            clipboardTimer.Enabled = index == 3;
            if (index == 0)
            {
                BuildQuickEntryView(false);
            }
            else if (index == 1)
            {
                BuildQuickEntryView(true);
            }
            else if (index == 2)
            {
                BuildToolView();
            }
            else if (index == 3)
            {
                BuildClipboardView();
                CaptureClipboardSnapshot();
            }
            else if (index == 4)
            {
                BuildNotesView();
            }
            else if (index == 5)
            {
                BuildAppearanceView();
            }
            else
            {
                BuildSettingsView();
            }

            ApplyThemeToControl(contentPanel);
        }

        private Panel BeginPage(string title, string subtitle)
        {
            contentPanel.Controls.Clear();
            TableLayoutPanel page = new TableLayoutPanel();
            page.Dock = DockStyle.Fill;
            page.ColumnCount = 1;
            page.RowCount = 3;
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            page.BackColor = currentTheme.ContentBack;
            contentPanel.Controls.Add(page);

            Label titleLabel = new Label();
            titleLabel.Text = title;
            titleLabel.Dock = DockStyle.Fill;
            titleLabel.Font = new Font(Font.FontFamily, 15F, FontStyle.Bold);
            titleLabel.ForeColor = currentTheme.HeaderText;
            page.Controls.Add(titleLabel, 0, 0);

            Label subtitleLabel = new Label();
            subtitleLabel.Text = subtitle;
            subtitleLabel.Dock = DockStyle.Fill;
            subtitleLabel.ForeColor = currentTheme.MutedText;
            page.Controls.Add(subtitleLabel, 0, 1);

            Panel body = new Panel();
            body.Dock = DockStyle.Fill;
            body.BackColor = currentTheme.ContentBack;
            page.Controls.Add(body, 0, 2);
            return body;
        }

        private void BuildQuickEntryView(bool appMode)
        {
            string file = appMode ? PanelStorage.AppsFile : PanelStorage.LinksFile;
            string title = appMode ? "应用启动" : "网址直达";
            string subtitle = appMode ? "保存关键词和网址/路径，双击或点击启动。" : "保存关键词和网址，双击或点击打开。";
            Panel body = BeginPage(title, subtitle);

            List<QuickEntry> entries = QuickEntryStore.Load(file);
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 2;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, appMode ? 148F : 118F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            body.Controls.Add(layout);

            GroupBox editor = new GroupBox();
            editor.Text = appMode ? "保存应用入口" : "保存网址入口";
            editor.Dock = DockStyle.Fill;
            layout.Controls.Add(editor, 0, 0);

            TableLayoutPanel form = new TableLayoutPanel();
            form.Dock = DockStyle.Fill;
            form.Padding = new Padding(12, 10, 12, 10);
            form.ColumnCount = 5;
            form.RowCount = appMode ? 3 : 2;
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72F));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42F));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82F));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, appMode ? 92F : 0F));
            editor.Controls.Add(form);

            TextBox keywordBox = new TextBox();
            TextBox targetBox = new TextBox();
            keywordBox.Dock = DockStyle.Fill;
            targetBox.Dock = DockStyle.Fill;
            Button browseButton = CreateActionButton("浏览");
            form.Controls.Add(CreateLabel("关键词"), 0, 0);
            form.Controls.Add(keywordBox, 1, 0);
            form.Controls.Add(CreateLabel(appMode ? "网址/路径" : "网址"), 2, 0);
            form.Controls.Add(targetBox, 3, 0);
            if (appMode)
            {
                form.Controls.Add(browseButton, 4, 0);
            }

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.LeftToRight;
            actions.WrapContents = false;
            Button saveButton = CreateActionButton("保存");
            Button deleteButton = CreateActionButton("删除");
            Button openButton = CreateActionButton(appMode ? "启动" : "打开");
            actions.Controls.Add(saveButton);
            actions.Controls.Add(deleteButton);
            actions.Controls.Add(openButton);
            form.Controls.Add(actions, 1, 1);
            form.SetColumnSpan(actions, appMode ? 4 : 3);

            ListView list = CreateDetailsList();
            list.Columns.Add("关键词", 160);
            list.Columns.Add(appMode ? "网址/路径" : "网址", 460);
            list.Columns.Add("更新时间", 150);
            layout.Controls.Add(list, 0, 1);
            PopulateQuickEntries(list, entries);

            list.SelectedIndexChanged += delegate
            {
                if (list.SelectedItems.Count == 0)
                {
                    return;
                }

                QuickEntry entry = (QuickEntry)list.SelectedItems[0].Tag;
                keywordBox.Text = entry.Keyword;
                targetBox.Text = entry.Target;
            };

            list.DoubleClick += delegate { OpenSelectedQuickEntry(list); };

            browseButton.Click += delegate
            {
                OpenFileDialog dialog = new OpenFileDialog();
                dialog.Filter = "可执行文件和脚本|*.exe;*.bat;*.cmd;*.ps1;*.lnk|所有文件|*.*";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    targetBox.Text = dialog.FileName;
                }
            };

            saveButton.Click += delegate
            {
                string keyword = keywordBox.Text.Trim();
                string target = targetBox.Text.Trim();
                if (keyword.Length == 0 || target.Length == 0)
                {
                    MessageBox.Show(this, "请填写关键词和目标。", "诺诺", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                QuickEntry entry = null;
                if (list.SelectedItems.Count > 0)
                {
                    entry = (QuickEntry)list.SelectedItems[0].Tag;
                }

                if (entry == null)
                {
                    entry = new QuickEntry();
                    entry.CreatedAt = DateTime.Now;
                    entries.Add(entry);
                }

                entry.Keyword = keyword;
                entry.Target = target;
                entry.UpdatedAt = DateTime.Now;
                QuickEntryStore.Save(file, entries);
                PopulateQuickEntries(list, entries);
                keywordBox.Clear();
                targetBox.Clear();
                keywordBox.Focus();
            };

            deleteButton.Click += delegate
            {
                if (list.SelectedItems.Count == 0)
                {
                    return;
                }

                QuickEntry entry = (QuickEntry)list.SelectedItems[0].Tag;
                entries.Remove(entry);
                QuickEntryStore.Save(file, entries);
                PopulateQuickEntries(list, entries);
                keywordBox.Clear();
                targetBox.Clear();
            };

            openButton.Click += delegate { OpenSelectedQuickEntry(list); };
        }

        private void PopulateQuickEntries(ListView list, List<QuickEntry> entries)
        {
            list.BeginUpdate();
            list.Items.Clear();
            for (int i = 0; i < entries.Count; i++)
            {
                QuickEntry entry = entries[i];
                ListViewItem item = new ListViewItem(entry.Keyword);
                item.SubItems.Add(entry.Target);
                item.SubItems.Add(entry.UpdatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture));
                item.Tag = entry;
                list.Items.Add(item);
            }
            list.EndUpdate();
        }

        private void OpenSelectedQuickEntry(ListView list)
        {
            if (list.SelectedItems.Count == 0)
            {
                return;
            }

            QuickEntry entry = (QuickEntry)list.SelectedItems[0].Tag;
            try
            {
                QuickTargetLauncher.Open(entry.Target);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BuildToolView()
        {
            Panel body = BeginPage("工具箱", "显示 CPU、内存、磁盘占用；屏幕取色；查询并结束端口或进程。");
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 3;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 112F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 108F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            body.Controls.Add(layout);

            GroupBox systemGroup = new GroupBox();
            systemGroup.Text = "系统信息";
            systemGroup.Dock = DockStyle.Fill;
            layout.Controls.Add(systemGroup, 0, 0);
            TableLayoutPanel sys = new TableLayoutPanel();
            sys.Dock = DockStyle.Fill;
            sys.Padding = new Padding(12);
            sys.ColumnCount = 3;
            sys.RowCount = 1;
            sys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            sys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
            sys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));
            systemGroup.Controls.Add(sys);
            cpuLabel = CreateInfoLabel();
            memoryLabel = CreateInfoLabel();
            diskLabel = CreateInfoLabel();
            sys.Controls.Add(cpuLabel, 0, 0);
            sys.Controls.Add(memoryLabel, 1, 0);
            sys.Controls.Add(diskLabel, 2, 0);

            GroupBox colorGroup = new GroupBox();
            colorGroup.Text = "颜色拾取器";
            colorGroup.Dock = DockStyle.Fill;
            layout.Controls.Add(colorGroup, 0, 1);
            FlowLayoutPanel colorFlow = new FlowLayoutPanel();
            colorFlow.Dock = DockStyle.Fill;
            colorFlow.Padding = new Padding(12, 18, 12, 10);
            colorFlow.WrapContents = false;
            colorGroup.Controls.Add(colorFlow);
            Button pickButton = CreateActionButton("点击屏幕取色");
            Button copyColorButton = CreateActionButton("复制颜色");
            colorSwatch = new Panel();
            colorSwatch.Width = 52;
            colorSwatch.Height = 30;
            colorSwatch.BackColor = Color.White;
            colorSwatch.BorderStyle = BorderStyle.FixedSingle;
            colorLabel = CreateInfoLabel();
            colorLabel.Width = 320;
            colorLabel.Text = "尚未取色";
            colorFlow.Controls.Add(pickButton);
            colorFlow.Controls.Add(colorSwatch);
            colorFlow.Controls.Add(colorLabel);
            colorFlow.Controls.Add(copyColorButton);
            pickButton.Click += delegate { StartColorPicking(); };
            copyColorButton.Click += delegate
            {
                if (colorLabel != null && colorLabel.Text.IndexOf("#", StringComparison.Ordinal) >= 0)
                {
                    Clipboard.SetText(colorLabel.Text);
                }
            };

            GroupBox targetGroup = new GroupBox();
            targetGroup.Text = "查询端口和进程";
            targetGroup.Dock = DockStyle.Fill;
            layout.Controls.Add(targetGroup, 0, 2);
            TableLayoutPanel targetLayout = new TableLayoutPanel();
            targetLayout.Dock = DockStyle.Fill;
            targetLayout.Padding = new Padding(12);
            targetLayout.ColumnCount = 1;
            targetLayout.RowCount = 2;
            targetLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            targetLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            targetGroup.Controls.Add(targetLayout);

            FlowLayoutPanel queryFlow = new FlowLayoutPanel();
            queryFlow.Dock = DockStyle.Fill;
            queryFlow.WrapContents = false;
            queryFlow.Controls.Add(CreateLabel("关键词/端口"));
            targetQueryBox = new TextBox();
            targetQueryBox.Width = 220;
            queryFlow.Controls.Add(targetQueryBox);
            Button queryButton = CreateActionButton("查询");
            Button killButton = CreateActionButton("结束选中");
            Button killAllButton = CreateActionButton("结束全部");
            queryFlow.Controls.Add(queryButton);
            queryFlow.Controls.Add(killButton);
            queryFlow.Controls.Add(killAllButton);
            targetLayout.Controls.Add(queryFlow, 0, 0);

            targetList = CreateDetailsList();
            targetList.Columns.Add("类型", 72);
            targetList.Columns.Add("名称/端口", 190);
            targetList.Columns.Add("PID", 80);
            targetList.Columns.Add("详情", 520);
            targetLayout.Controls.Add(targetList, 0, 1);

            queryButton.Click += delegate { RefreshSystemTargets(); };
            targetQueryBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    RefreshSystemTargets();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            killButton.Click += delegate { KillSelectedTarget(); };
            killAllButton.Click += delegate { KillAllTargets(); };

            RefreshSystemInfo();
            ShowSystemTargetMessage("请输入进程关键词或端口号，然后点击查询。");
        }

        private void RefreshSystemInfo()
        {
            if (cpuLabel == null || cpuLabel.IsDisposed)
            {
                return;
            }

            string cpu = "CPU: " + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture) + " 核";
            try
            {
                if (cpuCounter != null)
                {
                    cpu = "CPU: " + cpuCounter.NextValue().ToString("0.0", CultureInfo.CurrentCulture) + "% · " + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture) + " 核";
                }
            }
            catch
            {
            }

            MemorySnapshot memory = SystemInfoProvider.GetMemory();
            cpuLabel.Text = cpu;
            memoryLabel.Text = "内存: " + FormatBytes(memory.Used) + " / " + FormatBytes(memory.Total);
            diskLabel.Text = "磁盘: " + SystemInfoProvider.GetDiskSummary();
        }

        private void StartColorPicking()
        {
            ScreenColorPickerForm picker = new ScreenColorPickerForm();
            picker.ColorPicked += delegate
            {
                Color color = picker.PickedColor;
                colorSwatch.BackColor = color;
                colorLabel.Text = ColorToText(color);
            };
            picker.Show();
        }

        private void RefreshSystemTargets()
        {
            if (targetList == null || targetList.IsDisposed)
            {
                return;
            }

            string query = targetQueryBox == null ? String.Empty : targetQueryBox.Text.Trim();
            if (String.IsNullOrWhiteSpace(query))
            {
                ShowSystemTargetMessage("请输入进程关键词或端口号，然后点击查询。");
                return;
            }

            List<SystemTarget> rows = SystemTargetProvider.Query(query);
            targetList.BeginUpdate();
            targetList.Items.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                SystemTarget target = rows[i];
                ListViewItem item = new ListViewItem(target.Kind);
                item.SubItems.Add(target.Name);
                item.SubItems.Add(target.ProcessId > 0 ? target.ProcessId.ToString(CultureInfo.InvariantCulture) : "");
                item.SubItems.Add(target.Detail);
                item.Tag = target;
                targetList.Items.Add(item);
            }
            if (rows.Count == 0)
            {
                AddSystemTargetMessageItem("未找到匹配的端口或进程。");
            }
            targetList.EndUpdate();
        }

        private void ShowSystemTargetMessage(string message)
        {
            if (targetList == null || targetList.IsDisposed)
            {
                return;
            }

            targetList.BeginUpdate();
            targetList.Items.Clear();
            AddSystemTargetMessageItem(message);
            targetList.EndUpdate();
        }

        private void AddSystemTargetMessageItem(string message)
        {
            ListViewItem item = new ListViewItem("提示");
            item.SubItems.Add("");
            item.SubItems.Add("");
            item.SubItems.Add(message);
            item.Tag = new SystemTarget("提示", "", 0, message);
            targetList.Items.Add(item);
        }

        private void KillSelectedTarget()
        {
            if (targetList == null || targetList.SelectedItems.Count == 0)
            {
                return;
            }

            SystemTarget target = (SystemTarget)targetList.SelectedItems[0].Tag;
            if (target.ProcessId <= 0)
            {
                return;
            }

            DialogResult result = MessageBox.Show(
                this,
                "确定结束 PID " + target.ProcessId.ToString(CultureInfo.InvariantCulture) + "？\r\n" + target.Detail,
                "结束进程",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes)
            {
                return;
            }

            try
            {
                Process process = Process.GetProcessById(target.ProcessId);
                process.Kill();
                process.Dispose();
                RefreshSystemTargets();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "结束失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void KillAllTargets()
        {
            if (targetList == null || targetList.Items.Count == 0)
            {
                return;
            }

            Dictionary<int, SystemTarget> targets = new Dictionary<int, SystemTarget>();
            int currentProcessId = Process.GetCurrentProcess().Id;
            for (int i = 0; i < targetList.Items.Count; i++)
            {
                SystemTarget target = targetList.Items[i].Tag as SystemTarget;
                if (target == null || target.ProcessId <= 0 || target.ProcessId == currentProcessId)
                {
                    continue;
                }

                if (!targets.ContainsKey(target.ProcessId))
                {
                    targets.Add(target.ProcessId, target);
                }
            }

            if (targets.Count == 0)
            {
                MessageBox.Show(this, "当前查询结果中没有可结束的进程。", "诺诺", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string query = targetQueryBox == null ? "" : targetQueryBox.Text.Trim();
            DialogResult result = MessageBox.Show(
                this,
                "确定结束当前查询结果中的 " + targets.Count.ToString(CultureInfo.InvariantCulture) + " 个进程？\r\n关键词/端口: " + query,
                "结束全部",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes)
            {
                return;
            }

            int killed = 0;
            List<string> failures = new List<string>();
            foreach (KeyValuePair<int, SystemTarget> pair in targets)
            {
                try
                {
                    using (Process process = Process.GetProcessById(pair.Key))
                    {
                        process.Kill();
                    }

                    killed++;
                }
                catch (Exception ex)
                {
                    failures.Add("PID " + pair.Key.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message);
                }
            }

            RefreshSystemTargets();
            if (failures.Count > 0)
            {
                StringBuilder message = new StringBuilder();
                message.Append("已结束 ");
                message.Append(killed.ToString(CultureInfo.InvariantCulture));
                message.Append(" 个进程，");
                message.Append(failures.Count.ToString(CultureInfo.InvariantCulture));
                message.Append(" 个失败。");
                int shown = Math.Min(failures.Count, 6);
                for (int i = 0; i < shown; i++)
                {
                    message.Append("\r\n");
                    message.Append(failures[i]);
                }

                MessageBox.Show(this, message.ToString(), "结束结果", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void BuildClipboardView()
        {
            Panel body = BeginPage("剪贴板", "显示本次开机期间复制的文本和图片；重启或关机后会自动清空。");
            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.SplitterDistance = 280;
            body.Controls.Add(split);

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            TabPage textPage = new TabPage("文本");
            TabPage imagePage = new TabPage("图片");
            tabs.TabPages.Add(textPage);
            tabs.TabPages.Add(imagePage);
            split.Panel1.Controls.Add(tabs);

            textClipboardList = CreateDetailsList();
            textClipboardList.Columns.Add("时间", 135);
            textClipboardList.Columns.Add("长度", 80);
            textClipboardList.Columns.Add("预览", 420);
            textClipboardList.Dock = DockStyle.Fill;
            textClipboardList.SelectedIndexChanged += delegate { ShowSelectedClipboardItem(textClipboardList); };
            textPage.Controls.Add(textClipboardList);

            imageClipboardList = CreateDetailsList();
            imageClipboardList.Columns.Add("时间", 135);
            imageClipboardList.Columns.Add("大小", 110);
            imageClipboardList.Columns.Add("识别文本", 360);
            imageClipboardList.Dock = DockStyle.Fill;
            imageClipboardList.SelectedIndexChanged += delegate { ShowSelectedClipboardItem(imageClipboardList); };
            imagePage.Controls.Add(imageClipboardList);

            TableLayoutPanel detail = new TableLayoutPanel();
            detail.Dock = DockStyle.Fill;
            detail.ColumnCount = 1;
            detail.RowCount = 8;
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            detail.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            detail.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            detail.RowStyles.Add(new RowStyle(SizeType.Percent, 20F));
            split.Panel2.Controls.Add(detail);

            clipboardTypeLabel = CreateInfoLabel();
            clipboardLengthLabel = CreateInfoLabel();
            clipboardTimeLabel = CreateInfoLabel();
            detail.Controls.Add(clipboardTypeLabel, 0, 0);
            detail.Controls.Add(clipboardLengthLabel, 0, 1);
            detail.Controls.Add(clipboardTimeLabel, 0, 2);

            clipboardContentBox = new TextBox();
            clipboardContentBox.Multiline = true;
            clipboardContentBox.ScrollBars = ScrollBars.Both;
            clipboardContentBox.ReadOnly = true;
            clipboardContentBox.Dock = DockStyle.Fill;
            detail.Controls.Add(clipboardContentBox, 0, 3);

            copyClipboardButton = CreateActionButton("复制内容");
            copyClipboardButton.Click += delegate { CopySelectedClipboardContent(); };
            detail.Controls.Add(copyClipboardButton, 0, 4);

            clipboardPictureBox = new PictureBox();
            clipboardPictureBox.Dock = DockStyle.Fill;
            clipboardPictureBox.SizeMode = PictureBoxSizeMode.Zoom;
            clipboardPictureBox.BorderStyle = BorderStyle.FixedSingle;
            clipboardPictureBox.Cursor = Cursors.Hand;
            clipboardPictureBox.Click += delegate { ShowClipboardImagePreview(); };
            detail.Controls.Add(clipboardPictureBox, 0, 5);

            FlowLayoutPanel imageActions = new FlowLayoutPanel();
            imageActions.Dock = DockStyle.Fill;
            imageActions.FlowDirection = FlowDirection.LeftToRight;
            imageActions.WrapContents = false;
            imageActions.BackColor = currentTheme.ContentBack;

            saveClipboardImageButton = CreateActionButton("保存到本地");
            saveClipboardImageButton.Click += delegate { SaveSelectedClipboardImage(this); };
            imageActions.Controls.Add(saveClipboardImageButton);

            copyOcrButton = CreateActionButton("复制图片识别文本");
            copyOcrButton.Click += delegate
            {
                if (selectedClipboardItem != null && !String.IsNullOrEmpty(selectedClipboardItem.OcrText))
                {
                    Clipboard.SetText(selectedClipboardItem.OcrText);
                }
            };
            imageActions.Controls.Add(copyOcrButton);
            detail.Controls.Add(imageActions, 0, 6);

            clipboardOcrBox = new TextBox();
            clipboardOcrBox.Multiline = true;
            clipboardOcrBox.ScrollBars = ScrollBars.Both;
            clipboardOcrBox.ReadOnly = true;
            clipboardOcrBox.Dock = DockStyle.Fill;
            detail.Controls.Add(clipboardOcrBox, 0, 7);

            PopulateClipboardLists();
            ClearClipboardDetail();
        }

        private async void CaptureClipboardSnapshot()
        {
            bool captured = false;
            try
            {
                captured = await ClipboardSessionHistory.CaptureCurrentAsync();
            }
            catch
            {
            }

            if (captured && selectedIndex == 3)
            {
                PopulateClipboardLists();
            }
        }

        private void PopulateClipboardLists()
        {
            if (textClipboardList == null || textClipboardList.IsDisposed)
            {
                return;
            }

            List<ClipboardHistoryItem> textItems = ClipboardSessionHistory.GetTextItems();
            List<ClipboardHistoryItem> imageItems = ClipboardSessionHistory.GetImageItems();

            textClipboardList.BeginUpdate();
            textClipboardList.Items.Clear();
            for (int i = 0; i < textItems.Count; i++)
            {
                ClipboardHistoryItem entry = textItems[i];
                ListViewItem item = new ListViewItem(entry.CreatedAt.ToString("MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
                item.SubItems.Add(entry.Length.ToString(CultureInfo.InvariantCulture));
                item.SubItems.Add(Preview(entry.Text));
                item.Tag = entry;
                textClipboardList.Items.Add(item);
            }
            textClipboardList.EndUpdate();

            imageClipboardList.BeginUpdate();
            imageClipboardList.Items.Clear();
            for (int i = 0; i < imageItems.Count; i++)
            {
                ClipboardHistoryItem entry = imageItems[i];
                ListViewItem item = new ListViewItem(entry.CreatedAt.ToString("MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
                item.SubItems.Add(FormatBytes(entry.Length));
                item.SubItems.Add(Preview(entry.OcrText));
                item.Tag = entry;
                imageClipboardList.Items.Add(item);
            }
            imageClipboardList.EndUpdate();
        }

        private void ShowSelectedClipboardItem(ListView list)
        {
            if (list.SelectedItems.Count == 0)
            {
                return;
            }

            selectedClipboardItem = (ClipboardHistoryItem)list.SelectedItems[0].Tag;
            selectedClipboardItem.Image = ClipboardSessionHistory.EnsureImageLoaded(selectedClipboardItem);
            clipboardTypeLabel.Text = "类型: " + selectedClipboardItem.Kind;
            clipboardLengthLabel.Text = "长度: " + FormatBytesOrChars(selectedClipboardItem);
            clipboardTimeLabel.Text = "时间: " + selectedClipboardItem.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
            clipboardContentBox.Text = selectedClipboardItem.Kind == "文本" ? selectedClipboardItem.Text : "图片内容可通过“复制内容”复制回剪贴板。";
            clipboardPictureBox.Image = selectedClipboardItem.Image;
            clipboardOcrBox.Text = selectedClipboardItem.Kind == "图片" ? selectedClipboardItem.OcrText : "";
            bool hasImage = selectedClipboardItem.Kind == "图片" && selectedClipboardItem.Image != null;
            clipboardPictureBox.Cursor = hasImage ? Cursors.Hand : Cursors.Default;
            saveClipboardImageButton.Enabled = hasImage;
            copyOcrButton.Enabled = hasImage && !String.IsNullOrEmpty(selectedClipboardItem.OcrText);
        }

        private void ClearClipboardDetail()
        {
            selectedClipboardItem = null;
            clipboardTypeLabel.Text = "类型: -";
            clipboardLengthLabel.Text = "长度: -";
            clipboardTimeLabel.Text = "时间: -";
            clipboardContentBox.Text = "";
            clipboardPictureBox.Image = null;
            clipboardPictureBox.Cursor = Cursors.Default;
            clipboardOcrBox.Text = "";
            saveClipboardImageButton.Enabled = false;
            copyOcrButton.Enabled = false;
        }

        private void ShowClipboardImagePreview()
        {
            if (selectedClipboardItem == null || selectedClipboardItem.Kind != "图片" || selectedClipboardItem.Image == null)
            {
                return;
            }

            DateTime createdAt = selectedClipboardItem.CreatedAt;
            using (Bitmap previewImage = new Bitmap(selectedClipboardItem.Image))
            using (Form preview = new Form())
            {
                Rectangle workingArea = Screen.FromControl(this).WorkingArea;
                int maximumWidth = Math.Max(560, workingArea.Width - 80);
                int maximumHeight = Math.Max(420, workingArea.Height - 80);
                int width = Math.Min(Math.Max(previewImage.Width + 48, 720), maximumWidth);
                int height = Math.Min(Math.Max(previewImage.Height + 112, 540), maximumHeight);

                preview.Text = "图片预览";
                preview.StartPosition = FormStartPosition.CenterParent;
                preview.FormBorderStyle = FormBorderStyle.Sizable;
                preview.MinimizeBox = false;
                preview.MaximizeBox = true;
                preview.MinimumSize = new Size(560, 420);
                preview.ClientSize = new Size(width, height);
                preview.BackColor = currentTheme.WindowBack;
                preview.Font = Font;
                preview.KeyPreview = true;
                preview.KeyDown += delegate(object sender, KeyEventArgs e)
                {
                    if (e.KeyCode == Keys.Escape)
                    {
                        preview.Close();
                        e.SuppressKeyPress = true;
                    }
                };

                TableLayoutPanel layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.ColumnCount = 1;
                layout.RowCount = 2;
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
                layout.Padding = new Padding(12);
                layout.BackColor = currentTheme.WindowBack;
                preview.Controls.Add(layout);

                PictureBox picture = new PictureBox();
                picture.Dock = DockStyle.Fill;
                picture.SizeMode = PictureBoxSizeMode.Zoom;
                picture.BackColor = currentTheme.InputBack;
                picture.Image = previewImage;
                layout.Controls.Add(picture, 0, 0);

                FlowLayoutPanel actions = new FlowLayoutPanel();
                actions.Dock = DockStyle.Fill;
                actions.FlowDirection = FlowDirection.RightToLeft;
                actions.WrapContents = false;
                actions.Padding = new Padding(0, 9, 0, 0);
                actions.BackColor = currentTheme.WindowBack;
                layout.Controls.Add(actions, 0, 1);

                Button closeButton = CreateActionButton("关闭");
                closeButton.Click += delegate { preview.Close(); };
                actions.Controls.Add(closeButton);

                Button saveButton = CreateActionButton("保存到本地");
                saveButton.Click += delegate { SaveClipboardImage(preview, previewImage, createdAt); };
                actions.Controls.Add(saveButton);

                Label sizeLabel = new Label();
                sizeLabel.AutoSize = true;
                sizeLabel.Margin = new Padding(0, 6, 16, 0);
                sizeLabel.ForeColor = currentTheme.MutedText;
                sizeLabel.Text = previewImage.Width.ToString(CultureInfo.InvariantCulture) + " x " + previewImage.Height.ToString(CultureInfo.InvariantCulture);
                actions.Controls.Add(sizeLabel);

                preview.ShowDialog(this);
                picture.Image = null;
            }
        }

        private void SaveSelectedClipboardImage(IWin32Window owner)
        {
            if (selectedClipboardItem == null || selectedClipboardItem.Kind != "图片" || selectedClipboardItem.Image == null)
            {
                return;
            }

            SaveClipboardImage(owner, selectedClipboardItem.Image, selectedClipboardItem.CreatedAt);
        }

        private void SaveClipboardImage(IWin32Window owner, Image image, DateTime createdAt)
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "保存剪贴板图片";
                dialog.Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg;*.jpeg)|*.jpg;*.jpeg|BMP 图片 (*.bmp)|*.bmp";
                dialog.FilterIndex = 1;
                dialog.AddExtension = true;
                dialog.OverwritePrompt = true;
                dialog.FileName = "clipboard-" + createdAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                if (dialog.ShowDialog(owner) != DialogResult.OK)
                {
                    return;
                }

                ImageFormat format = ImageFormat.Png;
                if (dialog.FilterIndex == 2)
                {
                    format = ImageFormat.Jpeg;
                }
                else if (dialog.FilterIndex == 3)
                {
                    format = ImageFormat.Bmp;
                }

                try
                {
                    using (Bitmap copy = new Bitmap(image))
                    {
                        copy.Save(dialog.FileName, format);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(owner, "保存图片失败：\r\n" + ex.Message, "保存图片", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void CopySelectedClipboardContent()
        {
            if (selectedClipboardItem == null)
            {
                return;
            }

            if (selectedClipboardItem.Kind == "文本")
            {
                Clipboard.SetText(selectedClipboardItem.Text ?? "");
            }
            else if (selectedClipboardItem.Image != null)
            {
                Clipboard.SetImage(selectedClipboardItem.Image);
            }
        }

        private void BuildNotesView()
        {
            Panel body = BeginPage("便签", "目录以树形结构显示，点击便签查看内容和详情，右键目录或便签执行操作。");
            NotesStore.EnsureSeed();
            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.SplitterDistance = 240;
            body.Controls.Add(split);

            notesTree = new TreeView();
            notesTree.Dock = DockStyle.Fill;
            notesTree.HideSelection = false;
            notesTree.AfterSelect += delegate { ShowSelectedNote(); };
            notesTree.NodeMouseClick += OnNoteTreeNodeMouseClick;
            split.Panel1.Controls.Add(notesTree);

            TableLayoutPanel detail = new TableLayoutPanel();
            detail.Dock = DockStyle.Fill;
            detail.ColumnCount = 1;
            detail.RowCount = 3;
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 82F));
            detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            split.Panel2.Controls.Add(detail);

            noteDetailsLabel = new Label();
            noteDetailsLabel.Dock = DockStyle.Fill;
            noteDetailsLabel.ForeColor = Color.FromArgb(48, 64, 76);
            detail.Controls.Add(noteDetailsLabel, 0, 0);

            noteContentBox = new TextBox();
            noteContentBox.Multiline = true;
            noteContentBox.ScrollBars = ScrollBars.Both;
            noteContentBox.ReadOnly = true;
            noteContentBox.Dock = DockStyle.Fill;
            detail.Controls.Add(noteContentBox, 0, 1);

            Button copyButton = CreateActionButton("复制便签内容");
            copyButton.Click += delegate
            {
                if (!String.IsNullOrEmpty(noteContentBox.Text))
                {
                    Clipboard.SetText(noteContentBox.Text);
                }
            };
            detail.Controls.Add(copyButton, 0, 2);

            LoadNotesTree();
        }

        private void LoadNotesTree()
        {
            if (notesTree == null)
            {
                return;
            }

            notesTree.BeginUpdate();
            notesTree.Nodes.Clear();
            TreeNode root = BuildDirectoryNode(NotesStore.Root);
            root.Expand();
            notesTree.Nodes.Add(root);
            notesTree.EndUpdate();
        }

        private TreeNode BuildDirectoryNode(string path)
        {
            DirectoryInfo dir = new DirectoryInfo(path);
            TreeNode node = new TreeNode(dir.Name);
            node.Tag = new NoteNodeInfo(true, path);

            DirectoryInfo[] dirs = NotesStore.GetManagedDirectories(path);
            Array.Sort(dirs, delegate(DirectoryInfo a, DirectoryInfo b) { return String.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
            for (int i = 0; i < dirs.Length; i++)
            {
                node.Nodes.Add(BuildDirectoryNode(dirs[i].FullName));
            }

            FileInfo[] files = NotesStore.GetManagedNotes(path);
            Array.Sort(files, delegate(FileInfo a, FileInfo b) { return String.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
            for (int i = 0; i < files.Length; i++)
            {
                TreeNode fileNode = new TreeNode(Path.GetFileNameWithoutExtension(files[i].Name));
                fileNode.Tag = new NoteNodeInfo(false, files[i].FullName);
                node.Nodes.Add(fileNode);
            }

            return node;
        }

        private void OnNoteTreeNodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            notesTree.SelectedNode = e.Node;
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            NoteNodeInfo info = e.Node.Tag as NoteNodeInfo;
            if (info == null)
            {
                return;
            }

            ContextMenuStrip menu = new ContextMenuStrip();
            if (info.IsDirectory)
            {
                ToolStripMenuItem createDir = new ToolStripMenuItem("创建目录");
                createDir.Click += delegate { CreateNoteDirectory(info.Path); };
                ToolStripMenuItem createNote = new ToolStripMenuItem("创建便签");
                createNote.Click += delegate { CreateNote(info.Path); };
                ToolStripMenuItem deleteDir = new ToolStripMenuItem("删除目录");
                deleteDir.Click += delegate { DeleteNoteDirectory(info.Path); };
                menu.Items.Add(createDir);
                menu.Items.Add(createNote);
                menu.Items.Add(deleteDir);
            }
            else
            {
                ToolStripMenuItem editNote = new ToolStripMenuItem("编辑便签");
                editNote.Click += delegate { EditNote(info.Path); };
                ToolStripMenuItem deleteNote = new ToolStripMenuItem("删除便签");
                deleteNote.Click += delegate { DeleteNote(info.Path); };
                menu.Items.Add(editNote);
                menu.Items.Add(deleteNote);
            }

            menu.Show(notesTree, e.Location);
        }

        private void ShowSelectedNote()
        {
            if (notesTree == null || notesTree.SelectedNode == null)
            {
                return;
            }

            NoteNodeInfo info = notesTree.SelectedNode.Tag as NoteNodeInfo;
            if (info == null || info.IsDirectory)
            {
                selectedNotePath = null;
                noteDetailsLabel.Text = "请选择一个便签。";
                noteContentBox.Text = "";
                return;
            }

            selectedNotePath = info.Path;
            FileInfo file = new FileInfo(info.Path);
            noteDetailsLabel.Text =
                "所在目录: " + file.DirectoryName + "\r\n" +
                "创建时间: " + file.CreationTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) + "\r\n" +
                "更新时间: " + file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
            noteContentBox.Text = File.ReadAllText(info.Path, Encoding.UTF8);
        }

        private void CreateNoteDirectory(string parent)
        {
            string name = PromptDialog.Show(this, "创建目录", "目录名称");
            if (String.IsNullOrWhiteSpace(name))
            {
                return;
            }

            string path = NotesStore.UniqueDirectoryPath(parent, name.Trim());
            Directory.CreateDirectory(path);
            NotesStore.RegisterDirectory(path);
            LoadNotesTree();
        }

        private void CreateNote(string directory)
        {
            NoteEditorDialog dialog = new NoteEditorDialog("创建便签", "", "");
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            string name = PanelStorage.SafeFileName(dialog.NoteTitle);
            if (String.IsNullOrWhiteSpace(name))
            {
                name = "未命名便签";
            }

            string path = NotesStore.UniqueNotePath(directory, name);
            File.WriteAllText(path, dialog.NoteContent ?? "", Encoding.UTF8);
            NotesStore.RegisterNote(path);
            LoadNotesTree();
        }

        private void EditNote(string path)
        {
            string title = Path.GetFileNameWithoutExtension(path);
            string content = File.ReadAllText(path, Encoding.UTF8);
            NoteEditorDialog dialog = new NoteEditorDialog("编辑便签", title, content);
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            string target = path;
            string newName = PanelStorage.SafeFileName(dialog.NoteTitle);
            if (!String.IsNullOrWhiteSpace(newName) && !String.Equals(newName, title, StringComparison.OrdinalIgnoreCase))
            {
                target = NotesStore.UniqueNotePath(Path.GetDirectoryName(path), newName);
                File.Move(path, target);
                NotesStore.MoveNote(path, target);
            }

            File.WriteAllText(target, dialog.NoteContent ?? "", Encoding.UTF8);
            LoadNotesTree();
            selectedNotePath = target;
        }

        private void DeleteNote(string path)
        {
            DialogResult result = MessageBox.Show(this, "确定删除这个便签？", "删除便签", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes)
            {
                return;
            }

            File.Delete(path);
            NotesStore.RemoveNote(path);
            LoadNotesTree();
            noteContentBox.Clear();
            noteDetailsLabel.Text = "";
        }

        private void DeleteNoteDirectory(string path)
        {
            if (String.Equals(Path.GetFullPath(path).TrimEnd('\\'), Path.GetFullPath(NotesStore.Root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "根目录不能删除。", "诺诺", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult result = MessageBox.Show(this, "确定删除目录及其中所有便签？", "删除目录", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes)
            {
                return;
            }

            NotesStore.DeleteDirectory(path);
            LoadNotesTree();
        }

        private void BuildAppearanceView()
        {
            Panel body = BeginPage("外观", "选择一套适合当前桌面背景和光线的面板外观。设置会立即保存。");
            body.AutoScroll = true;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Top;
            layout.AutoSize = true;
            layout.ColumnCount = 1;
            layout.RowCount = 5;
            layout.BackColor = currentTheme.ContentBack;
            body.Controls.Add(layout);

            Label guidance = CreateMutedLabel("套装会同步调整左侧导航、内容区、输入框和列表的色彩层级。");
            guidance.AutoSize = false;
            guidance.Height = 34;
            guidance.Dock = DockStyle.Top;
            layout.Controls.Add(guidance, 0, 0);

            Label panelTitle = CreateLabel("面板外观");
            panelTitle.Font = new Font(Font.FontFamily, 10.5F, FontStyle.Bold);
            panelTitle.Height = 28;
            panelTitle.AutoSize = false;
            panelTitle.Dock = DockStyle.Top;
            layout.Controls.Add(panelTitle, 0, 1);

            FlowLayoutPanel themeGrid = new FlowLayoutPanel();
            themeGrid.Dock = DockStyle.Top;
            themeGrid.AutoSize = true;
            themeGrid.WrapContents = true;
            themeGrid.Padding = new Padding(0, 4, 0, 0);
            themeGrid.BackColor = currentTheme.ContentBack;
            layout.Controls.Add(themeGrid, 0, 2);

            PanelTheme[] themes = PanelThemeStore.All;
            for (int i = 0; i < themes.Length; i++)
            {
                PanelTheme theme = themes[i];
                AppearanceThemeTile tile = new AppearanceThemeTile(theme);
                tile.Width = 254;
                tile.Height = 132;
                tile.Margin = new Padding(0, 0, 12, 12);
                tile.Selected = String.Equals(theme.Id, currentTheme.Id, StringComparison.OrdinalIgnoreCase);
                tile.ThemeSelected += delegate(object sender, EventArgs e)
                {
                    AppearanceThemeTile selectedTile = (AppearanceThemeTile)sender;
                    SetPanelTheme(selectedTile.Theme);
                };
                themeGrid.Controls.Add(tile);
            }

            Label petTitle = CreateLabel("宠物外观");
            petTitle.Font = new Font(Font.FontFamily, 10.5F, FontStyle.Bold);
            petTitle.Height = 34;
            petTitle.Margin = new Padding(0, 10, 0, 0);
            petTitle.AutoSize = false;
            petTitle.Dock = DockStyle.Top;
            layout.Controls.Add(petTitle, 0, 3);

            FlowLayoutPanel petGrid = new FlowLayoutPanel();
            petGrid.Dock = DockStyle.Top;
            petGrid.AutoSize = true;
            petGrid.WrapContents = true;
            petGrid.Padding = new Padding(0, 4, 0, 0);
            petGrid.BackColor = currentTheme.ContentBack;
            layout.Controls.Add(petGrid, 0, 4);

            PetAppearance currentPet = petAppearanceGetter == null ? PetAppearanceStore.Load() : petAppearanceGetter();
            PetAppearance[] appearances = PetAppearanceStore.All;
            for (int i = 0; i < appearances.Length; i++)
            {
                PetAppearance appearance = appearances[i];
                PetAppearanceTile tile = new PetAppearanceTile(appearance, currentTheme);
                tile.Width = 254;
                tile.Height = 140;
                tile.Margin = new Padding(0, 0, 12, 12);
                tile.Selected = currentPet != null && String.Equals(appearance.Id, currentPet.Id, StringComparison.OrdinalIgnoreCase);
                tile.AppearanceSelected += delegate(object sender, EventArgs e)
                {
                    PetAppearanceTile selectedTile = (PetAppearanceTile)sender;
                    if (petAppearanceSetter != null)
                    {
                        petAppearanceSetter(selectedTile.Appearance.Id);
                    }
                    else
                    {
                        PetAppearanceStore.Save(selectedTile.Appearance);
                    }

                    BuildAppearanceView();
                };
                petGrid.Controls.Add(tile);
            }
        }

        private void SetPanelTheme(PanelTheme theme)
        {
            if (theme == null)
            {
                return;
            }

            currentTheme = theme;
            PanelThemeStore.Save(currentTheme);
            ApplyPanelTheme();
            SelectFeature(selectedIndex);
        }

        private void ApplyPanelTheme()
        {
            BackColor = currentTheme.WindowBack;
            if (navPanel != null)
            {
                navPanel.BackColor = currentTheme.NavBack;
            }

            if (brandLabel != null)
            {
                brandLabel.ForeColor = currentTheme.HeaderText;
                brandLabel.BackColor = currentTheme.NavBack;
            }

            if (panelSubtitleLabel != null)
            {
                panelSubtitleLabel.ForeColor = currentTheme.MutedText;
                panelSubtitleLabel.BackColor = currentTheme.NavBack;
            }

            if (contentPanel != null)
            {
                contentPanel.BackColor = currentTheme.ContentBack;
            }

            for (int i = 0; i < navButtons.Count; i++)
            {
                SidebarNavButton button = navButtons[i] as SidebarNavButton;
                if (button != null)
                {
                    button.SetTheme(currentTheme);
                }
            }
        }

        private void ApplyThemeToControl(Control root)
        {
            if (root == null)
            {
                return;
            }

            if (root is AppearanceThemeTile)
            {
                return;
            }

            if (root is TextBox)
            {
                root.BackColor = currentTheme.InputBack;
                root.ForeColor = currentTheme.Text;
            }
            else if (root is ListView)
            {
                root.BackColor = currentTheme.InputBack;
                root.ForeColor = currentTheme.Text;
            }
            else if (root is Button)
            {
                Button button = (Button)root;
                if (!(button is SidebarNavButton))
                {
                    button.BackColor = currentTheme.ButtonBack;
                    button.ForeColor = currentTheme.Text;
                    button.UseVisualStyleBackColor = false;
                }
            }
            else if (root is GroupBox || root is CheckBox || root is RadioButton)
            {
                root.BackColor = currentTheme.ContentBack;
                root.ForeColor = currentTheme.Text;
            }
            else if (root is TabPage)
            {
                root.BackColor = currentTheme.ContentBack;
                root.ForeColor = currentTheme.Text;
            }
            else if (root is Label)
            {
                root.BackColor = currentTheme.ContentBack;
            }
            else if (root is Panel || root is TableLayoutPanel || root is FlowLayoutPanel || root is SplitContainer)
            {
                root.BackColor = currentTheme.ContentBack;
            }

            for (int i = 0; i < root.Controls.Count; i++)
            {
                ApplyThemeToControl(root.Controls[i]);
            }
        }

        private void BuildSettingsView()
        {
            Panel body = BeginPage("设置", "控制快捷键设置。保存后会立即重新注册可用的全局快捷键。");
            body.AutoScroll = true;
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Top;
            layout.AutoSize = true;
            layout.ColumnCount = 1;
            layout.RowCount = 6;
            body.Controls.Add(layout);

            CheckBox startupCheck = new CheckBox();
            startupCheck.Text = "开机自启动诺诺";
            startupCheck.Checked = StartupManager.IsEnabled();
            startupCheck.AutoSize = true;
            startupCheck.Margin = new Padding(0, 12, 0, 16);
            startupCheck.CheckedChanged += delegate
            {
                try
                {
                    StartupManager.SetEnabled(startupCheck.Checked);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "更新启动项失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    startupCheck.Checked = StartupManager.IsEnabled();
                }
            };
            layout.Controls.Add(startupCheck, 0, 0);

            GroupBox petActionsGroup = new GroupBox();
            petActionsGroup.Text = "宠物动作";
            petActionsGroup.Width = 760;
            petActionsGroup.Height = 112;
            petActionsGroup.Margin = new Padding(0, 0, 0, 12);
            layout.Controls.Add(petActionsGroup, 0, 1);

            FlowLayoutPanel petActions = new FlowLayoutPanel();
            petActions.Dock = DockStyle.Fill;
            petActions.Padding = new Padding(12, 16, 12, 12);
            petActions.FlowDirection = FlowDirection.LeftToRight;
            petActions.WrapContents = true;
            petActionsGroup.Controls.Add(petActions);
            AddPetActionButton(petActions, "待机", "idle");
            AddPetActionButton(petActions, "向右移动", "running-right");
            AddPetActionButton(petActions, "向左移动", "running-left");
            AddPetActionButton(petActions, "运行中", "running");
            AddPetActionButton(petActions, "等待", "waiting");
            AddPetActionButton(petActions, "检查", "review");
            AddPetActionButton(petActions, "失败", "failed");
            AddPetActionButton(petActions, "跳跃一次", "jumping");
            AddPetActionButton(petActions, "挥手一次", "waving");

            GroupBox codexGroup = new GroupBox();
            codexGroup.Text = "Codex 状态";
            codexGroup.Width = 760;
            codexGroup.Height = 96;
            codexGroup.Margin = new Padding(0, 0, 0, 12);
            layout.Controls.Add(codexGroup, 0, 2);

            TableLayoutPanel codexLayout = new TableLayoutPanel();
            codexLayout.Dock = DockStyle.Fill;
            codexLayout.Padding = new Padding(12);
            codexLayout.ColumnCount = 3;
            codexLayout.RowCount = 2;
            codexLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190F));
            codexLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
            codexLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            codexGroup.Controls.Add(codexLayout);

            codexFollowCheck = new CheckBox();
            codexFollowCheck.Text = "自动跟随";
            codexFollowCheck.AutoSize = true;
            codexFollowCheck.Checked = codexFollowGetter != null && codexFollowGetter();
            codexFollowCheck.CheckedChanged += delegate
            {
                if (codexFollowSetter != null)
                {
                    codexFollowSetter(codexFollowCheck.Checked);
                }

                RefreshCodexStatusLine();
            };
            codexLayout.Controls.Add(codexFollowCheck, 0, 0);

            Button refreshCodexButton = CreateActionButton("立即检查");
            refreshCodexButton.Enabled = codexStatusProvider != null;
            refreshCodexButton.Click += delegate { RefreshCodexStatusLine(); };
            codexLayout.Controls.Add(refreshCodexButton, 1, 0);

            codexStateLabel = CreateMutedLabel("");
            codexStateLabel.AutoSize = false;
            codexStateLabel.Dock = DockStyle.Fill;
            codexLayout.Controls.Add(codexStateLabel, 0, 1);
            codexLayout.SetColumnSpan(codexStateLabel, 3);
            RefreshCodexStatusLine();

            GroupBox notesGroup = new GroupBox();
            notesGroup.Text = "便签";
            notesGroup.Width = 760;
            notesGroup.Height = 104;
            notesGroup.Margin = new Padding(0, 0, 0, 12);
            layout.Controls.Add(notesGroup, 0, 3);

            TableLayoutPanel notesLayout = new TableLayoutPanel();
            notesLayout.Dock = DockStyle.Fill;
            notesLayout.Padding = new Padding(12);
            notesLayout.ColumnCount = 4;
            notesLayout.RowCount = 2;
            notesLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82F));
            notesLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 450F));
            notesLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88F));
            notesLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            notesGroup.Controls.Add(notesLayout);

            TextBox notesRootBox = new TextBox();
            notesRootBox.Text = NotesStore.Root;
            notesRootBox.Dock = DockStyle.Fill;
            Button browseNotesRootButton = CreateActionButton("选择...");
            Button resetNotesRootButton = CreateActionButton("恢复默认");
            Label notesRootStatus = CreateMutedLabel("只显示由诺诺创建的便签，不会读取该目录中的其他文件。");
            notesRootStatus.AutoSize = false;
            notesRootStatus.Dock = DockStyle.Fill;
            notesLayout.Controls.Add(CreateLabel("保存位置"), 0, 0);
            notesLayout.Controls.Add(notesRootBox, 1, 0);
            notesLayout.Controls.Add(browseNotesRootButton, 2, 0);
            notesLayout.Controls.Add(resetNotesRootButton, 3, 0);
            notesLayout.Controls.Add(notesRootStatus, 1, 1);
            notesLayout.SetColumnSpan(notesRootStatus, 3);

            browseNotesRootButton.Click += delegate
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "选择便签保存位置";
                    dialog.ShowNewFolderButton = true;
                    string current = notesRootBox.Text.Trim();
                    if (Directory.Exists(current))
                    {
                        dialog.SelectedPath = current;
                    }

                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        notesRootBox.Text = dialog.SelectedPath;
                    }
                }
            };

            resetNotesRootButton.Click += delegate
            {
                try
                {
                    NoteSettingsStore.ResetRoot();
                    NotesStore.EnsureSeed();
                    notesRootBox.Text = NotesStore.Root;
                    selectedNotePath = null;
                    notesRootStatus.Text = "已恢复默认便签位置，不会读取目录中的其他文件。";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "恢复便签位置失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            notesRootBox.Leave += delegate
            {
                string normalized;
                string message;
                if (NoteSettingsStore.TryNormalizeRoot(notesRootBox.Text, out normalized, out message))
                {
                    notesRootBox.Text = normalized;
                }
            };

            GroupBox hotkeyGroup = new GroupBox();
            hotkeyGroup.Text = "快捷键";
            hotkeyGroup.Width = 760;
            hotkeyGroup.Height = 330;
            layout.Controls.Add(hotkeyGroup, 0, 4);

            List<HotkeyDefinition> definitions = HotkeySettingsStore.Load();
            TableLayoutPanel hotkeys = new TableLayoutPanel();
            hotkeys.Dock = DockStyle.Fill;
            hotkeys.Padding = new Padding(12);
            hotkeys.ColumnCount = 4;
            hotkeys.RowCount = definitions.Count + 1;
            hotkeys.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72F));
            hotkeys.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190F));
            hotkeys.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210F));
            hotkeys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            hotkeyGroup.Controls.Add(hotkeys);

            hotkeys.Controls.Add(CreateLabel("启用"), 0, 0);
            hotkeys.Controls.Add(CreateLabel("动作"), 1, 0);
            hotkeys.Controls.Add(CreateLabel("快捷键"), 2, 0);
            hotkeys.Controls.Add(CreateLabel("说明"), 3, 0);

            List<CheckBox> enabledChecks = new List<CheckBox>();
            List<TextBox> gestureBoxes = new List<TextBox>();
            for (int i = 0; i < definitions.Count; i++)
            {
                HotkeyDefinition definition = definitions[i];
                CheckBox enabled = new CheckBox();
                enabled.Checked = definition.Enabled;
                enabled.Dock = DockStyle.Fill;
                TextBox gesture = new TextBox();
                gesture.Text = definition.Gesture;
                gesture.Width = 190;
                gesture.KeyDown += CaptureHotkeyText;
                enabledChecks.Add(enabled);
                gestureBoxes.Add(gesture);

                hotkeys.Controls.Add(enabled, 0, i + 1);
                hotkeys.Controls.Add(CreateLabel(definition.Label), 1, i + 1);
                hotkeys.Controls.Add(gesture, 2, i + 1);
                hotkeys.Controls.Add(CreateMutedLabel(definition.Description), 3, i + 1);
            }

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Top;
            actions.Height = 48;
            actions.Margin = new Padding(0, 12, 0, 0);
            Button saveNotesRootButton = CreateActionButton("保存便签位置");
            Button saveButton = CreateActionButton("保存快捷键");
            Button resetButton = CreateActionButton("恢复默认");
            Label status = CreateMutedLabel(GlobalHotkeyManager.LastStatus);
            status.Width = 420;
            actions.Controls.Add(saveNotesRootButton);
            actions.Controls.Add(saveButton);
            actions.Controls.Add(resetButton);
            actions.Controls.Add(status);
            layout.Controls.Add(actions, 0, 5);

            Action saveNotesRoot = delegate
            {
                try
                {
                    NoteSettingsStore.SaveRoot(notesRootBox.Text);
                    NotesStore.EnsureSeed();
                    notesRootBox.Text = NotesStore.Root;
                    selectedNotePath = null;
                    notesRootStatus.Text = "已保存便签位置，不会读取目录中的其他文件。";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "保存便签位置失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            saveNotesRootButton.Click += delegate { saveNotesRoot(); };
            notesRootBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter)
                {
                    return;
                }

                e.Handled = true;
                e.SuppressKeyPress = true;
                saveNotesRoot();
            };

            saveButton.Click += delegate
            {
                for (int i = 0; i < definitions.Count; i++)
                {
                    definitions[i].Enabled = enabledChecks[i].Checked;
                    definitions[i].Gesture = gestureBoxes[i].Text.Trim();
                }

                string validation;
                if (!HotkeySettingsStore.Validate(definitions, out validation))
                {
                    MessageBox.Show(this, validation, "快捷键无效", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                HotkeySettingsStore.Save(definitions);
                status.Text = "已保存，快捷键已重新注册。";
            };

            resetButton.Click += delegate
            {
                HotkeySettingsStore.Reset();
                BuildSettingsView();
            };
        }

        private void RefreshCodexStatusLine()
        {
            if (codexStateLabel == null || codexStateLabel.IsDisposed)
            {
                return;
            }

            if (codexFollowCheck != null && codexFollowGetter != null)
            {
                bool enabled = codexFollowGetter();
                if (codexFollowCheck.Checked != enabled)
                {
                    codexFollowCheck.Checked = enabled;
                }
            }

            if (codexStatusProvider == null)
            {
                codexStateLabel.Text = "当前: 未连接";
                return;
            }

            CodexActivitySnapshot snapshot = codexStatusProvider();
            if (snapshot == null)
            {
                codexStateLabel.Text = "当前: 未知";
                return;
            }

            codexStateLabel.Text = String.IsNullOrWhiteSpace(snapshot.Detail)
                ? "当前: " + snapshot.Label
                : "当前: " + snapshot.Label + " · " + snapshot.Detail;
        }

        private void AddPetActionButton(FlowLayoutPanel panel, string label, string stateId)
        {
            Button button = CreateActionButton(label);
            button.Enabled = petAction != null;
            button.Click += delegate
            {
                if (petAction == null)
                {
                    return;
                }

                try
                {
                    petAction(stateId, !String.Equals(stateId, "idle", StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "执行宠物动作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            panel.Controls.Add(button);
        }

        private void CaptureHotkeyText(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin)
            {
                return;
            }

            TextBox box = (TextBox)sender;
            box.Text = HotkeyGesture.FromKeyEvent(e);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private Label CreateLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = currentTheme.Text;
            label.BackColor = currentTheme.ContentBack;
            return label;
        }

        private Label CreateMutedLabel(string text)
        {
            Label label = CreateLabel(text);
            label.ForeColor = currentTheme.MutedText;
            return label;
        }

        private Label CreateInfoLabel()
        {
            Label label = new Label();
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = currentTheme.Text;
            label.BackColor = currentTheme.ContentBack;
            label.AutoEllipsis = true;
            return label;
        }

        private Button CreateActionButton(string text)
        {
            Button button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.Height = 30;
            button.Margin = new Padding(0, 0, 8, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = currentTheme.Border;
            button.FlatAppearance.MouseOverBackColor = currentTheme.NavHot;
            button.FlatAppearance.MouseDownBackColor = currentTheme.NavSelected;
            button.BackColor = currentTheme.ButtonBack;
            button.ForeColor = currentTheme.Text;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private ListView CreateDetailsList()
        {
            ListView list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.MultiSelect = false;
            list.GridLines = true;
            list.HideSelection = false;
            list.BackColor = currentTheme.InputBack;
            list.ForeColor = currentTheme.Text;
            list.BorderStyle = BorderStyle.FixedSingle;
            return list;
        }

        private static string Preview(string text)
        {
            if (String.IsNullOrEmpty(text))
            {
                return "";
            }

            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length > 90 ? text.Substring(0, 90) + "..." : text;
        }

        private static string FormatBytesOrChars(ClipboardHistoryItem item)
        {
            if (item.Kind == "文本")
            {
                return item.Length.ToString(CultureInfo.InvariantCulture) + " 字符";
            }

            return FormatBytes(item.Length);
        }

        private static string ColorToText(Color color)
        {
            return "#" + color.R.ToString("X2", CultureInfo.InvariantCulture) + color.G.ToString("X2", CultureInfo.InvariantCulture) + color.B.ToString("X2", CultureInfo.InvariantCulture) +
                " · RGB(" + color.R.ToString(CultureInfo.InvariantCulture) + ", " + color.G.ToString(CultureInfo.InvariantCulture) + ", " + color.B.ToString(CultureInfo.InvariantCulture) + ")";
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            }

            double value = bytes;
            string[] units = new string[] { "B", "KB", "MB", "GB", "TB" };
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return value.ToString("0.##", CultureInfo.CurrentCulture) + " " + units[unit];
        }
    }

    internal sealed class QuickEntry
    {
        public string Keyword;
        public string Target;
        public DateTime CreatedAt;
        public DateTime UpdatedAt;
    }

    internal static class QuickEntryStore
    {
        public static List<QuickEntry> Load(string file)
        {
            List<QuickEntry> entries = new List<QuickEntry>();
            List<string[]> rows = PanelStorage.LoadRows(file);
            for (int i = 0; i < rows.Count; i++)
            {
                string[] row = rows[i];
                if (row.Length < 4)
                {
                    continue;
                }

                QuickEntry entry = new QuickEntry();
                entry.Keyword = row[0];
                entry.Target = row[1];
                entry.CreatedAt = PanelStorage.ParseDate(row[2]);
                entry.UpdatedAt = PanelStorage.ParseDate(row[3]);
                entries.Add(entry);
            }

            return entries;
        }

        public static void Save(string file, List<QuickEntry> entries)
        {
            List<string[]> rows = new List<string[]>();
            for (int i = 0; i < entries.Count; i++)
            {
                QuickEntry entry = entries[i];
                rows.Add(new string[]
                {
                    entry.Keyword ?? "",
                    entry.Target ?? "",
                    PanelStorage.FormatDate(entry.CreatedAt),
                    PanelStorage.FormatDate(entry.UpdatedAt)
                });
            }

            PanelStorage.SaveRows(file, rows);
        }
    }

    internal static class QuickTargetLauncher
    {
        public static void Open(string target)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(NormalizeTarget(target));
            startInfo.UseShellExecute = true;
            Process.Start(startInfo);
        }

        public static string NormalizeTarget(string target)
        {
            if (String.IsNullOrWhiteSpace(target))
            {
                throw new InvalidOperationException("请输入关键词、网址、路径或命令。");
            }

            string trimmed = target.Trim();
            string expanded = Environment.ExpandEnvironmentVariables(trimmed);
            if (File.Exists(expanded) || Directory.Exists(expanded) || HasUriScheme(expanded))
            {
                return expanded;
            }

            if (LooksLikeBareUrl(expanded))
            {
                return "https://" + expanded;
            }

            return expanded;
        }

        private static bool HasUriScheme(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                !String.IsNullOrEmpty(uri.Scheme) &&
                uri.Scheme.Length > 1;
        }

        private static bool LooksLikeBareUrl(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.IndexOfAny(new char[] { ' ', '\t', '\r', '\n' }) >= 0)
            {
                return false;
            }

            return value.IndexOf('.') > 0 ||
                value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class QuickLauncherForm : Form
    {
        private readonly TextBox queryBox;
        private readonly Label hintLabel;
        private readonly Label statusLabel;
        private readonly Font titleFont;
        private readonly Font hintFont;
        private bool dragging;
        private Point dragCursor;
        private Point dragWindow;

        public QuickLauncherForm()
        {
            Text = "诺诺快速直达";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 154);
            BackColor = Color.FromArgb(248, 252, 253);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            DoubleBuffered = true;
            KeyPreview = true;

            titleFont = new Font(Font.FontFamily, 12.5F, FontStyle.Bold);
            hintFont = new Font(Font.FontFamily, 8.75F, FontStyle.Regular);

            Label title = new Label();
            title.Text = "快速直达";
            title.Font = titleFont;
            title.ForeColor = Color.FromArgb(26, 42, 54);
            title.SetBounds(24, 18, 190, 24);
            title.MouseDown += BeginDrag;
            title.MouseMove += ContinueDrag;
            title.MouseUp += EndDrag;
            Controls.Add(title);

            hintLabel = new Label();
            hintLabel.Text = "输入保存的关键词、网址、文件路径或系统命令";
            hintLabel.Font = hintFont;
            hintLabel.ForeColor = Color.FromArgb(83, 102, 116);
            hintLabel.TextAlign = ContentAlignment.MiddleRight;
            hintLabel.SetBounds(214, 20, 322, 22);
            hintLabel.MouseDown += BeginDrag;
            hintLabel.MouseMove += ContinueDrag;
            hintLabel.MouseUp += EndDrag;
            Controls.Add(hintLabel);

            queryBox = new TextBox();
            queryBox.BorderStyle = BorderStyle.FixedSingle;
            queryBox.Font = new Font(Font.FontFamily, 13F, FontStyle.Regular);
            queryBox.SetBounds(24, 58, 512, 31);
            queryBox.KeyDown += OnQueryKeyDown;
            Controls.Add(queryBox);

            statusLabel = new Label();
            statusLabel.Text = "保存的关键词会优先匹配；匹配不到时按网址、路径或命令直达";
            statusLabel.Font = hintFont;
            statusLabel.ForeColor = Color.FromArgb(83, 102, 116);
            statusLabel.SetBounds(24, 101, 512, 20);
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.MouseDown += BeginDrag;
            statusLabel.MouseMove += ContinueDrag;
            statusLabel.MouseUp += EndDrag;
            Controls.Add(statusLabel);

            Label footer = new Label();
            footer.Text = "Enter 直达    Esc 关闭";
            footer.Font = hintFont;
            footer.ForeColor = Color.FromArgb(96, 113, 126);
            footer.SetBounds(24, 124, 512, 20);
            footer.TextAlign = ContentAlignment.MiddleRight;
            footer.MouseDown += BeginDrag;
            footer.MouseMove += ContinueDrag;
            footer.MouseUp += EndDrag;
            Controls.Add(footer);

            MouseDown += BeginDrag;
            MouseMove += ContinueDrag;
            MouseUp += EndDrag;
            Deactivate += delegate
            {
                if (Visible)
                {
                    Hide();
                }
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= NativeMethods.CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (GraphicsPath path = NoNoPanelForm.RoundedRect(new Rectangle(0, 0, Width, Height), 12))
            {
                Region = new Region(path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = NoNoPanelForm.RoundedRect(bounds, 12))
            using (SolidBrush brush = new SolidBrush(BackColor))
            using (Pen pen = new Pen(Color.FromArgb(184, 217, 226)))
            {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                Hide();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                titleFont.Dispose();
                hintFont.Dispose();
            }

            base.Dispose(disposing);
        }

        public void ShowLauncher(Rectangle workingArea)
        {
            Location = new Point(
                workingArea.Left + (workingArea.Width - Width) / 2,
                workingArea.Top + Math.Max(48, (workingArea.Height - Height) / 3));
            if (!Visible)
            {
                Show();
            }

            WindowState = FormWindowState.Normal;
            Activate();
            queryBox.Focus();
            queryBox.SelectAll();
        }

        private void OnQueryKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                ExecuteQuery();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void ExecuteQuery()
        {
            string query = queryBox.Text.Trim();
            if (query.Length == 0)
            {
                return;
            }

            try
            {
                QuickTargetLauncher.Open(ResolveTarget(query));
                Hide();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string ResolveTarget(string query)
        {
            QuickEntry entry = FindStoredEntry(QuickEntryStore.Load(PanelStorage.LinksFile), query);
            if (entry == null)
            {
                entry = FindStoredEntry(QuickEntryStore.Load(PanelStorage.AppsFile), query);
            }

            return entry == null ? query : entry.Target;
        }

        private static QuickEntry FindStoredEntry(List<QuickEntry> entries, string query)
        {
            QuickEntry prefixMatch = null;
            for (int i = 0; i < entries.Count; i++)
            {
                QuickEntry entry = entries[i];
                string keyword = entry.Keyword ?? "";
                if (String.Equals(keyword, query, StringComparison.CurrentCultureIgnoreCase))
                {
                    return entry;
                }

                if (prefixMatch == null && keyword.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
                {
                    prefixMatch = entry;
                }
            }

            return prefixMatch;
        }

        private void BeginDrag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            dragging = true;
            dragCursor = Cursor.Position;
            dragWindow = Location;
        }

        private void ContinueDrag(object sender, MouseEventArgs e)
        {
            if (!dragging)
            {
                return;
            }

            Point cursor = Cursor.Position;
            Location = new Point(dragWindow.X + cursor.X - dragCursor.X, dragWindow.Y + cursor.Y - dragCursor.Y);
        }

        private void EndDrag(object sender, MouseEventArgs e)
        {
            dragging = false;
        }
    }

    internal sealed class WeatherInfo
    {
        public readonly string City;
        public readonly string Temperature;
        public readonly string Condition;
        public readonly DateTime RetrievedAt;

        public WeatherInfo(string city, string temperature, string condition, DateTime retrievedAt)
        {
            City = city;
            Temperature = temperature;
            Condition = condition;
            RetrievedAt = retrievedAt;
        }
    }

    internal static class WeatherService
    {
        private const string WeatherBaseUrl = "https://wttr.in/";

        public static WeatherInfo FetchCurrent()
        {
            return FetchCurrent("");
        }

        public static WeatherInfo FetchCurrent(string city)
        {
            EnableTls12();
            using (WebClient client = new WebClient())
            {
                client.Encoding = Encoding.UTF8;
                client.Headers[HttpRequestHeader.UserAgent] = "NoNoStandalone/1.0";
                string response = client.DownloadString(BuildWeatherUrl(city));
                return Parse(response);
            }
        }

        private static string BuildWeatherUrl(string city)
        {
            string normalizedCity = NormalizePart(city, "");
            string path = normalizedCity.Length == 0 ? "" : Uri.EscapeDataString(normalizedCity);
            return WeatherBaseUrl + path + "?format=j1&lang=zh-cn";
        }

        private static WeatherInfo Parse(string response)
        {
            if (String.IsNullOrWhiteSpace(response))
            {
                throw new InvalidOperationException("天气服务没有返回有效内容。");
            }

            object parsed = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(response);
            Dictionary<string, object> root = parsed as Dictionary<string, object>;
            if (root == null)
            {
                throw new InvalidOperationException("天气服务返回格式无法识别。");
            }

            string city = BuildCity(root);
            string tempC = ReadString(root, "current_condition", 0, "temp_C");
            string condition = ReadString(root, "current_condition", 0, "lang_zh-cn", 0, "value");
            if (String.IsNullOrWhiteSpace(condition))
            {
                condition = ReadString(root, "current_condition", 0, "lang_xx", 0, "value");
            }
            if (String.IsNullOrWhiteSpace(condition))
            {
                condition = ReadString(root, "current_condition", 0, "weatherDesc", 0, "value");
            }

            string temperature = String.IsNullOrWhiteSpace(tempC) ? "未知温度" : tempC.Trim() + "°C";
            condition = NormalizePart(condition, "未知天气");

            return new WeatherInfo(city, temperature, condition, DateTime.Now);
        }

        private static string BuildCity(Dictionary<string, object> root)
        {
            string city = ReadString(root, "nearest_area", 0, "areaName", 0, "value");
            string region = ReadString(root, "nearest_area", 0, "region", 0, "value");
            string country = ReadString(root, "nearest_area", 0, "country", 0, "value");

            city = NormalizePart(city, "");
            region = NormalizePart(region, "");
            country = NormalizePart(country, "");

            if (city.Length == 0)
            {
                city = NormalizePart(ReadString(root, "request", 0, "query"), "未知城市");
            }

            if (region.Length > 0 && !String.Equals(city, region, StringComparison.CurrentCultureIgnoreCase))
            {
                city = city + "，" + region;
            }
            else if (country.Length > 0 && !String.Equals(city, country, StringComparison.CurrentCultureIgnoreCase))
            {
                city = city + "，" + country;
            }

            return city;
        }

        private static string ReadString(Dictionary<string, object> root, params object[] path)
        {
            object current = root;
            for (int i = 0; i < path.Length; i++)
            {
                string key = path[i] as string;
                if (key != null)
                {
                    Dictionary<string, object> dict = current as Dictionary<string, object>;
                    object next;
                    if (dict == null || !dict.TryGetValue(key, out next))
                    {
                        return "";
                    }

                    current = next;
                    continue;
                }

                if (path[i] is int)
                {
                    object[] array = current as object[];
                    int index = (int)path[i];
                    if (array == null || index < 0 || index >= array.Length)
                    {
                        return "";
                    }

                    current = array[index];
                    continue;
                }

                return "";
            }

            return current == null ? "" : Convert.ToString(current, CultureInfo.InvariantCulture);
        }

        private static string NormalizePart(string value, string fallback)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            return value.Trim();
        }

        private static void EnableTls12()
        {
            try
            {
                ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072;
            }
            catch
            {
            }
        }
    }

    internal sealed class WeatherDialog : Form
    {
        private const string AutoCityLabel = "自动定位";

        private static readonly string[] CityOptions = new string[]
        {
            AutoCityLabel,
            "北京",
            "上海",
            "广州",
            "深圳",
            "杭州",
            "南京",
            "苏州",
            "成都",
            "重庆",
            "武汉",
            "西安",
            "天津",
            "郑州",
            "长沙",
            "青岛",
            "厦门"
        };

        private readonly ComboBox citySelector;
        private readonly Label cityValue;
        private readonly Label temperatureValue;
        private readonly Label conditionValue;
        private readonly Label statusLabel;
        private readonly Button refreshButton;
        private bool loading;

        public WeatherDialog()
        {
            Text = "诺诺天气";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(410, 250);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(18, 16, 18, 12);
            layout.ColumnCount = 2;
            layout.RowCount = 7;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            Controls.Add(layout);

            Label title = new Label();
            title.Text = "当前天气";
            title.Font = new Font(Font.FontFamily, 12F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(24, 42, 54);
            title.Dock = DockStyle.Fill;
            title.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(title, 0, 0);
            layout.SetColumnSpan(title, 2);

            AddFieldLabel(layout, 1, "选择城市");
            citySelector = new ComboBox();
            citySelector.Dock = DockStyle.Fill;
            citySelector.DropDownStyle = ComboBoxStyle.DropDown;
            citySelector.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            citySelector.AutoCompleteSource = AutoCompleteSource.ListItems;
            citySelector.Margin = new Padding(0, 4, 0, 4);
            for (int i = 0; i < CityOptions.Length; i++)
            {
                citySelector.Items.Add(CityOptions[i]);
            }

            citySelector.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    BeginLoad();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            layout.Controls.Add(citySelector, 1, 1);
            SelectCity(WeatherSettingsStore.LoadCity());

            cityValue = AddWeatherRow(layout, 2, "城市", "正在获取...");
            temperatureValue = AddWeatherRow(layout, 3, "温度", "正在获取...");
            conditionValue = AddWeatherRow(layout, 4, "天气", "正在获取...");

            statusLabel = new Label();
            statusLabel.Text = "正在获取天气...";
            statusLabel.ForeColor = Color.FromArgb(83, 102, 116);
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(statusLabel, 0, 5);
            layout.SetColumnSpan(statusLabel, 2);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.WrapContents = false;
            actions.Padding = new Padding(0, 6, 0, 0);
            layout.Controls.Add(actions, 0, 6);
            layout.SetColumnSpan(actions, 2);

            Button closeButton = new Button();
            closeButton.Text = "关闭";
            closeButton.DialogResult = DialogResult.Cancel;
            closeButton.Width = 78;
            actions.Controls.Add(closeButton);

            refreshButton = new Button();
            refreshButton.Text = "查询";
            refreshButton.Width = 78;
            refreshButton.Click += delegate { BeginLoad(); };
            actions.Controls.Add(refreshButton);

            AcceptButton = refreshButton;
            CancelButton = closeButton;
            Shown += delegate { BeginLoad(); };
        }

        public static void ShowWeather(IWin32Window owner)
        {
            using (WeatherDialog dialog = new WeatherDialog())
            {
                Form ownerForm = owner as Form;
                if (ownerForm != null)
                {
                    dialog.TopMost = ownerForm.TopMost;
                }

                dialog.ShowDialog(owner);
            }
        }

        private static void AddFieldLabel(TableLayoutPanel layout, int row, string labelText)
        {
            Label label = new Label();
            label.Text = labelText;
            label.ForeColor = Color.FromArgb(83, 102, 116);
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(label, 0, row);
        }

        private static Label AddWeatherRow(TableLayoutPanel layout, int row, string labelText, string valueText)
        {
            AddFieldLabel(layout, row, labelText);

            Label value = new Label();
            value.Text = valueText;
            value.ForeColor = Color.FromArgb(24, 42, 54);
            value.Dock = DockStyle.Fill;
            value.TextAlign = ContentAlignment.MiddleLeft;
            value.AutoEllipsis = true;
            layout.Controls.Add(value, 1, row);
            return value;
        }

        private void BeginLoad()
        {
            if (loading)
            {
                return;
            }

            string requestedCity = SelectedCity();
            string requestedLabel = DisplayCity(requestedCity);
            loading = true;
            refreshButton.Enabled = false;
            cityValue.Text = requestedLabel;
            temperatureValue.Text = "正在获取...";
            conditionValue.Text = "正在获取...";
            statusLabel.ForeColor = Color.FromArgb(83, 102, 116);
            statusLabel.Text = "正在获取 " + requestedLabel + " 天气...";

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    WeatherInfo info = WeatherService.FetchCurrent(requestedCity);
                    PostToUi(delegate { ApplyWeather(info, requestedCity); });
                }
                catch (Exception ex)
                {
                    PostToUi(delegate { ApplyError(ex, requestedLabel); });
                }
            });
        }

        private void ApplyWeather(WeatherInfo info, string requestedCity)
        {
            loading = false;
            refreshButton.Enabled = true;
            WeatherSettingsStore.SaveCity(requestedCity);
            cityValue.Text = String.IsNullOrWhiteSpace(requestedCity) ? info.City : requestedCity;
            temperatureValue.Text = info.Temperature;
            conditionValue.Text = info.Condition;
            statusLabel.ForeColor = Color.FromArgb(83, 102, 116);
            statusLabel.Text = "更新时间 " + info.RetrievedAt.ToString("HH:mm", CultureInfo.CurrentCulture);
        }

        private void ApplyError(Exception ex, string requestedLabel)
        {
            loading = false;
            refreshButton.Enabled = true;
            cityValue.Text = requestedLabel;
            temperatureValue.Text = "未获取";
            conditionValue.Text = "未获取";
            statusLabel.ForeColor = Color.FromArgb(166, 73, 54);
            statusLabel.Text = "获取失败：" + ex.Message;
        }

        private void SelectCity(string city)
        {
            if (String.IsNullOrWhiteSpace(city))
            {
                citySelector.SelectedItem = AutoCityLabel;
                return;
            }

            citySelector.Text = city.Trim();
        }

        private string SelectedCity()
        {
            string city = citySelector.Text.Trim();
            if (city.Length == 0 || String.Equals(city, AutoCityLabel, StringComparison.CurrentCultureIgnoreCase))
            {
                return "";
            }

            return city;
        }

        private static string DisplayCity(string city)
        {
            return String.IsNullOrWhiteSpace(city) ? AutoCityLabel : city.Trim();
        }

        private void PostToUi(MethodInvoker action)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!IsDisposed)
                    {
                        action();
                    }
                });
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    internal static class WeatherSettingsStore
    {
        private const string WeatherCityPreference = "weather-city";

        public static string LoadCity()
        {
            try
            {
                return PanelStorage.LoadPreference(WeatherCityPreference);
            }
            catch
            {
                return "";
            }
        }

        public static void SaveCity(string city)
        {
            try
            {
                PanelStorage.SavePreference(WeatherCityPreference, city ?? "");
            }
            catch
            {
            }
        }
    }

    internal static class PanelStorage
    {
        public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoNoStandalone");
        public static readonly string LinksFile = Path.Combine(Root, "links.tsv");
        public static readonly string AppsFile = Path.Combine(Root, "apps.tsv");
        public static readonly string HotkeysFile = Path.Combine(Root, "hotkeys.tsv");
        public static readonly string PreferencesFile = Path.Combine(Root, "preferences.tsv");
        public static readonly string NotesIndexFile = Path.Combine(Root, "notes-index.tsv");
        public static readonly string NotesRoot = Path.Combine(Root, "notes");

        public static List<string[]> LoadRows(string file)
        {
            EnsureRoot();
            List<string[]> rows = new List<string[]>();
            if (!File.Exists(file))
            {
                return rows;
            }

            string[] lines = File.ReadAllLines(file, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                if (String.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                string[] parts = lines[i].Split('\t');
                for (int p = 0; p < parts.Length; p++)
                {
                    parts[p] = Decode(parts[p]);
                }

                rows.Add(parts);
            }

            return rows;
        }

        public static void SaveRows(string file, List<string[]> rows)
        {
            EnsureRoot();
            List<string> lines = new List<string>();
            for (int i = 0; i < rows.Count; i++)
            {
                string[] encoded = new string[rows[i].Length];
                for (int p = 0; p < rows[i].Length; p++)
                {
                    encoded[p] = Encode(rows[i][p]);
                }

                lines.Add(String.Join("\t", encoded));
            }

            File.WriteAllLines(file, lines.ToArray(), Encoding.UTF8);
        }

        public static string LoadPreference(string key)
        {
            List<string[]> rows = LoadRows(PreferencesFile);
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Length >= 2 && String.Equals(rows[i][0], key, StringComparison.OrdinalIgnoreCase))
                {
                    return rows[i][1];
                }
            }

            return "";
        }

        public static void SavePreference(string key, string value)
        {
            List<string[]> rows = LoadRows(PreferencesFile);
            bool updated = false;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Length >= 1 && String.Equals(rows[i][0], key, StringComparison.OrdinalIgnoreCase))
                {
                    rows[i] = new string[] { key, value ?? "" };
                    updated = true;
                    break;
                }
            }

            if (!updated)
            {
                rows.Add(new string[] { key, value ?? "" });
            }

            SaveRows(PreferencesFile, rows);
        }

        public static void EnsureRoot()
        {
            Directory.CreateDirectory(Root);
        }

        public static string FormatDate(DateTime value)
        {
            if (value == DateTime.MinValue)
            {
                value = DateTime.Now;
            }

            return value.ToString("o", CultureInfo.InvariantCulture);
        }

        public static DateTime ParseDate(string value)
        {
            DateTime parsed;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
            {
                return parsed.ToLocalTime();
            }

            return DateTime.Now;
        }

        public static string SafeFileName(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return "未命名";
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder builder = new StringBuilder(value.Trim());
            for (int i = 0; i < builder.Length; i++)
            {
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (builder[i] == invalid[j])
                    {
                        builder[i] = '_';
                        break;
                    }
                }
            }

            return builder.ToString();
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        }

        private static string Decode(string value)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch
            {
                return "";
            }
        }
    }

    internal sealed class PanelTheme
    {
        public readonly string Id;
        public readonly string Name;
        public readonly string Description;
        public readonly Color WindowBack;
        public readonly Color ContentBack;
        public readonly Color NavBack;
        public readonly Color NavNormal;
        public readonly Color NavHot;
        public readonly Color NavSelected;
        public readonly Color NavSelectedBorder;
        public readonly Color CardBack;
        public readonly Color InputBack;
        public readonly Color ButtonBack;
        public readonly Color Border;
        public readonly Color HeaderText;
        public readonly Color Text;
        public readonly Color MutedText;
        public readonly Color Accent;
        public readonly Color AccentSoft;

        public PanelTheme(
            string id,
            string name,
            string description,
            Color windowBack,
            Color contentBack,
            Color navBack,
            Color navNormal,
            Color navHot,
            Color navSelected,
            Color navSelectedBorder,
            Color cardBack,
            Color inputBack,
            Color buttonBack,
            Color border,
            Color headerText,
            Color text,
            Color mutedText,
            Color accent,
            Color accentSoft)
        {
            Id = id;
            Name = name;
            Description = description;
            WindowBack = windowBack;
            ContentBack = contentBack;
            NavBack = navBack;
            NavNormal = navNormal;
            NavHot = navHot;
            NavSelected = navSelected;
            NavSelectedBorder = navSelectedBorder;
            CardBack = cardBack;
            InputBack = inputBack;
            ButtonBack = buttonBack;
            Border = border;
            HeaderText = headerText;
            Text = text;
            MutedText = mutedText;
            Accent = accent;
            AccentSoft = accentSoft;
        }
    }

    internal static class PanelThemeStore
    {
        private const string PreferenceKey = "panel-theme";

        public static readonly PanelTheme[] All = new PanelTheme[]
        {
            new PanelTheme(
                "sea-mist",
                "海雾蓝",
                "清爽、低干扰，适合白天和浅色桌面。",
                Color.FromArgb(244, 247, 250),
                Color.FromArgb(244, 247, 250),
                Color.FromArgb(229, 245, 252),
                Color.FromArgb(235, 248, 253),
                Color.FromArgb(241, 251, 254),
                Color.FromArgb(203, 237, 248),
                Color.FromArgb(72, 181, 209),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(248, 252, 254),
                Color.FromArgb(190, 217, 226),
                Color.FromArgb(22, 53, 68),
                Color.FromArgb(35, 50, 62),
                Color.FromArgb(86, 103, 116),
                Color.FromArgb(0, 126, 160),
                Color.FromArgb(181, 229, 244)),
            new PanelTheme(
                "cedar",
                "雪松绿",
                "柔和、稳定，适合长时间整理资料。",
                Color.FromArgb(243, 247, 244),
                Color.FromArgb(246, 249, 246),
                Color.FromArgb(228, 240, 232),
                Color.FromArgb(236, 246, 239),
                Color.FromArgb(242, 249, 244),
                Color.FromArgb(205, 231, 215),
                Color.FromArgb(68, 151, 112),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(249, 252, 250),
                Color.FromArgb(190, 215, 199),
                Color.FromArgb(31, 58, 45),
                Color.FromArgb(38, 56, 48),
                Color.FromArgb(88, 108, 96),
                Color.FromArgb(46, 130, 95),
                Color.FromArgb(204, 235, 217)),
            new PanelTheme(
                "graphite-amber",
                "晨曦杏",
                "温和明亮的杏色套装，适合夜间也不压暗桌面。",
                Color.FromArgb(250, 246, 239),
                Color.FromArgb(252, 249, 244),
                Color.FromArgb(246, 232, 210),
                Color.FromArgb(251, 239, 222),
                Color.FromArgb(254, 246, 236),
                Color.FromArgb(239, 213, 176),
                Color.FromArgb(209, 128, 54),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(253, 249, 243),
                Color.FromArgb(225, 199, 166),
                Color.FromArgb(86, 54, 34),
                Color.FromArgb(70, 55, 44),
                Color.FromArgb(118, 92, 70),
                Color.FromArgb(189, 102, 43),
                Color.FromArgb(246, 220, 185)),
            new PanelTheme(
                "slate-teal",
                "云杉浅青",
                "偏冷的浅青套装，清爽但不使用黑色底面。",
                Color.FromArgb(241, 248, 248),
                Color.FromArgb(246, 251, 251),
                Color.FromArgb(224, 241, 239),
                Color.FromArgb(235, 248, 246),
                Color.FromArgb(243, 251, 250),
                Color.FromArgb(202, 230, 226),
                Color.FromArgb(61, 157, 145),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(249, 253, 253),
                Color.FromArgb(185, 214, 211),
                Color.FromArgb(29, 67, 65),
                Color.FromArgb(38, 61, 60),
                Color.FromArgb(83, 111, 109),
                Color.FromArgb(37, 139, 128),
                Color.FromArgb(198, 236, 231)),
            new PanelTheme(
                "berry-ink",
                "莓果墨",
                "温和、有识别度，适合想要一点个性的工作区。",
                Color.FromArgb(247, 244, 248),
                Color.FromArgb(249, 247, 250),
                Color.FromArgb(240, 231, 243),
                Color.FromArgb(248, 240, 249),
                Color.FromArgb(252, 247, 252),
                Color.FromArgb(232, 211, 237),
                Color.FromArgb(157, 92, 166),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(252, 249, 253),
                Color.FromArgb(219, 201, 224),
                Color.FromArgb(62, 39, 68),
                Color.FromArgb(57, 47, 61),
                Color.FromArgb(105, 88, 111),
                Color.FromArgb(142, 68, 151),
                Color.FromArgb(235, 213, 239))
        };

        public static PanelTheme DefaultTheme
        {
            get { return All[0]; }
        }

        public static PanelTheme Load()
        {
            try
            {
                return Find(PanelStorage.LoadPreference(PreferenceKey));
            }
            catch
            {
                return DefaultTheme;
            }
        }

        public static void Save(PanelTheme theme)
        {
            if (theme == null)
            {
                return;
            }

            try
            {
                PanelStorage.SavePreference(PreferenceKey, theme.Id);
            }
            catch
            {
            }
        }

        public static PanelTheme Find(string id)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (String.Equals(All[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return All[i];
                }
            }

            return DefaultTheme;
        }
    }

    internal sealed class PetAppearance
    {
        public readonly string Id;
        public readonly string Name;
        public readonly string Description;
        public readonly Color BodyTint;
        public readonly Color ScreenTint;
        public readonly Color GlowTint;
        public readonly float BodyBlend;
        public readonly float ScreenBlend;
        public readonly float GlowBlend;

        public PetAppearance(string id, string name, string description, Color bodyTint, Color screenTint, Color glowTint, float bodyBlend, float screenBlend, float glowBlend)
        {
            Id = id;
            Name = name;
            Description = description;
            BodyTint = bodyTint;
            ScreenTint = screenTint;
            GlowTint = glowTint;
            BodyBlend = bodyBlend;
            ScreenBlend = screenBlend;
            GlowBlend = glowBlend;
        }
    }

    internal static class PetAppearanceStore
    {
        private const string PreferenceKey = "pet-appearance";

        public static readonly PetAppearance[] All = new PetAppearance[]
        {
            new PetAppearance(
                "classic",
                "星白原型",
                "保留诺诺的白色机身和青蓝能量光。",
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(18, 22, 28),
                Color.FromArgb(65, 224, 255),
                0.0F,
                0.0F,
                0.0F),
            new PetAppearance(
                "arctic-blue",
                "极地蓝",
                "冰川蓝机身与电光蓝能量，冷静、清晰。",
                Color.FromArgb(126, 178, 245),
                Color.FromArgb(7, 24, 58),
                Color.FromArgb(46, 137, 255),
                0.52F,
                0.64F,
                0.94F),
            new PetAppearance(
                "mint-guard",
                "薄荷守卫",
                "浅绿机身和柔和绿光，适合长时间桌面陪伴。",
                Color.FromArgb(214, 248, 228),
                Color.FromArgb(16, 42, 34),
                Color.FromArgb(90, 233, 162),
                0.24F,
                0.38F,
                0.84F),
            new PetAppearance(
                "amber-core",
                "琥珀核心",
                "暖白外壳与琥珀能量，失败和等待状态更醒目。",
                Color.FromArgb(255, 234, 195),
                Color.FromArgb(46, 30, 18),
                Color.FromArgb(255, 174, 69),
                0.26F,
                0.42F,
                0.88F),
            new PetAppearance(
                "berry-neon",
                "莓果霓虹",
                "淡紫机身配莓粉光，有一点个性但不吵。",
                Color.FromArgb(244, 218, 255),
                Color.FromArgb(43, 24, 50),
                Color.FromArgb(244, 107, 214),
                0.24F,
                0.42F,
                0.86F),
            new PetAppearance(
                "terminal-shadow",
                "终端暗影",
                "深灰机身和终端绿光，适合暗色编辑器。",
                Color.FromArgb(92, 103, 108),
                Color.FromArgb(10, 18, 16),
                Color.FromArgb(89, 237, 143),
                0.48F,
                0.52F,
                0.9F)
        };

        public static PetAppearance DefaultAppearance
        {
            get { return All[0]; }
        }

        public static PetAppearance Load()
        {
            try
            {
                return Find(PanelStorage.LoadPreference(PreferenceKey));
            }
            catch
            {
                return DefaultAppearance;
            }
        }

        public static void Save(PetAppearance appearance)
        {
            if (appearance == null)
            {
                return;
            }

            try
            {
                PanelStorage.SavePreference(PreferenceKey, appearance.Id);
            }
            catch
            {
            }
        }

        public static PetAppearance Find(string id)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (String.Equals(All[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return All[i];
                }
            }

            return DefaultAppearance;
        }
    }

    internal static class PetAppearanceRenderer
    {
        public static Bitmap Apply(Bitmap source, PetAppearance appearance)
        {
            if (appearance == null || String.Equals(appearance.Id, "classic", StringComparison.OrdinalIgnoreCase))
            {
                return (Bitmap)source.Clone();
            }

            Bitmap output = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
            Rectangle rect = new Rectangle(0, 0, source.Width, source.Height);
            BitmapData sourceData = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            BitmapData outputData = output.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int sourceStride = sourceData.Stride;
                int outputStride = outputData.Stride;
                int sourceBytes = Math.Abs(sourceStride) * source.Height;
                int outputBytes = Math.Abs(outputStride) * output.Height;
                byte[] sourceBuffer = new byte[sourceBytes];
                byte[] outputBuffer = new byte[outputBytes];
                Marshal.Copy(sourceData.Scan0, sourceBuffer, 0, sourceBytes);

                for (int y = 0; y < source.Height; y++)
                {
                    int sourceRow = y * sourceStride;
                    int outputRow = y * outputStride;
                    for (int x = 0; x < source.Width; x++)
                    {
                        int sourceIndex = sourceRow + x * 4;
                        int outputIndex = outputRow + x * 4;
                        int alpha = sourceBuffer[sourceIndex + 3];
                        if (alpha == 0)
                        {
                            outputBuffer[outputIndex] = 0;
                            outputBuffer[outputIndex + 1] = 0;
                            outputBuffer[outputIndex + 2] = 0;
                            outputBuffer[outputIndex + 3] = 0;
                            continue;
                        }

                        int blue = Unpremultiply(sourceBuffer[sourceIndex], alpha);
                        int green = Unpremultiply(sourceBuffer[sourceIndex + 1], alpha);
                        int red = Unpremultiply(sourceBuffer[sourceIndex + 2], alpha);
                        int max = Math.Max(red, Math.Max(green, blue));
                        int min = Math.Min(red, Math.Min(green, blue));
                        int brightness = (red + green + blue) / 3;
                        int saturation = max - min;

                        Color target;
                        float blend;
                        if (brightness < 70)
                        {
                            target = appearance.ScreenTint;
                            blend = appearance.ScreenBlend;
                        }
                        else if (saturation > 34 || (blue > red + 18 && green > red + 8))
                        {
                            target = appearance.GlowTint;
                            blend = appearance.GlowBlend;
                        }
                        else
                        {
                            target = appearance.BodyTint;
                            blend = appearance.BodyBlend;
                        }

                        red = Blend(red, target.R, blend);
                        green = Blend(green, target.G, blend);
                        blue = Blend(blue, target.B, blend);

                        outputBuffer[outputIndex] = Premultiply(blue, alpha);
                        outputBuffer[outputIndex + 1] = Premultiply(green, alpha);
                        outputBuffer[outputIndex + 2] = Premultiply(red, alpha);
                        outputBuffer[outputIndex + 3] = (byte)alpha;
                    }
                }

                Marshal.Copy(outputBuffer, 0, outputData.Scan0, outputBytes);
            }
            finally
            {
                output.UnlockBits(outputData);
                source.UnlockBits(sourceData);
            }

            return output;
        }

        private static int Blend(int source, int target, float amount)
        {
            if (amount <= 0F)
            {
                return source;
            }

            if (amount >= 1F)
            {
                return target;
            }

            return Clamp((int)Math.Round(source * (1F - amount) + target * amount));
        }

        private static int Unpremultiply(byte value, int alpha)
        {
            if (alpha <= 0)
            {
                return 0;
            }

            if (alpha >= 255)
            {
                return value;
            }

            return Clamp((int)Math.Round(value * 255.0 / alpha));
        }

        private static byte Premultiply(int value, int alpha)
        {
            return (byte)Clamp((int)Math.Round(Clamp(value) * alpha / 255.0));
        }

        private static int Clamp(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            if (value > 255)
            {
                return 255;
            }

            return value;
        }
    }

    internal struct MemorySnapshot
    {
        public long Total;
        public long Used;
    }

    internal static class SystemInfoProvider
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

        public static MemorySnapshot GetMemory()
        {
            MemoryStatusEx status = new MemoryStatusEx();
            status.dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            if (GlobalMemoryStatusEx(ref status))
            {
                MemorySnapshot snapshot = new MemorySnapshot();
                snapshot.Total = (long)status.ullTotalPhys;
                snapshot.Used = (long)(status.ullTotalPhys - status.ullAvailPhys);
                return snapshot;
            }

            return new MemorySnapshot();
        }

        public static string GetDiskSummary()
        {
            try
            {
                DriveInfo system = new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)));
                long used = system.TotalSize - system.AvailableFreeSpace;
                return system.Name + " " + FunctionalNoNoPanelForm.FormatBytes(used) + " / " + FunctionalNoNoPanelForm.FormatBytes(system.TotalSize);
            }
            catch
            {
                return "不可用";
            }
        }
    }

    internal sealed class SystemTarget
    {
        public string Kind;
        public string Name;
        public int ProcessId;
        public string Detail;

        public SystemTarget(string kind, string name, int processId, string detail)
        {
            Kind = kind;
            Name = name;
            ProcessId = processId;
            Detail = detail;
        }
    }

    internal static class SystemTargetProvider
    {
        public static List<SystemTarget> Query(string query)
        {
            List<SystemTarget> rows = new List<SystemTarget>();
            string normalized = (query ?? "").Trim();
            if (String.IsNullOrWhiteSpace(normalized))
            {
                return rows;
            }

            AddProcessRows(rows, normalized);
            AddPortRows(rows, normalized);
            return rows;
        }

        private static void AddProcessRows(List<SystemTarget> rows, string query)
        {
            Process[] processes = Process.GetProcesses();
            Array.Sort(processes, delegate(Process a, Process b) { return String.Compare(SafeProcessName(a), SafeProcessName(b), StringComparison.CurrentCultureIgnoreCase); });
            for (int i = 0; i < processes.Length; i++)
            {
                using (Process process = processes[i])
                {
                    string name = SafeProcessName(process);
                    string pid = process.Id.ToString(CultureInfo.InvariantCulture);
                    if (!Matches(query, name, pid, ""))
                    {
                        continue;
                    }

                    string detail = "";
                    try
                    {
                        detail = "内存 " + FormatBytes(process.WorkingSet64);
                    }
                    catch
                    {
                    }

                    rows.Add(new SystemTarget("进程", name, process.Id, detail));
                    if (rows.Count > 260)
                    {
                        return;
                    }
                }
            }
        }

        private static void AddPortRows(List<SystemTarget> rows, string query)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo("netstat.exe", "-ano");
                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardOutput = true;
                startInfo.CreateNoWindow = true;
                using (Process process = Process.Start(startInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(2500);
                    string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (!(line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase) || line.StartsWith("UDP", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 4)
                        {
                            continue;
                        }

                        bool tcp = parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase);
                        string local = parts[1];
                        string state = tcp && parts.Length >= 5 ? parts[3] : "UDP";
                        string pidText = tcp && parts.Length >= 5 ? parts[4] : parts[3];
                        int pid;
                        if (!Int32.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid))
                        {
                            continue;
                        }

                        string port = ExtractPort(local);
                        string processName = ProcessName(pid);
                        if (!Matches(query, port, pidText, processName + " " + local + " " + state))
                        {
                            continue;
                        }

                        rows.Add(new SystemTarget("端口", port, pid, processName + " · " + local + " · " + state));
                    }
                }
            }
            catch
            {
            }
        }

        private static bool Matches(string query, string a, string b, string c)
        {
            if (String.IsNullOrWhiteSpace(query))
            {
                return false;
            }

            return Contains(a, query) || Contains(b, query) || Contains(c, query);
        }

        private static bool Contains(string value, string query)
        {
            return value != null && value.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private static string SafeProcessName(Process process)
        {
            try
            {
                return process.ProcessName;
            }
            catch
            {
                return "unknown";
            }
        }

        private static string ProcessName(int pid)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    return process.ProcessName;
                }
            }
            catch
            {
                return "PID " + pid.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string ExtractPort(string endpoint)
        {
            int index = endpoint.LastIndexOf(':');
            if (index >= 0 && index < endpoint.Length - 1)
            {
                return endpoint.Substring(index + 1).Trim(']');
            }

            return endpoint;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            }

            double value = bytes;
            string[] units = new string[] { "B", "KB", "MB", "GB", "TB" };
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return value.ToString("0.##", CultureInfo.CurrentCulture) + " " + units[unit];
        }
    }

    internal sealed class ClipboardHistoryItem
    {
        public string Kind;
        public string Text;
        public Bitmap Image;
        public string ImagePath;
        public string OcrText;
        public long Length;
        public DateTime CreatedAt;
        public string Signature;
    }

    internal static class ClipboardSessionHistory
    {
        private static readonly object Gate = new object();
        private static readonly List<ClipboardHistoryItem> TextItems = new List<ClipboardHistoryItem>();
        private static readonly List<ClipboardHistoryItem> ImageItems = new List<ClipboardHistoryItem>();
        private static readonly string ClipboardRoot = Path.Combine(PanelStorage.Root, "clipboard");
        private static readonly string ImagesRoot = Path.Combine(ClipboardRoot, "images");
        private static readonly string HistoryFile = Path.Combine(ClipboardRoot, "history.tsv");
        private static bool loaded;
        private static string bootKey;
        private static string lastClipboardSignature;
        private static int captureInProgress;

        public static event EventHandler Changed;

        public static void EnsureLoaded()
        {
            lock (Gate)
            {
                if (loaded)
                {
                    return;
                }

                Directory.CreateDirectory(ClipboardRoot);
                bootKey = GetCurrentBootKey();
                List<string[]> rows = File.Exists(HistoryFile) ? PanelStorage.LoadRows(HistoryFile) : new List<string[]>();
                string storedBootKey = "";
                if (rows.Count > 0 && rows[0].Length >= 2 && String.Equals(rows[0][0], "boot", StringComparison.OrdinalIgnoreCase))
                {
                    storedBootKey = rows[0][1];
                }

                if (!String.Equals(storedBootKey, bootKey, StringComparison.Ordinal))
                {
                    ClearFilesNoLock();
                    SaveNoLock();
                    loaded = true;
                    return;
                }

                TextItems.Clear();
                ImageItems.Clear();
                for (int i = 1; i < rows.Count; i++)
                {
                    string[] row = rows[i];
                    if (row.Length < 5)
                    {
                        continue;
                    }

                    string kind = row[0];
                    if (String.Equals(kind, "text", StringComparison.OrdinalIgnoreCase))
                    {
                        ClipboardHistoryItem item = new ClipboardHistoryItem();
                        item.Kind = "文本";
                        item.CreatedAt = PanelStorage.ParseDate(row[1]);
                        item.Length = ParseLong(row[2]);
                        item.Text = row[3];
                        item.Signature = row.Length >= 5 ? row[4] : "T:" + HashText(item.Text);
                        TextItems.Add(item);
                    }
                    else if (String.Equals(kind, "image", StringComparison.OrdinalIgnoreCase))
                    {
                        ClipboardHistoryItem item = new ClipboardHistoryItem();
                        item.Kind = "图片";
                        item.CreatedAt = PanelStorage.ParseDate(row[1]);
                        item.Length = ParseLong(row[2]);
                        item.ImagePath = Path.Combine(ImagesRoot, row[3]);
                        item.OcrText = row.Length >= 5 ? row[4] : "";
                        item.Signature = row.Length >= 6 ? row[5] : "";
                        if (File.Exists(item.ImagePath))
                        {
                            ImageItems.Add(item);
                        }
                    }
                }

                SortNoLock();
                lastClipboardSignature = GetNewestSignatureNoLock();
                loaded = true;
            }
        }

        private static bool CaptureCurrentLegacy()
        {
            EnsureLoaded();
            try
            {
                if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
                {
                    string text = Clipboard.GetText(TextDataFormat.UnicodeText);
                    string signature = "T:" + HashText(text);
                    lock (Gate)
                    {
                        if (String.Equals(signature, lastClipboardSignature, StringComparison.Ordinal))
                        {
                            return false;
                        }

                        ClipboardHistoryItem item = new ClipboardHistoryItem();
                        item.Kind = "文本";
                        item.Text = text;
                        item.Length = text == null ? 0 : text.Length;
                        item.CreatedAt = DateTime.Now;
                        item.Signature = signature;
                        TextItems.Insert(0, item);
                        lastClipboardSignature = signature;
                        SaveNoLock();
                    }

                    RaiseChanged();
                    return true;
                }

                if (Clipboard.ContainsImage())
                {
                    using (Image image = Clipboard.GetImage())
                    {
                        if (image == null)
                        {
                            return false;
                        }

                        Bitmap copy = new Bitmap(image);
                        byte[] data = ImageToPngBytes(copy);
                        string signature = "I:" + HashBytes(data);
                        lock (Gate)
                        {
                            if (String.Equals(signature, lastClipboardSignature, StringComparison.Ordinal))
                            {
                                copy.Dispose();
                                return false;
                            }

                            Directory.CreateDirectory(ImagesRoot);
                            ClipboardHistoryItem item = new ClipboardHistoryItem();
                            item.Kind = "图片";
                            item.Image = copy;
                            item.Length = data.Length;
                            item.CreatedAt = DateTime.Now;
                            item.Signature = signature;
                            item.ImagePath = Path.Combine(ImagesRoot, "clip-" + item.CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".png");
                            File.WriteAllBytes(item.ImagePath, data);
                            item.OcrText = ImageTextRecognizer.TryRecognize(copy);
                            ImageItems.Insert(0, item);
                            lastClipboardSignature = signature;
                            SaveNoLock();
                        }

                        RaiseChanged();
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        public static Task<bool> CaptureCurrentAsync()
        {
            EnsureLoaded();
            if (System.Threading.Interlocked.CompareExchange(ref captureInProgress, 1, 0) != 0)
            {
                return Task.FromResult(false);
            }

            try
            {
                if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
                {
                    string text = Clipboard.GetText(TextDataFormat.UnicodeText);
                    DateTime capturedAt = DateTime.Now;
                    return Task.Run<bool>(delegate
                    {
                        try
                        {
                            return StoreText(text, capturedAt);
                        }
                        finally
                        {
                            System.Threading.Interlocked.Exchange(ref captureInProgress, 0);
                        }
                    });
                }

                if (Clipboard.ContainsImage())
                {
                    using (Image image = Clipboard.GetImage())
                    {
                        if (image != null)
                        {
                            Bitmap copy = new Bitmap(image);
                            DateTime capturedAt = DateTime.Now;
                            return Task.Run<bool>(delegate
                            {
                                try
                                {
                                    return StoreImage(copy, capturedAt);
                                }
                                finally
                                {
                                    System.Threading.Interlocked.Exchange(ref captureInProgress, 0);
                                }
                            });
                        }
                    }
                }
            }
            catch
            {
            }

            System.Threading.Interlocked.Exchange(ref captureInProgress, 0);
            return Task.FromResult(false);
        }

        private static bool StoreText(string text, DateTime capturedAt)
        {
            try
            {
                string signature = "T:" + HashText(text);
                lock (Gate)
                {
                    if (String.Equals(signature, lastClipboardSignature, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    ClipboardHistoryItem item = new ClipboardHistoryItem();
                    item.Kind = "文本";
                    item.Text = text;
                    item.Length = text == null ? 0 : text.Length;
                    item.CreatedAt = capturedAt;
                    item.Signature = signature;
                    TextItems.Insert(0, item);
                    lastClipboardSignature = signature;
                    SaveNoLock();
                }

                RaiseChanged();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool StoreImage(Bitmap copy, DateTime capturedAt)
        {
            string imagePath = null;
            bool retained = false;
            try
            {
                byte[] data = ImageToPngBytes(copy);
                string signature = "I:" + HashBytes(data);
                lock (Gate)
                {
                    if (String.Equals(signature, lastClipboardSignature, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                Directory.CreateDirectory(ImagesRoot);
                imagePath = Path.Combine(ImagesRoot, "clip-" + capturedAt.Ticks.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".png");
                File.WriteAllBytes(imagePath, data);
                string ocrText = ImageTextRecognizer.TryRecognizeFile(imagePath);

                lock (Gate)
                {
                    if (String.Equals(signature, lastClipboardSignature, StringComparison.Ordinal))
                    {
                        TryDeleteClipboardFile(imagePath);
                        return false;
                    }

                    ClipboardHistoryItem item = new ClipboardHistoryItem();
                    item.Kind = "图片";
                    item.Image = copy;
                    item.Length = data.Length;
                    item.CreatedAt = capturedAt;
                    item.Signature = signature;
                    item.ImagePath = imagePath;
                    item.OcrText = ocrText;
                    ImageItems.Insert(0, item);
                    lastClipboardSignature = signature;
                    retained = true;
                    SaveNoLock();
                }

                RaiseChanged();
                return true;
            }
            catch
            {
                TryDeleteClipboardFile(imagePath);
                return false;
            }
            finally
            {
                if (!retained)
                {
                    copy.Dispose();
                }
            }
        }

        public static Bitmap EnsureImageLoaded(ClipboardHistoryItem item)
        {
            if (item == null)
            {
                return null;
            }

            lock (Gate)
            {
                if (item.Image == null && !String.IsNullOrWhiteSpace(item.ImagePath) && File.Exists(item.ImagePath))
                {
                    try
                    {
                        item.Image = LoadBitmapCopy(item.ImagePath);
                    }
                    catch
                    {
                    }
                }

                return item.Image;
            }
        }

        private static void TryDeleteClipboardFile(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        public static List<ClipboardHistoryItem> GetTextItems()
        {
            EnsureLoaded();
            lock (Gate)
            {
                return new List<ClipboardHistoryItem>(TextItems);
            }
        }

        public static List<ClipboardHistoryItem> GetImageItems()
        {
            EnsureLoaded();
            lock (Gate)
            {
                return new List<ClipboardHistoryItem>(ImageItems);
            }
        }

        public static void ClearCurrentSession()
        {
            lock (Gate)
            {
                ClearItemsNoLock();
                ClearFilesNoLock();
                lastClipboardSignature = null;
                loaded = false;
            }

            RaiseChanged();
        }

        private static void SaveNoLock()
        {
            Directory.CreateDirectory(ClipboardRoot);
            List<string[]> rows = new List<string[]>();
            rows.Add(new string[] { "boot", bootKey ?? GetCurrentBootKey() });

            List<ClipboardHistoryItem> all = new List<ClipboardHistoryItem>();
            all.AddRange(TextItems);
            all.AddRange(ImageItems);
            all.Sort(CompareNewestFirst);
            for (int i = 0; i < all.Count; i++)
            {
                ClipboardHistoryItem item = all[i];
                if (item.Kind == "文本")
                {
                    rows.Add(new string[]
                    {
                        "text",
                        PanelStorage.FormatDate(item.CreatedAt),
                        item.Length.ToString(CultureInfo.InvariantCulture),
                        item.Text ?? "",
                        item.Signature ?? "T:" + HashText(item.Text)
                    });
                }
                else if (item.Kind == "图片")
                {
                    rows.Add(new string[]
                    {
                        "image",
                        PanelStorage.FormatDate(item.CreatedAt),
                        item.Length.ToString(CultureInfo.InvariantCulture),
                        item.ImagePath == null ? "" : Path.GetFileName(item.ImagePath),
                        item.OcrText ?? "",
                        item.Signature ?? ""
                    });
                }
            }

            PanelStorage.SaveRows(HistoryFile, rows);
        }

        private static void SortNoLock()
        {
            TextItems.Sort(CompareNewestFirst);
            ImageItems.Sort(CompareNewestFirst);
        }

        private static int CompareNewestFirst(ClipboardHistoryItem left, ClipboardHistoryItem right)
        {
            return right.CreatedAt.CompareTo(left.CreatedAt);
        }

        private static string GetNewestSignatureNoLock()
        {
            ClipboardHistoryItem newest = null;
            for (int i = 0; i < TextItems.Count; i++)
            {
                if (newest == null || TextItems[i].CreatedAt > newest.CreatedAt)
                {
                    newest = TextItems[i];
                }
            }

            for (int i = 0; i < ImageItems.Count; i++)
            {
                if (newest == null || ImageItems[i].CreatedAt > newest.CreatedAt)
                {
                    newest = ImageItems[i];
                }
            }

            return newest == null ? null : newest.Signature;
        }

        private static void ClearItemsNoLock()
        {
            for (int i = 0; i < ImageItems.Count; i++)
            {
                if (ImageItems[i].Image != null)
                {
                    ImageItems[i].Image.Dispose();
                }
            }

            TextItems.Clear();
            ImageItems.Clear();
        }

        private static void ClearFilesNoLock()
        {
            try
            {
                if (Directory.Exists(ImagesRoot))
                {
                    Directory.Delete(ImagesRoot, true);
                }

                if (File.Exists(HistoryFile))
                {
                    File.Delete(HistoryFile);
                }
            }
            catch
            {
            }
        }

        private static string GetCurrentBootKey()
        {
            try
            {
                DateTime bootTime = DateTime.Now - TimeSpan.FromMilliseconds(NativeMethods.GetTickCount64());
                long ticks = bootTime.ToUniversalTime().Ticks;
                ticks -= ticks % TimeSpan.TicksPerMinute;
                return ticks.ToString(CultureInfo.InvariantCulture);
            }
            catch
            {
                return DateTime.Today.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static long ParseLong(string value)
        {
            long parsed;
            if (Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }

            return 0;
        }

        private static Bitmap LoadBitmapCopy(string path)
        {
            using (Image image = Image.FromFile(path))
            {
                return new Bitmap(image);
            }
        }

        private static byte[] ImageToPngBytes(Image image)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                image.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        private static string HashText(string text)
        {
            return HashBytes(Encoding.UTF8.GetBytes(text ?? ""));
        }

        private static string HashBytes(byte[] data)
        {
            using (SHA1 sha1 = SHA1.Create())
            {
                return Convert.ToBase64String(sha1.ComputeHash(data));
            }
        }

        private static void RaiseChanged()
        {
            EventHandler handler = Changed;
            if (handler != null)
            {
                handler(null, EventArgs.Empty);
            }
        }
    }

    internal static class ImageTextRecognizer
    {
        public static string TryRecognize(Image image)
        {
            string imagePath = null;
            string scriptPath = null;
            try
            {
                imagePath = Path.Combine(Path.GetTempPath(), "nono-ocr-" + Guid.NewGuid().ToString("N") + ".png");
                scriptPath = Path.Combine(Path.GetTempPath(), "nono-ocr-" + Guid.NewGuid().ToString("N") + ".ps1");
                image.Save(imagePath, ImageFormat.Png);
                File.WriteAllText(scriptPath, OcrPowerShellScript(), Encoding.UTF8);

                ProcessStartInfo startInfo = new ProcessStartInfo("powershell.exe");
                startInfo.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" \"" + imagePath + "\"";
                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.CreateNoWindow = true;
                using (Process process = Process.Start(startInfo))
                {
                    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> errorTask = process.StandardError.ReadToEndAsync();
                    try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
                    catch { }
                    if (!process.WaitForExit(8000))
                    {
                        try { process.Kill(); }
                        catch { }
                        return "OCR 超时，未能完成识别。";
                    }

                    string output = outputTask.GetAwaiter().GetResult().Trim();
                    if (output.Length > 0)
                    {
                        return output;
                    }

                    string error = errorTask.GetAwaiter().GetResult().Trim();
                    if (error.Length > 0)
                    {
                        return "OCR 不可用: " + error.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    }
                }
            }
            catch (Exception ex)
            {
                return "OCR 不可用: " + ex.Message;
            }
            finally
            {
                TryDelete(imagePath);
                TryDelete(scriptPath);
            }

            return "未识别到文本。";
        }

        public static string TryRecognizeFile(string imagePath)
        {
            string scriptPath = null;
            try
            {
                if (String.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                {
                    return "OCR image is unavailable.";
                }

                scriptPath = Path.Combine(Path.GetTempPath(), "nono-ocr-" + Guid.NewGuid().ToString("N") + ".ps1");
                File.WriteAllText(scriptPath, OcrPowerShellScript(), Encoding.UTF8);

                ProcessStartInfo startInfo = new ProcessStartInfo("powershell.exe");
                startInfo.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" \"" + imagePath + "\"";
                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.CreateNoWindow = true;
                using (Process process = Process.Start(startInfo))
                {
                    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> errorTask = process.StandardError.ReadToEndAsync();
                    try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
                    catch { }
                    if (!process.WaitForExit(8000))
                    {
                        try { process.Kill(); }
                        catch { }
                        return "OCR timed out.";
                    }

                    string output = outputTask.GetAwaiter().GetResult().Trim();
                    if (output.Length > 0)
                    {
                        return output;
                    }

                    string error = errorTask.GetAwaiter().GetResult().Trim();
                    if (error.Length > 0)
                    {
                        string[] lines = error.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        return lines.Length == 0 ? "OCR is unavailable." : "OCR is unavailable: " + lines[0];
                    }
                }
            }
            catch (Exception ex)
            {
                return "OCR is unavailable: " + ex.Message;
            }
            finally
            {
                TryDelete(scriptPath);
            }

            return "No text was recognized.";
        }

        private static string OcrPowerShellScript()
        {
            return
@"param([string]$Path)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
[Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime] | Out-Null
[Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Storage.Streams, ContentType=WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType=WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType=WindowsRuntime] | Out-Null
[Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime] | Out-Null
[Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType=WindowsRuntime] | Out-Null
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetGenericArguments().Length -eq 1 -and $_.GetParameters().Length -eq 1 } | Select-Object -First 1
function Await($op, $type) {
  $task = $asTask.MakeGenericMethod($type).Invoke($null, @($op))
  $task.Wait()
  $task.Result
}
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Path)) ([Windows.Storage.StorageFile])
$stream = Await ($file.OpenReadAsync()) ([Windows.Storage.Streams.IRandomAccessStreamWithContentType])
$decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
if ($engine -eq $null) { exit 0 }
$result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
$result.Text";
        }

        private static void TryDelete(string path)
        {
            if (String.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }

    internal sealed class NoteNodeInfo
    {
        public readonly bool IsDirectory;
        public readonly string Path;

        public NoteNodeInfo(bool isDirectory, string path)
        {
            IsDirectory = isDirectory;
            Path = path;
        }
    }

    internal static class NotesStore
    {
        private const string RootEntryKind = "R";
        private const string DirectoryEntryKind = "D";
        private const string NoteEntryKind = "F";
        private const string WelcomeContent = "这里可以记录临时想法、任务和代码上下文。";

        public static string Root
        {
            get { return NoteSettingsStore.LoadRoot(); }
        }

        public static void EnsureSeed()
        {
            string root = NormalizeDirectoryPath(Root);
            Directory.CreateDirectory(root);

            List<string[]> rows = PanelStorage.LoadRows(PanelStorage.NotesIndexFile);
            bool changed = EnsureRootInitialized(rows, root);
            if (!HasManagedContent(rows, root))
            {
                string inbox = UniqueDirectoryPath(root, "默认");
                Directory.CreateDirectory(inbox);
                changed |= AddDirectoryAndParents(rows, root, inbox);

                string welcome = UniqueNotePath(inbox, "欢迎");
                File.WriteAllText(welcome, WelcomeContent, Encoding.UTF8);
                changed |= AddEntry(rows, root, NoteEntryKind, GetRelativePath(root, welcome));
            }

            if (changed)
            {
                PanelStorage.SaveRows(PanelStorage.NotesIndexFile, rows);
            }
        }

        public static DirectoryInfo[] GetManagedDirectories(string parent)
        {
            string root = NormalizeDirectoryPath(Root);
            string normalizedParent = NormalizeDirectoryPath(parent);
            List<string[]> rows = PanelStorage.LoadRows(PanelStorage.NotesIndexFile);
            List<DirectoryInfo> directories = new List<DirectoryInfo>();
            for (int i = 0; i < rows.Count; i++)
            {
                if (!IsEntry(rows[i], root, DirectoryEntryKind))
                {
                    continue;
                }

                string path = ResolveEntryPath(root, rows[i][2]);
                if (String.IsNullOrEmpty(path) || !Directory.Exists(path))
                {
                    continue;
                }

                string candidateParent = Path.GetDirectoryName(path);
                if (PathsEqual(candidateParent, normalizedParent))
                {
                    directories.Add(new DirectoryInfo(path));
                }
            }

            return directories.ToArray();
        }

        public static FileInfo[] GetManagedNotes(string directory)
        {
            string root = NormalizeDirectoryPath(Root);
            string normalizedDirectory = NormalizeDirectoryPath(directory);
            List<string[]> rows = PanelStorage.LoadRows(PanelStorage.NotesIndexFile);
            List<FileInfo> notes = new List<FileInfo>();
            for (int i = 0; i < rows.Count; i++)
            {
                if (!IsEntry(rows[i], root, NoteEntryKind))
                {
                    continue;
                }

                string path = ResolveEntryPath(root, rows[i][2]);
                if (String.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    continue;
                }

                if (PathsEqual(Path.GetDirectoryName(path), normalizedDirectory))
                {
                    notes.Add(new FileInfo(path));
                }
            }

            return notes.ToArray();
        }

        public static string UniqueDirectoryPath(string parent, string name)
        {
            string safe = PanelStorage.SafeFileName(name);
            if (String.IsNullOrWhiteSpace(safe))
            {
                safe = "新建目录";
            }

            string path = Path.Combine(parent, safe);
            int index = 2;
            while (Directory.Exists(path) || File.Exists(path))
            {
                path = Path.Combine(parent, safe + "-" + index.ToString(CultureInfo.InvariantCulture));
                index++;
            }

            return path;
        }

        public static string UniqueNotePath(string directory, string title)
        {
            string safe = PanelStorage.SafeFileName(title);
            string path = Path.Combine(directory, safe + ".txt");
            int index = 2;
            while (File.Exists(path) || Directory.Exists(path))
            {
                path = Path.Combine(directory, safe + "-" + index.ToString(CultureInfo.InvariantCulture) + ".txt");
                index++;
            }

            return path;
        }

        public static void RegisterDirectory(string path)
        {
            string root = NormalizeDirectoryPath(Root);
            List<string[]> rows = PanelStorage.LoadRows(PanelStorage.NotesIndexFile);
            bool changed = EnsureRootInitialized(rows, root);
            changed |= AddDirectoryAndParents(rows, root, path);
            if (changed)
            {
                PanelStorage.SaveRows(PanelStorage.NotesIndexFile, rows);
            }
        }

        public static void RegisterNote(string path)
        {
            string root = NormalizeDirectoryPath(Root);
            List<string[]> rows = PanelStorage.LoadRows(PanelStorage.NotesIndexFile);
            bool changed = EnsureRootInitialized(rows, root);
            changed |= AddDirectoryAndParents(rows, root, Path.GetDirectoryName(path));
            changed |= AddEntry(rows, root, NoteEntryKind, GetRelativePath(root, path));
            if (changed)
            {
                PanelStorage.SaveRows(PanelStorage.NotesIndexFile, rows);
            }
        }

        public static void MoveNote(string source, string target)
        {
            string root = NormalizeDirectoryPath(Root);
            List<string[]> rows = PanelStorage.LoadRows(PanelStorage.NotesIndexFile);
            bool changed = EnsureRootInitialized(rows, root);
            changed |= RemoveEntry(rows, root, NoteEntryKind, GetRelativePath(root, source));
            changed |= AddDirectoryAndParents(rows, root, Path.GetDirectoryName(target));
            changed |= AddEntry(rows, root, NoteEntryKind, GetRelativePath(root, target));
            if (changed)
            {
                PanelStorage.SaveRows(PanelStorage.NotesIndexFile, rows);
            }
        }

        public static void RemoveNote(string path)
        {
            string root = NormalizeDirectoryPath(Root);
            List<string[]> rows = PanelStorage.LoadRows(PanelStorage.NotesIndexFile);
            if (RemoveEntry(rows, root, NoteEntryKind, GetRelativePath(root, path)))
            {
                PanelStorage.SaveRows(PanelStorage.NotesIndexFile, rows);
            }
        }

        public static void DeleteDirectory(string path)
        {
            string root = NormalizeDirectoryPath(Root);
            string target = NormalizeDirectoryPath(path);
            if (PathsEqual(root, target) || !IsInsideRoot(root, target))
            {
                throw new InvalidOperationException("不能删除便签根目录。");
            }

            List<string[]> rows = PanelStorage.LoadRows(PanelStorage.NotesIndexFile);
            List<string> files = new List<string>();
            List<string> directories = new List<string>();
            for (int i = 0; i < rows.Count; i++)
            {
                if (!IsEntryForRoot(rows[i], root) || rows[i].Length < 3)
                {
                    continue;
                }

                string entryPath = ResolveEntryPath(root, rows[i][2]);
                if (String.IsNullOrEmpty(entryPath) || !IsSameOrDescendant(target, entryPath))
                {
                    continue;
                }

                if (String.Equals(rows[i][1], NoteEntryKind, StringComparison.Ordinal))
                {
                    files.Add(entryPath);
                }
                else if (String.Equals(rows[i][1], DirectoryEntryKind, StringComparison.Ordinal))
                {
                    directories.Add(entryPath);
                }
            }

            for (int i = 0; i < files.Count; i++)
            {
                if (File.Exists(files[i]))
                {
                    File.Delete(files[i]);
                }
            }

            directories.Sort(delegate(string a, string b) { return b.Length.CompareTo(a.Length); });
            for (int i = 0; i < directories.Count; i++)
            {
                if (Directory.Exists(directories[i]) && Directory.GetFileSystemEntries(directories[i]).Length == 0)
                {
                    Directory.Delete(directories[i], false);
                }
            }

            if (RemoveSubtreeEntries(rows, root, target))
            {
                PanelStorage.SaveRows(PanelStorage.NotesIndexFile, rows);
            }
        }

        private static bool EnsureRootInitialized(List<string[]> rows, string root)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (IsEntry(rows[i], root, RootEntryKind))
                {
                    return false;
                }
            }

            bool changed = AddEntry(rows, root, RootEntryKind, "");
            if (PathsEqual(root, PanelStorage.NotesRoot) && Directory.Exists(root))
            {
                changed |= ImportLegacyDefaultDirectory(rows, root, root);
            }
            else
            {
                changed |= ImportRecognizedLegacyInbox(rows, root);
            }

            return changed;
        }

        private static bool ImportRecognizedLegacyInbox(List<string[]> rows, string root)
        {
            string inbox = Path.Combine(root, "默认");
            string welcome = Path.Combine(inbox, "欢迎.txt");
            if (!Directory.Exists(inbox) || !File.Exists(welcome))
            {
                return false;
            }

            try
            {
                if (!String.Equals(File.ReadAllText(welcome, Encoding.UTF8), WelcomeContent, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            bool changed = AddEntry(rows, root, DirectoryEntryKind, GetRelativePath(root, inbox));
            changed |= ImportLegacyDefaultDirectory(rows, root, inbox);
            return changed;
        }

        private static bool ImportLegacyDefaultDirectory(List<string[]> rows, string root, string directory)
        {
            bool changed = false;
            DirectoryInfo[] directories = new DirectoryInfo(directory).GetDirectories();
            for (int i = 0; i < directories.Length; i++)
            {
                changed |= AddEntry(rows, root, DirectoryEntryKind, GetRelativePath(root, directories[i].FullName));
                if ((directories[i].Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    changed |= ImportLegacyDefaultDirectory(rows, root, directories[i].FullName);
                }
            }

            FileInfo[] notes = new DirectoryInfo(directory).GetFiles("*.txt");
            for (int i = 0; i < notes.Length; i++)
            {
                changed |= AddEntry(rows, root, NoteEntryKind, GetRelativePath(root, notes[i].FullName));
            }

            return changed;
        }

        private static bool HasManagedContent(List<string[]> rows, string root)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (IsEntry(rows[i], root, DirectoryEntryKind) || IsEntry(rows[i], root, NoteEntryKind))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool AddDirectoryAndParents(List<string[]> rows, string root, string directory)
        {
            string current = NormalizeDirectoryPath(directory);
            if (!IsInsideOrEqualRoot(root, current))
            {
                throw new InvalidOperationException("便签目录必须位于当前保存位置内。");
            }

            List<string> pending = new List<string>();
            while (!PathsEqual(current, root))
            {
                pending.Add(current);
                current = Path.GetDirectoryName(current);
                if (String.IsNullOrEmpty(current))
                {
                    throw new InvalidOperationException("无法确定便签目录的父目录。");
                }
            }

            bool changed = false;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                changed |= AddEntry(rows, root, DirectoryEntryKind, GetRelativePath(root, pending[i]));
            }

            return changed;
        }

        private static bool AddEntry(List<string[]> rows, string root, string kind, string relativePath)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (IsEntry(rows[i], root, kind) &&
                    rows[i].Length >= 3 &&
                    String.Equals(rows[i][2], relativePath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            rows.Add(new string[] { root, kind, relativePath ?? "" });
            return true;
        }

        private static bool RemoveEntry(List<string[]> rows, string root, string kind, string relativePath)
        {
            bool changed = false;
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                if (IsEntry(rows[i], root, kind) &&
                    rows[i].Length >= 3 &&
                    String.Equals(rows[i][2], relativePath, StringComparison.OrdinalIgnoreCase))
                {
                    rows.RemoveAt(i);
                    changed = true;
                }
            }

            return changed;
        }

        private static bool RemoveSubtreeEntries(List<string[]> rows, string root, string target)
        {
            bool changed = false;
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                if (!IsEntryForRoot(rows[i], root) || rows[i].Length < 3 ||
                    (!String.Equals(rows[i][1], DirectoryEntryKind, StringComparison.Ordinal) &&
                     !String.Equals(rows[i][1], NoteEntryKind, StringComparison.Ordinal)))
                {
                    continue;
                }

                string entryPath = ResolveEntryPath(root, rows[i][2]);
                if (!String.IsNullOrEmpty(entryPath) && IsSameOrDescendant(target, entryPath))
                {
                    rows.RemoveAt(i);
                    changed = true;
                }
            }

            return changed;
        }

        private static bool IsEntry(string[] row, string root, string kind)
        {
            return row != null && row.Length >= 2 &&
                PathsEqual(row[0], root) &&
                String.Equals(row[1], kind, StringComparison.Ordinal);
        }

        private static bool IsEntryForRoot(string[] row, string root)
        {
            return row != null && row.Length >= 2 && PathsEqual(row[0], root);
        }

        private static string ResolveEntryPath(string root, string relativePath)
        {
            try
            {
                if (String.IsNullOrEmpty(relativePath))
                {
                    return root;
                }

                string path = Path.GetFullPath(Path.Combine(root, relativePath));
                return IsInsideRoot(root, path) ? path : "";
            }
            catch
            {
                return "";
            }
        }

        private static string GetRelativePath(string root, string path)
        {
            string normalizedRoot = NormalizeDirectoryPath(root);
            string fullPath = Path.GetFullPath(path);
            if (PathsEqual(normalizedRoot, fullPath))
            {
                return "";
            }

            string prefix = EnsureTrailingSeparator(normalizedRoot);
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("便签路径必须位于当前保存位置内。");
            }

            return fullPath.Substring(prefix.Length);
        }

        private static bool IsInsideOrEqualRoot(string root, string path)
        {
            return PathsEqual(root, path) || IsInsideRoot(root, path);
        }

        private static bool IsInsideRoot(string root, string path)
        {
            if (String.IsNullOrEmpty(root) || String.IsNullOrEmpty(path))
            {
                return false;
            }

            return Path.GetFullPath(path).StartsWith(EnsureTrailingSeparator(NormalizeDirectoryPath(root)), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSameOrDescendant(string parent, string path)
        {
            return PathsEqual(parent, path) || IsInsideRoot(parent, path);
        }

        private static bool PathsEqual(string left, string right)
        {
            if (String.IsNullOrEmpty(left) || String.IsNullOrEmpty(right))
            {
                return false;
            }

            try
            {
                return String.Equals(NormalizeDirectoryPath(left), NormalizeDirectoryPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeDirectoryPath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string pathRoot = Path.GetPathRoot(fullPath);
            if (!String.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase))
            {
                fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return fullPath;
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                return path;
            }

            return path + Path.DirectorySeparatorChar;
        }
    }

    internal static class NoteSettingsStore
    {
        private const string PreferenceKey = "notes-root";

        public static string LoadRoot()
        {
            string normalized;
            string message;
            if (TryNormalizeRoot(PanelStorage.LoadPreference(PreferenceKey), out normalized, out message))
            {
                return normalized;
            }

            return PanelStorage.NotesRoot;
        }

        public static void SaveRoot(string path)
        {
            string normalized;
            string message;
            if (!TryNormalizeRoot(path, out normalized, out message))
            {
                throw new InvalidOperationException(message);
            }

            Directory.CreateDirectory(normalized);
            PanelStorage.SavePreference(PreferenceKey, normalized);
        }

        public static void ResetRoot()
        {
            PanelStorage.SavePreference(PreferenceKey, "");
        }

        public static bool TryNormalizeRoot(string path, out string normalized, out string message)
        {
            normalized = "";
            message = "";
            if (String.IsNullOrWhiteSpace(path))
            {
                message = "便签保存位置不能为空。";
                return false;
            }

            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(path.Trim());
                normalized = Path.GetFullPath(expanded);
                string fileName = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!String.IsNullOrEmpty(fileName) && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    message = "便签保存位置包含无效字符。";
                    normalized = "";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                message = "便签保存位置无效：" + ex.Message;
                normalized = "";
                return false;
            }
        }
    }

    internal sealed class PromptDialog : Form
    {
        private readonly TextBox valueBox;

        private PromptDialog(string title, string label)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(360, 118);
            Font = new Font("Microsoft YaHei UI", 9F);

            Label labelControl = new Label();
            labelControl.Text = label;
            labelControl.SetBounds(14, 14, 330, 22);
            Controls.Add(labelControl);

            valueBox = new TextBox();
            valueBox.SetBounds(14, 40, 330, 24);
            Controls.Add(valueBox);

            Button ok = new Button();
            ok.Text = "确定";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(178, 78, 78, 28);
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(266, 78, 78, 28);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public static string Show(IWin32Window owner, string title, string label)
        {
            using (PromptDialog dialog = new PromptDialog(title, label))
            {
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.valueBox.Text : null;
            }
        }
    }

    internal sealed class NoteEditorDialog : Form
    {
        private readonly TextBox titleBox;
        private readonly TextBox contentBox;

        public string NoteTitle
        {
            get { return titleBox.Text.Trim(); }
        }

        public string NoteContent
        {
            get { return contentBox.Text; }
        }

        public NoteEditorDialog(string title, string noteTitle, string content)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = true;
            ClientSize = new Size(640, 460);
            MinimumSize = new Size(520, 360);
            Font = new Font("Microsoft YaHei UI", 9F);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(12);
            layout.ColumnCount = 1;
            layout.RowCount = 4;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
            Controls.Add(layout);

            layout.Controls.Add(new Label { Text = "标题", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            titleBox = new TextBox();
            titleBox.Dock = DockStyle.Fill;
            titleBox.Text = noteTitle;
            layout.Controls.Add(titleBox, 0, 1);

            contentBox = new TextBox();
            contentBox.Multiline = true;
            contentBox.ScrollBars = ScrollBars.Both;
            contentBox.AcceptsReturn = true;
            contentBox.AcceptsTab = true;
            contentBox.Dock = DockStyle.Fill;
            contentBox.Text = content;
            layout.Controls.Add(contentBox, 0, 2);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.RightToLeft;
            Button ok = new Button();
            ok.Text = "保存";
            ok.DialogResult = DialogResult.OK;
            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(cancel);
            actions.Controls.Add(ok);
            layout.Controls.Add(actions, 0, 3);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    internal sealed class ScreenColorPickerForm : Form
    {
        public event EventHandler ColorPicked;
        public Color PickedColor;

        public ScreenColorPickerForm()
        {
            Rectangle bounds = Screen.AllScreens[0].Bounds;
            for (int i = 1; i < Screen.AllScreens.Length; i++)
            {
                bounds = Rectangle.Union(bounds, Screen.AllScreens[i].Bounds);
            }

            Bounds = bounds;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Black;
            Opacity = 0.16;
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Font font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold))
            using (SolidBrush brush = new SolidBrush(Color.White))
            {
                e.Graphics.DrawString("在屏幕任意位置单击取色，按 Esc 取消", font, brush, new PointF(32, 32));
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                Close();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Point screenPoint = PointToScreen(e.Location);
            Hide();
            Application.DoEvents();
            System.Threading.Thread.Sleep(80);
            PickedColor = CapturePixel(screenPoint);
            if (ColorPicked != null)
            {
                ColorPicked(this, EventArgs.Empty);
            }

            Close();
        }

        private static Color CapturePixel(Point screenPoint)
        {
            using (Bitmap bitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(screenPoint, Point.Empty, new Size(1, 1));
                return bitmap.GetPixel(0, 0);
            }
        }
    }

    internal sealed class HotkeyDefinition
    {
        public string Action;
        public string Label;
        public string Gesture;
        public string Description;
        public bool Enabled;

        public HotkeyDefinition(string action, string label, string gesture, string description, bool enabled)
        {
            Action = action;
            Label = label;
            Gesture = gesture;
            Description = description;
            Enabled = enabled;
        }
    }

    internal static class PetScaleSettingsStore
    {
        private const float DefaultScale = 1.0f;
        private const float MinScale = 0.5f;
        private const float MaxScale = 2.0f;

        public static float Load()
        {
            try
            {
                List<string[]> rows = PanelStorage.LoadRows(PanelStorage.PreferencesFile);
                for (int i = 0; i < rows.Count; i++)
                {
                    string[] row = rows[i];
                    if (row.Length < 2 || !String.Equals(row[0], "scale", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    float parsed;
                    if (Single.TryParse(row[1], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    {
                        return Normalize(parsed);
                    }
                }
            }
            catch
            {
            }

            return DefaultScale;
        }

        public static void Save(float scale)
        {
            try
            {
                PanelStorage.SavePreference("scale", Normalize(scale).ToString("0.###", CultureInfo.InvariantCulture));
            }
            catch
            {
            }
        }

        private static float Normalize(float value)
        {
            if (Single.IsNaN(value) || Single.IsInfinity(value))
            {
                return DefaultScale;
            }

            if (value < MinScale)
            {
                return MinScale;
            }

            if (value > MaxScale)
            {
                return MaxScale;
            }

            return value;
        }
    }

    internal sealed class CodexActivitySnapshot
    {
        public readonly string StateId;
        public readonly string Label;
        public readonly string Source;
        public readonly string Detail;

        public CodexActivitySnapshot(string stateId, string label, string source, string detail)
        {
            StateId = stateId;
            Label = label;
            Source = source;
            Detail = detail;
        }
    }

    internal sealed class CodexActivityMonitor
    {
        private const int PollIntervalMs = 1500;
        private const int ProcessPathProbeIntervalMs = 30000;
        private const int StatusFileFreshMinutes = 45;
        private readonly List<string> statusFiles;
        private readonly object pollGate;
        private DateTime lastPollAt;
        private DateTime lastProcessPathProbeAt;
        private volatile CodexActivitySnapshot current;

        public CodexActivityMonitor()
        {
            statusFiles = BuildStatusFiles();
            pollGate = new object();
            lastPollAt = DateTime.MinValue;
            lastProcessPathProbeAt = DateTime.MinValue;
            current = new CodexActivitySnapshot("idle", "待机", "initial", "等待检测");
        }

        public CodexActivitySnapshot Current
        {
            get { return current; }
        }

        public CodexActivitySnapshot Poll(DateTime now)
        {
            lock (pollGate)
            {
                if ((now - lastPollAt).TotalMilliseconds < PollIntervalMs)
                {
                    return current;
                }

                lastPollAt = now;
            }

            current = Detect(now);
            return current;
        }

        public bool IsPollDue(DateTime now)
        {
            lock (pollGate)
            {
                return (now - lastPollAt).TotalMilliseconds >= PollIntervalMs;
            }
        }

        public CodexActivitySnapshot ForcePoll()
        {
            lock (pollGate)
            {
                lastPollAt = DateTime.MinValue;
            }

            return Poll(DateTime.UtcNow);
        }

        private CodexActivitySnapshot Detect(DateTime now)
        {
            CodexActivitySnapshot fileSnapshot = DetectFromStatusFiles(now);
            if (fileSnapshot != null)
            {
                return fileSnapshot;
            }

            return DetectFromProcesses(now);
        }

        private CodexActivitySnapshot DetectFromStatusFiles(DateTime now)
        {
            for (int i = 0; i < statusFiles.Count; i++)
            {
                string path = statusFiles[i];
                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    DateTime writtenAt = File.GetLastWriteTimeUtc(path);
                    if ((now - writtenAt).TotalMinutes > StatusFileFreshMinutes)
                    {
                        continue;
                    }

                    string content = File.ReadAllText(path, Encoding.UTF8);
                    string state = NormalizeStatusText(content);
                    if (state.Length == 0)
                    {
                        continue;
                    }

                    return new CodexActivitySnapshot(
                        state,
                        LabelForState(state),
                        "file",
                        Path.GetFileName(path));
                }
                catch
                {
                }
            }

            return null;
        }

        private CodexActivitySnapshot DetectFromProcesses(DateTime now)
        {
            int codexCount = 0;
            int runnerCount = 0;
            bool foregroundCodex = false;
            int foregroundProcessId = NativeMethods.GetForegroundProcessId();
            bool inspectProcessPaths = (now - lastProcessPathProbeAt).TotalMilliseconds >= ProcessPathProbeIntervalMs;
            if (inspectProcessPaths)
            {
                lastProcessPathProbeAt = now;
            }

            try
            {
                Process[] processes = Process.GetProcesses();
                for (int i = 0; i < processes.Length; i++)
                {
                    using (Process process = processes[i])
                    {
                        string name = SafeProcessName(process);
                        if (IsCodexCommandRunner(name))
                        {
                            runnerCount++;
                            continue;
                        }

                        if (!IsCodexProcess(process, name, inspectProcessPaths))
                        {
                            continue;
                        }

                        codexCount++;
                        if (foregroundProcessId > 0 && process.Id == foregroundProcessId)
                        {
                            foregroundCodex = true;
                        }
                    }
                }
            }
            catch
            {
            }

            if (runnerCount > 0)
            {
                return new CodexActivitySnapshot("running", "运行中", "process", "命令执行器 " + runnerCount.ToString(CultureInfo.InvariantCulture));
            }

            if (foregroundCodex)
            {
                return new CodexActivitySnapshot("waiting", "等待", "process", "Codex 在前台");
            }

            if (codexCount > 0)
            {
                return new CodexActivitySnapshot("idle", "待机", "process", "Codex 已打开");
            }

            return new CodexActivitySnapshot("idle", "待机", "process", "未检测到 Codex");
        }

        private static List<string> BuildStatusFiles()
        {
            List<string> paths = new List<string>();
            AddStatusFiles(paths, PanelStorage.Root);

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!String.IsNullOrWhiteSpace(userProfile))
            {
                AddStatusFiles(paths, Path.Combine(userProfile, ".codex"));
            }

            try
            {
                AddStatusFiles(paths, Path.Combine(Directory.GetCurrentDirectory(), ".codex"));
            }
            catch
            {
            }

            return paths;
        }

        private static void AddStatusFiles(List<string> paths, string directory)
        {
            if (String.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            paths.Add(Path.Combine(directory, "codex-status.txt"));
            paths.Add(Path.Combine(directory, "codex-status.json"));
            paths.Add(Path.Combine(directory, "status.txt"));
            paths.Add(Path.Combine(directory, "status.json"));
        }

        private static string NormalizeStatusText(string content)
        {
            if (String.IsNullOrWhiteSpace(content))
            {
                return "";
            }

            string trimmed = content.Trim();
            bool json = trimmed.StartsWith("{", StringComparison.Ordinal);
            if (json)
            {
                string value = FirstJsonFieldValue(trimmed, new string[] { "state", "status", "phase", "activity", "codexState" });
                return NormalizeStateValue(value);
            }

            string[] lines = trimmed.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0)
            {
                string lineState = NormalizeStateValue(lines[0]);
                if (lineState.Length > 0)
                {
                    return lineState;
                }
            }

            return NormalizeStateValue(trimmed);
        }

        private static string FirstJsonFieldValue(string text, string[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                string value = JsonFieldValue(text, keys[i]);
                if (!String.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return "";
        }

        private static string JsonFieldValue(string text, string key)
        {
            string quotedKey = "\"" + key + "\"";
            int keyIndex = text.IndexOf(quotedKey, StringComparison.OrdinalIgnoreCase);
            if (keyIndex < 0)
            {
                return "";
            }

            int colon = text.IndexOf(':', keyIndex + quotedKey.Length);
            if (colon < 0)
            {
                return "";
            }

            int index = colon + 1;
            while (index < text.Length && Char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index >= text.Length)
            {
                return "";
            }

            if (text[index] == '"')
            {
                index++;
                StringBuilder builder = new StringBuilder();
                while (index < text.Length)
                {
                    char c = text[index++];
                    if (c == '"')
                    {
                        break;
                    }

                    if (c == '\\' && index < text.Length)
                    {
                        c = text[index++];
                    }

                    builder.Append(c);
                }

                return builder.ToString();
            }

            int end = index;
            while (end < text.Length && text[end] != ',' && text[end] != '}')
            {
                end++;
            }

            return text.Substring(index, end - index).Trim();
        }

        private static string NormalizeStateValue(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            string lower = value.Trim().ToLowerInvariant();
            if (ContainsAny(lower, new string[] { "failed", "failure", "error", "crash", "cancelled", "canceled", "aborted" }))
            {
                return "failed";
            }

            if (ContainsAny(lower, new string[] { "waiting", "input", "prompt", "confirm", "approval", "blocked", "paused", "needs_user" }))
            {
                return "waiting";
            }

            if (ContainsAny(lower, new string[] { "review", "checking", "inspect", "diff", "verify", "verifying" }))
            {
                return "review";
            }

            if (ContainsAny(lower, new string[] { "running", "busy", "working", "thinking", "executing", "in_progress", "progress", "started", "tool" }))
            {
                return "running";
            }

            if (ContainsAny(lower, new string[] { "idle", "ready", "done", "complete", "completed", "success", "succeeded" }))
            {
                return "idle";
            }

            return "";
        }

        private static bool ContainsAny(string value, string[] needles)
        {
            for (int i = 0; i < needles.Length; i++)
            {
                if (value.IndexOf(needles[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string LabelForState(string state)
        {
            if (String.Equals(state, "running", StringComparison.OrdinalIgnoreCase))
            {
                return "运行中";
            }

            if (String.Equals(state, "waiting", StringComparison.OrdinalIgnoreCase))
            {
                return "等待";
            }

            if (String.Equals(state, "review", StringComparison.OrdinalIgnoreCase))
            {
                return "检查";
            }

            if (String.Equals(state, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return "失败";
            }

            return "待机";
        }

        private static string SafeProcessName(Process process)
        {
            try
            {
                return process.ProcessName ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static bool IsCodexCommandRunner(string processName)
        {
            return processName.StartsWith("codex-command-runner", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCodexProcess(Process process, string processName, bool inspectProcessPath)
        {
            if (String.Equals(processName, "NoNo-Standalone", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(processName, "NoNo-Standalone.dev", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (processName.IndexOf("codex", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            string title = "";
            try
            {
                title = process.MainWindowTitle;
            }
            catch
            {
            }

            if (!String.IsNullOrEmpty(title) && title.IndexOf("codex", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (!inspectProcessPath)
            {
                return false;
            }

            string path = "";
            try
            {
                path = process.MainModule == null ? "" : process.MainModule.FileName;
            }
            catch
            {
            }

            return !String.IsNullOrEmpty(path) && path.IndexOf("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal static class CodexMonitorSettingsStore
    {
        private const string PreferenceKey = "codex-monitor-enabled";

        public static bool LoadEnabled()
        {
            string value = PanelStorage.LoadPreference(PreferenceKey).Trim();
            if (String.Equals(value, "0", StringComparison.Ordinal) ||
                String.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        public static void SaveEnabled(bool enabled)
        {
            PanelStorage.SavePreference(PreferenceKey, enabled ? "1" : "0");
        }
    }

    internal static class HotkeySettingsStore
    {
        private const string QuickLaunchAction = "quick-launch";
        private const string QuickLaunchDefaultGesture = "Ctrl+Alt+O";
        private const string QuickLaunchOldDefaultGesture = "Ctrl+Alt+L";

        public static event EventHandler SettingsChanged;

        public static List<HotkeyDefinition> Load()
        {
            List<HotkeyDefinition> defaults = Defaults();
            List<string[]> rows = PanelStorage.LoadRows(PanelStorage.HotkeysFile);
            for (int i = 0; i < rows.Count; i++)
            {
                string[] row = rows[i];
                if (row.Length < 3)
                {
                    continue;
                }

                HotkeyDefinition definition = Find(defaults, row[0]);
                if (definition != null)
                {
                    string gesture = row[1];
                    if (String.Equals(definition.Action, QuickLaunchAction, StringComparison.OrdinalIgnoreCase) &&
                        String.Equals(gesture, QuickLaunchOldDefaultGesture, StringComparison.OrdinalIgnoreCase))
                    {
                        gesture = QuickLaunchDefaultGesture;
                    }

                    definition.Gesture = gesture;
                    definition.Enabled = String.Equals(row[2], "1", StringComparison.Ordinal);
                }
            }

            return defaults;
        }

        public static void Save(List<HotkeyDefinition> definitions)
        {
            List<string[]> rows = new List<string[]>();
            for (int i = 0; i < definitions.Count; i++)
            {
                rows.Add(new string[] { definitions[i].Action, definitions[i].Gesture ?? "", definitions[i].Enabled ? "1" : "0" });
            }

            PanelStorage.SaveRows(PanelStorage.HotkeysFile, rows);
            if (SettingsChanged != null)
            {
                SettingsChanged(null, EventArgs.Empty);
            }
        }

        public static void Reset()
        {
            Save(Defaults());
        }

        public static bool Validate(List<HotkeyDefinition> definitions, out string message)
        {
            for (int i = 0; i < definitions.Count; i++)
            {
                if (!definitions[i].Enabled)
                {
                    continue;
                }

                uint modifiers;
                uint key;
                if (!HotkeyGesture.TryParse(definitions[i].Gesture, out modifiers, out key))
                {
                    message = definitions[i].Label + " 的快捷键无效。";
                    return false;
                }
            }

            message = "";
            return true;
        }

        private static HotkeyDefinition Find(List<HotkeyDefinition> definitions, string action)
        {
            for (int i = 0; i < definitions.Count; i++)
            {
                if (String.Equals(definitions[i].Action, action, StringComparison.OrdinalIgnoreCase))
                {
                    return definitions[i];
                }
            }

            return null;
        }

        private static List<HotkeyDefinition> Defaults()
        {
            return new List<HotkeyDefinition>
            {
                new HotkeyDefinition("show-panel", "打开面板", "Ctrl+Alt+Space", "显示或聚焦诺诺面板。", true),
                new HotkeyDefinition(QuickLaunchAction, "快速直达", QuickLaunchDefaultGesture, "弹出小窗口，输入关键词、网址、路径或命令后回车直达。", true),
                new HotkeyDefinition("show-desktop", "回桌面", "Ctrl+Alt+D", "最小化所有窗口，直接回到桌面。", true),
                new HotkeyDefinition("toggle-passthrough", "鼠标穿透", "Ctrl+Alt+P", "切换桌宠鼠标穿透。", false),
                new HotkeyDefinition("stop-agent", "停止屏幕助手", "Ctrl+Alt+Escape", "立即停止当前屏幕查看或电脑操作任务。", true),
                new HotkeyDefinition("idle", "待机状态", "Ctrl+Alt+I", "切换到待机动画。", false),
                new HotkeyDefinition("running", "运行状态", "Ctrl+Alt+R", "切换到运行中动画。", false),
                new HotkeyDefinition("waiting", "等待状态", "Ctrl+Alt+W", "切换到等待动画。", false),
                new HotkeyDefinition("review", "检查状态", "Ctrl+Alt+V", "切换到检查动画。", false),
                new HotkeyDefinition("failed", "失败状态", "Ctrl+Alt+F", "切换到失败动画。", false)
            };
        }
    }

    internal static class HotkeyGesture
    {
        public static bool TryParse(string text, out uint modifiers, out uint key)
        {
            modifiers = 0;
            key = 0;
            if (String.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string[] parts = text.Split(new char[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (String.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase) || String.Equals(part, "Control", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= HotkeyNativeMethods.ModControl;
                }
                else if (String.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= HotkeyNativeMethods.ModAlt;
                }
                else if (String.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= HotkeyNativeMethods.ModShift;
                }
                else if (String.Equals(part, "Win", StringComparison.OrdinalIgnoreCase) || String.Equals(part, "Windows", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= HotkeyNativeMethods.ModWin;
                }
                else
                {
                    Keys parsed;
                    if (!TryParseKey(part, out parsed))
                    {
                        return false;
                    }

                    key = (uint)parsed;
                }
            }

            return key != 0 && modifiers != 0;
        }

        public static string FromKeyEvent(KeyEventArgs e)
        {
            List<string> parts = new List<string>();
            if (e.Control)
            {
                parts.Add("Ctrl");
            }

            if (e.Alt)
            {
                parts.Add("Alt");
            }

            if (e.Shift)
            {
                parts.Add("Shift");
            }

            if ((e.Modifiers & Keys.LWin) == Keys.LWin || (e.Modifiers & Keys.RWin) == Keys.RWin)
            {
                parts.Add("Win");
            }

            parts.Add(KeyToText(e.KeyCode));
            return String.Join("+", parts.ToArray());
        }

        private static bool TryParseKey(string text, out Keys key)
        {
            key = Keys.None;
            if (text.Length == 1)
            {
                char c = Char.ToUpperInvariant(text[0]);
                if (c >= 'A' && c <= 'Z')
                {
                    key = (Keys)Enum.Parse(typeof(Keys), c.ToString(), true);
                    return true;
                }

                if (c >= '0' && c <= '9')
                {
                    key = (Keys)((int)Keys.D0 + (c - '0'));
                    return true;
                }
            }

            if (String.Equals(text, "Esc", StringComparison.OrdinalIgnoreCase))
            {
                text = "Escape";
            }

            if (String.Equals(text, "Space", StringComparison.OrdinalIgnoreCase))
            {
                key = Keys.Space;
                return true;
            }

            try
            {
                key = (Keys)Enum.Parse(typeof(Keys), text, true);
                return key != Keys.None;
            }
            catch
            {
                return false;
            }
        }

        private static string KeyToText(Keys key)
        {
            if (key >= Keys.D0 && key <= Keys.D9)
            {
                return ((char)('0' + (key - Keys.D0))).ToString();
            }

            return key.ToString();
        }
    }

    internal static class GlobalHotkeyManager
    {
        public const int WmHotkey = 0x0312;
        private const int BaseId = 0x4E00;
        private static readonly Dictionary<int, string> actions = new Dictionary<int, string>();
        public static string LastStatus = "";

        public static void RegisterAll(IntPtr handle)
        {
            UnregisterAll(handle);
            List<HotkeyDefinition> definitions = HotkeySettingsStore.Load();
            List<string> failures = new List<string>();
            int offset = 0;
            for (int i = 0; i < definitions.Count; i++)
            {
                HotkeyDefinition definition = definitions[i];
                if (!definition.Enabled)
                {
                    continue;
                }

                uint modifiers;
                uint key;
                if (!HotkeyGesture.TryParse(definition.Gesture, out modifiers, out key))
                {
                    failures.Add(definition.Label + " 无效");
                    continue;
                }

                int id = BaseId + offset;
                offset++;
                if (HotkeyNativeMethods.RegisterHotKey(handle, id, modifiers | HotkeyNativeMethods.ModNoRepeat, key))
                {
                    actions[id] = definition.Action;
                }
                else
                {
                    failures.Add(definition.Label + " 注册失败");
                }
            }

            LastStatus = failures.Count == 0 ? "快捷键已就绪。" : String.Join("；", failures.ToArray());
        }

        public static void UnregisterAll(IntPtr handle)
        {
            foreach (int id in new List<int>(actions.Keys))
            {
                HotkeyNativeMethods.UnregisterHotKey(handle, id);
            }

            actions.Clear();
        }

        public static string GetAction(int id)
        {
            string action;
            return actions.TryGetValue(id, out action) ? action : "";
        }
    }

    internal static class HotkeyNativeMethods
    {
        public const uint ModAlt = 0x0001;
        public const uint ModControl = 0x0002;
        public const uint ModShift = 0x0004;
        public const uint ModWin = 0x0008;
        public const uint ModNoRepeat = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }

    internal sealed class NoNoPanelForm : Form
    {
        private readonly List<PanelFeature> features;
        private readonly List<PanelNavItem> navItems;
        private readonly Font brandFont;
        private readonly Font titleFont;
        private readonly Font bodyFont;
        private readonly Font labelFont;
        private readonly Font smallFont;
        private readonly Font monoFont;
        private readonly Icon windowIcon;
        private PanelFeature selectedFeature;
        private Rectangle titleBarBounds;
        private Rectangle dragBounds;
        private Rectangle minimizeButtonBounds;
        private Rectangle maximizeButtonBounds;
        private Rectangle closeButtonBounds;
        private Rectangle shellBounds;
        private Rectangle navBounds;
        private Rectangle stageBounds;
        private Rectangle footerBounds;
        private Rectangle normalBounds;
        private bool panelMaximized;
        private bool minimizeButtonHot;
        private bool maximizeButtonHot;
        private bool closeButtonHot;
        private bool dragging;
        private Point dragCursor;
        private Point dragWindow;

        public NoNoPanelForm()
        {
            features = new List<PanelFeature>();
            navItems = new List<PanelNavItem>();
            windowIcon = PetWindowIcon.Create();

            Text = "诺诺面板";
            Icon = windowIcon;
            FormBorderStyle = FormBorderStyle.None;
            MinimizeBox = true;
            MaximizeBox = true;
            ShowInTaskbar = true;
            TopMost = false;
            DoubleBuffered = true;
            MinimumSize = new Size(780, 540);
            Size = new Size(900, 560);
            BackColor = Color.FromArgb(239, 243, 247);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            brandFont = new Font(Font.FontFamily, 12.5F, FontStyle.Bold);
            titleFont = new Font(Font.FontFamily, 19F, FontStyle.Bold);
            bodyFont = new Font(Font.FontFamily, 9.5F, FontStyle.Regular);
            labelFont = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            smallFont = new Font(Font.FontFamily, 8.25F, FontStyle.Regular);
            monoFont = new Font("Consolas", 8.5F, FontStyle.Regular, GraphicsUnit.Point);

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);

            AddFeature("网址直达", "站点、文档、后台入口", PanelGlyph.Link, "为项目文档、看板、服务后台和常用网页预留快速入口。", "URL");
            AddFeature("应用启动", "程序、脚本、文件夹", PanelGlyph.App, "为本机应用、脚本命令、项目目录和系统位置预留启动位。", "APP");
            AddFeature("工具箱", "桌面效率动作", PanelGlyph.Tool, "为窗口整理、清理动作、系统开关和开发辅助工具预留空间。", "TOOL");
            AddFeature("剪贴板", "文本与代码片段", PanelGlyph.Clipboard, "为复制历史、常用片段、格式化结果和临时缓存预留列表区域。", "CLIP");
            AddFeature("便签", "任务、想法、草稿", PanelGlyph.Note, "为浮动记录、待办提醒、临时想法和编码上下文预留编辑空间。", "NOTE");
            AddFeature("设置", "外观与行为偏好", PanelGlyph.Settings, "为置顶、穿透、启动项、宠物尺寸和面板偏好预留控制区。", "SET");

            selectedFeature = features[0];
            foreach (PanelNavItem item in navItems)
            {
                item.Selected = ReferenceEquals(item.Feature, selectedFeature);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                brandFont.Dispose();
                titleFont.Dispose();
                bodyFont.Dispose();
                labelFont.Dispose();
                smallFont.Dispose();
                monoFont.Dispose();
                windowIcon.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= NativeMethods.CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateWindowRegion();
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (!panelMaximized && WindowState == FormWindowState.Normal)
            {
                normalBounds = Bounds;
            }
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);

            titleBarBounds = new Rectangle(0, 0, Width, 48);
            minimizeButtonBounds = new Rectangle(Width - 138, 10, 36, 28);
            maximizeButtonBounds = new Rectangle(Width - 94, 10, 36, 28);
            closeButtonBounds = new Rectangle(Width - 50, 10, 36, 28);
            dragBounds = new Rectangle(0, 0, Math.Max(1, Width - 152), 48);

            int outer = panelMaximized ? 24 : 18;
            shellBounds = new Rectangle(outer, 64, Width - outer * 2, Height - outer - 64);
            footerBounds = new Rectangle(shellBounds.Left, shellBounds.Bottom - 42, shellBounds.Width, 32);
            navBounds = new Rectangle(shellBounds.Left, shellBounds.Top, 250, shellBounds.Height - 52);
            stageBounds = new Rectangle(navBounds.Right + 18, shellBounds.Top, shellBounds.Right - navBounds.Right - 18, shellBounds.Height - 52);

            int itemHeight = 50;
            int itemGap = 8;
            for (int i = 0; i < navItems.Count; i++)
            {
                navItems[i].Bounds = new Rectangle(navBounds.Left + 12, navBounds.Top + 74 + i * (itemHeight + itemGap), navBounds.Width - 24, itemHeight);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 0 || Height <= 0)
            {
                return;
            }

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            DrawBackground(g);
            DrawTitleBar(g);
            DrawNavigation(g);
            DrawStage(g);
            DrawFooter(g);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            if (minimizeButtonBounds.Contains(e.Location))
            {
                WindowState = FormWindowState.Minimized;
                return;
            }

            if (maximizeButtonBounds.Contains(e.Location))
            {
                ToggleMaximizePanel();
                return;
            }

            if (closeButtonBounds.Contains(e.Location))
            {
                Close();
                return;
            }

            if (dragBounds.Contains(e.Location) && !panelMaximized)
            {
                dragging = true;
                dragCursor = Cursor.Position;
                dragWindow = Location;
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            UpdateButtonHotState(e.Location);

            if (!dragging)
            {
                return;
            }

            Point cursor = Cursor.Position;
            Location = new Point(dragWindow.X + cursor.X - dragCursor.X, dragWindow.Y + cursor.Y - dragCursor.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            Capture = false;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            UpdateButtonHotState(new Point(-1, -1));
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && dragBounds.Contains(e.Location))
            {
                ToggleMaximizePanel();
            }
        }

        private void AddFeature(string title, string subtitle, PanelGlyph glyph, string description, string code)
        {
            PanelFeature feature = new PanelFeature(title, subtitle, glyph, description);
            PanelNavItem item = new PanelNavItem(feature, code);
            item.FeatureSelected += OnFeatureSelected;
            features.Add(feature);
            navItems.Add(item);
            Controls.Add(item);
        }

        private void OnFeatureSelected(object sender, EventArgs e)
        {
            PanelNavItem item = (PanelNavItem)sender;
            selectedFeature = item.Feature;
            foreach (PanelNavItem sibling in navItems)
            {
                sibling.Selected = ReferenceEquals(sibling.Feature, selectedFeature);
            }

            Invalidate();
        }

        private void ToggleMaximizePanel()
        {
            if (panelMaximized)
            {
                panelMaximized = false;
                Rectangle target = normalBounds;
                if (target.Width < MinimumSize.Width || target.Height < MinimumSize.Height)
                {
                    target = new Rectangle(Location, new Size(900, 560));
                }

                Bounds = target;
            }
            else
            {
                if (!panelMaximized)
                {
                    normalBounds = Bounds;
                }

                panelMaximized = true;
                Bounds = Screen.FromControl(this).WorkingArea;
            }

            dragging = false;
            Capture = false;
            UpdateWindowRegion();
            Invalidate();
        }

        private void UpdateButtonHotState(Point point)
        {
            SetButtonHot(ref minimizeButtonHot, minimizeButtonBounds.Contains(point), minimizeButtonBounds);
            SetButtonHot(ref maximizeButtonHot, maximizeButtonBounds.Contains(point), maximizeButtonBounds);
            SetButtonHot(ref closeButtonHot, closeButtonBounds.Contains(point), closeButtonBounds);
        }

        private void SetButtonHot(ref bool field, bool value, Rectangle bounds)
        {
            if (field == value)
            {
                return;
            }

            field = value;
            Invalidate(bounds);
        }

        private void UpdateWindowRegion()
        {
            Region oldRegion = Region;
            Region = null;

            if (!panelMaximized && Width > 0 && Height > 0)
            {
                using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, Width, Height), 18))
                {
                    Region = new Region(path);
                }
            }

            if (oldRegion != null)
            {
                oldRegion.Dispose();
            }
        }

        private void DrawBackground(Graphics g)
        {
            Rectangle full = new Rectangle(0, 0, Width, Height);
            using (LinearGradientBrush brush = new LinearGradientBrush(full, Color.FromArgb(235, 241, 247), Color.FromArgb(248, 250, 252), LinearGradientMode.Vertical))
            {
                g.FillRectangle(brush, full);
            }

            using (Pen trace = new Pen(Color.FromArgb(34, 75, 104, 130), 1))
            {
                int y = Height - 86;
                g.DrawLine(trace, 28, y, 150, y);
                g.DrawLine(trace, 150, y, 188, y - 22);
                g.DrawLine(trace, 188, y - 22, 312, y - 22);
                g.DrawLine(trace, Width - 286, 74, Width - 170, 74);
                g.DrawLine(trace, Width - 170, 74, Width - 128, 100);
                g.DrawLine(trace, Width - 128, 100, Width - 44, 100);
            }

            DrawNode(g, new Point(150, Height - 86), Color.FromArgb(24, 145, 190));
            DrawNode(g, new Point(Width - 170, 74), Color.FromArgb(24, 145, 190));
        }

        private void DrawTitleBar(Graphics g)
        {
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(17, 25, 34)))
            {
                g.FillRectangle(brush, titleBarBounds);
            }

            using (Pen line = new Pen(Color.FromArgb(48, 77, 96), 1))
            {
                g.DrawLine(line, 0, titleBarBounds.Bottom - 1, Width, titleBarBounds.Bottom - 1);
            }

            DrawRobotMark(g, new Rectangle(18, 10, 30, 28));
            TextRenderer.DrawText(g, "诺诺", brandFont, new Rectangle(58, 12, 150, 22), Color.FromArgb(235, 248, 252), TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, "Panel", monoFont, new Rectangle(148, 15, 70, 20), Color.FromArgb(124, 203, 224), TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            DrawWindowButton(g, minimizeButtonBounds, minimizeButtonHot, WindowButtonKind.Minimize);
            DrawWindowButton(g, maximizeButtonBounds, maximizeButtonHot, panelMaximized ? WindowButtonKind.Restore : WindowButtonKind.Maximize);
            DrawCloseButton(g);
        }

        private void DrawNavigation(Graphics g)
        {
            using (GraphicsPath path = RoundedRect(navBounds, 14))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(229, 245, 252)))
            using (Pen pen = new Pen(Color.FromArgb(178, 218, 232), 1))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            TextRenderer.DrawText(g, "功能面板", labelFont, new Rectangle(navBounds.Left + 18, navBounds.Top + 18, 100, 22), Color.FromArgb(22, 53, 68), TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "先保留入口和空间，功能后续接入", smallFont, new Rectangle(navBounds.Left + 18, navBounds.Top + 40, navBounds.Width - 36, 22), Color.FromArgb(74, 108, 123), TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        private void DrawStage(Graphics g)
        {
            using (GraphicsPath path = RoundedRect(stageBounds, 14))
            using (SolidBrush brush = new SolidBrush(Color.White))
            using (Pen pen = new Pen(Color.FromArgb(198, 211, 221), 1))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            Rectangle hero = new Rectangle(stageBounds.Left + 20, stageBounds.Top + 20, stageBounds.Width - 40, 128);
            using (GraphicsPath path = RoundedRect(hero, 12))
            using (LinearGradientBrush brush = new LinearGradientBrush(hero, Color.FromArgb(15, 25, 35), Color.FromArgb(24, 45, 58), LinearGradientMode.Horizontal))
            {
                g.FillPath(brush, path);
            }

            using (Pen grid = new Pen(Color.FromArgb(46, 81, 102), 1))
            {
                for (int x = hero.Left + 24; x < hero.Right - 16; x += 42)
                {
                    g.DrawLine(grid, x, hero.Top + 18, x, hero.Bottom - 18);
                }

                for (int y = hero.Top + 24; y < hero.Bottom - 18; y += 28)
                {
                    g.DrawLine(grid, hero.Left + 18, y, hero.Right - 18, y);
                }
            }

            DrawGlyph(g, selectedFeature.Glyph, new Rectangle(hero.Left + 24, hero.Top + 28, 54, 54), Color.FromArgb(64, 226, 255));
            TextRenderer.DrawText(g, selectedFeature.Title, titleFont, new Rectangle(hero.Left + 96, hero.Top + 28, hero.Width - 120, 34), Color.White, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, selectedFeature.Subtitle, bodyFont, new Rectangle(hero.Left + 98, hero.Top + 66, hero.Width - 126, 24), Color.FromArgb(177, 222, 232), TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            DrawPill(g, new Rectangle(hero.Left + 98, hero.Top + 94, 118, 22), "预留模块", Color.FromArgb(52, 215, 245));

            Rectangle description = new Rectangle(stageBounds.Left + 24, hero.Bottom + 22, stageBounds.Width - 48, 48);
            TextRenderer.DrawText(g, selectedFeature.Description, bodyFont, description, Color.FromArgb(40, 55, 66), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            int laneTop = description.Bottom + 12;
            int laneHeight = Math.Max(48, (stageBounds.Bottom - 84 - laneTop - 18) / 3);
            DrawPlaceholderLane(g, new Rectangle(stageBounds.Left + 24, laneTop, stageBounds.Width - 48, laneHeight), "主内容区", "后续放列表、表单或快捷动作");
            DrawPlaceholderLane(g, new Rectangle(stageBounds.Left + 24, laneTop + laneHeight + 10, stageBounds.Width - 48, laneHeight), "侧向信息", "后续放最近使用、状态或上下文");
            DrawPlaceholderLane(g, new Rectangle(stageBounds.Left + 24, laneTop + (laneHeight + 10) * 2, stageBounds.Width - 48, laneHeight), "操作区", "后续放新增、编辑、绑定和执行按钮");

            Rectangle command = new Rectangle(stageBounds.Left + 24, stageBounds.Bottom - 58, stageBounds.Width - 48, 36);
            using (GraphicsPath path = RoundedRect(command, 9))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(242, 247, 250)))
            using (Pen pen = new Pen(Color.FromArgb(207, 220, 229), 1))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            TextRenderer.DrawText(g, "功能细节暂不实现：这里仅作为未来命令、搜索或新增入口的空间", smallFont, command, Color.FromArgb(73, 91, 105), TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        }

        private void DrawFooter(Graphics g)
        {
            TextRenderer.DrawText(g, "7 个功能入口已就绪", labelFont, new Rectangle(footerBounds.Left + 4, footerBounds.Top + 8, 180, 20), Color.FromArgb(43, 58, 70), TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "Windows 工具窗口 · 占位版", smallFont, new Rectangle(footerBounds.Right - 220, footerBounds.Top + 8, 216, 20), Color.FromArgb(88, 105, 118), TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }

        private void DrawPlaceholderLane(Graphics g, Rectangle bounds, string title, string text)
        {
            using (GraphicsPath path = RoundedRect(bounds, 10))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(247, 250, 252)))
            using (Pen pen = new Pen(Color.FromArgb(213, 225, 233), 1))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            Rectangle icon = new Rectangle(bounds.Left + 14, bounds.Top + bounds.Height / 2 - 12, 24, 24);
            using (GraphicsPath path = RoundedRect(icon, 7))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(224, 244, 249)))
            {
                g.FillPath(brush, path);
            }

            using (Pen pen = new Pen(Color.FromArgb(35, 155, 184), 1.8F))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, icon.Left + 7, icon.Top + 12, icon.Right - 7, icon.Top + 12);
                g.DrawLine(pen, icon.Left + 12, icon.Top + 7, icon.Left + 12, icon.Bottom - 7);
            }

            TextRenderer.DrawText(g, title, labelFont, new Rectangle(bounds.Left + 50, bounds.Top + 10, bounds.Width - 70, 20), Color.FromArgb(35, 48, 59), TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, text, smallFont, new Rectangle(bounds.Left + 50, bounds.Top + 30, bounds.Width - 70, 20), Color.FromArgb(89, 105, 117), TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        private void DrawRobotMark(Graphics g, Rectangle bounds)
        {
            using (GraphicsPath body = RoundedRect(bounds, 8))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(238, 249, 252)))
            {
                g.FillPath(brush, body);
            }

            Rectangle screen = new Rectangle(bounds.Left + 6, bounds.Top + 8, bounds.Width - 12, bounds.Height - 12);
            using (GraphicsPath path = RoundedRect(screen, 6))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(12, 19, 28)))
            {
                g.FillPath(brush, path);
            }

            using (SolidBrush eye = new SolidBrush(Color.FromArgb(63, 226, 255)))
            {
                g.FillEllipse(eye, screen.Left + 7, screen.Top + 7, 4, 4);
                g.FillEllipse(eye, screen.Right - 11, screen.Top + 7, 4, 4);
            }
        }

        private void DrawNode(Graphics g, Point center, Color color)
        {
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(48, color)))
            {
                g.FillEllipse(brush, center.X - 8, center.Y - 8, 16, 16);
            }

            using (SolidBrush brush = new SolidBrush(color))
            {
                g.FillEllipse(brush, center.X - 3, center.Y - 3, 6, 6);
            }
        }

        private void DrawCloseButton(Graphics g)
        {
            Color fill = closeButtonHot ? Color.FromArgb(224, 62, 83) : Color.FromArgb(31, 43, 55);
            Color stroke = closeButtonHot ? Color.FromArgb(255, 151, 163) : Color.FromArgb(69, 88, 103);

            using (GraphicsPath path = RoundedRect(closeButtonBounds, 8))
            using (SolidBrush brush = new SolidBrush(fill))
            using (Pen pen = new Pen(stroke, 1))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            using (Pen x = new Pen(Color.White, 1.8F))
            {
                x.StartCap = LineCap.Round;
                x.EndCap = LineCap.Round;
                g.DrawLine(x, closeButtonBounds.Left + 11, closeButtonBounds.Top + 9, closeButtonBounds.Right - 11, closeButtonBounds.Bottom - 9);
                g.DrawLine(x, closeButtonBounds.Right - 11, closeButtonBounds.Top + 9, closeButtonBounds.Left + 11, closeButtonBounds.Bottom - 9);
            }
        }

        private void DrawWindowButton(Graphics g, Rectangle bounds, bool hot, WindowButtonKind kind)
        {
            Color fill = hot ? Color.FromArgb(42, 59, 72) : Color.FromArgb(26, 37, 49);
            Color stroke = hot ? Color.FromArgb(83, 126, 146) : Color.FromArgb(62, 81, 96);
            Color icon = hot ? Color.FromArgb(220, 249, 255) : Color.FromArgb(167, 193, 207);

            using (GraphicsPath path = RoundedRect(bounds, 8))
            using (SolidBrush brush = new SolidBrush(fill))
            using (Pen pen = new Pen(stroke, 1))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            using (Pen pen = new Pen(icon, 1.8F))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                if (kind == WindowButtonKind.Minimize)
                {
                    g.DrawLine(pen, bounds.Left + 10, bounds.Top + 18, bounds.Right - 10, bounds.Top + 18);
                }
                else if (kind == WindowButtonKind.Maximize)
                {
                    g.DrawRectangle(pen, bounds.Left + 11, bounds.Top + 9, bounds.Width - 22, bounds.Height - 18);
                }
                else
                {
                    g.DrawRectangle(pen, bounds.Left + 13, bounds.Top + 8, bounds.Width - 22, bounds.Height - 18);
                    g.DrawRectangle(pen, bounds.Left + 9, bounds.Top + 12, bounds.Width - 22, bounds.Height - 18);
                }
            }
        }

        private void DrawPill(Graphics g, Rectangle bounds, string text, Color accent)
        {
            using (GraphicsPath path = RoundedRect(bounds, bounds.Height / 2))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(30, accent)))
            using (Pen pen = new Pen(Color.FromArgb(140, accent), 1))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            TextRenderer.DrawText(g, text, smallFont, bounds, Color.FromArgb(218, 249, 255), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        internal static void DrawGlyph(Graphics g, PanelGlyph glyph, Rectangle bounds, Color color)
        {
            using (Pen pen = new Pen(color, 2.2F))
            using (SolidBrush brush = new SolidBrush(color))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                if (glyph == PanelGlyph.Link)
                {
                    g.DrawArc(pen, bounds.Left + 2, bounds.Top + 9, 20, 18, 116, 244);
                    g.DrawArc(pen, bounds.Right - 22, bounds.Top + 9, 20, 18, -64, 244);
                    g.DrawLine(pen, bounds.Left + 17, bounds.Top + 18, bounds.Right - 17, bounds.Top + 18);
                }
                else if (glyph == PanelGlyph.App)
                {
                    g.DrawRectangle(pen, bounds.Left + 6, bounds.Top + 7, bounds.Width - 12, bounds.Height - 14);
                    g.DrawLine(pen, bounds.Left + 6, bounds.Top + 17, bounds.Right - 6, bounds.Top + 17);
                    Point[] play = new Point[] { new Point(bounds.Left + 19, bounds.Top + 23), new Point(bounds.Left + 19, bounds.Top + 36), new Point(bounds.Left + 32, bounds.Top + 29) };
                    g.FillPolygon(brush, play);
                }
                else if (glyph == PanelGlyph.Tool)
                {
                    g.DrawLine(pen, bounds.Left + 11, bounds.Bottom - 11, bounds.Right - 11, bounds.Top + 11);
                    g.DrawEllipse(pen, bounds.Right - 19, bounds.Top + 7, 12, 12);
                    g.DrawLine(pen, bounds.Left + 8, bounds.Bottom - 14, bounds.Left + 17, bounds.Bottom - 5);
                }
                else if (glyph == PanelGlyph.Clipboard)
                {
                    g.DrawRectangle(pen, bounds.Left + 10, bounds.Top + 8, bounds.Width - 20, bounds.Height - 12);
                    g.DrawRectangle(pen, bounds.Left + 16, bounds.Top + 4, bounds.Width - 32, 10);
                    g.DrawLine(pen, bounds.Left + 17, bounds.Top + 25, bounds.Right - 17, bounds.Top + 25);
                    g.DrawLine(pen, bounds.Left + 17, bounds.Top + 34, bounds.Right - 22, bounds.Top + 34);
                }
                else if (glyph == PanelGlyph.Note)
                {
                    g.DrawRectangle(pen, bounds.Left + 8, bounds.Top + 6, bounds.Width - 16, bounds.Height - 12);
                    g.DrawLine(pen, bounds.Right - 17, bounds.Top + 6, bounds.Right - 8, bounds.Top + 15);
                    g.DrawLine(pen, bounds.Right - 17, bounds.Top + 6, bounds.Right - 17, bounds.Top + 15);
                    g.DrawLine(pen, bounds.Right - 17, bounds.Top + 15, bounds.Right - 8, bounds.Top + 15);
                    g.DrawLine(pen, bounds.Left + 17, bounds.Top + 26, bounds.Right - 19, bounds.Top + 26);
                    g.DrawLine(pen, bounds.Left + 17, bounds.Top + 35, bounds.Right - 25, bounds.Top + 35);
                }
                else if (glyph == PanelGlyph.Settings)
                {
                    g.DrawEllipse(pen, bounds.Left + 12, bounds.Top + 12, bounds.Width - 24, bounds.Height - 24);
                    g.FillEllipse(brush, bounds.Left + bounds.Width / 2 - 3, bounds.Top + bounds.Height / 2 - 3, 6, 6);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = Math.PI * 2 * i / 8.0;
                        Point p1 = new Point(bounds.Left + bounds.Width / 2 + (int)(Math.Cos(a) * 15), bounds.Top + bounds.Height / 2 + (int)(Math.Sin(a) * 15));
                        Point p2 = new Point(bounds.Left + bounds.Width / 2 + (int)(Math.Cos(a) * 20), bounds.Top + bounds.Height / 2 + (int)(Math.Sin(a) * 20));
                        g.DrawLine(pen, p1, p2);
                    }
                }
                else if (glyph == PanelGlyph.Appearance)
                {
                    g.DrawEllipse(pen, bounds.Left + 7, bounds.Top + 9, bounds.Width - 14, bounds.Height - 16);
                    g.FillEllipse(brush, bounds.Left + 15, bounds.Top + 18, 5, 5);
                    g.FillEllipse(brush, bounds.Left + 25, bounds.Top + 14, 5, 5);
                    g.FillEllipse(brush, bounds.Left + 32, bounds.Top + 23, 5, 5);
                    g.FillEllipse(brush, bounds.Left + 19, bounds.Top + 31, 5, 5);
                    g.DrawArc(pen, bounds.Left + 25, bounds.Top + 27, 15, 13, 35, 210);
                }
                else if (glyph == PanelGlyph.Keyboard)
                {
                    g.DrawRectangle(pen, bounds.Left + 5, bounds.Top + 11, bounds.Width - 10, bounds.Height - 22);
                    for (int y = 0; y < 2; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            g.FillRectangle(brush, bounds.Left + 13 + x * 9, bounds.Top + 19 + y * 9, 4, 4);
                        }
                    }
                    g.DrawLine(pen, bounds.Left + 17, bounds.Bottom - 15, bounds.Right - 17, bounds.Bottom - 15);
                }
            }
        }

        internal static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class PanelNavItem : Control
    {
        private readonly string code;
        private bool selected;
        private bool hot;

        public event EventHandler FeatureSelected;
        public readonly PanelFeature Feature;

        public PanelNavItem(PanelFeature feature, string code)
        {
            Feature = feature;
            this.code = code;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            TabStop = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        }

        public bool Selected
        {
            get { return selected; }
            set
            {
                if (selected == value)
                {
                    return;
                }

                selected = value;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hot = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hot = false;
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Focus();
            if (FeatureSelected != null)
            {
                FeatureSelected(this, EventArgs.Empty);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = selected ? Color.FromArgb(211, 240, 249) : hot ? Color.FromArgb(239, 250, 254) : Color.FromArgb(246, 252, 254);
            Color border = selected ? Color.FromArgb(82, 189, 215) : hot ? Color.FromArgb(178, 218, 232) : Color.FromArgb(0, 0, 0, 0);
            Color title = Color.FromArgb(34, 66, 80);
            Color subtitle = Color.FromArgb(74, 108, 123);
            Color glyph = selected ? Color.FromArgb(12, 120, 151) : Color.FromArgb(55, 89, 104);

            using (GraphicsPath path = NoNoPanelForm.RoundedRect(bounds, 10))
            using (SolidBrush brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
            }

            if (selected || hot)
            {
                using (GraphicsPath path = NoNoPanelForm.RoundedRect(bounds, 10))
                using (Pen pen = new Pen(border, selected ? 1.4F : 1F))
                {
                    g.DrawPath(pen, path);
                }
            }

            Rectangle glyphBox = new Rectangle(10, 9, 32, 32);
            using (GraphicsPath path = NoNoPanelForm.RoundedRect(glyphBox, 9))
            using (SolidBrush brush = new SolidBrush(selected ? Color.FromArgb(193, 234, 246) : Color.FromArgb(224, 241, 248)))
            {
                g.FillPath(brush, path);
            }

            NoNoPanelForm.DrawGlyph(g, Feature.Glyph, new Rectangle(12, 11, 28, 28), glyph);

            using (Font titleFont = new Font(Font.FontFamily, 9.3F, FontStyle.Bold))
            using (Font subtitleFont = new Font(Font.FontFamily, 8F, FontStyle.Regular))
            using (Font codeFont = new Font("Consolas", 7.5F, FontStyle.Regular, GraphicsUnit.Point))
            {
                TextRenderer.DrawText(g, Feature.Title, titleFont, new Rectangle(52, 8, Width - 96, 20), title, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, Feature.Subtitle, subtitleFont, new Rectangle(52, 28, Width - 96, 18), subtitle, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, code, codeFont, new Rectangle(Width - 42, 18, 34, 16), selected ? Color.FromArgb(12, 120, 151) : Color.FromArgb(84, 121, 136), TextFormatFlags.Right | TextFormatFlags.NoPadding);
            }

            if (Focused)
            {
                ControlPaint.DrawFocusRectangle(g, new Rectangle(3, 3, Width - 7, Height - 7), title, fill);
            }
        }
    }

    internal sealed class LegacyNoNoPanelForm : Form
    {
        private readonly List<PanelFeature> features;
        private readonly List<FeatureTile> tiles;
        private readonly Font titleFont;
        private readonly Font subtitleFont;
        private readonly Font statusFont;
        private readonly Font previewTitleFont;
        private readonly Font previewBodyFont;
        private readonly Font pillFont;
        private readonly Icon windowIcon;
        private PanelFeature selectedFeature;
        private Rectangle minimizeButtonBounds;
        private Rectangle maximizeButtonBounds;
        private Rectangle closeButtonBounds;
        private Rectangle dragBounds;
        private Rectangle contentBounds;
        private Rectangle previewBounds;
        private Rectangle statusLabelBounds;
        private Rectangle normalBounds;
        private bool panelMaximized;
        private bool minimizeButtonHot;
        private bool maximizeButtonHot;
        private bool closeButtonHot;
        private bool dragging;
        private Point dragCursor;
        private Point dragWindow;

        public LegacyNoNoPanelForm()
        {
            features = new List<PanelFeature>();
            tiles = new List<FeatureTile>();
            windowIcon = PetWindowIcon.Create();

            Text = "诺诺面板";
            Icon = windowIcon;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MinimizeBox = true;
            MaximizeBox = false;
            ShowInTaskbar = true;
            TopMost = false;
            DoubleBuffered = true;
            MinimumSize = new Size(640, 428);
            Size = new Size(660, 438);
            BackColor = Color.FromArgb(243, 243, 243);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            titleFont = new Font(Font.FontFamily, 15F, FontStyle.Bold);
            subtitleFont = new Font(Font.FontFamily, 9F, FontStyle.Regular);
            statusFont = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            previewTitleFont = new Font(Font.FontFamily, 14F, FontStyle.Bold);
            previewBodyFont = new Font(Font.FontFamily, 9.5F, FontStyle.Regular);
            pillFont = new Font(Font.FontFamily, 8.5F, FontStyle.Bold);

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);

            AddFeature("网址直达", "收藏站点与常用入口", PanelGlyph.Link, "把常用文档、项目看板、服务后台收进一个轻入口。");
            AddFeature("应用启动", "本机程序快速唤起", PanelGlyph.App, "预留应用清单和启动位，后续可绑定 exe、脚本或系统位置。");
            AddFeature("工具箱", "桌面维护与效率工具", PanelGlyph.Tool, "承载清理、窗口、系统动作等轻量工具。");
            AddFeature("剪贴板", "近期文本与代码片段", PanelGlyph.Clipboard, "为复制历史、常用片段和格式化结果准备空间。");
            AddFeature("便签", "浮动记录与临时想法", PanelGlyph.Note, "用于快速记录任务、灵感或待处理事项。");
            AddFeature("设置", "外观、行为与启动项", PanelGlyph.Settings, "集中管理桌宠大小、置顶、穿透、启动和面板偏好。");
            AddFeature("快捷键", "键盘触发与命令映射", PanelGlyph.Keyboard, "后续可配置唤起面板、切换状态和执行动作。");

            selectedFeature = features[0];
            foreach (FeatureTile tile in tiles)
            {
                tile.Selected = ReferenceEquals(tile.Feature, selectedFeature);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                titleFont.Dispose();
                subtitleFont.Dispose();
                statusFont.Dispose();
                previewTitleFont.Dispose();
                previewBodyFont.Dispose();
                pillFont.Dispose();
                windowIcon.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= NativeMethods.CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdatePanelRegion();
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (!panelMaximized && WindowState == FormWindowState.Normal)
            {
                normalBounds = Bounds;
            }
        }

        private void UpdatePanelRegion()
        {
            Region oldRegion = Region;
            Region = null;

            if (oldRegion != null)
            {
                oldRegion.Dispose();
            }
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);

            minimizeButtonBounds = Rectangle.Empty;
            maximizeButtonBounds = Rectangle.Empty;
            closeButtonBounds = Rectangle.Empty;
            dragBounds = Rectangle.Empty;

            int padding = WindowState == FormWindowState.Maximized ? 36 : 24;
            int availableWidth = Math.Max(1, Width - padding * 2);
            int contentWidth = WindowState == FormWindowState.Maximized ? Math.Min(1220, availableWidth) : availableWidth;
            int contentLeft = (Width - contentWidth) / 2;
            int contentTop = 104;
            int contentHeight = Math.Max(260, Height - contentTop - padding);
            contentBounds = new Rectangle(contentLeft, contentTop, contentWidth, contentHeight);

            int sectionGap = contentWidth >= 860 ? 22 : 16;
            int featureWidth = Math.Min(430, Math.Max(360, (int)Math.Round(contentWidth * 0.42)));
            int previewWidth = contentWidth - featureWidth - sectionGap;
            if (previewWidth < 190)
            {
                previewWidth = 190;
                featureWidth = Math.Max(360, contentWidth - previewWidth - sectionGap);
            }

            previewBounds = new Rectangle(contentLeft + featureWidth + sectionGap, contentTop, previewWidth, contentHeight);
            statusLabelBounds = new Rectangle(contentLeft + 2, 70, 150, 22);

            int tileGap = contentWidth >= 860 ? 12 : 10;
            int columns = 2;
            int tileWidth = (featureWidth - tileGap) / columns;
            int tileHeight = Math.Max(58, Math.Min(74, (contentHeight - tileGap * 3) / 4));

            for (int i = 0; i < tiles.Count; i++)
            {
                int column = i % 2;
                int row = i / 2;
                tiles[i].Bounds = new Rectangle(
                    contentLeft + column * (tileWidth + tileGap),
                    contentTop + row * (tileHeight + tileGap),
                    tileWidth,
                    tileHeight);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle full = new Rectangle(0, 0, Width - 1, Height - 1);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(243, 243, 243)))
            {
                g.FillRectangle(bg, full);
            }

            DrawHeader(g);
            DrawStatusStrip(g);
            DrawPreview(g);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            if (minimizeButtonBounds.Contains(e.Location))
            {
                MinimizePanel();
                return;
            }

            if (maximizeButtonBounds.Contains(e.Location))
            {
                ToggleMaximizePanel();
                return;
            }

            if (closeButtonBounds.Contains(e.Location))
            {
                Close();
                return;
            }

            if (dragBounds.Contains(e.Location))
            {
                if (panelMaximized)
                {
                    return;
                }

                dragging = true;
                dragCursor = Cursor.Position;
                dragWindow = Location;
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            UpdateButtonHotState(e.Location);

            if (!dragging)
            {
                return;
            }

            Point cursor = Cursor.Position;
            Location = new Point(dragWindow.X + cursor.X - dragCursor.X, dragWindow.Y + cursor.Y - dragCursor.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            dragging = false;
            Capture = false;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            UpdateButtonHotState(new Point(-1, -1));
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && dragBounds.Contains(e.Location))
            {
                ToggleMaximizePanel();
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }
        }

        private void MinimizePanel()
        {
            dragging = false;
            Capture = false;
            WindowState = FormWindowState.Minimized;
        }

        private void ToggleMaximizePanel()
        {
            if (panelMaximized)
            {
                RestorePanel();
            }
            else
            {
                MaximizePanel();
            }
        }

        private void MaximizePanel()
        {
            dragging = false;
            Capture = false;
            if (!panelMaximized)
            {
                normalBounds = Bounds;
            }

            Rectangle area = Screen.FromControl(this).WorkingArea;
            panelMaximized = true;
            Bounds = area;
            UpdatePanelRegion();
            Invalidate();
        }

        private void RestorePanel()
        {
            dragging = false;
            Capture = false;
            panelMaximized = false;

            Rectangle target = normalBounds;
            if (target.Width < MinimumSize.Width || target.Height < MinimumSize.Height)
            {
                target = new Rectangle(Location, new Size(660, 438));
            }

            Bounds = target;
            UpdatePanelRegion();
            Invalidate();
        }

        private void UpdateButtonHotState(Point point)
        {
            SetButtonHot(ref minimizeButtonHot, minimizeButtonBounds.Contains(point), minimizeButtonBounds);
            SetButtonHot(ref maximizeButtonHot, maximizeButtonBounds.Contains(point), maximizeButtonBounds);
            SetButtonHot(ref closeButtonHot, closeButtonBounds.Contains(point), closeButtonBounds);
        }

        private void SetButtonHot(ref bool field, bool value, Rectangle bounds)
        {
            if (field == value)
            {
                return;
            }

            field = value;
            Invalidate(bounds);
        }

        private void AddFeature(string title, string subtitle, PanelGlyph glyph, string description)
        {
            PanelFeature feature = new PanelFeature(title, subtitle, glyph, description);
            FeatureTile tile = new FeatureTile(feature);
            tile.FeatureSelected += OnFeatureSelected;
            features.Add(feature);
            tiles.Add(tile);
            Controls.Add(tile);
        }

        private void OnFeatureSelected(object sender, EventArgs e)
        {
            FeatureTile tile = (FeatureTile)sender;
            selectedFeature = tile.Feature;
            foreach (FeatureTile sibling in tiles)
            {
                sibling.Selected = ReferenceEquals(sibling.Feature, selectedFeature);
            }

            Invalidate();
        }

        private void DrawGlow(Graphics g)
        {
            using (GraphicsPath glow = new GraphicsPath())
            {
                glow.AddEllipse(new Rectangle(Width - 300, -170, 360, 260));
                using (PathGradientBrush brush = new PathGradientBrush(glow))
                {
                    brush.CenterColor = Color.FromArgb(86, 0, 218, 255);
                    brush.SurroundColors = new Color[] { Color.FromArgb(0, 0, 218, 255) };
                    g.FillPath(brush, glow);
                }
            }

            using (GraphicsPath glow = new GraphicsPath())
            {
                glow.AddEllipse(new Rectangle(-120, Height - 160, 270, 220));
                using (PathGradientBrush brush = new PathGradientBrush(glow))
                {
                    brush.CenterColor = Color.FromArgb(42, 91, 129, 255);
                    brush.SurroundColors = new Color[] { Color.FromArgb(0, 91, 129, 255) };
                    g.FillPath(brush, glow);
                }
            }
        }

        private void DrawHeader(Graphics g)
        {
            Rectangle mark = new Rectangle(24, 22, 58, 58);
            using (GraphicsPath body = RoundedRect(mark, 12))
            using (SolidBrush bodyBrush = new SolidBrush(Color.White))
            using (Pen bodyBorder = new Pen(Color.FromArgb(210, 210, 210), 1))
            {
                g.FillPath(bodyBrush, body);
                g.DrawPath(bodyBorder, body);
            }

            using (GraphicsPath screen = RoundedRect(new Rectangle(34, 36, 38, 25), 10))
            using (SolidBrush screenBrush = new SolidBrush(Color.FromArgb(8, 13, 20)))
            {
                g.FillPath(screenBrush, screen);
            }

            using (SolidBrush eye = new SolidBrush(Color.FromArgb(23, 215, 255)))
            {
                g.FillEllipse(eye, new Rectangle(44, 46, 6, 6));
                g.FillEllipse(eye, new Rectangle(57, 46, 6, 6));
            }

            using (Pen antenna = new Pen(Color.FromArgb(187, 236, 255), 3))
            {
                antenna.StartCap = LineCap.Round;
                antenna.EndCap = LineCap.Round;
                g.DrawLine(antenna, 40, 28, 31, 15);
                g.DrawLine(antenna, 66, 28, 76, 15);
            }

            TextRenderer.DrawText(g, "诺诺面板", titleFont, new Rectangle(96, 23, Width - 138, 28), Color.FromArgb(32, 32, 32), TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, "把桌面入口收进桌宠旁边的小型控制台", subtitleFont, new Rectangle(98, 54, Width - 140, 22), Color.FromArgb(96, 96, 96), TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        private void DrawStatusStrip(Graphics g)
        {
            Rectangle strip = new Rectangle(contentBounds.Left, 71, contentBounds.Width, 20);
            using (Pen line = new Pen(Color.FromArgb(218, 218, 218), 1))
            {
                g.DrawLine(line, strip.Left, strip.Top + 10, strip.Right, strip.Top + 10);
            }

            using (SolidBrush brush = new SolidBrush(Color.FromArgb(243, 243, 243)))
            {
                g.FillRectangle(brush, new Rectangle(statusLabelBounds.Left - 2, 67, 178, 28));
            }

            TextRenderer.DrawText(g, "7 个入口已就绪", statusFont, statusLabelBounds, Color.FromArgb(64, 64, 64), TextFormatFlags.NoPadding);
        }

        private void DrawPreview(Graphics g)
        {
            Rectangle panel = previewBounds;
            using (GraphicsPath path = RoundedRect(panel, 16))
            using (SolidBrush brush = new SolidBrush(Color.White))
            using (Pen border = new Pen(Color.FromArgb(214, 214, 214), 1))
            {
                g.FillPath(brush, path);
                g.DrawPath(border, path);
            }

            int innerPad = panel.Width >= 300 ? 22 : 16;
            int screenHeight = Math.Max(104, Math.Min(148, panel.Height / 3));
            Rectangle screen = new Rectangle(panel.Left + innerPad, panel.Top + innerPad, panel.Width - innerPad * 2, screenHeight);
            using (GraphicsPath path = RoundedRect(screen, 14))
            using (LinearGradientBrush brush = new LinearGradientBrush(screen, Color.FromArgb(10, 17, 27), Color.FromArgb(18, 34, 48), LinearGradientMode.Vertical))
            {
                g.FillPath(brush, path);
            }

            using (Pen accent = new Pen(Color.FromArgb(31, 215, 255), 2))
            {
                g.DrawLine(accent, screen.Left + 18, screen.Top + 66, screen.Right - 18, screen.Top + 66);
            }

            DrawGlyph(g, selectedFeature.Glyph, new Rectangle(screen.Left + 18, screen.Top + 20, 38, 38), Color.FromArgb(36, 219, 255));
            TextRenderer.DrawText(g, selectedFeature.Title, previewTitleFont, new Rectangle(screen.Left + 70, screen.Top + 20, screen.Width - 88, 28), Color.White, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, "功能细节稍后接入", Font, new Rectangle(screen.Left + 72, screen.Top + 52, screen.Width - 88, 24), Color.FromArgb(150, 216, 230), TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            Rectangle description = new Rectangle(panel.Left + innerPad, screen.Bottom + 22, panel.Width - innerPad * 2, Math.Max(68, panel.Height - screen.Height - 120));
            TextRenderer.DrawText(g, selectedFeature.Description, previewBodyFont, description, Color.FromArgb(38, 54, 66), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            Rectangle future = new Rectangle(panel.Left + innerPad, panel.Bottom - innerPad - 30, panel.Width - innerPad * 2, 30);
            using (GraphicsPath path = RoundedRect(future, 9))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(210, 248, 255)))
            using (Pen pen = new Pen(Color.FromArgb(121, 211, 229), 1))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            TextRenderer.DrawText(g, "已预留入口，暂不执行动作", statusFont, future, Color.FromArgb(17, 90, 112), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void DrawCloseButton(Graphics g)
        {
            Color fill = closeButtonHot ? Color.FromArgb(234, 73, 96) : Color.FromArgb(37, 48, 61);
            Color stroke = closeButtonHot ? Color.FromArgb(255, 151, 165) : Color.FromArgb(65, 82, 98);

            using (GraphicsPath path = RoundedRect(closeButtonBounds, 9))
            using (SolidBrush brush = new SolidBrush(fill))
            using (Pen pen = new Pen(stroke, 1))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            using (Pen x = new Pen(Color.White, 1.8F))
            {
                x.StartCap = LineCap.Round;
                x.EndCap = LineCap.Round;
                g.DrawLine(x, closeButtonBounds.Left + 10, closeButtonBounds.Top + 10, closeButtonBounds.Right - 10, closeButtonBounds.Bottom - 10);
                g.DrawLine(x, closeButtonBounds.Right - 10, closeButtonBounds.Top + 10, closeButtonBounds.Left + 10, closeButtonBounds.Bottom - 10);
            }
        }

        private void DrawWindowButton(Graphics g, Rectangle bounds, bool hot, WindowButtonKind kind)
        {
            Color fill = hot ? Color.FromArgb(45, 68, 84) : Color.FromArgb(31, 42, 54);
            Color stroke = hot ? Color.FromArgb(95, 139, 161) : Color.FromArgb(58, 75, 91);
            Color icon = hot ? Color.FromArgb(218, 249, 255) : Color.FromArgb(169, 194, 210);

            using (GraphicsPath path = RoundedRect(bounds, 9))
            using (SolidBrush brush = new SolidBrush(fill))
            using (Pen pen = new Pen(stroke, 1))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            using (Pen pen = new Pen(icon, 1.8F))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                if (kind == WindowButtonKind.Minimize)
                {
                    g.DrawLine(pen, bounds.Left + 9, bounds.Top + 18, bounds.Right - 9, bounds.Top + 18);
                }
                else if (kind == WindowButtonKind.Maximize)
                {
                    g.DrawRectangle(pen, bounds.Left + 10, bounds.Top + 10, bounds.Width - 20, bounds.Height - 20);
                }
                else
                {
                    g.DrawRectangle(pen, bounds.Left + 12, bounds.Top + 9, bounds.Width - 20, bounds.Height - 20);
                    g.DrawRectangle(pen, bounds.Left + 8, bounds.Top + 13, bounds.Width - 20, bounds.Height - 20);
                }
            }
        }

        private void DrawPill(Graphics g, Rectangle bounds, string text, Color accent)
        {
            using (GraphicsPath path = RoundedRect(bounds, bounds.Height / 2))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(28, accent)))
            using (Pen pen = new Pen(Color.FromArgb(120, accent), 1))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            TextRenderer.DrawText(g, text, pillFont, bounds, Color.FromArgb(218, 249, 255), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        internal static void DrawGlyph(Graphics g, PanelGlyph glyph, Rectangle bounds, Color color)
        {
            using (Pen pen = new Pen(color, 2.2F))
            using (SolidBrush brush = new SolidBrush(color))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                if (glyph == PanelGlyph.Link)
                {
                    g.DrawArc(pen, bounds.Left + 2, bounds.Top + 9, 19, 17, 116, 244);
                    g.DrawArc(pen, bounds.Right - 21, bounds.Top + 9, 19, 17, -64, 244);
                    g.DrawLine(pen, bounds.Left + 16, bounds.Top + 18, bounds.Right - 16, bounds.Top + 18);
                }
                else if (glyph == PanelGlyph.App)
                {
                    g.DrawRectangle(pen, bounds.Left + 5, bounds.Top + 7, bounds.Width - 10, bounds.Height - 14);
                    g.DrawLine(pen, bounds.Left + 5, bounds.Top + 16, bounds.Right - 5, bounds.Top + 16);
                    Point[] play = new Point[] { new Point(bounds.Left + 17, bounds.Top + 22), new Point(bounds.Left + 17, bounds.Top + 34), new Point(bounds.Left + 29, bounds.Top + 28) };
                    g.FillPolygon(brush, play);
                }
                else if (glyph == PanelGlyph.Tool)
                {
                    g.DrawLine(pen, bounds.Left + 10, bounds.Bottom - 10, bounds.Right - 10, bounds.Top + 10);
                    g.DrawEllipse(pen, bounds.Right - 17, bounds.Top + 6, 10, 10);
                    g.DrawLine(pen, bounds.Left + 8, bounds.Bottom - 13, bounds.Left + 16, bounds.Bottom - 5);
                }
                else if (glyph == PanelGlyph.Clipboard)
                {
                    g.DrawRectangle(pen, bounds.Left + 9, bounds.Top + 8, bounds.Width - 18, bounds.Height - 12);
                    g.DrawRectangle(pen, bounds.Left + 15, bounds.Top + 4, bounds.Width - 30, 10);
                    g.DrawLine(pen, bounds.Left + 16, bounds.Top + 24, bounds.Right - 16, bounds.Top + 24);
                    g.DrawLine(pen, bounds.Left + 16, bounds.Top + 32, bounds.Right - 20, bounds.Top + 32);
                }
                else if (glyph == PanelGlyph.Note)
                {
                    g.DrawRectangle(pen, bounds.Left + 8, bounds.Top + 6, bounds.Width - 16, bounds.Height - 12);
                    g.DrawLine(pen, bounds.Right - 16, bounds.Top + 6, bounds.Right - 8, bounds.Top + 14);
                    g.DrawLine(pen, bounds.Right - 16, bounds.Top + 6, bounds.Right - 16, bounds.Top + 14);
                    g.DrawLine(pen, bounds.Right - 16, bounds.Top + 14, bounds.Right - 8, bounds.Top + 14);
                    g.DrawLine(pen, bounds.Left + 16, bounds.Top + 25, bounds.Right - 18, bounds.Top + 25);
                    g.DrawLine(pen, bounds.Left + 16, bounds.Top + 33, bounds.Right - 24, bounds.Top + 33);
                }
                else if (glyph == PanelGlyph.Settings)
                {
                    g.DrawEllipse(pen, bounds.Left + 11, bounds.Top + 11, bounds.Width - 22, bounds.Height - 22);
                    g.FillEllipse(brush, bounds.Left + bounds.Width / 2 - 3, bounds.Top + bounds.Height / 2 - 3, 6, 6);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = Math.PI * 2 * i / 8.0;
                        Point p1 = new Point(bounds.Left + bounds.Width / 2 + (int)(Math.Cos(a) * 14), bounds.Top + bounds.Height / 2 + (int)(Math.Sin(a) * 14));
                        Point p2 = new Point(bounds.Left + bounds.Width / 2 + (int)(Math.Cos(a) * 18), bounds.Top + bounds.Height / 2 + (int)(Math.Sin(a) * 18));
                        g.DrawLine(pen, p1, p2);
                    }
                }
                else if (glyph == PanelGlyph.Appearance)
                {
                    g.DrawEllipse(pen, bounds.Left + 7, bounds.Top + 8, bounds.Width - 14, bounds.Height - 14);
                    g.FillEllipse(brush, bounds.Left + 14, bounds.Top + 17, 5, 5);
                    g.FillEllipse(brush, bounds.Left + 24, bounds.Top + 13, 5, 5);
                    g.FillEllipse(brush, bounds.Left + 31, bounds.Top + 22, 5, 5);
                    g.FillEllipse(brush, bounds.Left + 18, bounds.Top + 30, 5, 5);
                    g.DrawArc(pen, bounds.Left + 24, bounds.Top + 26, 14, 12, 35, 210);
                }
                else if (glyph == PanelGlyph.Keyboard)
                {
                    g.DrawRectangle(pen, bounds.Left + 4, bounds.Top + 10, bounds.Width - 8, bounds.Height - 20);
                    for (int y = 0; y < 2; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            g.FillRectangle(brush, bounds.Left + 11 + x * 8, bounds.Top + 18 + y * 9, 4, 4);
                        }
                    }
                    g.DrawLine(pen, bounds.Left + 16, bounds.Bottom - 14, bounds.Right - 16, bounds.Bottom - 14);
                }
            }
        }

        internal static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class PanelFeature
    {
        public readonly string Title;
        public readonly string Subtitle;
        public readonly PanelGlyph Glyph;
        public readonly string Description;

        public PanelFeature(string title, string subtitle, PanelGlyph glyph, string description)
        {
            Title = title;
            Subtitle = subtitle;
            Glyph = glyph;
            Description = description;
        }
    }

    internal enum PanelGlyph
    {
        Link,
        App,
        Tool,
        Clipboard,
        Note,
        Appearance,
        Settings,
        Keyboard
    }

    internal enum WindowButtonKind
    {
        Minimize,
        Maximize,
        Restore
    }

    internal sealed class AppearanceThemeTile : Control
    {
        private bool selected;
        private bool hot;

        public event EventHandler ThemeSelected;
        public readonly PanelTheme Theme;

        public AppearanceThemeTile(PanelTheme theme)
        {
            Theme = theme;
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleName = theme.Name;
            AccessibleDescription = theme.Description;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        }

        public bool Selected
        {
            get { return selected; }
            set
            {
                if (selected == value)
                {
                    return;
                }

                selected = value;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hot = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hot = false;
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Focus();
            if (ThemeSelected != null)
            {
                ThemeSelected(this, EventArgs.Empty);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Parent == null ? Theme.ContentBack : Parent.BackColor);

            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            Color border = selected ? Theme.NavSelectedBorder : hot ? Theme.Accent : Theme.Border;
            using (GraphicsPath path = NoNoPanelForm.RoundedRect(bounds, 12))
            using (SolidBrush brush = new SolidBrush(Theme.CardBack))
            using (Pen pen = new Pen(border, selected ? 2F : 1F))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            Rectangle preview = new Rectangle(14, 14, Width - 28, 54);
            using (GraphicsPath path = NoNoPanelForm.RoundedRect(preview, 8))
            using (SolidBrush brush = new SolidBrush(Theme.ContentBack))
            using (Pen pen = new Pen(Theme.Border))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            Rectangle previewNav = new Rectangle(preview.Left, preview.Top, 54, preview.Height);
            using (GraphicsPath path = LeftRoundedRect(previewNav, 8))
            using (SolidBrush brush = new SolidBrush(Theme.NavBack))
            {
                g.FillPath(brush, path);
            }

            Rectangle navItem = new Rectangle(preview.Left + 10, preview.Top + 13, 34, 11);
            using (GraphicsPath path = NoNoPanelForm.RoundedRect(navItem, 5))
            using (SolidBrush brush = new SolidBrush(Theme.NavSelected))
            {
                g.FillPath(brush, path);
            }

            Rectangle contentLine = new Rectangle(preview.Left + 70, preview.Top + 14, preview.Width - 88, 8);
            Rectangle contentBlock = new Rectangle(preview.Left + 70, preview.Top + 30, 68, 10);
            using (GraphicsPath path = NoNoPanelForm.RoundedRect(contentLine, 4))
            using (SolidBrush brush = new SolidBrush(Theme.Accent))
            {
                g.FillPath(brush, path);
            }

            using (GraphicsPath path = NoNoPanelForm.RoundedRect(contentBlock, 4))
            using (SolidBrush brush = new SolidBrush(Theme.AccentSoft))
            {
                g.FillPath(brush, path);
            }

            using (Font titleFont = new Font(Font.FontFamily, 10.2F, FontStyle.Bold))
            using (Font descFont = new Font(Font.FontFamily, 8.4F, FontStyle.Regular))
            {
                Rectangle titleRect = new Rectangle(14, 78, Width - 52, 22);
                Rectangle descRect = new Rectangle(14, 101, Width - 28, 19);
                TextRenderer.DrawText(g, Theme.Name, titleFont, titleRect, Theme.HeaderText, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, Theme.Description, descFont, descRect, Theme.MutedText, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }

            if (selected)
            {
                Rectangle check = new Rectangle(Width - 34, 78, 18, 18);
                using (GraphicsPath path = NoNoPanelForm.RoundedRect(check, 9))
                using (SolidBrush brush = new SolidBrush(Theme.Accent))
                {
                    g.FillPath(brush, path);
                }

                Point[] tick = new Point[]
                {
                    new Point(check.Left + 5, check.Top + 9),
                    new Point(check.Left + 8, check.Top + 12),
                    new Point(check.Left + 13, check.Top + 6)
                };
                using (Pen pen = new Pen(Theme.CardBack, 2F))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, tick);
                }
            }

            if (Focused)
            {
                Rectangle focus = new Rectangle(4, 4, Width - 9, Height - 9);
                ControlPaint.DrawFocusRectangle(g, focus, Theme.Text, Theme.CardBack);
            }
        }

        private static GraphicsPath LeftRoundedRect(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddLine(bounds.Left + radius, bounds.Top, bounds.Right, bounds.Top);
            path.AddLine(bounds.Right, bounds.Top, bounds.Right, bounds.Bottom);
            path.AddLine(bounds.Right, bounds.Bottom, bounds.Left + radius, bounds.Bottom);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class PetAppearanceTile : Control
    {
        private static readonly object PreviewCacheLock = new object();
        private static readonly Dictionary<string, Bitmap> PreviewCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private readonly PanelTheme panelTheme;
        private bool selected;
        private bool hot;

        public event EventHandler AppearanceSelected;
        public readonly PetAppearance Appearance;

        public PetAppearanceTile(PetAppearance appearance, PanelTheme panelTheme)
        {
            Appearance = appearance;
            this.panelTheme = panelTheme ?? PanelThemeStore.DefaultTheme;
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleName = appearance.Name;
            AccessibleDescription = appearance.Description;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        }

        public bool Selected
        {
            get { return selected; }
            set
            {
                if (selected == value)
                {
                    return;
                }

                selected = value;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hot = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hot = false;
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Focus();
            if (AppearanceSelected != null)
            {
                AppearanceSelected(this, EventArgs.Empty);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Parent == null ? panelTheme.ContentBack : Parent.BackColor);

            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            Color border = selected ? Appearance.GlowTint : hot ? panelTheme.Accent : panelTheme.Border;
            using (GraphicsPath path = NoNoPanelForm.RoundedRect(bounds, 12))
            using (SolidBrush brush = new SolidBrush(panelTheme.CardBack))
            using (Pen pen = new Pen(border, selected ? 2F : 1F))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            Rectangle preview = new Rectangle(14, 14, 78, 80);
            DrawPetPreview(g, preview);

            using (Font titleFont = new Font(Font.FontFamily, 10.2F, FontStyle.Bold))
            using (Font descFont = new Font(Font.FontFamily, 8.4F, FontStyle.Regular))
            {
                Rectangle titleRect = new Rectangle(104, 20, Width - 144, 22);
                Rectangle descRect = new Rectangle(104, 46, Width - 118, 42);
                TextRenderer.DrawText(g, Appearance.Name, titleFont, titleRect, panelTheme.HeaderText, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, Appearance.Description, descFont, descRect, panelTheme.MutedText, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }

            DrawColorDot(g, new Rectangle(105, 104, 16, 16), Appearance.BodyTint);
            DrawColorDot(g, new Rectangle(128, 104, 16, 16), Appearance.ScreenTint);
            DrawColorDot(g, new Rectangle(151, 104, 16, 16), Appearance.GlowTint);

            if (selected)
            {
                Rectangle check = new Rectangle(Width - 34, 20, 18, 18);
                using (GraphicsPath path = NoNoPanelForm.RoundedRect(check, 9))
                using (SolidBrush brush = new SolidBrush(Appearance.GlowTint))
                {
                    g.FillPath(brush, path);
                }

                Point[] tick = new Point[]
                {
                    new Point(check.Left + 5, check.Top + 9),
                    new Point(check.Left + 8, check.Top + 12),
                    new Point(check.Left + 13, check.Top + 6)
                };
                using (Pen pen = new Pen(panelTheme.CardBack, 2F))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, tick);
                }
            }

            if (Focused)
            {
                Rectangle focus = new Rectangle(4, 4, Width - 9, Height - 9);
                ControlPaint.DrawFocusRectangle(g, focus, panelTheme.Text, panelTheme.CardBack);
            }
        }

        private void DrawPetPreview(Graphics g, Rectangle bounds)
        {
            Bitmap preview = GetPetPreview(Appearance);
            float scale = Math.Min((float)bounds.Width / preview.Width, (float)bounds.Height / preview.Height);
            int width = Math.Max(1, (int)Math.Round(preview.Width * scale));
            int height = Math.Max(1, (int)Math.Round(preview.Height * scale));
            Rectangle destination = new Rectangle(
                bounds.Left + (bounds.Width - width) / 2,
                bounds.Top + (bounds.Height - height) / 2,
                width,
                height);

            InterpolationMode interpolationMode = g.InterpolationMode;
            PixelOffsetMode pixelOffsetMode = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (ImageAttributes attributes = new ImageAttributes())
            {
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(preview, destination, 0, 0, preview.Width, preview.Height, GraphicsUnit.Pixel, attributes);
            }
            g.InterpolationMode = interpolationMode;
            g.PixelOffsetMode = pixelOffsetMode;
        }

        private static Bitmap GetPetPreview(PetAppearance appearance)
        {
            string appearanceId = appearance == null ? PetAppearanceStore.DefaultAppearance.Id : appearance.Id;
            lock (PreviewCacheLock)
            {
                Bitmap preview;
                if (PreviewCache.TryGetValue(appearanceId, out preview))
                {
                    return preview;
                }

                using (Bitmap source = PetWindowIcon.LoadIdleFrame())
                {
                    preview = PetAppearanceRenderer.Apply(source, appearance);
                }

                PreviewCache[appearanceId] = preview;
                return preview;
            }
        }

        private static void DrawColorDot(Graphics g, Rectangle bounds, Color color)
        {
            using (SolidBrush brush = new SolidBrush(color))
            using (Pen pen = new Pen(Color.FromArgb(90, 0, 0, 0)))
            {
                g.FillEllipse(brush, bounds);
                g.DrawEllipse(pen, bounds);
            }
        }

    }

    internal sealed class SidebarNavButton : Button
    {
        private readonly PanelFeature feature;
        private PanelTheme theme;
        private bool selected;
        private bool hot;
        private bool pressed;

        public SidebarNavButton(PanelFeature feature)
        {
            this.feature = feature;
            theme = PanelThemeStore.DefaultTheme;
            Text = feature.Title + " " + feature.Subtitle;
            AccessibleName = feature.Title;
            AccessibleDescription = feature.Subtitle;
            Cursor = Cursors.Hand;
            TabStop = true;
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            FlatAppearance.BorderSize = 0;
            BackColor = theme.NavBack;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        }

        public void SetTheme(PanelTheme value)
        {
            theme = value ?? PanelThemeStore.DefaultTheme;
            BackColor = theme.NavBack;
            Invalidate();
        }

        public bool Selected
        {
            get { return selected; }
            set
            {
                if (selected == value)
                {
                    return;
                }

                selected = value;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hot = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hot = false;
            pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            base.OnMouseDown(mevent);
            if (mevent.Button == MouseButtons.Left)
            {
                pressed = true;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            base.OnMouseUp(mevent);
            pressed = false;
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics g = pevent.Graphics;
            g.Clear(Parent == null ? BackColor : Parent.BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle bounds = new Rectangle(0, 1, Width - 1, Height - 3);
            if (pressed)
            {
                bounds.Offset(0, 1);
            }

            Color fill = selected ? theme.NavSelected : hot ? theme.NavHot : theme.NavNormal;
            Color border = selected ? theme.NavSelectedBorder : hot ? theme.Border : Color.FromArgb(0, 0, 0, 0);
            Color title = selected ? theme.HeaderText : theme.Text;
            Color subtitle = selected ? theme.Text : theme.MutedText;
            Color glyph = selected ? theme.Accent : theme.Text;
            Color glyphFill = selected ? theme.AccentSoft : theme.CardBack;

            using (GraphicsPath path = NoNoPanelForm.RoundedRect(bounds, 13))
            using (SolidBrush brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
            }

            if (selected || hot)
            {
                using (GraphicsPath path = NoNoPanelForm.RoundedRect(bounds, 13))
                using (Pen pen = new Pen(border, selected ? 1.6F : 1F))
                {
                    g.DrawPath(pen, path);
                }
            }

            Rectangle glyphBox = new Rectangle(bounds.Left + 11, bounds.Top + 11, 34, 34);
            using (GraphicsPath path = NoNoPanelForm.RoundedRect(glyphBox, 10))
            using (SolidBrush brush = new SolidBrush(glyphFill))
            {
                g.FillPath(brush, path);
            }

            NoNoPanelForm.DrawGlyph(g, feature.Glyph, new Rectangle(glyphBox.Left + 4, glyphBox.Top + 4, 26, 26), glyph);

            using (Font titleFont = new Font(Font.FontFamily, 9.6F, FontStyle.Bold))
            using (Font subtitleFont = new Font(Font.FontFamily, 8.4F, FontStyle.Regular))
            {
                Rectangle titleRect = new Rectangle(bounds.Left + 54, bounds.Top + 10, bounds.Width - 64, 21);
                Rectangle subtitleRect = new Rectangle(bounds.Left + 54, bounds.Top + 31, bounds.Width - 64, 18);
                TextRenderer.DrawText(g, feature.Title, titleFont, titleRect, title, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, feature.Subtitle, subtitleFont, subtitleRect, subtitle, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }

        }
    }

    internal sealed class FeatureTile : Control
    {
        private bool selected;
        private bool hot;

        public event EventHandler FeatureSelected;
        public readonly PanelFeature Feature;

        public FeatureTile(PanelFeature feature)
        {
            Feature = feature;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            TabStop = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        }

        public bool Selected
        {
            get { return selected; }
            set
            {
                if (selected == value)
                {
                    return;
                }

                selected = value;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hot = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hot = false;
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Focus();
            if (FeatureSelected != null)
            {
                FeatureSelected(this, EventArgs.Empty);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = selected
                ? Color.FromArgb(229, 246, 252)
                : hot ? Color.FromArgb(250, 250, 250) : Color.White;
            Color border = selected
                ? Color.FromArgb(0, 153, 188)
                : hot ? Color.FromArgb(196, 196, 196) : Color.FromArgb(218, 218, 218);
            Color title = Color.FromArgb(32, 32, 32);
            Color subtitle = Color.FromArgb(96, 96, 96);
            Color glyph = selected ? Color.FromArgb(0, 142, 182) : Color.FromArgb(64, 64, 64);

            using (GraphicsPath path = NoNoPanelForm.RoundedRect(bounds, 12))
            using (SolidBrush brush = new SolidBrush(fill))
            using (Pen pen = new Pen(border, selected ? 1.8F : 1F))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            Rectangle glyphBox = new Rectangle(13, 13, 34, 34);
            using (GraphicsPath path = NoNoPanelForm.RoundedRect(glyphBox, 10))
            using (SolidBrush brush = new SolidBrush(selected ? Color.FromArgb(204, 242, 250) : Color.FromArgb(238, 238, 238)))
            {
                g.FillPath(brush, path);
            }

            NoNoPanelForm.DrawGlyph(g, Feature.Glyph, new Rectangle(15, 15, 30, 30), glyph);

            using (Font titleFont = new Font(Font.FontFamily, 10F, FontStyle.Bold))
            using (Font subtitleFont = new Font(Font.FontFamily, 8.5F, FontStyle.Regular))
            {
                TextRenderer.DrawText(g, Feature.Title, titleFont, new Rectangle(56, 12, Width - 66, 22), title, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, Feature.Subtitle, subtitleFont, new Rectangle(56, 35, Width - 66, 20), subtitle, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }

            if (Focused)
            {
                Rectangle focus = new Rectangle(3, 3, Width - 7, Height - 7);
                ControlPaint.DrawFocusRectangle(g, focus, title, fill);
            }
        }
    }

    internal static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "NoNo Standalone";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    string command = key.GetValue(RunValueName) as string;
                    return String.Equals(ExtractExecutablePath(command), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null)
                    {
                        throw new InvalidOperationException("无法打开当前用户的启动项注册表。");
                    }

                    key.SetValue(RunValueName, Quote(Application.ExecutablePath), RegistryValueKind.String);
                }

                return;
            }

            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key != null)
                {
                    key.DeleteValue(RunValueName, false);
                }
            }
        }

        private static string Quote(string path)
        {
            return "\"" + path + "\"";
        }

        private static string ExtractExecutablePath(string command)
        {
            if (String.IsNullOrWhiteSpace(command))
            {
                return String.Empty;
            }

            command = command.Trim();
            if (command[0] == '"')
            {
                int endQuote = command.IndexOf('"', 1);
                if (endQuote > 1)
                {
                    return command.Substring(1, endQuote - 1);
                }
            }

            return command.Trim('"');
        }
    }

    internal static class SelfTest
    {
        public static int Run()
        {
            try
            {
                PetAtlas atlas = new PetAtlas();
                string failure;
                if (!atlas.Validate(out failure))
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NoNo-Standalone.selftest.log"), failure ?? "unknown validation failure");
                    return 2;
                }

                foreach (AnimationState state in PetAtlas.States)
                {
                    for (int frameIndex = 0; frameIndex < state.FrameCount; frameIndex++)
                    {
                        using (Bitmap frame = atlas.GetFrame(state, frameIndex))
                        {
                            if (frame.Width != PetAtlas.CellWidth || frame.Height != PetAtlas.CellHeight)
                            {
                                return 3;
                            }

                            using (LayeredFrame layeredFrame = new LayeredFrame(frame))
                            {
                                if (layeredFrame.BitmapHandle == IntPtr.Zero || layeredFrame.Size != frame.Size)
                                {
                                    return 3;
                                }
                            }
                        }
                    }
                }

                CodexActivityMonitor monitor = new CodexActivityMonitor();
                CodexActivitySnapshot snapshot = monitor.ForcePoll();
                if (snapshot == null || String.IsNullOrEmpty(snapshot.StateId))
                {
                    return 4;
                }

                atlas.GetState(snapshot.StateId);

                if (!DesktopAgentSelfTest.Run())
                {
                    return 5;
                }

                if (!VoiceCommandRouter.RunSelfTest())
                {
                    return 6;
                }

                return 0;
            }
            catch
            {
                return 1;
            }
        }
    }

    internal static class NativeMethods
    {
        public const int WS_EX_LAYERED = 0x00080000;
        public const int CS_DROPSHADOW = 0x00020000;
        public const int WM_NCHITTEST = 0x0084;
        public const int HTCLIENT = 1;
        public const int HTTRANSPARENT = -1;
        private const int VK_RBUTTON = 0x02;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int WM_COMMAND = 0x0111;
        private const int MIN_ALL = 419;
        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const uint SHERB_NOCONFIRMATION = 0x00000001;
        private const uint SHERB_NOPROGRESSUI = 0x00000002;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(
            IntPtr hwnd,
            IntPtr hdcDst,
            ref Point pptDst,
            ref Size psize,
            IntPtr hdcSrc,
            ref Point pptSrc,
            int crKey,
            ref BlendFunction pblend,
            int dwFlags);

        public static bool IsRightButtonDown()
        {
            return (GetAsyncKeyState(VK_RBUTTON) & unchecked((short)0x8000)) != 0;
        }

        public static void ShowDesktop()
        {
            Exception shellError = null;
            object shell = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null)
                {
                    throw new InvalidOperationException("无法创建 Shell.Application。");
                }

                shell = Activator.CreateInstance(shellType);
                shellType.InvokeMember("MinimizeAll", BindingFlags.InvokeMethod, null, shell, null);
                return;
            }
            catch (Exception ex)
            {
                shellError = ex;
            }
            finally
            {
                if (shell != null && Marshal.IsComObject(shell))
                {
                    Marshal.ReleaseComObject(shell);
                }
            }

            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero)
            {
                SendMessage(taskbar, WM_COMMAND, new IntPtr(MIN_ALL), IntPtr.Zero);
                return;
            }

            throw new InvalidOperationException("无法触发 Windows 回桌面。", shellError);
        }

        public static int GetForegroundProcessId()
        {
            try
            {
                IntPtr foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero)
                {
                    return 0;
                }

                int processId;
                GetWindowThreadProcessId(foreground, out processId);
                return processId;
            }
            catch
            {
                return 0;
            }
        }

        public static bool AreDesktopIconsVisible()
        {
            IntPtr desktopView = FindDesktopView();
            if (desktopView == IntPtr.Zero)
            {
                return true;
            }

            IntPtr iconsView = FindDesktopIconsView(desktopView);
            return iconsView == IntPtr.Zero || IsWindowVisible(iconsView);
        }

        public static bool SetDesktopIconsVisible(bool visible)
        {
            IntPtr desktopView = FindDesktopView();
            if (desktopView == IntPtr.Zero)
            {
                return false;
            }

            IntPtr iconsView = FindDesktopIconsView(desktopView);
            if (iconsView == IntPtr.Zero)
            {
                return false;
            }

            // Older builds hid SHELLDLL_DefView itself, which can also hide the wallpaper.
            // Restore that container, then change only the desktop icon list child window.
            ShowWindow(desktopView, SW_SHOWNOACTIVATE);
            ShowWindow(iconsView, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
            return true;
        }

        public static bool AreTaskbarsVisible()
        {
            List<IntPtr> windows = FindTaskbarWindows();
            if (windows.Count == 0)
            {
                return true;
            }

            foreach (IntPtr window in windows)
            {
                if (IsWindowVisible(window))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool SetTaskbarsVisible(bool visible)
        {
            List<IntPtr> windows = FindTaskbarWindows();
            foreach (IntPtr window in windows)
            {
                ShowWindow(window, visible ? SW_SHOW : SW_HIDE);
            }

            return windows.Count > 0;
        }

        public static void EmptyRecycleBin()
        {
            int result = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI);
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }
        }

        private static IntPtr FindDesktopView()
        {
            IntPtr progman = FindWindow("Progman", null);
            IntPtr desktopView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (desktopView != IntPtr.Zero)
            {
                return desktopView;
            }

            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr window, IntPtr lParam)
            {
                IntPtr view = FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (view != IntPtr.Zero)
                {
                    found = view;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        private static IntPtr FindDesktopIconsView(IntPtr desktopView)
        {
            IntPtr iconsView = FindWindowEx(desktopView, IntPtr.Zero, "SysListView32", "FolderView");
            if (iconsView == IntPtr.Zero)
            {
                iconsView = FindWindowEx(desktopView, IntPtr.Zero, "SysListView32", null);
            }

            return iconsView;
        }

        private static List<IntPtr> FindTaskbarWindows()
        {
            List<IntPtr> windows = new List<IntPtr>();

            IntPtr mainTaskbar = FindWindow("Shell_TrayWnd", null);
            if (mainTaskbar != IntPtr.Zero)
            {
                windows.Add(mainTaskbar);
            }

            IntPtr secondaryTaskbar = IntPtr.Zero;
            while (true)
            {
                secondaryTaskbar = FindWindowEx(IntPtr.Zero, secondaryTaskbar, "Shell_SecondaryTrayWnd", null);
                if (secondaryTaskbar == IntPtr.Zero)
                {
                    break;
                }

                windows.Add(secondaryTaskbar);
            }

            return windows;
        }

        public static IntPtr CreateLayeredBitmapHandle(Bitmap bitmap)
        {
            IntPtr bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
            if (bitmapHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not create the layered bitmap handle.");
            }

            return bitmapHandle;
        }

        public static void DeleteLayeredBitmapHandle(IntPtr bitmapHandle)
        {
            if (bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(bitmapHandle);
            }
        }

        public static void UpdateLayeredBitmap(IntPtr handle, IntPtr bitmapHandle, Size size, Point location)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memoryDc = CreateCompatibleDC(screenDc);
            IntPtr oldBitmap = SelectObject(memoryDc, bitmapHandle);

            try
            {
                Point source = new Point(0, 0);
                BlendFunction blend = new BlendFunction();
                blend.BlendOp = AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AC_SRC_ALPHA;

                if (!UpdateLayeredWindow(handle, screenDc, ref location, ref size, memoryDc, ref source, 0, ref blend, ULW_ALPHA))
                {
                    throw new InvalidOperationException("UpdateLayeredWindow failed.");
                }
            }
            finally
            {
                SelectObject(memoryDc, oldBitmap);
                DeleteDC(memoryDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}
