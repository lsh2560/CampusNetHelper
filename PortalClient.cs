using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

// The school rejected the request or needs a person (bad password, captcha,
// password change, dial failure). Automatic login must stop until the user acts.
internal sealed class PortalAuthenticationException : Exception
{
    public PortalAuthenticationException(string message) : base(message) { }
}

// Network trouble, timeouts or replies that cannot be trusted. Safe to retry later.
internal sealed class PortalException : Exception
{
    public PortalException(string message) : base(message) { }
}

// Strict JSON reader with JSON.parse semantics: objects keep key order and the last
// duplicate wins, numbers are doubles, and anything JSON.parse rejects (single
// quotes, unquoted keys, comments, NaN, leading zeros, a BOM) is rejected.
internal sealed class StrictJson
{
    private const int MaxDepth = 1000;
    private readonly string text;
    private int at, depth;

    private StrictJson(string text) { this.text = text; }

    internal static object Parse(string text)
    {
        var reader = new StrictJson(text ?? "");
        reader.Space();
        object value = reader.Value();
        reader.Space();
        if (reader.at != reader.text.Length) throw new FormatException();
        return value;
    }

    private void Space()
    {
        while (at < text.Length && (text[at] == ' ' || text[at] == '\t' || text[at] == '\n' || text[at] == '\r')) at++;
    }

    private char Peek() { if (at >= text.Length) throw new FormatException(); return text[at]; }

    private void Expect(char c) { if (Peek() != c) throw new FormatException(); at++; }

    private object Value()
    {
        char c = Peek();
        if (c == '{') return ReadObject();
        if (c == '[') return ReadArray();
        if (c == '"') return ReadString();
        if (c == 't') { Word("true"); return true; }
        if (c == 'f') { Word("false"); return false; }
        if (c == 'n') { Word("null"); return null; }
        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
        throw new FormatException();
    }

    private void Word(string word)
    {
        if (string.CompareOrdinal(text, at, word, 0, word.Length) != 0) throw new FormatException();
        at += word.Length;
    }

    private Dictionary<string, object> ReadObject()
    {
        if (++depth > MaxDepth) throw new FormatException();
        at++;
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        Space();
        if (Peek() == '}') { at++; depth--; return result; }
        while (true)
        {
            Space();
            if (Peek() != '"') throw new FormatException();
            string key = ReadString();
            Space();
            Expect(':');
            Space();
            result[key] = Value();
            Space();
            char c = Peek();
            at++;
            if (c == '}') break;
            if (c != ',') throw new FormatException();
        }
        depth--;
        return result;
    }

    private List<object> ReadArray()
    {
        if (++depth > MaxDepth) throw new FormatException();
        at++;
        var result = new List<object>();
        Space();
        if (Peek() == ']') { at++; depth--; return result; }
        while (true)
        {
            Space();
            result.Add(Value());
            Space();
            char c = Peek();
            at++;
            if (c == ']') break;
            if (c != ',') throw new FormatException();
        }
        depth--;
        return result;
    }

    private string ReadString()
    {
        at++;
        var builder = new StringBuilder();
        while (true)
        {
            char c = Peek();
            at++;
            if (c == '"') return builder.ToString();
            if (c < 0x20) throw new FormatException();
            if (c != '\\') { builder.Append(c); continue; }
            char e = Peek();
            at++;
            switch (e)
            {
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                case '/': builder.Append('/'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'u':
                    if (at + 4 > text.Length) throw new FormatException();
                    int code;
                    string hex = text.Substring(at, 4);
                    if (!hex.All(Uri.IsHexDigit) || !Int32.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code)) throw new FormatException();
                    builder.Append((char)code);
                    at += 4;
                    break;
                default: throw new FormatException();
            }
        }
    }

