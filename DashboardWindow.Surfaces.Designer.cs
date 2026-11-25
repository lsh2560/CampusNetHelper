using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

internal sealed partial class DashboardWindow
{
    private const int HomeMaxWidth = 1040;
    private CardPanel gameRecord, gameWifi, gameTips;
    private CampusStabilityView stabilityView;
    private Label gameWifiSignal, gameWifiDetail, gameWifiRoams;
    private CardPanel checkHero, checkResults, checkNetwork, checkQuality, checkAdvice, checkTools;
    private ConnectionRoute diagnosisRoute;

    private void ArrangeHomeSurface()
    {
        int available = Math.Max(1, content.Width - 36);
        int width = Math.Min(available, HomeMaxWidth);
        int x = 18 + (available - width) / 2;
        hero.SetBounds(x, 12, width, 236);
        statusIcon.SetBounds(24, 24, 64, 64);
        heroMetricFrame.SetBounds(24, 116, width - 48, 96);
        foreach (Label value in new[] { pingText, heroDown, wifiValue }) value.Font = heroMetricFrame.Width < 600 ? metricCompactFont : metricWideFont;
        LayoutHeroText();
        gameCard.SetBounds(x, hero.Bottom + 16, width, 84);
        gameButton.SetBounds(width - 24 - 156, 22, 156, 40);
        foreach (Label label in gameCard.Controls.OfType<Label>()) label.Width = Math.Max(1, gameButton.Left - label.Left - 16);
        eventsCard.SetBounds(x, gameCard.Bottom + 16, width, 56 + recentLines.Length * 30 + 16);
        eventsCard.Controls.OfType<Label>().First(l => l.Name == "HomeAllLogs").SetBounds(width - 154, 18, 132, 28);
        foreach (Label line in recentLines) line.Width = width - 44;
        home.AutoScrollMinSize = new Size(0, eventsCard.Bottom + 20);
        foreach (CardPanel card in new[] { hero, gameCard, eventsCard }) FitCardContents(card);
    }

    // The re-authenticate action only appears when it is useful, so the status
    // text gives way to it instead of being covered.
    private void LayoutHeroText()
    {
        if (hero == null) return;
        int width = hero.Width;
        loginButton.SetBounds(width - 24 - 136, 34, 136, 42);
        repairButton.SetBounds(loginButton.Left - 12 - 124, 34, 124, 42);
        int textRight = (repairButton.Visible ? repairButton.Left : loginButton.Left) - 16;
        statusTitle.SetBounds(104, 26, Math.Max(1, textRight - 104), 40);
        statusDetail.SetBounds(106, 68, Math.Max(1, textRight - 106), 26);
    }

    private void BuildGamePage()
    {
        pageBody.Text = "打游戏时开着，卡的时候点一下，帮你判断是无线、校园网还是你自己的下载造成的";
        gameRecord = ColumnCard(100, 600, "left");
        gameRecord.Padding = new Padding(5);
        stabilityView = new CampusStabilityView(StabilityFactory(), delegate { return speedup != null && speedup.Running; }) { Dock = DockStyle.Fill };
        stabilityView.RunningChanged = UpdateGameCard;
        gameRecord.Controls.Add(stabilityView);

        gameWifi = ColumnCard(100, 236, "right");
        Heading(gameWifi, UiSymbol.Wifi, "Wi-Fi 状态");
        gameWifiSignal = new Label { Name = "GameWifiSignal", Text = "—", Font = UiFont.Numeric(22f), ForeColor = SurfaceColor.Ink, BackColor = Color.Transparent, Location = new Point(22, 66), Size = new Size(300, 44) };
        gameWifiDetail = Label("正在读取…", 10, false, SurfaceColor.Muted, 24, 114, 300, 54);
        gameWifiDetail.Name = "GameWifiDetail";
        gameWifiRoams = Label("", 9, false, SurfaceColor.Muted, 24, 176, 300, 26);
        gameWifiRoams.Name = "GameWifiRoams";
        gameWifi.Controls.Add(gameWifiSignal); gameWifi.Controls.Add(gameWifiDetail); gameWifi.Controls.Add(gameWifiRoams);

        gameTips = ColumnCard(352, 230, "right");
        Heading(gameTips, UiSymbol.Bulb, "开局前建议");
        string[] tips = {
            "暂停 Steam、网盘等下载和上传",
            "能插网线就插网线；用无线尽量连 5 GHz",
            "卡的时候点“刚才卡了”，打完导出报告",
        };
        for (int i = 0; i < tips.Length; i++)
        {
            Label tip = Label("·  " + tips[i], 10, false, SurfaceColor.Ink, 24, 68 + i * 34, 300, 30);
            tip.Name = "GameTip" + i; gameTips.Controls.Add(tip);
        }
        FlatAction processes = Button("查看占网程序", Color.White, SurfaceColor.Blue, 24, 172, 160, 38);
        processes.Name = "GameProcesses"; processes.ForeColor = SurfaceColor.Blue; processes.Symbol = UiSymbol.External;
        processes.Click += delegate {
            try { Process.Start(new ProcessStartInfo("resmon.exe") { UseShellExecute = true }); }
            catch (Exception ex) { ShowNotice("无法打开资源监视器：" + ex.Message); }
        };
        gameTips.Controls.Add(processes);
        ApplyGameWifi();
    }

