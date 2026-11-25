using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class WindowCaptionPanel : Panel
{
    private Point pressPoint;
    private bool pendingDrag;
    private bool pendingMaximize;

    internal WindowCaptionPanel()
    {
        SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        pendingDrag = e.Clicks == 1;
        pressPoint = e.Location;
        pendingMaximize = e.Clicks == 2;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!pendingDrag || e.Button != MouseButtons.Left) return;
        Size threshold = SystemInformation.DragSize;
        if (Math.Abs(e.X - pressPoint.X) < threshold.Width / 2 && Math.Abs(e.Y - pressPoint.Y) < threshold.Height / 2) return;
        pendingDrag = false;
        Form window = FindForm();
        if (window == null) return;
        ReleaseCapture();
        // Start a native move only after movement. A stationary first click must
        // finish normally so WinForms can recognize the second MouseDown.
        SendMessage(window.Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        pendingDrag = false;
        base.OnMouseUp(e);
        // MouseDown still owns capture when its posted command is dispatched.
        // Queue from MouseUp so capture has been released before Windows handles it.
        if (pendingMaximize && e.Button == MouseButtons.Left)
        {
            pendingMaximize = false;
            Form window = FindForm();
            if (window != null) WindowBehavior.ToggleMaximize(window);
        }
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
}

// Keep Windows' ordinary top-level window contract while drawing our own title bar.
// FormBorderStyle.None removes WS_MINIMIZEBOX, which makes Explorer's taskbar
// activation stop behaving like a minimizable application window.
internal static class WindowBehavior
{
    internal const int Caption = 0x00C00000;
    internal const int ThickFrame = 0x00040000;
    internal const int SystemMenu = 0x00080000;
    internal const int MinimizeBox = 0x00020000;
    internal const int MaximizeBox = 0x00010000;
    internal const int SystemCommand = 0x0112;
    internal const int MinimizeCommand = 0xF020;
    internal const int MaximizeCommand = 0xF030;
    internal const int RestoreCommand = 0xF120;
    private const int NonClientCalculateSize = 0x0083;
    private const int NonClientPaint = 0x0085;
    private const int NonClientActivate = 0x0086;
    private const int PrintWindowMessage = 0x0317;
    private const long PrintNonClient = 0x00000002;

    internal static CreateParams ApplyStyles(CreateParams parameters)
    {
        parameters.Style |= Caption | ThickFrame | SystemMenu | MinimizeBox | MaximizeBox;
        return parameters;
    }

    internal static bool HandleMessage(ref Message message)
    {
        if (message.Msg == PrintWindowMessage)
        {
            // DrawToBitmap sends WM_PRINT with PRF_NONCLIENT, and DefWindowProc
            // otherwise paints a legacy caption from WS_CAPTION even though our
            // WM_NCCALCSIZE made the entire window client space. Preserve client,
            // background and child-control printing, suppress only that fake frame.
            message.LParam = new IntPtr(message.LParam.ToInt64() & ~PrintNonClient);
            return false;
        }
        if (message.Msg == NonClientPaint)
        {
            if (IsIconic(message.HWnd)) return false;
            message.Result = IntPtr.Zero;
            return true;
        }
        if (message.Msg == NonClientActivate)
        {
            if (IsIconic(message.HWnd)) return false;
            message.Result = new IntPtr(1);
            return true;
        }
        if (message.Msg != NonClientCalculateSize) return false;
        // The native frame capabilities remain in GWL_STYLE. The complete window
        // rectangle becomes client space, so no second title bar or border is drawn.
        message.Result = IntPtr.Zero;
        return true;
    }

    internal static void Minimize(Form window)
    {
        if (!window.IsDisposed) PostMessage(window.Handle, SystemCommand, new IntPtr(MinimizeCommand), IntPtr.Zero);
    }

    internal static void ToggleMaximize(Form window)
    {
        if (window.IsDisposed) return;
        int command = window.WindowState == FormWindowState.Maximized ? RestoreCommand : MaximizeCommand;
        // WinForms raises Click while the caption control still owns mouse capture.
        // DefWindowProc ignores synchronous minimize/maximize commands in that phase.
        // Queue the command so it runs after WM_LBUTTONUP releases capture.
        PostMessage(window.Handle, SystemCommand, new IntPtr(command), IntPtr.Zero);
    }

    internal static void RestoreAndActivate(Form window)
    {
        if (window.IsDisposed) return;
        if (!window.Visible) window.Show();
        if (IsIconic(window.Handle))
            SendMessage(window.Handle, SystemCommand, new IntPtr(RestoreCommand), IntPtr.Zero);
        window.Activate();
    }

    private static IntPtr classBackground;

    // WinForms registers its window class with NULL_BRUSH. While Windows animates
    // maximize or taskbar restore, DWM shows any not-yet-painted area of the window
    // in that brush, which appears as black blocks. Give the shared class a solid
    // canvas-coloured brush so those frames match the page background instead.
    // Every WinForms container in this process shares the class, so one call covers
    // the form and its child panels. The brush lives for the process lifetime.
    internal static void PaintClassBackground(Control window, Color color)
    {
        if (window.IsDisposed || !window.IsHandleCreated) return;
        if (classBackground == IntPtr.Zero) classBackground = CreateSolidBrush(ColorTranslator.ToWin32(color));
        if (classBackground == IntPtr.Zero) return;
        const int ClassBackgroundBrush = -10; // GCLP_HBRBACKGROUND
        if (IntPtr.Size == 8) SetClassLongPtr64(window.Handle, ClassBackgroundBrush, classBackground);
        else SetClassLong32(window.Handle, ClassBackgroundBrush, classBackground.ToInt32());
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int color);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern IntPtr SetClassLongPtr64(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetClassLongW")]
    private static extern int SetClassLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
}
