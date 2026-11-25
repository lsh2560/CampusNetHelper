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

internal static class DesignSurfaceVerify
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> results = new List<object>();
    private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
    private static IEnumerable<Control> All(Control parent) { foreach (Control c in parent.Controls) { yield return c; foreach (Control d in All(c)) yield return d; } }
    private static void Pump(int ms) { var watch = Stopwatch.StartNew(); do { Application.DoEvents(); Thread.Sleep(2); } while (watch.ElapsedMilliseconds < ms); }
    private static void Page(DashboardWindow w, string page) { typeof(DashboardWindow).GetMethod("ShowPage", Private).Invoke(w, new object[] { page }); }
    private static void Check(bool ok, string name) { results.Add(new { name = name, status = ok ? "PASS" : "FAIL" }); }
    private static void Capture(Control c, string path) { using (var b = new Bitmap(c.Width, c.Height)) { c.DrawToBitmap(b, c.ClientRectangle); b.Save(path); } }
    [STAThread] private static void Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        try { Run(output); } catch (Exception ex) { results.Add(new { name = "Exception", status = "FAIL", detail = ex.ToString() }); }
        int failed = results.Count(x => (string)x.GetType().GetProperty("status").GetValue(x, null) == "FAIL");
        File.WriteAllText(Path.Combine(output, "design-results.json"), new JavaScriptSerializer().Serialize(new { passed = results.Count - failed, failed = failed, results = results }));
        Console.WriteLine("Design checks: " + (results.Count - failed) + " passed, " + failed + " failed"); Environment.ExitCode = failed == 0 ? 0 : 1;
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
            w.StartPosition = FormStartPosition.Manual; w.Location = new Point(-20000, -20000); w.Show(); Pump(200);
            Field<WindowCaptionPanel>(w, "captionBar").Controls.Add(new Label { Text = "界面预览 · 独立测试数据", Bounds = new Rectangle(18, 4, 400, 27), BackColor = SurfaceColor.Canvas, ForeColor = SurfaceColor.Muted, Font = new Font("Microsoft YaHei UI", 9.5f) });
            foreach (Size size in new[] { new Size(1000, 640), new Size(1280, 820), new Size(1640, 924), new Size(1920, 1040) })
            {
                w.Size = size;
                foreach (string name in DashboardWindow.NavigationPages)
                {
                    Page(w, name); Pump(300);
                    Panel page = Field<Panel>(w, name == DashboardWindow.HomePage ? "home" : "otherPage");
                    page.AutoScrollPosition = Point.Empty; Pump(20);
                    Check(!page.HorizontalScroll.Visible, "No horizontal scrolling: " + name + " " + size);
                    foreach (Control c in All(page).Where(c => c.Visible && (c is FlatAction || c is ComboBox || c is ModernSwitch)))
                    {
                        Check(c.Left >= 0 && c.Top >= 0 && c.Right <= c.Parent.ClientSize.Width + 1 && c.Bottom <= c.Parent.ClientSize.Height + 1, "Action fits parent: " + name + " " + size + " / " + c.Name + c.Text);
                        page.ScrollControlIntoView(c); Pump(3);
                        Rectangle rect = page.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
                        Check(rect.Left >= 0 && rect.Right <= page.ClientSize.Width + 1 && rect.Top >= 0 && rect.Bottom <= page.ClientSize.Height + 1, "Action reachable by scrolling: " + name + " " + size + " / " + c.Name + c.Text);
                    }
                    page.AutoScrollPosition = Point.Empty; Pump(20);
                    if (name == DashboardWindow.HomePage)
                    {
                        typeof(DashboardWindow).GetMethod("SetStatus", Private).Invoke(w, new object[] { "online", "网络正常", "界面预览样例 · 非当前网络数据" });
                        Field<Label>(w, "heroDown").Text = "7.1 KB/s"; Field<Label>(w, "pingText").Text = "5 ms"; Field<Label>(w, "wifiValue").Text = "-52 dBm";
                        var lines = Field<Label[]>(w, "recentLines");
                        string[] samples = { "17:22    预览样例：网络正常", "17:22    预览样例：认证服务器响应不稳定，未做处理", "17:20    预览样例：已登录校园网", "17:18    预览样例：账号已保存", "17:15    预览样例：自动保持在线已启动" };
                        for (int i = 0; i < lines.Length; i++) { lines[i].Text = samples[i]; lines[i].ForeColor = i == 1 ? Color.FromArgb(154, 95, 18) : UiColor.Ink; }
                        Check(Field<CardPanel>(w, "hero").Width <= 1040, "Home content keeps a readable maximum width: " + size);
                    }
                    if (name == DashboardWindow.CheckupPage)
                    {
                        Check(Field<Label[]>(w, "diagnosisStates").Length == 8, "All eight check-up steps retained: " + size);
                        foreach (Label label in All(page).OfType<Label>())
                        {
                            if (label.Name == "DiagnosisAdapter") label.Text = "网卡　测试适配器";
                            if (label.Name == "DiagnosisIp") label.Text = "IP　　192.0.2.10";
                            if (label.Name == "DiagnosisGateway") label.Text = "网关　192.0.2.1";
                        }
                    }
                    if (name == DashboardWindow.GamePage)
                        foreach (string label in new[] { "GameWifiSignal", "GameWifiDetail", "GameWifiRoams" })
                        {
                            Control c = All(page).Single(x => x.Name == label);
                            Check(c.Right <= c.Parent.ClientSize.Width - 20, "Wi-Fi detail stays inside its card: " + label + " " + size);
                        }
                    string file = name == DashboardWindow.HomePage ? "home-" : name == DashboardWindow.GamePage ? "game-" : name == DashboardWindow.CheckupPage ? "checkup-" : "settings-";
                    File.WriteAllText(Path.Combine(output, "geometry-" + file + size.Width + ".json"), new JavaScriptSerializer().Serialize(new {
                        window = w.Bounds.ToString(), client = w.ClientSize.ToString(), rootScroll = w.AutoScrollPosition.ToString(),
                        content = Field<Panel>(w, "content").Bounds.ToString(), sidebar = Field<Panel>(w, "sidebar").Bounds.ToString(),
                        caption = Field<WindowCaptionPanel>(w, "captionBar").Bounds.ToString(), page = page.Bounds.ToString(), scroll = page.AutoScrollPosition.ToString(),
                        branding = Field<Panel>(w, "sidebar").Controls.Cast<Control>().Take(4).Select(c => new { text = c.Text, bounds = c.Bounds.ToString(), visible = c.Visible }).ToArray() }));
                    Capture(w, Path.Combine(output, file + size.Width + ".png"));
                }
            }
            Check(original.SequenceEqual(File.ReadAllBytes(AppPaths.Settings)), "Design inspection leaves settings unchanged");
            Check(!File.Exists(AppPaths.Secret) && !File.Exists(AppPaths.Startup), "No credentials or startup entries created");
            w.Close();
        }
    }
}
