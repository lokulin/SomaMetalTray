using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Timer = System.Threading.Timer;
using Windows.Media.Core;
using Windows.Media.Playback;
using LibVLCSharp.Shared;
using MediaPlayer = Windows.Media.Playback.MediaPlayer;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

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
/// Plays the selected station's stream directly via Windows.Media.Playback.MediaPlayer
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
    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private IStation _station;

    // The live MediaPlayer for the current play session - null whenever
    // nothing is playing/starting. Recreated from scratch on every Play(),
    // never reused across a Stop() -> Play() cycle (see class remarks above).
    private MediaPlayer? _player;
    private double _volume;

    // SPIKE: LibVLC engine, selected with BLASTBEAT_ENGINE=vlc (see DEVELOPING.md "Death.FM start-up pauses"). Unlike Media
    // Foundation it has a configurable start threshold (--network-caching), which is the whole point.
    private static readonly bool UseVlc = string.Equals(Environment.GetEnvironmentVariable("BLASTBEAT_ENGINE"), "vlc", StringComparison.OrdinalIgnoreCase);
    private static int VlcCachingMs => int.TryParse(Environment.GetEnvironmentVariable("BLASTBEAT_VLC_CACHING_MS"), out int ms) ? ms : 1000;
    private static LibVLC? _vlc;
    private VlcMediaPlayer? _vlcPlayer;

    private List<string> _streamUrls = new();
    private int _streamUrlIndex;
    private bool _userWantsPlaying;
    private bool _disposed;

    // Self-healing (see CheckForStall / OnPowerModeChanged / OnNetworkAvailabilityChanged): a live stream that
    // loses its connection doesn't always raise MediaFailed - it can sit in "buffering" forever, and a PC coming
    // back from sleep has a dead socket. These bring playback back without the user touching anything.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(25);
    private readonly Timer _watchdog;
    private PlaybackState _lastState = PlaybackState.Stopped;
    private long _lastStateChangeTicks = Stopwatch.GetTimestamp();
    private int _recovering; // 1 while a recovery attempt is already in flight

    /// <summary>
    /// Raised whenever playback state changes. May be raised on a background
    /// (MTA) thread, same caveat as SmtcService.ButtonPressed - callers that
    /// touch UI must marshal back to the UI thread themselves.
    /// </summary>
    public event Action<PlaybackState>? PlaybackStateChanged;

    public AudioPlayerService(AppSettings settings, IStation station)
    {
        _settings = settings;
        _station = station;
        _volume = Math.Clamp(_settings.Volume ?? 0.8, 0.0, 1.0);

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.UserAgentProduct, UpdateChecker.CurrentVersion().ToString(3)));

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _watchdog = new Timer(_ => CheckForStall(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
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
            if (_vlcPlayer is not null)
                _vlcPlayer.Volume = (int)Math.Round(clamped * 100);
            _settings.Volume = clamped;
            SettingsStore.Save(_settings);
        }
    }

    public IStation Station => _station;

    /// <summary>Stops playback and points the player at another station. Call <see cref="InitializeAsync"/> afterwards.</summary>
    public void SetStation(IStation station)
    {
        Stop();
        _station = station;
        _streamUrls = new List<string>();
        _streamUrlIndex = 0;
    }

    /// <summary>Resolves the current station's stream URLs. Call at startup and after <see cref="SetStation"/>, before Play().</summary>
    public async Task InitializeAsync()
    {
        _streamUrls = await _station.ResolveStreamUrlsAsync(_http);
        if (_streamUrls.Count == 0)
            _streamUrls = new List<string>(_station.FallbackStreamUrls);

        _streamUrlIndex = 0;
    }

    public void Play()
    {
        _userWantsPlaying = true;

        if (_streamUrls.Count == 0)
        {
            // Not initialized yet, or InitializeAsync failed to produce anything
            // usable - fall back rather than doing nothing.
            _streamUrls = new List<string>(_station.FallbackStreamUrls);
        }

        string url = _streamUrls[_streamUrlIndex];
        Logger.Log($"AudioPlayerService.Play() - streamUrl={url}, userWantsPlaying={_userWantsPlaying}");

        // Tear down whatever the previous session's MediaPlayer was (if any)
        // before building a fresh one - a call to Play() while already
        // playing (or mid-retry) should always start clean rather than layer
        // a second MediaPlayer on top of the old one.
        DisposeCurrentPlayer();

        if (UseVlc)
        {
            try
            {
                PlayVlc(url);
            }
            catch (Exception ex)
            {
                Logger.Log($"AudioPlayerService.Play() - LibVLC failed to start ({ex.Message}), advancing to next stream URL");
                _ = AdvanceAndRetryAsync();
            }
            return;
        }

        try
        {
            var player = new MediaPlayer
            {
                AutoPlay = false,
                Volume = _volume,
                // Low-latency live buffering. Without it Media Foundation insists on a multi-second buffer and, against Death.FM (a ~4s
                // burst, then exactly real time), pauses to refill ~3.3s at a time 2-3 times after starting; with it that drops to
                // about one pause. See DEVELOPING.md ("Death.FM start-up pauses").
                RealTimePlayback = true,
            };
            // MediaPlayer auto-registers its own System Media Transport
            // Controls session by default (separate from the one SmtcService
            // manages via GetForWindow) - left enabled, Windows' media flyout
            // shows a second, unbranded entry (just the app's AUMID as its
            // title, no metadata/art) alongside our real one. We only ever
            // want the one SmtcService drives.
            player.CommandManager.IsEnabled = false;
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

    private void PlayVlc(string url)
    {
        if (_vlc is null)
        {
            Core.Initialize(); // finds libvlc under <exe dir>\libvlc\win-x64 (from the VideoLAN.LibVLC.Windows package)
            _vlc = new LibVLC("--no-video", "--no-osd", "--no-snapshot-preview", $"--network-caching={VlcCachingMs}");
        }

        var player = new VlcMediaPlayer(_vlc) { Volume = (int)Math.Round(_volume * 100) };
        player.Opening += OnVlcOpening;
        player.Buffering += OnVlcBuffering;
        player.Playing += OnVlcPlaying;
        player.Paused += OnVlcPaused;
        player.EncounteredError += OnVlcError;
        _vlcPlayer = player;

        using var media = new Media(_vlc, new Uri(url));
        player.Play(media);
    }

    // LibVLC raises these on its own threads, and disposing a player from inside one of its callbacks deadlocks - so
    // anything that tears down or restarts goes through Task.Run.
    private void OnVlcOpening(object? s, EventArgs e) => VlcState(s, PlaybackState.Buffering, "opening");
    private void OnVlcPlaying(object? s, EventArgs e) => VlcState(s, PlaybackState.Playing, "playing");
    private void OnVlcPaused(object? s, EventArgs e) => VlcState(s, _userWantsPlaying ? PlaybackState.Buffering : PlaybackState.Stopped, "paused");

    private void OnVlcBuffering(object? s, MediaPlayerBufferingEventArgs e)
    {
        // Cache is 0-100 and fires repeatedly; below 100 the output is starved, 100 means it has resumed.
        if (e.Cache < 100f)
            VlcState(s, PlaybackState.Buffering, $"buffering {e.Cache:0}%");
        else
            VlcState(s, PlaybackState.Playing, "buffered");
    }

    private void OnVlcError(object? s, EventArgs e)
    {
        if (!ReferenceEquals(s, _vlcPlayer))
            return;
        Logger.Log($"LibVLC.EncounteredError - userWantsPlaying={_userWantsPlaying}");
        if (_userWantsPlaying)
            _ = Task.Run(AdvanceAndRetryAsync);
    }

    private void VlcState(object? sender, PlaybackState mapped, string detail)
    {
        if (!ReferenceEquals(sender, _vlcPlayer))
            return; // a straggler from a player we've already replaced
        // Buffering events spam while the cache fills - only log/raise on an actual change.
        if (mapped == _lastState)
            return;
        Logger.Log($"LibVLC state - {detail}, mapped={mapped}, userWantsPlaying={_userWantsPlaying}");
        MarkState(mapped);
        PlaybackStateChanged?.Invoke(mapped);
    }

    public void Stop()
    {
        Logger.Log($"AudioPlayerService.Stop() - userWantsPlaying was {_userWantsPlaying}");
        _userWantsPlaying = false;
        DisposeCurrentPlayer();
        MarkState(PlaybackState.Stopped);
        PlaybackStateChanged?.Invoke(PlaybackState.Stopped);
    }

    // Unsubscribes event handlers before disposing, so a straggling event from
    // an already-torn-down MediaPlayer can never fire into this service after
    // it's no longer the "current" one.
    private void DisposeCurrentPlayer()
    {
        VlcMediaPlayer? oldVlc = _vlcPlayer;
        _vlcPlayer = null;
        if (oldVlc is not null)
        {
            oldVlc.Opening -= OnVlcOpening;
            oldVlc.Buffering -= OnVlcBuffering;
            oldVlc.Playing -= OnVlcPlaying;
            oldVlc.Paused -= OnVlcPaused;
            oldVlc.EncounteredError -= OnVlcError;
            // Stop() can block on the network, so keep it off the UI thread.
            _ = Task.Run(() => { try { oldVlc.Stop(); oldVlc.Dispose(); } catch { } });
        }

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

        MarkState(mapped);

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
            List<string> refreshed = await _station.ResolveStreamUrlsAsync(_http);
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

    private void MarkState(PlaybackState state)
    {
        _lastState = state;
        Interlocked.Exchange(ref _lastStateChangeTicks, Stopwatch.GetTimestamp());
    }

    // Stuck "buffering" for too long while the user wants to be playing: the connection is dead. Move on to the
    // next stream URL (re-fetching the list if needed), the same recovery a MediaFailed gets.
    private void CheckForStall()
    {
        if (_disposed || !_userWantsPlaying || _lastState != PlaybackState.Buffering)
            return;

        TimeSpan stuckFor = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastStateChangeTicks));
        if (stuckFor < StallTimeout)
            return;

        Logger.Log($"AudioPlayerService - stalled buffering for {stuckFor.TotalSeconds:0}s, reconnecting");
        MarkState(PlaybackState.Buffering); // restart the clock so one stall triggers one recovery
        _ = AdvanceAndRetryAsync();
    }

    // After sleep the old connection is dead but the player may still think it's playing. Give the network a
    // few seconds to come back, then start a fresh session (the retry logic copes if it's still not up).
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
            return;

        Logger.Log($"AudioPlayerService - resumed from sleep, userWantsPlaying={_userWantsPlaying}");
        _ = RecoverAsync(TimeSpan.FromSeconds(5), onlyIfNotPlaying: false);
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable)
            return;

        Logger.Log($"AudioPlayerService - network available again, userWantsPlaying={_userWantsPlaying}");
        _ = RecoverAsync(TimeSpan.FromSeconds(3), onlyIfNotPlaying: true);
    }

    private async Task RecoverAsync(TimeSpan delay, bool onlyIfNotPlaying)
    {
        if (_disposed || !_userWantsPlaying)
            return;

        // Network/power events often arrive in bursts - only one recovery at a time.
        if (Interlocked.Exchange(ref _recovering, 1) == 1)
            return;

        try
        {
            await Task.Delay(delay);
            if (_disposed || !_userWantsPlaying)
                return;

            if (onlyIfNotPlaying && _lastState == PlaybackState.Playing)
                return;

            // Streams may have rotated while we were away - resolve afresh, then start a new session.
            _streamUrls = await _station.ResolveStreamUrlsAsync(_http);
            if (_streamUrls.Count == 0)
                _streamUrls = new List<string>(_station.FallbackStreamUrls);
            _streamUrlIndex = 0;

            if (!_disposed && _userWantsPlaying)
                Play();
        }
        catch (Exception ex)
        {
            Logger.Log($"AudioPlayerService - recovery failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _recovering, 0);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _watchdog.Dispose();
        DisposeCurrentPlayer();
        _http.Dispose();
    }
}