    private double ReadNumber()
    {
        int start = at;
        if (text[at] == '-') at++;
        if (Peek() == '0') at++;
        else if (Peek() >= '1' && Peek() <= '9') Digits();
        else throw new FormatException();
        if (at < text.Length && text[at] == '.') { at++; if (!(Peek() >= '0' && Peek() <= '9')) throw new FormatException(); Digits(); }
        if (at < text.Length && (text[at] == 'e' || text[at] == 'E'))
        {
            at++;
            if (Peek() == '+' || Peek() == '-') at++;
            if (!(Peek() >= '0' && Peek() <= '9')) throw new FormatException();
            Digits();
        }
        return PortalJson.ParseDecimal(text.Substring(start, at - start));
    }

    private void Digits() { while (at < text.Length && text[at] >= '0' && text[at] <= '9') at++; }
}

// JSON values read with JavaScript semantics, so the checks below mean exactly
// what the former login.cjs checks meant.
internal static class PortalJson
{
    // JavaScript WhiteSpace and LineTerminator characters (String.prototype.trim, \s, Number()).
    internal const string JsSpaceClass = @"\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff";
    private static readonly Regex JsSpace = new Regex("^[" + JsSpaceClass + "]+|[" + JsSpaceClass + "]+\\z", RegexOptions.CultureInvariant);
    private static readonly Regex DecimalLiteral = new Regex(@"^[+-]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?\z", RegexOptions.CultureInvariant);
    private static readonly Regex Infinity = new Regex(@"^[+-]?Infinity\z", RegexOptions.CultureInvariant);
    private static readonly Regex RadixLiteral = new Regex(@"^0([xX][0-9a-fA-F]+|[oO][0-7]+|[bB][01]+)\z", RegexOptions.CultureInvariant);

    internal static object Parse(string text)
    {
        try { return StrictJson.Parse(text); }
        catch { return null; }
    }

    // A decimal literal already validated by the caller; overflow becomes ±Infinity like JavaScript.
    internal static double ParseDecimal(string literal)
    {
        try { return Double.Parse(literal, NumberStyles.Float, CultureInfo.InvariantCulture); }
        catch (OverflowException) { return literal.StartsWith("-", StringComparison.Ordinal) ? Double.NegativeInfinity : Double.PositiveInfinity; }
    }

    internal static object Get(object value, string key)
    {
        var map = value as IDictionary<string, object>;
        object result;
        return map != null && map.TryGetValue(key, out result) ? result : null;
    }

    private static bool IsNumeric(object value)
    {
        return value is int || value is long || value is decimal || value is double || value is float;
    }

    // value === n
    internal static bool NumberIs(object value, double n)
    {
        return IsNumeric(value) && Convert.ToDouble(value, CultureInfo.InvariantCulture) == n;
    }

    // value === text
    internal static bool StringIs(object value, string text)
    {
        var s = value as string;
        return s != null && String.Equals(s, text, StringComparison.Ordinal);
    }

    internal static bool Truthy(object value)
    {
        if (value == null) return false;
        if (value is bool) return (bool)value;
        var s = value as string;
        if (s != null) return s.Length > 0;
        if (IsNumeric(value)) { double d = Convert.ToDouble(value, CultureInfo.InvariantCulture); return d != 0 && !Double.IsNaN(d); }
        return true;
    }

    // Number(value): arrays and objects go through String() first, as in JavaScript.
    internal static double ToNumber(object value)
    {
        if (value == null) return 0;
        if (value is bool) return (bool)value ? 1 : 0;
        if (IsNumeric(value)) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        var s = value as string ?? ToText(value);
        s = JsSpace.Replace(s, "");
        if (s.Length == 0) return 0;
        if (Infinity.IsMatch(s)) return s[0] == '-' ? Double.NegativeInfinity : Double.PositiveInfinity;
        if (RadixLiteral.IsMatch(s))
        {
            int radix = s[1] == 'x' || s[1] == 'X' ? 16 : s[1] == 'o' || s[1] == 'O' ? 8 : 2;
            double result = 0;
            foreach (char c in s.Substring(2)) result = result * radix + Convert.ToInt32(c.ToString(), 16);
            return result;
        }
        return DecimalLiteral.IsMatch(s) ? ParseDecimal(s) : Double.NaN;
    }

