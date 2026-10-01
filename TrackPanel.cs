using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SomaMetalTray;

/// <summary>Small cover thumbnails for the panel: loaded once per URL / file, off the UI thread, then reused.</summary>
internal sealed class ThumbnailCache : IDisposable
{
    private const int Size = 44;
    private const int MaxEntries = 400;

    private readonly Dictionary<string, Image?> _images = new();
    private readonly HashSet<string> _loading = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Raised on the UI thread (the thread that created the cache) when a thumbnail has arrived.</summary>
    public event Action? Loaded;

    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public ThumbnailCache() => _http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.UserAgentProduct}/1.0");

    /// <summary>The thumbnail if it is ready; otherwise null, and a load is started that raises <see cref="Loaded"/> when done.</summary>
    public Image? Get(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        lock (_images)
        {
            if (_images.TryGetValue(key, out Image? image))
                return image;
            if (!_loading.Add(key))
                return null;
        }

        _ = LoadAsync(key);
        return null;
    }

    private async Task LoadAsync(string key)
    {
        Image? thumb = null;
        try
        {
            byte[] bytes = Uri.TryCreate(key, UriKind.Absolute, out Uri? uri) && uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? await _http.GetByteArrayAsync(uri)
                : await File.ReadAllBytesAsync(key);

            using var stream = new MemoryStream(bytes);
            using var source = Image.FromStream(stream);
            var scaled = new Bitmap(Size, Size);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, 0, 0, Size, Size);
            }
            thumb = scaled;
        }
        catch
        {
            // Missing / unreachable art - the row just shows the placeholder.
        }

        lock (_images)
        {
            if (_images.Count >= MaxEntries)
            {
                foreach (Image? old in _images.Values)
                    old?.Dispose();
                _images.Clear();
            }
            _images[key] = thumb;
            _loading.Remove(key);
        }

        if (_ui is not null)
            _ui.Post(_ => Loaded?.Invoke(), null);
        else
            Loaded?.Invoke();
    }

    public void Dispose()
    {
        lock (_images)
        {
            foreach (Image? image in _images.Values)
                image?.Dispose();
            _images.Clear();
        }
        _http.Dispose();
    }
}

internal enum PanelTab
{
    History,
    Upcoming,
}

/// <summary>
/// The list under (or over) the player: a History tab - what you heard, with a heart on each row - and, for stations that publish one,
/// an Upcoming tab. One owner-drawn control: rows, thumbnails, hover, wheel scrolling, a context menu.
/// </summary>
internal sealed class TrackPanel : Control
{
    private const int HeaderHeight = 40;
    private const int RowHeight = 54;

    private sealed record Row(string Title, string Subtitle, string Right, string? ThumbKey, WishlistEntry? Entry);

    private readonly ThumbnailCache _thumbs;
    private readonly Func<WishlistEntry, bool> _isLiked;
    private readonly Font _titleFont = UiFonts.Body(10.5f, FontStyle.Bold);
    private readonly Font _subFont = UiFonts.Body(9f);
    private readonly Font _tabFont = UiFonts.Body(10f, FontStyle.Bold);
    private readonly Font _smallFont = UiFonts.Body(8.5f);
    private readonly Font _heartFont = UiFonts.Icon(13f);

    private List<Row> _historyRows = new();
    private List<Row> _upcomingRows = new();
    private PanelTab _tab = PanelTab.History;
    private bool _upcomingAvailable;
    private string _upcomingStatus = "";
    private int _scroll;
    private int _hotRow = -1;
    private bool _hotHeart;

    /// <summary>The user clicked a row's heart (or chose Like in the menu).</summary>
    public event Action<WishlistEntry>? LikeToggleRequested;

    public event Action? ClearHistoryRequested;

    public event Action<PanelTab>? TabChanged;

    public TrackPanel(Func<WishlistEntry, bool> isLiked)
    {
        _isLiked = isLiked;
        _thumbs = new ThumbnailCache();
        _thumbs.Loaded += Invalidate;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(0x14, 0x0c, 0x0c);
        Cursor = Cursors.Default;
    }

    public PanelTab Tab => _tab;

    /// <summary>Whether the current station has an Upcoming tab at all.</summary>
    public void SetUpcomingAvailable(bool available)
    {
        _upcomingAvailable = available;
        if (!available && _tab == PanelTab.Upcoming)
            SelectTab(PanelTab.History);
        Invalidate();
    }

    public void SelectTab(PanelTab tab)
    {
        if (tab == PanelTab.Upcoming && !_upcomingAvailable)
            tab = PanelTab.History;
        if (_tab == tab)
            return;

        _tab = tab;
        _scroll = 0;
        Invalidate();
        TabChanged?.Invoke(tab);
    }

    public void SetHistory(IReadOnlyList<HistoryItem> items, Func<string, string, string?> localArtPath)
    {
        _historyRows = items.Select(i => new Row(
            i.Title,
            string.IsNullOrEmpty(i.Album) ? i.Artist : $"{i.Artist}  ·  {i.Album}",
            FormatTime(i.At),
            localArtPath(i.Artist, i.Title) ?? i.ArtUrl,
            i.ToWishlistEntry())).ToList();
        ClampScroll();
        Invalidate();
    }

