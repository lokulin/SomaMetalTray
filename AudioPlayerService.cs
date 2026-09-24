using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace SomaMetalTray;

/// <summary>Mirrors DeathFmTray's PlaybackState; consumed by SmtcService/TrackChangeNotifier/etc.</summary>
public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,
    Buffering,
}

/// <summary>
/// Plays SomaFM's Metal Detector stream directly via Windows.Media.Playback.MediaPlayer
/// (WinRT - already available through the net10.0-windows10.0.19041.0 TFM, same as
/// SmtcService's Windows.Media.SystemMediaTransportControls, no extra package needed).
///
/// Replaces DeathFmTray's NowPlayingService+VolumeService (which drove a WebView2-hosted
/// &lt;audio&gt; element) entirely - there's no web page here, so this owns both playback
/// and volume persistence directly; there's exactly one place (this class) that reads or
/// writes AppSettings.Volume.
///
/// Also owns fetching/parsing the channel's .pls playlist to find a live ice*.somafm.com
/// stream URL, and rotates to the next listed URL (or re-fetches the .pls) on playback
/// failure - SomaFM's ice servers rotate and occasionally go down individually.
///
/// IMPORTANT: this constructs a *fresh* MediaPlayer instance on every Play() rather than
/// reusing one long-lived instance across stop/start cycles. Reusing a single instance
/// across a Source = null -> new Source cycle for a live network stream was found to
/// sometimes leave the MediaPlayer in a state where Play() silently no-ops (no exception,
/// no further PlaybackStateChanged/MediaFailed events) after a Stop() - reported
/// symptom: pausing playback via the Windows SMTC flyout, then being unable to resume.
/// See Logger/debug.log for the diagnostics added alongside this fix.
/// </summary>
public sealed class AudioPlayerService : IDisposable
{
    // SomaFM's per-channel .pls playlists live at api.somafm.com/<channel><bitrate>.pls -
    // "metal130" is the channel's 128kbps AAC stream, the same one somafm.com's own web
    // player defaults to.
    private const string PlsUrl = "https://api.somafm.com/metal130.pls";

    // Used only if the .pls fetch itself fails outright (e.g. no network at startup) -
    // SomaFM's ice server hostnames follow a well-known ice<N>.somafm.com/<channel>-<bitrate>-<format>
    // pattern; these are a last-resort fallback so playback can still be attempted while
    // AudioPlayerService keeps retrying a fresh .pls fetch in the background.
    private static readonly string[] FallbackStreamUrls =
    {
        "https://ice1.somafm.com/metal-128-mp3",
        "https://ice2.somafm.com/metal-128-mp3",
        "https://ice4.somafm.com/metal-128-mp3",
        "https://ice5.somafm.com/metal-128-mp3",
    };

    private readonly HttpClient _http;
    private readonly AppSettings _settings;

    // The live MediaPlayer for the current play session - null whenever
    // nothing is playing/starting. Recreated from scratch on every Play(),
    // never reused across a Stop() -> Play() cycle (see class remarks above).
    private MediaPlayer? _player;
    private double _volume;

    private List<string> _streamUrls = new();
    private int _streamUrlIndex;
    private bool _userWantsPlaying;
    private bool _disposed;

    /// <summary>
    /// Raised whenever playback state changes. May be raised on a background
    /// (MTA) thread, same caveat as SmtcService.ButtonPressed - callers that
    /// touch UI must marshal back to the UI thread themselves.
    /// </summary>
    public event Action<PlaybackState>? PlaybackStateChanged;

    public AudioPlayerService(AppSettings settings)
    {
        _settings = settings;
        _volume = Math.Clamp(_settings.Volume ?? 0.8, 0.0, 1.0);

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SomaMetalTray", "0.1"));
    }

