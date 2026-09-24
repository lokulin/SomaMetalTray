using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SomaMetalTray;

/// <summary>Applies a dark, theme-matched titlebar using DWM window attributes.</summary>
internal static class WindowChromeHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    public static void ApplyDarkTitleBar(Form form, Color captionColor, Color textColor)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            return;

        IntPtr hwnd = form.Handle;

        int enableDarkMode = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enableDarkMode, sizeof(int));

        int captionColorRef = ToColorRef(captionColor);
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColorRef, sizeof(int));

        int textColorRef = ToColorRef(textColor);
        DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref textColorRef, sizeof(int));

        // Deliberately not also setting DWMWA_BORDER_COLOR here: a previous
        // attempt at that (to match a separate 1px Windows 11 frame colour to
        // the caption) turned out to be the actual cause of a visible seam
        // right below the title bar, not a fix for one - DeathFmTray, which
        // has no such seam, never sets it either. Left at its default,
        // Windows blends the frame with the caption colour correctly on its
        // own; explicitly setting it is what introduced the mismatch.
    }

    // Win32 COLORREF is 0x00BBGGRR.
    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
}
