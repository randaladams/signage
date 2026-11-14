using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace SignagePlayer;

/// <summary>
/// The downloaded .exe installs itself for the current Windows user, without admin rights:
///   - copies itself to %LOCALAPPDATA%\Programs\SignagePlayer\SignagePlayer.exe (replacing an older copy),
///   - starts with Windows (HKCU Run key; can be switched off from the tray icon),
///   - adds a Start menu shortcut and an entry in Settings → Apps (for uninstalling).
/// Running a newer download again updates it in place and keeps the pairing.
/// </summary>
static class Installer
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Config.AppName;
    /// <summary>Signalled to ask a running player to close (used by updates and uninstall).</summary>
    public const string QuitEventName = @"Local\SignagePlayer.Quit";
    public const string MutexName = @"Local\SignagePlayer.Running";

    private static string StartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), Config.DisplayName + ".lnk");

    public static bool IsInstalledCopy =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(Config.InstalledExe),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies this .exe into place and registers it. Returns false (with a message) if it couldn't.</summary>
    public static bool Install(out string error)
    {
        error = "";
        try
        {
            StopRunningPlayer();
            Directory.CreateDirectory(Config.InstallDir);
            var src = Environment.ProcessPath!;
            Exception? last = null;
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    File.Copy(src, Config.InstalledExe, overwrite: true);
                    last = null;
                    break;
                }
                catch (IOException e)
                {
                    last = e;   // the old copy may still be closing
                    Thread.Sleep(500);
                }
            }
            if (last != null) throw last;

            SetStartWithWindows(true);
            CreateShortcut();
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", Config.DisplayName);
                k.SetValue("DisplayVersion", Config.Version.Replace("win-", ""));
                k.SetValue("Publisher", "Signage");
                k.SetValue("DisplayIcon", Config.InstalledExe);
                k.SetValue("InstallLocation", Config.InstallDir);
                k.SetValue("UninstallString", $"\"{Config.InstalledExe}\" --uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(new FileInfo(Config.InstalledExe).Length / 1024), RegistryValueKind.DWord);
            }
            Log.Info($"Installed {Config.Version} to {Config.InstalledExe}");
            return true;
        }
        catch (Exception e)
        {
            Log.Warn("Install failed", e);
            error = e.Message;
            return false;
        }
    }

    /// <summary>Removes the start-up entry, shortcut, Settings entry, saved media and pairing.
    /// Call <see cref="RemoveProgramFolderAfterExit"/> last.</summary>
    public static void Uninstall()
    {
        StopRunningPlayer();
        SetStartWithWindows(false);
        Store.TryDelete(StartMenuShortcut);
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        try { Directory.Delete(Config.DataDir, true); } catch { }
        Log.Info("Uninstalled");
    }

    /// <summary>This .exe may be the one being deleted: a short-lived cmd removes the folder once we've exited.</summary>
    public static void RemoveProgramFolderAfterExit()
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{Config.InstallDir}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch { }
    }

    public static bool StartsWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Config.AppName) is string;
        }
    }

    public static void SetStartWithWindows(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(Config.AppName, $"\"{Config.InstalledExe}\"");
            else k.DeleteValue(Config.AppName, false);
        }
        catch (Exception e)
        {
            Log.Warn("Could not change start-up setting", e);
        }
    }

    /// <summary>Asks a running player (an older version, or the installed copy) to close, and waits for it.</summary>
    public static void StopRunningPlayer()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(QuitEventName, out var ev)) return;
            using (ev) ev.Set();
            using var m = new Mutex(false, MutexName);
            try
            {
                if (m.WaitOne(TimeSpan.FromSeconds(10))) m.ReleaseMutex();
            }
            catch (AbandonedMutexException)
            {
                // it exited without releasing: that's fine
            }
        }
        catch (Exception e)
        {
            Log.Warn("Could not stop the running player", e);
        }
    }

    private static void CreateShortcut()
    {
        try
        {
            // Windows Script Host's shortcut object: the simplest way to write a .lnk without extra libraries.
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic lnk = shell.CreateShortcut(StartMenuShortcut);
            lnk.TargetPath = Config.InstalledExe;
            lnk.WorkingDirectory = Config.InstallDir;
            lnk.Description = Config.DisplayName;
            lnk.IconLocation = Config.InstalledExe + ",0";
            lnk.Save();
        }
        catch (Exception e)
        {
            Log.Warn("Could not create the Start menu shortcut", e);
        }
    }
}
