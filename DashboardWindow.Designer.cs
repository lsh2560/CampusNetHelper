using System.Drawing;
using System.Windows.Forms;
using System.Linq;

internal sealed partial class DashboardWindow
{
    private TableLayoutPanel heroMetrics;
    private SurfacePanel heroMetricFrame;
    private Font metricWideFont, metricCompactFont;

    private void ResizeSidebarContent(Label motto, Label footer)
    {
        motto.Top = sidebar.Height - 74;
        footer.Top = sidebar.Height - 46;
        navigation.SetBounds(12, 112, sidebar.Width - 24, System.Math.Max(220, sidebar.Height - 200));
        int gap = sidebar.Height < 700 ? 8 : sidebar.Height < 780 ? 12 : 18;
        if (navigation.Gap != gap) navigation.Gap = gap;
    }

    private static void FitCardContents(CardPanel card)
    {
        foreach (Control child in card.Controls)
        {
            var label = child as Label;
            if (label != null && !label.AutoSize && label.Right > card.ClientSize.Width - 24)
                label.Width = System.Math.Max(1, card.ClientSize.Width - label.Left - 24);
            if (child is Panel && child.Height == 1)
                child.Width = System.Math.Max(1, card.ClientSize.Width - child.Left - 24);
            var output = child as TextBox;
            if (output != null && output.ReadOnly && output.Multiline)
                output.Width = System.Math.Max(1, card.ClientSize.Width - output.Left - 26);
        }
    }

    // Home metrics: latency, download speed and Wi-Fi signal, side by side.
    private void InitializeComponent()
    {
        metricWideFont = UiFont.Numeric(19.5f);
        metricCompactFont = UiFont.Numeric(12.5f);
        home.Disposed += delegate { metricWideFont.Dispose(); metricCompactFont.Dispose(); };
        heroMetrics = new TableLayoutPanel();
        heroMetrics.SuspendLayout();

        heroMetrics.Name = "HeroMetrics";
        heroMetrics.BackColor = Color.White;
        heroMetrics.Margin = Padding.Empty;
        heroMetrics.ColumnCount = 3;
        heroMetrics.RowCount = 1;
        heroMetrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        heroMetrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        heroMetrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        heroMetrics.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        pingText = new MetricValueLabel { Name = "LatencyValue", Text = "— ms", AutoSize = false, UseCompatibleTextRendering = false, TextAlign = ContentAlignment.MiddleLeft };
        heroDown = new MetricValueLabel { Name = "HeroDownloadValue", Text = "— B/s", AutoSize = false, UseCompatibleTextRendering = false, TextAlign = ContentAlignment.MiddleLeft };
        wifiValue = new MetricValueLabel { Name = "WifiSignalValue", Text = "—", AutoSize = false, UseCompatibleTextRendering = false, TextAlign = ContentAlignment.MiddleLeft };
        var values = new[] { pingText, heroDown, wifiValue };
        var captions = new[] { "延迟", "下载速度", "Wi-Fi 信号" };
        var symbols = new[] { UiSymbol.Clock, UiSymbol.Down, UiSymbol.Wifi };
        var tints = new[] { SurfaceColor.Blue, SurfaceColor.Blue, SurfaceColor.Green };
        for (int i = 0; i < 3; i++)
        {
            var column = new BufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Color.White };
            var icon = new UiIcon { Symbol = symbols[i], Tint = tints[i], Bounds = new Rectangle(14, 16, 32, 32) };
            var caption = new Label { Text = captions[i], Font = new Font("Microsoft YaHei UI", 9.5f), ForeColor = SurfaceColor.Muted, BackColor = Color.White, Bounds = new Rectangle(56, 12, 160, 23), AutoEllipsis = true };
            if (i == 2) { wifiCaption = caption; caption.Name = "WifiSignalCaption"; }
            values[i].Font = metricWideFont; values[i].ForeColor = SurfaceColor.Ink;
            values[i].Margin = Padding.Empty;
            values[i].SetBounds(56, 36, 150, 38);
            column.Controls.Add(icon); column.Controls.Add(caption); column.Controls.Add(values[i]);
            Label value = values[i];
            column.Resize += delegate {
                bool compact = column.Width < 190;
                icon.SetBounds(compact ? 10 : 14, compact ? 12 : 16, compact ? 26 : 32, compact ? 26 : 32);
                int left = compact ? 44 : 56;
                caption.SetBounds(left, 12, System.Math.Max(1, column.Width - left - 6), 23);
                value.SetBounds(compact ? 12 : left, compact ? 42 : 36, System.Math.Max(1, column.Width - (compact ? 12 : left) - 4), System.Math.Max(1, column.Height - (compact ? 44 : 38)));
            };
            if (i > 0) column.Controls.Add(new Panel { Bounds = new Rectangle(0, 20, 1, 46), BackColor = SurfaceColor.Border });
            heroMetrics.Controls.Add(column, i, 0);
        }
        heroMetricFrame = new SurfacePanel { Name = "HeroMetricFrame", Fill = Color.White, BackColor = Color.White, Edge = SurfaceColor.Border, Radius = 10, Padding = new Padding(6) };
        heroMetricFrame.Controls.Add(heroMetrics); heroMetrics.Dock = DockStyle.Fill; heroMetrics.Padding = Padding.Empty;
        hero.Controls.Add(heroMetricFrame);
        heroMetrics.ResumeLayout(false);
        heroMetrics.PerformLayout();
    }
}
