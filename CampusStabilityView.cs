using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

internal sealed class CampusStabilityView : UserControl
{
    internal readonly CampusStabilityMonitor Monitor;
    internal Action<string> ExportReport;
    private readonly Func<bool> workBusy;
    private readonly FlatAction start, mark, export, processes;
    private readonly CheckBox quiet;
    private readonly Label gateway, portal, status, targets, insight;
    private readonly TextBox events;
    private readonly CampusLatencyChart chart;
    private readonly Timer refresh = new Timer { Interval = 500 };
    private IDisposable guard;
    private int revision = -1;
    internal bool Running { get { return Monitor.Running; } }
    internal CampusStabilityView(Func<bool> workBusy) : this(new CampusStabilityMonitor(), workBusy) { }
    internal CampusStabilityView(CampusStabilityMonitor monitor, Func<bool> workBusy)
    {
        Monitor = monitor; this.workBusy = workBusy; BackColor = Color.White; Padding = new Padding(20, 16, 20, 16);
        Name = "CampusStability";
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10, Margin = Padding.Empty, Padding = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (float height in new[] { 34f, 48f, 30f, 68f, 104f, 58f, 22f, 72f, 46f, 42f }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var heading = TextLabel("游戏期间网络记录", 14, true); heading.Name = "StabilityHeading"; layout.Controls.Add(heading, 0, 0);
        targets = TextLabel("每 2 秒检测一次宿舍网关和学校服务器，同时记录 Wi-Fi 信号\n只检测校内，不代表游戏服务器的线路", 9.5f, false); layout.Controls.Add(targets, 0, 1);
        quiet = new UiCheck { Name = "StabilityQuiet", Text = "记录期间暂停本软件的测速和下载", Checked = true, Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 9), ForeColor = SurfaceColor.Ink, AutoSize = false };
        layout.Controls.Add(quiet, 0, 2);
        var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        gateway = TextLabel("宿舍网络 · 尚未记录", 9.5f, false); gateway.Name = "StabilityGateway";
        portal = TextLabel("学校服务器 · 尚未记录", 9.5f, false); portal.Name = "StabilityPortal";
        gateway.ForeColor = SurfaceColor.Blue; portal.ForeColor = SurfaceColor.Green;
        metrics.Controls.Add(gateway, 0, 0); metrics.Controls.Add(portal, 1, 0); layout.Controls.Add(metrics, 0, 3);
        chart = new CampusLatencyChart { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4), Font = new Font("Microsoft YaHei UI", 8.5f) }; layout.Controls.Add(chart, 0, 4);
        insight = TextLabel("判断 · 还没有开始记录\n点“开始记录”后打游戏，卡的时候点“刚才卡了”。", 9, false);
        insight.Name = "StabilityInsight"; insight.Padding = new Padding(12, 7, 10, 5); insight.BackColor = SurfaceColor.Pale; insight.ForeColor = SurfaceColor.Muted;
        insight.Paint += delegate(object sender, PaintEventArgs e) {
            // Left accent in the verdict colour, so the judgement reads at a glance.
            using (var accent = new SolidBrush(insight.ForeColor)) e.Graphics.FillRectangle(accent, 0, 0, 3, insight.Height);
        };
        layout.Controls.Add(insight, 0, 5);
        layout.Controls.Add(TextLabel("发生了什么 · 蓝线是宿舍网络，绿线是学校服务器", 9, true), 0, 6);
        events = new TextBox { Name = "StabilityEvents", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None, BackColor = SurfaceColor.Pale, ForeColor = SurfaceColor.Ink, Font = new Font("Microsoft YaHei UI", 9), Text = "还没有记录。打游戏卡了，就点“刚才卡了”。" };
        var eventsFrame = new SurfacePanel { Name = "StabilityEventsFrame", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4), Padding = new Padding(12, 8, 4, 6),
            BackColor = Color.White, Fill = SurfaceColor.Pale, Edge = SurfaceColor.Border };
        eventsFrame.Controls.Add(events);
        layout.Controls.Add(eventsFrame, 0, 7);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty, Padding = new Padding(0, 5, 0, 0) };
        start = ActionButton("开始记录", "StabilityStart", 98); mark = ActionButton("刚才卡了", "StabilityMark", 98); export = ActionButton("导出报告", "StabilityExport", 92);
        processes = ActionButton("查看占网程序", "StabilityProcesses", 122);
        start.BackColor = SurfaceColor.Blue; start.ForeColor = Color.White;
        mark.Enabled = export.Enabled = false; actions.Controls.AddRange(new Control[] { start, mark, export, processes }); layout.Controls.Add(actions, 0, 8);
        status = TextLabel("开始后一直记录，最小化或切换页面也不停；关闭软件时停止。", 9, false); status.Name = "StabilityStatus";
        layout.Controls.Add(status, 0, 9); Controls.Add(layout);
        start.Click += delegate { if (Monitor.Running) Stop(); else Start(); };
        mark.Click += delegate { if (Monitor.Running) { Monitor.Log.Mark(DateTimeOffset.UtcNow); RefreshSnapshot(); } };
        export.Click += delegate {
            try { ExportReport(Monitor.Log.Report()); }
            catch (Exception ex) { status.Text = "导出未完成：" + ex.Message; }
        };
        processes.Click += delegate {
            try { Process.Start(new ProcessStartInfo("resmon.exe") { UseShellExecute = true }); status.Text = "已打开资源监视器；在“网络”页查看正在收发数据的程序。"; }
            catch (Exception ex) { status.Text = "无法打开资源监视器：" + ex.Message; }
        };
        ExportReport = SaveReport;
        quiet.CheckedChanged += delegate {
            if (!Monitor.Running) return;
            if (quiet.Checked && workBusy()) { quiet.Checked = false; status.Text = "高级工具里还有测速或下载在进行，先停止它再勾选。"; return; }
            SetGuard();
        };
        refresh.Tick += delegate { RefreshSnapshot(); };
    }
    private static Label TextLabel(string text, float size, bool bold)
    {
        return new Label { Text = text, Dock = DockStyle.Fill, Margin = Padding.Empty, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = bold ? SurfaceColor.Ink : SurfaceColor.Muted };
    }
    private static FlatAction ActionButton(string text, string name, int width)
    {
        return new FlatAction { Text = text, Name = name, Width = width, Height = 36, Margin = new Padding(0, 0, 8, 0), Refined = true, Radius = 8,
            Font = new Font("Microsoft YaHei UI", 9.5f), BackColor = Color.White, ForeColor = SurfaceColor.Blue };
    }
    internal void Start()
    {
        if (Monitor.Running) return;
        if (quiet.Checked && workBusy()) { status.Text = "高级工具里还有测速或下载在进行，先停止它再开始记录。"; return; }
        try { SetGuard(); Monitor.Start(); }
        catch { if (guard != null) { guard.Dispose(); guard = null; } status.Text = "记录没能开始，请稍后再试。"; return; }
        refresh.Start(); start.Text = "停止记录"; mark.Enabled = true;
        status.Text = "正在积累数据，大约 10 秒后开始判断。记录不会触发登录或重连。";
        if (RunningChanged != null) RunningChanged();
    }
    internal void Stop()
    {
        Monitor.Stop(); refresh.Stop(); if (guard != null) { guard.Dispose(); guard = null; }
        start.Text = "继续记录"; mark.Enabled = false; RefreshSnapshot(); status.Text = "已停止。本次记录保留到关闭软件前，可以导出报告。";
        if (RunningChanged != null) RunningChanged();
    }
    internal Action RunningChanged;
    private void SetGuard()
    {
        if (guard != null) { guard.Dispose(); guard = null; }
        if (quiet.Checked) guard = GameTrafficGuard.Enter();
    }
    internal void RefreshSnapshot()
    {
        Form form = FindForm();
        if (!Enabled || !Visible || form != null && form.WindowState == FormWindowState.Minimized) return;
        var snapshot = Monitor.Log.Snapshot(); if (snapshot.Revision == revision) return; revision = snapshot.Revision;
        gateway.Text = "宿舍网络 · " + snapshot.Gateway.Format(); portal.Text = "学校服务器 · " + snapshot.Portal.Format();
        CampusStabilityInsight finding = CampusStabilityAnalyzer.Analyze(snapshot);
        insight.Text = "判断 · " + finding.Title + "\n" + finding.Detail;
        insight.ForeColor = finding.State == "stable" ? SurfaceColor.Green : new[] { "gateway", "campus", "loaded", "wifi", "scan" }.Contains(finding.State) ? Color.FromArgb(154, 95, 18) : SurfaceColor.Muted;
        chart.Samples = snapshot.Samples; chart.Invalidate();
        export.Enabled = snapshot.Samples.Length > 0 || snapshot.Events.Length > 0;
        if (snapshot.Events.Length > 0) events.Text = String.Join(Environment.NewLine, snapshot.Events.Reverse().Take(12).Select(e => e.Time.ToLocalTime().ToString("HH:mm:ss") + "  " + e.Message));
        var last = snapshot.Samples.LastOrDefault();
        if (last != null) targets.Text = "宿舍网关 " + (last.Gateway ?? "未取得") + " · 学校服务器 " + CampusTargets.Portal + "\n最近一次 " + last.Time.ToLocalTime().ToString("HH:mm:ss") +
            (last.Wifi == null ? "" : " · Wi-Fi " + CampusStabilityLog.Signal(last.Wifi) + "（" + last.Wifi.Grade + "）") + (last.Http == null ? "" : " · " + last.Http);
    }
    private void SaveReport(string text)
    {
        using (var dialog = new SaveFileDialog { Title = "导出网络记录报告", Filter = "文本报告 (*.txt)|*.txt", FileName = "稳连网络记录-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" })
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK) { File.WriteAllText(dialog.FileName, text, new UTF8Encoding(true)); status.Text = "报告已保存至你选择的位置。"; }
    }
    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (refresh != null && Monitor != null) { if (Enabled && Monitor.Running) { refresh.Start(); RefreshSnapshot(); } else refresh.Stop(); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { refresh.Dispose(); Monitor.Dispose(); if (guard != null) { guard.Dispose(); guard = null; } }
        base.Dispose(disposing);
    }
}

