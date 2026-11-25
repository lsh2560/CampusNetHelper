using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class AppPaths
{
    public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    // Data and startup names are kept from earlier versions so upgrades keep accounts and settings.
    public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CampusEthernetAutoLogin");
    public static readonly string Secret = Path.Combine(Data, "credential.json");
    public static readonly string Log = Path.Combine(Data, "run.log");
    public static readonly string Settings = Path.Combine(Data, "settings.json");
    // Recovery counter of v0.9 and earlier. No longer written: the monitor now shares
    // the login limit in auth-state.json. Kept so isolated tests can still redirect it.
    public static readonly string RecoveryState = Path.Combine(Data, "recovery-state.json");
    public static readonly string MonitorStatus = Path.Combine(Data, "monitor-status.json");
    public static readonly string Startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "CampusEthernetAutoLogin.vbs");
    public static readonly string Executable = Path.Combine(Root, "CampusNetHelper.exe");
    public static readonly string IconFile = Path.Combine(Root, "CampusNetHelper.ico");
    public static readonly string IconPreview = Path.Combine(Root, "CampusNetHelper-icon-preview.png");
    // Follows Data, so tests that redirect Data also isolate the login limit.
    public static string AuthState { get { return Path.Combine(Data, "auth-state.json"); } }
}

internal sealed class SavedCredential
{
    public string username { get; set; }
    public string password { get; set; }
}

internal static class CredentialStore
{
    public static SavedCredential Read()
    {
        if (!File.Exists(AppPaths.Secret)) return null;
        return new JavaScriptSerializer().Deserialize<SavedCredential>(File.ReadAllText(AppPaths.Secret, Encoding.UTF8));
    }

    public static void Save(string username, string password)
    {
        byte[] clear = Encoding.UTF8.GetBytes(password);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser); }
        finally { Array.Clear(clear, 0, clear.Length); }
        Directory.CreateDirectory(AppPaths.Data);
        var record = new SavedCredential { username = username, password = Convert.ToBase64String(encrypted) };
        AtomicFile.Write(AppPaths.Secret, new JavaScriptSerializer().Serialize(record));
    }

    public static void SaveKeepingPassword(string username)
    {
        SavedCredential record = Read();
        if (record == null || String.IsNullOrEmpty(record.password))
            throw new InvalidOperationException("首次保存账号时请填写密码。");
        if (!String.Equals(record.username, username, StringComparison.Ordinal))
            throw new InvalidOperationException("更换账号时请填写该账号的密码，不能沿用原账号密码。");
        record.username = username;
        AtomicFile.Write(AppPaths.Secret, new JavaScriptSerializer().Serialize(record));
    }
}

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [STAThread]
    private static void Main(string[] args)
    {
        // Initialize DPI awareness before WinForms creates any window or reads screen bounds.
        try { SetProcessDPIAware(); } catch { }
        if (args.Length == 1 && args[0] == "--sync-startup")
        {
            try { StartupManager.Sync(AppSettings.Load()); Environment.ExitCode = 0; }
            catch { Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--environment-check")
        {
            try
            {
                EnvironmentCheckReport report = EnvironmentCheck.Run(false);
                File.WriteAllText(args[1], new JavaScriptSerializer().Serialize(report), new UTF8Encoding(false));
                Environment.ExitCode = report.ready ? 0 : 2;
            }
            catch { Environment.ExitCode = 3; }
            return;
        }
        if (args.Length == 1 && args[0] == "--self-check")
        {
            Environment.ExitCode = File.Exists(AppPaths.Secret) ? 0 : 1;
            return;
        }
        if (args.Length >= 1 && args[0] == "--background")
        {
            ConnectionMonitor.Run(args.Length > 1 && args[1] == "--startup");
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new DashboardWindow());
    }
}
