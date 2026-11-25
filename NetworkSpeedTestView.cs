using System;
using System.Globalization;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal sealed partial class NetworkSpeedTestView : UserControl
{
    private readonly BandwidthTestOptions options;
    private CancellationTokenSource cancellation;
    private BandwidthProgress latest;
    private readonly object progressGate = new object();
    private bool running, stopping;
    private BandwidthMeasurement downloadResult, uploadResult;
    private string report;
    internal Action<string> CopyResult = Clipboard.SetText;
    private static readonly Color ErrorColor = Color.FromArgb(172, 47, 47);
    private static readonly Color AttentionColor = Color.FromArgb(145, 99, 22);
    internal bool Running { get { return running; } }
    private bool campusOnly;
    internal bool CampusOnly
    {
        get { return campusOnly; }
        set {
            campusOnly = value;
            if (value && !CampusTargets.PrivateAddress(options.Server.Host)) {
                startButton.Enabled = false; serverLabel.Text = "校内带宽测速服务未配置";
                noteLabel.Text = "不使用公网测速服务器；带宽测速需学校提供校内测速服务。";
                statusLabel.Text = "暂不可用 · 校内测速服务未配置"; SetState("未配置", UiColor.Muted);
            }
        }
    }

    internal NetworkSpeedTestView() : this(new BandwidthTestOptions()) { }
    internal NetworkSpeedTestView(BandwidthTestOptions testOptions)
    {
        options = testOptions;
        InitializeComponent();
        serverLabel.Text = "服务器 " + options.Server.Host + " · " + options.Connections + " 连接 · 上传使用随机测试数据";
        noteLabel.Text = "每方向最多 " + (options.DurationMilliseconds / 1000.0).ToString("0.#") + " 秒 / " +
            (options.MaxBytesPerDirection / 1048576.0).ToString("0.#") + " MiB。结果为到此服务器的平均速率，可能受线路或代理影响。";
        startButton.Click += delegate { StartTest(); };
        stopButton.Click += delegate {
            if (!running || stopping) return;
            stopping = true; refreshTimer.Stop(); SetState("停止中", UiColor.Muted);
            if (cancellation != null) { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
            stopButton.Enabled = false; statusLabel.Text = "正在停止测速…";
        };
        refreshTimer.Tick += delegate { RefreshProgress(); };
        copyButton.Click += delegate {
            if (running || String.IsNullOrEmpty(report)) return;
            try { CopyResult(report); statusLabel.Text = "结果已复制 · " + DateTime.Now.ToString("HH:mm:ss"); }
            catch { statusLabel.Text = "暂时无法复制，请重试。"; }
        };
    }
    private void SetState(string text, Color color)
    {
        stateBadge.Text = text; stateBadge.ForeColor = color;
        stateBadge.BackColor = Color.White;
        progressBar.Fill = color;
    }
    private void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) action(); }); }
        catch (InvalidOperationException) { }
    }
    private void StartTest()
    {
        if (running) return;
        if (campusOnly && !CampusTargets.PrivateAddress(options.Server.Host)) { statusLabel.Text = "未配置校内专用测速服务，不会连接公网测速目标。"; return; }
        if (GameTrafficGuard.Active) { statusLabel.Text = "游戏免干扰已开启，请先停止校内监测或关闭其免干扰选项，再手动测速。"; return; }
        running = true; stopping = false; startButton.Enabled = false; stopButton.Enabled = true; copyButton.Enabled = false; report = null;
        startButton.Text = "正在测速"; SetState("连接中", UiColor.Blue);
        downloadResult = uploadResult = null;
        downloadSpark.Reset(); uploadSpark.Reset();
        downloadValue.Text = "正在测试…"; uploadValue.Text = "等待测试";
        downloadValue.ForeColor = uploadValue.ForeColor = UiColor.Muted;
        downloadDetail.ForeColor = uploadDetail.ForeColor = UiColor.Muted;
        downloadDetail.Text = uploadDetail.Text = "— MB/s";
        progressBar.Value = 0; statusLabel.Text = "正在连接下载测速服务…";
        lock (progressGate) latest = null;
        cancellation = new CancellationTokenSource();
        CancellationTokenSource source = cancellation;
        refreshTimer.Start();
        ThreadPool.QueueUserWorkItem(delegate
        {
            string summary;
            bool stopped = false;
            try
            {
                BandwidthTestResult result = BandwidthTestEngine.Run(options, source.Token,
                    delegate(BandwidthProgress p) { lock (progressGate) latest = p; },
                    delegate(BandwidthMeasurement m) { Ui(delegate { ApplyResult(m); }); });
                summary = result.Download.Success && result.Upload.Success ? "测速完成 · 下载与上传结果已更新" :
                    "测速结束 · 部分方向未测得有效结果，请查看原因";
            }
            catch (OperationCanceledException) { stopped = true; summary = "测速已停止 · 已完成的结果保留"; }
            catch (Exception ex) { summary = "测速失败：" + ex.Message; }
            finally { source.Dispose(); }
            Ui(delegate {
                refreshTimer.Stop(); running = false; cancellation = null;
                startButton.Enabled = true; startButton.Text = "重新测速"; stopButton.Enabled = false;
                if (downloadResult == null) { downloadValue.Text = "未完成"; downloadValue.ForeColor = UiColor.Muted; downloadDetail.Text = "未取得完整结果"; }
                if (uploadResult == null) { uploadValue.Text = "未完成"; uploadValue.ForeColor = UiColor.Muted; uploadDetail.Text = "未取得完整结果"; }
                bool downOk = downloadResult != null && downloadResult.Success, upOk = uploadResult != null && uploadResult.Success;
                if (downloadResult != null && uploadResult != null) progressBar.Value = 1;
                SetState(stopped ? "已停止" : downOk && upOk ? "已完成" : downOk || upOk ? "部分完成" : "失败",
                    stopped ? UiColor.Muted : downOk && upOk ? UiColor.Green : downOk || upOk ? AttentionColor : ErrorColor);
                DateTime finished = DateTime.Now;
                statusLabel.Text = summary + " · " + finished.ToString("HH:mm:ss");
                report = BuildReport(finished, summary); copyButton.Enabled = true;
            });
        });
    }
    private void RefreshProgress()
    {
        BandwidthProgress p;
        lock (progressGate) p = latest;
        if (p == null || !running || stopping) return;
        if (p.Bytes > 0 && p.Seconds > 0) (p.Upload ? uploadSpark : downloadSpark).Add(p.Bytes * 8 / 1000000.0 / p.Seconds);
        progressBar.Value = (float)Math.Max(0, Math.Min(.99, (p.Upload ? .5 : 0) + p.Fraction * .5));
        SetState(p.Upload ? "上传中" : "下载中", UiColor.Blue);
        var value = p.Upload ? uploadValue : downloadValue;
        var detail = p.Upload ? uploadDetail : downloadDetail;
        if ((p.Upload ? uploadResult : downloadResult) == null)
        {
            value.Text = p.Bytes == 0 ? "正在测试…" : (p.Bytes * 8 / 1000000.0 / Math.Max(.001, p.Seconds)).ToString("0.00", CultureInfo.InvariantCulture) + " Mbps";
            value.ForeColor = p.Bytes == 0 ? UiColor.Muted : p.Upload ? UiColor.Green : UiColor.Blue;
            detail.Text = "已确认 " + (p.Bytes / 1048576.0).ToString("0.00") + " MiB · " + p.Seconds.ToString("0.0") + " 秒";
        }
        statusLabel.Text = p.Upload ? "正在测试上传速度…" : "正在测试下载速度…";
    }
    private void ApplyResult(BandwidthMeasurement m)
    {
        if (m.Upload) uploadResult = m; else downloadResult = m;
        var value = m.Upload ? uploadValue : downloadValue;
        var detail = m.Upload ? uploadDetail : downloadDetail;
        value.Text = m.Success ? m.Mbps.ToString("0.00", CultureInfo.InvariantCulture) + " Mbps" : "未测得";
        value.ForeColor = m.Success ? m.Upload ? UiColor.Green : UiColor.Blue : ErrorColor;
        detail.ForeColor = m.Success ? UiColor.Muted : ErrorColor;
        detail.Text = m.Success ? m.MBps.ToString("0.00", CultureInfo.InvariantCulture) + " MB/s · " +
            (m.Bytes / 1048576.0).ToString("0.00") + " MiB / " + m.Seconds.ToString("0.0") + " 秒" : m.Error;
    }
    private string BuildReport(DateTime finished, string summary)
    {
        var text = new StringBuilder();
        text.AppendLine("校园网连接助手 · 网络测速");
        text.AppendLine("时间：" + finished.ToString("yyyy-MM-dd HH:mm:ss"));
        text.AppendLine("服务器：" + options.Server.Host + "；连接数：" + options.Connections);
        var measurements = new[] { downloadResult, uploadResult };
        for (int i = 0; i < measurements.Length; i++)
        {
            var m = measurements[i];
            text.AppendLine((i == 1 ? "上传：" : "下载：") + (m == null ? "未完成" : m.Success ? m.Mbps.ToString("0.00", CultureInfo.InvariantCulture) + " Mbps / " +
                m.MBps.ToString("0.00", CultureInfo.InvariantCulture) + " MB/s；有效数据 " + m.Bytes + " B；耗时 " + m.Seconds.ToString("0.000", CultureInfo.InvariantCulture) + " s" : "未测得，" + m.Error));
        }
        text.AppendLine(summary);
        text.AppendLine("结果为到此服务器的当前平均速率，受线路、服务器和代理影响。");
        return text.ToString();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (cancellation != null) { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
            refreshTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
