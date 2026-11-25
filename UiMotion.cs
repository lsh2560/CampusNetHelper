using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Linq;

// All transitions live on the UI thread. Idle/hidden windows own no animation ticks.
internal static class UiMotion
{
    private static readonly HashSet<UiTransition> active = new HashSet<UiTransition>();
    private static readonly Timer timer = new Timer { Interval = 15 };
    internal static bool Reduced { get; private set; }
    private static bool systemAnimations = true;
    internal static bool Enabled { get { return !Reduced && systemAnimations && !SystemInformation.HighContrast; } }
    internal static int ActiveCount { get { return active.Count; } }
    static UiMotion() { timer.Tick += Tick; }
    internal static void Configure(bool reduced)
    {
        Reduced = reduced;
        bool allowed;
        systemAnimations = !SystemParametersInfo(0x1042, 0, out allowed, 0) || allowed;
        AntdUI.Config.Animation = Enabled;
        if (!Enabled) foreach (UiTransition item in new List<UiTransition>(active)) item.Finish();
    }
    internal static void Add(UiTransition transition) { active.Add(transition); timer.Start(); }
    internal static void Remove(UiTransition transition) { active.Remove(transition); if (active.Count == 0) timer.Stop(); }
    private static void Tick(object sender, EventArgs e)
    {
        foreach (UiTransition item in new List<UiTransition>(active)) item.Step();
    }
    internal static Color Mix(Color from, Color to, double amount)
    {
        amount = Math.Max(0, Math.Min(1, amount));
        return Color.FromArgb((int)Math.Round(from.R + (to.R - from.R) * amount),
            (int)Math.Round(from.G + (to.G - from.G) * amount), (int)Math.Round(from.B + (to.B - from.B) * amount));
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
}

internal sealed class UiTransition : IDisposable
{
    private readonly Control owner;
    private readonly Stopwatch clock = new Stopwatch();
    private double start, target;
    private int duration;
    internal double Value { get; private set; }
    internal UiTransition(Control control) { owner = control; }
    internal void Snap(double value)
    {
        target = value; Finish();
    }
    internal void To(double value, int milliseconds)
    {
        Step();
        start = Value; target = value; duration = milliseconds;
        if (!UiMotion.Enabled || !CanAnimate()) { Finish(); return; }
        if (Math.Abs(target - start) < .001) { Finish(); return; }
        clock.Restart(); UiMotion.Add(this); owner.Invalidate();
    }
    private bool CanAnimate()
    {
        Form window = owner.FindForm();
        return !owner.IsDisposed && owner.Enabled && owner.Visible && owner.IsHandleCreated && window != null && window.Visible && window.WindowState != FormWindowState.Minimized;
    }
    internal void Step()
    {
        if (!clock.IsRunning) return;
        if (!UiMotion.Enabled || !CanAnimate()) { Finish(); return; }
        double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / Math.Max(1, duration));
        Value = start + (target - start) * (1 - Math.Pow(1 - t, 3));
        owner.Invalidate();
        if (t >= 1) Finish();
    }
    internal void Finish()
    {
        Value = target; clock.Stop(); UiMotion.Remove(this);
        Form window = owner.FindForm();
        if (!owner.IsDisposed && owner.Visible && (window == null || window.WindowState != FormWindowState.Minimized)) owner.Invalidate();
    }
    public void Dispose() { clock.Stop(); UiMotion.Remove(this); }
}

// Draw on the menu surface so the marker cannot intercept navigation input.
// AntdUI 2.3.0 owns row layout; read its actual row bounds rather than guessing
// from font height or spacing. The dependency is pinned by the build script.
internal sealed class AnimatedNavigationMenu : AntdUI.Menu
{
    private static readonly System.Reflection.PropertyInfo rowBounds = typeof(AntdUI.MenuItem).GetProperty(
        "rect", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
    private readonly UiTransition position;
    private int selectedIndex;
    private Rectangle targetBounds;
    private bool positioned;
    internal AnimatedNavigationMenu() { position = new UiTransition(this); }
    internal double IndicatorTop { get { return position.Value; } }
    internal int IndicatorIndex { get { return selectedIndex; } }
    private Rectangle SelectedBounds()
    {
        if (rowBounds == null || selectedIndex < 0 || selectedIndex >= Items.Count) return Rectangle.Empty;
        return (Rectangle)rowBounds.GetValue(Items[selectedIndex], null);
    }
    private float DpiScale { get { using (Graphics g = CreateGraphics()) return g.DpiX / 96F; } }
    private double TopFor(Rectangle row, float scale) { return row.Top + (row.Height - Math.Min(row.Height, 22F * scale)) / 2F; }
    internal void MoveIndicator(int index)
    {
        bool changed = selectedIndex != index;
        selectedIndex = index;
        Rectangle row = SelectedBounds();
        if (row.Width <= 0 || row.Height <= 0 || !IsHandleCreated) { Invalidate(); return; }
        double top = TopFor(row, DpiScale);
        if (!positioned) { position.Snap(top); positioned = true; }
        else if (changed) position.To(top, 210);
        else if (row != targetBounds) position.Snap(top);
        targetBounds = row;
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Rectangle row = SelectedBounds();
        if (row.Width <= 0 || row.Height <= 0) return;
        float scale = e.Graphics.DpiX / 96F;
        // Resize / font / compact-spacing changes keep the marker anchored.
        if (!positioned || row != targetBounds)
        {
            position.Snap(TopFor(row, scale)); positioned = true; targetBounds = row;
        }
        float width = 3F * scale, height = Math.Min(row.Height, 22F * scale);
        float x = row.Left + 3F * scale, y = (float)position.Value;
        var state = e.Graphics.Save();
        try
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Highlight : SurfaceColor.Blue))
            {
                e.Graphics.FillRectangle(brush, x, y + width / 2F, width, Math.Max(0, height - width));
                e.Graphics.FillEllipse(brush, x, y, width, width);
                e.Graphics.FillEllipse(brush, x, y + height - width, width, width);
            }
        }
        finally { e.Graphics.Restore(state); }
    }
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible && position != null) position.Finish();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && position != null) position.Dispose();
        base.Dispose(disposing);
    }
}

