using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

internal sealed class BandwidthTestOptions
{
    public Uri Server = new Uri("https://speed.cloudflare.com/");
    public int Connections = 4;
    public int DurationMilliseconds = 10000;
    public long MaxBytesPerDirection = 32L * 1024 * 1024;
}
internal sealed class BandwidthMeasurement
{
    public bool Upload, Limited;
    public long Bytes;
    public double Seconds;
    public string Error;
    public bool Success { get { return Error == null && Bytes > 0; } }
    public double MBps { get { return Bytes / 1000000.0 / Math.Max(.001, Seconds); } }
    public double Mbps { get { return MBps * 8; } }
}
internal sealed class BandwidthProgress
{
    public bool Upload;
    public long Bytes;
    public double Seconds, Fraction;
}
internal sealed class BandwidthTestResult
{
    public BandwidthMeasurement Download, Upload;
}
internal static class BandwidthTestEngine
{
    static BandwidthTestEngine() { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }

    private static HttpWebRequest Request(Uri uri, BandwidthTestOptions options)
    {
        var r = (HttpWebRequest)WebRequest.Create(uri);
        r.Timeout = Math.Max(3000, options.DurationMilliseconds + 2000);
        r.ReadWriteTimeout = r.Timeout;
        r.AllowAutoRedirect = false;
        r.AutomaticDecompression = DecompressionMethods.None;
        r.UserAgent = "CampusNetHelper/2.4.4";
        r.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
        r.Headers[HttpRequestHeader.AcceptEncoding] = "identity";
        r.ServicePoint.ConnectionLimit = Math.Max(8, options.Connections);
        r.ServicePoint.Expect100Continue = false;
        return r;
    }

