using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
internal static class SafetyVerify {
    static void Assert(bool condition,string message) {if(!condition) throw new Exception(message);}
    static int Main(string[] args) {
        try {
            string root=Path.GetFullPath(args[0]), node=args[1]; Directory.CreateDirectory(root);
            string valuePath=Path.Combine(root,"atomic.json");
            AtomicFile.Write(valuePath,"{\"initial\":true}");
            Parallel.For(0,40,i=>AtomicFile.Write(valuePath,"{\"index\":"+i+"}"));
            new JavaScriptSerializer().DeserializeObject(File.ReadAllText(valuePath));
            Assert(Directory.GetFiles(root,"*.tmp").Length==0,"Atomic temporary files leaked");
            string output;
            int code=ProcessRunner.Run(node,"-e \"process.stdout.write('x'.repeat(180000));process.stderr.write('y'.repeat(180000))\"",10000,out output);
            Assert(code==0 && output.Length<=65537,"Output draining failed or unbounded output");
            string pidPath=Path.Combine(root,"child.pid");
            string marker=Path.Combine(root,"child-survived.txt");
            string script=Path.Combine(root,"spawn.cjs");
            string childScript = "setTimeout(()=>require('fs').writeFileSync(" + JSON(marker) + ",'survived'),1500)";
            File.WriteAllText(script,"const fs=require('fs');const c=require('child_process').spawn(process.execPath,['-e',"+JSON(childScript)+"],{stdio:'ignore'});fs.writeFileSync("+JSON(pidPath)+",String(c.pid));setInterval(()=>{},1000);");
            code=ProcessRunner.Run(node,"\""+script+"\"",400,out output);
            Assert(code==-1,"Hung parent not timed out");
            Thread.Sleep(1800);
            Assert(!File.Exists(marker),"Child process survived job timeout");
            if(File.Exists(pidPath)){int pid=int.Parse(File.ReadAllText(pidPath));try{using(var p=Process.GetProcessById(pid)){Assert(p.HasExited,"Child still running");}}catch(ArgumentException){}}
            File.WriteAllText(Path.Combine(root,"safety-verification.txt"),"PASS: 40 concurrent atomic writes leave valid JSON and no temporary files.\r\nPASS: large stdout/stderr drains without deadlock and is bounded.\r\nPASS: timeout ends parent and descendant process; no delayed child side effect.\r\n");
            Console.WriteLine(File.ReadAllText(Path.Combine(root,"safety-verification.txt")));return 0;
        } catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
    static string JSON(string text){return new JavaScriptSerializer().Serialize(text);}
}
