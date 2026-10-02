using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ManagedBass;
using Timer = System.Threading.Timer;

namespace SomaMetalTray;

/// <summary>
/// BASS (un4seen) via ManagedBass. Chosen because its start threshold is configurable (NetPreBuffer) - Media Foundation's
/// isn't, which is what causes the Death.FM start-up pauses (DEVELOPING.md) - and it is ~0.5MB in total against ~300MB for
/// LibVLC. LICENCE: BASS is free for non-commercial use only; see DEVELOPING.md before shipping this commercially.
///
/// Needs bass.dll and bass_aac.dll (Death.FM and Metal Detector are AAC) beside the exe; they come from native/bass/x64
/// (gitignored - download from https://www.un4seen.com/). If they are missing <see cref="AudioEngines.Create"/> falls back.
///
/// BASS has no state events, so a 250ms poll turns ChannelIsActive into StateChanged/Failed. Opening the URL blocks until
/// connected, so Start() does that on the thread pool; <c>_session</c> invalidates a connect that was superseded meanwhile.
/// </summary>
internal sealed class BassEngine : IAudioEngine
{
    // Percent of the (5s) network buffer that must fill before playback begins. 0 still stuttered once; 25 gave no stalls
    // in testing at ~1.7s to first audio; 75 only delayed the start.
    private const int PreBufferPercent = 25;

    private static readonly object Gate = new();
    private static bool _pluginLoaded;

    // BASS_ACTIVE_PAUSED_DEVICE; ManagedBass 4.0.2's enum doesn't name it.
    private const ManagedBass.PlaybackState PausedDevice = (ManagedBass.PlaybackState)4;

    private int _session;
    private int _stream;
    private Timer? _poll;
    private PlaybackState _last = PlaybackState.Stopped;

    public string Name => "BASS";
    public event Action<PlaybackState>? StateChanged;
    public event Action? Failed;

    /// <summary>Throws if the native libraries are missing or unusable, so the caller can fall back to another engine.</summary>
    public BassEngine()
    {
        lock (Gate)
        {
            Initialize();
            Bass.Free(); // proves the DLLs load; every Start() re-initialises on the then-current default device
        }
    }

    private static void Initialize()
    {
        // Re-initialised per session so a new session lands on whatever Windows' default output is *now* (BASS, unlike
        // MediaPlayer, doesn't follow default-device changes after Init).
        Bass.Free();
        if (!Bass.Init(-1, 44100, DeviceInitFlags.Default))
            throw new InvalidOperationException($"BASS_Init failed: {Bass.LastError}");

        if (!_pluginLoaded)
        {
            string plugin = Path.Combine(AppContext.BaseDirectory, "bass_aac.dll");
            if (Bass.PluginLoad(plugin) == 0)
                throw new InvalidOperationException($"bass_aac.dll failed to load: {Bass.LastError}");
            _pluginLoaded = true;
        }

        Bass.NetPreBuffer = PreBufferPercent;
    }

    public void Start(string url, double volume)
    {
        Stop();
        int session = Interlocked.Increment(ref _session);
        Raise(session, PlaybackState.Buffering);
        _ = Task.Run(() => Open(session, url, volume));
    }

    private void Open(int session, string url, double volume)
    {
        int stream;
        lock (Gate)
        {
            if (session != Volatile.Read(ref _session))
                return;

            try
            {
                Initialize();
                stream = Bass.CreateStream(url, 0, BassFlags.Default, null);
                if (stream == 0)
                {
                    Logger.Log($"BASS - CreateStream failed: {Bass.LastError}");
                    Fail(session);
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"BASS - open failed: {ex.Message}");
                Fail(session);
                return;
            }

            if (session != Volatile.Read(ref _session))
            {
                Bass.StreamFree(stream); // superseded while connecting
                return;
            }

            _stream = stream;
            Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, (float)volume);
            Bass.ChannelPlay(stream);
        }

        Logger.Log($"BASS - connected, prebuffering ({PreBufferPercent}%)");
        _poll = new Timer(_ => Poll(session, stream), null, 250, 250);
    }

    private void Poll(int session, int stream)
    {
        if (session != Volatile.Read(ref _session))
            return;

        switch (Bass.ChannelIsActive(stream))
        {
            case ManagedBass.PlaybackState.Playing:
                Raise(session, PlaybackState.Playing);
                break;
            case ManagedBass.PlaybackState.Stalled:
                Raise(session, PlaybackState.Buffering);
                break;
            case ManagedBass.PlaybackState.Stopped: // the server closed the stream
            case PausedDevice: // output device went away (headphones unplugged etc) - re-Start lands on the new default
                Logger.Log($"BASS - stream ended ({Bass.ChannelIsActive(stream)})");
                Fail(session);
                break;
        }
    }

    private void Raise(int session, PlaybackState state)
    {
        if (session != Volatile.Read(ref _session) || state == _last)
            return;
        _last = state;
        Logger.Log($"BASS state - {state}");
        StateChanged?.Invoke(state);
    }

    private void Fail(int session)
    {
        if (session != Volatile.Read(ref _session))
            return;
        Failed?.Invoke();
    }

    public void Stop()
    {
        Interlocked.Increment(ref _session); // invalidates any in-flight connect and every pending poll/event
        _last = PlaybackState.Stopped;

        _poll?.Dispose();
        _poll = null;

        int stream = Interlocked.Exchange(ref _stream, 0);
        if (stream != 0)
        {
            // StreamFree on a connected network stream is quick; the slow part (connecting) is never on this thread.
            try { Bass.StreamFree(stream); } catch { }
        }
    }

    public void SetVolume(double volume)
    {
        int stream = _stream;
        if (stream != 0)
            Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, (float)volume);
    }

    public void Dispose()
    {
        Stop();
        lock (Gate)
            Bass.Free();
    }
}
