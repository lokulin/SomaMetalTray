using System;
using System.Drawing;
using System.Windows.Forms;

namespace SomaMetalTray;

/// <summary>
/// Editor for the settings that would otherwise require hand-editing
/// %AppData%\SomaMetalTray\settings.json: Last.fm/Discord/fanart.tv API
/// credentials. Everything else (start with Windows, minimize to tray, etc.)
/// already has its own tray/system menu checkbox and doesn't need a dialog.
///
/// SomaMetalTray only ever plays one channel (SomaFM's Metal Detector), so
/// unlike DeathFmTray's settings this has no station-switching UI.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly PlayerForm _playerForm;

    private readonly TextBox _lastFmApiKeyBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _lastFmApiSecretBox = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly Label _lastFmStatusLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _lastFmConnectButton = new() { Width = 100, Anchor = AnchorStyles.Left };

    private readonly TextBox _discordClientIdBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _discordImageKeyBox = new() { Dock = DockStyle.Fill };

    private readonly TextBox _fanArtTvApiKeyBox = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };

    public SettingsForm(AppSettings settings, PlayerForm playerForm)
    {
        _settings = settings;
        _playerForm = playerForm;

        Text = "Metal Detector Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 370);
        Padding = new Padding(16);

        HandleCreated += (_, _) => WindowChromeHelper.ApplyDarkTitleBar(
            this,
            captionColor: Color.FromArgb(0x22, 0x00, 0x00),
            textColor: Color.White);

        BuildLayout();

        _lastFmApiKeyBox.Text = _settings.LastFmApiKey ?? "";
        _lastFmApiSecretBox.Text = _settings.LastFmApiSecret ?? "";
        _discordClientIdBox.Text = _settings.DiscordClientId ?? "";
        _discordImageKeyBox.Text = _settings.DiscordDefaultImageKey ?? "";
        _fanArtTvApiKeyBox.Text = _settings.FanArtTvApiKey ?? "";

        UpdateLastFmStatus();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 12,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < layout.RowCount; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        int row = 0;

        layout.Controls.Add(SectionHeader("Last.fm scrobbling"), 0, row);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, row)!, 2);
        row++;

        layout.Controls.Add(new Label { Text = "API key:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(_lastFmApiKeyBox, 1, row);
        row++;

        layout.Controls.Add(new Label { Text = "Shared secret:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(_lastFmApiSecretBox, 1, row);
        row++;

        _lastFmConnectButton.Click += OnLastFmConnectClicked;
        layout.Controls.Add(_lastFmConnectButton, 1, row);
        row++;

        layout.Controls.Add(_lastFmStatusLabel, 1, row);
        row++;

        layout.Controls.Add(new Panel { Height = 10 }, 0, row);
        row++;

        layout.Controls.Add(SectionHeader("Discord Rich Presence"), 0, row);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, row)!, 2);
        row++;

        layout.Controls.Add(new Label { Text = "Client ID:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(_discordClientIdBox, 1, row);
        row++;

        layout.Controls.Add(new Label { Text = "Default image key:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(_discordImageKeyBox, 1, row);
        row++;

        layout.Controls.Add(new Panel { Height = 10 }, 0, row);
        row++;

        layout.Controls.Add(SectionHeader("Album art (fanart.tv)"), 0, row);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, row)!, 2);
        row++;

        layout.Controls.Add(new Label { Text = "API key:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(_fanArtTvApiKeyBox, 1, row);
        row++;

        var fanArtHint = new Label
        {
            Text = "Optional - get a free personal key at fanart.tv/get-an-api-key.\nWithout one, artwork lookup falls back to Deezer, then iTunes.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Font = new Font(Control.DefaultFont.FontFamily, 8f),
        };
        layout.Controls.Add(fanArtHint, 1, row);

        Controls.Add(layout);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 12, 0, 0),
        };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        var saveButton = new Button { Text = "Save", Width = 90 };
        saveButton.Click += OnSaveClicked;
        buttonPanel.Controls.Add(cancelButton);
        buttonPanel.Controls.Add(saveButton);
        Controls.Add(buttonPanel);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    private static Label SectionHeader(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(Control.DefaultFont, FontStyle.Bold),
        Margin = new Padding(0, 0, 0, 6),
    };

    private void UpdateLastFmStatus()
    {
        if (_playerForm.IsLastFmAuthorized)
        {
            _lastFmStatusLabel.Text = $"Connected as {_playerForm.LastFmUsername}";
            _lastFmConnectButton.Text = "Disconnect";
        }
        else
        {
            _lastFmStatusLabel.Text = "Not connected";
            _lastFmConnectButton.Text = "Connect...";
        }
    }

    private async void OnLastFmConnectClicked(object? sender, EventArgs e)
    {
        if (_playerForm.IsLastFmAuthorized)
        {
            _playerForm.DisconnectLastFm();
        }
        else
        {
            // Commit the key/secret immediately so Connect works right after
            // typing them in, without needing a separate Save step first.
            _settings.LastFmApiKey = _lastFmApiKeyBox.Text.Trim();
            _settings.LastFmApiSecret = _lastFmApiSecretBox.Text.Trim();
            await _playerForm.ConnectLastFmAsync();
        }

        UpdateLastFmStatus();
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        _settings.LastFmApiKey = _lastFmApiKeyBox.Text.Trim();
        _settings.LastFmApiSecret = _lastFmApiSecretBox.Text.Trim();
        _settings.DiscordClientId = _discordClientIdBox.Text.Trim();
        _settings.DiscordDefaultImageKey = _discordImageKeyBox.Text.Trim();
        _settings.FanArtTvApiKey = _fanArtTvApiKeyBox.Text.Trim();
        SettingsStore.Save(_settings);

        _playerForm.RestartDiscordPresence();

        DialogResult = DialogResult.OK;
        Close();
    }
}