    /// <summary>The upcoming list, or a status line ("Loading...", "Nothing queued") when there are no rows.</summary>
    public void SetUpcoming(IReadOnlyList<UpcomingItem>? items, string status = "")
    {
        _upcomingStatus = status;
        _upcomingRows = (items ?? Array.Empty<UpcomingItem>()).Select(i => new Row(
            i.Title,
            string.IsNullOrEmpty(i.Album) ? i.Artist : $"{i.Artist}  ·  {i.Album}",
            i.Duration,
            i.ThumbUrl,
            null)).ToList();
        ClampScroll();
        Invalidate();
    }

    private static string FormatTime(DateTimeOffset at)
    {
        DateTime local = at.LocalDateTime;
        return local.Date == DateTime.Today ? $"{local:HH:mm}" : $"{local:ddd HH:mm}";
    }

    private List<Row> Rows => _tab == PanelTab.History ? _historyRows : _upcomingRows;

    private Rectangle ListArea => new(0, HeaderHeight, Width, Math.Max(0, Height - HeaderHeight));

    private void ClampScroll()
    {
        int max = Math.Max(0, Rows.Count * RowHeight - ListArea.Height);
        _scroll = Math.Clamp(_scroll, 0, max);
    }

    private Rectangle HistoryTabRect => new(16, 0, 90, HeaderHeight);

    private Rectangle UpcomingTabRect => new(HistoryTabRect.Right + 8, 0, 100, HeaderHeight);

    private Rectangle HeartRect(int rowTop) => new(Width - 56, rowTop + (RowHeight - 34) / 2, 34, 34);

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        DrawHeader(g);

        Region oldClip = g.Clip;
        g.SetClip(ListArea);
        try
        {
            List<Row> rows = Rows;
            if (rows.Count == 0)
            {
                string empty = _tab == PanelTab.History ? "Nothing here yet - play something and the tracks you hear are listed here."
                    : string.IsNullOrEmpty(_upcomingStatus) ? "Nothing is queued." : _upcomingStatus;
                TextRenderer.DrawText(g, empty, _subFont, new Rectangle(20, HeaderHeight + 18, Width - 40, 40), UiColors.TextFaint,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                return;
            }

            int first = Math.Max(0, _scroll / RowHeight);
            int last = Math.Min(rows.Count - 1, (_scroll + ListArea.Height) / RowHeight);
            for (int i = first; i <= last; i++)
                DrawRow(g, rows[i], i, HeaderHeight + i * RowHeight - _scroll);

            DrawScrollBar(g, rows.Count);
        }
        finally
        {
            g.Clip = oldClip;
            oldClip.Dispose();
        }
    }

