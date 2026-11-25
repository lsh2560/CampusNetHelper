using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Linq;

internal static class Verify
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [STAThread]
    private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception ex) { Console.WriteLine(ex.GetType().FullName); Console.WriteLine(ex.Message); Console.WriteLine(ex.StackTrace); Environment.ExitCode = 1; }
    }
    private static void Run(string[] args)
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        string data = Path.Combine(output, "isolated-data");
        Directory.CreateDirectory(data);
        foreach (string field in new[] { "Data", "Secret", "Log", "Settings", "RecoveryState", "MonitorStatus", "Startup" })
            typeof(AppPaths).GetField(field).SetValue(null, field == "Data" ? data : Path.Combine(data, field + ".json"));
        CredentialStore.Save("test-account", "test-password");
        string encrypted = File.ReadAllText(AppPaths.Secret);
        CredentialStore.SaveKeepingPassword("test-account");
        bool rejected = false;
        try { CredentialStore.SaveKeepingPassword("different-account"); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected || File.ReadAllText(AppPaths.Secret) != encrypted) throw new Exception("Credential preservation failed");
        using (var window = new DashboardWindow(false))
        {
            window.ShowInTaskbar = false;
            window.StartPosition = FormStartPosition.Manual;
            window.Location = new Point(-20000, -20000);
            window.Show();
            Application.DoEvents();
            IntPtr handle = window.Handle;
            var pageMethod = typeof(DashboardWindow).GetMethod("ShowPage", BindingFlags.NonPublic | BindingFlags.Instance);
            string[] names = DashboardWindow.NavigationPages;
            foreach (int width in new[] { 1500, 1120, 1500 })
            {
                window.Size = new Size(width, 1000);
                for (int i = 0; i < names.Length; i++)
                {
                    pageMethod.Invoke(window, new object[] { names[i] });
                    window.PerformLayout();
                    Application.DoEvents();
                    Panel panel = (Panel)typeof(DashboardWindow).GetField(i == 0 ? "home" : "otherPage", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
                    if (panel.HorizontalScroll.Visible) throw new Exception("Horizontal overflow: " + names[i] + " " + width);
                    foreach (CardPanel card in panel.Controls.OfType<CardPanel>())
                    {
                        if (card.Right > panel.ClientSize.Width) throw new Exception("Card overflow: " + names[i]);
                        foreach (Control control in card.Controls)
                        {
                            // Labels may intentionally ellipsize, but actions and fields must remain fully reachable.
                            if ((control is Button || control is AntdUI.Button || control is TextBox || control is AntdUI.Input || control is ComboBox || control is ModernSwitch) &&
                                (control.Right > card.Width || control.Bottom > card.Height))
                                throw new Exception("Clipped action: " + names[i] + " " + control.Text + " window=" + width + " control=" + control.Bounds + " card=" + card.Bounds);
                        }
                    }
                    using (var bitmap = new Bitmap(window.Width, window.Height))
                    {
                        window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
                        bitmap.Save(Path.Combine(output, width + "-" + i + ".png"));
                    }
                    if (panel.VerticalScroll.Visible)
                    {
                        panel.AutoScrollPosition = new Point(0, panel.AutoScrollMinSize.Height);
                        Application.DoEvents();
                        using (var bitmap = new Bitmap(window.Width, window.Height))
                        {
                            window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
                            bitmap.Save(Path.Combine(output, width + "-" + i + "-bottom.png"));
                        }
                    }
                }
            }
            pageMethod.Invoke(window, new object[] { DashboardWindow.HomePage });
            AppSettings.Save(new SettingsRecord { priorityMode = "Smart", autoLogin = false });
            MonitorStatusStore.Publish("online", "test", 0);
            typeof(DashboardWindow).GetField("currentLinkState", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(window, LinkState.Ready);
            typeof(DashboardWindow).GetField("localPortal", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(window, PortalState.AuthenticationRequired);
            typeof(DashboardWindow).GetField("localObservedAt", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(window, DateTime.Now.AddMilliseconds(1));
            typeof(DashboardWindow).GetMethod("RefreshStatus", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null);
            Label heading = (Label)typeof(DashboardWindow).GetField("statusTitle", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
            if (heading.Text != "需要重新认证") throw new Exception("Fresh manual result was overridden by stale monitor status");
        }
        string tailPath = Path.Combine(data, "tail.log");
        File.WriteAllText(tailPath, new string('x', 100000) + "\r\n完整的中文行\r\n最后一行\r\n", System.Text.Encoding.UTF8);
        string[] tail = AppLog.ReadTail(tailPath, 64);
        if (tail.Length != 2 || tail[0] != "完整的中文行" || tail[1] != "最后一行") throw new Exception("Bounded UTF-8 log tail failed");
        if (!PortalProbe.ResolveHost("127.0.0.1").Any(x => x.Equals(System.Net.IPAddress.Loopback))) throw new Exception("IP literal resolution failed");
        using (var toggle = new ModernSwitch())
        {
            int count = 0;
            toggle.Changed += delegate { count++; };
            typeof(ModernSwitch).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(toggle, new object[] { new KeyEventArgs(Keys.Space) });
            if (!toggle.Value || count != 1) throw new Exception("Keyboard switch failed");
        }
        File.WriteAllText(Path.Combine(output, "verification.txt"), "PASS: 12 page/size layouts (4 pages x 3 widths), including scrolling; no horizontal overflow or clipped actions.\r\nPASS: credential reuse guard, original encrypted record preserved.\r\nPASS: Space key toggles switch once.\r\nPASS: fresh manual result overrides stale monitor status.\r\nPASS: bounded UTF-8 log tail and IP literal resolution.\r\nNo login, monitor or startup changes executed.\r\n");
    }
}
