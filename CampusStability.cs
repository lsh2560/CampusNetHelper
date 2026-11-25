using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;

// These are the existing school's authentication endpoint and the currently
// selected local gateway. No hostname resolution or public fallback probes.
internal static class CampusTargets
{
    internal const string Portal = "210.43.112.9";
    internal const string PortalUrl = "http://210.43.112.9/";
    internal static bool PrivateAddress(string text)
    {
        IPAddress address;
        if (!IPAddress.TryParse(text, out address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || address.ToString() != text) return false;
        byte[] b = address.GetAddressBytes();
        return b[3] != 255 && (b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168);
    }
    internal static bool Allowed(string target) { return target == Portal || PrivateAddress(target); }
    internal static string Gateway(LinkSnapshot link)
    {
        return link != null && link.State == LinkState.Ready && PrivateAddress(link.Ip) && PrivateAddress(link.Gateway) ? link.Gateway : null;
    }
    internal static string DnsConfiguration(LinkSnapshot link)
    {
        NetworkInterface adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(a => link != null &&
            link.Identity != null && link.Identity.StartsWith(a.Id + "|", StringComparison.Ordinal));
        if (adapter == null) return "无法读取当前网卡的 DNS 配置（未发送 DNS 查询）";
        var addresses = adapter.GetIPProperties().DnsAddresses.Select(a => a.ToString()).ToArray();
        return addresses.Length == 0 ? "未配置 DNS（只读检查）" : "已配置 " + String.Join("、", addresses) + "；未验证解析能力";
    }
}

internal sealed class CampusProbeResult
{
    internal long? Milliseconds;
    internal string State;
    internal bool Attempted;
    internal static CampusProbeResult Reply(long ms) { return new CampusProbeResult { Milliseconds = ms, State = "收到响应", Attempted = true }; }
    internal static CampusProbeResult Missing(string state) { return new CampusProbeResult { State = state, Attempted = true }; }
    internal static CampusProbeResult Skipped(string state) { return new CampusProbeResult { State = state }; }
}

internal interface ICampusProbe
{
    CampusProbeResult Ping(string target, CancellationToken token);
    string PortalHttp(CancellationToken token);
}

internal sealed class CampusOnlyProbe : ICampusProbe
{
    public CampusProbeResult Ping(string target, CancellationToken token)
    {
        if (!CampusTargets.Allowed(target)) return CampusProbeResult.Skipped("目标不在校内检测范围");
        token.ThrowIfCancellationRequested();
        try
        {
            using (var ping = new Ping())
            {
                PingReply reply = ping.Send(IPAddress.Parse(target), 700, new byte[32]);
                token.ThrowIfCancellationRequested();
                return reply.Status == IPStatus.Success ? CampusProbeResult.Reply(reply.RoundtripTime) : CampusProbeResult.Missing(reply.Status.ToString());
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return CampusProbeResult.Skipped("Ping 不可用或权限受限"); }
    }
    public string PortalHttp(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var request = (HttpWebRequest)WebRequest.Create(CampusTargets.PortalUrl);
        request.Proxy = null; request.AllowAutoRedirect = false;
        // Leave from the campus adapter so a VPN or accelerator that owns the
        // default route cannot carry (and distort) the campus-service check.
        LinkSnapshot link = null;
        try { link = PortalProbe.Link(); } catch { }
        IPAddress local;
        if (link != null && link.State == LinkState.Ready && CampusTargets.PrivateAddress(link.Ip) && IPAddress.TryParse(link.Ip, out local))
            request.ServicePoint.BindIPEndPointDelegate = delegate(ServicePoint point, IPEndPoint remote, int retry) { return retry == 0 ? new IPEndPoint(local, 0) : null; };
        request.Timeout = 1500; request.ReadWriteTimeout = 1500; request.KeepAlive = false;
        using (token.Register(request.Abort))
        {
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                    return "校内认证页 HTTP " + (int)response.StatusCode + (response.StatusCode >= HttpStatusCode.MultipleChoices ? "（未跟随跳转）" : "");
            }
            catch (WebException ex)
            {
                token.ThrowIfCancellationRequested();
                using (var response = ex.Response as HttpWebResponse)
                    return response == null ? "校内认证页暂未响应；不能据此判定认证失效" : "校内认证页 HTTP " + (int)response.StatusCode;
            }
        }
    }
}

internal sealed class CampusStabilitySample
{
    internal DateTimeOffset Time;
    internal string Identity, Gateway;
    internal int Segment;
    internal CampusProbeResult GatewayPing, PortalPing;
    internal string Http;
    internal double? Download, Upload;
    internal WifiStatus Wifi;   // null on Ethernet or when the WLAN API is unavailable
    internal bool Roamed;       // the access point (BSSID) changed since the previous sample
}

internal sealed class CampusStabilityEvent
{
    internal DateTimeOffset Time;
    internal string Message;
}

internal sealed class CampusPingStatistics
{
    internal int Attempts, Replies;
    internal double? Average, Peak, P95, Jitter;
    internal string Format()
    {
        if (Replies == 0) return Attempts == 0 ? "尚无有效采样" : "Ping 暂未响应，无法判断";
        return "均值 " + Average.Value.ToString("0.#") + " / 峰值 " + Peak.Value.ToString("0") + " ms\n" +
            "抖动 " + (Jitter.HasValue ? Jitter.Value.ToString("0.#") + " ms" : "待积累") + " · 未响应 " + (Attempts - Replies) + "/" + Attempts;
    }
    internal static CampusPingStatistics From(IEnumerable<CampusProbeResult> source)
    {
        var all = source.ToArray(); var values = all.Where(p => p.Milliseconds.HasValue).Select(p => (double)p.Milliseconds.Value).OrderBy(v => v).ToArray();
        var stats = new CampusPingStatistics { Attempts = all.Count(p => p.Attempted), Replies = values.Length };
        if (values.Length == 0) return stats;
        stats.Average = values.Average(); stats.Peak = values.Last(); stats.P95 = values[(int)Math.Ceiling(values.Length * .95) - 1];
        var differences = new List<double>();
        for (int i = 1; i < all.Length; i++) if (all[i - 1].Milliseconds.HasValue && all[i].Milliseconds.HasValue)
            differences.Add(Math.Abs(all[i].Milliseconds.Value - all[i - 1].Milliseconds.Value));
        if (differences.Count > 0) stats.Jitter = differences.Average();
        return stats;
    }
}

internal sealed class CampusStabilitySnapshot
{
    internal CampusStabilitySample[] Samples;
    internal CampusStabilityEvent[] Events;
    internal CampusPingStatistics Gateway, Portal;
    internal int Revision;
}

internal sealed class CampusStabilityInsight
{
    internal string State, Title, Detail;
    internal int GatewaySpikes, CampusSpikes, LoadedSpikes, WifiSpikes;
}

internal static class CampusStabilityAnalyzer
{
    internal const double DownloadFloor = 2 * 1024 * 1024;
    internal const double UploadFloor = 128 * 1024;
    internal static bool IsSpike(CampusProbeResult current, IEnumerable<CampusProbeResult> previous)
    {
        if (!current.Milliseconds.HasValue) return false;
        double[] values = previous.Where(p => p.Milliseconds.HasValue).Select(p => (double)p.Milliseconds.Value).Reverse().Take(15).OrderBy(v => v).ToArray();
        return values.Length >= 5 && current.Milliseconds.Value > Math.Max(30, values[values.Length / 2] * 3);
    }
    internal static bool HighTraffic(CampusStabilitySample current, IEnumerable<CampusStabilitySample> previous)
    {
        if (!current.Download.HasValue && !current.Upload.HasValue) return false;
        CampusStabilitySample[] recent = previous.Reverse().Take(15).Reverse().ToArray();
        double[] down = recent.Where(s => s.Download.HasValue).Select(s => s.Download.Value).OrderBy(v => v).ToArray();
        double[] up = recent.Where(s => s.Upload.HasValue).Select(s => s.Upload.Value).OrderBy(v => v).ToArray();
        double downLimit = Math.Max(DownloadFloor, down.Length == 0 ? 0 : down[down.Length / 2] * 3);
        double upLimit = Math.Max(UploadFloor, up.Length == 0 ? 0 : up[up.Length / 2] * 3);
        return current.Download.GetValueOrDefault() >= downLimit || current.Upload.GetValueOrDefault() >= upLimit;
    }
    // A gateway spike is put down to the radio when the signal was weak or the
    // computer had just switched access point around that sample.
    internal static bool WifiRelated(CampusStabilitySample[] samples, int index)
    {
        for (int i = Math.Max(0, index - 1); i <= index; i++)
            if (samples[i].Roamed || samples[i].Wifi != null && samples[i].Wifi.Weak) return true;
        return false;
    }
    // Windows' background Wi-Fi scan briefly leaves the channel about once a
    // minute; three or more gateway spikes spaced 50-75 s apart look like it.
    internal static bool PeriodicSpikes(IList<DateTimeOffset> spikes)
    {
        if (spikes.Count < 3) return false;
        int regular = 0;
        for (int i = 1; i < spikes.Count; i++)
        {
            double gap = (spikes[i] - spikes[i - 1]).TotalSeconds;
            if (gap >= 50 && gap <= 75) regular++;
        }
        return regular >= 2 && regular * 3 >= (spikes.Count - 1) * 2;
    }
    internal static CampusStabilityInsight Analyze(CampusStabilitySnapshot snapshot)
    {
        CampusStabilitySample last = snapshot.Samples.LastOrDefault();
        if (last == null) return Insight("idle", "还没有开始记录", "点“开始记录”后打游戏，卡的时候点“刚才卡了”。", 0, 0, 0, 0);
        CampusStabilitySample[] current = snapshot.Samples.Where(s => s.Segment == last.Segment).ToArray();
        int gatewaySpikes = 0, campusSpikes = 0, loadedSpikes = 0, wifiSpikes = 0;
        var gatewayTimes = new List<DateTimeOffset>();
        for (int i = 0; i < current.Length; i++)
        {
            CampusStabilitySample[] previous = current.Take(i).ToArray();
            bool gateway = IsSpike(current[i].GatewayPing, previous.Select(s => s.GatewayPing));
            bool campus = IsSpike(current[i].PortalPing, previous.Select(s => s.PortalPing));
            if (gateway) { gatewaySpikes++; gatewayTimes.Add(current[i].Time); if (WifiRelated(current, i)) wifiSpikes++; }
            if (campus && !gateway) campusSpikes++;
            if ((gateway || campus) && HighTraffic(current[i], previous)) loadedSpikes++;
        }
        int gatewayReplies = current.Count(s => s.GatewayPing.Milliseconds.HasValue);
        int campusReplies = current.Count(s => s.PortalPing.Milliseconds.HasValue);
        bool recentMissing = current.Reverse().Take(3).Count(s => s.GatewayPing.Attempted && !s.GatewayPing.Milliseconds.HasValue) >= 3 ||
            current.Reverse().Take(3).Count(s => s.PortalPing.Attempted && !s.PortalPing.Milliseconds.HasValue) >= 3;
        if (loadedSpikes > 0)
            return Insight("loaded", "卡的时候你的电脑正在大量下载或上传", "暂停 Steam、网盘或上传任务后再对比。这只说明两件事同时发生，不一定是原因。", gatewaySpikes, campusSpikes, loadedSpikes, wifiSpikes);
        if (PeriodicSpikes(gatewayTimes))
            return Insight("scan", "延迟尖峰大约每分钟出现一次", "很像 Windows 在后台扫描 Wi-Fi。插网线可以避开；用无线时尽量别移动电脑。", gatewaySpikes, campusSpikes, loadedSpikes, wifiSpikes);
        if (wifiSpikes > 0)
            return Insight("wifi", "无线信号弱或切换了热点", "靠近路由器、改连 5 GHz，或在网卡设置里把“漫游积极性”调低。", gatewaySpikes, campusSpikes, loadedSpikes, wifiSpikes);
        if (gatewaySpikes > 0)
            return Insight("gateway", "你的电脑到宿舍网络这一段有波动", "常见原因是无线干扰或同一热点人多；能插网线的话可以对比一下。", gatewaySpikes, campusSpikes, loadedSpikes, wifiSpikes);
        if (campusSpikes > 0)
            return Insight("campus", "学校内部线路有波动", "你这边到宿舍网络是正常的。可以导出报告，反馈给网络中心。", gatewaySpikes, campusSpikes, loadedSpikes, wifiSpikes);
        if (gatewayReplies == 0 && campusReplies >= 5)
            return Insight("partial", "宿舍网关不回应检测", "只能看学校服务器这一段；你的电脑到宿舍网络这一段暂时无法判断。", gatewaySpikes, campusSpikes, loadedSpikes, wifiSpikes);
        if (recentMissing)
            return Insight("unknown", "连续几次没有收到回应", "可能是对方不回应检测、限速或短暂丢包，暂时无法判断。软件不会因此重新登录。", gatewaySpikes, campusSpikes, loadedSpikes, wifiSpikes);
        if (gatewayReplies < 5 && campusReplies < 5)
            return Insight("building", "正在积累数据", "大约 10 秒后开始判断。", gatewaySpikes, campusSpikes, loadedSpikes, wifiSpikes);
        return Insight("stable", "目前没有发现明显波动", "继续打游戏，卡的时候点“刚才卡了”。校园网稳定不代表游戏服务器线路也稳定。", gatewaySpikes, campusSpikes, loadedSpikes, wifiSpikes);
    }
    private static CampusStabilityInsight Insight(string state, string title, string detail, int gateway, int campus, int loaded, int wifi)
    {
        return new CampusStabilityInsight { State = state, Title = title, Detail = detail, GatewaySpikes = gateway, CampusSpikes = campus, LoadedSpikes = loaded, WifiSpikes = wifi };
    }
}

internal sealed class CampusStabilityLog
{
    private readonly object gate = new object();
    private readonly List<CampusStabilitySample> samples = new List<CampusStabilitySample>();
    private readonly List<CampusStabilityEvent> events = new List<CampusStabilityEvent>();
    private string identity;
    private int segment, revision;
    private bool gatewayBad, portalBad;
    private void Event(DateTimeOffset time, string message)
    {
        events.Add(new CampusStabilityEvent { Time = time, Message = message });
        if (events.Count > 100) events.RemoveAt(0);
    }
    internal void Add(CampusStabilitySample sample)
    {
        lock (gate)
        {
            if (identity != sample.Identity)
            {
                identity = sample.Identity; segment++; gatewayBad = portalBad = false;
                Event(sample.Time, "连接已变化，重新积累基线；不同连接的延迟不会混算");
            }
            sample.Segment = segment;
            var previous = samples.Where(s => s.Segment == segment).ToArray();
            CampusStabilitySample before = previous.LastOrDefault();
            if (before != null && before.Wifi != null && sample.Wifi != null && before.Wifi.Bssid != sample.Wifi.Bssid)
            {
                sample.Roamed = true;
                Event(sample.Time, "无线切换了热点（" + Tail(before.Wifi.Bssid) + " → " + Tail(sample.Wifi.Bssid) + "），切换时可能卡一下");
            }
            bool gatewaySpike = CampusStabilityAnalyzer.IsSpike(sample.GatewayPing, previous.Select(s => s.GatewayPing));
            bool portalSpike = CampusStabilityAnalyzer.IsSpike(sample.PortalPing, previous.Select(s => s.PortalPing));
            bool highTraffic = CampusStabilityAnalyzer.HighTraffic(sample, previous);
            string traffic = highTraffic ? "；同时观测到本机高流量（下载 " + FormatTraffic(sample.Download) + "，上传 " + FormatTraffic(sample.Upload) + "）" : "";
            string radio = sample.Wifi != null && sample.Wifi.Weak ? "；当时 Wi-Fi 信号较弱（" + Signal(sample.Wifi) + "）" : "";
            if (gatewaySpike && !gatewayBad) Event(sample.Time, "宿舍网络延迟突增：" + sample.GatewayPing.Milliseconds + " ms" + traffic + radio);
            else if (portalSpike && !portalBad) Event(sample.Time, "学校服务器延迟突增：" + sample.PortalPing.Milliseconds + " ms" + traffic);
            if (previous.Length > 0)
            {
                CampusStabilitySample last = previous.Last();
                if (sample.GatewayPing.Attempted && !sample.GatewayPing.Milliseconds.HasValue && last.GatewayPing.Attempted && !last.GatewayPing.Milliseconds.HasValue &&
                    previous.Length >= 2 && previous[previous.Length - 2].GatewayPing.Milliseconds.HasValue) Event(sample.Time, "宿舍网关连续没有回应；可能是丢包、限速或不回应检测，不会自动重连");
            }
            gatewayBad = gatewaySpike; portalBad = portalSpike;
            samples.Add(sample); if (samples.Count > 300) samples.RemoveAt(0);
            revision++;
        }
    }
    internal void Mark(DateTimeOffset time)
    {
        lock (gate) { Event(time, "你标记了一次卡顿"); revision++; }
    }
    internal void BeginSession()
    {
        lock (gate) { identity = null; revision++; }
    }
    internal CampusStabilitySnapshot Snapshot()
    {
        lock (gate)
        {
            var current = samples.Where(s => s.Segment == segment).ToArray();
            return new CampusStabilitySnapshot { Samples = samples.ToArray(), Events = events.ToArray(), Revision = revision,
                Gateway = CampusPingStatistics.From(current.Select(s => s.GatewayPing)), Portal = CampusPingStatistics.From(current.Select(s => s.PortalPing)) };
        }
    }
    internal string Report()
    {
        var snapshot = Snapshot(); var b = new StringBuilder();
        b.AppendLine("稳连 · 网络记录报告 · v" + typeof(CampusStabilityLog).Assembly.GetName().Version.ToString(3));
        b.AppendLine("仅检测当前私网网关和学校认证服务器 " + CampusTargets.Portal + "；没有公网连通性检测或公网回退。");
        b.AppendLine("目标采样间隔约 2 秒；请求未完成时跳过重叠采样，短于间隔的波动可能漏采。最多保留最近 300 组样本和 100 条事件。抖动为相邻成功响应的 RTT 差绝对值均值，缺失样本不跨越计算。");
        b.AppendLine("突增规则：至少 5 个成功样本；当前 RTT 大于 30 ms 与此前最多 15 个成功样本中位数的 3 倍两者中的较大值。上下行流量只在采样网卡一致时记录，未采集时显示未测得。");
        b.AppendLine("高流量线索：下载不低于 2 MiB/s 或上传不低于 128 KiB/s，同时还需达到此前最多 15 个流量样本中位数的 3 倍；它只表示同时发生，不证明因果关系。");
        b.AppendLine("未响应比例只描述 ICMP 采样，禁 Ping/限速也会造成未响应；不代表游戏丢包率。仅供排查校内链路，不代表游戏服务器线路。统计仅含当前连接段。");
        b.AppendLine("网关：" + snapshot.Gateway.Format()); b.AppendLine("校内服务：" + snapshot.Portal.Format());
        b.AppendLine("P95 RTT：网关 " + Number(snapshot.Gateway.P95) + " ms；校内服务 " + Number(snapshot.Portal.P95) + " ms");
        CampusStabilityInsight insight = CampusStabilityAnalyzer.Analyze(snapshot);
        b.AppendLine("当前判断：" + insight.Title + "。" + insight.Detail);
        b.AppendLine("\n事件（时间含时区）：");
        foreach (var item in snapshot.Events) b.AppendLine(item.Time.ToLocalTime().ToString("O") + "  " + item.Message);
        b.AppendLine("Wi-Fi：信号强度为 Windows 无线 API 读取的 RSSI（dBm）与信号质量（%）；热点以 BSSID 末两段标识，变化即视为切换热点。");
        b.AppendLine("\n时间,连接段,网关,网关RTT(ms),网关状态,校内服务RTT(ms),校内服务状态,下载(B/s),上传(B/s),校内HTTP辅助结果,Wi-Fi信号(dBm),Wi-Fi质量(%),热点,切换热点");
        foreach (var s in snapshot.Samples) b.AppendLine(String.Join(",", new[] { s.Time.ToLocalTime().ToString("O"), s.Segment.ToString(), s.Gateway ?? "未取得", Number(s.GatewayPing.Milliseconds), s.GatewayPing.State,
            Number(s.PortalPing.Milliseconds), s.PortalPing.State, Number(s.Download), Number(s.Upload), s.Http ?? "未检查",
            s.Wifi == null || !s.Wifi.Rssi.HasValue ? "未测得" : s.Wifi.Rssi.Value.ToString(CultureInfo.InvariantCulture),
            s.Wifi == null ? "未测得" : s.Wifi.Quality.ToString(CultureInfo.InvariantCulture), s.Wifi == null ? "—" : Tail(s.Wifi.Bssid), s.Roamed ? "是" : "否" }));
        return b.ToString();
    }
    // Only the last two BSSID octets are kept, enough to tell access points apart.
    internal static string Tail(string bssid) { return String.IsNullOrEmpty(bssid) || bssid.Length < 5 ? "未知" : "…" + bssid.Substring(bssid.Length - 5); }
    internal static string Signal(WifiStatus wifi) { return wifi.Rssi.HasValue ? wifi.Rssi.Value + " dBm" : wifi.Quality + "%"; }
    private static string Number(double? value) { return value.HasValue ? value.Value.ToString("0.#", CultureInfo.InvariantCulture) : "未测得"; }
    private static string FormatTraffic(double? value) { return value.HasValue ? NetworkSpeedMonitor.Format(value.Value) : "未测得"; }
}

internal sealed class CampusStabilityMonitor : IDisposable
{
    internal readonly CampusStabilityLog Log = new CampusStabilityLog();
    private readonly ICampusProbe probe;
    private readonly Func<LinkSnapshot> link;
    private readonly object gate = new object();
    private Timer timer;
    private CancellationTokenSource cancellation;
    private int busy, generation, counter;
    private double download, upload;
    private string trafficAdapter;
    private readonly Func<WifiStatus> wifi;
    internal bool Running { get { lock (gate) return timer != null; } }
    internal CampusStabilityMonitor() : this(new CampusOnlyProbe(), PortalProbe.Link, WifiInfo.Current) { }
    internal CampusStabilityMonitor(ICampusProbe probe, Func<LinkSnapshot> link) : this(probe, link, null) { }
    internal CampusStabilityMonitor(ICampusProbe probe, Func<LinkSnapshot> link, Func<WifiStatus> wifi) { this.probe = probe; this.link = link; this.wifi = wifi; }
    internal void SetTraffic(string adapter, double down, double up) { lock (gate) { trafficAdapter = adapter; download = down; upload = up; } }
    internal void Start()
    {
        lock (gate)
        {
            if (timer != null) return;
            Log.BeginSession();
            cancellation = new CancellationTokenSource(); var token = cancellation.Token; int run = ++generation;
            timer = new Timer(delegate { Tick(run, token); }, null, 0, 2000);
        }
    }
    private void Tick(int run, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        try
        {
            CampusStabilitySample sample = Collect(token);
            lock (gate) if (!token.IsCancellationRequested && run == generation) Log.Add(sample);
        }
        catch (OperationCanceledException) { }
        catch { /* A failed OS query is not evidence of a disconnected campus. */ }
        finally { Interlocked.Exchange(ref busy, 0); }
    }
    internal CampusStabilitySample Collect(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); LinkSnapshot current;
        try { current = link(); } catch { current = null; }
        string gateway = CampusTargets.Gateway(current);
        var sample = new CampusStabilitySample { Time = DateTimeOffset.UtcNow, Identity = current == null ? "" : current.Identity ?? "", Gateway = gateway };
        sample.GatewayPing = gateway == null ? CampusProbeResult.Skipped("未取得可检测的私网网关") : probe.Ping(gateway, token);
        sample.PortalPing = current == null || current.State != LinkState.Ready ? CampusProbeResult.Skipped("连接未就绪") : probe.Ping(CampusTargets.Portal, token);
        // HTTP is only a sparse campus-only auxiliary check, never a substitute
        // RTT sample or an authentication/reconnect trigger.
        if (sample.PortalPing.Attempted && !sample.PortalPing.Milliseconds.HasValue && Interlocked.Increment(ref counter) % 5 == 1)
            sample.Http = probe.PortalHttp(token);
        lock (gate) if (current != null && !String.IsNullOrEmpty(trafficAdapter) && trafficAdapter == current.AdapterName) { sample.Download = download; sample.Upload = upload; }
        if (wifi != null && current != null && current.Wireless) { try { sample.Wifi = wifi(); } catch { } }
        return sample;
    }
    internal void Stop()
    {
        lock (gate)
        {
            generation++;
            if (timer != null) { timer.Dispose(); timer = null; }
            if (cancellation != null) { cancellation.Cancel(); cancellation.Dispose(); cancellation = null; }
        }
    }
    public void Dispose() { Stop(); }
}

internal static class GameTrafficGuard
{
    private static int owners;
    internal static bool Active { get { return Interlocked.CompareExchange(ref owners, 0, 0) > 0; } }
    internal static IDisposable Enter() { return new Lease(); }
    private sealed class Lease : IDisposable
    {
        private int released;
        internal Lease() { Interlocked.Increment(ref owners); }
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) Interlocked.Decrement(ref owners); }
    }
}
