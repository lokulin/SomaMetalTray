using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SomaMetalTray;

internal static class Program
{
    // Named mutex so a second launch (e.g. double-clicking the shortcut again)
    // notifies the user instead of spawning a duplicate tray icon.
    private const string SingleInstanceMutexName = "SomaMetalTray_SingleInstance_7c14a9d1";

    // Identifies this process to Windows for taskbar grouping, Alt+Tab, and
    // the name shown in the volume mixer / media flyout for our
    // MediaPlayer-hosted stream - without this it can show up as "Unknown app".
    // Must be set before SmtcService/MediaPlayer touch anything, so this
    // happens here, first thing (same pattern DeathFmTray uses).
    private const string AppUserModelId = "TerraEclectic.SomaMetalTray.v1";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    private static void Main()
    {
        SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

        // Unpackaged apps need a Start Menu shortcut stamped with the same
        // AUMID before Windows will show a friendly name (instead of
        // "Unknown app") in the volume mixer / media flyout / toasts.
        // Harmless no-op when the shortcut already exists.
        AumidShortcutHelper.EnsureStartMenuShortcut();

        ApplicationConfiguration.Initialize();

        using var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isNewInstance);

        if (!isNewInstance)
        {
            MessageBox.Show(
                "Metal Detector is already running - check your system tray.",
                "Metal Detector",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayAppContext());

        GC.KeepAlive(singleInstanceMutex);
    }
}
