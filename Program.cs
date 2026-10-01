using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SomaMetalTray;

internal static class Program
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    private static void Main()
    {
        // Identifies this process to Windows for taskbar grouping, Alt+Tab, and the name shown in the volume
        // mixer / media flyout for our MediaPlayer-hosted stream - without it we can show up as "Unknown app".
        // Must be set before SmtcService/MediaPlayer touch anything, so it happens here, first thing.
        SetCurrentProcessExplicitAppUserModelID(AppInfo.AppUserModelId);

        // Unpackaged apps need a Start Menu shortcut stamped with the same
        // AUMID before Windows will show a friendly name (instead of
        // "Unknown app") in the volume mixer / media flyout / toasts.
        // Harmless no-op when the shortcut already exists.
        AumidShortcutHelper.EnsureStartMenuShortcut();

        // Carry settings/likes/history/autostart over from SomaMetalTray or DeathFmTray, once.
        LegacyMigration.Run();

        ApplicationConfiguration.Initialize();

        // Named mutex so a second launch (e.g. double-clicking the shortcut again) notifies the user
        // instead of spawning a duplicate tray icon.
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, AppInfo.SingleInstanceMutexName, out bool isNewInstance);

        if (!isNewInstance)
        {
            MessageBox.Show(
                $"{AppInfo.Name} is already running - check your system tray.",
                AppInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayAppContext());

        GC.KeepAlive(singleInstanceMutex);
    }
}
