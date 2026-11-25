using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class InstallCheckItem
{
    public string name { get; set; }
    public bool ok { get; set; }
    public bool required { get; set; }
    public string detail { get; set; }
}

internal sealed class InstallCheckReport
{
    public bool ready { get; set; }
    public List<InstallCheckItem> checks { get; set; }
}

internal static class InstallerProgram
{
    private const string ResourceName = "CampusPayload";
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    public static Stream Payload()
    {
        Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream == null) throw new InvalidDataException("安装程序缺少内置软件文件，请重新下载。");
        return stream;
    }

    [STAThread]
    private static void Main(string[] args)
    {
        try { SetProcessDPIAware(); } catch { }
        if (args.Length == 1 && args[0] == "--verify-payload")
        {
            try
            {
                using (Stream payload = Payload())
                using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
                {
                    string[] required = { "CampusNetHelper.exe", "MirrorConfig.ps1", "DnsConfig.ps1",
                        "AntdUI.dll", "LICENSE.AntdUI.txt", "THIRD_PARTY_NOTICES.md" };
                    var names = new HashSet<string>(archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')),
                        StringComparer.OrdinalIgnoreCase);
                    Environment.ExitCode = required.All(names.Contains) ? 0 : 2;
                }
            }
            catch { Environment.ExitCode = 3; }
            return;
        }
        if (args.Length == 2 && args[0] == "--test-install")
        {
            try { Directory.CreateDirectory(args[1]); SetupWindow.ExtractPayload(args[1], null); Environment.ExitCode = 0; }
            catch { Environment.ExitCode = 3; }
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupWindow());
    }
}

internal sealed class SetupWindow : Form
{
    private readonly Label status;
    private readonly ProgressBar progress;
    private readonly TextBox report;
    private readonly Button install;
    private readonly Button open;
    private readonly CheckBox desktopShortcut;
    private string installedExecutable;

