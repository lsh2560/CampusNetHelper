using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using System.Net.NetworkInformation;
using System.Windows.Forms;

internal static class AppLog
{
    public static string[] ReadTail(string path, int maxBytes)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            long start = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                if (start > 0) reader.ReadLine(); // Discard the first potentially partial UTF-8 line.
                return reader.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }
    }

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            using (var mutex = new Mutex(false, RuntimeNames.Name("Log")))
            {
                bool owned = false;
                try
                {
                    try { owned = mutex.WaitOne(3000); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (owned)
                    {
                        if (File.Exists(AppPaths.Log) && new FileInfo(AppPaths.Log).Length > 1048576)
                        { if (File.Exists(AppPaths.Log + ".1")) File.Delete(AppPaths.Log + ".1"); File.Move(AppPaths.Log, AppPaths.Log + ".1"); }
                        File.AppendAllText(AppPaths.Log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine, new UTF8Encoding(false));
                    }
                }
                finally { if (owned) mutex.ReleaseMutex(); }
            }
        }
        catch { /* Logging must not stop network recovery. */ }
    }
}

internal sealed class MonitorStatusRecord
{
    public string state { get; set; }
    public string message { get; set; }
    public long updatedUtc { get; set; }
    public long onlineSinceUtc { get; set; }
}

internal static class MonitorStatusStore
{
    public static void Publish(string state, string message, long onlineSinceUtc)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            var record = new MonitorStatusRecord { state = state, message = message,
                updatedUtc = DateTime.UtcNow.Ticks, onlineSinceUtc = onlineSinceUtc };
            AtomicFile.Write(AppPaths.MonitorStatus, new JavaScriptSerializer().Serialize(record));
        }
        catch (Exception ex) { AppLog.Write("状态文件更新失败：" + ex.Message); }
    }

    public static MonitorStatusRecord Read()
    {
        if (!File.Exists(AppPaths.MonitorStatus)) return null;
        try { return new JavaScriptSerializer().Deserialize<MonitorStatusRecord>(File.ReadAllText(AppPaths.MonitorStatus, Encoding.UTF8)); }
        catch { return null; }
    }
}

internal static class UserActivity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Tick; }
    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder name, uint length, out uint needed);
    public static bool IsSessionLocked()
    {
        IntPtr desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) return true;
        try { uint needed; var name = new StringBuilder(256); return !GetUserObjectInformation(desktop, 2, name, 512, out needed) || !String.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase); }
        finally { CloseDesktop(desktop); }
    }

    public static bool IsIdle()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf(typeof(LastInputInfo)) };
        if (!GetLastInputInfo(ref info)) return true;
        return unchecked((uint)Environment.TickCount - info.Tick) >= 20U * 60U * 1000U;
    }
}

internal sealed class ConnectionMonitor
{
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private readonly EventWaitHandle manualWake = new EventWaitHandle(false, EventResetMode.AutoReset, RuntimeNames.Name("Wake"));
    private readonly EventWaitHandle stopSignal = new EventWaitHandle(false, EventResetMode.AutoReset, RuntimeNames.Name("Stop"));
    private volatile bool stopRequested;
    private DateTime nextCheck = DateTime.MinValue;
    private DateTime cooldownUntil = DateTime.MinValue;
    private DateTime lastProbeAt = DateTime.MinValue;
    private volatile bool locked;
    private volatile bool suspended;
    private int eventVersion;
    private bool awaitingConfirmation;
    private string firstIdentity;
    private string lastCondition = "";
    private bool seenOnline;
    private long onlineSinceUtc;
    private int failures;
    private readonly bool startedAtLogin;

    private ConnectionMonitor(bool startup) { startedAtLogin = startup; }

    public static void NotifyManualLogin()
    {
        try
        {
            using (EventWaitHandle signal = EventWaitHandle.OpenExisting(RuntimeNames.Name("Wake")))
                signal.Set();
        }
        catch { /* The monitor may be disabled or stopping. */ }
    }

    // Closing the main window exits the whole program, including this monitor.
    public static void RequestStop()
    {
        try
        {
            using (EventWaitHandle signal = EventWaitHandle.OpenExisting(RuntimeNames.Name("Stop")))
                signal.Set();
        }
        catch { /* No monitor is running. */ }
    }

    // Started at Windows sign-in, the monitor runs with no window; keep it visible
    // and easy to stop rather than hidden in Task Manager.
    private static NotifyIcon CreateTray(ConnectionMonitor monitor)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开稳连", null, delegate { OpenWindow(); });
        menu.Items.Add("退出自动保持在线", null, delegate
        {
            AppLog.Write("已从托盘退出后台监控");
            monitor.stopRequested = true;
            monitor.wake.Set();
        });
        Icon icon = SystemIcons.Application;
        try { if (File.Exists(AppPaths.IconFile)) icon = new Icon(AppPaths.IconFile); } catch { }
        var tray = new NotifyIcon { Text = "稳连 · 自动保持在线运行中", Icon = icon, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += delegate { OpenWindow(); };
        return tray;
    }

