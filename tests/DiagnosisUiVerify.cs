using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Layout checks for the Settings (account + keep-online), Check-up and Game
// pages at three window widths, plus result persistence across navigation.
internal static class DiagnosisUiVerify
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    private static T Field<T>(DashboardWindow w, string name) { return (T)typeof(DashboardWindow).GetField(name, Fields).GetValue(w); }
    private static IEnumerable<Control> All(Control c) { foreach (Control child in c.Controls) { yield return child; foreach (Control descendant in All(child)) yield return descendant; } }
    private static void Page(DashboardWindow w, string name) { typeof(DashboardWindow).GetMethod("ShowPage", Fields).Invoke(w, new object[] { name }); }
    private static int checks;
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks++; }
    private static void Pump() { for (int i = 0; i < 4; i++) { Application.DoEvents(); Thread.Sleep(25); } }
    private static bool Interactive(Control c) { return c.Visible && (c is FlatAction || c is AntdUI.Input || c is ComboBox || c is ModernSwitch || c is CheckBox || c is TextBox); }

    // Cards never overlap one another and their actions stay inside them,
    // without colliding with a sibling action.
    private static void CardsSound(Panel page, string name)
    {
        CardPanel[] cards = page.Controls.OfType<CardPanel>().Where(c => c.Visible).ToArray();
        for (int i = 0; i < cards.Length; i++)
            for (int j = i + 1; j < cards.Length; j++)
                Check(!cards[i].Bounds.IntersectsWith(cards[j].Bounds), name + ": cards overlap " + cards[i].Bounds + " / " + cards[j].Bounds);
        foreach (CardPanel card in cards)
        {
            Check(card.Right <= page.ClientSize.Width, name + ": card wider than the page");
            Control[] actions = card.Controls.Cast<Control>().Where(Interactive).ToArray();
            foreach (Control action in actions)
                Check(card.ClientRectangle.Contains(action.Bounds), name + ": clipped control " + action.Name + " " + action.Bounds + " in " + card.Size);
            for (int i = 0; i < actions.Length; i++)
                for (int j = i + 1; j < actions.Length; j++)
                    Check(!actions[i].Bounds.IntersectsWith(actions[j].Bounds), name + ": " + actions[i].Name + " overlaps " + actions[j].Name);
        }
    }

    [STAThread] private static void Main(string[] args)
    {
        try { Run(args); } catch (Exception ex) { Console.WriteLine(ex); Environment.ExitCode = 1; }
    }
    private static void Run(string[] args)
    {
        SetProcessDPIAware();
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        string data = Path.Combine(output, "isolated-data"); Directory.CreateDirectory(data);
        foreach (string name in new[] { "Data", "Secret", "Log", "Settings", "RecoveryState", "MonitorStatus", "Startup" })
            typeof(AppPaths).GetField(name).SetValue(null, name == "Data" ? data : Path.Combine(data, name + ".json"));
        AppSettings.Save(new SettingsRecord { autoLogin = false, priorityMode = "Off", minimizeToTray = false });
        byte[] initial = File.ReadAllBytes(AppPaths.Settings);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using (var w = new DashboardWindow(false))
        {
            w.StartPosition = FormStartPosition.Manual; w.Location = new Point(30, 20); w.Size = new Size(1500, 1000);
            w.Text = "稳连 设置与体检 UI 测试（独立数据）";
            w.Show(); Page(w, DashboardWindow.SettingsPage); Pump();
            foreach (int width in new[] { 1120, 1500, 1780 })
            {
                w.Size = new Size(width, 1000); Page(w, DashboardWindow.SettingsPage); Pump();
                Check(Field<Label>(w, "pageTitle").Top == 12 && Field<Label>(w, "pageBody").Top == 58, "Settings header moved after resizing");
                var settings = Field<Panel>(w, "otherPage");
                Check(Field<Label>(w, "pageBody").Bottom <= settings.Controls.OfType<CardPanel>().Min(c => c.Top), "Settings header overlaps a card");
                var user = All(w).Single(c => c.Name == "AccountUser");
                var pass = All(w).Single(c => c.Name == "AccountPassword");
                Check(user.Right == user.Parent.Width - 24 && pass.Width == user.Width, "Account fields do not use available width");
                var save = All(w).Single(c => c.Name == "AccountSave");
                Check(save.Right == save.Parent.Width - 24, "Account actions are not aligned to the card edge");
                var pill = All(w).Single(c => c.Name == "AccountSavedState");
                Check(pill.Right <= pill.Parent.Width - 20 && !pill.Parent.Controls.OfType<Label>().Any(l => l != pill && l.Bounds.IntersectsWith(pill.Bounds)), "Account status pill overlaps the heading");
                var keep = All(w).Single(c => c.Name == "Settings_KeepOnline");
                Check(keep.Right <= keep.Parent.Width - 20, "Keep-online switch leaves its card");
                CardsSound(settings, "settings-" + width);
                using (var bitmap = new Bitmap(w.Width, w.Height)) { w.DrawToBitmap(bitmap, new Rectangle(Point.Empty, w.Size)); bitmap.Save(Path.Combine(output, "settings-" + width + ".png")); }

                Page(w, DashboardWindow.CheckupPage); Pump();
                var checkup = Field<Panel>(w, "otherPage");
                Check(Field<Label>(w, "pageBody").Bottom <= checkup.Controls.OfType<CardPanel>().Min(c => c.Top), "Check-up header overlaps a card");
                CardsSound(checkup, "checkup-" + width);
                foreach (Label state in Field<Label[]>(w, "diagnosisStates"))
                    Check(state.Parent.ClientRectangle.Contains(state.Bounds), "Check-up result state clipped in its row");
                using (var bitmap = new Bitmap(w.Width, w.Height)) { w.DrawToBitmap(bitmap, new Rectangle(Point.Empty, w.Size)); bitmap.Save(Path.Combine(output, "checkup-" + width + ".png")); }

                Page(w, DashboardWindow.GamePage); Pump();
                var game = Field<Panel>(w, "otherPage");
                CardsSound(game, "game-" + width);
                var record = Field<CampusStabilityView>(w, "stabilityView");
                Check(record.Parent.ClientRectangle.Contains(record.Bounds), "Game record view leaves its card");
                foreach (Control c in All(record).Where(c => c is FlatAction))
                    Check(c.Parent.ClientRectangle.Contains(c.Bounds), "Game record action clipped: " + c.Name);
            }
            Page(w, DashboardWindow.CheckupPage);
            typeof(DashboardWindow).GetField("lastDiagnosis", Fields).SetValue(w, new DiagnosisSnapshot {
                States = Enumerable.Repeat("正常", 8).ToArray(), Details = Enumerable.Repeat("isolated diagnosis sample", 8).ToArray(),
                Summary = "isolated diagnosis summary", Latency = "延迟  4 ms", Loss = "未响应  0 / 10", Advice = "isolated advice" });
            Page(w, DashboardWindow.HomePage); Page(w, DashboardWindow.CheckupPage); Pump();
            Check(Field<Label>(w, "diagnosisSummary").Text == "isolated diagnosis summary", "Navigation loses completed check-up results");
            Check(Field<Label[]>(w, "diagnosisDetails").All(c => c.Text == "isolated diagnosis sample"), "Check-up rows lost their results");
            Check(Field<FlatAction>(w, "diagnosisRun").Text == "重新体检", "A finished check-up still offers to start the first one");
            typeof(DashboardWindow).GetField("diagnosing", Fields).SetValue(w, true);
            Page(w, DashboardWindow.HomePage); Page(w, DashboardWindow.CheckupPage); Pump();
            Check(!Field<FlatAction>(w, "diagnosisRun").Enabled && !Field<FlatAction>(w, "diagnosisRetry").Enabled,
                "Navigation enables a duplicate active check-up");
            Check(Field<FlatAction>(w, "diagnosisRun").Text == "正在体检…" && Field<FlatAction>(w, "diagnosisRetry").Text == "正在体检…", "Active check-up lacks action feedback");
            typeof(DashboardWindow).GetField("diagnosing", Fields).SetValue(w, false);
            w.Close();
        }
        Check(initial.SequenceEqual(File.ReadAllBytes(AppPaths.Settings)), "UI checks changed settings");
        Check(!File.Exists(AppPaths.Secret) && !File.Exists(AppPaths.Startup), "UI checks created account/startup files");
        File.WriteAllText(Path.Combine(output, "layout-results.txt"), "PASS " + checks + " settings, check-up and game layout checks; isolated settings unchanged.");
        Console.WriteLine("PASS " + checks + " layout checks");
    }
}
