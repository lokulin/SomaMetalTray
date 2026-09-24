using System;
using System.Security;
using System.Windows.Forms;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace SomaMetalTray;

/// <summary>
/// Shows a Windows toast notification whenever the currently playing track
/// changes, fed by the same events <see cref="SmtcService"/> and
/// <see cref="LastFmScrobbler"/> already use. Ported from DeathFmTray
/// unchanged aside from branding/paths.
///
/// Deliberately gated on actually playing, not just "metadata changed":
/// SomaFmService polls the channel's song history regardless of whether our
/// own AudioPlayerService is actually connected to the stream, so without
/// this guard a track change while stopped/idle would still pop a
/// notification for something nobody's actually listening to.
/// </summary>
public sealed class TrackChangeNotifier
{
    // Must stay in sync with Program.AppUserModelId - same convention
    // AumidShortcutHelper already follows. Toasts from an unpackaged app are
    // silently dropped without a valid AUMID (already set up for SMTC/the
    // Start Menu shortcut, so nothing extra is needed beyond reusing it here).
    private const string AppUserModelId = "TerraEclectic.SomaMetalTray.v1";

    private readonly AppSettings _settings;
    private bool _isPlaying;

    public TrackChangeNotifier(AppSettings settings)
    {
        _settings = settings;
    }

    public void OnPlaybackStateChanged(PlaybackState state)
    {
        _isPlaying = state == PlaybackState.Playing;
    }

    /// <summary>
    /// <paramref name="localArtPath"/> should be a file ArtworkService has
    /// already resolved/cached to disk (SomaFM's own feed never supplies
    /// art - see TrackMetadata.ArtUrl) - passing it directly here avoids a
    /// second network round-trip to whichever remote source it originally
    /// came from.
    /// </summary>
    public void OnMetadataChanged(TrackMetadata metadata, string? localArtPath)
    {
        if (!_isPlaying || !_settings.ShowTrackChangeNotifications)
        {
            return;
        }

        ShowInternal(metadata, localArtPath, reportErrors: false);
    }

    /// <summary>
    /// Fires a toast immediately, ignoring both the playing-state and the
    /// enabled-setting gates - used to verify the setting without waiting
    /// for a real track change (or even needing to be playing at all).
    /// </summary>
    public void ShowTest(TrackMetadata metadata, string? localArtPath = null)
    {
        ShowInternal(metadata, localArtPath, reportErrors: true);
    }

    private static void ShowInternal(TrackMetadata metadata, string? localArtPath, bool reportErrors)
    {
        try
        {
            Show(metadata, localArtPath);
        }
        catch (Exception ex)
        {
            Logger.Log($"TrackChangeNotifier.Show failed - localArtPath='{localArtPath ?? "(none)"}': {ex}");
            if (reportErrors)
            {
                MessageBox.Show(
                    $"Couldn't show a test notification:\n\n{ex.Message}",
                    "SomaFM Metal Detector Player",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            // Otherwise best-effort - a notification failing to show shouldn't affect playback.
        }
    }

    private static void Show(TrackMetadata metadata, string? localArtPath)
    {
        // hint-crop="circle" + appLogoOverride matches how most media-player
        // toasts present album art. localArtPath is ArtworkService's own
        // disk-cache file for this track (or the station logo) - toast image
        // sources need to be either a remote https URL or a local file URI,
        // and reusing the file already on disk avoids a redundant download.
        string imageNode = localArtPath is null
            ? ""
            : $"<image placement=\"appLogoOverride\" hint-crop=\"circle\" src=\"{SecurityElement.Escape(new Uri(localArtPath).AbsoluteUri)}\"/>";

        Logger.Log($"TrackChangeNotifier.Show - localArtPath='{localArtPath ?? "(none)"}', imageNode='{imageNode}'");

        string xml = $"""
            <toast>
                <visual>
                    <binding template="ToastGeneric">
                        <text>{SecurityElement.Escape(metadata.Title)}</text>
                        <text>{SecurityElement.Escape(metadata.Artist)}</text>
                        {imageNode}
                    </binding>
                </visual>
            </toast>
            """;

        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var toast = new ToastNotification(doc);
        ToastNotificationManager.CreateToastNotifier(AppUserModelId).Show(toast);
    }
}