    // String(value)
    internal static string ToText(object value)
    {
        if (value == null) return "null";
        var s = value as string;
        if (s != null) return s;
        if (value is bool) return (bool)value ? "true" : "false";
        if (IsNumeric(value)) return NumberText(Convert.ToDouble(value, CultureInfo.InvariantCulture));
        if (value is IDictionary<string, object>) return "[object Object]";
        var list = value as IEnumerable;
        if (list != null) return String.Join(",", list.Cast<object>().Select(item => item == null ? "" : ToText(item)).ToArray());
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    // Number.prototype.toString() (ECMAScript Number::toString with radix 10).
    internal static string NumberText(double value)
    {
        if (Double.IsNaN(value)) return "NaN";
        if (value == 0) return "0";
        if (Double.IsInfinity(value)) return value > 0 ? "Infinity" : "-Infinity";
        if (value < 0) return "-" + NumberText(-value);
        string r = value.ToString("R", CultureInfo.InvariantCulture);
        int exponent = 0, e = r.IndexOfAny(new[] { 'E', 'e' });
        string mantissa = r;
        if (e >= 0) { exponent = Int32.Parse(r.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture); mantissa = r.Substring(0, e); }
        int dot = mantissa.IndexOf('.');
        string whole = dot < 0 ? mantissa : mantissa.Substring(0, dot);
        string all = whole + (dot < 0 ? "" : mantissa.Substring(dot + 1));
        string digits = all.TrimStart('0');
        int n = whole.Length + exponent - (all.Length - digits.Length); // value = 0.digits × 10^n
        digits = digits.TrimEnd('0');
        int k = digits.Length;
        if (k == 0) return "0";
        if (k <= n && n <= 21) return digits + new string('0', n - k);
        if (0 < n && n <= 21) return digits.Substring(0, n) + "." + digits.Substring(n);
        if (-6 < n && n <= 0) return "0." + new string('0', -n) + digits;
        int power = n - 1;
        return digits.Substring(0, 1) + (k > 1 ? "." + digits.Substring(1) : "") + "e" + (power < 0 ? "-" : "+") + Math.Abs(power).ToString(CultureInfo.InvariantCulture);
    }

    // Object.entries(value): integer keys ascending first, then the rest in order.
    internal static List<KeyValuePair<string, object>> Entries(object value)
    {
        var result = new List<KeyValuePair<string, object>>();
        var map = value as IDictionary<string, object>;
        if (map != null)
        {
            var indexed = new List<KeyValuePair<string, object>>();
            foreach (KeyValuePair<string, object> pair in map)
                (IsArrayIndex(pair.Key) ? indexed : result).Add(pair);
            indexed.Sort((a, b) => UInt32.Parse(a.Key, CultureInfo.InvariantCulture).CompareTo(UInt32.Parse(b.Key, CultureInfo.InvariantCulture)));
            indexed.AddRange(result);
            return indexed;
        }
        var list = value as IList;
        if (list != null)
            for (int i = 0; i < list.Count; i++) result.Add(new KeyValuePair<string, object>(i.ToString(CultureInfo.InvariantCulture), list[i]));
        return result;
    }

    private static bool IsArrayIndex(string key)
    {
        uint n;
        return key.Length > 0 && (key == "0" || key[0] != '0') && key.All(c => c >= '0' && c <= '9') &&
            UInt32.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n != UInt32.MaxValue;
    }
}

// application/x-www-form-urlencoded exactly as URLSearchParams writes and reads it.
internal static class FormData
{
    internal static string Encode(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        return String.Join("&", pairs.Select(pair => Escape(pair.Key) + "=" + Escape(pair.Value)).ToArray());
    }

    private static string Escape(string text)
    {
        var builder = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(text ?? ""))
        {
            if (b == 0x2A || b == 0x2D || b == 0x2E || b == 0x5F || (b >= 0x30 && b <= 0x39) || (b >= 0x41 && b <= 0x5A) || (b >= 0x61 && b <= 0x7A))
                builder.Append((char)b);
            else if (b == 0x20) builder.Append('+');
            else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    internal static List<KeyValuePair<string, string>> Parse(string query)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (String.IsNullOrEmpty(query)) return result;
        if (query[0] == '?') query = query.Substring(1);
        foreach (string part in query.Split('&'))
        {
            if (part.Length == 0) continue;
            int equals = part.IndexOf('=');
            string name = equals < 0 ? part : part.Substring(0, equals);
            string value = equals < 0 ? "" : part.Substring(equals + 1);
            result.Add(new KeyValuePair<string, string>(Unescape(name), Unescape(value)));
        }
        return result;
    }

