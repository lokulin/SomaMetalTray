using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SomaMetalTray;

/// <summary>
/// Looks up album art for a track through a source chain, since SomaFM's own
/// song-history JSON often has an empty albumArt field and this station plays
/// a lot of obscure/underground metal that not every source has covered:
///
///   1. fanart.tv - best art when it has coverage, but keyed by MusicBrainz
///      release-group ID rather than free-text search, so this resolves
///      artist+title -> a MusicBrainz recording -> its release-group first.
///      Uses this app's own compiled-in key (AppCredentials.FanArtTvApiKey);
///      any failure/403 just falls through to the next source.
///   2. Deezer - no auth needed, free-text search, this is the workhorse in
///      practice (broad catalog, no key to configure, no MusicBrainz
///      round-trip first).
///   3. Bandcamp - a lot of this station's more obscure/underground bands are
///      only on Bandcamp, not covered by fanart.tv/Deezer/iTunes at all.
///      Bandcamp has no official public API; this uses the same undocumented
///      autocomplete endpoint their own site search box calls
///      (bcsearch_public_api/1/autocomplete_elastic). Unverified against a
///      live response as of writing (see the method below) - implemented
///      defensively so any unexpected shape/status just falls through to the
///      next source, same as every other source here.
///   4. iTunes - kept as a last-resort fallback since it tops out at a lower
///      resolution than the others and has the weakest coverage of
///      underground metal of the bunch.
///   5. The station's own logo (metal512.png), if all four come back empty.
///
/// iTunes/MusicBrainz/fanart.tv all have informal or explicit rate limits
/// (MusicBrainz: 1 req/sec, enforced below), and a large share of the artists
/// on this station return zero results everywhere - very underground stuff
/// that was never released commercially. So lookups are cached aggressively:
/// in memory for the process lifetime, and on disk under
/// %LOCALAPPDATA%\SomaMetalTray\ArtCache, so a restart doesn't re-spend
/// rate-limit budget re-fetching tracks already looked up before. A "no art
/// anywhere" result is cached too (with a shorter TTL, so a track that's
/// simply not out yet isn't retried forever, but also isn't re-hammered
/// across all three sources every ~18s poll tick).
/// </summary>
public sealed class ArtworkService : IDisposable
{
    private static readonly TimeSpan NegativeResultTtl = TimeSpan.FromDays(1);
    private static readonly TimeSpan MusicBrainzMinInterval = TimeSpan.FromMilliseconds(1100); // MusicBrainz asks for <=1 req/sec

    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private readonly string _diskCacheDir;
    private readonly Dictionary<string, CacheEntry> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    private DateTimeOffset _lastMusicBrainzRequestUtc = DateTimeOffset.MinValue;

    private Image? _fallbackLogo;
    private string? _fallbackLogoUrl;

    /// <summary>Local disk path of the cached station logo, once <see cref="GetFallbackLogoAsync"/> has fetched it - usable directly as a file:// art source for SMTC/toasts.</summary>
    public string? FallbackLogoPath { get; private set; }

    private readonly record struct CacheEntry(Image? Image, string? SourceUrl, TimeSpan? Duration, DateTimeOffset CachedAt)
    {
        public bool IsExpiredNegative => Image is null && DateTimeOffset.UtcNow - CachedAt >= NegativeResultTtl;
    }

    public ArtworkService(AppSettings settings)
    {
        _settings = settings;

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        // MusicBrainz requires a descriptive User-Agent identifying the app
        // (with a way to contact the maintainer); the other sources don't
        // require it but it's good citizenship to identify ourselves anyway.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SomaMetalTray", "0.1"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/lokulin/SomaMetalTray)"));

        _diskCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SomaMetalTray", "ArtCache");
    }

    public void Dispose()
    {
        _http.Dispose();
        _lock.Dispose();
        foreach (CacheEntry entry in _memoryCache.Values)
            entry.Image?.Dispose();
        _fallbackLogo?.Dispose();
    }