    private static void OpenWindow()
    {
        try { Process.Start(new ProcessStartInfo(AppPaths.Executable) { UseShellExecute = false }).Dispose(); }
        catch (Exception ex) { AppLog.Write("无法打开主界面：" + ex.Message); }
    }

    public static void Run(bool startup)
    {
        using (var singleton = new Mutex(false, RuntimeNames.Name("Monitor")))
        {
            bool owned;
            // Reopening the window right after closing it starts a new monitor while
            // the previous one may still be finishing its stop; wait briefly for it.
            try { owned = singleton.WaitOne(5000); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) return;
            try
            {
                var monitor = new ConnectionMonitor(startup);
                var worker = new Thread(delegate()
                {
                    try { monitor.Loop(); }
                    catch (Exception ex) { AppLog.Write("后台监控异常停止：" + ex.Message); }
                });
                worker.IsBackground = true;
                NotifyIcon tray = null;
                try { if (AppSettings.ParseMode(AppSettings.Load().priorityMode) != PriorityMode.Off) tray = CreateTray(monitor); }
                catch (Exception ex) { AppLog.Write("后台监控托盘图标无法创建：" + ex.Message); }
                using (var timer = new System.Windows.Forms.Timer { Interval = 500 })
                {
                    bool started = false;
                    bool workerStarted = false;
                    timer.Tick += delegate
                    {
                        if (!started)
                        {
                            started = true;
                            try
                            {
                                monitor.Subscribe();
                                worker.Start();
                                workerStarted = true;
                            }
                            catch (Exception ex)
                            {
                                AppLog.Write("无法监听 Windows 会话事件：" + ex.Message);
                                timer.Stop();
                                monitor.Unsubscribe();
                                Application.ExitThread();
                            }
                        }
                        else if (!worker.IsAlive)
                        {
                            timer.Stop();
                            monitor.Unsubscribe();
                            monitor.wake.Dispose();
                            monitor.manualWake.Dispose();
                            Application.ExitThread();
                        }
                        else if (!monitor.stopRequested && monitor.stopSignal.WaitOne(0))
                        {
                            AppLog.Write("主窗口已关闭，后台监控随之退出");
                            monitor.stopRequested = true;
                            monitor.wake.Set();
                        }
                    };
                    timer.Start();
                    try { Application.Run(new ApplicationContext()); }
                    finally { if (tray != null) { tray.Visible = false; tray.Dispose(); } }
                    if (workerStarted) worker.Join();
                }
                monitor.stopSignal.Dispose();
            }
            finally { singleton.ReleaseMutex(); }
        }
    }

    private void Subscribe()
    {
        locked = UserActivity.IsSessionLocked();
        SystemEvents.SessionSwitch += OnSession;
        SystemEvents.PowerModeChanged += OnPower;
        NetworkChange.NetworkAddressChanged += OnNetwork;
        NetworkChange.NetworkAvailabilityChanged += OnNetwork;
    }

    private void Unsubscribe()
    {
        NetworkChange.NetworkAddressChanged -= OnNetwork;
        NetworkChange.NetworkAvailabilityChanged -= OnNetwork;
        SystemEvents.SessionSwitch -= OnSession;
        SystemEvents.PowerModeChanged -= OnPower;
    }

