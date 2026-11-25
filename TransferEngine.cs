using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

internal sealed class MirrorSite
{
    public string Id, Name, Base;
    public override string ToString() { return Name; }
    public static readonly MirrorSite[] All = {
        new MirrorSite { Id="tencent", Name="腾讯云", Base="https://mirrors.cloud.tencent.com" },
        new MirrorSite { Id="huawei", Name="华为云", Base="https://mirrors.huaweicloud.com" },
        new MirrorSite { Id="aliyun", Name="阿里云", Base="https://mirrors.aliyun.com" },
        new MirrorSite { Id="ustc", Name="中科大", Base="https://mirrors.ustc.edu.cn" },
        new MirrorSite { Id="tuna", Name="清华 TUNA", Base="https://mirrors.tuna.tsinghua.edu.cn" },
        new MirrorSite { Id="163", Name="网易", Base="https://mirrors.163.com" }
    };
    public const string ProbePath = "/ubuntu/pool/main/l/linux-firmware/linux-firmware_20220329.git681281e4-0ubuntu1_all.deb";
}

internal sealed class RemoteFile
{
    public Uri Url; public long Length; public bool Ranges; public string ETag, Modified;
}
internal sealed class TransferStats
{
    public long Bytes; public double Seconds; public string Sha256;
    public double MBps { get { return Bytes / 1000000.0 / Math.Max(.001, Seconds); } }
    public double Mbps { get { return MBps * 8; } }
}
internal sealed class RateLimitedException : IOException
{
    public RateLimitedException(string message) : base(message) { }
}
internal static class TransferEngine
{
    static TransferEngine() { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
    public static Uri ParseUrl(string text)
    {
        Uri url;
        if (!Uri.TryCreate(text, UriKind.Absolute, out url) ||
            (url.Scheme != "http" && url.Scheme != "https") || !String.IsNullOrEmpty(url.UserInfo))
            throw new ArgumentException("请输入 http 或 https 文件直链，地址中不要包含账号密码。");
        return url;
    }
    private static HttpWebRequest Request(Uri url)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.UserAgent = "CampusNetHelper/2.3.0";
        request.Timeout = 15000; request.ReadWriteTimeout = 15000;
        request.AllowAutoRedirect = true; request.MaximumAutomaticRedirections = 6;
        request.AutomaticDecompression = DecompressionMethods.None;
        request.Headers[HttpRequestHeader.AcceptEncoding] = "identity";
        request.ServicePoint.ConnectionLimit = 8;
        return request;
    }
    private static HttpWebResponse Response(HttpWebRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return (HttpWebResponse)request.GetResponse(); }
        catch (WebException ex)
        {
            token.ThrowIfCancellationRequested();
            var response = ex.Response as HttpWebResponse;
            if (response == null) throw new IOException("连接失败或超时：" + ex.Status, ex);
            int code = (int)response.StatusCode; string retry = response.Headers["Retry-After"]; response.Close();
            if (code == 429 || code == 503)
                throw new RateLimitedException("HTTP " + code + "：服务器限流或暂不可用，已停止请求。" + (retry == null ? "请稍后重试。" : "Retry-After: " + retry));
            throw new IOException("HTTP " + code + "：服务器拒绝请求或文件不可用。", ex);
        }
    }
    private static long RangeTotal(HttpWebResponse response, long start, long end)
    {
        var match = Regex.Match(response.Headers["Content-Range"] ?? "", @"^bytes (\d+)-(\d+)/(\d+)$");
        if ((int)response.StatusCode != 206 || !match.Success ||
            Int64.Parse(match.Groups[1].Value) != start || Int64.Parse(match.Groups[2].Value) != end)
            throw new IOException("服务器未正确返回请求的文件分段，已停止，避免生成损坏文件。");
        if (!String.IsNullOrEmpty(response.ContentEncoding) && response.ContentEncoding != "identity")
            throw new IOException("服务器返回压缩分段，无法可靠校验。");
        return Int64.Parse(match.Groups[3].Value);
    }
    public static RemoteFile Inspect(Uri url, CancellationToken token)
    {
        var request = Request(url); request.AddRange(0, 0);
        using (token.Register(request.Abort))
        using (var response = Response(request, token))
        {
            bool ranges = (int)response.StatusCode == 206;
            long length = ranges ? RangeTotal(response, 0, 0) : response.ContentLength;
            if (response.ContentType != null && response.ContentType.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new IOException("地址返回了网页或验证页，请使用文件下载直链。");
            // No full body is fetched when the server ignores Range.
            return new RemoteFile { Url = response.ResponseUri, Length = length, Ranges = ranges,
                ETag = response.Headers["ETag"], Modified = response.Headers["Last-Modified"] };
        }
    }
    private static long Part(RemoteFile file, long start, long end, Stream target, CancellationToken token, Action<int> progress)
    {
        var request = Request(file.Url); request.AddRange(start, end);
        if (!String.IsNullOrEmpty(file.ETag) && !file.ETag.StartsWith("W/")) request.Headers["If-Range"] = file.ETag;
        else if (!String.IsNullOrEmpty(file.Modified)) request.Headers["If-Range"] = file.Modified;
        using (token.Register(request.Abort))
        using (var response = Response(request, token))
        {
            if (RangeTotal(response, start, end) != file.Length) throw new IOException("下载期间远端文件大小发生变化。");
            if (!String.IsNullOrEmpty(file.ETag) && response.Headers["ETag"] != file.ETag)
                throw new IOException("下载期间远端文件版本发生变化。");
            if (String.IsNullOrEmpty(file.ETag) && !String.IsNullOrEmpty(file.Modified) && response.Headers["Last-Modified"] != file.Modified)
                throw new IOException("下载期间远端文件修改时间发生变化。");
            using (Stream input = response.GetResponseStream())
            {
                byte[] buffer = new byte[65536]; long count = 0, expected = end - start + 1;
                while (count < expected)
                {
                    token.ThrowIfCancellationRequested();
                    int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, expected - count));
                    if (read == 0) throw new IOException("文件分段提前结束，已停止下载。");
                    target.Write(buffer, 0, read); count += read; if (progress != null) progress(read);
                }
                if (input.ReadByte() != -1) throw new IOException("服务器返回了超出请求范围的数据。");
                return count;
            }
        }
    }
    public static TransferStats Measure(RemoteFile file, int connections, long bytes, CancellationToken token)
    {
        if (connections < 1 || connections > 8) throw new ArgumentOutOfRangeException("connections");
        if (!file.Ranges || file.Length <= 0) throw new IOException("服务器不支持可靠分段，不能进行连接数对比；仍可单连接下载。");
        bytes = Math.Min(bytes, file.Length);
        if (bytes < connections) throw new IOException("测试文件过小。");
        long complete = 0; var watch = Stopwatch.StartNew();
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            stop.CancelAfter(60000);
            var tasks = new List<Task>();
            long chunk = (bytes + connections - 1) / connections;
            for (int i = 0; i < connections; i++)
            {
                long start = i * chunk, end = Math.Min(start + chunk - 1, bytes - 1);
                if (start > end) continue;
                tasks.Add(Task.Run(delegate {
                    try { long count = Part(file, start, end, Stream.Null, stop.Token, null); Interlocked.Add(ref complete, count); }
                    catch { stop.Cancel(); throw; }
                }));
            }
            try { Task.WaitAll(tasks.ToArray()); }
            catch (AggregateException ex) { token.ThrowIfCancellationRequested(); throw ex.Flatten().InnerExceptions.FirstOrDefault(e => e is RateLimitedException) ?? ex.Flatten().InnerExceptions.FirstOrDefault(e => e is IOException) ?? new IOException("测速超时或中断。"); }
        }
        watch.Stop();
        if (complete != bytes) throw new IOException("采样不完整，本次数值无效。");
        return new TransferStats { Bytes = complete, Seconds = watch.Elapsed.TotalSeconds };
    }
    public static TransferStats Download(Uri url, string destination, int connections, bool overwrite, string expectedHash,
        CancellationToken token, Action<long, long, double> progress)
    {
        if (connections < 1 || connections > 8) throw new ArgumentOutOfRangeException("connections");
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination) && !overwrite) throw new IOException("文件已存在，请选择其他文件名。");
        if (!String.IsNullOrWhiteSpace(expectedHash) && !Regex.IsMatch(expectedHash, "^[0-9a-fA-F]{64}$")) throw new ArgumentException("SHA256 应为 64 位十六进制字符。");
        RemoteFile file = Inspect(url, token);
        if (!file.Ranges) connections = 1;
        string folder = Path.GetDirectoryName(destination);
        if (!Directory.Exists(folder)) throw new IOException("保存文件夹不存在。");
        string temporary = Path.Combine(folder, ".campus-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var watch = Stopwatch.StartNew(); long received = 0; long lastUpdate = 0;
        Action<int> report = delegate(int count) {
            long value = Interlocked.Add(ref received, count); long now = watch.ElapsedMilliseconds;
            if (progress != null && now - Interlocked.Read(ref lastUpdate) > 200) {
                Interlocked.Exchange(ref lastUpdate, now); progress(value, file.Length, watch.Elapsed.TotalSeconds);
            }
        };
        try
        {
            string merged = Path.Combine(temporary, "complete.tmp");
            if (file.Ranges && file.Length > 0)
            {
                using (var stop = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    var tasks = new List<Task>(); long chunk = (file.Length + connections - 1) / connections;
                    for (int i = 0; i < connections; i++)
                    {
                        long start = i * chunk, end = Math.Min(start + chunk - 1, file.Length - 1);
                        string part = Path.Combine(temporary, i.ToString("D2") + ".part");
                        if (start > end) continue;
                        tasks.Add(Task.Run(delegate {
                            try { using (Stream output = File.Create(part)) Part(file, start, end, output, stop.Token, report); }
                            catch { stop.Cancel(); throw; }
                        }));
                    }
                    try { Task.WaitAll(tasks.ToArray()); }
                    catch (AggregateException ex) { token.ThrowIfCancellationRequested(); throw ex.Flatten().InnerExceptions.FirstOrDefault(e => e is RateLimitedException) ?? ex.Flatten().InnerExceptions.FirstOrDefault(e => e is IOException) ?? ex; }
                }
                using (Stream output = File.Create(merged)) foreach (string part in Directory.GetFiles(temporary, "*.part").OrderBy(p => p)) {
                    token.ThrowIfCancellationRequested(); using (Stream input = File.OpenRead(part)) Copy(input, output, token, null);
                }
            }
            else
            {
                var request = Request(file.Url);
                using (token.Register(request.Abort))
                using (var response = Response(request, token))
                {
                    if ((int)response.StatusCode != 200) throw new IOException("下载服务器未返回完整文件。");
                    if (response.ContentType != null && response.ContentType.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0) throw new IOException("服务器返回了网页，下载已停止。");
                    file.Length = response.ContentLength;
                    using (Stream input = response.GetResponseStream()) using (Stream output = File.Create(merged)) Copy(input, output, token, report);
                }
            }
            token.ThrowIfCancellationRequested();
            long actual = new FileInfo(merged).Length;
            if (file.Length >= 0 && actual != file.Length) throw new IOException("文件大小校验失败，原文件未被替换。");
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create()) using (Stream input = File.OpenRead(merged))
            {
                byte[] buffer = new byte[1024 * 1024]; int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); sha.TransformBlock(buffer, 0, read, buffer, 0); }
                sha.TransformFinalBlock(new byte[0], 0, 0); hash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
            if (!String.IsNullOrWhiteSpace(expectedHash) && !hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("SHA256 与发布者提供的值不符，未保存为最终文件。");
            token.ThrowIfCancellationRequested();
            if (File.Exists(destination)) { if (!overwrite) throw new IOException("目标文件已存在。"); File.Replace(merged, destination, null); }
            else File.Move(merged, destination);
            watch.Stop(); if (progress != null) progress(actual, actual, watch.Elapsed.TotalSeconds);
            return new TransferStats { Bytes = actual, Seconds = watch.Elapsed.TotalSeconds, Sha256 = hash };
        }
        finally { try { Directory.Delete(temporary, true); } catch { } }
    }
    private static void Copy(Stream input, Stream output, CancellationToken token, Action<int> progress)
    {
        byte[] buffer = new byte[65536]; int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); if (progress != null) progress(read); }
    }
    public static double DnsLatency(string server, CancellationToken token)
    {
        using (var client = new UdpClient(AddressFamily.InterNetwork))
        {
            client.Client.ReceiveTimeout = 1800; client.Connect(server, 53);
            var random = Guid.NewGuid().ToByteArray();
            var query = new List<byte>(new byte[] { random[0], random[1], 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
            foreach (string label in "www.qq.com".Split('.')) { query.Add((byte)label.Length); query.AddRange(Encoding.ASCII.GetBytes(label)); }
            query.AddRange(new byte[] { 0, 0, 1, 0, 1 });
            token.ThrowIfCancellationRequested(); var watch = Stopwatch.StartNew();
            using (token.Register(client.Close))
            {
                client.Send(query.ToArray(), query.Count); IPEndPoint endpoint = null; byte[] answer = client.Receive(ref endpoint); watch.Stop();
                if (answer.Length < 12 || answer[0] != random[0] || answer[1] != random[1] || (answer[2] & 128) == 0 || (answer[3] & 15) != 0 || (answer[6] == 0 && answer[7] == 0)) throw new IOException("DNS 未返回有效解析结果。");
                return watch.Elapsed.TotalMilliseconds;
            }
        }
    }
}
