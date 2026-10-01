using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Timer = System.Windows.Forms.Timer;

namespace SomaMetalTray;

/// <summary>Now-playing metadata for the current track, source-agnostic (fed to SMTC/Last.fm/Discord/toasts).</summary>
/// <param name="StartedAt">
/// When this track started playing on the station, per SomaFM's "date" field
/// (a Unix timestamp, seconds), if it parsed successfully - null otherwise.
/// Used purely for PlayerForm's cosmetic fake progress bar (see
/// PlayerForm.ComputeFakeProgress); there's no real track-duration API to be
/// accurate against.
/// </param>
/// <param name="StationTrackId">The station's own id for the track when it has one (Death.FM: the album ASIN, used to look up its queue).</param>
/// <param name="Duration">Real track length when the station's own feed reports it (Death.FM does; SomaFM doesn't).</param>
public readonly record struct TrackMetadata(string Title, string Artist, string Album, string? ArtUrl, DateTimeOffset? StartedAt = null, TimeSpan? Duration = null, string? StationTrackId = null);

/// <summary>
/// Polls SomaFM's public "now playing" JSON endpoints for the Metal Detector
/// channel and raises <see cref="MetadataChanged"/> when the current track
/// changes. Unlike DeathFmTray's NowPlayingService (which scraped a WebView2
/// page's DOM), this is a plain unauthenticated HTTP poll - SomaFM has no
/// websocket/push API for this, so short-interval polling is the documented
/// approach their own web player uses too.
/// </summary>
public sealed class SomaFmService : INowPlayingSource
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
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.UserAgentProduct, UpdateChecker.CurrentVersion().ToString(3)));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/lokulin/SomaMetalTray)"));

        _pollTimer = new Timer { Interval = (int)(pollInterval ?? TimeSpan.FromSeconds(18)).TotalMilliseconds };
        _pollTimer.Tick += async (_, _) => await PollSongsAsync();
    }

    public void Stop() => _pollTimer.Stop();

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

            // channels.json is actually {"channels": [...]}, not a bare
            // top-level array as originally assumed here - that assumption
            // made EnumerateArray() throw on every real response, silently
            // caught below, so this method has never actually read live
            // channel branding before now. Handle both shapes defensively,
            // same spirit as PollSongsAsync's array-or-object handling.
            JsonElement channels = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : doc.RootElement.TryGetProperty("channels", out JsonElement channelsEl) ? channelsEl : default;

            if (channels.ValueKind != JsonValueKind.Array)
                return;

            foreach (JsonElement channel in channels.EnumerateArray())
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

            string title = StripLeadingTrackNumber(GetStringProperty(newest, "title") ?? "Unknown Track");
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

            DateTimeOffset? startedAt = long.TryParse(date, out long unixSeconds)
                ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
                : null;

            var metadata = new TrackMetadata(title, artist, album, string.IsNullOrEmpty(art) ? null : art, startedAt);
            CurrentTrack = metadata;
            MetadataChanged?.Invoke(metadata);
        }
        catch
        {
            // Keep last known state and retry on the next tick - a single
            // failed fetch shouldn't disrupt playback or clear the display.
        }
    }

    // Some rips in this station's rotation carry an embedded track number in
    // the title tag (e.g. "01 Ut av deg elv"). Stripped conservatively: only
    // when the leading number is zero-padded (a real title essentially never
    // starts with "01 ") or followed by a "." or "-" separator (the other
    // common "01. Title" / "01 - Title" tagging convention) - a bare leading
    // number with no such signal (e.g. "7 Cries", "1349") is left alone,
    // since that's much more likely to be part of the actual title.
    private static readonly Regex TrackNumberPrefix = new(@"^(?<num>\d{1,3})[\.\-_\s]+(?=\S)", RegexOptions.Compiled);

    internal static string StripLeadingTrackNumber(string title)
    {
        Match match = TrackNumberPrefix.Match(title);
        if (!match.Success)
            return title;

        string num = match.Groups["num"].Value;
        bool looksLikeTrackNumber = (num.Length >= 2 && num[0] == '0') || match.Value.Contains('.') || match.Value.Contains('-');
        if (!looksLikeTrackNumber)
            return title;

        string rest = title[match.Length..].TrimStart();
        return rest.Length > 0 ? rest : title;
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
