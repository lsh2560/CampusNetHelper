using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Diagnostics;
using System.Linq;

internal enum PriorityMode { Off, Preferred, Smart }

internal sealed class SettingsRecord
{
    public bool autoLogin { get; set; }
    public string priorityMode { get; set; }
    public bool? quickRecheck { get; set; }
    public int? checkIntervalSeconds { get; set; }
    public string carrier { get; set; }
    public bool minimizeToTray { get; set; }
    public bool reduceAnimations { get; set; }
}

internal static class AppSettings
{
    private static JavaScriptSerializer Json { get { return new JavaScriptSerializer(); } }

    public static SettingsRecord Load()
    {
        if (!File.Exists(AppPaths.Settings))
            return new SettingsRecord { autoLogin = File.Exists(AppPaths.Startup), priorityMode = "Off", quickRecheck = true };
        SettingsRecord value = Json.Deserialize<SettingsRecord>(File.ReadAllText(AppPaths.Settings, Encoding.UTF8));
        if (value == null) throw new InvalidDataException("设置文件为空");
        ParseMode(value.priorityMode);
        if (!value.quickRecheck.HasValue) value.quickRecheck = true;
        if (!value.checkIntervalSeconds.HasValue || value.checkIntervalSeconds < 30 || value.checkIntervalSeconds > 60)
            value.checkIntervalSeconds = 45;
        if (String.IsNullOrEmpty(value.carrier)) value.carrier = "auto";
        if (!new[] { "auto", "campus", "cmcc", "cucc", "ctcc" }.Contains(value.carrier)) throw new InvalidDataException("未知的运营商设置");
        return value;
    }

    public static PriorityMode ParseMode(string value)
    {
        PriorityMode mode;
        if (!Enum.TryParse<PriorityMode>(value, true, out mode) || !Enum.IsDefined(typeof(PriorityMode), mode))
            throw new InvalidDataException("未知的本机优先设置");
        return mode;
    }

    public static void Save(SettingsRecord value)
    {
        ParseMode(value.priorityMode);
        if (!value.checkIntervalSeconds.HasValue || value.checkIntervalSeconds < 30 || value.checkIntervalSeconds > 60)
            value.checkIntervalSeconds = 45;
        Directory.CreateDirectory(AppPaths.Data);
        AtomicFile.Write(AppPaths.Settings, Json.Serialize(value));
    }
}

internal static class StartupManager
{
    public static void Sync(SettingsRecord settings)
    {
        if (!settings.autoLogin && AppSettings.ParseMode(settings.priorityMode) == PriorityMode.Off)
        {
            if (File.Exists(AppPaths.Startup)) File.Delete(AppPaths.Startup);
            return;
        }
        if (!File.Exists(AppPaths.Executable)) throw new FileNotFoundException("找不到后台程序", AppPaths.Executable);
        string executable = AppPaths.Executable.Replace("\"", "\"\"");
        string launcher = "CreateObject(\"WScript.Shell\").Run \"\"\"" + executable + "\"\" --background --startup\", 0, False";
        AtomicFile.Write(AppPaths.Startup, launcher, Encoding.Unicode);
    }

    public static void StartBackground()
    {
        var info = new ProcessStartInfo(AppPaths.Executable, "--background");
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.WindowStyle = ProcessWindowStyle.Hidden;
        Process.Start(info).Dispose();
    }
}
