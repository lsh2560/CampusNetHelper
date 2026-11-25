using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

internal sealed class WifiStatus
{
    internal string Ssid, Bssid;
    internal int Quality;        // Windows signal quality, 0-100
    internal int? Rssi;          // dBm, when the driver reports it
    internal int? Channel;
    internal int RxMbps, TxMbps;

    internal string Band { get { return !Channel.HasValue ? null : Channel.Value <= 14 ? "2.4 GHz" : "5 GHz"; } }

    // Plain-language grade used by the home page and the game-mode analysis.
    internal string Grade
    {
        get
        {
            if (Rssi.HasValue) return Rssi >= -55 ? "很好" : Rssi >= -67 ? "良好" : Rssi >= -75 ? "一般" : "较弱";
            return Quality >= 80 ? "很好" : Quality >= 60 ? "良好" : Quality >= 40 ? "一般" : "较弱";
        }
    }

    internal bool Weak { get { return Rssi.HasValue ? Rssi.Value < -72 : Quality < 45; } }
}

// Reads the connected Wi-Fi link through the native WLAN API, which is
// locale-independent (netsh output is translated) and needs no elevation.
internal static class WifiInfo
{
    private const int CurrentConnection = 7, ChannelNumber = 8, Rssi = 0x10000102, Connected = 1;

    internal static WifiStatus Current()
    {
        IntPtr client = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            uint version;
            if (WlanOpenHandle(2, IntPtr.Zero, out version, out client) != 0) return null;
            if (WlanEnumInterfaces(client, IntPtr.Zero, out list) != 0) return null;
            int count = Marshal.ReadInt32(list);
            // WLAN_INTERFACE_INFO: GUID (16) + WCHAR[256] description (512) + state (4).
            for (int i = 0; i < count; i++)
            {
                IntPtr item = list + 8 + i * 532;
                if (Marshal.ReadInt32(item, 528) != Connected) continue;
                Guid id = (Guid)Marshal.PtrToStructure(item, typeof(Guid));
                WifiStatus status = Read(client, id);
                if (status != null) return status;
            }
            return null;
        }
        catch { return null; } // WLAN service stopped or no wireless adapter.
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero);
        }
    }

    private static WifiStatus Read(IntPtr client, Guid id)
    {
        IntPtr data = Query(client, id, CurrentConnection);
        if (data == IntPtr.Zero) return null;
        var status = new WifiStatus();
        try
        {
            // WLAN_CONNECTION_ATTRIBUTES: association attributes start at byte 520.
            int ssidLength = Math.Min(32, Math.Max(0, Marshal.ReadInt32(data, 520)));
            var ssid = new byte[ssidLength];
            Marshal.Copy(data + 524, ssid, 0, ssidLength);
            status.Ssid = Encoding.UTF8.GetString(ssid);
            var bssid = new byte[6];
            Marshal.Copy(data + 560, bssid, 0, 6);
            status.Bssid = String.Join(":", bssid.Select(b => b.ToString("X2")));
            status.Quality = Marshal.ReadInt32(data, 576);
            status.RxMbps = Marshal.ReadInt32(data, 580) / 1000;
            status.TxMbps = Marshal.ReadInt32(data, 584) / 1000;
        }
        finally { WlanFreeMemory(data); }
        IntPtr rssi = Query(client, id, Rssi);
        if (rssi != IntPtr.Zero) { int value = Marshal.ReadInt32(rssi); WlanFreeMemory(rssi); if (value < 0 && value > -110) status.Rssi = value; }
        IntPtr channel = Query(client, id, ChannelNumber);
        if (channel != IntPtr.Zero) { int value = Marshal.ReadInt32(channel); WlanFreeMemory(channel); if (value > 0) status.Channel = value; }
        return status;
    }

    private static IntPtr Query(IntPtr client, Guid id, int opcode)
    {
        uint size; IntPtr data; int type;
        return WlanQueryInterface(client, ref id, opcode, IntPtr.Zero, out size, out data, out type) == 0 ? data : IntPtr.Zero;
    }

    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr client);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr client, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr client, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(IntPtr client, ref Guid id, int opcode, IntPtr reserved, out uint size, out IntPtr data, out int type);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
}