    internal static string Get(List<KeyValuePair<string, string>> pairs, string name)
    {
        foreach (KeyValuePair<string, string> pair in pairs)
            if (String.Equals(pair.Key, name, StringComparison.Ordinal)) return pair.Value;
        return null;
    }

    private static string Unescape(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text.Replace('+', ' '));
        var output = new List<byte>(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            int high, low;
            if (bytes[i] == (byte)'%' && i + 2 < bytes.Length && (high = Hex(bytes[i + 1])) >= 0 && (low = Hex(bytes[i + 2])) >= 0)
            {
                output.Add((byte)(high * 16 + low));
                i += 2;
            }
            else output.Add(bytes[i]);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static int Hex(byte b)
    {
        if (b >= '0' && b <= '9') return b - '0';
        if (b >= 'a' && b <= 'f') return b - 'a' + 10;
        if (b >= 'A' && b <= 'F') return b - 'A' + 10;
        return -1;
    }
}

// Same HTTP API as the school's login page. Runs in-process: no Node.js, no
// PowerShell script and never a browser.
internal sealed class CampusPortalClient
{
    internal const string DefaultOrigin = "http://210.43.112.9";
    internal const int StatusOnline = 0, StatusLoginRequired = 10, StatusUnknown = 20;
    private const int MaxResponseBytes = 512 * 1024;
    // JavaScript "." matches anything except line terminators; .NET "." would also match \r, U+2028 and U+2029.
    private static readonly string JsDot = "[^\n\r" + (char)0x2028 + (char)0x2029 + "]";
    private static readonly Regex OfflineMessage = new Regex("未在线|不在线|未登录|未认证|离线|not" + JsDot + "?online|not" + JsDot + "?logged",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CookiePair = new Regex("^([^=;" + PortalJson.JsSpaceClass + "]+)=([^;]*)", RegexOptions.CultureInvariant);
    private static readonly Regex Digits = new Regex(@"^[0-9]+\z", RegexOptions.CultureInvariant);

    private readonly string scheme, host;
    private readonly int port;
    private readonly string origin;
    private readonly int timeoutMs, pollMs;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly long deadlineMs;
    private readonly List<KeyValuePair<string, string>> cookies = new List<KeyValuePair<string, string>>();
    private string csrf = "";
    private List<KeyValuePair<string, string>> pageParameters = new List<KeyValuePair<string, string>>();

    internal IPAddress LocalAddress { get; private set; }

    internal CampusPortalClient() : this(DefaultOrigin, 10000, 70000, 2000, null) { }

    // VPNs and game accelerators often take the default route. Portal traffic leaves
    // from the campus Wi-Fi/Ethernet address so the portal sees this computer's
    // campus IP, not a tunnel. Without a campus address, system routing is kept.
    internal CampusPortalClient(string origin, int timeoutMs, int operationMs, int pollMs, IPAddress localAddress)
    {
        var uri = new Uri(origin, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("认证地址必须是 http 或 https", "origin");
        scheme = uri.Scheme; host = uri.Host; port = uri.Port;
        this.origin = scheme + "://" + uri.Authority;
        this.timeoutMs = timeoutMs;
        this.pollMs = pollMs;
        deadlineMs = operationMs;
        LocalAddress = localAddress ?? (String.Equals(this.origin, DefaultOrigin, StringComparison.OrdinalIgnoreCase) ? DetectCampusAddress() : null);
    }

    internal static bool IsCampusAddress(IPAddress address)
    {
        if (address == null || address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] octets = address.GetAddressBytes();
        return octets[0] == 10 && (octets[1] == 14 || octets[1] == 15);
    }

    internal static IPAddress DetectCampusAddress()
    {
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation entry in adapter.GetIPProperties().UnicastAddresses)
                    if (IsCampusAddress(entry.Address)) return entry.Address;
            }
        }
        catch { /* Adapter information unavailable: keep system routing. */ }
        return null;
    }

