using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class DashboardWindowBehaviorVerify
{
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    private static void Assert(bool condition, string text)
    {
        if (!condition) throw new Exception(text);
        checks++; Console.WriteLine("PASS " + text);
    }
    private static void Pump()
    {
        for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(25); }
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Click(Control control)
    {
        typeof(Control).GetMethod("OnClick", PrivateInstance).Invoke(control, new object[] { EventArgs.Empty });
        Pump();
    }

    [STAThread]
    private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception ex)
        {
            Console.WriteLine(ex.GetType().FullName); Console.WriteLine(ex.Message);
            Console.WriteLine(ex.StackTrace); Environment.ExitCode = 1;
        }
    }

    private static void Run(string[] args)
    {
        string output = Path.GetFullPath(args[0]);
        string data = Path.Combine(output, "isolated-data"); Directory.CreateDirectory(data);
        foreach (string field in new[] { "Data", "Secret", "Log", "Settings", "RecoveryState", "MonitorStatus", "Startup" })
            typeof(AppPaths).GetField(field).SetValue(null, field == "Data" ? data : Path.Combine(data, field + ".json"));
        AppSettings.Save(new SettingsRecord { autoLogin = false, priorityMode = "Off", minimizeToTray = false, carrier = "auto" });
        byte[] initialSettings = File.ReadAllBytes(AppPaths.Settings);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using (var window = new DashboardWindow(false))
        {
            window.ShowInTaskbar = true; window.StartPosition = FormStartPosition.Manual;
            window.Location = new Point(-20000, -20000); window.Size = new Size(1280, 900);
            window.Show(); Pump();
            IntPtr handle = window.Handle;
            int expected = WindowBehavior.Caption | WindowBehavior.ThickFrame | WindowBehavior.SystemMenu |
                WindowBehavior.MinimizeBox | WindowBehavior.MaximizeBox;
            Assert((GetWindowLong(handle, -16) & expected) == expected, "Dashboard retains native taskbar window capabilities");
            Assert(window.ClientSize == window.Size, "Dashboard uses a complete custom client rectangle");
            Assert(!((System.Windows.Forms.Timer)typeof(DashboardWindow).GetField("liveTimer", PrivateInstance).GetValue(window)).Enabled,
                "Dashboard(false) has no live network timer");

            SendMessage(handle, WindowBehavior.SystemCommand, new IntPtr(WindowBehavior.MinimizeCommand), IntPtr.Zero); Pump();
            Assert(IsIconic(handle) && window.WindowState == FormWindowState.Minimized, "Dashboard accepts native SC_MINIMIZE");
            typeof(DashboardWindow).GetMethod("ShowFromTray", PrivateInstance).Invoke(window, null); Pump();
            Assert(!IsIconic(handle) && window.WindowState == FormWindowState.Normal, "Dashboard tray restoration restores a minimized window");

            Label minimize = Descendants(window).OfType<Label>().Single(item => item.Text == "−");
            Label maximize = Descendants(window).OfType<Label>().Single(item => item.Text == "□");
            Label close = Descendants(window).OfType<Label>().Single(item => item.Text == "×");
            Click(maximize);
            Assert(window.WindowState == FormWindowState.Maximized, "Dashboard custom maximize caption button works");
            Click(minimize);
            Assert(IsIconic(handle), "Dashboard custom minimize caption button works when maximized");
            typeof(DashboardWindow).GetMethod("ShowFromTray", PrivateInstance).Invoke(window, null); Pump();
            Assert(window.WindowState == FormWindowState.Maximized, "Dashboard restore keeps the previous maximized state");
            Click(maximize);
            Assert(window.WindowState == FormWindowState.Normal, "Dashboard custom maximize button restores normal bounds");

            SendMessage(handle, 0x00A3, new IntPtr(2), IntPtr.Zero); Pump();
            Assert(window.WindowState == FormWindowState.Maximized, "Dashboard title bar double-click maximizes");
            SendMessage(handle, 0x00A3, new IntPtr(2), IntPtr.Zero); Pump();
            Assert(window.WindowState == FormWindowState.Normal, "Dashboard title bar double-click restores");
            window.Hide();
            typeof(DashboardWindow).GetMethod("ShowFromTray", PrivateInstance).Invoke(window, null); Pump();
            Assert(window.Visible && !IsIconic(handle), "Dashboard hidden-window restoration shows the interface");

            using (var bitmap = new Bitmap(window.Width, window.Height))
            {
                window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
                Assert(bitmap.GetPixel(500, 10).ToArgb() == UiColor.Canvas.ToArgb(), "Dashboard rendering has no legacy second caption");
                bitmap.Save(Path.Combine(output, "dashboard-window-behavior.png"));
            }
            Click(close);
            Assert(window.IsDisposed, "Dashboard custom close button closes with tray mode disabled");
        }
        Assert(initialSettings.SequenceEqual(File.ReadAllBytes(AppPaths.Settings)), "Dashboard window messages leave settings unchanged");
        Assert(!File.Exists(AppPaths.Secret) && !File.Exists(AppPaths.Startup), "no credential or startup file was created");
        File.WriteAllText(Path.Combine(output, "dashboard-window-behavior.txt"),
            "PASS " + checks + " actual DashboardWindow(false) integration checks. Isolated settings; no real taskbar clicks or network configuration writes.");
        Console.WriteLine("CHECKS " + checks);
    }
}
