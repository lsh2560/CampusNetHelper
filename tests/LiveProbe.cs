using System;
using System.Threading;
internal static class LiveProbe
{
    private static void Main()
    {
        foreach (var site in MirrorSite.All) {
            try { var file=TransferEngine.Inspect(new Uri(site.Base+MirrorSite.ProbePath),CancellationToken.None);
                var value=TransferEngine.Measure(file,1,65536,CancellationToken.None);
                Console.WriteLine(site.Name+" | 206 | "+file.Length+" bytes | "+value.Mbps.ToString("0.00")+" Mbps (64 KiB availability probe only)");
            } catch(Exception ex) {Console.WriteLine(site.Name+" | unavailable | "+ex.Message);}
        }
    }
}
