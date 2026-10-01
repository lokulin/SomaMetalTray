using System;
using System.Diagnostics;
using System.IO;

namespace SomaMetalTray;

/// <summary>
/// Minimal rolling debug log written to %LOCALAPPDATA%\BlastbeatPlayer\debug.log,
/// used to diagnose the "SMTC pause kills playback forever" bug report (see
/// AudioPlayerService/SmtcService/PlayerForm) without needing a full logging
/// framework. Enabled by default (not behind a flag) - a user can just run the
/// app, reproduce the issue, and send this file back.
///
/// Every line also goes through Debug.WriteLine, same as before this existed.
/// </summary>
internal static class Logger
{
    // Small cap so this never grows unbounded across long-running sessions -
    // truncated (not rotated/archived) on overflow, since this is meant purely
    // as a "reproduce it once, grab the file" diagnostic aid, not a durable log.
    private const long MaxBytes = 2 * 1024 * 1024; // 2 MB

    private static readonly string LogDirectory = AppInfo.LocalDir;

    private static readonly string LogPath = Path.Combine(LogDirectory, "debug.log");
    private static readonly object Lock = new();
    private static bool _truncatedThisRun;

    public static void Log(string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        Debug.WriteLine(line);

        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(LogDirectory);

                if (!_truncatedThisRun)
                {
                    // Truncate once per process (fresh log per run) rather than
                    // appending forever - keeps a single repro's log easy to read
                    // without needing to hunt through old sessions.
                    File.WriteAllText(LogPath, "");
                    _truncatedThisRun = true;
                }
                else if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                {
                    File.WriteAllText(LogPath, "");
                }

                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Best-effort - logging must never be the reason the app misbehaves.
        }
    }
}
