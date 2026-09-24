using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Media;

namespace SomaMetalTray;

/// <summary>
/// Main window: a custom-painted WinForms shell (no WebView2/browser involved -
/// see AudioPlayerService for direct stream playback) styled after the
/// Death.FM web player / DeathFmAndroid's portrait layout / DeathFmCastReceiver:
/// album art with a reflection on the left, over a dark-to-red gradient
/// background, track info on the right, custom Play/Stop + volume controls
/// underneath, and a progress bar - real (from ArtworkService.GetCachedDuration,
/// picked up from Deezer/iTunes/MusicBrainz alongside the art lookup) when a
/// source had one, otherwise falling back to a cosmetic ever-creeping curve
/// (see ComputeFakeProgress) since SomaFM itself never provides a duration.
/// Closing the window hides it to the tray instead of exiting (configurable);
/// the tray icon is what actually owns app lifetime.
/// </summary>
public sealed class PlayerForm : Form
{
    // Matches the death.fm/DeathFmAndroid palette family - near-black fading
    // to a dark red glow concentrated toward the bottom edge.
    private static readonly Color GradientTop = Color.FromArgb(0x12, 0x10, 0x10);
    private static readonly Color GradientBottom = Color.FromArgb(0x3a, 0x0c, 0x0c);
    private static readonly Color GlowCore = Color.FromArgb(0xb5, 0x1f, 0x1f);
    private static readonly Color AccentColor = Color.FromArgb(0xc0, 0x30, 0x30);

    // Fully custom title bar (see DrawTitleBar/OnMouseDown) - FormBorderStyle
    // is None, so unlike a native caption this height has to be reserved out
    // of our own ClientSize/content layout ourselves.
    private const int TitleBarHeight = 32;

    private const int WindowHeight = 380 + TitleBarHeight;

    private const int ArtSize = 300;
    private const int ArtCornerRadius = 5;
    private const int ArtMarginLeft = 30;
    private const int ArtMarginTop = TitleBarHeight + 26;
    private const int ReflectionTop = ArtMarginTop + ArtSize + 2;
    private const int ReflectionHeight = WindowHeight - ReflectionTop; // runs flush to the window's bottom edge

    private const int InfoLeft = ArtMarginLeft + ArtSize + 30; // 360
    private const int TitleTop = ArtMarginTop + ArtSize / 10;    // ~10% down the album art, 56
    private const int ArtistTop = TitleTop + 60;                 // 116
    private const int AlbumTop = ArtistTop + 28;                 // 144
    private const int ProgressTop = AlbumTop + 30;               // 174
    private const int ProgressHeight = 10;
    private const int TimeLabelsTop = ProgressTop + ProgressHeight + 4; // 188
    private const int StatusTop = TimeLabelsTop + 18;             // 206
    private const int ControlsTop = StatusTop + 22;               // 228 - snug under the progress bar/status line

    private const int LivePillWidth = 100;
    private const int LivePillHeight = 34;

    // Fake progress model (see ComputeFakeProgress): a 5-minute baseline, then
    // a halve-the-remaining-distance/double-the-segment-duration curve that
    // approaches but never reaches 100% - purely cosmetic, there's no real
    // per-track duration API for this station.
    private const double BaselineTotalSeconds = 300.0;
    private const double BaselineRampSeconds = 240.0;
    private const double BaselineRampFraction = 0.8;

    private readonly AppSettings _settings;
    private readonly SomaFmService _somaFm;
    private readonly ArtworkService _artwork;
    private readonly AudioPlayerService _audio;
    private readonly TrackChangeNotifier _trackChangeNotifier;
    private readonly LastFmScrobbler _lastFm;
    private readonly DiscordPresenceService _discord;

