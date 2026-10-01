using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SomaMetalTray;

/// <summary>
/// The app's real "root" - a NotifyIcon plus context menu that owns the
/// PlayerForm's lifetime. Using ApplicationContext (rather than a normal
/// Application.Run(form)) means closing/hiding the window never quits the app;
/// only "Exit" from the tray menu does.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly PlayerForm _playerForm;
    private readonly NotifyIcon _trayIcon;
    private readonly Icon _idleTrayIcon;
    private readonly Icon _playingTrayIcon;
    private readonly UpdateChecker _updateChecker;
    private ToolStripMenuItem? _updateItem;
    private string? _updateUrl;

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public TrayAppContext()
    {
        _settings = SettingsStore.Load();
        _playerForm = new PlayerForm(_settings);
        _playerForm.PlaybackStateChanged += OnPlaybackStateChanged;
        _playerForm.ExitRequested += ExitApplication;

        _idleTrayIcon = LoadTrayIcon();
        _playingTrayIcon = BuildPlayingIcon(_idleTrayIcon);
        _trayIcon = new NotifyIcon
        {
            Icon = _idleTrayIcon,
            Text = AppInfo.Name,
            ContextMenuStrip = BuildContextMenu(),
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowPlayer();

        _updateChecker = new UpdateChecker(() => _settings.CheckForUpdates);
        _updateChecker.UpdateAvailable += OnUpdateAvailable;

        if (ShouldStartMinimized())
        {
            // Stay tray-only until the user opens the player.
        }
        else
        {
            _playerForm.Show();
        }
    }

    private void OnPlaybackStateChanged(PlaybackState state)
    {
        _trayIcon.Icon = state == PlaybackState.Playing ? _playingTrayIcon : _idleTrayIcon;
    }

    // Draws a small green "playing" dot over the base tray icon rather than
    // needing a second hand-authored .ico asset.
    private static Icon BuildPlayingIcon(Icon baseIcon)
    {
        using Bitmap bitmap = baseIcon.ToBitmap();
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int dotSize = Math.Max(4, bitmap.Width / 2);
            var rect = new Rectangle(bitmap.Width - dotSize, bitmap.Height - dotSize, dotSize, dotSize);
            g.FillEllipse(Brushes.LimeGreen, rect);
            g.DrawEllipse(Pens.Black, rect);
        }

        // Bitmap.GetHicon() hands back an HICON we own and must destroy
        // ourselves; Icon.FromHandle(...).Clone() copies the icon data into a
        // fully independent, normally-disposable Icon so we can safely
        // destroy the raw handle right after.
        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using Icon transient = Icon.FromHandle(hIcon);
            return (Icon)transient.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private bool ShouldStartMinimized()
    {
        bool launchedMinimized = Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(arg => arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        return launchedMinimized || _settings.StartMinimizedToTray;
    }

    // SystemIcons.Application is a shared system icon and must never be disposed;
    // only an icon actually loaded from our embedded resource is ours to dispose.
    private static Icon LoadTrayIcon()
    {
        try
        {
            using Stream? stream = typeof(TrayAppContext).Assembly.GetManifestResourceStream("tray.ico");
            if (stream is null)
                return SystemIcons.Application;
            return new Icon(stream);
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    // Kept to what you can't do from the player window itself (it may be hidden): show it, settings, the background toggles, exit.
    // Station, cast, like, history and layouts all have their own controls in the window.
    private ContextMenuStrip BuildContextMenu()
    {
        ContextMenuStrip menu = ThemedMenu.Create();

        _updateItem = ThemedMenu.Add(menu, "Update available", (_, _) => OpenUpdatePage());
        _updateItem.Visible = false;

        ToolStripMenuItem showItem = ThemedMenu.Add(menu, "Show Player", (_, _) => ShowPlayer(), Glyphs.Music);
        showItem.Font = new Font(menu.Font, FontStyle.Bold);

        ThemedMenu.Add(menu, "Settings...", (_, _) => ShowSettings(), Glyphs.Settings);

        menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem startWithWindowsItem = Toggle(menu, "Start with Windows", StartupManager.IsEnabled(), on => StartupManager.SetEnabled(on));
        ToolStripMenuItem startMinimizedItem = Toggle(menu, "Start Minimized to Tray", _settings.StartMinimizedToTray, on => _settings.StartMinimizedToTray = on);
        ToolStripMenuItem minimizeToTrayItem = Toggle(menu, "Minimize to Tray on Close", _settings.MinimizeToTrayOnClose, on => _settings.MinimizeToTrayOnClose = on);
        ToolStripMenuItem notificationsItem = Toggle(menu, "Show Notification on Track Change", _settings.ShowTrackChangeNotifications, on => _settings.ShowTrackChangeNotifications = on);
        ToolStripMenuItem checkUpdatesItem = Toggle(menu, "Check for Updates", _settings.CheckForUpdates, on =>
        {
            _settings.CheckForUpdates = on;
            if (on)
                _ = _updateChecker.CheckAsync();
        });

        menu.Items.Add(new ToolStripSeparator());
        ThemedMenu.Add(menu, "Exit", (_, _) => ExitApplication(), Glyphs.Exit);

        // The checkmarks can change elsewhere (e.g. Start with Windows from outside) - refresh right before showing.
        menu.Opening += (_, _) =>
        {
            startWithWindowsItem.Checked = StartupManager.IsEnabled();
            startMinimizedItem.Checked = _settings.StartMinimizedToTray;
            minimizeToTrayItem.Checked = _settings.MinimizeToTrayOnClose;
            notificationsItem.Checked = _settings.ShowTrackChangeNotifications;
            checkUpdatesItem.Checked = _settings.CheckForUpdates;
        };

        return menu;
    }

    /// <summary>A tick-style setting: flips on click, applies the change and saves the settings.</summary>
    private ToolStripMenuItem Toggle(ContextMenuStrip menu, string text, bool isChecked, Action<bool> apply)
    {
        ToolStripMenuItem item = ThemedMenu.Add(menu, text, (_, _) => { }, null, isChecked);
        item.Click += (_, _) =>
        {
            item.Checked = !item.Checked;
            apply(item.Checked);
            SettingsStore.Save(_settings);
        };
        return item;
    }

    // UpdateChecker raises this on a thread-pool thread - marshal to the UI thread via the player window.
    private void OnUpdateAvailable(Version latest, string url)
    {
        _playerForm.BeginInvoke(new Action(() =>
        {
            _updateUrl = url;
            if (_updateItem is not null)
            {
                _updateItem.Text = $"Update available: v{latest.ToString(3)}...";
                _updateItem.Visible = true;
            }

            // One balloon per new version, not one per launch.
            if (_settings.LastNotifiedVersion != latest.ToString(3))
            {
                _settings.LastNotifiedVersion = latest.ToString(3);
                SettingsStore.Save(_settings);
                _trayIcon.BalloonTipClicked += (_, _) => OpenUpdatePage();
                _trayIcon.ShowBalloonTip(8000, $"{AppInfo.Name} update available", $"Version {latest.ToString(3)} is out - click to open the download page.", ToolTipIcon.Info);
            }
        }));
    }

    private void OpenUpdatePage()
    {
        if (_updateUrl is null)
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_updateUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Log($"Couldn't open the update page: {ex.Message}");
        }
    }

    private void ShowPlayer() => _playerForm.ShowAndActivate();

    private void ShowSettings()
    {
        using var settingsForm = new SettingsForm(_settings, _playerForm);
        settingsForm.ShowDialog();
    }

    private void ExitApplication()
    {
        _playerForm.PlaybackStateChanged -= OnPlaybackStateChanged;
        _playerForm.ExitRequested -= ExitApplication;
        _updateChecker.UpdateAvailable -= OnUpdateAvailable;
        _updateChecker.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        if (!ReferenceEquals(_idleTrayIcon, SystemIcons.Application))
        {
            _idleTrayIcon.Dispose();
        }
        _playingTrayIcon.Dispose();
        _playerForm.ForceClose();
        ExitThread();
    }
}
