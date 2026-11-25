using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class AuthStateRecord
{
    public string fingerprint { get; set; }
    public List<long> attempts { get; set; }
    public bool blocked { get; set; }
}

// One login task with the existing safeguards (formerly Campus-AutoLogin.ps1):
// one task at a time per Windows user, at most 3 submissions per 10 minutes across
// all processes, and a school rejection blocks automatic submission until the
// saved account, password or carrier changes. auth-state.json keeps its format,
// so a block or attempt history from an earlier version still applies.
internal static class CampusLogin
{
    internal const int Success = 0, Failed = 1, NeedsPerson = 2, Busy = 3, RateLimited = 4;
    private const long Window = 6000000000L; // 10 minutes in ticks
    private const int MaxAttemptsPerWindow = 3;

    internal static int Run(bool once)
    {
        return Run(once, delegate(string username, string password, string carrier)
        {
            return new CampusPortalClient().Login(username, password, carrier);
        }, Thread.Sleep);
    }

    // login returns true when the device was already online.
    internal static int Run(bool once, Func<string, string, string, bool> login, Action<int> sleep)
    {
        using (var mutex = new Mutex(false, RuntimeNames.Name("Login")))
        {
            bool taken = false;
            try
            {
                try { taken = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { taken = true; }
                if (!taken) { AppLog.Write("已有登录任务正在运行，本次请求已跳过"); return Busy; }
                return RunExclusive(once, login, sleep);
            }
            catch (Exception ex)
            {
                // Never include credentials in diagnostics.
                AppLog.Write("登录任务异常，已停止；请检查本地配置和网络。错误类型：" + ex.GetType().Name);
                return Failed;
            }
            finally { if (taken) mutex.ReleaseMutex(); }
        }
    }

    private static int RunExclusive(bool once, Func<string, string, string, bool> login, Action<int> sleep)
    {
        if (!File.Exists(AppPaths.Secret)) { AppLog.Write("找不到加密账号配置，请重新保存账号。"); return NeedsPerson; }
        SavedCredential credential = CredentialStore.Read();
        if (credential == null) throw new InvalidDataException("账号配置为空");
        string carrier = ReadCarrier();
        AuthStateRecord state = CurrentState(carrier);
        if (state.blocked)
        {
            AppLog.Write("此前认证被学校拒绝，已暂停自动提交；请检查学校认证页并重新保存账号密码或运营商。");
            return NeedsPerson;
        }
        string password = Decrypt(credential.password);
        string username = credential.username ?? "";
        int maxAttempts = once ? 1 : 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            long now = DateTime.UtcNow.Ticks;
            state.attempts = Recent(state.attempts, now);
            if (state.attempts.Count >= MaxAttemptsPerWindow)
            {
                AppLog.Write("10 分钟内认证尝试达到 3 次，已暂停重试；稍后自动恢复。");
                return RateLimited;
            }
            state.attempts.Add(now);
            SaveState(state);
            try
            {
                bool alreadyOnline = login(username, password, carrier);
                AppLog.Write(alreadyOnline ? "校园网已经登录，无需重复提交" : "校园网登录成功（认证及拨号已确认）");
                return Success;
            }
            catch (PortalAuthenticationException ex)
            {
                AppLog.Write(ex.Message);
                state.blocked = true;
                SaveState(state);
                AppLog.Write("认证需要人工处理，已停止自动重试。");
                return NeedsPerson;
            }
            catch (PortalException ex) { AppLog.Write(ex.Message); }
            catch (Exception ex) { AppLog.Write("校园网认证组件异常（" + ex.GetType().Name + "），本次未完成"); }
            if (attempt < maxAttempts) sleep(15000 * attempt);
        }
        AppLog.Write("网络暂不可用，有限次数重试结束；请查看学校认证页或运行日志。");
        return Failed;
    }

    // True when a submission now would exceed the shared limit; lets the monitor
    // back off quietly before starting a recovery.
    internal static bool AtRateLimit()
    {
        try
        {
            if (!File.Exists(AppPaths.Secret)) return false;
            AuthStateRecord state = CurrentState(ReadCarrier());
            return Recent(state.attempts, DateTime.UtcNow.Ticks).Count >= MaxAttemptsPerWindow;
        }
        catch { return false; }
    }

    private static List<long> Recent(List<long> attempts, long now)
    {
        return (attempts ?? new List<long>()).Where(t => t > now - Window && t <= now).ToList();
    }

    private static AuthStateRecord CurrentState(string carrier)
    {
        string fingerprint = Fingerprint(carrier);
        var state = new AuthStateRecord { fingerprint = fingerprint, attempts = new List<long>(), blocked = false };
        AuthStateRecord previous = ReadState();
        if (previous != null && previous.fingerprint == fingerprint)
        {
            state.attempts = previous.attempts ?? new List<long>();
            state.blocked = previous.blocked;
        }
        return state;
    }

    // Same value the PowerShell runner stored: SHA-256 of the encrypted credential file plus the carrier.
    private static string Fingerprint(string carrier)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(AppPaths.Secret))) + ":" + carrier;
    }

    private static string ReadCarrier()
    {
        if (!File.Exists(AppPaths.Settings)) return "auto";
        string text = File.ReadAllText(AppPaths.Settings, Encoding.UTF8);
        // An empty file or "null" reads as no settings, as with PowerShell's ConvertFrom-Json.
        if (String.IsNullOrWhiteSpace(text)) return "auto";
        object settings = new JavaScriptSerializer().DeserializeObject(text);
        object value = PortalJson.Get(settings, "carrier");
        string carrier = value == null ? "" : Convert.ToString(value);
        if (new[] { "auto", "campus", "cmcc", "cucc", "ctcc" }.Contains(carrier)) return carrier;
        if (carrier.Length > 0) throw new InvalidDataException("运营商设置无效，请重新保存设置。");
        return "auto";
    }

    // Reads both this program's output and files written by the earlier PowerShell runner.
    private static AuthStateRecord ReadState()
    {
        if (!File.Exists(AppPaths.AuthState)) return null;
        string text = File.ReadAllText(AppPaths.AuthState, Encoding.UTF8);
        if (String.IsNullOrWhiteSpace(text)) return null;
        // Unreadable JSON stops the task (as before); valid JSON that is not an object
        // ("null", a list) has no fingerprint and starts a fresh record, as PowerShell did.
        object value = new JavaScriptSerializer().DeserializeObject(text);
        if (!(value is IDictionary<string, object>)) return null;
        object attempts = PortalJson.Get(value, "attempts");
        if (attempts is IDictionary<string, object>) attempts = PortalJson.Get(attempts, "value");
        var times = new List<long>();
        var list = attempts as IEnumerable;
        if (list != null && !(attempts is string))
            foreach (object item in list)
                if (item is int || item is long || item is decimal || item is double) times.Add(Convert.ToInt64(item));
        object blocked = PortalJson.Get(value, "blocked");
        return new AuthStateRecord
        {
            fingerprint = PortalJson.Get(value, "fingerprint") as string,
            attempts = times,
            blocked = blocked is bool && (bool)blocked,
        };
    }

    private static void SaveState(AuthStateRecord state)
    {
        Directory.CreateDirectory(AppPaths.Data);
        AtomicFile.Write(AppPaths.AuthState, new JavaScriptSerializer().Serialize(state));
    }

    private static string Decrypt(string protectedPassword)
    {
        byte[] encrypted = Convert.FromBase64String(protectedPassword ?? "");
        byte[] clear = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(clear); }
        finally { Array.Clear(clear, 0, clear.Length); }
    }
}
