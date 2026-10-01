using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SomaMetalTray;

/// <summary>A radio track the user liked - identified by text, since a live stream has no catalog ids.</summary>
public sealed record WishlistEntry(string Artist, string Title, string Album = "", string? CoverUrl = null, string Source = "deathfm")
{
    private const string PlaceholderKey = "death fm|death fm";
    private static readonly Regex NonAlphanumeric = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>
    /// Local identity for "is this already liked": the same folding the server's
    /// normalizeWishlistKey does (case, accents, punctuation, "&amp;" = "and"), so a
    /// station that spells a title two ways doesn't get two hearts.
    /// </summary>
    [JsonIgnore]
    public string Key => $"{Fold(Artist)}|{Fold(Title)}";

    /// <summary>False for the "Death.FM / Death.FM" placeholder shown before the first real track, and for blanks.</summary>
    [JsonIgnore]
    public bool IsRealTrack => !string.IsNullOrWhiteSpace(Artist) && !string.IsNullOrWhiteSpace(Title) && Key != PlaceholderKey;

    public static WishlistEntry From(TrackMetadata track, IStation station) =>
        new(track.Artist, track.Title, track.Album, track.ArtUrl, station.CastStationId);

    // string.Normalize() silently does nothing for non-ASCII text under InvariantGlobalization (which this
    // app ships with), so accents would never fold. The Win32 API works regardless of the globalization mode.
    [System.Runtime.InteropServices.DllImport("normaliz.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "NormalizeString")]
    private static extern int NormalizeString(int normForm, string source, int sourceLength, StringBuilder? destination, int destinationLength);

    private const int NormalizationFormD = 2;

    private static string Decompose(string text)
    {
        try
        {
            int needed = NormalizeString(NormalizationFormD, text, -1, null, 0);
            if (needed <= 0)
                return text.Normalize(NormalizationForm.FormD);

            var buffer = new StringBuilder(needed);
            int written = NormalizeString(NormalizationFormD, text, -1, buffer, needed);
            return written > 0 ? buffer.ToString(0, written - 1) : text.Normalize(NormalizationForm.FormD);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return text.Normalize(NormalizationForm.FormD);
        }
    }

    internal static string Fold(string text)
    {
        var stripped = new StringBuilder();
        foreach (char c in Decompose(text))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                stripped.Append(c);
        }

        string folded = stripped.ToString().ToLowerInvariant().Replace("&", " and ");
        return NonAlphanumeric.Replace(folded, " ").Trim();
    }
}

/// <summary>Something still to tell the server: add (<see cref="Liked"/> = true) or remove the entry.</summary>
public sealed record PendingOp(WishlistEntry Entry, bool Liked, long At);

/// <summary>Where a small JSON state blob (liked set + pending ops, play history) survives restarts.</summary>
public interface ITextStorage
{
    string? Read();
    void Write(string text);
}

/// <summary>Delivers one op. Returns true if it's done with (delivered, or permanently rejected); false to keep it and retry later.</summary>
public interface IWishlistSender
{
    Task<bool> SendAsync(PendingOp op);
}

/// <summary>
/// The liked radio tracks on this PC plus the queue of changes still to send to
/// wishlist server. A like takes effect immediately (the heart fills) and is delivered
/// whenever the network allows: a failed send leaves it queued for the next toggle
/// or <see cref="FlushAsync"/> - nothing is lost while offline. Only the latest
/// intent per track is kept, so like-then-unlike offline sends a single "remove".
/// Port of DeathFmAndroid's WishlistRepository. A null sender (public builds) keeps
/// likes purely local - nothing is queued.
/// </summary>
public sealed class WishlistRepository
{
    private readonly ITextStorage _storage;
    private readonly IWishlistSender? _sender;
    private readonly Func<long> _clock;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private readonly Dictionary<string, WishlistEntry> _liked = new(); // by key
    private readonly List<string> _likedOrder = new();                 // oldest first
    private readonly List<PendingOp> _pending = new();                 // oldest first, one per key

    public WishlistRepository(ITextStorage storage, IWishlistSender? sender, Func<long>? clock = null)
    {
        _storage = storage;
        _sender = sender;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Load();
    }

    /// <summary>Raised after the liked set changes.</summary>
    public event Action? Changed;

    public int PendingCount
    {
        get
        {
            lock (_pending) return _pending.Count;
        }
    }

    public bool IsLiked(WishlistEntry entry)
    {
        lock (_liked) return _liked.ContainsKey(entry.Key);
    }

    /// <summary>The liked tracks, most recently liked first.</summary>
    public IReadOnlyList<WishlistEntry> LikedEntries()
    {
        lock (_liked) return _likedOrder.Select(k => _liked[k]).Reverse().ToList();
    }

