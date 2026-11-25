using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

internal static class BandwidthVerify
{
    private static int checks;
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks++; Console.WriteLine("PASS " + text); }
    private static BandwidthTestOptions Options(string root, string path)
    { return new BandwidthTestOptions { Server = new Uri(root + path + "/"), Connections = 2, DurationMilliseconds = 2500, MaxBytesPerDirection = 512 * 1024 }; }
    private static void Main(string[] args)
    {
        try
        {
            if (args.Length > 2 && args[2] == "--live")
            {
                var live = BandwidthTestEngine.Run(new BandwidthTestOptions { Connections = 2, DurationMilliseconds = 4000, MaxBytesPerDirection = 1024 * 1024 }, CancellationToken.None, null, null);
                File.WriteAllText(args[1], new JavaScriptSerializer().Serialize(live));
                Check(live.Download.Success && live.Upload.Success, "Live public endpoint downloads and acknowledges upload"); return;
            }
            var options = Options(args[0], "normal");
            int updates = 0, completed = 0;
            var good = BandwidthTestEngine.Run(options, CancellationToken.None, delegate(BandwidthProgress p) { Interlocked.Increment(ref updates); }, delegate { completed++; });
            Check(good.Download.Success && good.Upload.Success, "Both transfer directions produce real results");
            Check(good.Download.Bytes == options.MaxBytesPerDirection && good.Upload.Bytes == options.MaxBytesPerDirection, "Per-direction byte budgets are exact across workers");
            Check(Math.Abs(good.Download.Mbps - good.Download.MBps * 8) < .000001 && Math.Abs(good.Upload.Mbps - good.Upload.MBps * 8) < .000001, "Mbps and decimal MB/s use the correct conversion");
            Check(good.Upload.Seconds >= .18, "Upload includes server acknowledgement instead of only local buffer writes");
            Check(updates > 2 && completed == 2, "Progress and two direction-completion updates are delivered");
            var unacked = BandwidthTestEngine.Run(Options(args[0], "unacked"), CancellationToken.None, null, null);
            Check(!unacked.Upload.Success && unacked.Upload.Bytes == 0, "Unacknowledged upload is not reported as measured speed");
            var rejected = BandwidthTestEngine.Run(Options(args[0], "reject-upload"), CancellationToken.None, null, null);
            Check(rejected.Download.Success && !rejected.Upload.Success && rejected.Upload.Error.Contains("403"), "Upload rejection preserves independent download result");
            var html = BandwidthTestEngine.Run(Options(args[0], "html"), CancellationToken.None, null, null);
            Check(!html.Download.Success && !html.Upload.Success, "Login/captcha HTML is rejected in both directions");
            var wrong = BandwidthTestEngine.Run(Options(args[0], "wrong"), CancellationToken.None, null, null);
            Check(!wrong.Download.Success, "Wrong download content length is rejected");
            var limited = BandwidthTestEngine.Run(Options(args[0], "limited"), CancellationToken.None, null, null);
            Check(limited.Download.Limited && limited.Upload.Limited && !limited.Upload.Success, "Rate limiting stops further upload attempts");
            using (var cancel = new CancellationTokenSource(200))
            {
                bool stopped = false; var clock = Stopwatch.StartNew();
                try { BandwidthTestEngine.Run(Options(args[0], "stall"), cancel.Token, null, null); } catch (OperationCanceledException) { stopped = true; }
                Check(stopped && clock.ElapsedMilliseconds < 2000, "Cancellation aborts a blocked request promptly");
            }
            bool invalid = false; options.Connections = 9;
            try { BandwidthTestEngine.Run(options, CancellationToken.None, null, null); } catch (ArgumentException) { invalid = true; }
            Check(invalid, "Invalid connection options are rejected before network access");
            File.WriteAllText(args[1], "PASS " + checks + " bandwidth engine checks. Loopback fixture values are not campus Internet speed results.");
        }
        catch (Exception ex) { Console.WriteLine(ex); Environment.ExitCode = 1; }
    }
}