    private readonly Label _titleLabel = new();
    private readonly Label _artistLabel = new();
    private readonly Label _albumLabel = new();
    private readonly Label _elapsedLabel = new();
    private readonly Label _statusLabel = new();
    private readonly CirclePlayButton _playButton = new();
    private readonly VolumeSliderControl _volumeSlider = new();
    private readonly TitleBarButton _minimizeButton = new(TitleBarGlyph.Minimize);
    private readonly TitleBarButton _closeButton = new(TitleBarGlyph.Close);
    private readonly ToolTip _copyToolTip = new();

    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 100 };
    private int _uiTimerTicks;

    private SmtcService? _smtc;
    private Icon? _formIcon;
    private bool _allowClose;
    private bool _isPlaying;   // true only while real audio is flowing (PlaybackState.Playing)
    private bool _isActive;    // true whenever the user hasn't stopped playback (Playing or Buffering) - drives the toggle button/progress bar
    private TrackMetadata? _lastMetadata;

    private DateTimeOffset? _trackStartTime;
    private double _progressFraction;
    private double _progressEffectiveTotalSeconds;
    // Set once ArtworkService's lookup resolves for the current track, if any
    // source had one (Deezer/iTunes/MusicBrainz all report it) - see
    // UpdateProgress, which prefers this real value over ComputeFakeProgress
    // whenever it's known. Null until then/if nothing has it.
    private TimeSpan? _realDuration;
    private double _pulseAlpha = 255;

    private Image? _currentArt;       // owned by ArtworkService - never dispose this one
    private Bitmap? _currentReflection; // owned by us - dispose on replace
    private CancellationTokenSource? _artworkCts;

    /// <summary>Raised when Exit is chosen from the titlebar's system menu - TrayAppContext owns actually quitting.</summary>
    public event Action? ExitRequested;

    /// <summary>Raised whenever playback starts/stops/pauses - used by TrayAppContext to reflect state in the tray icon.</summary>
    public event Action<PlaybackState>? PlaybackStateChanged;

    // Custom system menu command IDs - see DeathFmTray.PlayerForm for the WM_SYSCOMMAND conventions this follows.
    private const int CmdSettings = 0x1000;
    private const int CmdStartWithWindows = 0x1010;
    private const int CmdStartMinimized = 0x1020;
    private const int CmdMinimizeToTrayOnClose = 0x1030;
    private const int CmdShowTrackChangeNotifications = 0x1040;
    private const int CmdExit = 0x1050;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int WM_INITMENU = 0x0116;

    public PlayerForm(AppSettings settings)
    {
        _settings = settings;
        _somaFm = new SomaFmService();
        _artwork = new ArtworkService(settings);
        _audio = new AudioPlayerService(settings);
        _trackChangeNotifier = new TrackChangeNotifier(settings);
        _lastFm = new LastFmScrobbler(settings);
        _discord = new DiscordPresenceService(settings);
        _discord.Start();

        Text = "Metal Detector";

        var size = new Size(900, WindowHeight);
        MinimumSize = size;
        MaximumSize = size;
        ClientSize = size;
        // No native title bar at all - a standard FixedSingle caption always
        // left a faint 1px seam where it met our own gradient (see
        // WindowChromeHelper for the investigation); DrawTitleBar/OnMouseDown
        // below hand-draw the title row and handle dragging/the system menu
        // ourselves instead. Fixed-size (Minimum==Maximum), so no resize
        // border/hit-testing is needed either.
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        if (settings.WindowX is int x && settings.WindowY is int y && IsOnScreen(x, y))
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(x, y);
        }

        TrySetIcon();

        HandleCreated += (_, _) => WindowChromeHelper.ApplyDarkChrome(this);
        HandleCreated += (_, _) => BuildSystemMenu();

        BuildControls();

        Load += PlayerForm_Load;
        Resize += PlayerForm_Resize;
        FormClosing += PlayerForm_FormClosing;

        _somaFm.MetadataChanged += OnMetadataChanged;
        _audio.PlaybackStateChanged += OnAudioPlaybackStateChanged;

        _uiTimer.Tick += OnUiTimerTick;
        _uiTimer.Start();

        _trackChangeNotifier.OnPlaybackStateChanged(PlaybackState.Stopped);
    }

    private void BuildControls()
    {
        _closeButton.Size = new Size(46, TitleBarHeight);
        _closeButton.Location = new Point(ClientSize.Width - _closeButton.Width, 0);
        _closeButton.Activated += (_, _) => Close();

        _minimizeButton.Size = new Size(46, TitleBarHeight);
        _minimizeButton.Location = new Point(_closeButton.Left - _minimizeButton.Width, 0);
        _minimizeButton.Activated += (_, _) => WindowState = FormWindowState.Minimized;

        int infoWidth = ClientSize.Width - InfoLeft - 30;

        _titleLabel.Text = "Loading...";
        _titleLabel.Font = new Font("Segoe UI", 20f, FontStyle.Bold);
        _titleLabel.ForeColor = Color.White;
        _titleLabel.BackColor = Color.Transparent;
        _titleLabel.AutoEllipsis = true;
        _titleLabel.Location = new Point(InfoLeft, TitleTop);
        _titleLabel.Size = new Size(infoWidth, 64);

        _artistLabel.Font = new Font("Segoe UI", 13f, FontStyle.Regular);
        _artistLabel.ForeColor = Color.Gainsboro;
        _artistLabel.BackColor = Color.Transparent;
        _artistLabel.AutoEllipsis = true;
        _artistLabel.Location = new Point(InfoLeft, ArtistTop);
        _artistLabel.Size = new Size(infoWidth, 28);

        _albumLabel.Font = new Font("Segoe UI", 10f, FontStyle.Italic);
        _albumLabel.ForeColor = Color.Silver;
        _albumLabel.BackColor = Color.Transparent;
        _albumLabel.AutoEllipsis = true;
        _albumLabel.Location = new Point(InfoLeft, AlbumTop);
        _albumLabel.Size = new Size(infoWidth, 24);

        // Click title/artist/album to copy the current track info - a small
        // convenience for pasting it somewhere (chat, a search box, etc.)
        // without having to retype it by hand.
        foreach (Label label in new[] { _titleLabel, _artistLabel, _albumLabel })
        {
            label.Cursor = Cursors.Hand;
            label.Click += (_, _) => CopyTrackInfoToClipboard(label);
        }

        _elapsedLabel.Font = new Font("Segoe UI", 8f);
        _elapsedLabel.ForeColor = Color.Gainsboro;
        _elapsedLabel.BackColor = Color.Transparent;
        _elapsedLabel.AutoSize = true;
        _elapsedLabel.Location = new Point(InfoLeft, TimeLabelsTop);
        _elapsedLabel.Text = "";

        _statusLabel.Font = new Font("Segoe UI", 8f, FontStyle.Italic);
        _statusLabel.ForeColor = Color.Silver;
        _statusLabel.BackColor = Color.Transparent;
        _statusLabel.AutoSize = true;
        _statusLabel.Location = new Point(InfoLeft, StatusTop);
        _statusLabel.Text = "";

        _playButton.Location = new Point(InfoLeft, ControlsTop);
        _playButton.Toggled += (_, _) => TogglePlayback();

        int volumeLeft = InfoLeft + 80 + 40;
        _volumeSlider.Location = new Point(volumeLeft, ControlsTop + 30);
        _volumeSlider.Size = new Size(ClientSize.Width - 30 - volumeLeft, 20);
        _volumeSlider.Value = Math.Clamp(_settings.Volume ?? 0.8, 0.0, 1.0);
        _volumeSlider.ValueChanged += (_, _) => _audio.Volume = _volumeSlider.Value;

        Controls.Add(_titleLabel);
        Controls.Add(_artistLabel);
        Controls.Add(_albumLabel);
        Controls.Add(_elapsedLabel);
        Controls.Add(_statusLabel);
        Controls.Add(_playButton);
        Controls.Add(_volumeSlider);
        Controls.Add(_minimizeButton);
        Controls.Add(_closeButton);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        DrawBackground(g);

        var artRect = new Rectangle(ArtMarginLeft, ArtMarginTop, ArtSize, ArtSize);
        DrawRoundedImage(g, artRect, _currentArt, ArtCornerRadius, Color.FromArgb(0x20, 0x20, 0x20));
        DrawRoundedBorder(g, artRect, ArtCornerRadius, Color.FromArgb(80, Color.White), 1f);

        if (_currentReflection is not null)
        {
            var reflectionRect = new Rectangle(ArtMarginLeft, ReflectionTop, ArtSize, ReflectionHeight);
            DrawRoundedImage(g, reflectionRect, _currentReflection, ArtCornerRadius, null);
        }

        DrawLivePill(g);
        DrawProgressBar(g);
        DrawSpeakerIcon(g);
        DrawTitleBar(g);
    }

    private void DrawTitleBar(Graphics g)
    {
        // Drawn last, directly on top of the same gradient DrawBackground
        // already filled the whole window with - there's no separate
        // panel/colour block here, which is what makes this seamless (see
        // WindowChromeHelper for why a native caption never quite was).
        if (_formIcon is not null)
        {
            var iconRect = new Rectangle(10, (TitleBarHeight - 18) / 2, 18, 18);
            g.DrawIcon(_formIcon, iconRect);
        }

        using var font = new Font("Segoe UI", 9.5f);
        using var textBrush = new SolidBrush(Color.Gainsboro);
        using var format = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Near };
        var textRect = new Rectangle(36, 0, _minimizeButton.Left - 36, TitleBarHeight);
        g.DrawString(Text, font, textBrush, textRect, format);
    }

    private void DrawBackground(Graphics g)
    {
        using (var baseBrush = new LinearGradientBrush(ClientRectangle, GradientTop, GradientBottom, LinearGradientMode.Vertical))
        {
            g.FillRectangle(baseBrush, ClientRectangle);
        }

        // Warm radial-ish glow bleeding up from the bottom edge - centered
        // below the window so only its upper arc shows within the client
        // area, concentrating the brightest point at bottom-center/right and
        // fading to near-black toward the top and corners.
        int cx = (int)(Width * 0.62);
        int cy = Height + (int)(Height * 0.15);
        int rx = (int)(Width * 0.95);
        int ry = (int)(Height * 0.95);
        var glowRect = new Rectangle(cx - rx, cy - ry, rx * 2, ry * 2);

        using var glowPath = new GraphicsPath();
        glowPath.AddEllipse(glowRect);
        using var glowBrush = new PathGradientBrush(glowPath)
        {
            CenterColor = Color.FromArgb(150, GlowCore),
            SurroundColors = new[] { Color.FromArgb(0, GlowCore) },
        };
        g.FillPath(glowBrush, glowPath);
    }

    private static void DrawRoundedImage(Graphics g, Rectangle rect, Image? image, int radius, Color? placeholderColor)
    {
        using GraphicsPath path = RoundedRectPath(rect, radius);
        Region oldClip = g.Clip;
        g.SetClip(path, CombineMode.Intersect);
        try
        {
            if (image is not null)
                g.DrawImage(image, rect);
            else if (placeholderColor is Color pc)
                using (var b = new SolidBrush(pc))
                    g.FillRectangle(b, rect);
        }
        finally
        {
            g.Clip = oldClip;
            oldClip.Dispose();
        }
    }

    private static void DrawRoundedBorder(Graphics g, Rectangle rect, int radius, Color color, float width)
    {
        using GraphicsPath path = RoundedRectPath(rect, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRectPath(Rectangle rect, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private Rectangle LivePillRect => new(ClientSize.Width - 30 - LivePillWidth, TitleBarHeight + 16, LivePillWidth, LivePillHeight);

    private void DrawLivePill(Graphics g)
    {
        Rectangle pillRect = LivePillRect;
        int radius = LivePillHeight / 2;

        using (GraphicsPath pillPath = RoundedRectPath(pillRect, radius))
        {
            using (var fill = new SolidBrush(Color.FromArgb(60, 0x40, 0x08, 0x08)))
                g.FillPath(fill, pillPath);
            using (var border = new Pen(AccentColor, 1f))
                g.DrawPath(border, pillPath);
        }

        int dotSize = 9;
        var dotRect = new Rectangle(pillRect.X + 12, pillRect.Y + (pillRect.Height - dotSize) / 2, dotSize, dotSize);
        using (var dotBrush = new SolidBrush(Color.FromArgb((int)_pulseAlpha, AccentColor)))
            g.FillEllipse(dotBrush, dotRect);

        using var textBrush = new SolidBrush(AccentColor);
        using var font = new Font("Segoe UI", 11f, FontStyle.Bold);
        var textRect = new Rectangle(dotRect.Right + 6, pillRect.Y, pillRect.Right - (dotRect.Right + 6) - 12, pillRect.Height);
        using var format = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Near };
        g.DrawString("LIVE", font, textBrush, textRect, format);
    }

    private void DrawProgressBar(Graphics g)
    {
        if (!_isActive || _trackStartTime is null)
            return;

        int infoWidth = ClientSize.Width - InfoLeft - 30;
        var barRect = new Rectangle(InfoLeft, ProgressTop, infoWidth, ProgressHeight);

        using (GraphicsPath trackPath = RoundedRectPath(barRect, ProgressHeight / 2))
        {
            using var trackBrush = new SolidBrush(Color.FromArgb(0x2a, 0x22, 0x22));
            g.FillPath(trackBrush, trackPath);
        }

        int filledWidth = (int)(infoWidth * _progressFraction);
        if (filledWidth > 0)
        {
            var filledRect = new Rectangle(InfoLeft, ProgressTop, Math.Max(ProgressHeight, filledWidth), ProgressHeight);
            using GraphicsPath filledPath = RoundedRectPath(filledRect, ProgressHeight / 2);
            using var filledBrush = new SolidBrush(AccentColor);
            g.FillPath(filledBrush, filledPath);
        }

        int thumbDiameter = ProgressHeight + 4;
        int thumbX = InfoLeft + Math.Clamp(filledWidth - thumbDiameter / 2, 0, infoWidth - thumbDiameter);
        var thumbRect = new Rectangle(thumbX, ProgressTop - 2, thumbDiameter, thumbDiameter);
        using (var thumbBrush = new SolidBrush(Color.White))
            g.FillEllipse(thumbBrush, thumbRect);
    }

    private void DrawSpeakerIcon(Graphics g)
    {
        int volumeLeft = InfoLeft + 80 + 40;
        var rect = new Rectangle(volumeLeft - 30, ControlsTop + 28, 20, 24);

        using var brush = new SolidBrush(Color.Gainsboro);
        // Speaker body: a small rectangle plus a triangle "horn", a compact
        // hand-drawn glyph rather than pulling in an icon font/resource.
        var body = new Rectangle(rect.X, rect.Y + 7, 6, 10);
        g.FillRectangle(brush, body);

        var horn = new[]
        {
            new Point(rect.X + 6, rect.Y + 7),
            new Point(rect.X + 12, rect.Y + 2),
            new Point(rect.X + 12, rect.Y + 22),
            new Point(rect.X + 6, rect.Y + 17),
        };
        g.FillPolygon(brush, horn);

        using var pen = new Pen(Color.Gainsboro, 1.5f);
        g.DrawArc(pen, rect.X + 13, rect.Y + 5, 8, 14, -50, 100);
    }

    // Form-level mouse events only fire for pixels not already claimed by a
    // child control, so this naturally only sees clicks on the "empty" part
    // of the title bar strip - the minimize/close TitleBarButtons handle
    // their own clicks independently via their Activated event.
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Y >= TitleBarHeight)
            return;

        if (e.Button == MouseButtons.Left)
        {
            WindowChromeHelper.BeginDrag(Handle);
        }
        else if (e.Button == MouseButtons.Right)
        {
            WindowChromeHelper.ShowSystemMenu(Handle, PointToScreen(e.Location));
        }
    }

    private async void PlayerForm_Load(object? sender, EventArgs e)
    {
        _smtc = new SmtcService(Handle);
        _smtc.ButtonPressed += OnSmtcButtonPressed;

        // Track-change toasts are ephemeral - sweep out this app's own
        // Notification Center history on every launch (including anything
        // left over from before notifications were turned off) rather than
        // letting old entries pile up indefinitely.
        TrackChangeNotifier.ClearHistory();

        // Show the station's own logo as a placeholder immediately, rather
        // than leaving the art panel blank until the first track's own
        // artwork resolves (which can take a few seconds through the
        // fanart.tv/Deezer/Bandcamp/iTunes chain). _somaFm.LogoUrl already
        // has a sane hardcoded default even before channels.json has loaded.
        _ = LoadPlaceholderArtAsync();

        try
        {
            await _audio.InitializeAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AudioPlayerService.InitializeAsync failed: {ex.Message}");
        }

        try
        {
            await _somaFm.StartAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SomaFmService.StartAsync failed: {ex.Message}");
        }
    }

    private void OnMetadataChanged(TrackMetadata metadata)
    {
        if (IsDisposed) return;

        // SomaFmService's Timer already ticks on the UI thread, so no
        // marshaling is strictly required here, but BeginInvoke is cheap
        // insurance if that ever changes.
        BeginInvoke(new Action(() =>
        {
            _lastMetadata = metadata;
            _titleLabel.Text = metadata.Title;
            _artistLabel.Text = metadata.Artist;
            _albumLabel.Text = metadata.Album;

            // SomaFmService already dedupes by track identity, so any
            // MetadataChanged event here means a genuinely new track -
            // always reset the fake-progress elapsed-time basis, never
            // continue accumulating from the previous track's start time.
            // The real duration (if any) isn't known yet either - it's set
            // once artwork resolves, below/in UpdateArtworkAsync - so this
            // starts on the fake curve and may snap to the real one shortly
            // after if a source has a duration for this track.
            _trackStartTime = metadata.StartedAt ?? DateTimeOffset.UtcNow;
            _realDuration = null;
            UpdateProgress();

            if (_isPlaying)
                _lastFm.OnTrackChanged(metadata);

            // SMTC/toast/Discord metadata that needs art is pushed once
            // artwork resolves - see UpdateArtworkAsync - rather than here,
            // since SomaFM's own feed never supplies art (TrackMetadata.ArtUrl
            // is always empty) and pushing text-only metadata first just
            // means a second update moments later once art shows up. Last.fm
            // scrobbling doesn't use art, so it fires immediately above.
            _ = UpdateArtworkAsync(metadata);
        }));
    }

    private async Task LoadPlaceholderArtAsync()
    {
        try
        {
            Image? logo = await _artwork.GetFallbackLogoAsync(_somaFm.LogoUrl, CancellationToken.None);
            // Only apply it if a real track's artwork hasn't already resolved
            // and won the race - this is purely a "don't sit blank while
            // waiting" placeholder, never allowed to clobber real art.
            if (logo is not null && _currentArt is null && !IsDisposed)
                SetAlbumArt(logo);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LoadPlaceholderArtAsync failed: {ex.Message}");
        }
    }

    private async Task UpdateArtworkAsync(TrackMetadata metadata)
    {
        _artworkCts?.Cancel();
        var cts = new CancellationTokenSource();
        _artworkCts = cts;

        Image? art = null;
        string? artPath = null;
        try
        {
            art = await _artwork.GetArtworkAsync(metadata.Artist, metadata.Title, metadata.Album, cts.Token);
            artPath = art is not null ? _artwork.GetCachedArtPath(metadata.Artist, metadata.Title) : null;

            if (art is null)
            {
                art = await _artwork.GetFallbackLogoAsync(_somaFm.LogoUrl, cts.Token);
                artPath = _artwork.FallbackLogoPath;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Artwork lookup failed: {ex.Message}");
        }

        if (cts.IsCancellationRequested || IsDisposed)
            return;

        SetAlbumArt(art);

        // Deezer/iTunes/MusicBrainz all report track duration in the same
        // response already fetched for the art - free data. Prefer it over
        // the fake progress curve once it's known (see UpdateProgress); a
        // Bandcamp-only match or no match at all leaves this null, in which
        // case the fake curve keeps running uninterrupted.
        _realDuration = _artwork.GetCachedDuration(metadata.Artist, metadata.Title);
        UpdateProgress();

        Logger.Log($"UpdateArtworkAsync - resolved artPath='{artPath ?? "(none)"}', duration={_realDuration?.ToString() ?? "(unknown)"} for '{metadata.Artist} - {metadata.Title}'");

        if (_smtc is not null)
            await _smtc.UpdateMetadataAsync(metadata.Title, metadata.Artist, metadata.Album, artPath);
        _trackChangeNotifier.OnMetadataChanged(metadata, artPath);

        if (_isPlaying)
            _discord.OnTrackChanged(metadata, ResolveDiscordArtUrl(metadata));
    }

    /// <summary>
    /// A public, fetchable URL for Discord Rich Presence's image field - NOT
    /// TrackMetadata.ArtUrl (SomaFM's feed never supplies one). Prefers
    /// whichever remote source ArtworkService's chain resolved to for this
    /// track (see GetCachedArtSourceUrl); falls back to the station's own
    /// logo URL, which is itself always a valid public URL.
    /// </summary>
    private string? ResolveDiscordArtUrl(TrackMetadata metadata) =>
        _artwork.GetCachedArtSourceUrl(metadata.Artist, metadata.Title) ?? _somaFm.LogoUrl;

    private void SetAlbumArt(Image? art)
    {
        // art is owned by ArtworkService's cache - never disposed here.
        _currentArt = art;

        _currentReflection?.Dispose();
        _currentReflection = art is not null
            ? CreateReflection(art, ArtSize, ReflectionHeight, GradientBottom)
            : null;

        Invalidate();
    }

    private static Bitmap? CreateReflection(Image source, int width, int reflectionHeight, Color fadeToColor)
    {
        try
        {
            using var flipped = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(flipped))
            {
                g.DrawImage(source, 0, 0, source.Width, source.Height);
            }
            flipped.RotateFlip(RotateFlipType.RotateNoneFlipY);

            var reflection = new Bitmap(width, reflectionHeight, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(reflection))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                // Scale the flipped image to the target width and draw it
                // anchored at the top - GDI+ clips drawing to the destination
                // bitmap's bounds by default, so only the top `reflectionHeight`
                // slice (== the source's bottom edge, now flipped to the top)
                // actually shows up. That slice is what a short reflection strip
                // directly beneath the album art should show.
                //
                // Scaled down to ~35% alpha via a ColorMatrix (rather than
                // drawn at full opacity) so the whole reflection reads as a
                // subtle, translucent echo rather than a second solid copy of
                // the artwork - a plain top-to-bottom fade alone still left it
                // looking too solid near the top edge.
                float scale = (float)width / flipped.Width;
                int scaledFullHeight = (int)(flipped.Height * scale);

                var alphaMatrix = new ColorMatrix { Matrix33 = 0.35f };
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(alphaMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                g.DrawImage(flipped, new Rectangle(0, 0, width, scaledFullHeight), 0, 0, flipped.Width, flipped.Height, GraphicsUnit.Pixel, attributes);

                // Fade top-to-bottom into the background colour (a standard
                // GDI+ reflection trick) so it blends into the gradient
                // instead of ending with a hard edge - starting the fade
                // already partway opaque (rather than fully transparent at
                // the very top) means the whole strip trails off gradually
                // toward the bottom instead of reading as a sharp cutoff.
                using var fadeBrush = new LinearGradientBrush(
                    new Rectangle(0, 0, width, reflectionHeight),
                    Color.FromArgb(20, fadeToColor),
                    Color.FromArgb(255, fadeToColor),
                    LinearGradientMode.Vertical);
                g.FillRectangle(fadeBrush, 0, 0, width, reflectionHeight);
            }

            return reflection;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CreateReflection failed: {ex.Message}");
            return null;
        }
    }

    private void TogglePlayback()
    {
        if (_isActive)
            _audio.Stop();
        else
            _audio.Play();
    }

    private void CopyTrackInfoToClipboard(Control anchor)
    {
        if (_lastMetadata is not TrackMetadata metadata)
            return;

        string text = $"{metadata.Title} — {metadata.Artist} — {metadata.Album}";

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            // Clipboard access can transiently fail (another app briefly
            // holding it) - not worth surfacing as an error to the user.
            Debug.WriteLine($"CopyTrackInfoToClipboard failed: {ex.Message}");
            return;
        }

        _copyToolTip.Show("Copied to clipboard", anchor, anchor.Width / 2, -22, 1200);
    }

    // AudioPlayerService may raise this from a background (MTA) thread - the
    // same caveat SmtcService.ButtonPressed carries - so always marshal back
    // to the UI thread before touching any control.
    private void OnAudioPlaybackStateChanged(PlaybackState state)
    {
        if (IsDisposed) return;

        BeginInvoke(new Action(() =>
        {
            bool wasPlaying = _isPlaying;
            _isPlaying = state == PlaybackState.Playing;
            _isActive = state != PlaybackState.Stopped;

            _playButton.IsPlaying = _isActive;
            _statusLabel.Text = state == PlaybackState.Buffering ? "Reconnecting…" : "";

            if (!_isActive)
            {
                _trackStartTime = null;
                _progressFraction = 0;
            }
            else if (_trackStartTime is null && _lastMetadata is TrackMetadata resumed)
            {
                // Resuming playback (e.g. after Stop) without a genuinely new
                // track showing up - SomaFmService dedupes by track identity,
                // so no fresh MetadataChanged event fires here. Without this,
                // the progress bar stayed hidden forever after a resume,
                // since only OnMetadataChanged used to set _trackStartTime.
                _trackStartTime = resumed.StartedAt ?? DateTimeOffset.UtcNow;
                UpdateProgress();
            }

            // Externally (SMTC/tray icon), playback only ever reports as
            // Playing or Stopped - never Paused/Buffering/Changing. Buffering
            // is treated as "still trying to be Playing" for SMTC purposes:
            // flipping the session to Stopped while merely reconnecting would
            // leave it unable to be resumed from the flyout, which is exactly
            // the bug this pass fixes (see AudioPlayerService remarks).
            _smtc?.SetPlaybackStatus(state == PlaybackState.Stopped
                ? MediaPlaybackStatus.Stopped
                : MediaPlaybackStatus.Playing);

            _trackChangeNotifier.OnPlaybackStateChanged(state);

            if (state == PlaybackState.Stopped && wasPlaying)
            {
                _lastFm.OnPlaybackStopped();
                _discord.OnPlaybackStopped();
            }
            else if (!wasPlaying && _isPlaying && _lastMetadata is TrackMetadata current)
            {
                // SomaFmService polls independently of play state and dedupes
                // by track identity, so if the current track was already
                // captured before Play was pressed, no new metadata event
                // will fire for it - feed it in directly here instead of
                // waiting for the next track change.
                _lastFm.OnTrackChanged(current);
                _discord.OnTrackChanged(current, ResolveDiscordArtUrl(current));
            }

            Invalidate();
            PlaybackStateChanged?.Invoke(state);
        }));
    }

    private void OnSmtcButtonPressed(SystemMediaTransportControlsButton button)
    {
        if (IsDisposed) return;

        BeginInvoke(new Action(() =>
        {
            switch (button)
            {
                case SystemMediaTransportControlsButton.Play:
                    _audio.Play();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                case SystemMediaTransportControlsButton.Stop:
                    // Live stream, no real "paused" position - Pause and Stop
                    // both just stop it. SMTC's Pause button is disabled (see
                    // SmtcService) but some Windows versions can still send
                    // one occasionally - handled the same way defensively.
                    _audio.Stop();
                    break;
            }
        }));
    }

    private void OnUiTimerTick(object? sender, EventArgs e)
    {
        _uiTimerTicks++;

        // Pulse the LIVE dot's alpha smoothly (~2.6s period) - a restrained
        // animation, not distracting, and scoped to just the dot itself.
        double phase = (_uiTimerTicks * _uiTimer.Interval / 1000.0) * (2 * Math.PI / 2.6);
        _pulseAlpha = 153 + (255 - 153) * (0.5 + 0.5 * Math.Sin(phase)); // oscillates ~60%-100% alpha
        Invalidate(LivePillRect);

        // Recompute the fake progress + elapsed/remaining labels roughly once
        // a second (every 10th 100ms tick) rather than every tick.
        if (_uiTimerTicks % 10 == 0)
        {
            UpdateProgress();
        }
    }

    private void UpdateProgress()
    {
        // Full-width, generously-padded invalidate - a previous version only
        // invalidated a rect starting at ProgressTop, but the thumb paints
        // 2px above that (see DrawProgressBar's thumbRect) and anti-aliased
        // circle edges bleed a pixel or two further still, leaving a faint
        // leftover smear as the thumb moved right and the old edge pixels
        // were never repainted. A wider margin all around is cheap insurance
        // against the same class of artifact recurring.
        int infoWidth = ClientSize.Width - InfoLeft - 30;
        Rectangle invalidateRect = new(InfoLeft - 4, ProgressTop - 8, infoWidth + 8, ProgressHeight + 20);

        if (!_isActive || _trackStartTime is null)
        {
            _elapsedLabel.Text = "";
            Invalidate(invalidateRect);
            return;
        }

        double elapsedSeconds = Math.Max(0, (DateTimeOffset.UtcNow - _trackStartTime.Value).TotalSeconds);

        if (_realDuration is TimeSpan real && real.TotalSeconds > 0)
        {
            _progressEffectiveTotalSeconds = real.TotalSeconds;
            _progressFraction = Math.Clamp(elapsedSeconds / real.TotalSeconds, 0.0, 1.0);
        }
        else
        {
            (_progressFraction, _progressEffectiveTotalSeconds) = ComputeFakeProgress(elapsedSeconds);
        }

        _elapsedLabel.Text = FormatTime(elapsedSeconds);

        Invalidate(invalidateRect);
    }

    private static string FormatTime(double totalSeconds)
    {
        var span = TimeSpan.FromSeconds(totalSeconds);
        return span.Hours > 0
            ? $"{span.Hours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";
    }

    /// <summary>
    /// Purely cosmetic, deterministic progress fraction as a function of real
    /// elapsed seconds since track start - never jumps backward, never
    /// reaches 100%. There's no real track-duration API for this station, so
    /// this exists purely to give the progress bar "some visual interest,
    /// not accuracy" (the user's own framing) rather than sitting static:
    ///
    ///   - first 240s (of a 300s baseline): ramps linearly 0.0 -> 0.8
    ///   - beyond that: each further segment doubles the previous segment's
    ///     duration while halving the remaining distance to 1.0 (240s: 0.8,
    ///     +480s: 0.9, +960s: 0.95, +1920s: 0.975, ...), continuing
    ///     indefinitely - a smooth, continuously-forward, decelerating
    ///     asymptotic curve that never actually finishes.
    ///
    /// Also returns the "effective total duration" implied at this point on
    /// the curve (segmentStart + segmentDuration of whichever segment
    /// elapsedSeconds currently falls in), used to derive a "remaining time"
    /// label that reads sensibly next to the bar without inventing an
    /// unrelated number.
    /// </summary>
    internal static (double Fraction, double EffectiveTotalSeconds) ComputeFakeProgress(double elapsedSeconds)
    {
        if (elapsedSeconds <= 0)
            return (0.0, BaselineTotalSeconds);

        if (elapsedSeconds <= BaselineRampSeconds)
        {
            double fraction = (elapsedSeconds / BaselineRampSeconds) * BaselineRampFraction;
            return (fraction, BaselineTotalSeconds);
        }

        double segmentStart = BaselineRampSeconds;
        double segmentDuration = BaselineTotalSeconds - BaselineRampSeconds; // 480s (first post-ramp segment)
        double fractionStart = BaselineRampFraction;
        double remainingDistance = 1.0 - BaselineRampFraction; // 0.2

        while (true)
        {
            double segmentEnd = segmentStart + segmentDuration;
            double segmentTargetFraction = fractionStart + remainingDistance / 2.0;

            if (elapsedSeconds <= segmentEnd)
            {
                double progressInSegment = (elapsedSeconds - segmentStart) / segmentDuration;
                double fraction = fractionStart + (segmentTargetFraction - fractionStart) * progressInSegment;
                return (fraction, segmentEnd);
            }

            fractionStart = segmentTargetFraction;
            remainingDistance /= 2.0;
            segmentStart = segmentEnd;
            segmentDuration *= 2.0;
        }
    }

    private void TrySetIcon()
    {
        try
        {
            _formIcon = LoadEmbeddedIcon("app.ico");
            Icon = _formIcon;
        }
        catch
        {
            // Missing/invalid icon resource shouldn't stop the app from running.
        }
    }

    private static Icon LoadEmbeddedIcon(string resourceName)
    {
        using Stream? stream = typeof(PlayerForm).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        return new Icon(stream);
    }

    private void PlayerForm_Resize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            Hide();
        }
    }

    private void PlayerForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_allowClose && _settings.MinimizeToTrayOnClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        PersistWindowState();
        SettingsStore.Save(_settings);
    }

    private void PersistWindowState()
    {
        if (WindowState == FormWindowState.Normal)
        {
            _settings.WindowX = Location.X;
            _settings.WindowY = Location.Y;
        }
    }

    private static bool IsOnScreen(int x, int y)
    {
        foreach (Screen screen in Screen.AllScreens)
        {
            if (screen.WorkingArea.Contains(x, y))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Brings the player window to the foreground, restoring it if minimized/hidden.</summary>
    public void ShowAndActivate()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        Activate();
    }

    /// <summary>Actually closes the window (bypassing minimize-to-tray) - used when the app is exiting.</summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    public bool IsLastFmAuthorized => _lastFm.IsAuthorized;
    public string? LastFmUsername => _settings.LastFmUsername;

    /// <summary>
    /// Runs Last.fm's "desktop application" auth flow: get a token, send the
    /// user to authorize it in their real browser, then (once they confirm
    /// they've done so) exchange it for a session key that doesn't expire
    /// until revoked.
    /// </summary>
    public async Task ConnectLastFmAsync()
    {
        if (!_lastFm.IsConfigured)
        {
            MessageBox.Show(
                "Add a Last.fm API key and secret in Settings first " +
                "(get one free at last.fm/api/account/create), then try again.",
                "Metal Detector",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            string token = await _lastFm.GetAuthTokenAsync();
            Process.Start(new ProcessStartInfo(_lastFm.BuildAuthorizeUrl(token)) { UseShellExecute = true });

            DialogResult result = MessageBox.Show(
                "A browser window opened so you can authorize this app on Last.fm.\n\n" +
                "Once you've approved it there, click OK to finish connecting.",
                "Connect Last.fm",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information);
            if (result != DialogResult.OK)
                return;

            (string sessionKey, string username) = await _lastFm.CompleteAuthAsync(token);
            _settings.LastFmSessionKey = sessionKey;
            _settings.LastFmUsername = username;
            SettingsStore.Save(_settings);

            MessageBox.Show(
                $"Connected to Last.fm as {username}.",
                "Metal Detector",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Couldn't connect to Last.fm.\n\nDetails: {ex.Message}",
                "Metal Detector",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    public void DisconnectLastFm()
    {
        _settings.LastFmSessionKey = null;
        _settings.LastFmUsername = null;
        SettingsStore.Save(_settings);
    }

    /// <summary>Re-opens the Discord RPC connection - used by the settings dialog after the Client ID/toggle is changed.</summary>
    public void RestartDiscordPresence() => _discord.Restart();

    private void BuildSystemMenu()
    {
        SystemMenuHelper.AddSeparator(Handle);
        SystemMenuHelper.AddItem(Handle, CmdSettings, "Settings...");
        SystemMenuHelper.AddItem(Handle, CmdStartWithWindows, "Start with Windows");
        SystemMenuHelper.AddItem(Handle, CmdStartMinimized, "Start Minimized to Tray");
        SystemMenuHelper.AddItem(Handle, CmdMinimizeToTrayOnClose, "Minimize to Tray on Close");
        SystemMenuHelper.AddItem(Handle, CmdShowTrackChangeNotifications, "Show Notification on Track Change");
        SystemMenuHelper.AddSeparator(Handle);
        SystemMenuHelper.AddItem(Handle, CmdExit, "Exit");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INITMENU)
        {
            SystemMenuHelper.SetChecked(Handle, CmdStartWithWindows, StartupManager.IsEnabled());
            SystemMenuHelper.SetChecked(Handle, CmdStartMinimized, _settings.StartMinimizedToTray);
            SystemMenuHelper.SetChecked(Handle, CmdMinimizeToTrayOnClose, _settings.MinimizeToTrayOnClose);
            SystemMenuHelper.SetChecked(Handle, CmdShowTrackChangeNotifications, _settings.ShowTrackChangeNotifications);
        }
        else if (m.Msg == WM_SYSCOMMAND)
        {
            int cmd = (int)m.WParam & 0xFFF0;
            if (HandleSystemMenuCommand(cmd))
                return;
        }

        base.WndProc(ref m);
    }

    private bool HandleSystemMenuCommand(int cmd)
    {
        switch (cmd)
        {
            case CmdSettings:
                using (var settingsForm = new SettingsForm(_settings, this))
                    settingsForm.ShowDialog(this);
                return true;

            case CmdStartWithWindows:
                StartupManager.SetEnabled(!StartupManager.IsEnabled());
                return true;

            case CmdStartMinimized:
                _settings.StartMinimizedToTray = !_settings.StartMinimizedToTray;
                SettingsStore.Save(_settings);
                return true;

            case CmdMinimizeToTrayOnClose:
                _settings.MinimizeToTrayOnClose = !_settings.MinimizeToTrayOnClose;
                SettingsStore.Save(_settings);
                return true;

            case CmdShowTrackChangeNotifications:
                _settings.ShowTrackChangeNotifications = !_settings.ShowTrackChangeNotifications;
                SettingsStore.Save(_settings);
                return true;

            case CmdExit:
                ExitRequested?.Invoke();
                return true;

            default:
                return false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _uiTimer.Stop();
            _uiTimer.Dispose();
            _copyToolTip.Dispose();
            _artworkCts?.Cancel();
            _currentReflection?.Dispose();
            _formIcon?.Dispose();
            _smtc?.Dispose();
            _somaFm.Dispose();
            _artwork.Dispose();
            _audio.Dispose();
            _lastFm.Dispose();
            _discord.Dispose();
        }
        base.Dispose(disposing);
    }
}