internal class BufferedPanel : Panel
{
    internal BufferedPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
}

internal sealed class AdaptiveActionRow : FlowLayoutPanel
{
    private bool adjusting;
    private float initialHeight;
    internal AdaptiveActionRow() { WrapContents = true; }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var table = Parent as TableLayoutPanel;
        if (adjusting || table == null || ClientSize.Width <= 0 || Controls.Count == 0) return;
        int row = table.GetRow(this);
        if (row < 0 || row >= table.RowStyles.Count) return;
        if (initialHeight == 0) initialHeight = table.RowStyles[row].Height;
        int required = Controls.Cast<Control>().Where(c => c.Visible).Select(c => c.Bottom + c.Margin.Bottom + Padding.Bottom).DefaultIfEmpty(0).Max();
        float next = Math.Max(initialHeight, required + 2);
        if (Math.Abs(table.RowStyles[row].Height - next) < 1) return;
        adjusting = true;
        try
        {
            table.RowStyles[row].Height = next;
            table.Height = (int)Math.Ceiling(table.RowStyles.Cast<RowStyle>().Where(s => s.SizeType == SizeType.Absolute).Sum(s => s.Height));
        }
        finally { adjusting = false; }
    }
}

// Batch synchronous changes without WM_SETREDRAW: on a top-level window that
// message also clears WS_VISIBLE and exposes stale child surfaces to DWM/WGC.
internal sealed class UiPaintBatch : IDisposable
{
    private readonly Control owner;
    internal UiPaintBatch(Control control)
    {
        owner = control;
        owner.SuspendLayout();
    }
    public void Dispose()
    {
        try { owner.ResumeLayout(true); }
        finally
        {
            if (!owner.IsDisposed && owner.IsHandleCreated && owner.Visible &&
                (!(owner is Form) || ((Form)owner).WindowState != FormWindowState.Minimized))
                owner.Invalidate();
        }
    }
    internal static void Render(Control control)
    {
        if (!control.IsDisposed && control.IsHandleCreated)
            RedrawWindow(control.Handle, IntPtr.Zero, IntPtr.Zero, 0x0185);
    }
    internal static void PresentRestored(Control control)
    {
        if (!control.IsDisposed && control.IsHandleCreated && control.Visible)
            // INVALIDATE | ERASE | ALLCHILDREN | UPDATENOW. Native/transparent
            // children need their backgrounds restored too; all painting is
            // completed in this dispatch with the opaque window styles intact.
            RedrawWindow(control.Handle, IntPtr.Zero, IntPtr.Zero, 0x0185);
    }
    internal static void FlushPending(Control control)
    {
        if (!control.IsDisposed && control.IsHandleCreated && control.Visible)
            // ALLCHILDREN | UPDATENOW. Process existing dirt only: neither
            // INVALIDATE nor ERASE creates a second full-tree presentation.
            RedrawWindow(control.Handle, IntPtr.Zero, IntPtr.Zero, 0x0180);
    }
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, int flags);
}

// Keep inactive page HWND trees outside the viewport and disabled. Move the
// ready destination and its predecessor in one native window-position batch,
// avoiding recursive ShowWindow/HideWindow painting on every cached switch.
internal static class UiPageDeck
{
    internal static void Activate(Control host, IList<Control> pages, Control destination, Point origin)
    {
        bool changed = false;
        foreach (Control page in pages)
        {
            if (!page.Visible) { page.Location = new Point(-10000, origin.Y); page.Visible = true; changed = true; }
            PrepareHandles(page);
            Point wanted = page == destination ? origin : new Point(-10000, origin.Y);
            if (page.Location != wanted || page.Enabled != (page == destination)) changed = true;
        }
        if (!changed && host.Controls.GetChildIndex(destination) == 0) return;
        destination.Enabled = true;
        IntPtr batch = BeginDeferWindowPos(pages.Count);
        if (batch != IntPtr.Zero)
        {
            foreach (Control page in pages)
            {
                batch = DeferWindowPos(batch, page.Handle, IntPtr.Zero,
                    page == destination ? origin.X : -10000, origin.Y, 0, 0, 0x021D);
                if (batch == IntPtr.Zero) break;
            }
            if (batch != IntPtr.Zero) EndDeferWindowPos(batch);
        }
        // Keep WinForms bounds in sync; this also supplies a local fallback if
        // a native batch cannot be allocated. No input or focus is synthesized.
        foreach (Control page in pages)
        {
            Point position = page == destination ? origin : new Point(-10000, origin.Y);
            if (page.Location != position) page.Location = position;
            page.Enabled = page == destination;
        }
        if (host.Controls.GetChildIndex(destination) != 0) destination.BringToFront();
        destination.Invalidate(true);
    }
    private static void PrepareHandles(Control control)
    {
        IntPtr handle = control.Handle;
        foreach (Control child in control.Controls) PrepareHandles(child);
    }
    [DllImport("user32.dll")] private static extern IntPtr BeginDeferWindowPos(int count);
    [DllImport("user32.dll")] private static extern IntPtr DeferWindowPos(IntPtr batch, IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool EndDeferWindowPos(IntPtr batch);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, int flags);
}

