using System.Drawing;
using System.Windows.Forms;

internal sealed partial class NetworkSpeedTestView
{
    private Label serverLabel, noteLabel, statusLabel, stateBadge, downloadValue, uploadValue, downloadDetail, uploadDetail;
    private FlatAction startButton, stopButton, copyButton;
    private AntdUI.Progress progressBar;
    private Timer refreshTimer;
    private SampleSpark downloadSpark, uploadSpark;
    private void InitializeComponent()
    {
        SuspendLayout();
        Name = "NetworkSpeedTest"; BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 10.5f); ForeColor = SurfaceColor.Ink;
        Padding = new Padding(12, 7, 12, 4);
        var root = new TableLayoutPanel { ColumnCount = 1, RowCount = 7, Dock = DockStyle.Fill, Margin = Padding.Empty };
        foreach (int height in new[] { 34, 24, 110, 38, 42, 10 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new BufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Color.White };
        heading.Controls.Add(new UiIcon { Symbol = UiSymbol.Speed, Bounds = new Rectangle(0, 0, 34, 34) });
        heading.Controls.Add(new Label { Text = "网络测速", Font = new Font("Microsoft YaHei UI", 14, FontStyle.Bold), ForeColor = SurfaceColor.Ink, Bounds = new Rectangle(55, 3, 180, 30) });
        stateBadge = new StatePill { Name = "BandwidthState", Text = "就绪", Size = new Size(98, 28), ForeColor = SurfaceColor.Green, BackColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold) };
        heading.Controls.Add(stateBadge); heading.Resize += delegate { stateBadge.SetBounds(heading.Width - 98, 4, 98, 28); };
        serverLabel = new Label { Dock = DockStyle.Fill, Padding = new Padding(56, 0, 0, 0), ForeColor = SurfaceColor.Muted, Font = new Font("Microsoft YaHei UI", 9.5f),
            Margin = Padding.Empty, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        var metrics = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, Margin = Padding.Empty };
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        downloadValue = new Label { Name = "BandwidthDownloadValue", Text = "— Mbps", Font = new Font("Microsoft YaHei UI", 19, FontStyle.Bold), ForeColor = SurfaceColor.Ink, Bounds = new Rectangle(64, 32, 280, 36), AutoEllipsis = true, BackColor = SurfaceColor.Pale };
        uploadValue = new Label { Name = "BandwidthUploadValue", Text = "— Mbps", Font = new Font("Microsoft YaHei UI", 19, FontStyle.Bold), ForeColor = SurfaceColor.Ink, Bounds = new Rectangle(64, 32, 280, 36), AutoEllipsis = true, BackColor = SurfaceColor.Pale };
        downloadDetail = new Label { Name = "BandwidthDownloadDetail", Text = "— MB/s", ForeColor = SurfaceColor.Muted, Font = new Font("Microsoft YaHei UI", 9.5f), Bounds = new Rectangle(20, 78, 280, 33), BackColor = SurfaceColor.Pale };
        uploadDetail = new Label { Name = "BandwidthUploadDetail", Text = "— MB/s", ForeColor = SurfaceColor.Muted, Font = new Font("Microsoft YaHei UI", 9.5f), Bounds = new Rectangle(20, 78, 280, 33), BackColor = SurfaceColor.Pale };
        downloadSpark = new SampleSpark { Name = "BandwidthDownloadChart", Line = SurfaceColor.Blue };
        uploadSpark = new SampleSpark { Name = "BandwidthUploadChart", Line = SurfaceColor.Green };
        for (int i = 0; i < 2; i++)
        {
            var box = new SurfacePanel { Dock = DockStyle.Fill, Margin = i == 0 ? new Padding(0, 0, 8, 0) : new Padding(8, 0, 0, 0), BackColor = Color.White, Fill = SurfaceColor.Pale, Radius = 9 };
            box.Controls.Add(new UiIcon { Symbol = i == 0 ? UiSymbol.Down : UiSymbol.Up, Tint = i == 0 ? SurfaceColor.Blue : SurfaceColor.Green, Bounds = new Rectangle(16, 16, 36, 36) });
            box.Controls.Add(new Label { Text = i == 0 ? "下载速度" : "上传速度", ForeColor = SurfaceColor.Ink, Font = new Font("Microsoft YaHei UI", 10), Bounds = new Rectangle(64, 8, 130, 24), BackColor = SurfaceColor.Pale });
            Label value = i == 0 ? downloadValue : uploadValue, detail = i == 0 ? downloadDetail : uploadDetail;
            SampleSpark spark = i == 0 ? downloadSpark : uploadSpark;
            detail.Top = 76; detail.Height = 29;
            box.Controls.Add(spark); box.Controls.Add(value); box.Controls.Add(detail);
            box.Resize += delegate {
                bool plot = box.Width >= 360; spark.Visible = plot;
                spark.SetBounds(box.Width - 126, 38, 108, 36);
                value.Width = System.Math.Max(1, box.Width - 78 - (plot ? 116 : 0));
                detail.Width = System.Math.Max(1, box.Width - 36);
            };
            metrics.Controls.Add(box, i, 0);
        }
        noteLabel = new Label { ForeColor = SurfaceColor.Muted, BackColor = SurfaceColor.Pale, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3),
            Font = new Font("Microsoft YaHei UI", 9.5f), Padding = new Padding(8, 2, 6, 0), AutoEllipsis = true };
        var actions = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, Dock = DockStyle.Fill, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30)); actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        startButton = new FlatAction { Name = "BandwidthStart", AccessibleName = "开始网络测速", Text = "开始测速", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0), BackColor = SurfaceColor.Blue, ForeColor = Color.White, Refined = true, Symbol = UiSymbol.Play, Radius = 8 };
        stopButton = new FlatAction { Name = "BandwidthStop", AccessibleName = "停止网络测速", Text = "停止", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0), Enabled = false, Refined = true, Symbol = UiSymbol.Stop, Radius = 8 };
        copyButton = new FlatAction { Name = "BandwidthCopy", AccessibleName = "复制测速结果", Text = "复制结果", Dock = DockStyle.Fill, Margin = Padding.Empty, Enabled = false, Refined = true, Symbol = UiSymbol.Copy, Radius = 8 };
        actions.Controls.Add(startButton, 0, 0); actions.Controls.Add(stopButton, 1, 0); actions.Controls.Add(copyButton, 2, 0);
        progressBar = new AntdUI.Progress { Name = "BandwidthProgress", Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 2), Radius = 4, Fill = SurfaceColor.Blue, Back = SurfaceColor.Pale, UseSystemText = false, Text = "", Value = 0 };
        progressBar.ValueFormatChanged += delegate(object sender, AntdUI.FloatEventArgs e) { return null; };
        statusLabel = new Label { Name = "BandwidthStatus", Text = "点击开始后，依次测试下载和上传速度", Dock = DockStyle.Fill, Margin = Padding.Empty, ForeColor = SurfaceColor.Muted, Font = new Font("Microsoft YaHei UI", 9.5f), AutoEllipsis = true };
        root.Controls.Add(heading, 0, 0); root.Controls.Add(serverLabel, 0, 1); root.Controls.Add(metrics, 0, 2); root.Controls.Add(noteLabel, 0, 3);
        root.Controls.Add(actions, 0, 4); root.Controls.Add(progressBar, 0, 5); root.Controls.Add(statusLabel, 0, 6);
        Controls.Add(root); refreshTimer = new Timer { Interval = 150 };
        ResumeLayout(false);
    }
}
