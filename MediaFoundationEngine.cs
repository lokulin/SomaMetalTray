using System;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace SomaMetalTray;

/// <summary>
/// Windows.Media.Playback.MediaPlayer (Media Foundation). Already available through the net10.0-windows10.0.19041.0 TFM, no
/// extra package. Its weakness is the start-up pauses against Death.FM described in DEVELOPING.md.
///
/// IMPORTANT: this constructs a *fresh* MediaPlayer on every Start() rather than reusing one across stop/start cycles.
/// Reusing a single instance across a Source = null -> new Source cycle for a live network stream was found to
/// sometimes leave the MediaPlayer in a state where Play() silently no-ops (no exception, no further events) after a
/// Stop() - reported symptom: pausing via the Windows SMTC flyout, then being unable to resume.
/// </summary>
internal sealed class MediaFoundationEngine : IAudioEngine
{
    private MediaPlayer? _player;

    public string Name => "MediaFoundation";
    public event Action<PlaybackState>? StateChanged;
    public event Action? Failed;

    public void Start(string url, double volume)
    {
        Stop();

        var player = new MediaPlayer
        {
            AutoPlay = false,
            Volume = volume,
            // Low-latency live buffering. Without it Media Foundation insists on a multi-second buffer and, against Death.FM (a ~4s
            // burst, then exactly real time), pauses to refill ~3.3s at a time 2-3 times after starting; with it that drops to
            // about one pause. See DEVELOPING.md ("Death.FM start-up pauses").
            RealTimePlayback = true,
        };
        // MediaPlayer auto-registers its own System Media Transport Controls session by default (separate from the one
        // SmtcService manages via GetForWindow) - left enabled, Windows' media flyout shows a second, unbranded entry
        // alongside our real one. We only ever want the one SmtcService drives.
        player.CommandManager.IsEnabled = false;
        player.MediaFailed += OnMediaFailed;
        player.PlaybackSession.PlaybackStateChanged += OnPlaybackSessionStateChanged;
        _player = player;

        player.Source = MediaSource.CreateFromUri(new Uri(url));
        player.Play();
    }

    public void Stop()
    {
        MediaPlayer? old = _player;
        _player = null;
        if (old is null)
            return;

        // Unsubscribe first, so a straggling event from an already-torn-down player can never fire into the service.
        try
        {
            old.MediaFailed -= OnMediaFailed;
            old.PlaybackSession.PlaybackStateChanged -= OnPlaybackSessionStateChanged;
            old.Pause();
            old.Source = null; // releases the live connection instead of leaving it buffering in the background
            old.Dispose();
        }
        catch
        {
            // Best-effort - tearing down the old player should never throw into a UI event handler.
        }
    }

    public void SetVolume(double volume)
    {
        if (_player is not null)
            _player.Volume = volume;
    }

    private void OnPlaybackSessionStateChanged(MediaPlaybackSession sender, object args)
    {
        MediaPlaybackState native = sender.PlaybackState;
        Logger.Log($"MediaPlayer.PlaybackStateChanged - native={native}");
        StateChanged?.Invoke(native switch
        {
            MediaPlaybackState.Playing => PlaybackState.Playing,
            MediaPlaybackState.Paused => PlaybackState.Paused,
            MediaPlaybackState.Buffering or MediaPlaybackState.Opening => PlaybackState.Buffering,
            _ => PlaybackState.Stopped,
        });
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        Logger.Log($"MediaPlayer.MediaFailed - error={args.Error}, message={args.ErrorMessage}");
        Failed?.Invoke();
    }

    public void Dispose() => Stop();
}