    // 0 online, 10 the portal clearly says this device is not logged in, 20 anything else.
    internal static int Classify(object result)
    {
        if (!(result is IDictionary<string, object>)) return StatusUnknown;
        object code = PortalJson.Get(result, "code"), online = PortalJson.Get(result, "online");
        if (PortalJson.NumberIs(code, 0) && PortalJson.Truthy(online) && PortalJson.Truthy(PortalJson.Get(online, "Username")))
        {
            if (DialEnabled(result) && !PortalJson.StringIs(PortalJson.Get(result, "dialCode"), "ok:dialup")) return StatusUnknown;
            return StatusOnline;
        }
        if (PortalJson.NumberIs(code, 1))
        {
            object message = PortalJson.Get(result, "msg");
            if (OfflineMessage.IsMatch(PortalJson.Truthy(message) ? PortalJson.ToText(message) : "")) return StatusLoginRequired;
        }
        return StatusUnknown;
    }

    private static bool DialEnabled(object result)
    {
        object value = PortalJson.Get(result, "enableDial");
        return value is bool && (bool)value;
    }

    // Read-only status check used by the monitor and the home page.
    internal static int Probe(out string reason)
    {
        reason = "";
        try
        {
            var client = new CampusPortalClient(DefaultOrigin, 10000, 30000, 2000, null);
            client.Initialize();
            int state = Classify(client.Status());
            if (state == StatusUnknown) reason = "认证页返回的状态不明确";
            return state;
        }
        catch (PortalException ex) { reason = ex.Message; }
        catch (Exception ex) { reason = "认证组件异常（" + ex.GetType().Name + "）"; }
        return StatusUnknown;
    }

    private long Remaining() { return deadlineMs - clock.ElapsedMilliseconds; }

    private sealed class Reply
    {
        public int Status;
        public Uri Url;
        public string Location;
        public string Text;
    }

    internal sealed class Page
    {
        public Uri Url;
        public string Text;
    }

    internal Page Request(string path, IList<KeyValuePair<string, string>> form)
    {
        return Request(path, form, 0);
    }

    private Page Request(string path, IList<KeyValuePair<string, string>> form, int redirects)
    {
        Uri url;
        if (!Uri.TryCreate(new Uri(origin + "/"), path, out url) || !SameOrigin(url))
            throw new PortalException("认证地址发生变化，已停止请求");
        Reply reply = Send(url, form);
        if (reply.Status >= 300 && reply.Status < 400 && !String.IsNullOrEmpty(reply.Location))
        {
            if (form != null || redirects >= 5) throw new PortalException("认证接口重定向异常，已停止");
            Uri destination;
            if (!Uri.TryCreate(url, BrowserLocation(reply.Location), out destination)) throw new PortalException("认证地址发生变化，已停止请求");
            return Request(destination.AbsoluteUri, null, redirects + 1);
        }
        if (reply.Status != 200) throw new PortalException("校园网接口暂不可用（HTTP " + reply.Status + "）");
        return new Page { Url = reply.Url, Text = reply.Text };
    }

    // Reads a Location value the way a browser (and Node's URL) does before resolving it:
    // surrounding control characters and spaces are dropped, tabs and newlines removed,
    // and for http(s) a backslash before the query acts as "/" — so "/\host" is another host.
    internal static string BrowserLocation(string location)
    {
        string value = (location ?? "").Trim(Enumerable.Range(0, 0x21).Select(c => (char)c).ToArray());
        value = value.Replace("\t", "").Replace("\n", "").Replace("\r", "");
        int query = value.IndexOfAny(new[] { '?', '#' });
        string head = query < 0 ? value : value.Substring(0, query);
        return head.Replace('\\', '/') + (query < 0 ? "" : value.Substring(query));
    }

