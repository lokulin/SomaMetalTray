using System;

namespace SomaMetalTray;

/// <summary>Picks the playback engine. The one place to change when swapping backends.</summary>
internal static class AudioEngines
{
    public const string MediaFoundation = "mediafoundation";
    public const string Bass = "bass";

    /// <summary>The engine used when neither the environment nor settings.json says otherwise.</summary>
    public const string Default = Bass;

    /// <summary>
    /// Order of precedence: the BLASTBEAT_ENGINE environment variable, then AppSettings.AudioEngine, then <see cref="Default"/>.
    /// An engine that cannot start (e.g. BASS's DLLs are missing) falls back to Media Foundation rather than leaving the
    /// app silent.
    /// </summary>
    public static IAudioEngine Create(AppSettings settings)
    {
        string wanted = (Environment.GetEnvironmentVariable("BLASTBEAT_ENGINE") ?? settings.AudioEngine ?? Default).Trim().ToLowerInvariant();

        if (wanted == Bass)
        {
            try
            {
                return new BassEngine();
            }
            catch (Exception ex)
            {
                Logger.Log($"AudioEngines - BASS unavailable ({ex.Message}); falling back to Media Foundation");
            }
        }
        else if (wanted != MediaFoundation)
        {
            Logger.Log($"AudioEngines - unknown engine '{wanted}'; using Media Foundation");
        }

        return new MediaFoundationEngine();
    }
}
