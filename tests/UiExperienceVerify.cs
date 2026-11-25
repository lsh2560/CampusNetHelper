using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class UiExperienceVerify
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> results = new List<object>();
    private static readonly List<object> samples = new List<object>();
    private static readonly string[] pages = DashboardWindow.NavigationPages.Concat(new[] { DashboardWindow.ToolsPage }).ToArray();
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int kind);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    private static T Field<T>(DashboardWindow w, string name) { return (T)typeof(DashboardWindow).GetField(name, Private).GetValue(w); }
    private static void Page(DashboardWindow w, string page) { typeof(DashboardWindow).GetMethod("ShowPage", Private).Invoke(w, new object[] { page }); }
    private static IEnumerable<Control> All(Control root) { foreach (Control c in root.Controls) { yield return c; foreach (Control d in All(c)) yield return d; } }
    private static void Call(Control c, string method, object arg) { c.GetType().GetMethod(method, Private).Invoke(c, new[] { arg }); }
    private static void Pump(int ms) { var watch = Stopwatch.StartNew(); do { Application.DoEvents(); Thread.Sleep(5); } while (watch.ElapsedMilliseconds < ms); }
    private static void Check(bool ok, string name) { results.Add(new { name = name, status = ok ? "PASS" : "FAIL" }); if (!ok) throw new Exception(name); }
    private static void Capture(Control c, string path) { using (var bmp = new Bitmap(c.Width, c.Height)) { c.DrawToBitmap(bmp, new Rectangle(Point.Empty, c.Size)); bmp.Save(path); } }
    private static Color Pixel(Control c) { using (var bmp = new Bitmap(c.Width, c.Height)) { c.DrawToBitmap(bmp, new Rectangle(Point.Empty, c.Size)); return bmp.GetPixel(18, 12); } }

    [STAThread] private static void Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        try { Run(args, output); }
        catch (Exception ex) { results.Add(new { name = "Exception", status = "FAIL", detail = ex.ToString() }); Console.WriteLine(ex); Environment.ExitCode = 1; }
        finally { File.WriteAllText(Path.Combine(output, "experience-results.json"), new JavaScriptSerializer().Serialize(new { results = results, samples = samples })); }
    }
    private static void Run(string[] args, string output)
    {
        SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        string data = Path.Combine(output, "isolated-data"); Directory.CreateDirectory(data);
        foreach (string name in new[] { "Data", "Secret", "Settings", "Log", "Startup", "RecoveryState", "MonitorStatus" })
            typeof(AppPaths).GetField(name).SetValue(null, name == "Data" ? data : Path.Combine(data, name + ".json"));
        AppSettings.Save(new SettingsRecord { priorityMode = "Off", autoLogin = false, quickRecheck = true, carrier = "auto", checkIntervalSeconds = 45 });
        byte[] initial = File.ReadAllBytes(AppPaths.Settings);
        var watch = Stopwatch.StartNew();
        using (var w = new DashboardWindow(false))
        {
            w.Text = "稳连 交互验证（独立数据）";
            w.StartPosition = FormStartPosition.Manual; w.Location = new Point(40, 30); w.Size = new Size(1500, 1000);
            if (!args.Contains("--ui-host")) w.Location = new Point(-20000, -20000);
            Check(w.Opacity == 0, "Initial window remains unexposed until the complete first frame is prepared");
            w.Show(); Pump(50); watch.Stop();
            if (args.Contains("--ui-host")) { RunHost(w, output, args.Contains("--presentation-host")); return; }
            Check((GetWindowLong(w.Handle, -20) & 0x02000000) == 0, "Window uses local buffering without global native-dropdown compositing");
            Check(w.Opacity == 1, "First-frame readiness gate releases to full opacity without a fade");
            samples.Add(new { metric = "constructor + Show + 50ms pump", milliseconds = watch.Elapsed.TotalMilliseconds });
            Page(w, DashboardWindow.SettingsPage); Pump(100);
            var account = All(w).Single(c => c.Name == "AccountUser");
            var password = (AntdUI.Input)All(w).Single(c => c.Name == "AccountPassword");
            account.Text = "fictional-unsaved-account"; password.Text = "fictional-unsaved-password";
            Page(w, DashboardWindow.HomePage); Page(w, DashboardWindow.SettingsPage); Pump(70);
            Check(Object.ReferenceEquals(account, All(w).Single(c => c.Name == "AccountUser")) && account.Text == "fictional-unsaved-account",
                "Account controls and non-secret draft survive navigation");
            Check(password.Text == "", "Navigating away clears the unsaved password from the cached page");
            Page(w, DashboardWindow.GamePage); Pump(70); var record = Field<CampusStabilityView>(w, "stabilityView");
            Page(w, DashboardWindow.SettingsPage); Pump(70); Panel settings = Field<Panel>(w, "otherPage");
            settings.AutoScrollPosition = new Point(0, 350); Pump(20); Point scrolled = settings.AutoScrollPosition;
            Rectangle[] oldPageBounds = settings.Controls.OfType<CardPanel>().Select(c => c.Bounds).ToArray();
            Page(w, DashboardWindow.HomePage);
            Check(oldPageBounds.SequenceEqual(settings.Controls.OfType<CardPanel>().Select(c => c.Bounds)), "Navigating home never reflows the previous page with home layout rules");
            Check(!settings.Enabled && !settings.Parent.ClientRectangle.IntersectsWith(settings.Bounds), "Inactive cached page is disabled and outside the viewport");
            Page(w, DashboardWindow.SettingsPage); Pump(70);
            Check(Object.ReferenceEquals(settings, Field<Panel>(w, "otherPage")) && scrolled == settings.AutoScrollPosition, "Settings page and scroll position survive navigation");
            var quick = (CheckBox)All(settings).Single(c => c.Name == "SettingsQuickRecheck");
            var focusedStrategy = All(settings).Single(c => c.Name == "SettingsInterval");
            ((ComboBox)focusedStrategy).SelectedIndex = 2;
            quick.Checked = false; Pump(50);
            Check(!quick.IsDisposed && Object.ReferenceEquals(settings, Field<Panel>(w, "otherPage")) && AppSettings.Load().quickRecheck == false &&
                AppSettings.Load().checkIntervalSeconds == 60 && ((ComboBox)focusedStrategy).SelectedIndex == 2,
                "Settings save inline without rebuilding the page or resetting another choice");
            quick.Checked = true; ((ComboBox)focusedStrategy).SelectedIndex = 1; Pump(50);
            var reduced = (ModernSwitch)All(settings).Single(c => c.Name == "Settings_Animation");
            reduced.Value = true; Check(UiMotion.Reduced && !AntdUI.Config.Animation, "Reduced motion preference applies to shared and AntdUI transitions");
            reduced.Value = false; Pump(250);
            Page(w, DashboardWindow.HomePage); Pump(180);
            FlatAction button = Field<FlatAction>(w, "gameButton");
            Color normal = Pixel(button); Capture(button, Path.Combine(output, "button-normal.png"));
            Call(button, "OnMouseEnter", EventArgs.Empty); Pump(35); Color intermediate = Pixel(button); Pump(170); Color hovered = Pixel(button);
            Capture(button, Path.Combine(output, "button-hover.png"));
            Check(!UiMotion.Enabled || intermediate != normal && intermediate != hovered, "Hover passes through an intermediate visual state");
            Check(hovered != normal || !UiMotion.Enabled, "Hover has a distinct tinted surface");
            // Use a dedicated harmless action to verify activation exactly once.
            using (var probe = new FlatAction { Text = "测试操作", Bounds = new Rectangle(300, 70, 150, 44) })
            {
                w.Controls.Add(probe); probe.BringToFront(); int clicks = 0; probe.Click += delegate { clicks++; };
                Call(probe, "OnMouseEnter", EventArgs.Empty); Pump(150);
                Call(probe, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 20, 20, 0)); Pump(200);
                Capture(probe, Path.Combine(output, "button-pressed.png"));
                Check(UiMotion.ActiveCount == 0, "Short press transition settles without a perpetual timer");
                Call(probe, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 20, 20, 0)); Pump(150);
                Check(clicks == 1, "Mouse release emits exactly one Click");
                ((IButtonControl)probe).PerformClick(); Pump(170); Check(clicks == 2, "Programmatic button activation emits exactly one additional Click");
                probe.Focus(); Capture(probe, Path.Combine(output, "button-focus.png"));
                SendMessage(probe.Handle, 0x0100, new IntPtr((int)Keys.Space), IntPtr.Zero);
                Pump(80);
                SendMessage(probe.Handle, 0x0101, new IntPtr((int)Keys.Space), IntPtr.Zero); Pump(170);
                Check(clicks == 3, "Space key activates a focused button exactly once");
                Call(probe, "OnMouseLeave", EventArgs.Empty); Pump(30); Call(probe, "OnMouseEnter", EventArgs.Empty); Pump(170);
                Check(UiMotion.ActiveCount == 0, "Rapidly reversed hover settles without an animation backlog");
                UiMotion.Configure(true); Call(probe, "OnMouseLeave", EventArgs.Empty);
                Check(UiMotion.ActiveCount == 0, "Reduced motion settles immediately"); UiMotion.Configure(false);
                Call(probe, "OnMouseEnter", EventArgs.Empty); probe.Hide(); Pump(35);
                Check(UiMotion.ActiveCount == 0, "Hidden controls release shared animation work");
            }
            Call(button, "OnMouseLeave", EventArgs.Empty); Pump(150);
            Call(button, "OnMouseEnter", EventArgs.Empty);
            SendMessage(w.Handle, WindowBehavior.SystemCommand, new IntPtr(WindowBehavior.MinimizeCommand), IntPtr.Zero); Pump(50);
            Check(UiMotion.ActiveCount == 0, "Minimized window releases shared animation work");
            Check(w.Opacity == 1, "Minimized window retains its opaque surface for native restoration animation");
            WindowBehavior.RestoreAndActivate(w); Pump(150);
            Check(w.WindowState == FormWindowState.Normal && password.Text == "", "Restoration preserves a ready page without reviving passwords");
            Check(w.Opacity == 1, "Restored complete frame returns to full opacity");
            foreach (var size in new[] { new Size(1120,720), new Size(1280,820), new Size(1500,1000), new Size(1780,1000) })
            {
                w.Size = size;
                foreach (string page in DashboardWindow.NavigationPages)
                {
                    Page(w,page); Pump(100);
                    Panel active = Field<Panel>(w, page == DashboardWindow.HomePage ? "home" : "otherPage");
                    Check(!active.HorizontalScroll.Visible, "No horizontal overflow: " + page + " " + size.Width + "x" + size.Height);
                    Capture(w, Path.Combine(output, "page-" + Array.IndexOf(pages,page) + "-" + size.Width + "x" + size.Height + ".png"));
                }
            }
            w.Size = new Size(1500,1000);
            foreach (string page in pages) { Page(w,page); Pump(80); }
            Page(w,DashboardWindow.HomePage); Pump(250); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            int gdiBefore = GetGuiResources(Process.GetCurrentProcess().Handle,0);
            for (int cycle=0;cycle<31;cycle++) foreach(string page in pages)
            {
                watch.Restart(); Page(w,page); Application.DoEvents(); watch.Stop();
                samples.Add(new {cycle=cycle,page=page,milliseconds=Math.Round(watch.Elapsed.TotalMilliseconds,2)});
            }
            Pump(300); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            int gdiAfter = GetGuiResources(Process.GetCurrentProcess().Handle,0);
            Check(gdiAfter <= gdiBefore + 12, "GDI usage remains bounded over " + 31 * pages.Length + " cached page switches (" + gdiBefore + " to " + gdiAfter + ")");
            Check(Object.ReferenceEquals(record, Field<CampusStabilityView>(w,"stabilityView")) && !record.IsDisposed, "Game-mode recording controls survive all cached page switches");
            Check(initial.SequenceEqual(File.ReadAllBytes(AppPaths.Settings)), "Reversible interaction tests restore their isolated settings byte for byte");
            Check(!File.Exists(AppPaths.Secret) && !File.Exists(AppPaths.Startup), "No account or startup file created");
            w.Close(); Pump(30); Check(UiMotion.ActiveCount == 0, "Closed window retains no shared animation work");
        }
        Console.WriteLine("PASS " + results.Count + " UI experience checks");
    }
    private static void RunHost(DashboardWindow w, string output, bool presentationHost = false)
    {
        Form backdrop = null;
        System.Windows.Forms.Timer pulse = null;
        if (presentationHost)
        {
            backdrop = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Bounds = Screen.FromControl(w).WorkingArea, BackColor = Color.FromArgb(228, 234, 243), Text = "界面验收背景" };
            backdrop.Show();
            var banner = new Label { Text = "界面验收 · 独立测试数据", Bounds = new Rectangle(18, 5, 420, 28), ForeColor = SurfaceColor.Muted,
                BackColor = SurfaceColor.Canvas, Font = new Font("Microsoft YaHei UI", 10) };
            Field<WindowCaptionPanel>(w, "captionBar").Controls.Add(banner);
            w.Activate();
            int sample = 0;
            pulse = new System.Windows.Forms.Timer { Interval = 1000 };
            pulse.Tick += delegate {
                sample++;
                typeof(DashboardWindow).GetField("lastSpeedSample", Private).SetValue(w, new SpeedSample { Download = 2000 + sample % 5 * 300, Upload = 400 + sample % 3 * 100 });
                typeof(DashboardWindow).GetMethod("ApplySpeedSample", Private).Invoke(w, null);
            };
            pulse.Start();
        }
        Action<string> eventLog = delegate(string name) {
            File.AppendAllText(Path.Combine(output, "motion-events.jsonl"), new JavaScriptSerializer().Serialize(new {
                name = name, utc = DateTime.UtcNow.ToString("O"), state = w.WindowState.ToString() }) + Environment.NewLine);
        };
        w.Resize += delegate { eventLog("resize"); };
        w.KeyPreview = true;
        w.KeyDown += delegate(object sender, KeyEventArgs e) {
            if (e.KeyCode >= Keys.F5 && (int)e.KeyCode - (int)Keys.F5 < pages.Length) { Page(w,pages[(int)e.KeyCode-(int)Keys.F5]); e.Handled=true; }
            if (e.KeyCode == Keys.F2)
            {
                eventLog("request-minimize");
                WindowBehavior.Minimize(w);
                var restore = new System.Windows.Forms.Timer { Interval = 700 };
                restore.Tick += delegate { restore.Stop(); restore.Dispose(); eventLog("request-restore"); WindowBehavior.RestoreAndActivate(w); eventLog("restore-returned"); };
                restore.Start(); e.Handled = true;
            }
            if (e.KeyCode == Keys.F3)
            {
                var firstShow = new DashboardWindow(false) { StartPosition = FormStartPosition.Manual, Location = w.Location, Size = w.Size, Text = "稳连 首次显示验证（独立数据）" };
                firstShow.Show();
                var close = new System.Windows.Forms.Timer { Interval = 1300 };
                close.Tick += delegate { close.Stop(); close.Dispose(); firstShow.Close(); firstShow.Dispose(); w.Activate(); };
                close.Start(); e.Handled = true;
            }
            if (e.KeyCode == Keys.F4) { eventLog("request-maximize-toggle"); WindowBehavior.ToggleMaximize(w); e.Handled = true; }
        };
        var timer = new System.Windows.Forms.Timer { Interval = 75 };
        timer.Tick += delegate {
            try { File.WriteAllText(Path.Combine(output,"state.json"), new JavaScriptSerializer().Serialize(new {
                page=Field<string>(w,"currentPage"), state=w.WindowState.ToString(), hwnd=w.Handle.ToInt64(), backdropHwnd=backdrop == null ? 0 : backdrop.Handle.ToInt64(), bounds=new {x=w.Left,y=w.Top,width=w.Width,height=w.Height},
                activeAnimations=UiMotion.ActiveCount,reduced=UiMotion.Reduced,
                running=Field<CampusStabilityView>(w,"stabilityView") != null && Field<CampusStabilityView>(w,"stabilityView").Running,
                wifi=Field<Label>(w,"wifiValue").Text,download=Field<Label>(w,"heroDown").Text })); } catch(IOException) { }
        };
        timer.Start(); Application.Run(w); timer.Dispose();
        if (pulse != null) pulse.Dispose();
        if (backdrop != null) { backdrop.Close(); backdrop.Dispose(); }
    }
}
