using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SignagePlayer;

/// <summary>
/// The diagnostics page (F1 / D / right-click, or "Show diagnostics" in the admin page): a step-by-step
/// connection check where the first FAIL is where it breaks, then versions and health. Same layout as the
/// Raspberry Pi and Fire Stick players.
/// </summary>
static class Diagnostics
{
    public const int Cols = 80;

    public static async Task<string> Run(PlayerWindow w)
    {
        var sb = new StringBuilder();
        static string T(long ms) => ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("ddd HH:mm:ss") : "never";
        static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
        void Line(bool? ok, string label, string detail) =>
            sb.Append(ok == true ? " OK  " : ok == false ? "FAIL " : " ??  ").Append(label.PadRight(24))
              .Append(Clip(detail, Cols - 29)).Append('\n');
        void Info(string label, string value) => sb.Append(label.PadRight(18)).Append(Clip(value, Cols - 18)).Append('\n');

        sb.Append(Clip("DIAGNOSTICS" + (w.ScreenName.Length > 0 ? " - " + w.ScreenName : ""), Cols).PadRight(Cols)).Append('\n');
        sb.Append("Esc closes · F1 re-checks · refreshes every 15 s · closes after 5 min\n\n");
        sb.Append("Connection checks (first FAIL = where it breaks)\n");

        var net = SysInfo.Network();
        if (net == null)
            Line(false, "Network (cable or Wi-Fi)", NetworkInterface.GetIsNetworkAvailable() ? "connected, but no router (gateway)" : "not connected");
        else if (net.Type == "wifi")
            Line(true, "Wi-Fi connected", $"{net.Ssid ?? "(name hidden by Windows)"}{(net.Signal is { } s ? $", signal {s}%{(s < 40 ? " (weak)" : "")}" : "")}");
        else
            Line(true, "Network connected", net.Type switch { "ethernet" => "Ethernet", "mobile" => "Mobile data", _ => net.Adapter ?? "other" });

        Line(net?.Ip != null, "IP address", net?.Ip ?? "none (DHCP failed?)");

        if (net?.Gateway is not { } gw) Line(null, "Router (gateway)", "unknown");
        else
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(gw, 2000);
                var ok = reply.Status == IPStatus.Success;
                Line(ok ? true : null, "Router reachable", $"{gw}{(ok ? $" ({reply.RoundtripTime} ms)" : " (no reply; often normal)")}");
            }
            catch
            {
                Line(null, "Router reachable", $"{gw} (no reply; often normal)");
            }
        }

        var host = Uri.TryCreate(Config.ServerUrl, UriKind.Absolute, out var u) ? u.Host : "(none)";
        var dnsOk = false;
        try
        {
            var start = Environment.TickCount64;
            var a = await Dns.GetHostAddressesAsync(host);
            dnsOk = a.Length > 0;
            Line(dnsOk, "Internet names (DNS)", dnsOk ? $"{a[0]} ({Environment.TickCount64 - start} ms)" : "no address");
        }
        catch (Exception e)
        {
            Line(false, "Internet names (DNS)", e.Message);
        }

        var serverOk = false;
        if (dnsOk)
        {
            try
            {
                var start = Environment.TickCount64;
                var r = await Net.Call(HttpMethod.Get, "/", null, null);
                serverOk = r.Code == 200;
                Line(serverOk, "Signage server", serverOk ? $"HTTPS OK ({Environment.TickCount64 - start} ms)" : $"HTTP {r.Code}");
            }
            catch (Exception e)
            {
                Line(false, "Signage server", (e.InnerException ?? e).Message);
            }
        }
        else Line(false, "Signage server", "skipped (DNS failed)");

        var token = Store.Token;
        if (token == null) Line(false, "Pairing key", "none: this screen is not paired");
        else if (!serverOk) Line(null, "Pairing key", "can't check without the server");
        else
        {
            try
            {
                var r = await Net.Call(HttpMethod.Get, "/v1/manifest", token, w.Dispatcher.Invoke(w.DeviceHeaders));
                switch (r.Code)
                {
                    case 200:
                        using (var d = JsonDocument.Parse(r.Body))
                        {
                            var paired = Playlist.Str(d.RootElement, "status") == "paired";
                            Line(paired, "Pairing key", paired ? "accepted (paired)" : "waiting to be paired in the admin page");
                        }
                        break;
                    case 401:
                        Line(false, "Pairing key", "rejected: screen was removed; it will show a new pairing code");
                        break;
                    default:
                        Line(null, "Pairing key", $"HTTP {r.Code}");
                        break;
                }
            }
            catch (Exception e)
            {
                Line(null, "Pairing key", e.Message);
            }
        }

        sb.Append('\n');
        var pl = w.CurrentPlaylist;
        Info("Screen", $"{(w.ScreenName.Length > 0 ? w.ScreenName : "(not paired)")} · playlist {pl?.Name ?? "-"}");
        Info("Player", $"{Config.Version} · {SysInfo.Hardware()}");
        Info("Windows", SysInfo.WindowsName());
        Info("Display", $"{w.ScreenW}x{w.ScreenH} · monitor {(w.MonitorCount > 1 ? $"1 of {w.MonitorCount} (right-click to move)" : "1 of 1")}");
        Info("Last check-in OK", T(w.LastOkMs) + (w.NextSyncMs > 0 ? $"  (next {T(w.NextSyncMs)})" : ""));
        if (w.LastError is { } le) Info("Last error", $"{T(le.at)}: {le.msg}");
        if (pl != null)
        {
            var have = pl.Items.Count(Store.Has);
            Info("Content", $"{have} of {pl.Items.Count} items saved on this PC ({Store.MediaBytes() / 1_000_000} MB){(pl.Paused ? " · PAUSED" : "")}");
        }
        if (w.VideoFailedOnce)
            Info("Video", "a clip failed to play: Windows 'N' editions need the Media Feature Pack");
        var (free, total) = SysInfo.Memory();
        Info("Memory", $"{free} MB free of {total} MB");
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Config.DataDir)!);
            Info("Disk", $"{drive.AvailableFreeSpace / 1_000_000_000.0:0.0} GB free on {drive.Name}");
        }
        catch { }
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        Info("Uptime", $"{up.Days}d {up.Hours}h {up.Minutes}m");
        Info("Clock", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} · {w.ClockOffsetMs / 1000} s from server");
        Info("Server", host);
        Info("Start with Windows", Installer.StartsWithWindows ? "yes" : "no (tray icon → Start with Windows)");
        Info("Log", Log.Path);
        Info("Checked", T(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        return sb.ToString();
    }
}
