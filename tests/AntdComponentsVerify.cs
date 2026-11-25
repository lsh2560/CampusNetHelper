using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class AntdComponentsVerify
{
    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static int checks;
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static object Get(object target, string field) { return target.GetType().GetField(field, Private).GetValue(target); }
    private static void Call(object target, string name, params object[] args) { target.GetType().GetMethod(name, Private).Invoke(target, args); }
    private static IEnumerable<Control> All(Control parent)
    {
        foreach (Control control in parent.Controls) { yield return control; foreach (Control item in All(control)) yield return item; }
    }
    private static void Pump() { for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(35); } }
    [STAThread] private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception ex) { Console.WriteLine(ex.GetType().FullName); Console.WriteLine(ex.Message); Console.WriteLine(ex.StackTrace); Environment.ExitCode = 1; }
    }
    private static void Run(string[] args)
    {
        string folder = Path.GetFullPath(args[0]); Directory.CreateDirectory(folder);
        foreach (string field in new[] { "Data", "Secret", "Log", "Settings", "RecoveryState", "MonitorStatus", "Startup" })
            typeof(AppPaths).GetField(field).SetValue(null, field == "Data" ? folder : Path.Combine(folder, field + ".json"));
        AppSettings.Save(new SettingsRecord { autoLogin = false, priorityMode = "Off", minimizeToTray = false });
        byte[] settings = File.ReadAllBytes(AppPaths.Settings);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using (var window = new DashboardWindow(false))
        {
            window.ShowInTaskbar = false; window.StartPosition = FormStartPosition.Manual; window.Location = new Point(-20000, -20000);
            window.Size = new Size(1500, 1000); window.Show(); Pump();
            using (var action = new FlatAction { Name = "AccessibleProbe", Text = "可访问操作", Size = new Size(150, 40) })
            {
                window.Controls.Add(action); action.BringToFront(); Pump();
                int clicks = 0, clickThread = 0, uiThread = Thread.CurrentThread.ManagedThreadId;
                action.Click += delegate { clicks++; clickThread = Thread.CurrentThread.ManagedThreadId; };
                AccessibleObject accessible = action.AccessibilityObject;
                Assert(accessible.DefaultAction == "按下" && accessible.Role == AccessibleRole.PushButton, "accessible action exposes button semantics");
                typeof(AntdUI.Button).GetProperty("ExtraMouseHover", Private | BindingFlags.Public).SetValue(action, true, null); Pump();
                using (var hover = new Bitmap(action.Width, action.Height))
                {
                    action.DrawToBitmap(hover, new Rectangle(Point.Empty, action.Size));
                    int darkTextPixels = 0;
                    for (int y = 8; y < hover.Height - 8; y++) for (int x = 12; x < hover.Width - 12; x++)
                    { Color pixel = hover.GetPixel(x, y); if (pixel.R < 140 && pixel.G < 150 && pixel.B < 180) darkTextPixels++; }
                    Assert(darkTextPixels > 30, "white button hover retains readable text pixels");
                    hover.Save(Path.Combine(folder, "button-hover.png"));
                }
                typeof(AntdUI.Button).GetProperty("ExtraMouseHover", Private | BindingFlags.Public).SetValue(action, false, null); Pump();
                typeof(AntdUI.Button).GetProperty("ExtraMouseDown", Private | BindingFlags.Public).SetValue(action, true, null); Pump();
                using (var pressed = new Bitmap(action.Width, action.Height))
                {
                    action.DrawToBitmap(pressed, new Rectangle(Point.Empty, action.Size));
                    int darkTextPixels = 0;
                    for (int y = 8; y < pressed.Height - 8; y++) for (int x = 12; x < pressed.Width - 12; x++)
                    { Color pixel = pressed.GetPixel(x, y); if (pixel.R < 140 && pixel.G < 150 && pixel.B < 180) darkTextPixels++; }
                    Assert(darkTextPixels > 30, "white button pressed state retains readable text pixels");
                    pressed.Save(Path.Combine(folder, "button-pressed.png"));
                }
                typeof(AntdUI.Button).GetProperty("ExtraMouseDown", Private | BindingFlags.Public).SetValue(action, false, null);
                Exception failure = null;
                var worker = new Thread(delegate() { try { accessible.DoDefaultAction(); } catch (Exception ex) { failure = ex; } });
                worker.Start(); worker.Join(); Pump();
                Assert(failure == null && clicks == 1 && clickThread == uiThread, "cross-thread accessible action dispatches exactly one Click on the UI thread");
                accessible.DoDefaultAction(); action.Enabled = false; Pump();
                Assert(clicks == 1, "a queued action rechecks disabled state before dispatch");
                accessible.DoDefaultAction(); Pump();
                Assert(clicks == 1, "a disabled action cannot fire Click");
                action.Enabled = true; action.Hide(); accessible.DoDefaultAction(); Pump();
                Assert(clicks == 1, "a hidden action cannot fire Click");
                action.Show(); ((IButtonControl)action).PerformClick(); Pump();
                Assert(clicks == 2, "ordinary AntdUI button activation still fires one Click");
                action.Dispose(); accessible.DoDefaultAction(); Pump();
                Assert(clicks == 2, "a stale accessible object cannot activate a disposed button");
            }
            var menu = (AntdUI.Menu)Get(window, "navigation");
            Assert(menu.Items.Count == 4 && menu.Items.All(i => i.Visible), "four AntdUI navigation items are present");
            menu.SelectIndex(3, false); Pump();
            Assert((string)Get(window, "currentPage") == DashboardWindow.SettingsPage, "AntdUI navigation dispatches to the requested page without recursion");
            Call(window, "ShowPage", DashboardWindow.ToolsPage); Pump();
            Assert((string)Get(window, "currentPage") == DashboardWindow.ToolsPage && menu.Items[3].Select && !menu.Items[0].Select, "advanced tools open from Settings and keep Settings selected");
            var view = (SpeedupView)Get(window, "speedup");
            var tabs = (AntdUI.Tabs)Get(view, "tabs");
            var mirrors = (UiResultTable)Get(view, "mirrors");
            var streams = (UiResultTable)Get(view, "streams");
            Assert(mirrors is AntdUI.Table && streams is AntdUI.Table && mirrors.Rows.Count == 6 && streams.Rows.Count == 8, "native AntdUI tables retain all six servers and eight connection options");
            mirrors.Rows[2].Cells[1].Value = "12.34";
            Assert(Convert.ToString(((DataTable)mirrors.DataSource).Rows[2]["value1"]) == "12.34", "incremental result updates reach the native table data source");
            ((AntdUI.Table)mirrors).SetSelected(((DataTable)mirrors.DataSource).Rows[2]); Pump();
            Assert(mirrors.CurrentRow.Index == 2 && ((ComboBox)Get(view, "provider")).SelectedIndex == 2, "native table selection updates the selected mirror provider");
            Call(view, "UseMirror", false); Pump();
            var testUrl = (AntdUI.Input)Get(view, "testUrl");
            Assert(tabs.SelectedIndex == 1 && testUrl.Text.StartsWith(MirrorSite.All[2].Base, StringComparison.Ordinal), "selected mirror URL reaches connection testing");
            ((AntdUI.Table)streams).SetSelected(((DataTable)streams.DataSource).Rows[3]); Pump();
            Assert(streams.CurrentRow.Index == 3, "native connection table selection maps to four connections");
            var choose = All(view).OfType<FlatAction>().Single(b => b.Text == "将选中连接数用于下载");
            ((IButtonControl)choose).PerformClick(); Pump();
            Assert(tabs.SelectedIndex == 2 && ((ComboBox)Get(view, "connections")).SelectedIndex == 3, "AntdUI action carries the chosen connection count to downloading");
            Assert(((AntdUI.Input)Get(view, "downloadUrl")).Text == testUrl.Text, "download URL is preserved during parameter transfer");
            var savedPath = (AntdUI.Input)Get(view, "filePath");
            Assert(savedPath.ReadOnly, "destination field stays read-only");
            var pending = new TaskCompletionSource<bool>();
            Call(view, "Work", "isolated pending task", true, new Func<CancellationToken, Task>(token => pending.Task));
            Assert(!testUrl.Enabled && !((AntdUI.Input)Get(view, "downloadUrl")).Enabled, "active work locks AntdUI input fields");
            pending.SetResult(true); Pump();
            Assert(testUrl.Enabled && savedPath.ReadOnly && !(bool)Get(view, "running"), "completion restores input access without weakening the read-only field");
            Assert(All(window).OfType<AntdUI.Button>().Count() >= 14 && All(window).OfType<AntdUI.Switch>().Count() >= 3, "dashboard and tool actions use real AntdUI controls");
            Assert(tabs.Pages.Count == 4 && tabs.Pages.All(p => !String.IsNullOrWhiteSpace(p.Text)), "four native AntdUI pages retain full labels");
            window.Close();
        }
        Assert(settings.SequenceEqual(File.ReadAllBytes(AppPaths.Settings)), "UI selection and navigation leave saved settings unchanged");
        Assert(!File.Exists(AppPaths.Secret) && !File.Exists(AppPaths.Startup), "component checks create no account or startup files");
        File.WriteAllText(Path.Combine(folder, "antd-components.txt"), "PASS " + checks + " AntdUI component integration checks.");
        Console.WriteLine("CHECKS " + checks);
    }
}