    // Header lines exactly as received. WebHeaderCollection.GetValues(name) would split
    // Set-Cookie at commas, and the indexer joins repeated lines with commas.
    private static List<string> HeaderLines(WebHeaderCollection headers, string name)
    {
        var lines = new List<string>();
        for (int i = 0; i < headers.Count; i++)
            if (String.Equals(headers.GetKey(i), name, StringComparison.OrdinalIgnoreCase))
                lines.AddRange(headers.GetValues(i) ?? new string[0]);
        return lines;
    }

    private bool SameOrigin(Uri url)
    {
        return url.IsAbsoluteUri && String.Equals(url.Scheme, scheme, StringComparison.OrdinalIgnoreCase) &&
            String.Equals(url.Host, host, StringComparison.OrdinalIgnoreCase) && url.Port == port && url.UserInfo.Length == 0;
    }

    private Reply Send(Uri url, IList<KeyValuePair<string, string>> form)
    {
        long remaining = Remaining();
        if (remaining <= 0) throw new PortalException("校园网认证请求超时");
        int budget = (int)Math.Max(1, Math.Min(timeoutMs, remaining));
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = form != null ? "POST" : "GET";
        request.AllowAutoRedirect = false;
        request.Proxy = null;          // Portal traffic never goes through a system proxy.
        request.KeepAlive = false;     // A fresh connection per request, bound below when needed.
        request.Timeout = budget;
        request.ReadWriteTimeout = budget;
        request.Accept = "application/json, text/html";
        request.UserAgent = "CampusNetHelper/" + ProductVersion();
        request.ServicePoint.Expect100Continue = false;
        IPAddress local = LocalAddress;
        request.ServicePoint.BindIPEndPointDelegate = local == null ? null : (BindIPEndPoint)delegate(ServicePoint point, IPEndPoint remote, int retry)
        {
            // Refuse to fall back to the default route if the campus address cannot be used.
            if (retry > 0 || remote.AddressFamily != local.AddressFamily) throw new PortalException("无法从校园网地址连接认证服务器");
            return new IPEndPoint(local, 0);
        };
        if (cookies.Count > 0) request.Headers[HttpRequestHeader.Cookie] = String.Join("; ", cookies.Select(c => c.Key + "=" + c.Value).ToArray());
        if (csrf.Length > 0) request.Headers["X-CSRF-Token"] = csrf;
        byte[] body = null;
        if (form != null)
        {
            body = Encoding.UTF8.GetBytes(FormData.Encode(form));
            request.ContentType = "application/x-www-form-urlencoded; charset=UTF-8";
            request.ContentLength = body.Length;
            request.Headers["Origin"] = origin;
            request.Referer = origin + "/tpl/default/login_account.html";
        }

        int timedOut = 0;
        bool reading = false;
        using (new Timer(delegate { Interlocked.Exchange(ref timedOut, 1); try { request.Abort(); } catch { } }, null, budget, Timeout.Infinite))
        {
            try
            {
                if (body != null)
                    using (Stream upload = request.GetRequestStream()) upload.Write(body, 0, body.Length);
                HttpWebResponse response;
                try { response = (HttpWebResponse)request.GetResponse(); }
                catch (WebException ex)
                {
                    response = ex.Response as HttpWebResponse;
                    if (response == null) throw;
                }
                using (response)
                {
                    StoreCookies(response);
                    // Like Node, a repeated Location header keeps only the first value.
                    var reply = new Reply { Status = (int)response.StatusCode, Url = response.ResponseUri ?? url, Location = HeaderLines(response.Headers, "Location").FirstOrDefault() };
                    if (reply.Status != 200) return reply;
                    reading = true;
                    reply.Text = ReadBody(response);
                    return reply;
                }
            }
            catch (PortalException) { throw; }
            catch (Exception)
            {
                if (Interlocked.CompareExchange(ref timedOut, 0, 0) == 1) throw new PortalException("校园网接口请求超时");
                throw new PortalException(reading ? "校园网接口响应中断" : "无法连接校园网认证服务器");
            }
        }
    }

