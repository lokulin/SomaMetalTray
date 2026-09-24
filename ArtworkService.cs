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
///      Entirely optional: skipped outright if no API key is configured
///      (AppSettings.FanArtTvApiKey, set via Settings - see SettingsForm),
///      and any failure/403 just falls through to the next source.
///   2. Deezer - no auth needed, free-text search, this is the workhorse in
///      practice (broad catalog, no key to configure, no MusicBrainz
///      round-trip first).
///   3. iTunes - the original plan, kept as a last-resort fallback since it
///      tops out at a lower resolution than the other two and has the
///      weakest coverage of underground metal of the three.
///   4. The station's own logo (metal512.png), if all three come back empty.
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

    private readonly record struct CacheEntry(Image? Image, DateTimeOffset CachedAt)
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
    /// the fanart.tv -> Deezer -> iTunes source chain. Returns null (letting
    /// the caller fall back to the station logo via
    /// <see cref="GetFallbackLogoAsync"/>) on a zero-result search anywhere
    /// or any failure. Never throws - a bad lookup should never take down
    /// the UI it's feeding.
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

            (Image? diskImage, bool isNegative, bool isExpired) = TryLoadFromDisk(key);
            if (diskImage is not null)
            {
                _memoryCache[key] = new CacheEntry(diskImage, DateTimeOffset.UtcNow);
                return diskImage;
            }
            if (isNegative && !isExpired)
            {
                _memoryCache[key] = new CacheEntry(null, DateTimeOffset.UtcNow);
                return null;
            }

            Image? fetched = await TryFetchFromFanArtTvAsync(artist, title, album, ct)
                ?? await TryFetchFromDeezerAsync(artist, title, ct)
                ?? await TryFetchFromItunesAsync(artist, title, ct);

            var entry = new CacheEntry(fetched, DateTimeOffset.UtcNow);
            _memoryCache[key] = entry;

            if (fetched is not null)
                TrySaveToDisk(key, fetched);
            else
                TrySaveNegativeMarkerToDisk(key);

            return fetched;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Fetches (once) and caches the station's own logo, for use when a track has no art anywhere.</summary>
    public async Task<Image?> GetFallbackLogoAsync(string logoUrl, CancellationToken ct)
    {
        if (_fallbackLogo is not null && string.Equals(_fallbackLogoUrl, logoUrl, StringComparison.Ordinal))
            return _fallbackLogo;

        try
        {
            byte[] bytes = await _http.GetByteArrayAsync(logoUrl, ct);
            using var ms = new MemoryStream(bytes);
            var image = Image.FromStream(ms);

            _fallbackLogo?.Dispose();
            _fallbackLogo = image;
            _fallbackLogoUrl = logoUrl;
            return _fallbackLogo;
        }
        catch
        {
            return null;
        }
    }

    // --- Source 1: fanart.tv (via a MusicBrainz recording -> release-group resolution) ---

    private async Task<Image?> TryFetchFromFanArtTvAsync(string artist, string title, string album, CancellationToken ct)
    {
        string? apiKey = _settings.FanArtTvApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return null; // Not configured - skip straight to Deezer, no error.

        try
        {
            string? releaseGroupId = await ResolveMusicBrainzReleaseGroupIdAsync(artist, title, ct);
            if (string.IsNullOrEmpty(releaseGroupId))
                return null;

            string url = $"https://webservice.fanart.tv/v3/music/albums/{releaseGroupId}?api_key={Uri.EscapeDataString(apiKey)}";
            using HttpResponseMessage response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return null; // Includes 403 (bad/unauthorized key) - just fall through.

            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("albums", out JsonElement albums) || albums.ValueKind != JsonValueKind.Object)
                return null;

            foreach (JsonProperty albumEntry in albums.EnumerateObject())
            {
                if (!albumEntry.Value.TryGetProperty("albumcover", out JsonElement covers) || covers.ValueKind != JsonValueKind.Array || covers.GetArrayLength() == 0)
                    continue;

                string? coverUrl = covers[0].TryGetProperty("url", out JsonElement urlEl) ? urlEl.GetString() : null;
                if (string.IsNullOrEmpty(coverUrl))
                    continue;

                byte[] bytes = await _http.GetByteArrayAsync(coverUrl, ct);
                using var ms = new MemoryStream(bytes);
                return Image.FromStream(ms);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    // Resolves artist+title to a MusicBrainz release-group ID via a two-step
    // lookup: search for the recording, then fetch that recording with its
    // releases/release-groups included (the search endpoint alone doesn't
    // embed release-group data). Free, unauthenticated - but MusicBrainz asks
    // for <=1 request/sec, enforced via _lastMusicBrainzRequestUtc across
    // both requests this makes.
    private async Task<string?> ResolveMusicBrainzReleaseGroupIdAsync(string artist, string title, CancellationToken ct)
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
                return null;
            }

            string? recordingId = recordings[0].TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(recordingId))
                return null;

            await ThrottleMusicBrainzAsync(ct);
            string lookupUrl = $"https://musicbrainz.org/ws/2/recording/{recordingId}?inc=releases+release-groups&fmt=json";
            using JsonDocument lookupDoc = JsonDocument.Parse(await _http.GetStringAsync(lookupUrl, ct));

            if (!lookupDoc.RootElement.TryGetProperty("releases", out JsonElement releases) ||
                releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() == 0)
            {
                return null;
            }

            foreach (JsonElement release in releases.EnumerateArray())
            {
                if (release.TryGetProperty("release-group", out JsonElement rg) &&
                    rg.TryGetProperty("id", out JsonElement rgIdEl))
                {
                    return rgIdEl.GetString();
                }
            }

            return null;
        }
        catch
        {
            return null;
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

    private async Task<Image?> TryFetchFromDeezerAsync(string artist, string title, CancellationToken ct)
    {
        try
        {
            string term = Uri.EscapeDataString($"{artist} {title}");
            string url = $"https://api.deezer.com/search?q={term}&limit=1";

            using JsonDocument doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
            if (!doc.RootElement.TryGetProperty("data", out JsonElement data) ||
                data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement first = data[0];
            if (!first.TryGetProperty("album", out JsonElement albumEl) ||
                !albumEl.TryGetProperty("cover_xl", out JsonElement coverEl))
            {
                return null;
            }

            string? coverUrl = coverEl.GetString();
            if (string.IsNullOrEmpty(coverUrl))
                return null;

            byte[] bytes = await _http.GetByteArrayAsync(coverUrl, ct);
            using var ms = new MemoryStream(bytes);
            return Image.FromStream(ms);
        }
        catch
        {
            return null;
        }
    }

    // --- Source 3: iTunes (last resort - lower res, weakest underground-metal coverage of the three) ---

    private async Task<Image?> TryFetchFromItunesAsync(string artist, string title, CancellationToken ct)
    {
        try
        {
            string term = Uri.EscapeDataString($"{artist} {title}");
            string url = $"https://itunes.apple.com/search?term={term}&entity=song&limit=1";

            using JsonDocument doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
            if (!doc.RootElement.TryGetProperty("results", out JsonElement results) ||
                results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement first = results[0];
            if (!first.TryGetProperty("artworkUrl100", out JsonElement artworkEl))
                return null;

            string? artworkUrl100 = artworkEl.GetString();
            if (string.IsNullOrEmpty(artworkUrl100))
                return null;

            // iTunes serves the same artwork at other sizes by swapping the
            // "100x100bb" segment of the URL - 600x600 is the largest size
            // reliably available without hitting their (undocumented, much
            // larger) originals endpoint.
            string hiResUrl = artworkUrl100.Replace("100x100bb", "600x600bb");

            byte[] bytes = await _http.GetByteArrayAsync(hiResUrl, ct);
            using var ms = new MemoryStream(bytes);
            return Image.FromStream(ms);
        }
        catch
        {
            return null;
        }
    }

    // --- Disk cache ---

    // Positive results cache as "<hash>.jpg" (the actual image bytes).
    // Negative results cache as "<hash>.miss" containing just the UTC ticks
    // of when the miss was recorded, so it can expire after NegativeResultTtl
    // instead of caching "no art" forever for a track that just isn't out yet.

    private (Image? Image, bool IsNegative, bool IsExpired) TryLoadFromDisk(string key)
    {
        try
        {
            string imagePath = DiskCachePath(key, positive: true);
            if (File.Exists(imagePath))
            {
                byte[] bytes = File.ReadAllBytes(imagePath);
                using var ms = new MemoryStream(bytes);
                return (Image.FromStream(ms), false, false);
            }

            string missPath = DiskCachePath(key, positive: false);
            if (File.Exists(missPath))
            {
                string text = File.ReadAllText(missPath);
                if (long.TryParse(text, out long ticks))
                {
                    var cachedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
                    bool expired = DateTimeOffset.UtcNow - cachedAt >= NegativeResultTtl;
                    return (null, true, expired);
                }
            }

            return (null, false, false);
        }
        catch
        {
            return (null, false, false);
        }
    }

    private void TrySaveToDisk(string key, Image image)
    {
        try
        {
            Directory.CreateDirectory(_diskCacheDir);
            image.Save(DiskCachePath(key, positive: true), System.Drawing.Imaging.ImageFormat.Jpeg);

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
