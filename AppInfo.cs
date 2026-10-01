using System;
using System.IO;

namespace SomaMetalTray;

/// <summary>User-visible app identity in one place (window titles, dialogs, tray tooltip).</summary>
public static class AppInfo
{
    public const string Name = "Blastbeat Player";

    /// <summary>Folder name under %AppData% (settings, likes, history) and %LocalAppData% (log, art cache).</summary>
    public const string DataFolderName = "BlastbeatPlayer";

    /// <summary>The name this app's data folder had before the rename (migrated from on first run).</summary>
    public const string LegacyDataFolderName = "SomaMetalTray";

    /// <summary>
    /// Identifies this process to Windows for taskbar grouping, the media flyout / volume mixer name and toasts.
    /// Must match the Start Menu shortcut stamped by AumidShortcutHelper.
    /// </summary>
    public const string AppUserModelId = "TerraEclectic.BlastbeatPlayer.v1";

    public const string SingleInstanceMutexName = "BlastbeatPlayer_SingleInstance_7c14a9d1";

    /// <summary>Product token for the User-Agent sent to MusicBrainz and friends (they ask for a descriptive one).</summary>
    public const string UserAgentProduct = "BlastbeatPlayer";

    public static string RoamingDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DataFolderName);

    public static string LocalDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolderName);

    /// <summary>GitHub "owner/repo" the update check and Releases link point at.</summary>
    public const string GitHubRepo = "lokulin/SomaMetalTray";
}
