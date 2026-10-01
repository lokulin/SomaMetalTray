using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SomaMetalTray;

/// <summary>
/// Adds/removes a per-user "run at Windows startup" entry via the standard
/// HKCU Run key - no installer, admin rights, or Task Scheduler entry required.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BlastbeatPlayer";

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is not null;
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                                 ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

        if (enabled)
        {
            string exePath = Environment.ProcessPath ?? Application.ExecutablePath;

            // --minimized tells Program/TrayAppContext to come up hidden in the tray
            // rather than popping the player window on every login.
            key.SetValue(ValueName, $"\"{exePath}\" --minimized");
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName);
        }
    }
}
