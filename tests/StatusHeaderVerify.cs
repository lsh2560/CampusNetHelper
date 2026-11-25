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

// The home status header: title, detail, emblem and the conditional
// re-authenticate action, across widths, enlarged fonts and every state.
// Also the saved-account pill in Settings, which shares the same pill control.
internal static class StatusHeaderVerify
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> results = new List<object>();
    private static void Check(bool ok, string name) { results.Add(new { name = name, status = ok ? "PASS" : "FAIL" }); }
    private static void Pump() { var watch = Stopwatch.StartNew(); do { Application.DoEvents(); Thread.Sleep(2); } while (watch.ElapsedMilliseconds < 70); }
    private static T Field<T>(DashboardWindow w, string name) { return (T)typeof(DashboardWindow).GetField(name, Private).GetValue(w); }
    private static void Invoke(DashboardWindow w, string name, params object[] args) { typeof(DashboardWindow).GetMethod(name, Private).Invoke(w, args); }
    [STAThread] private static void Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        try { Run(output); } catch (Exception ex) { results.Add(new { name = "Exception", status = "FAIL", detail = ex.ToString() }); }
        int failed = results.Count(x => (string)x.GetType().GetProperty("status").GetValue(x, null) == "FAIL");
        File.WriteAllText(Path.Combine(output, "status-header-results.json"), new JavaScriptSerializer().Serialize(new { passed = results.Count - failed, failed = failed, results = results }));
        Console.WriteLine("Status header: " + (results.Count - failed) + " passed, " + failed + " failed"); Environment.ExitCode = failed == 0 ? 0 : 1;
    }
    private static void Run(string output)
    {
        string data = Path.Combine(output, "isolated-data"); Directory.CreateDirectory(data);
        foreach (string name in new[] { "Data", "Secret", "Settings", "Log", "Startup", "RecoveryState", "MonitorStatus" }) typeof(AppPaths).GetField(name).SetValue(null, name == "Data" ? data : Path.Combine(data, name + ".json"));
        AppSettings.Save(new SettingsRecord { priorityMode = "Off", autoLogin = false }); byte[] settings = File.ReadAllBytes(AppPaths.Settings);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        string[][] states = {
            new[] { "online", "网络正常", "已在线 12 小时 59 分 · 自动保持在线已开启", "✓" },
            new[] { "auth-required", "需要重新认证", "校园网认证已失效，点“重新认证” · 自动保持在线未开启", "!" },
            new[] { "limited", "自动恢复暂停中", "10 分钟内已自动恢复 3 次，稍后再试 · 自动保持在线已开启", "!" },
            new[] { "disconnected", "网络未连接", "还没有取得 IP 地址，稍等一下或重新连接 Wi-Fi", "!" },
            new[] { "checking", "正在检查网络", "稍等几秒 · 自动保持在线已开启", "↻" },
        };
        using (var window = new DashboardWindow(false))
        {
            window.StartPosition = FormStartPosition.Manual; window.Location = new Point(-20000, -20000); window.Show(); Pump();
            var hero = Field<CardPanel>(window, "hero");
            Label title = Field<Label>(window, "statusTitle"), detail = Field<Label>(window, "statusDetail"), emblem = Field<Label>(window, "statusIcon");
            FlatAction detect = Field<FlatAction>(window, "loginButton"), repair = Field<FlatAction>(window, "repairButton");
            Font titleFont = title.Font, detailFont = detail.Font;
            foreach (int width in new[] { 1000, 1280, 1500, 1920 }) foreach (float scale in new[] { 1f, 1.25f, 1.5f })
            using (var largeTitle = new Font(titleFont.FontFamily, titleFont.Size * scale, titleFont.Style))
            using (var largeDetail = new Font(detailFont.FontFamily, detailFont.Size * scale, detailFont.Style))
            {
                window.Size = new Size(width, 820); title.Font = largeTitle; detail.Font = largeDetail;
                foreach (string[] state in states)
                {
                    Invoke(window, "SetStatus", state[0], state[1], state[2]); Invoke(window, "ArrangeHomeSurface"); Pump();
                    string scenario = width + "/font " + scale + "/" + state[0];
                    bool expectRepair = state[0] == "auth-required" || state[0] == "limited";
                    Check(repair.Visible == expectRepair, "Re-authenticate appears only when it helps: " + scenario);
                    Check(emblem.Text == state[3], "Emblem matches the state: " + scenario);
                    foreach (FlatAction action in new[] { detect, repair }.Where(a => a.Visible))
                    {
                        Check(hero.ClientRectangle.Contains(action.Bounds), "Header action fits the card: " + scenario + " " + action.Text);
                        Check(!title.Bounds.IntersectsWith(action.Bounds) && !detail.Bounds.IntersectsWith(action.Bounds), "Status text never runs under an action: " + scenario + " " + action.Text);
                    }
                    Check(title.Left > emblem.Right && detail.Left > emblem.Right, "Status text starts after the emblem: " + scenario);
                    Check(title.Bottom <= detail.Top + 2 && detail.Bottom < Field<SurfacePanel>(window, "heroMetricFrame").Top, "Title, detail and metrics stack without overlap: " + scenario);
                    Check(title.Text == state[1] && detail.Text == state[2], "Full status text is retained for accessibility: " + scenario);
                    if (scale == 1 && (width == 1000 || width == 1500)) using (var bitmap = new Bitmap(hero.Width, hero.Height)) {
                        hero.DrawToBitmap(bitmap, hero.ClientRectangle);
                        bitmap.Save(Path.Combine(output, "header-" + width + "-" + state[0] + ".png"));
                    }
                }
                title.Font = titleFont; detail.Font = detailFont;
            }
            Invoke(window, "ShowPage", DashboardWindow.SettingsPage); Pump();
            foreach (int width in new[] { 1000, 1280, 1920 })
            {
                window.Size = new Size(width, 820); Pump();
                var pill = (StatePill)window.Controls.Find("AccountSavedState", true).Single();
                Size drawn;
                using (Graphics graphics = pill.CreateGraphics()) drawn = TextRenderer.MeasureText(graphics, pill.DisplayText, pill.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                Check(drawn.Width <= pill.TextBounds.Width, "Account status text fits its pill: " + width);
                Check(!pill.Parent.Controls.OfType<Label>().Any(l => l != pill && l.Bounds.IntersectsWith(pill.Bounds)), "Account pill never overlaps the heading: " + width);
            }
            Check(settings.SequenceEqual(File.ReadAllBytes(AppPaths.Settings)) && !File.Exists(AppPaths.Secret) && !File.Exists(AppPaths.Startup), "Header inspection leaves account, settings and startup unchanged"); window.Close();
        }
    }
}