    /// <summary>Flips the entry's liked state, persists it, tries to deliver, and returns the new state.</summary>
    public async Task<bool> ToggleAsync(WishlistEntry entry)
    {
        await _lock.WaitAsync();
        bool nowLiked;
        try
        {
            lock (_liked)
            {
                nowLiked = !_liked.ContainsKey(entry.Key);
                _likedOrder.Remove(entry.Key);
                if (nowLiked)
                {
                    _liked[entry.Key] = entry;
                    _likedOrder.Add(entry.Key);
                }
                else
                {
                    _liked.Remove(entry.Key);
                }
            }

            if (_sender is not null)
            {
                lock (_pending)
                {
                    // The latest intent wins, and goes to the back of the queue.
                    _pending.RemoveAll(p => p.Entry.Key == entry.Key);
                    _pending.Add(new PendingOp(entry, nowLiked, _clock()));
                }
            }

            Save();
            await FlushLockedAsync();
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
        return nowLiked;
    }

    /// <summary>Retries everything still queued (app start, connectivity back).</summary>
    public async Task FlushAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await FlushLockedAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    // Oldest first, stopping at the first failure: if the network is down there is no point trying the rest.
    private async Task FlushLockedAsync()
    {
        if (_sender is null)
            return;

        while (true)
        {
            PendingOp? next;
            lock (_pending) next = _pending.FirstOrDefault();
            if (next is null)
                return;

            if (!await _sender.SendAsync(next))
                return;

            lock (_pending) _pending.Remove(next);
            Save();
        }
    }

    private sealed class State
    {
        public List<WishlistEntry> Liked { get; set; } = new();
        public List<PendingOp> Pending { get; set; } = new();
    }

    private void Save()
    {
        State state;
        lock (_liked)
        lock (_pending)
        {
            state = new State
            {
                Liked = _likedOrder.Select(k => _liked[k]).ToList(),
                Pending = _pending.ToList(),
            };
        }

        try
        {
            _storage.Write(JsonSerializer.Serialize(state));
        }
        catch
        {
            // Best-effort - a failed save shouldn't take the player down.
        }
    }

    private void Load()
    {
        try
        {
            string? text = _storage.Read();
            if (string.IsNullOrWhiteSpace(text))
                return;

            State? state = JsonSerializer.Deserialize<State>(text);
            if (state is null)
                return;

            foreach (WishlistEntry entry in state.Liked)
            {
                if (_liked.TryAdd(entry.Key, entry))
                    _likedOrder.Add(entry.Key);
            }

            if (_sender is not null)
            {
                foreach (PendingOp op in state.Pending)
                {
                    _pending.RemoveAll(p => p.Entry.Key == op.Entry.Key);
                    _pending.Add(op);
                }
            }
        }
        catch
        {
            // A corrupt file shouldn't take the player down - start empty.
            _liked.Clear();
            _likedOrder.Clear();
            _pending.Clear();
        }
    }
}

/// <summary>A text blob in a file under %AppData% (creates the folder on first write).</summary>
public sealed class FileTextStorage(string path) : ITextStorage
{
    public string? Read() => File.Exists(path) ? File.ReadAllText(path) : null;

    public void Write(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}

/// <summary>
/// Sends wishlist changes to a private wishlist server (POST/DELETE /wishlist,
/// authenticated with its Cloudflare Access service token). Only constructed in
/// builds whose local.properties supplied those credentials (see PrivateConfig) -
/// public release builds have none, so the feature simply isn't there.
/// </summary>
public sealed class RemoteWishlistApi : IWishlistSender, IDisposable
{
    // Auth problems and throttling aren't the entry's fault, so those stay queued for a later retry;
    // any other 4xx means the server looked at it and said no - retrying forever would wedge the queue.
    private static readonly HashSet<int> RetryLater = new() { 401, 403, 408, 429 };

    private readonly string _baseUrl;
    private readonly HttpClient _http;

    public RemoteWishlistApi(string baseUrl, string clientId, string clientSecret, HttpClient? http = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Add("CF-Access-Client-Id", clientId);
        _http.DefaultRequestHeaders.Add("CF-Access-Client-Secret", clientSecret);
    }

    public async Task<bool> SendAsync(PendingOp op)
    {
        try
        {
            var body = new Dictionary<string, object?>
            {
                ["artist"] = op.Entry.Artist,
                ["title"] = op.Entry.Title,
                ["album"] = op.Entry.Album,
                ["source"] = op.Entry.Source,
                ["device"] = Environment.MachineName,
                ["likedAt"] = op.At,
            };
            if (op.Entry.CoverUrl is string cover)
                body["coverUrl"] = cover;

            using var request = new HttpRequestMessage(op.Liked ? HttpMethod.Post : HttpMethod.Delete, $"{_baseUrl}/wishlist")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };

            using HttpResponseMessage response = await _http.SendAsync(request);
            int code = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                return true;

            if (code is >= 400 and <= 499 && !RetryLater.Contains(code))
            {
                Logger.Log($"Wishlist request rejected ({code}) for {op.Entry.Key}, dropping it");
                return true;
            }

            Logger.Log($"Wishlist request failed ({code}), will retry");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Logger.Log($"Wishlist request failed (offline?), will retry: {ex.Message}");
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
