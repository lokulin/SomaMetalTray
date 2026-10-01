using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Timer = System.Windows.Forms.Timer;

namespace SomaMetalTray;

/// <summary>
/// Polls (or otherwise watches) one station's now-playing feed. Implementations raise
/// <see cref="MetadataChanged"/> only for a genuinely new track, on the UI thread.
/// </summary>
public interface INowPlayingSource : IDisposable
{
    event Action<TrackMetadata>? MetadataChanged;

    /// <summary>Raised once, after station branding has loaded (may never fire - callers keep their own fallbacks).</summary>
    event Action? StationInfoLoaded;

    string StationTitle { get; }

    /// <summary>Host/DJ line shown under the station name, or empty.</summary>
    string DjName { get; }

    string LogoUrl { get; }

    TrackMetadata? CurrentTrack { get; }

    Task StartAsync();

    /// <summary>Stops polling without disposing (used when switching station).</summary>
    void Stop();
}

/// <summary>One playable station: how to find its stream and how to learn what's on it.</summary>
public interface IStation
{
    /// <summary>Stable id persisted in settings.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Public logo URL, valid before the source has loaded any branding.</summary>
    string FallbackLogoUrl { get; }

    /// <summary>Resolves candidate stream URLs, best first. Empty on failure (caller uses <see cref="FallbackStreamUrls"/>).</summary>
    Task<List<string>> ResolveStreamUrlsAsync(HttpClient http);

    IReadOnlyList<string> FallbackStreamUrls { get; }

    INowPlayingSource CreateNowPlaying();

    /// <summary>Id the Cast receiver knows this station by (its STATIONS registry key).</summary>
    string CastStationId { get; }

    string CastContentUrl { get; }

    string CastContentType { get; }

    /// <summary>
    /// A URL to poke shortly before playing so the stream is warm when Play is pressed (null = nothing to warm). Death.FM's receiver
    /// proxy only starts fast while its upstream connection is open, and it closes that 90s after the last listener.
    /// </summary>
    string? WarmUpUrl { get; }

    /// <summary>True if the station publishes what plays next (shows the Upcoming tab).</summary>
    bool HasUpcoming { get; }

    /// <summary>What plays after <paramref name="current"/>; empty if unknown. Only called when <see cref="HasUpcoming"/>.</summary>
    Task<IReadOnlyList<UpcomingItem>> GetUpcomingAsync(HttpClient http, TrackMetadata current);
}

public static class Stations
{
    public static readonly IStation MetalDetector = new SomaMetalDetectorStation();
    public static readonly IStation DeathFm = new DeathFmStation();

    public static IReadOnlyList<IStation> All { get; } = new[] { DeathFm, MetalDetector };

    public static IStation ById(string? id)
    {
        foreach (IStation station in All)
        {
            if (string.Equals(station.Id, id, StringComparison.OrdinalIgnoreCase))
                return station;
        }
        return All[0];
    }
}

public sealed class SomaMetalDetectorStation : IStation
{
    // "metal130" is the channel's 128kbps AAC stream, the same one somafm.com's own web player defaults to.
    private const string PlsUrl = "https://api.somafm.com/metal130.pls";

    public string Id => "metal";
    public string DisplayName => "Metal Detector";
    public string FallbackLogoUrl => "https://api.somafm.com/logos/512/metal512.png";

    // Used only if the .pls fetch fails outright - ice server hostnames follow a well-known pattern.
    public IReadOnlyList<string> FallbackStreamUrls { get; } = new[]
    {
        "https://ice1.somafm.com/metal-128-mp3",
        "https://ice2.somafm.com/metal-128-mp3",
        "https://ice4.somafm.com/metal-128-mp3",
        "https://ice5.somafm.com/metal-128-mp3",
    };

    public async Task<List<string>> ResolveStreamUrlsAsync(HttpClient http)
    {
        var urls = new List<string>();
        try
        {
            string text = await http.GetStringAsync(PlsUrl);
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim().TrimEnd('\r');

                // .pls entries look like "File1=http://...". Order matters (File1 is the preferred entry).
                if (!line.StartsWith("File", StringComparison.OrdinalIgnoreCase))
                    continue;

                int eq = line.IndexOf('=');
                if (eq > 0 && eq < line.Length - 1)
                {
                    string url = line[(eq + 1)..].Trim();
                    if (Uri.TryCreate(url, UriKind.Absolute, out _))
                        urls.Add(url);
                }
            }
        }
        catch
        {
            // Best-effort - caller falls back to FallbackStreamUrls when this comes back empty.
        }

