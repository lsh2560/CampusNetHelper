using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class CaptionCaptureVerify
{
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    private static int checks;
    private static readonly List<string> results = new List<string>();
    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
        checks++; results.Add("PASS " + message); Console.WriteLine("PASS " + message);
    }
    private static void Pump()
    {
        for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(20); }
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            yield return control;
            foreach (Control child in Descendants(control)) yield return child;
        }
    }
    private static void CapturedCommand(Form window, Control caption, Action command,
        FormWindowState before, FormWindowState after, string name)
    {
        Assert(window.WindowState == before, name + ": initial state");
        caption.Capture = true;
        Assert(caption.Capture && GetCapture() == caption.Handle, name + ": caption owns mouse capture");
        command();
        // This is the state during WinForms' Click callback, before WmMouseUp's
        // finally block releases capture. Do not pump the queued command here.
        Assert(window.WindowState == before, name + ": command is queued, not executed inside captured Click");
        caption.Capture = false;
        Assert(GetCapture() != caption.Handle, name + ": capture released before message dispatch");
        Pump();
        Assert(window.WindowState == after, name + ": queued command takes effect after mouse-up");
    }
    [STAThread] private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception ex) { Console.WriteLine(ex.GetType().FullName); Console.WriteLine(ex.Message); Console.WriteLine(ex.StackTrace); Environment.ExitCode = 1; }
    }
    private static void Run(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        string data = Path.Combine(output, "isolated-data"); Directory.CreateDirectory(data);
        foreach (string field in new[] { "Data", "Secret", "Log", "Settings", "RecoveryState", "MonitorStatus", "Startup" })
            typeof(AppPaths).GetField(field).SetValue(null, field == "Data" ? data : Path.Combine(data, field + ".json"));
        AppSettings.Save(new SettingsRecord { autoLogin = false, priorityMode = "Off", minimizeToTray = false, carrier = "auto" });
        byte[] settings = File.ReadAllBytes(AppPaths.Settings);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using (var window = new DashboardWindow(false))
        {
            window.ShowInTaskbar = false; window.StartPosition = FormStartPosition.Manual;
            window.Location = new Point(-20000, -20000); window.Size = new Size(1280, 900);
            window.Show(); Pump();
            Control minimize = Descendants(window).Single(c => c.Text == "−");
            Control maximize = Descendants(window).Single(c => c.Text == "□");
            CapturedCommand(window, maximize, delegate { WindowBehavior.ToggleMaximize(window); },
                FormWindowState.Normal, FormWindowState.Maximized, "normal to maximized");
            CapturedCommand(window, maximize, delegate { WindowBehavior.ToggleMaximize(window); },
                FormWindowState.Maximized, FormWindowState.Normal, "maximized to normal");
            CapturedCommand(window, minimize, delegate { WindowBehavior.Minimize(window); },
                FormWindowState.Normal, FormWindowState.Minimized, "normal to minimized");
            Assert(IsIconic(window.Handle), "normal minimize makes the native window iconic");
            WindowBehavior.RestoreAndActivate(window); Pump();
            Assert(window.WindowState == FormWindowState.Normal && !IsIconic(window.Handle), "restore after normal minimize returns normal");
            CapturedCommand(window, maximize, delegate { WindowBehavior.ToggleMaximize(window); },
                FormWindowState.Normal, FormWindowState.Maximized, "second maximize");
            CapturedCommand(window, minimize, delegate { WindowBehavior.Minimize(window); },
                FormWindowState.Maximized, FormWindowState.Minimized, "maximized to minimized");
            Assert(IsIconic(window.Handle), "maximized minimize makes the native window iconic");
            WindowBehavior.RestoreAndActivate(window); Pump();
            Assert(window.WindowState == FormWindowState.Maximized && !IsIconic(window.Handle), "restore after maximized minimize remembers maximized state");
            CapturedCommand(window, maximize, delegate { WindowBehavior.ToggleMaximize(window); },
                FormWindowState.Maximized, FormWindowState.Normal, "final restore via maximize caption");
            window.Close();
        }
        Assert(settings.SequenceEqual(File.ReadAllBytes(AppPaths.Settings)), "capture regression leaves isolated settings unchanged");
        Assert(!File.Exists(AppPaths.Secret) && !File.Exists(AppPaths.Startup), "capture regression creates no credential or startup file");
        results.Add("CHECKS " + checks);
        File.WriteAllLines(Path.Combine(output, "caption-capture-verification.txt"), results);
        Console.WriteLine("CHECKS " + checks);
    }
}
