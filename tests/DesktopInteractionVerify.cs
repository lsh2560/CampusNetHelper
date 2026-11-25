using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class DesktopInteractionVerify
{
    private sealed class InputTrace : NativeWindow
    {
        internal string Path;
        internal string Source;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0201 || m.Msg == 0x0202 || m.Msg == 0x0203 || m.Msg == 0xA1 || m.Msg == 0xA2 || m.Msg == 0xA3 || m.Msg == 0x112)
                File.AppendAllText(Path, Source + " " + m.Msg.ToString("X") + " w=" + m.WParam + "\r\n");
            base.WndProc(ref m);
        }
    }
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    private static T Field<T>(DashboardWindow w, string n) { return (T)typeof(DashboardWindow).GetField(n, Fields).GetValue(w); }
    private static int checks;
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks++; }
    private static void Pump() { for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(20); } }
    private static int Hit(DashboardWindow w, Point p)
    {
        Point s = w.PointToScreen(p);
        return SendMessage(w.Handle, 0x84, IntPtr.Zero, new IntPtr((s.Y << 16) | (s.X & 0xffff))).ToInt32();
    }
    private static object Rect(Rectangle r) { return new { x = r.X, y = r.Y, width = r.Width, height = r.Height }; }
    private static void Seed(DashboardWindow w)
    {
        Field<Label>(w, "statusTitle").Text = "网络正常";
        Field<Label>(w, "statusDetail").Text = "界面测试 · 示例数据";
        Field<Label>(w, "pingText").Text = "4 ms";
        Field<Label>(w, "heroDown").Text = "240.1 KB/s";
        Field<Label>(w, "wifiValue").Text = "-52 dBm";
    }
    private static readonly string[] MetricNames = { "pingText", "heroDown", "wifiValue" };
    // MetricValueLabel drops the arrow and paints a large figure plus a small
    // unit, so measure that layout rather than the raw Text in one font.
    private static Size PaintedMetric(Label c)
    {
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        string value = c.Text.Replace("延迟", "").TrimStart('↓', '↑', ' ').Trim();
        int split = value.LastIndexOf(' ');
        if (!UiFont.IsAscii(value) || split <= 0) return TextRenderer.MeasureText(value, c.Font, Size.Empty, flags);
        Size number = TextRenderer.MeasureText(value.Substring(0, split), c.Font, Size.Empty, flags);
        using (var unitFont = new Font(UiFont.Family, Math.Max(8.5F, c.Font.Size * .5F)))
        {
            Size unit = TextRenderer.MeasureText(value.Substring(split + 1), unitFont, Size.Empty, flags);
            return new Size(number.Width + 4 + unit.Width, Math.Max(number.Height, unit.Height));
        }
    }
    private static void Metrics(DashboardWindow w)
    {
        foreach (string name in MetricNames)
        {
            Label c = Field<Label>(w, name);
            Size needed = c is MetricValueLabel ? PaintedMetric(c) : TextRenderer.MeasureText(c.Text, c.Font, Size.Empty, TextFormatFlags.SingleLine);
            Check(needed.Width <= c.ClientSize.Width && needed.Height <= c.ClientSize.Height,
                "Clipped " + name + ": " + c.Text + " needs " + needed + " available " + c.ClientSize);
            Check(c.Parent.ClientRectangle.Contains(c.Bounds), "Metric is outside its strip: " + name);
            Check(!c.Parent.Controls.Cast<Control>().Any(other => other != c && other.Visible && other.Bounds.IntersectsWith(c.Bounds)), "Overlapping " + name);
        }
        // The status text gives way to the action buttons in every hero state.
        var hero = Field<CardPanel>(w, "hero");
        Label title = Field<Label>(w, "statusTitle"), detail = Field<Label>(w, "statusDetail");
        foreach (FlatAction action in hero.Controls.OfType<FlatAction>().Where(a => a.Visible))
            Check(!title.Bounds.IntersectsWith(action.Bounds) && !detail.Bounds.IntersectsWith(action.Bounds), "Status text overlaps " + action.Text);
        Check(Field<SurfacePanel>(w, "heroMetricFrame").Top > detail.Bottom, "Metrics overlap the status text");
        Check(hero.ClientRectangle.Contains(Field<SurfacePanel>(w, "heroMetricFrame").Bounds), "Metrics leave the status card");
    }
    [STAThread] private static void Main(string[] args)
    {
        try { Run(args); } catch (Exception ex) { Console.WriteLine(ex); Environment.ExitCode = 1; }
    }
    private static void Run(string[] args)
    {
        SetProcessDPIAware();
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        string data = Path.Combine(output, "isolated-data"); Directory.CreateDirectory(data);
        foreach (string n in new[] { "Data", "Secret", "Log", "Settings", "RecoveryState", "MonitorStatus", "Startup" })
            typeof(AppPaths).GetField(n).SetValue(null, n == "Data" ? data : Path.Combine(data, n + ".json"));
        AppSettings.Save(new SettingsRecord { autoLogin = false, priorityMode = "Off", minimizeToTray = false });
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using (var w = new DashboardWindow(false))
        {
            w.StartPosition = FormStartPosition.Manual; w.Location = new Point(80, 80); w.Size = new Size(1280, 820);
            w.Text = "校园网助手 UI 自动测试（示例数据）";
            w.Show(); Seed(w); Pump();
            if (args.Contains("--ui-host"))
            {
                var trace = new InputTrace { Path = Path.Combine(output, "input-trace.txt"), Source = "caption" };
                trace.AssignHandle(Field<Panel>(w, "captionBar").Handle);
                var rootTrace = new InputTrace { Path = trace.Path, Source = "window" };
                rootTrace.AssignHandle(w.Handle);
                Field<Panel>(w, "captionBar").MouseDown += delegate(object sender, MouseEventArgs e)
                { File.AppendAllText(trace.Path, "MouseDown clicks=" + e.Clicks + " " + e.Button + "\r\n"); };
                var timer = new System.Windows.Forms.Timer { Interval = 150 };
                timer.Tick += delegate
                {
                    var values = new Dictionary<string, object>();
                    foreach (string name in MetricNames)
                    {
                        var c = Field<Label>(w, name);
                        values[name] = new { text = c.Text, bounds = Rect(c.RectangleToScreen(c.ClientRectangle)) };
                    }
                    var state = new { state = w.WindowState.ToString(), bounds = Rect(w.Bounds),
                        titlebar = Rect(Field<Panel>(w, "captionBar").RectangleToScreen(Field<Panel>(w, "captionBar").ClientRectangle)),
                        padding = w.Padding.Left, values = values };
                    string path = Path.Combine(output, "window-state.json");
                    string json = new JavaScriptSerializer().Serialize(state);
                    try { File.WriteAllText(path + ".next", json); File.Copy(path + ".next", path, true); } catch (IOException) { }
                };
                timer.Start(); Application.Run(w); timer.Dispose(); trace.ReleaseHandle(); rootTrace.ReleaseHandle(); return;
            }
            foreach (int width in new[] { 1120, 1280, 1500, 1780 })
            {
                w.Size = new Size(width, 900); Pump();
                string[] downloads = { "17.0 KB/s", "1023.9 KB/s", "1023.9 MB/s", "1023.9 GB/s" };
                string[] latencies = { "4 ms", "999 ms", "无响应", "12 ms" };
                string[] signals = { "-52 dBm", "-100 dBm", "有线", "85%" };
                for (int i = 0; i < downloads.Length; i++)
                {
                    Field<Label>(w, "heroDown").Text = downloads[i];
                    Field<Label>(w, "pingText").Text = latencies[i];
                    Field<Label>(w, "wifiValue").Text = signals[i];
                    foreach (bool repair in new[] { false, true })
                    {
                        Field<FlatAction>(w, "repairButton").Visible = repair;
                        typeof(DashboardWindow).GetMethod("LayoutHeroText", Fields).Invoke(w, null);
                        Pump(); Metrics(w);
                    }
                }
                Field<FlatAction>(w, "repairButton").Visible = false;
                Seed(w); Pump();
                using (var bmp = new Bitmap(w.Width, w.Height))
                { w.DrawToBitmap(bmp, new Rectangle(Point.Empty, w.Size)); bmp.Save(Path.Combine(output, "metrics-" + width + ".png")); }
            }
            int x = w.Width - 1, y = w.Height - 1;
            foreach (var p in new[] { new[] { 0, 0, 13 }, new[] { x, 0, 14 }, new[] { 0, y, 16 }, new[] { x, y, 17 },
                new[] { 0, 100, 10 }, new[] { x, 100, 11 }, new[] { 400, 0, 12 }, new[] { 400, y, 15 } })
                Check(Hit(w, new Point(p[0], p[1])) == p[2], "Incorrect native resize hit at " + p[0] + "," + p[1]);
            var bar = Field<Panel>(w, "captionBar");
            Check(Hit(w, w.PointToClient(bar.PointToScreen(new Point(50, 20)))) == 2, "Blank title bar must be native caption");
            Check(w.GetChildAtPoint(new Point(2, 100)) == null, "A child window intercepts resize border");
            WindowBehavior.ToggleMaximize(w); Pump();
            Check(w.WindowState == FormWindowState.Maximized && w.Padding.Left == 0, "Maximized layout retains resize gutter");
            WindowBehavior.ToggleMaximize(w); Pump();
            Check(w.WindowState == FormWindowState.Normal && w.Padding.Left == 8, "Restored window lost resize gutter");
            w.Close();
        }
        File.WriteAllText(Path.Combine(output, "desktop-layout-results.txt"), "PASS " + checks + " metric/layout/native-hit checks. Real input is covered separately by ui-tests.ps1.");
        Console.WriteLine("PASS " + checks + " checks");
    }
}
