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

internal static class PresentationVerify
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> results = new List<object>(), measurements = new List<object>();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool GetUpdateRect(IntPtr h, IntPtr r, bool erase);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
    private static IEnumerable<Control> All(Control parent) { foreach (Control c in parent.Controls) { yield return c; foreach (Control d in All(c)) yield return d; } }
    private static T Field<T>(DashboardWindow w, string name) { return (T)typeof(DashboardWindow).GetField(name, Private).GetValue(w); }
    private static void Page(DashboardWindow w, string page) { typeof(DashboardWindow).GetMethod("ShowPage", Private).Invoke(w, new object[] { page }); }
    private static void Pump(int ms) { var watch = Stopwatch.StartNew(); do { Application.DoEvents(); Thread.Sleep(2); } while (watch.ElapsedMilliseconds < ms); }
    private static bool Exposed(Control c)
    {
        if (!c.Visible || c.IsDisposed || !c.IsHandleCreated) return false;
        Rectangle box = c.RectangleToScreen(c.ClientRectangle);
        for (Control p = c.Parent; p != null; p = p.Parent) box.Intersect(p.RectangleToScreen(p.ClientRectangle));
        return box.Width > 0 && box.Height > 0;
    }
    private sealed class PaintProbe : NativeWindow
    {
        internal int Paints, Erases;
        internal readonly Control Control;
        internal PaintProbe(Control control) { Control = control; AssignHandle(control.Handle); }
        protected override void WndProc(ref Message m) { if (m.Msg == 0xF) Paints++; if (m.Msg == 0x14) Erases++; base.WndProc(ref m); }
    }
    private sealed class RootProbe : NativeWindow
    {
        internal int SizePending = -1, PaintsAtSize;
        private readonly List<PaintProbe> children;
        internal RootProbe(DashboardWindow w, List<PaintProbe> controls) { children = controls; AssignHandle(w.Handle); }
        protected override void WndProc(ref Message m)
        {
            bool size = m.Msg == 5 && (m.WParam == IntPtr.Zero || m.WParam == new IntPtr(2));
            base.WndProc(ref m);
            if (size) { SizePending = children.Count(p => Exposed(p.Control) && GetUpdateRect(p.Control.Handle, IntPtr.Zero, false)); PaintsAtSize = children.Sum(p => p.Paints); }
        }
    }
    private static void Check(bool ok, string name) { results.Add(new { name = name, status = ok ? "PASS" : "FAIL" }); }
    [STAThread] private static void Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        try { Run(output); } catch (Exception ex) { results.Add(new { name = "Exception", status = "FAIL", detail = ex.ToString() }); }
        int failures = results.Count(x => (string)x.GetType().GetProperty("status").GetValue(x, null) == "FAIL");
        File.WriteAllText(Path.Combine(output, "presentation-results.json"), new JavaScriptSerializer().Serialize(new { passed = results.Count - failures, failed = failures, measurements = measurements, results = results }));
        Console.WriteLine("Presentation checks: " + (results.Count - failures) + " passed, " + failures + " failed");
        Environment.ExitCode = failures == 0 || args.Contains("--baseline") ? 0 : 1;
    }
    private static void Run(string output)
    {
        string data = Path.Combine(output, "isolated-data"); Directory.CreateDirectory(data);
        foreach (string name in new[] { "Data", "Secret", "Settings", "Log", "Startup", "RecoveryState", "MonitorStatus" })
            typeof(AppPaths).GetField(name).SetValue(null, name == "Data" ? data : Path.Combine(data, name + ".json"));
        AppSettings.Save(new SettingsRecord { autoLogin = false, priorityMode = "Off", minimizeToTray = false, carrier = "auto", checkIntervalSeconds = 45 });
        byte[] original = File.ReadAllBytes(AppPaths.Settings);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using (var w = new DashboardWindow(false))
        {
            w.StartPosition = FormStartPosition.Manual; w.Location = new Point(-20000, -20000); w.Size = new Size(1280, 820); w.Show(); Pump(200);
            foreach (string page in new[] { "首页", "游戏模式", "体检与报告", "设置" })
            {
                Page(w, page); Pump(280);
                foreach (string transition in new[] { "maximize", "unmaximize", "restore-normal", "restore-maximized" })
                {
                    if (transition == "maximize" || transition == "restore-normal") { w.WindowState = FormWindowState.Normal; w.Size = new Size(1280, 820); Pump(70); }
                    else { w.WindowState = FormWindowState.Maximized; Pump(70); }
                    if (transition.StartsWith("restore")) { w.WindowState = FormWindowState.Minimized; Pump(50); }
                    var probes = All(w).Where(c => c.IsHandleCreated).Select(c => new PaintProbe(c)).ToList();
                    var root = new RootProbe(w, probes);
                    if (transition.StartsWith("restore")) foreach (var p in probes) p.Control.Invalidate();
                    var clock = Stopwatch.StartNew();
                    SendMessage(w.Handle, WindowBehavior.SystemCommand, new IntPtr(transition == "maximize" ? WindowBehavior.MaximizeCommand : WindowBehavior.RestoreCommand), IntPtr.Zero);
                    clock.Stop();
                    int immediate = probes.Sum(p => p.Paints), erases = probes.Sum(p => p.Erases), first = root.PaintsAtSize;
                    Pump(300);
                    int settled = probes.Sum(p => p.Paints), visible = probes.Count(p => Exposed(p.Control));
                    measurements.Add(new { page = page, transition = transition, pendingAtSizeReturn = root.SizePending, paintsAtSizeReturn = first,
                        paintsAtCommandReturn = immediate, paintsAfterSettle = settled, postSizePaints = settled - first, erasesAtCommandReturn = erases, exposedControls = visible, commandMs = clock.Elapsed.TotalMilliseconds });
                    Check(root.SizePending == 0, "Complete surface before native animation snapshot: " + page + "/" + transition);
                    Check(w.Opacity == 1 && (GetWindowLong(w.Handle, -20) & 0x02080000) == 0, "Opaque local-buffered window retained: " + page + "/" + transition);
                    Check(w.WindowState == (transition == "maximize" || transition == "restore-maximized" ? FormWindowState.Maximized : FormWindowState.Normal), "Correct native state: " + page + "/" + transition);
                    // User32 may expose one complete tree after the prepared
                    // source; reject the two extra forced application redraws.
                    Check(settled - first <= visible + 24, "No repeated forced complete-tree redraws after the prepared surface: " + page + "/" + transition);
                    root.ReleaseHandle(); foreach (PaintProbe probe in probes) probe.ReleaseHandle();
                }
            }
            Check(original.SequenceEqual(File.ReadAllBytes(AppPaths.Settings)), "Presentation leaves isolated settings unchanged");
            Check(!File.Exists(AppPaths.Secret) && !File.Exists(AppPaths.Startup), "No credential or startup file created");
            w.Close();
        }
    }
}