    /// <summary>
    /// Looks up artwork for a track, in order: memory cache, disk cache, then
    /// the fanart.tv -> Deezer -> Bandcamp -> iTunes source chain. Returns null
    /// (letting the caller fall back to the station logo via
    /// <see cref="GetFallbackLogoAsync"/>) on a zero-result search anywhere
    /// or any failure. Never throws - a bad lookup should never take down
    /// the UI it's feeding. The remote URL the winning source resolved to is
    /// available afterwards via <see cref="GetCachedArtSourceUrl"/>, for
    /// consumers (Discord Rich Presence) that need a public URL rather than a
    /// local file.
    /// </summary>
    public async Task<Image?> GetArtworkAsync(string artist, string title, string album, CancellationToken ct)
    {
        string key = BuildCacheKey(artist, title);

        // One lookup in flight at a time - avoids hammering these APIs with
        // parallel requests if the UI asks for artwork for several tracks in
        // quick succession (e.g. rapid track changes while testing).
        await _lock.WaitAsync(ct);
        try
        {
            if (_memoryCache.TryGetValue(key, out CacheEntry cached) && !cached.IsExpiredNegative)
                return cached.Image;

            (Image? diskImage, string? diskSourceUrl, TimeSpan? diskDuration, bool isNegative, bool isExpired) = TryLoadFromDisk(key);
            if (diskImage is not null)
            {
                _memoryCache[key] = new CacheEntry(diskImage, diskSourceUrl, diskDuration, DateTimeOffset.UtcNow);
                return diskImage;
            }
            if (isNegative && !isExpired)
            {
                _memoryCache[key] = new CacheEntry(null, null, null, DateTimeOffset.UtcNow);
                return null;
            }

            (Image? Image, string? SourceUrl, TimeSpan? Duration) fetched = await TryFetchFromFanArtTvAsync(artist, title, album, ct);
            if (fetched.Image is null) fetched = await TryFetchFromDeezerAsync(artist, title, ct);
            if (fetched.Image is null) fetched = await TryFetchFromBandcampAsync(artist, title, ct);
            if (fetched.Image is null) fetched = await TryFetchFromItunesAsync(artist, title, ct);

            _memoryCache[key] = new CacheEntry(fetched.Image, fetched.SourceUrl, fetched.Duration, DateTimeOffset.UtcNow);

            if (fetched.Image is not null)
                TrySaveToDisk(key, fetched.Image, fetched.SourceUrl, fetched.Duration);
            else
                TrySaveNegativeMarkerToDisk(key);

            return fetched.Image;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The public remote URL a previous <see cref="GetArtworkAsync"/> call
    /// resolved this artist+title's art to (whichever source won), if any -
    /// usable as-is for consumers that need a fetchable public URL (Discord
    /// Rich Presence) rather than a local file path (see
    /// <see cref="GetCachedArtPath"/> for that).
    /// </summary>
    public string? GetCachedArtSourceUrl(string artist, string title)
    {
        string key = BuildCacheKey(artist, title);
        if (_memoryCache.TryGetValue(key, out CacheEntry cached))
            return cached.SourceUrl;

        string urlPath = DiskCachePath(key, positive: true) + ".url";
        try
        {
            return File.Exists(urlPath) ? File.ReadAllText(urlPath).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The track duration a previous <see cref="GetArtworkAsync"/> call
    /// picked up alongside the art (Deezer/iTunes/MusicBrainz all report it
    /// in the same response used for cover art - Bandcamp/fanart.tv don't),
    /// if any. Null means no source had a duration for this track, not that
    /// the lookup failed - callers should fall back to a cosmetic estimate.
    /// </summary>
    public TimeSpan? GetCachedDuration(string artist, string title)
    {
        string key = BuildCacheKey(artist, title);
        if (_memoryCache.TryGetValue(key, out CacheEntry cached))
            return cached.Duration;

        string durationPath = DiskCachePath(key, positive: true) + ".duration";
        try
        {
            if (File.Exists(durationPath) && double.TryParse(File.ReadAllText(durationPath).Trim(), out double seconds))
                return TimeSpan.FromSeconds(seconds);
        }
        catch
        {
            // Fall through to null.
        }
        return null;
    }

    /// <summary>Fetches (once) and caches the station's own logo, for use when a track has no art anywhere.</summary>
    public async Task<Image?> GetFallbackLogoAsync(string logoUrl, CancellationToken ct)
    {
        if (_fallbackLogo is not null && string.Equals(_fallbackLogoUrl, logoUrl, StringComparison.Ordinal))
            return _fallbackLogo;

        try
        {
            byte[] bytes = await _http.GetByteArrayAsync(logoUrl, ct);
            Image image = LoadIndependentImage(bytes);

            _fallbackLogo?.Dispose();
            _fallbackLogo = image;
            _fallbackLogoUrl = logoUrl;

            try
            {
                Directory.CreateDirectory(_diskCacheDir);
                string path = Path.Combine(_diskCacheDir, "_station_logo.jpg");
                await File.WriteAllBytesAsync(path, bytes, ct);
                FallbackLogoPath = path;
            }
            catch
            {
                // Best-effort - SMTC/toast art just falls back to having none for this track.
                FallbackLogoPath = null;
            }

            return _fallbackLogo;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The local disk-cache file path for a track's art, if <see cref="GetArtworkAsync"/>
    /// resolved one for this artist+title - usable directly as a file:// art source for
    /// SMTC/toasts without a second network round-trip to whichever remote source it came from.
    /// </summary>
    public string? GetCachedArtPath(string artist, string title)
    {
        string path = DiskCachePath(BuildCacheKey(artist, title), positive: true);
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0 ? path : null;
    }

    // --- Source 1: fanart.tv (via a MusicBrainz recording -> release-group resolution) ---

    private async Task<(Image? Image, string? SourceUrl, TimeSpan? Duration)> TryFetchFromFanArtTvAsync(string artist, string title, string album, CancellationToken ct)
    {
        string apiKey = AppCredentials.FanArtTvApiKey;

        try
        {
            (string? releaseGroupId, TimeSpan? duration) = await ResolveMusicBrainzReleaseGroupIdAsync(artist, title, ct);
            if (string.IsNullOrEmpty(releaseGroupId))
                return (null, null, null);

            string url = $"https://webservice.fanart.tv/v3/music/albums/{releaseGroupId}?api_key={Uri.EscapeDataString(apiKey)}";
            using HttpResponseMessage response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return (null, null, null); // Includes 403 (bad/unauthorized key) - just fall through.

            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("albums", out JsonElement albums) || albums.ValueKind != JsonValueKind.Object)
                return (null, null, null);

            foreach (JsonProperty albumEntry in albums.EnumerateObject())
            {
                if (!albumEntry.Value.TryGetProperty("albumcover", out JsonElement covers) || covers.ValueKind != JsonValueKind.Array || covers.GetArrayLength() == 0)
                    continue;

                string? coverUrl = covers[0].TryGetProperty("url", out JsonElement urlEl) ? urlEl.GetString() : null;
                if (string.IsNullOrEmpty(coverUrl))
                    continue;

                byte[] bytes = await _http.GetByteArrayAsync(coverUrl, ct);
                return (LoadIndependentImage(bytes), coverUrl, duration);
            }

            return (null, null, null);
        }
        catch
        {
            return (null, null, null);
        }
    }

    // Resolves artist+title to a MusicBrainz release-group ID via a two-step
    // lookup: search for the recording, then fetch that recording with its
    // releases/release-groups included (the search endpoint alone doesn't
    // embed release-group data). Free, unauthenticated - but MusicBrainz asks
    // for <=1 request/sec, enforced via _lastMusicBrainzRequestUtc across
    // both requests this makes. Also returns the recording's own "length"
    // (milliseconds) from the search response, alongside the release-group ID -
    // free duration data from a call already being made for the art lookup.
    private async Task<(string? ReleaseGroupId, TimeSpan? Duration)> ResolveMusicBrainzReleaseGroupIdAsync(string artist, string title, CancellationToken ct)
    {
        try
        {
            await ThrottleMusicBrainzAsync(ct);
            string searchQuery = Uri.EscapeDataString($"artist:\"{artist}\" AND recording:\"{title}\"");
            string searchUrl = $"https://musicbrainz.org/ws/2/recording/?query={searchQuery}&fmt=json&limit=1";

            using JsonDocument searchDoc = JsonDocument.Parse(await _http.GetStringAsync(searchUrl, ct));
            if (!searchDoc.RootElement.TryGetProperty("recordings", out JsonElement recordings) ||
                recordings.ValueKind != JsonValueKind.Array || recordings.GetArrayLength() == 0)
            {
                return (null, null);
            }

            JsonElement topRecording = recordings[0];

            string? recordingArtist = topRecording.TryGetProperty("artist-credit", out JsonElement credits) &&
                credits.ValueKind == JsonValueKind.Array && credits.GetArrayLength() > 0 &&
                credits[0].TryGetProperty("name", out JsonElement creditNameEl)
                ? creditNameEl.GetString()
                : null;
            if (!ArtistNamesLooselyMatch(artist, recordingArtist))
                return (null, null); // Free-text search matched an unrelated recording - don't hand back its art.

            TimeSpan? duration = topRecording.TryGetProperty("length", out JsonElement lengthEl) && lengthEl.TryGetInt64(out long lengthMs) && lengthMs > 0
                ? TimeSpan.FromMilliseconds(lengthMs)
                : null;

            string? recordingId = topRecording.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(recordingId))
                return (null, duration);

            await ThrottleMusicBrainzAsync(ct);
            string lookupUrl = $"https://musicbrainz.org/ws/2/recording/{recordingId}?inc=releases+release-groups&fmt=json";
            using JsonDocument lookupDoc = JsonDocument.Parse(await _http.GetStringAsync(lookupUrl, ct));

            if (!lookupDoc.RootElement.TryGetProperty("releases", out JsonElement releases) ||
                releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() == 0)
            {
                return (null, duration);
            }

            foreach (JsonElement release in releases.EnumerateArray())
            {
                if (release.TryGetProperty("release-group", out JsonElement rg) &&
                    rg.TryGetProperty("id", out JsonElement rgIdEl))
                {
                    return (rgIdEl.GetString(), duration);
                }
            }

            return (null, duration);
        }
        catch
        {
            return (null, null);
        }
    }

    private async Task ThrottleMusicBrainzAsync(CancellationToken ct)
    {
        TimeSpan sinceLast = DateTimeOffset.UtcNow - _lastMusicBrainzRequestUtc;
        if (sinceLast < MusicBrainzMinInterval)
            await Task.Delay(MusicBrainzMinInterval - sinceLast, ct);
        _lastMusicBrainzRequestUtc = DateTimeOffset.UtcNow;
    }

    // --- Source 2: Deezer (no auth, free-text search) ---

    private async Task<(Image? Image, string? SourceUrl, TimeSpan? Duration)> TryFetchFromDeezerAsync(string artist, string title, CancellationToken ct)
    {
        try
        {
            string term = Uri.EscapeDataString($"{artist} {title}");
            // A handful of candidates, not just the top one - Deezer's free-text
            // relevance ranking can put an unrelated result first (e.g. a more
            // "popular" completely different artist with a similarly-worded
            // track title), so check a few for one whose artist actually matches
            // before giving up on this source.
            string url = $"https://api.deezer.com/search?q={term}&limit=5";

            using JsonDocument doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
            if (!doc.RootElement.TryGetProperty("data", out JsonElement data) ||
                data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                return (null, null, null);
            }

            foreach (JsonElement candidate in data.EnumerateArray())
            {
                string? candidateArtist = candidate.TryGetProperty("artist", out JsonElement artistEl) &&
                    artistEl.TryGetProperty("name", out JsonElement nameEl)
                    ? nameEl.GetString()
                    : null;
                if (!ArtistNamesLooselyMatch(artist, candidateArtist))
                    continue;

                if (!candidate.TryGetProperty("album", out JsonElement albumEl) ||
                    !albumEl.TryGetProperty("cover_xl", out JsonElement coverEl))
                {
                    continue;
                }

                string? coverUrl = coverEl.GetString();
                if (string.IsNullOrEmpty(coverUrl))
                    continue;

                // "duration" sits right alongside the fields already read
                // above, in seconds - free track-length data from a search
                // we're already making for the cover art.
                TimeSpan? duration = candidate.TryGetProperty("duration", out JsonElement durationEl) &&
                    durationEl.TryGetInt64(out long durationSeconds) && durationSeconds > 0
                    ? TimeSpan.FromSeconds(durationSeconds)
                    : null;

                byte[] bytes = await _http.GetByteArrayAsync(coverUrl, ct);
                return (LoadIndependentImage(bytes), coverUrl, duration);
            }

            return (null, null, null);
        }
        catch
        {
            return (null, null, null);
        }
    }

    // --- Source 3: Bandcamp (undocumented autocomplete endpoint - unverified, fails soft) ---

    // NOTE: this endpoint/response shape could not be confirmed against a live
    // response while writing this (every attempt from the dev sandbox came
    // back 403, which looks like an IP/bot block rather than the endpoint
    // being wrong - Bandcamp is known to be aggressive about that from
    // datacenter IPs). The request shape below matches what Bandcamp's own
    // site search box is widely documented (informally) to call. Needs a
    // real test from a residential/user IP to confirm the JSON field names
    // guessed at below (art_id / cover image URL construction) actually match
    // - until then this should safely no-op (fall through to iTunes) rather
    // than throw if any of those guesses are wrong.
    private async Task<(Image? Image, string? SourceUrl, TimeSpan? Duration)> TryFetchFromBandcampAsync(string artist, string title, CancellationToken ct)
    {
        try
        {
            string searchText = $"{artist} {title}";
            var payload = new
            {
                search_text = searchText,
                search_filter = "",
                full_page = false,
                fan_id = (string?)null,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://bandcamp.com/api/bcsearch_public_api/1/autocomplete_elastic")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            // Bandcamp's undocumented search API is known to reject requests
            // that don't look like they came from a real browser hitting the
            // search page - a plain HttpClient UA/no-referer combo gets
            // blocked outright regardless of query correctness.
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            request.Headers.Referrer = new Uri("https://bandcamp.com/search");

            using HttpResponseMessage response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return (null, null, null); // Includes a bot-block 403 - just fall through to iTunes.

            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("auto", out JsonElement auto) ||
                !auto.TryGetProperty("results", out JsonElement results) ||
                results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            {
                return (null, null, null);
            }

            foreach (JsonElement result in results.EnumerateArray())
            {
                // Only care about track/album results, not artist/label/fan entries.
                string? type = result.TryGetProperty("type", out JsonElement typeEl) ? typeEl.GetString() : null;
                if (type is not ("t" or "a"))
                    continue;

                string? coverUrl = TryBuildBandcampArtUrl(result);
                if (string.IsNullOrEmpty(coverUrl))
                    continue;

                // No duration field in this endpoint's response shape.
                byte[] bytes = await _http.GetByteArrayAsync(coverUrl, ct);
                return (LoadIndependentImage(bytes), coverUrl, null);
            }

            return (null, null, null);
        }
        catch
        {
            return (null, null, null);
        }
    }

    // Bandcamp's search results have historically exposed art either as a
    // ready-made image URL, or as a numeric "art_id" that has to be formatted
    // into their static CDN's f4.bcbits.com/img/a<art_id>_10.jpg convention
    // (the "_10" suffix selects a large-ish square crop) - try both shapes
    // defensively since this is unverified against a live response.
    private static string? TryBuildBandcampArtUrl(JsonElement result)
    {
        foreach (string directField in new[] { "art_url", "cover_url", "image_url", "art" })
        {
            if (result.TryGetProperty(directField, out JsonElement direct) && direct.ValueKind == JsonValueKind.String)
            {
                string? url = direct.GetString();
                if (!string.IsNullOrEmpty(url))
                    return url;
            }
        }

        if (result.TryGetProperty("art_id", out JsonElement artIdEl))
        {
            string? artId = artIdEl.ValueKind switch
            {
                JsonValueKind.Number => artIdEl.GetRawText(),
                JsonValueKind.String => artIdEl.GetString(),
                _ => null,
            };
            if (!string.IsNullOrEmpty(artId))
                return $"https://f4.bcbits.com/img/a{artId}_10.jpg";
        }

        return null;
    }

    // --- Source 4: iTunes (last resort - lower res, weakest underground-metal coverage of the four) ---

    private async Task<(Image? Image, string? SourceUrl, TimeSpan? Duration)> TryFetchFromItunesAsync(string artist, string title, CancellationToken ct)
    {
        try
        {
            string term = Uri.EscapeDataString($"{artist} {title}");
            // A few candidates, not just the top one - same reasoning as Deezer,
            // check each for a genuinely matching artist before giving up.
            string url = $"https://itunes.apple.com/search?term={term}&entity=song&limit=5";

            using JsonDocument doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
            if (!doc.RootElement.TryGetProperty("results", out JsonElement results) ||
                results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            {
                return (null, null, null);
            }

            foreach (JsonElement candidate in results.EnumerateArray())
            {
                string? candidateArtist = candidate.TryGetProperty("artistName", out JsonElement artistNameEl)
                    ? artistNameEl.GetString()
                    : null;
                if (!ArtistNamesLooselyMatch(artist, candidateArtist))
                    continue;

                if (!candidate.TryGetProperty("artworkUrl100", out JsonElement artworkEl))
                    continue;

                string? artworkUrl100 = artworkEl.GetString();
                if (string.IsNullOrEmpty(artworkUrl100))
                    continue;

                // iTunes serves the same artwork at other sizes by swapping the
                // "100x100bb" segment of the URL - 600x600 is the largest size
                // reliably available without hitting their (undocumented, much
                // larger) originals endpoint.
                string hiResUrl = artworkUrl100.Replace("100x100bb", "600x600bb");

                // "trackTimeMillis" sits right alongside the artwork field -
                // free track-length data from a search already being made.
                TimeSpan? duration = candidate.TryGetProperty("trackTimeMillis", out JsonElement timeEl) &&
                    timeEl.TryGetInt64(out long trackTimeMillis) && trackTimeMillis > 0
                    ? TimeSpan.FromMilliseconds(trackTimeMillis)
                    : null;

                byte[] bytes = await _http.GetByteArrayAsync(hiResUrl, ct);
                return (LoadIndependentImage(bytes), hiResUrl, duration);
            }

            return (null, null, null);
        }
        catch
        {
            return (null, null, null);
        }
    }

    // --- Disk cache ---

    // Positive results cache as "<hash>.jpg" (the actual image bytes).
    // Negative results cache as "<hash>.miss" containing just the UTC ticks
    // of when the miss was recorded, so it can expire after NegativeResultTtl
    // instead of caching "no art" forever for a track that just isn't out yet.

    private (Image? Image, string? SourceUrl, TimeSpan? Duration, bool IsNegative, bool IsExpired) TryLoadFromDisk(string key)
    {
        try
        {
            string imagePath = DiskCachePath(key, positive: true);
            var imageInfo = new FileInfo(imagePath);
            if (imageInfo.Exists)
            {
                if (imageInfo.Length == 0)
                {
                    // A leftover 0-byte file from the Image.FromStream/disposed-
                    // stream bug this class used to have (see LoadIndependentImage)
                    // - self-heal by deleting it and falling through to a normal
                    // re-fetch rather than treating it as a valid cache hit.
                    File.Delete(imagePath);
                    return (null, null, null, false, false);
                }

                byte[] bytes = File.ReadAllBytes(imagePath);
                string? sourceUrl = TryReadSourceUrlSidecar(imagePath);
                TimeSpan? duration = TryReadDurationSidecar(imagePath);
                return (LoadIndependentImage(bytes), sourceUrl, duration, false, false);
            }

            string missPath = DiskCachePath(key, positive: false);
            if (File.Exists(missPath))
            {
                string text = File.ReadAllText(missPath);
                if (long.TryParse(text, out long ticks))
                {
                    var cachedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
                    bool expired = DateTimeOffset.UtcNow - cachedAt >= NegativeResultTtl;
                    return (null, null, null, true, expired);
                }
            }

            return (null, null, null, false, false);
        }
        catch
        {
            return (null, null, null, false, false);
        }
    }

    private static string? TryReadSourceUrlSidecar(string imagePath)
    {
        try
        {
            string urlPath = imagePath + ".url";
            return File.Exists(urlPath) ? File.ReadAllText(urlPath).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan? TryReadDurationSidecar(string imagePath)
    {
        try
        {
            string durationPath = imagePath + ".duration";
            if (File.Exists(durationPath) && double.TryParse(File.ReadAllText(durationPath).Trim(), out double seconds))
                return TimeSpan.FromSeconds(seconds);
        }
        catch
        {
            // Fall through to null.
        }
        return null;
    }

    private void TrySaveToDisk(string key, Image image, string? sourceUrl, TimeSpan? duration)
    {
        try
        {
            Directory.CreateDirectory(_diskCacheDir);
            string imagePath = DiskCachePath(key, positive: true);
            image.Save(imagePath, System.Drawing.Imaging.ImageFormat.Jpeg);

            // Sidecar files so the resolved remote URL/duration survive a
            // restart too (the in-memory cache alone wouldn't) - consumers
            // that need a fetchable public URL rather than this local file
            // (Discord Rich Presence) read it back via GetCachedArtSourceUrl,
            // and the real track length (when a source had one) via
            // GetCachedDuration.
            if (!string.IsNullOrEmpty(sourceUrl))
                File.WriteAllText(imagePath + ".url", sourceUrl);
            if (duration is TimeSpan d)
                File.WriteAllText(imagePath + ".duration", d.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // Clear any stale negative marker now that we have a real result.
            string missPath = DiskCachePath(key, positive: false);
            if (File.Exists(missPath))
                File.Delete(missPath);
        }
        catch
        {
            // Best-effort - the in-memory cache still works for this run even if disk caching fails.
        }
    }

    private void TrySaveNegativeMarkerToDisk(string key)
    {
        try
        {
            Directory.CreateDirectory(_diskCacheDir);
            File.WriteAllText(DiskCachePath(key, positive: false), DateTimeOffset.UtcNow.Ticks.ToString());
        }
        catch
        {
            // Best-effort.
        }
    }

    // Free-text search sources (Deezer, iTunes, MusicBrainz) can rank an
    // unrelated result first when there's no exact match in their catalog -
    // e.g. this station's "137 - Lie For A Lie" matching a popular classical
    // piano album because the search terms happened to overlap. Reject a
    // candidate whose own reported artist name doesn't actually resemble the
    // one we searched for, rather than trusting relevance ranking alone.
    // Deliberately loose (case/punctuation-insensitive substring check, not
    // an exact match) since real-world naming varies ("The Beatles" vs
    // "Beatles", "AC/DC" vs "ACDC") - it only needs to catch results that are
    // genuinely a different artist, not penalize minor formatting differences.
    private static bool ArtistNamesLooselyMatch(string searched, string? candidate)
    {
        string a = NormalizeForComparison(searched);
        string b = NormalizeForComparison(candidate);
        if (a.Length == 0 || b.Length == 0)
            return false;

        return b.Contains(a, StringComparison.Ordinal) || a.Contains(b, StringComparison.Ordinal);
    }

    private static string NormalizeForComparison(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return "";

        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    // Image.FromStream(stream) keeps a lazy dependency on the backing stream
    // for pixel decoding - it is NOT safe to dispose that stream once
    // FromStream returns despite how common the "using var ms = ...; return
    // Image.FromStream(ms);" pattern looks. Every call site here used to do
    // exactly that, which silently corrupted the image the moment its source
    // MemoryStream got disposed: drawing it in-app happened to still work
    // (already decoded/cached internally by GDI+ by that point), but calling
    // .Save() on it later (see TrySaveToDisk) failed - caught by that
    // method's own catch-all - leaving a 0-byte .jpg on disk that still
    // passed File.Exists, so SMTC/toast notifications got handed a corrupt,
    // empty "art" file. Wrapping the load in a Bitmap copy here makes the
    // returned Image fully independent of the source stream.
    private static Image LoadIndependentImage(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var loaded = Image.FromStream(ms);
        return new Bitmap(loaded);
    }

    private static string BuildCacheKey(string artist, string title) =>
        $"{artist.Trim().ToLowerInvariant()}|{title.Trim().ToLowerInvariant()}";

    private string DiskCachePath(string key, bool positive)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
            sb.Append(b.ToString("x2"));
        return Path.Combine(_diskCacheDir, sb + (positive ? ".jpg" : ".miss"));
    }
}
