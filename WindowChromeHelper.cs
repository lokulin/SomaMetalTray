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

    // DWMWA_BORDER_COLOR (34) was investigated for a seam that appears just
    // below the title bar on FixedSingle windows - ruled out via a magenta
    // diagnostic build: that attribute only visibly affects a window's frame
    // when FormBorderStyle is None (it becomes the whole outline there);
    // with FixedSingle (what this app actually uses) it has no visible
    // effect at all, confirmed with SWP_FRAMECHANGED applied too. DWM does
    // report a genuine 1px DWMWA_VISIBLE_FRAME_BORDER_THICKNESS on both this
    // app and DeathFmTray (which has no visible seam) identically, so the
    // seam isn't an "extra" element any DWM attribute here controls - still
    // unresolved; see the WindowChromeHelper.cs files in this repo and in
    // DeathFmTray for the two configurations being compared.

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
    }

    // Win32 COLORREF is 0x00BBGGRR.
    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
}
