using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class NavigationIndicatorVerify
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> results = new List<object>();
    private static readonly string[] pages = DashboardWindow.NavigationPages;
    private static void Check(bool ok, string name) { results.Add(new { name = name, status = ok ? "PASS" : "FAIL" }); }
    private static void Pump(int ms) { var c = Stopwatch.StartNew(); do { Application.DoEvents(); Thread.Sleep(1); } while (c.ElapsedMilliseconds < ms); }
    private static void Page(DashboardWindow w, int index) { typeof(DashboardWindow).GetMethod("ShowPage", Private).Invoke(w, new object[] { pages[index] }); }
    private static Rectangle Row(AnimatedNavigationMenu menu, int index) { return (Rectangle)typeof(AntdUI.MenuItem).GetProperty("rect", Private | BindingFlags.Public).GetValue(menu.Items[index], null); }
    private static double Target(AnimatedNavigationMenu menu, int index) { Rectangle r = Row(menu, index); return r.Top + (r.Height - 22) / 2.0; }
    private static void Capture(Control control, string path) { using (var b = new Bitmap(control.Width, control.Height)) { control.DrawToBitmap(b, control.ClientRectangle); b.Save(path); } }
    [STAThread] private static void Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        try { Run(output); } catch (Exception ex) { results.Add(new { name = "Exception", status = "FAIL", detail = ex.ToString() }); }
        int failed = results.Count(x => (string)x.GetType().GetProperty("status").GetValue(x, null) == "FAIL");
        File.WriteAllText(Path.Combine(output, "navigation-results.json"), new JavaScriptSerializer().Serialize(new { passed = results.Count - failed, failed = failed, results = results }));
        Console.WriteLine("Navigation checks: " + (results.Count - failed) + " passed, " + failed + " failed"); Environment.ExitCode = failed == 0 ? 0 : 1;
    }
    private static void Run(string output)
    {
        string data = Path.Combine(output, "isolated-data"); Directory.CreateDirectory(data);
        foreach (string name in new[] { "Data", "Secret", "Settings", "Log", "Startup", "RecoveryState", "MonitorStatus" })
            typeof(AppPaths).GetField(name).SetValue(null, name == "Data" ? data : Path.Combine(data, name + ".json"));
        AppSettings.Save(new SettingsRecord { priorityMode = "Off", autoLogin = false, carrier = "auto", checkIntervalSeconds = 45 });
        byte[] original = File.ReadAllBytes(AppPaths.Settings);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using (var w = new DashboardWindow(false))
        {
            w.StartPosition = FormStartPosition.Manual; w.Location = new Point(-20000, -20000); w.Size = new Size(1280, 820); w.Show(); Pump(130);
            var menu = (AnimatedNavigationMenu)typeof(DashboardWindow).GetField("navigation", Private).GetValue(w);
            bool enabled = UiMotion.Enabled;
            Check(enabled, "Current Windows environment permits animations");
            Check(Math.Abs(menu.IndicatorTop - Target(menu, 0)) < .1, "First frame anchors indicator to Home without entrance travel");
            double start = menu.IndicatorTop; var firstClick = Stopwatch.StartNew(); Page(w, 1);
            long firstBuild = firstClick.ElapsedMilliseconds;
            Check((string)typeof(DashboardWindow).GetField("currentPage", Private).GetValue(w) == pages[1], "Game-mode page activates immediately, without waiting for motion");
            Pump(45); double middle = menu.IndicatorTop;
            Check(middle > start + .5 && middle < Target(menu, 1) - .5, "First navigation exposes a real intermediate downward position: start=" + start + ", middle=" + middle + ", target=" + Target(menu, 1) + ", pageBuildMs=" + firstBuild);
            Pump(240); Check(Math.Abs(menu.IndicatorTop - Target(menu, 1)) < .1, "Indicator lands exactly at Game mode");
            Page(w, 3); Pump(45); double reversing = menu.IndicatorTop;
            Page(w, 0); double restarted = menu.IndicatorTop;
            Check(restarted >= reversing - .5 && restarted < Target(menu, 3), "Rapid reverse starts from current travel position");
            Pump(45); Check(menu.IndicatorTop < restarted && menu.IndicatorTop > Target(menu, 0), "Reverse travel has an intermediate upward position");
            Page(w, 1); Page(w, 3); Page(w, 2); Pump(260);
            Check(menu.IndicatorIndex == 2 && Math.Abs(menu.IndicatorTop - Target(menu, 2)) < .1, "Rapid repeated selection ends at the latest destination");
            Page(w, 2); Check(UiMotion.ActiveCount == 0, "Selecting the current page does not start idle animation work");
            menu.SelectIndex(1, false); Pump(35);
            Check(menu.IndicatorIndex == 1 && (string)typeof(DashboardWindow).GetField("currentPage", Private).GetValue(w) == pages[1], "AntdUI selection dispatch updates both page and indicator");
            Pump(240);
            foreach (Size size in new[] { new Size(1000, 640), new Size(1280, 820), new Size(1780, 1000) })
            {
                Page(w, 2); Pump(240);
                w.Size = size; Pump(60);
                Capture(menu, Path.Combine(output, "resize-" + size.Width + ".png"));
                Check(Math.Abs(menu.IndicatorTop - Target(menu, 2)) < .1, "Resize anchors indicator to compact/tall row geometry " + size + " actual=" + menu.IndicatorTop + " target=" + Target(menu, 2));
                for (int i = 0; i < pages.Length; i++)
                {
                    Page(w, i); Pump(235);
                    Check(menu.IndicatorIndex == i && Math.Abs(menu.IndicatorTop - Target(menu, i)) < .1 && menu.ClientRectangle.Contains(Row(menu, i)), "Every sidebar destination remains aligned/reachable " + size + " / " + pages[i]);
                }
            }
            // Settings re-applies the saved motion preference on entry, so check
            // the snap on a page that leaves the test's temporary preference alone.
            UiMotion.Configure(true); Page(w, 0); Page(w, 2);
            Check(Math.Abs(menu.IndicatorTop - Target(menu, 2)) < .1 && UiMotion.ActiveCount == 0, "Reduce motion snaps immediately without a running timer");
            UiMotion.Configure(false); Page(w, 0); Pump(250); Page(w, 3); Pump(30); w.Hide(); Pump(25);
            Check(UiMotion.ActiveCount == 0 && Math.Abs(menu.IndicatorTop - Target(menu, 3)) < .1, "Hiding a window finishes travel and stops animation ticks");
            w.Show(); Pump(50); Page(w, 0); Pump(30); w.WindowState = FormWindowState.Minimized; Pump(60);
            Check(UiMotion.ActiveCount == 0, "Minimized windows own no animation ticks");
            w.WindowState = FormWindowState.Normal; w.Size = new Size(1280, 820); Page(w, 0); Pump(250);
            Capture(w, Path.Combine(output, "preview.png"));
            // Actual control rendering frames, not a fabricated animation mockup.
            Control sidebar = (Control)typeof(DashboardWindow).GetField("sidebar", Private).GetValue(w);
            int frame = 0; Capture(sidebar, Path.Combine(output, "frame-" + (frame++).ToString("D3") + ".png"));
            foreach (int index in new[] { 1, 2, 3, 0 })
            {
                Page(w, index);
                for (int f = 0; f < 18; f++) { Pump(16); Capture(sidebar, Path.Combine(output, "frame-" + (frame++).ToString("D3") + ".png")); }
                for (int f = 0; f < 12; f++) Capture(sidebar, Path.Combine(output, "frame-" + (frame++).ToString("D3") + ".png"));
            }
            Check(original.SequenceEqual(File.ReadAllBytes(AppPaths.Settings)), "Navigation and motion leave isolated settings unchanged");
            Check(!File.Exists(AppPaths.Secret) && !File.Exists(AppPaths.Startup), "No credentials or startup entries created by verification");
            Page(w, 3); w.Close();
        }
        Check(UiMotion.ActiveCount == 0, "Disposal releases the marker transition from the shared scheduler");
    }
}
