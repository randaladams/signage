using System;
using System.IO;

namespace SignagePlayer;

/// <summary>Small text log in the data folder (log.txt; the previous one is kept as log.old.txt).</summary>
static class Log
{
    private static readonly object Lock = new();
    private static string PathNow => System.IO.Path.Combine(Config.DataDir, "log.txt");
    public static string Path => PathNow;

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg, Exception? e = null) => Write("WARN", e == null ? msg : $"{msg}: {e.GetType().Name}: {e.Message}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Config.DataDir);
                var p = PathNow;
                var fi = new FileInfo(p);
                if (fi.Exists && fi.Length > 1_000_000)
                {
                    var old = System.IO.Path.Combine(Config.DataDir, "log.old.txt");
                    File.Delete(old);
                    File.Move(p, old);
                }
                File.AppendAllText(p, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {msg}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never break the player
        }
    }
}
