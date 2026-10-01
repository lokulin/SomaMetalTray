using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SomaMetalTray;

/// <summary>
/// Scrobbles now-playing tracks to Last.fm via its Audioscrobbler API
/// (plain REST/JSON over HTTPS). Ported from DeathFmTray essentially
/// unchanged - it was already playback-source-agnostic (fed metadata by
/// whoever owns playback state; here that's PlayerForm reacting to
/// AudioPlayerService/SomaFmService instead of a WebView2-hosted page).
///
/// Uses the "desktop application" auth flow: fetch a token (auth.getToken),
/// have the user authorize it in their browser, then exchange it for a
/// session key (auth.getSession) that doesn't expire until revoked. Every
/// authenticated call is signed per Last.fm's spec: sort all parameters
/// (excluding format) by key, concatenate key+value pairs, append the shared
/// secret, then MD5 the result.
/// </summary>
public sealed class LastFmScrobbler : IDisposable
{
    private const string ApiRoot = "https://ws.audioscrobbler.com/2.0/";

    // Last.fm's own guidance: a track qualifies for scrobbling once it's
    // been playing for at least half its length or 4 minutes (whichever is
    // shorter), and is itself longer than 30 seconds. We don't know the
    // actual track length (this is a live internet radio stream), so this
    // uses elapsed wall-clock time since we noticed the track start as a
    // proxy - good enough to avoid scrobbling brief blips without needing
    // exact durations.
    private static readonly TimeSpan MinimumScrobbleDuration = TimeSpan.FromSeconds(30);

    private readonly AppSettings _settings;
    private readonly HttpClient _http = new();

    private TrackMetadata? _currentTrack;
    private DateTimeOffset _currentTrackStartedAt;

    public LastFmScrobbler(AppSettings settings)
    {
        _settings = settings;
    }

    public bool IsAuthorized => !string.IsNullOrEmpty(_settings.LastFmSessionKey);

    public void Dispose() => _http.Dispose();

    /// <summary>Called whenever the current track changes, but only while actually playing.</summary>
    public void OnTrackChanged(TrackMetadata metadata)
    {
        if (!IsAuthorized)
            return;

        if (string.IsNullOrWhiteSpace(metadata.Title) || string.IsNullOrWhiteSpace(metadata.Artist))
            return;

        if (_currentTrack is TrackMetadata prev && prev.Title == metadata.Title && prev.Artist == metadata.Artist)
            return;

        ScrobblePreviousIfQualified();

        _currentTrack = metadata;
        _currentTrackStartedAt = DateTimeOffset.UtcNow;
        _ = UpdateNowPlayingAsync(metadata);
    }

    /// <summary>Called when playback stops - finalizes any scrobble that qualifies before forgetting the track.</summary>
    public void OnPlaybackStopped()
    {
        ScrobblePreviousIfQualified();
        _currentTrack = null;
    }

    private void ScrobblePreviousIfQualified()
    {
        if (_currentTrack is not TrackMetadata prev)
            return;

        if (DateTimeOffset.UtcNow - _currentTrackStartedAt >= MinimumScrobbleDuration)
            _ = ScrobbleAsync(prev, _currentTrackStartedAt);
    }

    private async Task UpdateNowPlayingAsync(TrackMetadata track)
    {
        try
        {
            var parameters = new SortedDictionary<string, string>
            {
                ["method"] = "track.updateNowPlaying",
                ["artist"] = track.Artist,
                ["track"] = track.Title,
                ["api_key"] = AppCredentials.LastFmApiKey,
                ["sk"] = _settings.LastFmSessionKey!,
            };
            if (!string.IsNullOrEmpty(track.Album))
                parameters["album"] = track.Album;

            await PostSignedAsync(parameters);
        }
        catch
        {
            // Best-effort - a failed now-playing update shouldn't affect playback.
        }
    }