        return urls;
    }

    public INowPlayingSource CreateNowPlaying() => new SomaFmService();

    public string CastStationId => "somafm-metal";
    public string CastContentUrl => FallbackStreamUrls[0];
    public string CastContentType => "audio/mpeg";


    // SomaFM publishes recent songs but nothing about what is coming next.
    public string? WarmUpUrl => null;

    public bool HasUpcoming => false;

    public Task<IReadOnlyList<UpcomingItem>> GetUpcomingAsync(HttpClient http, TrackMetadata current) =>
        Task.FromResult<IReadOnlyList<UpcomingItem>>(Array.Empty<UpcomingItem>());
}

public sealed class DeathFmStation : IStation
{
    public string Id => "dfm";
    public string DisplayName => "Death.FM";

    // death.fm's own logo URL 404s on their end, so this is our already-hosted copy
    // of the same skull/headphones logo (same asset the Cast skin uses).
    public string FallbackLogoUrl => "https://deathfm-cast.pages.dev/logo.png";

    // In a private build whose local.properties sets DEATHFM_STREAM_PROXY_URL, playback goes through the DeathFmCastReceiver's
    // buffering proxy first, falling back to death.fm directly if it is unreachable (AudioPlayerService moves on to the next URL on a
    // failure or stall). Played directly, the stream is a burst of ~4s of audio and then exactly real time, which Windows' player can't
    // buffer enough of, so it pauses to refill 2-3 times at the start. The proxy presents the same audio as a huge seekable file (see
    // DeathFmCastReceiver/src/deathfm-live-buffer.js) and the player then starts in a quarter of a second and plays straight through.
    // It is the owner's own Worker, so public builds (no local.properties) play directly. Casting is unaffected: the receiver already
    // uses the proxy itself.
    internal const string DirectUrl = "https://death.fm/live";

    /// <summary>Stream URLs, best first: the proxy (if one is configured) and then the direct stream.</summary>
    internal static IReadOnlyList<string> StreamUrls(string? proxyUrl) =>
        string.IsNullOrWhiteSpace(proxyUrl) ? new[] { DirectUrl } : new[] { proxyUrl.Trim(), DirectUrl };

    public IReadOnlyList<string> FallbackStreamUrls { get; } = StreamUrls(PrivateConfig.DeathFmProxyUrl);

    public Task<List<string>> ResolveStreamUrlsAsync(HttpClient http) => Task.FromResult(FallbackStreamUrls.ToList());

    public INowPlayingSource CreateNowPlaying() => new DeathFmNowPlayingSource();

    public string CastStationId => "deathfm";
    public string CastContentUrl => "https://death.fm/live";
    public string CastContentType => "audio/aac";


    public string? WarmUpUrl => FallbackStreamUrls.Count > 1 ? FallbackStreamUrls[0] : null; // only the proxy needs it

    public bool HasUpcoming => true;

    public Task<IReadOnlyList<UpcomingItem>> GetUpcomingAsync(HttpClient http, TrackMetadata current) =>
        current.StationTrackId is { Length: > 0 } asin
            ? DeathFmQueue.FetchQueueAsync(http, "dfm", asin)
            : Task.FromResult<IReadOnlyList<UpcomingItem>>(Array.Empty<UpcomingItem>());
}

/// <summary>
/// Polls death.fm's undocumented now-playing JSON (the same endpoint its own player page and
/// DeathFmAndroid use). Quirks handled here (see the death.fm API notes):
/// - Track/Artist/Album are HTML-entity-encoded.
/// - PlayStart/SystemTime are naive timestamps on a station clock that is ~4h off real UTC, so only
///   their *difference* is trusted; elapsed time is anchored to the local clock at fetch time.
/// - CoverLink is the real art (ThumbnailLink is a smaller variant, not used).
/// - Don't poll faster than ~30s; the next poll is aimed just after the expected end of the track.
/// </summary>
public sealed class DeathFmNowPlayingSource : INowPlayingSource
{
    private const string NowPlayingUrl = "https://death.fm/soap/FM24sevenJSON.php?action=GetCurrentlyPlaying";
    private const string PlaceholderName = "Death.FM";

