using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

internal sealed class EnvironmentCheckItem
{
    public string name { get; set; }
    public bool ok { get; set; }
    public bool required { get; set; }
    public string detail { get; set; }
}

internal sealed class EnvironmentCheckReport
{
    public bool ready { get; set; }
    public List<EnvironmentCheckItem> checks { get; set; }

    public string Format()
    {
        var lines = new List<string> { ready ? "运行环境已就绪" : "运行环境需要处理以下问题", "" };
        foreach (EnvironmentCheckItem check in checks)
            lines.Add((check.ok ? "✓ " : check.required ? "✕ " : "· ") + check.name + "：" + check.detail);
        return String.Join(Environment.NewLine, lines.ToArray());
    }
}

internal static class EnvironmentCheck
{
    private static void Add(EnvironmentCheckReport report, string name, bool ok, bool required, string detail)
    {
        report.checks.Add(new EnvironmentCheckItem { name = name, ok = ok, required = required, detail = detail });
    }

    public static EnvironmentCheckReport Run(bool requireAccount)
    {
        var report = new EnvironmentCheckReport { checks = new List<EnvironmentCheckItem>() };
        Add(report, "认证组件", true, true, "已内置于主程序，认证和检测无需 Node、脚本或浏览器");
        bool toolsReady = File.Exists(Path.Combine(AppPaths.Root, "MirrorConfig.ps1")) && File.Exists(Path.Combine(AppPaths.Root, "DnsConfig.ps1"));
        Add(report, "下载与提速组件", toolsReady, true, toolsReady ? "测速、下载和配置组件齐全" : "缺少换源或 DNS 配置组件，请重新安装");
        string uiLibrary = Path.Combine(AppPaths.Root, "AntdUI.dll");
        bool uiReady = File.Exists(uiLibrary);
        if (uiReady)
        {
            using (var sha = SHA256.Create()) using (var input = File.OpenRead(uiLibrary))
                uiReady = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").Equals("407B39BDE1EF89AB4AA9B37B9E1786E0229E2FB58F1A246EB146C08C90F611BD", StringComparison.OrdinalIgnoreCase);
        }
        Add(report, "AntdUI 界面组件", uiReady, true, uiReady ? "2.3.0 组件完整，校验通过" : "界面组件缺失或不匹配，请重新安装");

        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            string probe = Path.Combine(AppPaths.Data, "write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "ok", Encoding.ASCII);
            File.Delete(probe);
            Add(report, "本地数据目录", true, true, "当前 Windows 用户可写入");
        }
        catch (Exception ex) { Add(report, "本地数据目录", false, true, ex.Message); }

        if (!File.Exists(AppPaths.Secret))
            Add(report, "校园网账号", false, requireAccount, "尚未保存；安装后在“账号与认证”页面填写");
        else
        {
            try
            {
                SavedCredential saved = CredentialStore.Read();
                byte[] encrypted = Convert.FromBase64String(saved.password);
                byte[] clear = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                try { Add(report, "校园网账号", !String.IsNullOrWhiteSpace(saved.username) && clear.Length > 0,
                    requireAccount, "当前用户的 DPAPI 凭据可读取"); }
                finally { Array.Clear(clear, 0, clear.Length); }
            }
            catch (Exception ex) { Add(report, "校园网账号", false, requireAccount, "加密凭据无法读取：" + ex.Message); }
        }

        try
        {
            LinkSnapshot link = PortalProbe.Link();
            Add(report, "当前网络", link.State == LinkState.Ready, false,
                link.State == LinkState.Ready ? (link.AdapterName ?? "已连接") + " · " + (link.Ip ?? "") :
                "网络未就绪；安装可以继续，连接校园网后再登录");
        }
        catch (Exception ex) { Add(report, "当前网络", false, false, ex.Message); }

        report.ready = report.checks.Where(item => item.required).All(item => item.ok);
        return report;
    }
}