    private static HttpWebResponse Response(HttpWebRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var r = (HttpWebResponse)request.GetResponse();
            if ((int)r.StatusCode < 200 || (int)r.StatusCode >= 300)
            { int status = (int)r.StatusCode; r.Close(); throw new IOException("HTTP " + status + "，测速服务器未接受请求。"); }
            return r;
        }
        catch (WebException ex)
        {
            token.ThrowIfCancellationRequested();
            var r = ex.Response as HttpWebResponse;
            if (r == null) throw new IOException("测速连接失败或超时（" + ex.Status + "）。", ex);
            int status = (int)r.StatusCode; r.Close();
            if (status == 429 || status == 503) throw new RateLimitedException("HTTP " + status + "，测速服务器限流或暂不可用，已停止。");
            throw new IOException("HTTP " + status + "，测速服务器拒绝请求。", ex);
        }
    }

    public static BandwidthTestResult Run(BandwidthTestOptions options, CancellationToken token,
        Action<BandwidthProgress> progress, Action<BandwidthMeasurement> completed)
    {
        if (options == null || options.Server == null || (options.Server.Scheme != "http" && options.Server.Scheme != "https") ||
            !String.IsNullOrEmpty(options.Server.UserInfo) || options.Connections < 1 || options.Connections > 8 ||
            options.DurationMilliseconds < 200 || options.DurationMilliseconds > 30000 ||
            options.MaxBytesPerDirection < 65536 || options.MaxBytesPerDirection > 64L * 1024 * 1024)
            throw new ArgumentException("测速参数无效。");
        token.ThrowIfCancellationRequested();
        var result = new BandwidthTestResult();
        result.Download = Measure(options, false, token, progress);
        if (completed != null) completed(result.Download);
        token.ThrowIfCancellationRequested();
        result.Upload = result.Download.Limited ? new BandwidthMeasurement {
            Upload = true, Error = "服务器限流，未继续上传测试。", Limited = true
        } : Measure(options, true, token, progress);
        if (completed != null) completed(result.Upload);
        return result;
    }

    private static BandwidthMeasurement Measure(BandwidthTestOptions options, bool upload,
        CancellationToken userToken, Action<BandwidthProgress> progress)
    {
        long allocated = 0, bytes = 0;
        Exception failure = null;
        object gate = new object();
        byte[] payload = new byte[65536];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(payload);
        var clock = Stopwatch.StartNew();
        using (var deadline = new CancellationTokenSource(options.DurationMilliseconds))
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(userToken, deadline.Token))
        {
            CancellationToken token = stop.Token;
            Action report = delegate {
                if (progress != null) progress(new BandwidthProgress { Upload = upload,
                    Bytes = Interlocked.Read(ref bytes), Seconds = clock.Elapsed.TotalSeconds,
                    Fraction = Math.Min(1, Math.Max(clock.Elapsed.TotalMilliseconds / options.DurationMilliseconds,
                        Interlocked.Read(ref bytes) / (double)options.MaxBytesPerDirection)) });
            };
            report();
            Task[] workers = new Task[options.Connections];
            for (int i = 0; i < workers.Length; i++) workers[i] = Task.Factory.StartNew(delegate
            {
                int size = 256 * 1024;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        int count;
                        lock (gate)
                        {
                            count = (int)Math.Min(size, options.MaxBytesPerDirection - allocated);
                            if (count <= 0) return;
                            allocated += count;
                        }
                        var sample = Stopwatch.StartNew();
                        Uri endpoint = new Uri(options.Server, upload ? "__up" : "__down?bytes=" + count + "&r=" + Guid.NewGuid().ToString("N"));
                        var request = Request(endpoint, options);
                        using (token.Register(request.Abort))
                        {
                            if (upload)
                            {
                                request.Method = "POST";
                                request.ContentType = "application/octet-stream";
                                request.ContentLength = count;
                                request.AllowWriteStreamBuffering = false;
                                using (Stream stream = request.GetRequestStream())
                                {
                                    for (int sent = 0; sent < count;)
                                    {
                                        token.ThrowIfCancellationRequested();
                                        int n = Math.Min(payload.Length, count - sent);
                                        stream.Write(payload, 0, n); sent += n;
                                    }
                                }
                                // A write can stop in a local socket buffer. Count upload
                                // bytes only after a successful server acknowledgement.
                                using (var response = Response(request, token))
                                {
                                    if ((response.ContentType ?? "").IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0)
                                        throw new IOException("上传接口返回了验证网页，未计入有效速度。");
                                }
                                Interlocked.Add(ref bytes, count);
                            }
                            else using (var response = Response(request, token))
                            {
                                string type = response.ContentType ?? "";
                                if ((int)response.StatusCode != 200 || type.IndexOf("text/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    type.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    !String.IsNullOrEmpty(response.ContentEncoding) && response.ContentEncoding != "identity" ||
                                    response.ContentLength >= 0 && response.ContentLength != count)
                                    throw new IOException("测速接口返回了网页、压缩内容或错误长度，未计入有效速度。");
                                using (Stream stream = response.GetResponseStream())
                                {
                                    byte[] buffer = new byte[65536];
                                    int read = 0;
                                    while (read < count)
                                    {
                                        token.ThrowIfCancellationRequested();
                                        int n = stream.Read(buffer, 0, Math.Min(buffer.Length, count - read));
                                        if (n == 0) throw new IOException("下载测速数据提前中断。");
                                        read += n; Interlocked.Add(ref bytes, n);
                                        report();
                                    }
                                }
                            }
                        }
                        report();
                        if (sample.ElapsedMilliseconds < 600) size = Math.Min(4 * 1024 * 1024, size * 2);
                    }
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) return;
                    lock (gate) { if (failure == null) failure = ex; }
                    stop.Cancel();
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task.WaitAll(workers);
            clock.Stop();
            userToken.ThrowIfCancellationRequested();
            return new BandwidthMeasurement { Upload = upload, Bytes = Interlocked.Read(ref bytes), Seconds = clock.Elapsed.TotalSeconds,
                Limited = failure is RateLimitedException,
                Error = failure != null ? failure.Message : bytes == 0 ? "未完成有效数据采样，请检查网络或稍后重试。" : null };
        }
    }
}
