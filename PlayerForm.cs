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
/// album art with a reflection on the left, over a dark gradient background,
/// track info on the right, play/stop + volume underneath. Closing the window
/// hides it to the tray instead of exiting (configurable); the tray icon is
/// what actually owns app lifetime.
/// </summary>
public sealed class PlayerForm : Form
{
    // Matches the death.fm/DeathFmAndroid palette family - near-black fading
    // to a dark red, "metal-appropriate" per the brief. Refine in a v2 pass.
    private static readonly Color GradientTop = Color.FromArgb(0x12, 0x10, 0x10);
    private static readonly Color GradientBottom = Color.FromArgb(0x3a, 0x0c, 0x0c);
    private static readonly Color AccentColor = Color.FromArgb(0xc0, 0x30, 0x30);

    private const int ArtSize = 260;
    private const int ArtMarginLeft = 30;
    private const int ArtMarginTop = 70;
    private const int ReflectionHeight = 90;

    private readonly AppSettings _settings;
    private readonly SomaFmService _somaFm;
    private readonly ArtworkService _artwork;
    private readonly AudioPlayerService _audio;
    private readonly TrackChangeNotifier _trackChangeNotifier;
    private readonly LastFmScrobbler _lastFm;
    private readonly DiscordPresenceService _discord;

    private readonly Label _stationLabel = new();
    private readonly Label _titleLabel = new();
    private readonly Label _artistLabel = new();
    private readonly Label _albumLabel = new();
    private readonly Button _playStopButton = new();
    private readonly TrackBar _volumeSlider = new();
    private readonly Label _volumeLabel = new();

    private SmtcService? _smtc;
    private Icon? _formIcon;
    private bool _allowClose;
    private bool _isPlaying;
    private TrackMetadata? _lastMetadata;

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

        Text = "SomaFM Metal Detector Player";

        var size = new Size(720, 470);
        MinimumSize = size;
        MaximumSize = size;
        ClientSize = size;
        FormBorderStyle = FormBorderStyle.FixedSingle;
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

        HandleCreated += (_, _) => WindowChromeHelper.ApplyDarkTitleBar(this, GradientTop, Color.White);
        HandleCreated += (_, _) => BuildSystemMenu();

        BuildControls();

        Load += PlayerForm_Load;
        Resize += PlayerForm_Resize;
        FormClosing += PlayerForm_FormClosing;

        _somaFm.MetadataChanged += OnMetadataChanged;
        _somaFm.StationInfoLoaded += OnStationInfoLoaded;
        _audio.PlaybackStateChanged += OnAudioPlaybackStateChanged;

