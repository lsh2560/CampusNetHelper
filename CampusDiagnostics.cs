using System;
using System.Linq;
using System.Threading;

internal static class CampusDiagnostics
{
    internal static DiagnosisSnapshot Run()
    {
        LinkSnapshot link = PortalProbe.Link();
        return Run(link, new CampusOnlyProbe(), PortalProbe.Check, CampusTargets.DnsConfiguration);
    }
    internal static DiagnosisSnapshot Run(LinkSnapshot link, ICampusProbe probe, Func<PortalState> authentication, Func<LinkSnapshot, string> dns)
    {
        string[] states = Enumerable.Repeat("未检测", 8).ToArray();
        string[] details = Enumerable.Repeat("本机连接未就绪，跳过此项", 8).ToArray();
        string latency = "延迟  未测得", loss = "未响应  无法判断";
        PortalState portal = PortalState.Unknown;
        states[0] = link != null && link.AdapterName != null ? "正常" : "异常";
        details[0] = link == null ? "未发现活动网卡" : link.AdapterName ?? "未发现活动网卡";
        bool ready = link != null && link.State == LinkState.Ready;
        states[1] = link != null && (ready || link.State == LinkState.NoIp) ? "正常" : "异常";
        details[1] = link == null || link.State == LinkState.Disconnected ? "网卡未连接" :
            link.State == LinkState.WifiDisconnected ? "Wi-Fi 已断开" : link.Wireless ? "无线网卡已连接" : "有线网卡已连接";
        states[2] = ready ? "正常" : "异常"; details[2] = ready ? "IPv4  " + link.Ip : "尚未取得有效 IPv4 或网关";
        if (ready)
        {
            string gateway = CampusTargets.Gateway(link);
            var pings = new CampusProbeResult[4];
            for (int i = 0; i < pings.Length; i++) pings[i] = gateway == null ? CampusProbeResult.Skipped("不在私网网关检测范围") : probe.Ping(gateway, CancellationToken.None);
            CampusPingStatistics stats = CampusPingStatistics.From(pings);
            states[3] = stats.Replies > 0 ? "正常" : "无法判断";
            details[3] = stats.Replies > 0 ? "当前网关 " + gateway + " 已响应" : "网关未响应或不可检测；可能禁 Ping";
            latency = stats.Replies > 0 ? "延迟  " + stats.Average.Value.ToString("0.#") + " ms（网关）" : "延迟  未测得";
            loss = stats.Replies > 0 ? "未响应  " + (stats.Attempts - stats.Replies) + "/" + stats.Attempts + " 次" : "未响应  无法判断（ICMP）";
            states[7] = stats.Replies == 4 ? "正常" : stats.Replies > 0 ? "需关注" : "无法判断";
            details[7] = "4 次网关 Ping，仅反映此刻；偶发波动请使用持续监测";
            try { details[4] = dns(link); states[4] = details[4].StartsWith("已配置", StringComparison.Ordinal) ? "正常" : "无法判断"; }
            catch { states[4] = "无法判断"; details[4] = "DNS 配置无法读取；未发送域名查询"; }
            try { portal = authentication(); } catch { portal = PortalState.Unknown; }
            states[5] = portal == PortalState.Online ? "正常" : portal == PortalState.AuthenticationRequired ? "需认证" : "无法判断";
            details[5] = portal == PortalState.Online ? "学校认证接口确认已认证" : portal == PortalState.AuthenticationRequired ? "学校认证页明确要求登录" : "学校认证状态无法确认，暂不恢复";
            try { details[6] = probe.PortalHttp(CancellationToken.None); states[6] = HttpState(details[6]); }
            catch { states[6] = "无法判断"; details[6] = "校内认证页访问未完成"; }
        }
        string advice = !ready ? "请检查 Wi-Fi / 网线及 IP 获取情况；当前不会尝试校园网认证。" :
            portal == PortalState.AuthenticationRequired ? "学校认证页要求重新登录。可点击“修复校园网认证”使用已保存账号恢复。" :
            portal == PortalState.Unknown ? "学校认证状态暂无法确认。Ping 未响应不能证明认证失效，请稍后复检。" :
            states[3] != "正常" ? "学校确认认证在线。网关可能禁 Ping，可开启持续监测观察；不要因 Ping 未响应重复登录。" :
            "校内连接和认证检测已完成。持续监测可记录偶发波动；这些结果不代表游戏服务器线路。";
        return new DiagnosisSnapshot { States = states, Details = details, Latency = latency, Loss = loss, Advice = advice,
            Summary = "已完成 8 项校内检查 · " + states.Count(s => s == "正常") + " 项正常 · " + DateTime.Now.ToString("HH:mm:ss") };
    }
    internal static string HttpState(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text ?? "", @"HTTP (\d{3})"); int code;
        return match.Success && Int32.TryParse(match.Groups[1].Value, out code) ? code >= 200 && code < 400 ? "正常" : "需关注" : "无法判断";
    }
    internal static string Report(DiagnosisSnapshot result)
    {
        string[] names = { "网络适配器", "Wi-Fi / 有线连接", "IP 地址获取", "私网网关", "本机 DNS 配置", "校园网认证", "校内认证页访问", "网关采样" };
        return result.Summary + Environment.NewLine + String.Join(Environment.NewLine, names.Select((n, i) => "[" + n + "] " + result.States[i] + " · " + result.Details[i])) +
            Environment.NewLine + result.Advice + Environment.NewLine + "连接检查仅使用私网网关和学校认证服务器；未连接公网测试站点，未发送 DNS 解析查询。";
    }
}