    private async Task ScrobbleAsync(TrackMetadata track, DateTimeOffset startedAt)
    {
        try
        {
            var parameters = new SortedDictionary<string, string>
            {
                ["method"] = "track.scrobble",
                ["artist"] = track.Artist,
                ["track"] = track.Title,
                ["timestamp"] = startedAt.ToUnixTimeSeconds().ToString(),
                ["api_key"] = AppCredentials.LastFmApiKey,
                ["sk"] = _settings.LastFmSessionKey!,
            };
            if (!string.IsNullOrEmpty(track.Album))
                parameters["album"] = track.Album;

            await PostSignedAsync(parameters);
        }
        catch
        {
            // Best-effort - Last.fm being unreachable shouldn't affect playback.
        }
    }

    /// <summary>Loves or un-loves a track on the connected Last.fm account. Returns false (never throws) if not connected or the call fails.</summary>
    public async Task<bool> SetLovedAsync(string artist, string title, bool loved)
    {
        if (!IsAuthorized)
            return false;

        try
        {
            var parameters = new SortedDictionary<string, string>
            {
                ["method"] = loved ? "track.love" : "track.unlove",
                ["artist"] = artist,
                ["track"] = title,
                ["api_key"] = AppCredentials.LastFmApiKey,
                ["sk"] = _settings.LastFmSessionKey!,
            };
            using JsonDocument doc = await PostSignedAsync(parameters);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"Last.fm {(loved ? "love" : "unlove")} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Step 1 of the desktop auth flow: get an unauthorized request token.</summary>
    public async Task<string> GetAuthTokenAsync()
    {
        var parameters = new SortedDictionary<string, string>
        {
            ["method"] = "auth.getToken",
            ["api_key"] = AppCredentials.LastFmApiKey,
        };
        using JsonDocument doc = await PostSignedAsync(parameters);
        return doc.RootElement.GetProperty("token").GetString()!;
    }

    /// <summary>Step 2: the URL the user needs to open to grant this app access.</summary>
    public string BuildAuthorizeUrl(string token) =>
        $"https://www.last.fm/api/auth/?api_key={Uri.EscapeDataString(AppCredentials.LastFmApiKey)}&token={Uri.EscapeDataString(token)}";

    /// <summary>Step 3: once the user has authorized in their browser, exchange the token for a session key.</summary>
    public async Task<(string SessionKey, string Username)> CompleteAuthAsync(string token)
    {
        var parameters = new SortedDictionary<string, string>
        {
            ["method"] = "auth.getSession",
            ["api_key"] = AppCredentials.LastFmApiKey,
            ["token"] = token,
        };
        using JsonDocument doc = await PostSignedAsync(parameters);
        JsonElement session = doc.RootElement.GetProperty("session");
        return (session.GetProperty("key").GetString()!, session.GetProperty("name").GetString()!);
    }

    private async Task<JsonDocument> PostSignedAsync(SortedDictionary<string, string> parameters)
    {
        string signature = ComputeSignature(parameters, AppCredentials.LastFmApiSecret);

        var form = new Dictionary<string, string>(parameters)
        {
            ["api_sig"] = signature,
            ["format"] = "json",
        };

        using var content = new FormUrlEncodedContent(form);
        using HttpResponseMessage response = await _http.PostAsync(ApiRoot, content);
        string json = await response.Content.ReadAsStringAsync();

        JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("error", out JsonElement errorEl))
        {
            string message = doc.RootElement.TryGetProperty("message", out JsonElement msgEl) ? msgEl.GetString() ?? "" : "";
            doc.Dispose();
            throw new InvalidOperationException($"Last.fm API error {errorEl.GetInt32()}: {message}");
        }

        return doc;
    }

    // Last.fm's signing spec: sort all parameters (excluding format/callback)
    // by key, concatenate each key immediately followed by its value with no
    // separators, append the shared secret, then MD5 the whole string.
    private static string ComputeSignature(SortedDictionary<string, string> parameters, string secret)
    {
        var sb = new StringBuilder();
        foreach (KeyValuePair<string, string> kvp in parameters)
            sb.Append(kvp.Key).Append(kvp.Value);
        sb.Append(secret);

        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        var hex = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
            hex.Append(b.ToString("x2"));
        return hex.ToString();
    }
}
