using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

internal static class RuntimeNames
{
    public static string Name(string purpose) { return @"Local\CampusNetHelper." + purpose + "." + WindowsIdentity.GetCurrent().User.Value; }
}

internal static class AtomicFile
{
    public static void Write(string path, string content, Encoding encoding = null)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var mutex = new Mutex(false, RuntimeNames.Name("Files")))
        {
            bool owned = false;
            try
            {
                try { owned = mutex.WaitOne(5000); } catch (AbandonedMutexException) { owned = true; }
                if (!owned) throw new IOException("配置正在使用，请稍后重试。");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(temp, content, encoding ?? new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); if (owned) mutex.ReleaseMutex(); }
        }
    }
}

internal sealed class ChildProcessJob : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long PerProcessUserTime, PerJobUserTime; public uint Flags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    private IntPtr handle;
    public ChildProcessJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits(); limits.Basic.Flags = 0x2000; // KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(limits))) { int error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error); }
    }
    public void Attach(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
}

internal static class ProcessRunner
{
    public static int Run(string executable, string arguments, int timeoutMs, out string output)
    {
        var text = new StringBuilder(); var gate = new object();
        using (var job = new ChildProcessJob())
        using (var process = new Process())
        {
            process.StartInfo = new ProcessStartInfo(executable, arguments) { UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            DataReceivedEventHandler receive = delegate(object sender, DataReceivedEventArgs e)
            { if (e.Data != null) lock (gate) { if (text.Length < 65536) text.AppendLine(e.Data.Substring(0, Math.Min(e.Data.Length, 65536 - text.Length))); } };
            process.OutputDataReceived += receive; process.ErrorDataReceived += receive;
            process.Start();
            try
            {
                job.Attach(process);
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                if (!process.WaitForExit(timeoutMs))
                {
                    job.Dispose(); process.WaitForExit(5000); output = "操作超时，已结束此次任务及子进程。"; return -1;
                }
                int code = process.ExitCode;
                job.Dispose(); // Also closes descendants that inherited the pipes.
                process.WaitForExit();
                lock (gate) output = text.ToString().Trim();
                return code;
            }
            catch
            {
                job.Dispose();
                try { if (!process.HasExited) process.Kill(); } catch { }
                throw;
            }
        }
    }
}
