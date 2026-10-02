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
/// Plays the selected station's stream through an <see cref="IAudioEngine"/> (BASS or Windows' MediaPlayer - see
/// <see cref="AudioEngines"/>) and owns everything engine-independent: volume persistence, fetching/parsing the
/// channel's playlist for a live stream URL, and rotating to the next URL (or re-fetching) on playback failure -
/// SomaFM's ice servers rotate and occasionally go down individually.
///
/// Replaces DeathFmTray's NowPlayingService+VolumeService (which drove a WebView2-hosted &lt;audio&gt; element) entirely;
/// there's exactly one place (this class) that reads or writes AppSettings.Volume.
///
/// Every Play() starts a fresh engine session rather than reusing one across stop/start cycles (see
/// MediaFoundationEngine for the bug that motivated that). See Logger/debug.log for state diagnostics.
/// </summary>
public sealed class AudioPlayerService : IDisposable
{
    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private IStation _station;

    private readonly IAudioEngine _engine;
    private double _volume;

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

        _engine = AudioEngines.Create(settings);
        _engine.StateChanged += OnEngineStateChanged;
        _engine.Failed += OnEngineFailed;
        Logger.Log($"AudioPlayerService - engine={_engine.Name}");

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.UserAgentProduct, UpdateChecker.CurrentVersion().ToString(3)));

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _watchdog = new Timer(_ => CheckForStall(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>Volume as 0.0-1.0. Setting it both applies it live (if a session is currently live) and persists it (this is the one place that owns volume persistence).</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            _volume = clamped;
            _engine.SetVolume(clamped);
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

        try
        {
            // Start() tears down whatever the previous session was, so a Play() while already playing (or
            // mid-retry) starts clean rather than layering a second session on top of the old one.
            _engine.Start(url, _volume);
        }
        catch (Exception ex)
        {
            // Bad URL or session creation failure - treat like a playback
            // failure and roll to the next candidate stream URL.
            Logger.Log($"AudioPlayerService.Play() - failed to start ({ex.Message}), advancing to next stream URL");
            _ = AdvanceAndRetryAsync();
        }
    }

    public void Stop()
    {
        Logger.Log($"AudioPlayerService.Stop() - userWantsPlaying was {_userWantsPlaying}");
        _userWantsPlaying = false;
        _engine.Stop();
        MarkState(PlaybackState.Stopped);
        PlaybackStateChanged?.Invoke(PlaybackState.Stopped);
    }

    private void OnEngineStateChanged(PlaybackState raw)
    {
        // A pause nobody asked for is the engine running dry, so it reads as buffering; after a deliberate Stop() it is just stopped.
        PlaybackState mapped = raw == PlaybackState.Paused
            ? (_userWantsPlaying ? PlaybackState.Buffering : PlaybackState.Stopped)
            : raw;

        Logger.Log($"AudioPlayerService - engine state={raw}, mapped={mapped}, userWantsPlaying={_userWantsPlaying}");

        MarkState(mapped);

        PlaybackStateChanged?.Invoke(mapped);
    }

    // Ice servers rotate/occasionally go down - on a failure, try the next
    // URL from the .pls we already have; once we've exhausted the list,
    // re-fetch the .pls entirely (the rotation may have changed) and try
    // again from the top. Only acts if the user still wants to be playing -
    // a failure after a deliberate Stop() shouldn't restart playback.
    private void OnEngineFailed()
    {
        Logger.Log($"AudioPlayerService - engine reported failure, userWantsPlaying={_userWantsPlaying}");

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
        _engine.Dispose();
        _http.Dispose();
    }
}