    private void DrawHeader(Graphics g)
    {
        using (var line = new Pen(UiColors.Border))
            g.DrawLine(line, 0, HeaderHeight - 1, Width, HeaderHeight - 1);

        DrawTab(g, "History", HistoryTabRect, _tab == PanelTab.History);
        if (_upcomingAvailable)
            DrawTab(g, "Upcoming", UpcomingTabRect, _tab == PanelTab.Upcoming);

        string count = _tab == PanelTab.History ? $"{_historyRows.Count} heard" : _upcomingRows.Count > 0 ? $"{_upcomingRows.Count} queued" : "";
        TextRenderer.DrawText(g, count, _smallFont, new Rectangle(Width - 150, 0, 134, HeaderHeight), UiColors.TextFaint,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void DrawTab(Graphics g, string text, Rectangle rect, bool active)
    {
        TextRenderer.DrawText(g, text, _tabFont, rect, active ? UiColors.TextBright : UiColors.TextDim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (active)
        {
            using var accent = new SolidBrush(UiColors.Accent);
            g.FillRectangle(accent, rect.X + 14, HeaderHeight - 3, rect.Width - 28, 3);
        }
    }

    private void DrawRow(Graphics g, Row row, int index, int top)
    {
        bool hot = index == _hotRow;
        if (hot)
        {
            using GraphicsPath path = Draw.Rounded(new RectangleF(6, top + 2, Width - 12, RowHeight - 4), 8);
            using var fill = new SolidBrush(Color.FromArgb(120, UiColors.SurfaceHover));
            g.FillPath(fill, path);
        }

        // Thumbnail (or a placeholder square until it loads / when there is none).
        var thumbRect = new Rectangle(18, top + (RowHeight - 40) / 2, 40, 40);
        using (GraphicsPath thumbPath = Draw.Rounded(thumbRect, 6))
        {
            Image? thumb = _thumbs.Get(row.ThumbKey);
            if (thumb is not null)
            {
                Region clip = g.Clip;
                g.SetClip(thumbPath, CombineMode.Intersect);
                g.DrawImage(thumb, thumbRect);
                g.Clip = clip;
                clip.Dispose();
            }
            else
            {
                using var placeholder = new SolidBrush(UiColors.Surface);
                g.FillPath(placeholder, thumbPath);
                Draw.Glyph(g, Glyphs.Music, _heartFont, UiColors.TextFaint, thumbRect);
            }
        }

        int textLeft = thumbRect.Right + 14;
        int textRight = Width - (row.Entry is not null ? 150 : 100);
        TextRenderer.DrawText(g, row.Title, _titleFont, new Rectangle(textLeft, top + 8, Math.Max(10, textRight - textLeft), 20), UiColors.TextBright,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, row.Subtitle, _subFont, new Rectangle(textLeft, top + 28, Math.Max(10, textRight - textLeft), 18), UiColors.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        int rightEdge = Width - 20;
        if (row.Entry is not null)
        {
            bool liked = _isLiked(row.Entry);
            Rectangle heart = HeartRect(top);
            Color heartColor = liked ? UiColors.Accent : (hot && _hotHeart) ? UiColors.Text : UiColors.TextFaint;
            Draw.Glyph(g, liked ? Glyphs.HeartFilled : Glyphs.Heart, _heartFont, heartColor, heart);
            rightEdge = heart.Left - 6;
        }
        TextRenderer.DrawText(g, row.Right, _smallFont, new Rectangle(rightEdge - 90, top, 90, RowHeight), UiColors.TextFaint,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void DrawScrollBar(Graphics g, int rowCount)
    {
        int content = rowCount * RowHeight;
        if (content <= ListArea.Height)
            return;

        float ratio = (float)ListArea.Height / content;
        int thumbHeight = Math.Max(24, (int)(ListArea.Height * ratio));
        int travel = ListArea.Height - thumbHeight;
        int thumbTop = HeaderHeight + (int)(travel * ((float)_scroll / (content - ListArea.Height)));
        using var brush = new SolidBrush(Color.FromArgb(110, UiColors.TextDim));
        using GraphicsPath path = Draw.Rounded(new RectangleF(Width - 7, thumbTop, 4, thumbHeight), 2);
        g.FillPath(brush, path);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scroll -= Math.Sign(e.Delta) * RowHeight;
        ClampScroll();
        UpdateHot(PointToClient(Cursor.Position));
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateHot(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hotRow = -1;
        _hotHeart = false;
        Cursor = Cursors.Default;
        Invalidate();
    }

    private int RowAt(Point p)
    {
        if (!ListArea.Contains(p))
            return -1;
        int index = (p.Y - HeaderHeight + _scroll) / RowHeight;
        return index >= 0 && index < Rows.Count ? index : -1;
    }

    private void UpdateHot(Point p)
    {
        int row = RowAt(p);
        bool heart = row >= 0 && Rows[row].Entry is not null && HeartRect(HeaderHeight + row * RowHeight - _scroll).Contains(p);
        bool overTab = p.Y < HeaderHeight && (HistoryTabRect.Contains(p) || (_upcomingAvailable && UpcomingTabRect.Contains(p)));
        Cursor = heart || overTab ? Cursors.Hand : Cursors.Default;

        if (row != _hotRow || heart != _hotHeart)
        {
            _hotRow = row;
            _hotHeart = heart;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button == MouseButtons.Left)
        {
            if (e.Y < HeaderHeight)
            {
                if (HistoryTabRect.Contains(e.Location)) SelectTab(PanelTab.History);
                else if (_upcomingAvailable && UpcomingTabRect.Contains(e.Location)) SelectTab(PanelTab.Upcoming);
                return;
            }

            int row = RowAt(e.Location);
            if (row >= 0 && Rows[row].Entry is { } entry && HeartRect(HeaderHeight + row * RowHeight - _scroll).Contains(e.Location))
                LikeToggleRequested?.Invoke(entry);
        }
        else if (e.Button == MouseButtons.Right)
        {
            int row = RowAt(e.Location);
            if (row >= 0)
                ShowRowMenu(Rows[row], PointToScreen(e.Location));
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int row = RowAt(e.Location);
        if (e.Button == MouseButtons.Left && row >= 0 && Rows[row].Entry is { } entry)
            LikeToggleRequested?.Invoke(entry);
    }

    private void ShowRowMenu(Row row, Point screen)
    {
        ContextMenuStrip menu = ThemedMenu.Create();
        if (row.Entry is { } entry)
            ThemedMenu.Add(menu, _isLiked(entry) ? "Unlike" : "Like", (_, _) => LikeToggleRequested?.Invoke(entry), _isLiked(entry) ? Glyphs.HeartFilled : Glyphs.Heart);

        ThemedMenu.Add(menu, "Copy track info", (_, _) => CopyToClipboard($"{row.Title} — {row.Subtitle.Replace("  ·  ", " — ")}"), Glyphs.Copy);

        if (_tab == PanelTab.History)
        {
            menu.Items.Add(new ToolStripSeparator());
            ThemedMenu.Add(menu, "Clear history", (_, _) => ClearHistoryRequested?.Invoke(), Glyphs.Clear);
        }

        ThemedMenu.ShowAndDispose(this, menu, screen);
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // The clipboard can be held by another app for a moment - not worth an error dialog.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbs.Loaded -= Invalidate;
            _thumbs.Dispose();
            _titleFont.Dispose();
            _subFont.Dispose();
            _tabFont.Dispose();
            _smallFont.Dispose();
            _heartFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
