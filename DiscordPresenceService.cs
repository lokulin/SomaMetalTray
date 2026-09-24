using System;
using DiscordRPC;

namespace SomaMetalTray;

/// <summary>
/// Shows the current track as a Discord Rich Presence status, via Discord's
/// local RPC (a named pipe the Discord desktop client listens on - nothing
/// shows up unless Discord is actually running; DiscordRpcClient handles
/// connecting and reconnecting to it in the background on its own).
///
/// Ported from DeathFmTray unchanged aside from branding - fed by PlayerForm
/// the same way as LastFmScrobbler: only while actually playing.
/// </summary>
public sealed class DiscordPresenceService : IDisposable
{
    private readonly AppSettings _settings;
    private DiscordRpcClient? _client;

    public DiscordPresenceService(AppSettings settings)
    {
        _settings = settings;
    }

    public bool IsConfigured => _settings.DiscordPresenceEnabled && !string.IsNullOrEmpty(_settings.DiscordClientId);

    /// <summary>Opens the connection to Discord's local RPC pipe. Safe to call even if Discord isn't running yet.</summary>
    public void Start()
    {
        if (!IsConfigured || _client is not null)
            return;

        _client = new DiscordRpcClient(_settings.DiscordClientId);
        _client.Initialize();
    }

    /// <summary>Tears down and re-opens the connection - used after the Client ID/enabled toggle is changed via the settings dialog.</summary>
    public void Restart()
    {
        _client?.Dispose();
        _client = null;
        Start();
    }

    public void OnTrackChanged(TrackMetadata metadata)
    {
        if (_client is null)
            return;

        if (string.IsNullOrWhiteSpace(metadata.Title) || string.IsNullOrWhiteSpace(metadata.Artist))
            return;

        var presence = new RichPresence
        {
            Details = Truncate(metadata.Title),
            State = Truncate(metadata.Artist),
            Timestamps = new Timestamps { Start = DateTime.UtcNow },
        };

        // Discord accepts a direct external image URL here (not just a
        // pre-uploaded asset key) - falls back to whatever default image key
        // is configured (uploaded under Rich Presence -> Art Assets in the
        // Discord Developer Portal for your own application) when a track
        // has no album art of its own yet.
        string? imageKey = !string.IsNullOrEmpty(metadata.ArtUrl) ? metadata.ArtUrl : _settings.DiscordDefaultImageKey;
        if (!string.IsNullOrEmpty(imageKey))
        {
            presence.Assets = new Assets
            {
                LargeImageKey = imageKey,
                LargeImageText = "SomaFM Metal Detector",
            };
        }

        _client.SetPresence(presence);
    }

    public void OnPlaybackStopped() => _client?.ClearPresence();

    // Discord silently rejects Details/State over 128 characters; keep it well under that.
    private static string Truncate(string value) => value.Length <= 120 ? value : value[..117] + "...";

    public void Dispose() => _client?.Dispose();
}
