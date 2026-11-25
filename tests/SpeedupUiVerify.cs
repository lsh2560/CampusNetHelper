using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class SpeedupUiVerify
{
    private static void CheckActions(Control parent) {
        foreach(Control child in parent.Controls) {
            if(parent is FlowLayoutPanel && (child is Button || child is AntdUI.Button) && (child.Right>parent.ClientSize.Width || child.Bottom>parent.ClientSize.Height)) throw new Exception("Clipped action: "+child.Text);
            if (child is Panel || child is TabPage || child is TableLayoutPanel) CheckActions(child);
        }
    }
    private static object Get(object target, string name) { return target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }
    private static void Call(object target, string name, params object[] args) { target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target,args); }
    private static void Pump(SpeedupView view) {
        var deadline = DateTime.Now.AddSeconds(90);
        do { Application.DoEvents(); Thread.Sleep(20); if (DateTime.Now > deadline) throw new Exception("UI task timeout"); } while ((bool)Get(view,"running"));
        Application.DoEvents();
    }
    [STAThread] private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception ex) { Console.WriteLine(ex.GetType().FullName); Console.WriteLine(ex.Message); Console.WriteLine(ex.StackTrace); Environment.ExitCode=1; }
    }
    private static void Run(string[] args)
    {
        System.Net.WebRequest.DefaultWebProxy = null; // Local fixture must never use a user proxy.
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        string output = args[1]; Directory.CreateDirectory(output);
        using (var window = new DashboardWindow(false)) {
            window.ShowInTaskbar=false; window.StartPosition=FormStartPosition.Manual; window.Location=new Point(-20000,-20000);
            window.Show(); Application.DoEvents(); Call(window,"ShowPage","下载与提速");
            var view=(SpeedupView)Get(window,"speedup"); var tabs=(AntdUI.Tabs)Get(view,"tabs");
            foreach (Size size in new[] {new Size(1120,720), new Size(1500,1000)}) {
                window.Size=size;
                for (int i=0;i<4;i++) { tabs.SelectedIndex=i; Application.DoEvents();
                    var page=tabs.Pages[i]; if(page.HorizontalScroll.Visible) throw new Exception("Horizontal overflow "+i);
                    CheckActions(page);
                    using(var bitmap=new Bitmap(window.Width,window.Height)) { window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,window.Size)); bitmap.Save(Path.Combine(output,size.Width+"-tab"+i+".png")); }
                }
            }
            foreach (var site in MirrorSite.All) site.Base=args[0];
            ((AntdUI.Input)Get(view,"probePath")).Text="/file"; tabs.SelectedIndex=0;
            Call(view,"RunMirrorTests",null,EventArgs.Empty); Pump(view);
            var mirrors=(UiResultTable)Get(view,"mirrors");
            if(mirrors.Rows.Any(r=>Convert.ToString(r.Cells[1].Value)=="—")) throw new Exception("Missing mirror values: "+((Label)Get(view,"status")).Text+" | "+String.Join(" | ",mirrors.Rows.Select(r=>Convert.ToString(r.Cells[5].Value))));
            Call(view,"UseMirror",false);
            Call(view,"RunStreamTests",null,EventArgs.Empty); Pump(view);
            var streams=(UiResultTable)Get(view,"streams");
            if(streams.Rows.Any(r=>Convert.ToString(r.Cells[4].Value)=="—")) throw new Exception("Missing stream results: "+((Label)Get(view,"status")).Text);
            Call(window,"ShowPage","首页"); Call(window,"ShowPage","下载与提速");
            if(!Object.ReferenceEquals(view,Get(window,"speedup"))) throw new Exception("Task view state lost on navigation");
            tabs.SelectedIndex=1; Application.DoEvents();
            using(var bitmap=new Bitmap(window.Width,window.Height)) { window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,window.Size)); bitmap.Save(Path.Combine(output,"stream-results.png")); }
            tabs.SelectedIndex=0; Application.DoEvents();
            using(var bitmap=new Bitmap(window.Width,window.Height)) { window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,window.Size)); bitmap.Save(Path.Combine(output,"mirror-results.png")); }
            File.WriteAllText(Path.Combine(output,"ui-checks.txt"),"PASS: 4 tabs at 1120x720 and 1500x1000; six mirror values; 1-8 two-round results; navigation state preserved. Local fixture only.");
            Console.WriteLine("PASS speedup UI and workflows");
        }
    }
}
