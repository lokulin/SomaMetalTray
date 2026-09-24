using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Timer = System.Windows.Forms.Timer;

namespace SomaMetalTray;

/// <summary>Now-playing metadata for the current track, source-agnostic (fed to SMTC/Last.fm/Discord/toasts).</summary>
public readonly record struct TrackMetadata(string Title, string Artist, string Album, string? ArtUrl);

/// <summary>
/// Polls SomaFM's public "now playing" JSON endpoints for the Metal Detector
/// channel and raises <see cref="MetadataChanged"/> when the current track
/// changes. Unlike DeathFmTray's NowPlayingService (which scraped a WebView2
/// page's DOM), this is a plain unauthenticated HTTP poll - SomaFM has no
/// websocket/push API for this, so short-interval polling is the documented
/// approach their own web player uses too.
/// </summary>
public sealed class SomaFmService : IDisposable
{
    private const string ChannelId = "metal";
    private const string SongsUrl = "https://somafm.com/songs/metal.json";
    private const string ChannelsUrl = "https://somafm.com/channels.json";

    // Fallback branding if channels.json is unreachable at startup (its own
    // "Metal Detector" channel page, confirmed against the site as of 2026).
    private const string FallbackStationTitle = "Metal Detector";
    private const string FallbackDjName = "Mark Luntzel";
    private const string FallbackLogoUrl = "https://api.somafm.com/logos/512/metal512.png";

    private readonly HttpClient _http;
    private readonly Timer _pollTimer;

    private string _lastTrackKey = "";

    public event Action<TrackMetadata>? MetadataChanged;

    /// <summary>Raised once, after the first successful channels.json fetch (or never, if it fails - callers should have their own fallback text ready).</summary>
    public event Action? StationInfoLoaded;

    public string StationTitle { get; private set; } = FallbackStationTitle;
    public string DjName { get; private set; } = FallbackDjName;
    public string LogoUrl { get; private set; } = FallbackLogoUrl;

    public TrackMetadata? CurrentTrack { get; private set; }

    public SomaFmService(TimeSpan? pollInterval = null)
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SomaMetalTray", "0.1"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/lokulin/SomaMetalTray)"));

        _pollTimer = new Timer { Interval = (int)(pollInterval ?? TimeSpan.FromSeconds(18)).TotalMilliseconds };
        _pollTimer.Tick += async (_, _) => await PollSongsAsync();
    }

    public void Dispose()
    {
        _pollTimer.Dispose();
        _http.Dispose();
    }

    /// <summary>Fetches channel branding once and starts the recurring song poll. Best-effort - failures here don't stop the app.</summary>
    public async Task StartAsync()
    {
        await FetchChannelInfoAsync();
        await PollSongsAsync();
        _pollTimer.Start();
    }

    private async Task FetchChannelInfoAsync()
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(await _http.GetStringAsync(ChannelsUrl));

            // channels.json is a top-level array of channel objects; find "metal".
            foreach (JsonElement channel in doc.RootElement.EnumerateArray())
            {
                string? id = GetStringProperty(channel, "id");
                if (!string.Equals(id, ChannelId, StringComparison.OrdinalIgnoreCase))
                    continue;

                StationTitle = GetStringProperty(channel, "title") ?? FallbackStationTitle;
                DjName = GetStringProperty(channel, "dj") ?? FallbackDjName;

                // SomaFM's channels.json exposes a few image size variants
                // under different keys across API versions - try the ones
                // documented/observed, then fall back to the well-known
                // pattern (https://api.somafm.com/logos/512/<id>512.png).
                LogoUrl = GetStringProperty(channel, "xlimage")
                    ?? GetStringProperty(channel, "largeimage")
                    ?? GetStringProperty(channel, "image")
                    ?? FallbackLogoUrl;

                break;
            }

            StationInfoLoaded?.Invoke();
        }
        catch
        {
            // Keep the hardcoded fallbacks - best-effort, never blocks startup.
        }
    }

    private async Task PollSongsAsync()
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(await _http.GetStringAsync(SongsUrl));

            // songs/<channel>.json is either a top-level array of song
            // entries, or an object with a "songs" array - handle both since
            // SomaFM's undocumented endpoints have changed shape before.
            JsonElement songs = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : doc.RootElement.TryGetProperty("songs", out JsonElement songsEl) ? songsEl : default;

            if (songs.ValueKind != JsonValueKind.Array || songs.GetArrayLength() == 0)
                return;

            JsonElement newest = songs[0];

            string title = GetStringProperty(newest, "title") ?? "Unknown Track";
            string artist = GetStringProperty(newest, "artist") ?? StationTitle;
            string album = GetStringProperty(newest, "album") ?? "";
            string? art = GetStringProperty(newest, "albumArt")
                ?? GetStringProperty(newest, "albumart")
                ?? GetStringProperty(newest, "art");
            string date = GetStringProperty(newest, "date") ?? "";

            string key = $"{title}|{artist}|{date}";
            if (key == _lastTrackKey)
                return;

            _lastTrackKey = key;

            var metadata = new TrackMetadata(title, artist, album, string.IsNullOrEmpty(art) ? null : art);
            CurrentTrack = metadata;
            MetadataChanged?.Invoke(metadata);
        }
        catch
        {
            // Keep last known state and retry on the next tick - a single
            // failed fetch shouldn't disrupt playback or clear the display.
        }
    }

    private static string? GetStringProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(name, out JsonElement value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }
}
