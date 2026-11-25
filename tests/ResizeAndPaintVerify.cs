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

internal static class ResizeAndPaintVerify
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> results = new List<object>();
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    private sealed class PaintCounter : NativeWindow
    {
        internal int Paints;
        internal PaintCounter(Control control) { AssignHandle(control.Handle); }
        protected override void WndProc(ref Message m) { if (m.Msg == 0xF) Paints++; base.WndProc(ref m); }
    }
    private static IEnumerable<Control> All(Control root) { foreach(Control c in root.Controls) { yield return c; foreach(Control d in All(c)) yield return d; } }
    private static T Field<T>(DashboardWindow w, string name) { return (T)typeof(DashboardWindow).GetField(name,Private).GetValue(w); }
    private static void Page(DashboardWindow w, string name) { typeof(DashboardWindow).GetMethod("ShowPage",Private).Invoke(w,new object[]{name}); }
    private static void Pump(int ms) { var clock=Stopwatch.StartNew(); do { Application.DoEvents(); Thread.Sleep(5); } while(clock.ElapsedMilliseconds<ms); }
    private static void Check(bool ok, string name) { results.Add(new {name=name,status=ok?"PASS":"FAIL"}); }
    private static void Capture(Control c,string path) { using(var b=new Bitmap(c.Width,c.Height)) { c.DrawToBitmap(b,new Rectangle(Point.Empty,c.Size)); b.Save(path); } }
    [STAThread] private static void Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
        try { Run(args,output); } catch(Exception ex) { results.Add(new{name="Exception",status="FAIL",detail=ex.ToString()}); }
        int failed=results.Count(x=>(string)x.GetType().GetProperty("status").GetValue(x,null)=="FAIL");
        File.WriteAllText(Path.Combine(output,"resize-paint-results.json"),new JavaScriptSerializer().Serialize(new{passed=results.Count-failed,failed=failed,results=results}));
        Console.WriteLine("Resize/paint checks: "+(results.Count-failed)+" passed, "+failed+" failed");Environment.ExitCode=failed>0?1:0;
    }
    private static void Run(string[] args,string output)
    {
        string data=Path.Combine(output,"isolated-data"); Directory.CreateDirectory(data);
        foreach(string name in new[]{"Data","Secret","Settings","Log","Startup","RecoveryState","MonitorStatus"})
            typeof(AppPaths).GetField(name).SetValue(null,name=="Data"?data:Path.Combine(data,name+".json"));
        AppSettings.Save(new SettingsRecord{priorityMode="Off",autoLogin=false,quickRecheck=true,carrier="auto",checkIntervalSeconds=45});
        byte[] original=File.ReadAllBytes(AppPaths.Settings);
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        using(var w=new DashboardWindow(false))
        {
            Size initial=w.Size;
            w.StartPosition=FormStartPosition.Manual;w.Location=new Point(-20000,-20000);w.Size=new Size(1280,820);w.Show();Pump(120);
            Page(w,"设置");Pump(150);
            var mode=(ComboBox)All(w).Single(c=>c.Name=="SettingsMode");
            var counter=new PaintCounter(mode);
            int before=counter.Paints;Pump(400);int idle=counter.Paints-before;
            for(int i=0;i<20;i++) { Field<Label>(w,"heroDown").Text="sample "+i;Field<Label>(w,"statusDetail").Text="sample "+i;Pump(35); }
            int busy=counter.Paints-before-idle;
            File.WriteAllText(Path.Combine(output,"paint-measurement.json"),new JavaScriptSerializer().Serialize(new {
                version=w.GetType().Assembly.GetName().Version.ToString(),initialWidth=initial.Width,initialHeight=initial.Height,
                rootComposited=(GetWindowLong(w.Handle,-20)&0x02000000)!=0,comboStyle=mode.FlatStyle.ToString(),idlePaints=idle,hiddenUpdates=20,comboPaintsDuringHiddenUpdates=busy}));
            counter.ReleaseHandle();
            if(args.Contains("--baseline")) { w.Close();return; }
            Check(initial.Width<=1280 && initial.Height<=820,"Default window is no larger than 1280x820");
            Check((GetWindowLong(w.Handle,-20)&0x02000000)==0,"No global descendant compositing that couples native dropdowns to hidden updates");
            Check(mode.FlatStyle==FlatStyle.Standard,"Strategy uses stable standard native dropdown rendering");
            Check(busy<=2,"20 hidden home updates do not repeatedly repaint the strategy dropdown ("+busy+")");
            SendMessage(w.Handle,WindowBehavior.SystemCommand,new IntPtr(WindowBehavior.MinimizeCommand),IntPtr.Zero);Pump(80);
            Check(w.Opacity==1 && (GetWindowLong(w.Handle,-20)&0x00080000)==0,"Minimization preserves full-opacity native surface for the system restore animation");
            WindowBehavior.RestoreAndActivate(w);Pump(120);Check(w.Opacity==1 && w.WindowState==FormWindowState.Normal,"Restore does not hide and abruptly reveal the window");
            string[] pages={"首页","游戏模式","体检与报告","设置","高级工具"};
            foreach(Size size in new[]{new Size(1000,640),new Size(1120,720),new Size(1280,820),new Size(1500,900),new Size(1780,1000)})
            {
                w.Size=size;
                Pump(60);
                var menu=Field<AntdUI.Menu>(w,"navigation");
                foreach(var item in menu.Items)
                {
                    Rectangle itemBounds=(Rectangle)item.GetType().GetProperty("rect",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(item,null);
                    Check(menu.ClientRectangle.Contains(itemBounds),"Sidebar navigation remains inside its viewport: "+item.Text+" "+size.Width+"x"+size.Height);
                }
                foreach(string page in pages)
                {
                    Page(w,page);Pump(40);
                    Control active=page=="首页"?(Control)Field<Panel>(w,"home"):page=="高级工具"?(Control)Field<SpeedupView>(w,"speedup"):Field<Panel>(w,"otherPage");
                    if(page=="高级工具")
                    {
                        var tabs=(AntdUI.Tabs)typeof(SpeedupView).GetField("tabs",Private).GetValue(active);
                        for(int tab=0;tab<tabs.Pages.Count;tab++)
                        { tabs.SelectedIndex=tab;Pump(40);VerifyControls(w,tabs.Pages[tab],page+"/"+tab+" "+size.Width); }
                    }
                    else VerifyControls(w,active,page+" "+size.Width);
                    if(size.Width==1000 || size.Width==1280)
                    {
                        var scroll=active as ScrollableControl;if(scroll!=null&&scroll.AutoScroll)scroll.AutoScrollPosition=Point.Empty;
                        Pump(35);Capture(w,Path.Combine(output,"layout-"+Array.IndexOf(pages,page)+"-"+size.Width+".png"));
                    }
                }
            }
            Page(w,"设置");w.Size=new Size(1280,820);Pump(60);
            ComboBox current=(ComboBox)All(w).Single(c=>c.Name=="SettingsMode");
            current.Focus();current.SelectedIndex=1;Pump(30);Check(current.SelectedIndex==1,"Strategy selection remains usable after resizing");
            Check(original.SequenceEqual(File.ReadAllBytes(AppPaths.Settings)),"Layout, selection and window tests leave isolated settings unchanged");
            Check(!File.Exists(AppPaths.Secret)&&!File.Exists(AppPaths.Startup),"Tests create no credentials or startup entry");w.Close();
        }
    }
    private static void VerifyControls(DashboardWindow w,Control root,string scene)
    {
        var scroll=root as ScrollableControl;
        if(scroll!=null && scroll.AutoScroll) Check(!scroll.HorizontalScroll.Visible,"No horizontal clipping: "+scene);
        foreach(Control c in All(root))
        {
            if(!(c is FlatAction || c is ComboBox || c is AntdUI.Input || c is CheckBox || c is ModernSwitch || c is TextBox))continue;
            bool fits=c.Left>=0 && c.Top>=0 && c.Right<=c.Parent.ClientSize.Width+1 && c.Bottom<=c.Parent.ClientSize.Height+1;
            var parentScroll=c.Parent as ScrollableControl;
            if(parentScroll!=null && parentScroll.AutoScroll) fits=true;
            Check(fits,"Control fits its layout: "+scene+" / "+(String.IsNullOrEmpty(c.Name)?c.Text:c.Name));
            if(scroll!=null && scroll.AutoScroll)
            {
                scroll.ScrollControlIntoView(c);Pump(5);
                Rectangle box=scroll.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
                Check(box.Left>=0&&box.Top>=0&&box.Right<=scroll.ClientSize.Width+1&&box.Bottom<=scroll.ClientSize.Height+1,
                    "Control is reachable by scrolling: "+scene+" / "+(String.IsNullOrEmpty(c.Name)?c.Text:c.Name));
            }
        }
    }
}
