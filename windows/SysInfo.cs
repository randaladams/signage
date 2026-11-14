using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SignagePlayer;

/// <summary>What the player tells the server about this PC, and the facts the diagnostics page shows.</summary>
static class SysInfo
{
    /// <summary>"Windows 11 Pro 23H2 (10.0.22631) | Dell OptiPlex 3080 | 1920x1080", ASCII only (HTTP header).</summary>
    public static string DeviceInfo(int screenW, int screenH) => Ascii($"{WindowsName()} | {Hardware()} | {screenW}x{screenH}");

    public static string WindowsName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = k?.GetValue("ProductName") as string ?? "Windows";
            var build = Environment.OSVersion.Version.Build;
            // ProductName still says "Windows 10" on Windows 11; the build number tells them apart.
            if (build >= 22000) name = name.Replace("Windows 10", "Windows 11");
            var display = k?.GetValue("DisplayVersion") as string;
            return $"{name}{(display != null ? " " + display : "")} ({Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}.{build})";
        }
        catch
        {
            return "Windows " + Environment.OSVersion.Version;
        }
    }

    public static string Hardware()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var maker = (k?.GetValue("SystemManufacturer") as string ?? "").Trim();
            var model = (k?.GetValue("SystemProductName") as string ?? "").Trim();
            var s = (model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? model : $"{maker} {model}").Trim();
            return s.Length > 0 ? s : Environment.MachineName;
        }
        catch
        {
            return Environment.MachineName;
        }
    }

    private static string Ascii(string s) => Regex.Replace(s, @"[^\x20-\x7E]", "?");

    // ---------------------------------------------------------------- network

    public sealed record NetState(string Type, string? Ssid, int? Signal, string? Ip, IPAddress? Gateway, string? Adapter);

    /// <summary>The adapter Windows uses for the internet: the one that's up and has a default gateway.</summary>
    public static NetState? Network()
    {
        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(n => (n, p: n.GetIPProperties()))
                .Where(x => x.p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .OrderBy(x => x.p.GetIPv4Properties()?.Index ?? 999)
                .ToList();
            if (nics.Count == 0) return null;
            // A cable wins over Wi-Fi when both are connected (Windows prefers it too).
            var (nic, props) = nics.Any(x => x.n.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                ? nics.First(x => x.n.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                : nics[0];
            var ip = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            var gw = props.GatewayAddresses.First(g => g.Address.AddressFamily == AddressFamily.InterNetwork).Address;
            var type = nic.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "wifi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "ethernet",
                NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "mobile",
                _ => "other",
            };
            string? ssid = null;
            int? signal = null;
            if (type == "wifi") (ssid, signal) = Wifi();
            return new NetState(type, ssid, signal, ip, gw, nic.Description);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>X-Network header: {"type","ssid","signal","ip"}.</summary>
    public static string? NetworkHeader()
    {
        var n = Network();
        if (n == null) return null;
        var o = new System.Collections.Generic.Dictionary<string, object> { ["type"] = n.Type };
        if (n.Ssid != null) o["ssid"] = n.Ssid;
        if (n.Signal != null) o["signal"] = n.Signal;
        if (n.Ip != null) o["ip"] = n.Ip;
        return Ascii(JsonSerializer.Serialize(o));
    }

    private static (string?, int?) _wifiCache;
    private static long _wifiCacheAt;

    /// <summary>Wi-Fi name and signal % from "netsh wlan show interfaces" (cached 50 s). Windows 11 24H2+ hides the
    /// network name unless location access is on; then only the signal is reported.</summary>
    private static (string?, int?) Wifi()
    {
        if (Environment.TickCount64 - _wifiCacheAt < 50_000) return _wifiCache;
        string? ssid = null;
        int? signal = null;
        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            foreach (var raw in output.Split('\n'))
            {
                var parts = raw.Split(':', 2);
                if (parts.Length != 2) continue;
                var key = parts[0].Trim();
                var val = parts[1].Trim();
                if (key == "SSID" && val.Length > 0) ssid = val;
                if (key == "Signal" && int.TryParse(val.TrimEnd('%', ' '), out var s)) signal = Math.Clamp(s, 0, 100);
            }
        }
        catch
        {
        }
        _wifiCache = (ssid, signal);
        _wifiCacheAt = Environment.TickCount64;
        return _wifiCache;
    }

    // ---------------------------------------------------------------- memory

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);

    public static (ulong freeMb, ulong totalMb) Memory()
    {
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref m) ? (m.AvailPhys / 1_000_000, m.TotalPhys / 1_000_000) : (0, 0);
    }

    // ---------------------------------------------------------------- keep the screen on

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001, EsDisplayRequired = 0x00000002;

    /// <summary>While the slideshow is on screen, stop Windows turning the display off or going to sleep.
    /// Call from the UI thread (the setting belongs to the calling thread).</summary>
    public static void KeepAwake(bool on) =>
        SetThreadExecutionState(on ? EsContinuous | EsSystemRequired | EsDisplayRequired : EsContinuous);
}
