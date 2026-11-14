using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SignagePlayer;

sealed record Item(string Id, string? StartDate, string? EndDate, bool IsVideo, long DurationMs);

/// <summary>What the server's /v1/manifest says to show. Same rules as the Android and Pi players.</summary>
sealed class Playlist
{
    public string Name = "";
    public int IntervalSec = 10;
    public bool Sync = true;
    public string Tz = "UTC";
    public List<Item> Items = new();
    public bool Paused;
    public string PauseMessage = "";

    /// <summary>Changes when anything that affects what's on screen changes.</summary>
    public string Signature => $"{IntervalSec}|{Sync}|{Tz}|{Paused}|{PauseMessage}|" +
        string.Join(",", Items.Select(i => $"{i.Id}:{i.StartDate}:{i.EndDate}:{i.IsVideo}:{i.DurationMs}"));

    /// <summary>Today's date (yyyy-MM-dd) in the company's time zone; start/end dates switch at midnight there.</summary>
    public string Today(long nowMs)
    {
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(nowMs);
        try
        {
            // .NET on Windows understands IANA names ("America/New_York") through ICU.
            var tz = TimeZoneInfo.FindSystemTimeZoneById(Tz);
            return TimeZoneInfo.ConvertTime(utc, tz).ToString("yyyy-MM-dd");
        }
        catch
        {
            return utc.ToLocalTime().ToString("yyyy-MM-dd");
        }
    }

    public static Playlist Parse(JsonElement j)
    {
        var pl = j.GetProperty("playlist");
        var pm = Str(j, "pause_message") ?? "";
        var p = new Playlist
        {
            Name = Str(pl, "name") ?? "",
            IntervalSec = Math.Clamp(Int(pl, "interval_sec", 10), 3, 3600),
            Sync = Bool(pl, "sync", true),
            Tz = Str(j, "tz") ?? "UTC",
            Paused = Bool(j, "paused", false),
            PauseMessage = pm.Length > 120 ? pm[..120] : pm,
        };
        foreach (var o in j.GetProperty("images").EnumerateArray())
        {
            var id = Str(o, "id");
            if (id == null || !IsId(id)) continue;
            p.Items.Add(new Item(id, Str(o, "start_date"), Str(o, "end_date"), Str(o, "kind") == "video", Long(o, "duration_ms", 0)));
        }
        return p;
    }

    /// <summary>Media ids are 32 hex characters; anything else is ignored (they become file names).</summary>
    public static bool IsId(string s) => s.Length == 32 && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string? Str(JsonElement o, string k) =>
        o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
    public static int Int(JsonElement o, string k, int d) =>
        o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : d;
    public static long Long(JsonElement o, string k, long d) =>
        o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : d;
    public static bool Bool(JsonElement o, string k, bool d) =>
        o.TryGetProperty(k, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => d } : d;
}
