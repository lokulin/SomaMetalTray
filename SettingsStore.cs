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

    // SomaMetalTray only ever plays one channel (SomaFM's "Metal Detector"),
    // so unlike DeathFmTray there's no station-switching setting here.

    // Null means "never changed it, use the default" rather than baking a
    // value in twice. Owned by AudioPlayerService (there's no webview <audio>
    // element for a separate VolumeService to wrap here).
    public double? Volume { get; set; }

    // Last.fm scrobbling. LastFmApiKey/LastFmApiSecret identify this app to
    // Last.fm's API - register a free one at last.fm/api/account/create and
    // paste them in here (not committed to source, since this file lives
    // under %AppData%, well away from the git repo). LastFmSessionKey/
    // LastFmUsername are filled in automatically by the "Connect Last.fm..."
    // flow once you've authorized the app in your browser.
    public string? LastFmApiKey { get; set; }
    public string? LastFmApiSecret { get; set; }
    public string? LastFmSessionKey { get; set; }
    public string? LastFmUsername { get; set; }

    // Discord Rich Presence. Register a free application at
    // discord.com/developers/applications to get a Client ID - no OAuth/user
    // consent needed beyond that, unlike Last.fm, since this only talks to
    // your own already-running Discord desktop client over a local pipe.
    // DiscordDefaultImageKey is optional: the asset key of an image uploaded
    // under Rich Presence -> Art Assets for that application, shown when the
    // current track has no album art of its own yet.
    public string? DiscordClientId { get; set; }
    public string? DiscordDefaultImageKey { get; set; }
    public bool DiscordPresenceEnabled { get; set; } = true;

    // Optional. ArtworkService's fanart.tv lookup (first in its source chain,
    // ahead of Deezer/iTunes) is skipped entirely when this is empty - a
    // fanart.tv personal API key isn't provisioned anywhere for this project
    // yet, so this just sits unused until one is added here. Get a free
    // personal key at fanart.tv/get-an-api-key.
    public string? FanArtTvApiKey { get; set; }
}

/// <summary>Reads/writes AppSettings as JSON under %AppData%\SomaMetalTray\settings.json.</summary>
public static class SettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SomaMetalTray");

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