    /// <summary>Volume as 0.0-1.0. Setting it both applies it live (if a MediaPlayer is currently live) and persists it (this is the one place that owns volume persistence).</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            _volume = clamped;
            if (_player is not null)
                _player.Volume = clamped;
            _settings.Volume = clamped;
            SettingsStore.Save(_settings);
        }
    }

    /// <summary>Fetches and parses the channel's .pls playlist. Call once at startup, before the first Play().</summary>
    public async Task InitializeAsync()
    {
        _streamUrls = await FetchPlsUrlsAsync();
        if (_streamUrls.Count == 0)
            _streamUrls = new List<string>(FallbackStreamUrls);

        _streamUrlIndex = 0;
    }

    public void Play()
    {
        _userWantsPlaying = true;

        if (_streamUrls.Count == 0)
        {
            // Not initialized yet, or InitializeAsync failed to produce anything
            // usable - fall back rather than doing nothing.
            _streamUrls = new List<string>(FallbackStreamUrls);
        }

        string url = _streamUrls[_streamUrlIndex];
        Logger.Log($"AudioPlayerService.Play() - streamUrl={url}, userWantsPlaying={_userWantsPlaying}");

        // Tear down whatever the previous session's MediaPlayer was (if any)
        // before building a fresh one - a call to Play() while already
        // playing (or mid-retry) should always start clean rather than layer
        // a second MediaPlayer on top of the old one.
        DisposeCurrentPlayer();

        try
        {
            var player = new MediaPlayer
            {
                AutoPlay = false,
                Volume = _volume,
            };
            player.MediaFailed += OnMediaFailed;
            player.PlaybackSession.PlaybackStateChanged += OnPlaybackSessionStateChanged;
            _player = player;

            var uri = new Uri(url);
            player.Source = MediaSource.CreateFromUri(uri);
            player.Play();
        }
        catch (Exception ex)
        {
            // Bad URL or MediaSource creation failure - treat like a playback
            // failure and roll to the next candidate stream URL.
            Logger.Log($"AudioPlayerService.Play() - failed to start ({ex.Message}), advancing to next stream URL");
            _ = AdvanceAndRetryAsync();
        }
    }

    public void Stop()
    {
        Logger.Log($"AudioPlayerService.Stop() - userWantsPlaying was {_userWantsPlaying}");
        _userWantsPlaying = false;
        DisposeCurrentPlayer();
        PlaybackStateChanged?.Invoke(PlaybackState.Stopped);
    }

    // Unsubscribes event handlers before disposing, so a straggling event from
    // an already-torn-down MediaPlayer can never fire into this service after
    // it's no longer the "current" one.
    private void DisposeCurrentPlayer()
    {
        MediaPlayer? old = _player;
        _player = null;
        if (old is null)
            return;

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

    private void OnPlaybackSessionStateChanged(MediaPlaybackSession sender, object args)
    {
        MediaPlaybackState nativeState = sender.PlaybackState;
        PlaybackState mapped = nativeState switch
        {
            MediaPlaybackState.Playing => PlaybackState.Playing,
            MediaPlaybackState.Paused => _userWantsPlaying ? PlaybackState.Buffering : PlaybackState.Stopped,
            MediaPlaybackState.Buffering => PlaybackState.Buffering,
            MediaPlaybackState.Opening => PlaybackState.Buffering,
            _ => PlaybackState.Stopped,
        };

        Logger.Log($"MediaPlayer.PlaybackStateChanged - native={nativeState}, mapped={mapped}, userWantsPlaying={_userWantsPlaying}");

        PlaybackStateChanged?.Invoke(mapped);
    }

    // Ice servers rotate/occasionally go down - on a failure, try the next
    // URL from the .pls we already have; once we've exhausted the list,
    // re-fetch the .pls entirely (the rotation may have changed) and try
    // again from the top. Only acts if the user still wants to be playing -
    // a failure after a deliberate Stop() shouldn't restart playback.
    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        Logger.Log($"MediaPlayer.MediaFailed - error={args.Error}, message={args.ErrorMessage}, userWantsPlaying={_userWantsPlaying}");

        if (!_userWantsPlaying)
            return;

        _ = AdvanceAndRetryAsync();
    }

    private async Task AdvanceAndRetryAsync()
    {
        if (_disposed || !_userWantsPlaying)
            return;

        _streamUrlIndex++;
        if (_streamUrlIndex >= _streamUrls.Count)
        {
            // Exhausted the current list - re-fetch the .pls in case the ice
            // server rotation changed, then start over from the top.
            List<string> refreshed = await FetchPlsUrlsAsync();
            if (refreshed.Count > 0)
                _streamUrls = refreshed;
            _streamUrlIndex = 0;
        }

        if (_disposed || !_userWantsPlaying)
            return;

        // Brief backoff so a fully-down ice cluster doesn't spin retries in a tight loop.
        await Task.Delay(2000);

        if (_disposed || !_userWantsPlaying)
            return;

        Play();
    }

    private async Task<List<string>> FetchPlsUrlsAsync()
    {
        var urls = new List<string>();
        try
        {
            string text = await _http.GetStringAsync(PlsUrl);
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim().TrimEnd('\r');

                // .pls entries look like "File1=http://...", "File2=http://...".
                // Order matters (File1 is the playlist's preferred/primary entry).
                if (line.StartsWith("File", StringComparison.OrdinalIgnoreCase))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && eq < line.Length - 1)
                    {
                        string url = line[(eq + 1)..].Trim();
                        if (Uri.TryCreate(url, UriKind.Absolute, out _))
                            urls.Add(url);
                    }
                }
            }
        }
        catch
        {
            // Best-effort - caller falls back to FallbackStreamUrls when this comes back empty.
        }

        return urls;
    }

    public void Dispose()
    {
        _disposed = true;
        DisposeCurrentPlayer();
        _http.Dispose();
    }
}