internal enum LinkState { Ready, WifiDisconnected, Disconnected, NoIp }
internal enum PortalState { Online, AuthenticationRequired, Unknown }

internal sealed class LinkSnapshot
{
    public LinkState State;
    public string Identity;
    public string AdapterName;
    public string Ip;
    public string Gateway;
    public string Mac;
    public bool Wireless;
}

internal static class PortalProbe
{
    public static IPAddress[] ResolveHost(string host)
    {
        var task = Dns.GetHostAddressesAsync(host);
        try
        {
            if (!task.Wait(5000)) throw new TimeoutException("DNS 查询超过 5 秒，请稍后重试。");
            return task.Result;
        }
        catch (AggregateException ex) { throw ex.GetBaseException(); }
    }

    private static bool Supported(NetworkInterface adapter)
    {
        return adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
            adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
            adapter.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
            adapter.NetworkInterfaceType == NetworkInterfaceType.FastEthernetFx ||
            adapter.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT;
    }

    public static LinkSnapshot Link()
    {
        NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces().Where(Supported).ToArray();
        bool wifiPresent = all.Any(a => a.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        NetworkInterface[] active = all.Where(a => a.OperationalStatus == OperationalStatus.Up).ToArray();
        if (active.Length == 0)
            return new LinkSnapshot { State = wifiPresent ? LinkState.WifiDisconnected : LinkState.Disconnected, Identity = "" };

        var candidates = active.Select(a =>
        {
            try
            {
                IPInterfaceProperties props = a.GetIPProperties();
                IPAddress ip = props.UnicastAddresses.Select(x => x.Address)
                    .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(x) && !x.Equals(IPAddress.Any) &&
                        !(x.GetAddressBytes()[0] == 169 && x.GetAddressBytes()[1] == 254));
                IPAddress gateway = props.GatewayAddresses.Select(x => x.Address)
                    .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !x.Equals(IPAddress.Any));
                int rank = ip == null ? 0 : 20;
                if (gateway != null) rank += 60;
                if (ip != null)
                {
                    byte[] octets = ip.GetAddressBytes();
                    if (octets[0] == 10 && (octets[1] == 14 || octets[1] == 15)) rank += 130;
                }
                string name = (a.Name + " " + a.Description).ToLowerInvariant();
                if (name.Contains("virtual") || name.Contains("vpn") || name.Contains("vmware") ||
                    name.Contains("hyper-v") || name.Contains("radmin")) rank -= 120;
                return new { Adapter = a, Ip = ip, Gateway = gateway, Rank = rank };
            }
            catch { return new { Adapter = a, Ip = (IPAddress)null, Gateway = (IPAddress)null, Rank = -1 }; }
        }).OrderByDescending(x => x.Rank).ToArray();
        var chosen = candidates.First();
        if (chosen.Ip == null || chosen.Gateway == null)
            return new LinkSnapshot { State = LinkState.NoIp, Identity = chosen.Adapter.Id,
                AdapterName = chosen.Adapter.Name, Wireless = chosen.Adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 };
        return new LinkSnapshot { State = LinkState.Ready,
            Identity = chosen.Adapter.Id + "|" + chosen.Ip + "|" + chosen.Gateway,
            AdapterName = chosen.Adapter.Name, Ip = chosen.Ip.ToString(), Gateway = chosen.Gateway.ToString(),
            Mac = String.Join(":", chosen.Adapter.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))),
            Wireless = chosen.Adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 };
    }

    // Read-only: asks the portal for this device's status and never submits an account.
    public static PortalState Check()
    {
        string reason;
        int code = CampusPortalClient.Probe(out reason);
        if (code == CampusPortalClient.StatusOnline) return PortalState.Online;
        if (code == CampusPortalClient.StatusLoginRequired) return PortalState.AuthenticationRequired;
        AppLog.Write("认证状态无法判断，暂不恢复：" + reason);
        return PortalState.Unknown;
    }
}