    private void ArrangeGameSurface()
    {
        int available = Math.Max(1, content.Width - 40);
        bool columns = available >= 960;
        int right = columns ? Math.Max(320, (int)(available * .34)) : available;
        int left = columns ? available - right - 16 : available;
        int rightX = columns ? 36 + left : 20;
        gameRecord.SetBounds(20, 100, left, 600);
        int top = columns ? 100 : gameRecord.Bottom + 16;
        gameWifi.SetBounds(rightX, top, right, 216);
        gameTips.SetBounds(rightX, gameWifi.Bottom + 16, right, 230);
        foreach (CardPanel card in new[] { gameWifi, gameTips }) FitCardContents(card);
        foreach (Label label in new[] { gameWifiSignal, gameWifiDetail, gameWifiRoams }) label.Width = Math.Max(1, right - 46);
        otherPage.AutoScrollMinSize = new Size(0, Math.Max(gameRecord.Bottom, gameTips.Bottom) + 24);
    }

    // The home card mirrors whether game mode is recording, so it is never left
    // running unnoticed after switching pages.
    private void UpdateGameCard()
    {
        bool running = stabilityView != null && stabilityView.Running;
        gameButton.Text = running ? "查看记录" : "开启游戏模式";
        gameButton.Symbol = running ? UiSymbol.Chart : UiSymbol.Play;
        Label title = gameCard.Controls.OfType<Label>().First(l => l.Name == "HomeGameTitle");
        Label body = gameCard.Controls.OfType<Label>().First(l => l.Name == "HomeGameBody");
        title.Text = running ? "游戏模式正在记录" : "要打游戏？";
        body.Text = running ? "卡的时候到游戏模式页点“刚才卡了”，打完可以导出报告" : "开启游戏模式，记录卡顿是无线、校园网还是你自己的下载造成的";
    }

    private void ApplyGameWifi()
    {
        if (gameWifiSignal == null || gameWifiSignal.IsDisposed) return;
        if (currentLinkState == LinkState.Ready && !linkWireless)
        {
            gameWifiSignal.Text = "LAN";
            gameWifiDetail.Text = "有线连接：插着网线，不受无线干扰影响。";
            gameWifiRoams.Text = "";
            return;
        }
        if (lastWifi == null)
        {
            gameWifiSignal.Text = "—";
            gameWifiDetail.Text = "没有读到 Wi-Fi 信息。可能没连 Wi-Fi，或系统的无线服务没有运行。";
            gameWifiRoams.Text = "";
            return;
        }
        // The numeric face has no CJK glyphs; the grade goes on the text line below.
        gameWifiSignal.Text = CampusStabilityLog.Signal(lastWifi);
        gameWifiSignal.ForeColor = lastWifi.Weak ? Color.FromArgb(154, 95, 18) : SurfaceColor.Ink;
        gameWifiDetail.Text = "信号" + lastWifi.Grade + " · " + lastWifi.Ssid + (lastWifi.Band == null ? "" : " · " + lastWifi.Band + " · 信道 " + lastWifi.Channel) +
            "\n连接速率 " + lastWifi.RxMbps + " Mbps · 热点 " + CampusStabilityLog.Tail(lastWifi.Bssid);
        gameWifiRoams.Text = wifiRoamCount == 0 ? "打开软件以来没有切换过热点" : "打开软件以来切换了 " + wifiRoamCount + " 次热点";
    }

