using System;
using System.IO;
using System.Text.Json;

namespace SomaMetalTray;

/// <summary>Everything about the app's state that should survive a restart.</summary>
public sealed class AppSettings
{
    public bool StartMinimizedToTray { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool ShowTrackChangeNotifications { get; set; } = true;

    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    // Id of the last-selected station (see Stations). Null means "never
    // chose one" - Stations.ById then returns the default.
    public string? StationId { get; set; }

    // Notify (tray menu + one balloon) when a newer GitHub release exists. Nothing is ever downloaded or installed.
    public bool CheckForUpdates { get; set; } = true;

    // Newest version we've already shown a balloon for, so it isn't repeated every launch.
    public string? LastNotifiedVersion { get; set; }

    // Last-used window layout (see ViewMode). Null means the full player.
    public ViewMode ViewMode { get; set; } = ViewMode.Full;

    // Null means "never changed it, use the default" rather than baking a
    // value in twice. Owned by AudioPlayerService (there's no webview <audio>
    // element for a separate VolumeService to wrap here).
    public double? Volume { get; set; }

    // Playback engine: "bass" or "mediafoundation" (see AudioEngines). Null means the build's default; the
    // BLASTBEAT_ENGINE environment variable overrides this.
    public string? AudioEngine { get; set; }

    // Last.fm scrobbling. The API key/secret identifying this app to Last.fm's
    // API are compiled-in constants (see AppCredentials), not per-user
    // settings. LastFmSessionKey/LastFmUsername are filled in automatically
    // by the "Connect Last.fm..." flow once you've authorized the app in
    // your browser.
    public string? LastFmSessionKey { get; set; }
    public string? LastFmUsername { get; set; }

    // Discord Rich Presence. The Client ID/default image key are compiled-in
    // constants (see AppCredentials) - no OAuth/user consent needed beyond
    // that, unlike Last.fm, since this only talks to your own
    // already-running Discord desktop client over a local pipe.
    public bool DiscordPresenceEnabled { get; set; } = true;
}

/// <summary>Reads/writes AppSettings as JSON under %AppData%\BlastbeatPlayer\settings.json.</summary>
public static class SettingsStore
{
    private static readonly string SettingsDirectory = AppInfo.RoamingDir;

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults rather than crash on startup.
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort save; a failure here shouldn't take the app down.
        }
    }
}