internal sealed class CampusLatencyChart : Control
{
    internal CampusStabilitySample[] Samples = new CampusStabilitySample[0];
    internal CampusLatencyChart() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = Color.White; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var samples = Samples;
        var box = new Rectangle(35, 7, Math.Max(1, Width - 45), Math.Max(1, Height - 28));
        double max = Math.Max(20, samples.SelectMany(s => new[] { s.GatewayPing.Milliseconds, s.PortalPing.Milliseconds }).Where(v => v.HasValue).Select(v => (double)v.Value).DefaultIfEmpty(0).Max() * 1.15);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var guide = new Pen(Color.FromArgb(236, 239, 244))) e.Graphics.DrawLine(guide, box.Left, box.Top, box.Right, box.Top);
        using (var pen = new Pen(SurfaceColor.Border)) e.Graphics.DrawLine(pen, box.Left, box.Bottom, box.Right, box.Bottom);
        TextRenderer.DrawText(e.Graphics, max.ToString("0", CultureInfo.InvariantCulture), Font, new Point(0, box.Top), SurfaceColor.Faint);
        TextRenderer.DrawText(e.Graphics, "0 ms", Font, new Point(0, box.Bottom - 12), SurfaceColor.Faint);
        if (samples.Length == 0) { TextRenderer.DrawText(e.Graphics, "开始记录后这里显示延迟曲线；空白处表示没有测到", Font, box, SurfaceColor.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
        for (int direction = 0; direction < 2; direction++) using (var pen = new Pen(direction == 0 ? SurfaceColor.Blue : SurfaceColor.Green, 1.7f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
        {
            PointF? previous = null; int segment = -1;
            for (int i = 0; i < samples.Length; i++)
            {
                long? value = direction == 0 ? samples[i].GatewayPing.Milliseconds : samples[i].PortalPing.Milliseconds;
                if (!value.HasValue) { previous = null; continue; }
                double duration = (samples.Last().Time - samples.First().Time).TotalSeconds;
                double x = duration > 0 ? (samples[i].Time - samples.First().Time).TotalSeconds / duration : i / (double)Math.Max(1, samples.Length - 1);
                var point = new PointF(box.Left + (float)Math.Max(0, Math.Min(1, x)) * box.Width, box.Bottom - (float)(value.Value / max * box.Height));
                if (previous.HasValue && segment == samples[i].Segment && (samples[i].Time - samples[i - 1].Time).TotalSeconds > 0 && (samples[i].Time - samples[i - 1].Time).TotalSeconds <= 6) e.Graphics.DrawLine(pen, previous.Value, point);
                else e.Graphics.DrawEllipse(pen, point.X - 1, point.Y - 1, 2, 2);
                previous = point; segment = samples[i].Segment;
            }
        }
        TextRenderer.DrawText(e.Graphics, samples.First().Time.ToLocalTime().ToString("HH:mm:ss"), Font, new Point(box.Left, box.Bottom + 2), SurfaceColor.Muted);
        TextRenderer.DrawText(e.Graphics, "现在", Font, new Point(box.Right - 32, box.Bottom + 2), SurfaceColor.Muted);
    }
}
