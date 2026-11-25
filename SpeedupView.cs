using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class AdapterChoice
{
    public NetworkInterface Adapter;
    public override string ToString() { return Adapter.Name + " · " + Adapter.OperationalStatus; }
}
internal sealed class SpeedupView : UserControl
{
    private readonly AntdUI.Tabs tabs = new AntdUI.Tabs();
    private readonly List<Control> actions = new List<Control>();
    private readonly Label status = new Label();
    private readonly FlatAction stop = new FlatAction();
    private readonly ToolTip details = new ToolTip { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 150 };
    private CancellationTokenSource cancellation;
    private bool running;
    internal bool Running { get { return running; } }
    private UiResultTable mirrors, streams, dnsGrid;
    private AntdUI.Input probePath, testUrl, downloadUrl, hash, filePath;
    private ComboBox provider, ecosystem, sample, connections, adapters;
    private Label mirrorConfig, dnsCurrent, suggestion, downloadState;
    private ProgressBar downloadProgress;
    private string lastDownload;

    public SpeedupView()
    {
        BackColor = UiColor.Canvas; Padding = new Padding(24, 16, 24, 18);
        Font = new Font("Microsoft YaHei UI", 10.5f);
        ForeColor = UiColor.Ink;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        root.Controls.Add(new Label { Text = "高级工具", Font = new Font(Font.FontFamily, 21, FontStyle.Bold), ForeColor = UiColor.Ink, Dock = DockStyle.Fill, UseCompatibleTextRendering = false }, 0, 0);
        bool virtualLink = NetworkInterface.GetAllNetworkInterfaces().Any(a => a.OperationalStatus == OperationalStatus.Up &&
            (a.Name.IndexOf("clash", StringComparison.OrdinalIgnoreCase) >= 0 || a.Description.IndexOf("wintun", StringComparison.OrdinalIgnoreCase) >= 0));
        root.Controls.Add(new Label { Text = virtualLink ? "当前存在代理或虚拟网卡，测速结果可能包含代理路径。测速会消耗流量。" : "镜像测速与换源、多连接下载、DNS 设置。测速会消耗流量，高峰期请少用。", ForeColor = UiColor.Muted, Dock = DockStyle.Fill, UseCompatibleTextRendering = false }, 0, 1);
        tabs.Dock = DockStyle.Fill; tabs.Font = Font;
        tabs.ItemSize = 160; tabs.Type = AntdUI.TabType.Line; tabs.Fill = UiColor.Blue; tabs.FillActive = UiColor.Blue;
        tabs.ForeColor = SurfaceColor.Muted; tabs.FillHover = SurfaceColor.BlueHover;
        tabs.Padding = Padding.Empty;
        root.Controls.Add(tabs, 0, 2);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        status.Text = "就绪"; status.Dock = DockStyle.Fill; status.ForeColor = UiColor.Ink; status.TextAlign = ContentAlignment.MiddleLeft; status.AutoEllipsis = true; status.UseCompatibleTextRendering = false;
        stop.Text = "停止任务"; stop.Dock = DockStyle.Fill; stop.Margin = new Padding(2, 9, 2, 5); stop.Enabled = false;
        StyleButton(stop, false);
        stop.Click += delegate { if (cancellation != null) { cancellation.Cancel(); status.Text = "正在停止，等待当前请求释放…"; stop.Enabled = false; } };
        footer.Controls.Add(status, 0, 0); footer.Controls.Add(stop, 1, 0); root.Controls.Add(footer, 0, 3);
        Controls.Add(root);
        BuildMirrors(); BuildStreams(); BuildDownload(); BuildDns();
        foreach (Label label in new[] { status, mirrorConfig, dnsCurrent, suggestion, downloadState })
        {
            details.SetToolTip(label, label.Text);
            label.TextChanged += delegate(object sender, EventArgs e) { var target = (Label)sender; details.SetToolTip(target, target.Text); };
        }
    }
    private AntdUI.TabPage Page(string title)
    {
        var page = new AntdUI.TabPage { Text = title, BackColor = Color.White, Padding = new Padding(18, 14, 18, 16), AutoScroll = true };
        tabs.Pages.Add(page); return page;
    }
    private new TableLayoutPanel Layout(AntdUI.TabPage page, params int[] heights)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = false, Height = heights.Sum(), MinimumSize = new Size(0, heights.Sum()) };
        for (int i = 0; i < heights.Length; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, heights[i]));
        page.Controls.Add(layout); return layout;
    }
    private static Label Note(string text) { return new Label { Text = text, Dock = DockStyle.Fill, ForeColor = UiColor.Muted, AutoEllipsis = false, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Microsoft YaHei UI", 10.5f), UseCompatibleTextRendering = false }; }
    private static FlowLayoutPanel Row() { return new AdaptiveActionRow { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 1, 0, 0) }; }
    private FlatAction Action(string text, int width, EventHandler click)
    {
        var button = new FlatAction { Text = text, Width = width, Height = 38 };
        StyleButton(button, text == "测试全部服务器" || text == "开始两轮对比" || text == "开始下载" || text == "应用选中的 DNS");
        button.Click += click; actions.Add(button); return button;
    }
    private static void StyleButton(FlatAction button, bool primary)
    {
        button.Font = new Font("Microsoft YaHei UI", 10.5f);
        button.BackColor = primary ? UiColor.Blue : Color.White;
        button.ForeColor = primary ? Color.White : UiColor.Ink;
    }
    private static ComboBox Choice(params object[] values)
    {
        var combo = new UiCombo { Width = 135, Font = new Font("Microsoft YaHei UI", 10f), ItemHeight = 24, BackColor = Color.White, ForeColor = UiColor.Ink, Margin = new Padding(3, 6, 6, 3) };
        combo.Items.AddRange(values); if (values.Length > 0) combo.SelectedIndex = 0; return combo;
    }
    private static UiResultTable Grid(params string[] columns) { return new UiResultTable(columns); }
    private static AntdUI.Input Field(string value) {
        var input = AppUiTheme.Input(value); input.Dock = DockStyle.Fill; input.Margin = new Padding(0, 5, 0, 5); return input;
    }
    private void BuildMirrors()
    {
        var page = Page("镜像服务器"); var layout = Layout(page, 52, 44, 48, 275, 54, 52, 88, 88);
        layout.Controls.Add(Note("同一 Ubuntu 文件的单连接采样，每站两次各 4 MiB（合计约 48 MiB）。结果只代表当前线路和探针文件。"), 0, 0);
        probePath = Field(MirrorSite.ProbePath); probePath.AccessibleName = "镜像测速文件相对路径"; layout.Controls.Add(probePath, 0, 1);
        var buttons = Row(); buttons.Controls.Add(Action("测试全部服务器", 155, RunMirrorTests));
        buttons.Controls.Add(Action("使用选中站测试连接数", 190, delegate { UseMirror(false); }));
        buttons.Controls.Add(Action("恢复默认探针", 145, delegate { probePath.Text = MirrorSite.ProbePath; })); layout.Controls.Add(buttons, 0, 2);
        mirrors = Grid("服务器", "MB/s", "Mbps", "耗时 s", "有效采样", "结果 / HTTP");
        mirrors.Columns[5].FillWeight = 200;
        foreach (var site in MirrorSite.All) mirrors.AddRow(site.Name, "—", "—", "—", "—", "待测试");
        mirrors.SelectionChanged += delegate { if (provider != null && mirrors.CurrentRow != null && mirrors.CurrentRow.Index < 3) provider.SelectedIndex = mirrors.CurrentRow.Index; };
        layout.Controls.Add(mirrors, 0, 3);
        layout.Controls.Add(Note("上表测 Ubuntu 文件，下方切换所选软件源，地址与可用性分别检查。清华、中科大、网易仅供下载参考。"), 0, 4);
        var configRow = Row(); provider = Choice("腾讯云", "华为云", "阿里云", "官方源"); ecosystem = Choice("pip", "npm", "conda", "go", "cargo"); ecosystem.Width = 92;
        configRow.Controls.Add(provider); configRow.Controls.Add(ecosystem);
        configRow.Controls.Add(Action("应用到此软件", 130, delegate { ConfigureMirror(false); }));
        configRow.Controls.Add(Action("恢复原配置", 125, delegate { ConfigureMirror(true); }));
        configRow.Controls.Add(Action("查看当前配置", 130, delegate { ReadMirrorStatus(); })); layout.Controls.Add(configRow, 0, 5);
        mirrorConfig = Note("配置在应用前会检查目标软件源是否可用，并备份原文件；恢复会还原首次切换前的文件。项目配置和环境变量可能优先生效。");
        layout.Controls.Add(mirrorConfig, 0, 6);
        layout.Controls.Add(Note("说明：MB/s 为十进制兆字节/秒，Mbps 为兆比特/秒（约 8 倍）。Ubuntu 源的排名不代表 pip/npm 等软件源的排名。失败和限流单独显示，不计入最快结果。"), 0, 7);
    }
    private void UseMirror(bool unused)
    {
        if (mirrors.CurrentRow == null) return;
        int index = mirrors.CurrentRow.Index;
        testUrl.Text = MirrorSite.All[index].Base + probePath.Text.Trim(); tabs.SelectedIndex = 1;
    }
    private void BuildStreams()
    {
        var page = Page("1—8 连接测速"); var layout = Layout(page, 54, 44, 52, 357, 88, 64);
        layout.Controls.Add(Note("同一文件、相同采样量，两轮按正序与倒序测试 1—8 连接。建议只在网络空闲时测试。"), 0, 0);
        testUrl = Field(MirrorSite.All[0].Base + MirrorSite.ProbePath); testUrl.AccessibleName = "连接对比测试文件地址"; layout.Controls.Add(testUrl, 0, 1);
        var row = Row(); sample = Choice("8 MiB / 档", "16 MiB / 档", "32 MiB / 档"); sample.SelectedIndex = 1;
        row.Controls.Add(sample); row.Controls.Add(Action("开始两轮对比", 150, RunStreamTests));
        row.Controls.Add(Action("将选中连接数用于下载", 200, delegate {
            if (streams.CurrentRow != null) connections.SelectedIndex = streams.CurrentRow.Index;
            downloadUrl.Text = testUrl.Text; tabs.SelectedIndex = 2;
        })); layout.Controls.Add(row, 0, 2);
        streams = Grid("连接数", "第一轮 Mbps", "第二轮 Mbps", "综合 MB/s", "综合 Mbps", "状态");
        streams.Columns[5].FillWeight = 170;
        for (int i = 1; i <= 8; i++) streams.AddRow(i, "—", "—", "—", "—", "待测试"); layout.Controls.Add(streams, 0, 3);
        suggestion = Note("默认完整测试约 256 MiB；选择 8 / 32 MiB 时约 128 / 512 MiB。小文件按实际大小采样。综合速度 = 两轮有效字节 ÷ 两轮总耗时。"); layout.Controls.Add(suggestion, 0, 4);
        layout.Controls.Add(Note("测试结果随时间、文件与服务器变化。速度相差不超过 5% 时优先建议较少连接；不证明学校存在单连接限速。"), 0, 5);
    }
    private void BuildDownload()
    {
        var page = Page("多连接下载"); var layout = Layout(page, 40, 44, 40, 44, 52, 40, 44, 70, 36, 120, 64);
        layout.Controls.Add(Note("文件直链（支持 http / https）"), 0, 0); downloadUrl = Field(""); downloadUrl.AccessibleName = "下载地址"; layout.Controls.Add(downloadUrl, 0, 1);
        layout.Controls.Add(Note("保存位置（选择文件后开始下载）"), 0, 2);
        filePath = Field(""); filePath.ReadOnly = true; filePath.AccessibleName = "下载保存位置"; layout.Controls.Add(filePath, 0, 3);
        var row = Row(); connections = Choice("1 连接", "2 连接", "3 连接", "4 连接", "5 连接", "6 连接", "7 连接", "8 连接");
        row.Controls.Add(connections); row.Controls.Add(Action("选择保存位置", 145, ChooseDestination)); row.Controls.Add(Action("开始下载", 130, Download));
        row.Controls.Add(Action("打开所在文件夹", 160, delegate { if (lastDownload != null && File.Exists(lastDownload)) Process.Start("explorer.exe", "/select,\"" + lastDownload + "\""); })); layout.Controls.Add(row, 0, 4);
        layout.Controls.Add(Note("发布者提供的 SHA256（可选，用于验证文件真实性和完整性）"), 0, 5);
        hash = Field(""); hash.AccessibleName = "预期 SHA256"; layout.Controls.Add(hash, 0, 6);
        layout.Controls.Add(Note("服务器不支持分段时自动使用单连接。遇到限流会停止，不自动增加连接数。已有文件仅在成功下载并校验后替换。"), 0, 7);
        downloadProgress = new UiProgress { Dock = DockStyle.Fill, Maximum = 1000, Margin = new Padding(3, 7, 3, 7) }; layout.Controls.Add(downloadProgress, 0, 8);
        downloadState = Note("等待下载。可使用底部“停止任务”取消；切换页面不会中断任务。"); downloadState.AutoEllipsis = false; layout.Controls.Add(downloadState, 0, 9);
        layout.Controls.Add(Note("连接数限制为 1—8。请遵守校园网套餐与服务器规则，避免长时间占满共享带宽。"), 0, 10);
    }
    private void BuildDns()
    {
        var page = Page("DNS 设置"); var layout = Layout(page, 54, 52, 82, 238, 56, 100, 64);
        layout.Controls.Add(Note("仅修改所选网卡的 IPv4 DNS，IPv6 设置保持原样。外部 DNS 配置附带南华校内域名分流。"), 0, 0);
        var row = Row(); adapters = Choice(); adapters.Width = 385; row.Controls.Add(adapters);
        row.Controls.Add(Action("刷新网卡", 120, delegate { RefreshAdapters(); })); row.Controls.Add(Action("测试 DNS 延迟", 140, RunDnsTests)); layout.Controls.Add(row, 0, 1);
        dnsCurrent = Note(""); dnsCurrent.AutoEllipsis = false; layout.Controls.Add(dnsCurrent, 0, 2);
        dnsGrid = Grid("DNS 服务", "IPv4 地址", "延迟 ms", "状态"); dnsGrid.Columns[1].FillWeight = 210; dnsGrid.Columns[3].FillWeight = 150;
        dnsGrid.AddRow("腾讯 DNSPod", "119.29.29.29 / 182.254.116.116", "—", "未测试");
        dnsGrid.AddRow("阿里 DNS", "223.5.5.5 / 223.6.6.6", "—", "未测试");
        dnsGrid.AddRow("华为云 DNS", "122.112.208.1 / 139.9.23.90", "—", "未测试");
        dnsGrid.AddRow("校内 DNS", "210.43.112.1", "—", "未测试");
        dnsGrid.AddRow("自动获取", "由当前网络分配", "—", "不测试"); layout.Controls.Add(dnsGrid, 0, 3);
        var buttons = Row(); buttons.Controls.Add(Action("应用选中的 DNS", 170, delegate { ConfigureDns(false); }));
        buttons.Controls.Add(Action("恢复此网卡原 DNS", 185, delegate { ConfigureDns(true); })); layout.Controls.Add(buttons, 0, 4);
        layout.Controls.Add(Note("应用和恢复会由 Windows 请求管理员权限。首次应用前按网卡保存原模式与地址，恢复时仅删除助手创建的分流规则。已有兼容分流规则会保留，遇到冲突则停止。"), 0, 5);
        layout.Controls.Add(Note("延迟为首地址解析 www.qq.com 的三次中位数；只反映解析响应，不代表下载带宽。"), 0, 6);
        adapters.SelectedIndexChanged += delegate { ShowDns(); }; RefreshAdapters();
    }
    private void Ui(Action action)
    {
        if (!IsDisposed && IsHandleCreated) try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) action(); }); } catch (InvalidOperationException) { }
    }
    private async void Work(string label, bool cancellable, Func<CancellationToken, Task> job)
    {
        if (GameTrafficGuard.Active) { status.Text = "游戏免干扰已开启，请先停止校内监测或关闭其免干扰选项，再执行此操作。"; return; }
        if (running) return; running = true; cancellation = new CancellationTokenSource();
        foreach (Control action in actions) action.Enabled = false;
        SetInputs(tabs, false);
        stop.Enabled = cancellable; status.Text = label;
        try { await job(cancellation.Token); if (!cancellation.IsCancellationRequested) status.Text = "完成 · " + label; }
        catch (OperationCanceledException) { status.Text = "已停止 · 本次未完成的结果不作推荐"; }
        catch (Exception ex) { status.Text = "未完成：" + ex.Message; }
        finally {
            if (cancellation != null) { cancellation.Dispose(); cancellation = null; }
            running = false; if (!IsDisposed) { stop.Enabled = false; SetInputs(tabs, true); foreach (Control action in actions) if (!action.IsDisposed) action.Enabled = true; }
        }
    }
    private static void SetInputs(Control parent, bool enabled)
    {
        foreach (Control control in parent.Controls) { if (control is TextBox || control is AntdUI.Input || control is ComboBox) control.Enabled = enabled; SetInputs(control, enabled); }
    }
    private void RunMirrorTests(object sender, EventArgs args)
    {
        string path = probePath.Text.Trim();
        if (!path.StartsWith("/") || path.StartsWith("//") || path.Contains("://") || path.Contains("\\")) { status.Text = "请填写以 / 开头的镜像文件相对路径。"; return; }
        for (int i = 0; i < mirrors.Rows.Count; i++) { mirrors.Rows[i].SetValues(MirrorSite.All[i].Name, "—", "—", "—", "—", "等待测试"); mirrors.Rows[i].DefaultCellStyle.BackColor = Color.White; }
        Work("镜像服务器测速", true, token => Task.Run(delegate {
            double fastest = 0; int best = -1;
            for (int i = 0; i < MirrorSite.All.Length; i++)
            {
                token.ThrowIfCancellationRequested(); int index = i;
                Ui(delegate { status.Text = "正在测试 " + MirrorSite.All[index].Name; mirrors.Rows[index].Cells[5].Value = "测试中…"; });
                try {
                    RemoteFile file = TransferEngine.Inspect(TransferEngine.ParseUrl(MirrorSite.All[i].Base + path), token);
                    var first = TransferEngine.Measure(file, 1, 4 * 1024 * 1024, token);
                    if (token.WaitHandle.WaitOne(400)) token.ThrowIfCancellationRequested();
                    var second = TransferEngine.Measure(file, 1, 4 * 1024 * 1024, token);
                    var total = new TransferStats { Bytes = first.Bytes + second.Bytes, Seconds = first.Seconds + second.Seconds };
                    if (total.Mbps > fastest) { fastest = total.Mbps; best = i; }
                    Ui(delegate { mirrors.Rows[index].SetValues(MirrorSite.All[index].Name, total.MBps.ToString("0.00"), total.Mbps.ToString("0.0"), total.Seconds.ToString("0.00"), (total.Bytes / 1048576.0).ToString("0.##") + " MiB", "成功 · 206"); });
                } catch (OperationCanceledException) { Ui(delegate { mirrors.Rows[index].Cells[5].Value = "已停止"; }); throw; }
                catch (Exception ex) { string message = ex.Message; Ui(delegate { mirrors.Rows[index].Cells[5].Value = message; mirrors.Rows[index].Cells[5].ToolTipText = message; }); }
                if (token.WaitHandle.WaitOne(600)) token.ThrowIfCancellationRequested();
            }
            if (best >= 0) { int selected = best; Ui(delegate { mirrors.Rows[selected].Selected = true; mirrors.CurrentCell = mirrors.Rows[selected].Cells[0]; mirrors.Rows[selected].DefaultCellStyle.BackColor = Color.FromArgb(236, 245, 239); }); }
        }));
    }
    private void RunStreamTests(object sender, EventArgs args)
    {
        Uri url; try { url = TransferEngine.ParseUrl(testUrl.Text.Trim()); } catch (Exception ex) { status.Text = ex.Message; return; }
        long bytes = (8L << sample.SelectedIndex) * 1024 * 1024;
        for (int i = 0; i < 8; i++) { streams.Rows[i].SetValues(i + 1, "—", "—", "—", "—", "等待测试"); streams.Rows[i].DefaultCellStyle.BackColor = Color.White; }
        suggestion.Text = "测试中，建议值将在两轮完整结束后显示。本次最多约 " + (bytes * 16 / 1048576) + " MiB。";
        Work("1—8 连接对比测速", true, token => Task.Run(delegate {
            var file = TransferEngine.Inspect(url, token); var results = new TransferStats[8, 2];
            for (int round = 0; round < 2; round++) for (int step = 0; step < 8; step++)
            {
                token.ThrowIfCancellationRequested(); int index = round == 0 ? step : 7 - step; int r = round;
                Ui(delegate { status.Text = "第 " + (r + 1) + " 轮 · " + (index + 1) + " 连接"; streams.Rows[index].Cells[5].Value = "第 " + (r + 1) + " 轮测试中…"; });
                try {
                    TransferStats value = TransferEngine.Measure(file, index + 1, bytes, token); results[index, round] = value;
                    Ui(delegate { streams.Rows[index].Cells[r + 1].Value = value.Mbps.ToString("0.0"); streams.Rows[index].Cells[5].Value = r == 0 ? "第一轮完成" : "完成"; });
                } catch (OperationCanceledException) { throw; }
                catch (Exception ex) { string message = ex.Message; Ui(delegate { streams.Rows[index].Cells[5].Value = message; streams.Rows[index].Cells[5].ToolTipText = message; suggestion.Text = "测试因服务器错误停止，已完成值仅供参考：" + message; }); throw; }
                if (token.WaitHandle.WaitOne(700)) token.ThrowIfCancellationRequested();
            }
            double[] rates = new double[8];
            for (int i = 0; i < 8; i++) { var total = new TransferStats { Bytes = results[i, 0].Bytes + results[i, 1].Bytes, Seconds = results[i, 0].Seconds + results[i, 1].Seconds }; rates[i] = total.Mbps;
                int index = i; Ui(delegate { streams.Rows[index].Cells[3].Value = total.MBps.ToString("0.00"); streams.Rows[index].Cells[4].Value = total.Mbps.ToString("0.0"); }); }
            int best = Enumerable.Range(0, 8).First(i => rates[i] >= rates.Max() * .95);
            Ui(delegate { streams.CurrentCell = streams.Rows[best].Cells[0]; streams.Rows[best].DefaultCellStyle.BackColor = Color.FromArgb(236, 245, 239);
                suggestion.Text = "本次建议 " + (best + 1) + " 连接，综合 " + rates[best].ToString("0.0") + " Mbps；峰值 " + rates.Max().ToString("0.0") + " Mbps。点击“将选中连接数用于下载”后仍可手动调整。"; });
        }));
    }
    private void ChooseDestination(object sender, EventArgs args)
    {
        Uri url; try { url = TransferEngine.ParseUrl(downloadUrl.Text.Trim()); } catch (Exception ex) { status.Text = ex.Message; return; }
        string name = Path.GetFileName(url.LocalPath); foreach (char ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        using (var dialog = new SaveFileDialog { FileName = String.IsNullOrWhiteSpace(name) ? "download.bin" : name, Filter = "所有文件|*.*", OverwritePrompt = true })
            if (dialog.ShowDialog(this) == DialogResult.OK) filePath.Text = dialog.FileName;
    }
    private void Download(object sender, EventArgs args)
    {
        Uri url; try { url = TransferEngine.ParseUrl(downloadUrl.Text.Trim()); } catch (Exception ex) { status.Text = ex.Message; return; }
        if (String.IsNullOrWhiteSpace(filePath.Text)) { ChooseDestination(sender, args); if (String.IsNullOrWhiteSpace(filePath.Text)) return; }
        string destination = filePath.Text, expected = hash.Text.Trim(); int count = connections.SelectedIndex + 1;
        if (File.Exists(destination) && MessageBox.Show(this, "成功下载并校验后替换已有文件？\r\n" + destination, "保存下载", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        downloadProgress.Value = 0; downloadState.Text = "正在探测文件与分段支持…";
        Work("文件下载", true, token => Task.Run(delegate {
            try {
                var value = TransferEngine.Download(url, destination, count, true, expected, token, delegate(long received, long total, double seconds) {
                    Ui(delegate { downloadProgress.Value = total > 0 ? (int)Math.Min(1000, received * 1000.0 / total) : 0;
                        downloadState.Text = (received / 1000000.0).ToString("0.0") + " / " + (total >= 0 ? (total / 1000000.0).ToString("0.0") : "未知") + " MB · " + (received / 1000000.0 / Math.Max(.001, seconds)).ToString("0.00") + " MB/s（平均）"; });
                });
                lastDownload = destination; Ui(delegate { downloadProgress.Value = 1000; downloadState.Text = "完成：" + destination + "\r\n" + value.MBps.ToString("0.00") + " MB/s · SHA256：" + value.Sha256 + (String.IsNullOrEmpty(expected) ? "\r\n未提供发布者校验值；以上为本地文件摘要。" : "\r\n与发布者提供的 SHA256 一致。"); });
            } catch (Exception ex) { string message = ex is OperationCanceledException ? "已取消下载，原有文件保持不变。" : ex.Message; Ui(delegate { downloadState.Text = message; }); throw; }
        }));
    }
    private Dictionary<string, object> MirrorCommand(string action, string tool, string source)
    {
        string output; string script = Path.Combine(AppPaths.Root, "MirrorConfig.ps1");
        string arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -Action " + action + " -Tool " + tool + " -Provider " + source;
        int code = ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), arguments, 60000, out output);
        if (code == -1) throw new IOException(output);
        try { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(output.Trim('\uFEFF', ' ', '\r', '\n')); }
        catch { throw new IOException("无法读取换源结果，请检查配置工具是否完整。"); }
    }
    private void ConfigureMirror(bool restore)
    {
        string tool = ecosystem.Text; string source = new[] { "tencent", "huawei", "aliyun", "official" }[provider.SelectedIndex];
        Work(restore ? "恢复软件源" : "切换软件源", false, token => Task.Run(delegate {
            var result = MirrorCommand(restore ? "Restore" : "Apply", tool, source); string text = Convert.ToString(result["message"]);
            Ui(delegate { mirrorConfig.Text = text; }); if (!(bool)result["ok"]) throw new IOException(text);
        }));
    }
    private void ReadMirrorStatus()
    {
        Work("读取当前软件源", false, token => Task.Run(delegate {
            var result = MirrorCommand("Status", "pip", "official");
            if (!(bool)result["ok"]) throw new IOException(Convert.ToString(result["message"]));
            var lines = new List<string>(); foreach (var item in (System.Collections.IEnumerable)result["items"]) { var record = (Dictionary<string, object>)item;
                lines.Add(record["tool"] + ": " + record["description"] + ((bool)record["canRestore"] ? "（可恢复）" : "")); }
            Ui(delegate { mirrorConfig.Text = String.Join("  |  ", lines); });
        }));
    }
    private void RefreshAdapters()
    {
        string previous = adapters.SelectedItem is AdapterChoice ? ((AdapterChoice)adapters.SelectedItem).Adapter.Id : "";
        adapters.Items.Clear();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(a => a.NetworkInterfaceType != NetworkInterfaceType.Loopback && a.NetworkInterfaceType != NetworkInterfaceType.Tunnel).OrderByDescending(a => a.OperationalStatus == OperationalStatus.Up).ThenBy(a => a.Name.IndexOf("clash", StringComparison.OrdinalIgnoreCase) >= 0 || a.Description.IndexOf("wintun", StringComparison.OrdinalIgnoreCase) >= 0).ThenByDescending(a => HasGateway(a)))
        { try { if (adapter.GetIPProperties().GetIPv4Properties() != null) adapters.Items.Add(new AdapterChoice { Adapter = adapter }); } catch { } }
        if (adapters.Items.Count > 0) { adapters.SelectedIndex = 0; for (int i = 0; i < adapters.Items.Count; i++) if (((AdapterChoice)adapters.Items[i]).Adapter.Id == previous) adapters.SelectedIndex = i; }
        ShowDns();
    }
    private static bool HasGateway(NetworkInterface adapter)
    {
        try { return adapter.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork); }
        catch { return false; }
    }
    private void ShowDns()
    {
        try { var choice = adapters.SelectedItem as AdapterChoice; dnsCurrent.Text = choice == null ? "没有可配置的 IPv4 网卡" : "当前 DNS（含 IPv6）：" + String.Join(" / ", choice.Adapter.GetIPProperties().DnsAddresses.Select(a => a.ToString())); }
        catch { dnsCurrent.Text = "网卡状态发生变化，请刷新。"; }
    }
    private void RunDnsTests(object sender, EventArgs args)
    {
        Work("DNS 响应测速", true, token => Task.Run(delegate {
            string[] addresses = { "119.29.29.29", "223.5.5.5", "122.112.208.1", "210.43.112.1" };
            for (int i = 0; i < addresses.Length; i++) {
                int index = i; var values = new List<double>();
                try { for (int round = 0; round < 3; round++) { token.ThrowIfCancellationRequested(); values.Add(TransferEngine.DnsLatency(addresses[i], token)); }
                    values.Sort(); Ui(delegate { dnsGrid.Rows[index].Cells[2].Value = values[1].ToString("0.0"); dnsGrid.Rows[index].Cells[3].Value = "有效解析 · 三次中位数"; });
                } catch (Exception ex) { token.ThrowIfCancellationRequested(); string message = ex.Message; Ui(delegate { dnsGrid.Rows[index].Cells[2].Value = "—"; dnsGrid.Rows[index].Cells[3].Value = "超时 / 无有效应答"; dnsGrid.Rows[index].Cells[3].ToolTipText = message; }); }
            }
        }));
    }
    private void ConfigureDns(bool restore)
    {
        var choice = adapters.SelectedItem as AdapterChoice;
        if (choice == null || dnsGrid.CurrentRow == null) { status.Text = "请选择网卡和 DNS。"; return; }
        string source = new[] { "tencent", "aliyun", "huawei", "campus", "automatic" }[dnsGrid.CurrentRow.Index];
        int index = choice.Adapter.GetIPProperties().GetIPv4Properties().Index; string guid = choice.Adapter.Id;
        Work(restore ? "恢复原 DNS" : "应用 DNS", false, token => Task.Run(delegate {
            string folder = Path.Combine(AppPaths.Data, "operations"); Directory.CreateDirectory(folder);
            string resultPath = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json");
            try {
                var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(AppPaths.Root, "DnsConfig.ps1") + "\" -Action " + (restore ? "Restore" : "Apply") +
                    " -InterfaceIndex " + index + " -InterfaceGuid \"" + guid + "\" -Provider " + source + " -ResultPath \"" + resultPath + "\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var process = Process.Start(info)) { if (!process.WaitForExit(120000)) throw new IOException("DNS 配置仍在运行，请稍后刷新网卡状态；不要重复提交。"); }
                if (!File.Exists(resultPath)) throw new IOException("未收到 DNS 配置结果，可能取消了管理员授权。");
                var result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(resultPath, Encoding.UTF8));
                string message = Convert.ToString(result["message"]); Ui(delegate { dnsCurrent.Text = message; });
                if (!(bool)result["ok"]) throw new IOException(message);
            } finally { try { if (File.Exists(resultPath)) File.Delete(resultPath); } catch { } }
        }));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { if (cancellation != null) cancellation.Cancel(); details.Dispose(); } base.Dispose(disposing);
    }
}
