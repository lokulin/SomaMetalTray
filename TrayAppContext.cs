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
            Text = "SomaFM Metal Detector Player",
            ContextMenuStrip = BuildContextMenu(),
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowPlayer();

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

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(new ToolStripMenuItem("Show Player", null, (_, _) => ShowPlayer())
        {
            Font = new Font(menu.Font, FontStyle.Bold)
        });

        menu.Items.Add(new ToolStripMenuItem("Settings...", null, (_, _) => ShowSettings()));

        menu.Items.Add(new ToolStripSeparator());

        var startWithWindowsItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupManager.IsEnabled(),
            CheckOnClick = true
        };
        startWithWindowsItem.Click += (_, _) =>
        {
            StartupManager.SetEnabled(startWithWindowsItem.Checked);
        };
        menu.Items.Add(startWithWindowsItem);

        var startMinimizedItem = new ToolStripMenuItem("Start Minimized to Tray")
        {
            Checked = _settings.StartMinimizedToTray,
            CheckOnClick = true
        };
        startMinimizedItem.Click += (_, _) =>
        {
            _settings.StartMinimizedToTray = startMinimizedItem.Checked;
            SettingsStore.Save(_settings);
        };
        menu.Items.Add(startMinimizedItem);

        var minimizeToTrayItem = new ToolStripMenuItem("Minimize to Tray on Close")
        {
            Checked = _settings.MinimizeToTrayOnClose,
            CheckOnClick = true
        };
        minimizeToTrayItem.Click += (_, _) =>
        {
            _settings.MinimizeToTrayOnClose = minimizeToTrayItem.Checked;
            SettingsStore.Save(_settings);
        };
        menu.Items.Add(minimizeToTrayItem);

        var trackChangeNotificationsItem = new ToolStripMenuItem("Show Notification on Track Change")
        {
            Checked = _settings.ShowTrackChangeNotifications,
            CheckOnClick = true
        };
        trackChangeNotificationsItem.Click += (_, _) =>
        {
            _settings.ShowTrackChangeNotifications = trackChangeNotificationsItem.Checked;
            SettingsStore.Save(_settings);
        };
        menu.Items.Add(trackChangeNotificationsItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));

        // The titlebar's system menu (see PlayerForm.BuildSystemMenu) offers
        // the same toggles - refresh these right before showing rather than
        // trying to keep the two menus in sync via events.
        menu.Opening += (_, _) =>
        {
            startWithWindowsItem.Checked = StartupManager.IsEnabled();
            startMinimizedItem.Checked = _settings.StartMinimizedToTray;
            minimizeToTrayItem.Checked = _settings.MinimizeToTrayOnClose;
            trackChangeNotificationsItem.Checked = _settings.ShowTrackChangeNotifications;
        };

        return menu;
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
