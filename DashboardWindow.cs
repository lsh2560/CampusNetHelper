using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Legacy name kept for existing pages and tests; every value now comes from
// the single SurfaceColor palette so all pages share one material.
internal static class UiColor
{
    public static readonly Color Ink = SurfaceColor.Ink;
    public static readonly Color Muted = SurfaceColor.Muted;
    public static readonly Color Blue = SurfaceColor.Blue;
    public static readonly Color Green = SurfaceColor.Green;
    public static readonly Color Purple = SurfaceColor.Muted;
    public static readonly Color Canvas = SurfaceColor.Canvas;
    public static readonly Color Pale = SurfaceColor.Pale;
}

internal sealed class DiagnosisSnapshot
{
    internal string[] States, Details;
    internal string Summary, Latency, Loss, Advice;
}

internal sealed class CardPanel : Panel
{
    public int Radius = 12;
    public Color Fill = Color.White;
    public Color Stroke = SurfaceColor.Border;
    internal bool Refined;
    // Top-level page cards float on a soft shadow painted by the page; inner
    // wells (Fill != White) stay flat.
    internal bool Elevated { get { return Fill.ToArgb() == Color.White.ToArgb(); } }
    private Rectangle lastBounds;

    public CardPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = MakePath(new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), Radius))
        using (Brush fill = new SolidBrush(Fill))
        using (Pen pen = new Pen(Elevated ? Color.FromArgb(220, Stroke) : Stroke))
        {
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(pen, path);
        }
        base.OnPaint(e);
    }

    // The shadow lives on the parent, outside these bounds; refresh both the
    // old and new footprint whenever the card is laid out again.
    protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); RefreshShadow(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); RefreshShadow(); }
    private void RefreshShadow()
    {
        if (Parent == null || !Elevated) { lastBounds = Bounds; return; }
        if (!lastBounds.IsEmpty) Parent.Invalidate(Rectangle.Inflate(lastBounds, 10, 12), false);
        Parent.Invalidate(Rectangle.Inflate(Bounds, 10, 12), false);
        lastBounds = Bounds;
    }

    public static GraphicsPath MakePath(Rectangle r, int radius)
    {
        int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}


internal sealed partial class DashboardWindow : Form
{
    internal const string HomePage = "首页", GamePage = "游戏模式", CheckupPage = "体检与报告", SettingsPage = "设置";
    // Reached from Settings; it keeps the Settings item selected in the sidebar.
    internal const string ToolsPage = "高级工具";
    internal static readonly string[] NavigationPages = { HomePage, GamePage, CheckupPage, SettingsPage };
    internal const string AppName = "稳连";
    internal static string VersionText { get { return "v" + typeof(DashboardWindow).Assembly.GetName().Version.ToString(3); } }

    private readonly Panel sidebar = new BufferedPanel();
    private readonly Panel content = new BufferedPanel();
    private readonly Panel home = new BufferedPanel();
    private Panel otherPage = new BufferedPanel();
    private sealed class PageSurface
    {
        internal Panel Panel;
        internal Label Title, Body;
    }
    private readonly Dictionary<string, PageSurface> pageCache = new Dictionary<string, PageSurface>();
    private Size arrangedSize;
    private string arrangedPage;
    private bool building = true;
    private readonly bool startLiveServices;
    private bool waitingForPresentation, presentationQueued, liveServicesStarted;
    private bool firstPresentationComplete;
    private bool windowLayoutParked;
    private bool nativeRestoreDispatch;
    private Size nativeRestoreSize;
    private bool restoredPaintQueued;
    private SpeedSample lastSpeedSample;
    private WifiStatus lastWifi;
    private bool linkWireless;
    private int wifiRoamCount;
    private string wifiLastBssid;
    private WindowCaptionPanel captionBar;
    private const int ResizeBorder = 8;
    private SpeedupView speedup;
    internal Func<CampusStabilityMonitor> StabilityFactory = delegate { return new CampusStabilityMonitor(); };
    private AnimatedNavigationMenu navigation;
    private readonly List<Image> navigationImages = new List<Image>();
    private bool selectingNavigation;
    private readonly NetworkSpeedMonitor speedMonitor = new NetworkSpeedMonitor();
    private readonly System.Windows.Forms.Timer liveTimer = new System.Windows.Forms.Timer();
    private readonly Stopwatch refreshClock = Stopwatch.StartNew();
    private CardPanel hero, gameCard, eventsCard;
    private Label statusIcon, statusTitle, statusDetail;
    private Label pingText, heroDown, wifiValue, wifiCaption;
    private Label pageTitle, pageBody;
    private Label diagnosisSummary, diagnosisLatency, diagnosisLoss, diagnosisAdvice;
    private Label[] diagnosisStates, diagnosisDetails;
    private FlatAction diagnosisRun, diagnosisRetry;
    private DiagnosisSnapshot lastDiagnosis;
    private Label[] recentLines;
    private FlatAction loginButton, repairButton, gameButton;
    private string currentPage = HomePage;
    private bool updatingSettings;
    private NotifyIcon trayIcon;
    private bool exitingFromTray;
    private bool probing;
    private bool authenticating;
    private bool diagnosing;
    private bool diagnosisNetworkPending;
    private bool arranging;
    private long lastProbeMs = -60000, lastDetailsMs = -5000, lastLogMs = -10000, lastPingMs = -60000, lastWifiMs = -2000;
    private PortalState localPortal = PortalState.Unknown;
    private DateTime localObservedAt = DateTime.MinValue;
    private LinkState currentLinkState = LinkState.Disconnected;
    private string currentLinkIdentity = "";

    public DashboardWindow() : this(true) { }

    internal DashboardWindow(bool startServices)
    {
        startLiveServices = startServices;
        SuspendLayout();
        content.SuspendLayout(); sidebar.SuspendLayout(); home.SuspendLayout();
        AppUiTheme.Initialize();
        Opacity = 0; waitingForPresentation = true;
        try { UiMotion.Configure(AppSettings.Load().reduceAnimations); } catch { UiMotion.Configure(false); }
        Text = AppName + " · 校园网助手";
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Microsoft YaHei UI", 10.5f);
        BackColor = SurfaceColor.Canvas;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 640);
        Rectangle work = Screen.PrimaryScreen.WorkingArea;
        MaximizedBounds = work;
        Size = new Size(Math.Min(1280, Math.Max(1000, (int)(work.Width * .9))),
            Math.Min(820, Math.Max(640, (int)(work.Height * .9))));
        if (File.Exists(AppPaths.IconFile)) Icon = new Icon(AppPaths.IconFile);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Padding = new Padding(ResizeBorder);

        sidebar.Dock = DockStyle.Left;
        sidebar.Width = 220;
        sidebar.BackColor = Color.White;
        sidebar.Paint += PaintSidebar;
        Controls.Add(sidebar);
        BuildSidebar();

        content.Dock = DockStyle.Fill;
        content.BackColor = SurfaceColor.Canvas;
        Controls.Add(content);
        content.BringToFront();
        BuildTitleBar();
        home.BackColor = UiColor.Canvas;
        home.AutoScroll = true;
        UiSurface.AttachCardShadows(home);
        content.Controls.Add(home);
        BuildHome();
        otherPage.BackColor = UiColor.Canvas;
        otherPage.AutoScroll = true;
        otherPage.Visible = false;
        UiSurface.AttachCardShadows(otherPage);
        content.Controls.Add(otherPage);
        BuildOtherPage();