    private static readonly TimeSpan MaxPollDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinPollDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EndOfTrackBuffer = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly Timer _pollTimer;
    private string _lastTrackKey = "";
    private int _consecutiveFailures;

    public event Action<TrackMetadata>? MetadataChanged;
    public event Action? StationInfoLoaded;

    public string StationTitle => "Death.FM";
    public string DjName => "";
    public string LogoUrl => Stations.DeathFm.FallbackLogoUrl;
    public TrackMetadata? CurrentTrack { get; private set; }

    public DeathFmNowPlayingSource()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SomaMetalTray", "0.1"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/lokulin/SomaMetalTray)"));

        _pollTimer = new Timer { Interval = (int)MaxPollDelay.TotalMilliseconds };
        _pollTimer.Tick += async (_, _) =>
        {
            _pollTimer.Stop();
            await PollAsync();
        };
    }

    public async Task StartAsync()
    {
        StationInfoLoaded?.Invoke(); // branding is static - nothing to fetch
        await PollAsync();
    }

    public void Stop() => _pollTimer.Stop();

    public void Dispose()
    {
        _pollTimer.Dispose();
        _http.Dispose();
    }

    private async Task PollAsync()
    {
        TimeSpan nextDelay;
        try
        {
            string json = await _http.GetStringAsync($"{NowPlayingUrl}&_t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
            using JsonDocument doc = JsonDocument.Parse(json);
            nextDelay = Apply(doc.RootElement);
            _consecutiveFailures = 0;
        }
        catch
        {
            // Keep last known state; back off (doubling, capped) while the endpoint is down - the
            // stream itself can stay up while death.fm's database is down.
            _consecutiveFailures++;
            int shift = Math.Min(_consecutiveFailures - 1, 4);
            nextDelay = TimeSpan.FromSeconds(Math.Min(MaxPollDelay.TotalSeconds * (1 << shift), 300));
        }

        try
        {
            _pollTimer.Interval = Math.Max(1000, (int)nextDelay.TotalMilliseconds);
            _pollTimer.Start();
        }
        catch (ObjectDisposedException)
        {
            // Disposed mid-poll (station switch / exit) - nothing more to schedule.
        }
    }

    /// <summary>Applies one response; returns how long to wait before the next poll.</summary>
    private TimeSpan Apply(JsonElement root)
    {
        string title = Decode(GetString(root, "Track"), PlaceholderName);
        string artist = Decode(GetString(root, "Artist"), PlaceholderName);
        string album = Decode(GetString(root, "Album"), "");
        string? cover = NullIfBlank(GetString(root, "CoverLink"));

        long lengthMs = long.TryParse(GetString(root, "Length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : 0;
        TimeSpan? duration = lengthMs > 0 ? TimeSpan.FromMilliseconds(lengthMs) : null;

        string playStartRaw = GetString(root, "PlayStart");
        TimeSpan elapsed = TimeSpan.Zero;
        if (TryParseStationTime(playStartRaw, out DateTime playStart) && TryParseStationTime(GetString(root, "SystemTime"), out DateTime systemTime))
            elapsed = systemTime > playStart ? systemTime - playStart : TimeSpan.Zero;

        DateTimeOffset startedAt = DateTimeOffset.UtcNow - elapsed;

        string key = $"{title}|{artist}|{playStartRaw}";
        if (key != _lastTrackKey)
        {
            _lastTrackKey = key;
            var metadata = new TrackMetadata(title, artist, album, cover, startedAt, duration, DeathFmQueue.AsinFromSiteLink(GetString(root, "SiteLink")));
            CurrentTrack = metadata;
            MetadataChanged?.Invoke(metadata);
        }

        if (duration is TimeSpan d)
        {
            TimeSpan untilNext = d - elapsed + EndOfTrackBuffer;
            if (untilNext < MinPollDelay) return MinPollDelay;
            return untilNext < MaxPollDelay ? untilNext : MaxPollDelay;
        }
        return MaxPollDelay;
    }

    // Both fields are parsed with the same (wrong) assumption, so the station's real offset cancels
    // out when they're subtracted.
    private static bool TryParseStationTime(string raw, out DateTime value) =>
        DateTime.TryParseExact(raw, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    private static string Decode(string raw, string fallback)
    {
        string decoded = WebUtility.HtmlDecode(raw).Trim();
        return decoded.Length > 0 ? decoded : fallback;
    }

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
            return "";

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => "",
        };
    }
}
