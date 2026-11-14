using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SignagePlayer;

/// <summary>
/// Local cache in %LOCALAPPDATA%\SignagePlayer: the pairing key (encrypted for this Windows user with DPAPI),
/// the last good manifest and the media files. Lets the screen keep playing offline.
/// </summary>
static class Store
{
    private static readonly string MediaDir = Path.Combine(Config.DataDir, "media");
    private static readonly string ManifestFile = Path.Combine(Config.DataDir, "manifest.json");
    private static readonly string TokenFile = Path.Combine(Config.DataDir, "token.bin");
    private static readonly string SettingsFile = Path.Combine(Config.DataDir, "settings.json");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("signage-player-token");

    static Store() => Directory.CreateDirectory(MediaDir);

    public static string? Token
    {
        get
        {
            try
            {
                if (!File.Exists(TokenFile)) return null;
                var raw = ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), Entropy, DataProtectionScope.CurrentUser);
                var t = Encoding.ASCII.GetString(raw);
                return t.Length == 64 ? t : null;
            }
            catch (Exception e)
            {
                Log.Warn("Could not read the pairing key", e);
                return null;
            }
        }
        set
        {
            if (value == null) { TryDelete(TokenFile); return; }
            var enc = ProtectedData.Protect(Encoding.ASCII.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
            WriteAtomic(TokenFile, enc);
        }
    }

    public static string MediaPath(string id, bool isVideo) => Path.Combine(MediaDir, id + (isVideo ? ".mp4" : ".jpg"));
    public static string MediaPath(Item i) => MediaPath(i.Id, i.IsVideo);
    public static bool Has(Item i) => new FileInfo(MediaPath(i)) is { Exists: true, Length: > 0 };

    public static void SaveManifest(string json) => WriteAtomic(ManifestFile, Encoding.UTF8.GetBytes(json));

    public static JsonElement? LoadManifest()
    {
        try
        {
            if (!File.Exists(ManifestFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(ManifestFile));
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Deletes saved media that is no longer in the playlist (a file in use is retried next time).</summary>
    public static void Prune(HashSet<string> keep)
    {
        foreach (var f in Directory.EnumerateFiles(MediaDir))
        {
            var name = Path.GetFileName(f);
            var id = name.Split('.')[0];
            if (!keep.Contains(id) || name.EndsWith(".part")) TryDelete(f);
        }
    }

    /// <summary>Wipes everything (the screen was removed in the admin page).</summary>
    public static void ClearAll()
    {
        foreach (var f in Directory.EnumerateFiles(MediaDir)) TryDelete(f);
        TryDelete(ManifestFile);
        Token = null;
    }

    public static long MediaBytes()
    {
        long n = 0;
        foreach (var f in Directory.EnumerateFiles(MediaDir)) try { n += new FileInfo(f).Length; } catch { }
        return n;
    }

    // ---- small local settings (which monitor to use)

    public sealed class Settings
    {
        public string? Monitor { get; set; }
    }

    public static Settings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public static void SaveSettings(Settings s)
    {
        try { WriteAtomic(SettingsFile, JsonSerializer.SerializeToUtf8Bytes(s)); }
        catch (Exception e) { Log.Warn("Could not save settings", e); }
    }

    // ---- helpers

    private static void WriteAtomic(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(data);
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
