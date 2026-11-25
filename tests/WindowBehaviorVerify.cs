using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal sealed class WindowBehaviorFixture : Form
{
    internal WindowBehaviorFixture()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-20000, -20000);
        Size = new Size(800, 600);
        MaximizedBounds = Screen.PrimaryScreen.WorkingArea;
        ShowInTaskbar = true;
        BackColor = Color.FromArgb(250, 250, 250);
        Text = "This native caption must never be printed";
        var combo = new ComboBox { Location = new Point(40, 100), Size = new Size(250, 32), DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 11), Name = "nativeCombo" };
        combo.Items.Add("Native combo rendering"); combo.SelectedIndex = 0; Controls.Add(combo);
    }
    protected override CreateParams CreateParams
    {
        get { return WindowBehavior.ApplyStyles(base.CreateParams); }
    }
    protected override void WndProc(ref Message message)
    {
        if (WindowBehavior.HandleMessage(ref message)) return;
        base.WndProc(ref message);
    }
}

internal static class WindowBehaviorVerify
{
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr window, bool revert);
    [DllImport("user32.dll")]
    private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

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

    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var window = new WindowBehaviorFixture())
        {
            window.Show(); Pump();
            int style = GetWindowLong(window.Handle, -16);
            int expected = WindowBehavior.Caption | WindowBehavior.ThickFrame | WindowBehavior.SystemMenu |
                WindowBehavior.MinimizeBox | WindowBehavior.MaximizeBox;
            Assert((style & expected) == expected, "native caption, resize, system menu, minimize and maximize styles retained");
            Assert(window.ClientSize == window.Size, "custom title bar keeps the complete client rectangle");
            Message print = Message.Create(window.Handle, 0x0317, IntPtr.Zero, new IntPtr(2 | 4 | 8 | 16));
            Assert(!WindowBehavior.HandleMessage(ref print) && print.LParam.ToInt64() == (4 | 8 | 16), "WM_PRINT suppresses only non-client rendering");
            using (var bitmap = new Bitmap(window.Width, window.Height))
            {
                window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
                Assert(bitmap.GetPixel(200, 5).ToArgb() == window.BackColor.ToArgb(), "DrawToBitmap contains no legacy caption");
                int dark = 0;
                for (int y = 105; y < 125; y++) for (int x = 50; x < 220; x++) {
                    Color pixel = bitmap.GetPixel(x, y); if (pixel.R < 150 && pixel.G < 150 && pixel.B < 150) dark++;
                }
                Assert(dark > 40, "native combo selection text survives DrawToBitmap");
                string output = args.Length > 0 ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
                Directory.CreateDirectory(output); bitmap.Save(Path.Combine(output, "window-behavior-print.png"));
            }
            IntPtr menu = GetSystemMenu(window.Handle, false);
            uint minimizeState = GetMenuState(menu, (uint)WindowBehavior.MinimizeCommand, 0);
            Assert(menu != IntPtr.Zero && minimizeState != UInt32.MaxValue && (minimizeState & 3) == 0,
                "native system menu exposes an enabled minimize command");
            // Explorer uses the native minimization contract rather than a custom click handler.
            SendMessage(window.Handle, WindowBehavior.SystemCommand, new IntPtr(WindowBehavior.MinimizeCommand), IntPtr.Zero); Pump();
            Assert(IsIconic(window.Handle) && window.WindowState == FormWindowState.Minimized, "SC_MINIMIZE makes the window iconic");
            WindowBehavior.RestoreAndActivate(window); Pump();
            Assert(!IsIconic(window.Handle) && window.WindowState == FormWindowState.Normal, "restore returns the window to normal");
            WindowBehavior.ToggleMaximize(window); Pump();
            Assert(window.WindowState == FormWindowState.Maximized, "custom maximize action uses the native command");
            WindowBehavior.Minimize(window); Pump();
            Assert(IsIconic(window.Handle), "maximized window can minimize");
            WindowBehavior.RestoreAndActivate(window); Pump();
            Assert(window.WindowState == FormWindowState.Maximized, "restore remembers the maximized state");
            WindowBehavior.ToggleMaximize(window); Pump();
            Assert(window.WindowState == FormWindowState.Normal, "maximize toggle restores normal bounds");
            window.Hide(); WindowBehavior.RestoreAndActivate(window); Pump();
            Assert(window.Visible && window.WindowState == FormWindowState.Normal, "tray show restores a hidden window");
            window.Close();
        }
        Console.WriteLine("CHECKS " + checks);
    }
}
