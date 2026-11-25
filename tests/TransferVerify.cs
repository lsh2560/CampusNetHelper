using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

internal static class TransferVerify
{
    private static int checks;
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static void Main(string[] args)
    {
        string root = args[0], folder = args[1]; Directory.CreateDirectory(folder);
        byte[] bytes = new byte[1024 * 1024 + 333]; for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i * 31 + 7) % 251);
        string hash; using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        var token = CancellationToken.None;
        var file = TransferEngine.Inspect(new Uri(root + "/redirect"), token);
        Assert(file.Ranges && file.Length == bytes.Length && file.Url.AbsolutePath == "/file", "metadata and relative redirect");
        for (int n = 1; n <= 8; n++) {
            var result = TransferEngine.Measure(file, n, 262144, token);
            Assert(result.Bytes == 262144 && result.Mbps > 0, "valid benchmark " + n);
        }
        string path = Path.Combine(folder, "payload.bin");
        foreach (int n in new[] {1,4,8}) {
            var result = TransferEngine.Download(new Uri(root + "/file"), path, n, true, hash, token, null);
            Assert(File.ReadAllBytes(path).SequenceEqual(bytes) && result.Sha256 == hash, "byte-exact download " + n);
        }
        var plain = TransferEngine.Download(new Uri(root + "/plain"), path, 8, true, hash, token, null);
        Assert(plain.Bytes == bytes.Length && File.ReadAllBytes(path).SequenceEqual(bytes), "single stream fallback");
        foreach (string endpoint in new[] {"wrong", "mutate", "429", "html"}) {
            File.WriteAllText(path, "original"); bool rejected = false;
            try { TransferEngine.Download(new Uri(root + "/" + endpoint), path, 4, true, "", token, null); }
            catch { rejected = true; }
            Assert(rejected && File.ReadAllText(path) == "original", "reject " + endpoint + " and preserve destination");
        }
        bool hashRejected = false;
        try { TransferEngine.Download(new Uri(root + "/file"), path, 4, true, new string('0',64), token, null); } catch { hashRejected = true; }
        Assert(hashRejected && File.ReadAllText(path) == "original", "hash mismatch preserves original");
        using (var cancel = new CancellationTokenSource()) {
            cancel.CancelAfter(250); bool stopped = false;
            try { TransferEngine.Download(new Uri(root + "/slow"), path, 8, true, "", cancel.Token, null); }
            catch (OperationCanceledException) { stopped = true; }
            Assert(stopped && File.ReadAllText(path) == "original", "cancellation preserves original");
        }
        Assert(!Directory.GetDirectories(folder, ".campus-download-*").Any(), "temporary parts cleaned");
        bool invalid = false; try { TransferEngine.ParseUrl("file:///C:/windows/win.ini"); } catch { invalid = true; }
        Assert(invalid, "only HTTP file URLs accepted");
        Console.WriteLine("CHECKS " + checks);
    }
}
