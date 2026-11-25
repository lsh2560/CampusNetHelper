using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

// Ports tests/portal-client.test.cjs and tests/Verify-Runner.ps1 to C# after the
// Node.js authentication client was replaced by PortalClient.cs / CampusLogin.cs.
// Uses only loopback fixtures and fictional accounts; never contacts the school.
internal static class PortalClientVerify
{
    private static int passed;
    private static readonly List<string> lines = new List<string>();

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        passed++;
        lines.Add("PASS: " + name);
    }

    private static int Main(string[] args)
    {
        try
        {
            string output = Path.GetFullPath(args.Length > 0 ? args[0] : "portal-output");
            Directory.CreateDirectory(output);
            ProtocolTests();
            FormAndJsonTests();
            RunnerTests(output);
            File.WriteAllText(Path.Combine(output, "portal-verification.txt"), String.Join("\r\n", lines.ToArray()) + "\r\n" + passed + " checks passed\r\n", new UTF8Encoding(false));
            Console.WriteLine(passed + " checks passed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Console.WriteLine(String.Join("\r\n", lines.ToArray()));
            return 1;
        }
    }

    // ---------------------------------------------------------------- fixture

    internal sealed class Call
    {
        public string Method, Path, Query, Body;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string RemoteAddress;
    }

    internal sealed class Options
    {
        public bool ShowIsp, DialPending, AlreadyOnline, Unknown, ChangePassword, Reject, Captcha, Slow, DialFails, NeverOnline, Huge, ServerError, ExpiresCookie;
        public string ExternalRedirect, PostRedirect, IspJson, CsrfJson, RawCsrf, RawRedirect;
        public string[] CsrfCookies;
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly Options options;
        private readonly Thread thread;
        private volatile bool stopped;
        public readonly List<Call> Calls = new List<Call>();
        private bool authenticated;
        private int polls;
        public string Origin { get { return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port; } }

        public Fixture(Options options)
        {
            this.options = options;
            listener.Start();
            thread = new Thread(Serve) { IsBackground = true };
            thread.Start();
        }

        private void Serve()
        {
            while (!stopped)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); } catch { return; }
                ThreadPool.QueueUserWorkItem(delegate { try { Handle(client); } catch { } finally { client.Close(); } });
            }
        }

        private static string ReadLine(Stream stream)
        {
            var bytes = new List<byte>();
            int b;
            while ((b = stream.ReadByte()) >= 0)
            {
                if (b == '\n') break;
                if (b != '\r') bytes.Add((byte)b);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private void Handle(TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 5000;
            string requestLine = ReadLine(stream);
            if (String.IsNullOrEmpty(requestLine)) return;
            string[] parts = requestLine.Split(' ');
            var call = new Call { Method = parts[0], RemoteAddress = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString() };
            string target = parts[1];
            int q = target.IndexOf('?');
            call.Path = q < 0 ? target : target.Substring(0, q);
            call.Query = q < 0 ? "" : target.Substring(q + 1);
            string line;
            while ((line = ReadLine(stream)).Length > 0)
            {
                int colon = line.IndexOf(':');
                if (colon > 0) call.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            int length = call.Headers.ContainsKey("Content-Length") ? Int32.Parse(call.Headers["Content-Length"]) : 0;
            var body = new byte[length];
            int read = 0;
            while (read < length) { int n = stream.Read(body, read, length - read); if (n <= 0) break; read += n; }
            call.Body = Encoding.UTF8.GetString(body, 0, read);
            lock (Calls) Calls.Add(call);
            Respond(stream, call);
        }

        private static void Write(Stream stream, int status, string[] headers, string body)
        {
            byte[] content = Encoding.UTF8.GetBytes(body ?? "");
            var head = new StringBuilder("HTTP/1.1 " + status + " X\r\n");
            foreach (string header in headers ?? new string[0]) head.Append(header).Append("\r\n");
            head.Append("Content-Type: application/json\r\nContent-Length: " + content.Length + "\r\nConnection: close\r\n\r\n");
            byte[] bytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(bytes, 0, bytes.Length);
            stream.Write(content, 0, content.Length);
            stream.Flush();
        }

        private static void WriteRaw(Stream stream, string raw)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(raw);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private const string Online ="{\"code\":0,\"online\":{\"Username\":\"fictional-student\"},\"enableDial\":true,\"dialCode\":\"ok:dialup\"}";
        private const string DialPendingReply = "{\"code\":0,\"online\":{\"Username\":\"fictional-student\"},\"enableDial\":true,\"dialCode\":\"\"}";

        private void Respond(Stream stream, Call call)
        {
            if (call.Path == "/api/r/default" && options.RawRedirect != null) { WriteRaw(stream, options.RawRedirect); return; }
            if (call.Path == "/api/csrf-token" && options.RawCsrf != null) { WriteRaw(stream, options.RawCsrf); return; }
            if (call.Path == "/api/csrf-token" && (options.CsrfJson != null || options.CsrfCookies != null))
            {
                Write(stream, 200, (options.CsrfCookies ?? new string[0]).Select(c => "Set-Cookie: " + c).ToArray(), options.CsrfJson ?? "{\"csrf_token\":\"fixture-csrf\"}");
                return;
            }
            if (call.Path == "/api/r/default")
            {
                var headers = new List<string> { "Set-Cookie: campus_session=local-test; HttpOnly; Path=/" };
                if (options.ExpiresCookie) headers.Add("Set-Cookie: campus_lang=zh-CN; Expires=Wed, 21 Oct 2037 07:28:00 GMT; Path=/api");
                headers.Add("Location: " + (options.ExternalRedirect ?? "/tpl/default/login.html?nasId=1&ip=10.14.1.2&mac=aa%3Abb&note=a+b%20c"));
                Write(stream, 302, headers.ToArray(), "");
                return;
            }
            if (call.Path == "/tpl/default/login.html") { Write(stream, 200, null, "<html>fixture</html>"); return; }
            if (options.Slow) { Thread.Sleep(500); try { Write(stream, 200, null, "{}"); } catch { } return; }
            if (call.Path == "/api/csrf-token") { Write(stream, 200, null, "{\"csrf_token\":\"fixture-csrf\"}"); return; }
            if (options.ServerError) { Write(stream, 500, null, "{}"); return; }
            if (options.Huge) { Write(stream, 200, null, "{\"x\":\"" + new string('a', 600 * 1024) + "\"}"); return; }
            if (call.Path == "/api/account/status")
            {
                polls++;
                if (options.Unknown) { Write(stream, 200, null, "{\"code\":1,\"msg\":\"服务器内部错误\"}"); return; }
                if (options.AlreadyOnline || authenticated)
                {
                    if (options.DialFails) { Write(stream, 200, null, "{\"code\":0,\"online\":{\"Username\":\"u\"},\"enableDial\":true,\"dialCode\":\"error:busy\"}"); return; }
                    Write(stream, 200, null, options.NeverOnline || (options.DialPending && polls < 3) ? DialPendingReply : Online);
                    return;
                }
                Write(stream, 200, null, "{\"code\":1,\"msg\":\"用户未在线\"}");
                return;
            }
            if (call.Path == "/api/config")
            {
                Write(stream, 200, null, "{\"code\":0,\"data\":{\"config\":{\"auth_methods\":\"account\",\"show_isp\":\"" + (options.ShowIsp ? "1" : "0") + "\",\"enable_online_time\":\"1\",\"online_time_options\":\"240,480\"}}}");
                return;
            }
            if (call.Path == "/api/default/isp") { Write(stream, 200, null, options.IspJson ?? "{\"code\":0,\"data\":{\"cmcc\":\"中国移动\",\"cucc\":\"中国联通\"}}"); return; }
            if (call.Path == "/api/account/check") { Write(stream, 200, null, options.ChangePassword ? "{\"code\":0,\"isChangePwd\":\"1\"}" : "{\"code\":0,\"isChangePwd\":0}"); return; }
            if (call.Path == "/api/account/login")
            {
                if (options.PostRedirect != null) { Write(stream, 302, new[] { "Location: " + options.PostRedirect }, ""); return; }
                if (options.Reject) { Write(stream, 200, null, "{\"code\":1,\"msg\":\"错误的账号密码\"}"); return; }
                if (options.Captcha) { Write(stream, 200, null, "{\"code\":2,\"captcha\":{\"captchaId\":\"fixture\"}}"); return; }
                authenticated = true;
                Write(stream, 200, null, options.DialPending || options.NeverOnline || options.DialFails ? DialPendingReply : Online);
                return;
            }
            Write(stream, 404, null, "{}");
        }

        public CampusPortalClient Client(int timeoutMs = 2000, int operationMs = 4000)
        {
            return new CampusPortalClient(Origin, timeoutMs, operationMs, 5, null);
        }

        public int Posts { get { lock (Calls) return Calls.Count(c => c.Method == "POST"); } }
        public int CallsTo(string path) { lock (Calls) return Calls.Count(c => c.Path == path); }

        public void Dispose() { stopped = true; listener.Stop(); }
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T ex) { return ex; }
        catch (Exception ex) { throw new Exception("Expected " + typeof(T).Name + " but got " + ex.GetType().Name + ": " + ex.Message); }
        throw new Exception("Expected " + typeof(T).Name + " but nothing was thrown");
    }

    // ---------------------------------------------------------------- protocol (portal-client.test.cjs)

    private static void ProtocolTests()
    {
        using (var f = new Fixture(new Options { ShowIsp = true, DialPending = true, ExpiresCookie = true }))
        {
            CampusPortalClient c = f.Client();
            bool already = c.Login("fictional-student", "fictional secret+&=é%");
            Check(!already, "login follows CSRF/cookie/check/login protocol and reports a new login");
            Call[] posts; lock (f.Calls) posts = f.Calls.Where(x => x.Method == "POST").ToArray();
            Check(posts.Length == 2 && posts[0].Path == "/api/account/check" && posts[1].Path == "/api/account/login", "exactly two submissions: check then login");
            List<KeyValuePair<string, string>> form = FormData.Parse(posts[1].Body);
            Check(FormData.Get(form, "username") == "fictional-student" && FormData.Get(form, "password") == "fictional secret+&=é%", "account and special-character password round-trip");
            Check(posts[1].Body.Contains("password=fictional+secret%2B%26%3D%C3%A9%25"), "password encoded exactly as URLSearchParams");
            Check(FormData.Get(form, "isp") == "cucc" && FormData.Get(form, "nasId") == "1" && FormData.Get(form, "userIpv4") == "10.14.1.2" && FormData.Get(form, "userMac") == "aa:bb", "portal parameters and default carrier forwarded");
            Check(FormData.Get(form, "timeLimit") == "240", "first online-time option forwarded");
            Check(f.CallsTo("/api/account/status") >= 3, "status polled until dial confirmed");
            Call api; lock (f.Calls) api = f.Calls.First(x => x.Path == "/api/account/status");
            Check(api.Headers.ContainsKey("X-CSRF-Token") && api.Headers["X-CSRF-Token"] == "fixture-csrf", "CSRF header sent");
            Check(api.Headers.ContainsKey("Cookie") && api.Headers["Cookie"].Contains("campus_session=local-test") && api.Headers["Cookie"].Contains("campus_lang=zh-CN"), "multiple Set-Cookie (incl. Expires with comma) stored and sent everywhere");
            Check(!posts[1].Headers.ContainsKey("Expect"), "no Expect: 100-continue on submissions");
            Check(posts[1].Headers["Origin"] == f.Origin && posts[1].Headers["Referer"] == f.Origin + "/tpl/default/login_account.html", "Origin and Referer match the school page");
            Check(posts[1].Headers["Content-Type"].StartsWith("application/x-www-form-urlencoded"), "form content type");
            Check(posts[1].Headers.ContainsKey("User-Agent") && posts[1].Headers["User-Agent"].StartsWith("CampusNetHelper/"), "product user agent");
        }
        using (var f = new Fixture(new Options { AlreadyOnline = true }))
            Check(f.Client().Login("u", "p") && f.Posts == 0, "already online never submits credentials");
        using (var f = new Fixture(new Options { Unknown = true }))
        {
            Throws<PortalException>(() => f.Client().Login("u", "p"));
            Check(f.Posts == 0, "unknown status never submits credentials");
        }
        using (var f = new Fixture(new Options()))
        {
            f.Client().Login("u", "p", "ctcc");
            Call login; lock (f.Calls) login = f.Calls.First(x => x.Path == "/api/account/login");
            Check(FormData.Get(FormData.Parse(login.Body), "isp") == null, "hidden carrier control does not send a provider");
        }
        using (var f = new Fixture(new Options { ShowIsp = true }))
        {
            Throws<PortalAuthenticationException>(() => f.Client().Login("u", "p", "ctcc"));
            Check(f.Posts == 0, "missing selected carrier stops before password submission");
        }
        using (var f = new Fixture(new Options { ShowIsp = true, IspJson = "{\"code\":0,\"data\":{\"9\":\"中国电信\",\"2\":\"中国移动\"}}" }))
        {
            f.Client().Login("u", "p", "auto");
            Call login; lock (f.Calls) login = f.Calls.First(x => x.Path == "/api/account/login");
            Check(FormData.Get(FormData.Parse(login.Body), "isp") == "2", "carrier fallback uses JavaScript key order (integer keys ascending)");
        }
        using (var f = new Fixture(new Options { ChangePassword = true }))
        {
            Throws<PortalAuthenticationException>(() => f.Client().Login("u", "p"));
            Check(f.CallsTo("/api/account/login") == 0, "password change requirement (string \"1\") stops before login");
        }
        using (var f = new Fixture(new Options { Reject = true }))
        {
            Throws<PortalAuthenticationException>(() => f.Client().Login("u", "p"));
            Check(f.CallsTo("/api/account/login") == 1, "bad credentials are terminal and do not retry");
        }
        using (var f = new Fixture(new Options { Captcha = true }))
        {
            Throws<PortalAuthenticationException>(() => f.Client().Login("u", "p"));
            Check(f.CallsTo("/api/account/login") == 1, "captcha is terminal and does not retry");
        }
        using (var f = new Fixture(new Options { DialFails = true }))
            Check(Throws<PortalAuthenticationException>(() => f.Client().Login("u", "p")).Message.Contains("拨号失败"), "dial failure is terminal");
        using (var f = new Fixture(new Options { NeverOnline = true }))
            Check(Throws<PortalException>(() => f.Client(500, 800).Login("u", "p")).Message.Contains("未确认拨号"), "unconfirmed dial stops at the operation deadline");
        using (var f = new Fixture(new Options { ExternalRedirect = "http://127.0.0.2:9/" }))
            Check(Throws<PortalException>(() => f.Client().Initialize()).Message.Contains("认证地址"), "external redirect refused before contacting destination");
        using (var f = new Fixture(new Options { PostRedirect = "/untrusted" }))
        {
            Check(Throws<PortalException>(() => f.Client().Login("u", "p")).Message.Contains("重定向"), "POST redirect refused");
            Check(f.CallsTo("/untrusted") == 0, "POST redirect never replays credentials");
        }
        using (var f = new Fixture(new Options { Slow = true }))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Check(Throws<PortalException>(() => f.Client(30, 1500).Initialize()).Message.Contains("超时") && watch.ElapsedMilliseconds < 1500, "hung server stops at the request timeout");
        }
        using (var f = new Fixture(new Options { ServerError = true }))
            Check(Throws<PortalException>(() => f.Client().Login("u", "p")).Message.Contains("HTTP 500"), "HTTP errors are transient portal errors");
        using (var f = new Fixture(new Options { Huge = true }))
            Check(Throws<PortalException>(() => f.Client().Login("u", "p")).Message.Contains("过大"), "oversized replies are refused");
        // Windows retries a refused loopback connection for about 2 s, so allow longer than that.
        string refused = Throws<PortalException>(() => new CampusPortalClient("http://127.0.0.1:9", 8000, 10000, 5, null).Initialize()).Message;
        Check(refused.Contains("无法连接"), "closed port reports connection failure (" + refused + ")");
        Check(Throws<PortalAuthenticationException>(() => new CampusPortalClient("http://127.0.0.1:9", 500, 1000, 5, null).Login("", "p")) != null, "empty account stops before any request");

        // Header handling found by the differential test against the former login.cjs.
        using (var f = new Fixture(new Options { CsrfCookies = new[] { "a=1, b=2", "list=x,y,z; Path=/", "c=3; expires=Thu, 01 Jan 2099 00:00:00 GMT, d=4" } }))
        {
            f.Client().Login("u", "p");
            Call status; lock (f.Calls) status = f.Calls.First(x => x.Path == "/api/account/status");
            Check(status.Headers["Cookie"] == "campus_session=local-test; a=1, b=2; list=x,y,z; c=3", "Set-Cookie lines are read whole, commas included (" + status.Headers["Cookie"] + ")");
        }
        using (var f = new Fixture(new Options { RawRedirect = "HTTP/1.1 302 Found\r\nLocation: /tpl/default/login.html?nasId=A\r\nLocation: http://evil.example/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n" }))
        {
            f.Client().Login("u", "p");
            Call login; lock (f.Calls) login = f.Calls.First(x => x.Path == "/api/account/login");
            Check(FormData.Get(FormData.Parse(login.Body), "nasId") == "A", "repeated Location keeps only the first value");
        }
        using (var f = new Fixture(new Options { RawRedirect = "HTTP/1.1 302 Found\r\nLocation: /\\evil.example/x\r\nContent-Length: 0\r\nConnection: close\r\n\r\n" }))
        {
            Check(Throws<PortalException>(() => f.Client().Initialize()).Message.Contains("认证地址") && f.CallsTo("//evil.example/x") == 0, "backslash Location (/\\host) is another host and refused");
        }
        using (var f = new Fixture(new Options { RawCsrf = "HTTP/1.1 200 OK\r\nContent-Length: 50\r\nConnection: close\r\n\r\n{\"csrf_token\":\"t\"}" }))
            Check(Throws<PortalException>(() => f.Client().Initialize()).Message.Contains("中断"), "body shorter than Content-Length is an interrupted reply");
        using (var f = new Fixture(new Options { CsrfJson = "{\"csrf_token\":\"a" + (char)0x2028 + "b\"}" }))
        {
            Check(Throws<PortalException>(() => f.Client().Login("u", "p")).Message.Contains("令牌") && f.Posts == 0, "CSRF token a header cannot carry stops before submission");
        }
        using (var f = new Fixture(new Options { CsrfJson = "{'csrf_token':'fixture-csrf'}" }))
            Check(Throws<PortalException>(() => f.Client().Initialize()).Message.Contains("无法识别"), "non-standard JSON (single quotes) is refused like JSON.parse");

        Check(CampusPortalClient.IsCampusAddress(IPAddress.Parse("10.14.1.2")) && CampusPortalClient.IsCampusAddress(IPAddress.Parse("10.15.0.9")) &&
            !CampusPortalClient.IsCampusAddress(IPAddress.Parse("10.16.0.1")) && !CampusPortalClient.IsCampusAddress(IPAddress.Parse("198.18.0.1")), "campus address range 10.14/10.15 only");
        Check(new CampusPortalClient("http://127.0.0.1:9", 500, 1000, 5, null).LocalAddress == null, "other origins keep system routing");
        using (var f = new Fixture(new Options()))
        {
            new CampusPortalClient(f.Origin, 2000, 4000, 5, IPAddress.Loopback).Json("/api/csrf-token", null);
            Call bound; lock (f.Calls) bound = f.Calls.Last();
            Check(bound.RemoteAddress == "127.0.0.1", "bound requests leave from the requested local address");
        }
    }

    // ---------------------------------------------------------------- pure helpers

    private static object J(string json) { return PortalJson.Parse(json); }

    private static void FormAndJsonTests()
    {
        Check(CampusPortalClient.Classify(J("{\"code\":0,\"online\":{\"Username\":\"u\"},\"enableDial\":true,\"dialCode\":\"error\"}")) == 20, "dial error is not online");
        Check(CampusPortalClient.Classify(J("{\"code\":0}")) == 20, "missing online block is not online");
        Check(CampusPortalClient.Classify(J("{\"code\":1,\"msg\":\"server error\"}")) == 20, "unrelated error is unknown");
        Check(CampusPortalClient.Classify(J("{\"code\":1,\"msg\":\"未在线\"}")) == 10, "offline message means login required");
        Check(CampusPortalClient.Classify(J("{\"code\":1,\"msg\":\"User NOT ONLINE\"}")) == 10, "offline match is case-insensitive");
        Check(CampusPortalClient.Classify(J("{\"code\":0,\"online\":{\"Username\":\"u\"},\"enableDial\":true,\"dialCode\":\"ok:dialup\"}")) == 0, "dial confirmed is online");
        Check(CampusPortalClient.Classify(J("{\"code\":0,\"online\":{\"Username\":\"u\"}}")) == 0, "no dial required is online");
        Check(CampusPortalClient.Classify(J("{\"code\":\"0\",\"online\":{\"Username\":\"u\"}}")) == 20, "string code \"0\" is not strictly 0");
        Check(CampusPortalClient.Classify(J("{\"code\":0,\"online\":{\"Username\":\"\"}}")) == 20, "empty username is not online");
        Check(CampusPortalClient.Classify(J("[1,2]")) == 20 && CampusPortalClient.Classify(null) == 20, "non-object replies are unknown");
        Check(PortalJson.Parse("not json") == null, "malformed JSON yields nothing");
        Check(PortalJson.ToNumber("1") == 1 && PortalJson.ToNumber(true) == 1 && PortalJson.ToNumber(" ") == 0 && Double.IsNaN(PortalJson.ToNumber("x")), "Number() coercion");

        // Differences found by the differential test against the former login.cjs.
        Check(CampusPortalClient.Classify(J("{\"code\":1,\"msg\":\"not\\ronline\"}")) == 20 && CampusPortalClient.Classify(J("{\"code\":1,\"msg\":\"not\\nonline\"}")) == 20 &&
            CampusPortalClient.Classify(J("{\"code\":1,\"msg\":\"not online\"}")) == 10, "offline pattern '.' does not match line terminators (JavaScript semantics)");
        foreach (string bad in new[] { "{'code':1}", "{code:1}", "{\"code\":01}", "{\"code\":NaN}", "{\"code\":1,/*c*/\"msg\":\"x\"}", (char)0xFEFF + "{\"code\":1}",
            "{\"a\":\"x\ty\"}", "{\"a\":1,}", "[1,]", "{\"a\":.5}", "{\"a\":1.}", "{\"a\":-}", "{\"a\":\"\\x41\"}", "{\"a\":1} x", "" })
            Check(PortalJson.Parse(bad) == null, "JSON.parse would reject: " + bad.Replace("\t", "\\t"));
        object dup = J("{\"code\":0,\"msg\":\"m\",\"code\":1}");
        Check(PortalJson.NumberIs(PortalJson.Get(dup, "code"), 1) && PortalJson.Entries(dup)[0].Key == "code", "duplicate keys: last value wins, first position kept");
        Check(Double.IsPositiveInfinity((double)PortalJson.Get(J("{\"a\":1e400}"), "a")) && (double)PortalJson.Get(J("{\"a\":-0.0}"), "a") == 0, "number overflow becomes Infinity");
        Check(PortalJson.Get(J("{\"a\":\"\\u4e2d\\/\\\"\"}"), "a") as string == "中/\"", "string escapes");
        Check(PortalJson.Parse(new string('[', 1001) + new string(']', 1001)) == null && PortalJson.Parse(new string('[', 500) + new string(']', 500)) != null, "nesting limit is conservative");
        Check(PortalJson.ToNumber("0x1") == 1 && PortalJson.ToNumber("0b11") == 3 && PortalJson.ToNumber("0o17") == 15 && Double.IsNaN(PortalJson.ToNumber("-0x1")), "Number() radix literals");
        Check(PortalJson.ToNumber(J("[1]")) == 1 && PortalJson.ToNumber(J("[\"1\"]")) == 1 && PortalJson.ToNumber(J("[]")) == 0 && Double.IsNaN(PortalJson.ToNumber(J("[1,2]"))) && Double.IsNaN(PortalJson.ToNumber(J("{}"))), "Number() of arrays and objects");
        Check(PortalJson.ToNumber((char)0xFEFF + "1") == 1 && PortalJson.ToNumber((char)0x3000 + "1 ") == 1 && Double.IsNaN(PortalJson.ToNumber("1,0")) && Double.IsNaN(PortalJson.ToNumber("" + (char)0xFF11)), "Number() whitespace and digits follow JavaScript");
        Check(PortalJson.ToNumber("Infinity") == Double.PositiveInfinity && PortalJson.ToNumber("1e0") == 1 && PortalJson.ToNumber("+1") == 1 && PortalJson.ToNumber("1.") == 1 && Double.IsNaN(PortalJson.ToNumber("1_0")), "Number() decimal literals");
        Check(PortalJson.NumberText(12345678901234567890d) == "12345678901234567000" && PortalJson.NumberText(0.5) == "0.5" && PortalJson.NumberText(1e21) == "1e+21" &&
            PortalJson.NumberText(1e-7) == "1e-7" && PortalJson.NumberText(123.45) == "123.45" && PortalJson.NumberText(100) == "100" && PortalJson.NumberText(0.000001) == "0.000001" &&
            PortalJson.NumberText(-2.5e-8) == "-2.5e-8" && PortalJson.NumberText(240) == "240", "String(number) matches JavaScript");
        Check(CampusPortalClient.BrowserLocation("/\\evil.example/x") == "//evil.example/x" && CampusPortalClient.BrowserLocation(" \t/a\nb?x=\\y ") == "/ab?x=\\y", "Location read like a browser URL parser");

        List<KeyValuePair<string, string>> q = FormData.Parse("?a=1&b=x+y%20z&c=%E4%B8%AD&d=%zz&e&a=2");
        Check(FormData.Get(q, "a") == "1" && FormData.Get(q, "b") == "x y z" && FormData.Get(q, "c") == "中" && FormData.Get(q, "d") == "%zz" && FormData.Get(q, "e") == "", "query parsing matches URLSearchParams");
        Check(FormData.Encode(new[] { new KeyValuePair<string, string>("k", "a-b_c.d*e~f!'()") }) == "k=a-b_c.d*e%7Ef%21%27%28%29", "form encoding keeps only *-._ and alphanumerics");
    }

    // ---------------------------------------------------------------- runner (Verify-Runner.ps1)

    private static void Isolate(string data)
    {
        Directory.CreateDirectory(data);
        foreach (string field in new[] { "Data", "Secret", "Log", "Settings", "RecoveryState", "MonitorStatus", "Startup" })
            typeof(AppPaths).GetField(field).SetValue(null, field == "Data" ? data : Path.Combine(data, field == "Log" ? "run.log" : field + ".json"));
    }

    private static void RunnerTests(string output)
    {
        string data = Path.Combine(output, "runner-" + Guid.NewGuid().ToString("N"));
        Isolate(data);
        File.WriteAllText(AppPaths.Settings, "{\"priorityMode\":\"Off\",\"autoLogin\":true,\"carrier\":\"auto\"}");
        int calls = 0;
        var sleeps = new List<int>();
        Action<int> sleep = delegate(int ms) { sleeps.Add(ms); };
        Func<string, string, string, bool> reject = delegate(string u, string p, string c) { calls++; throw new PortalAuthenticationException("校园网认证被拒绝（测试）"); };
        Func<string, string, string, bool> ok = delegate(string u, string p, string c)
        {
            calls++;
            if (u != "fictional-student" || p != "fictional-password" || c != "auto") throw new Exception("wrong credential passed");
            return false;
        };
        Func<string, string, string, bool> transient = delegate(string u, string p, string c) { calls++; throw new PortalException("无法连接校园网认证服务器"); };

        Check(CampusLogin.Run(true, ok, sleep) == CampusLogin.NeedsPerson && calls == 0, "missing account is reported without contacting the portal");

        CredentialStore.Save("fictional-student", "fictional-password");
        Check(CampusLogin.Run(true, reject, sleep) == CampusLogin.NeedsPerson, "terminal rejection");
        Check(CampusLogin.Run(true, ok, sleep) == CampusLogin.NeedsPerson && calls == 1, "saved rejection blocks the next run without contacting the portal");

        CredentialStore.Save("fictional-student", "fictional-password");
        Check(CampusLogin.Run(true, ok, sleep) == 0 && CampusLogin.Run(true, ok, sleep) == 0 && CampusLogin.Run(true, ok, sleep) == 0, "new saved credential unblocks; three attempts allowed");
        Check(CampusLogin.AtRateLimit(), "monitor sees the shared limit");
        Check(CampusLogin.Run(true, ok, sleep) == CampusLogin.RateLimited && calls == 4, "fourth attempt within 10 minutes is refused before contacting the portal");

        CredentialStore.Save("fictional-student", "fictional-password");
        Check(!CampusLogin.AtRateLimit(), "changed credential resets the limit");
        calls = 0;
        Check(CampusLogin.Run(false, transient, sleep) == CampusLogin.Failed && calls == 3 && sleeps.SequenceEqual(new[] { 15000, 30000 }), "startup login retries 3 times with 15 s / 30 s pauses");

        CredentialStore.Save("fictional-student", "fictional-password");
        var started = new ManualResetEvent(false);
        var release = new ManualResetEvent(false);
        int firstResult = -1;
        var first = new Thread(delegate()
        {
            firstResult = CampusLogin.Run(true, delegate(string u, string p, string c) { started.Set(); release.WaitOne(5000); return false; }, sleep);
        });
        first.Start();
        started.WaitOne(5000);
        Check(CampusLogin.Run(true, ok, sleep) == CampusLogin.Busy, "concurrent login is skipped");
        release.Set();
        first.Join(5000);
        Check(firstResult == 0, "original concurrent login completes");

        // A state file written by the former PowerShell runner (blocked) must still apply.
        CredentialStore.Save("fictional-student", "fictional-password");
        string fingerprint;
        using (var sha = System.Security.Cryptography.SHA256.Create())
            fingerprint = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(AppPaths.Secret))) + ":auto";
        File.WriteAllText(AppPaths.AuthState, "{\r\n    \"blocked\":  true,\r\n    \"attempts\":  [\r\n                     " + DateTime.UtcNow.Ticks + "\r\n                 ],\r\n    \"fingerprint\":  \"" + fingerprint + "\"\r\n}", new UTF8Encoding(true));
        calls = 0;
        Check(CampusLogin.Run(true, ok, sleep) == CampusLogin.NeedsPerson && calls == 0, "block recorded by the PowerShell runner (UTF-8 BOM) still applies");

        File.WriteAllText(AppPaths.AuthState, "null");
        Check(CampusLogin.Run(true, ok, sleep) == 0 && calls == 1, "\"null\" state file starts a fresh record (as PowerShell did)");
        File.WriteAllText(AppPaths.AuthState, "{garbage");
        Check(CampusLogin.Run(true, ok, sleep) == CampusLogin.Failed && calls == 1, "unreadable state file stops before submission");
        File.Delete(AppPaths.AuthState);
        File.WriteAllText(AppPaths.Settings, "  \r\n");
        Check(CampusLogin.Run(true, ok, sleep) == 0 && calls == 2, "empty settings file means the default carrier");

        File.WriteAllText(AppPaths.Settings, "{\"carrier\":\"bogus\"}");
        Check(CampusLogin.Run(true, ok, sleep) == CampusLogin.Failed && calls == 2, "invalid carrier setting stops before submission");

        string log = File.ReadAllText(AppPaths.Log, Encoding.UTF8);
        Check(!log.Contains("fictional-password"), "password never written to the log");
        Check(log.Contains("校园网登录成功（认证及拨号已确认）") && log.Contains("10 分钟内认证尝试达到 3 次"), "log keeps the messages the home page recognises");
    }
}