        _trackChangeNotifier.OnPlaybackStateChanged(PlaybackState.Stopped);
    }

    private void BuildControls()
    {
        _stationLabel.Text = "SomaFM Metal Detector";
        _stationLabel.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _stationLabel.ForeColor = Color.Gainsboro;
        _stationLabel.BackColor = Color.Transparent;
        _stationLabel.AutoSize = true;
        _stationLabel.Location = new Point(ArtMarginLeft, 20);

        int infoLeft = ArtMarginLeft + ArtSize + 30;
        int infoWidth = ClientSize.Width - infoLeft - 30;

        _titleLabel.Text = "Loading...";
        _titleLabel.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
        _titleLabel.ForeColor = Color.White;
        _titleLabel.BackColor = Color.Transparent;
        _titleLabel.AutoEllipsis = true;
        _titleLabel.Location = new Point(infoLeft, ArtMarginTop + 10);
        _titleLabel.Size = new Size(infoWidth, 60);

        _artistLabel.Font = new Font("Segoe UI", 12f, FontStyle.Regular);
        _artistLabel.ForeColor = Color.Gainsboro;
        _artistLabel.BackColor = Color.Transparent;
        _artistLabel.AutoEllipsis = true;
        _artistLabel.Location = new Point(infoLeft, ArtMarginTop + 75);
        _artistLabel.Size = new Size(infoWidth, 30);

        _albumLabel.Font = new Font("Segoe UI", 10f, FontStyle.Italic);
        _albumLabel.ForeColor = Color.Silver;
        _albumLabel.BackColor = Color.Transparent;
        _albumLabel.AutoEllipsis = true;
        _albumLabel.Location = new Point(infoLeft, ArtMarginTop + 110);
        _albumLabel.Size = new Size(infoWidth, 30);

        _playStopButton.Text = "▶  Play";
        _playStopButton.FlatStyle = FlatStyle.Flat;
        _playStopButton.FlatAppearance.BorderColor = AccentColor;
        _playStopButton.BackColor = Color.FromArgb(0x28, 0x10, 0x10);
        _playStopButton.ForeColor = Color.White;
        _playStopButton.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        _playStopButton.Size = new Size(140, 44);
        _playStopButton.Location = new Point(ArtMarginLeft, ClientSize.Height - 70);
        _playStopButton.Click += (_, _) => TogglePlayback();

        _volumeLabel.Text = "Volume";
        _volumeLabel.ForeColor = Color.Gainsboro;
        _volumeLabel.BackColor = Color.Transparent;
        _volumeLabel.AutoSize = true;
        _volumeLabel.Location = new Point(ArtMarginLeft + 160, ClientSize.Height - 68);

        _volumeSlider.Minimum = 0;
        _volumeSlider.Maximum = 100;
        _volumeSlider.TickFrequency = 10;
        _volumeSlider.Value = (int)Math.Round(Math.Clamp(_settings.Volume ?? 0.8, 0.0, 1.0) * 100);
        _volumeSlider.Size = new Size(220, 45);
        _volumeSlider.Location = new Point(ArtMarginLeft + 160, ClientSize.Height - 55);
        _volumeSlider.Scroll += (_, _) => _audio.Volume = _volumeSlider.Value / 100.0;

        Controls.Add(_stationLabel);
        Controls.Add(_titleLabel);
        Controls.Add(_artistLabel);
        Controls.Add(_albumLabel);
        Controls.Add(_playStopButton);
        Controls.Add(_volumeLabel);
        Controls.Add(_volumeSlider);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        using (var backgroundBrush = new LinearGradientBrush(ClientRectangle, GradientTop, GradientBottom, LinearGradientMode.Vertical))
        {
            g.FillRectangle(backgroundBrush, ClientRectangle);
        }

        var artRect = new Rectangle(ArtMarginLeft, ArtMarginTop, ArtSize, ArtSize);

        if (_currentArt is not null)
        {
            g.DrawImage(_currentArt, artRect);
        }
        else
        {
            using var placeholderBrush = new SolidBrush(Color.FromArgb(0x20, 0x20, 0x20));
            g.FillRectangle(placeholderBrush, artRect);
        }

        using (var borderPen = new Pen(Color.FromArgb(80, Color.White), 1f))
        {
            g.DrawRectangle(borderPen, artRect);
        }

        if (_currentReflection is not null)
        {
            g.DrawImage(_currentReflection, new Rectangle(ArtMarginLeft, artRect.Bottom + 2, ArtSize, ReflectionHeight));
        }
    }

    private async void PlayerForm_Load(object? sender, EventArgs e)
    {
        _smtc = new SmtcService(Handle);
        _smtc.ButtonPressed += OnSmtcButtonPressed;

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

    private void OnStationInfoLoaded()
    {
        if (IsDisposed) return;
        BeginInvoke(new Action(() =>
        {
            _stationLabel.Text = $"SomaFM {_somaFm.StationTitle}  •  DJ {_somaFm.DjName}";
        }));
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

            _smtc?.UpdateMetadata(metadata.Title, metadata.Artist, metadata.Album, metadata.ArtUrl);
            _trackChangeNotifier.OnMetadataChanged(metadata);

            if (_isPlaying)
            {
                _lastFm.OnTrackChanged(metadata);
                _discord.OnTrackChanged(metadata);
            }

            _ = UpdateArtworkAsync(metadata);
        }));
    }

    private async Task UpdateArtworkAsync(TrackMetadata metadata)
    {
        _artworkCts?.Cancel();
        var cts = new CancellationTokenSource();
        _artworkCts = cts;

        Image? art = null;
        try
        {
            art = await _artwork.GetArtworkAsync(metadata.Artist, metadata.Title, metadata.Album, cts.Token);
            art ??= await _artwork.GetFallbackLogoAsync(_somaFm.LogoUrl, cts.Token);
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
    }

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
                float scale = (float)width / flipped.Width;
                int scaledFullHeight = (int)(flipped.Height * scale);
                g.DrawImage(flipped, new Rectangle(0, 0, width, scaledFullHeight));

                // Fade top-to-bottom into the background colour (a standard
                // GDI+ reflection trick) so it blends into the gradient
                // instead of ending with a hard edge.
                using var fadeBrush = new LinearGradientBrush(
                    new Rectangle(0, 0, width, reflectionHeight),
                    Color.FromArgb(0, fadeToColor),
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
        if (_isPlaying)
            _audio.Stop();
        else
            _audio.Play();
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

            _playStopButton.Text = _isPlaying ? "■  Stop" : "▶  Play";

            _smtc?.SetPlaybackStatus(state switch
            {
                PlaybackState.Playing => MediaPlaybackStatus.Playing,
                PlaybackState.Paused => MediaPlaybackStatus.Paused,
                PlaybackState.Buffering => MediaPlaybackStatus.Changing,
                _ => MediaPlaybackStatus.Stopped,
            });

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
                _discord.OnTrackChanged(current);
            }

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
                    // Live stream, no real "paused" position - Pause and Stop both just stop it.
                    _audio.Stop();
                    break;
            }
        }));
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
                "SomaFM Metal Detector Player",
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
                "SomaFM Metal Detector Player",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Couldn't connect to Last.fm.\n\nDetails: {ex.Message}",
                "SomaFM Metal Detector Player",
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
