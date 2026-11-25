using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Forms;

internal sealed class SpeedSample
{
    public string AdapterName;
    public bool IsWireless;
    public bool Connected;
    public double Download;
    public double Upload;
}

internal sealed class NetworkSpeedMonitor
{
    private string lastId;
    private long lastReceived;
    private long lastSent;
    private long lastTime;
    private readonly Stopwatch clock = Stopwatch.StartNew();

    private static int Rank(NetworkInterface adapter)
    {
        if (adapter.OperationalStatus != OperationalStatus.Up) return -1;
        if (adapter.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
            adapter.NetworkInterfaceType != NetworkInterfaceType.GigabitEthernet &&
            adapter.NetworkInterfaceType != NetworkInterfaceType.FastEthernetFx &&
            adapter.NetworkInterfaceType != NetworkInterfaceType.FastEthernetT &&
            adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) return -1;
        try
        {
            IPInterfaceProperties properties = adapter.GetIPProperties();
            IPAddress[] addresses = properties.UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address).ToArray();
            if (addresses.Length == 0) return -1;
            int score = 20;
            if (properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) score += 60;
            foreach (IPAddress address in addresses)
            {
                byte[] b = address.GetAddressBytes();
                if (b[0] == 10) score += 30;
                if (b[0] == 10 && (b[1] == 14 || b[1] == 15)) score += 130;
            }
            string name = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
            if (name.Contains("vmware") || name.Contains("virtual") || name.Contains("radmin") || name.Contains("hyper-v") || name.Contains("vpn")) score -= 120;
            return score;
        }
        catch { return -1; }
    }

    public SpeedSample Read()
    {
        NetworkInterface chosen = NetworkInterface.GetAllNetworkInterfaces()
            .Select(a => new { Adapter = a, Score = Rank(a) })
            .Where(x => x.Score >= 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Adapter)
            .FirstOrDefault();

        if (chosen == null)
        {
            lastId = null;
            return new SpeedSample { Connected = false, AdapterName = "未检测到活动网络" };
        }

        IPv4InterfaceStatistics stats = chosen.GetIPv4Statistics();
        long now = clock.ElapsedTicks;
        var result = new SpeedSample
        {
            Connected = true,
            AdapterName = chosen.Name,
            IsWireless = chosen.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
        };
        if (lastId == chosen.Id && lastTime > 0)
        {
            double seconds = (now - lastTime) / (double)Stopwatch.Frequency;
            if (seconds > 0.1 && stats.BytesReceived >= lastReceived && stats.BytesSent >= lastSent)
            {
                result.Download = (stats.BytesReceived - lastReceived) / seconds;
                result.Upload = (stats.BytesSent - lastSent) / seconds;
            }
        }
        lastId = chosen.Id;
        lastReceived = stats.BytesReceived;
        lastSent = stats.BytesSent;
        lastTime = now;
        return result;
    }

    public static string Format(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024) return Math.Round(bytesPerSecond).ToString("0") + " B/s";
        if (bytesPerSecond < 1024 * 1024) return (bytesPerSecond / 1024).ToString("0.0") + " KB/s";
        if (bytesPerSecond < 1024d * 1024 * 1024) return (bytesPerSecond / (1024 * 1024)).ToString("0.00") + " MB/s";
        return (bytesPerSecond / (1024d * 1024 * 1024)).ToString("0.00") + " GB/s";
    }
}

internal sealed class SpeedWaveChart : Control
{
    private readonly Queue<double> download = new Queue<double>();
    private readonly Queue<double> upload = new Queue<double>();
    private const int Count = 60;

    public SpeedWaveChart()
    {
        BackColor = Color.White;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        for (int i = 0; i < Count; i++) { download.Enqueue(0); upload.Enqueue(0); }
    }

    public void Add(double down, double up)
    {
        download.Dequeue(); download.Enqueue(Math.Max(0, down));
        upload.Dequeue(); upload.Enqueue(Math.Max(0, up));
        Invalidate();
    }

    private static void DrawWave(Graphics graphics, Rectangle bounds, double[] values, double scale, Color color)
    {
        var points = new PointF[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            float x = bounds.Left + (bounds.Width - 1) * i / (float)(values.Length - 1);
            float y = bounds.Bottom - 2 - (float)(Math.Min(1, values[i] / scale) * (bounds.Height - 7));
            points[i] = new PointF(x, y);
        }
        using (var fill = new GraphicsPath())
        using (var brush = new SolidBrush(Color.FromArgb(34, color)))
        using (var pen = new Pen(color, 2.3f))
        {
            fill.AddLines(points);
            fill.AddLine(points[points.Length - 1], new PointF(bounds.Right - 1, bounds.Bottom));
            fill.AddLine(bounds.Right - 1, bounds.Bottom, bounds.Left, bounds.Bottom);
            fill.CloseFigure();
            graphics.FillPath(brush, fill);
            graphics.DrawCurve(pen, points, 0.18f);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle bounds = new Rectangle(5, 7, Math.Max(10, Width - 10), Math.Max(10, Height - 14));
        using (var gridPen = new Pen(Color.FromArgb(234, 239, 245), 1))
            for (int i = 0; i <= 3; i++)
            {
                int y = bounds.Top + i * bounds.Height / 3;
                graphics.DrawLine(gridPen, bounds.Left, y, bounds.Right, y);
            }
        double[] down = download.ToArray();
        double[] up = upload.ToArray();
        double scale = Math.Max(1024, Math.Max(down.Max(), up.Max()) * 1.15);
        DrawWave(graphics, bounds, down, scale, Color.FromArgb(42, 111, 202));
        DrawWave(graphics, bounds, up, scale, Color.FromArgb(17, 182, 188));
    }
}
