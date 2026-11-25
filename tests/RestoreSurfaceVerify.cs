using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class RestoreSurfaceVerify
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> results = new List<object>(), measurements = new List<object>();
    private sealed class MessageTrace : NativeWindow
    {
        private readonly DashboardWindow window;
        internal readonly List<string> Lines = new List<string>();
        internal int PendingAtRestoreSize = -1;
        internal MessageTrace(DashboardWindow w) { window = w; AssignHandle(w.Handle); }
        protected override void WndProc(ref Message m)
        {
            bool record = m.Msg == 5 || m.Msg == 0x46 || m.Msg == 0x47 || m.Msg == 0x112;
            int msg = m.Msg;
            if (msg == 0x47) Lines.Add("WINDOWPOS " + Marshal.ReadInt32(m.LParam, IntPtr.Size * 2 + 8) + "x" + Marshal.ReadInt32(m.LParam, IntPtr.Size * 2 + 12) + " flags=" + Marshal.ReadInt32(m.LParam, IntPtr.Size * 2 + 16).ToString("X"));
            if (record) Lines.Add("before " + msg.ToString("X") + "/" + m.WParam.ToInt64().ToString("X") + " " + window.WindowState + " " + window.ClientSize + " content=" + Field<Panel>(window, "content").Size);
            base.WndProc(ref m);
            if (msg == 5 && m.WParam.ToInt64() != 1) PendingAtRestoreSize = Pending(Exposed(window));
            if (record) Lines.Add("after " + msg.ToString("X") + " " + window.WindowState + " " + window.ClientSize + " content=" + Field<Panel>(window, "content").Size);
        }
    }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool GetUpdateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    private static IEnumerable<Control> All(Control root) { foreach (Control c in root.Controls) { yield return c; foreach (Control d in All(c)) yield return d; } }
    private static T Field<T>(DashboardWindow w, string name) { return (T)typeof(DashboardWindow).GetField(name, Private).GetValue(w); }
    private static void Page(DashboardWindow w, string page) { typeof(DashboardWindow).GetMethod("ShowPage", Private).Invoke(w, new object[] { page }); }
    private static void Pump(int ms) { var watch = Stopwatch.StartNew(); do { Application.DoEvents(); Thread.Sleep(2); } while (watch.ElapsedMilliseconds < ms); }
    private static void Check(bool condition, string name) { results.Add(new { name = name, status = condition ? "PASS" : "FAIL" }); }
    private static Dictionary<Control, Rectangle> Geometry(DashboardWindow w) { return All(w).ToDictionary(c => c, c => c.Bounds); }
    private static int Changed(Dictionary<Control, Rectangle> before) { return before.Count(pair => !pair.Key.IsDisposed && pair.Key.Bounds != pair.Value); }
    private static Control[] Exposed(DashboardWindow w)
    {
        return All(w).Where(c => c.Visible && c.IsHandleCreated && !c.IsDisposed && IsExposed(c)).ToArray();
    }
    private static bool IsExposed(Control control)
    {
        Rectangle visible = control.RectangleToScreen(control.ClientRectangle);
        for (Control parent = control.Parent; parent != null; parent = parent.Parent)
            visible.Intersect(parent.RectangleToScreen(parent.ClientRectangle));
        return visible.Width > 0 && visible.Height > 0;
    }
    private static int Pending(Control[] controls) { return controls.Count(c => GetUpdateRect(c.Handle, IntPtr.Zero, false)); }
    [STAThread] private static void Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        try { Run(args, output); } catch (Exception ex) { results.Add(new { name = "Exception", status = "FAIL", detail = ex.ToString() }); }
        int failed = results.Count(x => (string)x.GetType().GetProperty("status").GetValue(x, null) == "FAIL");
        File.WriteAllText(Path.Combine(output, "restore-results.json"), new JavaScriptSerializer().Serialize(new { passed = results.Count - failed, failed = failed, measurements = measurements, results = results }));
        Console.WriteLine("Restore surface checks: " + (results.Count - failed) + " passed, " + failed + " failed");
        Environment.ExitCode = failed == 0 || args.Contains("--baseline") ? 0 : 1;
    }
    private static void Run(string[] args, string output)
    {
        string data = Path.Combine(output, "isolated-data"); Directory.CreateDirectory(data);
        foreach (string name in new[] { "Data", "Secret", "Settings", "Log", "Startup", "RecoveryState", "MonitorStatus" })
            typeof(AppPaths).GetField(name).SetValue(null, name == "Data" ? data : Path.Combine(data, name + ".json"));
        AppSettings.Save(new SettingsRecord { priorityMode = "Off", autoLogin = false, minimizeToTray = false, carrier = "auto", checkIntervalSeconds = 45 });
        byte[] original = File.ReadAllBytes(AppPaths.Settings);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using (var w = new DashboardWindow(false))
        {
            w.StartPosition = FormStartPosition.Manual; w.Location = args.Contains("--ui-host") ? new Point(40, 30) : new Point(-20000, -20000);
            w.Size = new Size(1280, 820); w.Show(); Pump(150);
            foreach (string page in args.Contains("--probe") ? new[] { "首页" } : new[] { "首页", "游戏模式", "体检与报告", "设置", "高级工具" })
            {
                Page(w, page); Pump(250);
                if (page == "设置") { Field<Panel>(w, "otherPage").AutoScrollPosition = new Point(0, 180); Pump(30); }
                foreach (bool maximized in new[] { false, true })
                {
                    Size normalSize = w.Size;
                    if (maximized) { w.WindowState = FormWindowState.Maximized; Pump(50); }
                    var before = Geometry(w); Size contentBefore = Field<Panel>(w, "content").Size;
                    Point scrollBefore = Field<Panel>(w, page == "首页" ? "home" : "otherPage").AutoScrollPosition;
                    var exposed = Exposed(w); int resizes = 0; EventHandler resized = delegate { resizes++; };
                    foreach (Control c in before.Keys) c.Resize += resized;
                    var trace = new MessageTrace(w);
                    SendMessage(w.Handle, WindowBehavior.SystemCommand, new IntPtr(WindowBehavior.MinimizeCommand), IntPtr.Zero); Pump(35);
                    int minimizedChanges = Changed(before), minimizedResizes = resizes;
                    Size minimizedContent = Field<Panel>(w, "content").Size;
                    // Simulate content becoming dirty while the window is minimized.
                    foreach (Control c in exposed) c.Invalidate();
                    var watch = Stopwatch.StartNew();
                    SendMessage(w.Handle, WindowBehavior.SystemCommand, new IntPtr(WindowBehavior.RestoreCommand), IntPtr.Zero);
                    watch.Stop();
                    int pending = Pending(exposed), restoreChanges = Changed(before);
                    measurements.Add(new { page = page, maximized = maximized, minimizedGeometryChanges = minimizedChanges,
                        minimizedResizeEvents = minimizedResizes, restoreResizeEvents = resizes - minimizedResizes, pendingPaintsAtRestoreReturn = pending,
                        pendingPaintsAtRestoreSizeReturn = trace.PendingAtRestoreSize,
                        exposedControls = exposed.Length, contentBefore = contentBefore.ToString(), minimizedContent = minimizedContent.ToString(), restoreCommandMs = watch.Elapsed.TotalMilliseconds,
                        changedControls = before.Where(p => p.Key.Bounds != p.Value).Take(14).Select(p => new { type = p.Key.GetType().Name, name = p.Key.Name, before = p.Value.ToString(), after = p.Key.Bounds.ToString() }).ToArray(),
                        pendingControls = exposed.Where(c => GetUpdateRect(c.Handle, IntPtr.Zero, false)).Select(c => c.GetType().Name + "/" + c.Name + "/" + c.Text).ToArray() });
                    File.WriteAllLines(Path.Combine(output, "trace-" + page + "-" + maximized + ".txt"), trace.Lines); trace.ReleaseHandle();
                    string scene = page + (maximized ? " maximized" : " normal");
                    Check(minimizedChanges == 0 && minimizedContent == contentBefore, "Minimize retains ready child geometry: " + scene);
                    Check(minimizedResizes == 0 && resizes == 0, "Minimize/restore do not resize child controls: " + scene);
                    Check(restoreChanges == 0, "Restoration preserves all existing child bounds: " + scene);
                    Check(trace.PendingAtRestoreSize == 0, "Children are ready before WM_SIZE returns to the native animation: " + scene);
                    Check(w.WindowState == (maximized ? FormWindowState.Maximized : FormWindowState.Normal), "Native restore returns to previous state: " + scene);
                    Check(w.Opacity == 1 && (GetWindowLong(w.Handle, -20) & (0x02000000 | 0x00080000)) == 0, "Native opaque surface and local buffering retained: " + scene);
                    Check(scrollBefore == Field<Panel>(w, page == "首页" ? "home" : "otherPage").AutoScrollPosition, "Scroll position survives restore: " + scene);
                    // Focus/hover can legitimately invalidate a native control
                    // after the first complete frame; let short feedback settle.
                    Pump(260);
                    Check(Pending(exposed) == 0, "Restore painting settles after native focus/hover feedback: " + scene);
                    foreach (Control c in before.Keys) c.Resize -= resized;
                    if (maximized)
                    {
                        w.WindowState = FormWindowState.Normal; Pump(80);
                        Check(w.Size == normalSize, "Unmaximizing returns to the same normal dimensions: " + page);
                    }
                }
            }
            Page(w, "设置"); Pump(250);
            foreach (bool nativeShow in new[] { false, true })
            {
                Size size = w.Size; var ready = Geometry(w);
                for (int cycle = 0; cycle < 8; cycle++)
                {
                    if (nativeShow) ShowWindow(w.Handle, 6); else w.WindowState = FormWindowState.Minimized;
                    Pump(20);
                    Check(Changed(ready) == 0, "Repeated minimize retains geometry via " + (nativeShow ? "ShowWindow" : "WindowState") + " / " + cycle);
                    if (nativeShow) ShowWindow(w.Handle, 9); else w.WindowState = FormWindowState.Normal;
                    Pump(25);
                    Check(w.Size == size && Changed(ready) == 0, "Repeated restoration has no frame-size growth via " + (nativeShow ? "ShowWindow" : "WindowState") + " / " + cycle);
                    Check(Pending(Exposed(w)) == 0, "Direct restoration drains painting via " + (nativeShow ? "ShowWindow" : "WindowState") + " / " + cycle);
                }
            }
            var mode = (ComboBox)All(w).Single(c => c.Name == "SettingsMode"); mode.SelectedIndex = 1;
            Check(mode.SelectedIndex == 1 && mode.FlatStyle == FlatStyle.Standard, "Strategy dropdown remains a usable standard native control");
            w.Size = new Size(1000, 640); Pump(60); w.Size = new Size(1280, 820); Pump(60);
            Check(Field<Panel>(w, "content").Width > 900, "Ordinary resizing still lays out the interface after restoration");
            using (var image = new Bitmap(w.Width, w.Height)) { w.DrawToBitmap(image, w.ClientRectangle); image.Save(Path.Combine(output, "preview.png")); }
            Check(original.SequenceEqual(File.ReadAllBytes(AppPaths.Settings)), "Restore tests leave isolated settings unchanged");
            Check(!File.Exists(AppPaths.Secret) && !File.Exists(AppPaths.Startup), "No credentials or startup entries created");
            w.Close();
        }
        using (var closing = new DashboardWindow(false))
        {
            closing.StartPosition = FormStartPosition.Manual; closing.Location = new Point(-20000, -20000); closing.Show(); Pump(50);
            closing.WindowState = FormWindowState.Minimized; Pump(25); closing.Close();
            Check(closing.IsDisposed, "Closing while minimized releases parked layout without recreating the window");
        }
    }
}