    private void OnSession(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            locked = true;
            Interlocked.Increment(ref eventVersion);
            ScheduleSoon(true);
        }
        if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            locked = false;
            Interlocked.Increment(ref eventVersion);
            ScheduleSoon(true);
        }
    }

    private void OnPower(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            suspended = true;
            Interlocked.Increment(ref eventVersion);
        }
        if (e.Mode == PowerModes.Resume)
        {
            suspended = false;
            locked = UserActivity.IsSessionLocked();
            Interlocked.Increment(ref eventVersion);
            ScheduleSoon(true);
        }
    }

    private void OnNetwork(object sender, EventArgs e)
    {
        Interlocked.Increment(ref eventVersion);
        try { if (AppSettings.Load().quickRecheck != false) ScheduleSoon(false); }
        catch { ScheduleSoon(false); }
    }

    private void ScheduleSoon(bool urgent)
    {
        DateTime soon = DateTime.UtcNow.AddSeconds(4);
        if (!urgent && lastProbeAt != DateTime.MinValue && soon < lastProbeAt.AddSeconds(10))
            soon = lastProbeAt.AddSeconds(10);
        if (soon < cooldownUntil) soon = cooldownUntil;
        if (nextCheck > soon) nextCheck = soon;
        wake.Set();
    }

    private void LogCondition(string value, string message)
    {
        if (lastCondition != value) { AppLog.Write(message); lastCondition = value; }
    }

    private void Loop()
    {
        bool initialStartupCheck = true;
        AppLog.Write("本机优先后台监控已启动");
        MonitorStatusStore.Publish("checking", "后台监控已启动", 0);
        try
        {
            while (true)
            {
                if (stopRequested) return;
                SettingsRecord settings;
                try { settings = AppSettings.Load(); }
                catch (Exception ex)
                {
                    LogCondition("settings", "设置无法读取，暂停主动恢复：" + ex.Message);
                    wake.WaitOne(5000);
                    continue;
                }
                PriorityMode mode = AppSettings.ParseMode(settings.priorityMode);
                if (mode == PriorityMode.Off)
                {
                    if (initialStartupCheck && startedAtLogin && settings.autoLogin) RunLegacyStartup();
                    return;
                }
                initialStartupCheck = false;
                if (DateTime.UtcNow < nextCheck)
                {
                    if (WaitHandle.WaitAny(new WaitHandle[] { wake, manualWake }, 1000) == 1)
                        nextCheck = DateTime.MinValue;
                    continue;
                }
                int version = eventVersion;
                try { Check(settings, mode); }
                catch (Exception ex)
                {
                    awaitingConfirmation = false;
                    onlineSinceUtc = 0;
                    LogCondition("check-error", "本次检测异常，稍后重试：" + ex.Message);
                    MonitorStatusStore.Publish("unknown", "本次网络检测暂不可用", 0);
                    nextCheck = DateTime.UtcNow.AddSeconds(30);
                }
                if (eventVersion != version)
                {
                    try { if (AppSettings.Load().quickRecheck != false) ScheduleSoon(false); }
                    catch { ScheduleSoon(false); }
                }
            }
        }
        finally
        {
            AppLog.Write("本机优先后台监控已停止");
            MonitorStatusStore.Publish("stopped", "后台监控已停止", 0);
        }
    }

    private void Check(SettingsRecord settings, PriorityMode mode)
    {
        int versionAtCheckStart = eventVersion;
        if (suspended)
        {
            awaitingConfirmation = false;
            nextCheck = DateTime.UtcNow.AddSeconds(15);
            return;
        }
        LinkSnapshot link;
        try { link = PortalProbe.Link(); }
        catch (Exception ex)
        {
            LogCondition("link-unknown", "网卡状态无法判断，暂不恢复：" + ex.Message);
            MonitorStatusStore.Publish("unknown", "网卡状态无法判断", 0);
            nextCheck = DateTime.UtcNow.AddSeconds(settings.checkIntervalSeconds ?? 45);
            return;
        }
        if (link.State != LinkState.Ready)
        {
            awaitingConfirmation = false;
            onlineSinceUtc = 0;
            string detail = link.State == LinkState.WifiDisconnected ? "Wi-Fi 已断开" :
                link.State == LinkState.NoIp ? "网卡尚未取得有效 IP 或网关" : "网络连接已断开";
            LogCondition(link.State.ToString(), detail + "，暂不进行认证");
            MonitorStatusStore.Publish("disconnected", detail, 0);
            nextCheck = DateTime.UtcNow.AddSeconds(15);
            return;
        }
        if (mode == PriorityMode.Smart && (locked || UserActivity.IsIdle()))
        {
            awaitingConfirmation = false;
            LogCondition("paused", locked ? "电脑已锁屏，暂停主动恢复" : "电脑已空闲约 20 分钟，暂停主动恢复");
            MonitorStatusStore.Publish("paused", locked ? "电脑已锁屏" : "电脑长时间未操作", 0);
            nextCheck = DateTime.UtcNow.AddSeconds(settings.checkIntervalSeconds ?? 45);
            return;
        }

        lastProbeAt = DateTime.UtcNow;
        PortalState result = PortalProbe.Check();
        if (eventVersion != versionAtCheckStart || PortalProbe.Link().Identity != link.Identity)
        {
            awaitingConfirmation = false;
            onlineSinceUtc = 0;
            LogCondition("network-changed", "检测期间网络状态发生变化，已丢弃旧结果");
            MonitorStatusStore.Publish("checking", "网络已变化，正在重新检测", 0);
            nextCheck = DateTime.UtcNow.AddSeconds(4);
            return;
        }
        if (result == PortalState.Online)
        {
            if (lastCondition != "online") AppLog.Write("校园网认证在线");
            lastCondition = "online";
            seenOnline = true;
            if (onlineSinceUtc == 0) onlineSinceUtc = DateTime.UtcNow.Ticks;
            MonitorStatusStore.Publish("online", "校园网认证在线", onlineSinceUtc);
            awaitingConfirmation = false;
            failures = 0;
            cooldownUntil = DateTime.MinValue;
            nextCheck = DateTime.UtcNow.AddSeconds(settings.checkIntervalSeconds ?? 45);
            return;
        }
        if (result == PortalState.Unknown)
        {
            awaitingConfirmation = false;
            onlineSinceUtc = 0;
            LogCondition("unknown", "校内认证页暂时波动，无法确认认证失效；暂不恢复");
            MonitorStatusStore.Publish("unknown", "认证状态暂无法判断", 0);
            nextCheck = DateTime.UtcNow.AddSeconds(settings.checkIntervalSeconds ?? 45);
            return;
        }
        if (!awaitingConfirmation || firstIdentity != link.Identity)
        {
            awaitingConfirmation = true;
            onlineSinceUtc = 0;
            firstIdentity = link.Identity;
            LogCondition("confirm", "检测到认证页要求登录，5 秒后二次确认");
            MonitorStatusStore.Publish("confirming", "认证页要求登录，正在二次确认", 0);
            nextCheck = DateTime.UtcNow.AddSeconds(5);
            return;
        }
        awaitingConfirmation = false;
        AppLog.Write("二次确认：认证会话失效，可能由其他设备上线或服务器注销导致");
        MonitorStatusStore.Publish("auth-required", "认证会话失效", 0);
        if (eventVersion != versionAtCheckStart)
        {
            LogCondition("network-changed", "检测期间网络状态发生变化，重新确认后再恢复");
            nextCheck = DateTime.UtcNow.AddSeconds(4);
            return;
        }
        if (mode == PriorityMode.Smart && (locked || UserActivity.IsIdle()))
        {
            LogCondition("paused", "电脑已锁屏或空闲，暂停主动恢复");
            nextCheck = DateTime.UtcNow.AddSeconds(settings.checkIntervalSeconds ?? 45);
            return;
        }
        if (!settings.autoLogin && startedAtLogin && !seenOnline)
        {
            LogCondition("unarmed", "开机自动登录已关闭，等待本次使用中建立认证后再进行恢复");
            nextCheck = DateTime.UtcNow.AddSeconds(settings.checkIntervalSeconds ?? 45);
            return;
        }
        if (DateTime.UtcNow < cooldownUntil)
        {
            LogCondition("cooldown", "自动恢复处于退避冷却期");
            nextCheck = cooldownUntil;
            return;
        }
        if (!File.Exists(AppPaths.Secret))
        {
            LogCondition("missing-files", "缺少加密账号，暂停主动恢复");
            nextCheck = DateTime.UtcNow.AddSeconds(60);
            return;
        }
        // One limit for every login (manual or automatic, any process): auth-state.json.
        if (CampusLogin.AtRateLimit())
        {
            EnterRateLimit();
            return;
        }
        AppLog.Write("开始自动恢复校园网认证");
        MonitorStatusStore.Publish("recovering", "正在恢复校园网认证", 0);
        int exitCode = RunLogin(true);
        if (exitCode == CampusLogin.RateLimited)
        {
            EnterRateLimit();
            return;
        }
        if (exitCode == 0)
        {
            AppLog.Write("自动恢复成功，稍后复查认证状态");
            failures = 0;
            cooldownUntil = DateTime.MinValue;
            nextCheck = DateTime.UtcNow.AddSeconds(5);
        }
        else
        {
            int[] delays = { 5, 15, 30, 60 };
            int delay = delays[Math.Min(failures, delays.Length - 1)];
            failures++;
            AppLog.Write("自动恢复失败（退出码 " + exitCode + "），" + delay + " 秒后重新检测");
            cooldownUntil = DateTime.UtcNow.AddSeconds(delay);
            nextCheck = cooldownUntil;
        }
        lastCondition = "recovering";
    }

    private void EnterRateLimit()
    {
        LogCondition("limited", "10 分钟内已主动恢复 3 次，进入频率保护");
        MonitorStatusStore.Publish("limited", "恢复频率已达上限", 0);
        cooldownUntil = DateTime.UtcNow.AddSeconds(60);
        nextCheck = cooldownUntil;
    }

    private static int RunLogin(bool once)
    {
        try { return CampusLogin.Run(once); }
        catch (Exception ex) { AppLog.Write("登录任务运行失败：" + ex.GetType().Name); return CampusLogin.Failed; }
    }

    private static void RunLegacyStartup()
    {
        AppLog.Write("执行开机自动登录（HTTP 认证，不启动浏览器）");
        RunLogin(false);
    }
}
