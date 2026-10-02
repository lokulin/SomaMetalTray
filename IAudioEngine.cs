using System;

namespace SomaMetalTray;

/// <summary>
/// One playback backend for <see cref="AudioPlayerService"/>. The service owns everything engine-independent (stream-URL
/// rotation, stall watchdog, sleep/network recovery, volume persistence); an engine only plays one URL at a time and
/// reports what it is doing. Swapping engines is a one-line change in <see cref="AudioEngines.Create"/>.
/// </summary>
internal interface IAudioEngine : IDisposable
{
    string Name { get; }

    /// <summary>
    /// Raised as the session's state changes (Buffering/Playing/Paused/Stopped), possibly on a background thread. Never
    /// raised for a session that has since been replaced or stopped.
    /// </summary>
    event Action<PlaybackState>? StateChanged;

    /// <summary>The connection failed or the stream ended on its own; the service moves to the next URL.</summary>
    event Action? Failed;

    /// <summary>Tears down any previous session and starts playing <paramref name="url"/>. Throws if it cannot even try.</summary>
    void Start(string url, double volume);

    /// <summary>Tears down the current session. No events are raised afterwards.</summary>
    void Stop();

    /// <summary>0.0-1.0, applied to the live session.</summary>
    void SetVolume(double volume);
}
