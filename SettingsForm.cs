using System;
using System.Drawing;
using System.Windows.Forms;

namespace SomaMetalTray;

/// <summary>
/// Editor for the settings that would otherwise require hand-editing
/// %AppData%\SomaMetalTray\settings.json. Last.fm's API key/secret and the
/// Discord Client ID/default image key/fanart.tv API key are this app's own
/// compiled-in identifiers (see AppCredentials) rather than something each
/// user has to register/paste in, so the only thing left here is connecting
/// your own Last.fm account. Everything else (start with Windows, minimize
/// to tray, etc.) already has its own tray/system menu checkbox and doesn't
/// need a dialog.
///
/// SomaMetalTray only ever plays one channel (SomaFM's Metal Detector), so
/// unlike DeathFmTray's settings this has no station-switching UI.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly PlayerForm _playerForm;

    private readonly Label _lastFmStatusLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _lastFmConnectButton = new() { Width = 100, Anchor = AnchorStyles.Left };

    public SettingsForm(AppSettings settings, PlayerForm playerForm)
    {
        _settings = settings;
        _playerForm = playerForm;

        Text = "Metal Detector Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 160);
        Padding = new Padding(16);

        HandleCreated += (_, _) => WindowChromeHelper.ApplyDarkTitleBar(
            this,
            captionColor: Color.FromArgb(0x22, 0x00, 0x00),
            textColor: Color.White);

        BuildLayout();

        UpdateLastFmStatus();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < layout.RowCount; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        int row = 0;

        layout.Controls.Add(SectionHeader("Last.fm scrobbling"), 0, row);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, row)!, 2);
        row++;

        _lastFmConnectButton.Click += OnLastFmConnectClicked;
        layout.Controls.Add(_lastFmConnectButton, 1, row);
        row++;

        layout.Controls.Add(_lastFmStatusLabel, 1, row);

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
            await _playerForm.ConnectLastFmAsync();
        }

        UpdateLastFmStatus();
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        SettingsStore.Save(_settings);

        _playerForm.RestartDiscordPresence();

        DialogResult = DialogResult.OK;
        Close();
    }
}
