using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// Renders every page and the shared control states to PNG for visual review.
// Uses isolated data paths and DashboardWindow(false): no login, probing,
// background monitor, startup entry or settings change touches the real app.
// All values shown are documentation-range samples, not this computer's network.
internal static class StylePreview
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
    private static void SetField(object owner, string name, object value) { owner.GetType().GetField(name, Private).SetValue(owner, value); }
    private static IEnumerable<Control> All(Control parent) { foreach (Control c in parent.Controls) { yield return c; foreach (Control d in All(c)) yield return d; } }
    private static void Pump(int ms) { var watch = Stopwatch.StartNew(); do { Application.DoEvents(); Thread.Sleep(2); } while (watch.ElapsedMilliseconds < ms); }
    private static void Page(DashboardWindow w, string page) { typeof(DashboardWindow).GetMethod("ShowPage", Private).Invoke(w, new object[] { page }); }
    private static void Invoke(DashboardWindow w, string method, params object[] args) { typeof(DashboardWindow).GetMethod(method, Private).Invoke(w, args); }
    private static void Call(Control c, string method, object arg) { c.GetType().GetMethod(method, Private | BindingFlags.Public).Invoke(c, new[] { arg }); }
    private static void Capture(Control c, string path) { using (var b = new Bitmap(c.Width, c.Height)) { c.DrawToBitmap(b, new Rectangle(Point.Empty, c.Size)); b.Save(path); } }

    private sealed class NoProbe : ICampusProbe
    {
        public CampusProbeResult Ping(string target, CancellationToken token) { return CampusProbeResult.Skipped("预览"); }
        public string PortalHttp(CancellationToken token) { return null; }
    }

    [STAThread] private static void Main(string[] args)
    {
        string output = Path.GetFullPath(args.Length > 0 ? args[0] : "style-preview");
        Directory.CreateDirectory(output);
        try { Run(output); File.WriteAllText(Path.Combine(output, "preview-done.txt"), "ok " + DateTime.Now.ToString("s")); }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "preview-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
    }

    private static WifiStatus SampleWifi(int rssi, string bssid)
    {
        return new WifiStatus { Ssid = "USCWIFI", Bssid = bssid, Quality = rssi > -60 ? 85 : 45, Rssi = rssi, Channel = 149, RxMbps = 573, TxMbps = 573 };
    }

    // A short game session: steady 4-8 ms with one roaming spike and one
    // spike during a large download, so the verdict and events have content.
    private static void SeedRecording(CampusStabilityMonitor monitor)
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.AddSeconds(-120);
        var random = new Random(7);
        for (int i = 0; i < 60; i++)
        {
            bool roam = i == 38, loaded = i == 52;
            long gateway = roam ? 186 : loaded ? 142 : 4 + random.Next(5);
            long portal = roam ? 204 : loaded ? 160 : 9 + random.Next(6);
            monitor.Log.Add(new CampusStabilitySample {
                Time = start.AddSeconds(i * 2), Identity = "preview", Gateway = "192.0.2.1",
                GatewayPing = CampusProbeResult.Reply(gateway), PortalPing = CampusProbeResult.Reply(portal),
                Download = loaded ? 6.5 * 1048576 : 40 * 1024, Upload = 12 * 1024,
                Wifi = SampleWifi(roam ? -74 : -52, i >= 38 ? "C8:9F:1A:B4:22:31" : "C8:9F:1A:B4:19:0C") });
            if (i == 39) monitor.Log.Mark(start.AddSeconds(i * 2 + 1));
        }
    }

    private static void Run(string output)
    {
        string data = Path.Combine(output, "isolated-data"); Directory.CreateDirectory(data);
        foreach (string name in new[] { "Data", "Secret", "Settings", "Log", "Startup", "RecoveryState", "MonitorStatus" })
            typeof(AppPaths).GetField(name).SetValue(null, name == "Data" ? data : Path.Combine(data, name + ".json"));
        AppSettings.Save(new SettingsRecord { priorityMode = "Off", autoLogin = false, carrier = "auto", checkIntervalSeconds = 45 });
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        File.WriteAllText(Path.Combine(output, "fonts.txt"), "numeric=" + UiFont.NumericFamily);

        using (var w = new DashboardWindow(false))
        {
            var monitor = new CampusStabilityMonitor(new NoProbe(), delegate { return null; });
            w.StabilityFactory = delegate { return monitor; };
            w.StartPosition = FormStartPosition.Manual; w.Location = new Point(-20000, -20000); w.Size = new Size(1280, 820); w.Show(); Pump(300);
            SetField(w, "currentLinkState", LinkState.Ready);
            SetField(w, "linkWireless", true);
            SetField(w, "lastWifi", SampleWifi(-52, "C8:9F:1A:B4:19:0C"));
            SetField(w, "wifiRoamCount", 1);
            string[] pages = DashboardWindow.NavigationPages.Concat(new[] { DashboardWindow.ToolsPage }).ToArray();
            for (int p = 0; p < pages.Length; p++)
            {
                string page = pages[p];
                Page(w, page); Pump(350);
                if (page == DashboardWindow.HomePage)
                {
                    Invoke(w, "SetStatus", "online", "网络正常", "已在线 2 小时 14 分 · 自动保持在线已开启");
                    Field<Label>(w, "pingText").Text = "12 ms"; Field<Label>(w, "heroDown").Text = "7.1 MB/s";
                    Invoke(w, "ApplyWifi");
                    var lines = Field<Label[]>(w, "recentLines");
                    string[] samples = { "13:02    认证掉线，已自动恢复", "13:01    检测到校园网认证失效", "11:41    网络正常", "08:15    已登录校园网", "08:15    自动保持在线已启动" };
                    for (int i = 0; i < lines.Length; i++) { lines[i].Text = "●   " + samples[i]; lines[i].ForeColor = i == 1 ? Color.FromArgb(154, 95, 18) : SurfaceColor.Ink; }
                    Pump(900);
                }
                if (page == DashboardWindow.GamePage)
                {
                    SeedRecording(monitor);
                    var view = Field<CampusStabilityView>(w, "stabilityView");
                    view.RefreshSnapshot(); Invoke(w, "ApplyWifi"); Pump(400);
                }
                if (page == DashboardWindow.CheckupPage)
                {
                    SetField(w, "lastDiagnosis", new DiagnosisSnapshot {
                        States = new[] { "正常", "正常", "正常", "正常", "正常", "正常", "正常", "有波动" },
                        Details = new[] { "WLAN 已启用", "已连接 USCWIFI（5 GHz）", "192.0.2.10", "192.0.2.1 · 6 ms", "已配置 2 个 DNS", "校园网认证在线", "学校认证页可以访问", "10 次中 1 次延迟偏高" },
                        Summary = "8 项中 7 项正常，1 项有波动", Latency = "延迟  6 ms（网关）", Loss = "未响应  0 / 10", Advice = "网关偶尔延迟偏高，多半是无线干扰。可以打开游戏模式记录一段时间再看。" });
                    Invoke(w, "RenderDiagnosisState");
                    Pump(600);
                    foreach (Label label in All(w).OfType<Label>())
                    {
                        if (label.Name == "DiagnosisLink") { label.Text = "●  已连接"; label.ForeColor = SurfaceColor.Green; }
                        if (label.Name == "DiagnosisWifi") label.Text = "Wi-Fi　USCWIFI · -52 dBm（很好） · 5 GHz";
                        if (label.Name == "DiagnosisAdapter") label.Text = "网卡　WLAN";
                        if (label.Name == "DiagnosisIp") label.Text = "IP　　192.0.2.10";
                        if (label.Name == "DiagnosisGateway") label.Text = "网关　192.0.2.1";
                    }
                    Pump(100);
                }
                string file = "page-" + (p + 1);
                Capture(w, Path.Combine(output, file + ".png"));
                Panel scroll = page == DashboardWindow.HomePage ? Field<Panel>(w, "home") : Field<Panel>(w, "otherPage");
                if (page == DashboardWindow.CheckupPage || page == DashboardWindow.SettingsPage || page == DashboardWindow.GamePage)
                {
                    scroll.AutoScrollPosition = new Point(0, 620); Pump(150);
                    Capture(w, Path.Combine(output, file + "-scroll.png"));
                    scroll.AutoScrollPosition = Point.Empty; Pump(50);
                }
            }
            Page(w, DashboardWindow.HomePage); Pump(200);
            Invoke(w, "SetStatus", "auth-required", "需要重新认证", "校园网认证已失效，点“重新认证” · 自动保持在线未开启");
            Pump(300);
            Capture(Field<Panel>(w, "home"), Path.Combine(output, "home-auth-required.png"));
            w.Size = new Size(1000, 680); Pump(400);
            Invoke(w, "SetStatus", "online", "网络正常", "已在线 2 小时 14 分 · 自动保持在线已开启"); Pump(200);
            Capture(w, Path.Combine(output, "home-narrow.png"));
            w.Close();
        }

        // Control state gallery on a plain card.
        using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(980, 520), BackColor = SurfaceColor.Canvas, FormBorderStyle = FormBorderStyle.None })
        {
            var host = new BufferedPanel { Dock = DockStyle.Fill, BackColor = SurfaceColor.Canvas }; form.Controls.Add(host);
            UiSurface.AttachCardShadows(host);
            var card = new CardPanel { Bounds = new Rectangle(24, 24, 930, 470) }; host.Controls.Add(card);
            string[] states = { "默认", "悬停", "按下", "处理中", "禁用", "键盘焦点" };
            var buttons = new List<FlatAction>();
            for (int row = 0; row < 2; row++)
                for (int i = 0; i < states.Length; i++)
                {
                    var b = new FlatAction { Text = (row == 0 ? "立即检测" : "重新认证"), Bounds = new Rectangle(24 + i * 148, 52 + row * 76, 136, 46), Symbol = row == 0 ? UiSymbol.Refresh : UiSymbol.Wrench, Refined = true, Radius = 8 };
                    b.BackColor = row == 0 ? SurfaceColor.Blue : Color.White; b.ForeColor = row == 0 ? Color.White : SurfaceColor.Ink;
                    card.Controls.Add(b); buttons.Add(b);
                    card.Controls.Add(new Label { Text = states[i], Bounds = new Rectangle(24 + i * 148, 20, 136, 24), ForeColor = SurfaceColor.Muted, BackColor = Color.Transparent, Font = new Font(UiFont.Family, 9f) });
                }
            var combo = new UiCombo { Bounds = new Rectangle(24, 220, 260, 34) }; combo.Items.AddRange(new object[] { "智能（锁屏或长时间不用时暂停）", "始终（锁屏时也恢复）" }); combo.SelectedIndex = 0; card.Controls.Add(combo);
            var combo2 = new UiCombo { Bounds = new Rectangle(300, 220, 180, 34), Enabled = false }; combo2.Items.Add("45 秒（默认）"); combo2.SelectedIndex = 0; card.Controls.Add(combo2);
            card.Controls.Add(new UiCheck { Text = "网络变化后马上复查", Checked = true, Bounds = new Rectangle(500, 222, 200, 30) });
            card.Controls.Add(new UiCheck { Text = "记录期间暂停下载", Checked = false, Bounds = new Rectangle(710, 222, 180, 30) });
            card.Controls.Add(new UiProgress { Bounds = new Rectangle(24, 280, 456, 20), Maximum = 1000, Value = 420 });
            var input = AppUiTheme.Input("210.43.112.9"); input.SetBounds(500, 272, 380, 40); card.Controls.Add(input);
            var sw1 = new ModernSwitch { Location = new Point(24, 330), Value = true, Fill = SurfaceColor.Blue }; var sw2 = new ModernSwitch { Location = new Point(96, 330), Value = false };
            card.Controls.Add(sw1); card.Controls.Add(sw2);
            string[] emblems = { "✓", "!", "↻" }; Color[] tones = { SurfaceColor.Green, Color.FromArgb(138, 92, 18), SurfaceColor.Blue };
            for (int i = 0; i < 3; i++) card.Controls.Add(new StatusEmblem { Text = emblems[i], ForeColor = tones[i], Bounds = new Rectangle(200 + i * 100, 320, 84, 84) });
            card.Controls.Add(new UiIcon { Symbol = UiSymbol.Game, Bounds = new Rectangle(520, 330, 44, 44) });
            card.Controls.Add(new UiIcon { Symbol = UiSymbol.Wifi, Tint = SurfaceColor.Green, Bounds = new Rectangle(580, 330, 44, 44) });
            var link = new UiLink { Text = "查看全部日志  ›", Bounds = new Rectangle(720, 340, 150, 28), Font = new Font(UiFont.Family, 9.5f), TextAlign = ContentAlignment.MiddleRight }; card.Controls.Add(link);
            form.Show(); Pump(250);
            for (int row = 0; row < 2; row++)
            {
                FlatAction hover = buttons[row * 6 + 1], press = buttons[row * 6 + 2], busy = buttons[row * 6 + 3], off = buttons[row * 6 + 4], focus = buttons[row * 6 + 5];
                Call(hover, "OnMouseEnter", EventArgs.Empty);
                Call(press, "OnMouseEnter", EventArgs.Empty); Call(press, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 20, 20, 0));
                busy.Enabled = false; busy.Busy = true; off.Enabled = false;
                focus.Focus();
            }
            Pump(400);
            Capture(form, Path.Combine(output, "controls.png"));
            foreach (FlatAction b in buttons) b.Busy = false;
            form.Close();
        }
    }
}