    private static string ReadBody(HttpWebResponse response)
    {
        using (Stream stream = response.GetResponseStream())
        using (var buffer = new MemoryStream())
        {
            var chunk = new byte[16384];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes) throw new PortalException("校园网接口响应过大");
                buffer.Write(chunk, 0, read);
            }
            // The connection closed before the announced length arrived.
            if (response.ContentLength >= 0 && buffer.Length < response.ContentLength) throw new PortalException("校园网接口响应中断");
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    private void StoreCookies(HttpWebResponse response)
    {
        foreach (string value in HeaderLines(response.Headers, "Set-Cookie"))
        {
            Match match = CookiePair.Match(value ?? "");
            if (!match.Success) continue;
            string name = match.Groups[1].Value, content = match.Groups[2].Value;
            int index = cookies.FindIndex(c => c.Key == name);
            if (index >= 0) cookies[index] = new KeyValuePair<string, string>(name, content);
            else cookies.Add(new KeyValuePair<string, string>(name, content));
        }
    }

    private static string ProductVersion()
    {
        Version version = typeof(CampusPortalClient).Assembly.GetName().Version;
        return version.Major + "." + version.Minor + "." + Math.Max(0, version.Build);
    }

    internal object Json(string path, IList<KeyValuePair<string, string>> form)
    {
        Page page = Request(path, form);
        object value = PortalJson.Parse(page.Text);
        if (!(value is IDictionary<string, object>)) throw new PortalException("校园网接口返回了无法识别的内容");
        return value;
    }

    internal void Initialize()
    {
        Page page = Request("/api/r/default", null);
        pageParameters = FormData.Parse(page.Url.GetComponents(UriComponents.Query, UriFormat.UriEscaped));
        object token = Json("/api/csrf-token", null);
        string value = PortalJson.Get(token, "csrf_token") as string;
        // Only characters a header can carry (Node refused anything else before sending).
        if (String.IsNullOrEmpty(value) || value.Any(c => !(c == '\t' || (c >= 0x20 && c <= 0x7E) || (c >= 0x80 && c <= 0xFF))))
            throw new PortalException("无法取得校园网认证令牌");
        csrf = value;
    }

    internal object Status() { return Json("/api/account/status", null); }

    private static void Set(List<KeyValuePair<string, string>> form, string key, string value)
    {
        int index = form.FindIndex(pair => pair.Key == key);
        if (index >= 0) form[index] = new KeyValuePair<string, string>(key, value);
        else form.Add(new KeyValuePair<string, string>(key, value));
    }

