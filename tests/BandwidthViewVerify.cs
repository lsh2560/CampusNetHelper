using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class BandwidthViewVerify
{
    private static readonly List<object> results = new List<object>();
    private static readonly List<string> screenshots = new List<string>();
    private static IEnumerable<Control> All(Control root) { foreach (Control c in root.Controls) { yield return c; foreach (Control child in All(c)) yield return child; } }
    private static T Find<T>(Control root, string name) where T : Control { return (T)All(root).Single(c => c.Name == name); }
    private static void Click(Control c) { typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(c, new object[] { EventArgs.Empty }); }
    private static void Check(bool ok, string name) { results.Add(new { name = name, status = ok ? "PASS" : "FAIL" }); if (!ok) throw new Exception(name); }
    private static void Wait(Func<bool> done, int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        do { Application.DoEvents(); Thread.Sleep(10); if (done()) return; } while (clock.ElapsedMilliseconds < milliseconds);
        throw new Exception("Window callback did not complete before timeout");
    }
    private static void Capture(Control c, string path)
    {
        using (var bmp = new Bitmap(c.Width, c.Height)) { c.DrawToBitmap(bmp, new Rectangle(Point.Empty, c.Size)); bmp.Save(path); }
        screenshots.Add(path);
    }
    [STAThread] private static void Main(string[] args)
    {
        string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        int failed = 0;
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            foreach (string mode in new[] { "normal", "reject-upload", "html", "slow" })
            {
                using (var form = new Form { ClientSize = new Size(700, 344), StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) })
                using (var view = new NetworkSpeedTestView(new BandwidthTestOptions { Server = new Uri(args[0] + mode + "/"), Connections = 4,
                    DurationMilliseconds = mode == "slow" ? 10000 : 2500, MaxBytesPerDirection = mode == "slow" ? 32 * 1024 * 1024 : 512 * 1024 }))
                {
                    form.Controls.Add(view); view.Dock = DockStyle.Fill; form.Show(); Application.DoEvents();
                    string copied = null; view.CopyResult = delegate(string text) { copied = text; };
                    Check(!Find<FlatAction>(view, "BandwidthCopy").Enabled, mode + ": copy disabled before a result exists");
                    Click(Find<FlatAction>(view, "BandwidthStart"));
                    Check(view.Running && !Find<FlatAction>(view, "BandwidthStart").Enabled && Find<FlatAction>(view, "BandwidthStop").Enabled,
                        mode + ": controls reflect active measurement");
                    if (mode == "slow") {
                        Thread.Sleep(180); Application.DoEvents(); Click(Find<FlatAction>(view, "BandwidthStop"));
                        Check(Find<Label>(view, "BandwidthState").Text == "停止中", "Stop immediately shows pending cancellation");
                        typeof(NetworkSpeedTestView).GetMethod("RefreshProgress", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, null);
                        Check(Find<Label>(view, "BandwidthStatus").Text == "正在停止测速…", "Late progress cannot overwrite pending cancellation feedback");
                    }
                    Wait(delegate { return !view.Running; }, 7000);
                    string state = Find<Label>(view, "BandwidthState").Text;
                    string expected = mode == "normal" ? "已完成" : mode == "reject-upload" ? "部分完成" : mode == "html" ? "失败" : "已停止";
                    Check(state == expected, mode + ": correct final state " + expected);
                    var progress = Find<AntdUI.Progress>(view, "BandwidthProgress");
                    Check(mode == "normal" ? progress.Fill == UiColor.Green : progress.Fill != UiColor.Green, mode + ": final progress color is meaningful");
                    Check(Find<FlatAction>(view, "BandwidthStart").Text == "重新测速" && Find<FlatAction>(view, "BandwidthStart").Enabled,
                        mode + ": retry action is available");
                    Check(!Find<FlatAction>(view, "BandwidthStop").Enabled, mode + ": stop is disabled after completion");
                    Check(Find<FlatAction>(view, "BandwidthCopy").Enabled, mode + ": copy becomes available");
                    Click(Find<FlatAction>(view, "BandwidthCopy"));
                    Check(copied != null && copied.Contains("时间：") && copied.Contains("服务器：127.0.0.1") && copied.Contains("下载：") && copied.Contains("上传："), mode + ": report has server, time and both directions");
                    if (mode == "normal")
                    {
                        Check(copied.Contains("Mbps / ") && copied.Contains(" MB/s；有效数据 "), "Success report includes units and actual sample evidence");
                        view.CopyResult = delegate { throw new InvalidOperationException("clipboard busy fixture"); };
                        Click(Find<FlatAction>(view, "BandwidthCopy"));
                        Check(Find<Label>(view, "BandwidthStatus").Text.Contains("请重试"), "Clipboard contention produces a recoverable message");
                        view.CopyResult = delegate(string text) { copied = text; };
                        Click(Find<FlatAction>(view, "BandwidthCopy"));
                    }
                    if (mode == "reject-upload" || mode == "html")
                    {
                        var label = Find<Label>(view, "BandwidthUploadValue");
                        Check(label.Text == "未测得" && label.ForeColor.R > label.ForeColor.G * 2, mode + ": failure text uses error color");
                        Check(!copied.Contains("0.00 Mbps"), mode + ": failure is not fabricated as zero throughput");
                    }
                    Wait(delegate {
                        using (var bitmap = new Bitmap(view.Width, view.Height))
                        {
                            view.DrawToBitmap(bitmap, new Rectangle(Point.Empty, view.Size));
                            Point point = view.PointToClient(progress.PointToScreen(new Point(progress.Width / 2, progress.Height / 2)));
                            Color pixel = bitmap.GetPixel(point.X, point.Y);
                            return mode == "slow" ? pixel.ToArgb() == progress.Back.Value.ToArgb() || pixel.ToArgb() == progress.Fill.Value.ToArgb() : pixel.ToArgb() == progress.Fill.Value.ToArgb();
                        }
                    }, 1800);
                    Check(true, mode + ": progress track is actually painted");
                    Capture(view, Path.Combine(output, mode + ".png")); form.Close();
                }
            }
        }
        catch (Exception ex) { failed++; results.Add(new { name = "Execution", status = "FAIL", detail = ex.ToString() }); Console.WriteLine(ex); }
        File.WriteAllText(Path.Combine(output, "window-functional-results.json"), new JavaScriptSerializer().Serialize(new {
            passed = results.Count - failed, failed = failed, results = results, screenshots = screenshots,
            method = "In-process window integration checks invoke registered Click handlers and pump UI callbacks against a loopback HTTP fixture. No physical mouse or UIA InvokePattern claim.", visualReview = "Pending" }));
        Console.WriteLine("Window checks: " + results.Count + ", failures: " + failed);
        if (failed != 0) Environment.ExitCode = 1;
    }
}