    private void BuildCheckupPage()
    {
        pageBody.Text = "一键检查网络的每个环节；结果可以截图发给网络中心或同学";
        checkHero = ColumnCard(100, 150, "left");
        Heading(checkHero, UiSymbol.Pulse, "网络体检");
        Label intro = Label("依次检查网卡、Wi-Fi、IP、网关、DNS、认证和学校服务器", 10, false, SurfaceColor.Muted, 68, 52, 480, 24);
        intro.Name = "CheckIntro"; checkHero.Controls.Add(intro);
        diagnosisRoute = new ConnectionRoute { Bounds = new Rectangle(500, 18, 225, 54) }; checkHero.Controls.Add(diagnosisRoute);
        diagnosisRun = Button("开始体检", SurfaceColor.Blue, Color.White, 22, 92, 220, 42);
        diagnosisRun.Name = "DiagnosisStart"; diagnosisRun.Symbol = UiSymbol.Play;
        FlatAction repair = Button("重新认证", Color.White, SurfaceColor.Ink, 256, 92, 160, 42);
        repair.Name = "CheckRepair"; repair.Symbol = UiSymbol.Wrench;
        diagnosisRun.Click += delegate { StartDetailedDiagnosis(diagnosisRun); }; repair.Click += delegate { RunLogin(true, repair); };
        checkHero.Controls.Add(diagnosisRun); checkHero.Controls.Add(repair);

        checkResults = ColumnCard(266, 540, "left");
        Heading(checkResults, UiSymbol.List, "体检结果");
        diagnosisSummary = Label("点“开始体检”后显示每一项的结果", 9, false, SurfaceColor.Muted, 68, 52, 620, 24);
        diagnosisSummary.Name = "DiagnosisSummary"; checkResults.Controls.Add(diagnosisSummary);
        string[] names = { "网络适配器", "Wi-Fi / 有线连接", "IP 地址", "宿舍网关", "本机 DNS 配置", "校园网认证", "学校认证页", "网关稳定性" };
        UiSymbol[] rowIcons = { UiSymbol.Router, UiSymbol.Wifi, UiSymbol.Info, UiSymbol.Router, UiSymbol.Globe, UiSymbol.Shield, UiSymbol.Globe, UiSymbol.Chart };
        diagnosisStates = new Label[names.Length]; diagnosisDetails = new Label[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            var row = new SurfacePanel { Bounds = new Rectangle(22, 86 + i * 54, 650, 50), BackColor = Color.White, Fill = SurfaceColor.Pale, Edge = SurfaceColor.Pale, Radius = 8 };
            row.Controls.Add(new UiIcon { Symbol = rowIcons[i], Tint = SurfaceColor.Blue, Bounds = new Rectangle(10, 8, 34, 34) });
            row.Controls.Add(Label(names[i], 10, true, SurfaceColor.Ink, 56, 4, 260, 25));
            diagnosisDetails[i] = Label("等待检测", 9, false, SurfaceColor.Muted, 56, 27, 450, 22);
            diagnosisStates[i] = Label("待检测", 9, false, SurfaceColor.Muted, 540, 12, 92, 25);
            diagnosisStates[i].TextAlign = ContentAlignment.MiddleRight;
            row.Controls.Add(diagnosisDetails[i]); row.Controls.Add(diagnosisStates[i]); checkResults.Controls.Add(row);
        }

        checkNetwork = ColumnCard(100, 214, "right");
        Heading(checkNetwork, UiSymbol.Laptop, "当前网络");
        var state = new StatePill { Name = "DiagnosisLink", Text = "正在读取", Font = new Font("Microsoft YaHei UI", 9.5f), ForeColor = SurfaceColor.Muted, BackColor = Color.White, Bounds = new Rectangle(250, 24, 100, 28) };
        checkNetwork.Controls.Add(state);
        string[] rows = { "DiagnosisWifi", "DiagnosisAdapter", "DiagnosisIp", "DiagnosisGateway" };
        for (int i = 0; i < rows.Length; i++)
        {
            Label line = Label("—", 10, false, SurfaceColor.Ink, 24, 66 + i * 34, 300, 28);
            line.Name = rows[i]; checkNetwork.Controls.Add(line);
        }

        checkQuality = ColumnCard(330, 160, "right");
        Heading(checkQuality, UiSymbol.Chart, "延迟与回应");
        diagnosisLatency = new QualityValueLabel { Name = "DiagnosisLatency", Caption = "延迟", Symbol = UiSymbol.Clock, Text = "延迟  等待检测", Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold), ForeColor = SurfaceColor.Blue, BackColor = Color.White };
        diagnosisLoss = new QualityValueLabel { Name = "DiagnosisLoss", Caption = "未响应", Symbol = UiSymbol.Chart, Text = "未响应  等待检测", Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold), ForeColor = SurfaceColor.Green, BackColor = Color.White };
        checkQuality.Controls.Add(diagnosisLatency); checkQuality.Controls.Add(diagnosisLoss);

        checkAdvice = ColumnCard(506, 170, "right");
        Heading(checkAdvice, UiSymbol.Bulb, "建议");
        diagnosisAdvice = Label("体检完成后，这里会根据结果给出建议。", 10, false, SurfaceColor.Muted, 24, 64, 370, 50);
        diagnosisAdvice.Name = "DiagnosisAdvice"; checkAdvice.Controls.Add(diagnosisAdvice);
        diagnosisRetry = Button("重新体检", Color.White, SurfaceColor.Ink, 24, 116, 160, 38); diagnosisRetry.Name = "DiagnosisRetry"; diagnosisRetry.Symbol = UiSymbol.Refresh;
        FlatAction portal = Button("打开认证页", Color.White, SurfaceColor.Ink, 196, 116, 160, 38); portal.Name = "CheckPortal"; portal.Symbol = UiSymbol.External;
        diagnosisRetry.Click += delegate { StartDetailedDiagnosis(diagnosisRun); }; portal.Click += delegate { OpenPortal(); };
        checkAdvice.Controls.Add(diagnosisRetry); checkAdvice.Controls.Add(portal);

        checkTools = ColumnCard(692, 250, "right");
        Heading(checkTools, UiSymbol.Tools, "小工具");
        AntdUI.Input host = AppUiTheme.Input("210.43.112.9");
        host.Name = "ToolsHost"; host.AccessibleName = "检测目标 IP"; host.SetBounds(24, 66, 200, 38);
        checkTools.Controls.Add(host);
        FlatAction ping = Button("Ping", SurfaceColor.Blue, Color.White, 236, 64, 90, 40);
        FlatAction dns = Button("DNS 配置", Color.White, SurfaceColor.Ink, 334, 64, 110, 40);
        ping.Name = "ToolsPing"; dns.Name = "ToolsDns";
        checkTools.Controls.Add(ping); checkTools.Controls.Add(dns);
        var outputFrame = new SurfacePanel { Name = "ToolsOutputFrame", Bounds = new Rectangle(24, 116, 400, 70), BackColor = Color.White, Fill = UiColor.Pale, Edge = SurfaceColor.Border, Padding = new Padding(12, 8, 6, 6) };
        TextBox output = new TextBox { Name = "ToolsOutput", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BackColor = UiColor.Pale, BorderStyle = BorderStyle.None,
            Font = new Font("Microsoft YaHei UI", 9.5f), ForeColor = UiColor.Ink, Text = "输入校内 IP 测延迟，或查看本机的 DNS 配置。" };
        outputFrame.Controls.Add(output); checkTools.Controls.Add(outputFrame);
        ping.Click += delegate { RunHostTool(host.Text, true, output, ping, dns); };
        dns.Click += delegate { RunHostTool(host.Text, false, output, ping, dns); };
        FlatAction windows = Button("Windows 网络设置", Color.White, SurfaceColor.Ink, 24, 198, 200, 38);
        windows.Name = "ToolsWindowsSettings"; windows.Symbol = UiSymbol.External;
        windows.Click += delegate { try { Process.Start(new ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true }); } catch (Exception ex) { ShowNotice(ex.Message); } };
        checkTools.Controls.Add(windows);
        checkTools.Resize += delegate
        {
            int w = checkTools.Width;
            dns.SetBounds(w - 24 - 110, 64, 110, 40); ping.SetBounds(dns.Left - 8 - 90, 64, 90, 40);
            host.SetBounds(24, 66, Math.Max(100, ping.Left - 8 - 24), 38);
            outputFrame.SetBounds(24, 116, Math.Max(1, w - 48), 70);
        };
    }

    private void ArrangeCheckupSurface()
    {
        int available = Math.Max(1, content.Width - 40);
        bool columns = available >= 990;
        int right = columns ? Math.Max(340, (int)(available * .38)) : available;
        int left = columns ? available - right - 16 : available;
        int rightX = columns ? 36 + left : 20;
        checkHero.SetBounds(20, 100, left, 150);
        checkResults.SetBounds(20, checkHero.Bottom + 16, left, 86 + diagnosisStates.Length * 54 + 12);
        int top = columns ? 100 : checkResults.Bottom + 16;
        checkNetwork.SetBounds(rightX, top, right, 214);
        checkQuality.SetBounds(rightX, checkNetwork.Bottom + 16, right, 184);
        checkAdvice.SetBounds(rightX, checkQuality.Bottom + 16, right, 170);
        checkTools.SetBounds(rightX, checkAdvice.Bottom + 16, right, 250);
        foreach (CardPanel card in new[] { checkHero, checkResults, checkNetwork, checkQuality, checkAdvice, checkTools }) FitCardContents(card);
        int action = Math.Min(240, (left - 60) / 2);
        diagnosisRun.SetBounds(22, 92, action, 42);
        checkHero.Controls.OfType<FlatAction>().First(b => b != diagnosisRun).SetBounds(38 + action, 92, Math.Min(180, action), 42);
        diagnosisRoute.SetBounds(left - 250, 18, 225, 54); diagnosisRoute.Visible = left >= 760;
        foreach (Label label in checkHero.Controls.OfType<Label>()) label.Width = Math.Max(1, (diagnosisRoute.Visible ? diagnosisRoute.Left : left - 24) - label.Left - 8);
        foreach (SurfacePanel row in checkResults.Controls.OfType<SurfacePanel>()) row.Width = left - 44;
        diagnosisSummary.Width = left - 92;
        for (int i = 0; i < diagnosisStates.Length; i++)
        {
            diagnosisStates[i].SetBounds(left - 150, 12, 92, 25);
            diagnosisDetails[i].SetBounds(56, 27, Math.Max(1, left - 216), 22);
        }
        var link = checkNetwork.Controls.OfType<Label>().First(l => l.Name == "DiagnosisLink"); link.SetBounds(right - 120, 24, 98, 28);
        checkNetwork.Controls.OfType<Label>().First(l => l.Text == "当前网络").Width = Math.Max(60, right - 200);
        foreach (Label label in checkNetwork.Controls.OfType<Label>().Where(l => l.Name.StartsWith("Diagnosis") && l.Name != "DiagnosisLink")) label.Width = right - 48;
        diagnosisLatency.SetBounds(20, 66, right - 40, 48); diagnosisLoss.SetBounds(20, 122, right - 40, 48);
        diagnosisAdvice.SetBounds(24, 62, right - 48, 50);
        int half = (right - 60) / 2;
        diagnosisRetry.SetBounds(24, 116, half, 38);
        checkAdvice.Controls.OfType<FlatAction>().First(b => b != diagnosisRetry).SetBounds(diagnosisRetry.Right + 12, 116, half, 38);
        otherPage.AutoScrollMinSize = new Size(0, Math.Max(checkResults.Bottom, checkTools.Bottom) + 24);
    }
}