        content.Resize += delegate { ArrangePages(); if (!building && !arranging) PlaceActivePage(); };
        Resize += delegate
        {
            if (building) return;
            // Keep the existing opaque surface and styles while Windows performs
            // its native taskbar minimize/restore animation.
            if (WindowState == FormWindowState.Minimized) return;
            Padding next = WindowState == FormWindowState.Normal ? new Padding(ResizeBorder) : Padding.Empty;
            if (Padding != next) Padding = next;
            ArrangePages();
            PlaceActivePage();
            if (waitingForPresentation) QueuePresentation();
        };
        UpdateTrayIcon();
        FormClosing += DashboardFormClosing;
        FormClosed += delegate { if (startLiveServices) ConnectionMonitor.RequestStop(); liveTimer.Stop(); if (trayIcon != null) trayIcon.Dispose(); if (stabilityView != null) stabilityView.Stop(); foreach (Image image in navigationImages) image.Dispose(); };
        liveTimer.Interval = 1000;
        liveTimer.Tick += LiveTick;
        building = false;
        home.ResumeLayout(false); sidebar.ResumeLayout(false); content.ResumeLayout(false); ResumeLayout(true);
        ShowPage(HomePage);
    }

    protected override void OnShown(EventArgs e)
    {
        // Create/lay out the complete first surface before starting live work.
        using (new UiPaintBatch(this)) { ArrangePages(); base.OnShown(e); }
        QueuePresentation();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowBehavior.PaintClassBackground(this, SurfaceColor.Canvas);
    }

    protected override void SetVisibleCore(bool value)
    {
        if (value && !IsDisposed && !firstPresentationComplete) { waitingForPresentation = true; Opacity = 0; }
        base.SetVisibleCore(value);
        if (value && !building && IsHandleCreated && !firstPresentationComplete) QueuePresentation();
    }

    private void QueuePresentation()
    {
        if (presentationQueued || !IsHandleCreated || IsDisposed) return;
        presentationQueued = true;
        BeginInvoke((MethodInvoker)delegate {
            presentationQueued = false;
            if (IsDisposed || !Visible || WindowState == FormWindowState.Minimized) return;
            ArrangePages(); PlaceActivePage();
            // Paint the complete, already-created child tree while the window is
            // fully transparent, then expose it at full opacity in one step.
            // This is a readiness gate, never a fade or a timed entrance effect.
            UiPaintBatch.Render(this);
            Opacity = 1; waitingForPresentation = false; firstPresentationComplete = true;
            if (startLiveServices && !liveServicesStarted)
            {
                liveServicesStarted = true;
                BeginInvoke((MethodInvoker)delegate {
                    if (IsDisposed) return;
                    TryStartMonitor(); liveTimer.Start(); LiveTick(this, EventArgs.Empty);
                });
            }
        });
    }

    private static Label Label(string text, int size, bool bold, Color color, int x, int y, int width, int height)
    {
        Font font = new Font("Microsoft YaHei UI", Math.Max(9.5f, size), bold ? FontStyle.Bold : FontStyle.Regular);
        int lineHeight = TextRenderer.MeasureText("校园网", font).Height + 2;
        return new Label { Text = text, Font = font,
            ForeColor = color, BackColor = Color.Transparent, Location = new Point(x, y), Size = new Size(width, Math.Max(height, lineHeight)),
            AutoEllipsis = true, UseCompatibleTextRendering = false };
    }

    private static FlatAction Button(string text, Color fill, Color color, int x, int y, int width, int height)
    {
        bool primary = fill.ToArgb() == UiColor.Blue.ToArgb() || fill.ToArgb() == SurfaceColor.Blue.ToArgb();
        return new FlatAction { Text = text, BackColor = primary ? fill : Color.White, ForeColor = primary ? Color.White : UiColor.Ink, Location = new Point(x, y), Size = new Size(width, height),
            Refined = true, Radius = 8 };
    }

    private static CardPanel Card(Panel parent)
    {
        var card = new CardPanel { Refined = true, Radius = 12, Stroke = SurfaceColor.Border };
        parent.Controls.Add(card);
        return card;
    }

    // Card heading: a small tinted icon plate followed by the title.
    private static Label Heading(CardPanel card, UiSymbol symbol, string title)
    {
        card.Controls.Add(new UiIcon { Symbol = symbol, Bounds = new Rectangle(20, 18, 36, 36) });
        Label label = Label(title, 13, true, SurfaceColor.Ink, 68, 21, 300, 30);
        card.Controls.Add(label);
        return label;
    }

    private void PaintSidebar(object sender, PaintEventArgs e)
    {
        using (var brush = new SolidBrush(sidebar.BackColor))
            e.Graphics.FillRectangle(brush, sidebar.ClientRectangle);
        using (var pen = new Pen(SurfaceColor.Border))
            e.Graphics.DrawLine(pen, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height);
    }

    private void BuildSidebar()
    {
        sidebar.Controls.Add(new UiIcon { Name = "BrandMark", Symbol = UiSymbol.Wifi, Bounds = new Rectangle(22, 28, 44, 44) });
        sidebar.Controls.Add(Label(AppName, 17, true, UiColor.Ink, 78, 24, sidebar.Width - 90, 34));
        sidebar.Controls.Add(Label("校园网助手 · " + VersionText, 9, false, SurfaceColor.Muted, 79, 56, sidebar.Width - 90, 24));
        navigation = new AnimatedNavigationMenu {
            Bounds = new Rectangle(12, 112, sidebar.Width - 24, 300), Font = new Font("Microsoft YaHei UI", 11.5f),
            BackColor = sidebar.BackColor, ForeColor = Color.FromArgb(52, 64, 88), ForeActive = SurfaceColor.BlueHover,
            BackHover = Color.FromArgb(244, 246, 250), BackActive = SurfaceColor.BlueTint,
            Radius = 8, Gap = 18, itemMargin = 6, InlineIndent = 16, MouseRightCtrl = false
        };
        UiSymbol[] menuSymbols = { UiSymbol.Home, UiSymbol.Game, UiSymbol.Pulse, UiSymbol.Settings };
        for (int i = 0; i < NavigationPages.Length; i++)
        {
            Image icon = UiSymbols.CreateImage(menuSymbols[i], Color.FromArgb(88, 100, 124), 24);
            Image selected = UiSymbols.CreateImage(menuSymbols[i], SurfaceColor.Blue, 24);
            navigationImages.Add(icon); navigationImages.Add(selected);
            navigation.Items.Add(new AntdUI.MenuItem { Text = NavigationPages[i], ID = NavigationPages[i], Tag = NavigationPages[i], Icon = icon, IconActive = selected });
        }
        navigation.ItemClick += delegate(object sender, AntdUI.MenuItemEventArgs e) { if (!selectingNavigation) ShowPage(e.Item.Text); };
        sidebar.Controls.Add(navigation);
        Label motto = Label("适用于南华大学校园网", 9, false, UiColor.Muted, 16, 0, sidebar.Width - 32, 26);
        Label footer = Label("非官方工具 · 不上传任何数据", 9, false, SurfaceColor.Faint, 16, 0, sidebar.Width - 32, 24);
        motto.Name = "SidebarMotto"; footer.Name = "SidebarFooter";
        sidebar.Controls.Add(motto);
        sidebar.Controls.Add(footer);
        sidebar.Resize += delegate { ResizeSidebarContent(motto, footer); };
        ResizeSidebarContent(motto, footer);
    }

    private void BuildTitleBar()
    {
        var bar = captionBar = new WindowCaptionPanel { Name = "WindowTitleBar", Dock = DockStyle.Top, Height = 40, BackColor = SurfaceColor.Canvas };
        content.Controls.Add(bar);
        bar.BringToFront();
        Label close = new CaptionGlyph { Text = "×", ForeColor = UiColor.Ink, BackColor = Color.Transparent, Size = new Size(46, 38) };
        Label max = new CaptionGlyph { Text = "□", ForeColor = UiColor.Ink, BackColor = Color.Transparent, Size = new Size(46, 38) };
        Label min = new CaptionGlyph { Text = "−", ForeColor = UiColor.Ink, BackColor = Color.Transparent, Size = new Size(46, 38) };
        close.Name = "WindowClose"; close.AccessibleName = "关闭窗口";
        max.Name = "WindowMaximize"; max.AccessibleName = "最大化或还原窗口";
        min.Name = "WindowMinimize"; min.AccessibleName = "最小化窗口";
        foreach (Label item in new[] { min, max, close })
        {
            item.TextAlign = ContentAlignment.MiddleCenter;
            item.Cursor = Cursors.Hand;
            bar.Controls.Add(item);
        }
        bar.Resize += delegate
        {
            close.Left = bar.Width - 50; max.Left = bar.Width - 100; min.Left = bar.Width - 150;
        };
        close.Click += delegate { Close(); };
        max.Click += delegate { WindowBehavior.ToggleMaximize(this); };
        min.Click += delegate { WindowBehavior.Minimize(this); };
        foreach (Label item in new[] { min, max, close })
        {
            Label caption = item;
            caption.MouseEnter += delegate { caption.BackColor = caption == close ? Color.FromArgb(196, 43, 28) : Color.FromArgb(230, 234, 240); caption.ForeColor = caption == close ? Color.White : UiColor.Ink; };
            caption.MouseLeave += delegate { caption.BackColor = Color.Transparent; caption.ForeColor = UiColor.Ink; };
            caption.MouseDown += delegate { if (caption.BackColor.A == 255) caption.BackColor = caption == close ? Color.FromArgb(160, 34, 22) : Color.FromArgb(218, 223, 231); };
            caption.MouseUp += delegate { if (caption.BackColor.A == 255) caption.BackColor = caption == close ? Color.FromArgb(196, 43, 28) : Color.FromArgb(230, 234, 240); };
        }
    }

    private void UpdateWindowRegion()
    {
        Region = null;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = WindowBehavior.ApplyStyles(base.CreateParams);
            // Buffer the painted containers locally. Persistent whole-window
            // descendant buffering did not eliminate the observed DWM flash.
            return parameters;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x001A) UiMotion.Configure(UiMotion.Reduced); // WM_SETTINGCHANGE
        if (m.Msg == 0x0005 && (m.WParam.ToInt64() == 0 || m.WParam.ToInt64() == 2) && (windowLayoutParked || nativeRestoreDispatch))
        {
            // A state-only WINDOWPOS notification can precede the actual
            // restored rectangle. Do not lay out the legacy iconic bounds.
            if (windowLayoutParked && nativeRestoreSize.IsEmpty) { DefWndProc(ref m); return; }
            base.WndProc(ref m);
            if (windowLayoutParked) { windowLayoutParked = false; ResumeLayout(true); }
            ArrangePages(); PlaceActivePage();
            // Windows can capture the animation source as soon as WM_SIZE
            // returns. Finish the child tree here, inside that boundary.
            UiPaintBatch.PresentRestored(this);
            return;
        }
        if (m.Msg == WindowBehavior.SystemCommand && !building && firstPresentationComplete &&
            (((m.WParam.ToInt64() & 0xFFF0) == WindowBehavior.RestoreCommand && (IsIconic(Handle) || WindowState == FormWindowState.Maximized)) ||
             ((m.WParam.ToInt64() & 0xFFF0) == WindowBehavior.MaximizeCommand && WindowState != FormWindowState.Maximized)))
        {
            nativeRestoreDispatch = true;
            try { base.WndProc(ref m); }
            finally { nativeRestoreDispatch = false; }
            if (WindowState != FormWindowState.Minimized) FinishRestoredPaint();
            return;
        }
        // WM_WINDOWPOSCHANGED reaches Control before it updates managed bounds
        // and dispatches WM_SIZE. IsIconic already reflects the native state.
        // Keep the ready child tree at its last real size while minimized, so
        // dock/anchor layout cannot collapse it and rebuild it during restore.
        if (m.Msg == 0x0047 && !building && firstPresentationComplete)
        {
            bool iconic = IsIconic(Handle);
            if (iconic && !windowLayoutParked)
            {
                SuspendLayout(); windowLayoutParked = true;
            }
            // Unmaximizing uses the same saved-client-size path in WinForms.
            // Batch its one necessary reflow at the actual restored HWND size.
            if (!iconic && !IsZoomed(Handle) && WindowState == FormWindowState.Maximized && !windowLayoutParked)
            {
                SuspendLayout(); windowLayoutParked = true;
            }
            bool restoring = windowLayoutParked && !iconic;
            if (restoring)
            {
                var position = (NativeWindowPosition)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(NativeWindowPosition));
                if ((position.Flags & 0x0001) != 0 && WindowState == FormWindowState.Minimized)
                {
                    // Keep WinForms from applying MinimumSize to the temporary
                    // 160x28 native rectangle before the real resize arrives.
                    DefWndProc(ref m); return;
                }
                if ((position.Flags & 0x0001) == 0 && position.Width > 0 && position.Height > 0)
                    nativeRestoreSize = new Size(position.Width, position.Height);
                else
                {
                    NativeWindowRect native;
                    if (GetWindowRect(Handle, out native)) nativeRestoreSize = new Size(native.Right - native.Left, native.Bottom - native.Top);
                }
            }
            base.WndProc(ref m);
            if (restoring)
            {
                if (windowLayoutParked) { windowLayoutParked = false; ResumeLayout(true); }
                nativeRestoreSize = Size.Empty;
                ArrangePages(); PlaceActivePage();
                // Finish dirty child surfaces synchronously in this restore
                // dispatch, instead of leaving independent paints for later.
                if (!nativeRestoreDispatch) FinishRestoredPaint();
            }
            return;
        }
        if (WindowBehavior.HandleMessage(ref m)) return;
        if (m.Msg == 0x84)
        {
            long point = m.LParam.ToInt64();
            Point screen = new Point((short)(point & 0xffff), (short)((point >> 16) & 0xffff));
            Point p = PointToClient(screen);
            if (WindowState == FormWindowState.Normal)
            {
                bool left = p.X >= 0 && p.X < ResizeBorder, right = p.X >= Width - ResizeBorder && p.X < Width;
                bool top = p.Y >= 0 && p.Y < ResizeBorder, bottom = p.Y >= Height - ResizeBorder && p.Y < Height;
                int hit = top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 0;
                if (hit != 0) { m.Result = new IntPtr(hit); return; }
            }
            // Native caption fallback for top-level hit tests. The child caption
            // panel handles clicks and waits for actual movement before dragging.
            if (captionBar != null && captionBar.RectangleToScreen(captionBar.ClientRectangle).Contains(screen))
            { m.Result = new IntPtr(2); return; }
        }
        base.WndProc(ref m);
    }

    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        // Framework 4.8 can add caption/frame metrics to its saved client size
        // inside restore. Our WM_NCCALCSIZE makes the whole HWND client space.
        // Preserve the size Windows has just restored instead of growing and
        // reflowing the window a second time during that same dispatch.
        if (!nativeRestoreSize.IsEmpty && (specified & BoundsSpecified.Size) != 0)
        {
            width = nativeRestoreSize.Width; height = nativeRestoreSize.Height;
        }
        base.SetBoundsCore(x, y, width, height, specified);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (windowLayoutParked) { windowLayoutParked = false; ResumeLayout(false); }
        nativeRestoreSize = Size.Empty; restoredPaintQueued = false;
        base.OnHandleDestroyed(e);
    }

    private void FinishRestoredPaint()
    {
        // The complete surface was already painted inside WM_SIZE. Respect
        // any new focus/hover invalidations without erasing that surface again.
        UiPaintBatch.FlushPending(this);
        if (restoredPaintQueued || IsDisposed || !IsHandleCreated) return;
        restoredPaintQueued = true;
        // User32 can invalidate redirected child surfaces after the nested
        // restore messages return. Complete one coalesced paint after that
        // native call unwinds; there is no fade, timer or delayed page reveal.
        BeginInvoke((MethodInvoker)delegate {
            restoredPaintQueued = false;
            if (IsDisposed || !Visible || WindowState == FormWindowState.Minimized) return;
            UiPaintBatch.FlushPending(this);
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr window);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeWindowRect { internal int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeWindowPosition
    {
        internal IntPtr Window, InsertAfter;
        internal int X, Y, Width, Height, Flags;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeWindowRect bounds);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, int wparam, int lparam);

    private void BuildHome()
    {
        hero = Card(home);
        gameCard = Card(home);
        eventsCard = Card(home);

        statusIcon = new StatusEmblem { Name = "HomeStatusIcon", Text = "↻", ForeColor = SurfaceColor.Blue, BackColor = Color.White, TextAlign = ContentAlignment.MiddleCenter };
        hero.Controls.Add(statusIcon);
        statusTitle = Label("正在检查网络", 20, true, SurfaceColor.Ink, 104, 26, 400, 40);
        statusTitle.Name = "HomeStatusTitle";
        statusDetail = Label("正在读取校园网状态…", 10, false, SurfaceColor.Muted, 106, 66, 400, 26);
        statusDetail.Name = "HomeStatusDetail";
        hero.Controls.Add(statusTitle); hero.Controls.Add(statusDetail);
        loginButton = Button("立即检测", SurfaceColor.Blue, Color.White, 0, 34, 136, 42);
        loginButton.Name = "HomeDetect"; loginButton.Symbol = UiSymbol.Refresh;
        repairButton = Button("重新认证", Color.White, SurfaceColor.Ink, 0, 34, 124, 42);
        repairButton.Name = "HomeRepair"; repairButton.Symbol = UiSymbol.Wrench; repairButton.Visible = false;
        loginButton.Click += delegate { RunDetection(); };
        repairButton.Click += delegate { RunLogin(true); };
        hero.Controls.Add(loginButton); hero.Controls.Add(repairButton);
        InitializeComponent();

        gameCard.Controls.Add(new UiIcon { Symbol = UiSymbol.Game, Bounds = new Rectangle(20, 20, 44, 44) });
        Label gameTitle = Label("要打游戏？", 12, true, SurfaceColor.Ink, 78, 16, 400, 28);
        Label gameBody = Label("开启游戏模式，记录卡顿是无线、校园网还是你自己的下载造成的", 10, false, SurfaceColor.Muted, 79, 46, 500, 24);
        gameTitle.Name = "HomeGameTitle"; gameBody.Name = "HomeGameBody";
        gameCard.Controls.Add(gameTitle); gameCard.Controls.Add(gameBody);
        gameButton = Button("开启游戏模式", Color.White, SurfaceColor.Blue, 0, 22, 156, 40);
        gameButton.Name = "HomeGameMode"; gameButton.Symbol = UiSymbol.Play; gameButton.ForeColor = SurfaceColor.Blue;
        gameButton.Click += delegate { OpenGameMode(true); };
        gameCard.Controls.Add(gameButton);

        eventsCard.Controls.Add(new UiIcon { Symbol = UiSymbol.Clock, Disc = false, Tint = SurfaceColor.Muted, Bounds = new Rectangle(22, 21, 22, 22) });
        Label eventsTitle = Label("最近发生", 12, true, SurfaceColor.Ink, 54, 16, 200, 30);
        eventsTitle.Name = "HomeEventsTitle"; eventsCard.Controls.Add(eventsTitle);
        Label allLogs = new UiLink { Name = "HomeAllLogs", Text = "查看全部日志  ›", Font = new Font(UiFont.Family, 9.5f), Location = new Point(0, 19), Size = new Size(130, 28), TextAlign = ContentAlignment.MiddleRight, ForeColor = SurfaceColor.Blue };
        allLogs.Click += delegate { OpenLog(); };
        eventsCard.Controls.Add(allLogs);
        recentLines = new Label[5];
        for (int i = 0; i < recentLines.Length; i++)
        {
            recentLines[i] = new TimelineLabel { Text = i == 0 ? "还没有记录。登录、掉线和自动恢复都会显示在这里。" : "", Font = new Font("Microsoft YaHei UI", 9.5f), ForeColor = SurfaceColor.Muted, BackColor = Color.White, Bounds = new Rectangle(22, 56 + i * 30, 600, 30), AutoEllipsis = true };
            eventsCard.Controls.Add(recentLines[i]);
        }
    }

    private void ArrangePages()
    {
        if (building || hero == null || content.Width <= 0 || arranging || WindowState == FormWindowState.Minimized) return;
        if (arrangedSize == content.ClientSize && arrangedPage == currentPage) return;
        Point homeScroll = new Point(-home.AutoScrollPosition.X, -home.AutoScrollPosition.Y);
        Point pageScroll = new Point(-otherPage.AutoScrollPosition.X, -otherPage.AutoScrollPosition.Y);
        bool arrangeOther = currentPage != HomePage && currentPage != ToolsPage;
        arranging = true;
        home.SuspendLayout(); if (arrangeOther) otherPage.SuspendLayout();
        try
        {
        home.AutoScrollPosition = Point.Empty;
        if (arrangeOther) otherPage.AutoScrollPosition = Point.Empty;
        Size viewport = new Size(content.Width, Math.Max(0, content.Height - 40));
        home.Size = viewport;
        if (arrangeOther) otherPage.Size = viewport;
        if (speedup != null) speedup.Size = viewport;
        ArrangeHomeSurface();
        // The previous page can still be on screen while Home/Tools is being
        // prepared. Do not reflow it using the destination's layout.
        if (!arrangeOther) return;
        int pageWidth = Math.Max(1, content.Width - 40);
        if (pageTitle != null) pageTitle.SetBounds(22, 12, Math.Max(400, pageWidth - 20), 44);
        if (pageBody != null) pageBody.SetBounds(24, 58, Math.Max(400, pageWidth - 20), 28);
        if (currentPage == GamePage) { ArrangeGameSurface(); return; }
        if (currentPage == CheckupPage) { ArrangeCheckupSurface(); return; }
        // Settings: two columns when wide, one stacked column otherwise.
        bool widePage = pageWidth >= 960;
        CardPanel[] cards = otherPage.Controls.OfType<CardPanel>().ToArray();
        int stackY = 100;
        foreach (CardPanel card in cards.OrderBy(c => Int32.Parse(c.Name)).ThenBy(c => (c.Tag as string) == "left" ? 0 : (c.Tag as string) == "right" ? 1 : 2))
        {
            string placement = card.Tag as string;
            if (widePage && (placement == "left" || placement == "right"))
            {
                int columnWidth = (pageWidth - 16) / 2;
                card.Top = Int32.Parse(card.Name);
                card.Left = placement == "left" ? 20 : 20 + columnWidth + 16;
                card.Width = placement == "left" ? columnWidth : pageWidth - columnWidth - 16;
            }
            else
            {
                card.Left = 20;
                card.Width = pageWidth;
                if (widePage) card.Top = Int32.Parse(card.Name);
                else { card.Top = stackY; stackY += card.Height + 16; }
            }
            FitCardContents(card);
        }
        otherPage.AutoScrollMinSize = new Size(0, cards.Length == 0 ? 0 : cards.Max(c => c.Bottom) + 24);
        }
        finally
        {
            home.ResumeLayout(true); if (arrangeOther) otherPage.ResumeLayout(true);
            home.AutoScrollPosition = homeScroll; if (arrangeOther) otherPage.AutoScrollPosition = pageScroll;
            arrangedSize = content.ClientSize; arrangedPage = currentPage;
            arranging = false;
        }
    }

    private void BuildOtherPage()
    {
        pageTitle = Label("", 20, true, UiColor.Ink, 22, 12, 900, 44);
        pageBody = Label("", 10, false, UiColor.Muted, 24, 58, 900, 28);
        pageTitle.Name = "PageTitle"; pageBody.Name = "PageBody";
        otherPage.Controls.Add(pageTitle);
        otherPage.Controls.Add(pageBody);
    }

    private void ShowPage(string page)
    {
        int navigationIndex = -1;
        // Passwords are never kept in an invisible cached settings page.
        if (currentPage == SettingsPage && page != currentPage)
            foreach (AntdUI.Input input in otherPage.Controls.Cast<Control>().SelectMany(DescendantInputs))
                if (input.Name == "AccountPassword") input.Clear();
        string highlighted = page == ToolsPage ? SettingsPage : page;
        if (navigation != null) {
            for (int i = 0; i < navigation.Items.Count; i++)
                if (navigation.Items[i].Text == highlighted) {
                    selectingNavigation = true;
                    try { navigation.SelectIndex(i, false); } finally { selectingNavigation = false; }
                    navigationIndex = i;
                    break;
                }
        }
        using (new UiPaintBatch(content))
        {
            currentPage = page;
            if (page == ToolsPage && speedup == null)
            { speedup = new SpeedupView { Visible = false, Location = new Point(-10000, 40) }; content.Controls.Add(speedup); }
            if (page != HomePage && page != ToolsPage)
            {
                PageSurface surface;
                if (!pageCache.TryGetValue(page, out surface))
                {
                    Panel unused = otherPage;
                    if (!pageCache.Values.Any(entry => entry.Panel == unused)) { content.Controls.Remove(unused); unused.Dispose(); }
                    otherPage = new BufferedPanel { BackColor = UiColor.Canvas, AutoScroll = true, Visible = false,
                        Bounds = new Rectangle(-10000, 40, home.Width, home.Height) };
                    UiSurface.AttachCardShadows(otherPage);
                    otherPage.SuspendLayout(); content.Controls.Add(otherPage);
                    try
                    {
                        BuildOtherPage(); pageTitle.Text = page;
                        if (page == GamePage) BuildGamePage();
                        if (page == CheckupPage) BuildCheckupPage();
                        if (page == SettingsPage) BuildSettingsPage();
                        surface = new PageSurface { Panel = otherPage, Title = pageTitle, Body = pageBody };
                        pageCache.Add(page, surface);
                    }
                    finally { otherPage.ResumeLayout(true); }
                }
                otherPage = surface.Panel; pageTitle = surface.Title; pageBody = surface.Body;
                if (page == SettingsPage) SyncSettingsPage(true);
                if (page == CheckupPage) { RenderDiagnosisState(); RefreshDiagnosisNetwork(surface.Panel); }
                if (page == GamePage) ApplyWifi();
            }
            ArrangePages();
            PlaceActivePage();
            if (page == HomePage && startLiveServices)
            { ApplySpeedSample(); ApplyWifi(); RefreshRecentEvents(); RefreshStatus(); }
        }
        // Lazy construction can take longer than the transition itself. Start
        // after the new page is ready, so its first visit still shows the slide.
        if (navigationIndex >= 0) navigation.MoveIndicator(navigationIndex);
    }

    private void OpenGameMode(bool startRecording)
    {
        ShowPage(GamePage);
        if (startRecording && stabilityView != null && !stabilityView.Running) stabilityView.Start();
    }

    private void PlaceActivePage()
    {
        if (building || arranging || WindowState == FormWindowState.Minimized) return;
        Control destination = currentPage == HomePage ? (Control)home : currentPage == ToolsPage ? (Control)speedup : otherPage;
        if (destination == null || destination.IsDisposed) return;
        var pages = new List<Control> { home };
        pages.AddRange(pageCache.Values.Select(entry => (Control)entry.Panel));
        if (speedup != null) pages.Add(speedup);
        UiPageDeck.Activate(content, pages, destination, new Point(0, 40));
    }

    private static IEnumerable<AntdUI.Input> DescendantInputs(Control parent)
    {
        var input = parent as AntdUI.Input;
        if (input != null) yield return input;
        foreach (Control child in parent.Controls)
            foreach (var descendant in DescendantInputs(child)) yield return descendant;
    }

    private void InvalidatePage(string page)
    {
        PageSurface old;
        if (!pageCache.TryGetValue(page, out old)) return;
        content.Controls.Remove(old.Panel); pageCache.Remove(page); old.Panel.Dispose();
        arrangedPage = null;
    }

    private CardPanel PageCard(int y, int height)
    {
        CardPanel card = Card(otherPage);
        card.Name = y.ToString();
        card.Bounds = new Rectangle(20, y, Math.Max(1, otherPage.Width - 40), height);
        return card;
    }

    private CardPanel ColumnCard(int y, int height, string side)
    {
        CardPanel card = PageCard(y, height);
        card.Tag = side;
        return card;
    }

    private static void AddRule(Panel parent, int y, int width)
    {
        parent.Controls.Add(new Panel { Location = new Point(24, y), Size = new Size(width, 1), BackColor = SurfaceColor.Border });
    }

    private static ComboBox Choice(int x, int y, int width, string[] options)
    {
        var box = new UiCombo { Location = new Point(x, y), Width = width, Height = 36,
            Font = new Font("Microsoft YaHei UI", 10), ItemHeight = 28,
            BackColor = Color.White, ForeColor = UiColor.Ink, FlatStyle = FlatStyle.Standard };
        box.Items.AddRange(options);
        return box;
    }

    private static IEnumerable<Label> DescendantLabels(Control parent)
    {
        var label = parent as Label;
        if (label != null) yield return label;
        foreach (Control child in parent.Controls)
            foreach (Label descendant in DescendantLabels(child)) yield return descendant;
    }

    private void RefreshDiagnosisNetwork(Panel panel)
    {
        if (diagnosisNetworkPending || !IsHandleCreated) return;
        diagnosisNetworkPending = true;
        ThreadPool.QueueUserWorkItem(delegate {
            LinkSnapshot snapshot = null;
            WifiStatus wifi = null;
            try { snapshot = PortalProbe.Link(); if (snapshot.Wireless) wifi = WifiInfo.Current(); } catch { }
            Ui(delegate {
                diagnosisNetworkPending = false;
                if (panel.IsDisposed) return;
                bool ready = snapshot != null && snapshot.State == LinkState.Ready;
                foreach (Label label in DescendantLabels(panel))
                {
                    if (label.Name == "DiagnosisLink")
                    {
                        label.Text = snapshot == null ? "无法读取" : ready ? "●  已连接" : "●  未就绪";
                        label.ForeColor = ready ? UiColor.Green : UiColor.Muted;
                    }
                    if (label.Name == "DiagnosisAdapter") label.Text = "网卡　" + (snapshot == null ? "未知" : snapshot.AdapterName ?? "未连接");
                    if (label.Name == "DiagnosisIp") label.Text = "IP　　" + (snapshot == null ? "未获取" : snapshot.Ip ?? "未获取");
                    if (label.Name == "DiagnosisGateway") label.Text = "网关　" + (snapshot == null ? "未获取" : snapshot.Gateway ?? "未获取");
                    if (label.Name == "DiagnosisWifi")
                        label.Text = "Wi-Fi　" + (wifi == null ? (snapshot != null && ready && !snapshot.Wireless ? "有线连接" : "未连接") :
                            wifi.Ssid + " · " + CampusStabilityLog.Signal(wifi) + "（" + wifi.Grade + "）" + (wifi.Band == null ? "" : " · " + wifi.Band));
                }
            });
        });
    }

    private void BuildSettingsPage()
    {
        pageBody.Text = "账号、自动保持在线和其他选项";
        SettingsRecord s;
        try { s = AppSettings.Load(); }
        catch { s = new SettingsRecord { autoLogin = false, priorityMode = "Off", quickRecheck = true }; }
        SavedCredential saved = null;
        try { saved = CredentialStore.Read(); } catch { }

        CardPanel account = ColumnCard(100, 318, "left");
        Label accountHeading = Heading(account, UiSymbol.User, "校园网账号");
        bool hasAccount = saved != null && !String.IsNullOrEmpty(saved.username);
        var accountState = new StatePill { Name = "AccountSavedState", Text = hasAccount ? "已保存 · " + saved.username : "尚未保存",
            Font = new Font("Microsoft YaHei UI", 9.5f), ForeColor = hasAccount ? SurfaceColor.Green : SurfaceColor.Muted, BackColor = Color.White };
        accountState.Size = new Size(Math.Min(220, TextRenderer.MeasureText(accountState.Text, accountState.Font).Width + 40), 28);
        account.Controls.Add(accountState);
        account.Controls.Add(Label("账号", 10, true, SurfaceColor.Ink, 24, 82, 60, 28));
        AntdUI.Input user = AppUiTheme.Input(hasAccount ? saved.username : "");
        user.Name = "AccountUser"; user.AccessibleName = "校园网账号"; user.TabIndex = 0; user.SetBounds(88, 76, 300, 38);
        account.Controls.Add(user);
        account.Controls.Add(Label("密码", 10, true, SurfaceColor.Ink, 24, 134, 60, 28));
        AntdUI.Input password = AppUiTheme.Input("");
        password.Name = "AccountPassword"; password.AccessibleName = "校园网密码"; password.TabIndex = 1; password.UseSystemPasswordChar = true;
        password.SetBounds(88, 128, 300, 38);
        account.Controls.Add(password);
        Label passwordHint = Label(hasAccount ? "留空表示不修改密码" : "第一次保存需要填写密码", 9, false, SurfaceColor.Muted, 90, 170, 300, 24);
        passwordHint.Name = "AccountPasswordHint"; account.Controls.Add(passwordHint);
        Label carrierHint = Label("运营商线路在开户时与账号绑定，软件无法更改", 9, false, SurfaceColor.Muted, 24, 200, 400, 24);
        carrierHint.Name = "AccountCarrierHint"; account.Controls.Add(carrierHint);
        FlatAction test = Button("保存并测试登录", SurfaceColor.Blue, Color.White, 24, 246, 200, 44);
        FlatAction save = Button("保存", Color.White, SurfaceColor.Blue, 236, 246, 120, 44);
        test.Name = "AccountSaveAndTest"; test.TabIndex = 2; save.Name = "AccountSave"; save.TabIndex = 3; save.ForeColor = SurfaceColor.Blue;
        test.Click += delegate { if (SaveAccountPage(user, password)) RunLogin(false); };
        save.Click += delegate { SaveAccountPage(user, password); };
        account.Controls.Add(test); account.Controls.Add(save);
        account.Resize += delegate
        {
            int field = Math.Max(160, account.Width - 112);
            user.Width = password.Width = field;
            accountState.Location = new Point(Math.Max(accountHeading.Left + 120, account.Width - accountState.Width - 22), 24);
            accountHeading.Width = Math.Max(80, accountState.Left - accountHeading.Left - 8);
            int buttons = account.Width - 48;
            test.SetBounds(24, 246, (int)(buttons * .62), 44); save.SetBounds(test.Right + 12, 246, buttons - test.Width - 12, 44);
        };

        CardPanel online = ColumnCard(100, 318, "right");
        Heading(online, UiSymbol.Shield, "自动保持在线");
        AddSettingsSwitch(online, 70, "Settings_KeepOnline", "自动保持在线", "开机自动登录，掉线后自动重新认证", s.autoLogin || s.priorityMode != "Off",
            delegate(bool value) { SaveKeepOnline(value, null, null, null); }, true);
        Label advanced = Label("高级选项", 9, true, SurfaceColor.Muted, 24, 156, 200, 24);
        advanced.Name = "SettingsAdvancedLabel"; online.Controls.Add(advanced);
        online.Controls.Add(Label("恢复时机", 10, false, SurfaceColor.Ink, 24, 192, 100, 28));
        ComboBox mode = Choice(130, 186, 260, new[] { "智能（锁屏或长时间不用时暂停）", "始终（锁屏时也恢复）" });
        mode.Name = "SettingsMode"; mode.SelectedIndex = s.priorityMode == "Preferred" ? 1 : 0;
        mode.SelectedIndexChanged += delegate { if (!updatingSettings) SaveKeepOnline(null, mode.SelectedIndex, null, null); };
        online.Controls.Add(mode);
        online.Controls.Add(Label("检测间隔", 10, false, SurfaceColor.Ink, 24, 236, 100, 28));
        ComboBox interval = Choice(130, 230, 260, new[] { "30 秒", "45 秒（默认）", "60 秒" });
        interval.Name = "SettingsInterval";
        int seconds = s.checkIntervalSeconds ?? 45;
        interval.SelectedIndex = seconds == 30 ? 0 : seconds == 60 ? 2 : 1;
        interval.SelectedIndexChanged += delegate { if (!updatingSettings) SaveKeepOnline(null, null, interval.SelectedIndex == 0 ? 30 : interval.SelectedIndex == 2 ? 60 : 45, null); };
        online.Controls.Add(interval);
        var quick = new UiCheck { Name = "SettingsQuickRecheck", Text = "网络变化后马上复查", Checked = s.quickRecheck != false, Location = new Point(24, 276), Size = new Size(300, 30),
            Font = new Font("Microsoft YaHei UI", 10), ForeColor = SurfaceColor.Ink, AccessibleName = "网络变化后马上复查" };
        quick.CheckedChanged += delegate { if (!updatingSettings) SaveKeepOnline(null, null, null, quick.Checked); };
        online.Controls.Add(quick);
        online.Resize += delegate { mode.Width = interval.Width = Math.Max(160, online.Width - 154); };

        CardPanel general = ColumnCard(434, 236, "left");
        Heading(general, UiSymbol.Settings, "常规");
        AddSettingsSwitch(general, 70, "Settings_Tray", "关闭窗口时留在托盘", "开启后点 × 不退出，在右下角托盘里继续运行", s.minimizeToTray,
            delegate(bool value)
            {
                try
                {
                    SettingsRecord next = AppSettings.Load(); next.minimizeToTray = value;
                    AppSettings.Save(next); UpdateTrayIcon(); SyncSettingsPage(false);
                }
                catch (Exception ex) { ShowNotice("保存失败：" + ex.Message); SyncSettingsPage(false); }
            }, true);
        AddSettingsSwitch(general, 148, "Settings_Animation", "减少界面动画", "关闭切换动画，操作反馈仍然保留", s.reduceAnimations,
            delegate(bool value) {
                try { SettingsRecord next = AppSettings.Load(); next.reduceAnimations = value; AppSettings.Save(next); UiMotion.Configure(value); }
                catch (Exception ex) { ShowNotice("保存失败：" + ex.Message); SyncSettingsPage(false); }
            }, false);

        CardPanel tools = ColumnCard(434, 236, "right");
        Heading(tools, UiSymbol.Tools, "高级工具");
        tools.Controls.Add(Label("镜像测速与换源、多连接下载、DNS 设置。适合写代码、经常下载大文件的同学。", 10, false, SurfaceColor.Muted, 24, 70, 420, 50));
        FlatAction openTools = Button("打开高级工具", Color.White, SurfaceColor.Blue, 24, 132, 200, 42);
        openTools.Name = "SettingsOpenTools"; openTools.ForeColor = SurfaceColor.Blue; openTools.Symbol = UiSymbol.External;
        openTools.Click += delegate { ShowPage(ToolsPage); };
        tools.Controls.Add(openTools);

        CardPanel about = PageCard(686, 196);
        Heading(about, UiSymbol.Info, "关于" + AppName);
        about.Controls.Add(Label(AppName + " " + VersionText + " · 学生开发的非官方工具，适用于南华大学校园网。", 10, false, SurfaceColor.Ink, 24, 66, 900, 26));
        about.Controls.Add(Label("密码用 Windows 自带的加密保存在本机；不上传任何数据；只访问学校认证服务器，不改网卡和 MAC。", 10, false, SurfaceColor.Muted, 24, 94, 900, 26));
        Label dataDir = Label("数据目录：" + AppPaths.Data, 9, false, SurfaceColor.Faint, 24, 120, 900, 24);
        dataDir.Name = "SettingsDataDir"; about.Controls.Add(dataDir);
        FlatAction logs = Button("查看运行日志", Color.White, SurfaceColor.Ink, 24, 140, 150, 40);
        logs.Name = "SettingsOpenLogs"; logs.Click += delegate { OpenLog(); };
        FlatAction checkEnvironment = Button("运行环境自检", Color.White, SurfaceColor.Ink, 186, 140, 150, 40);
        checkEnvironment.Name = "SettingsEnvironmentCheck";
        checkEnvironment.Click += delegate
        {
            checkEnvironment.Enabled = false; checkEnvironment.Busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string result;
                try { result = EnvironmentCheck.Run(true).Format(); }
                catch (Exception ex) { result = "环境自检失败：" + ex.Message; }
                Ui(delegate { if (!checkEnvironment.IsDisposed) { checkEnvironment.Busy = false; checkEnvironment.Enabled = true; ShowNotice(result); } });
            });
        };
        FlatAction portal = Button("打开认证页", Color.White, SurfaceColor.Ink, 348, 140, 150, 40);
        portal.Name = "SettingsOpenPortal"; portal.Symbol = UiSymbol.External; portal.Click += delegate { OpenPortal(); };
        about.Controls.Add(logs); about.Controls.Add(checkEnvironment); about.Controls.Add(portal);
        foreach (FlatAction button in new[] { logs, checkEnvironment, portal }) button.Top = 146;
    }

    private bool SaveAccountPage(AntdUI.Input account, AntdUI.Input password)
    {
        try
        {
            string username = account.Text.Trim();
            if (username.Length == 0) throw new InvalidOperationException("请填写校园网账号。");
            SettingsRecord previous = AppSettings.Load();
            string encryptedBackup = File.Exists(AppPaths.Secret) ? File.ReadAllText(AppPaths.Secret, Encoding.UTF8) : null;
            try
            {
                if (password.Text.Length > 0) CredentialStore.Save(username, password.Text);
                else CredentialStore.SaveKeepingPassword(username);
                StartupManager.Sync(previous);
            }
            catch
            {
                try
                {
                    if (encryptedBackup == null) { if (File.Exists(AppPaths.Secret)) File.Delete(AppPaths.Secret); }
                    else AtomicFile.Write(AppPaths.Secret, encryptedBackup);
                    StartupManager.Sync(previous);
                }
                catch { }
                throw;
            }
            password.Clear();
            AppLog.Write("校园网账号已保存");
            ShowNotice("账号已保存，密码已加密保存在本机。");
            InvalidatePage(SettingsPage); ShowPage(SettingsPage);
            return true;
        }
        catch (Exception ex) { ShowNotice("保存失败：" + ex.Message); return false; }
    }

    private void AddSettingsSwitch(CardPanel card, int y, string name, string title, string subtitle, bool enabled, Action<bool> changed, bool rule)
    {
        Label heading = Label(title, 11, true, UiColor.Ink, 24, y + 2, 370, 28);
        Label detail = Label(subtitle, 9, false, UiColor.Muted, 24, y + 30, 430, 24);
        card.Controls.Add(heading); card.Controls.Add(detail);
        var toggle = new ModernSwitch { Name = name, Location = new Point(461, y + 12), Value = enabled, AccessibleName = title, Fill = SurfaceColor.Blue };
        toggle.Changed += delegate { if (!updatingSettings) changed(toggle.Value); };
        card.Controls.Add(toggle);
        Panel separator = null;
        if (rule) { separator = new Panel { Location = new Point(24, y + 66), Size = new Size(520, 1), BackColor = SurfaceColor.Border }; card.Controls.Add(separator); }
        card.Resize += delegate {
            toggle.Left = card.Width - 82;
            heading.Width = detail.Width = Math.Max(120, toggle.Left - 40);
            if (separator != null) separator.Width = card.Width - 48;
        };
    }

    // One switch drives both the sign-in launch and the recovery monitor; the
    // advanced options only tune how recovery behaves while it is on.
    private void SaveKeepOnline(bool? enabled, int? mode, int? interval, bool? quick)
    {
        if (updatingSettings) return;
        try
        {
            SettingsRecord previous = AppSettings.Load();
            bool on = enabled ?? (previous.autoLogin || previous.priorityMode != "Off");
            // The recovery timing is stored inside priorityMode, which is "Off"
            // while the switch is off; keep the choice on screen until it is on.
            if (!on && enabled == null && mode.HasValue) return;
            if (on && !File.Exists(AppPaths.Secret))
                throw new InvalidOperationException("请先在左边保存校园网账号和密码。");
            if (!mode.HasValue) mode = SettingsChoice("SettingsMode");
            string chosen = mode.HasValue ? (mode.Value == 1 ? "Preferred" : "Smart") : (previous.priorityMode == "Preferred" ? "Preferred" : "Smart");
            var next = new SettingsRecord { autoLogin = on, priorityMode = on ? chosen : "Off",
                quickRecheck = quick ?? (previous.quickRecheck != false), checkIntervalSeconds = interval ?? previous.checkIntervalSeconds,
                carrier = previous.carrier, minimizeToTray = previous.minimizeToTray, reduceAnimations = previous.reduceAnimations };
            AppSettings.Save(next);
            try
            {
                StartupManager.Sync(next);
                if (on && startLiveServices) StartupManager.StartBackground();
            }
            catch
            {
                AppSettings.Save(previous);
                try { StartupManager.Sync(previous); } catch { }
                throw;
            }
            AppLog.Write(on ? "自动保持在线已开启（" + (next.priorityMode == "Preferred" ? "始终" : "智能") + "，每 " + (next.checkIntervalSeconds ?? 45) + " 秒检查）" : "自动保持在线已关闭");
        }
        catch (Exception ex) { ShowNotice("设置失败：" + ex.Message); }
        SyncSettingsPage(true);
        RefreshStatus();
    }

    private int? SettingsChoice(string name)
    {
        PageSurface surface;
        if (!pageCache.TryGetValue(SettingsPage, out surface)) return null;
        ComboBox choice = surface.Panel.Controls.Cast<Control>().SelectMany(card => card.Controls.OfType<ComboBox>()).FirstOrDefault(c => c.Name == name);
        return choice == null || choice.SelectedIndex < 0 ? (int?)null : choice.SelectedIndex;
    }

    private void SyncSettingsPage(bool refreshStrategy)
    {
        PageSurface surface;
        if (!pageCache.TryGetValue(SettingsPage, out surface)) return;
        bool previousUpdating = updatingSettings; updatingSettings = true;
        try
        {
            SettingsRecord settings = AppSettings.Load();
            foreach (Control card in surface.Panel.Controls)
            {
                foreach (ModernSwitch toggle in card.Controls.OfType<ModernSwitch>())
                {
                    if (toggle.Name == "Settings_KeepOnline") toggle.Value = settings.autoLogin || settings.priorityMode != "Off";
                    if (toggle.Name == "Settings_Tray") toggle.Value = settings.minimizeToTray;
                    if (toggle.Name == "Settings_Animation") toggle.Value = settings.reduceAnimations;
                }
                foreach (CheckBox check in card.Controls.OfType<CheckBox>().Where(c => c.Name == "SettingsQuickRecheck"))
                    check.Checked = settings.quickRecheck != false;
                if (refreshStrategy) foreach (ComboBox choice in card.Controls.OfType<ComboBox>())
                {
                    if (choice.Name == "SettingsMode" && settings.priorityMode != "Off") choice.SelectedIndex = settings.priorityMode == "Preferred" ? 1 : 0;
                    if (choice.Name == "SettingsInterval") choice.SelectedIndex = settings.checkIntervalSeconds == 30 ? 0 : settings.checkIntervalSeconds == 60 ? 2 : 1;
                }
            }
            UiMotion.Configure(settings.reduceAnimations);
        }
        catch (Exception ex) { ShowNotice("设置无法读取：" + ex.Message); }
        finally { updatingSettings = previousUpdating; }
    }

    private void UpdateTrayIcon()
    {
        bool enabled = false;
        try { enabled = AppSettings.Load().minimizeToTray; } catch { }
        if (!enabled)
        {
            if (trayIcon != null) { trayIcon.Dispose(); trayIcon = null; }
            return;
        }
        if (trayIcon != null) return;
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开" + AppName, null, delegate { ShowFromTray(); });
        menu.Items.Add("退出", null, delegate
        {
            exitingFromTray = true;
            ShowFromTray();
            Close();
        });
        trayIcon = new NotifyIcon { Text = AppName + " · 校园网助手", Icon = Icon ?? SystemIcons.Application,
            ContextMenuStrip = menu, Visible = true };
        trayIcon.DoubleClick += delegate { ShowFromTray(); };
    }

    private void ShowFromTray()
    {
        WindowBehavior.RestoreAndActivate(this);
    }

    private void DashboardFormClosing(object sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !exitingFromTray && trayIcon != null)
        {
            e.Cancel = true;
            Hide();
            AppLog.Write("主界面已最小化到系统托盘");
        }
    }

    private void TryStartMonitor()
    {
        try
        {
            if (AppSettings.ParseMode(AppSettings.Load().priorityMode) != PriorityMode.Off)
                StartupManager.StartBackground();
        }
        catch (Exception ex) { AppLog.Write("后台监控未启动：" + ex.Message); }
    }

    private void LiveTick(object sender, EventArgs e)
    {
        try
        {
            SpeedSample sample = speedMonitor.Read();
            lastSpeedSample = sample;
            if (stabilityView != null) stabilityView.Monitor.SetTraffic(sample.AdapterName, sample.Download, sample.Upload);
            ApplySpeedSample();
        }
        catch { if (HomeUiActive) heroDown.Text = "暂不可用"; }
        long elapsed = refreshClock.ElapsedMilliseconds;
        if (elapsed - lastDetailsMs >= 5000) {
            lastDetailsMs = elapsed; RefreshNetworkDetails();
            PageSurface checkup;
            if (currentPage == CheckupPage && pageCache.TryGetValue(currentPage, out checkup)) RefreshDiagnosisNetwork(checkup.Panel);
        }
        if (elapsed - lastWifiMs >= 2000) { lastWifiMs = elapsed; RefreshWifi(); }
        if (elapsed - lastLogMs >= 10000) { lastLogMs = elapsed; RefreshRecentEvents(); }
        if (currentLinkState == LinkState.Ready && elapsed - lastPingMs >= 30000)
        {
            lastPingMs = elapsed;
            RunPing("210.43.112.9", false);
        }
        RefreshStatus();
        try
        {
            if (AppSettings.ParseMode(AppSettings.Load().priorityMode) == PriorityMode.Off &&
                !probing && elapsed - lastProbeMs >= 60000)
            {
                lastProbeMs = elapsed;
                ProbeInBackground(false);
            }
        }
        catch { }
    }

    private bool HomeUiActive { get { return currentPage == HomePage && Visible && WindowState != FormWindowState.Minimized && !windowLayoutParked && !nativeRestoreDispatch && !restoredPaintQueued; } }

    private void ApplySpeedSample()
    {
        if (!HomeUiActive || lastSpeedSample == null) return;
        string down = NetworkSpeedMonitor.Format(lastSpeedSample.Download);
        if (heroDown.Text != down) heroDown.Text = down;
    }

    private void RefreshWifi()
    {
        WifiStatus wifi = null;
        try { if (linkWireless) wifi = WifiInfo.Current(); } catch { }
        if (wifi != null && wifiLastBssid != null && wifi.Bssid != wifiLastBssid) wifiRoamCount++;
        if (wifi != null) wifiLastBssid = wifi.Bssid;
        lastWifi = wifi;
        ApplyWifi();
    }

    private void ApplyWifi()
    {
        if (HomeUiActive)
        {
            string value, caption;
            if (currentLinkState == LinkState.Ready && !linkWireless) { value = "有线"; caption = "网络连接"; }
            else if (lastWifi == null) { value = "—"; caption = "Wi-Fi 信号"; }
            else { value = CampusStabilityLog.Signal(lastWifi); caption = "Wi-Fi 信号 · " + lastWifi.Grade; }
            if (wifiValue.Text != value) wifiValue.Text = value;
            if (wifiCaption.Text != caption) wifiCaption.Text = caption;
        }
        if (currentPage == GamePage) ApplyGameWifi();
    }

    private void RefreshNetworkDetails()
    {
        try
        {
            LinkSnapshot link = PortalProbe.Link();
            currentLinkState = link.State;
            linkWireless = link.Wireless;
            if (currentLinkIdentity != link.Identity)
            {
                currentLinkIdentity = link.Identity;
                localPortal = PortalState.Unknown;
                localObservedAt = DateTime.MinValue;
                lastProbeMs = -60000;
            }
        }
        catch { currentLinkState = LinkState.Disconnected; }
    }

    private void RefreshStatus()
    {
        bool monitoring;
        try { monitoring = AppSettings.ParseMode(AppSettings.Load().priorityMode) != PriorityMode.Off; }
        catch { monitoring = false; }
        string keep = monitoring ? "自动保持在线已开启" : "自动保持在线未开启";
        if (currentLinkState != LinkState.Ready)
        {
            SetStatus("disconnected", "网络未连接",
                currentLinkState == LinkState.NoIp ? "还没有取得 IP 地址，稍等一下或重新连接 Wi-Fi" : "Wi-Fi 或网线没有连接");
            return;
        }
        MonitorStatusRecord monitorStatus = monitoring ? MonitorStatusStore.Read() : null;
        bool freshLocal = localObservedAt != DateTime.MinValue &&
            DateTime.Now - localObservedAt < TimeSpan.FromSeconds(60) &&
            (monitorStatus == null || localObservedAt.ToUniversalTime().Ticks > monitorStatus.updatedUtc);
        if (monitoring && !freshLocal)
        {
            MonitorStatusRecord status = monitorStatus;
            if (status == null || status.updatedUtc <= 0 ||
                DateTime.UtcNow.Ticks - status.updatedUtc > TimeSpan.FromSeconds(100).Ticks)
            {
                SetStatus("checking", "正在检查网络", "稍等几秒 · " + keep);
                return;
            }
            if (status.state == "online")
            {
                string since = "";
                if (status.onlineSinceUtc > 0)
                {
                    TimeSpan seen = DateTime.UtcNow - new DateTime(status.onlineSinceUtc, DateTimeKind.Utc);
                    since = seen.TotalHours >= 1 ? "已在线 " + (int)seen.TotalHours + " 小时 " + seen.Minutes + " 分 · " : "已在线 " + Math.Max(1, (int)seen.TotalMinutes) + " 分钟 · ";
                }
                SetStatus("online", StatusHeading("online"), since + keep);
                return;
            }
            SetStatus(status.state, StatusHeading(status.state), FriendlyMonitorMessage(status) + " · " + keep);
            return;
        }
        string state = localPortal == PortalState.Online ? "online" : localPortal == PortalState.AuthenticationRequired ? "auth-required" : "unknown";
        SetStatus(state, StatusHeading(state), (state == "online" ? "校园网已认证" :
            state == "auth-required" ? "校园网认证已失效，点“重新认证”" :
            localObservedAt == DateTime.MinValue ? "点“立即检测”确认状态" : "暂时无法确认认证状态") + " · " + keep);
    }

    private static string FriendlyMonitorMessage(MonitorStatusRecord status)
    {
        switch (status.state)
        {
            case "auth-required": return "校园网认证已失效";
            case "recovering": return "正在重新认证";
            case "confirming": return "认证页要求登录，正在再确认一次";
            case "paused": return status.message == "电脑已锁屏" ? "电脑已锁屏，暂停自动恢复" : "长时间没有操作，暂停自动恢复";
            case "limited": return "10 分钟内已自动恢复 3 次，稍后再试";
            case "disconnected": return "网络没有连接";
            default: return "暂时无法确认认证状态";
        }
    }

    private static string StatusHeading(string state)
    {
        if (state == "online") return "网络正常";
        if (state == "auth-required") return "需要重新认证";
        if (state == "recovering") return "正在自动恢复";
        if (state == "confirming") return "正在确认状态";
        if (state == "paused") return "自动恢复已暂停";
        if (state == "disconnected") return "网络未连接";
        if (state == "limited") return "自动恢复暂停中";
        if (state == "checking") return "正在检查网络";
        return "状态待确认";
    }

    private void SetStatus(string state, string title, string detail)
    {
        if (!HomeUiActive) return;
        if (statusTitle.Text != title) statusTitle.Text = title;
        if (statusDetail.Text != detail) statusDetail.Text = detail;
        bool good = state == "online";
        bool warn = state == "auth-required" || state == "limited";
        statusDetail.ForeColor = SurfaceColor.Muted;
        string glyph = good ? "✓" : state == "recovering" || state == "checking" || state == "confirming" ? "↻" : "!";
        if (statusIcon.Text != glyph) statusIcon.Text = glyph;
        statusIcon.ForeColor = good ? UiColor.Green : warn ? Color.FromArgb(138, 92, 18) : state == "disconnected" ? SurfaceColor.Muted : UiColor.Blue;
        bool showRepair = warn || state == "unknown" && localObservedAt != DateTime.MinValue;
        if (repairButton.Visible != showRepair) { repairButton.Visible = showRepair; LayoutHeroText(); }
    }

    private void RefreshRecentEvents()
    {
        if (!HomeUiActive) return;
        try
        {
            if (!File.Exists(AppPaths.Log)) return;
            var friendly = new List<string>();
            foreach (string line in AppLog.ReadTail(AppPaths.Log, 65536))
            {
                string text = FriendlyLogLine(line);
                if (text == null) continue;
                // Collapse repeats such as several "网络正常" checks in a row.
                if (friendly.Count > 0 && friendly[friendly.Count - 1].Substring(6) == text.Substring(6)) friendly[friendly.Count - 1] = text;
                else friendly.Add(text);
            }
            string[] lines = Enumerable.Reverse(friendly).Take(recentLines.Length).ToArray();
            for (int i = 0; i < recentLines.Length; i++)
            {
                recentLines[i].Text = i < lines.Length ? "●   " + lines[i] : (i == 0 ? "还没有记录。登录、掉线和自动恢复都会显示在这里。" : "");
                recentLines[i].ForeColor = i < lines.Length && (lines[i].Contains("失效") || lines[i].Contains("失败") || lines[i].Contains("暂停"))
                    ? Color.FromArgb(154, 95, 18) : i < lines.Length ? UiColor.Ink : SurfaceColor.Muted;
            }
        }
        catch { /* Another process may be appending to the shared log. */ }
    }

    // Turns technical log lines into short, plain sentences; noise returns null.
    internal static string FriendlyLogLine(string line)
    {
        if (String.IsNullOrWhiteSpace(line) || line.Length < 21) return null;
        string time = line.Substring(11, 5);
        string message = line.Substring(20).Trim();
        string[][] map = {
            new[] { "自动恢复成功", "认证掉线，已自动恢复" },
            new[] { "二次确认：认证会话失效", "检测到校园网认证失效" },
            new[] { "校园网登录成功", "已登录校园网" },
            new[] { "校园网已经登录", "已登录校园网" },
            new[] { "手动检测：", null },
            new[] { "自动恢复失败", "自动恢复没有成功，稍后会再试" },
            new[] { "10 分钟内已主动恢复 3 次", "恢复太频繁，暂停 10 分钟" },
            new[] { "校内认证页暂时波动", "认证服务器响应不稳定，未做处理" },
            new[] { "电脑已锁屏", "电脑锁屏，暂停自动恢复" },
            new[] { "电脑已空闲约 20 分钟", "长时间没有操作，暂停自动恢复" },
            new[] { "本机优先后台监控已启动", "自动保持在线已启动" },
            new[] { "本机优先后台监控已停止", "自动保持在线已停止" },
            new[] { "网卡尚未取得有效 IP", "网络还没连上" },
            new[] { "Wi-Fi 已断开", "Wi-Fi 断开了" },
            new[] { "网络连接已断开", "网络断开了" },
            new[] { "校园网认证在线", "网络正常" },
            new[] { "界面认证操作：", null },
            new[] { "自动保持在线已", null },
            new[] { "校园网账号已保存", "账号已保存" },
        };
        foreach (string[] entry in map)
        {
            int at = message.IndexOf(entry[0], StringComparison.Ordinal);
            if (at < 0) continue;
            string text = entry[1] ?? message.Substring(at + (entry[0].EndsWith("：") ? entry[0].Length : 0));
            return time + "    " + text.TrimEnd('。');
        }
        return null;
    }

    private void RunDetection() { ProbeInBackground(true); }

    private void ProbeInBackground(bool userRequested)
    {
        if (probing) return;
        probing = true;
        if (userRequested) { loginButton.Enabled = false; loginButton.Busy = true; }
        ThreadPool.QueueUserWorkItem(delegate
        {
            PortalState state = PortalState.Unknown;
            string message;
            try
            {
                LinkSnapshot link = PortalProbe.Link();
                if (link.State != LinkState.Ready)
                    message = link.State == LinkState.NoIp ? "还没有取得 IP 地址" : "网络没有连接";
                else
                {
                    state = PortalProbe.Check();
                    message = state == PortalState.Online ? "网络正常，校园网已认证" :
                        state == PortalState.AuthenticationRequired ? "校园网认证已失效" : "暂时无法确认认证状态";
                    if (PortalProbe.Link().Identity != link.Identity)
                    { state = PortalState.Unknown; message = "检测时网络发生了变化，请再检测一次"; }
                }
            }
            catch (Exception ex) { message = "检测失败：" + ex.Message; }
            if (userRequested) AppLog.Write("手动检测：" + message);
            PortalState result = state;
            Ui(delegate
            {
                localPortal = result;
                localObservedAt = DateTime.Now;
                probing = false;
                loginButton.Busy = false;
                loginButton.Enabled = !authenticating;
                RefreshStatus();
                if (userRequested) ShowNotice(message);
            });
        });
    }

    private void RunLogin(bool confirmFirst) { RunLogin(confirmFirst, null); }

    private void RunLogin(bool confirmFirst, FlatAction source)
    {
        if (authenticating) { ShowNotice("认证正在进行，请等一下。"); return; }
        if (!File.Exists(AppPaths.Secret)) { ShowNotice("请先在“设置”里保存校园网账号和密码。"); return; }
        authenticating = true;
        loginButton.Enabled = false;
        repairButton.Enabled = false; repairButton.Busy = true;
        if (source != null && source != repairButton) { source.Enabled = false; source.Busy = true; }
        ThreadPool.QueueUserWorkItem(delegate
        {
            string message;
            int code = -1;
            try
            {
                if (confirmFirst)
                {
                    LinkSnapshot firstLink = PortalProbe.Link();
                    if (firstLink.State != LinkState.Ready) { message = "网络没有连接，未执行认证。"; goto Done; }
                    PortalState first = PortalProbe.Check();
                    if (first == PortalState.Online) { message = "校园网已在线，无需重新认证。"; code = 0; goto Done; }
                    if (first != PortalState.AuthenticationRequired) { message = "认证状态不明确，未执行认证。"; goto Done; }
                    Thread.Sleep(5000);
                    LinkSnapshot secondLink = PortalProbe.Link();
                    if (secondLink.State != LinkState.Ready || secondLink.Identity != firstLink.Identity ||
                        PortalProbe.Check() != PortalState.AuthenticationRequired)
                    {
                        message = "两次检查结果不一致，未执行认证。";
                        goto Done;
                    }
                }
                code = CampusLogin.Run(true);
                message = code == 0 ? "校园网已在线或登录成功。" :
                    code == 3 ? "已经有登录任务在进行，请稍后再试。" :
                    code == 2 ? "学校要求人工处理（如验证码或改密码），已暂停自动登录。请打开认证页处理后，在设置里重新保存密码。" :
                    code == 4 ? "登录尝试太频繁，10 分钟后再试。" : "登录没有成功，可以查看运行日志。";
                if (code == 0) ConnectionMonitor.NotifyManualLogin();
            }
            catch (Exception ex) { message = "认证操作失败：" + ex.Message; }
        Done:
            AppLog.Write("界面认证操作：" + message);
            Ui(delegate
            {
                authenticating = false;
                loginButton.Enabled = true;
                repairButton.Busy = false;
                repairButton.Enabled = true;
                if (source != null && !source.IsDisposed) { source.Busy = false; source.Enabled = true; }
                RefreshRecentEvents();
                ShowNotice(message);
                if (code == 0) ProbeInBackground(false);
            });
        });
    }

    private void StartDetailedDiagnosis(FlatAction button)
    {
        if (diagnosing || diagnosisStates == null || button == null || button.IsDisposed) return;
        diagnosing = true; RenderDiagnosisState();
        ThreadPool.QueueUserWorkItem(delegate
        {
            DiagnosisSnapshot result;
            try { result = CampusDiagnostics.Run(); }
            catch (Exception ex) {
                result = new DiagnosisSnapshot { States = Enumerable.Repeat("无法判断", 8).ToArray(), Details = Enumerable.Repeat("检测未完成", 8).ToArray(),
                    Summary = "体检没有完成", Latency = "延迟  未测得", Loss = "未响应  无法判断", Advice = "体检没有完成：" + ex.Message + "；不会因此重新登录。" };
            }
            AppLog.Write("网络体检完成");
            Ui(delegate { diagnosing = false; lastDiagnosis = result; RenderDiagnosisState(); });
        });
    }

    private void RenderDiagnosisState()
    {
        if (currentPage != CheckupPage || diagnosisSummary == null || diagnosisSummary.IsDisposed) return;
        if (diagnosisRoute != null) { diagnosisRoute.Verified = !diagnosing && lastDiagnosis != null && lastDiagnosis.States.All(value => value == "正常"); diagnosisRoute.Invalidate(); }
        diagnosisRun.Enabled = diagnosisRetry.Enabled = !diagnosing;
        diagnosisRun.Busy = diagnosisRetry.Busy = diagnosing;
        diagnosisRun.Text = diagnosing ? "正在体检…" : lastDiagnosis == null ? "开始体检" : "重新体检";
        diagnosisRetry.Text = diagnosing ? "正在体检…" : "重新体检";
        if (diagnosing)
        {
            diagnosisSummary.Text = "正在检查 8 个环节，大约需要 10 秒…";
            for (int i = 0; i < diagnosisStates.Length; i++)
            { diagnosisStates[i].Text = "检测中"; diagnosisStates[i].ForeColor = UiColor.Blue; diagnosisDetails[i].Text = "正在等待结果"; }
            return;
        }
        if (lastDiagnosis == null) return;
        diagnosisSummary.Text = lastDiagnosis.Summary;
        diagnosisLatency.Text = lastDiagnosis.Latency; diagnosisLoss.Text = lastDiagnosis.Loss; diagnosisAdvice.Text = lastDiagnosis.Advice;
        for (int i = 0; i < diagnosisStates.Length; i++)
        {
            string state = lastDiagnosis.States[i];
            diagnosisStates[i].Text = state; diagnosisDetails[i].Text = lastDiagnosis.Details[i];
            diagnosisStates[i].ForeColor = state == "正常" ? UiColor.Green : state == "未检测" || state == "无法判断" ? UiColor.Muted : Color.FromArgb(145, 99, 22);
        }
    }

    private void RunPing(string host, bool popup)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            string result;
            try
            {
                using (var ping = new Ping())
                {
                    PingReply reply = ping.Send(host, 2000);
                    result = reply.Status == IPStatus.Success ? host + " 延迟 " + reply.RoundtripTime + " ms" : host + " Ping：" + reply.Status;
                    Ui(delegate { pingText.Text = reply.Status == IPStatus.Success ? reply.RoundtripTime + " ms" : "无响应"; });
                }
            }
            catch (Exception ex) { result = "Ping 失败：" + ex.Message; }
            if (popup) Ui(delegate { ShowNotice(result); });
        });
    }

    private void RunHostTool(string rawHost, bool ping, TextBox output, params FlatAction[] buttons)
    {
        if (output.Tag != null) return;
        string host = (rawHost ?? "").Trim();
        if (ping && !CampusTargets.Allowed(host))
        { output.Text = "只能检测校内地址（10.x、172.16-31.x、192.168.x）和学校认证服务器 " + CampusTargets.Portal + "。"; return; }
        output.Tag = true;
        foreach (FlatAction button in buttons) button.Enabled = false;
        if (buttons.Length > 0) buttons[ping ? 0 : Math.Min(1, buttons.Length - 1)].Busy = true;
        Action<string> complete = delegate(string result)
        {
            Ui(delegate
            {
                if (output.IsDisposed) return;
                output.Tag = null;
                output.Text = result;
                foreach (FlatAction button in buttons) if (!button.IsDisposed) { button.Busy = false; button.Enabled = true; }
            });
        };
        if (ping)
        {
            output.Text = "正在 Ping " + host + "…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string result;
                try
                {
                    CampusProbeResult reply = new CampusOnlyProbe().Ping(host, CancellationToken.None);
                    result = reply.Milliseconds.HasValue ? host + " 的延迟是 " + reply.Milliseconds + " ms" : "没有收到回应。对方可能不回应 Ping，不能据此认定断网。";
                }
                catch (Exception ex) { result = "Ping 失败：" + ex.Message; }
                complete(result);
            });
        }
        else
        {
            output.Text = "正在读取本机 DNS 配置…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string result;
                try { result = CampusTargets.DnsConfiguration(PortalProbe.Link()); }
                catch (Exception ex) { result = "DNS 配置读取失败：" + ex.Message; }
                complete(result);
            });
        }
    }

    private void OpenPortal()
    {
        try { Process.Start(new ProcessStartInfo("http://210.43.112.9/") { UseShellExecute = true }); }
        catch (Exception ex) { ShowNotice("无法打开认证页：" + ex.Message); }
    }

    private void OpenLog()
    {
        try
        {
            if (File.Exists(AppPaths.Log)) Process.Start("notepad.exe", AppPaths.Log);
            else ShowNotice("还没有运行日志。");
        }
        catch (Exception ex) { ShowNotice("无法打开日志：" + ex.Message); }
    }

    private void Ui(Action action)
    {
        if (!IsDisposed && IsHandleCreated)
            try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) action(); }); }
            catch (InvalidOperationException) { }
    }

    private Font noticeFont;
    // Short results appear as a toast inside the window and fade on their own;
    // longer explanations open a styled dialog. Neither uses the system beep box.
    private void ShowNotice(string message)
    {
        if (IsDisposed || String.IsNullOrEmpty(message)) return;
        if (noticeFont == null) { noticeFont = new Font(UiFont.Family, 10f); Disposed += delegate { noticeFont.Dispose(); }; }
        bool good = NoticeTone(message) > 0, bad = NoticeTone(message) < 0;
        try
        {
            if (!Visible || WindowState == FormWindowState.Minimized || !IsHandleCreated) throw new InvalidOperationException();
            if (message.Length <= 46 && message.IndexOf('\n') < 0)
            {
                if (good) AntdUI.Message.success(this, message, noticeFont, null);
                else if (bad) AntdUI.Message.warn(this, message, noticeFont, null);
                else AntdUI.Message.info(this, message, noticeFont, null);
                return;
            }
            var dialog = new AntdUI.Modal.Config(this, bad ? "需要注意" : good ? "已完成" : "提示", message,
                bad ? AntdUI.TType.Warn : good ? AntdUI.TType.Success : AntdUI.TType.Info)
            { Font = noticeFont, OkText = "知道了", CancelText = null, MaskClosable = true };
            AntdUI.Modal.open(dialog);
        }
        catch (Exception)
        {
            MessageBox.Show(this, message, AppName, MessageBoxButtons.OK, bad ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
    }

    private static int NoticeTone(string message)
    {
        foreach (string word in new[] { "失败", "无法", "没有成功", "失效", "未执行", "不明确", "太频繁", "请先", "找不到", "不可用", "人工处理", "不一致", "变化" })
            if (message.Contains(word)) return -1;
        foreach (string word in new[] { "成功", "已保存", "已在线", "无需", "网络正常" })
            if (message.Contains(word)) return 1;
        return 0;
    }
}
