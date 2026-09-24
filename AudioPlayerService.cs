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
    private readonly MediaPlayer _player = new();

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

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SomaMetalTray", "0.1"));

        _player.AutoPlay = false;
        _player.Volume = Math.Clamp(_settings.Volume ?? 0.8, 0.0, 1.0);
        _player.MediaFailed += OnMediaFailed;
        _player.PlaybackSession.PlaybackStateChanged += OnPlaybackSessionStateChanged;
    }

    /// <summary>Volume as 0.0-1.0. Setting it both applies it live and persists it (this is the one place that owns volume persistence).</summary>
    public double Volume
    {
        get => _player.Volume;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
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

        try
        {
            var uri = new Uri(_streamUrls[_streamUrlIndex]);
            _player.Source = MediaSource.CreateFromUri(uri);
            _player.Play();
        }
        catch
        {
            // Bad URL or MediaSource creation failure - treat like a playback
            // failure and roll to the next candidate stream URL.
            _ = AdvanceAndRetryAsync();
        }
    }

    public void Stop()
    {
        _userWantsPlaying = false;
        try
        {
            _player.Pause();
            _player.Source = null; // releases the live connection instead of leaving it buffering in the background
        }
        catch
        {
            // Best-effort - Stop should never throw into a UI event handler.
        }
        PlaybackStateChanged?.Invoke(PlaybackState.Stopped);
    }

    private void OnPlaybackSessionStateChanged(MediaPlaybackSession sender, object args)
    {
        PlaybackState state = sender.PlaybackState switch
        {
            MediaPlaybackState.Playing => PlaybackState.Playing,
            MediaPlaybackState.Paused => _userWantsPlaying ? PlaybackState.Buffering : PlaybackState.Stopped,
            MediaPlaybackState.Buffering => PlaybackState.Buffering,
            MediaPlaybackState.Opening => PlaybackState.Buffering,
            _ => PlaybackState.Stopped,
        };

        PlaybackStateChanged?.Invoke(state);
    }

    // Ice servers rotate/occasionally go down - on a failure, try the next
    // URL from the .pls we already have; once we've exhausted the list,
    // re-fetch the .pls entirely (the rotation may have changed) and try
    // again from the top. Only acts if the user still wants to be playing -
    // a failure after a deliberate Stop() shouldn't restart playback.
    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
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
        _player.MediaFailed -= OnMediaFailed;
        _player.PlaybackSession.PlaybackStateChanged -= OnPlaybackSessionStateChanged;
        _player.Dispose();
        _http.Dispose();
    }
}