    public SetupWindow()
    {
        Text = "稳连 · 安装";
        Font = new Font("Microsoft YaHei UI", 9);
        ClientSize = new Size(660, 515);
        MinimumSize = new Size(660, 515);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(244, 248, 255);

        Controls.Add(new Label { Text = "稳连 · 校园网助手", Location = new Point(30, 27),
            Size = new Size(590, 60), Font = new Font("Microsoft YaHei UI", 18, FontStyle.Bold),
            ForeColor = Color.FromArgb(20, 50, 105) });
        Controls.Add(new Label { Text = "安装后直接填写账号，不需要额外运行环境。使用当前用户目录，不要求管理员权限。",
            Location = new Point(32, 88), Size = new Size(590, 48), ForeColor = Color.FromArgb(85, 105, 139) });
        desktopShortcut = new CheckBox { Text = "创建桌面快捷方式", Checked = true,
            Location = new Point(32, 145), Size = new Size(250, 32) };
        Controls.Add(desktopShortcut);
        status = new Label { Text = "准备安装（安装过程不会读取或上传校园网密码）",
            Location = new Point(32, 182), Size = new Size(590, 31), ForeColor = Color.FromArgb(25, 53, 105) };
        Controls.Add(status);
        progress = new ProgressBar { Location = new Point(32, 220), Size = new Size(590, 20) };
        Controls.Add(progress);
        report = new TextBox { Location = new Point(32, 259), Size = new Size(590, 169),
            Font = new Font("Microsoft YaHei UI", 9), Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, BackColor = Color.White,
            Text = "安装完成后将检查软件文件、界面组件、本地写入权限和当前网络。\r\n校园网账号可在软件内加密保存。" };
        Controls.Add(report);
        install = new Button { Text = "开始安装", Location = new Point(315, 450), Size = new Size(148, 40),
            BackColor = Color.FromArgb(29, 111, 246), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        install.FlatAppearance.BorderSize = 0;
        install.Click += delegate { BeginInstall(); };
        Controls.Add(install);
        open = new Button { Text = "打开软件", Location = new Point(474, 450), Size = new Size(148, 40), Enabled = false };
        open.Click += delegate { if (installedExecutable != null) Process.Start(installedExecutable); Close(); };
        Controls.Add(open);
    }

    private void Ui(Action action)
    {
        if (!IsDisposed && IsHandleCreated) BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) action(); });
    }

    private void BeginInstall()
    {
        install.Enabled = false;
        desktopShortcut.Enabled = false;
        report.Text = "正在释放软件文件…";
        bool addDesktop = desktopShortcut.Checked;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                if (!Environment.Is64BitOperatingSystem)
                    throw new NotSupportedException("此安装包适用于 64 位 Windows。请使用 64 位 Windows 10 或 11。");
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "CampusNetHelper");
                Directory.CreateDirectory(root);
                string staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                try
                {
                    ExtractPayload(staging, percentage => Ui(delegate { progress.Value = percentage; }));
                    string destination = Path.Combine(root, "app-0.10.0");
                    if (Directory.Exists(destination))
                        destination = Path.Combine(root, "app-0.10.0-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                    Directory.Move(staging, destination);
                    installedExecutable = Path.Combine(destination, "CampusNetHelper.exe");
                    Ui(delegate { status.Text = "文件已安装，正在检查运行环境…"; progress.Value = 100; });
                    var notes = new List<string>();
                    try { CreateShortcuts(installedExecutable, addDesktop); }
                    catch (Exception ex) { notes.Add("快捷方式创建失败：" + ex.Message); }
                    try { UpdateExistingStartup(installedExecutable); }
                    catch (Exception ex) { notes.Add("原有开机启动项更新失败：" + ex.Message); }
                    InstallCheckReport check = CheckInstalled(installedExecutable);
                    var lines = new List<string>();
                    lines.Add("安装目录：" + destination);
                    lines.Add(check.ready ? "环境自检通过，可以打开软件配置校园网账号。" :
                        "已安装，但有必要组件未通过自检。请查看下方结果。");
                    lines.Add("");
                    foreach (InstallCheckItem item in check.checks)
                        lines.Add((item.ok ? "✓ " : item.required ? "✕ " : "· ") + item.name + "：" + item.detail);
                    lines.AddRange(notes);
                    Ui(delegate
                    {
                        status.Text = check.ready ? "安装完成 · 环境自检通过" : "安装完成 · 请处理环境自检提示";
                        report.Text = String.Join(Environment.NewLine, lines.ToArray());
                        open.Enabled = true;
                        install.Text = "已安装";
                    });
                }
                finally
                {
                    if (Directory.Exists(staging)) Directory.Delete(staging, true);
                }
            }
            catch (Exception ex)
            {
                Ui(delegate
                {
                    status.Text = "安装未完成";
                    report.Text = ex.Message;
                    install.Enabled = true;
                    desktopShortcut.Enabled = true;
                });
            }
        });
    }

    internal static void ExtractPayload(string directory, Action<int> onProgress)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using (Stream payload = InstallerProgram.Payload())
        using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
        {
            int done = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                string target = Path.GetFullPath(Path.Combine(directory, relative));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包包含非法路径。");
                if (String.IsNullOrEmpty(entry.Name)) Directory.CreateDirectory(target);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (Stream source = entry.Open())
                    using (var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                        source.CopyTo(destination);
                }
                done++;
                int percentage = Math.Min(95, done * 95 / Math.Max(1, archive.Entries.Count));
                if (onProgress != null) onProgress(percentage);
            }
        }
    }

    private static void CreateShortcuts(string executable, bool desktop)
    {
        var paths = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "稳连.lnk") };
        if (desktop) paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "稳连.lnk"));
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        foreach (string path in paths)
        {
            dynamic shortcut = shell.CreateShortcut(path);
            shortcut.TargetPath = executable;
            shortcut.WorkingDirectory = Path.GetDirectoryName(executable);
            shortcut.IconLocation = Path.Combine(Path.GetDirectoryName(executable), "CampusNetHelper.ico") + ",0";
            shortcut.Description = "稳连 · 校园网助手（非官方）";
            shortcut.Save();
        }
    }

    private static void UpdateExistingStartup(string executable)
    {
        string startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "CampusEthernetAutoLogin.vbs");
        if (!File.Exists(startup)) return;
        string escaped = executable.Replace("\"", "\"\"");
        string launcher = "CreateObject(\"WScript.Shell\").Run \"\"\"" + escaped + "\"\" --background --startup\", 0, False";
        AtomicFile.Write(startup, launcher, Encoding.Unicode);
    }

    private static InstallCheckReport CheckInstalled(string executable)
    {
        string output = Path.Combine(Path.GetTempPath(), "campus-check-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            string diagnostic;
            int code = ProcessRunner.Run(executable, "--environment-check \"" + output + "\"", 35000, out diagnostic);
            if (code == -1) throw new TimeoutException("环境自检超时，已结束自检及子进程。可在软件“设置”中重新运行。");
            if (!File.Exists(output)) throw new InvalidDataException("环境自检没有生成结果，请在软件“设置”中重试。");
            return new JavaScriptSerializer().Deserialize<InstallCheckReport>(File.ReadAllText(output, Encoding.UTF8));
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }
}
