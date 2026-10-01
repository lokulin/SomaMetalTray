using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SomaMetalTray;

/// <summary>One track that went by while the app was playing (locally or on a Chromecast).</summary>
public sealed record HistoryItem(DateTimeOffset At, string StationId, string Artist, string Title, string Album = "", string? ArtUrl = null)
{
    /// <summary>The like-able form of this item (source id matches what the Cast receiver / SpaceStation call the station).</summary>
    public WishlistEntry ToWishlistEntry()
    {
        IStation station = Stations.ById(StationId);
        return new WishlistEntry(Artist, Title, Album, ArtUrl, station.CastStationId);
    }
}

/// <summary>
/// The last <see cref="MaxItems"/> tracks heard, newest first, persisted so it survives restarts.
/// Neither station offers a history of what <em>you</em> heard, and the tray app is often running in
/// the background - this is the "what was that song?" list.
/// </summary>
public sealed class PlayHistory
{
    public const int MaxItems = 500;

    private readonly ITextStorage _storage;
    private readonly List<HistoryItem> _items = new(); // newest first
    private readonly object _gate = new();

    public PlayHistory(ITextStorage storage)
    {
        _storage = storage;
        Load();
    }

    /// <summary>Raised after an item is added or the history is cleared.</summary>
    public event Action? Changed;

    public IReadOnlyList<HistoryItem> Items
    {
        get
        {
            lock (_gate) return _items.ToList();
        }
    }

    /// <summary>
    /// Records a track. Returns false (and records nothing) for blanks and for a repeat of the newest entry -
    /// stopping and restarting on the same song shouldn't list it twice.
    /// </summary>
    public bool Add(HistoryItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Artist) || string.IsNullOrWhiteSpace(item.Title))
            return false;

        lock (_gate)
        {
            if (_items.Count > 0 && SameTrack(_items[0], item))
                return false;

            _items.Insert(0, item);
            if (_items.Count > MaxItems)
                _items.RemoveRange(MaxItems, _items.Count - MaxItems);

            Save();
        }

        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            Save();
        }

        Changed?.Invoke();
    }

    private static bool SameTrack(HistoryItem a, HistoryItem b) =>
        a.StationId == b.StationId
        && WishlistEntry.Fold(a.Artist) == WishlistEntry.Fold(b.Artist)
        && WishlistEntry.Fold(a.Title) == WishlistEntry.Fold(b.Title);

    private void Save()
    {
        try
        {
            _storage.Write(JsonSerializer.Serialize(_items));
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

            List<HistoryItem>? loaded = JsonSerializer.Deserialize<List<HistoryItem>>(text);
            if (loaded is not null)
                _items.AddRange(loaded.Take(MaxItems));
        }
        catch
        {
            // A corrupt file shouldn't take the player down - start empty.
            _items.Clear();
        }
    }
}