    // Returns true when the device was already online and nothing was submitted.
    internal bool Login(string username, string password, string carrier = "auto")
    {
        if (String.IsNullOrEmpty(username) || String.IsNullOrEmpty(password)) throw new PortalAuthenticationException("请先保存校园网账号和密码");
        carrier = carrier ?? "auto";
        Initialize();
        object before = Status();
        int state = Classify(before);
        if (state == StatusOnline) return true;
        if (PortalJson.NumberIs(PortalJson.Get(before, "code"), 0) && PortalJson.Truthy(PortalJson.Get(before, "online")) &&
            PortalJson.Truthy(PortalJson.Get(PortalJson.Get(before, "online"), "Username")))
            return WaitOnline(before);
        if (state != StatusLoginRequired) throw new PortalException("当前认证状态不明确，未提交账号");

        object settings = Json("/api/config", null);
        object config = PortalJson.Get(PortalJson.Get(settings, "data"), "config");
        string methods = PortalJson.Get(config, "auth_methods") as string;
        if (!PortalJson.NumberIs(PortalJson.Get(settings, "code"), 0) || methods == null || !methods.Split(',').Contains("account"))
            throw new PortalException("认证页配置发生变化，未提交账号");

        var form = new List<KeyValuePair<string, string>>();
        Set(form, "username", username);
        Set(form, "password", password);
        foreach (string[] map in new[] { new[] { "switchip", "switchip" }, new[] { "nasId", "nasId" }, new[] { "wlanuserip", "userIpv4" }, new[] { "mac", "userMac" } })
        {
            string value = FormData.Get(pageParameters, map[0]);
            if (!String.IsNullOrEmpty(value)) Set(form, map[1], value);
        }
        if (String.IsNullOrEmpty(FormData.Get(form, "userIpv4")) && !String.IsNullOrEmpty(FormData.Get(pageParameters, "ip")))
            Set(form, "userIpv4", FormData.Get(pageParameters, "ip"));

        if (PortalJson.StringIs(PortalJson.Get(config, "show_isp"), "1"))
        {
            object providers = Json("/api/default/isp", null);
            object data = PortalJson.Get(providers, "data");
            if (!PortalJson.NumberIs(PortalJson.Get(providers, "code"), 0) || !(data is IDictionary<string, object> || data is IList))
                throw new PortalException("无法读取运营商选项");
            var names = new Dictionary<string, Regex>(StringComparer.Ordinal)
            {
                { "campus", new Regex("校园网|南华大学", RegexOptions.CultureInvariant) },
                { "cmcc", new Regex("移动|cmcc", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) },
                { "cucc", new Regex("联通|unicom|cucc", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) },
                { "ctcc", new Regex("电信|telecom|ctcc", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) },
            };
            List<KeyValuePair<string, object>> choices = PortalJson.Entries(data);
            string requested = carrier == "auto" ? "cucc" : carrier;
            Regex pattern;
            names.TryGetValue(requested, out pattern);
            int selected = choices.FindIndex(choice => choice.Key == requested || (pattern != null && pattern.IsMatch(PortalJson.ToText(choice.Value))));
            if (selected < 0 && carrier != "auto") throw new PortalAuthenticationException("学校认证页没有所选运营商，请更改运营商设置");
            if (selected >= 0) Set(form, "isp", choices[selected].Key);
            else if (choices.Count > 0) Set(form, "isp", choices[0].Key);
        }
        if (PortalJson.StringIs(PortalJson.Get(config, "enable_online_time"), "1"))
        {
            object options = PortalJson.Get(config, "online_time_options");
            string first = (PortalJson.Truthy(options) ? PortalJson.ToText(options) : "").Split(',')[0];
            if (Digits.IsMatch(first)) Set(form, "timeLimit", first);
        }

        object checkedAccount = Json("/api/account/check", form);
        if (PortalJson.NumberIs(PortalJson.Get(checkedAccount, "code"), 0) && PortalJson.ToNumber(PortalJson.Get(checkedAccount, "isChangePwd")) == 1)
            throw new PortalAuthenticationException("学校要求修改校园网密码，请在学校认证页处理后重新保存密码");
        if (!PortalJson.NumberIs(PortalJson.Get(checkedAccount, "code"), 0))
            throw new PortalAuthenticationException("学校未通过账号检查，请检查账号密码或在学校认证页完成验证");
        object result = Json("/api/account/login", form);
        if (PortalJson.NumberIs(PortalJson.Get(result, "code"), 2)) throw new PortalAuthenticationException("学校要求验证码，请在学校认证页完成验证");
        if (!PortalJson.NumberIs(PortalJson.Get(result, "code"), 0)) throw new PortalAuthenticationException("校园网认证被拒绝，请检查账号、密码及学校认证页提示；已停止自动重试");
        return WaitOnline(result);
    }

    private bool WaitOnline(object first)
    {
        object result = first;
        while (Remaining() > 0)
        {
            if (Classify(result) == StatusOnline) return false;
            object dialCode = PortalJson.Get(result, "dialCode");
            if (PortalJson.NumberIs(PortalJson.Get(result, "code"), 0) && DialEnabled(result) && PortalJson.Truthy(dialCode) && !PortalJson.StringIs(dialCode, "ok:dialup"))
                throw new PortalAuthenticationException("运营商拨号失败，请在学校认证页检查线路或重拨");
            Thread.Sleep((int)Math.Min(pollMs, Math.Max(1, Remaining())));
            if (Remaining() <= 0) break;
            result = Status();
        }
        throw new PortalException("认证已提交，但未确认拨号成功，请稍后检查状态");
    }
}
