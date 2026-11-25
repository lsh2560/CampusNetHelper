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

internal static class CampusStabilityVerify
{
    private static readonly List<object> checks = new List<object>();
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool ok, string name) { checks.Add(new { name = name, status = ok ? "PASS" : "FAIL" }); if (!ok) throw new Exception(name); }
    private static void Pump(int ms) { var watch = Stopwatch.StartNew(); do { Application.DoEvents(); Thread.Sleep(3); } while (watch.ElapsedMilliseconds < ms); }
    private static LinkSnapshot Link(string gateway = "10.14.0.1") { return new LinkSnapshot { State = LinkState.Ready, AdapterName = "独立测试网卡", Identity = "isolated|10.14.0.250|" + gateway, Ip = "10.14.0.250", Gateway = gateway }; }
    private sealed class Probe : ICampusProbe
    {
        internal readonly List<string> Targets = new List<string>();
        internal bool Missing;
        internal int HttpCalls, Active, MaxActive;
        internal int Delay;
        public CampusProbeResult Ping(string target, CancellationToken token)
        {
            int active = Interlocked.Increment(ref Active); MaxActive = Math.Max(MaxActive, active);
            try { lock (Targets) Targets.Add(target); if (Delay > 0) Thread.Sleep(Delay); token.ThrowIfCancellationRequested(); return Missing ? CampusProbeResult.Missing("TimedOut") : CampusProbeResult.Reply(target == CampusTargets.Portal ? 8 : 2); }
            finally { Interlocked.Decrement(ref Active); }
        }
        public string PortalHttp(CancellationToken token) { HttpCalls++; return "校内认证页 HTTP 200"; }
    }
    private static CampusStabilitySample Sample(int index, long? gateway, long? portal, string identity = "A", double? download = null, double? upload = null)
    {
        return new CampusStabilitySample { Time = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero).AddSeconds(index * 2), Identity = identity, Gateway = "10.14.0.1",
            GatewayPing = gateway.HasValue ? CampusProbeResult.Reply(gateway.Value) : CampusProbeResult.Missing("TimedOut"),
            PortalPing = portal.HasValue ? CampusProbeResult.Reply(portal.Value) : CampusProbeResult.Missing("TimedOut"), Download = download, Upload = upload };
    }
    private static CampusStabilitySample Radio(CampusStabilitySample sample, int rssi, string bssid)
    {
        sample.Wifi = new WifiStatus { Ssid = "fixture", Bssid = bssid, Rssi = rssi, Quality = rssi > -70 ? 80 : 30, Channel = 149 };
        return sample;
    }
    [STAThread] private static void Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        if (args.Contains("--live-campus")) { Live(output); return; }
        try { Run(output); } catch (Exception ex) { checks.Add(new { name = "Exception", status = "FAIL", detail = ex.ToString() }); }
        int failed = checks.Count(c => (string)c.GetType().GetProperty("status").GetValue(c, null) == "FAIL");
        File.WriteAllText(Path.Combine(output, "campus-stability-results.json"), new JavaScriptSerializer().Serialize(new { passed = checks.Count - failed, failed = failed, checks = checks }));
        Console.WriteLine("Campus stability: " + (checks.Count - failed) + " passed, " + failed + " failed"); Environment.ExitCode = failed == 0 ? 0 : 1;
    }
    private static void Live(string output)
    {
        using (var monitor = new CampusStabilityMonitor())
        {
            for (int i = 0; i < 3; i++) { monitor.Log.Add(monitor.Collect(CancellationToken.None)); if (i < 2) Thread.Sleep(2000); }
            var snapshot = monitor.Log.Snapshot();
            File.WriteAllText(Path.Combine(output, "live-campus.json"), new JavaScriptSerializer().Serialize(new {
                scope = "Private gateway and existing school authentication server only; no public connectivity fallback, no DNS lookup, no login",
                samples = snapshot.Samples.Select(s => new { utc = s.Time.ToString("O"), gateway = s.Gateway, gatewayState = s.GatewayPing.State, gatewayRttMs = s.GatewayPing.Milliseconds,
                    school = CampusTargets.Portal, schoolState = s.PortalPing.State, schoolRttMs = s.PortalPing.Milliseconds, schoolHttp = s.Http }).ToArray() }));
            Console.WriteLine("Campus-only live sampling recorded; availability is observed, never an automatic authentication verdict.");
        }
    }
    private static void Run(string output)
    {
        foreach (string target in new[] { "10.14.0.1", "10.15.0.1", "172.16.2.1", "172.31.1.1", "192.168.1.1", CampusTargets.Portal }) Check(CampusTargets.Allowed(target), "Known campus/private literal accepted: " + target);
        foreach (string target in new[] { "8.8.8.8", "1.1.1.1", "114.114.114.114", "example.com", "localhost", "127.0.0.1", "169.254.1.1", "224.0.0.1", "255.255.255.255", "172.32.0.1", "::1", "10.14.0.255", "10.14.0.1.evil.test", "" })
            Check(!CampusTargets.Allowed(target), "External, hostname or unsafe target rejected: " + target);
        Check(!new CampusOnlyProbe().Ping("8.8.8.8", CancellationToken.None).Attempted, "Rejected public Ping does not issue an attempt");
        Check(CampusTargets.Gateway(Link("8.8.8.8")) == null, "A public gateway is never used as a fallback target");
        var stats = CampusPingStatistics.From(new[] { CampusProbeResult.Reply(10), CampusProbeResult.Reply(20), CampusProbeResult.Missing("TimedOut"), CampusProbeResult.Reply(100) });
        Check(stats.Attempts == 4 && stats.Replies == 3 && Math.Abs(stats.Average.Value - 130.0 / 3) < .001, "RTT statistics use successful replies only");
        Check(stats.Peak == 100 && stats.P95 == 100 && stats.Jitter == 10, "P95 and jitter are correct; missing samples break jitter pairs");
        Check(!CampusPingStatistics.From(new[] { CampusProbeResult.Missing("TimedOut") }).Average.HasValue, "All ICMP timeouts remain unknown instead of invented RTT");
        Check(CampusPingStatistics.From(new[] { CampusProbeResult.Missing("TimedOut") }).Format().Contains("无法判断"), "All timeouts do not declare the campus offline");
        Check(CampusStabilityAnalyzer.Analyze(new CampusStabilityLog().Snapshot()).State == "idle", "Empty monitoring has an explicit idle insight");
        var stableAnalysis = new CampusStabilityLog(); for (int i = 0; i < 6; i++) stableAnalysis.Add(Sample(i, 2, 8, "stable", 120000, 12000));
        Check(CampusStabilityAnalyzer.Analyze(stableAnalysis.Snapshot()).State == "stable", "A completed calm baseline is described as stable");
        stableAnalysis.Add(Sample(6, 100, 8, "stable", 120000, 12000));
        Check(CampusStabilityAnalyzer.Analyze(stableAnalysis.Snapshot()).State == "gateway", "A gateway-only spike points to the local gateway path");
        var campusAnalysis = new CampusStabilityLog(); for (int i = 0; i < 6; i++) campusAnalysis.Add(Sample(i, 2, 8, "campus", 120000, 12000));
        campusAnalysis.Add(Sample(6, 2, 100, "campus", 120000, 12000));
        Check(CampusStabilityAnalyzer.Analyze(campusAnalysis.Snapshot()).State == "campus", "A school-service-only spike points beyond the gateway");
        var loadedAnalysis = new CampusStabilityLog(); for (int i = 0; i < 6; i++) loadedAnalysis.Add(Sample(i, 2, 8, "loaded", 120000, 12000));
        loadedAnalysis.Add(Sample(6, 100, 8, "loaded", 5 * 1024 * 1024, 12000));
        Check(CampusStabilityAnalyzer.Analyze(loadedAnalysis.Snapshot()).State == "loaded" && CampusStabilityAnalyzer.Analyze(loadedAnalysis.Snapshot()).LoadedSpikes == 1, "A latency spike with exceptional local traffic is presented as correlation only");
        var busyCalm = new CampusStabilityLog(); for (int i = 0; i < 6; i++) busyCalm.Add(Sample(i, 2, 8, "busy-calm", 5 * 1024 * 1024, 256 * 1024));
        Check(CampusStabilityAnalyzer.Analyze(busyCalm.Snapshot()).State == "stable", "High traffic without an RTT spike is not mislabeled as a network fault");
        var partialAnalysis = new CampusStabilityLog(); for (int i = 0; i < 6; i++) partialAnalysis.Add(Sample(i, null, 8, "partial"));
        Check(CampusStabilityAnalyzer.Analyze(partialAnalysis.Snapshot()).State == "partial", "A non-pinging gateway with a responsive school service remains a partial observation");
        var buildingAnalysis = new CampusStabilityLog(); for (int i = 0; i < 3; i++) buildingAnalysis.Add(Sample(i, 2, 8, "building"));
        Check(CampusStabilityAnalyzer.Analyze(buildingAnalysis.Snapshot()).State == "building", "Fewer than five successful samples never produce a stability verdict");
        Check(loadedAnalysis.Report().Contains("当前判断：卡的时候你的电脑正在大量下载或上传") && loadedAnalysis.Report().Contains("不证明因果关系"), "Exported report preserves the evidence limits and current insight");
        var roamLog = new CampusStabilityLog();
        for (int i = 0; i < 6; i++) roamLog.Add(Radio(Sample(i, 2, 8, "roam"), -50, "AA:BB:CC:DD:EE:01"));
        var roamed = Radio(Sample(6, 100, 8, "roam"), -50, "AA:BB:CC:DD:EE:02"); roamLog.Add(roamed);
        Check(roamed.Roamed && CampusStabilityAnalyzer.Analyze(roamLog.Snapshot()).State == "wifi" && roamLog.Snapshot().Events.Any(e => e.Message.StartsWith("无线切换了热点")), "A gateway spike right after an access-point switch is put down to Wi-Fi roaming");
        Check(roamLog.Report().Contains("…EE:02") && !roamLog.Report().Contains("AA:BB:CC:DD:EE:02") && roamLog.Report().Contains("Wi-Fi信号(dBm)"), "Reports keep only the BSSID tail and include the Wi-Fi columns");
        var weakLog = new CampusStabilityLog();
        for (int i = 0; i < 6; i++) weakLog.Add(Radio(Sample(i, 2, 8, "weak"), -60, "AA:BB:CC:DD:EE:01"));
        weakLog.Add(Radio(Sample(6, 100, 8, "weak"), -80, "AA:BB:CC:DD:EE:01"));
        Check(CampusStabilityAnalyzer.Analyze(weakLog.Snapshot()).State == "wifi", "A gateway spike on a weak signal is put down to Wi-Fi");
        var scanLog = new CampusStabilityLog();
        for (int i = 0; i < 80; i++) scanLog.Add(Radio(Sample(i, i == 10 || i == 40 || i == 70 ? 100 : 2, 8, "scan"), -50, "AA:BB:CC:DD:EE:01"));
        Check(CampusStabilityAnalyzer.Analyze(scanLog.Snapshot()).State == "scan", "Gateway spikes about a minute apart point to the Windows Wi-Fi background scan");
        var log = new CampusStabilityLog();
        for (int i = 0; i < 6; i++) log.Add(Sample(i, 2, 8));
        log.Add(Sample(6, 100, 8)); log.Add(Sample(7, 110, 8));
        Check(log.Snapshot().Events.Count(e => e.Message.StartsWith("宿舍网络延迟突增")) == 1, "A sustained spike creates one onset event");
        log.Add(Sample(8, 2, 8)); log.Add(Sample(9, 100, 8));
        Check(log.Snapshot().Events.Count(e => e.Message.StartsWith("宿舍网络延迟突增")) == 2, "A second spike after recovery is retained");
        log.Add(Sample(10, 500, 600, "B"));
        Check(log.Snapshot().Gateway.Average == 500 && log.Snapshot().Gateway.Attempts == 1, "Changing connections resets metric scope");
        Check(log.Snapshot().Events.Count(e => e.Message.StartsWith("宿舍网络延迟突增")) == 2, "A new connection does not inherit the old baseline");
        var timeouts = new CampusStabilityLog(); timeouts.Add(Sample(0, 2, 8));
        for (int i = 1; i < 6; i++) timeouts.Add(Sample(i, null, 8));
        Check(timeouts.Snapshot().Events.Count(e => e.Message.StartsWith("宿舍网关连续没有回应")) == 1, "An ICMP timeout run creates one onset warning");
        log.Mark(DateTimeOffset.UtcNow);
        Check(log.Report().Contains("你标记了一次卡顿") && log.Report().Contains("不代表游戏丢包率") && log.Report().Contains("+"), "Report includes markers, time zones and measurement limits");
        for (int i = 11; i < 350; i++) log.Add(Sample(i, i % 20, 8, "B"));
        for (int i = 0; i < 110; i++) log.Mark(DateTimeOffset.UtcNow);
        Check(log.Snapshot().Samples.Length == 300 && log.Snapshot().Events.Length == 100, "Sample and event buffers remain bounded");
        var probe = new Probe();
        using (var monitor = new CampusStabilityMonitor(probe, delegate { return Link(); }))
        {
            monitor.SetTraffic("独立测试网卡", 1234, 567); var sample = monitor.Collect(CancellationToken.None);
            Check(probe.Targets.SequenceEqual(new[] { "10.14.0.1", CampusTargets.Portal }), "Collection uses exactly the current gateway and school service");
            Check(sample.Download == 1234 && sample.Upload == 567 && probe.HttpCalls == 0, "Traffic context is read-only; successful ICMP needs no HTTP fallback");
            monitor.SetTraffic("其他网卡", 9999, 8888); sample = monitor.Collect(CancellationToken.None);
            Check(!sample.Download.HasValue && !sample.Upload.HasValue, "Traffic from another adapter is never attributed to the monitored link");
            probe.Missing = true; for (int i = 0; i < 11; i++) monitor.Collect(CancellationToken.None);
            Check(probe.HttpCalls == 3 && probe.Targets.All(CampusTargets.Allowed), "ICMP misses only trigger sparse school HTTP checks, never public targets");
            var source = new CancellationTokenSource(); source.Cancel(); int before = probe.Targets.Count; bool cancelled = false;
            try { monitor.Collect(source.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && probe.Targets.Count == before, "Cancellation prevents new probes"); source.Dispose();
        }
        var disconnectedProbe = new Probe();
        using (var monitor = new CampusStabilityMonitor(disconnectedProbe, delegate { return new LinkSnapshot { State = LinkState.Disconnected }; }))
        { var sample = monitor.Collect(CancellationToken.None); Check(disconnectedProbe.Targets.Count == 0 && !sample.PortalPing.Attempted, "Disconnected links do not probe any destination"); }
        var diagnosticProbe = new Probe(); int authentications = 0, configurations = 0;
        var diagnosis = CampusDiagnostics.Run(Link(), diagnosticProbe, delegate { authentications++; return PortalState.Online; }, delegate { configurations++; return "已配置 校内DNS；未验证解析能力"; });
        Check(authentications == 1 && configurations == 1 && diagnosticProbe.Targets.Count == 4 && diagnosticProbe.Targets.All(t => t == "10.14.0.1"), "Eight-step diagnosis uses gateway and injected school-only status checks");
        Check(diagnosticProbe.HttpCalls == 1 && diagnosis.States.Length == 8 && diagnosis.States.All(s => s == "正常"), "School HTTP replaces external connectivity checks");
        Check(CampusDiagnostics.HttpState("校内认证页 HTTP 503") == "需关注" && CampusDiagnostics.HttpState("校内认证页 HTTP 302（未跟随跳转）") == "正常" && CampusDiagnostics.HttpState("暂未响应") == "无法判断", "HTTP errors, reachable redirects and timeouts remain distinct");
        diagnosticProbe.Missing = true;
        diagnosis = CampusDiagnostics.Run(Link(), diagnosticProbe, delegate { return PortalState.Online; }, delegate { return "已配置"; });
        Check(diagnosis.States[3] == "无法判断" && diagnosis.States[5] == "正常" && diagnosis.Advice.Contains("不要"), "ICMP restrictions never override confirmed campus authentication");
        var slowProbe = new Probe { Delay = 180 };
        using (var monitor = new CampusStabilityMonitor(slowProbe, delegate { return Link(); }))
        {
            monitor.Start(); Thread.Sleep(35); monitor.Stop(); Thread.Sleep(400);
            Check(!monitor.Running && monitor.Log.Snapshot().Samples.Length == 0, "Stopping during a probe rejects its late result");
            monitor.Start(); Thread.Sleep(450); monitor.Stop();
            Check(monitor.Log.Snapshot().Samples.Length == 1 && slowProbe.MaxActive == 1, "Restart works and samples never overlap");
        }
        Ui(output);
    }
    private static IEnumerable<Control> All(Control parent) { foreach (Control c in parent.Controls) { yield return c; foreach (var d in All(c)) yield return d; } }
    private static void Click(Control parent, string name) { var control = All(parent).Single(c => c.Name == name); control.GetType().GetMethod("OnClick", Private).Invoke(control, new object[] { EventArgs.Empty }); }
    private static void Ui(string output)
    {
        string data = Path.Combine(output, "isolated-data"); Directory.CreateDirectory(data);
        foreach (string field in new[] { "Data", "Secret", "Log", "Settings", "RecoveryState", "MonitorStatus", "Startup" }) typeof(AppPaths).GetField(field).SetValue(null, field == "Data" ? data : Path.Combine(data, field + ".json"));
        AppSettings.Save(new SettingsRecord { priorityMode = "Off", autoLogin = false }); byte[] settings = File.ReadAllBytes(AppPaths.Settings);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        var probe = new Probe(); bool busy = true;
        using (var form = new Form { Text = "独立测试数据 · 非当前校园网", StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(600, 660) })
        using (var view = new CampusStabilityView(new CampusStabilityMonitor(probe, delegate { return Link(); }), delegate { return busy; }) { Dock = DockStyle.Fill })
        {
            form.Controls.Add(view); form.Show(); Pump(30); view.Start();
            Check(!view.Running && !GameTrafficGuard.Active, "Game mode refuses to interrupt an existing transfer");
            busy = false; view.Start(); Pump(140); view.RefreshSnapshot();
            Check(view.Running && GameTrafficGuard.Active && view.Monitor.Log.Snapshot().Samples.Length > 0, "Starting game monitoring captures samples and acquires the transfer guard");
            using (var bandwidth = new NetworkSpeedTestView())
            { typeof(NetworkSpeedTestView).GetMethod("StartTest", Private).Invoke(bandwidth, null); Check(!bandwidth.Running, "Game monitoring blocks new bandwidth tests before network work"); }
            using (var bandwidth = new NetworkSpeedTestView { CampusOnly = true })
            { typeof(NetworkSpeedTestView).GetMethod("StartTest", Private).Invoke(bandwidth, null); Check(!bandwidth.Running && All(bandwidth).OfType<Label>().Any(l => l.Text.Contains("不") && l.Text.Contains("公网")), "The campus diagnosis page refuses its old public bandwidth endpoint"); }
            Click(view, "StabilityMark"); string exported = null; view.ExportReport = delegate(string text) { exported = text; }; Click(view, "StabilityExport");
            Check(exported != null && exported.Contains("你标记了一次卡顿") && exported.Contains(CampusTargets.Portal), "Marker and export actions operate on the captured local report");
            ((CheckBox)All(view).Single(c => c.Name == "StabilityQuiet")).Checked = false;
            Check(view.Running && !GameTrafficGuard.Active, "Disabling quiet mode keeps measurement running");
            ((CheckBox)All(view).Single(c => c.Name == "StabilityQuiet")).Checked = true;
            view.Enabled = false; Check(view.Running && GameTrafficGuard.Active, "Leaving the page preserves the active recording session"); view.Enabled = true;
            for (int i = 0; i < 30; i++) view.Monitor.Log.Add(Sample(i, i == 20 ? 90 : 3, 8)); view.RefreshSnapshot();
            foreach (int width in new[] { 540, 720, 1000 })
            {
                form.Size = new Size(width, 660); Pump(30);
                foreach (var action in All(view).Where(c => c is FlatAction || c is CheckBox)) Check(action.Bounds.Left >= 0 && action.Bounds.Right <= action.Parent.ClientSize.Width && action.Bottom <= action.Parent.ClientSize.Height,
                    "Monitoring action fits at " + width + ": " + action.Name);
                using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, form.ClientRectangle); bitmap.Save(Path.Combine(output, "stability-" + width + ".png")); }
            }
            view.Stop(); Check(!view.Running && !GameTrafficGuard.Active && All(view).Single(c => c.Name == "StabilityExport").Enabled, "Stopping releases guard and preserves report access");
            view.Start(); form.Close();
        }
        Check(!GameTrafficGuard.Active, "Disposal releases the game guard");
        using (var dashboard = new DashboardWindow(false))
        {
            dashboard.StabilityFactory = delegate { return new CampusStabilityMonitor(new Probe(), delegate { return Link(); }); };
            dashboard.StartPosition = FormStartPosition.Manual; dashboard.Location = new Point(-20000, -20000); dashboard.Show(); Pump(80);
            var page = typeof(DashboardWindow).GetMethod("ShowPage", Private); page.Invoke(dashboard, new object[] { DashboardWindow.GamePage }); Pump(60);
            var monitorView = (CampusStabilityView)typeof(DashboardWindow).GetField("stabilityView", Private).GetValue(dashboard);
            Click(dashboard, "StabilityStart"); Pump(100); Click(dashboard, "StabilityMark");
            Check(monitorView.Running && GameTrafficGuard.Active && monitorView.Monitor.Log.Snapshot().Events.Any(e => e.Message.StartsWith("你标记了一次卡顿")), "Game-mode page starts the real recording workflow with isolated probes");
            page.Invoke(dashboard, new object[] { "首页" }); Pump(30); page.Invoke(dashboard, new object[] { DashboardWindow.GamePage }); Pump(40);
            Check(Object.ReferenceEquals(monitorView, typeof(DashboardWindow).GetField("stabilityView", Private).GetValue(dashboard)) && monitorView.Running, "Cached navigation preserves the recording view and its samples");
            dashboard.WindowState = FormWindowState.Minimized; Pump(50); Check(monitorView.Running, "Minimizing the app preserves the recording session"); dashboard.WindowState = FormWindowState.Normal; Pump(70);
            string report = null; monitorView.ExportReport = delegate(string value) { report = value; }; Click(dashboard, "StabilityExport");
            Check(report != null && report.Contains("你标记了一次卡顿"), "Integrated export preserves the marker after navigation and restoration");
            Click(dashboard, "StabilityStart"); Check(!monitorView.Running && !GameTrafficGuard.Active, "Integrated stop releases game mode");
            dashboard.Close();
        }
        Check(settings.SequenceEqual(File.ReadAllBytes(AppPaths.Settings)) && !File.Exists(AppPaths.Secret) && !File.Exists(AppPaths.Startup), "Monitoring leaves account, persisted settings and startup untouched");
    }
}
