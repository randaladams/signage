using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SignagePlayer;

/// <summary>Fixed facts about this build and where it keeps its files.</summary>
static class Config
{
    /// <summary>Reported to the server as X-App-Version. The "win-" prefix tells the portal this is a Windows PC.</summary>
    public static readonly string Version = "win-" + (Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");

    /// <summary>Built in at publish time (-p:SignageServerUrl=...). SIGNAGE_SERVER_URL overrides it, for development.</summary>
    public static readonly string ServerUrl = (Environment.GetEnvironmentVariable("SIGNAGE_SERVER_URL") is { Length: > 0 } env
        ? env
        : Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ServerUrl")?.Value ?? "").TrimEnd('/');

    public const string AppName = "SignagePlayer";
    public const string DisplayName = "Signage Player";

    /// <summary>%LOCALAPPDATA%\Programs\SignagePlayer: where the .exe installs itself (per user, no admin rights).</summary>
    public static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
    public static readonly string InstalledExe = Path.Combine(InstallDir, AppName + ".exe");

    /// <summary>%LOCALAPPDATA%\SignagePlayer: pairing key, saved playlist, media, settings, log.</summary>
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
}
